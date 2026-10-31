using UpdateHub.Models;
using UpdateHub.Services;

namespace UpdateHub.Core.Scanning;

/// <summary>
/// Runs every scanner and folds their results into one inventory. The same product is
/// routinely reported by three different sources, so the merge order matters: the registry
/// supplies the richest metadata, winget supplies package IDs and upgrade availability,
/// and the rest fill gaps.
/// </summary>
public sealed class ScanCoordinator
{
    private readonly SettingsService _settings;
    private readonly LogService _log;

    public ScanCoordinator(SettingsService settings, LogService log)
    {
        _settings = settings;
        _log = log;
    }

    public bool WingetAvailable { get; private set; } = true;

    public async Task<List<InstalledApp>> ScanAllAsync(
        CancellationToken ct, IProgress<string>? progress = null)
    {
        var s = _settings.Current;

        var registryScanner = new RegistryScanner(includeSystemComponents: !s.HideSystemComponents);
        var wingetScanner = new WingetScanner();

        var tasks = new List<(string Label, Task<List<InstalledApp>> Task)>
        {
            ("registry", Run(registryScanner, ct)),
            ("winget", Run(wingetScanner, ct)),
        };

        if (s.IncludeMsStore) tasks.Add(("store", Run(new MsStoreScanner(), ct)));
        if (s.IncludePortable) tasks.Add(("portable", Run(new PortableScanner(), ct)));
        if (s.IncludeChocolatey) tasks.Add(("choco", Run(new ChocolateyScanner(), ct)));
        if (s.IncludeScoop) tasks.Add(("scoop", Run(new ScoopScanner(), ct)));
        tasks.Add(("startmenu", Run(new StartMenuScanner(), ct)));
        if (s.CustomFolders.Count > 0)
            tasks.Add(("custom", Run(new PortableScanner(s.CustomFolders), ct)));

        progress?.Report("Scanning installed software…");
        await Task.WhenAll(tasks.Select(t => t.Task)).ConfigureAwait(false);
        WingetAvailable = wingetScanner.WingetAvailable;

        var byLabel = tasks.ToDictionary(t => t.Label, t => t.Task.Result);
        foreach (var (label, list) in byLabel)
            _log.Info($"scan/{label}: {list.Count} entries");

        var merged = Merge(byLabel);

        // Ignore rules are keyed on the merged identity, so they are applied last.
        progress?.Report($"Found {merged.Count} applications");
        return merged;
    }

    private async Task<List<InstalledApp>> Run(IAppScanner scanner, CancellationToken ct)
    {
        try
        {
            return await scanner.ScanAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // One failing source should never abort the whole scan.
            _log.Error($"Scanner '{scanner.Name}' failed", ex);
            return new List<InstalledApp>();
        }
    }

    private List<InstalledApp> Merge(Dictionary<string, List<InstalledApp>> bySource)
    {
        var registry = bySource.GetValueOrDefault("registry") ?? new();
        var winget = bySource.GetValueOrDefault("winget") ?? new();
        var store = bySource.GetValueOrDefault("store") ?? new();

        var result = new List<InstalledApp>(registry.Count + 64);
        var byRegistryKey = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        var byNormalized = new Dictionary<string, List<InstalledApp>>(StringComparer.OrdinalIgnoreCase);

        void Index(InstalledApp app)
        {
            result.Add(app);
            if (app.Key.StartsWith("reg:", StringComparison.Ordinal))
                byRegistryKey[app.Key] = app;
            if (app.NormalizedName.Length > 0)
            {
                if (!byNormalized.TryGetValue(app.NormalizedName, out var list))
                    byNormalized[app.NormalizedName] = list = new List<InstalledApp>();
                list.Add(app);
            }
        }

        foreach (var app in registry) Index(app);

        // ---- winget rows ------------------------------------------------
        foreach (var w in winget)
        {
            var target = MatchWingetRow(w, byRegistryKey, byNormalized);
            if (target is not null)
            {
                target.Sources |= w.Sources;
                target.WingetId ??= w.WingetId;
                target.MsStoreFamilyName ??= w.MsStoreFamilyName;
                if (w.LatestVersion is not null)
                {
                    target.LatestVersion = w.LatestVersion;
                    target.UpdateAvailable = true;
                    target.UpdateMethod = w.UpdateMethod == UpdateMethod.MsStore
                        ? UpdateMethod.MsStore
                        : UpdateMethod.Winget;
                }
                else if (w.WingetId is not null && target.UpdateMethod == UpdateMethod.None)
                {
                    target.UpdateMethod = UpdateMethod.Winget;
                }
                // The registry occasionally omits DisplayVersion where winget has one.
                if (string.IsNullOrWhiteSpace(target.InstalledVersion))
                    target.InstalledVersion = w.InstalledVersion;
            }
            else if (w.WingetId is not null || w.MsStoreFamilyName is not null)
            {
                // A winget-managed package with no registry footprint is still real.
                Index(w);
            }
        }

        // ---- MSIX / Store -----------------------------------------------
        foreach (var st in store)
        {
            var target = result.FirstOrDefault(a =>
                a.MsStoreFamilyName is not null &&
                string.Equals(a.MsStoreFamilyName, st.MsStoreFamilyName, StringComparison.OrdinalIgnoreCase));

            // Store packages that also register an uninstall entry have no family name on
            // the registry side, so the family-name join misses and the product would be
            // listed twice. Fall back to the display name before treating it as new.
            if (target is null && st.NormalizedName.Length > 0 &&
                byNormalized.TryGetValue(st.NormalizedName, out var byName))
                target = byName.FirstOrDefault(a => a.MsStoreFamilyName is null);

            if (target is not null)
            {
                target.Sources |= DetectionSource.MsStore;
                // Get-AppxPackage resolves the real display name; winget shows the family name.
                if (LooksLikeFamilyName(target.Name) && !LooksLikeFamilyName(st.Name))
                {
                    target.Name = st.Name;
                    target.NormalizedName = st.NormalizedName;
                }
                // MSIX products often register a package GUID where the publisher belongs;
                // Get-AppxPackage has the real name.
                if (target.Publisher is null || LooksLikeGuid(target.Publisher))
                    target.Publisher = st.Publisher ?? target.Publisher;
                target.InstallLocation ??= st.InstallLocation;
                target.InstallDate ??= st.InstallDate;
                if (string.IsNullOrWhiteSpace(target.InstalledVersion))
                    target.InstalledVersion = st.InstalledVersion;
                target.IsSystemComponent |= st.IsSystemComponent;
            }
            else Index(st);
        }

        // ---- Package managers and portables ------------------------------
        foreach (var label in new[] { "choco", "scoop", "portable", "custom", "startmenu" })
        {
            if (!bySource.TryGetValue(label, out var list)) continue;
            foreach (var item in list)
            {
                var target = MatchLoose(item, byNormalized, result);
                if (target is not null)
                {
                    target.Sources |= item.Sources;
                    target.ChocoId ??= item.ChocoId;
                    target.ScoopId ??= item.ScoopId;
                    target.ExePath ??= item.ExePath;
                    target.Homepage ??= item.Homepage;
                    target.InstallLocation ??= item.InstallLocation;
                    if (target.UpdateMethod == UpdateMethod.None)
                        target.UpdateMethod = item.UpdateMethod;
                }
                else Index(item);
            }
        }

        foreach (var app in result)
        {
            if (string.IsNullOrWhiteSpace(app.Name)) app.Name = "(unknown)";
            if (app.NormalizedName.Length == 0)
                app.NormalizedName = NameNormalizer.Normalize(app.Name);
            // Classified after merging, so the rules see the union of every source's
            // publisher, winget id and size rather than one scanner's partial view.
            AppClassifier.Classify(app);
        }

        return CollapseIdenticalTwins(result)
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .Where(a => !_settings.Current.HideSystemComponents || !IsNoise(a))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Collapses rows that are the same product seen twice.
    ///
    /// A single install can register under both the 32- and 64-bit registry views, or under
    /// both HKLM and HKCU, producing two entries with the same name and the same version.
    /// The scope is part of the dedupe key, so those survive as separate rows. Matching on
    /// name *and* version keeps genuinely parallel installs — .NET 6 alongside .NET 8, or
    /// the x86 and x64 redistributables, whose names now retain their bitness — intact.
    /// </summary>
    private static List<InstalledApp> CollapseIdenticalTwins(List<InstalledApp> apps)
    {
        var groups = apps.GroupBy(a => (
            Name: a.NormalizedName,
            Version: VersionComparer.SortKey(a.InstalledVersion)));

        var collapsed = new List<InstalledApp>(apps.Count);
        foreach (var group in groups)
        {
            if (group.Count() == 1) { collapsed.Add(group.First()); continue; }

            // Keep the richest row and fold the others' identifiers into it, so nothing a
            // single source contributed is lost.
            var best = group
                .OrderByDescending(a => a.EstimatedSizeKb)
                .ThenByDescending(a => a.Sources.ToString().Length)
                .First();

            foreach (var other in group.Where(a => !ReferenceEquals(a, best)))
            {
                best.Sources |= other.Sources;
                best.WingetId ??= other.WingetId;
                best.ChocoId ??= other.ChocoId;
                best.ScoopId ??= other.ScoopId;
                best.MsStoreFamilyName ??= other.MsStoreFamilyName;
                best.GitHubRepo ??= other.GitHubRepo;
                best.InstallLocation ??= other.InstallLocation;
                best.UninstallString ??= other.UninstallString;
                best.ExePath ??= other.ExePath;
                best.Homepage ??= other.Homepage;
                best.Publisher ??= other.Publisher;
                best.InstallDate ??= other.InstallDate;
                if (best.EstimatedSizeKb == 0) best.EstimatedSizeKb = other.EstimatedSizeKb;
                if (other.LatestVersion is not null && best.LatestVersion is null)
                {
                    best.LatestVersion = other.LatestVersion;
                    best.UpdateAvailable = other.UpdateAvailable;
                    best.UpdateMethod = other.UpdateMethod;
                }
            }
            collapsed.Add(best);
        }
        return collapsed;
    }

    /// <summary>
    /// Filters the entries that are not software a person installed: OS-signed Store
    /// packages, and MSIX apps whose display name never resolved past a raw GUID or
    /// package identifier.
    /// </summary>
    private static bool IsNoise(InstalledApp app)
    {
        if (app.IsSystemComponent) return true;

        if (app.Sources.HasFlag(DetectionSource.MsStore))
        {
            if (LooksLikeGuid(app.Name)) return true;
            // "96BC3D00.Currency" — a publisher-prefixed package id, not a display name.
            if (!app.Name.Contains(' ') && app.Name.Contains('.')
                && app.Name.Split('.') is [{ Length: >= 6 } head, ..]
                && head.All(c => char.IsLetterOrDigit(c)) && head.Any(char.IsDigit))
                return true;
        }

        return false;
    }

    private static bool LooksLikeGuid(string name)
    {
        var trimmed = name.Trim('{', '}');
        return Guid.TryParse(trimmed, out _)
            || (trimmed.Length is >= 32 and <= 40
                && trimmed.Count(c => c == '-') >= 4
                && trimmed.All(c => char.IsAsciiHexDigit(c) || c == '-'));
    }

    /// <summary>
    /// winget echoes registry entries with IDs shaped like "ARP\Machine\X64\{guid}", where
    /// the trailing segment is the uninstall subkey name. That gives an exact join back to
    /// the registry scan instead of guessing from display names.
    /// </summary>
    private static InstalledApp? MatchWingetRow(
        InstalledApp w,
        Dictionary<string, InstalledApp> byRegistryKey,
        Dictionary<string, List<InstalledApp>> byNormalized)
    {
        if (w.Key.StartsWith("winget-arp:", StringComparison.Ordinal))
        {
            var id = w.Key["winget-arp:".Length..];
            var parts = id.Split('\\');
            if (parts.Length >= 4)
            {
                var scope = parts[1];   // Machine | User
                var arch = parts[2];    // X64 | X86
                var subKey = string.Join('\\', parts.Skip(3));
                var mapped = scope.Equals("User", StringComparison.OrdinalIgnoreCase)
                    ? $"reg:HKCU:{subKey}"
                    : arch.Equals("X86", StringComparison.OrdinalIgnoreCase)
                        ? $"reg:HKLM32:{subKey}"
                        : $"reg:HKLM64:{subKey}";
                if (byRegistryKey.TryGetValue(mapped, out var exact)) return exact;

                // Scope/arch labels do not always line up with the hive we read it from.
                foreach (var alt in new[] { $"reg:HKLM64:{subKey}", $"reg:HKLM32:{subKey}", $"reg:HKCU:{subKey}" })
                    if (byRegistryKey.TryGetValue(alt, out var found)) return found;
            }
        }

        if (w.NormalizedName.Length > 0 && byNormalized.TryGetValue(w.NormalizedName, out var candidates))
        {
            // Exact version agreement is the strongest signal.
            var exact = candidates.FirstOrDefault(c =>
                !string.IsNullOrWhiteSpace(c.InstalledVersion) &&
                VersionComparer.Compare(c.InstalledVersion, w.InstalledVersion) == 0);
            if (exact is not null) return exact;

            // Then allow for differing precision: several runtimes are installed
            // side by side under one name (.NET 6 and 8, for instance), so the right row
            // is the one agreeing on every segment both versions actually have.
            var samePrecisionTolerant = candidates.Where(c =>
                VersionComparer.IsSameProductVersion(c.InstalledVersion, w.InstalledVersion))
                .ToList();
            if (samePrecisionTolerant.Count == 1) return samePrecisionTolerant[0];
            if (samePrecisionTolerant.Count > 1)
                // Ambiguous, so prefer the richest row rather than inventing a new one.
                return samePrecisionTolerant
                    .OrderByDescending(c => c.EstimatedSizeKb)
                    .First();

            return candidates.Count == 1 ? candidates[0] : null;
        }

        return null;
    }

    private static InstalledApp? MatchLoose(
        InstalledApp item,
        Dictionary<string, List<InstalledApp>> byNormalized,
        List<InstalledApp> all)
    {
        if (item.ExePath is not null)
        {
            var exact = all.FirstOrDefault(a => a.Key != item.Key &&
                string.Equals(a.ExePath, item.ExePath, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }

        if (item.NormalizedName.Length > 0 &&
            byNormalized.TryGetValue(item.NormalizedName, out var candidates) &&
            candidates.Count > 0)
            return candidates[0];

        // A portable executable sitting inside a registered app's install folder is that
        // app's own binary, not a separate product.
        if (item.ExePath is not null)
        {
            var dir = item.InstallLocation;
            if (!string.IsNullOrWhiteSpace(dir))
            {
                var match = all.FirstOrDefault(a =>
                    a.Key != item.Key &&
                    !string.IsNullOrWhiteSpace(a.InstallLocation) &&
                    dir.StartsWith(a.InstallLocation!.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
                if (match is not null) return match;
            }
        }

        return null;
    }

    /// <summary>Detects a raw MSIX family name being used where a display name belongs.</summary>
    private static bool LooksLikeFamilyName(string name) =>
        name.Contains('_') && (name.Contains("__") || name.Count(c => c == '.') >= 2);
}

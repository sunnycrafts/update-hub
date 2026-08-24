using UpdateHub.Core;
using UpdateHub.Data;
using UpdateHub.Models;
using UpdateHub.Services;

namespace UpdateHub.Sources;

/// <summary>
/// Runs the providers for each app, reconciles what they say, and turns the result into
/// the numbers the UI shows: how far behind, how many releases missed, and how much of
/// that to believe.
/// </summary>
public sealed class ProviderRegistry
{
    private readonly List<IVersionProvider> _providers;
    private readonly AppCatalog _catalog;
    private readonly ReleaseHistoryStore _history;
    private readonly SettingsService _settings;
    private readonly LogService _log;
    public GitHubClient GitHub { get; }
    public VendorProvider Vendor { get; }

    public ProviderRegistry(
        AppCatalog catalog, ReleaseHistoryStore history, CacheStore cache,
        SettingsService settings, LogService log)
    {
        _catalog = catalog;
        _history = history;
        _settings = settings;
        _log = log;

        GitHub = new GitHubClient(cache, log, settings);
        Vendor = new VendorProvider(cache, log, App.DataRoot);

        _providers = new List<IVersionProvider>
        {
            new GitHubProvider(GitHub),
            Vendor,
            new WingetProvider(GitHub, cache, log),
            new ChocolateyProvider(cache),
            new ScoopProvider(),
        };
    }

    public IReadOnlyList<IVersionProvider> Providers => _providers;

    public async Task RefreshAllAsync(
        List<InstalledApp> apps, bool forceRefresh,
        IProgress<(int Done, int Total, string Label)>? progress,
        CancellationToken ct)
    {
        var ignores = new Dictionary<string, (string? Reason, string? Skipped)>();
        try { ignores = _settings.Repo?.LoadIgnores() ?? ignores; } catch { }

        int done = 0;
        var total = apps.Count;
        using var throttle = new SemaphoreSlim(Math.Max(1, _settings.Current.MaxConcurrency));

        var tasks = apps.Select(async app =>
        {
            await throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await RefreshOneAsync(app, forceRefresh, ignores, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Error($"Refresh failed for {app.Name}", ex);
            }
            finally
            {
                throttle.Release();
                var n = Interlocked.Increment(ref done);
                progress?.Report((n, total, app.Name));
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task RefreshOneAsync(
        InstalledApp app, bool forceRefresh,
        Dictionary<string, (string? Reason, string? Skipped)>? ignores,
        CancellationToken ct)
    {
        _catalog.Enrich(app);

        var results = new List<ProviderResult>();
        foreach (var provider in _providers)
        {
            ct.ThrowIfCancellationRequested();
            if (!provider.CanHandle(app)) continue;
            var r = await provider.QueryAsync(app, forceRefresh, ct).ConfigureAwait(false);
            if (r.HasData) results.Add(r);
        }

        // A winget lookup can reveal a GitHub repo we did not know about (its release-notes
        // URL usually points straight at the project). Retry GitHub once with that.
        if (app.GitHubRepo is null)
        {
            var discovered = AppCatalog.DiscoverGitHubRepo(
                results.Select(r => r.NotesUrl).Concat(results.Select(r => r.Homepage)).ToArray());
            if (discovered is not null)
            {
                app.GitHubRepo = discovered;
                var gh = _providers.OfType<GitHubProvider>().First();
                if (gh.CanHandle(app))
                {
                    var r = await gh.QueryAsync(app, forceRefresh, ct).ConfigureAwait(false);
                    if (r.HasData) results.Add(r);
                }
            }
        }

        Apply(app, results, ignores);
        app.LastChecked = DateTime.Now;
    }

    /// <summary>Folds provider results into the app and computes every derived number.</summary>
    private void Apply(
        InstalledApp app, List<ProviderResult> results,
        Dictionary<string, (string? Reason, string? Skipped)>? ignores)
    {
        var productKey = ProductKey(app);

        // Persist everything learned, then read back the union so previously cached
        // releases still count when a provider is rate limited this run.
        foreach (var r in results.Where(r => r.History.Count > 0))
            _history.Upsert(productKey, r.History);

        var history = _history.GetHistory(productKey);

        // ---- pick the authoritative latest version -----------------------
        var ranked = results
            .Where(r => r.LatestVersion is not null)
            .OrderByDescending(r => _providers.First(p => p.Name == r.Source).Priority)
            .ToList();

        var winner = ranked.FirstOrDefault();
        if (winner is not null)
        {
            app.LatestVersion = winner.LatestVersion;
            app.LatestReleaseDate = winner.LatestReleaseDate;
            app.LatestReleasePrecision = winner.Precision;
            app.LatestNotes ??= winner.Notes;
            app.LatestNotesUrl ??= winner.NotesUrl;
            app.DownloadUrl ??= winner.DownloadUrl;
            app.DownloadPageUrl ??= winner.DownloadPageUrl;
            app.Homepage ??= winner.Homepage;
            app.Publisher ??= winner.Publisher;
        }

        // A winget-supplied "Available" column from the scan still counts when no
        // provider could be reached.
        if (app.LatestVersion is null) { ScoreConfidence(app, results, history, ranked); ApplyIgnores(app, ignores); return; }

        // ---- fill in dates from history ----------------------------------
        if (app.LatestReleaseDate is null)
        {
            var match = FindRelease(history, app.LatestVersion);
            if (match?.ReleaseDate is not null)
            {
                app.LatestReleaseDate = match.ReleaseDate;
                app.LatestReleasePrecision = match.Precision;
            }
        }

        var installedRelease = FindRelease(history, app.InstalledVersion);
        if (installedRelease?.ReleaseDate is not null)
        {
            app.InstalledReleaseDate = installedRelease.ReleaseDate;
            app.InstalledReleasePrecision = installedRelease.Precision;
        }
        else if (app.InstallDate is not null)
        {
            // Nothing published tells us when this build shipped. The install date is a
            // ceiling on the release date, not the release date itself, so it is labelled
            // Estimated and the UI marks it as such rather than quietly implying accuracy.
            app.InstalledReleaseDate = app.InstallDate;
            app.InstalledReleasePrecision = DatePrecision.Estimated;
        }

        // ---- verdict ------------------------------------------------------
        var cmp = VersionComparer.Compare(app.InstalledVersion, app.LatestVersion);
        if (cmp is { } c)
        {
            // Preserve a true verdict winget already gave us during the scan.
            app.UpdateAvailable = c < 0 || (app.UpdateAvailable && c == 0 && winner is null);
            app.MajorVersionsBehind = VersionComparer.MajorsBehind(app.InstalledVersion, app.LatestVersion);
            app.MinorVersionsBehind = VersionComparer.MinorsBehind(app.InstalledVersion, app.LatestVersion);
        }
        else if (winner is not null)
        {
            // Versions are not comparable; only trust winget's own upgrade signal.
            app.UpdateAvailable = app.UpdateAvailable && app.WingetId is not null;
            app.MajorVersionsBehind = 0;
            app.MinorVersionsBehind = 0;
        }

        if (app.UpdateAvailable && app.UpdateMethod == UpdateMethod.None)
            app.UpdateMethod = app.WingetId is not null ? UpdateMethod.Winget
                : app.ChocoId is not null ? UpdateMethod.Chocolatey
                : app.ScoopId is not null ? UpdateMethod.Scoop
                : app.DownloadUrl is not null ? UpdateMethod.Installer
                : UpdateMethod.Manual;

        var (missed, security) = _history.CountMissed(
            productKey, app.InstalledVersion, app.LatestVersion);
        app.ReleasesMissed = missed;
        app.SecurityReleasesMissed = security;

        ValidateDates(app);

        ScoreConfidence(app, results, history, ranked);
        ApplyIgnores(app, ignores);
    }

    private void ApplyIgnores(
        InstalledApp app, Dictionary<string, (string? Reason, string? Skipped)>? ignores)
    {
        if (ignores is null || !ignores.TryGetValue(app.Key, out var rule)) return;

        if (rule.Skipped is not null)
        {
            app.SkippedVersion = rule.Skipped;
            // Skipping one version suppresses only that version, not the app.
            if (app.LatestVersion is not null &&
                VersionComparer.Compare(app.LatestVersion, rule.Skipped) == 0)
                app.UpdateAvailable = false;
        }
        if (rule.Reason is not null || rule.Skipped is null)
        {
            app.IsIgnored = true;
            app.IgnoreReason = rule.Reason;
        }
    }

    /// <summary>
    /// Cross-checks the dates against the version verdict. An installed build that is
    /// genuinely older must also carry the older date; when it does not, the two dates
    /// came from sources that do not agree and the gap between them is meaningless.
    /// </summary>
    private static void ValidateDates(InstalledApp app)
    {
        app.DatesInconsistent = false;

        if (app.InstalledReleaseDate is not { } installed) return;
        if (app.LatestReleaseDate is not { } latest) return;

        if (app.UpdateAvailable && installed.Date >= latest.Date)
        {
            app.DatesInconsistent = true;
            return;
        }

        // Identical versions must not report a gap, whichever commits the dates came from.
        if (!app.UpdateAvailable &&
            VersionComparer.Compare(app.InstalledVersion, app.LatestVersion) == 0)
            app.InstalledReleaseDate = app.LatestReleaseDate;
    }

    private static ReleaseInfo? FindRelease(List<ReleaseInfo> history, string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;

        var exact = history.FirstOrDefault(h =>
            string.Equals(h.Version, version, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        // Registry versions carry padding the manifest does not ("3.0.21.0" vs "3.0.21").
        return history.FirstOrDefault(h => VersionComparer.Compare(h.Version, version) == 0);
    }

    /// <summary>Stable identity for release history, independent of how it was installed.</summary>
    public static string ProductKey(InstalledApp app)
    {
        if (!string.IsNullOrWhiteSpace(app.GitHubRepo)) return "gh:" + app.GitHubRepo.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(app.WingetId)) return "wg:" + app.WingetId.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(app.ChocoId)) return "ch:" + app.ChocoId.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(app.ScoopId)) return "sc:" + app.ScoopId.ToLowerInvariant();
        return "nm:" + app.NormalizedName;
    }

    // ---- Confidence ------------------------------------------------------

    /// <summary>
    /// How much to trust this row. Driven by which source answered, whether independent
    /// sources agree, and whether the two version strings were even comparable. Shown in
    /// the UI so a 70% answer is not read as if it were a 100% one.
    /// </summary>
    private void ScoreConfidence(
        InstalledApp app, List<ProviderResult> results,
        List<ReleaseInfo> history, List<ProviderResult> ranked)
    {
        app.ConfidenceSources.Clear();
        foreach (var r in results) app.ConfidenceSources.Add(r.Source);
        if (app.Sources.HasFlag(DetectionSource.Registry)) app.ConfidenceSources.Add("Registry");

        if (app.LatestVersion is null)
        {
            app.Confidence = app.Sources.HasFlag(DetectionSource.Registry) ? 30 : 20;
            app.ConfidenceNote = "No online source could identify this product; " +
                                 "installed version shown from the registry only.";
            return;
        }

        int score = ranked.FirstOrDefault()?.Source switch
        {
            "GitHub" => 100,
            "Vendor" => 90,
            "Winget" => 80,
            "Chocolatey" or "Scoop" => 65,
            _ => 50,
        };
        var notes = new List<string>();

        // Independent agreement is the strongest signal available.
        var distinct = ranked
            .Select(r => r.LatestVersion!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ranked.Count > 1)
        {
            bool allAgree = distinct.Count == 1 ||
                ranked.All(r => VersionComparer.Compare(r.LatestVersion, ranked[0].LatestVersion) == 0);
            if (allAgree) { score = Math.Min(100, score + 8); notes.Add($"{ranked.Count} sources agree"); }
            else { score -= 20; notes.Add("Sources disagree on the latest version"); }
        }

        if (VersionComparer.Compare(app.InstalledVersion, app.LatestVersion) is null)
        {
            score -= 30;
            notes.Add("Installed and published version numbers use different schemes, " +
                      "so they cannot be compared directly");
        }

        if (app.DatesInconsistent)
        {
            score -= 25;
            notes.Add("The release dates disagree with the version numbers about which " +
                      "build is newer, so the age gap is not shown");
        }

        if (VersionComparer.Compare(app.InstalledVersion, app.LatestVersion) is > 0)
        {
            score -= 20;
            notes.Add("The version reported as latest is older than the one installed, " +
                      "so this source is probably stale or tracking a different channel");
        }

        if (app.InstalledReleasePrecision == DatePrecision.Estimated)
        {
            score -= 10;
            notes.Add("Installed release date estimated from the install date");
        }
        else if (app.InstalledReleaseDate is null && app.LatestReleaseDate is not null)
        {
            score -= 8;
            notes.Add("No release date known for the installed version");
        }

        if (app.LatestReleasePrecision == DatePrecision.Approximate)
            notes.Add("Dates from winget manifest history are accurate to within a week or two");

        if (history.Count == 0 && app.LatestVersion is not null)
            notes.Add("No release history available, so missed-release counts are unavailable");

        app.Confidence = Math.Clamp(score, 5, 100);
        app.ConfidenceNote = notes.Count > 0 ? string.Join(". ", notes) + "." : null;
    }
}

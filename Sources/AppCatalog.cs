using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using UpdateHub.Core;
using UpdateHub.Models;

namespace UpdateHub.Sources;

public sealed class CatalogEntry
{
    [JsonPropertyName("match")] public List<string> Match { get; set; } = new();
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("winget")] public string? Winget { get; set; }
    [JsonPropertyName("github")] public string? GitHub { get; set; }
    [JsonPropertyName("home")] public string? Home { get; set; }
    [JsonPropertyName("choco")] public string? Choco { get; set; }
    [JsonPropertyName("scoop")] public string? Scoop { get; set; }

    /// <summary>Where this entry came from: "built-in", "community" or a plugin file name.</summary>
    [JsonIgnore] public string Origin { get; set; } = "built-in";
}

public sealed class CatalogFile
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("updated")] public string? Updated { get; set; }
    [JsonPropertyName("entries")] public List<CatalogEntry> Entries { get; set; } = new();
}

/// <summary>
/// Maps installed products onto the identifiers providers need. Three layers, later ones
/// winning: the embedded bootstrap list, a community list the user can refresh, and any
/// JSON dropped into the Plugins folder.
/// </summary>
public sealed class AppCatalog
{
    private readonly Dictionary<string, CatalogEntry> _byMatch = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CatalogEntry> _all = new();
    private readonly string _dataRoot;

    public int EntryCount => _all.Count;
    public string? CommunityUpdated { get; private set; }
    public int PluginEntryCount { get; private set; }

    public AppCatalog(string dataRoot)
    {
        _dataRoot = dataRoot;
        LoadEmbedded();
        LoadCommunity();
        LoadPlugins();
    }

    private void LoadEmbedded()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resource = asm.GetManifestResourceNames()
                              .FirstOrDefault(n => n.EndsWith("catalog.json", StringComparison.OrdinalIgnoreCase));
            if (resource is null) return;
            using var stream = asm.GetManifestResourceStream(resource);
            if (stream is null) return;
            using var reader = new StreamReader(stream);
            Ingest(reader.ReadToEnd(), "built-in");
        }
        catch (Exception) { /* a missing bootstrap catalog degrades to auto-discovery only */ }
    }

    private void LoadCommunity()
    {
        var path = Path.Combine(_dataRoot, "catalog-community.json");
        if (!File.Exists(path)) return;
        try
        {
            var json = File.ReadAllText(path);
            var file = Ingest(json, "community");
            CommunityUpdated = file?.Updated;
        }
        catch (Exception) { }
    }

    private void LoadPlugins()
    {
        var dir = Path.Combine(_dataRoot, "Plugins");
        if (!Directory.Exists(dir)) return;
        foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var before = _all.Count;
                Ingest(File.ReadAllText(path), Path.GetFileName(path));
                PluginEntryCount += _all.Count - before;
            }
            catch (Exception) { /* one bad plugin must not break the catalog */ }
        }
    }

    private CatalogFile? Ingest(string json, string origin)
    {
        var file = JsonSerializer.Deserialize<CatalogFile>(json);
        if (file?.Entries is null) return null;
        foreach (var entry in file.Entries)
        {
            entry.Origin = origin;
            _all.Add(entry);
            foreach (var key in entry.Match)
            {
                var norm = NameNormalizer.Normalize(key);
                if (norm.Length > 0) _byMatch[norm] = entry;   // later layers override
            }
            var byName = NameNormalizer.Normalize(entry.Name);
            if (byName.Length > 0 && !_byMatch.ContainsKey(byName)) _byMatch[byName] = entry;
        }
        return file;
    }

    /// <summary>Replaces the community layer and reloads. Returns the entry count ingested.</summary>
    public void SaveCommunity(string json)
    {
        Directory.CreateDirectory(_dataRoot);
        File.WriteAllText(Path.Combine(_dataRoot, "catalog-community.json"), json);
    }

    public CatalogEntry? Resolve(InstalledApp app)
    {
        if (app.NormalizedName.Length == 0) return null;

        if (_byMatch.TryGetValue(app.NormalizedName, out var exact)) return exact;

        // "Autodesk AutoCAD" filed in the catalog as "AutoCAD".
        var stripped = NameNormalizer.StripPublisher(app.NormalizedName, app.Publisher);
        if (stripped != app.NormalizedName && stripped.Length > 2 &&
            _byMatch.TryGetValue(stripped, out var byStripped)) return byStripped;

        // A known winget ID identifies the product even when the display name does not.
        if (!string.IsNullOrEmpty(app.WingetId))
        {
            var byWinget = _all.FirstOrDefault(e =>
                string.Equals(e.Winget, app.WingetId, StringComparison.OrdinalIgnoreCase));
            if (byWinget is not null) return byWinget;
        }

        // Fuzzy last resort, deliberately strict: a wrong match produces confidently
        // wrong version data, which is worse than reporting nothing.
        CatalogEntry? best = null;
        double bestScore = 0;
        foreach (var entry in _all)
        {
            foreach (var key in entry.Match)
            {
                var score = NameNormalizer.Similarity(app.NormalizedName, NameNormalizer.Normalize(key));
                if (score > bestScore) { bestScore = score; best = entry; }
            }
        }
        return bestScore >= 0.92 ? best : null;
    }

    // ---- Auto-discovery (Phase 4) ---------------------------------------

    private static readonly Regex GitHubUrl = new(
        @"github\.com/([A-Za-z0-9][A-Za-z0-9\-_.]*)/([A-Za-z0-9][A-Za-z0-9\-_.]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Extracts an "owner/repo" pair from any URL the scanners collected. Registry
    /// URLInfoAbout/HelpLink values and winget's release-notes URLs both point at GitHub
    /// for a large share of open-source software, which covers products the catalog
    /// has never heard of.
    /// </summary>
    public static string? DiscoverGitHubRepo(params string?[] urls)
    {
        foreach (var url in urls)
        {
            if (string.IsNullOrWhiteSpace(url)) continue;
            var m = GitHubUrl.Match(url);
            if (!m.Success) continue;

            var owner = m.Groups[1].Value;
            var repo = m.Groups[2].Value.TrimEnd('.');
            if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo[..^4];

            // These paths are GitHub's own, not a project's.
            if (owner is "orgs" or "users" or "sponsors" or "topics" or "features"
                or "marketplace" or "apps" or "site" or "about") continue;
            if (repo.Length == 0) continue;

            return $"{owner}/{repo}";
        }
        return null;
    }

    private static readonly Regex SeriesSuffix = new(
        @"\.(\d+\.\d+)$", RegexOptions.Compiled);

    /// <summary>
    /// Rejects a catalog winget ID whose version series contradicts what is installed.
    ///
    /// Several products ship one winget package per release line — Python.Python.3.13,
    /// Python.Python.3.14, Microsoft.WindowsAppRuntime.1.6 — so a single hardcoded ID is
    /// only right until the next series ships. A stale one is worse than none: it compares
    /// the installed build against a different line's latest version, and an update would
    /// install the wrong package.
    /// </summary>
    private static bool SeriesMatchesInstalled(string wingetId, string? installedVersion)
    {
        var m = SeriesSuffix.Match(wingetId);
        if (!m.Success) return true;                       // not a series-pinned ID
        if (string.IsNullOrWhiteSpace(installedVersion)) return true;

        var installed = Core.ParsedVersion.Parse(installedVersion);
        var series = Core.ParsedVersion.Parse(m.Groups[1].Value);
        if (!installed.IsValid || !series.IsValid) return true;

        return installed.Major == series.Major && installed.Minor == series.Minor;
    }

    /// <summary>Fills in identifiers from the catalog plus discovery, without overwriting scan data.</summary>
    public void Enrich(InstalledApp app)
    {
        var entry = Resolve(app);
        if (entry is not null)
        {
            if (entry.Winget is not null && !SeriesMatchesInstalled(entry.Winget, app.InstalledVersion))
                app.ConfidenceNote = null;                 // leave WingetId unset
            else
                app.WingetId ??= entry.Winget;
            app.GitHubRepo ??= entry.GitHub;
            app.Homepage ??= entry.Home;
            app.ChocoId ??= entry.Choco;
            app.ScoopId ??= entry.Scoop;
            if (app.DownloadPageUrl is null && entry.Home is not null)
                app.DownloadPageUrl = entry.Home;
        }

        app.GitHubRepo ??= DiscoverGitHubRepo(app.Homepage, app.DownloadPageUrl, app.LatestNotesUrl);
    }
}

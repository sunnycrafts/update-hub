using System.Text.Json;
using System.Text.RegularExpressions;
using UpdateHub.Core;
using UpdateHub.Models;

namespace UpdateHub.Sources;

/// <summary>
/// The best source we have: GitHub Releases carry an exact published date for every
/// version, which is what makes "you are 2 years 5 months behind" answerable rather than
/// guessed. Also yields release notes and per-architecture download assets.
/// </summary>
public sealed class GitHubProvider : IVersionProvider
{
    public string Name => "GitHub";
    public int Priority => 100;

    private readonly GitHubClient _client;
    public GitHubProvider(GitHubClient client) => _client = client;

    public bool CanHandle(InstalledApp app) => !string.IsNullOrWhiteSpace(app.GitHubRepo);

    public async Task<ProviderResult> QueryAsync(InstalledApp app, bool forceRefresh, CancellationToken ct)
    {
        var result = ProviderResult.Empty(Name);
        var repo = app.GitHubRepo;
        if (string.IsNullOrWhiteSpace(repo)) return result;

        // 100 releases is deep enough to place almost any installed version on the timeline.
        var body = await _client.GetAsync(
            $"/repos/{repo}/releases?per_page=100",
            TimeSpan.FromHours(Math.Max(1, 12)),
            forceRefresh, interactive: forceRefresh, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(body)) return result;

        JsonElement root;
        try { root = JsonDocument.Parse(body).RootElement; }
        catch (JsonException) { return result; }
        if (root.ValueKind != JsonValueKind.Array) return result;

        foreach (var rel in root.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            if (GetBool(rel, "draft")) continue;

            var tag = GetString(rel, "tag_name") ?? GetString(rel, "name");
            if (string.IsNullOrWhiteSpace(tag)) continue;

            var version = CleanTag(tag);
            if (version.Length == 0 || !LooksLikeVersion(version)) continue;

            var notes = GetString(rel, "body");
            var info = new ReleaseInfo
            {
                Version = version,
                ReleaseDate = GetDate(rel, "published_at") ?? GetDate(rel, "created_at"),
                Precision = DatePrecision.Exact,
                Notes = notes,
                NotesUrl = GetString(rel, "html_url"),
                Source = "github",
                IsPrerelease = GetBool(rel, "prerelease"),
                IsSecurityRelease = LooksLikeSecurityRelease(notes, GetString(rel, "name")),
                DownloadUrl = PickAsset(rel),
            };
            result.History.Add(info);
        }

        if (result.History.Count == 0) return result;

        // The newest stable release is the one to compare against; a prerelease is not
        // something a user is "behind" on.
        var latest = result.History
            .Where(h => !h.IsPrerelease)
            .OrderByDescending(h => VersionComparer.SortKey(h.Version), StringComparer.Ordinal)
            .FirstOrDefault() ?? result.History[0];

        result.LatestVersion = latest.Version;
        result.LatestReleaseDate = latest.ReleaseDate;
        result.Precision = DatePrecision.Exact;
        result.Notes = latest.Notes;
        result.NotesUrl = latest.NotesUrl;
        result.DownloadUrl = latest.DownloadUrl;
        result.DownloadPageUrl = $"https://github.com/{repo}/releases/latest";
        result.Homepage = $"https://github.com/{repo}";

        return result;
    }

    private static readonly Regex ProductPrefix = new(
        @"^[A-Za-z][A-Za-z0-9._ ]*?[-_/]v?(?=\d)", RegexOptions.Compiled);

    /// <summary>
    /// Release tags carry prefixes: "v1.2.3", "release-1.2.3", "app@1.2.3", and
    /// product-named forms like "Audacity-3.7.8" that would otherwise be shown to the
    /// user verbatim as the latest version.
    /// </summary>
    private static string CleanTag(string tag)
    {
        var t = tag.Trim();
        int at = t.LastIndexOf('@');
        if (at >= 0 && at < t.Length - 1) t = t[(at + 1)..];

        foreach (var prefix in new[] { "release-", "releases/", "version-", "v." })
            if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) t = t[prefix.Length..];

        // Strip any leading word joined to the number by - or _ ("Audacity-3.7.8").
        t = ProductPrefix.Replace(t, "");

        if (t.Length > 1 && (t[0] == 'v' || t[0] == 'V') && char.IsDigit(t[1])) t = t[1..];
        return t.Trim();
    }

    private static readonly Regex HexHash = new(
        @"^[0-9a-f]{7,40}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VersionShape = new(
        @"^\d+(\.\d+)*", RegexOptions.Compiled);

    /// <summary>
    /// Rejects tags that are not versions at all. Some projects tag with a commit SHA, and
    /// "4114c11a9ec5320e" happily parses as 4114.11.9.5320 — which then gets shown to the
    /// user as the latest available version.
    /// </summary>
    private static bool LooksLikeVersion(string candidate)
    {
        if (HexHash.IsMatch(candidate) && candidate.Any(char.IsLetter)) return false;
        if (!VersionShape.IsMatch(candidate)) return false;

        // A version's leading number is a major, not a build stamp; five or more digits
        // before the first dot means this is an identifier of some other kind.
        var head = candidate.Split('.', '-', '_')[0];
        if (head.Length > 4 && head.All(char.IsDigit)) return false;

        return true;
    }

    private static readonly Regex SecurityHint = new(
        @"\b(CVE-\d{4}-\d{4,}|security (fix|update|release|advisory)|vulnerabilit(y|ies)|GHSA-)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool LooksLikeSecurityRelease(params string?[] texts) =>
        texts.Any(t => !string.IsNullOrWhiteSpace(t) && SecurityHint.IsMatch(t!));

    /// <summary>
    /// Chooses the installer asset matching this machine's architecture, so an ARM64 box
    /// is not handed an x64 build. Falls back through x64 to whatever installer exists.
    /// </summary>
    private static string? PickAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        var candidates = new List<(string Name, string Url)>();
        foreach (var a in assets.EnumerateArray())
        {
            var name = GetString(a, "name");
            var url = GetString(a, "browser_download_url");
            if (name is null || url is null) continue;
            candidates.Add((name.ToLowerInvariant(), url));
        }
        if (candidates.Count == 0) return null;

        var arch = ArchitectureHelper.Current;
        string[] archTokens = arch switch
        {
            "arm64" => new[] { "arm64", "aarch64" },
            "x86" => new[] { "x86", "win32", "ia32" },
            _ => new[] { "x64", "amd64", "win64", "x86_64" },
        };

        bool IsInstaller(string n) =>
            n.EndsWith(".exe") || n.EndsWith(".msi") || n.EndsWith(".msix") || n.EndsWith(".appx");

        // Architecture-matched installer.
        foreach (var (n, u) in candidates)
            if (IsInstaller(n) && n.Contains("win") && archTokens.Any(t => n.Contains(t))) return u;
        foreach (var (n, u) in candidates)
            if (IsInstaller(n) && archTokens.Any(t => n.Contains(t))) return u;
        // Any Windows installer.
        foreach (var (n, u) in candidates)
            if (IsInstaller(n) && (n.Contains("win") || n.Contains("setup"))) return u;
        foreach (var (n, u) in candidates)
            if (IsInstaller(n)) return u;

        return null;
    }

    private static string? GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool GetBool(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTime? GetDate(JsonElement el, string prop)
    {
        var s = GetString(el, prop);
        return s is not null && DateTime.TryParse(s, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToLocalTime() : null;
    }
}

/// <summary>Resolves the architecture used to pick installers.</summary>
public static class ArchitectureHelper
{
    public static string Current => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        _ => "x64",
    };

    public static string Display => Current switch
    {
        "arm64" => "ARM64",
        "x86" => "x86 (32-bit)",
        _ => "x64 (64-bit)",
    };
}

using System.Text.Json;
using System.Text.RegularExpressions;
using UpdateHub.Core;
using UpdateHub.Models;
using UpdateHub.Services;

namespace UpdateHub.Sources;

/// <summary>
/// Two-part provider. `winget show` runs locally and costs nothing, giving the latest
/// version plus publisher metadata. Release *dates* come from the winget-pkgs repository:
/// a single commits query against a package folder returns up to a hundred commits, and
/// each commit message names the version it added — so one HTTP request reconstructs a
/// dated history dozens of versions deep instead of one request per version.
///
/// Dates are the manifest's landing date, which trails the vendor's own release by days
/// to a couple of weeks, so everything here is reported as Approximate.
/// </summary>
public sealed class WingetProvider : IVersionProvider
{
    public string Name => "Winget";
    public int Priority => 80;

    private readonly GitHubClient _github;
    private readonly Data.CacheStore _cache;
    private readonly LogService _log;

    public WingetProvider(GitHubClient github, Data.CacheStore cache, LogService log)
    {
        _github = github;
        _cache = cache;
        _log = log;
    }

    public bool CanHandle(InstalledApp app) => !string.IsNullOrWhiteSpace(app.WingetId);

    public async Task<ProviderResult> QueryAsync(InstalledApp app, bool forceRefresh, CancellationToken ct)
    {
        var result = ProviderResult.Empty(Name);
        var id = app.WingetId;
        if (string.IsNullOrWhiteSpace(id)) return result;

        await ReadWingetShowAsync(id, result, forceRefresh, ct).ConfigureAwait(false);
        await ReadManifestHistoryAsync(id, result, forceRefresh, ct).ConfigureAwait(false);

        return result;
    }

    // ---- winget show ----------------------------------------------------

    private async Task ReadWingetShowAsync(
        string id, ProviderResult result, bool forceRefresh, CancellationToken ct)
    {
        var cacheKey = $"wingetshow:{id}";
        string? text = forceRefresh ? null : _cache.GetFresh(cacheKey);

        if (text is null)
        {
            var r = await ProcessRunner.WingetAsync(
                $"show --id {Quote(id)} --exact --disable-interactivity --accept-source-agreements",
                ct, 60_000).ConfigureAwait(false);
            if (!r.Ok || r.StdOut.Length == 0) return;
            text = r.StdOut;
            _cache.Set(cacheKey, text, TimeSpan.FromHours(12));
        }

        var fields = ParseShowOutput(text);

        if (fields.TryGetValue("Version", out var version) && version.Length > 0)
        {
            result.LatestVersion = version;
            // winget show alone gives no date; the manifest history fills that in.
            result.Precision = DatePrecision.Unknown;
        }
        if (fields.TryGetValue("Publisher", out var pub)) result.Publisher = pub;
        if (fields.TryGetValue("Homepage", out var home)) result.Homepage = home;
        else if (fields.TryGetValue("Publisher Url", out var purl)) result.Homepage = purl;
        if (fields.TryGetValue("Release Notes", out var notes)) result.Notes = notes;
        if (fields.TryGetValue("Release Notes Url", out var notesUrl))
        {
            result.NotesUrl = notesUrl;
            result.DownloadPageUrl ??= notesUrl;
        }
        result.DownloadPageUrl ??= result.Homepage;
    }

    /// <summary>
    /// `winget show` prints "Key: value" lines with indented continuations. Values that
    /// wrap are joined so release notes survive intact.
    /// </summary>
    public static Dictionary<string, string> ParseShowOutput(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? currentKey = null;
        var buffer = new System.Text.StringBuilder();

        void Flush()
        {
            if (currentKey is not null)
            {
                var v = buffer.ToString().Trim();
                if (v.Length > 0) fields[currentKey] = v;
            }
            buffer.Clear();
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;

            // "Found 7-Zip [7zip.7zip]" header line.
            if (line.StartsWith("Found ", StringComparison.Ordinal)) continue;

            bool indented = line.StartsWith("  ", StringComparison.Ordinal);
            int colon = line.IndexOf(':');

            // A top-level "Key: value" starts a new field. Indented lines continue the
            // current one, except for the nested key/value blocks winget uses for
            // Documentation and Tags, which we do not need.
            if (!indented && colon > 0 && colon < 40)
            {
                Flush();
                currentKey = line[..colon].Trim();
                buffer.Append(line[(colon + 1)..].Trim());
            }
            else if (currentKey is not null && indented)
            {
                if (buffer.Length > 0) buffer.Append('\n');
                buffer.Append(line.Trim());
            }
        }
        Flush();
        return fields;
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    // ---- winget-pkgs manifest history -----------------------------------

    private static readonly Regex VersionInCommit = new(
        @"version\s+([0-9][^\s(),\[\]]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Turns a winget ID into its manifest path. Every dot in the ID becomes a directory,
    /// under a first-letter bucket: "VideoLAN.VLC" -> "manifests/v/VideoLAN/VLC".
    /// </summary>
    public static string? ManifestPath(string wingetId)
    {
        if (string.IsNullOrWhiteSpace(wingetId)) return null;
        var parts = wingetId.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        var bucket = char.ToLowerInvariant(wingetId[0]);
        if (!char.IsLetterOrDigit(bucket)) return null;
        return $"manifests/{bucket}/{string.Join('/', parts)}";
    }

    private async Task ReadManifestHistoryAsync(
        string id, ProviderResult result, bool forceRefresh, CancellationToken ct)
    {
        var path = ManifestPath(id);
        if (path is null) return;

        var body = await _github.GetAsync(
            $"/repos/microsoft/winget-pkgs/commits?path={Uri.EscapeDataString(path)}&per_page=100",
            // Manifest history is append-only, so a long TTL costs nothing in accuracy.
            TimeSpan.FromDays(3),
            forceRefresh, interactive: forceRefresh, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(body)) return;

        JsonElement root;
        try { root = JsonDocument.Parse(body).RootElement; }
        catch (JsonException) { return; }
        if (root.ValueKind != JsonValueKind.Array) return;

        // Commits arrive newest-first. Overwriting as we go leaves each version mapped to
        // its *oldest* commit — the one that first added the manifest, which is the date
        // closest to the actual release. Later commits are metadata edits and would
        // otherwise report a version as far newer than it is.
        var dates = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        foreach (var commit in root.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            if (!commit.TryGetProperty("commit", out var c)) continue;

            var message = c.TryGetProperty("message", out var m) ? m.GetString() : null;
            if (string.IsNullOrWhiteSpace(message)) continue;
            var firstLine = message.Split('\n')[0];

            var when = ReadCommitDate(c);
            if (when is null) continue;

            var match = VersionInCommit.Match(firstLine);
            if (!match.Success) continue;

            var version = match.Groups[1].Value.Trim().TrimEnd('.', ',');
            if (version.Length == 0) continue;

            // "Remove" commits delete a manifest and must not register as a release.
            if (firstLine.StartsWith("Remove", StringComparison.OrdinalIgnoreCase)) continue;

            dates[version] = when.Value;
        }

        if (dates.Count == 0) return;

        foreach (var (version, date) in dates)
        {
            result.History.Add(new ReleaseInfo
            {
                Version = version,
                ReleaseDate = date,
                Precision = DatePrecision.Approximate,
                Source = "winget-pkgs",
                IsPrerelease = LooksPrerelease(version),
            });
        }

        // Only supply a latest-version verdict if winget show did not already.
        if (result.LatestVersion is null)
        {
            var newest = result.History
                .Where(h => !h.IsPrerelease)
                .OrderByDescending(h => VersionComparer.SortKey(h.Version), StringComparer.Ordinal)
                .FirstOrDefault();
            if (newest is not null)
            {
                result.LatestVersion = newest.Version;
                result.LatestReleaseDate = newest.ReleaseDate;
                result.Precision = DatePrecision.Approximate;
            }
        }
        else
        {
            var match = result.History.FirstOrDefault(h =>
                VersionComparer.Compare(h.Version, result.LatestVersion) == 0);
            if (match?.ReleaseDate is not null)
            {
                result.LatestReleaseDate = match.ReleaseDate;
                result.Precision = DatePrecision.Approximate;
            }
        }

        _log.Info($"winget-pkgs history for {id}: {dates.Count} versions");
    }

    private static DateTime? ReadCommitDate(JsonElement commit)
    {
        foreach (var who in new[] { "committer", "author" })
        {
            if (!commit.TryGetProperty(who, out var person)) continue;
            if (!person.TryGetProperty("date", out var d)) continue;
            var s = d.GetString();
            if (s is not null && DateTime.TryParse(s, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                return parsed.ToLocalTime().Date;
        }
        return null;
    }

    private static bool LooksPrerelease(string version)
    {
        var v = version.ToLowerInvariant();
        return v.Contains("nightly") || v.Contains("beta") || v.Contains("alpha")
            || v.Contains("-rc") || v.Contains("preview") || v.Contains("insider")
            || v.Contains("canary") || v.Contains("dev");
    }
}

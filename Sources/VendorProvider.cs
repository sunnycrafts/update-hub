using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using UpdateHub.Core;
using UpdateHub.Models;
using UpdateHub.Services;

namespace UpdateHub.Sources;

/// <summary>
/// A vendor rule described entirely in JSON. Plugins are data, not code: a rule names an
/// endpoint and how to read a version out of it, so a user can add support for new
/// software by dropping a file in the Plugins folder without any assembly loading.
/// </summary>
public sealed class VendorRule
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("match")] public List<string> Match { get; set; } = new();
    [JsonPropertyName("url")] public string Url { get; set; } = "";

    /// <summary>Dotted path into a JSON response, e.g. "versions[0].version".</summary>
    [JsonPropertyName("json")] public string? JsonPath { get; set; }

    /// <summary>Regex with one capture group, applied to a text/HTML response.</summary>
    [JsonPropertyName("regex")] public string? Regex { get; set; }

    [JsonPropertyName("dateJson")] public string? DateJsonPath { get; set; }
    [JsonPropertyName("dateRegex")] public string? DateRegex { get; set; }
    [JsonPropertyName("dateFormat")] public string? DateFormat { get; set; }

    [JsonPropertyName("downloadPage")] public string? DownloadPage { get; set; }
    [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; set; }
    [JsonPropertyName("notesUrl")] public string? NotesUrl { get; set; }
    [JsonPropertyName("ttlHours")] public int TtlHours { get; set; } = 12;

    [JsonIgnore] public string Origin { get; set; } = "built-in";
}

public sealed class VendorRuleFile
{
    [JsonPropertyName("providers")] public List<VendorRule> Providers { get; set; } = new();
}

/// <summary>
/// Handles software that publishes a version endpoint but no GitHub releases — the large
/// commercial and foundation-run products that a GitHub-only updater cannot see.
/// </summary>
public sealed class VendorProvider : IVersionProvider
{
    public string Name => "Vendor";
    public int Priority => 90;

    private readonly List<VendorRule> _rules = new();
    private readonly Dictionary<string, VendorRule> _byMatch = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _http;
    private readonly Data.CacheStore _cache;
    private readonly LogService _log;

    public int RuleCount => _rules.Count;
    public IReadOnlyList<VendorRule> Rules => _rules;

    public VendorProvider(Data.CacheStore cache, LogService log, string dataRoot)
    {
        _cache = cache;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("UpdateHub/0.5");

        LoadBuiltIn();
        LoadPlugins(dataRoot);
    }

    /// <summary>
    /// Endpoints for major products that ship no GitHub releases. Kept small and
    /// well-known on purpose; everything else belongs in a community plugin file.
    /// </summary>
    private void LoadBuiltIn()
    {
        Add(new VendorRule
        {
            Name = "Mozilla Firefox",
            Match = { "firefox", "mozillafirefox" },
            Url = "https://product-details.mozilla.org/1.0/firefox_versions.json",
            JsonPath = "LATEST_FIREFOX_VERSION",
            DownloadPage = "https://www.mozilla.org/firefox/new/",
            NotesUrl = "https://www.mozilla.org/firefox/releases/",
        });
        Add(new VendorRule
        {
            Name = "Mozilla Thunderbird",
            Match = { "thunderbird", "mozillathunderbird" },
            Url = "https://product-details.mozilla.org/1.0/thunderbird_versions.json",
            JsonPath = "LATEST_THUNDERBIRD_VERSION",
            DownloadPage = "https://www.thunderbird.net/",
        });
        Add(new VendorRule
        {
            Name = "Google Chrome",
            Match = { "googlechrome", "chrome" },
            Url = "https://versionhistory.googleapis.com/v1/chrome/platforms/win64/channels/stable/versions",
            JsonPath = "versions[0].version",
            DownloadPage = "https://www.google.com/chrome/",
        });
    }

    private void Add(VendorRule rule)
    {
        _rules.Add(rule);
        foreach (var m in rule.Match)
        {
            var norm = NameNormalizer.Normalize(m);
            if (norm.Length > 0) _byMatch[norm] = rule;
        }
    }

    private void LoadPlugins(string dataRoot)
    {
        var dir = Path.Combine(dataRoot, "Plugins");
        if (!Directory.Exists(dir)) return;

        foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var file = JsonSerializer.Deserialize<VendorRuleFile>(File.ReadAllText(path));
                if (file?.Providers is null) continue;
                foreach (var rule in file.Providers)
                {
                    // Plugins are user-supplied data; refuse anything that is not a plain
                    // https endpoint so a rule cannot reach the local filesystem.
                    if (!rule.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        _log.Warn($"Plugin rule '{rule.Name}' in {Path.GetFileName(path)} skipped: URL must be https");
                        continue;
                    }
                    rule.Origin = Path.GetFileName(path);
                    Add(rule);
                }
                _log.Info($"Loaded plugin {Path.GetFileName(path)}: {file.Providers.Count} rules");
            }
            catch (Exception ex)
            {
                _log.Warn($"Plugin {Path.GetFileName(path)} failed to load: {ex.Message}");
            }
        }
    }

    public bool CanHandle(InstalledApp app) =>
        app.NormalizedName.Length > 0 && _byMatch.ContainsKey(app.NormalizedName);

    public async Task<ProviderResult> QueryAsync(InstalledApp app, bool forceRefresh, CancellationToken ct)
    {
        var result = ProviderResult.Empty(Name);
        if (!_byMatch.TryGetValue(app.NormalizedName, out var rule)) return result;

        var cacheKey = "vendor:" + rule.Url;
        var body = forceRefresh ? null : _cache.GetFresh(cacheKey);

        if (body is null)
        {
            try
            {
                body = await _http.GetStringAsync(rule.Url, ct).ConfigureAwait(false);
                _cache.Set(cacheKey, body, TimeSpan.FromHours(Math.Max(1, rule.TtlHours)));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.Warn($"Vendor rule '{rule.Name}' failed: {ex.Message}");
                return result;
            }
        }
        else result.FromCache = true;

        result.LatestVersion = Extract(body, rule.JsonPath, rule.Regex);
        if (result.LatestVersion is null) return result;

        var dateText = Extract(body, rule.DateJsonPath, rule.DateRegex);
        if (dateText is not null && TryParseDate(dateText, rule.DateFormat, out var date))
        {
            result.LatestReleaseDate = date;
            result.Precision = DatePrecision.Exact;
        }

        result.DownloadPageUrl = rule.DownloadPage;
        result.DownloadUrl = rule.DownloadUrl;
        result.NotesUrl = rule.NotesUrl;
        result.History.Add(new ReleaseInfo
        {
            Version = result.LatestVersion,
            ReleaseDate = result.LatestReleaseDate,
            Precision = result.Precision,
            NotesUrl = rule.NotesUrl,
            Source = "vendor:" + rule.Name,
        });
        return result;
    }

    private static string? Extract(string body, string? jsonPath, string? regex)
    {
        if (!string.IsNullOrWhiteSpace(jsonPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var value = ReadJsonPath(doc.RootElement, jsonPath!);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            catch (JsonException) { }
        }

        if (!string.IsNullOrWhiteSpace(regex))
        {
            try
            {
                var m = Regex.Match(body, regex!, RegexOptions.IgnoreCase,
                    TimeSpan.FromSeconds(2));
                if (m.Success && m.Groups.Count > 1) return m.Groups[1].Value.Trim();
            }
            catch (RegexMatchTimeoutException) { }
            catch (ArgumentException) { /* malformed pattern in a plugin */ }
        }

        return null;
    }

    /// <summary>Reads "a.b[0].c" out of a JSON document.</summary>
    private static string? ReadJsonPath(JsonElement root, string path)
    {
        var current = root;
        foreach (var rawSegment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = rawSegment;
            var indices = new List<int>();

            int bracket = segment.IndexOf('[');
            if (bracket >= 0)
            {
                foreach (Match m in Regex.Matches(segment[bracket..], @"\[(\d+)\]"))
                    indices.Add(int.Parse(m.Groups[1].Value));
                segment = segment[..bracket];
            }

            if (segment.Length > 0)
            {
                if (current.ValueKind != JsonValueKind.Object) return null;
                if (!current.TryGetProperty(segment, out current)) return null;
            }

            foreach (var i in indices)
            {
                if (current.ValueKind != JsonValueKind.Array) return null;
                if (i >= current.GetArrayLength()) return null;
                current = current[i];
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            _ => null,
        };
    }

    private static bool TryParseDate(string text, string? format, out DateTime date)
    {
        if (!string.IsNullOrWhiteSpace(format) &&
            DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out date)) return true;
        return DateTime.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date);
    }
}

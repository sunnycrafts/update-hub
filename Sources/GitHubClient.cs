using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using UpdateHub.Data;
using UpdateHub.Services;

namespace UpdateHub.Sources;

/// <summary>
/// Shared GitHub API access with the rate limit treated as a first-class budget.
/// Anonymous callers get 60 requests an hour, which a 150-app inventory would burn through
/// instantly, so every response is cached, conditional requests are used where an ETag is
/// known, and the client refuses to spend the last few requests on background work.
/// </summary>
public sealed class GitHubClient
{
    private readonly HttpClient _http;
    private readonly CacheStore _cache;
    private readonly LogService _log;
    private readonly SettingsService _settings;
    private readonly SemaphoreSlim _gate = new(4, 4);

    public int? RateRemaining { get; private set; }
    public int? RateLimit { get; private set; }
    public DateTime? RateResetsAt { get; private set; }

    /// <summary>Requests held back so interactive lookups are not starved by a bulk refresh.</summary>
    private const int ReserveForInteractive = 5;

    public GitHubClient(CacheStore cache, LogService log, SettingsService settings)
    {
        _cache = cache;
        _log = log;
        _settings = settings;
        _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UpdateHub", "0.5"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public bool HasToken => !string.IsNullOrWhiteSpace(_settings.Current.GitHubToken);

    /// <summary>True when we still have budget to spend on a non-interactive lookup.</summary>
    public bool CanSpend(bool interactive) =>
        RateRemaining is null || RateRemaining > (interactive ? 0 : ReserveForInteractive);

    /// <summary>
    /// GETs a GitHub API path, serving from cache when fresh and revalidating with an ETag
    /// otherwise. A 304 costs a request but no quota on GitHub's side and lets us keep the
    /// cached body.
    /// </summary>
    public async Task<string?> GetAsync(
        string path, TimeSpan ttl, bool forceRefresh, bool interactive, CancellationToken ct)
    {
        var cacheKey = "gh:" + path;
        var entry = _cache.Get(cacheKey);

        if (!forceRefresh && entry is { IsFresh: true } && entry.Payload is not null)
            return entry.Payload;

        if (!CanSpend(interactive))
        {
            // Out of budget: stale data beats no data.
            if (entry?.Payload is not null)
            {
                _log.Info($"GitHub budget exhausted, serving stale {path}");
                return entry.Payload;
            }
            return null;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com" + path);

            var token = _settings.Current.GitHubToken;
            if (!string.IsNullOrWhiteSpace(token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());

            if (entry?.ETag is not null && entry.Payload is not null)
                req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(entry.ETag));

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            ReadRateHeaders(resp);

            if (resp.StatusCode == HttpStatusCode.NotModified && entry?.Payload is not null)
            {
                _cache.Touch(cacheKey, ttl);
                return entry.Payload;
            }

            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                _log.Warn($"GitHub rate limited on {path}; resets {RateResetsAt:t}");
                return entry?.Payload;
            }

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                // Cache the miss so we stop asking about a repo that does not exist.
                _cache.Set(cacheKey, null, TimeSpan.FromDays(7));
                return null;
            }

            if (!resp.IsSuccessStatusCode)
            {
                _log.Warn($"GitHub {(int)resp.StatusCode} on {path}");
                return entry?.Payload;
            }

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var etag = resp.Headers.ETag?.Tag;
            _cache.Set(cacheKey, body, ttl, etag);
            return body;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.Warn($"GitHub timeout on {path}");
            return entry?.Payload;
        }
        catch (HttpRequestException ex)
        {
            _log.Warn($"GitHub request failed on {path}: {ex.Message}");
            return entry?.Payload;
        }
        finally { _gate.Release(); }
    }

    private void ReadRateHeaders(HttpResponseMessage resp)
    {
        if (resp.Headers.TryGetValues("X-RateLimit-Remaining", out var rem)
            && int.TryParse(rem.FirstOrDefault(), out var r)) RateRemaining = r;
        if (resp.Headers.TryGetValues("X-RateLimit-Limit", out var lim)
            && int.TryParse(lim.FirstOrDefault(), out var l)) RateLimit = l;
        if (resp.Headers.TryGetValues("X-RateLimit-Reset", out var res)
            && long.TryParse(res.FirstOrDefault(), out var epoch))
            RateResetsAt = DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime;
    }

    public string RateStatusText
    {
        get
        {
            if (RateRemaining is null) return HasToken ? "Authenticated" : "Not yet queried";
            var scope = HasToken ? "token" : "anonymous";
            var reset = RateResetsAt is { } t ? $", resets {t:t}" : "";
            return $"{RateRemaining}/{RateLimit} ({scope}{reset})";
        }
    }
}

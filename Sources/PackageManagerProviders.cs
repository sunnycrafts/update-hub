using UpdateHub.Core;
using UpdateHub.Core.Scanning;
using UpdateHub.Models;

namespace UpdateHub.Sources;

/// <summary>
/// Asks Chocolatey what the newest published package version is. Chocolatey does not
/// expose release dates, so this contributes a version but never a date.
/// </summary>
public sealed class ChocolateyProvider : IVersionProvider
{
    public string Name => "Chocolatey";
    public int Priority => 60;

    private readonly Data.CacheStore _cache;
    public ChocolateyProvider(Data.CacheStore cache) => _cache = cache;

    public bool CanHandle(InstalledApp app) =>
        !string.IsNullOrWhiteSpace(app.ChocoId) && ChocolateyScanner.ChocoExe is not null;

    public async Task<ProviderResult> QueryAsync(InstalledApp app, bool forceRefresh, CancellationToken ct)
    {
        var result = ProviderResult.Empty(Name);
        var id = app.ChocoId;
        var exe = ChocolateyScanner.ChocoExe;
        if (string.IsNullOrWhiteSpace(id) || exe is null) return result;

        var cacheKey = $"choco:{id}";
        var cached = forceRefresh ? null : _cache.GetFresh(cacheKey);
        string output;

        if (cached is not null) { output = cached; result.FromCache = true; }
        else
        {
            var r = await ProcessRunner.RunAsync(exe, $"search {id} --exact -r --no-color", ct, 60_000)
                                       .ConfigureAwait(false);
            if (!r.Ok && r.StdOut.Length == 0) return result;
            output = r.StdOut;
            _cache.Set(cacheKey, output, TimeSpan.FromHours(12));
        }

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split('|');
            if (parts.Length < 2) continue;
            if (!parts[0].Trim().Equals(id, StringComparison.OrdinalIgnoreCase)) continue;
            result.LatestVersion = parts[1].Trim();
            result.DownloadPageUrl = $"https://community.chocolatey.org/packages/{id}";
            break;
        }
        return result;
    }
}

/// <summary>Reads the newest version Scoop's local bucket manifests advertise.</summary>
public sealed class ScoopProvider : IVersionProvider
{
    public string Name => "Scoop";
    public int Priority => 60;

    public bool CanHandle(InstalledApp app) =>
        !string.IsNullOrWhiteSpace(app.ScoopId) && ScoopScanner.ScoopRoot is not null;

    public Task<ProviderResult> QueryAsync(InstalledApp app, bool forceRefresh, CancellationToken ct)
    {
        var result = ProviderResult.Empty(Name);
        var root = ScoopScanner.ScoopRoot;
        var id = app.ScoopId;
        if (root is null || string.IsNullOrWhiteSpace(id)) return Task.FromResult(result);

        var bucketsDir = System.IO.Path.Combine(root, "buckets");
        if (!System.IO.Directory.Exists(bucketsDir)) return Task.FromResult(result);

        foreach (var bucket in System.IO.Directory.EnumerateDirectories(bucketsDir))
        {
            ct.ThrowIfCancellationRequested();
            // Buckets keep manifests either at the root or under a bucket/ subfolder.
            foreach (var sub in new[] { System.IO.Path.Combine(bucket, "bucket"), bucket })
            {
                var manifest = System.IO.Path.Combine(sub, id + ".json");
                if (!System.IO.File.Exists(manifest)) continue;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(manifest));
                    if (doc.RootElement.TryGetProperty("version", out var v))
                        result.LatestVersion = v.GetString();
                    if (doc.RootElement.TryGetProperty("homepage", out var h))
                        result.Homepage = h.GetString();
                    return Task.FromResult(result);
                }
                catch (System.Text.Json.JsonException) { }
                catch (System.IO.IOException) { }
            }
        }
        return Task.FromResult(result);
    }
}

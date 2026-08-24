using System.IO;
using System.Text.Json;
using UpdateHub.Models;

namespace UpdateHub.Core.Scanning;

/// <summary>Reads Chocolatey's local package list, when Chocolatey is installed.</summary>
public sealed class ChocolateyScanner : IAppScanner
{
    public string Name => "Chocolatey";
    public DetectionSource Source => DetectionSource.Chocolatey;

    public static string? ChocoExe
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("ChocolateyInstall")
                       ?? @"C:\ProgramData\chocolatey";
            var exe = Path.Combine(root, "bin", "choco.exe");
            return File.Exists(exe) ? exe : null;
        }
    }

    public async Task<List<InstalledApp>> ScanAsync(CancellationToken ct)
    {
        var results = new List<InstalledApp>();
        var exe = ChocoExe;
        if (exe is null) return results;

        // -r gives machine-readable "name|version" rows with no header or box drawing.
        var r = await ProcessRunner.RunAsync(exe, "list --local-only -r --no-color", ct, 60_000)
                                   .ConfigureAwait(false);
        if (!r.Ok && string.IsNullOrWhiteSpace(r.StdOut)) return results;

        foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            ct.ThrowIfCancellationRequested();
            var parts = line.Trim().Split('|');
            if (parts.Length < 2) continue;
            var id = parts[0].Trim();
            var version = parts[1].Trim();
            if (id.Length == 0 || id.Contains(' ')) continue;

            results.Add(new InstalledApp
            {
                Key = "choco:" + id.ToLowerInvariant(),
                Name = NameNormalizer.Clean(id),
                NormalizedName = NameNormalizer.Normalize(id),
                InstalledVersion = version,
                ChocoId = id,
                Sources = DetectionSource.Chocolatey,
                UpdateMethod = UpdateMethod.Chocolatey,
            });
        }
        return results;
    }
}

/// <summary>
/// Reads Scoop's install tree directly. Scoop is plain JSON on disk, so this avoids
/// paying for a PowerShell round trip.
/// </summary>
public sealed class ScoopScanner : IAppScanner
{
    public string Name => "Scoop";
    public DetectionSource Source => DetectionSource.Scoop;

    public static string? ScoopRoot
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("SCOOP");
            if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;
            var def = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop");
            return Directory.Exists(def) ? def : null;
        }
    }

    public Task<List<InstalledApp>> ScanAsync(CancellationToken ct)
    {
        var results = new List<InstalledApp>();
        var root = ScoopRoot;
        if (root is null) return Task.FromResult(results);

        var appsDir = Path.Combine(root, "apps");
        if (!Directory.Exists(appsDir)) return Task.FromResult(results);

        foreach (var appDir in Directory.EnumerateDirectories(appsDir))
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileName(appDir);
            var current = Path.Combine(appDir, "current");
            if (!Directory.Exists(current)) continue;

            string version = "";
            string? homepage = null;
            var manifest = Path.Combine(current, "manifest.json");
            if (File.Exists(manifest))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                    if (doc.RootElement.TryGetProperty("version", out var v))
                        version = v.GetString() ?? "";
                    if (doc.RootElement.TryGetProperty("homepage", out var h))
                        homepage = h.GetString();
                }
                catch (JsonException) { }
                catch (IOException) { }
            }

            if (version.Length == 0)
            {
                // `current` is a junction onto the versioned folder; its target names the version.
                try
                {
                    var target = Directory.ResolveLinkTarget(current, true);
                    if (target is not null) version = Path.GetFileName(target.FullName);
                }
                catch { }
            }

            results.Add(new InstalledApp
            {
                Key = "scoop:" + id.ToLowerInvariant(),
                Name = NameNormalizer.Clean(id),
                NormalizedName = NameNormalizer.Normalize(id),
                InstalledVersion = version,
                ScoopId = id,
                Homepage = homepage,
                InstallLocation = current,
                Sources = DetectionSource.Scoop,
                UpdateMethod = UpdateMethod.Scoop,
            });
        }

        return Task.FromResult(results);
    }
}

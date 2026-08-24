using System.Diagnostics;
using System.IO;
using UpdateHub.Models;

namespace UpdateHub.Core.Scanning;

/// <summary>
/// Finds standalone executables that never registered an uninstall entry. Version data
/// comes from the PE version resource, which is all a portable app exposes.
/// </summary>
public sealed class PortableScanner : IAppScanner
{
    public string Name => "Portable apps";
    public DetectionSource Source => DetectionSource.Portable;

    private readonly List<string> _roots;
    private readonly bool _isCustom;
    private const int MaxDepth = 3;
    private const int MaxFiles = 4000;

    public PortableScanner(IEnumerable<string>? customFolders = null)
    {
        if (customFolders is not null)
        {
            _roots = customFolders.Where(Directory.Exists).ToList();
            _isCustom = true;
        }
        else
        {
            _roots = DefaultRoots();
            _isCustom = false;
        }
    }

    public DetectionSource EffectiveSource =>
        _isCustom ? DetectionSource.CustomFolder : DetectionSource.Portable;

    private static List<string> DefaultRoots()
    {
        var roots = new List<string>();
        void Add(string p) { if (Directory.Exists(p)) roots.Add(p); }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"));
        Add(Path.Combine(profile, "Tools"));
        Add(Path.Combine(profile, "Apps"));
        Add(Path.Combine(profile, "PortableApps"));
        Add(@"C:\PortableApps");
        Add(@"C:\Tools");
        return roots;
    }

    public Task<List<InstalledApp>> ScanAsync(CancellationToken ct)
    {
        var results = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int budget = MaxFiles;

        foreach (var root in _roots)
        {
            ct.ThrowIfCancellationRequested();
            Walk(root, 0, results, seen, ref budget, ct);
            if (budget <= 0) break;
        }

        return Task.FromResult(results);
    }

    private void Walk(string dir, int depth, List<InstalledApp> results,
        HashSet<string> seen, ref int budget, CancellationToken ct)
    {
        if (depth > MaxDepth || budget <= 0) return;
        ct.ThrowIfCancellationRequested();

        string[] files;
        // DirectoryNotFoundException and PathTooLongException both derive from IOException,
        // so the broad catch has to come last.
        try { files = Directory.GetFiles(dir, "*.exe"); }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }

        foreach (var file in files)
        {
            if (--budget <= 0) return;
            var app = FromExecutable(file);
            if (app is null) continue;
            if (!seen.Add(app.Key)) continue;
            results.Add(app);
        }

        string[] subdirs;
        try { subdirs = Directory.GetDirectories(dir); }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }

        foreach (var sub in subdirs)
        {
            var leaf = Path.GetFileName(sub);
            // Skip the folders that only ever hold payload, not the app itself.
            if (leaf.StartsWith('.') || leaf is "node_modules" or "__pycache__"
                or "cache" or "Cache" or "logs" or "Logs" or "temp" or "Temp") continue;
            Walk(sub, depth + 1, results, seen, ref budget, ct);
            if (budget <= 0) return;
        }
    }

    private InstalledApp? FromExecutable(string path)
    {
        FileVersionInfo fvi;
        FileInfo fi;
        try
        {
            fi = new FileInfo(path);
            // Sub-megabyte executables are overwhelmingly helpers, updaters and stubs.
            if (fi.Length < 512 * 1024) return null;
            fvi = FileVersionInfo.GetVersionInfo(path);
        }
        catch { return null; }

        var name = FirstNonEmpty(fvi.ProductName, fvi.FileDescription, Path.GetFileNameWithoutExtension(path));
        if (string.IsNullOrWhiteSpace(name)) return null;

        // Uninstallers and updaters are not products in their own right.
        var lower = Path.GetFileName(path).ToLowerInvariant();
        if (lower.Contains("uninst") || lower.Contains("setup") || lower.Contains("installer")
            || lower is "update.exe" or "updater.exe" or "crashpad_handler.exe") return null;

        var version = FirstNonEmpty(fvi.ProductVersion, fvi.FileVersion) ?? "";

        return new InstalledApp
        {
            Key = "portable:" + path.ToLowerInvariant(),
            Name = NameNormalizer.Clean(name),
            NormalizedName = NameNormalizer.Normalize(name),
            Publisher = string.IsNullOrWhiteSpace(fvi.CompanyName) ? null : fvi.CompanyName.Trim(),
            InstalledVersion = version.Trim(),
            ExePath = path,
            InstallLocation = Path.GetDirectoryName(path),
            Sources = EffectiveSource,
            InstallDate = fi.LastWriteTime,
            EstimatedSizeKb = fi.Length / 1024,
            // Nothing to upgrade against until a provider identifies the product.
            UpdateMethod = UpdateMethod.Manual,
        };
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}

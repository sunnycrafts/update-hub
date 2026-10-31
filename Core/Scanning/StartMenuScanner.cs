using System.Diagnostics;
using System.IO;
using UpdateHub.Models;

namespace UpdateHub.Core.Scanning;

/// <summary>
/// Reads the Start Menu shortcuts (all-users and current-user) and resolves each to the
/// program it launches. Comparing that list with the installed-programs list answers
/// three questions the registry alone cannot: which installed programs have no Start Menu
/// entry, which programs are reachable only from the Start Menu (portable or hand-copied
/// apps with no uninstall record), and which are in both.
/// </summary>
public sealed class StartMenuScanner : IAppScanner
{
    public string Name => "Start Menu";
    public DetectionSource Source => DetectionSource.StartMenu;

    private static readonly string[] Roots =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Microsoft\Windows\Start Menu\Programs"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Start Menu\Programs"),
    };

    public Task<List<InstalledApp>> ScanAsync(CancellationToken ct) =>
        Task.Run(() => Scan(ct), ct);

    private static List<InstalledApp> Scan(CancellationToken ct)
    {
        var results = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return results;
        dynamic? shell = null;
        try { shell = Activator.CreateInstance(shellType); } catch { }
        if (shell is null) return results;

        foreach (var root in Roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> links;
            try { links = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).ToList(); }
            catch (Exception) { continue; }

            foreach (var link in links)
            {
                ct.ThrowIfCancellationRequested();
                var label = Path.GetFileNameWithoutExtension(link);
                if (IsNoiseLabel(label)) continue;

                string? target;
                try { target = (string?)shell.CreateShortcut(link).TargetPath; }
                catch (Exception) { continue; }

                if (string.IsNullOrWhiteSpace(target)
                    || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(target)
                    || target.StartsWith(windows, StringComparison.OrdinalIgnoreCase)
                    || !seen.Add(target)) continue;

                string version = "", publisher = "";
                long sizeKb = 0;
                try
                {
                    var fvi = FileVersionInfo.GetVersionInfo(target);
                    version = (fvi.ProductVersion ?? fvi.FileVersion ?? "").Trim();
                    publisher = (fvi.CompanyName ?? "").Trim();
                    sizeKb = new FileInfo(target).Length / 1024;
                }
                catch (Exception) { }

                results.Add(new InstalledApp
                {
                    Key = "startmenu:" + target.ToLowerInvariant(),
                    Name = NameNormalizer.Clean(label),
                    NormalizedName = NameNormalizer.Normalize(label),
                    Publisher = publisher.Length == 0 ? null : publisher,
                    InstalledVersion = version,
                    ExePath = target,
                    InstallLocation = Path.GetDirectoryName(target),
                    EstimatedSizeKb = sizeKb,
                    Sources = DetectionSource.StartMenu,
                    UpdateMethod = UpdateMethod.Manual,
                });
            }
        }
        return results;
    }

    /// <summary>Shortcuts that are not programs: uninstallers, help, readmes, websites.</summary>
    private static bool IsNoiseLabel(string label)
    {
        var l = label.ToLowerInvariant();
        return l.Contains("uninstall") || l.StartsWith("readme") || l.Contains("release notes")
            || l.EndsWith(" help") || l.Contains("documentation") || l.Contains("website")
            || l.Contains("license");
    }
}

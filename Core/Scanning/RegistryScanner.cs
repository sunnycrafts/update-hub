using System.Globalization;
using System.IO;
using Microsoft.Win32;
using UpdateHub.Models;

namespace UpdateHub.Core.Scanning;

/// <summary>
/// Reads Add/Remove Programs from all three uninstall hives. This is the authoritative
/// inventory: it is the only source that carries publisher, install date, install location
/// and uninstall strings, which winget's own listing omits.
/// </summary>
public sealed class RegistryScanner : IAppScanner
{
    public string Name => "Registry";
    public DetectionSource Source => DetectionSource.Registry;

    private readonly bool _includeSystemComponents;
    public RegistryScanner(bool includeSystemComponents) => _includeSystemComponents = includeSystemComponents;

    private static readonly (RegistryHive Hive, RegistryView View, string Path)[] Locations =
    {
        (RegistryHive.LocalMachine, RegistryView.Registry64,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        (RegistryHive.LocalMachine, RegistryView.Registry32,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        (RegistryHive.CurrentUser, RegistryView.Registry64,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
    };

    public Task<List<InstalledApp>> ScanAsync(CancellationToken ct)
    {
        var results = new List<InstalledApp>(256);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hive, view, path) in Locations)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(path);
                if (uninstall is null) continue;

                foreach (var subName in uninstall.GetSubKeyNames())
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        using var key = uninstall.OpenSubKey(subName);
                        if (key is null) continue;
                        var app = ReadEntry(key, subName, hive, view);
                        if (app is null) continue;
                        // The same product can appear in both the 32- and 64-bit views.
                        if (!seen.Add(app.Key)) continue;
                        results.Add(app);
                    }
                    catch (System.Security.SecurityException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
        }

        return Task.FromResult(results);
    }

    private InstalledApp? ReadEntry(RegistryKey key, string subName, RegistryHive hive, RegistryView view)
    {
        var displayName = Str(key, "DisplayName");
        if (string.IsNullOrWhiteSpace(displayName)) return null;

        // Windows/Office patches are not products; they clutter the list and have no
        // meaningful "latest version" of their own.
        var releaseType = Str(key, "ReleaseType");
        if (releaseType is "Update" or "Security Update" or "Hotfix" or "ServicePack") return null;
        if (displayName.StartsWith("Update for ", StringComparison.OrdinalIgnoreCase)) return null;
        if (displayName.StartsWith("Security Update for ", StringComparison.OrdinalIgnoreCase)) return null;
        if (displayName.StartsWith("Hotfix for ", StringComparison.OrdinalIgnoreCase)) return null;
        // KB patch entries: "... (KB1234567)".
        if (System.Text.RegularExpressions.Regex.IsMatch(displayName, @"\(KB\d{6,}\)")) return null;

        // Entries owned by a parent product are components, not separate installs.
        if (!string.IsNullOrEmpty(Str(key, "ParentKeyName"))) return null;
        if (Int(key, "SystemComponent") == 1 && !_includeSystemComponents) return null;

        var version = Str(key, "DisplayVersion") ?? "";
        var publisher = Str(key, "Publisher");

        var app = new InstalledApp
        {
            Key = MakeKey(hive, view, subName),
            Name = NameNormalizer.Clean(displayName),
            Publisher = publisher,
            NormalizedName = NameNormalizer.Normalize(displayName),
            InstalledVersion = version,
            Sources = DetectionSource.Registry,
            RegistryKey = $@"{(hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU")}\...\Uninstall\{subName}",
            InstallLocation = Str(key, "InstallLocation"),
            UninstallString = Str(key, "QuietUninstallString") ?? Str(key, "UninstallString"),
            Homepage = FirstUrl(Str(key, "URLInfoAbout"), Str(key, "HelpLink"), Str(key, "URLUpdateInfo")),
            InstallDate = ParseInstallDate(Str(key, "InstallDate")),
            EstimatedSizeKb = Int(key, "EstimatedSize") ?? 0,
            IsSystemComponent = Int(key, "SystemComponent") == 1,
            ExePath = ResolveIconExe(Str(key, "DisplayIcon")),
        };

        // A GUID subkey is an MSI product code — useful later for MSI-based updates.
        if (subName.StartsWith('{') && subName.EndsWith('}'))
            app.ConfidenceNote = null;

        if (app.InstallDate is null && app.ExePath is not null)
            app.InstallDate = FileDate(app.ExePath);

        return app;
    }

    private static string MakeKey(RegistryHive hive, RegistryView view, string subName)
    {
        var scope = hive == RegistryHive.LocalMachine
            ? (view == RegistryView.Registry32 ? "HKLM32" : "HKLM64")
            : "HKCU";
        return $"reg:{scope}:{subName}";
    }

    private static string? Str(RegistryKey key, string name)
    {
        try
        {
            var v = key.GetValue(name);
            var s = v?.ToString();
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }
        catch { return null; }
    }

    private static long? Int(RegistryKey key, string name)
    {
        try
        {
            var v = key.GetValue(name);
            if (v is int i) return i;
            if (v is long l) return l;
            return v is not null && long.TryParse(v.ToString(), out var p) ? p : null;
        }
        catch { return null; }
    }

    /// <summary>Registry InstallDate is "YYYYMMDD"; a few installers write a locale date instead.</summary>
    private static DateTime? ParseInstallDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (raw.Length == 8 && DateTime.TryParseExact(raw, "yyyyMMdd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        return DateTime.TryParse(raw, CultureInfo.CurrentCulture, DateTimeStyles.None, out var d2) ? d2 : null;
    }

    /// <summary>DisplayIcon is usually "C:\path\app.exe,0" — strip the icon index.</summary>
    private static string? ResolveIconExe(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        var s = displayIcon.Trim().Trim('"');
        int comma = s.LastIndexOf(',');
        if (comma > 2) s = s[..comma];
        s = s.Trim().Trim('"');
        if (!s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        try { return File.Exists(s) ? s : null; } catch { return null; }
    }

    private static DateTime? FileDate(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.LastWriteTime : null;
        }
        catch { return null; }
    }

    private static string? FirstUrl(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)
            && (c!.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
             || c.StartsWith("https://", StringComparison.OrdinalIgnoreCase)));
}

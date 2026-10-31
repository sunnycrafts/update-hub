using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace UpdateHub.Core;

/// <summary>
/// How Windows should start one program: as administrator, in a compatibility mode, with
/// a few common display overrides. These are stored exactly where the Properties,
/// Compatibility tab stores them (HKCU AppCompatFlags\Layers, one value per exe path), so
/// they take effect everywhere the program is launched from, and no admin rights are
/// needed to set them.
/// </summary>
public sealed class LaunchSettings
{
    public bool RunAsAdmin { get; set; }

    /// <summary>A compatibility OS token such as WIN7RTM, or null for none.</summary>
    public string? CompatOs { get; set; }

    public bool DisableFullscreenOptimizations { get; set; }
    public bool OverrideHighDpi { get; set; }
    public bool Reduced256Colors { get; set; }
    public bool Resolution640x480 { get; set; }

    /// <summary>Flags this app does not model, kept so saving never drops them.</summary>
    public List<string> OtherFlags { get; set; } = new();
}

public static class LaunchOptions
{
    private const string LayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

    /// <summary>Compatibility choices in the order they are shown: label, registry token.</summary>
    public static readonly (string Label, string? Token)[] CompatModes =
    {
        ("Off (run normally)", null),
        ("Windows 95", "WIN95"),
        ("Windows 98 / Me", "WIN98"),
        ("Windows NT 4.0 (SP5)", "WIN4SP5"),
        ("Windows 2000", "WIN2000"),
        ("Windows XP (SP2)", "WINXPSP2"),
        ("Windows XP (SP3)", "WINXPSP3"),
        ("Windows Vista", "VISTARTM"),
        ("Windows Vista (SP1)", "VISTASP1"),
        ("Windows Vista (SP2)", "VISTASP2"),
        ("Windows 7", "WIN7RTM"),
        ("Windows 8", "WIN8RTM"),
    };

    private static readonly HashSet<string> OsTokens = CompatModes
        .Where(m => m.Token is not null).Select(m => m.Token!).ToHashSet();

    /// <summary>Parses a Layers value such as "~ RUNASADMIN WIN7RTM HIGHDPIAWARE".</summary>
    public static LaunchSettings Parse(string? value)
    {
        var s = new LaunchSettings();
        if (string.IsNullOrWhiteSpace(value)) return s;

        foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var upper = token.ToUpperInvariant();
            switch (upper)
            {
                case "~": break;
                case "RUNASADMIN": s.RunAsAdmin = true; break;
                case "DISABLEDXMAXIMIZEDWINDOWEDMODE": s.DisableFullscreenOptimizations = true; break;
                case "HIGHDPIAWARE": s.OverrideHighDpi = true; break;
                case "256COLOR": s.Reduced256Colors = true; break;
                case "640X480": s.Resolution640x480 = true; break;
                default:
                    if (OsTokens.Contains(upper)) s.CompatOs = upper;
                    else s.OtherFlags.Add(token);
                    break;
            }
        }
        return s;
    }

    /// <summary>Builds the Layers value; an empty string means "no settings, remove the value".</summary>
    public static string Compose(LaunchSettings s)
    {
        var tokens = new List<string>();
        if (s.CompatOs is not null) tokens.Add(s.CompatOs);
        if (s.RunAsAdmin) tokens.Add("RUNASADMIN");
        if (s.DisableFullscreenOptimizations) tokens.Add("DISABLEDXMAXIMIZEDWINDOWEDMODE");
        if (s.OverrideHighDpi) tokens.Add("HIGHDPIAWARE");
        if (s.Reduced256Colors) tokens.Add("256COLOR");
        if (s.Resolution640x480) tokens.Add("640X480");
        tokens.AddRange(s.OtherFlags);
        return tokens.Count == 0 ? "" : "~ " + string.Join(' ', tokens);
    }

    public static LaunchSettings Read(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LayersKey);
            return Parse(key?.GetValue(exePath) as string);
        }
        catch (Exception) { return new LaunchSettings(); }
    }

    public static void Write(string exePath, LaunchSettings settings)
    {
        var value = Compose(settings);
        using var key = Registry.CurrentUser.CreateSubKey(LayersKey);
        if (value.Length == 0) key.DeleteValue(exePath, throwOnMissingValue: false);
        else key.SetValue(exePath, value, RegistryValueKind.String);
    }

    /// <summary>Starts the program now. Admin goes through UAC; returns false if declined.</summary>
    public static bool Launch(string exePath, bool asAdmin)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
                Verb = asAdmin ? "runas" : "open",
            });
            return true;
        }
        catch (System.ComponentModel.Win32Exception) { return false; } // UAC declined
    }
}

/// <summary>
/// Global shortcut keys for programs, using the Hotkey property of a Start Menu shortcut
/// (the same mechanism as Properties, Shortcut, Shortcut key), so Windows itself handles
/// the key and UpdateHub does not need to be running.
/// </summary>
public static class ShortcutHotkey
{
    private static readonly Regex Valid = new(
        @"^((CTRL|ALT|SHIFT)\+){2,3}([A-Z0-9]|F([1-9]|1[0-9]|2[0-4]))$", RegexOptions.Compiled);

    /// <summary>Upper-cases and tidies a typed combination such as "ctrl + alt + f".</summary>
    public static string Normalize(string? text) =>
        Regex.Replace((text ?? "").ToUpperInvariant(), @"\s+", "").Replace("CONTROL", "CTRL");

    /// <summary>
    /// At least two modifiers are required so a plain letter or Ctrl+letter can never be
    /// taken over system-wide. Empty is valid and means "remove the shortcut key".
    /// </summary>
    public static bool IsValid(string? text)
    {
        var n = Normalize(text);
        if (n.Length == 0) return true;
        if (!Valid.IsMatch(n)) return false;
        var mods = n.Split('+')[..^1];
        return mods.Distinct().Count() == mods.Length;
    }

    private static string[] Roots() => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Start Menu\Programs"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Microsoft\Windows\Start Menu\Programs"),
    };

    private static dynamic? NewShell()
    {
        var t = Type.GetTypeFromProgID("WScript.Shell");
        return t is null ? null : Activator.CreateInstance(t);
    }

    /// <summary>All Start Menu shortcuts pointing at this exe: path and current key.</summary>
    private static List<(string Path, string Hotkey, bool UserOwned)> FindAll(string exePath)
    {
        var found = new List<(string, string, bool)>();
        var shell = NewShell();
        if (shell is null) return found;
        var roots = Roots();
        for (int i = 0; i < roots.Length; i++)
        {
            if (!Directory.Exists(roots[i])) continue;
            foreach (var lnk in Directory.EnumerateFiles(roots[i], "*.lnk", SearchOption.AllDirectories))
            {
                try
                {
                    dynamic sc = shell.CreateShortcut(lnk);
                    if (string.Equals((string)sc.TargetPath, exePath, StringComparison.OrdinalIgnoreCase))
                        found.Add((lnk, (string)(sc.Hotkey ?? ""), i == 0));
                }
                catch (Exception) { }
            }
        }
        return found;
    }

    /// <summary>The key currently assigned to this program, or empty.</summary>
    public static string Current(string exePath) =>
        FindAll(exePath).Select(f => f.Hotkey).FirstOrDefault(h => h.Length > 0) ?? "";

    /// <summary>
    /// Sets (or clears) the key on the user's own shortcut for this exe, creating one in
    /// the user's Start Menu if none exists. All-users shortcuts are never edited, since
    /// that needs administrator rights. Returns the shortcut path.
    /// </summary>
    public static string Apply(string displayName, string exePath, string? hotkey)
    {
        var shell = NewShell() ?? throw new InvalidOperationException("Windows Script Host is unavailable.");
        var mine = FindAll(exePath).Where(f => f.UserOwned).Select(f => f.Path).FirstOrDefault();
        string path;
        if (mine is not null) path = mine;
        else
        {
            var invalid = Path.GetInvalidFileNameChars();
            var safe = string.Concat(displayName.Select(c => invalid.Contains(c) ? '_' : c));
            Directory.CreateDirectory(Roots()[0]);
            path = Path.Combine(Roots()[0], safe + ".lnk");
        }

        dynamic sc = shell.CreateShortcut(path);
        sc.TargetPath = exePath;
        sc.WorkingDirectory = Path.GetDirectoryName(exePath) ?? "";
        sc.Hotkey = Normalize(hotkey);
        sc.Save();
        return path;
    }
}

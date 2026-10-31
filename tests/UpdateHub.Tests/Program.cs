using UpdateHub.Core; using UpdateHub.Models;
int pass=0, fail=0;
void Chk(string label, object? got, object? want){
  bool ok = string.Equals(got?.ToString(), want?.ToString(), StringComparison.Ordinal);
  if(ok) pass++; else { fail++; Console.WriteLine($"  FAIL {label}: got '{got}' want '{want}'"); }
}
Console.WriteLine("== version compare (neg = installed older) ==");
Chk("3.0.18 vs 3.0.21", VersionComparer.Compare("3.0.18","3.0.21"), -1);
Chk("25.00 vs 25.01", VersionComparer.Compare("25.00","25.01"), -1);
Chk("3.0 vs 3.0.0", VersionComparer.Compare("3.0","3.0.0"), 0);
Chk("ad 9.7.9 vs 9.7.11", VersionComparer.Compare("ad 9.7.9","9.7.11"), -1);
Chk("139.0.7258.128 vs 140.0.7321", VersionComparer.Compare("139.0.7258.128","140.0.7321"), -1);
Chk("26.1.5.0370 vs 26.1.5.371", VersionComparer.Compare("26.1.5.0370","26.1.5.371"), -1);
Chk("v1.2.3 vs 1.2.3", VersionComparer.Compare("v1.2.3","1.2.3"), 0);
Chk("1.0.0-beta vs 1.0.0", VersionComparer.Compare("1.0.0-beta","1.0.0"), -1);
Chk("2.0 vs 1.9 (newer)", VersionComparer.Compare("2.0","1.9"), 1);
Chk("junk vs 1.0 -> null", VersionComparer.Compare("abc","1.0"), null);
Chk("year vs semver -> null", VersionComparer.Compare("2022.1.3","24.1"), null);
Chk("2025.3 vs 2026.1 (both yr)", VersionComparer.Compare("2025.3","2026.1"), -1);
Chk("1.0 vs 900.0 -> null(scheme)", VersionComparer.Compare("1.0","900.0"), null);
Console.WriteLine("== majors/minors behind ==");
Chk("majors 31.0->32.1", VersionComparer.MajorsBehind("31.0","32.1"), 1);
Chk("minors 3.0.18->3.0.21", VersionComparer.MinorsBehind("3.0.18","3.0.21"), 0);
Chk("minors 25.00->25.01", VersionComparer.MinorsBehind("25.00","25.01"), 1);
Chk("majors same", VersionComparer.MajorsBehind("3.7.7","3.7.8"), 0);
Console.WriteLine("== age behind (calendar) ==");
Chk("8Nov2022->5Apr2025", AgeBehind.Between(new DateTime(2022,11,8), new DateTime(2025,4,5)).Long, "2 years, 4 months, 28 days");
Chk("  ..short", AgeBehind.Between(new DateTime(2022,11,8), new DateTime(2025,4,5)).Short, "2 yr 4 mo");
Chk("10Jun2026->22Jul2026", AgeBehind.Between(new DateTime(2026,6,10), new DateTime(2026,7,22)).Short, "42 days");
Chk("31Jan->1Mar borrow", AgeBehind.Between(new DateTime(2025,1,31), new DateTime(2025,3,1)).Long, "1 month, 1 day");
Chk("exact 1yr", AgeBehind.Between(new DateTime(2024,3,15), new DateTime(2025,3,15)).Long, "1 year");
Chk("leap 29Feb", AgeBehind.Between(new DateTime(2024,2,29), new DateTime(2025,3,1)).Long, "1 year, 1 day");
Chk("band 2yr+", AgeBehind.Between(new DateTime(2022,11,8), new DateTime(2025,4,5)).Band, AgeBand.VeryOutdated);
Chk("band 42d", AgeBehind.Between(new DateTime(2026,6,10), new DateTime(2026,7,22)).Band, AgeBand.Recent);
Chk("unknown", AgeBehind.Between(null, new DateTime(2025,1,1)).Short, "—");
Console.WriteLine("== name normalize ==");
Chk("Audacity 3.7.7", NameNormalizer.Normalize("Audacity 3.7.7"), "audacity");
Chk("Apple App Support (64-bit)", NameNormalizer.Normalize("Apple Application Support (64-bit)"), "appleapplicationsupport");
Chk("AutoCAD - English", NameNormalizer.Clean("Autodesk AutoCAD 2022 - English"), "Autodesk AutoCAD 2022");
Chk("Office 2019 keeps yr", NameNormalizer.Clean("Microsoft Office 2019"), "Microsoft Office 2019");
Chk("AutoCAD22 != AutoCAD24", NameNormalizer.Normalize("AutoCAD 2022")==NameNormalizer.Normalize("AutoCAD 2024"), false);
Chk("31Dec->1Jan", AgeBehind.Between(new DateTime(2024,12,31), new DateTime(2025,1,1)).Long, "1 day");
Chk("Mar31->Apr30 (Long=calendar)", AgeBehind.Between(new DateTime(2025,3,31), new DateTime(2025,4,30)).Long, "1 month");
Chk("Mar31->Apr30 (Short=days)", AgeBehind.Between(new DateTime(2025,3,31), new DateTime(2025,4,30)).Short, "30 days");
Chk("3yr2mo spec", AgeBehind.Between(new DateTime(2021,3,24), new DateTime(2026,7,25)).Short, "5 yr 4 mo");
Chk("AURA x64", NameNormalizer.Normalize("AURA lighting effect add-on x64"), "auralightingeffectaddon");
Chk("VLC media player", NameNormalizer.Normalize("VLC media player"), "vlcmediaplayer");
Chk("7-Zip 25.00 (x64)", NameNormalizer.Normalize("7-Zip 25.00 (x64)"), "7zip");
Chk("strip publisher", NameNormalizer.StripPublisher("autodeskautocad","Autodesk"), "autocad");

Console.WriteLine("== releases-missed interval (installed, latest] ==");
string[] hist = { "1.4.1.1030", "1.4.1.1031", "1.4.1.1032", "1.5.0.1391", "1.5.0.1392", "1.5.0.1393" };
int Missed(string installed, string? latest) {
    int n = 0;
    foreach (var v in hist) {
        if (VersionComparer.Compare(installed, v) is not { } cmp || cmp >= 0) continue;
        if (latest is not null && VersionComparer.Compare(v, latest) is { } up && up > 0) continue;
        n++;
    }
    return n;
}
Chk("bounded by latest", Missed("1.4.1.1030", "1.4.1.1032"), 2);
Chk("unbounded (the old bug)", Missed("1.4.1.1030", null), 5);
Chk("already on latest", Missed("1.4.1.1032", "1.4.1.1032"), 0);

Console.WriteLine("== commit hashes rejected as versions ==");
bool IsVer(string c) {
    if (System.Text.RegularExpressions.Regex.IsMatch(c, "^[0-9a-f]{7,40}$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase) && c.Any(char.IsLetter)) return false;
    if (!System.Text.RegularExpressions.Regex.IsMatch(c, @"^\d+(\.\d+)*")) return false;
    var head = c.Split('.', '-', '_')[0];
    if (head.Length > 4 && head.All(char.IsDigit)) return false;
    return true;
}
Chk("sha rejected", IsVer("4114c11a9ec5320e"), false);
Chk("short sha rejected", IsVer("a1b2c3d"), false);
Chk("normal version ok", IsVer("3.5.9"), true);
Chk("year version ok", IsVer("2025.3"), true);
Chk("date stamp rejected", IsVer("20250731"), false);
Chk("chrome version ok", IsVer("140.0.7321.128"), true);
Chk("long dotted ok", IsVer("1.4.1.1030"), true);

Console.WriteLine("== same-product version tolerance (registry vs winget precision) ==");
Chk("8.0.29.36224 ~ 8.0.29", VersionComparer.IsSameProductVersion("8.0.29.36224","8.0.29"), true);
Chk("6.0.36.34217 ~ 6.0.36", VersionComparer.IsSameProductVersion("6.0.36.34217","6.0.36"), true);
Chk("3.1.32.31915 ~ 3.1.32", VersionComparer.IsSameProductVersion("3.1.32.31915","3.1.32"), true);
Chk("identical full match", VersionComparer.IsSameProductVersion("1.0.2600.1","1.0.2600.1"), true);
Chk("8.0.29 vs 6.0.36 differ", VersionComparer.IsSameProductVersion("8.0.29","6.0.36"), false);
Chk("8.0.29.1 vs 8.0.30 differ", VersionComparer.IsSameProductVersion("8.0.29.1","8.0.30"), false);
Chk("single seg 8 ~ 8", VersionComparer.IsSameProductVersion("8","8"), true);
Chk("8 vs 8.0.29 too vague", VersionComparer.IsSameProductVersion("8","8.0.29"), false);
// Python encodes a build number where winget puts a patch, so these must NOT merge:
// treating them as one product could just as easily merge 3.14.6 with 3.14.7150.0.
Chk("py 3.14.6150.0 vs 3.14.6 kept apart", VersionComparer.IsSameProductVersion("3.14.6150.0","3.14.6"), false);

Console.WriteLine("== elevated batch status mapping (matches the PowerShell script's JSON shape) ==");
Chk("success -> Success", ElevatedUpdateRunner.InterpretStatus("success", "exit=0", "Successfully installed"), UpdateOutcome.Success);
Chk("failed exit=1603 -> Failed", ElevatedUpdateRunner.InterpretStatus("failed", "exit=1603", "simulated failure"), UpdateOutcome.Failed);
Chk("failed exit=1618 -> Failed", ElevatedUpdateRunner.InterpretStatus("failed", "exit=1618", "another installation is in progress"), UpdateOutcome.Failed);
Chk("no applicable update -> NoUpdateNeeded", ElevatedUpdateRunner.InterpretStatus("failed", "exit=8A150011", "No applicable update found"), UpdateOutcome.NoUpdateNeeded);
Chk("describe success", ElevatedUpdateRunner.DescribeOutcome(UpdateOutcome.Success, "exit=0", "ok"), "Updated (elevated background).");
Chk("describe no-update", ElevatedUpdateRunner.DescribeOutcome(UpdateOutcome.NoUpdateNeeded, "x", "y"), "No applicable update was found.");
{
    var longDetail = new string('x', 500);
    var described = ElevatedUpdateRunner.DescribeOutcome(UpdateOutcome.Failed, "exit=1603", longDetail);
    Chk("describe truncates long detail", described.Length <= 180, true);
    Chk("describe keeps message prefix", described.StartsWith("exit=1603"), true);
}
Console.WriteLine("== start menu search, no-source reason, manual-check link ==");
{
    var both = new InstalledApp { Name = "Both", Sources = DetectionSource.Registry | DetectionSource.StartMenu };
    var only = new InstalledApp { Name = "Only", Sources = DetectionSource.StartMenu };
    var none = new InstalledApp { Name = "None", Sources = DetectionSource.Registry };
    Chk("startmenu:only matches only-menu", SearchQuery.Parse("startmenu:only").Matches(only), true);
    Chk("startmenu:only skips shared", SearchQuery.Parse("startmenu:only").Matches(both), false);
    Chk("startmenu:yes matches shared", SearchQuery.Parse("startmenu:yes").Matches(both), true);
    Chk("startmenu:no matches absent", SearchQuery.Parse("startmenu:no").Matches(none), true);
    Chk("startmenu:no skips present", SearchQuery.Parse("startmenu:no").Matches(both), false);
    Chk("text for only", only.StartMenuText, "Start Menu only (no install record)");
    Chk("text for absent", none.StartMenuText, "Not in the Start Menu");
    Chk("reason null when latest known", new InstalledApp { LatestVersion = "1.0" }.NoSourceReason is null, true);
    Chk("reason present when unknown", none.NoSourceReason is not null, true);
    Chk("manual link is https", none.ManualCheckUrl.StartsWith("https://"), true);
    Chk("manual link prefers download page",
        new InstalledApp { Name = "X", DownloadPageUrl = "https://example.com/dl" }.ManualCheckUrl, "https://example.com/dl");
    Chk("manual link rejects non-https homepage",
        new InstalledApp { Name = "X", Homepage = "http://example.com" }.ManualCheckUrl.StartsWith("https://duckduckgo.com/"), true);
}
Console.WriteLine("== launch options (admin / compatibility) and shortcut keys ==");
{
    var p = LaunchOptions.Parse("~ RUNASADMIN WIN7RTM HIGHDPIAWARE SOMEFUTUREFLAG");
    Chk("parse admin", p.RunAsAdmin, true);
    Chk("parse compat os", p.CompatOs, "WIN7RTM");
    Chk("parse high dpi", p.OverrideHighDpi, true);
    Chk("parse keeps unknown flag", p.OtherFlags.Contains("SOMEFUTUREFLAG"), true);
    Chk("compose round trip", LaunchOptions.Compose(p), "~ WIN7RTM RUNASADMIN HIGHDPIAWARE SOMEFUTUREFLAG");
    Chk("compose empty -> blank", LaunchOptions.Compose(new LaunchSettings()), "");
    Chk("parse empty", LaunchOptions.Parse(null).RunAsAdmin, false);

    Chk("hotkey ctrl+alt+f ok", ShortcutHotkey.IsValid("ctrl + alt + f"), true);
    Chk("hotkey ctrl+shift+F12 ok", ShortcutHotkey.IsValid("CTRL+SHIFT+F12"), true);
    Chk("hotkey empty ok (clears)", ShortcutHotkey.IsValid(""), true);
    Chk("hotkey single letter rejected", ShortcutHotkey.IsValid("F"), false);
    Chk("hotkey ctrl+f rejected (one modifier)", ShortcutHotkey.IsValid("CTRL+F"), false);
    Chk("hotkey repeated modifier rejected", ShortcutHotkey.IsValid("CTRL+CTRL+F"), false);
    Chk("hotkey modifiers only rejected", ShortcutHotkey.IsValid("CTRL+ALT"), false);
    Chk("hotkey normalize", ShortcutHotkey.Normalize(" control + alt + g "), "CTRL+ALT+G");

    // Real registry round trip on a throwaway path, cleaned up afterwards.
    var fake = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uh-test-" + Guid.NewGuid().ToString("N") + ".exe");
    try
    {
        LaunchOptions.Write(fake, new LaunchSettings { RunAsAdmin = true, CompatOs = "WINXPSP3" });
        var back = LaunchOptions.Read(fake);
        Chk("registry write/read admin", back.RunAsAdmin, true);
        Chk("registry write/read os", back.CompatOs, "WINXPSP3");
    }
    finally { LaunchOptions.Write(fake, new LaunchSettings()); }
    Chk("registry cleaned up", LaunchOptions.Read(fake).RunAsAdmin, false);

    // Real shortcut-key round trip through a Start Menu shortcut, then remove it.
    var exe = System.IO.Path.Combine(Environment.SystemDirectory, "notepad.exe");
    string? lnk = null;
    try
    {
        lnk = ShortcutHotkey.Apply("UpdateHub test shortcut", exe, "ctrl+alt+f9");
        Chk("hotkey stored on shortcut", ShortcutHotkey.Current(exe).ToUpperInvariant().Contains("F9"), true);
    }
    finally { if (lnk is not null && System.IO.File.Exists(lnk)) System.IO.File.Delete(lnk); }
    Chk("hotkey cleaned up", ShortcutHotkey.Current(exe).ToUpperInvariant().Contains("F9"), false);
}
Console.WriteLine("== drivers: real local scan, vendor pages, privacy ==");
{
    var machine = DriverScanner.ReadMachine();
    Chk("machine manufacturer read", machine.Manufacturer.Length > 0, true);
    Chk("machine cpu read", machine.Cpu.Length > 0, true);
    Chk("machine ram read", machine.RamGb > 1, true);
    var drivers = DriverScanner.ReadDrivers();
    Chk("drivers found on this PC", drivers.Count > 0, true);
    Chk("every driver has a version", drivers.All(d => d.Version.Length > 0), true);
    Chk("no inbox Microsoft drivers listed (bar basic display)",
        drivers.All(d => !d.Manufacturer.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
                         || d.Device.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)), true);
    Chk("vendor link: nvidia", DriverScanner.VendorPage("NVIDIA", "GeForce", "Graphics").Contains("nvidia.com"), true);
    Chk("vendor link: intel", DriverScanner.VendorPage("Intel Corporation", "Wi-Fi", "Network").Contains("intel.com"), true);
    Chk("vendor link: unknown falls back to search", DriverScanner.VendorPage("Acme", "Widget", "").StartsWith("https://duckduckgo.com/"), true);
    Chk("all vendor links are https", drivers.All(d => d.VendorUrl.StartsWith("https://")), true);
    // Privacy: a link may carry the maker and device name, never a serial or machine name.
    Chk("fallback link contains no machine name",
        !DriverScanner.VendorPage("Acme", "Widget", "").Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase), true);
    var flagged = new List<DriverInfo> { new() { Device = "Test GPU", Date = new DateTime(2020, 1, 1) } };
    DriverScanner.Flag(flagged, new List<PendingDriver> { new() { Model = "Test GPU", Date = new DateTime(2024, 1, 1) } });
    Chk("newer Windows Update driver flagged", flagged[0].NewerOnWindowsUpdate, true);
    var notFlagged = new List<DriverInfo> { new() { Device = "Test GPU", Date = new DateTime(2025, 1, 1) } };
    DriverScanner.Flag(notFlagged, new List<PendingDriver> { new() { Model = "Test GPU", Date = new DateTime(2024, 1, 1) } });
    Chk("older Windows Update driver not flagged", notFlagged[0].NewerOnWindowsUpdate, false);
}
Console.WriteLine("== driver installer: output mapping and a real dry run of the script ==");
{
    var lines = new[]
    {
        "{\"status\":\"restorepoint\",\"message\":\"created\"}",
        "{\"status\":\"downloading\",\"count\":2}",
        "{\"status\":\"item\",\"title\":\"Intel - Display - 31.0\",\"code\":2}",
        "{\"status\":\"item\",\"title\":\"Realtek - Sound - 6.0\",\"code\":4}",
        "{\"status\":\"done\",\"reboot\":true}",
    };
    var r = DriverInstaller.ParseOutput(lines);
    Chk("restore point parsed", r.RestorePoint, "created");
    Chk("two items parsed", r.Items.Count, 2);
    Chk("code 2 is ok", r.Items[0].Ok, true);
    Chk("code 4 is failed", r.Items[1].Ok, false);
    Chk("code 4 detail", r.Items[1].Detail, "Failed");
    Chk("reboot flag parsed", r.RebootRequired, true);
    Chk("error line parsed", DriverInstaller.ParseOutput(new[] { "{\"status\":\"error\",\"message\":\"boom\"}" }).Error, "boom");
    Chk("garbage line ignored", DriverInstaller.ParseOutput(new[] { "{not json", "" }).Items.Count, 0);

    // Real run of the actual script, unelevated, with no titles: it must search Windows
    // Update successfully and report "none" without installing anything.
    var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uh-drv-" + Guid.NewGuid().ToString("N"));
    System.IO.Directory.CreateDirectory(dir);
    try
    {
        var ps1 = System.IO.Path.Combine(dir, "d.ps1");
        var inp = System.IO.Path.Combine(dir, "in.json");
        var outp = System.IO.Path.Combine(dir, "out.jsonl");
        System.IO.File.WriteAllText(ps1, DriverInstaller.ScriptText, new System.Text.UTF8Encoding(true));
        System.IO.File.WriteAllText(inp, "[]");
        System.IO.File.WriteAllText(outp, "");
        var psi = new System.Diagnostics.ProcessStartInfo(
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\WindowsPowerShell\v1.0\powershell.exe"),
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{ps1}\" -In \"{inp}\" -Out \"{outp}\"")
        { UseShellExecute = false, CreateNoWindow = true };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        Chk("dry run finishes", proc.WaitForExit(180_000), true);
        var dry = DriverInstaller.ParseOutput(System.IO.File.ReadAllLines(outp));
        Chk("dry run: nothing installed", dry.Items.Count, 0);
        Chk("dry run: reports no matching drivers (not a script error)",
            dry.Error, "Windows Update no longer offers those drivers.");
    }
    finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
}
Console.WriteLine($"\n{pass} passed, {fail} failed");
return fail==0?0:1;

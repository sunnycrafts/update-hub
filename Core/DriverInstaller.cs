using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace UpdateHub.Core;

public sealed class DriverInstallResult
{
    public List<(string Title, bool Ok, string Detail)> Items { get; } = new();
    public bool RebootRequired { get; set; }
    public string RestorePoint { get; set; } = "";
    public string? Error { get; set; }
    public bool Declined { get; set; }
}

/// <summary>
/// Installs driver updates that Windows Update offers for this PC, through the Windows
/// Update Agent, so only Microsoft-distributed, signed drivers are ever installed.
///
/// It runs in one hidden, elevated Windows PowerShell session (a single UAC prompt). A
/// system restore point is attempted first, so a driver that misbehaves can be undone.
/// The script is static: the driver titles travel in a JSON file and are only ever compared
/// with titles Windows Update itself returned, never executed or interpolated.
/// </summary>
public static class DriverInstaller
{
    private const string Script = @"
param([string]$In, [string]$Out)
function W($o) { ($o | ConvertTo-Json -Compress) | Add-Content -Path $Out -Encoding UTF8 }
try {
    $titles = @(Get-Content -Path $In -Raw | ConvertFrom-Json)
    try {
        Enable-ComputerRestore -Drive ""$env:SystemDrive\"" -ErrorAction SilentlyContinue
        Checkpoint-Computer -Description 'UpdateHub: before driver update' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop
        W @{ status = 'restorepoint'; message = 'created' }
    } catch {
        W @{ status = 'restorepoint'; message = ('not created: ' + $_.Exception.Message) }
    }

    $session = New-Object -ComObject Microsoft.Update.Session
    $found = $session.CreateUpdateSearcher().Search(""IsInstalled=0 and Type='Driver' and IsHidden=0"")
    $coll = New-Object -ComObject Microsoft.Update.UpdateColl
    foreach ($u in $found.Updates) {
        if ($titles -contains $u.Title) {
            if (-not $u.EulaAccepted) { $u.AcceptEula() }
            [void]$coll.Add($u)
        }
    }
    if ($coll.Count -eq 0) { W @{ status = 'none' }; return }

    W @{ status = 'downloading'; count = $coll.Count }
    $dl = $session.CreateUpdateDownloader()
    $dl.Updates = $coll
    [void]$dl.Download()

    W @{ status = 'installing'; count = $coll.Count }
    $inst = $session.CreateUpdateInstaller()
    $inst.Updates = $coll
    $res = $inst.Install()
    for ($i = 0; $i -lt $coll.Count; $i++) {
        $code = $res.GetUpdateResult($i).ResultCode
        W @{ status = 'item'; title = $coll.Item($i).Title; code = [int]$code }
    }
    W @{ status = 'done'; reboot = [bool]$res.RebootRequired }
} catch {
    W @{ status = 'error'; message = $_.Exception.Message }
}
";

    /// <summary>The script text, exposed so it can be syntax-checked in tests.</summary>
    public static string ScriptText => Script;

    /// <summary>Turns the script's JSON-lines output into a result. Public for testing.</summary>
    public static DriverInstallResult ParseOutput(IEnumerable<string> lines)
    {
        var result = new DriverInstallResult();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line.TrimStart('﻿'));
                var root = doc.RootElement;
                var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
                switch (status)
                {
                    case "restorepoint":
                        result.RestorePoint = root.GetProperty("message").GetString() ?? "";
                        break;
                    case "item":
                        var code = root.GetProperty("code").GetInt32();
                        // Windows Update result codes: 2 succeeded, 3 succeeded with errors.
                        var ok = code is 2 or 3;
                        result.Items.Add((root.GetProperty("title").GetString() ?? "", ok,
                            code == 2 ? "Installed" : code == 3 ? "Installed with warnings"
                            : code == 4 ? "Failed" : code == 5 ? "Cancelled" : $"Result code {code}"));
                        break;
                    case "done":
                        result.RebootRequired = root.TryGetProperty("reboot", out var r) && r.GetBoolean();
                        break;
                    case "none":
                        result.Error = "Windows Update no longer offers those drivers.";
                        break;
                    case "error":
                        result.Error = root.GetProperty("message").GetString();
                        break;
                }
            }
            catch (Exception) { /* a partial line while the file is still being written */ }
        }
        return result;
    }

    private static string WindowsPowerShell() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"System32\WindowsPowerShell\v1.0\powershell.exe");

    public static async Task<DriverInstallResult> InstallAsync(
        IReadOnlyList<string> titles, string workRoot, IProgress<string>? progress, CancellationToken ct)
    {
        var dir = Path.Combine(workRoot, "Elevated", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var scriptPath = Path.Combine(dir, "drivers.ps1");
        var inputPath = Path.Combine(dir, "input.json");
        var outputPath = Path.Combine(dir, "output.jsonl");
        File.WriteAllText(scriptPath, Script, new System.Text.UTF8Encoding(true));
        File.WriteAllText(inputPath, JsonSerializer.Serialize(titles));
        File.WriteAllText(outputPath, "");

        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = WindowsPowerShell(),
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden " +
                            $"-File \"{scriptPath}\" -In \"{inputPath}\" -Out \"{outputPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (proc is null) return new DriverInstallResult { Error = "Could not start the installer." };

            var wait = proc.WaitForExitAsync(ct);
            while (!wait.IsCompleted)
            {
                await Task.WhenAny(wait, Task.Delay(1000, ct)).ConfigureAwait(false);
                var interim = ParseOutput(SafeReadLines(outputPath));
                progress?.Report(interim.RestorePoint.Length == 0
                    ? "Preparing… creating a restore point"
                    : $"Restore point: {interim.RestorePoint}. Downloading and installing drivers…");
            }
            return ParseOutput(SafeReadLines(outputPath));
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new DriverInstallResult { Declined = true, Error = "Administrator approval was declined." };
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    private static string[] SafeReadLines(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd().Split('\n');
        }
        catch (Exception) { return Array.Empty<string>(); }
    }
}

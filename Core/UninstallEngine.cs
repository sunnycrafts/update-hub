using System.Diagnostics;
using System.Text.RegularExpressions;
using UpdateHub.Data;
using UpdateHub.Models;
using UpdateHub.Services;

namespace UpdateHub.Core;

public enum UninstallMode
{
    /// <summary>Runs without any prompts.</summary>
    Silent,
    /// <summary>The vendor's own uninstaller appears and the user drives it.</summary>
    Interactive,
    /// <summary>No usable uninstall route was found.</summary>
    Unavailable,
}

public sealed record UninstallPlan(UninstallMode Mode, string Description, string? Command);

/// <summary>
/// Removes installed software using whichever route the scanners recorded for it. Silent
/// removal is used only where the publisher supplied a quiet command; for everything else
/// the vendor's uninstaller is shown rather than guessing switches, because a wrong silent
/// flag can put an installer into repair or modify mode instead of uninstalling.
/// </summary>
public sealed class UninstallEngine
{
    private readonly AppRepository _repo;
    private readonly LogService _log;

    public UninstallEngine(AppRepository repo, LogService log)
    {
        _repo = repo;
        _log = log;
    }

    private static readonly Regex MsiGuid = new(
        @"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}",
        RegexOptions.Compiled);

    /// <summary>Works out how this app would be removed, without removing anything.</summary>
    public UninstallPlan Plan(InstalledApp app)
    {
        if (IsSelf(app))
            return new UninstallPlan(UninstallMode.Interactive,
                "This is UpdateHub. Removing it closes the app and runs its own uninstaller.",
                null);

        if (!string.IsNullOrWhiteSpace(app.MsStoreFamilyName))
            return new UninstallPlan(UninstallMode.Silent,
                "Removed as a Microsoft Store package.", null);

        // An MSI product code gives a reliable quiet uninstall regardless of what the
        // registry's UninstallString happens to say.
        if (app.UninstallString is not null && MsiGuid.Match(app.UninstallString) is { Success: true } m
            && app.UninstallString.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
            return new UninstallPlan(UninstallMode.Silent,
                "Removed silently with Windows Installer.", $"msiexec.exe /x {m.Value} /qn /norestart");

        if (!string.IsNullOrWhiteSpace(app.WingetId))
            return new UninstallPlan(UninstallMode.Silent,
                "Removed silently through winget.", null);

        if (!string.IsNullOrWhiteSpace(app.ChocoId))
            return new UninstallPlan(UninstallMode.Silent, "Removed through Chocolatey.", null);

        if (!string.IsNullOrWhiteSpace(app.ScoopId))
            return new UninstallPlan(UninstallMode.Silent, "Removed through Scoop.", null);

        if (!string.IsNullOrWhiteSpace(app.UninstallString))
            return new UninstallPlan(UninstallMode.Interactive,
                "The publisher's uninstaller will open — follow its prompts to finish.",
                app.UninstallString);

        return new UninstallPlan(UninstallMode.Unavailable,
            "No uninstall information was recorded for this app, so it has to be removed " +
            "from Windows Settings instead.", null);
    }

    /// <summary>True when the row refers to this very copy of UpdateHub.</summary>
    public static bool IsSelf(InstalledApp app)
    {
        if (app.Name.Equals("UpdateHub", StringComparison.OrdinalIgnoreCase)) return true;
        var self = Environment.ProcessPath;
        return self is not null && app.ExePath is not null &&
               string.Equals(self, app.ExePath, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<(UpdateOutcome Outcome, string Message)> UninstallAsync(
        InstalledApp app, CancellationToken ct)
    {
        var plan = Plan(app);
        if (plan.Mode == UninstallMode.Unavailable)
            return (UpdateOutcome.Failed, plan.Description);

        _log.Info($"Uninstall start: {app.Name} ({plan.Mode})");
        var recordId = _repo.BeginUpdate(app);

        try
        {
            var (outcome, message) = await RunAsync(app, plan, ct).ConfigureAwait(false);
            _repo.FinishUpdate(recordId, outcome, message);
            _log.Info($"Uninstall end: {app.Name} -> {outcome}: {message}");
            return (outcome, message);
        }
        catch (OperationCanceledException)
        {
            _repo.FinishUpdate(recordId, UpdateOutcome.Cancelled, "Cancelled");
            return (UpdateOutcome.Cancelled, "Cancelled.");
        }
        catch (Exception ex)
        {
            _log.Error($"Uninstall failed for {app.Name}", ex);
            _repo.FinishUpdate(recordId, UpdateOutcome.Failed, ex.Message);
            return (UpdateOutcome.Failed, ex.Message);
        }
    }

    private async Task<(UpdateOutcome, string)> RunAsync(
        InstalledApp app, UninstallPlan plan, CancellationToken ct)
    {
        if (IsSelf(app)) return LaunchSelfUninstaller();

        if (!string.IsNullOrWhiteSpace(app.MsStoreFamilyName))
        {
            var r = await ProcessRunner.PowerShellAsync(
                $"Remove-AppxPackage -Package (Get-AppxPackage | " +
                $"Where-Object PackageFamilyName -eq '{app.MsStoreFamilyName}' | " +
                "Select-Object -First 1 -ExpandProperty PackageFullName) -ErrorAction Stop",
                ct, 300_000).ConfigureAwait(false);
            return r.Ok
                ? (UpdateOutcome.Success, "Removed.")
                : (UpdateOutcome.Failed, Detail(r.Combined) ?? "The Store package could not be removed.");
        }

        if (plan.Command is not null && plan.Command.StartsWith("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            var split = plan.Command.IndexOf(' ');
            var file = plan.Command[..split];
            var args = plan.Command[(split + 1)..];

            // Per-machine MSI products cannot be removed without elevation, and most
            // desktop software installs that way. Going straight to an elevated launch
            // avoids a guaranteed privilege failure on the first attempt; the user has
            // already confirmed the uninstall, and Windows itself prompts here too.
            if (!IsElevated())
                return await RunElevatedAsync(file, args, ct).ConfigureAwait(false);

            var r = await ProcessRunner.RunAsync(file, args, ct, 900_000).ConfigureAwait(false);
            return InterpretExitCode(r.ExitCode, r.Combined);
        }

        if (!string.IsNullOrWhiteSpace(app.WingetId))
        {
            var r = await ProcessRunner.WingetAsync(
                $"uninstall --id {Quote(app.WingetId)} --exact --silent " +
                "--accept-source-agreements --disable-interactivity", ct, 900_000).ConfigureAwait(false);
            if (r.Ok) return (UpdateOutcome.Success, "Removed through winget.");
            if (r.Combined.Contains("requires elevation", StringComparison.OrdinalIgnoreCase))
                return (UpdateOutcome.RequiresElevation,
                    "This uninstall needs administrator rights. Run UpdateHub as administrator and retry.");
            return (UpdateOutcome.Failed, Detail(r.Combined) ?? "winget could not remove this package.");
        }

        if (!string.IsNullOrWhiteSpace(app.ChocoId))
        {
            var exe = Scanning.ChocolateyScanner.ChocoExe;
            if (exe is null) return (UpdateOutcome.Failed, "Chocolatey is not available.");
            var r = await ProcessRunner.RunAsync(exe,
                $"uninstall {app.ChocoId} -y --no-progress --limit-output", ct, 900_000).ConfigureAwait(false);
            return r.Ok
                ? (UpdateOutcome.Success, "Removed through Chocolatey.")
                : (UpdateOutcome.Failed, Detail(r.Combined) ?? "Chocolatey uninstall failed.");
        }

        if (!string.IsNullOrWhiteSpace(app.ScoopId))
        {
            var r = await ProcessRunner.PowerShellAsync(
                $"scoop uninstall {app.ScoopId} 6>&1", ct, 900_000).ConfigureAwait(false);
            return r.Ok
                ? (UpdateOutcome.Success, "Removed through Scoop.")
                : (UpdateOutcome.Failed, Detail(r.Combined) ?? "Scoop uninstall failed.");
        }

        // Interactive: hand off to the publisher's uninstaller and wait for it.
        if (plan.Command is not null)
        {
            var (file, args) = SplitCommand(plan.Command);
            var r = await ProcessRunner.RunAsync(file, args, ct, 3_600_000).ConfigureAwait(false);
            var (outcome, message) = InterpretExitCode(r.ExitCode, r.Combined);

            // Many vendor uninstallers live under Program Files and need elevation; retry
            // through UAC rather than reporting a privilege error the user cannot act on.
            if (outcome == UpdateOutcome.RequiresElevation && !IsElevated())
                return await RunElevatedAsync(file, args, ct).ConfigureAwait(false);

            return (outcome, message);
        }

        return (UpdateOutcome.Failed, "No uninstall command is available.");
    }

    /// <summary>
    /// UpdateHub cannot delete its own running executable, so the uninstaller is started
    /// detached and this process exits, leaving the script to clean up behind it.
    /// </summary>
    private (UpdateOutcome, string) LaunchSelfUninstaller()
    {
        var script = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "", "uninstall.ps1");

        if (!System.IO.File.Exists(script))
            return (UpdateOutcome.Failed,
                "UpdateHub does not appear to be installed — it is running as a portable " +
                "executable, so there is nothing to uninstall. Just delete the file.");

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });

        return (UpdateOutcome.Success, "UpdateHub will close and remove itself.");
    }

    public static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// Runs a command through ShellExecute with the runas verb, which raises the standard
    /// UAC prompt. Output cannot be captured this way, so the verdict comes from the exit
    /// code alone.
    /// </summary>
    private static async Task<(UpdateOutcome, string)> RunElevatedAsync(
        string file, string args, CancellationToken ct)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            if (proc is null)
                return (UpdateOutcome.Failed, "The elevated uninstaller could not be started.");

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return InterpretExitCode(proc.ExitCode, "");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the user dismissed the UAC prompt.
            return (UpdateOutcome.Cancelled, "Administrator approval was declined.");
        }
        catch (Exception ex)
        {
            return (UpdateOutcome.Failed, ex.Message);
        }
    }

    private static (UpdateOutcome, string) InterpretExitCode(int code, string output) => code switch
    {
        0 => (UpdateOutcome.Success, "Removed."),
        3010 or 1641 => (UpdateOutcome.Success, "Removed. A restart is required to finish."),
        1605 => (UpdateOutcome.Success, "Already removed."),
        1602 => (UpdateOutcome.Cancelled, "The uninstall was cancelled."),
        1603 => (UpdateOutcome.Failed, "The uninstaller reported a fatal error."),
        // Windows Installer's family of privilege failures. Without these the app
        // reported a bare exit code for the very common case of a per-machine product.
        740 or 1925 or 1730 or 1260 or 5 => (UpdateOutcome.RequiresElevation,
            "This uninstall needs administrator rights."),
        1618 => (UpdateOutcome.Failed,
            "Another installation is already in progress. Wait for it to finish and retry."),
        1619 or 1620 => (UpdateOutcome.Failed, "The installer package could not be opened."),
        1614 => (UpdateOutcome.Success, "Already removed."),
        _ => (UpdateOutcome.Failed, Detail(output) ?? $"The uninstaller exited with code {code}."),
    };

    private static string? Detail(string text) =>
        text.Split('\n').Select(l => l.Trim())
            .LastOrDefault(l => l.Length > 8 && !l.StartsWith('-'));

    /// <summary>Splits a registry UninstallString into executable and arguments.</summary>
    public static (string File, string Args) SplitCommand(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int close = command.IndexOf('"', 1);
            if (close > 0)
                return (command[1..close], command[(close + 1)..].Trim());
        }

        // Unquoted paths containing spaces are ambiguous; split at the first ".exe".
        int exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0 && exe + 4 <= command.Length)
            return (command[..(exe + 4)], command[(exe + 4)..].Trim());

        int space = command.IndexOf(' ');
        return space < 0 ? (command, "") : (command[..space], command[(space + 1)..]);
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
}

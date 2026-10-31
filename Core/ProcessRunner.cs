using System.Diagnostics;
using System.IO;
using System.Text;

namespace UpdateHub.Core;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Ok => ExitCode == 0 && !TimedOut;
    public string Combined => string.IsNullOrWhiteSpace(StdErr) ? StdOut : StdOut + "\n" + StdErr;
}

/// <summary>
/// Runs console tools and captures their output. winget is invoked directly rather than
/// through PowerShell — it is a plain executable, and the extra shell only adds a process
/// and another layer of output mangling.
/// </summary>
public static class ProcessRunner
{
    /// <summary>
    /// winget renders progress spinners and box-drawing characters that corrupt captured
    /// output. Stripping ANSI escapes plus the spinner glyph set leaves parseable text.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex AnsiEscape =
        new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", System.Text.RegularExpressions.RegexOptions.Compiled);

    private const string SpinnerChars = "⠇⠋⠙⠸⠴⠦█░▒▓●·";

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        CancellationToken ct = default,
        int timeoutMs = 120_000,
        Action<string>? onOutputLine = null,
        string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        // Ask winget to skip the animated progress bar where it honours the hint.
        psi.Environment["WINGET_DISABLE_INTERACTIVITY"] = "1";
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var line = Clean(e.Data);
            if (line.Length == 0) return;
            stdout.AppendLine(line);
            onOutputLine?.Invoke(line);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var line = Clean(e.Data);
            if (line.Length > 0) stderr.AppendLine(line);
        };
        proc.Exited += (_, _) => done.TrySetResult(true);

        try
        {
            if (!proc.Start())
                return new ProcessResult(-1, "", $"Could not start {fileName}", false);
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, "", ex.Message, false);
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        try
        {
            await done.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            return new ProcessResult(-1, stdout.ToString(), stderr.ToString(), TimedOut: !ct.IsCancellationRequested);
        }

        // Let the async readers drain before reading the buffers.
        try { proc.WaitForExit(2000); } catch { /* already gone */ }

        return new ProcessResult(proc.ExitCode, stdout.ToString(), stderr.ToString(), false);
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* racing exit */ }
    }

    private static string Clean(string line)
    {
        var s = AnsiEscape.Replace(line, "");
        s = s.Trim(SpinnerChars.ToCharArray());
        return s.TrimEnd();
    }

    private static string? _wingetPath;
    private static bool _wingetProbed;

    /// <summary>
    /// Locates winget.exe. It lives under WindowsApps with an execution alias that does not
    /// always resolve for a redirected-stdout child process, so we fall back to walking the
    /// package folders and picking the newest build.
    /// </summary>
    /// <summary>Last non-empty line of tool output, for one-line status messages.</summary>
    public static string LastLine(string text) =>
        text.Split((char)10).Select(l => l.Trim()).LastOrDefault(l => l.Length > 3) ?? "";

    public static string? FindWinget()
    {
        if (_wingetProbed) return _wingetPath;
        _wingetProbed = true;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var alias = Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(alias)) { _wingetPath = alias; return _wingetPath; }

        try
        {
            var packages = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            if (Directory.Exists(packages))
            {
                var candidate = Directory
                    .EnumerateDirectories(packages, "Microsoft.DesktopAppInstaller_*")
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .Select(d => Path.Combine(d, "winget.exe"))
                    .FirstOrDefault(File.Exists);
                if (candidate is not null) { _wingetPath = candidate; return _wingetPath; }
            }
        }
        catch (UnauthorizedAccessException) { /* WindowsApps is ACL'd; alias path is the norm */ }
        catch (IOException) { }

        // Last resort: trust PATH.
        _wingetPath = "winget.exe";
        return _wingetPath;
    }

    public static async Task<bool> IsWingetAvailableAsync(CancellationToken ct = default)
    {
        var exe = FindWinget();
        if (exe is null) return false;
        var r = await RunAsync(exe, "--version", ct, 15_000).ConfigureAwait(false);
        return r.Ok && r.StdOut.Contains('v');
    }

    public static async Task<ProcessResult> WingetAsync(
        string arguments, CancellationToken ct = default,
        int timeoutMs = 120_000, Action<string>? onLine = null)
    {
        var exe = FindWinget() ?? "winget.exe";
        return await RunAsync(exe, arguments, ct, timeoutMs, onLine).ConfigureAwait(false);
    }

    /// <summary>Runs a PowerShell snippet. Prefers pwsh 7, falls back to Windows PowerShell.</summary>
    public static async Task<ProcessResult> PowerShellAsync(
        string script, CancellationToken ct = default, int timeoutMs = 120_000)
    {
        var exe = FindPwsh();
        // -EncodedCommand avoids every layer of quote mangling between here and the shell.
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return await RunAsync(exe,
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            ct, timeoutMs).ConfigureAwait(false);
    }

    private static string? _pwshPath;

    /// <summary>Locates a PowerShell executable, preferring pwsh 7 and falling back to Windows PowerShell.</summary>
    public static string FindPowerShell() => FindPwsh();

    private static string FindPwsh()
    {
        if (_pwshPath is not null) return _pwshPath;
        foreach (var candidate in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "PowerShell", "7", "pwsh.exe"),
        })
        {
            if (File.Exists(candidate)) { _pwshPath = candidate; return _pwshPath; }
        }
        _pwshPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        return _pwshPath;
    }
}

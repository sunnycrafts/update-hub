using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using UpdateHub.Models;
using UpdateHub.Services;

namespace UpdateHub.Core;

public sealed record ElevatedUpdateResult(string WingetId, string Name, UpdateOutcome Outcome, string Message);

public sealed record ElevatedBatchProgress(int Done, int Total, string CurrentName);

/// <summary>
/// Applies several winget updates from a single elevated, hidden PowerShell session.
///
/// The per-app elevation path in <see cref="UpdateEngine"/> is reactive: it tries
/// unelevated first, and only re-launches through UAC when that fails, one "runas" per
/// app. For a batch that needs administrator rights that means one UAC prompt per app —
/// tolerable for one Critical update, unworkable for "Update all" on a machine where most
/// pending updates are per-machine installs. This runs the whole batch inside one elevated
/// PowerShell process, started with a single consent prompt, its window hidden, so the
/// user is not interrupted per package and never sees a console flash.
///
/// The elevated process only runs winget and reports results to a plain file; it never
/// touches the SQLite database or app settings. Elevation on a standard "admin approves
/// their own action" account keeps the same user profile, so this is not strictly
/// necessary for correctness, but keeping all persistence in the unelevated caller avoids
/// ever having to reason about a second identity touching user data.
/// </summary>
public sealed class ElevatedUpdateRunner
{
    private readonly string _dataRoot;
    private readonly LogService _log;

    public ElevatedUpdateRunner(string dataRoot, LogService log)
    {
        _dataRoot = dataRoot;
        _log = log;
    }

    /// <summary>
    /// The static script body. Package IDs are never interpolated into it — they travel
    /// through a JSON input file instead — so there is nothing here for a malformed or
    /// hostile app name to inject into.
    /// </summary>
    private const string ScriptBody = """
        param(
            [Parameter(Mandatory = $true)][string]$InputPath,
            [Parameter(Mandatory = $true)][string]$OutputPath,
            [Parameter(Mandatory = $true)][string]$WingetPath
        )
        $ErrorActionPreference = 'Continue'
        $ProgressPreference = 'SilentlyContinue'

        # Truncated up front so the watching process can tell the run has actually
        # started, rather than reading a stale file from a previous run.
        Set-Content -LiteralPath $OutputPath -Value '' -Encoding UTF8

        $items = Get-Content -LiteralPath $InputPath -Raw | ConvertFrom-Json

        function Write-Result($id, $name, $status, $message, $detail) {
            $row = [ordered]@{
                wingetId = $id
                name     = $name
                status   = $status
                message  = $message
                detail   = $detail
            }
            ($row | ConvertTo-Json -Compress) | Add-Content -LiteralPath $OutputPath -Encoding UTF8
        }

        foreach ($item in $items) {
            $id = $item.wingetId
            $name = $item.name
            Write-Result $id $name 'running' '' ''

            try {
                $output = & $WingetPath upgrade --id $id --exact --silent --source winget `
                    --accept-package-agreements --accept-source-agreements `
                    --disable-interactivity --include-unknown 2>&1 | Out-String
                $code = $LASTEXITCODE
            } catch {
                $output = $_.Exception.Message
                $code = -1
            }

            $status = if ($code -eq 0) { 'success' } else { 'failed' }
            $tail = $output.Trim()
            if ($tail.Length -gt 2000) { $tail = $tail.Substring($tail.Length - 2000) }
            Write-Result $id $name $status "exit=$code" $tail
        }

        Set-Content -LiteralPath ($OutputPath + '.done') -Value 'done' -Encoding UTF8
        """;

    /// <summary>
    /// Runs the batch. Returns per-app outcomes once the elevated process has exited;
    /// <paramref name="progress"/> is reported as each app finishes, read live from the
    /// file the elevated script is writing.
    /// </summary>
    public async Task<List<ElevatedUpdateResult>> RunAsync(
        IReadOnlyList<InstalledApp> apps,
        IProgress<ElevatedBatchProgress>? progress,
        CancellationToken ct)
    {
        var targets = apps.Where(a => !string.IsNullOrWhiteSpace(a.WingetId)).ToList();
        if (targets.Count == 0) return new List<ElevatedUpdateResult>();

        var runDir = Path.Combine(_dataRoot, "Elevated", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDir);
        var scriptPath = Path.Combine(runDir, "update.ps1");
        var inputPath = Path.Combine(runDir, "input.json");
        var outputPath = Path.Combine(runDir, "output.jsonl");
        var donePath = outputPath + ".done";

        try
        {
            File.WriteAllText(scriptPath, ScriptBody, Encoding.UTF8);

            var input = targets.Select(a => new { wingetId = a.WingetId, name = a.Name });
            File.WriteAllText(inputPath, JsonSerializer.Serialize(input), Encoding.UTF8);

            var wingetPath = ProcessRunner.FindWinget() ?? "winget.exe";
            var pwsh = ProcessRunner.FindPowerShell();

            var args = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden " +
                       $"-File \"{scriptPath}\" -InputPath \"{inputPath}\" -OutputPath \"{outputPath}\" " +
                       $"-WingetPath \"{wingetPath}\"";

            _log.Info($"Elevated batch update: {targets.Count} package(s), requesting administrator approval once.");

            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = pwsh,
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
            });

            if (proc is null)
            {
                _log.Error("Elevated batch update could not be started.");
                return targets.Select(a => new ElevatedUpdateResult(
                    a.WingetId!, a.Name, UpdateOutcome.Failed, "The elevated process could not be started."))
                    .ToList();
            }

            // Tail the output file while the process runs. It is opened with
            // FileShare.ReadWrite because the elevated script keeps appending to the same
            // file for the life of the batch.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var byId = new Dictionary<string, ElevatedUpdateResult>(StringComparer.OrdinalIgnoreCase);
            var doneCount = new int[1];

            while (!proc.HasExited)
            {
                await ReadNewLinesAsync(outputPath, seen, byId, targets.Count, progress, doneCount)
                    .ConfigureAwait(false);
                try { await Task.Delay(500, ct).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    // Cancellation does not kill an elevated process from an unelevated
                    // caller; leave it running and stop watching it.
                    _log.Warn("Elevated batch update: cancellation requested, no longer tracking progress.");
                    break;
                }
            }

            // Drain whatever was written between the last poll and process exit.
            await ReadNewLinesAsync(outputPath, seen, byId, targets.Count, progress, doneCount)
                .ConfigureAwait(false);

            if (proc.HasExited && proc.ExitCode != 0 && !File.Exists(donePath))
            {
                // The elevated process itself failed to launch the script (for example the
                // UAC prompt was dismissed) rather than any individual winget call failing.
                _log.Warn($"Elevated batch update process exited {proc.ExitCode} without completing.");
                foreach (var a in targets)
                    byId.TryAdd(a.WingetId!, new ElevatedUpdateResult(
                        a.WingetId!, a.Name, UpdateOutcome.Cancelled,
                        "Administrator approval was not granted, or the batch was interrupted."));
            }

            // Anything never reported (the elevated process crashed mid-batch) is unknown,
            // not silently dropped.
            foreach (var a in targets)
                byId.TryAdd(a.WingetId!, new ElevatedUpdateResult(
                    a.WingetId!, a.Name, UpdateOutcome.Failed, "No result was reported for this app."));

            return targets.Select(a => byId[a.WingetId!]).ToList();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _log.Info("Elevated batch update: administrator approval was declined.");
            return targets.Select(a => new ElevatedUpdateResult(
                a.WingetId!, a.Name, UpdateOutcome.Cancelled, "Administrator approval was declined."))
                .ToList();
        }
        catch (Exception ex)
        {
            _log.Error("Elevated batch update failed", ex);
            return targets.Select(a => new ElevatedUpdateResult(
                a.WingetId!, a.Name, UpdateOutcome.Failed, ex.Message))
                .ToList();
        }
        finally
        {
            try { Directory.Delete(runDir, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    /// <summary>
    /// Reads and applies any output lines not seen on a previous poll.
    /// <paramref name="doneCount"/> is a single-element array rather than a ref parameter
    /// because this method is async, and C# does not allow ref/out parameters there.
    /// </summary>
    private static async Task ReadNewLinesAsync(
        string outputPath,
        HashSet<string> seen,
        Dictionary<string, ElevatedUpdateResult> byId,
        int total,
        IProgress<ElevatedBatchProgress>? progress,
        int[] doneCount)
    {
        if (!File.Exists(outputPath)) return;

        string[] lines;
        try
        {
            using var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            lines = (await reader.ReadToEndAsync().ConfigureAwait(false))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (IOException)
        {
            return; // the script is mid-write; retry on the next poll
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || !seen.Add(line)) continue;

            JsonElement el;
            try { el = JsonDocument.Parse(line).RootElement; }
            catch (JsonException) { continue; }

            var id = el.GetProperty("wingetId").GetString() ?? "";
            var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? id : id;
            var status = el.GetProperty("status").GetString();
            var message = el.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            var detail = el.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "";

            if (status == "running")
            {
                progress?.Report(new ElevatedBatchProgress(doneCount[0], total, name));
                continue;
            }

            var outcome = InterpretStatus(status, message, detail);
            byId[id] = new ElevatedUpdateResult(id, name, outcome,
                DescribeOutcome(outcome, message, detail));
            doneCount[0]++;
            progress?.Report(new ElevatedBatchProgress(doneCount[0], total, name));
        }
    }

    /// <summary>
    /// Maps the elevated script's reported status to an <see cref="UpdateOutcome"/>. Public
    /// and static because it is pure logic worth testing directly against the exact JSON
    /// shape the PowerShell side emits, without needing a live elevated process.
    /// </summary>
    public static UpdateOutcome InterpretStatus(string? status, string message, string detail)
    {
        if (status != "success")
        {
            if (message.Contains("exit=1618", StringComparison.Ordinal)) return UpdateOutcome.Failed;
            if (detail.Contains("No applicable update", StringComparison.OrdinalIgnoreCase))
                return UpdateOutcome.NoUpdateNeeded;
            if (WingetLimits.Recognise(detail + " " + message) is not null)
                return UpdateOutcome.NotInWinget;
            return UpdateOutcome.Failed;
        }
        return UpdateOutcome.Success;
    }

    public static string DescribeOutcome(UpdateOutcome outcome, string message, string detail) => outcome switch
    {
        UpdateOutcome.Success => "Updated (elevated background).",
        UpdateOutcome.NoUpdateNeeded => "No applicable update was found.",
        UpdateOutcome.NotInWinget => WingetLimits.Recognise(detail + " " + message) ?? "winget cannot do this update.",
        _ => string.IsNullOrWhiteSpace(detail) ? message : $"{message}: {Truncate(detail, 160)}",
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

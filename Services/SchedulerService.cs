using UpdateHub.Core;
using UpdateHub.Models;

namespace UpdateHub.Services;

/// <summary>
/// Registers the recurring scan with Windows Task Scheduler through schtasks.exe. Using
/// the OS scheduler means the check still happens when UpdateHub is not running, which a
/// background thread inside the app could not do.
/// </summary>
public sealed class SchedulerService
{
    private const string TaskName = "UpdateHub Scheduled Scan";
    private readonly LogService _log;

    public SchedulerService(LogService log) => _log = log;

    public async Task<bool> ApplyAsync(AppSettings settings, CancellationToken ct = default)
    {
        if (settings.Schedule == ScheduleFrequency.Never)
            return await RemoveAsync(ct).ConfigureAwait(false);

        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            _log.Error("Cannot schedule: executable path unavailable");
            return false;
        }

        var schedule = settings.Schedule switch
        {
            ScheduleFrequency.Daily => "/sc DAILY",
            ScheduleFrequency.Weekly => "/sc WEEKLY /d MON",
            ScheduleFrequency.Monthly => "/sc MONTHLY /d 1",
            _ => "/sc DAILY",
        };

        var args = settings.ScheduleAutoUpdate ? "--scan-silent --auto-update" : "--scan-silent";
        var time = $"{Math.Clamp(settings.ScheduleHour, 0, 23):00}:00";

        // schtasks wants the whole command as one quoted token; inner quotes are doubled.
        var command = $"\\\"{exe}\\\" {args}";

        var result = await ProcessRunner.RunAsync("schtasks.exe",
            $"/create /tn \"{TaskName}\" /tr \"{command}\" {schedule} /st {time} /f",
            ct, 30_000).ConfigureAwait(false);

        if (!result.Ok)
        {
            _log.Error($"Scheduling failed ({result.ExitCode}): {result.Combined.Trim()}");
            return false;
        }

        _log.Info($"Scheduled task registered: {settings.Schedule} at {time}");
        return true;
    }

    public async Task<bool> RemoveAsync(CancellationToken ct = default)
    {
        var result = await ProcessRunner.RunAsync("schtasks.exe",
            $"/delete /tn \"{TaskName}\" /f", ct, 30_000).ConfigureAwait(false);
        // A missing task is the desired end state, so absence is not a failure.
        return result.Ok || result.Combined.Contains("cannot find", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> StatusAsync(CancellationToken ct = default)
    {
        var result = await ProcessRunner.RunAsync("schtasks.exe",
            $"/query /tn \"{TaskName}\" /fo LIST", ct, 20_000).ConfigureAwait(false);
        if (!result.Ok) return "Not scheduled";

        string? next = null, last = null;
        foreach (var line in result.StdOut.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("Next Run Time:", StringComparison.OrdinalIgnoreCase))
                next = t["Next Run Time:".Length..].Trim();
            else if (t.StartsWith("Last Run Time:", StringComparison.OrdinalIgnoreCase))
                last = t["Last Run Time:".Length..].Trim();
        }
        return next is null ? "Scheduled" : $"Next run {next}" + (last is null ? "" : $" · last {last}");
    }
}

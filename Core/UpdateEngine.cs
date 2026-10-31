using System.Diagnostics;
using System.IO;
using System.Net.Http;
using UpdateHub.Data;
using UpdateHub.Models;
using UpdateHub.Services;

namespace UpdateHub.Core;

public sealed class UpdateProgress
{
    public InstalledApp App { get; init; } = null!;
    public string Message { get; init; } = "";
    public UpdateOutcome Outcome { get; init; } = UpdateOutcome.Running;
    public int Index { get; init; }
    public int Total { get; init; }
}

/// <summary>
/// Applies updates. winget does the heavy lifting where a package is available; otherwise
/// we download the vendor's installer, verify its signature, and run it silently.
/// </summary>
public sealed class UpdateEngine
{
    private readonly AppRepository _repo;
    private readonly LogService _log;
    private readonly SettingsService _settings;
    private readonly HttpClient _http;

    public UpdateEngine(AppRepository repo, LogService log, SettingsService settings)
    {
        _repo = repo;
        _log = log;
        _settings = settings;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("UpdateHub/0.5");
    }

    public async Task<UpdateOutcome> UpdateOneAsync(
        InstalledApp app, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        var recordId = _repo.BeginUpdate(app);
        var previousVersion = app.InstalledVersion;

        void Report(string message, UpdateOutcome outcome = UpdateOutcome.Running) =>
            progress?.Report(new UpdateProgress { App = app, Message = message, Outcome = outcome });

        try
        {
            Report($"Updating {app.Name}…");
            _log.Info($"Update start: {app.Name} {previousVersion} -> {app.LatestVersion} via {app.UpdateMethod}");

            var (outcome, message) = app.UpdateMethod switch
            {
                UpdateMethod.Winget => await RunWingetUpgradeAsync(app, Report, ct).ConfigureAwait(false),
                UpdateMethod.Chocolatey => await RunChocoUpgradeAsync(app, ct).ConfigureAwait(false),
                UpdateMethod.Scoop => await RunScoopUpgradeAsync(app, ct).ConfigureAwait(false),
                UpdateMethod.Installer => await RunInstallerAsync(app, Report, ct).ConfigureAwait(false),
                UpdateMethod.MsStore => await RunMsStoreUpgradeAsync(app, Report, ct).ConfigureAwait(false),
                _ => (UpdateOutcome.Failed, "No automatic update method is available for this app."),
            };

            _repo.FinishUpdate(recordId, outcome, message);
            _log.Info($"Update end: {app.Name} -> {outcome}: {message}");

            if (outcome == UpdateOutcome.Success) MarkSuccess(app);

            Report(message, outcome);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            _repo.FinishUpdate(recordId, UpdateOutcome.Cancelled, "Cancelled");
            Report("Cancelled", UpdateOutcome.Cancelled);
            return UpdateOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            _log.Error($"Update failed for {app.Name}", ex);
            _repo.FinishUpdate(recordId, UpdateOutcome.Failed, ex.Message);
            Report(ex.Message, UpdateOutcome.Failed);
            return UpdateOutcome.Failed;
        }
    }

    /// <summary>
    /// Moves an app's recorded state onto the version it just updated to. Shared between
    /// the inline update path and the elevated batch runner so a successful update looks
    /// identical in the UI regardless of which path performed it.
    /// </summary>
    public void MarkSuccess(InstalledApp app)
    {
        // Record what we came from so a rollback has a target.
        app.InstalledVersion = app.LatestVersion ?? app.InstalledVersion;
        app.UpdateAvailable = false;
        app.InstalledReleaseDate = app.LatestReleaseDate;
        app.InstalledReleasePrecision = app.LatestReleasePrecision;
        app.InstallDate = DateTime.Now;
        _settings.Current.LastUpdate = DateTime.Now;
        _settings.Save();
    }

    /// <summary>
    /// Records an outcome produced by <see cref="ElevatedUpdateRunner"/> into the same
    /// update-history table the inline path writes, and applies success state. The
    /// elevated script itself never touches the database — this keeps all persistence in
    /// the unelevated caller.
    /// </summary>
    public void RecordElevatedResult(InstalledApp app, UpdateOutcome outcome, string message)
    {
        var recordId = _repo.BeginUpdate(app);
        _repo.FinishUpdate(recordId, outcome, message);
        _log.Info($"Elevated update end: {app.Name} -> {outcome}: {message}");
        if (outcome == UpdateOutcome.Success) MarkSuccess(app);
    }

    /// <summary>
    /// Applies several updates with bounded concurrency.
    ///
    /// These used to run strictly one at a time to avoid the Windows Installer mutex, but
    /// that made every download wait for the previous install to finish, which is where
    /// nearly all the wall-clock time went. Overlapping them lets downloads proceed in
    /// parallel; Windows Installer still serialises the install phase itself and returns
    /// 1618 when busy, which is retried rather than treated as a failure.
    /// </summary>
    public async Task<Dictionary<string, UpdateOutcome>> UpdateManyAsync(
        IReadOnlyList<InstalledApp> apps, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        var results = new System.Collections.Concurrent.ConcurrentDictionary<string, UpdateOutcome>();
        var lanes = Math.Clamp(_settings.Current.UpdateConcurrency, 1, 6);
        using var throttle = new SemaphoreSlim(lanes);

        int completed = 0;
        var total = apps.Count;

        var work = apps.Select(async app =>
        {
            await throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var reporter = new Progress<UpdateProgress>(p => progress?.Report(new UpdateProgress
                {
                    App = p.App, Message = p.Message, Outcome = p.Outcome,
                    Index = Volatile.Read(ref completed), Total = total,
                }));
                results[app.Key] = await UpdateOneAsync(app, reporter, ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Increment(ref completed);
                throttle.Release();
            }
        });

        await Task.WhenAll(work).ConfigureAwait(false);
        return results.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    // ---- winget ----------------------------------------------------------

    private async Task<(UpdateOutcome, string)> RunWingetUpgradeAsync(
        InstalledApp app, Action<string, UpdateOutcome> report, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(app.WingetId))
            return (UpdateOutcome.Failed, "No winget package ID.");

        // Pinning the source skips resolution against msstore, which measured ~240 ms of
        // pure overhead per invocation and several seconds on a cold start.
        var args = $"upgrade --id {Quote(app.WingetId)} --exact --silent --source winget " +
                   "--accept-package-agreements --accept-source-agreements " +
                   "--disable-interactivity --include-unknown";

        // Windows Installer allows one install at a time. With updates now overlapping,
        // a busy signal is expected rather than exceptional, so back off and retry.
        const int maxAttempts = 4;
        for (int attempt = 1; ; attempt++)
        {
            var result = await ProcessRunner.WingetAsync(args, ct, 900_000,
                line => { if (line.Length > 0 && !line.StartsWith("  ")) report(line, UpdateOutcome.Running); })
                .ConfigureAwait(false);

            var verdict = InterpretWinget(result);

            // Plenty of updates — MSIX packages and per-machine MSIs especially — simply
            // cannot install unelevated. Reporting that and stopping leaves the user with
            // a Critical item they cannot act on, so retry through UAC instead.
            if (verdict.Item1 == UpdateOutcome.RequiresElevation && !UninstallEngine.IsElevated())
            {
                report("Administrator approval needed…", UpdateOutcome.Running);
                return await RunElevatedWingetAsync(args, ct).ConfigureAwait(false);
            }

            if (attempt >= maxAttempts || !IsInstallerBusy(result)) return verdict;

            report($"Windows Installer busy, retrying ({attempt}/{maxAttempts - 1})…",
                UpdateOutcome.Running);
            await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Updates a Microsoft Store app through winget's msstore source, silently. winget
    /// lists Store apps by product ID when it knows them; otherwise the exact display name
    /// is the only handle available.
    /// </summary>
    private async Task<(UpdateOutcome, string)> RunMsStoreUpgradeAsync(
        InstalledApp app, Action<string, UpdateOutcome> report, CancellationToken ct)
    {
        var target = !string.IsNullOrWhiteSpace(app.WingetId)
            ? $"--id {Quote(app.WingetId)} --exact"
            : $"--name {Quote(app.Name)} --exact";
        var args = $"upgrade {target} --source msstore --silent " +
                   "--accept-package-agreements --accept-source-agreements --disable-interactivity";

        var result = await ProcessRunner.WingetAsync(args, ct, 900_000,
            line => { if (line.Length > 0 && !line.StartsWith("  ")) report(line, UpdateOutcome.Running); })
            .ConfigureAwait(false);

        var (outcome, message) = InterpretWinget(result);
        if (outcome == UpdateOutcome.Failed &&
            result.Combined.Contains("No installed package", StringComparison.OrdinalIgnoreCase))
            return (UpdateOutcome.Failed, "winget could not match this Store app; update it in the Microsoft Store.");
        return (outcome, outcome == UpdateOutcome.Success ? "Updated through the Microsoft Store." : message);
    }

    private static (UpdateOutcome, string) InterpretWinget(ProcessResult result)
    {
        var text = result.Combined;

        if (result.Ok) return (UpdateOutcome.Success, "Updated successfully.");
        if (result.TimedOut) return (UpdateOutcome.Failed, "The installer did not finish in time.");

        // winget returns unsigned 32-bit HRESULTs; the common ones deserve real messages.
        var code = unchecked((uint)result.ExitCode);
        var known = code switch
        {
            0x8A150011 => "No applicable update was found for this package.",
            0x8A15002B => "No applicable installer is available for this system's architecture.",
            0x8A150109 => "The package is pinned and was skipped.",
            0x8A150056 => "This package must be updated by its own updater.",
            _ => null,
        };
        if (known is not null)
            return (code == 0x8A150011 ? UpdateOutcome.NoUpdateNeeded : UpdateOutcome.Failed, known);

        if (NeedsElevation(result))
            return (UpdateOutcome.RequiresElevation,
                "This update needs administrator rights.");

        if (text.Contains("No available upgrade", StringComparison.OrdinalIgnoreCase)
            || text.Contains("No installed package", StringComparison.OrdinalIgnoreCase))
            return (UpdateOutcome.NoUpdateNeeded, "winget reports no upgrade is available.");

        var detail = LastMeaningfulLine(text);
        return (UpdateOutcome.Failed, detail ?? $"winget exited with 0x{code:X8}.");
    }

    /// <summary>
    /// Re-runs a winget upgrade elevated. Output cannot be captured through ShellExecute,
    /// so the verdict comes from the exit code; winget returns 0 on a successful upgrade.
    /// </summary>
    private async Task<(UpdateOutcome, string)> RunElevatedWingetAsync(
        string args, CancellationToken ct)
    {
        var exe = ProcessRunner.FindWinget() ?? "winget.exe";
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (proc is null)
                return (UpdateOutcome.Failed, "The elevated updater could not be started.");

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return proc.ExitCode == 0
                ? (UpdateOutcome.Success, "Updated (with administrator approval).")
                : (UpdateOutcome.Failed,
                   $"The elevated update did not complete (exit code {proc.ExitCode}).");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (UpdateOutcome.Cancelled, "Administrator approval was declined.");
        }
        catch (Exception ex)
        {
            return (UpdateOutcome.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Detects an update that failed purely for want of elevation.
    ///
    /// winget reports the *installer's* HRESULT inside its output while returning its own
    /// short exit code, so the message matters as much as the number. The wording varies
    /// by installer technology — MSIX says "administrator privileges are required" rather
    /// than "requires elevation" — which is why matching one phrase was not enough.
    /// </summary>
    private static bool NeedsElevation(ProcessResult result)
    {
        var text = result.Combined;
        var code = unchecked((uint)result.ExitCode);

        if (code is 0x80070005 or 740) return true;

        // ERROR_INSTALL_PACKAGE_REQUIRES_ADMIN and the MSI privilege codes, as embedded
        // HRESULTs in winget's message.
        foreach (var hresult in new[] { "0x80073D28", "0x80070005", "0x800704EC" })
            if (text.Contains(hresult, StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var phrase in new[]
        {
            "administrator privileges are required", "requires elevation",
            "access is denied", "run as administrator", "elevated",
            "sufficient privileges",
        })
            if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>
    /// 1618 is ERROR_INSTALL_ALREADY_RUNNING. winget surfaces the installer's own exit
    /// code, so both the number and the message are checked.
    /// </summary>
    private static bool IsInstallerBusy(ProcessResult result) =>
        result.ExitCode == 1618
        || result.Combined.Contains("another installation", StringComparison.OrdinalIgnoreCase)
        || result.Combined.Contains("already in progress", StringComparison.OrdinalIgnoreCase);

    private static string? LastMeaningfulLine(string text) =>
        text.Split('\n')
            .Select(l => l.Trim())
            .LastOrDefault(l => l.Length > 8 && !l.StartsWith("-") && !l.All(char.IsPunctuation));

    // ---- other package managers -----------------------------------------

    private async Task<(UpdateOutcome, string)> RunChocoUpgradeAsync(InstalledApp app, CancellationToken ct)
    {
        var exe = Scanning.ChocolateyScanner.ChocoExe;
        if (exe is null || string.IsNullOrWhiteSpace(app.ChocoId))
            return (UpdateOutcome.Failed, "Chocolatey is not available.");

        var r = await ProcessRunner.RunAsync(exe,
            $"upgrade {app.ChocoId} -y --no-progress --limit-output", ct, 900_000).ConfigureAwait(false);

        if (r.Ok) return (UpdateOutcome.Success, "Updated via Chocolatey.");
        if (r.Combined.Contains("administrator", StringComparison.OrdinalIgnoreCase))
            return (UpdateOutcome.RequiresElevation, "Chocolatey needs administrator rights.");
        return (UpdateOutcome.Failed, LastMeaningfulLine(r.Combined) ?? "Chocolatey upgrade failed.");
    }

    private async Task<(UpdateOutcome, string)> RunScoopUpgradeAsync(InstalledApp app, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(app.ScoopId))
            return (UpdateOutcome.Failed, "Scoop is not available.");

        // scoop is a PowerShell function, so it has to be invoked through a shell.
        var r = await ProcessRunner.PowerShellAsync(
            $"scoop update {app.ScoopId} 6>&1", ct, 900_000).ConfigureAwait(false);

        if (r.Ok) return (UpdateOutcome.Success, "Updated via Scoop.");
        return (UpdateOutcome.Failed, LastMeaningfulLine(r.Combined) ?? "Scoop update failed.");
    }

    // ---- direct installer download --------------------------------------

    private async Task<(UpdateOutcome, string)> RunInstallerAsync(
        InstalledApp app, Action<string, UpdateOutcome> report, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(app.DownloadUrl))
            return (UpdateOutcome.Failed, "No installer download URL is known.");
        if (!app.DownloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return (UpdateOutcome.Failed, "Refusing to download an installer over an insecure connection.");

        var dir = Path.Combine(App.DataRoot, "Downloads");
        Directory.CreateDirectory(dir);

        var fileName = SafeFileName(app.DownloadUrl, app.Name);
        var path = Path.Combine(dir, fileName);

        report($"Downloading {fileName}…", UpdateOutcome.Running);
        try
        {
            using var resp = await _http.GetAsync(app.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(path);
            await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return (UpdateOutcome.Failed, $"Download failed: {ex.Message}");
        }

        // Nothing gets executed until Windows agrees the signature is genuine.
        report("Verifying signature…", UpdateOutcome.Running);
        var (ok, reason) = SignatureVerifier.VerifyDownload(path, app.Publisher);
        if (!ok)
        {
            try { File.Delete(path); } catch { }
            return (UpdateOutcome.Failed, $"Installer rejected: {reason}");
        }

        report($"Running installer ({reason})…", UpdateOutcome.Running);
        var args = SilentArgsFor(path);
        var run = await ProcessRunner.RunAsync(path, args, ct, 1_800_000).ConfigureAwait(false);

        if (run.Ok) return (UpdateOutcome.Success, $"Installed. {reason}");
        if (run.ExitCode == 1602) return (UpdateOutcome.Cancelled, "The installer was cancelled.");
        if (run.ExitCode == 1603) return (UpdateOutcome.Failed, "The installer reported a fatal error.");
        if (run.ExitCode == 3010) return (UpdateOutcome.Success, "Installed. A restart is required.");
        if (run.ExitCode == 740) return (UpdateOutcome.RequiresElevation, "The installer needs administrator rights.");
        return (UpdateOutcome.Failed, $"Installer exited with code {run.ExitCode}.");
    }

    /// <summary>Silent-install switches by installer family, inferred from the file type.</summary>
    private static string SilentArgsFor(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".msi") return $"/i \"{path}\" /qn /norestart";

        // Inno Setup, NSIS and InstallShield each want different switches; these three
        // cover the overwhelming majority of Windows .exe installers.
        return "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /S /silent /qn";
    }

    private static string SafeFileName(string url, string appName)
    {
        string name;
        try { name = Path.GetFileName(new Uri(url).LocalPath); }
        catch (UriFormatException) { name = ""; }

        if (string.IsNullOrWhiteSpace(name) || name.Length > 120)
            name = NameNormalizer.Normalize(appName) + ".exe";

        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    // ---- Rollback (Phase 4) ---------------------------------------------

    /// <summary>
    /// Reinstalls a specific earlier version. Only winget can do this reliably, because it
    /// keeps every published version in its manifest index; other sources only ever expose
    /// the newest build, so there is nothing to roll back to.
    /// </summary>
    public async Task<(UpdateOutcome Outcome, string Message)> RollbackAsync(
        InstalledApp app, string targetVersion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(app.WingetId))
            return (UpdateOutcome.Failed,
                "Rollback needs a winget package, which keeps older versions available. " +
                "This app was not installed through winget.");

        _log.Info($"Rollback: {app.Name} -> {targetVersion}");
        var recordId = _repo.BeginUpdate(app);

        var args = $"install --id {Quote(app.WingetId)} --exact --version {Quote(targetVersion)} " +
                   "--silent --accept-package-agreements --accept-source-agreements " +
                   "--disable-interactivity --allow-downgrade --force";

        var result = await ProcessRunner.WingetAsync(args, ct, 900_000).ConfigureAwait(false);
        var (outcome, message) = InterpretWinget(result);

        if (outcome == UpdateOutcome.Success)
        {
            message = $"Rolled back to {targetVersion}.";
            app.InstalledVersion = targetVersion;
            app.UpdateAvailable = true;
        }

        _repo.FinishUpdate(recordId, outcome, message);
        return (outcome, message);
    }

    /// <summary>Versions winget can install for this package, newest first.</summary>
    public async Task<List<string>> GetAvailableVersionsAsync(InstalledApp app, CancellationToken ct)
    {
        var versions = new List<string>();
        if (string.IsNullOrWhiteSpace(app.WingetId)) return versions;

        var r = await ProcessRunner.WingetAsync(
            $"show --id {Quote(app.WingetId)} --exact --versions --disable-interactivity " +
            "--accept-source-agreements", ct, 60_000).ConfigureAwait(false);
        if (!r.Ok) return versions;

        bool past = false;
        foreach (var raw in r.StdOut.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("---")) { past = true; continue; }
            if (!past) continue;
            // The version list is one bare version per line.
            if (line.Contains(' ')) continue;
            versions.Add(line);
        }
        return versions;
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
}

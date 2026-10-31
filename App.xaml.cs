using System.IO;
using System.Windows;
using System.Windows.Threading;
using UpdateHub.Services;

namespace UpdateHub;

public partial class App : Application
{
    /// <summary>Per-user data root: %LOCALAPPDATA%\UpdateHub.</summary>
    /// <summary>Single source of truth for the displayed version: the csproj &lt;Version&gt;.</summary>
    public static string VersionText { get; } =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";

    public static string DataRoot { get; } =Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpdateHub");

    public static AppServices Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(Path.Combine(DataRoot, "Logs"));

        Services = new AppServices(DataRoot);
        Services.Log.Info("UpdateHub starting");

        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Services.Log.Error("Fatal", args.ExceptionObject as Exception);

        // Headless mode for the scheduled task: scan, refresh, optionally update, exit.
        var args2 = e.Args;
        if (args2.Any(a => a.Equals("--scan-silent", StringComparison.OrdinalIgnoreCase)))
        {
            _ = RunHeadlessAsync(args2.Any(a => a.Equals("--auto-update", StringComparison.OrdinalIgnoreCase)));
            return;
        }

        // Diagnostics: run the full pipeline and write a plain-text report. Useful for
        // support and for verifying a scan without opening the window.
        var reportIdx = Array.FindIndex(args2, a =>
            a.Equals("--report", StringComparison.OrdinalIgnoreCase));
        if (reportIdx >= 0)
        {
            var outPath = reportIdx + 1 < args2.Length
                ? args2[reportIdx + 1]
                : Path.Combine(DataRoot, "report.txt");
            _ = RunReportAsync(outPath, args2.Any(a =>
                a.Equals("--no-network", StringComparison.OrdinalIgnoreCase)));
            return;
        }

        // Smoke test: build every page and report XAML or binding failures, then exit.
        if (args2.Any(a => a.Equals("--uitest", StringComparison.OrdinalIgnoreCase)))
        {
            RunUiTest();
            return;
        }

        // Renders each page to a PNG so the UI can be reviewed without a live session.
        var shotIdx = Array.FindIndex(args2, a =>
            a.Equals("--screenshot", StringComparison.OrdinalIgnoreCase));
        if (shotIdx >= 0 && shotIdx + 1 < args2.Length)
        {
            RunScreenshots(args2[shotIdx + 1]);
            return;
        }

        // Scriptable export of the last scan: --export <path> (format from the extension).
        var exportIdx = Array.FindIndex(args2, a =>
            a.Equals("--export", StringComparison.OrdinalIgnoreCase));
        if (exportIdx >= 0 && exportIdx + 1 < args2.Length)
        {
            RunExport(args2[exportIdx + 1]);
            return;
        }

        // The app has one committed visual design (bright silver, hardcoded in App.xaml's
        // custom brushes) — there is no dark-mode counterpart for it. Applying anything
        // but Light here re-themes WPF-UI's native control resources (checkbox, combo box,
        // number box, password box — the controls Settings is dense with) to dark while
        // every custom brush stays hardcoded light, making most text render as
        // near-white-on-white. This bit a real user via a "Dark" value left over from
        // before the redesign; ServiceSettings now migrates that away on load too, but
        // this call is intentionally unconditional as a second line of defence.
        Services.Theme.Apply("Light");
        new UI.MainWindow().Show();
    }

    private async Task RunHeadlessAsync(bool autoUpdate)
    {
        try
        {
            var apps = await Services.Scanner.ScanAllAsync(CancellationToken.None);
            await Services.Providers.RefreshAllAsync(apps, false, null, CancellationToken.None);
            Services.Repo.CarryForwardKnownVersions(apps);
            Services.Repo.SaveApps(apps);

            var outdated = apps.Where(a => a.UpdateAvailable && !a.IsIgnored).ToList();
            if (outdated.Count > 0)
            {
                Services.Notifications.Show("Updates available",
                    $"{outdated.Count} app(s) need updating.");
                if (autoUpdate)
                    await Services.Updater.UpdateManyAsync(
                        outdated.Where(a => a.CanWingetUpdate).ToList(), null, CancellationToken.None);
            }
            Services.Settings.Current.LastScan = DateTime.Now;
            Services.Settings.Save();
        }
        catch (Exception ex)
        {
            Services.Log.Error("Headless run failed", ex);
        }
        finally
        {
            Shutdown();
        }
    }

    /// <summary>
    /// Constructs the window and every page against real data, capturing WPF's binding
    /// trace. Binding failures are silent at runtime, so without this they would only
    /// surface as blank fields nobody notices.
    /// </summary>
    private void RunUiTest()
    {
        var results = new List<string>();
        var bindingErrors = new System.Text.StringBuilder();

        var listener = new BindingTraceListener(bindingErrors);
        System.Diagnostics.PresentationTraceSources.Refresh();
        var source = System.Diagnostics.PresentationTraceSources.DataBindingSource;
        source.Listeners.Add(listener);
        source.Switch.Level = System.Diagnostics.SourceLevels.Error | System.Diagnostics.SourceLevels.Warning;

        try
        {
            Services.Theme.Apply("Light");

            var window = new UI.MainWindow();
            results.Add("MainWindow: OK");

            // Rendering off-screen forces the full layout pass that evaluates bindings.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -10000;
            window.Show();

            foreach (var page in new (string Name, Func<System.Windows.Controls.UserControl> Make)[]
            {
                ("DashboardPage", () => new UI.Views.DashboardPage()),
                ("InsightsPage", () => new UI.Views.InsightsPage()),
                ("InstalledAppsPage", () => new UI.Views.InstalledAppsPage()),
                ("UpdatesPage", () => new UI.Views.UpdatesPage()),
                ("HistoryPage", () => new UI.Views.HistoryPage()),
                ("IgnoredPage", () => new UI.Views.IgnoredPage()),
                ("SettingsPage", () => new UI.Views.SettingsPage()),
                ("DriversPage", () => new UI.Views.DriversPage()),
                ("AppListView", () => new UI.Views.AppListView()),
            })
            {
                try
                {
                    var view = page.Make();
                    view.DataContext = window.DataContext;
                    view.Measure(new System.Windows.Size(1400, 900));
                    view.Arrange(new Rect(0, 0, 1400, 900));
                    view.UpdateLayout();
                    results.Add($"{page.Name}: OK");
                }
                catch (Exception ex)
                {
                    results.Add($"{page.Name}: FAILED — {ex.GetType().Name}: {ex.Message}");
                }
            }

            try
            {
                var dialog = new UI.Views.LaunchOptionsDialog(
                    new Models.InstalledApp { Name = "Self-test" },
                    System.IO.Path.Combine(Environment.SystemDirectory, "notepad.exe"));
                dialog.Left = -10000;
                dialog.Show();
                // Let the title bar finish loading before closing, or WPF-UI throws
                // "Hwnd of zero" from its ContentRendered handler on a dead window.
                Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                dialog.Close();
                results.Add("LaunchOptionsDialog: OK");
            }
            catch (Exception ex)
            {
                results.Add($"LaunchOptionsDialog: FAILED — {ex.GetType().Name}: {ex.Message}");
            }

            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            window.Close();
        }
        catch (Exception ex)
        {
            results.Add($"MainWindow: FAILED — {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            source.Listeners.Remove(listener);
        }

        var report = string.Join("\n", results);
        var errors = bindingErrors.ToString();
        var outPath = System.IO.Path.Combine(DataRoot, "uitest.txt");
        System.IO.File.WriteAllText(outPath,
            report + "\n\n=== BINDING DIAGNOSTICS ===\n" +
            (errors.Length == 0 ? "(none)" : errors));

        Shutdown();
    }

    /// <summary>
    /// Renders every page to a PNG. The window is composited onto an opaque backdrop
    /// first: a FluentWindow's own background is transparent because DWM paints the Mica
    /// material behind it, and that material is not part of the visual tree, so a direct
    /// capture would come out see-through.
    /// </summary>
    private void RunScreenshots(string outDir)
    {
        try
        {
            System.IO.Directory.CreateDirectory(outDir);
            Services.Theme.Apply("Light");

            const int width = 1420, height = 900;
            var window = new UI.MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Width = width,
                Height = height,
            };
            window.Show();
            window.UpdateLayout();

            // Select the worst offender so the detail pane is exercised rather than
            // captured showing its empty-state placeholder.
            if (window.DataContext is UI.ViewModels.MainViewModel vm)
                vm.SelectedApp = vm.Apps
                    .Where(a => a.Age.IsKnown && a.Age.TotalDays > 0)
                    .OrderByDescending(a => a.Age.TotalDays)
                    .FirstOrDefault();

            foreach (var page in new[] { "dashboard", "updates", "apps", "insights", "history", "settings" })
            {
                window.ShowPage(page);

                // Let bindings, layout and the virtualised grid settle before capturing.
                for (int i = 0; i < 3; i++)
                {
                    window.UpdateLayout();
                    Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                }

                var target = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);

                var visual = new System.Windows.Media.DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    var backdrop = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0xF2, 0xF5, 0xF9));
                    var area = new Rect(0, 0, width, height);
                    dc.DrawRectangle(backdrop, null, area);
                    dc.DrawRectangle(new System.Windows.Media.VisualBrush(window), null, area);
                }
                target.Render(visual);

                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(target));
                var path = System.IO.Path.Combine(outDir, page + ".png");
                using var stream = System.IO.File.Create(path);
                encoder.Save(stream);

                Services.Log.Info($"Captured {path}");
            }

            window.Close();
        }
        catch (Exception ex)
        {
            Services.Log.Error("Screenshot capture failed", ex);
        }
        finally { Shutdown(); }
    }

    /// <summary>Exports the stored inventory; the file extension picks the format.</summary>
    private void RunExport(string path)
    {
        try
        {
            var format = System.IO.Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".xlsx" => Models.ExportFormat.Excel,
                ".json" => Models.ExportFormat.Json,
                ".pdf" => Models.ExportFormat.Pdf,
                _ => Models.ExportFormat.Csv,
            };
            var apps = Services.Repo.LoadApps();
            Services.Export.Export(apps, path, format);
            Services.Log.Info($"Exported {apps.Count} apps to {path}");
        }
        catch (Exception ex)
        {
            Services.Log.Error("Export failed", ex);
        }
        finally { Shutdown(); }
    }

    private sealed class BindingTraceListener : System.Diagnostics.TraceListener
    {
        private readonly System.Text.StringBuilder _sink;
        public BindingTraceListener(System.Text.StringBuilder sink) => _sink = sink;
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (message is not null) _sink.AppendLine(message);
        }
    }

    /// <summary>Runs a scan (and optionally a provider refresh) and writes a text report.</summary>
    private async Task RunReportAsync(string outPath, bool noNetwork)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sb = new System.Text.StringBuilder();
        try
        {
            var apps = await Services.Scanner.ScanAllAsync(CancellationToken.None);
            var scanMs = sw.ElapsedMilliseconds;

            if (!noNetwork)
                await Services.Providers.RefreshAllAsync(apps, false, null, CancellationToken.None);
            // Never let a scan that could not reach the network erase what earlier runs learned.
            Services.Repo.CarryForwardKnownVersions(apps);
            Services.Repo.SaveApps(apps);

            // This really did scan, so record it — otherwise the dashboard reports
            // "Last scan: Never" while showing the results of this very run.
            Services.Settings.Current.LastScan = DateTime.Now;
            Services.Settings.Save();

            sb.AppendLine($"UpdateHub report — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Machine: {Environment.MachineName}   Arch: {Sources.ArchitectureHelper.Display}");
            sb.AppendLine($"Scan: {scanMs} ms   Total: {sw.ElapsedMilliseconds} ms   winget: {Services.Scanner.WingetAvailable}");
            sb.AppendLine($"GitHub budget: {Services.Providers.GitHub.RateStatusText}");
            sb.AppendLine($"Catalog: {Services.Catalog.EntryCount}   Release rows cached: {Services.History.CountAll()}");
            sb.AppendLine(new string('=', 110));
            sb.AppendLine($"Applications: {apps.Count}");
            sb.AppendLine($"  with winget id : {apps.Count(a => a.WingetId is not null)}");
            sb.AppendLine($"  with github    : {apps.Count(a => a.GitHubRepo is not null)}");
            sb.AppendLine($"  latest known   : {apps.Count(a => a.LatestVersion is not null)}");
            sb.AppendLine($"  update avail   : {apps.Count(a => a.UpdateAvailable)}");
            sb.AppendLine($"  age known      : {apps.Count(a => a.Age.IsKnown)}");
            sb.AppendLine(new string('=', 110));
            sb.AppendLine($"{"NAME",-38} {"INSTALLED",-16} {"RELEASED",-13} {"LATEST",-16} {"RELEASED",-13} {"BEHIND",-14} {"CONF",-5} STATUS");
            sb.AppendLine(new string('-', 130));

            foreach (var a in apps.OrderByDescending(x => x.Age.IsKnown ? x.Age.TotalDays : -1))
            {
                sb.AppendLine(
                    $"{Trim(a.Name, 38),-38} {Trim(a.InstalledVersion, 16),-16} " +
                    $"{Trim(a.InstalledReleaseText, 13),-13} {Trim(a.LatestVersion ?? "—", 16),-16} " +
                    $"{Trim(a.LatestReleaseText, 13),-13} {Trim(a.Age.Short, 14),-14} " +
                    $"{a.Confidence + "%",-5} {a.StatusText}");
            }

            System.IO.File.WriteAllText(outPath, sb.ToString());
            Services.Log.Info($"Report written to {outPath}");
        }
        catch (Exception ex)
        {
            Services.Log.Error("Report failed", ex);
            try { System.IO.File.WriteAllText(outPath, sb + "\n\nFAILED: " + ex); } catch { }
        }
        finally { Shutdown(); }
    }

    private static string Trim(string? s, int max)
    {
        s ??= "";
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Services.Log.Error("Unhandled UI exception", e.Exception);
        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\nDetails written to the log.",
            "UpdateHub", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Services?.Dispose(); } catch { /* shutting down anyway */ }
        base.OnExit(e);
    }
}

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using UpdateHub.Core;
using UpdateHub.UI.ViewModels;

namespace UpdateHub.UI.Views;

public partial class DriversPage : UserControl
{
    private MachineInfo? _machine;
    private List<PendingDriver> _pending = new();

    public DriversPage() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel;

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        StatusText.Text = "Reading hardware and drivers…";
        try
        {
            var (machine, drivers) = await Task.Run(() => (DriverScanner.ReadMachine(), DriverScanner.ReadDrivers()));
            _machine = machine;
            MachineText.Text = machine.Summary;
            DriverList.ItemsSource = drivers;
            IntelButton.Visibility =
                (machine.Cpu + " " + string.Join(' ', machine.Gpus)).Contains("Intel", StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible : Visibility.Collapsed;

            StatusText.Text = $"Found {drivers.Count} drivers. Asking Windows Update for newer ones (up to a minute)…";
            PendingText.Text = "Checking…";

            var pending = await Task.Run(DriverScanner.ReadPendingFromWindowsUpdate);
            DriverScanner.Flag(drivers, pending);
            DriverList.ItemsSource = null;
            DriverList.ItemsSource = drivers;

            _pending = pending;
            PendingList.ItemsSource = pending;
            PendingList.Visibility = InstallPanel.Visibility =
                pending.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            PendingText.Text = pending.Count == 0
                ? "Windows Update has no newer drivers for this PC right now."
                : $"{pending.Count} newer driver update(s) available from Windows Update:";

            var attention = drivers.Count(d => d.Level == 2);
            StatusText.Text = $"{drivers.Count} drivers checked; {attention} need attention. " +
                              "Install driver updates from Windows optional updates, or from the maker's page.";
            Vm?.Services.Log.Info($"Drivers scan: {drivers.Count} drivers, {pending.Count} pending from Windows Update");
        }
        catch (Exception ex)
        {
            StatusText.Text = "The scan failed: " + ex.Message;
            Vm?.Services.Log.Error("Drivers scan failed", ex);
        }
        finally { ScanButton.IsEnabled = true; }
    }

    private async void InstallSelected_Click(object sender, RoutedEventArgs e)
    {
        var titles = PendingList.SelectedItems.OfType<PendingDriver>().Select(p => p.Title).ToList();
        if (titles.Count == 0)
        {
            InstallStatus.Text = "Select one or more driver updates first (Ctrl-click or Shift-click), or press Install all.";
            return;
        }
        await InstallAsync(titles);
    }

    private async void InstallAll_Click(object sender, RoutedEventArgs e) =>
        await InstallAsync(_pending.Select(p => p.Title).ToList());

    private async Task InstallAsync(List<string> titles)
    {
        if (titles.Count == 0) return;
        var confirm = MessageBox.Show(
            $"Install {titles.Count} driver update(s) from Windows Update?" + Environment.NewLine + Environment.NewLine +
            "Windows will ask for administrator approval. UpdateHub first tries to create a " +
            "system restore point so you can undo the change. The screen may flicker, and a " +
            "restart may be needed." + Environment.NewLine + Environment.NewLine +
            "Only Microsoft-distributed, signed drivers are installed.",
            "Install driver updates", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        InstallSelectedButton.IsEnabled = InstallAllButton.IsEnabled = ScanButton.IsEnabled = false;
        InstallStatus.Text = "Waiting for administrator approval…";
        try
        {
            var progress = new Progress<string>(m => InstallStatus.Text = m);
            var result = await DriverInstaller.InstallAsync(titles, App.DataRoot, progress, default);
            Vm?.Services.Log.Info($"Driver install: {result.Items.Count} items, reboot={result.RebootRequired}, error={result.Error}");

            if (result.Error is not null && result.Items.Count == 0)
            {
                InstallStatus.Text = result.Error;
            }
            else
            {
                var ok = result.Items.Count(i => i.Ok);
                InstallStatus.Text =
                    $"{ok} of {result.Items.Count} driver update(s) installed." +
                    (result.RebootRequired ? " Restart your PC to finish." : "") +
                    (result.RestorePoint.Length > 0 && result.RestorePoint != "created"
                        ? $" No restore point was made ({result.RestorePoint})." : " A restore point was created first.") +
                    Environment.NewLine +
                    string.Join(Environment.NewLine, result.Items.Select(i => $"• {i.Title}: {i.Detail}"));
            }
        }
        catch (Exception ex)
        {
            InstallStatus.Text = "Install failed: " + ex.Message;
            Vm?.Services.Log.Error("Driver install failed", ex);
        }
        finally
        {
            InstallSelectedButton.IsEnabled = InstallAllButton.IsEnabled = ScanButton.IsEnabled = true;
        }
    }

    private void Driver_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DriverList.SelectedItem is DriverInfo d) OpenUrl(d.VendorUrl);
    }

    private void OemPage_Click(object sender, RoutedEventArgs e)
    {
        var machine = _machine ?? DriverScanner.ReadMachine();
        OpenUrl(DriverScanner.OemPage(machine));
    }

    private void OptionalUpdates_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:windowsupdate-optionalupdates") { UseShellExecute = true }); }
        catch (Exception) { }
    }

    private async void InstallIntel_Click(object sender, RoutedEventArgs e)
    {
        IntelButton.IsEnabled = false;
        StatusText.Text = "Installing Intel Driver & Support Assistant silently…";
        var r = await ProcessRunner.WingetAsync(
            "install --id Intel.IntelDriverAndSupportAssistant --exact --source winget --silent " +
            "--accept-package-agreements --accept-source-agreements --disable-interactivity",
            default, 900_000);
        StatusText.Text = r.Ok
            ? "Installed. Open Intel Driver & Support Assistant from the Start Menu to find Intel driver updates."
            : "winget could not install it: " + ProcessRunner.LastLine(r.Combined);
        IntelButton.IsEnabled = true;
    }

    private static void OpenUrl(string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { }
    }
}

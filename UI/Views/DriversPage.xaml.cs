using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using UpdateHub.Core;
using UpdateHub.UI.ViewModels;

namespace UpdateHub.UI.Views;

public partial class DriversPage : UserControl
{
    private MachineInfo? _machine;

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

            PendingText.Text = pending.Count == 0
                ? "Windows Update has no newer drivers for this PC right now."
                : string.Join(Environment.NewLine, pending.Select(p =>
                    $"• {p.Title}" + (p.Date is null ? "" : $"  ({p.Date:d MMM yyyy})")));

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

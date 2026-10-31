using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using UpdateHub.Core;
using UpdateHub.Models;
using UpdateHub.UI.ViewModels;

namespace UpdateHub.UI.Views;

public partial class AppListView : UserControl
{
    public AppListView() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>Every selected row, so context actions apply to a multi-selection.</summary>
    private List<InstalledApp> Selection =>
        Grid.SelectedItems.Cast<InstalledApp>().ToList();

    private async void Grid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Vm?.SelectedApp is { UpdateAvailable: true } app)
            await Vm.UpdateManyAsync(new[] { app });
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var targets = Selection.Where(a => a.UpdateAvailable).ToList();
        if (targets.Count == 0) return;
        await Vm.UpdateManyAsync(targets);
    }

    private void Homepage_Click(object sender, RoutedEventArgs e)
    {
        var url = Vm?.SelectedApp?.Homepage ?? Vm?.SelectedApp?.DownloadPageUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        OpenExternal(url);
    }

    private void LaunchOptions_Click(object sender, RoutedEventArgs e)
    {
        var app = Vm?.SelectedApp;
        if (app is null) return;

        var exe = app.ExePath;
        if (string.IsNullOrWhiteSpace(exe) || !System.IO.File.Exists(exe))
        {
            MessageBox.Show(
                "UpdateHub does not know which program file starts this app, so it cannot set " +
                "launch options for it. This happens with Store apps and some system components.",
                app.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new LaunchOptionsDialog(app, exe) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void CheckManually_Click(object sender, RoutedEventArgs e)
    {
        if (Vm?.SelectedApp is { } app) OpenExternal(app.ManualCheckUrl);
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        var path = Vm?.SelectedApp?.InstallLocation;
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Directory.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private static void OpenExternal(string url)
    {
        // Only http(s) is ever launched; a file or shell URL from provider data must not
        // be handed to ShellExecute.
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { /* no browser registered */ }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var app = Vm?.SelectedApp;
        if (app is null) return;

        var text = $"""
            {app.Name}
            Publisher:        {app.Publisher ?? "Unknown"}
            Installed:        {app.InstalledVersion}   (released {app.InstalledReleaseText})
            Latest:           {app.LatestVersion ?? "Unknown"}   (released {app.LatestReleaseText})
            Installed on:     {app.InstallDate:d MMMM yyyy}
            Behind by:        {(app.Age.IsKnown ? app.Age.Long : "Unknown")}
            Versions behind:  {app.MajorVersionsBehind} major, {app.MinorVersionsBehind} minor
            Releases missed:  {app.ReleasesMissed} ({app.SecurityReleasesMissed} security)
            Status:           {app.StatusText}
            Confidence:       {app.Confidence}%  [{string.Join(", ", app.ConfidenceSources.Distinct())}]
            Winget ID:        {app.WingetId ?? "—"}
            """;
        try { Clipboard.SetText(text); } catch (Exception) { /* clipboard busy */ }
    }

    private void Verify_Click(object sender, RoutedEventArgs e)
    {
        var app = Vm?.SelectedApp;
        var path = app?.ExePath;
        if (app is null) return;

        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            MessageBox.Show(
                "No executable was recorded for this app, so its signature cannot be checked.",
                app.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var info = SignatureVerifier.Verify(path);
        var detail = info.SubjectName is null ? "" :
            $"\n\nSigned by: {info.SubjectName}\nIssued by: {info.IssuerName}\n" +
            $"Expires:   {info.NotAfter:d MMMM yyyy}\nThumbprint: {info.Thumbprint}";

        MessageBox.Show($"{info.StatusText}{detail}\n\n{path}",
            $"Signature — {app.Name}", MessageBoxButton.OK,
            info.IsTrusted ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var app = Vm?.SelectedApp;
        if (app is null || Vm is null) return;

        var plan = Vm.Services.Uninstaller.Plan(app);

        if (plan.Mode == UninstallMode.Unavailable)
        {
            MessageBox.Show(plan.Description, app.Name,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Uninstalling cannot be undone, so the confirmation spells out what will happen
        // and defaults to Cancel rather than to the destructive button.
        var confirm = MessageBox.Show(
            $"Uninstall {app.Name} {app.InstalledVersion}?\n\n" +
            $"{plan.Description}\n\n" +
            "This removes the program from your PC and cannot be undone.",
            "Confirm uninstall",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;

        if (UninstallEngine.IsSelf(app))
        {
            var self = MessageBox.Show(
                "UpdateHub will close and then remove itself.\n\n" +
                "Your settings and cached version history are kept, so reinstalling " +
                "restores them. Continue?",
                "Uninstall UpdateHub",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (self != MessageBoxResult.OK) return;

            await Vm.UninstallAsync(app);
            Application.Current.Shutdown();
            return;
        }

        await Vm.UninstallAsync(app);
    }

    private async void Rollback_Click(object sender, RoutedEventArgs e)
    {
        var app = Vm?.SelectedApp;
        if (app is null || Vm is null) return;

        if (string.IsNullOrWhiteSpace(app.WingetId))
        {
            MessageBox.Show(
                "Rollback works by reinstalling an older published version through winget. " +
                "This app was not installed from a winget package, so no earlier version is available.",
                app.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new RollbackDialog(app, Vm.Services) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.SelectedVersion is null) return;

        var confirm = MessageBox.Show(
            $"Reinstall {app.Name} at version {dialog.SelectedVersion}?\n\n" +
            "This downgrades the installed copy. Settings and data are usually preserved, " +
            "but that depends on the application.",
            "Confirm rollback", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        await Vm.RollbackAsync(app, dialog.SelectedVersion);
    }
}

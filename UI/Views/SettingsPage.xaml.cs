using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using UpdateHub.Models;
using UpdateHub.UI.ViewModels;

namespace UpdateHub.UI.Views;

public partial class SettingsPage : UserControl
{
    /// <summary>Suppresses the change handlers while controls are being populated.</summary>
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Load();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;
    private AppSettings? S => Vm?.Services.Settings.Current;

    private void Load()
    {
        if (Vm is null || S is null) return;
        _loading = true;

        HideSystem.IsChecked = S.HideSystemComponents;
        IncludeStore.IsChecked = S.IncludeMsStore;
        IncludePortable.IsChecked = S.IncludePortable;
        IncludeChoco.IsChecked = S.IncludeChocolatey;
        IncludeScoop.IsChecked = S.IncludeScoop;

        FolderList.Items.Clear();
        foreach (var f in S.CustomFolders) FolderList.Items.Add(f);
        UpdateFolderEmptyState();

        TokenBox.Password = S.GitHubToken ?? "";
        Select(CacheBox, S.CacheHours.ToString());
        ConcurrencyBox.Value = S.MaxConcurrency;

        ElevatedBatch.IsChecked = S.UseElevatedBackgroundUpdates;
        UpdateConcurrencyBox.Value = S.UpdateConcurrency;

        CheckStartup.IsChecked = S.CheckOnStartup;
        Notify.IsChecked = S.NotifyOnUpdates;
        Select(ScheduleBox, S.Schedule.ToString());
        HourBox.Value = S.ScheduleHour;
        AutoUpdate.IsChecked = S.ScheduleAutoUpdate;

        RefreshStats();
        _loading = false;

        _ = ShowScheduleStatusAsync();
    }

    private void RefreshStats()
    {
        if (Vm is null) return;
        BudgetText.Text = "Current budget: " + Vm.Services.Providers.GitHub.RateStatusText;
        CacheStats.Text = "Cached: " + Vm.CacheText;
        PluginStats.Text =
            $"{Vm.Services.Catalog.EntryCount} catalog entries " +
            $"({Vm.Services.Catalog.PluginEntryCount} from plugins), " +
            $"{Vm.Services.Providers.Vendor.RuleCount} vendor rules.";
        AboutText.Text =
            $"UpdateHub {App.VersionText} —{Sources.ArchitectureHelper.Display}\n" +
            $"Data folder: {App.DataRoot}\n" +
            $"Database: {Vm.Services.Db.Path} ({Vm.Services.Db.SizeBytes / 1024:N0} KB)";
    }

    private async Task ShowScheduleStatusAsync()
    {
        if (Vm is null) return;
        try { ScheduleStatus.Text = await Vm.Services.Scheduler.StatusAsync(); }
        catch (Exception ex) { ScheduleStatus.Text = "Could not read the scheduled task: " + ex.Message; }
    }

    private static void Select(ComboBox box, string tag)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (Equals(item.Tag?.ToString(), tag)) { box.SelectedItem = item; return; }
        }
        box.SelectedIndex = 0;
    }

    private static string TagOf(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";


    private void Token_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || S is null) return;
        var text = TokenBox.Password.Trim();
        S.GitHubToken = text.Length == 0 ? null : text;
        Save();
    }

    private void Setting_Changed(object sender, RoutedEventArgs e) => Persist();
    private void Setting_Changed(object sender, SelectionChangedEventArgs e) => Persist();

    private void Persist()
    {
        if (_loading || S is null) return;

        S.HideSystemComponents = HideSystem.IsChecked == true;
        S.IncludeMsStore = IncludeStore.IsChecked == true;
        S.IncludePortable = IncludePortable.IsChecked == true;
        S.IncludeChocolatey = IncludeChoco.IsChecked == true;
        S.IncludeScoop = IncludeScoop.IsChecked == true;

        if (int.TryParse(TagOf(CacheBox), out var hours)) S.CacheHours = hours;
        S.MaxConcurrency = (int)(ConcurrencyBox.Value ?? 6);

        S.UseElevatedBackgroundUpdates = ElevatedBatch.IsChecked == true;
        S.UpdateConcurrency = (int)(UpdateConcurrencyBox.Value ?? 3);

        S.CheckOnStartup = CheckStartup.IsChecked == true;
        S.NotifyOnUpdates = Notify.IsChecked == true;
        if (Vm is not null) Vm.Services.Notifications.Enabled = S.NotifyOnUpdates;

        if (Enum.TryParse<ScheduleFrequency>(TagOf(ScheduleBox), out var freq)) S.Schedule = freq;
        S.ScheduleHour = (int)(HourBox.Value ?? 9);
        S.ScheduleAutoUpdate = AutoUpdate.IsChecked == true;

        Save();
    }

    private void Save() => Vm?.Services.Settings.Save();

    private void UpdateFolderEmptyState() =>
        FolderEmpty.Visibility = FolderList.Items.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (S is null) return;
        var dialog = new OpenFolderDialog { Title = "Choose a folder to scan for portable apps" };
        if (dialog.ShowDialog() != true) return;
        if (S.CustomFolders.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase)) return;
        S.CustomFolders.Add(dialog.FolderName);
        FolderList.Items.Add(dialog.FolderName);
        UpdateFolderEmptyState();
        Save();
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (S is null || FolderList.SelectedItem is not string folder) return;
        S.CustomFolders.Remove(folder);
        FolderList.Items.Remove(folder);
        UpdateFolderEmptyState();
        Save();
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        if (MessageBox.Show(
                "Discard all cached version data and release history?\n\n" +
                "Rebuilding it needs network requests, and the GitHub hourly limit applies.",
                "Clear cache", MessageBoxButton.OKCancel, MessageBoxImage.Question)
            != MessageBoxResult.OK) return;

        Vm.Services.Db.ClearCache();
        RefreshStats();
    }

    private async void ApplySchedule_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null || S is null) return;
        Persist();
        var ok = await Vm.Services.Scheduler.ApplyAsync(S);
        ScheduleStatus.Text = ok
            ? "Schedule applied."
            : "Could not create the scheduled task — see the log.";
        if (ok) await ShowScheduleStatusAsync();
    }

    private async void RemoveSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null || S is null) return;
        S.Schedule = ScheduleFrequency.Never;
        Select(ScheduleBox, "Never");
        Save();
        await Vm.Services.Scheduler.RemoveAsync();
        ScheduleStatus.Text = "Not scheduled";
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Back up the app list",
            Filter = "App list (*.json)|*.json",
            FileName = $"UpdateHub-apps-{DateTime.Now:yyyy-MM-dd}.json",
        };
        if (dialog.ShowDialog() != true) return;

        BackupButton.IsEnabled = RestoreButton.IsEnabled = false;
        BackupStatus.Text = "Backing up…";
        try
        {
            var r = await Core.ProcessRunner.WingetAsync(
                $"export -o \"{dialog.FileName}\" --accept-source-agreements --disable-interactivity",
                default, 180_000);
            BackupStatus.Text = r.Ok && File.Exists(dialog.FileName)
                ? $"Saved to {dialog.FileName}"
                : "winget could not create the backup. " + r.Combined.Trim();
        }
        catch (Exception ex) { BackupStatus.Text = "Backup failed: " + ex.Message; }
        finally { BackupButton.IsEnabled = RestoreButton.IsEnabled = true; }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a backup to restore",
            Filter = "App list (*.json)|*.json",
        };
        if (dialog.ShowDialog() != true) return;

        if (MessageBox.Show(
                "Install every program in this backup that is not already installed?" + (char)10 + (char)10 +
                "They install silently, one after another, and this can take a long while.",
                "Restore from backup", MessageBoxButton.OKCancel, MessageBoxImage.Question)
            != MessageBoxResult.OK) return;

        BackupButton.IsEnabled = RestoreButton.IsEnabled = false;
        BackupStatus.Text = "Restoring… this can take a while.";
        try
        {
            var r = await Core.ProcessRunner.WingetAsync(
                $"import -i \"{dialog.FileName}\" --accept-package-agreements " +
                "--accept-source-agreements --ignore-unavailable --ignore-versions --disable-interactivity",
                default, 3_600_000);
            BackupStatus.Text = r.Ok
                ? "Restore finished. Run a scan to see the new programs."
                : "Restore finished with problems — see the log. " + Core.ProcessRunner.LastLine(r.Combined);
            Vm?.Services.Log.Info($"winget import exit {r.ExitCode}");
        }
        catch (Exception ex) { BackupStatus.Text = "Restore failed: " + ex.Message; }
        finally { BackupButton.IsEnabled = RestoreButton.IsEnabled = true; }
    }

    private void OpenPlugins_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(App.DataRoot, "Plugins");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private void SamplePlugin_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(App.DataRoot, "Plugins");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "example-plugin.json");

        File.WriteAllText(path, SamplePluginJson);

        MessageBox.Show(
            $"Wrote:\n{path}\n\nEdit it, then restart UpdateHub to load your rules.",
            "Example plugin", MessageBoxButton.OK, MessageBoxImage.Information);
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    /// <summary>Template showing both plugin sections, written on demand from Settings.</summary>
    private const string SamplePluginJson = """
        {
          "_comment": "Example UpdateHub plugin. Both sections are optional.",

          "entries": [
            {
              "match": ["myapp", "my app"],
              "name": "My App",
              "winget": "Publisher.MyApp",
              "github": "owner/repo",
              "home": "https://example.com/"
            }
          ],

          "providers": [
            {
              "name": "Example vendor JSON endpoint",
              "match": ["myapp"],
              "url": "https://example.com/api/latest.json",
              "json": "releases[0].version",
              "dateJson": "releases[0].date",
              "dateFormat": "yyyy-MM-dd",
              "downloadPage": "https://example.com/download",
              "ttlHours": 12
            },
            {
              "name": "Example vendor HTML page",
              "match": ["otherapp"],
              "url": "https://example.com/download.html",
              "regex": "Version ([0-9][0-9.]*)",
              "dateRegex": "Released ([0-9]{4}-[0-9]{2}-[0-9]{2})",
              "dateFormat": "yyyy-MM-dd"
            }
          ]
        }
        """;

    private void ImportCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var dialog = new OpenFileDialog
        {
            Title = "Import a community catalog",
            Filter = "Catalog JSON (*.json)|*.json",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var json = File.ReadAllText(dialog.FileName);
            // Parsed before saving so a malformed file cannot break the next start-up.
            var parsed = System.Text.Json.JsonSerializer.Deserialize<Sources.CatalogFile>(json);
            if (parsed?.Entries is null || parsed.Entries.Count == 0)
            {
                MessageBox.Show("That file has no catalog entries.", "Import",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Vm.Services.Catalog.SaveCommunity(json);
            MessageBox.Show(
                $"Imported {parsed.Entries.Count} entries.\nRestart UpdateHub to use them.",
                "Import", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not read that catalog:\n\n" + ex.Message, "Import",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{App.DataRoot}\"")
        { UseShellExecute = true });

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Vm.Services.Log.Path}\"")
        { UseShellExecute = true });
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        if (MessageBox.Show("Restore all settings to their defaults?", "Reset settings",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Vm.Services.Settings.Reset();
        Load();
    }
}

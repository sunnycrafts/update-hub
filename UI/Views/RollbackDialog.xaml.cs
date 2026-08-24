using System.Windows;
using System.Windows.Controls;
using UpdateHub.Core;
using UpdateHub.Models;
using UpdateHub.Services;

namespace UpdateHub.UI.Views;

/// <summary>Lists the versions winget can reinstall, so a rollback target can be chosen.</summary>
public partial class RollbackDialog
{
    private readonly InstalledApp _app;
    private readonly AppServices _services;

    public string? SelectedVersion { get; private set; }

    public RollbackDialog(InstalledApp app, AppServices services)
    {
        InitializeComponent();
        _app = app;
        _services = services;

        AppName.Text = app.Name;
        Subtitle.Text = $"Currently installed: {app.InstalledVersion}. " +
                        "Choose a published version to reinstall.";

        Loaded += async (_, _) => await LoadVersionsAsync();
    }

    private async Task LoadVersionsAsync()
    {
        try
        {
            var versions = await _services.Updater.GetAvailableVersionsAsync(
                _app, CancellationToken.None);

            Busy.Visibility = Visibility.Collapsed;
            VersionList.Visibility = Visibility.Visible;

            if (versions.Count == 0)
            {
                Subtitle.Text = "winget did not return any versions for this package, " +
                                "so there is nothing to roll back to.";
                return;
            }

            foreach (var v in versions)
            {
                var isCurrent = VersionComparer.Compare(v, _app.InstalledVersion) == 0;
                VersionList.Items.Add(new ListBoxItem
                {
                    Content = isCurrent ? $"{v}    (installed)" : v,
                    Tag = v,
                    IsEnabled = !isCurrent,
                    Padding = new Thickness(10, 6, 10, 6),
                });
            }
        }
        catch (Exception ex)
        {
            Busy.Visibility = Visibility.Collapsed;
            Subtitle.Text = "Could not list versions: " + ex.Message;
        }
    }

    private void VersionList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        OkButton.IsEnabled = VersionList.SelectedItem is ListBoxItem { Tag: string };

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (VersionList.SelectedItem is ListBoxItem { Tag: string version })
        {
            SelectedVersion = version;
            DialogResult = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

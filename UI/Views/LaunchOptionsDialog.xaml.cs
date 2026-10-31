using System.Windows;
using UpdateHub.Core;
using UpdateHub.Models;

namespace UpdateHub.UI.Views;

/// <summary>
/// Per-program launch settings: run as administrator, compatibility mode, display
/// overrides and a global shortcut key. Everything is stored in the places Windows itself
/// reads, so nothing depends on UpdateHub running.
/// </summary>
public partial class LaunchOptionsDialog
{
    private readonly InstalledApp _app;
    private readonly string _exe;
    private LaunchSettings _loaded = new();

    public LaunchOptionsDialog(InstalledApp app, string exePath)
    {
        InitializeComponent();
        _app = app;
        _exe = exePath;

        AppName.Text = app.Name;
        ExeText.Text = exePath;

        foreach (var mode in LaunchOptions.CompatModes) CompatBox.Items.Add(mode.Label);

        _loaded = LaunchOptions.Read(exePath);
        AdminBox.IsChecked = _loaded.RunAsAdmin;
        var index = Array.FindIndex(LaunchOptions.CompatModes, m => m.Token == _loaded.CompatOs);
        CompatBox.SelectedIndex = index < 0 ? 0 : index;
        FullscreenBox.IsChecked = _loaded.DisableFullscreenOptimizations;
        DpiBox.IsChecked = _loaded.OverrideHighDpi;
        ColorBox.IsChecked = _loaded.Reduced256Colors;
        ResBox.IsChecked = _loaded.Resolution640x480;

        try { HotkeyBox.Text = ShortcutHotkey.Current(exePath); }
        catch (Exception) { /* Windows Script Host unavailable; the box simply starts empty. */ }
    }

    private LaunchSettings Collect() => new()
    {
        RunAsAdmin = AdminBox.IsChecked == true,
        CompatOs = LaunchOptions.CompatModes[Math.Max(0, CompatBox.SelectedIndex)].Token,
        DisableFullscreenOptimizations = FullscreenBox.IsChecked == true,
        OverrideHighDpi = DpiBox.IsChecked == true,
        Reduced256Colors = ColorBox.IsChecked == true,
        Resolution640x480 = ResBox.IsChecked == true,
        OtherFlags = _loaded.OtherFlags,
    };

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!ShortcutHotkey.IsValid(HotkeyBox.Text))
        {
            Status.Text = "That shortcut key is not allowed. Use at least two of CTRL, ALT, SHIFT " +
                          "plus one letter, number or F-key, for example CTRL+ALT+F.";
            return;
        }

        try
        {
            LaunchOptions.Write(_exe, Collect());
            var hotkey = ShortcutHotkey.Normalize(HotkeyBox.Text);
            if (hotkey.Length > 0 || ShortcutHotkey.Current(_exe).Length > 0)
                ShortcutHotkey.Apply(_app.Name, _exe, hotkey);
            Status.Text = hotkey.Length > 0
                ? $"Saved. Press {hotkey} to open {_app.Name}."
                : "Saved.";
        }
        catch (Exception ex)
        {
            Status.Text = "Could not save: " + ex.Message;
        }
    }

    private void OpenNow_Click(object sender, RoutedEventArgs e) => Launch(false);
    private void OpenAdmin_Click(object sender, RoutedEventArgs e) => Launch(true);

    private void Launch(bool admin)
    {
        // Saved options apply to a launch, so write the current choices first.
        try { LaunchOptions.Write(_exe, Collect()); } catch (Exception) { }
        Status.Text = LaunchOptions.Launch(_exe, admin)
            ? "Started."
            : "The program did not start (administrator approval was declined?).";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

using Wpf.Ui.Appearance;

namespace UpdateHub.Services;

/// <summary>Applies light/dark/system theming through WPF-UI's application theme manager.</summary>
public sealed class ThemeService
{
    public string Current { get; private set; } = "Dark";

    public void Apply(string theme)
    {
        Current = theme;
        var resolved = theme switch
        {
            "Light" => ApplicationTheme.Light,
            "Dark" => ApplicationTheme.Dark,
            _ => SystemThemeIsLight() ? ApplicationTheme.Light : ApplicationTheme.Dark,
        };
        ApplicationThemeManager.Apply(resolved, Wpf.Ui.Controls.WindowBackdropType.Mica);
    }

    private static bool SystemThemeIsLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }
}

using Wpf.Ui.Appearance;

namespace UpdateHub.Services;

/// <summary>
/// Applies the app's theme through WPF-UI's application theme manager.
///
/// This once switched between Light/Dark/System. The app has since settled on one fixed
/// bright design — every card, text and status colour is a hardcoded custom brush in
/// App.xaml with no dark-mode counterpart — so Dark/System branches were removed rather
/// than left dead. They previously caused a real bug: applying anything but Light
/// re-themes WPF-UI's *native* control resources (checkbox, combo box, number box,
/// password box) to dark while the custom brushes stayed light, rendering most text on
/// controls-heavy pages like Settings as near-white-on-white. Keeping only one reachable
/// path here means that class of bug cannot resurface through this service, even if a
/// stale setting or a careless future call passes something else.
/// </summary>
public sealed class ThemeService
{
    public string Current { get; private set; } = "Light";

    public void Apply(string theme)
    {
        Current = "Light";
        ApplicationThemeManager.Apply(ApplicationTheme.Light, Wpf.Ui.Controls.WindowBackdropType.Mica);
    }
}

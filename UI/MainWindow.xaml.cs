using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UpdateHub.UI.ViewModels;
using UpdateHub.UI.Views;
// UseWindowsForms (for the tray icon elsewhere) puts System.Drawing.Color in scope
// alongside System.Windows.Media.Color; be explicit here.
using Color = System.Windows.Media.Color;

namespace UpdateHub.UI;

public partial class MainWindow
{
    private readonly MainViewModel _vm;
    private readonly Dictionary<string, UserControl> _pages = new();

    public MainWindow()
    {
        InitializeComponent();
        var windowTitle = $"UpdateHub {App.VersionText}";
        Title = windowTitle;
        AppTitleBar.Title = windowTitle;
        _vm = new MainViewModel(App.Services);
        DataContext = _vm;
        _vm.PropertyChanged += OnViewModelChanged;

        UpdateNavLabels();
        Navigate("dashboard");

        // Ctrl+F focuses search from anywhere in the window; F5 rescans.
        InputBindings.Add(new KeyBinding(
            new RelayCommandShim(() => { SearchBox.Focus(); SearchBox.SelectAll(); }),
            Key.F, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(_vm.ScanCommand, Key.F5, ModifierKeys.None));

        Loaded += async (_, _) =>
        {
            if (App.Services.Settings.Current.CheckOnStartup && _vm.Apps.Count == 0)
                await _vm.ScanCommand.ExecuteAsync(null);
        };

        Loaded += (_, _) => TintCloseButton();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.TotalApps)
            or nameof(MainViewModel.NeedUpdate) or nameof(MainViewModel.IgnoredCount))
            UpdateNavLabels();
    }

    /// <summary>Nav labels carry live counts, so they are rebuilt as the data changes.</summary>
    private void UpdateNavLabels()
    {
        NavApps.Content = _vm.TotalApps > 0 ? $"Applications   ({_vm.TotalApps})" : "Applications";
        NavUpdates.Content = _vm.NeedUpdate > 0 ? $"Updates   ({_vm.NeedUpdate})" : "Updates";
        NavIgnored.Content = _vm.IgnoredCount > 0 ? $"Ignored   ({_vm.IgnoredCount})" : "Ignored";
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag }) Navigate(tag);
    }

    /// <summary>Switches page programmatically. Used by the screenshot diagnostic mode.</summary>
    internal void ShowPage(string page) => Navigate(page);

    private void Navigate(string page)
    {
        if (!IsInitialized) return;
        _vm.CurrentPage = page;

        // The filter is a property of the page being viewed rather than a separate control.
        _vm.FilterMode = page switch
        {
            "updates" => "updates",
            "ignored" => "ignored",
            _ => "all",
        };
        if (page is "dashboard" or "insights") _vm.RefreshStats();

        if (!_pages.TryGetValue(page, out var view))
        {
            view = page switch
            {
                "dashboard" => new DashboardPage(),
                "insights" => new InsightsPage(),
                "drivers" => new DriversPage(),
                "apps" => new InstalledAppsPage(),
                "updates" => new UpdatesPage(),
                "history" => new HistoryPage(),
                "ignored" => new IgnoredPage(),
                "settings" => new SettingsPage(),
                _ => new DashboardPage(),
            };
            view.DataContext = _vm;
            _pages[page] = view;
        }

        if (page == "history") _vm.ReloadHistory();
        Host.Content = view;
    }

    /// <summary>Minimal ICommand for key bindings that only need to run an action.</summary>
    /// <summary>
    /// Colours the close button's glyph at rest, not just on hover.
    ///
    /// WPF-UI's own TitleBar template only tints the close button on MouseOver (white
    /// glyph on a red fill) — at rest it renders identically to Minimize and Maximize, a
    /// plain ink-coloured symbol. That is standard Windows chrome, but it does not fit
    /// this app's brighter, higher-contrast look, and it means Close cannot be told apart
    /// from the other two caption buttons until the cursor is already on it. TitleBarButton
    /// exposes ButtonsForeground per-button, so the close button alone can be retinted
    /// without touching WPF-UI's template or affecting Minimize/Maximize; the built-in
    /// hover behaviour (white on red) is untouched.
    /// </summary>
    private void TintCloseButton()
    {
        AppTitleBar.ApplyTemplate();
        if (AppTitleBar.Template.FindName("PART_CloseButton", AppTitleBar)
            is Wpf.Ui.Controls.TitleBarButton closeButton)
        {
            // Matches the app's own "Critical" priority red, so the close button reads as
            // consistent with the rest of the palette rather than an arbitrary red.
            var brush = new SolidColorBrush(Color.FromRgb(0xC0, 0x2A, 0x2A));
            brush.Freeze();
            closeButton.ButtonsForeground = brush;
        }
    }

    private sealed class RelayCommandShim : ICommand
    {
        private readonly Action _action;
        public RelayCommandShim(Action action) => _action = action;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _action();
    }
}

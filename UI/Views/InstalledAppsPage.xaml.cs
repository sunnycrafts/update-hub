using System.Windows.Controls;
using UpdateHub.UI.ViewModels;

namespace UpdateHub.UI.Views;

public partial class InstalledAppsPage : UserControl
{
    public InstalledAppsPage() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Vm is null) return;
        if (FilterBox.SelectedItem is ComboBoxItem { Tag: string tag }) Vm.FilterMode = tag;
    }

    private void Sort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Vm is null) return;
        if (SortBox.SelectedItem is ComboBoxItem { Tag: string tag }) Vm.SortMode = tag;
    }

    private void Export_Click(object sender, System.Windows.RoutedEventArgs e) =>
        ExportHelper.Prompt(Vm, this);
}

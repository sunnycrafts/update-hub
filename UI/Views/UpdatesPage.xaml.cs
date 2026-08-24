using System.Windows.Controls;
using UpdateHub.UI.ViewModels;

namespace UpdateHub.UI.Views;

public partial class UpdatesPage : UserControl
{
    public UpdatesPage() => InitializeComponent();

    private void Export_Click(object sender, System.Windows.RoutedEventArgs e) =>
        ExportHelper.Prompt(DataContext as MainViewModel, this);
}

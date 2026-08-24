using System.Windows;
using System.Windows.Controls;
using UpdateHub.UI.ViewModels;

namespace UpdateHub.UI.Views;

public partial class HistoryPage : UserControl
{
    public HistoryPage() => InitializeComponent();

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (MessageBox.Show("Delete the whole update history? Installed software is not affected.",
                "Clear history", MessageBoxButton.OKCancel, MessageBoxImage.Question)
            != MessageBoxResult.OK) return;

        vm.Services.Repo.ClearHistory();
        vm.ReloadHistory();
    }
}

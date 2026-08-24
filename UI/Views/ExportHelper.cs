using System.Windows;
using Microsoft.Win32;
using UpdateHub.Models;
using UpdateHub.UI.ViewModels;

namespace UpdateHub.UI.Views;

/// <summary>Shared save-file flow for the export buttons on several pages.</summary>
internal static class ExportHelper
{
    public static void Prompt(MainViewModel? vm, DependencyObject owner)
    {
        if (vm is null) return;

        var dialog = new SaveFileDialog
        {
            Title = "Export installed software",
            FileName = $"UpdateHub-{Environment.MachineName}-{DateTime.Now:yyyy-MM-dd}",
            DefaultExt = ".csv",
            Filter = "CSV spreadsheet (*.csv)|*.csv|" +
                     "Excel workbook (*.xlsx)|*.xlsx|" +
                     "JSON data (*.json)|*.json|" +
                     "PDF report (*.pdf)|*.pdf",
            InitialDirectory = vm.Services.Settings.Current.LastExportFolder
                               ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };

        if (dialog.ShowDialog() != true) return;

        // FilterIndex is 1-based and its order must match the Filter string above.
        var format = dialog.FilterIndex switch
        {
            2 => ExportFormat.Excel,
            3 => ExportFormat.Json,
            4 => ExportFormat.Pdf,
            _ => ExportFormat.Csv,
        };

        var path = dialog.FileName;
        var expected = Services.ExportService.DefaultExtension(format);
        if (!path.EndsWith(expected, StringComparison.OrdinalIgnoreCase))
            path = System.IO.Path.ChangeExtension(path, expected);

        try
        {
            vm.Export(path, format, filteredOnly: true);
            vm.Services.Settings.Current.LastExportFolder = System.IO.Path.GetDirectoryName(path);
            vm.Services.Settings.Save();

            if (MessageBox.Show($"Exported to:\n{path}\n\nOpen it now?", "Export complete",
                    MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            vm.Services.Log.Error("Export failed", ex);
            MessageBox.Show("Could not write the file:\n\n" + ex.Message,
                "Export failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

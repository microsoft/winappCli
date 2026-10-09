using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace ContosoImporter;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "CSV files (*.csv)|*.csv" };
        if (dialog.ShowDialog() != true) return;

        StatusText.Text = "Importing...";
        int rows = await Task.Run(() => File.ReadLines(dialog.FileName).Skip(1).Count());
        StatusText.Text = $"Imported {rows} rows.";
        // TODO: notify the user with a Windows notification here
    }
}

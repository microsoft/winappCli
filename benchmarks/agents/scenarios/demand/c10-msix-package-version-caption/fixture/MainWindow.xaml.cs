using System.Reflection;
using System.Windows;

namespace BorealLedger;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0.0";
        Title = $"Boreal Ledger {version}";
    }
}

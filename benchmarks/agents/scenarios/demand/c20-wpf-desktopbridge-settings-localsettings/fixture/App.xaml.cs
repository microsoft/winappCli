using System.Windows;

namespace LedgerLane;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Properties.Settings.Default.Upgrade();
        Properties.Settings.Default.Save();
        Properties.Settings.Default.Reload();
    }
}

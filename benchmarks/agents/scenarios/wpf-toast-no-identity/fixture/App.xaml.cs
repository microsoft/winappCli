using System.Windows;
using Microsoft.Windows.AppNotifications;

namespace Larkspur.Ledger;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppNotificationManager.Default.Register();
        base.OnStartup(e);
    }
}

using System.Windows;
namespace Alderfield;
public sealed class ConsoleApplication : Application {
    protected override void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnLastWindowClose;
        new Window { Title = "Alderfield Console" }.Show();
    }
}

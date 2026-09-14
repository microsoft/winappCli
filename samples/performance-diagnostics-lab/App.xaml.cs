using Microsoft.UI.Xaml;
using PerformanceDiagnosticsLab.Services;

namespace PerformanceDiagnosticsLab;

public partial class App : Application
{
    public static Window Window { get; private set; } = null!;

    public static LaunchOptions LaunchOptions { get; private set; } = LaunchOptions.Default;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        LaunchOptions = LaunchOptions.Parse(Environment.GetCommandLineArgs().Skip(1));
        LaunchOptions.ApplyPreWindowWorkload();

        Window = new MainWindow(LaunchOptions);
        Window.Activate();
    }
}

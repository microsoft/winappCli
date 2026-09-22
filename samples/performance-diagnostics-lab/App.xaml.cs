using Microsoft.UI.Xaml;
using PerformanceDiagnosticsLab.Contracts;
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
        if (LaunchOptions.ExitBeforeWindowCode is { } exitCode)
        {
            Environment.Exit(exitCode);
        }

        var startup = new StartupOrchestrator(LaunchOptions.ToStartupContext());
        var featurePages = new FeaturePageLoader();
        startup.RunCore();

        if (LaunchOptions.StartupMode == StartupMode.Eager)
        {
            startup.RunAll();
        }

        var launchContext = new AppLaunchContext(LaunchOptions, startup, featurePages);
        Window = new MainWindow(launchContext);
        Window.Activate();
    }
}

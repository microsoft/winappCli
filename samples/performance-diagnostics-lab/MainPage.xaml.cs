using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PerformanceDiagnosticsLab.Contracts;
using PerformanceDiagnosticsLab.Services;
using PerformanceDiagnosticsLab.ViewModels;

namespace PerformanceDiagnosticsLab;

public sealed partial class MainPage : Page
{
    private AppLaunchContext? _launchContext;

    public MainPageViewModel ViewModel { get; private set; } = null!;

    public MainPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _launchContext = (AppLaunchContext)e.Parameter;
        ViewModel = new MainPageViewModel(_launchContext.Options);

        if (_launchContext.Options.StartupMode == StartupMode.Eager)
        {
            _launchContext.FeaturePages.PreloadStartupPayload();
        }

        Loaded += MainPage_Loaded;
        Bindings.Update();
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainPage_Loaded;

        if (_launchContext!.Options.StartupMode == StartupMode.Deferred)
        {
            // Leave enough dispatcher time for the first frame and response probe before deferred work.
            await Task.Delay(250);
            _launchContext.Startup.RunAll();
            _launchContext.FeaturePages.PreloadStartupPayload();
        }

        await ViewModel.RunStartupScenarioAsync();
    }

    private void OpenRenderingWorkloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_launchContext is null)
        {
            return;
        }

        if (_launchContext.Options.StartupMode == StartupMode.Lazy)
        {
            _launchContext.Startup.RunAll();
            _launchContext.FeaturePages.PreloadStartupPayload();
        }

        FeatureContentHost.Content = _launchContext.FeaturePages.GetOrCreateRenderingPage();
        WorkloadsPanel.Visibility = Visibility.Collapsed;
        RenderingPanel.Visibility = Visibility.Visible;
    }

    private void BackToWorkloadsButton_Click(object sender, RoutedEventArgs e)
    {
        RenderingPanel.Visibility = Visibility.Collapsed;
        WorkloadsPanel.Visibility = Visibility.Visible;
    }
}

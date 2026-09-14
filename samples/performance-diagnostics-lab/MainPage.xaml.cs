using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PerformanceDiagnosticsLab.Services;
using PerformanceDiagnosticsLab.ViewModels;

namespace PerformanceDiagnosticsLab;

public sealed partial class MainPage : Page
{
    public MainPageViewModel ViewModel { get; private set; } = null!;

    public MainPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel = new MainPageViewModel(e.Parameter as LaunchOptions ?? LaunchOptions.Default);
        Loaded += MainPage_Loaded;
        Bindings.Update();
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainPage_Loaded;
        await ViewModel.RunStartupScenarioAsync();
    }

    private void RootNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItemContainer as NavigationViewItem)?.Tag?.ToString();
        ScenariosPanel.Visibility = tag == "scenarios" ? Visibility.Visible : Visibility.Collapsed;
        AutomationPanel.Visibility = tag == "automation" ? Visibility.Visible : Visibility.Collapsed;
        EnvironmentPanel.Visibility = tag == "environment" ? Visibility.Visible : Visibility.Collapsed;
    }
}

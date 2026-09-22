using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PerformanceDiagnosticsLab.Models;
using PerformanceDiagnosticsLab.Services;
using System.Collections.ObjectModel;

namespace PerformanceDiagnosticsLab.ViewModels;

public partial class MainPageViewModel : ObservableObject
{
    private static readonly TimeSpan StartupScenarioSettleDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ExitAfterScenarioDelay = TimeSpan.FromMilliseconds(500);
    private readonly LaunchOptions _launchOptions;
    private readonly PerformanceScenarioRunner _runner = new();
    private CancellationTokenSource? _scenarioCancellation;

    public MainPageViewModel(LaunchOptions launchOptions)
    {
        _launchOptions = launchOptions;
        Scenarios = new ObservableCollection<ScenarioCardViewModel>(
            ScenarioDefinition.All.Select(definition => new ScenarioCardViewModel(definition, RunScenarioAsync)));
    }

    public ObservableCollection<ScenarioCardViewModel> Scenarios { get; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Ready";

    [ObservableProperty]
    public partial bool IsCancelEnabled { get; set; }

    public async Task RunStartupScenarioAsync()
    {
        if (string.IsNullOrWhiteSpace(_launchOptions.ScenarioId))
        {
            return;
        }

        var scenario = Scenarios.FirstOrDefault(item =>
            string.Equals(item.Id, _launchOptions.ScenarioId, StringComparison.OrdinalIgnoreCase));

        if (scenario is null)
        {
            StatusText = $"Unknown startup scenario '{_launchOptions.ScenarioId}'.";
            return;
        }

        await Task.Delay(StartupScenarioSettleDelay);
        await RunScenarioAsync(scenario, _launchOptions);
    }

    private Task RunScenarioAsync(ScenarioCardViewModel scenario)
    {
        return RunScenarioAsync(scenario, LaunchOptions.Default);
    }

    private async Task RunScenarioAsync(ScenarioCardViewModel scenario, LaunchOptions options)
    {
        if (_scenarioCancellation is not null)
        {
            return;
        }

        _scenarioCancellation = new CancellationTokenSource();
        SetScenarioButtonsEnabled(false);
        IsCancelEnabled = scenario.Definition.SupportsCancellation;
        StatusText = $"Running: {scenario.Name}";

        try
        {
            await _runner.RunAsync(scenario.Definition, options, _scenarioCancellation.Token);
            StatusText = $"Completed: {scenario.Name}";

            if (options.ExitAfterScenario)
            {
                await Task.Delay(ExitAfterScenarioDelay);
                App.Window.Close();
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = $"Cancelled: {scenario.Name}";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed: {scenario.Name} ({ex.GetType().Name})";
        }
        finally
        {
            _scenarioCancellation.Dispose();
            _scenarioCancellation = null;
            IsCancelEnabled = false;
            SetScenarioButtonsEnabled(true);
        }
    }

    private void SetScenarioButtonsEnabled(bool isEnabled)
    {
        foreach (var scenario in Scenarios)
        {
            scenario.IsEnabled = isEnabled;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _scenarioCancellation?.Cancel();
    }

}

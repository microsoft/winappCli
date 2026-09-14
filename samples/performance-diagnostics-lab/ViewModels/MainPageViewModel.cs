using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PerformanceDiagnosticsLab.Models;
using PerformanceDiagnosticsLab.Services;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;

namespace PerformanceDiagnosticsLab.ViewModels;

public partial class MainPageViewModel : ObservableObject
{
    private readonly LaunchOptions _launchOptions;
    private readonly PerformanceScenarioRunner _runner = new();
    private CancellationTokenSource? _scenarioCancellation;

    public MainPageViewModel(LaunchOptions launchOptions)
    {
        _launchOptions = launchOptions;
        Scenarios = new ObservableCollection<ScenarioCardViewModel>(
            ScenarioDefinition.All.Select(definition => new ScenarioCardViewModel(definition, RunScenarioAsync)));

        try
        {
            PackageIdentity = Windows.ApplicationModel.Package.Current.Id.FullName;
        }
        catch (InvalidOperationException)
        {
            PackageIdentity = "Unpackaged";
        }
    }

    public ObservableCollection<ScenarioCardViewModel> Scenarios { get; }

    public IReadOnlyList<string> SemanticItems { get; } = ["Document 1", "Document 2", "Document 3"];

    public string ProcessId { get; } = Environment.ProcessId.ToString();

    public string ProcessArchitecture { get; } = RuntimeInformation.ProcessArchitecture.ToString();

    public string RuntimeVersion { get; } = RuntimeInformation.FrameworkDescription;

    public string PackageIdentity { get; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Ready";

    [ObservableProperty]
    public partial bool IsCancelEnabled { get; set; }

    [ObservableProperty]
    public partial int SemanticCounter { get; set; }

    [ObservableProperty]
    public partial bool IsSemanticFeatureEnabled { get; set; }

    [ObservableProperty]
    public partial string SemanticInput { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? SelectedSemanticItem { get; set; }

    [ObservableProperty]
    public partial string SemanticResult { get; set; } = "No interaction submitted.";

    public string SemanticCounterText => $"Invoked {SemanticCounter} time{(SemanticCounter == 1 ? string.Empty : "s")}";

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

    [RelayCommand]
    private void IncrementSemanticCounter()
    {
        SemanticCounter++;
        OnPropertyChanged(nameof(SemanticCounterText));
    }

    [RelayCommand]
    private void SubmitSemanticInteraction()
    {
        var selection = SelectedSemanticItem ?? "no item";
        SemanticResult = $"Submitted '{SemanticInput}' with {selection}; feature is {(IsSemanticFeatureEnabled ? "on" : "off")}.";
    }
}

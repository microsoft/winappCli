using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PerformanceDiagnosticsLab.Models;

namespace PerformanceDiagnosticsLab.ViewModels;

public sealed partial class ScenarioCardViewModel : ObservableObject
{
    private readonly Func<ScenarioCardViewModel, Task> _runAsync;

    public ScenarioCardViewModel(ScenarioDefinition definition, Func<ScenarioCardViewModel, Task> runAsync)
    {
        Definition = definition;
        _runAsync = runAsync;
    }

    public ScenarioDefinition Definition { get; }

    public string Id => Definition.Id;

    public string Name => Definition.Name;

    public string Description => Definition.Description;

    public string WorkloadLabel => Definition.WorkloadLabel;

    public string ExpectedObservation => Definition.ExpectedObservation;

    public string RunAutomationId => $"Run-{Id}";

    public string RunAccessibleName => $"Run {Name} scenario";

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    [RelayCommand]
    private Task RunAsync()
    {
        return _runAsync(this);
    }
}

namespace PerformanceDiagnosticsLab.Contracts;

public interface IStartupModule
{
    string Name { get; }

    void Initialize(StartupContext context);
}

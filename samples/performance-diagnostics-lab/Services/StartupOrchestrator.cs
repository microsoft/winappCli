using PerformanceDiagnosticsLab.Contracts;
using System.Reflection;

namespace PerformanceDiagnosticsLab.Services;

public sealed class StartupOrchestrator
{
    private static readonly IReadOnlyDictionary<string, ModuleDescriptor> Modules =
        new Dictionary<string, ModuleDescriptor>(StringComparer.Ordinal)
        {
            ["Core"] = new(
                "PerformanceDiagnosticsLab.Startup.Core",
                "PerformanceDiagnosticsLab.Startup.Core.CoreStartupModule"),
            ["Data"] = new(
                "PerformanceDiagnosticsLab.Startup.Data",
                "PerformanceDiagnosticsLab.Startup.Data.DataStartupModule"),
            ["Search"] = new(
                "PerformanceDiagnosticsLab.Startup.Search",
                "PerformanceDiagnosticsLab.Startup.Search.SearchStartupModule")
        };

    private readonly StartupContext _context;
    private readonly List<string> _loadedModules = [];

    public StartupOrchestrator(StartupContext context)
    {
        _context = context;
    }

    public IReadOnlyList<string> LoadedModules => _loadedModules;

    public void RunCore()
    {
        EnsureInitialized("Core");
    }

    public void RunData()
    {
        EnsureInitialized("Core");
        EnsureInitialized("Data");
    }

    public void RunSearch()
    {
        EnsureInitialized("Core");
        EnsureInitialized("Data");
        EnsureInitialized("Search");
    }

    public void RunAll()
    {
        RunSearch();
    }

    private void EnsureInitialized(string name)
    {
        if (_loadedModules.Contains(name, StringComparer.Ordinal))
        {
            return;
        }

        var descriptor = Modules[name];
        var assembly = Assembly.Load(new AssemblyName(descriptor.AssemblyName));
        var type = assembly.GetType(descriptor.TypeName, throwOnError: true)!;
        var module = (IStartupModule)Activator.CreateInstance(type)!;
        module.Initialize(_context);
        _loadedModules.Add(module.Name);
    }

    private sealed record ModuleDescriptor(string AssemblyName, string TypeName);
}

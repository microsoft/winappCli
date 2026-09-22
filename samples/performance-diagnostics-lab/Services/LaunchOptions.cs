using PerformanceDiagnosticsLab.Contracts;

namespace PerformanceDiagnosticsLab.Services;

public sealed record LaunchOptions(
    string? ScenarioId,
    int? DurationMilliseconds,
    int? MemoryMegabytes,
    StartupMode StartupMode,
    bool ExitAfterScenario,
    int? ExitBeforeWindowCode)
{
    public static LaunchOptions Default { get; } = new(
        null,
        null,
        null,
        StartupMode.Eager,
        false,
        null);

    public static LaunchOptions Parse(IEnumerable<string> arguments)
    {
        var values = arguments.ToArray();
        string? scenarioId = null;
        int? durationMilliseconds = null;
        int? memoryMegabytes = null;
        var startupMode = StartupMode.Eager;
        var exitAfterScenario = false;
        int? exitBeforeWindowCode = null;

        for (var index = 0; index < values.Length; index++)
        {
            switch (values[index])
            {
                case "--scenario":
                    scenarioId = ReadValue(values, ref index, "--scenario");
                    break;
                case "--duration-ms":
                    durationMilliseconds = ReadBoundedInteger(values, ref index, "--duration-ms", 1, 60_000);
                    break;
                case "--memory-mb":
                    memoryMegabytes = ReadBoundedInteger(values, ref index, "--memory-mb", 1, 512);
                    break;
                case "--startup-mode":
                    startupMode = ParseStartupMode(ReadValue(values, ref index, "--startup-mode"));
                    break;
                case "--exit-after":
                    exitAfterScenario = true;
                    break;
                case "--exit-before-window":
                    exitBeforeWindowCode = ReadInteger(values, ref index, "--exit-before-window");
                    break;
            }
        }

        return new LaunchOptions(
            scenarioId,
            durationMilliseconds,
            memoryMegabytes,
            startupMode,
            exitAfterScenario,
            exitBeforeWindowCode);
    }

    public StartupContext ToStartupContext()
    {
        return new StartupContext(StartupMode);
    }

    private static string ReadValue(IReadOnlyList<string> values, ref int index, string option)
    {
        if (++index >= values.Count || values[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        return values[index];
    }

    private static int ReadBoundedInteger(
        IReadOnlyList<string> values,
        ref int index,
        string option,
        int minimum,
        int maximum)
    {
        var value = ReadValue(values, ref index, option);
        if (!int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
        {
            throw new ArgumentOutOfRangeException(option, $"{option} must be between {minimum} and {maximum}.");
        }

        return parsed;
    }

    private static int ReadInteger(
        IReadOnlyList<string> values,
        ref int index,
        string option)
    {
        var value = ReadValue(values, ref index, option);
        if (!int.TryParse(value, out var parsed))
        {
            throw new ArgumentException($"{option} must be a 32-bit integer.");
        }

        return parsed;
    }

    private static StartupMode ParseStartupMode(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "eager" => StartupMode.Eager,
            "deferred" => StartupMode.Deferred,
            "lazy" => StartupMode.Lazy,
            _ => throw new ArgumentException("--startup-mode must be eager, deferred, or lazy.")
        };
    }
}

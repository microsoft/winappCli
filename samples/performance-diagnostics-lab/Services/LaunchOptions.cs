using System.Diagnostics;

namespace PerformanceDiagnosticsLab.Services;

public sealed record LaunchOptions(
    string? ScenarioId,
    int? DurationMilliseconds,
    int? MemoryMegabytes,
    int StartupDelayMilliseconds,
    int StartupCpuMilliseconds,
    bool ExitAfterScenario)
{
    public static LaunchOptions Default { get; } = new(null, null, null, 0, 0, false);

    public static LaunchOptions Parse(IEnumerable<string> arguments)
    {
        var values = arguments.ToArray();
        string? scenarioId = null;
        int? durationMilliseconds = null;
        int? memoryMegabytes = null;
        var startupDelayMilliseconds = 0;
        var startupCpuMilliseconds = 0;
        var exitAfterScenario = false;

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
                case "--startup-delay-ms":
                    startupDelayMilliseconds = ReadBoundedInteger(values, ref index, "--startup-delay-ms", 0, 30_000);
                    break;
                case "--startup-cpu-ms":
                    startupCpuMilliseconds = ReadBoundedInteger(values, ref index, "--startup-cpu-ms", 0, 30_000);
                    break;
                case "--exit-after":
                    exitAfterScenario = true;
                    break;
            }
        }

        return new LaunchOptions(
            scenarioId,
            durationMilliseconds,
            memoryMegabytes,
            startupDelayMilliseconds,
            startupCpuMilliseconds,
            exitAfterScenario);
    }

    public void ApplyPreWindowWorkload()
    {
        if (StartupDelayMilliseconds > 0)
        {
            Thread.Sleep(StartupDelayMilliseconds);
        }

        if (StartupCpuMilliseconds > 0)
        {
            BurnCpu(StartupCpuMilliseconds);
        }
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

    private static void BurnCpu(int durationMilliseconds)
    {
        var stopwatch = Stopwatch.StartNew();
        var value = 0.5;
        while (stopwatch.ElapsedMilliseconds < durationMilliseconds)
        {
            value = Math.Sqrt(value + 1.23456789);
        }

        GC.KeepAlive(value);
    }
}

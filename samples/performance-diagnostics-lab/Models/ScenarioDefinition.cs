namespace PerformanceDiagnosticsLab.Models;

public sealed record ScenarioDefinition(
    string Id,
    string Name,
    string Description,
    string WorkloadLabel,
    string ExpectedObservation,
    int DurationMilliseconds,
    int MemoryMegabytes = 0,
    bool SupportsCancellation = true)
{
    public static IReadOnlyList<ScenarioDefinition> All { get; } =
    [
        new(
            "baseline",
            "Baseline",
            "Remain responsive with no intentional work.",
            "5 seconds",
            "Stable resources and successful response probes.",
            5_000),
        new(
            "ui-stall-100",
            "UI stall (100 ms)",
            "Block the UI thread briefly.",
            "100 ms",
            "The stall may fall below the configured probe cadence.",
            100,
            SupportsCancellation: false),
        new(
            "ui-stall-1000",
            "UI stall (1 second)",
            "Block the UI thread for one second.",
            "1 second",
            "A short failed-probe interval followed by recovery.",
            1_000,
            SupportsCancellation: false),
        new(
            "ui-stall-4000",
            "UI stall (4 seconds)",
            "Block the UI thread long enough to be clearly observed.",
            "4 seconds",
            "A sustained failed-probe interval followed by recovery.",
            4_000,
            SupportsCancellation: false),
        new(
            "ui-stall-6000",
            "UI stall (6 seconds)",
            "Block the UI thread beyond common hung-window thresholds.",
            "6 seconds",
            "A long failed-probe interval followed by recovery.",
            6_000,
            SupportsCancellation: false),
        new(
            "ui-cpu",
            "UI-thread CPU",
            "Run bounded compute work on the UI thread.",
            "5 seconds",
            "High CPU correlated with failed response probes.",
            5_000,
            SupportsCancellation: false),
        new(
            "background-cpu",
            "Background CPU",
            "Run the same bounded compute work on a worker thread.",
            "5 seconds",
            "High CPU while the window remains responsive.",
            5_000),
        new(
            "memory-step",
            "Memory step",
            "Allocate, touch, hold, and release a bounded memory block.",
            "256 MB / 5 seconds",
            "Private commit rises while memory is held, then may recede.",
            5_000,
            256),
        new(
            "file-io",
            "File I/O",
            "Write and read a bounded temporary file, then remove it.",
            "64 MB",
            "A process-attributed I/O increase without a UI stall.",
            0),
        new(
            "normal-exit",
            "Normal exit",
            "Close the main window without an error.",
            "500 ms delay",
            "The target exits normally and recording finalizes.",
            500,
            SupportsCancellation: false)
    ];
}

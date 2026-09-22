// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Services.Performance;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class PerformanceRecordCommandTests : BaseCommandTests
{
    [TestMethod]
    public async Task RecordCommand_RejectsRemovedExternalCollectorOptionBeforeLaunching()
    {
        var command = GetRequiredService<PerfRecordCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            [".", "--with-dotnet-trace"]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains($"{ConsoleStdOut}{ConsoleStdErr}", "Unrecognized argument: '--with-dotnet-trace'.");
    }

    [TestMethod]
    public async Task ObserveUntilStopped_ContinuesSamplingWithoutConsoleInput()
    {
        using var recording = new ExitingRecordingSession(exitAfterObservations: 3);
        await using var managed = new NoOpManagedDiagnosticsSession();

        var reason = await PerfRecordCommand.Handler.ObserveUntilStoppedAsync(
            recording,
            managed,
            durationSec: 0,
            onEvents: null,
            TestContext.CancellationToken);

        Assert.AreEqual("target-exited", reason);
        Assert.AreEqual(4, recording.ObservationCount);
        Assert.AreEqual(1, recording.ForcedSnapshotCount);
    }

    [TestMethod]
    public async Task ObserveUntilStopped_StopsWhenLaunchProcessCouldNotBeAdmitted()
    {
        using var recording = new MissingLaunchRecordingSession();
        await using var managed = new NoOpManagedDiagnosticsSession();

        var reason = await PerfRecordCommand.Handler.ObserveUntilStoppedAsync(
            recording,
            managed,
            durationSec: 0,
            onEvents: null,
            TestContext.CancellationToken);

        Assert.AreEqual("launch-process-not-observed", reason);
        Assert.AreEqual(1, recording.ObservationCount);
        Assert.AreEqual(1, recording.ForcedSnapshotCount);
    }

    [TestMethod]
    public void RecordCommand_DetectsValuelessProperty()
    {
        var command = new PerfRecordCommand();
        var parseResult = command.Parse(
            ["--property", "--json"],
            WinAppParserConfiguration.Default);

        Assert.IsTrue(RunCommand.Handler.HasValuelessProperty(
            parseResult,
            PerfRecordCommand.PropertyOption));
    }

    [TestMethod]
    public void FormatLiveEvent_UsesElapsedTimeAndConcreteIdentity()
    {
        var line = PerfRecordCommand.Handler.FormatLiveEvent(new()
        {
            Type = nameof(StartupEventType.WindowResponseFailed),
            ElapsedMs = 37_210.29,
            BoundaryResolutionMs = 100,
            ProcessId = 14240,
            WindowHandle = 984940,
        });

        Assert.AreEqual(
            "[+37.210s] Window response failed HWND 0xF076C PID 14240",
            line);
    }

    [TestMethod]
    public void FormatStartupStage_ReportsFactsWithoutInferringCause()
    {
        var line = PerfRecordCommand.Handler.FormatStartupStage(new()
        {
            Name = "process-to-visible",
            StartBoundary = "process",
            EndBoundary = "visible",
            StartMs = 180,
            EndMs = 1_710,
            DurationMs = 1_530,
            StartBoundaryResolutionMs = 100,
            EndBoundaryResolutionMs = 250,
            ResponseFailureDurationMs = 400,
            Resources = new()
            {
                Status = "complete",
                StartSampleMs = 185,
                EndSampleMs = 1_720,
                StartSampleDelayMs = 5,
                EndSampleDelayMs = 10,
                CpuTimeMs = 1_310,
                PrivateBytesChange = 10 * 1024 * 1024,
                ReadBytes = 8UL * 1024 * 1024,
                WriteBytes = 0,
            },
        });

        Assert.AreEqual(
            "Process -> Visible: 1530.0 ms; CPU 1310.0 ms over 1535.0 ms sample window; " +
            "private +10.0 MiB; " +
            "I/O read 8.0 MiB, write 0 bytes; unresponsive 400.0 ms",
            line);
    }

    [TestMethod]
    public void FormatStartupFailure_PreservesRawExitWithoutCallingItACrash()
    {
        var summary = new PerformanceStartupSummary
        {
            SchemaVersion = "0.3",
            Status = "partial",
            Outcome = "exited-before-responsive",
            StopReason = "target-exited",
            Disposition = "Pending",
            LastBoundary = "first-window",
            LastBoundaryMs = 620,
            ObservationEndMs = 910,
            ProcessExits = [],
            Stages = [],
            Evidence = new()
            {
                TimelineEventCount = 4,
                ResourceSampleCount = 2,
                PartialResourceSampleCount = 0,
            },
        };
        var processExit = new StartupProcessExit
        {
            ProcessId = 42,
            ProcessStartTimeUtcTicks = 1234,
            ElapsedMs = 910,
            ExitCode = unchecked((int)0xC0000005),
            ExitCodeHex = "0xC0000005",
            ExitKind = "nonzero",
            BeforeBoundary = "visible",
        };

        Assert.AreEqual(
            "Startup outcome: process exited before Responsive; last observed " +
            "First window at 620.0 ms; stop reason target-exited",
            PerfRecordCommand.Handler.FormatStartupOutcome(summary));
        var exitLine = PerfRecordCommand.Handler.FormatStartupProcessExit(processExit);
        Assert.AreEqual(
            "Process exit: PID 42 at 910.0 ms; code -1073741819 (0xC0000005); " +
            "nonzero exit; before Visible",
            exitLine);
        Assert.DoesNotContain("crash", exitLine, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void FormatStartupStage_DoesNotRoundObservedSmallIoToZero()
    {
        var line = PerfRecordCommand.Handler.FormatStartupStage(new()
        {
            Name = "process-to-first-window",
            StartBoundary = "process",
            EndBoundary = "first-window",
            StartMs = 200,
            EndMs = 800,
            DurationMs = 600,
            StartBoundaryResolutionMs = 200,
            EndBoundaryResolutionMs = 250,
            ResponseFailureDurationMs = 0,
            Resources = new()
            {
                Status = "complete",
                CpuTimeMs = 300,
                PrivateBytesChange = 1024,
                ReadBytes = 4156,
                WriteBytes = 0,
            },
        });

        Assert.Contains("I/O read 4.1 KiB, write 0 bytes", line);
    }

    private sealed class ExitingRecordingSession(int exitAfterObservations)
        : IPerformanceRecordingSession
    {
        public int ObservationCount { get; private set; }
        public int ForcedSnapshotCount { get; private set; }
        public string BundlePath => string.Empty;
        public StartupLaunchDisposition Disposition => StartupLaunchDisposition.Launched;
        public int ActivationProcessId => 1;
        public bool HasObservedProcesses => true;
        public bool LaunchProcessAdmissionFailed => false;
        public bool HasActiveProcesses => ObservationCount < exitAfterObservations;
        public bool HasNewlyLaunchedTarget => true;
        public bool HasNewlyLaunchedTargetExited => !HasActiveProcesses;
        public int? TargetExitCode => HasActiveProcesses ? null : 0;
        public ProcessIdentity? TargetProcess => new(1, 1);
        public PerformanceWindowTarget? ResponsiveWindow => null;
        public IReadOnlyList<PerformanceTimelineEntry> LastWrittenEvents { get; private set; } = [];
        public IReadOnlyList<StartupEvent> LastObservedEvents { get; private set; } = [];

        public StartupObservationUpdate Observe()
        {
            ObservationCount++;
            LastWrittenEvents = [];
            LastObservedEvents = [];
            return new([], Disposition, new Dictionary<int, ProcessAdmissionStatus>());
        }

        public void CaptureResourceSnapshot(bool force = false)
        {
            if (force)
            {
                ForcedSnapshotCount++;
            }
        }

        public (string EtlPath, string TemporaryDirectory) CreateWprPaths() =>
            throw new NotSupportedException();

        public string CreateManagedPath() =>
            throw new NotSupportedException();

        public void ConfigureWprCollector(IWprCollector collector) =>
            throw new NotSupportedException();

        public Task<PerformanceRecordingLaunchResult> LaunchAsync(
            IReadOnlyList<string> runArguments,
            TextWriter output,
            TextWriter error,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public double WriteScenarioMarker(
            string phase,
            string boundary,
            int? stepOrdinal = null,
            string? verb = null,
            string? status = null) =>
            throw new NotSupportedException();

        public void WriteUiActionBoundary(
            string phase,
            int stepOrdinal,
            string verb,
            Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.UiActionBoundary boundary,
            PerformanceWindowTarget target) =>
            throw new NotSupportedException();


        public PerformanceRecordResult Complete(
            string status,
            string stopReason,
            WprCollectorResult wpr,
            ManagedDiagnosticsResult managed,
            XamlAnalysisResult? xaml = null) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class NoOpManagedDiagnosticsSession : IManagedDiagnosticsSession
    {
        public ManagedDiagnosticsResult Result => new()
        {
            Collector = "Managed EventPipe",
            Status = "not-collected",
            Coverage = "not-collected",
            LossStatus = "not-applicable",
        };

        public Task ObserveAsync(
            IReadOnlyList<StartupEvent> events,
            StartupLaunchDisposition disposition,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task StopAsync(StartupLaunchDisposition disposition) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MissingLaunchRecordingSession : IPerformanceRecordingSession
    {
        public int ObservationCount { get; private set; }
        public int ForcedSnapshotCount { get; private set; }
        public string BundlePath => string.Empty;
        public StartupLaunchDisposition Disposition => StartupLaunchDisposition.Pending;
        public int ActivationProcessId => 42;
        public bool HasObservedProcesses => false;
        public bool LaunchProcessAdmissionFailed => true;
        public bool HasActiveProcesses => false;
        public bool HasNewlyLaunchedTarget => false;
        public bool HasNewlyLaunchedTargetExited => false;
        public int? TargetExitCode => null;
        public ProcessIdentity? TargetProcess => null;
        public PerformanceWindowTarget? ResponsiveWindow => null;
        public IReadOnlyList<PerformanceTimelineEntry> LastWrittenEvents => [];
        public IReadOnlyList<StartupEvent> LastObservedEvents => [];

        public StartupObservationUpdate Observe()
        {
            ObservationCount++;
            return new(
                [],
                Disposition,
                new Dictionary<int, ProcessAdmissionStatus>
                {
                    [ActivationProcessId] = ProcessAdmissionStatus.NotFound,
                });
        }

        public void CaptureResourceSnapshot(bool force = false)
        {
            if (force)
            {
                ForcedSnapshotCount++;
            }
        }

        public (string EtlPath, string TemporaryDirectory) CreateWprPaths() =>
            throw new NotSupportedException();
        public string CreateManagedPath() =>
            throw new NotSupportedException();
        public void ConfigureWprCollector(IWprCollector collector) =>
            throw new NotSupportedException();
        public Task<PerformanceRecordingLaunchResult> LaunchAsync(
            IReadOnlyList<string> runArguments,
            TextWriter output,
            TextWriter error,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public double WriteScenarioMarker(
            string phase,
            string boundary,
            int? stepOrdinal = null,
            string? verb = null,
            string? status = null) =>
            throw new NotSupportedException();
        public void WriteUiActionBoundary(
            string phase,
            int stepOrdinal,
            string verb,
            Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.UiActionBoundary boundary,
            PerformanceWindowTarget target) =>
            throw new NotSupportedException();
        public PerformanceRecordResult Complete(
            string status,
            string stopReason,
            WprCollectorResult wpr,
            ManagedDiagnosticsResult managed,
            XamlAnalysisResult? xaml = null) =>
            throw new NotSupportedException();
        public void Dispose()
        {
        }
    }
}

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
    public void RecordCommand_DefaultsToExistingOutputDiscoveryWithoutImplicitFilters()
    {
        var project = WriteTarget("App.csproj");
        var command = new PerfRecordCommand();
        var parseResult = command.Parse([project.FullName], WinAppParserConfiguration.Default);

        var arguments = PerfRecordCommand.Handler.BuildRunArguments(parseResult);

        CollectionAssert.Contains(arguments, "--no-build");
        CollectionAssert.Contains(arguments, "--discover-existing-output");
        CollectionAssert.DoesNotContain(arguments, "--configuration");
        CollectionAssert.DoesNotContain(arguments, "--arch");
    }

    [TestMethod]
    public void RecordCommand_BuildUsesReleaseUnlessConfigurationIsExplicit()
    {
        var project = WriteTarget("App.csproj");
        var command = new PerfRecordCommand();

        var defaultBuild = PerfRecordCommand.Handler.BuildRunArguments(
            command.Parse([project.FullName, "--build"], WinAppParserConfiguration.Default));
        var debugBuild = PerfRecordCommand.Handler.BuildRunArguments(
            command.Parse([project.FullName, "--build", "--configuration", "Debug"], WinAppParserConfiguration.Default));

        CollectionAssert.Contains(defaultBuild, "Release");
        CollectionAssert.DoesNotContain(defaultBuild, "--no-build");
        CollectionAssert.Contains(debugBuild, "Debug");
        CollectionAssert.DoesNotContain(debugBuild, "--discover-existing-output");
    }

    [TestMethod]
    public void RecordCommand_NoBuildSelectionOptionsBecomeDiscoveryFilters()
    {
        var project = WriteTarget("App.csproj");
        var command = new PerfRecordCommand();
        var parseResult = command.Parse(
            [project.FullName, "--configuration", "Custom", "--arch", "arm64"],
            WinAppParserConfiguration.Default);

        var arguments = PerfRecordCommand.Handler.BuildRunArguments(parseResult);

        CollectionAssert.Contains(arguments, "Custom");
        CollectionAssert.Contains(arguments, "arm64");
        CollectionAssert.Contains(arguments, "--discover-existing-output");
    }

    [TestMethod]
    public void RecordCommand_ExecutableRoutesWithoutBuildOrDiscoveryOptions()
    {
        var executable = WriteTarget("App.exe");
        var command = new PerfRecordCommand();
        var parseResult = command.Parse(
            [executable.FullName, "--args", "--hello"],
            WinAppParserConfiguration.Default);

        Assert.IsNull(PerfRecordCommand.Handler.ValidateRunOptions(parseResult));
        var arguments = PerfRecordCommand.Handler.BuildRunArguments(parseResult);
        CollectionAssert.DoesNotContain(arguments, "--no-build");
        CollectionAssert.DoesNotContain(arguments, "--discover-existing-output");
        CollectionAssert.DoesNotContain(arguments, "--configuration");
    }

    [TestMethod]
    public void RecordCommand_RejectsInvalidBuildAndExecutableCombinations()
    {
        var executable = WriteTarget("App.exe");
        var project = WriteTarget("App.csproj");
        var command = new PerfRecordCommand();

        var exeError = PerfRecordCommand.Handler.ValidateRunOptions(
            command.Parse([executable.FullName, "--build", "--arch", "x64"], WinAppParserConfiguration.Default));
        var restoreError = PerfRecordCommand.Handler.ValidateRunOptions(
            command.Parse([project.FullName, "--no-restore"], WinAppParserConfiguration.Default));

        StringAssert.Contains(exeError, "--build");
        StringAssert.Contains(exeError, "--arch");
        StringAssert.Contains(restoreError, "--no-restore requires --build");
    }

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
    public async Task RecordCommand_RejectsRemovedNoBuildOption()
    {
        var command = GetRequiredService<PerfRecordCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [".", "--no-build"]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains($"{ConsoleStdOut}{ConsoleStdErr}", "Unrecognized argument: '--no-build'.");
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
    public void LiveOutput_DefaultUsesTimelineAndHumanMeaning()
    {
        var output = new StringWriter();
        var presenter = new PerformanceLiveOutputPresenter(verbose: false);

        presenter.Write(output,
        [
            Event(StartupEventType.ActivationRequested, 0),
            Event(StartupEventType.ProcessObserved, 276),
            Event(StartupEventType.WindowObserved, 1_447, windowHandle: 7),
            Event(StartupEventType.WindowVisible, 1_802, windowHandle: 7),
            Event(StartupEventType.WindowResponsive, 2_249, windowHandle: 7),
            Event(StartupEventType.WindowResponseFailed, 7_849, windowHandle: 7),
            Event(StartupEventType.WindowResponseRecovered, 12_222, windowHandle: 7),
            Event(StartupEventType.ProcessExited, 18_418, exitCode: 0),
        ]);

        Assert.AreEqual(
            string.Join(
                Environment.NewLine,
                "[T+00:00:00.000] App activation requested",
                "[T+00:00:00.276] App process started",
                "[T+00:00:01.802] App window visible",
                "[T+00:00:02.249] App window responding - 2.25 s after activation",
                "[T+00:00:07.849] App window stopped responding",
                "[T+00:00:12.222] App window responding again - observed unresponsive for 4.37 s",
                "[T+00:00:18.418] App exited normally",
                string.Empty),
            output.ToString());
    }

    [TestMethod]
    public void LiveOutput_VerboseRetainsTechnicalIdentityAndProbeDetails()
    {
        var presenter = new PerformanceLiveOutputPresenter(verbose: true);
        var line = presenter.Format(new()
        {
            Type = nameof(StartupEventType.WindowResponseProbeFailed),
            ElapsedMs = 37_210.29,
            BoundaryResolutionMs = 100,
            ProcessId = 14240,
            WindowHandle = 984940,
            WindowThreadId = 73,
            ResponseProbeOutcome = nameof(WindowResponseProbeOutcome.Win32Failure),
            Win32ErrorCode = 0,
        });

        Assert.AreEqual(
            "[T+00:00:37.210] Window response probe failed; PID 14240; " +
            "HWND 0xF076C; thread 73; probe Win32Failure; Win32 0",
            line);
    }

    [TestMethod]
    public void LiveOutput_DefaultLabelsMultipleVisibleWindows()
    {
        var output = new StringWriter();
        var presenter = new PerformanceLiveOutputPresenter(verbose: false);

        presenter.Write(output,
        [
            Event(StartupEventType.WindowVisible, 100, windowHandle: 7),
            Event(StartupEventType.WindowVisible, 200, windowHandle: 8),
            Event(StartupEventType.WindowResponseFailed, 300, windowHandle: 8),
            Event(StartupEventType.WindowResponseRecovered, 750, windowHandle: 8),
        ]);

        StringAssert.Contains(output.ToString(), "[T+00:00:00.200] App window 2 visible");
        StringAssert.Contains(output.ToString(), "[T+00:00:00.300] App window 2 stopped responding");
        StringAssert.Contains(
            output.ToString(),
            "[T+00:00:00.750] App window 2 responding again - observed unresponsive for 450 ms");
    }

    [TestMethod]
    public void LiveOutput_TimelinePositionDoesNotWrapAfterOneHour()
    {
        var presenter = new PerformanceLiveOutputPresenter(verbose: false);

        var line = presenter.Format(Event(
            StartupEventType.ProcessObserved,
            25 * 60 * 60 * 1_000 + 2_003));

        Assert.AreEqual("[T+25:00:02.003] App process started", line);
    }

    [TestMethod]
    public void LiveOutput_DefaultHidesNonTargetProcessLifecycle()
    {
        var output = new StringWriter();
        var presenter = new PerformanceLiveOutputPresenter(verbose: false);

        presenter.Write(
            output,
            [
                Event(StartupEventType.ProcessObserved, 100, processId: 41),
                Event(StartupEventType.ProcessObserved, 200, processId: 42),
                Event(StartupEventType.ProcessExited, 300, processId: 41, exitCode: 0),
                Event(StartupEventType.ProcessExited, 400, processId: 42, exitCode: 7),
            ],
            new ProcessIdentity(42, 1234));

        Assert.AreEqual(
            string.Join(
                Environment.NewLine,
                "[T+00:00:00.200] App process started",
                "[T+00:00:00.400] App exited with code 7",
                string.Empty),
            output.ToString());
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

    private static PerformanceTimelineEntry Event(
        StartupEventType type,
        double elapsedMs,
        long? windowHandle = null,
        int processId = 42,
        int? exitCode = null) =>
        new()
        {
            Type = type.ToString(),
            ElapsedMs = elapsedMs,
            BoundaryResolutionMs = 100,
            ProcessId = processId,
            ProcessStartTimeUtcTicks = 1234,
            WindowHandle = windowHandle,
            WindowThreadId = windowHandle is null ? null : 73,
            ExitCode = exitCode,
        };

    private FileInfo WriteTarget(string name)
    {
        var path = Path.Combine(_tempDirectory.FullName, name);
        File.WriteAllText(path, string.Empty);
        return new(path);
    }

    private sealed class ExitingRecordingSession(int exitAfterObservations)
        : IPerformanceRecordingSession
    {
        public int ObservationCount { get; private set; }
        public int ForcedSnapshotCount { get; private set; }
        public string BundlePath => string.Empty;
        public DateTimeOffset TimelineStartedUtc => DateTimeOffset.UnixEpoch;
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
        public DateTimeOffset TimelineStartedUtc => DateTimeOffset.UnixEpoch;
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

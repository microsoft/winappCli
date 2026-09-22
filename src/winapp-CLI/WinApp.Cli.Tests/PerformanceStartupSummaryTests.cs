// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class PerformanceStartupSummaryTests
{
    private static readonly string[] ExpectedStageNames =
    [
        "activation-to-process",
        "process-to-first-window",
        "first-window-to-visible",
        "visible-to-responsive",
    ];
    private static readonly string[] ExpectedCollapsedBoundaryNames =
    [
        "first-window",
        "visible",
    ];

    [TestMethod]
    public void Create_SeparatesCompleteStartupIntoConsecutiveObservedStages()
    {
        PerformanceTimelineEntry[] events =
        [
            Event(StartupEventType.ActivationRequested, 0, 0),
            Event(StartupEventType.ProcessObserved, 180, 100),
            Event(StartupEventType.WindowObserved, 620, 250),
            Event(StartupEventType.WindowVisible, 1_710, 250),
            Event(StartupEventType.WindowResponsive, 1_760, 100),
            Event(StartupEventType.WindowResponseFailed, 2_000, 100),
        ];
        ResourceTimelineEntry[] resources =
        [
            Resource(180, 0, "process", 10, 100, 100, 200),
            Resource(680, 500, "first-window", 60, 150, 600, 300),
            Resource(1_180, 500, "visible", 100, 180, 700, 400),
            Resource(1_780, 600, "responsive", 110, 190, 750, 450),
            Resource(2_000, 220, null, 120, 200, 800, 500),
        ];

        var summary = PerformanceStartupSummaryBuilder.Create(
            StartupLaunchDisposition.Launched,
            "target-exited",
            events,
            resources);

        Assert.AreEqual("complete", summary.Status);
        Assert.AreEqual("responsive-observed", summary.Outcome);
        Assert.AreEqual("responsive", summary.LastBoundary);
        Assert.AreEqual(1_760, summary.LastBoundaryMs);
        Assert.AreEqual(1_780, summary.ObservationEndMs);
        CollectionAssert.AreEqual(
            ExpectedStageNames,
            summary.Stages.Select(stage => stage.Name).ToArray());
        Assert.AreEqual(1_090, summary.Stages[2].DurationMs);
        Assert.AreEqual(250, summary.Stages[2].EndBoundaryResolutionMs);
        Assert.AreEqual(5, summary.Evidence.TimelineEventCount);
        Assert.AreEqual(4, summary.Evidence.ResourceSampleCount);
        Assert.AreEqual(0, summary.Evidence.PartialResourceSampleCount);
        Assert.AreEqual(
            533.333,
            Math.Round(summary.Evidence.AverageResourceIntervalMs!.Value, 3));
        Assert.AreEqual(600, summary.Evidence.MaximumResourceIntervalMs);
        var processToWindow = summary.Stages[1];
        Assert.AreEqual("complete", processToWindow.Resources.Status);
        Assert.AreEqual(50, processToWindow.Resources.CpuTimeMs);
        Assert.AreEqual(50, processToWindow.Resources.PrivateBytesChange);
        Assert.AreEqual(500UL, processToWindow.Resources.ReadBytes);
        Assert.AreEqual(100UL, processToWindow.Resources.WriteBytes);
        Assert.AreEqual(60, processToWindow.Resources.EndSampleDelayMs);
    }

    [TestMethod]
    public void Create_AttachedLateDoesNotDescribeObservedResponsiveWindowAsCompleteStartup()
    {
        PerformanceTimelineEntry[] events =
        [
            Event(StartupEventType.ActivationRequested, 0, 0),
            Event(StartupEventType.ProcessObserved, 100, 100),
            Event(StartupEventType.WindowObserved, 100, 100),
            Event(StartupEventType.WindowVisible, 100, 100),
            Event(StartupEventType.WindowResponsive, 100, 100),
        ];

        var summary = PerformanceStartupSummaryBuilder.Create(
            StartupLaunchDisposition.AttachedLate,
            "duration",
            events,
            []);

        Assert.AreEqual("attached-late", summary.Status);
        Assert.AreEqual("attached-late", summary.Outcome);
        Assert.AreEqual("AttachedLate", summary.Disposition);
    }

    [TestMethod]
    public void Create_PreservesNonzeroExitBeforeVisibleWithoutCallingItACrash()
    {
        PerformanceTimelineEntry[] events =
        [
            Event(StartupEventType.ActivationRequested, 0, 0),
            Event(StartupEventType.ProcessObserved, 180, 100),
            Event(StartupEventType.WindowObserved, 620, 250),
            new()
            {
                Type = nameof(StartupEventType.ProcessExited),
                ElapsedMs = 910,
                BoundaryResolutionMs = 250,
                ProcessId = 42,
                ProcessStartTimeUtcTicks = 1234,
                ExitCode = unchecked((int)0xC0000005),
            },
        ];

        var summary = PerformanceStartupSummaryBuilder.Create(
            StartupLaunchDisposition.Pending,
            "target-exited",
            events,
            []);

        Assert.AreEqual("partial", summary.Status);
        Assert.AreEqual("exited-before-responsive", summary.Outcome);
        Assert.AreEqual("first-window", summary.LastBoundary);
        Assert.AreEqual(620, summary.LastBoundaryMs);
        Assert.AreEqual(910, summary.ObservationEndMs);
        Assert.HasCount(1, summary.ProcessExits);
        var processExit = summary.ProcessExits[0];
        Assert.AreEqual(42, processExit.ProcessId);
        Assert.AreEqual(1234, processExit.ProcessStartTimeUtcTicks);
        Assert.AreEqual(unchecked((int)0xC0000005), processExit.ExitCode);
        Assert.AreEqual("0xC0000005", processExit.ExitCodeHex);
        Assert.AreEqual("nonzero", processExit.ExitKind);
        Assert.AreEqual("visible", processExit.BeforeBoundary);
    }

    [TestMethod]
    public void GetResourceBoundaryNames_CollapsesMultipleWindowsInOneObservation()
    {
        StartupEvent[] events =
        [
            new(
                StartupEventType.WindowObserved,
                new PerformanceTimestamp(100),
                TimeSpan.FromMilliseconds(250)),
            new(
                StartupEventType.WindowObserved,
                new PerformanceTimestamp(100),
                TimeSpan.FromMilliseconds(250)),
            new(
                StartupEventType.WindowVisible,
                new PerformanceTimestamp(100),
                TimeSpan.FromMilliseconds(250)),
        ];

        CollectionAssert.AreEqual(
            ExpectedCollapsedBoundaryNames,
            PerformanceStartupSummaryBuilder.GetResourceBoundaryNames(events).ToArray());
    }

    private static PerformanceTimelineEntry Event(
        StartupEventType type,
        double elapsedMs,
        double resolutionMs) => new()
        {
            Type = type.ToString(),
            ElapsedMs = elapsedMs,
            BoundaryResolutionMs = resolutionMs,
        };

    private static ResourceTimelineEntry Resource(
        double elapsedMs,
        double intervalMs,
        string? boundary,
        double totalProcessorTimeMs,
        long privateBytes,
        ulong readBytes,
        ulong writeBytes) => new()
        {
            Type = nameof(ResourceSample),
            ElapsedMs = elapsedMs,
            IntervalMs = intervalMs,
            StartupBoundaries = boundary is null ? null : [boundary],
            OwnedProcessCount = 1,
            PartialProcessCount = 0,
            IsTerminal = false,
            Processes =
            [
                new()
                {
                    ProcessId = 42,
                    ProcessStartTimeUtcTicks = 1234,
                    TotalProcessorTimeMs = totalProcessorTimeMs,
                    ReadBytes = readBytes,
                    WriteBytes = writeBytes,
                    IsTerminal = false,
                    IsPartial = false,
                },
            ],
            Aggregate = new()
            {
                PrivateBytes = privateBytes,
            },
        };
}

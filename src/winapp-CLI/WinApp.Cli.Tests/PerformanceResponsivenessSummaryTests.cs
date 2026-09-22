// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class PerformanceResponsivenessSummaryTests
{
    [TestMethod]
    public void Create_SummarizesRecoveredAndUnrecoveredIntervals()
    {
        PerformanceTimelineEntry[] events =
        [
            Event(StartupEventType.WindowObserved, 100, windowHandle: 7),
            Event(StartupEventType.WindowResponsive, 150, windowHandle: 7),
            Event(StartupEventType.WindowResponseFailed, 1_000, windowHandle: 7),
            Event(StartupEventType.WindowResponseRecovered, 1_450, windowHandle: 7),
            Event(StartupEventType.WindowResponseFailed, 2_000, windowHandle: 7),
        ];

        var result = PerformanceResponsivenessSummaryBuilder.Create(events, 2_800);

        Assert.AreEqual("complete", result.Status);
        Assert.AreEqual(2, result.FailureCount);
        Assert.AreEqual(1, result.RecoveryCount);
        Assert.AreEqual(1, result.UnrecoveredFailureCount);
        Assert.AreEqual(800, result.LongestObservedFailureMs);
        Assert.IsTrue(result.Intervals[0].Recovered);
        Assert.IsFalse(result.Intervals[1].Recovered);
    }

    [TestMethod]
    public void Create_DistinguishesProbeErrorsFromResponseFailures()
    {
        PerformanceTimelineEntry[] events =
        [
            Event(StartupEventType.WindowObserved, 100, windowHandle: 7),
            Event(StartupEventType.WindowResponseProbeFailed, 200, windowHandle: 7),
        ];

        var result = PerformanceResponsivenessSummaryBuilder.Create(events, 500);

        Assert.AreEqual("partial", result.Status);
        Assert.AreEqual(0, result.FailureCount);
        Assert.AreEqual(1, result.ProbeErrorCount);
        Assert.IsNull(result.LongestObservedFailureMs);
    }

    [TestMethod]
    public void Create_ExcludesTimeoutBeforeFirstResponsiveBoundary()
    {
        PerformanceTimelineEntry[] events =
        [
            Event(StartupEventType.WindowObserved, 100, windowHandle: 7),
            Event(StartupEventType.WindowResponseFailed, 120, windowHandle: 7),
            Event(StartupEventType.WindowResponsive, 300, windowHandle: 7),
            Event(StartupEventType.WindowResponseRecovered, 300, windowHandle: 7),
            Event(StartupEventType.WindowResponseFailed, 1_000, windowHandle: 7),
            Event(StartupEventType.WindowResponseRecovered, 1_450, windowHandle: 7),
        ];

        var result = PerformanceResponsivenessSummaryBuilder.Create(events, 1_500);

        Assert.AreEqual(1, result.FailureCount);
        Assert.AreEqual(1, result.RecoveryCount);
        Assert.AreEqual(450, result.LongestObservedFailureMs);
        Assert.AreEqual(1_000, result.Intervals[0].StartMs);
    }

    [TestMethod]
    public void Create_ReportsNotObservedWithoutAWindow()
    {
        var result = PerformanceResponsivenessSummaryBuilder.Create(
            [Event(StartupEventType.ProcessObserved, 100)],
            500);

        Assert.AreEqual("not-observed", result.Status);
        Assert.AreEqual(0, result.FailureCount);
    }

    private static PerformanceTimelineEntry Event(
        StartupEventType type,
        double elapsedMs,
        long? windowHandle = null) => new()
        {
            Type = type.ToString(),
            ElapsedMs = elapsedMs,
            BoundaryResolutionMs = 100,
            ProcessId = 42,
            ProcessStartTimeUtcTicks = 1234,
            WindowHandle = windowHandle,
            WindowThreadId = windowHandle is null ? null : 9,
        };
}

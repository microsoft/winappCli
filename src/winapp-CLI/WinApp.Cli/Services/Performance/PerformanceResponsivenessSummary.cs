// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Performance;

internal sealed record PerformanceResponsivenessSummary
{
    public const string CurrentSchemaVersion = "0.1";

    public required string SchemaVersion { get; init; }
    public required string Status { get; init; }
    public required int FailureCount { get; init; }
    public required int RecoveryCount { get; init; }
    public required int UnrecoveredFailureCount { get; init; }
    public required int ProbeErrorCount { get; init; }
    public double? LongestObservedFailureMs { get; init; }
    public required IReadOnlyList<PerformanceResponseFailureInterval> Intervals { get; init; }
}

internal sealed record PerformanceResponseFailureInterval
{
    public int? ProcessId { get; init; }
    public long? ProcessStartTimeUtcTicks { get; init; }
    public required long WindowHandle { get; init; }
    public int? WindowThreadId { get; init; }
    public required double StartMs { get; init; }
    public required double EndMs { get; init; }
    public required double ObservedDurationMs { get; init; }
    public required double StartBoundaryResolutionMs { get; init; }
    public required double EndBoundaryResolutionMs { get; init; }
    public required bool Recovered { get; init; }
}

internal static class PerformanceResponsivenessSummaryBuilder
{
    public static PerformanceResponsivenessSummary Create(
        IReadOnlyList<PerformanceTimelineEntry> events,
        double observationEndMs)
    {
        var active = new Dictionary<ResponseTarget, PerformanceTimelineEntry>();
        var intervals = new List<PerformanceResponseFailureInterval>();
        var responsiveTargets = new HashSet<ResponseTarget>();

        foreach (var entry in events.OrderBy(entry => entry.ElapsedMs))
        {
            if (entry.WindowHandle is not { } windowHandle)
            {
                continue;
            }

            var target = new ResponseTarget(
                entry.ProcessId,
                entry.ProcessStartTimeUtcTicks,
                windowHandle);
            if (entry.Type == nameof(StartupEventType.WindowResponsive))
            {
                responsiveTargets.Add(target);
            }
            else if (entry.Type == nameof(StartupEventType.WindowResponseFailed)
                && responsiveTargets.Contains(target))
            {
                active.TryAdd(target, entry);
            }
            else if (entry.Type == nameof(StartupEventType.WindowResponseRecovered)
                && active.Remove(target, out var failure))
            {
                intervals.Add(CreateInterval(failure, entry.ElapsedMs, entry.BoundaryResolutionMs, recovered: true));
            }
        }

        foreach (var failure in active.Values)
        {
            intervals.Add(CreateInterval(
                failure,
                Math.Max(observationEndMs, failure.ElapsedMs),
                0,
                recovered: false));
        }

        intervals.Sort((left, right) => left.StartMs.CompareTo(right.StartMs));
        var probeErrorCount = events.Count(entry =>
            entry.Type == nameof(StartupEventType.WindowResponseProbeFailed));
        var windowObserved = events.Any(entry =>
            entry.Type == nameof(StartupEventType.WindowObserved));

        return new()
        {
            SchemaVersion = PerformanceResponsivenessSummary.CurrentSchemaVersion,
            Status = !windowObserved
                ? "not-observed"
                : probeErrorCount > 0 ? "partial" : "complete",
            FailureCount = intervals.Count,
            RecoveryCount = intervals.Count(interval => interval.Recovered),
            UnrecoveredFailureCount = intervals.Count(interval => !interval.Recovered),
            ProbeErrorCount = probeErrorCount,
            LongestObservedFailureMs = intervals.Count == 0
                ? null
                : intervals.Max(interval => interval.ObservedDurationMs),
            Intervals = intervals,
        };
    }

    private static PerformanceResponseFailureInterval CreateInterval(
        PerformanceTimelineEntry failure,
        double endMs,
        double endBoundaryResolutionMs,
        bool recovered) => new()
        {
            ProcessId = failure.ProcessId,
            ProcessStartTimeUtcTicks = failure.ProcessStartTimeUtcTicks,
            WindowHandle = failure.WindowHandle!.Value,
            WindowThreadId = failure.WindowThreadId,
            StartMs = failure.ElapsedMs,
            EndMs = endMs,
            ObservedDurationMs = endMs - failure.ElapsedMs,
            StartBoundaryResolutionMs = failure.BoundaryResolutionMs,
            EndBoundaryResolutionMs = endBoundaryResolutionMs,
            Recovered = recovered,
        };

    private readonly record struct ResponseTarget(
        int? ProcessId,
        long? ProcessStartTimeUtcTicks,
        long WindowHandle);
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Performance;

internal sealed record PerformanceStartupSummary
{
    public required string SchemaVersion { get; init; }
    public required string Status { get; init; }
    public required string Outcome { get; init; }
    public required string StopReason { get; init; }
    public required string Disposition { get; init; }
    public required string LastBoundary { get; init; }
    public required double LastBoundaryMs { get; init; }
    public required double ObservationEndMs { get; init; }
    public required IReadOnlyList<StartupProcessExit> ProcessExits { get; init; }
    public required IReadOnlyList<StartupStageSummary> Stages { get; init; }
    public required StartupEvidenceCoverage Evidence { get; init; }
}

internal sealed record StartupProcessExit
{
    public int? ProcessId { get; init; }
    public long? ProcessStartTimeUtcTicks { get; init; }
    public required double ElapsedMs { get; init; }
    public int? ExitCode { get; init; }
    public string? ExitCodeHex { get; init; }
    public required string ExitKind { get; init; }
    public string? BeforeBoundary { get; init; }
}

internal sealed record StartupStageSummary
{
    public required string Name { get; init; }
    public required string StartBoundary { get; init; }
    public required string EndBoundary { get; init; }
    public required double StartMs { get; init; }
    public required double EndMs { get; init; }
    public required double DurationMs { get; init; }
    public required double StartBoundaryResolutionMs { get; init; }
    public required double EndBoundaryResolutionMs { get; init; }
    public required double ResponseFailureDurationMs { get; init; }
    public required StartupStageResourceFacts Resources { get; init; }
}

internal sealed record StartupStageResourceFacts
{
    public required string Status { get; init; }
    public double? StartSampleMs { get; init; }
    public double? EndSampleMs { get; init; }
    public double? StartSampleDelayMs { get; init; }
    public double? EndSampleDelayMs { get; init; }
    public double? CpuTimeMs { get; init; }
    public long? PrivateBytesChange { get; init; }
    public ulong? ReadBytes { get; init; }
    public ulong? WriteBytes { get; init; }
}

internal sealed record StartupEvidenceCoverage
{
    public required int TimelineEventCount { get; init; }
    public required int ResourceSampleCount { get; init; }
    public required int PartialResourceSampleCount { get; init; }
    public double? AverageResourceIntervalMs { get; init; }
    public double? MaximumResourceIntervalMs { get; init; }
}

internal static class PerformanceStartupSummaryBuilder
{
    public static PerformanceStartupSummary Create(
        StartupLaunchDisposition disposition,
        string stopReason,
        IReadOnlyList<PerformanceTimelineEntry> events,
        IReadOnlyList<ResourceTimelineEntry> resourceSamples)
    {
        var boundaries = new[]
        {
            FindBoundary(events, StartupEventType.ActivationRequested),
            FindBoundary(events, StartupEventType.ProcessObserved),
            FindBoundary(events, StartupEventType.WindowObserved),
            FindBoundary(events, StartupEventType.WindowVisible),
            FindBoundary(events, StartupEventType.WindowResponsive),
        };
        var observedBoundaries = boundaries.OfType<StartupBoundary>().ToArray();
        var lastBoundary = observedBoundaries[^1];
        var responsive = boundaries[^1];
        var eventEndMs = responsive?.ElapsedMs
            ?? events.Max(startupEvent => startupEvent.ElapsedMs);
        var startupResourceEndMs = responsive is { } responsiveBoundary
            ? FindBoundarySample(resourceSamples, responsiveBoundary.Name)?.ElapsedMs
                ?? responsiveBoundary.ElapsedMs
            : resourceSamples.Count == 0
                ? eventEndMs
                : Math.Max(eventEndMs, resourceSamples.Max(sample => sample.ElapsedMs));
        var startupResources = resourceSamples
            .Where(sample => sample.ElapsedMs <= startupResourceEndMs)
            .ToArray();
        var processExits = events
            .Where(startupEvent =>
                startupEvent.Type == nameof(StartupEventType.ProcessExited)
                && startupEvent.ElapsedMs <= eventEndMs)
            .Select(startupEvent => CreateProcessExit(startupEvent, boundaries))
            .ToArray();
        var measuredIntervals = startupResources
            .Where(sample => !sample.IsTerminal && sample.IntervalMs > 0)
            .Select(sample => sample.IntervalMs)
            .ToArray();

        return new()
        {
            SchemaVersion = "0.3",
            Status = disposition == StartupLaunchDisposition.AttachedLate
                ? "attached-late"
                : boundaries[^1] is not null ? "complete" : "partial",
            Outcome = CreateOutcome(
                disposition,
                stopReason,
                boundaries,
                processExits),
            StopReason = stopReason,
            Disposition = disposition.ToString(),
            LastBoundary = lastBoundary.Name,
            LastBoundaryMs = lastBoundary.ElapsedMs,
            ObservationEndMs = Math.Max(eventEndMs, startupResourceEndMs),
            ProcessExits = processExits,
            Stages = CreateStages(boundaries, events, resourceSamples),
            Evidence = new()
            {
                TimelineEventCount = events.Count(startupEvent =>
                    startupEvent.ElapsedMs <= eventEndMs),
                ResourceSampleCount = startupResources.Length,
                PartialResourceSampleCount = startupResources.Count(sample =>
                    sample.PartialProcessCount > 0),
                AverageResourceIntervalMs = measuredIntervals.Length == 0
                    ? null
                    : measuredIntervals.Average(),
                MaximumResourceIntervalMs = measuredIntervals.Length == 0
                    ? null
                    : measuredIntervals.Max(),
            },
        };
    }

    private static string CreateOutcome(
        StartupLaunchDisposition disposition,
        string stopReason,
        IReadOnlyList<StartupBoundary?> boundaries,
        StartupProcessExit[] processExits)
    {
        if (disposition == StartupLaunchDisposition.AttachedLate)
        {
            return "attached-late";
        }
        if (boundaries[^1] is not null)
        {
            return "responsive-observed";
        }
        if (string.Equals(stopReason, "launch-failed", StringComparison.Ordinal))
        {
            return "activation-failed";
        }
        if (boundaries[1] is null)
        {
            return "process-not-observed";
        }
        return processExits.Length > 0
            ? "exited-before-responsive"
            : "recording-ended-before-responsive";
    }

    private static StartupProcessExit CreateProcessExit(
        PerformanceTimelineEntry entry,
        IReadOnlyList<StartupBoundary?> boundaries)
    {
        string? beforeBoundary = null;
        for (var index = 2; index < boundaries.Count; index++)
        {
            if (boundaries[index] is not { } boundary
                || boundary.ElapsedMs > entry.ElapsedMs)
            {
                beforeBoundary = index switch
                {
                    2 => "first-window",
                    3 => "visible",
                    4 => "responsive",
                    _ => throw new InvalidOperationException("Unexpected startup boundary index."),
                };
                break;
            }
        }
        return new()
        {
            ProcessId = entry.ProcessId,
            ProcessStartTimeUtcTicks = entry.ProcessStartTimeUtcTicks,
            ElapsedMs = entry.ElapsedMs,
            ExitCode = entry.ExitCode,
            ExitCodeHex = entry.ExitCode is { } exitCode
                ? $"0x{unchecked((uint)exitCode):X8}"
                : null,
            ExitKind = entry.ExitCode switch
            {
                null => "unknown",
                0 => "zero",
                _ => "nonzero",
            },
            BeforeBoundary = beforeBoundary,
        };
    }

    internal static IReadOnlyList<string> GetResourceBoundaryNames(
        IReadOnlyList<StartupEvent> events) =>
        events
            .Select(startupEvent => startupEvent.Type)
            .Where(type => type is StartupEventType.ProcessObserved
                or StartupEventType.WindowObserved
                or StartupEventType.WindowVisible
                or StartupEventType.WindowResponsive)
            .Select(BoundaryName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static List<StartupStageSummary> CreateStages(
        IReadOnlyList<StartupBoundary?> boundaries,
        IReadOnlyList<PerformanceTimelineEntry> events,
        IReadOnlyList<ResourceTimelineEntry> resourceSamples)
    {
        var stages = new List<StartupStageSummary>();
        for (var index = 1; index < boundaries.Count; index++)
        {
            if (boundaries[index - 1] is not { } start
                || boundaries[index] is not { } end)
            {
                continue;
            }

            var resources = CreateResourceFacts(start, end, resourceSamples);
            stages.Add(new()
            {
                Name = $"{start.Name}-to-{end.Name}",
                StartBoundary = start.Name,
                EndBoundary = end.Name,
                StartMs = start.ElapsedMs,
                EndMs = end.ElapsedMs,
                DurationMs = Math.Max(0, end.ElapsedMs - start.ElapsedMs),
                StartBoundaryResolutionMs = start.ResolutionMs,
                EndBoundaryResolutionMs = end.ResolutionMs,
                ResponseFailureDurationMs = PerformanceMetricExtractor.FailedProbeDuration(
                    events,
                    start.ElapsedMs,
                    end.ElapsedMs),
                Resources = resources,
            });
        }
        return stages;
    }

    private static StartupStageResourceFacts CreateResourceFacts(
        StartupBoundary start,
        StartupBoundary end,
        IReadOnlyList<ResourceTimelineEntry> resourceSamples)
    {
        var startSample = FindBoundarySample(resourceSamples, start.Name);
        var endSample = FindBoundarySample(resourceSamples, end.Name);
        if (startSample is null
            || endSample is null
            || endSample.ElapsedMs <= startSample.ElapsedMs)
        {
            return new()
            {
                Status = "not-observed",
            };
        }

        ResourceTimelineEntry[] samples = [startSample, endSample];
        var cpuTime = PerformanceMetricExtractor.CpuTime(samples);
        var privateBytesChange = PerformanceMetricExtractor.BoundaryDifference(
            samples.Select(sample => sample.Aggregate.PrivateBytes));
        var readBytes = PerformanceMetricExtractor.CounterDelta(
            samples,
            sample => sample.ReadBytes);
        var writeBytes = PerformanceMetricExtractor.CounterDelta(
            samples,
            sample => sample.WriteBytes);
        return new()
        {
            Status = samples.Any(sample => sample.PartialProcessCount > 0)
                || cpuTime is null
                || privateBytesChange is null
                || readBytes is null
                || writeBytes is null
                    ? "partial"
                    : "complete",
            StartSampleMs = startSample.ElapsedMs,
            EndSampleMs = endSample.ElapsedMs,
            StartSampleDelayMs = startSample.ElapsedMs - start.ElapsedMs,
            EndSampleDelayMs = endSample.ElapsedMs - end.ElapsedMs,
            CpuTimeMs = cpuTime,
            PrivateBytesChange = privateBytesChange,
            ReadBytes = readBytes,
            WriteBytes = writeBytes,
        };
    }

    private static ResourceTimelineEntry? FindBoundarySample(
        IReadOnlyList<ResourceTimelineEntry> resourceSamples,
        string boundary) =>
        resourceSamples.FirstOrDefault(sample =>
            sample.StartupBoundaries?.Contains(boundary, StringComparer.Ordinal) == true);

    private static StartupBoundary? FindBoundary(
        IReadOnlyList<PerformanceTimelineEntry> events,
        StartupEventType type)
    {
        var entry = events.FirstOrDefault(entry =>
            string.Equals(entry.Type, type.ToString(), StringComparison.Ordinal));
        return entry is null
            ? null
            : new(
                BoundaryName(type),
                entry.ElapsedMs,
                entry.BoundaryResolutionMs);
    }

    private static string BoundaryName(StartupEventType type) => type switch
    {
        StartupEventType.ActivationRequested => "activation",
        StartupEventType.ProcessObserved => "process",
        StartupEventType.WindowObserved => "first-window",
        StartupEventType.WindowVisible => "visible",
        StartupEventType.WindowResponsive => "responsive",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private sealed record StartupBoundary(
        string Name,
        double ElapsedMs,
        double ResolutionMs);
}

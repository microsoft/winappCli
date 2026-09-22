// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace WinApp.Cli.Services.Performance;

internal static class PerformanceMetricExtractor
{
    public static IReadOnlyList<PerformanceMetricValue> Extract(
        string bundleDirectory,
        double measureStartMs,
        double measureEndMs,
        IReadOnlyList<PerformanceMetricDefinition> definitions)
    {
        if (measureStartMs < 0 || measureEndMs < measureStartMs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(measureStartMs),
                "Measurement boundaries must be ordered and non-negative.");
        }
        var manifest = JsonSerializer.Deserialize(
            File.ReadAllText(Path.Join(bundleDirectory, "manifest.json")),
            PerformanceJsonContext.Default.PerformanceBundleManifest)
            ?? throw new InvalidDataException("Performance bundle manifest is empty.");
        var (events, resources) = ReadTimeline(Path.Join(bundleDirectory, "timeline.ndjson"));
        var selected = resources
            .Where(sample =>
                sample.ElapsedMs >= measureStartMs && sample.ElapsedMs <= measureEndMs)
            .OrderBy(sample => sample.ElapsedMs)
            .ToArray();
        var cpuTime = CpuTime(selected);
        var duration = measureEndMs - measureStartMs;
        var values = new Dictionary<string, double?>(StringComparer.Ordinal)
        {
            ["startup.firstProcessMs"] = manifest.Startup.FirstProcessMs,
            ["startup.firstWindowMs"] = manifest.Startup.FirstWindowMs,
            ["startup.firstVisibleMs"] = manifest.Startup.FirstVisibleWindowMs,
            ["startup.firstResponsiveMs"] = manifest.Startup.FirstResponsiveWindowMs,
            ["measure.durationMs"] = duration,
            ["measure.cpuTimeMs"] = cpuTime,
            ["measure.averageCpuCores"] = duration > 0 && cpuTime is { } cpu
                ? cpu / duration
                : null,
            ["measure.peakPrivateBytes"] = MaximumPresent(
                selected.Select(sample => sample.Aggregate.PrivateBytes)),
            ["measure.privateBytesChange"] = BoundaryDifference(
                selected.Select(sample => sample.Aggregate.PrivateBytes)),
            ["measure.readBytes"] = CounterDelta(
                selected,
                sample => sample.ReadBytes),
            ["measure.writeBytes"] = CounterDelta(
                selected,
                sample => sample.WriteBytes),
            ["measure.failedProbeDurationMs"] = FailedProbeDuration(
                events,
                measureStartMs,
                measureEndMs),
        };
        return definitions.Select(definition => new PerformanceMetricValue
        {
            Name = definition.Name,
            Value = values[definition.Name],
        }).ToArray();
    }

    private static (IReadOnlyList<PerformanceTimelineEntry> Events, IReadOnlyList<ResourceTimelineEntry> Resources)
        ReadTimeline(string path)
    {
        var events = new List<PerformanceTimelineEntry>();
        var resources = new List<ResourceTimelineEntry>();
        foreach (var line in File.ReadLines(path))
        {
            using var document = JsonDocument.Parse(line);
            var type = document.RootElement.GetProperty("type").GetString();
            if (string.Equals(type, nameof(ResourceSample), StringComparison.Ordinal))
            {
                resources.Add(JsonSerializer.Deserialize(
                    line,
                    PerformanceJsonContext.Default.ResourceTimelineEntry)
                    ?? throw new InvalidDataException("Resource timeline entry is empty."));
            }
            else if (type is "ScenarioMarker" or "UiAction")
            {
                continue;
            }
            else
            {
                events.Add(JsonSerializer.Deserialize(
                    line,
                    PerformanceJsonContext.Default.PerformanceTimelineEntry)
                    ?? throw new InvalidDataException("Performance timeline entry is empty."));
            }
        }
        return (events, resources);
    }

    internal static double? CpuTime(IReadOnlyList<ResourceTimelineEntry> samples)
    {
        if (samples.Count == 0
            || samples.SelectMany(sample => sample.Processes)
                .Any(process => process.TotalProcessorTimeMs is null))
        {
            return null;
        }
        var groups = samples
            .SelectMany(sample => sample.Processes)
            .GroupBy(process => (process.ProcessId, process.ProcessStartTimeUtcTicks))
            .Select(group => group.Select(process => process.TotalProcessorTimeMs!.Value).ToArray())
            .ToArray();
        if (groups.Any(values => values.Length < 2))
        {
            return null;
        }
        return groups.Sum(values => Math.Max(0, values.Max() - values.Min()));
    }

    private static double? MaximumPresent(IEnumerable<long?> values)
    {
        var samples = values.OfType<long>().ToArray();
        return samples.Length == 0
            ? null
            : samples.Max(value => (double)value);
    }

    internal static long? BoundaryDifference(IEnumerable<long?> values)
    {
        var samples = values.ToArray();
        return samples.Length < 2 || samples[0] is null || samples[^1] is null
            ? null
            : samples[^1]!.Value - samples[0]!.Value;
    }

    internal static ulong? CounterDelta(
        IReadOnlyList<ResourceTimelineEntry> samples,
        Func<ProcessResourceSample, ulong?> select)
    {
        var groups = samples
            .SelectMany(sample => sample.Processes)
            .GroupBy(process => (process.ProcessId, process.ProcessStartTimeUtcTicks))
            .ToArray();
        if (groups.Length == 0)
        {
            return null;
        }

        ulong total = 0;
        foreach (var group in groups)
        {
            var values = group.Select(select).ToArray();
            if (values.Length < 2
                || values.Any(value => value is null)
                || values[^1] < values[0])
            {
                return null;
            }
            var delta = values[^1]!.Value - values[0]!.Value;
            if (ulong.MaxValue - total < delta)
            {
                return null;
            }
            total += delta;
        }
        return total;
    }

    internal static double FailedProbeDuration(
        IReadOnlyList<PerformanceTimelineEntry> events,
        double start,
        double end)
    {
        var active = new HashSet<long>();
        var total = 0d;
        var previous = start;
        foreach (var entry in events.OrderBy(entry => entry.ElapsedMs))
        {
            if (entry.ElapsedMs > end)
            {
                break;
            }
            if (entry.ElapsedMs >= start)
            {
                if (active.Count > 0)
                {
                    total += entry.ElapsedMs - previous;
                }
                previous = entry.ElapsedMs;
            }
            Apply(entry, active);
        }
        if (active.Count > 0)
        {
            total += end - previous;
        }
        return Math.Max(0, total);

        static void Apply(PerformanceTimelineEntry entry, HashSet<long> active)
        {
            var window = entry.WindowHandle ?? 0;
            if (entry.Type == nameof(StartupEventType.WindowResponseFailed))
            {
                active.Add(window);
            }
            else if (entry.Type is nameof(StartupEventType.WindowResponseRecovered)
                     or nameof(StartupEventType.WindowResponsive))
            {
                active.Remove(window);
            }
        }
    }
}

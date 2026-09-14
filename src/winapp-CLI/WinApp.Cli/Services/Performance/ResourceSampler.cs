// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Performance;

internal sealed record ProcessResourceCounters(
    ProcessIdentity Process,
    bool IsTerminal = false,
    long? TotalProcessorTimeTicks = null,
    long? UserProcessorTimeTicks = null,
    long? KernelProcessorTimeTicks = null,
    long? PrivateBytes = null,
    long? WorkingSetBytes = null,
    ulong? ReadOperationCount = null,
    ulong? WriteOperationCount = null,
    ulong? OtherOperationCount = null,
    ulong? ReadBytes = null,
    ulong? WriteBytes = null,
    ulong? OtherBytes = null,
    int? ThreadCount = null,
    int? HandleCount = null,
    uint? GdiObjectCount = null,
    uint? UserObjectCount = null);

internal sealed record ProcessResourceSample
{
    public required int ProcessId { get; init; }
    public required long ProcessStartTimeUtcTicks { get; init; }
    public double? TotalProcessorTimeMs { get; init; }
    public double? UserProcessorTimeMs { get; init; }
    public double? KernelProcessorTimeMs { get; init; }
    public double? CpuCoresUsed { get; init; }
    public double? CpuPercentOfMachine { get; init; }
    public long? PrivateBytes { get; init; }
    public long? WorkingSetBytes { get; init; }
    public ulong? ReadOperationCount { get; init; }
    public ulong? WriteOperationCount { get; init; }
    public ulong? OtherOperationCount { get; init; }
    public ulong? ReadBytes { get; init; }
    public ulong? WriteBytes { get; init; }
    public ulong? OtherBytes { get; init; }
    public double? ReadBytesPerSecond { get; init; }
    public double? WriteBytesPerSecond { get; init; }
    public int? ThreadCount { get; init; }
    public int? HandleCount { get; init; }
    public uint? GdiObjectCount { get; init; }
    public uint? UserObjectCount { get; init; }
    public required bool IsTerminal { get; init; }
    public required bool IsPartial { get; init; }
}

internal sealed record AggregateResourceSample
{
    public double? CpuCoresUsed { get; init; }
    public double? CpuPercentOfMachine { get; init; }
    public long? PrivateBytes { get; init; }
    public long? WorkingSetBytes { get; init; }
    public ulong? ReadBytes { get; init; }
    public ulong? WriteBytes { get; init; }
    public double? ReadBytesPerSecond { get; init; }
    public double? WriteBytesPerSecond { get; init; }
    public int? ThreadCount { get; init; }
    public int? HandleCount { get; init; }
    public uint? GdiObjectCount { get; init; }
    public uint? UserObjectCount { get; init; }
}

internal sealed record ResourceSample
{
    public required PerformanceTimestamp Timestamp { get; init; }
    public required double IntervalMs { get; init; }
    public required int OwnedProcessCount { get; init; }
    public required int PartialProcessCount { get; init; }
    public required bool IsTerminal { get; init; }
    public required IReadOnlyList<ProcessResourceSample> Processes { get; init; }
    public required AggregateResourceSample Aggregate { get; init; }
}

internal sealed class ResourceSampler(
    IPerformanceClock clock,
    TimeSpan requestedCadence,
    int logicalProcessorCount)
{
    private readonly Dictionary<ProcessIdentity, (PerformanceTimestamp Timestamp, ProcessResourceCounters Counters)> _previous = [];
    private PerformanceTimestamp? _lastSample;

    public TimeSpan RequestedCadence { get; } = requestedCadence;

    public ResourceSample? TrySample(IReadOnlyList<ProcessResourceCounters> counters)
    {
        if (counters.Count == 0)
        {
            return null;
        }

        var timestamp = clock.GetTimestamp();
        var isTerminal = counters.Any(counter => counter.IsTerminal);
        if (_lastSample is { } previousSample
            && !isTerminal
            && timestamp.ElapsedSince(previousSample, clock.Frequency) < RequestedCadence)
        {
            return null;
        }

        var interval = _lastSample is { } last
            ? timestamp.ElapsedSince(last, clock.Frequency)
            : TimeSpan.Zero;
        _lastSample = timestamp;

        var samples = counters
            .Select(current => CreateProcessSample(timestamp, current))
            .ToArray();
        foreach (var current in counters)
        {
            _previous[current.Process] = (timestamp, current);
        }

        return new()
        {
            Timestamp = timestamp,
            IntervalMs = interval.TotalMilliseconds,
            OwnedProcessCount = counters.Count,
            PartialProcessCount = samples.Count(sample => sample.IsPartial),
            IsTerminal = isTerminal,
            Processes = samples,
            Aggregate = Aggregate(samples),
        };
    }

    private ProcessResourceSample CreateProcessSample(
        PerformanceTimestamp timestamp,
        ProcessResourceCounters current)
    {
        double? cpuCores = null;
        double? readRate = null;
        double? writeRate = null;
        if (_previous.TryGetValue(current.Process, out var previous))
        {
            var elapsedSeconds = timestamp.ElapsedSince(previous.Timestamp, clock.Frequency).TotalSeconds;
            if (elapsedSeconds > 0)
            {
                cpuCores = Delta(current.TotalProcessorTimeTicks, previous.Counters.TotalProcessorTimeTicks)
                    is { } cpuTicks
                    ? Math.Max(0, TimeSpan.FromTicks(cpuTicks).TotalSeconds / elapsedSeconds)
                    : null;
                readRate = Delta(current.ReadBytes, previous.Counters.ReadBytes) is { } readBytes
                    ? readBytes / elapsedSeconds
                    : null;
                writeRate = Delta(current.WriteBytes, previous.Counters.WriteBytes) is { } writeBytes
                    ? writeBytes / elapsedSeconds
                    : null;
            }
        }

        var partial = current.TotalProcessorTimeTicks is null
            || current.UserProcessorTimeTicks is null
            || current.KernelProcessorTimeTicks is null
            || current.ReadOperationCount is null
            || current.WriteOperationCount is null
            || current.OtherOperationCount is null
            || current.ReadBytes is null
            || current.WriteBytes is null
            || current.OtherBytes is null
            || (!current.IsTerminal
                && (current.PrivateBytes is null
                    || current.WorkingSetBytes is null
                    || current.ThreadCount is null
                    || current.HandleCount is null
                    || current.GdiObjectCount is null
                    || current.UserObjectCount is null));
        return new()
        {
            ProcessId = current.Process.ProcessId,
            ProcessStartTimeUtcTicks = current.Process.StartTimeUtcTicks,
            TotalProcessorTimeMs = ToMilliseconds(current.TotalProcessorTimeTicks),
            UserProcessorTimeMs = ToMilliseconds(current.UserProcessorTimeTicks),
            KernelProcessorTimeMs = ToMilliseconds(current.KernelProcessorTimeTicks),
            CpuCoresUsed = cpuCores,
            CpuPercentOfMachine = cpuCores * 100 / logicalProcessorCount,
            PrivateBytes = current.PrivateBytes,
            WorkingSetBytes = current.WorkingSetBytes,
            ReadOperationCount = current.ReadOperationCount,
            WriteOperationCount = current.WriteOperationCount,
            OtherOperationCount = current.OtherOperationCount,
            ReadBytes = current.ReadBytes,
            WriteBytes = current.WriteBytes,
            OtherBytes = current.OtherBytes,
            ReadBytesPerSecond = readRate,
            WriteBytesPerSecond = writeRate,
            ThreadCount = current.ThreadCount,
            HandleCount = current.HandleCount,
            GdiObjectCount = current.GdiObjectCount,
            UserObjectCount = current.UserObjectCount,
            IsTerminal = current.IsTerminal,
            IsPartial = partial,
        };
    }

    private static AggregateResourceSample Aggregate(IReadOnlyList<ProcessResourceSample> samples) => new()
    {
        CpuCoresUsed = SumComplete(samples, sample => sample.CpuCoresUsed),
        CpuPercentOfMachine = SumComplete(samples, sample => sample.CpuPercentOfMachine),
        PrivateBytes = SumComplete(samples, sample => sample.PrivateBytes),
        WorkingSetBytes = SumComplete(samples, sample => sample.WorkingSetBytes),
        ReadBytes = SumComplete(samples, sample => sample.ReadBytes),
        WriteBytes = SumComplete(samples, sample => sample.WriteBytes),
        ReadBytesPerSecond = SumComplete(samples, sample => sample.ReadBytesPerSecond),
        WriteBytesPerSecond = SumComplete(samples, sample => sample.WriteBytesPerSecond),
        ThreadCount = SumComplete(samples, sample => sample.ThreadCount),
        HandleCount = SumComplete(samples, sample => sample.HandleCount),
        GdiObjectCount = SumComplete(samples, sample => sample.GdiObjectCount),
        UserObjectCount = SumComplete(samples, sample => sample.UserObjectCount),
    };

    private static double? ToMilliseconds(long? ticks) =>
        ticks is { } value ? TimeSpan.FromTicks(value).TotalMilliseconds : null;

    private static long? Delta(long? current, long? previous) =>
        current >= previous ? current - previous : null;

    private static ulong? Delta(ulong? current, ulong? previous) =>
        current >= previous ? current - previous : null;

    private static T? SumComplete<T>(
        IReadOnlyList<ProcessResourceSample> samples,
        Func<ProcessResourceSample, T?> select)
        where T : struct, System.Numerics.INumber<T>
    {
        var values = samples.Select(select).ToArray();
        return values.Any(value => value is null)
            ? null
            : values.Aggregate(T.Zero, (sum, value) => sum + value!.Value);
    }
}

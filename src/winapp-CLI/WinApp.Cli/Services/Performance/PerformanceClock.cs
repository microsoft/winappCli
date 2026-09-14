// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;

namespace WinApp.Cli.Services.Performance;

internal readonly record struct PerformanceTimestamp(long Counter)
{
    public TimeSpan ElapsedSince(PerformanceTimestamp earlier, long frequency) =>
        TimeSpan.FromSeconds((Counter - earlier.Counter) / (double)frequency);
}

internal readonly record struct PerformanceClockCalibration(
    PerformanceTimestamp Timestamp,
    DateTimeOffset Utc,
    long Frequency,
    TimeSpan Uncertainty);

internal interface IPerformanceClock
{
    long Frequency { get; }

    PerformanceTimestamp GetTimestamp();

    PerformanceClockCalibration Calibrate();
}

/// <summary>
/// Uses the same high-resolution monotonic counter exposed by QPC and records a bounded UTC
/// calibration. UTC is metadata only; event ordering and elapsed time always use the counter.
/// </summary>
internal sealed class PerformanceClock : IPerformanceClock
{
    public long Frequency => Stopwatch.Frequency;

    public PerformanceTimestamp GetTimestamp() => new(Stopwatch.GetTimestamp());

    public PerformanceClockCalibration Calibrate()
    {
        var before = GetTimestamp();
        var utc = DateTimeOffset.UtcNow;
        var after = GetTimestamp();
        var midpoint = new PerformanceTimestamp(before.Counter + ((after.Counter - before.Counter) / 2));
        var uncertainty = TimeSpan.FromSeconds(
            (after.Counter - before.Counter) / (2d * Frequency));
        return new(midpoint, utc, Frequency, uncertainty);
    }
}

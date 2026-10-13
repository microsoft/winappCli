// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json.Serialization;

namespace WinApp.Cli.Services.Performance;

internal sealed record PerfResourceValues(long PrivateBytes, long WorkingSetBytes, int ThreadCount, int HandleCount);

internal sealed record PerfResourceSample(
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long Qpc,
    double UserCpuMs, double KernelCpuMs, PerfResourceValues Values);

internal sealed class PerfProcessResources
{
    internal const int MaximumSamples = 304;
    public List<PerfResourceSample> Samples { get; set; } = [];
    public int FailedSamples { get; set; }
    public string? LastError { get; set; }

    public static PerfResourceSample Read(Process process)
    {
        process.Refresh();
        var user = process.UserProcessorTime.TotalMilliseconds;
        var kernel = process.PrivilegedProcessorTime.TotalMilliseconds;
        return new(Stopwatch.GetTimestamp(), user, kernel,
            new(process.PrivateMemorySize64, process.WorkingSet64, process.Threads.Count, process.HandleCount));
    }

    public void Validate()
    {
        if (Samples is null || Samples.Count > MaximumSamples || FailedSamples < 0 || LastError?.Length > 512)
        {
            throw new InvalidDataException("Invalid process resource counters.");
        }
        PerfResourceSample? previous = null;
        foreach (var sample in Samples)
        {
            if (sample is null || sample.Values is null || sample.Qpc < 0 ||
                !double.IsFinite(sample.UserCpuMs) || !double.IsFinite(sample.KernelCpuMs) ||
                sample.UserCpuMs < 0 || sample.KernelCpuMs < 0 ||
                sample.Values.PrivateBytes < 0 || sample.Values.WorkingSetBytes < 0 ||
                sample.Values.ThreadCount < 0 || sample.Values.HandleCount < 0 ||
                previous is not null && (sample.Qpc < previous.Qpc ||
                    sample.UserCpuMs < previous.UserCpuMs || sample.KernelCpuMs < previous.KernelCpuMs))
            {
                throw new InvalidDataException("Invalid or unordered process resource samples.");
            }
            previous = sample;
        }
    }
}

internal sealed record PerfResourceSummary(string Availability, int SampleCount, int FailedSamples, string? LastError,
    double? FirstSampleMs, double? LastSampleMs, double? UserCpuMs, double? KernelCpuMs, double? TotalCpuMs,
    PerfResourceValues? First, PerfResourceValues? Last, PerfResourceValues? MaximumObserved)
{
    public static PerfResourceSummary Create(PerfCaptureDocument capture, PerfRange range)
    {
        var resources = capture.ProcessResources;
        var samples = resources?.Samples.Where(sample =>
            TimeMs(capture, sample) >= range.FromMs && TimeMs(capture, sample) <= range.ToMs).ToList() ?? [];
        if (samples.Count == 0)
        {
            return new(resources is null ? "not-recorded" : "not-observed", 0, resources?.FailedSamples ?? 0,
                resources?.LastError, null, null, null, null, null, null, null, null);
        }
        var first = samples[0];
        var last = samples[^1];
        double? user = samples.Count > 1 ? last.UserCpuMs - first.UserCpuMs : null;
        double? kernel = samples.Count > 1 ? last.KernelCpuMs - first.KernelCpuMs : null;
        return new("observed", samples.Count, resources!.FailedSamples, resources.LastError,
            TimeMs(capture, first), TimeMs(capture, last), user, kernel, user + kernel,
            first.Values, last.Values, new(samples.Max(s => s.Values.PrivateBytes),
                samples.Max(s => s.Values.WorkingSetBytes), samples.Max(s => s.Values.ThreadCount),
                samples.Max(s => s.Values.HandleCount)));
    }

    internal static double TimeMs(PerfCaptureDocument capture, PerfResourceSample sample) =>
        (sample.Qpc - capture.ReadyQpc!.Value) * 1000.0 / capture.Frequency;
}

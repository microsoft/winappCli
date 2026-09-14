// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;

namespace WinApp.Cli.Services.Performance;

internal sealed record PerformanceTimelineEntry
{
    public required string Type { get; init; }
    public required double ElapsedMs { get; init; }
    public required double BoundaryResolutionMs { get; init; }
    public int? ProcessId { get; init; }
    public long? ProcessStartTimeUtcTicks { get; init; }
    public long? WindowHandle { get; init; }
    public int? ExitCode { get; init; }
    public bool? WasPresentBeforeActivation { get; init; }
}

internal sealed record PerformanceBundleManifest
{
    public required string SchemaVersion { get; init; }
    public required string Status { get; init; }
    public required string StopReason { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public required DateTimeOffset CompletedUtc { get; init; }
    public required long MonotonicFrequency { get; init; }
    public required long UtcCalibrationCounter { get; init; }
    public required double UtcCalibrationUncertaintyMs { get; init; }
    public required long TimelineOriginCounter { get; init; }
    public required string StartupDisposition { get; init; }
    public required int ActivationProcessId { get; init; }
    public required int EventCount { get; init; }
    public required StartupTimingManifest Startup { get; init; }
    public required ResponseProbeManifest ResponseProbe { get; init; }
    public required WprCollectorResult Wpr { get; init; }
}

internal sealed record ResponseProbeManifest
{
    public required double CadenceMs { get; init; }
    public required double TimeoutMs { get; init; }
    public required string Method { get; init; }
}

internal sealed record StartupTimingManifest
{
    public double ActivationRequestedMs { get; init; }
    public double? FirstProcessMs { get; init; }
    public double? FirstWindowMs { get; init; }
    public double? FirstVisibleWindowMs { get; init; }
    public double? FirstResponsiveWindowMs { get; init; }
}

internal sealed record PerformanceRecordResult
{
    public required string Status { get; init; }
    public required string Bundle { get; init; }
    public required string StartupDisposition { get; init; }
    public required int ActivationProcessId { get; init; }
    public required int EventCount { get; init; }
    public required StartupTimingManifest Startup { get; init; }
    public required ResponseProbeManifest ResponseProbe { get; init; }
    public required WprCollectorResult Wpr { get; init; }
}

internal sealed class PerformanceBundleWriter : IDisposable
{
    private readonly string _finalDirectory;
    private readonly string _stagingDirectory;
    private readonly StreamWriter _timelineWriter;
    private readonly PerformanceClockCalibration _calibration;
    private PerformanceTimestamp? _timelineOrigin;
    private double? _firstProcessMs;
    private double? _firstWindowMs;
    private double? _firstVisibleWindowMs;
    private double? _firstResponsiveWindowMs;
    private int _eventCount;
    private bool _published;

    public PerformanceBundleWriter(string finalDirectory, PerformanceClockCalibration calibration)
    {
        _finalDirectory = Path.GetFullPath(finalDirectory);
        _calibration = calibration;
        if (Path.Exists(_finalDirectory))
        {
            throw new IOException($"Performance evidence bundle already exists: {_finalDirectory}");
        }

        var parent = Path.GetDirectoryName(_finalDirectory)
            ?? throw new IOException("Performance evidence bundle must have a parent directory.");
        Directory.CreateDirectory(parent);
        var leaf = Path.GetFileName(_finalDirectory);
        _stagingDirectory = Path.Join(parent, $".{leaf}.{Guid.NewGuid():N}.staging");

        try
        {
            Directory.CreateDirectory(_stagingDirectory);
            _timelineWriter = new StreamWriter(
                new FileStream(
                    Path.Join(_stagingDirectory, "timeline.ndjson"),
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                NewLine = "\n",
            };
        }
        catch
        {
            if (Directory.Exists(_stagingDirectory))
            {
                Directory.Delete(_stagingDirectory, recursive: true);
            }
            throw;
        }
    }

    public string FinalDirectory => _finalDirectory;

    public int EventCount => _eventCount;

    public (string EtlPath, string TemporaryDirectory) CreateWprPaths()
    {
        var tracesDirectory = Path.Join(_stagingDirectory, "traces");
        Directory.CreateDirectory(tracesDirectory);
        return (
            Path.Join(tracesDirectory, "system.etl"),
            Path.Join(tracesDirectory, ".wpr-temp"));
    }

    public void Write(IEnumerable<StartupEvent> events)
    {
        foreach (var startupEvent in events)
        {
            _timelineOrigin ??= startupEvent.Timestamp;
            var entry = new PerformanceTimelineEntry
            {
                Type = startupEvent.Type.ToString(),
                ElapsedMs = startupEvent.Timestamp.ElapsedSince(
                    _timelineOrigin.Value,
                    _calibration.Frequency).TotalMilliseconds,
                BoundaryResolutionMs = startupEvent.BoundaryResolution.TotalMilliseconds,
                ProcessId = startupEvent.Process?.ProcessId,
                ProcessStartTimeUtcTicks = startupEvent.Process?.StartTimeUtcTicks,
                WindowHandle = startupEvent.WindowHandle,
                ExitCode = startupEvent.ExitCode,
                WasPresentBeforeActivation = startupEvent.WasPresentBeforeActivation ? true : null,
            };
            CaptureStartupMilestone(entry);
            _timelineWriter.WriteLine(JsonSerializer.Serialize(
                entry,
                PerformanceJsonContext.Default.PerformanceTimelineEntry));
            _timelineWriter.Flush();
            _eventCount++;
        }
    }

    public PerformanceRecordResult Complete(
        string status,
        string stopReason,
        StartupLaunchDisposition disposition,
        int activationProcessId,
        ResponseProbeManifest responseProbe,
        WprCollectorResult wpr)
    {
        if (_published)
        {
            throw new InvalidOperationException("Performance evidence bundle has already been published.");
        }

        _timelineWriter.Dispose();
        var timelineOrigin = _timelineOrigin
            ?? throw new InvalidOperationException("A performance bundle cannot be published without timeline events.");
        var manifest = new PerformanceBundleManifest
        {
            SchemaVersion = "0.1",
            Status = status,
            StopReason = stopReason,
            StartedUtc = _calibration.Utc + timelineOrigin.ElapsedSince(
                _calibration.Timestamp,
                _calibration.Frequency),
            CompletedUtc = DateTimeOffset.UtcNow,
            MonotonicFrequency = _calibration.Frequency,
            UtcCalibrationCounter = _calibration.Timestamp.Counter,
            UtcCalibrationUncertaintyMs = _calibration.Uncertainty.TotalMilliseconds,
            TimelineOriginCounter = timelineOrigin.Counter,
            StartupDisposition = disposition.ToString(),
            ActivationProcessId = activationProcessId,
            EventCount = _eventCount,
            Startup = CreateStartupTiming(),
            ResponseProbe = responseProbe,
            Wpr = wpr,
        };
        File.WriteAllText(
            Path.Join(_stagingDirectory, "manifest.json"),
            JsonSerializer.Serialize(manifest, PerformanceJsonContext.Default.PerformanceBundleManifest),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (Path.Exists(_finalDirectory))
        {
            throw new IOException($"Performance evidence bundle appeared before publication: {_finalDirectory}");
        }
        Directory.Move(_stagingDirectory, _finalDirectory);
        _published = true;

        return new()
        {
            Status = status,
            Bundle = _finalDirectory,
            StartupDisposition = disposition.ToString(),
            ActivationProcessId = activationProcessId,
            EventCount = _eventCount,
            Startup = CreateStartupTiming(),
            ResponseProbe = responseProbe,
            Wpr = wpr,
        };
    }

    private void CaptureStartupMilestone(PerformanceTimelineEntry entry)
    {
        switch (entry.Type)
        {
            case nameof(StartupEventType.ProcessObserved):
                _firstProcessMs ??= entry.ElapsedMs;
                break;
            case nameof(StartupEventType.WindowObserved):
                _firstWindowMs ??= entry.ElapsedMs;
                break;
            case nameof(StartupEventType.WindowVisible):
                _firstVisibleWindowMs ??= entry.ElapsedMs;
                break;
            case nameof(StartupEventType.WindowResponsive):
                _firstResponsiveWindowMs ??= entry.ElapsedMs;
                break;
        }
    }

    private StartupTimingManifest CreateStartupTiming() => new()
    {
        ActivationRequestedMs = 0,
        FirstProcessMs = _firstProcessMs,
        FirstWindowMs = _firstWindowMs,
        FirstVisibleWindowMs = _firstVisibleWindowMs,
        FirstResponsiveWindowMs = _firstResponsiveWindowMs,
    };

    public void Dispose()
    {
        if (_published)
        {
            return;
        }

        _timelineWriter.Dispose();
        if (Directory.Exists(_stagingDirectory))
        {
            Directory.Delete(_stagingDirectory, recursive: true);
        }
    }
}

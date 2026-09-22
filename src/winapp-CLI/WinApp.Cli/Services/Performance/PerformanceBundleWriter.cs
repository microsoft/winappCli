// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

namespace WinApp.Cli.Services.Performance;

internal sealed record PerformanceTimelineEntry
{
    public required string Type { get; init; }
    public required double ElapsedMs { get; init; }
    public required double BoundaryResolutionMs { get; init; }
    public int? ProcessId { get; init; }
    public long? ProcessStartTimeUtcTicks { get; init; }
    public long? WindowHandle { get; init; }
    public int? WindowThreadId { get; init; }
    public int? ExitCode { get; init; }
    public string? ResponseProbeOutcome { get; init; }
    public int? Win32ErrorCode { get; init; }
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
    public required string StartupSummaryPath { get; init; }
    public required string ReportPath { get; init; }
    public required ResponseProbeManifest ResponseProbe { get; init; }
    public required ResourceCaptureManifest Resources { get; init; }
    public required WprCollectorResult Wpr { get; init; }
    public required ManagedDiagnosticsResult Managed { get; init; }
    public required XamlAnalysisManifest Xaml { get; init; }
    public required IReadOnlyList<PerformanceArtifact> Artifacts { get; init; }
}

internal sealed record PerformanceReport
{
    public const string CurrentSchemaVersion = "0.1";

    public required string SchemaVersion { get; init; }
    public required PerformanceReportRecording Recording { get; init; }
    public required PerformanceReportStartup Startup { get; init; }
    public required PerformanceResponsivenessSummary Responsiveness { get; init; }
    public required ResourceCaptureManifest Resources { get; init; }
    public required PerformanceReportCollectors Collectors { get; init; }
    public required XamlAnalysisManifest Xaml { get; init; }
    public required PerformanceReportEvidence Evidence { get; init; }
}

internal sealed record PerformanceReportRecording
{
    public required string Status { get; init; }
    public required string StopReason { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public required DateTimeOffset CompletedUtc { get; init; }
    public required double DurationMs { get; init; }
}

internal sealed record PerformanceReportStartup
{
    public required StartupTimingManifest Timing { get; init; }
    public required string SummaryPath { get; init; }
    public required PerformanceStartupSummary Summary { get; init; }
}

internal sealed record PerformanceReportCollectors
{
    public required WprCollectorResult Wpr { get; init; }
    public required ManagedDiagnosticsResult Managed { get; init; }
}

internal sealed record PerformanceReportEvidence
{
    public required string TimelinePath { get; init; }
    public required IReadOnlyList<PerformanceArtifact> Artifacts { get; init; }
}

internal sealed record PerformanceArtifact
{
    public required string Path { get; init; }
    public required string Kind { get; init; }
    public required string Collector { get; init; }
    public required long SizeBytes { get; init; }
    public string? RecommendedViewer { get; init; }
    public string? LossStatus { get; init; }
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

internal sealed record ResourceTimelineEntry
{
    public required string Type { get; init; }
    public required double ElapsedMs { get; init; }
    public required double IntervalMs { get; init; }
    public IReadOnlyList<string>? StartupBoundaries { get; init; }
    public required int OwnedProcessCount { get; init; }
    public required int PartialProcessCount { get; init; }
    public required bool IsTerminal { get; init; }
    public required IReadOnlyList<ProcessResourceSample> Processes { get; init; }
    public required AggregateResourceSample Aggregate { get; init; }
}

internal sealed record PerformanceScenarioMarkerEntry
{
    public required string Type { get; init; }
    public required double ElapsedMs { get; init; }
    public required string Phase { get; init; }
    public required string Boundary { get; init; }
    public int? StepOrdinal { get; init; }
    public string? Verb { get; init; }
    public string? Status { get; init; }
}

internal sealed record PerformanceUiActionEntry
{
    public required string Type { get; init; }
    public required double ElapsedMs { get; init; }
    public required string Phase { get; init; }
    public required int StepOrdinal { get; init; }
    public required string Verb { get; init; }
    public required string Boundary { get; init; }
    public required string ActionKind { get; init; }
    public string? Status { get; init; }
    public required int ProcessId { get; init; }
    public required long ProcessStartTimeUtcTicks { get; init; }
    public required long WindowHandle { get; init; }
}

internal sealed record ResourceCaptureManifest
{
    public required double RequestedCadenceMs { get; init; }
    public required int SampleCount { get; init; }
    public double? AverageIntervalMs { get; init; }
    public double? MaximumIntervalMs { get; init; }
    public required int ProcessGenerationCount { get; init; }
    public required int PartialSampleCount { get; init; }
    public required int TerminalSampleCount { get; init; }
    public required ResourceSummary Summary { get; init; }
}

internal sealed record ResourceSummary
{
    public double? AverageCpuCoresUsed { get; init; }
    public double? PeakCpuCoresUsed { get; init; }
    public double? AverageCpuPercentOfMachine { get; init; }
    public double? PeakCpuPercentOfMachine { get; init; }
    public long? InitialPrivateBytes { get; init; }
    public long? FinalPrivateBytes { get; init; }
    public long? PeakPrivateBytes { get; init; }
    public long? PrivateBytesChange { get; init; }
    public long? PeakWorkingSetBytes { get; init; }
    public ulong? ReadBytesDuringRecording { get; init; }
    public ulong? WriteBytesDuringRecording { get; init; }
    public int? PeakThreadCount { get; init; }
    public int? PeakHandleCount { get; init; }
    public uint? PeakGdiObjectCount { get; init; }
    public uint? PeakUserObjectCount { get; init; }
}

internal sealed record PerformanceRecordResult
{
    public required string Status { get; init; }
    public required string Bundle { get; init; }
    public required string StartupDisposition { get; init; }
    public required int ActivationProcessId { get; init; }
    public required int EventCount { get; init; }
    public required StartupTimingManifest Startup { get; init; }
    public required PerformanceStartupSummary StartupSummary { get; init; }
    public required string StartupSummaryPath { get; init; }
    public required string ReportPath { get; init; }
    public required PerformanceReport Report { get; init; }
    public required ResponseProbeManifest ResponseProbe { get; init; }
    public required ResourceCaptureManifest Resources { get; init; }
    public required WprCollectorResult Wpr { get; init; }
    public required ManagedDiagnosticsResult Managed { get; init; }
    public required XamlAnalysisManifest Xaml { get; init; }
    public required IReadOnlyList<PerformanceArtifact> Artifacts { get; init; }
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
    private readonly List<PerformanceTimelineEntry> _startupEvents = [];
    private readonly List<ResourceTimelineEntry> _resourceSamples = [];
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

    public DateTimeOffset? TimelineStartedUtc =>
        _timelineOrigin is { } timelineOrigin
            ? _calibration.Utc + timelineOrigin.ElapsedSince(
                _calibration.Timestamp,
                _calibration.Frequency)
            : null;

    public (string EtlPath, string TemporaryDirectory) CreateWprPaths()
    {
        var tracesDirectory = Path.Join(_stagingDirectory, "traces");
        Directory.CreateDirectory(tracesDirectory);
        return (
            Path.Join(tracesDirectory, "system.etl"),
            Path.Join(tracesDirectory, ".wpr-temp"));
    }

    public string CreateManagedPath()
    {
        var tracesDirectory = Path.Join(_stagingDirectory, "traces");
        return Path.Join(tracesDirectory, "managed.nettrace");
    }

    public IReadOnlyList<PerformanceTimelineEntry> Write(IEnumerable<StartupEvent> events)
    {
        var written = new List<PerformanceTimelineEntry>();
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
                WindowThreadId = startupEvent.WindowThreadId,
                ExitCode = startupEvent.ExitCode,
                ResponseProbeOutcome = startupEvent.ResponseProbeOutcome?.ToString(),
                Win32ErrorCode = startupEvent.Win32ErrorCode,
                WasPresentBeforeActivation = startupEvent.WasPresentBeforeActivation ? true : null,
            };
            CaptureStartupMilestone(entry);
            _timelineWriter.WriteLine(JsonSerializer.Serialize(
                entry,
                PerformanceJsonContext.Default.PerformanceTimelineEntry));
            _timelineWriter.Flush();
            _eventCount++;
            _startupEvents.Add(entry);
            written.Add(entry);
        }
        return written;
    }

    public void Write(
        ResourceSample? sample,
        IReadOnlyList<string>? startupBoundaries = null)
    {
        if (sample is null)
        {
            return;
        }

        var timelineOrigin = _timelineOrigin
            ?? throw new InvalidOperationException("Startup events must establish the timeline before resource samples.");
        var entry = new ResourceTimelineEntry
        {
            Type = nameof(ResourceSample),
            ElapsedMs = sample.Timestamp.ElapsedSince(
                timelineOrigin,
                _calibration.Frequency).TotalMilliseconds,
            IntervalMs = sample.IntervalMs,
            StartupBoundaries = startupBoundaries is { Count: > 0 }
                ? startupBoundaries
                : null,
            OwnedProcessCount = sample.OwnedProcessCount,
            PartialProcessCount = sample.PartialProcessCount,
            IsTerminal = sample.IsTerminal,
            Processes = sample.Processes,
            Aggregate = sample.Aggregate,
        };
        _resourceSamples.Add(entry);
        _timelineWriter.WriteLine(JsonSerializer.Serialize(
            entry,
            PerformanceJsonContext.Default.ResourceTimelineEntry));
        _timelineWriter.Flush();
        _eventCount++;
    }

    public double WriteScenarioMarker(
        PerformanceTimestamp timestamp,
        string phase,
        string boundary,
        int? stepOrdinal = null,
        string? verb = null,
        string? status = null)
    {
        var timelineOrigin = _timelineOrigin
            ?? throw new InvalidOperationException("Startup events must establish the timeline before scenario markers.");
        var elapsedMs = timestamp.ElapsedSince(
            timelineOrigin,
            _calibration.Frequency).TotalMilliseconds;
        var entry = new PerformanceScenarioMarkerEntry
        {
            Type = "ScenarioMarker",
            ElapsedMs = elapsedMs,
            Phase = phase,
            Boundary = boundary,
            StepOrdinal = stepOrdinal,
            Verb = verb,
            Status = status,
        };
        _timelineWriter.WriteLine(JsonSerializer.Serialize(
            entry,
            PerformanceJsonContext.Default.PerformanceScenarioMarkerEntry));
        _timelineWriter.Flush();
        _eventCount++;
        return elapsedMs;
    }

    public void WriteUiAction(
        PerformanceTimestamp timestamp,
        string phase,
        int stepOrdinal,
        string verb,
        UiActionBoundary boundary,
        PerformanceWindowTarget target)
    {
        var timelineOrigin = _timelineOrigin
            ?? throw new InvalidOperationException("Startup events must establish the timeline before UI actions.");
        var entry = new PerformanceUiActionEntry
        {
            Type = "UiAction",
            ElapsedMs = timestamp.ElapsedSince(
                timelineOrigin,
                _calibration.Frequency).TotalMilliseconds,
            Phase = phase,
            StepOrdinal = stepOrdinal,
            Verb = verb,
            Boundary = boundary.Boundary,
            ActionKind = boundary.ActionKind,
            Status = boundary.Status,
            ProcessId = target.Process.ProcessId,
            ProcessStartTimeUtcTicks = target.Process.StartTimeUtcTicks,
            WindowHandle = target.WindowHandle,
        };
        _timelineWriter.WriteLine(JsonSerializer.Serialize(
            entry,
            PerformanceJsonContext.Default.PerformanceUiActionEntry));
        _timelineWriter.Flush();
        _eventCount++;
    }

    public PerformanceRecordResult Complete(
        string status,
        string stopReason,
        StartupLaunchDisposition disposition,
        int activationProcessId,
        ResponseProbeManifest responseProbe,
        WprCollectorResult wpr,
        ManagedDiagnosticsResult managed,
        XamlAnalysisResult? xaml = null)
    {
        if (_published)
        {
            throw new InvalidOperationException("Performance evidence bundle has already been published.");
        }

        _timelineWriter.Dispose();
        var timelineOrigin = _timelineOrigin
            ?? throw new InvalidOperationException("A performance bundle cannot be published without timeline events.");
        var resources = CreateResourceManifest();
        var artifacts = CreateArtifacts(wpr, managed);
        const string startupSummaryPath = "startup/summary.json";
        const string reportPath = "report.json";
        var startupSummary = PerformanceStartupSummaryBuilder.Create(
            disposition,
            stopReason,
            _startupEvents,
            _resourceSamples);
        var startupDirectory = Path.Join(_stagingDirectory, "startup");
        Directory.CreateDirectory(startupDirectory);
        File.WriteAllText(
            Path.Join(_stagingDirectory, startupSummaryPath),
            JsonSerializer.Serialize(
                startupSummary,
                PerformanceJsonContext.Default.PerformanceStartupSummary),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var xamlResult = xaml ?? new(
            new()
            {
                Requested = false,
                Status = "not-requested",
                Coverage = "not-requested",
                Profile = XamlPerformanceAnalyzer.ProfileName,
                MatchedIntervalCount = 0,
            },
            null);
        if (xamlResult.Summary is { } xamlSummary)
        {
            var xamlSummaryPath = xamlResult.Manifest.SummaryPath
                ?? throw new InvalidOperationException("An analyzed XAML result must declare its summary path.");
            var xamlSummaryDirectory = Path.GetDirectoryName(
                Path.Join(_stagingDirectory, xamlSummaryPath))
                ?? throw new InvalidOperationException("The XAML summary path must have a parent directory.");
            Directory.CreateDirectory(xamlSummaryDirectory);
            File.WriteAllText(
                Path.Join(_stagingDirectory, xamlSummaryPath),
                JsonSerializer.Serialize(
                    xamlSummary,
                    PerformanceJsonContext.Default.XamlPerformanceSummary),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        var startedUtc = TimelineStartedUtc
            ?? throw new InvalidOperationException("The timeline origin must be available.");
        var completedUtc = DateTimeOffset.UtcNow;
        var startupTiming = CreateStartupTiming();
        var responsiveness = PerformanceResponsivenessSummaryBuilder.Create(
            _startupEvents,
            (completedUtc - startedUtc).TotalMilliseconds);
        var report = new PerformanceReport
        {
            SchemaVersion = PerformanceReport.CurrentSchemaVersion,
            Recording = new()
            {
                Status = status,
                StopReason = stopReason,
                StartedUtc = startedUtc,
                CompletedUtc = completedUtc,
                DurationMs = (completedUtc - startedUtc).TotalMilliseconds,
            },
            Startup = new()
            {
                Timing = startupTiming,
                SummaryPath = startupSummaryPath,
                Summary = startupSummary,
            },
            Responsiveness = responsiveness,
            Resources = resources,
            Collectors = new()
            {
                Wpr = wpr,
                Managed = managed,
            },
            Xaml = xamlResult.Manifest,
            Evidence = new()
            {
                TimelinePath = "timeline.ndjson",
                Artifacts = artifacts,
            },
        };
        File.WriteAllText(
            Path.Join(_stagingDirectory, reportPath),
            JsonSerializer.Serialize(report, PerformanceJsonContext.Default.PerformanceReport),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var manifest = new PerformanceBundleManifest
        {
            SchemaVersion = PerformanceBundleSchema.CurrentVersion,
            Status = status,
            StopReason = stopReason,
            StartedUtc = startedUtc,
            CompletedUtc = completedUtc,
            MonotonicFrequency = _calibration.Frequency,
            UtcCalibrationCounter = _calibration.Timestamp.Counter,
            UtcCalibrationUncertaintyMs = _calibration.Uncertainty.TotalMilliseconds,
            TimelineOriginCounter = timelineOrigin.Counter,
            StartupDisposition = disposition.ToString(),
            ActivationProcessId = activationProcessId,
            EventCount = _eventCount,
            Startup = startupTiming,
            StartupSummaryPath = startupSummaryPath,
            ReportPath = reportPath,
            ResponseProbe = responseProbe,
            Resources = resources,
            Wpr = wpr,
            Managed = managed,
            Xaml = xamlResult.Manifest,
            Artifacts = artifacts,
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
            Startup = startupTiming,
            StartupSummary = startupSummary,
            StartupSummaryPath = startupSummaryPath,
            ReportPath = reportPath,
            Report = report,
            ResponseProbe = responseProbe,
            Resources = resources,
            Wpr = wpr,
            Managed = managed,
            Xaml = xamlResult.Manifest,
            Artifacts = artifacts,
        };
    }

    private static List<PerformanceArtifact> CreateArtifacts(
        WprCollectorResult wpr,
        ManagedDiagnosticsResult managed)
    {
        var artifacts = new List<PerformanceArtifact>();
        Add(wpr.Artifact, "etl", "wpr", wpr.FileSize, wpr.RecommendedViewer, wpr.LossStatus);
        Add(
            managed.Artifact,
            "nettrace",
            managed.Collector,
            managed.FileSize,
            managed.RecommendedViewer,
            managed.LossStatus);
        return artifacts;

        void Add(
            string? path,
            string kind,
            string collector,
            long? size,
            string? recommendedViewer,
            string? lossStatus)
        {
            if (path is null || size is null)
            {
                return;
            }
            artifacts.Add(new()
            {
                Path = path,
                Kind = kind,
                Collector = collector,
                SizeBytes = size.Value,
                RecommendedViewer = recommendedViewer,
                LossStatus = lossStatus,
            });
        }
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

    private ResourceCaptureManifest CreateResourceManifest()
    {
        var measuredIntervals = _resourceSamples
            .Where(sample => !sample.IsTerminal)
            .Select(sample => sample.IntervalMs)
            .Where(interval => interval > 0)
            .ToArray();
        var processGenerations = _resourceSamples
            .SelectMany(sample => sample.Processes)
            .Select(sample => new ProcessIdentity(
                sample.ProcessId,
                sample.ProcessStartTimeUtcTicks))
            .Distinct()
            .ToArray();
        return new()
        {
            RequestedCadenceMs = 500,
            SampleCount = _resourceSamples.Count,
            AverageIntervalMs = measuredIntervals.Length == 0 ? null : measuredIntervals.Average(),
            MaximumIntervalMs = measuredIntervals.Length == 0 ? null : measuredIntervals.Max(),
            ProcessGenerationCount = processGenerations.Length,
            PartialSampleCount = _resourceSamples.Count(sample => sample.PartialProcessCount > 0),
            TerminalSampleCount = _resourceSamples.Count(sample => sample.IsTerminal),
            Summary = CreateResourceSummary(processGenerations),
        };
    }

    private ResourceSummary CreateResourceSummary(IReadOnlyList<ProcessIdentity> processGenerations)
    {
        var aggregates = _resourceSamples.Select(sample => sample.Aggregate).ToArray();
        var periodicAggregates = _resourceSamples
            .Where(sample => !sample.IsTerminal)
            .Select(sample => sample.Aggregate)
            .ToArray();
        var firstPrivate = periodicAggregates.FirstOrDefault()?.PrivateBytes;
        var lastPrivate = periodicAggregates.LastOrDefault()?.PrivateBytes;
        return new()
        {
            AverageCpuCoresUsed = WeightedAveragePresent(
                _resourceSamples,
                sample => sample.Aggregate.CpuCoresUsed),
            PeakCpuCoresUsed = MaxPresent(aggregates.Select(sample => sample.CpuCoresUsed)),
            AverageCpuPercentOfMachine = WeightedAveragePresent(
                _resourceSamples,
                sample => sample.Aggregate.CpuPercentOfMachine),
            PeakCpuPercentOfMachine = MaxPresent(aggregates.Select(sample => sample.CpuPercentOfMachine)),
            InitialPrivateBytes = firstPrivate,
            FinalPrivateBytes = lastPrivate,
            PeakPrivateBytes = MaxPresent(periodicAggregates.Select(sample => sample.PrivateBytes)),
            PrivateBytesChange = firstPrivate is { } first && lastPrivate is { } last ? last - first : null,
            PeakWorkingSetBytes = MaxPresent(periodicAggregates.Select(sample => sample.WorkingSetBytes)),
            ReadBytesDuringRecording = SumCounterDeltas(processGenerations, sample => sample.ReadBytes),
            WriteBytesDuringRecording = SumCounterDeltas(processGenerations, sample => sample.WriteBytes),
            PeakThreadCount = MaxPresent(periodicAggregates.Select(sample => sample.ThreadCount)),
            PeakHandleCount = MaxPresent(periodicAggregates.Select(sample => sample.HandleCount)),
            PeakGdiObjectCount = MaxPresent(periodicAggregates.Select(sample => sample.GdiObjectCount)),
            PeakUserObjectCount = MaxPresent(periodicAggregates.Select(sample => sample.UserObjectCount)),
        };
    }

    private static double? WeightedAveragePresent(
        IEnumerable<ResourceTimelineEntry> samples,
        Func<ResourceTimelineEntry, double?> select)
    {
        double weightedTotal = 0;
        double totalInterval = 0;
        foreach (var sample in samples)
        {
            var value = select(sample);
            if (value is null || sample.IntervalMs <= 0)
            {
                continue;
            }

            weightedTotal += value.Value * sample.IntervalMs;
            totalInterval += sample.IntervalMs;
        }
        return totalInterval > 0 ? weightedTotal / totalInterval : null;
    }

    private ulong? SumCounterDeltas(
        IReadOnlyList<ProcessIdentity> processGenerations,
        Func<ProcessResourceSample, ulong?> select)
    {
        ulong total = 0;
        foreach (var generation in processGenerations)
        {
            var values = _resourceSamples
                .SelectMany(sample => sample.Processes)
                .Where(sample => sample.ProcessId == generation.ProcessId
                    && sample.ProcessStartTimeUtcTicks == generation.StartTimeUtcTicks)
                .Select(select)
                .ToArray();
            if (values.Length < 2 || values.Any(value => value is null))
            {
                return null;
            }

            var first = values[0]!.Value;
            var last = values[^1]!.Value;
            if (last < first)
            {
                return null;
            }
            var delta = last - first;
            if (ulong.MaxValue - total < delta)
            {
                return null;
            }
            total += delta;
        }
        return processGenerations.Count == 0 ? null : total;
    }

    private static double? AveragePresent(IEnumerable<double?> values)
    {
        var present = values.Where(value => value is not null).Select(value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Average();
    }

    private static T? MaxPresent<T>(IEnumerable<T?> values) where T : struct, IComparable<T>
    {
        var present = values.Where(value => value is not null).Select(value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Max();
    }

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

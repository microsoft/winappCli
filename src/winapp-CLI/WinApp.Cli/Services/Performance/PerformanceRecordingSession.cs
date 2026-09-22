// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using WinApp.Cli.Helpers;
using WinApp.Cli.Commands;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

namespace WinApp.Cli.Services.Performance;

internal sealed record PerformanceRecordingLaunchResult(
    int ExitCode,
    bool ObservationStarted,
    IReadOnlyList<string> ParseErrors);

internal readonly record struct PerformanceWindowTarget(
    long WindowHandle,
    ProcessIdentity Process);

internal interface IPerformanceRecordingSession : IDisposable
{
    string BundlePath { get; }
    StartupLaunchDisposition Disposition { get; }
    int ActivationProcessId { get; }
    bool HasObservedProcesses { get; }
    bool LaunchProcessAdmissionFailed { get; }
    bool HasActiveProcesses { get; }
    bool HasNewlyLaunchedTarget { get; }
    bool HasNewlyLaunchedTargetExited { get; }
    int? TargetExitCode { get; }
    ProcessIdentity? TargetProcess { get; }
    PerformanceWindowTarget? ResponsiveWindow { get; }
    IReadOnlyList<PerformanceTimelineEntry> LastWrittenEvents { get; }
    IReadOnlyList<StartupEvent> LastObservedEvents { get; }

    (string EtlPath, string TemporaryDirectory) CreateWprPaths();
    string CreateManagedPath();
    void ConfigureWprCollector(IWprCollector collector);

    Task<PerformanceRecordingLaunchResult> LaunchAsync(
        IReadOnlyList<string> runArguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken);

    StartupObservationUpdate Observe();
    void CaptureResourceSnapshot(bool force = false);
    double WriteScenarioMarker(
        string phase,
        string boundary,
        int? stepOrdinal = null,
        string? verb = null,
        string? status = null);
    void WriteUiActionBoundary(
        string phase,
        int stepOrdinal,
        string verb,
        UiActionBoundary boundary,
        PerformanceWindowTarget target);

    PerformanceRecordResult Complete(
        string status,
        string stopReason,
        WprCollectorResult wpr,
        ManagedDiagnosticsResult managed,
        XamlAnalysisResult? xaml = null);
}

internal interface IPerformanceRecordingSessionFactory
{
    IPerformanceRecordingSession Create(string outputDirectory);
}

internal sealed class PerformanceRecordingSessionFactory(
    RunCommand runCommand,
    RunCommand.Handler runHandler,
    IPerformanceClock clock,
    IPackageProcessSnapshot packageProcesses,
    ITopLevelWindowProbe windowProbe,
    IWindowResponseProbe responseProbe,
    IProcessIdentityProbe processProbe,
    ISystemUiQuery systemUiQuery) : IPerformanceRecordingSessionFactory
{
    public IPerformanceRecordingSession Create(string outputDirectory) =>
        new PerformanceRecordingSession(
            runCommand,
            runHandler,
            clock,
            packageProcesses,
            windowProbe,
            responseProbe,
            processProbe,
            systemUiQuery,
            outputDirectory);
}

internal sealed class PerformanceRecordingSession : IPerformanceRecordingSession
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan ResourceCadence = TimeSpan.FromMilliseconds(500);

    private readonly RunCommand _runCommand;
    private readonly RunCommand.Handler _runHandler;
    private readonly IPerformanceClock _clock;
    private readonly IWindowResponseProbe _responseProbe;
    private readonly PerformanceClockCalibration _calibration;
    private readonly PerformanceBundleWriter _writer;
    private readonly StartupLaunchObserver _observer;
    private readonly ResourceSampler _resourceSampler;
    private readonly Dictionary<ProcessIdentity, int?> _exitedNewProcesses = [];
    private readonly HashSet<string> _sampledStartupBoundaries = new(StringComparer.Ordinal);
    private ProcessIdentity? _targetProcess;
    private bool _completed;

    public PerformanceRecordingSession(
        RunCommand runCommand,
        RunCommand.Handler runHandler,
        IPerformanceClock clock,
        IPackageProcessSnapshot packageProcesses,
        ITopLevelWindowProbe windowProbe,
        IWindowResponseProbe responseProbe,
        IProcessIdentityProbe processProbe,
        ISystemUiQuery systemUiQuery,
        string outputDirectory)
    {
        _runCommand = runCommand;
        _runHandler = runHandler;
        _clock = clock;
        _responseProbe = responseProbe;
        _calibration = clock.Calibrate();
        _writer = new(outputDirectory, _calibration);
        _observer = new(
            clock,
            packageProcesses,
            windowProbe,
            processProbe,
            systemUiQuery,
            _calibration);
        _resourceSampler = new(clock, ResourceCadence, Environment.ProcessorCount);
    }

    public string BundlePath => _writer.FinalDirectory;

    public StartupLaunchDisposition Disposition =>
        _observer.Session?.Disposition ?? StartupLaunchDisposition.Pending;

    public int ActivationProcessId => _observer.ActivationProcessId;

    public bool HasObservedProcesses => _observer.Session?.HasObservedProcesses == true;

    public bool LaunchProcessAdmissionFailed => _observer.LaunchProcessAdmissionFailed;

    public bool HasActiveProcesses => _observer.Session?.HasActiveProcesses == true;

    public bool HasNewlyLaunchedTarget => _targetProcess is not null;

    public bool HasNewlyLaunchedTargetExited =>
        _targetProcess is { } target
        && _exitedNewProcesses.ContainsKey(target);

    public int? TargetExitCode { get; private set; }

    public ProcessIdentity? TargetProcess => _targetProcess;

    public PerformanceWindowTarget? ResponsiveWindow { get; private set; }

    public IReadOnlyList<PerformanceTimelineEntry> LastWrittenEvents { get; private set; } = [];
    public IReadOnlyList<StartupEvent> LastObservedEvents { get; private set; } = [];

    public (string EtlPath, string TemporaryDirectory) CreateWprPaths() =>
        _writer.CreateWprPaths();

    public string CreateManagedPath() => _writer.CreateManagedPath();

    public void ConfigureWprCollector(IWprCollector collector) =>
        _observer.WprCollector = collector;

    public async Task<PerformanceRecordingLaunchResult> LaunchAsync(
        IReadOnlyList<string> runArguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var parseResult = _runCommand.Parse(
            [.. runArguments],
            WinAppParserConfiguration.Default);
        if (parseResult.Errors.Count > 0)
        {
            return new(
                1,
                false,
                parseResult.Errors.Select(value => value.Message).ToArray());
        }

        parseResult.InvocationConfiguration.Output = output;
        parseResult.InvocationConfiguration.Error = error;
        var exitCode = await _runHandler.InvokeForObservationAsync(
            parseResult,
            _observer,
            cancellationToken);
        if (_observer.Session is null)
        {
            return new(exitCode, false, []);
        }

        LastWrittenEvents = _writer.Write(_observer.Session.Events);
        LastObservedEvents = _observer.Session.Events;
        CaptureEvents(_observer.Session.Events);
        CaptureResourceSnapshotForEvents(_observer.Session.Events);
        return new(exitCode, true, []);
    }

    public StartupObservationUpdate Observe()
    {
        var update = _observer.Observe();
        LastWrittenEvents = _writer.Write(update.Events);
        LastObservedEvents = update.Events;
        CaptureEvents(update.Events);
        CaptureResourceSnapshotForEvents(update.Events);
        return update;
    }

    public void CaptureResourceSnapshot(bool force = false)
        => CaptureResourceSnapshot(force, startupBoundaries: null);

    private void CaptureResourceSnapshotForEvents(IReadOnlyList<StartupEvent> events)
    {
        var boundaries = PerformanceStartupSummaryBuilder.GetResourceBoundaryNames(events);
        boundaries = boundaries
            .Where(_sampledStartupBoundaries.Add)
            .ToArray();
        CaptureResourceSnapshot(
            force: boundaries.Count > 0,
            startupBoundaries: boundaries);
    }

    private void CaptureResourceSnapshot(
        bool force,
        IReadOnlyList<string>? startupBoundaries)
    {
        if (_observer.Session is null)
        {
            return;
        }
        _writer.Write(_resourceSampler.TrySample(
            _observer.Session.CaptureResourceCounters(),
            force),
            startupBoundaries);
    }

    public double WriteScenarioMarker(
        string phase,
        string boundary,
        int? stepOrdinal = null,
        string? verb = null,
        string? status = null) =>
        _writer.WriteScenarioMarker(
            _clock.GetTimestamp(),
            phase,
            boundary,
            stepOrdinal,
            verb,
            status);

    public void WriteUiActionBoundary(
        string phase,
        int stepOrdinal,
        string verb,
        UiActionBoundary boundary,
        PerformanceWindowTarget target) =>
        _writer.WriteUiAction(
            _clock.GetTimestamp(),
            phase,
            stepOrdinal,
            verb,
            boundary,
            target);

    public PerformanceRecordResult Complete(
        string status,
        string stopReason,
        WprCollectorResult wpr,
        ManagedDiagnosticsResult managed,
        XamlAnalysisResult? xaml = null)
    {
        var result = _writer.Complete(
            status,
            stopReason,
            Disposition,
            ActivationProcessId,
            new()
            {
                CadenceMs = PollInterval.TotalMilliseconds,
                TimeoutMs = _responseProbe.Timeout.TotalMilliseconds,
                Method = "SendMessageTimeout(WM_NULL)",
            },
            wpr,
            managed,
            xaml);
        _completed = true;
        return result;
    }

    private void CaptureEvents(IEnumerable<StartupEvent> events)
    {
        foreach (var startupEvent in events)
        {
            if (startupEvent.Process is not { } process
                || startupEvent.WasPresentBeforeActivation)
            {
                continue;
            }

            if (startupEvent.Type == StartupEventType.ProcessObserved)
            {
                if (process.ProcessId == ActivationProcessId)
                {
                    SetTargetProcess(process);
                }
            }
            else if (startupEvent.Type == StartupEventType.ProcessExited)
            {
                _exitedNewProcesses[process] = startupEvent.ExitCode;
                UpdateTargetExitCode();
            }
            else if (startupEvent.Type == StartupEventType.WindowResponsive
                && startupEvent.WindowHandle is { } windowHandle
                && ResponsiveWindow is null)
            {
                SetTargetProcess(process);
                ResponsiveWindow = new(windowHandle, process);
            }
        }
    }

    private void SetTargetProcess(ProcessIdentity process)
    {
        _targetProcess = process;
        TargetExitCode = null;
        UpdateTargetExitCode();
    }

    private void UpdateTargetExitCode()
    {
        if (_targetProcess is { } target
            && _exitedNewProcesses.TryGetValue(target, out var exitCode))
        {
            TargetExitCode = exitCode;
        }
    }

    public void Dispose()
    {
        _observer.Dispose();
        if (!_completed)
        {
            _writer.Dispose();
        }
    }
}

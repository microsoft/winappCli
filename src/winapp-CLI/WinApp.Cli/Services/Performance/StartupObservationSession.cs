// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Performance;

internal enum StartupEventType
{
    ActivationRequested,
    ProcessObserved,
    WindowObserved,
    WindowVisible,
    WindowResponseFailed,
    WindowResponseProbeFailed,
    WindowResponsive,
    WindowResponseRecovered,
    ProcessExited,
}

internal enum StartupLaunchDisposition
{
    Pending,
    Launched,
    AttachedLate,
}

internal readonly record struct StartupEvent(
    StartupEventType Type,
    PerformanceTimestamp Timestamp,
    TimeSpan BoundaryResolution,
    ProcessIdentity? Process = null,
    long? WindowHandle = null,
    int? ExitCode = null,
    WindowResponseProbeOutcome? ResponseProbeOutcome = null,
    int? Win32ErrorCode = null,
    bool WasPresentBeforeActivation = false,
    int? WindowThreadId = null);

internal readonly record struct StartupObservationUpdate(
    IReadOnlyList<StartupEvent> Events,
    StartupLaunchDisposition Disposition,
    IReadOnlyDictionary<int, ProcessAdmissionStatus> ProcessFailures);

internal readonly record struct PackageActivationBaseline(
    string PackageFamilyName,
    IReadOnlySet<ProcessIdentity> Processes);

/// <summary>
/// Converts one polling snapshot into ordered startup milestones while retaining process-generation
/// evidence between snapshots. The caller supplies package-associated candidate PIDs; this class
/// never broadens ownership from a window alone.
/// </summary>
internal sealed class StartupObservationSession : IDisposable
{
    private readonly IPerformanceClock _clock;
    private readonly ITopLevelWindowProbe _windowProbe;
    private readonly TargetOwnership _ownership;
    private readonly HashSet<ProcessIdentity> _preActivationProcesses;
    private readonly HashSet<ProcessIdentity> _reportedProcesses = [];
    private readonly HashSet<ProcessIdentity> _reportedExits = [];
    private readonly HashSet<OwnedWindow> _reportedWindows = [];
    private readonly HashSet<OwnedWindow> _reportedVisibleWindows = [];
    private readonly HashSet<OwnedWindow> _reportedResponsiveWindows = [];
    private readonly Dictionary<OwnedWindow, bool> _windowResponseStates = [];
    private readonly Dictionary<OwnedWindow, WindowResponseProbeResult> _windowProbeFailures = [];
    private readonly List<StartupEvent> _events;
    private PerformanceTimestamp _previousSnapshot;

    public StartupObservationSession(
        IPerformanceClock clock,
        ITopLevelWindowProbe windowProbe,
        TargetOwnership ownership,
        PerformanceTimestamp activationRequested,
        IEnumerable<ProcessIdentity> preActivationProcesses)
    {
        _clock = clock;
        _windowProbe = windowProbe;
        _ownership = ownership;
        _preActivationProcesses = preActivationProcesses.ToHashSet();
        _previousSnapshot = activationRequested;
        _events =
        [
            new(
                StartupEventType.ActivationRequested,
                activationRequested,
                TimeSpan.Zero)
        ];
    }

    public StartupLaunchDisposition Disposition { get; private set; } = StartupLaunchDisposition.Pending;

    public IReadOnlyList<StartupEvent> Events => _events;

    public bool HasObservedProcesses => _ownership.Processes.Count > 0;

    public bool HasActiveProcesses => _ownership.Processes.Any(process => !process.HasExited);

    public IReadOnlyList<ProcessResourceCounters> CaptureResourceCounters() =>
        _ownership.CaptureResourceCounters();

    public static PackageActivationBaseline CapturePackageBaseline(
        string packageFamilyName,
        IPackageProcessSnapshot packageProcesses) =>
        new(
            packageFamilyName,
            packageProcesses.Capture(packageFamilyName).ToHashSet());

    public StartupObservationUpdate Observe(
        int activationProcessId,
        IEnumerable<ProcessIdentity> packageCandidateProcesses)
    {
        var observedAt = _clock.GetTimestamp();
        var boundaryResolution = observedAt.ElapsedSince(_previousSnapshot, _clock.Frequency);
        _previousSnapshot = observedAt;
        var newEvents = new List<StartupEvent>();
        var failures = new Dictionary<int, ProcessAdmissionStatus>();
        if (activationProcessId > 0)
        {
            var status = _ownership.AdmitProcess(
                activationProcessId,
                ProcessOwnershipEvidence.Launched);
            if (status is ProcessAdmissionStatus.NotFound
                or ProcessAdmissionStatus.AccessDenied
                or ProcessAdmissionStatus.ConflictingLiveGeneration)
            {
                failures[activationProcessId] = status;
            }
        }

        foreach (var candidate in packageCandidateProcesses.Distinct())
        {
            var status = _ownership.AdmitProcess(
                candidate,
                ProcessOwnershipEvidence.PackageIdentity);
            if (status is ProcessAdmissionStatus.NotFound
                or ProcessAdmissionStatus.AccessDenied
                or ProcessAdmissionStatus.ProcessGenerationChanged
                or ProcessAdmissionStatus.ConflictingLiveGeneration)
            {
                failures[candidate.ProcessId] = status;
            }
        }

        var ownedProcesses = _ownership.Processes.ToArray();
        foreach (var process in ownedProcesses)
        {
            if (_reportedProcesses.Add(process.Identity))
            {
                AddEvent(new(
                    StartupEventType.ProcessObserved,
                    observedAt,
                    boundaryResolution,
                    Process: process.Identity,
                    WasPresentBeforeActivation: _preActivationProcesses.Contains(process.Identity)), newEvents);
            }
        }

        var ownedIds = ownedProcesses
            .Where(process => !process.HasExited)
            .Select(process => process.Identity.ProcessId)
            .ToHashSet();
        foreach (var window in _windowProbe.Snapshot(ownedIds))
        {
            if (_ownership.AdmitWindow(window.WindowHandle) is not
                (WindowAdmissionStatus.Added or WindowAdmissionStatus.AlreadyObserved))
            {
                continue;
            }

            var owner = _ownership.Windows
                .First(owned => owned.WindowHandle == window.WindowHandle);
            if (_reportedWindows.Add(owner))
            {
                AddEvent(new(
                    StartupEventType.WindowObserved,
                    observedAt,
                    boundaryResolution,
                    Process: owner.Process,
                    WindowHandle: window.WindowHandle,
                    WasPresentBeforeActivation: _preActivationProcesses.Contains(owner.Process),
                    WindowThreadId: window.ThreadId), newEvents);
            }

            if (window.IsVisible && _reportedVisibleWindows.Add(owner))
            {
                AddEvent(new(
                    StartupEventType.WindowVisible,
                    observedAt,
                    boundaryResolution,
                    Process: owner.Process,
                    WindowHandle: window.WindowHandle,
                    WasPresentBeforeActivation: _preActivationProcesses.Contains(owner.Process),
                    WindowThreadId: window.ThreadId), newEvents);

                if (!_preActivationProcesses.Contains(owner.Process))
                {
                    Disposition = StartupLaunchDisposition.Launched;
                }
                else if (Disposition == StartupLaunchDisposition.Pending)
                {
                    Disposition = StartupLaunchDisposition.AttachedLate;
                }
            }

            if (!window.IsVisible || window.Response is not { } response)
            {
                continue;
            }

            if (response.Outcome is not
                (WindowResponseProbeOutcome.Responsive or WindowResponseProbeOutcome.Timeout))
            {
                if (!_windowProbeFailures.TryGetValue(owner, out var previousFailure)
                    || previousFailure != response)
                {
                    AddEvent(new(
                        StartupEventType.WindowResponseProbeFailed,
                        observedAt,
                        boundaryResolution,
                        Process: owner.Process,
                        WindowHandle: window.WindowHandle,
                        ResponseProbeOutcome: response.Outcome,
                        Win32ErrorCode: response.Win32ErrorCode,
                        WasPresentBeforeActivation: _preActivationProcesses.Contains(owner.Process),
                        WindowThreadId: window.ThreadId), newEvents);
                    _windowProbeFailures[owner] = response;
                }
                continue;
            }

            _windowProbeFailures.Remove(owner);
            var hadPreviousState = _windowResponseStates.TryGetValue(owner, out var wasResponsive);
            var isResponsive = response.Outcome == WindowResponseProbeOutcome.Responsive;
            if (isResponsive)
            {
                var wasEverResponsive = _reportedResponsiveWindows.Contains(owner);
                if (!wasEverResponsive)
                {
                    _reportedResponsiveWindows.Add(owner);
                    AddEvent(new(
                        StartupEventType.WindowResponsive,
                        observedAt,
                        boundaryResolution,
                        Process: owner.Process,
                        WindowHandle: window.WindowHandle,
                        WasPresentBeforeActivation: _preActivationProcesses.Contains(owner.Process),
                        WindowThreadId: window.ThreadId), newEvents);
                }
                if (wasEverResponsive && hadPreviousState && !wasResponsive)
                {
                    AddEvent(new(
                        StartupEventType.WindowResponseRecovered,
                        observedAt,
                        boundaryResolution,
                        Process: owner.Process,
                        WindowHandle: window.WindowHandle,
                        WasPresentBeforeActivation: _preActivationProcesses.Contains(owner.Process),
                        WindowThreadId: window.ThreadId), newEvents);
                }
            }
            else if (hadPreviousState && wasResponsive)
            {
                AddEvent(new(
                    StartupEventType.WindowResponseFailed,
                    observedAt,
                    boundaryResolution,
                    Process: owner.Process,
                    WindowHandle: window.WindowHandle,
                    ResponseProbeOutcome: response.Outcome,
                    Win32ErrorCode: response.Win32ErrorCode,
                    WasPresentBeforeActivation: _preActivationProcesses.Contains(owner.Process),
                    WindowThreadId: window.ThreadId), newEvents);
            }
            _windowResponseStates[owner] = isResponsive;
        }

        foreach (var process in _ownership.Processes)
        {
            if (process.HasExited && _reportedExits.Add(process.Identity))
            {
                AddEvent(new(
                    StartupEventType.ProcessExited,
                    observedAt,
                    boundaryResolution,
                    Process: process.Identity,
                    ExitCode: process.ExitCode), newEvents);
            }
        }

        return new(newEvents, Disposition, failures);
    }

    private void AddEvent(StartupEvent startupEvent, List<StartupEvent> update)
    {
        _events.Add(startupEvent);
        update.Add(startupEvent);
    }

    public void Dispose() => _ownership.Dispose();
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Performance;

internal enum ProcessOwnershipEvidence
{
    Launched,
    PackageIdentity,
    ParentProcess,
}

internal enum ProcessAdmissionStatus
{
    Added,
    AlreadyObserved,
    ReplacedExitedGeneration,
    NotFound,
    AccessDenied,
    ProcessGenerationChanged,
    ConflictingLiveGeneration,
}

internal enum WindowAdmissionStatus
{
    Added,
    AlreadyObserved,
    NotFound,
    ProcessNotOwned,
    ProcessGenerationChanged,
    ProcessAccessDenied,
}

internal readonly record struct OwnedProcess(
    ProcessIdentity Identity,
    ProcessOwnershipEvidence Evidence,
    bool HasExited,
    int? ExitCode);

internal readonly record struct OwnedWindow(
    long WindowHandle,
    ProcessIdentity Process);

/// <summary>
/// Maintains the processes and windows whose ownership has been independently evidenced for one
/// recording. Windows never admit processes by themselves: their PID must resolve to the same
/// retained process generation already present in the graph.
/// </summary>
internal sealed class TargetOwnership(
    IProcessIdentityProbe processProbe,
    ISystemUiQuery systemQuery) : IDisposable
{
    private readonly Dictionary<int, (IObservedProcess Handle, ProcessOwnershipEvidence Evidence)> _processes = [];
    private readonly Dictionary<long, OwnedWindow> _windows = [];

    public IReadOnlyCollection<OwnedProcess> Processes =>
        _processes.Values
            .Select(value =>
            {
                int? exitCode = value.Handle.TryGetExitCode(out var code) ? code : null;
                return new OwnedProcess(
                    value.Handle.Identity,
                    value.Evidence,
                    value.Handle.HasExited,
                    exitCode);
            })
            .ToArray();

    public IReadOnlyCollection<OwnedWindow> Windows => _windows.Values.ToArray();

    public ProcessAdmissionStatus AdmitProcess(int processId, ProcessOwnershipEvidence evidence)
        => AdmitProcessCore(processId, expectedIdentity: null, evidence);

    public ProcessAdmissionStatus AdmitProcess(
        ProcessIdentity expectedIdentity,
        ProcessOwnershipEvidence evidence)
        => AdmitProcessCore(expectedIdentity.ProcessId, expectedIdentity, evidence);

    private ProcessAdmissionStatus AdmitProcessCore(
        int processId,
        ProcessIdentity? expectedIdentity,
        ProcessOwnershipEvidence evidence)
    {
        var observation = processProbe.Observe(processId);
        if (observation.Process is null)
        {
            return observation.Failure == ProcessObservationFailure.AccessDenied
                ? ProcessAdmissionStatus.AccessDenied
                : ProcessAdmissionStatus.NotFound;
        }

        var candidate = observation.Process;
        if (expectedIdentity is { } expected && candidate.Identity != expected)
        {
            candidate.Dispose();
            return ProcessAdmissionStatus.ProcessGenerationChanged;
        }

        if (!_processes.TryGetValue(processId, out var existing))
        {
            _processes.Add(processId, (candidate, evidence));
            return ProcessAdmissionStatus.Added;
        }

        if (existing.Handle.Identity == candidate.Identity)
        {
            candidate.Dispose();
            return ProcessAdmissionStatus.AlreadyObserved;
        }

        if (!existing.Handle.HasExited)
        {
            candidate.Dispose();
            return ProcessAdmissionStatus.ConflictingLiveGeneration;
        }

        RemoveWindowsFor(existing.Handle.Identity);
        existing.Handle.Dispose();
        _processes[processId] = (candidate, evidence);
        return ProcessAdmissionStatus.ReplacedExitedGeneration;
    }

    public WindowAdmissionStatus AdmitWindow(long windowHandle)
    {
        if (windowHandle <= 0)
        {
            return WindowAdmissionStatus.NotFound;
        }

        var processId = unchecked((int)systemQuery.GetProcessIdForWindow(windowHandle));
        if (processId <= 0)
        {
            return WindowAdmissionStatus.NotFound;
        }

        if (!_processes.TryGetValue(processId, out var owned))
        {
            return WindowAdmissionStatus.ProcessNotOwned;
        }

        var current = processProbe.Observe(processId);
        if (current.Process is null)
        {
            return current.Failure == ProcessObservationFailure.AccessDenied
                ? WindowAdmissionStatus.ProcessAccessDenied
                : WindowAdmissionStatus.ProcessGenerationChanged;
        }

        using var currentHandle = current.Process;
        if (currentHandle.Identity != owned.Handle.Identity)
        {
            return WindowAdmissionStatus.ProcessGenerationChanged;
        }

        var window = new OwnedWindow(windowHandle, owned.Handle.Identity);
        if (_windows.TryGetValue(windowHandle, out var existing) && existing == window)
        {
            return WindowAdmissionStatus.AlreadyObserved;
        }

        _windows[windowHandle] = window;
        return WindowAdmissionStatus.Added;
    }

    private void RemoveWindowsFor(ProcessIdentity process)
    {
        foreach (var windowHandle in _windows
            .Where(pair => pair.Value.Process == process)
            .Select(pair => pair.Key)
            .ToArray())
        {
            _windows.Remove(windowHandle);
        }
    }

    public void Dispose()
    {
        foreach (var process in _processes.Values)
        {
            process.Handle.Dispose();
        }

        _processes.Clear();
        _windows.Clear();
    }
}

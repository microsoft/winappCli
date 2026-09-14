// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;

namespace WinApp.Cli.Services.Performance;

/// <summary>
/// Identifies one process generation. A PID alone is insufficient because Windows can reuse it after
/// the process exits.
/// </summary>
internal readonly record struct ProcessIdentity(int ProcessId, long StartTimeUtcTicks);

internal enum ProcessObservationFailure
{
    None,
    NotFound,
    AccessDenied,
}

internal readonly record struct ProcessObservationResult(
    IObservedProcess? Process,
    ProcessObservationFailure Failure)
{
    public bool Succeeded => Process is not null;
}

/// <summary>
/// A retained process handle and the immutable identity captured from it.
/// </summary>
internal interface IObservedProcess : IDisposable
{
    ProcessIdentity Identity { get; }

    bool HasExited { get; }

    bool TryGetExitCode(out int exitCode);
}

/// <summary>
/// Opens a process and captures its reuse-safe identity.
/// </summary>
internal interface IProcessIdentityProbe
{
    ProcessObservationResult Observe(int processId);
}

internal sealed class ProcessIdentityProbe : IProcessIdentityProbe
{
    public ProcessObservationResult Observe(int processId)
    {
        if (processId <= 0)
        {
            return new(null, ProcessObservationFailure.NotFound);
        }

        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return new(null, ProcessObservationFailure.NotFound);
        }

        try
        {
            // Force Process to open and retain its native handle before capturing StartTime. The handle
            // keeps referring to this generation even after exit and prevents a later PID reuse from
            // changing the identity or exit evidence underneath the recorder.
            _ = process.SafeHandle;
            var identity = new ProcessIdentity(
                process.Id,
                process.StartTime.ToUniversalTime().Ticks);
            return new(new ObservedProcess(process, identity), ProcessObservationFailure.None);
        }
        catch (InvalidOperationException)
        {
            process.Dispose();
            return new(null, ProcessObservationFailure.NotFound);
        }
        catch (Win32Exception)
        {
            process.Dispose();
            return new(null, ProcessObservationFailure.AccessDenied);
        }
    }
}

internal sealed class ObservedProcess(
    Process process,
    ProcessIdentity identity) : IObservedProcess
{
    public ProcessIdentity Identity { get; } = identity;

    public bool HasExited
    {
        get
        {
            try
            {
                return process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public bool TryGetExitCode(out int exitCode)
    {
        if (!HasExited)
        {
            exitCode = default;
            return false;
        }

        try
        {
            if (global::Windows.Win32.PInvoke.GetExitCodeProcess(process.SafeHandle, out var nativeExitCode))
            {
                exitCode = unchecked((int)nativeExitCode);
                return true;
            }

            exitCode = default;
            return false;
        }
        catch (InvalidOperationException)
        {
            exitCode = default;
            return false;
        }
    }

    public void Dispose() => process.Dispose();
}

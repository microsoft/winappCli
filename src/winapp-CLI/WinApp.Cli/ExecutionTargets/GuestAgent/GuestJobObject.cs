// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.JobObjects;
using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.GuestAgent;

/// <summary>
/// A Windows Job Object that owns a guest process and everything it spawns.
/// </summary>
/// <remarks>
/// Killing a process ID alone leaves orphaned grandchildren behind, and in a Sandbox those keep
/// holding files the next deployment needs to replace. A Job Object with
/// <c>KILL_ON_JOB_CLOSE</c> makes the whole tree die together, including if the agent itself
/// crashes — the kernel closes the handle and the job goes with it.
/// <para>
/// The process host supplies this job in PROC_THREAD_ATTRIBUTE_JOB_LIST, so Windows assigns it
/// atomically during creation, before any child code can run. Agent-level membership is inherited
/// in addition to this per-operation job.
/// </para>
/// </remarks>
internal sealed class GuestJobObject : IDisposable
{
    private static GuestJobObject? _agentJob;

    private readonly SafeHandle _handle;
    private bool _disposed;

    private GuestJobObject(SafeHandle handle) => _handle = handle;

    /// <summary>Borrowed for atomic process creation; the caller must hold a safe-handle reference.</summary>
    internal SafeHandle Handle => _handle;

    /// <summary>
    /// Places the agent process itself in a job, so every descendant it ever creates is contained
    /// from the instant it exists.
    /// </summary>
    /// <remarks>
    /// Idempotent, and non-fatal on failure: an environment that refuses job assignment (already
    /// being in a job that disallows nesting, for instance) still gets per-operation containment,
    /// which is what the previous behaviour provided on its own.
    /// <para>
    /// Deliberately <em>not</em> <c>KILL_ON_JOB_CLOSE</c>: this job's handle is held for the life of
    /// the agent, and marking it kill-on-close would mean an unexpected handle close took the agent
    /// down with it. Its job is to guarantee membership, not termination.
    /// </para>
    /// </remarks>
    public static void EnsureAgentContainment()
    {
        if (_agentJob is not null)
        {
            return;
        }

        var handle = PInvoke.CreateJobObject((Windows.Win32.Security.SECURITY_ATTRIBUTES?)null, lpName: null);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return;
        }

        using var current = Process.GetCurrentProcess();

        if (!PInvoke.AssignProcessToJobObject(handle, current.SafeHandle))
        {
            handle.Dispose();

            System.Diagnostics.Trace.TraceWarning(
                "The guest agent could not place itself in a job object (error {0}); guest processes are contained per operation only.",
                Marshal.GetLastWin32Error());
            return;
        }

        _agentJob = new GuestJobObject(handle);
    }

    /// <summary>Creates a job that terminates its members when the handle closes.</summary>
    /// <exception cref="ExecutionTargetException">The job could not be created or configured.</exception>
    public static GuestJobObject Create()
    {
        var handle = PInvoke.CreateJobObject((Windows.Win32.Security.SECURITY_ATTRIBUTES?)null, lpName: null);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw Failure("Could not create the job object that tracks guest processes.");
        }

        var job = new GuestJobObject(handle);
        try
        {
            job.ConfigureKillOnClose();
            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    /// <summary>Terminates every process in the job.</summary>
    /// <remarks>
    /// Used only after a graceful stop has been requested and its timeout elapsed, so a
    /// well-behaved child still gets the chance to flush and exit cleanly.
    /// </remarks>
    public void TerminateAll(uint exitCode = 1)
    {
        if (_disposed)
        {
            return;
        }

        // A failure here is not actionable: the job may already be empty because everything exited.
        _ = PInvoke.TerminateJobObject(_handle, exitCode);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Closing the last handle terminates the job's members, which is what guarantees no orphans
        // survive an agent crash.
        _handle.Dispose();
    }

    private unsafe void ConfigureKillOnClose()
    {
        var information = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };

        // SetInformationJobObject takes a raw handle. Ref-counting the SafeHandle across the call
        // keeps it from being finalized underneath us.
        var addedRef = false;
        try
        {
            _handle.DangerousAddRef(ref addedRef);

            if (!PInvoke.SetInformationJobObject(
                    new Windows.Win32.Foundation.HANDLE(_handle.DangerousGetHandle()),
                    JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                    &information,
                    (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))
            {
                throw Failure("Could not configure the guest job object to clean up its processes.");
            }
        }
        finally
        {
            if (addedRef)
            {
                _handle.DangerousRelease();
            }
        }
    }

    private static ExecutionTargetException Failure(string message) =>
        ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.TransportFailed,
            message,
            userAction: "Retry the command.",
            context: new Dictionary<string, string>
            {
                ["win32Error"] = Marshal.GetLastWin32Error().ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
}

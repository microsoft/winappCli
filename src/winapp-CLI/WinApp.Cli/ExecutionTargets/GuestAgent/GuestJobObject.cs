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
/// Containment is layered because <see cref="Process.Start(ProcessStartInfo)"/> offers no way to
/// create a process already inside a job. Assigning immediately after start leaves a window,
/// however brief, in which the child could spawn a descendant that is not yet a job member and can
/// therefore outlive a per-operation kill.
/// </para>
/// <para>
/// <see cref="EnsureAgentContainment"/> closes the consequential half of that: the agent puts
/// <em>itself</em> in a job at startup, and Windows places every descendant of a job member into
/// that job at creation time, with no window at all. So no guest process can escape the agent under
/// any timing. The per-operation job then provides the finer-grained kill, and the residual race
/// affects only whether a descendant spawned in those first microseconds is caught by a
/// per-operation cancel rather than by agent teardown.
/// </para>
/// </remarks>
internal sealed class GuestJobObject : IDisposable
{
    private static GuestJobObject? _agentJob;

    private readonly SafeHandle _handle;
    private bool _disposed;

    private GuestJobObject(SafeHandle handle) => _handle = handle;

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

    /// <summary>Whether this process is a member of any job object.</summary>
    /// <remarks>
    /// A minimum-containment check, not proof of a specific assignment. Because the agent places
    /// itself in a job, its children inherit that membership at creation, so a true result does not
    /// distinguish "assigned to this operation's job" from "inherited the agent's". It is used as a
    /// backstop by the containment barrier for the case where agent-level containment failed.
    /// </remarks>
    public static bool IsCurrentProcessInJob()
    {
        using var current = Process.GetCurrentProcess();

        if (!PInvoke.IsProcessInJob(current.SafeHandle, null, out var inJob))
        {
            // Unknown means unproven, and this is the check that gates starting user code, so the
            // safe answer is no.
            return false;
        }

        return inJob;
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

    /// <summary>Places <paramref name="process"/> and its descendants under this job.</summary>
    /// <remarks>
    /// A very short-lived child can exit between <c>Process.Start</c> and this call, and Windows
    /// refuses to assign an already-terminated process to a job. That is not a failure worth
    /// surfacing: a process that has exited has no tree left to contain. Assignment failures are
    /// therefore only reported when the process is still alive and genuinely could not be tracked.
    /// </remarks>
    public void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (PInvoke.AssignProcessToJobObject(_handle, process.SafeHandle))
        {
            return;
        }

        if (process.HasExited)
        {
            return;
        }

        throw Failure("Could not attach the guest process to its job object.");
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

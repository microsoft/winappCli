// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using Windows.Win32;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

/// <summary>
/// An owned handle to a directly-launched child process or execution alias. Holding the
/// underlying <see cref="Process"/> — rather than re-attaching by PID later — preserves the exit
/// code after the process exits and prevents the OS from reusing the PID while the handle is open,
/// which is why callers must keep and dispose the handle instead of tracking a bare PID.
/// </summary>
internal interface ILaunchedProcess : IDisposable
{
    /// <summary>The launched process's ID (for diagnostics / JSON output).</summary>
    uint ProcessId { get; }
    long? StartTicksUtc => null;

    bool HasExited { get; }

    /// <summary>Package identity read from the owned handle, or null when it cannot be established.</summary>
    string? PackageFamilyName { get; }

    string? ApplicationUserModelId { get; }

    string? ExecutablePath { get; }

    /// <summary>Waits for the process to exit (returns immediately if it already has).</summary>
    Task WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The process's exit code. Valid after <see cref="WaitForExitAsync"/> completes; because the
    /// handle is owned, this stays correct even when the process exited before the wait began.
    /// </summary>
    int ExitCode { get; }

    /// <summary>Kills the process (and its child tree). No-op if it already exited.</summary>
    void Kill();

    /// <summary>Terminates only the retained process, never its descendants.</summary>
    void KillProcessOnly() => throw new NotSupportedException("Root-only termination is unavailable.");

    /// <summary>Requests a normal main-window close without terminating the process.</summary>
    bool RequestClose() => false;
}

/// <summary>
/// Real <see cref="ILaunchedProcess"/> backed by a <see cref="Process"/> returned from
/// <see cref="Process.Start(ProcessStartInfo)"/>. Disposing releases the local handle only; it does
/// not terminate the OS process, so <c>--detach</c> can dispose safely and leave the app running.
/// </summary>
internal sealed class LaunchedProcess(Process process) : ILaunchedProcess
{
    public uint ProcessId => unchecked((uint)process.Id);
    public long? StartTicksUtc => process.StartTime.ToUniversalTime().Ticks;

    public bool HasExited => process.HasExited;

    public string? PackageFamilyName => ProcessPackageIdentity.TryGetPackageFamilyName(process.Handle);

    public string? ApplicationUserModelId => ProcessPackageIdentity.TryGetApplicationUserModelId(process.Handle);

    public string? ExecutablePath
    {
        get
        {
            var buffer = new char[32768];
            uint length = (uint)buffer.Length;
            return PInvoke.QueryFullProcessImageName(process.SafeHandle, default, buffer, ref length) &&
                length > 0 && length < buffer.Length
                ? new string(buffer, 0, (int)length)
                : null;
        }
    }

    public int ExitCode => process.ExitCode;

    public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);

    public bool RequestClose() => process.CloseMainWindow();

    public void Kill()
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Process already exited.
        }
    }

    public void KillProcessOnly()
    {
        try
        {
            process.Kill(entireProcessTree: false);
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            // The retained process exited before termination.
        }
    }

    public void Dispose() => process.Dispose();
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

/// <summary>
/// Proof of which host process winapp asked to open a Sandbox client window.
/// </summary>
/// <remarks>
/// Windows Sandbox creates <c>WindowsSandboxRemoteSession.exe</c> as a direct child of the
/// <c>wsb connect</c> process that asked for it. That parentage is what makes ownership provable:
/// a client whose parent is this launcher is the one winapp asked for, and a client whose parent is
/// anything else belongs to another caller — a distinction that holds however the two connects
/// happen to be interleaved.
/// </remarks>
/// <param name="LauncherProcessId">
/// The <c>wsb connect</c> process winapp started. Valid only while the attempt that produced it is
/// undisposed, which is what keeps Windows from recycling the number underneath the comparison.
/// </param>
/// <param name="StartTicksUtc">
/// UTC ticks that launcher started. A process ID alone is a reusable number, and the parent ID
/// Windows reports for a client is only a number too — it is never revalidated once recorded, so a
/// long-lived client can go on naming a parent that exited and whose ID has since been handed to
/// winapp's own launcher. Pairing the ID with the moment it began is what tells those apart: a real
/// child cannot have started before the parent that created it.
/// </param>
internal sealed record SandboxConnectOwnership(int LauncherProcessId, long StartTicksUtc);

/// <summary>
/// A <c>wsb connect</c> winapp started, held open long enough to identify the window it created.
/// </summary>
/// <remarks>
/// This exists to own a process handle, not to control the client. Windows only guarantees a process
/// ID is not reused while some handle to it is open, so the handle is kept until the caller has
/// finished matching client windows against <see cref="Ownership"/> — releasing it earlier would
/// reintroduce exactly the mistaken-identity problem the parent ID is there to prevent.
/// <para>
/// Disposing does not stop the client: the connect process is deliberately never waited on and never
/// killed, because it is the user's interactive Sandbox window.
/// </para>
/// </remarks>
internal sealed class SandboxConnectAttempt : IDisposable
{
    private readonly Process? _launcher;

    private SandboxConnectAttempt(Process? launcher, SandboxConnectOwnership? ownership)
    {
        _launcher = launcher;
        Ownership = ownership;
    }

    /// <summary>An attempt whose launcher Windows would not identify.</summary>
    /// <remarks>
    /// Not a failure. The client may well be starting; winapp simply has no evidence to tie a window
    /// to this connect, and says so rather than claiming one.
    /// </remarks>
    public static SandboxConnectAttempt Unidentified => new(null, null);

    /// <summary>The launcher to attribute new client windows to, or null when there is none.</summary>
    public SandboxConnectOwnership? Ownership { get; }

    /// <summary>
    /// Placement started as soon as the caller received this attempt, if exact ownership was available.
    /// </summary>
    internal Task<SandboxClientWindow?>? Placement { get; set; }

    /// <summary>Wraps a launched connect process, keeping its ID reserved.</summary>
    /// <remarks>
    /// A launcher whose start time Windows will not report yields no ownership rather than an
    /// ID-only claim: without the timestamp the comparison degrades to a bare process ID, which is
    /// the weak evidence this type exists to avoid relying on. The handle is still held and disposed
    /// with the attempt, because the caller goes on using the process either way.
    /// </remarks>
    public static SandboxConnectAttempt From(Process launcher)
    {
        ArgumentNullException.ThrowIfNull(launcher);

        var startTicksUtc = TryReadStartTicks(launcher);

        return new SandboxConnectAttempt(
            launcher,
            startTicksUtc == 0 ? null : new SandboxConnectOwnership(launcher.Id, startTicksUtc));
    }

    /// <summary>Names a launcher by ID and start time, for tests that have no process to launch.</summary>
    internal static SandboxConnectAttempt ForLauncher(int launcherProcessId, long startTicksUtc) =>
        new(null, new SandboxConnectOwnership(launcherProcessId, startTicksUtc));

    /// <summary>UTC start ticks of a process, or 0 when Windows will not say.</summary>
    private static long TryReadStartTicks(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _launcher?.Dispose();
}

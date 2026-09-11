// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.ExecutionTargets.Abstractions;

/// <summary>
/// The window on <em>this</em> machine that a target's whole guest desktop is drawn into.
/// </summary>
/// <param name="WindowHandle">Host window handle. Never 0.</param>
/// <param name="ProcessId">Host process that owns the window.</param>
/// <param name="ProcessName">Host process name, for diagnostics and error messages.</param>
/// <param name="Adopted">
/// True when winapp recognised the window rather than having recorded creating it. Reported so a
/// result can say plainly which of the two it captured.
/// </param>
/// <param name="IsMinimized">Whether the host client is minimized right now.</param>
internal sealed record TargetDesktopSurface(
    nint WindowHandle,
    int ProcessId,
    string ProcessName,
    bool Adopted,
    bool IsMinimized);

/// <summary>Why a host-rendered target window must be ready.</summary>
internal enum TargetDesktopUse
{
    /// <summary>The next operation injects real input.</summary>
    RealInput,

    /// <summary>The next operation captures pixels.</summary>
    PixelCapture,
}

/// <summary>
/// A backend whose guest desktop is rendered by a client window on the host.
/// </summary>
/// <remarks>
/// Host client identity and readiness are resolved by the provider that owns that client.
/// This does not describe the guest's pixel coordinate space. Whole-desktop screenshot and record
/// execute in the guest instead, so a future backend without a host window need not implement this
/// interface to support native desktop capture.
/// <para>
/// Implemented by the backend rather than reported through
/// <see cref="ExecutionTargetCapabilities"/>, because this is a fact about the host, not something
/// the guest can observe or report.
/// </para>
/// </remarks>
internal interface IHostRenderedTarget
{
    /// <summary>
    /// Resolves the host window this target's guest desktop is currently rendered into.
    /// </summary>
    /// <remarks>
    /// May record what it resolved, so a later command in the same run agrees with this one. Callers
    /// that must not write use <see cref="InspectDesktopSurface"/> instead.
    /// </remarks>
    /// <exception cref="ExecutionTargetException">
    /// No client window is open, or several are and none of them can be proved to be the managed
    /// one. Both fail rather than guessing: capturing the wrong desktop would produce a result that
    /// looks exactly like a correct one.
    /// </exception>
    TargetDesktopSurface ResolveDesktopSurface(TargetDesktopUse use);

    /// <summary>
    /// Answers the same question as <see cref="ResolveDesktopSurface"/>, writing nothing.
    /// </summary>
    /// <remarks>
    /// The inspect-only counterpart, for commands that report state and must not become part of it.
    /// Persisting what an inspection resolved would give a read command a write it never advertised:
    /// it would bump the state revision under whatever command is preparing the target, and would
    /// make repeated snapshots of an idle machine keep changing the file they describe.
    /// </remarks>
    /// <exception cref="ExecutionTargetException">
    /// No client window is open, or several are and none can be proved to be the managed one.
    /// </exception>
    TargetDesktopSurface InspectDesktopSurface();
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;

namespace WinApp.Cli.Services.InteractiveDesktop;

/// <summary>
/// Scope around a desktop-sensitive section — foreground, focus, cursor, <c>SendInput</c>, synthetic
/// pointer input, window restore, or live-screen capture. Entering takes <c>active.lock</c>; disposing
/// releases it.
/// </summary>
/// <remarks>
/// Passed to services (screenshot, record) as an explicit parameter rather than read from ambient
/// state, matching the repo's existing injectable-seam style and letting unit tests substitute a no-op.
/// </remarks>
internal interface IDesktopSection
{
    /// <summary>
    /// Acquires <c>active.lock</c> for the duration of the returned scope.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Not reentrant.</strong> Every enter takes <c>active.lock</c>, and concurrent enters
    /// within one execution are serialized. A nested enter inside an already-held scope therefore
    /// deadlocks against itself; code that needs the desktop from within a section must reuse the
    /// enclosing scope or be refactored so the enters are sequential.
    /// </para>
    /// <para>
    /// This is deliberate. A process- or execution-wide refcount would let two unrelated concurrent
    /// tasks share one acquisition: the second would see a non-zero count and proceed <em>without</em>
    /// holding the lock, driving the desktop outside the exclusive section. Cheap reentrancy is not
    /// worth silently losing the guarantee the section exists to provide.
    /// </para>
    /// </remarks>
    Task<IAsyncDisposable> EnterAsync(CancellationToken cancellationToken);
}

/// <summary>The workflow turn a coordinated command is executing under.</summary>
internal interface IUiTurn : IDesktopSection
{
    /// <summary>The mode this command was admitted with.</summary>
    UiTurnMode Mode { get; }

    /// <summary>Milliseconds spent queued before execution began. Zero when the turn was free.</summary>
    long WaitedMs { get; }
}

/// <summary>What an explicit early release did.</summary>
internal enum UiYieldResult
{
    /// <summary>
    /// The caller has no explicit <c>WINAPP_UI_WORKFLOW_ID</c>, so it has no turn that outlives a
    /// command and nothing it could release. Reported before coordination state is read.
    /// </summary>
    NotAWorkflow,

    /// <summary>This workflow's idle turn was ended and any waiting workflow promoted.</summary>
    Released,

    /// <summary>Nothing to release: the desktop was unowned, or another workflow held it.</summary>
    NothingHeld,

    /// <summary>
    /// This workflow holds the turn but still has a live command running or queued under it, so the
    /// turn is not idle and releasing it would pull the desktop out from under that command.
    /// </summary>
    Busy,
}

/// <summary>
/// Cooperative desktop turn coordination across concurrent <c>winapp.exe</c> processes (issue #764).
/// </summary>
internal interface IInteractiveDesktopLock
{
    /// <summary>
    /// Runs <paramref name="body"/> under the workflow turn implied by <paramref name="mode"/>.
    /// </summary>
    /// <remarks>
    /// Callers must have completed every local validation before calling this: a malformed command must
    /// never open a lease, take a ticket, or join an indefinite queue (spec §10). The forward barrier
    /// wraps <paramref name="body"/>; <c>active.lock</c> is <em>not</em> held across it — the body takes
    /// it only for its desktop-sensitive section via <see cref="IDesktopSection.EnterAsync"/>.
    /// </remarks>
    /// <param name="mode">The command's coordination mode.</param>
    /// <param name="operation">Command name for diagnostics, e.g. <c>ui click</c>. Never arguments.</param>
    /// <param name="parseResult">Used only to resolve <c>--json</c> / <c>--verbose</c> / <c>--quiet</c>.</param>
    /// <param name="body">The command's work, which contacts the target app.</param>
    /// <returns>
    /// The body's exit code, or <c>130</c> when the command was cancelled while queued and never ran.
    /// </returns>
    Task<int> RunCoordinatedAsync(
        UiTurnMode mode,
        string operation,
        ParseResult parseResult,
        Func<IUiTurn, CancellationToken, Task<int>> body,
        CancellationToken cancellationToken);

    /// <summary>
    /// Ends this workflow's post-command idle grace immediately instead of waiting it out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A control operation over a reservation this workflow already holds, not a command that runs on
    /// the desktop. It therefore never takes <c>active.lock</c>, opens no participant lease and adds no
    /// entry to the state: it takes <c>state.lock</c>, ends the grace, promotes whoever was waiting and
    /// wakes them, all in one transaction.
    /// </para>
    /// <para>
    /// It only ever releases the caller's own idle turn. A turn held by another workflow, or one this
    /// workflow still has a live command under, is left exactly as it was.
    /// </para>
    /// </remarks>
    UiYieldResult ReleaseIdleTurn(CancellationToken cancellationToken);
}

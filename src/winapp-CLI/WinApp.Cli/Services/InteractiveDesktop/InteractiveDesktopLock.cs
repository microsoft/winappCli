// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services.InteractiveDesktop;

/// <summary>
/// Cooperative desktop turn coordination for concurrent <c>winapp ui</c> processes (issue #764).
/// </summary>
/// <remarks>
/// <para>
/// Lock ordering is mandatory and enforced by the shape of this class (spec §9): <c>state.lock</c> is
/// only ever held inside a <c>using</c> that contains no awaits on UI work, a participant lease is
/// always opened before its state entry is published and closed after the entry is removed, and
/// <c>active.lock</c> is never acquired while <c>state.lock</c> is held.
/// </para>
/// <para>
/// The forward barrier wraps command execution, but <c>active.lock</c> deliberately does not: a
/// command takes it only around the moment it touches the shared desktop, so output formatting, PNG
/// encoding, file publication and logging never block another workflow.
/// </para>
/// </remarks>
internal sealed class InteractiveDesktopLock : IInteractiveDesktopLock
{
    /// <summary>Exit code for a command cancelled before it ever ran (128 + SIGINT).</summary>
    internal const int CancelledExitCode = 130;

    /// <summary>
    /// How often the true global head — and a command blocked at the front of its own owner's barrier —
    /// rechecks state even without a wake-up.
    /// </summary>
    /// <remarks>
    /// The head is the one waiter whose progress nobody may be alive to signal: if the owner is killed
    /// there is no orderly completion to publish and no promoter to wake anyone, so somebody has to
    /// notice. Keeping that duty at the head means exactly one process per desktop recovers, however
    /// deep the queue.
    /// </remarks>
    internal const int HeadRecoveryMs = 500;

    /// <summary>
    /// Lost-signal backstop for waiters that are not the head.
    /// </summary>
    /// <remarks>
    /// These are woken by the promoter in every ordinary case, so this only covers a promoter that
    /// crashed between publishing and signalling. Rare enough to be worth almost nothing, which is why
    /// it is ten times the head interval rather than the old 50-75 ms poll.
    /// </remarks>
    internal const int DeepRecoveryMs = 5_000;

    private const int ActiveLockRetryMinMs = 10;
    private const int ActiveLockRetryMaxMs = 25;

    // Explicit fields rather than primary-constructor parameters: the nested CoordinatedExecution needs
    // access to them, and captured primary-constructor parameters are not visible to nested types.
    private readonly IInteractiveDesktopStateStore _store;
    private readonly IInteractiveDesktopPaths _paths;
    private readonly IParticipantRegistry _participants;
    private readonly IUiOwnerResolver _ownerResolver;
    private readonly IProcessInspector _processInspector;
    private readonly IPollDelay _pollDelay;
    private readonly IParticipantSignals _signals;
    private readonly IAnsiConsole _console;
    private readonly ILogger<InteractiveDesktopLock> _logger;
    private readonly IMonotonicClock _clock;
    private readonly InteractiveDesktopScheduler _scheduler;

    public InteractiveDesktopLock(
        IInteractiveDesktopStateStore store,
        IInteractiveDesktopPaths paths,
        IParticipantRegistry participants,
        IUiOwnerResolver ownerResolver,
        IProcessInspector processInspector,
        IMonotonicClock clock,
        IPollDelay pollDelay,
        IParticipantSignals signals,
        IAnsiConsole console,
        ILogger<InteractiveDesktopLock> logger)
    {
        _store = store;
        _paths = paths;
        _participants = participants;
        _ownerResolver = ownerResolver;
        _processInspector = processInspector;
        _pollDelay = pollDelay;
        _signals = signals;
        _console = console;
        _logger = logger;
        _clock = clock;
        _scheduler = new InteractiveDesktopScheduler(clock);
    }

    public async Task<int> RunCoordinatedAsync(
        UiTurnMode mode,
        string operation,
        ParseResult parseResult,
        Func<IUiTurn, CancellationToken, Task<int>> body,
        CancellationToken cancellationToken)
    {
        var outputMode = UiCoordinationOutputMode.FromParseResult(parseResult);
        var owner = _ownerResolver.Resolve();
        var participant = new UiParticipantIdentity(
            _processInspector.CurrentProcessId,
            _processInspector.CurrentProcessStartTicksUtc,
            operation);

        var execution = new CoordinatedExecution(this, owner, participant, mode, outputMode, parseResult);
        using (execution)
        {
            return await execution.RunAsync(body, cancellationToken).ConfigureAwait(false);
        }
    }

    private LivenessProbe CreateProbe() => new LivenessProbe(_participants);

    /// <summary>
    /// Publishes a mutated state and wakes every participant the mutation made runnable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single place state reaches disk during a transaction that can change who may run, so no
    /// transition path — promotion, absorption, barrier release, cancellation cleanup, crash pruning,
    /// an explicit yield — has to remember to wake anyone. The set is computed by comparing what the
    /// state said was runnable before the mutation with what it says afterwards, which is a property of
    /// the state rather than of the code path that produced it.
    /// </para>
    /// <para>
    /// Signalling happens strictly after the publish. A wake-up that arrived first would send its
    /// target to read state that has not changed yet, and the target would go back to sleep having
    /// consumed the only notification it was going to get.
    /// </para>
    /// </remarks>
    /// <param name="self">
    /// The caller's own participant identity, skipped because waking ourselves would only cost a
    /// spurious loop after we already know. Null when the caller is not a participant at all, which is
    /// the case for <see cref="ReleaseIdleTurn"/>.
    /// </param>
    private void PublishAndSignal(
        InteractiveDesktopState state,
        HashSet<(int Pid, long StartTicksUtc)> runnableBefore,
        (int Pid, long StartTicksUtc)? self)
    {
        _store.Publish(state);

        foreach (var target in InteractiveDesktopScheduler.RunnableParticipants(state))
        {
            if (target == self || runnableBefore.Contains(target))
            {
                continue;
            }

            _signals.Signal(target.Pid, target.StartTicksUtc);
        }
    }

    public UiYieldResult ReleaseIdleTurn(CancellationToken cancellationToken)
    {
        var owner = _ownerResolver.Resolve();
        if (!owner.HasContinuity)
        {
            // An anonymous owner releases the desktop the instant its one command ends, so it can never
            // be holding an idle turn. Answered before state.lock: reading state to prove a structural
            // impossibility would only add contention.
            return UiYieldResult.NotAWorkflow;
        }

        using var stateLock = _store.AcquireStateLock(cancellationToken);
        var read = _store.Read();
        if (read.UnknownNewerVersion)
        {
            throw new UiCoordinationException(
                UiCoordinationErrorCodes.Unavailable,
                "UI turn coordination state was written by a newer version of winapp, so this build cannot release the turn safely.",
                "Update winapp so every process on this desktop uses a compatible version, then retry.");
        }

        var state = read.State!;
        var runnableBefore = InteractiveDesktopScheduler.RunnableParticipants(state);

        // Normalization alone can already have released this turn — the grace may have lapsed while the
        // caller was getting here — and can promote a waiter. Either way the result must be published.
        // `||` is safe despite Normalize's side effects: it is the LEFT operand, so it always runs.
        // The right side is a plain flag read from the state we already have.
        var changed = _scheduler.Normalize(state, CreateProbe()) || read.RecoveredFromCorruption;

        var result = UiYieldResult.NothingHeld;
        if (InteractiveDesktopScheduler.IsCurrentOwner(state, owner))
        {
            if (state.OwnerCommands.Count > 0)
            {
                // Normalization just pruned every dead participant, so anything left here is a live
                // command of this workflow's own, running or queued behind its barrier. The turn is not
                // idle, and ending it would hand the desktop to somebody else mid-command.
                result = UiYieldResult.Busy;
            }
            else
            {
                _scheduler.ReleaseIdleTurn(state, CreateProbe());
                changed = true;
                result = UiYieldResult.Released;
            }
        }

        if (changed)
        {
            // Strictly before the return, so `Released` is only ever reported for a release that
            // actually reached disk: a failed publish throws out of here rather than telling the
            // caller the desktop was handed over when it was not. The `NothingHeld` and `Busy`
            // branches reach this too — normalization may have pruned a dead participant, expired
            // somebody's grace or promoted a waiter, and that recovery has to be published by
            // whoever performed it.
            PublishAndSignal(state, runnableBefore, self: null);
        }

        return result;
    }

    /// <summary>
    /// Waits for <c>active.lock</c>. Never steals it from a live process — a hung owner is recovered by
    /// cancelling or terminating it, not by another process forcing its way onto the desktop (spec §7.3).
    /// </summary>
    private async Task<FileStream> AcquireActiveLockAsync(string activeLockPath, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return new FileStream(
                    activeLockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException ex) when (CoordinationLockIo.IsContention(ex))
            {
                // Held by a process inside its desktop-sensitive section.
            }
            catch (IOException ex)
            {
                // Not contention: retrying would wait forever on a failure that will never clear.
                throw CoordinationLockIo.CannotOpen(activeLockPath, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new UiCoordinationException(
                    UiCoordinationErrorCodes.Unavailable,
                    $"The UI desktop lock could not be opened: {ex.Message}",
                    "Check that the current user can write to the coordination directory.");
            }

            await _pollDelay
                .DelayAsync(Random.Shared.Next(ActiveLockRetryMinMs, ActiveLockRetryMaxMs + 1), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Lease-backed participant liveness, the only basis for pruning.</summary>
    private sealed class LivenessProbe(IParticipantRegistry participants)
        : ICoordinationLivenessProbe
    {
        public bool IsParticipantLive(int processId, long startTicksUtc)
            => participants.IsParticipantLive(processId, startTicksUtc);
    }

    /// <summary>
    /// One command's participation: registration, queue waiting, desktop sections and teardown. Held as
    /// a separate object so <see cref="InteractiveDesktopLock"/> itself stays a stateless singleton.
    /// </summary>
    private sealed class CoordinatedExecution(
        InteractiveDesktopLock coordinator,
        UiOwnerIdentity owner,
        UiParticipantIdentity participant,
        UiTurnMode mode,
        UiCoordinationOutputMode outputMode,
        ParseResult parseResult) : IUiTurn, IDisposable
    {
        private readonly LivenessProbe _probe = coordinator.CreateProbe();
        private readonly Stopwatch _waitWatch = new();

        /// <summary>
        /// This process's wake-up channel, opened before any entry naming it is published so a promoter
        /// can never find it missing.
        /// </summary>
        private readonly IParticipantSignal _signal =
            coordinator._signals.Create(participant.ProcessId, participant.StartTicksUtc);

        /// <summary>
        /// Serializes desktop sections opened by this one command.
        /// </summary>
        /// <remarks>
        /// <c>active.lock</c> is <c>FileShare.None</c>, so a second handle blocks even from the same
        /// process. Without this gate, two concurrent tasks in one command would race on the file lock;
        /// with an earlier refcount design one of them would have skipped the lock entirely and run its
        /// desktop work unprotected. The gate makes unrelated concurrent enters queue up instead.
        /// <para>
        /// Desktop sections are deliberately NOT reentrant. Every call site is sequential: the screenshot
        /// pass enters once per restore/foreground/live-screen moment and closes each scope before the
        /// next, and recording enters once before capture plus once per rare blank-frame retry. Code that
        /// genuinely needs the desktop inside an open section must receive the existing
        /// <see cref="IDesktopSection"/> scope rather than opening a nested one, which would self-deadlock
        /// on the file lock.
        /// </para>
        /// </remarks>
        private readonly SemaphoreSlim _sectionGate = new(1, 1);

        private IParticipantLease? _lease;
        private FileStream? _activeLock;        private bool _detached;
        private bool _recoveredFromCorruption;
        private long? _ticket;
        private UiTurnAction _turnAction = UiTurnAction.New;
        private int _observedQueueDepth;

        /// <summary>
        /// Monotonic tick at which the turn this command runs under was claimed, copied from the state
        /// every time this command observes the turn it belongs to. Reported as the turn-age bucket, so
        /// it measures how long the whole workflow has held the desktop rather than how long this one
        /// command waited (spec §16). Null for detached observations, which hold no turn.
        /// </summary>
        private long? _turnStartedTick64;

        public UiTurnMode Mode { get; private set; } = mode;

        public long WaitedMs { get; private set; }

        /// <summary>
        /// Publishes a mutated state and wakes every participant the mutation made runnable, skipping
        /// this command itself.
        /// </summary>
        private void PublishAndSignal(
            InteractiveDesktopState state,
            HashSet<(int Pid, long StartTicksUtc)> runnableBefore)
            => coordinator.PublishAndSignal(
                state, runnableBefore, (participant.ProcessId, participant.StartTicksUtc));

        public async Task<int> RunAsync(
            Func<IUiTurn, CancellationToken, Task<int>> body,
            CancellationToken cancellationToken)
        {
            var bodyCompletedNormally = false;
            var outcome = UiCoordinationOutcome.Completed;

            try
            {
                Register(cancellationToken);

                if (!_detached)
                {
                    await WaitUntilRunnableAsync(cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    var exitCode = await body(this, cancellationToken).ConfigureAwait(false);

                    // "Completed normally" means the body RETURNED rather than threw — deliberately not
                    // "the token is unset". `ui record` observes Ctrl+C on purpose, finalizes the MP4 and
                    // returns success; that is a completed command and must renew the owner's grace.
                    // Commands that must not renew let the cancellation propagate instead, which is why
                    // handler catch-alls are filtered with UiCoordinatedAction.IsCoordinationFault.
                    bodyCompletedNormally = true;
                    return exitCode;
                }
                finally
                {
                    await ReleaseAllSectionsAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!bodyCompletedNormally)
            {
                // The body threw rather than returned, so there is no result to preserve (spec §11.1 for
                // a queued command; §11.2 for one cancelled after acquisition, whose command semantics are
                // "it produced nothing"). Either way the command must not renew the owner's idle grace,
                // which the `finally` below guarantees by passing bodyCompletedNormally: false.
                //
                // A command that DOES have something to preserve — an active `ui record` finalizing its
                // MP4 on Ctrl+C — returns instead of throwing, never reaches here, and still renews.
                outcome = UiCoordinationOutcome.Cancelled;
                EmitCancellation(cancelledWhileQueued: _waitWatch.IsRunning);
                return CancelledExitCode;
            }
            catch (UiCoordinationException)
            {
                outcome = UiCoordinationOutcome.CoordinationFailure;
                throw;
            }
            finally
            {
                Complete(bodyCompletedNormally);
                PublishTelemetry(bodyCompletedNormally, outcome);
            }
        }

        private void Register(CancellationToken cancellationToken)
        {
            using var stateLock = coordinator._store.AcquireStateLock(cancellationToken);
            var read = coordinator._store.Read();
            _recoveredFromCorruption = read.RecoveredFromCorruption;

            if (read.UnknownNewerVersion)
            {
                RegisterAgainstUnknownVersion();
                return;
            }

            var state = read.State!;

            // Captured before any mutation: everything published from this transaction compares against
            // it to find who became runnable.
            var runnableBefore = InteractiveDesktopScheduler.RunnableParticipants(state);

            if (Mode == UiTurnMode.Observe)
            {
                RegisterObserve(state, runnableBefore);
                return;
            }

            RegisterParticipating(state, runnableBefore);
        }

        /// <summary>
        /// A newer binary owns the state file. Observations continue detached without touching it;
        /// anything that would claim or mutate a turn fails closed rather than driving the desktop
        /// outside coordination (spec §12.4).
        /// </summary>
        private void RegisterAgainstUnknownVersion()
        {
            if (Mode != UiTurnMode.Observe)
            {
                throw new UiCoordinationException(
                    UiCoordinationErrorCodes.Unavailable,
                    "UI turn coordination state was written by a newer version of winapp, so this build cannot coordinate safely.",
                    "Update winapp so every process on this desktop uses a compatible version, then retry.");
            }

            coordinator._logger.LogDebug(
                "UI coordination state has a newer schema version; running {Operation} detached.", participant.Operation);
            _detached = true;
            _turnAction = UiTurnAction.Detached;
        }

        private void RegisterObserve(InteractiveDesktopState state, HashSet<(int Pid, long StartTicksUtc)> runnableBefore)
        {
            var changed = coordinator._scheduler.Normalize(state, _probe);

            if (!InteractiveDesktopScheduler.IsCurrentOwner(state, owner))
            {
                // Spec §6.2: a non-owner observation never claims a free turn, so it runs with no lease
                // and no state entry and cannot block anyone.
                if (changed || _recoveredFromCorruption)
                {
                    PublishAndSignal(state, runnableBefore);
                }

                _detached = true;
                _turnAction = UiTurnAction.Detached;
                return;
            }

            // The lease is opened before the entry is published so no published command ever lacks
            // liveness proof (spec §9 rule 5).
            _lease = coordinator._participants.OpenLease(participant.ProcessId, participant.StartTicksUtc);
            var admission = coordinator._scheduler.BeginObserve(state, _probe, owner, participant);

            if (admission.Admission == UiAdmission.Detached)
            {
                // BeginObserve re-normalizes, so ownership can lapse between the check above and here —
                // an expiring grace released by normalization. Nothing was added to
                // the state, so the lease must go too: keeping it open would publish liveness for a
                // participant with no entry, and Complete would later adjust a foreign owner's turn.
                _lease.Dispose();
                _lease = null;
                _detached = true;
                _turnAction = UiTurnAction.Detached;

                if (changed || _recoveredFromCorruption)
                {
                    PublishAndSignal(state, runnableBefore);
                }

                return;
            }

            _turnAction = admission.TurnAction;
            _turnStartedTick64 = TurnStartTick(state);
            PublishAndSignal(state, runnableBefore);
        }

        private void RegisterParticipating(InteractiveDesktopState state, HashSet<(int Pid, long StartTicksUtc)> runnableBefore)
        {
            _lease = coordinator._participants.OpenLease(participant.ProcessId, participant.StartTicksUtc);

            UiAdmissionResult admission;
            try
            {
                admission = coordinator._scheduler.BeginParticipating(state, _probe, owner, participant, Mode);
            }
            catch
            {
                // Nothing was published, so close the lease immediately rather than leaving an orphan for
                // another coordinator to prune (spec §10.3).
                _lease.Dispose();
                _lease = null;
                throw;
            }

            _ticket = admission.Ticket;
            _turnAction = admission.TurnAction;
            _turnStartedTick64 = admission.Admission == UiAdmission.GlobalWaiter
                ? null
                : TurnStartTick(state);
            // Normalized inside BeginParticipating, so the list is already only live waiters.
            _observedQueueDepth = InteractiveDesktopScheduler.CountWaiters(state);
            PublishAndSignal(state, runnableBefore);

            if (admission.Admission is UiAdmission.OwnerCommandWaiting or UiAdmission.GlobalWaiter)
            {
                _waitWatch.Start();
            }
        }

        /// <summary>
        /// Waits until this command's entry is <see cref="UiCommandStatus.Running"/> — covering both the
        /// global FIFO wait and the owner-local forward barrier. Cancellable and indefinite: there is no
        /// coordination timeout in v1 (spec §10.3, §10.4).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Waiting is push-based: whoever makes this command runnable wakes it, so the common case costs
        /// one state read rather than one every 50-75 ms for the whole wait. The signal is only a hint,
        /// though — the status is always re-read under <c>state.lock</c> before returning, so a stale,
        /// duplicated or spurious wake cannot start a command that is not actually eligible.
        /// </para>
        /// <para>
        /// A wake-up that never arrives must not strand anyone, which is what the recovery deadlines are
        /// for. Only the head of the queue can be waiting on a process that died without publishing
        /// anything, so only the head rechecks briskly; everyone behind it is covered by a much longer
        /// backstop, because their turn cannot come before the head's does.
        /// </para>
        /// </remarks>
        private async Task WaitUntilRunnableAsync(CancellationToken cancellationToken)
        {
            if (!_waitWatch.IsRunning)
            {
                return;
            }

            var reporter = new UiCoordinationWaitReporter(
                coordinator._console, outputMode, participant.Operation);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                WaitPlan plan;
                using (var stateLock = coordinator._store.AcquireStateLock(cancellationToken))
                {
                    var read = coordinator._store.Read();
                    if (read.UnknownNewerVersion)
                    {
                        throw new UiCoordinationException(
                            UiCoordinationErrorCodes.Unavailable,
                            "UI turn coordination state was replaced by a newer version of winapp while this command was waiting.",
                            "Update winapp so every process on this desktop uses a compatible version, then retry.");
                    }

                    var state = read.State!;
                    var runnableBefore = InteractiveDesktopScheduler.RunnableParticipants(state);
                    if (coordinator._scheduler.Normalize(state, _probe) || read.RecoveredFromCorruption)
                    {
                        // This waiter may itself be the one that recovers a crashed owner, in which case
                        // it has just promoted somebody — possibly not itself.
                        PublishAndSignal(state, runnableBefore);
                    }

                    // Telemetry reports the deepest queue this command ever saw, so it is sampled on
                    // every look rather than alongside the status line: under --json and --quiet no
                    // status line is ever due, and sampling there would have reported only the depth at
                    // registration and missed everything that queued up behind it afterwards. Counting a
                    // normalized list is a list length, so it is cheap enough to do unconditionally.
                    _observedQueueDepth = Math.Max(
                        _observedQueueDepth, InteractiveDesktopScheduler.CountWaiters(state));

                    var entry = InteractiveDesktopScheduler.FindOwnerCommand(state, participant);
                    if (entry is { Status: UiCommandStatus.Running })
                    {
                        _waitWatch.Stop();
                        WaitedMs = _waitWatch.ElapsedMilliseconds;
                        Mode = entry.Mode;
                        _ticket = entry.Ticket;

                        // A global waiter only learns its turn's start once it has been promoted into
                        // ownerCommands, which may be many turns after it queued.
                        _turnStartedTick64 = TurnStartTick(state);
                        return;
                    }

                    plan = BuildWaitPlan(state, entry, reporter.IsReportDue(_waitWatch.ElapsedMilliseconds));
                }

                if (plan.Diagnostics is { } diagnostics)
                {
                    reporter.ReportIfDue(_waitWatch.ElapsedMilliseconds, diagnostics);
                }

                // A signal that arrived before this call is still latched on the auto-reset event, so a
                // promoter that published and woke us while we were between iterations cannot be missed.
                await _signal.WaitAsync(plan.Timeout, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>How long to sleep next, and the diagnostics to render first when one is due.</summary>
        private readonly record struct WaitPlan(TimeSpan Timeout, UiWaitDiagnostics? Diagnostics);

        /// <summary>
        /// Decides how long this command may sleep before it must look again on its own.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The head of the global queue, and a command sitting at the front of its own owner's barrier,
        /// take the short interval: they are the ones whose unblocking may depend on a process that
        /// died without publishing anything, and a dead process sends no signals. Everyone else keeps
        /// the long backstop, which only exists for a promoter that crashed between publishing and
        /// signalling.
        /// </para>
        /// <para>
        /// An explicit idle grace is a deadline nobody will announce — the turn simply becomes stale at
        /// a known time — so the head also wakes exactly then rather than up to an interval late.
        /// </para>
        /// </remarks>
        private WaitPlan BuildWaitPlan(InteractiveDesktopState state, OwnerCommandEntry? ownEntry, bool reportDue)
        {
            var isHead = IsRecoveryResponsible(state, ownEntry);
            var timeoutMs = isHead ? HeadRecoveryMs : DeepRecoveryMs;

            if (isHead && state.Owner is not null && state.OwnerCommands.Count == 0)
            {
                // The turn is idle and will lapse at a known tick; waking then turns a grace expiry into
                // an immediate handoff instead of one that waits for the next interval.
                var untilGrace = state.IdleExpiresTick64 - coordinator._clock.NowTicks64;
                if (untilGrace > 0 && untilGrace < timeoutMs)
                {
                    timeoutMs = (int)untilGrace;
                }
            }

            if (outputMode.AllowsWaitingStatus)
            {
                // Human output has its own cadence to keep, so never sleep past the next status line.
                var untilReport = NextReportInMs(_waitWatch.ElapsedMilliseconds);
                if (untilReport < timeoutMs)
                {
                    timeoutMs = untilReport;
                }
            }

            return new WaitPlan(
                TimeSpan.FromMilliseconds(Math.Max(1, timeoutMs)),
                reportDue ? BuildDiagnostics(state, ownEntry) : null);
        }

        /// <summary>
        /// Whether this command is the one that must notice a failure nobody will report.
        /// </summary>
        private bool IsRecoveryResponsible(InteractiveDesktopState state, OwnerCommandEntry? ownEntry)
        {
            if (ownEntry is not null)
            {
                // Blocked behind its own owner's barrier: responsible when nothing of this owner's is
                // ahead of it, because then the only thing it waits on is a command that may have died.
                var ownTicket = ownEntry.Ticket ?? long.MaxValue;
                return !state.OwnerCommands.Any(c =>
                    (c.Pid != participant.ProcessId || c.ProcessStartTicksUtc != participant.StartTicksUtc)
                    && (c.Ticket ?? long.MaxValue) < ownTicket);
            }

            // Global queue: the lowest live ticket. Normalization has already pruned the dead, so the
            // minimum is the true head.
            var head = state.Waiters.MinBy(w => w.Ticket);
            return head is not null
                && head.Pid == participant.ProcessId
                && head.ProcessStartTicksUtc == participant.StartTicksUtc;
        }

        /// <summary>Milliseconds until the wait reporter would next print, for the sleep clamp.</summary>
        private static int NextReportInMs(long elapsedMs)
        {
            if (elapsedMs < UiCoordinationWaitReporter.FirstReportAfterMs)
            {
                return (int)(UiCoordinationWaitReporter.FirstReportAfterMs - elapsedMs);
            }

            var sinceCycle = (elapsedMs - UiCoordinationWaitReporter.FirstReportAfterMs)
                % UiCoordinationWaitReporter.RepeatIntervalMs;
            return (int)(UiCoordinationWaitReporter.RepeatIntervalMs - sinceCycle);
        }

        private UiWaitDiagnostics BuildDiagnostics(InteractiveDesktopState state, OwnerCommandEntry? ownEntry)
        {
            // Called only from inside a transaction that has just normalized, so the lists hold live
            // participants only and no entry here needs a process handle to confirm it. The observed
            // depth is not tracked here: this runs only when a status line is due, and the telemetry
            // has to be the same whether or not anyone was watching.
            var queueDepth = InteractiveDesktopScheduler.CountWaiters(state);

            var active = state.OwnerCommands
                .Where(c => c.Status == UiCommandStatus.Running && c.Pid != participant.ProcessId)
                .OrderBy(c => c.Ticket ?? long.MaxValue)
                .FirstOrDefault();

            int commandsAhead;
            if (ownEntry is { Ticket: { } ownTicket })
            {
                // Owner-local: everything ahead of us in our own owner's barrier order.
                commandsAhead = state.OwnerCommands.Count(c => (c.Ticket ?? long.MaxValue) < ownTicket);
            }
            else if (_ticket is { } queuedTicket)
            {
                commandsAhead = state.OwnerCommands.Count
                    + state.Waiters.Count(w => w.Ticket < queuedTicket);
            }
            else
            {
                commandsAhead = state.OwnerCommands.Count;
            }

            return new UiWaitDiagnostics(
                queueDepth,
                commandsAhead,
                active?.Pid,
                active?.Operation);
        }

        public async Task<IAsyncDisposable> EnterAsync(CancellationToken cancellationToken)
        {
            // Serialize within this command first, then take the cross-process lock. Both are required:
            // the gate stops two concurrent tasks in this command from racing, and active.lock stops
            // other winapp processes from acting on the desktop at the same time.
            await _sectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                _activeLock = await coordinator
                    .AcquireActiveLockAsync(coordinator._paths.ActiveLockPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                _sectionGate.Release();
                throw;
            }

            return new SectionScope(this);
        }

        private async Task ReleaseAllSectionsAsync()
        {
            // Safety net for a body that returned or threw without disposing its scope. Releasing the
            // file lock here (rather than the gate) is deliberate: a leaked gate would only stall this
            // already-finishing command, whereas a leaked active.lock would block the whole desktop
            // until the process exits.
            if (_activeLock is not null)
            {
                await _activeLock.DisposeAsync().ConfigureAwait(false);
                _activeLock = null;
            }
        }

        /// <summary>
        /// Removes this command's entry and applies the idle-grace rule, then closes the lease — in that
        /// order, so no entry is ever left without liveness proof (spec §9 rule 6, §10.6).
        /// </summary>
        private void Complete(bool renewGrace)
        {
            if (_lease is null)
            {
                return;
            }

            try
            {
                using var stateLock = coordinator._store.AcquireStateLock(CancellationToken.None);
                var read = coordinator._store.Read();
                if (read.State is { } state)
                {
                    // The most important wake-up of all: finishing is what frees the desktop, so the
                    // participants this completion promotes must be told before this process exits.
                    var runnableBefore = InteractiveDesktopScheduler.RunnableParticipants(state);
                    coordinator._scheduler.CompleteCommand(state, _probe, participant, owner, renewGrace);
                    PublishAndSignal(state, runnableBefore);
                }
            }
            catch (Exception ex) when (ex is UiCoordinationException or IOException)
            {
                // Teardown must never mask the command's own result. Windows deletes the lease below, so
                // the next coordinator prunes this entry anyway.
                coordinator._logger.LogDebug("UI coordination teardown could not update state: {Message}", ex.Message);
            }
            finally
            {
                _lease.Dispose();
                _lease = null;
            }
        }

        /// <summary>
        /// Emits the structured <c>cancelled</c> envelope (spec §14).
        /// </summary>
        /// <param name="cancelledWhileQueued">
        /// <see langword="true"/> when the command never reached execution, which is the case that also
        /// carries a queue position. <see langword="false"/> when it was cancelled after acquiring the
        /// turn, where UI side effects may already have happened.
        /// </param>
        private void EmitCancellation(bool cancelledWhileQueued)
        {
            var waitedMs = _waitWatch.ElapsedMilliseconds;
            int? queuePosition = null;

            try
            {
                using var stateLock = coordinator._store.AcquireStateLock(CancellationToken.None);
                var read = coordinator._store.Read();
                if (read.State is { } state && _ticket is { } ticket
                    && InteractiveDesktopScheduler.FindWaiter(state, participant) is not null)
                {
                    queuePosition = InteractiveDesktopScheduler.QueuePositionOf(state, _probe, ticket);
                }
            }
            catch (Exception ex) when (ex is UiCoordinationException or IOException)
            {
                coordinator._logger.LogDebug("Queue position could not be read while cancelling: {Message}", ex.Message);
            }

            var message = cancelledWhileQueued
                ? "UI turn wait was cancelled."
                : "The command was cancelled after it acquired the desktop; any UI changes it had already made remain.";

            UiJsonError.Emit(
                outputMode.Json,
                UiCoordinationErrorCodes.Cancelled,
                message,
                errorOut: parseResult.InvocationConfiguration.Error,
                coordination: new UiCoordinationInfo
                {
                    WaitedMs = waitedMs,
                    QueuePosition = queuePosition,
                });

            if (!outputMode.Json && !outputMode.Quiet)
            {
                if (cancelledWhileQueued)
                {
                    coordinator._logger.LogWarning(
                        "{Symbol} Cancelled while waiting {WaitedMs} ms for the desktop.",
                        UiSymbols.Warning,
                        waitedMs);
                }
                else
                {
                    coordinator._logger.LogWarning("{Symbol} {Message}", UiSymbols.Warning, message);
                }
            }
        }

        private void PublishTelemetry(bool completedNormally, UiCoordinationOutcome outcome)
        {
            var effectiveOutcome = _recoveredFromCorruption && completedNormally
                ? UiCoordinationOutcome.CorruptionRecovery
                : outcome;

            UiCoordinationTelemetryScope.Set(new UiCoordinationSummary(
                owner.Kind,
                Mode,
                _turnAction,
                effectiveOutcome,
                WaitedMs,
                _observedQueueDepth,
                MeasureTurnAgeMs()));
        }

        /// <summary>
        /// The turn's claim tick, or <see langword="null"/> when it is unknown. Zero is the "no turn"
        /// sentinel written by <c>CreateFresh</c> and by idle expiry, and is also what an older state
        /// file that predates the field deserializes to — measuring an age from it would report the
        /// machine's uptime.
        /// </summary>
        private static long? TurnStartTick(InteractiveDesktopState state)
            => state.TurnStartedTick64 == 0 ? null : state.TurnStartedTick64;

        /// <summary>
        /// How long the turn this command ran under had been held when the command finished. Zero for a
        /// detached observation and for a command that never acquired a turn, which have no turn to age.
        /// </summary>
        private long MeasureTurnAgeMs()
        {
            if (_turnStartedTick64 is not { } startedTick)
            {
                return 0;
            }

            // Clamped rather than trusted: the tick was written by whichever process claimed the turn,
            // and a state file that survived a reboot carries pre-restart ticks.
            var age = coordinator._clock.NowTicks64 - startedTick;
            return age > 0 ? age : 0;
        }

        /// <summary>
        /// Releases the in-process section gate once the command has finished.
        /// </summary>
        /// <remarks>
        /// The participant lease is normally closed by <see cref="Complete"/>, which must remove this
        /// command's state entry <em>before</em> the lease closes (spec §9 rule 6). This runs strictly
        /// after that, so the null-conditional call here is only a safety net for a lease that somehow
        /// outlived completion — it never inverts the ordering.
        /// </remarks>
        public void Dispose()
        {
            _lease?.Dispose();
            _lease = null;

            // Closed last, after the lease. While this handle is open another process can still name and
            // signal us, which is harmless; closing it before the entry is gone would only lose wake-ups
            // we might still legitimately receive.
            _signal.Dispose();
            _sectionGate.Dispose();
        }

        private sealed class SectionScope(CoordinatedExecution execution) : IAsyncDisposable
        {
            private bool _disposed;

            public async ValueTask DisposeAsync()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                // Release the cross-process lock before the in-process gate, so the next waiter in this
                // command never finds the gate open while the file lock is still held.
                if (execution._activeLock is not null)
                {
                    await execution._activeLock.DisposeAsync().ConfigureAwait(false);
                    execution._activeLock = null;
                }

                execution._sectionGate.Release();
            }
        }
    }
}

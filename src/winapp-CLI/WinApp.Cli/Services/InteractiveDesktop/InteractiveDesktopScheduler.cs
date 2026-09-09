// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.InteractiveDesktop;

/// <summary>
/// Answers the two liveness questions the scheduler needs, kept behind an interface so every state
/// transition is testable without real processes or lease files.
/// </summary>
internal interface ICoordinationLivenessProbe
{
    /// <summary>
    /// Whether a recorded participant still holds its lease. This is the <em>only</em> basis for
    /// pruning: there are no heartbeats and no timestamps, so a merely suspended process is correctly
    /// reported alive and keeps its queue position.
    /// </summary>
    bool IsParticipantLive(int processId, long startTicksUtc);
}

/// <summary>Identity of the command this process is registering.</summary>
/// <param name="ProcessId">This process's id.</param>
/// <param name="StartTicksUtc">This process's start ticks, pairing with the PID to defeat PID reuse.</param>
/// <param name="Operation">Command name for diagnostics, e.g. <c>ui click</c>. Never arguments.</param>
internal readonly record struct UiParticipantIdentity(int ProcessId, long StartTicksUtc, string Operation);

/// <summary>Where a newly admitted command landed.</summary>
internal enum UiAdmission
{
    /// <summary>Registered in <see cref="InteractiveDesktopState.OwnerCommands"/> and already eligible.</summary>
    OwnerCommandRunning,

    /// <summary>Registered in <see cref="InteractiveDesktopState.OwnerCommands"/> but behind an earlier barrier.</summary>
    OwnerCommandWaiting,

    /// <summary>Queued in the global FIFO behind another owner's turn.</summary>
    GlobalWaiter,

    /// <summary>A non-owner observation that runs detached, with no lease and no state entry.</summary>
    Detached,
}

/// <summary>Result of admitting a command.</summary>
/// <param name="Admission">Where the command landed.</param>
/// <param name="Ticket">Arrival ticket, or <see langword="null"/> for detached observations.</param>
/// <param name="TurnAction">How the turn was obtained, for telemetry and verbose output.</param>
/// <param name="QueuePosition">One-based position among live global waiters, when queued.</param>
internal readonly record struct UiAdmissionResult(
    UiAdmission Admission,
    long? Ticket,
    UiTurnAction TurnAction,
    int? QueuePosition);

/// <summary>
/// The cooperative-turn state machine (spec §10.1–§10.7). Deliberately free of file, clock-reading and
/// process side effects: it mutates an <see cref="InteractiveDesktopState"/> instance that the caller
/// read and will publish under <c>state.lock</c>. That keeps every scheduling rule — expiry, the
/// forward barrier, FIFO promotion, handoff — exhaustively testable without touching the desktop.
/// </summary>
internal sealed class InteractiveDesktopScheduler(IMonotonicClock clock)
{
    /// <summary>
    /// Idle grace after the most recent non-cancelled owner command completes (spec §4). Four seconds
    /// comfortably covers the gap between commands in one shell script while intentionally expiring
    /// during model reasoning, so an adaptive agent must reacquire and replay rather than hold the
    /// desktop hostage.
    /// </summary>
    internal const int IdleGraceMs = 4_000;

    /// <summary>Maximum live global waiters, applied after pruning dead entries (spec §8).</summary>
    internal const int MaxGlobalWaiters = 64;

    /// <summary>
    /// Applies section 10.1 normalization: prune dead participants, expire an idle turn, promote the
    /// oldest live waiter, and re-evaluate owner-local eligibility.
    /// </summary>
    /// <returns><see langword="true"/> when anything changed and the state must be published.</returns>
    public bool Normalize(InteractiveDesktopState state, ICoordinationLivenessProbe probe)
    {
        var changed = ClampStaleDeadline(state);
        changed |= PruneDeadParticipants(state, probe);
        changed |= ExpireIdleTurn(state);
        changed |= PromoteOldestWaiter(state);
        changed |= AbsorbSameOwnerWaiters(state);
        changed |= ApplyOwnerLocalEligibility(state);
        return changed;
    }

    /// <summary>
    /// Treats an idle deadline further out than the grace itself as already expired.
    /// </summary>
    /// <remarks>
    /// The only writer sets <c>now + <see cref="IdleGraceMs"/></c>, so a larger value cannot have been
    /// produced during this boot. Windows resets <see cref="Environment.TickCount64"/> on restart, so a
    /// state file written after days of uptime carries a deadline far beyond the new uptime and would
    /// otherwise pin the turn to an owner that died with the previous boot — for days. Clamping to
    /// <c>now</c> lets <see cref="ExpireIdleTurn"/> release it on the very next normalization.
    /// </remarks>
    private bool ClampStaleDeadline(InteractiveDesktopState state)
    {
        var now = clock.NowTicks64;
        if (state.IdleExpiresTick64 <= now + IdleGraceMs)
        {
            return false;
        }

        state.IdleExpiresTick64 = now;
        return true;
    }

    /// <summary>
    /// Section 10.2: registers a current-owner observation so it pins and renews the turn, or reports
    /// that a non-owner observation should run detached.
    /// </summary>
    public UiAdmissionResult BeginObserve(
        InteractiveDesktopState state,
        ICoordinationLivenessProbe probe,
        UiOwnerIdentity owner,
        UiParticipantIdentity participant)
    {
        Normalize(state, probe);

        if (state.Owner is null || !OwnerMatches(state.Owner, owner))
        {
            // Another owner holds the turn, or nobody does. Observations never claim a free turn, so
            // this runs concurrently without a lease or a state entry.
            return new UiAdmissionResult(UiAdmission.Detached, null, UiTurnAction.Detached, null);
        }

        state.OwnerCommands.Add(new OwnerCommandEntry
        {
            Ticket = null,
            Pid = participant.ProcessId,
            ProcessStartTicksUtc = participant.StartTicksUtc,
            Operation = participant.Operation,
            Mode = UiTurnMode.Observe,
            Status = UiCommandStatus.Running,
        });

        return new UiAdmissionResult(UiAdmission.OwnerCommandRunning, null, UiTurnAction.Continuation, null);
    }

    /// <summary>
    /// Section 10.3: admits a <see cref="UiTurnMode.TurnShared"/> or
    /// <see cref="UiTurnMode.DesktopExclusive"/> command — starting a new turn, joining the owner's
    /// existing turn, or queueing globally behind another owner.
    /// </summary>
    /// <exception cref="UiCoordinationException">The global queue is full after pruning.</exception>
    public UiAdmissionResult BeginParticipating(
        InteractiveDesktopState state,
        ICoordinationLivenessProbe probe,
        UiOwnerIdentity owner,
        UiParticipantIdentity participant,
        UiTurnMode mode)
    {
        // Captured before normalization, which is what releases an expired owner: afterwards there is no
        // way to tell "the desktop was free" from "another workflow's idle grace just ran out".
        var previousOwnerKey = state.Owner?.Key;

        Normalize(state, probe);

        // Normalize just pruned every dead participant, so the in-memory list IS the live queue. The
        // cap therefore counts entries rather than re-probing each one: at a full queue that was 64
        // process handles opened per admission, which is most of what made a deep queue expensive.
        var liveWaiters = CountWaiters(state);

        if (state.Owner is null && liveWaiters == 0)
        {
            // A previous owner released by the normalization above means this command did not simply
            // find a free desktop — it took over a turn whose idle grace had run out (spec §10.7).
            var handoff = previousOwnerKey is not null
                && !string.Equals(previousOwnerKey, owner.Key, StringComparison.Ordinal);

            ClaimTurn(state, ToOwnerRecord(owner));
            AddOwnerCommand(state, participant, mode);
            ApplyOwnerLocalEligibility(state);
            return Describe(
                state,
                participant,
                handoff ? UiTurnAction.HandoffAfterIdle : UiTurnAction.New);
        }

        if (state.Owner is not null && OwnerMatches(state.Owner, owner))
        {
            AddOwnerCommand(state, participant, mode);
            ApplyOwnerLocalEligibility(state);
            return Describe(state, participant, UiTurnAction.Continuation);
        }

        if (liveWaiters >= MaxGlobalWaiters)
        {
            // Refuse before publishing anything, so the caller can close its lease and exit without
            // leaving an entry that other coordinators would have to prune.
            throw new UiCoordinationException(
                UiCoordinationErrorCodes.QueueCapacityExceeded,
                $"{MaxGlobalWaiters} winapp ui commands are already waiting for the desktop.",
                "Wait for the queued commands to finish, or stop some of the waiting winapp ui processes, then retry.");
        }

        var ticket = state.AllocateTicket();
        state.Waiters.Add(new WaiterEntry
        {
            Ticket = ticket,
            OwnerKey = owner.Key,
            OwnerKind = owner.Kind,
            Pid = participant.ProcessId,
            ProcessStartTicksUtc = participant.StartTicksUtc,
            Operation = participant.Operation,
            Mode = mode,
        });

        return new UiAdmissionResult(
            UiAdmission.GlobalWaiter,
            ticket,
            UiTurnAction.Queued,
            QueuePositionOf(state, ticket));
    }

    /// <summary>
    /// Ends the current owner's idle grace now, then re-normalizes so the release, the promotion of the
    /// next waiter and that waiter's eligibility all land in the same transaction.
    /// </summary>
    /// <remarks>
    /// The caller must have already established that the turn belongs to the yielding workflow and that
    /// it has no live commands under it — this deliberately does not re-check, because it is the same
    /// primitive <see cref="ExpireIdleTurn"/> applies when the grace runs out on its own, only at a time
    /// the owner chose. Expressing it as "the deadline is now" rather than as a second way to clear
    /// <see cref="InteractiveDesktopState.Owner"/> means an explicit yield and a lapsed grace cannot
    /// diverge.
    /// </remarks>
    public void ReleaseIdleTurn(InteractiveDesktopState state, ICoordinationLivenessProbe probe)
    {
        state.IdleExpiresTick64 = clock.NowTicks64;
        Normalize(state, probe);
    }

    /// <summary>
    /// Section 10.6: removes this process's command and sets the idle deadline. A non-cancelled
    /// completion renews the grace; an anonymous owner gets none and hands off immediately;
    /// cancellation never renews.
    /// </summary>
    /// <remarks>
    /// The deadline belongs to whoever currently holds the turn, so it is only touched when
    /// <paramref name="owner"/> is that owner. Without this check a process finishing under a
    /// different identity — a global waiter that was cancelled, or a command whose owner already lost
    /// the turn — would rewrite a stranger's grace: an anonymous completion would revoke it outright
    /// (deadline = now) and a normal one would silently extend it.
    /// </remarks>
    public void CompleteCommand(
        InteractiveDesktopState state,
        ICoordinationLivenessProbe probe,
        UiParticipantIdentity participant,
        UiOwnerIdentity owner,
        bool renewGrace)
    {
        RemoveParticipantEntries(state, participant);

        if (state.Owner is not null && OwnerMatches(state.Owner, owner))
        {
            // The two tiers differ only here. A named workflow banks a grace so its next command finds
            // the desktop still reserved; an anonymous one-shot has nothing that could issue a follow-up,
            // so holding the desktop any longer would only delay everyone else.
            state.IdleExpiresTick64 = renewGrace && owner.HasContinuity
                ? clock.NowTicks64 + IdleGraceMs
                : clock.NowTicks64;
        }

        Normalize(state, probe);
    }

    /// <summary>
    /// Whether <paramref name="owner"/> currently holds the turn. Callers use this before opening a
    /// participant lease, so a detached observation never creates one.
    /// </summary>
    public static bool IsCurrentOwner(InteractiveDesktopState state, UiOwnerIdentity owner)
        => state.Owner is { } record && OwnerMatches(record, owner);

    /// <summary>
    /// This process's current owner-command entry, or <see langword="null"/> when it is still queued
    /// globally or has been pruned.
    /// </summary>
    public static OwnerCommandEntry? FindOwnerCommand(InteractiveDesktopState state, UiParticipantIdentity participant)
        => state.OwnerCommands.FirstOrDefault(
            c => c.Pid == participant.ProcessId && c.ProcessStartTicksUtc == participant.StartTicksUtc);

    /// <summary>This process's global waiter entry, or <see langword="null"/> once promoted or pruned.</summary>
    public static WaiterEntry? FindWaiter(InteractiveDesktopState state, UiParticipantIdentity participant)
        => state.Waiters.FirstOrDefault(
            w => w.Pid == participant.ProcessId && w.ProcessStartTicksUtc == participant.StartTicksUtc);

    /// <summary>
    /// One-based position of a ticket among live global waiters, for cancellation diagnostics.
    /// </summary>
    /// <remarks>
    /// Probes each waiter ahead, because this overload is reached from cancellation teardown where no
    /// normalization is guaranteed to have run and the list may still name processes that have exited.
    /// Inside a transaction that has just normalized, use <see cref="QueuePositionOf(InteractiveDesktopState, long)"/>.
    /// </remarks>
    public static int? QueuePositionOf(InteractiveDesktopState state, ICoordinationLivenessProbe probe, long ticket)
    {
        var ahead = 0;
        var found = false;
        foreach (var waiter in state.Waiters.OrderBy(w => w.Ticket))
        {
            if (waiter.Ticket == ticket)
            {
                found = true;
                break;
            }

            if (probe.IsParticipantLive(waiter.Pid, waiter.ProcessStartTicksUtc))
            {
                ahead++;
            }
        }

        return found ? ahead + 1 : null;
    }

    /// <summary>
    /// One-based position of a ticket in an already-normalized queue, where every remaining waiter is
    /// live by construction.
    /// </summary>
    public static int? QueuePositionOf(InteractiveDesktopState state, long ticket)
    {
        var ahead = 0;
        foreach (var waiter in state.Waiters.OrderBy(w => w.Ticket))
        {
            if (waiter.Ticket == ticket)
            {
                return ahead + 1;
            }

            ahead++;
        }

        return null;
    }

    /// <summary>Live global waiter count, used for verbose output and the queue cap.</summary>
    public static int CountLiveWaiters(InteractiveDesktopState state, ICoordinationLivenessProbe probe)
        => state.Waiters.Count(w => probe.IsParticipantLive(w.Pid, w.ProcessStartTicksUtc));

    /// <summary>
    /// Live global waiter count taken straight from a normalized state, without re-probing.
    /// </summary>
    /// <remarks>
    /// <see cref="Normalize"/> has already removed every dead participant from the lists it returns, so
    /// asking the OS again inside the same <c>state.lock</c> transaction can only produce the same
    /// answer at the cost of one process handle per waiter — the cost that showed up as CPU burn when
    /// the queue was deep. Callers that have not just normalized must keep using the probing overload.
    /// </remarks>
    public static int CountWaiters(InteractiveDesktopState state) => state.Waiters.Count;

    /// <summary>
    /// The participants a published state says may run right now, keyed by the identity the state file
    /// carries.
    /// </summary>
    /// <remarks>
    /// Comparing this set before and after a transaction is how newly-runnable commands are found. That
    /// is deliberately a property of the state rather than of any one transition: promotion, same-owner
    /// absorption, barrier release, cancellation cleanup and crash pruning all reach the same place, so
    /// a future transition cannot forget to wake anyone.
    /// </remarks>
    public static HashSet<(int Pid, long StartTicksUtc)> RunnableParticipants(InteractiveDesktopState state)
    {
        var runnable = new HashSet<(int, long)>();
        foreach (var command in state.OwnerCommands)
        {
            if (command.Status == UiCommandStatus.Running)
            {
                runnable.Add((command.Pid, command.ProcessStartTicksUtc));
            }
        }

        return runnable;
    }

    private static void AddOwnerCommand(InteractiveDesktopState state, UiParticipantIdentity participant, UiTurnMode mode)
        => state.OwnerCommands.Add(new OwnerCommandEntry
        {
            Ticket = state.AllocateTicket(),
            Pid = participant.ProcessId,
            ProcessStartTicksUtc = participant.StartTicksUtc,
            Operation = participant.Operation,
            Mode = mode,
            Status = UiCommandStatus.Waiting,
        });

    private static UiAdmissionResult Describe(
        InteractiveDesktopState state, UiParticipantIdentity participant, UiTurnAction turnAction)
    {
        var entry = FindOwnerCommand(state, participant);
        var admission = entry?.Status == UiCommandStatus.Running
            ? UiAdmission.OwnerCommandRunning
            : UiAdmission.OwnerCommandWaiting;
        return new UiAdmissionResult(admission, entry?.Ticket, turnAction, null);
    }

    private static OwnerRecord ToOwnerRecord(UiOwnerIdentity owner) => new()
    {
        Kind = owner.Kind,
        Key = owner.Key,
    };

    private static bool OwnerMatches(OwnerRecord record, UiOwnerIdentity owner)
        => string.Equals(record.Key, owner.Key, StringComparison.Ordinal);

    private static void RemoveParticipantEntries(InteractiveDesktopState state, UiParticipantIdentity participant)
    {
        state.OwnerCommands.RemoveAll(
            c => c.Pid == participant.ProcessId && c.ProcessStartTicksUtc == participant.StartTicksUtc);
        state.Waiters.RemoveAll(
            w => w.Pid == participant.ProcessId && w.ProcessStartTicksUtc == participant.StartTicksUtc);
    }

    private static bool PruneDeadParticipants(InteractiveDesktopState state, ICoordinationLivenessProbe probe)
    {
        var removed = state.OwnerCommands.RemoveAll(
            c => !probe.IsParticipantLive(c.Pid, c.ProcessStartTicksUtc));
        removed += state.Waiters.RemoveAll(
            w => !probe.IsParticipantLive(w.Pid, w.ProcessStartTicksUtc));
        return removed > 0;
    }

    /// <summary>
    /// Whether the idle turn expired.
    /// </summary>
    private bool ExpireIdleTurn(InteractiveDesktopState state)
    {
        // Any live entry — waiting or running — counts as owner activity, so the turn is never taken
        // from an owner that still has work queued behind its own barrier.
        if (state.Owner is null
            || state.OwnerCommands.Count > 0
            || state.IdleExpiresTick64 > clock.NowTicks64)
        {
            return false;
        }

        state.Owner = null;
        state.IdleExpiresTick64 = 0;
        state.TurnStartedTick64 = 0;
        return true;
    }

    private bool PromoteOldestWaiter(InteractiveDesktopState state)
    {
        if (state.Owner is not null)
        {
            return false;
        }

        // Strict FIFO by persisted ticket, never by file-lock acquisition order. A suspended live waiter
        // therefore keeps the head of the queue until it resumes or is terminated.
        //
        // No liveness re-probe: PruneDeadParticipants ran first in this same normalization, so this list
        // is already only live waiters, and asking the OS again would cost one process handle per waiter
        // to learn nothing.
        var oldest = state.Waiters.MinBy(w => w.Ticket);

        if (oldest is null)
        {
            return false;
        }

        ClaimTurn(state, new OwnerRecord
        {
            Kind = oldest.OwnerKind,
            Key = oldest.OwnerKey,
        });
        return true;
    }

    /// <summary>
    /// Installs <paramref name="ownerRecord"/> as the current owner and stamps the turn. The only writer
    /// of <see cref="InteractiveDesktopState.TurnId"/> and
    /// <see cref="InteractiveDesktopState.TurnStartedTick64"/>, so the two can never disagree.
    /// </summary>
    private void ClaimTurn(InteractiveDesktopState state, OwnerRecord ownerRecord)
    {
        state.Owner = ownerRecord;
        state.TurnId++;
        state.TurnStartedTick64 = clock.NowTicks64;
        state.IdleExpiresTick64 = 0;
    }

    /// <summary>
    /// Moves the contiguous run of global waiters at the head of the queue that belong to the current
    /// owner into that owner's command list, preserving each waiter's arrival ticket.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Absorption stops at the first waiter belonging to a different owner. That is what keeps global
    /// FIFO strict: with tickets <c>B10, C11, B12</c> only <c>B10</c> is absorbed, so <c>C11</c> still
    /// runs before <c>B12</c>. With <c>B10, B11, C12</c> both <c>B10</c> and <c>B11</c> are absorbed,
    /// because no other owner is waiting between them.
    /// </para>
    /// <para>
    /// Absorbing the head prefix at all matches how section 10.3 admits a <em>new</em> same-owner command
    /// directly into <c>ownerCommands</c>. Without it, an owner that queued two commands behind another
    /// owner would run the first and then stall a full four seconds before its own second command, even
    /// though the turn is already theirs and nobody else is ahead of it.
    /// </para>
    /// </remarks>
    private static bool AbsorbSameOwnerWaiters(InteractiveDesktopState state)
    {
        if (state.Owner is not { } owner)
        {
            return false;
        }

        var absorbed = new List<WaiterEntry>();
        foreach (var waiter in state.Waiters.OrderBy(w => w.Ticket))
        {
            // Dead waiters were already pruned, so the ordered list is the live queue. The first
            // foreign owner ends the prefix — everything behind it keeps its place in global FIFO.
            if (!string.Equals(waiter.OwnerKey, owner.Key, StringComparison.Ordinal))
            {
                break;
            }

            absorbed.Add(waiter);
        }

        if (absorbed.Count == 0)
        {
            return false;
        }

        foreach (var waiter in absorbed)
        {
            state.Waiters.Remove(waiter);
            state.OwnerCommands.Add(new OwnerCommandEntry
            {
                Ticket = waiter.Ticket,
                Pid = waiter.Pid,
                ProcessStartTicksUtc = waiter.ProcessStartTicksUtc,
                Operation = waiter.Operation,
                Mode = waiter.Mode,
                Status = UiCommandStatus.Waiting,
            });
        }

        return true;
    }

    /// <summary>
    /// Section 10.4: a <see cref="UiTurnMode.DesktopExclusive"/> command with ticket T is a forward
    /// barrier — every later <see cref="UiTurnMode.TurnShared"/> or
    /// <see cref="UiTurnMode.DesktopExclusive"/> command waits behind it whether it is waiting or
    /// running, while earlier commands (including already-running <c>TurnShared</c> work such as a
    /// recording) continue.
    /// </summary>
    private static bool ApplyOwnerLocalEligibility(InteractiveDesktopState state)
    {
        long? earliestBarrier = null;
        foreach (var command in state.OwnerCommands)
        {
            if (command.Mode == UiTurnMode.DesktopExclusive && command.Ticket is { } ticket
                && (earliestBarrier is null || ticket < earliestBarrier))
            {
                earliestBarrier = ticket;
            }
        }

        var changed = false;
        foreach (var command in state.OwnerCommands)
        {
            if (command.Status != UiCommandStatus.Waiting)
            {
                continue;
            }

            // Observations never queue; they only pin the turn.
            var eligible = command.Mode == UiTurnMode.Observe
                || earliestBarrier is null
                || (command.Ticket is { } ticket && ticket <= earliestBarrier);

            if (eligible)
            {
                command.Status = UiCommandStatus.Running;
                changed = true;
            }
        }

        return changed;
    }
}

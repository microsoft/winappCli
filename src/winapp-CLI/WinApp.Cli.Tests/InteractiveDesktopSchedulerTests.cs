// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Tests;

/// <summary>
/// Deterministic, file-free coverage of the cooperative-turn state machine
/// (<see cref="InteractiveDesktopScheduler"/>, issue #764). Every rule from spec sections 10.1–10.7 is
/// asserted here against in-memory state and a fake clock, so scheduling bugs surface without needing
/// a desktop, real processes, or timing luck.
/// </summary>
[TestClass]
public class InteractiveDesktopSchedulerTests
{
    private FakeClock _clock = null!;
    private FakeLivenessProbe _probe = null!;
    private InteractiveDesktopScheduler _scheduler = null!;

    private static readonly UiOwnerIdentity OwnerA = new(UiOwnerKind.Workflow, "aaaa");
    private static readonly UiOwnerIdentity OwnerB = new(UiOwnerKind.Workflow, "bbbb");

    [TestInitialize]
    public void Setup()
    {
        _clock = new FakeClock();
        _probe = new FakeLivenessProbe();
        _scheduler = new InteractiveDesktopScheduler(_clock);
    }

    private UiParticipantIdentity Participant(int pid, string operation = "ui click")
    {
        _probe.Alive.Add((pid, pid));
        return new UiParticipantIdentity(pid, pid, operation);
    }

    // ---------------------------------------------------------------- identity and turn acquisition

    [TestMethod]
    public void BeginParticipating_OnFreeDesktop_StartsNewTurnAndRunsImmediately()
    {
        var state = InteractiveDesktopState.CreateFresh();

        var result = _scheduler.BeginParticipating(
            state, _probe, OwnerA, Participant(100), UiTurnMode.DesktopExclusive);

        Assert.AreEqual(UiAdmission.OwnerCommandRunning, result.Admission);
        Assert.AreEqual(UiTurnAction.New, result.TurnAction);
        Assert.AreEqual(OwnerA.Key, state.Owner!.Key);
        Assert.AreEqual(1, state.TurnId);
        Assert.AreEqual(1, state.OwnerCommands.Count);
        Assert.AreEqual(UiCommandStatus.Running, state.OwnerCommands[0].Status);
    }

    [TestMethod]
    public void BeginParticipating_SameOwner_JoinsExistingTurnAsContinuation()
    {
        var state = InteractiveDesktopState.CreateFresh();
        _scheduler.BeginParticipating(state, _probe, OwnerA, Participant(100, "ui record"), UiTurnMode.TurnShared);

        var result = _scheduler.BeginParticipating(
            state, _probe, OwnerA, Participant(101), UiTurnMode.DesktopExclusive);

        Assert.AreEqual(UiTurnAction.Continuation, result.TurnAction);
        Assert.AreEqual(1, state.TurnId, "joining an owned turn must not start a new one");
        Assert.AreEqual(2, state.OwnerCommands.Count);
    }

    [TestMethod]
    public void BeginParticipating_OtherOwner_QueuesGloballyWithTicket()
    {
        var state = InteractiveDesktopState.CreateFresh();
        _scheduler.BeginParticipating(state, _probe, OwnerA, Participant(100), UiTurnMode.DesktopExclusive);

        var result = _scheduler.BeginParticipating(
            state, _probe, OwnerB, Participant(200), UiTurnMode.DesktopExclusive);

        Assert.AreEqual(UiAdmission.GlobalWaiter, result.Admission);
        Assert.AreEqual(UiTurnAction.Queued, result.TurnAction);
        Assert.AreEqual(1, result.QueuePosition);
        Assert.AreEqual(1, state.Waiters.Count);
        Assert.AreEqual(UiTurnMode.DesktopExclusive, state.Waiters[0].Mode,
            "the requested mode must be persisted so any process can promote the waiter");
    }

    // ---------------------------------------------------------------------------- forward barrier

    [TestMethod]
    public void Barrier_RunningDesktopExclusive_BlocksLaterTurnSharedAndExclusive()
    {
        var state = InteractiveDesktopState.CreateFresh();
        _scheduler.BeginParticipating(state, _probe, OwnerA, Participant(100), UiTurnMode.DesktopExclusive);

        var laterShared = _scheduler.BeginParticipating(
            state, _probe, OwnerA, Participant(101, "ui record"), UiTurnMode.TurnShared);
        var laterExclusive = _scheduler.BeginParticipating(
            state, _probe, OwnerA, Participant(102), UiTurnMode.DesktopExclusive);

        Assert.AreEqual(UiAdmission.OwnerCommandWaiting, laterShared.Admission);
        Assert.AreEqual(UiAdmission.OwnerCommandWaiting, laterExclusive.Admission);
    }

    [TestMethod]
    public void Barrier_WaitingDesktopExclusive_AlsoBlocksLaterCommands()
    {
        var state = InteractiveDesktopState.CreateFresh();
        // Ticket 1 exclusive runs; ticket 2 exclusive waits behind it; ticket 3 must wait behind ticket 2
        // even though ticket 2 has not started — a pending barrier blocks just like a running one.
        _scheduler.BeginParticipating(state, _probe, OwnerA, Participant(100), UiTurnMode.DesktopExclusive);
        _scheduler.BeginParticipating(state, _probe, OwnerA, Participant(101), UiTurnMode.DesktopExclusive);

        var third = _scheduler.BeginParticipating(
            state, _probe, OwnerA, Participant(102, "ui record"), UiTurnMode.TurnShared);

        Assert.AreEqual(UiAdmission.OwnerCommandWaiting, third.Admission);
        Assert.AreEqual(UiCommandStatus.Waiting, state.OwnerCommands[1].Status);
    }

    [TestMethod]
    public void Barrier_EarlierRunningTurnShared_ContinuesAcrossALaterExclusive()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var recorder = Participant(100, "ui record");
        _scheduler.BeginParticipating(state, _probe, OwnerA, recorder, UiTurnMode.TurnShared);

        _scheduler.BeginParticipating(state, _probe, OwnerA, Participant(101), UiTurnMode.DesktopExclusive);

        var recording = InteractiveDesktopScheduler.FindOwnerCommand(state, recorder)!;
        Assert.AreEqual(UiCommandStatus.Running, recording.Status,
            "a recording that already started keeps running while same-owner input takes the desktop");
    }

    [TestMethod]
    public void Barrier_MultipleTurnShared_OverlapWhenNoExclusiveIsPending()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var first = _scheduler.BeginParticipating(
            state, _probe, OwnerA, Participant(100, "ui record"), UiTurnMode.TurnShared);
        var second = _scheduler.BeginParticipating(
            state, _probe, OwnerA, Participant(101, "ui record"), UiTurnMode.TurnShared);

        Assert.AreEqual(UiAdmission.OwnerCommandRunning, first.Admission);
        Assert.AreEqual(UiAdmission.OwnerCommandRunning, second.Admission);
    }

    [TestMethod]
    public void Barrier_ReleasesNextCommandInTicketOrderWhenTheBarrierCompletes()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var first = Participant(100);
        var second = Participant(101);
        var third = Participant(102);
        _scheduler.BeginParticipating(state, _probe, OwnerA, first, UiTurnMode.DesktopExclusive);
        _scheduler.BeginParticipating(state, _probe, OwnerA, third, UiTurnMode.DesktopExclusive);
        _scheduler.BeginParticipating(state, _probe, OwnerA, second, UiTurnMode.DesktopExclusive);

        _probe.Alive.Remove((first.ProcessId, first.StartTicksUtc));
        _scheduler.CompleteCommand(state, _probe, first, OwnerA, renewGrace: true);

        // 'third' was admitted before 'second', so it owns the smaller ticket and runs first.
        Assert.AreEqual(UiCommandStatus.Running,
            InteractiveDesktopScheduler.FindOwnerCommand(state, third)!.Status);
        Assert.AreEqual(UiCommandStatus.Waiting,
            InteractiveDesktopScheduler.FindOwnerCommand(state, second)!.Status);
    }

    // -------------------------------------------------------------------------------- observations

    [TestMethod]
    public void BeginObserve_NonOwner_RunsDetachedWithoutTouchingTheQueue()
    {
        var state = InteractiveDesktopState.CreateFresh();
        _scheduler.BeginParticipating(state, _probe, OwnerA, Participant(100), UiTurnMode.DesktopExclusive);

        var result = _scheduler.BeginObserve(state, _probe, OwnerB, Participant(200, "ui inspect"));

        Assert.AreEqual(UiAdmission.Detached, result.Admission);
        Assert.AreEqual(UiTurnAction.Detached, result.TurnAction);
        Assert.AreEqual(1, state.OwnerCommands.Count, "a detached observation must not register");
        Assert.AreEqual(0, state.Waiters.Count);
    }

    [TestMethod]
    public void BeginObserve_CurrentOwner_PinsTheTurnAndRunsImmediately()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var actor = Participant(100);
        _scheduler.BeginParticipating(state, _probe, OwnerA, actor, UiTurnMode.DesktopExclusive);
        _probe.Alive.Remove((actor.ProcessId, actor.StartTicksUtc));
        _scheduler.CompleteCommand(state, _probe, actor, OwnerA, renewGrace: true);

        var observation = Participant(101, "ui inspect");
        var result = _scheduler.BeginObserve(state, _probe, OwnerA, observation);

        Assert.AreEqual(UiAdmission.OwnerCommandRunning, result.Admission);
        Assert.IsNull(InteractiveDesktopScheduler.FindOwnerCommand(state, observation)!.Ticket,
            "observations carry no ticket, so they never act as a barrier");

        // The pin must survive past the original grace: another owner cannot take the desktop while the
        // observation is still reading transient UI.
        _clock.Advance(InteractiveDesktopScheduler.IdleGraceMs + 1_000);
        _scheduler.Normalize(state, _probe);
        Assert.AreEqual(OwnerA.Key, state.Owner!.Key);
    }

    [TestMethod]
    public void CompletingAnObservation_StartsAFreshGrace()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var actor = Participant(100);
        _scheduler.BeginParticipating(state, _probe, OwnerA, actor, UiTurnMode.DesktopExclusive);
        _probe.Alive.Remove((actor.ProcessId, actor.StartTicksUtc));
        _scheduler.CompleteCommand(state, _probe, actor, OwnerA, renewGrace: true);

        _clock.Advance(3_000);
        var observation = Participant(101, "ui inspect");
        _scheduler.BeginObserve(state, _probe, OwnerA, observation);
        _clock.Advance(5_000);
        _probe.Alive.Remove((observation.ProcessId, observation.StartTicksUtc));
        _scheduler.CompleteCommand(state, _probe, observation, OwnerA, renewGrace: true);

        _clock.Advance(InteractiveDesktopScheduler.IdleGraceMs - 100);
        _scheduler.Normalize(state, _probe);
        Assert.AreEqual(OwnerA.Key, state.Owner!.Key, "the observation's completion renewed the grace");
    }

    // ------------------------------------------------------------------------- expiry and handoff

    [TestMethod]
    public void BeginParticipating_AfterAnotherOwnersGraceExpired_ReportsHandoffAfterIdle()
    {
        // Spec §16 advertises a `handoff-after-idle` turn action. Taking over a turn that normalization
        // just released is not the same as finding a free desktop, and the two must stay distinguishable
        // in telemetry.
        var state = InteractiveDesktopState.CreateFresh();
        var actor = Participant(100);
        _scheduler.BeginParticipating(state, _probe, OwnerA, actor, UiTurnMode.DesktopExclusive);
        _probe.Alive.Remove((actor.ProcessId, actor.StartTicksUtc));
        _scheduler.CompleteCommand(state, _probe, actor, OwnerA, renewGrace: true);

        _clock.Advance(InteractiveDesktopScheduler.IdleGraceMs);

        var result = _scheduler.BeginParticipating(
            state, _probe, OwnerB, Participant(200), UiTurnMode.DesktopExclusive);

        Assert.AreEqual(UiAdmission.OwnerCommandRunning, result.Admission);
        Assert.AreEqual(UiTurnAction.HandoffAfterIdle, result.TurnAction,
            "the desktop was not free — another workflow's idle grace had just run out");
        Assert.AreEqual(OwnerB.Key, state.Owner!.Key);
    }

    [TestMethod]
    public void BeginParticipating_AfterItsOwnGraceExpired_ReportsNewRatherThanHandoff()
    {
        // The same workflow reclaiming its own lapsed turn took nothing from anybody, so it is a new
        // turn. Reporting a handoff here would make the value meaningless.
        var state = InteractiveDesktopState.CreateFresh();
        var actor = Participant(100);
        _scheduler.BeginParticipating(state, _probe, OwnerA, actor, UiTurnMode.DesktopExclusive);
        _probe.Alive.Remove((actor.ProcessId, actor.StartTicksUtc));
        _scheduler.CompleteCommand(state, _probe, actor, OwnerA, renewGrace: true);

        _clock.Advance(InteractiveDesktopScheduler.IdleGraceMs);

        var result = _scheduler.BeginParticipating(
            state, _probe, OwnerA, Participant(101), UiTurnMode.DesktopExclusive);

        Assert.AreEqual(UiTurnAction.New, result.TurnAction);
    }

    [TestMethod]
    public void BeginParticipating_OnAGenuinelyFreeDesktop_ReportsNew()
    {
        var state = InteractiveDesktopState.CreateFresh();

        var result = _scheduler.BeginParticipating(
            state, _probe, OwnerB, Participant(200), UiTurnMode.DesktopExclusive);

        Assert.AreEqual(UiTurnAction.New, result.TurnAction,
            "no previous owner existed, so nothing was handed off");
    }

    // ------------------------------------------------------------------------------- turn stamping

    [TestMethod]
    public void ClaimingATurn_StampsTheTurnStartTick()
    {
        // The turn-age telemetry bucket measures how long the workflow has held the desktop, so the
        // start tick must be written by whoever claims the turn, not derived from a per-command wait.
        _clock.Advance(5_000);
        var state = InteractiveDesktopState.CreateFresh();

        _scheduler.BeginParticipating(state, _probe, OwnerA, Participant(100), UiTurnMode.DesktopExclusive);

        Assert.AreEqual(_clock.NowTicks64, state.TurnStartedTick64);
    }

    [TestMethod]
    public void JoiningAnExistingTurn_KeepsTheOriginalStartTick()
    {
        var state = InteractiveDesktopState.CreateFresh();
        _scheduler.BeginParticipating(
            state, _probe, OwnerA, Participant(100, "ui record"), UiTurnMode.TurnShared);
        var claimedAt = state.TurnStartedTick64;

        _clock.Advance(30_000);
        _scheduler.BeginParticipating(state, _probe, OwnerA, Participant(101), UiTurnMode.DesktopExclusive);

        Assert.AreEqual(claimedAt, state.TurnStartedTick64,
            "a continuation joins the turn already in progress; its age keeps accumulating");
    }

    [TestMethod]
    public void Handoff_RestampsTheTurnStartTickForThePromotedOwner()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var actor = Participant(100);
        _scheduler.BeginParticipating(state, _probe, OwnerA, actor, UiTurnMode.DesktopExclusive);
        _scheduler.BeginParticipating(state, _probe, OwnerB, Participant(200), UiTurnMode.DesktopExclusive);

        _probe.Alive.Remove((actor.ProcessId, actor.StartTicksUtc));
        _scheduler.CompleteCommand(state, _probe, actor, OwnerA, renewGrace: true);
        _clock.Advance(InteractiveDesktopScheduler.IdleGraceMs);
        _scheduler.Normalize(state, _probe);

        Assert.AreEqual(OwnerB.Key, state.Owner!.Key);
        Assert.AreEqual(_clock.NowTicks64, state.TurnStartedTick64,
            "the promoted owner's turn starts now — it must not inherit the previous owner's age");
    }

    [TestMethod]
    public void ExpiringAnIdleTurn_ClearsTheTurnStartTick()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var actor = Participant(100);
        _scheduler.BeginParticipating(state, _probe, OwnerA, actor, UiTurnMode.DesktopExclusive);
        _probe.Alive.Remove((actor.ProcessId, actor.StartTicksUtc));
        _scheduler.CompleteCommand(state, _probe, actor, OwnerA, renewGrace: true);

        _clock.Advance(InteractiveDesktopScheduler.IdleGraceMs);
        _scheduler.Normalize(state, _probe);

        Assert.IsNull(state.Owner);
        Assert.AreEqual(0, state.TurnStartedTick64, "an unowned desktop has no turn to age");
    }

    [TestMethod]
    public void IdleTurn_ExpiresAfterExactlyFourSeconds()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var actor = Participant(100);
        _scheduler.BeginParticipating(state, _probe, OwnerA, actor, UiTurnMode.DesktopExclusive);
        _probe.Alive.Remove((actor.ProcessId, actor.StartTicksUtc));
        _scheduler.CompleteCommand(state, _probe, actor, OwnerA, renewGrace: true);

        _clock.Advance(InteractiveDesktopScheduler.IdleGraceMs - 1);
        _scheduler.Normalize(state, _probe);
        Assert.IsNotNull(state.Owner, "the turn is still reserved inside the grace");

        _clock.Advance(1);
        _scheduler.Normalize(state, _probe);
        Assert.IsNull(state.Owner, "the turn is released once the grace elapses");
    }

    [TestMethod]
    public void ActiveOwner_HasNoHardDeadline()
    {
        var state = InteractiveDesktopState.CreateFresh();
        _scheduler.BeginParticipating(
            state, _probe, OwnerA, Participant(100, "ui record"), UiTurnMode.TurnShared);
        _scheduler.BeginParticipating(state, _probe, OwnerB, Participant(200), UiTurnMode.DesktopExclusive);

        _clock.Advance(10 * 60 * 1_000);
        _scheduler.Normalize(state, _probe);

        Assert.AreEqual(OwnerA.Key, state.Owner!.Key, "a live owner command is never preempted");
        Assert.AreEqual(1, state.Waiters.Count);
    }

    [TestMethod]
    public void WaitingOwnerCommand_CountsAsActivityAndBlocksHandoff()
    {
        var state = InteractiveDesktopState.CreateFresh();
        _scheduler.BeginParticipating(state, _probe, OwnerA, Participant(100), UiTurnMode.DesktopExclusive);
        _scheduler.BeginParticipating(state, _probe, OwnerA, Participant(101), UiTurnMode.DesktopExclusive);
        _scheduler.BeginParticipating(state, _probe, OwnerB, Participant(200), UiTurnMode.DesktopExclusive);

        _clock.Advance(60_000);
        _scheduler.Normalize(state, _probe);

        Assert.AreEqual(OwnerA.Key, state.Owner!.Key);
    }

    [TestMethod]
    public void Handoff_PromotesOldestLiveWaiterAndIncrementsTurnId()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var actor = Participant(100);
        _scheduler.BeginParticipating(state, _probe, OwnerA, actor, UiTurnMode.DesktopExclusive);
        var waiterB = Participant(200);
        _scheduler.BeginParticipating(state, _probe, OwnerB, waiterB, UiTurnMode.DesktopExclusive);

        _probe.Alive.Remove((actor.ProcessId, actor.StartTicksUtc));
        _scheduler.CompleteCommand(state, _probe, actor, OwnerA, renewGrace: true);
        _clock.Advance(InteractiveDesktopScheduler.IdleGraceMs);
        _scheduler.Normalize(state, _probe);

        Assert.AreEqual(OwnerB.Key, state.Owner!.Key);
        Assert.AreEqual(2, state.TurnId);
        Assert.AreEqual(0, state.Waiters.Count);
        Assert.AreEqual(UiCommandStatus.Running,
            InteractiveDesktopScheduler.FindOwnerCommand(state, waiterB)!.Status);
    }

    [TestMethod]
    public void Handoff_SkipsDeadWaitersAndPicksTheOldestLiveTicket()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var actor = Participant(100);
        _scheduler.BeginParticipating(state, _probe, OwnerA, actor, UiTurnMode.DesktopExclusive);

        var deadWaiter = Participant(200);
        _scheduler.BeginParticipating(state, _probe, OwnerB, deadWaiter, UiTurnMode.DesktopExclusive);
        var ownerC = new UiOwnerIdentity(UiOwnerKind.Workflow, "cccc");
        var liveWaiter = Participant(300);
        _scheduler.BeginParticipating(state, _probe, ownerC, liveWaiter, UiTurnMode.DesktopExclusive);

        _probe.Alive.Remove((deadWaiter.ProcessId, deadWaiter.StartTicksUtc));
        _probe.Alive.Remove((actor.ProcessId, actor.StartTicksUtc));
        _scheduler.CompleteCommand(state, _probe, actor, OwnerA, renewGrace: true);
        _clock.Advance(InteractiveDesktopScheduler.IdleGraceMs);
        _scheduler.Normalize(state, _probe);

        Assert.AreEqual("cccc", state.Owner!.Key);
    }

    [TestMethod]
    public void ExpiredOwner_ReRegisteringGoesToTheBackOfTheQueue()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var actor = Participant(100);
        _scheduler.BeginParticipating(state, _probe, OwnerA, actor, UiTurnMode.DesktopExclusive);
        _scheduler.BeginParticipating(state, _probe, OwnerB, Participant(200), UiTurnMode.DesktopExclusive);

        _probe.Alive.Remove((actor.ProcessId, actor.StartTicksUtc));
        _scheduler.CompleteCommand(state, _probe, actor, OwnerA, renewGrace: true);
        _clock.Advance(InteractiveDesktopScheduler.IdleGraceMs);

        var result = _scheduler.BeginParticipating(
            state, _probe, OwnerA, Participant(101), UiTurnMode.DesktopExclusive);

        Assert.AreEqual(UiAdmission.GlobalWaiter, result.Admission,
            "the previous owner must not race ahead of a waiter that was already queued");
        Assert.AreEqual(OwnerB.Key, state.Owner!.Key);
    }

    // ------------------------------------------------------------------------------- grace rules

    [TestMethod]
    public void NonCancelledFailure_RenewsTheGrace()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var actor = Participant(100);
        _scheduler.BeginParticipating(state, _probe, OwnerA, actor, UiTurnMode.DesktopExclusive);
        _probe.Alive.Remove((actor.ProcessId, actor.StartTicksUtc));

        // A command that ran and returned a non-zero exit code still renews: the workflow is alive and
        // its next command is likely a retry.
        _scheduler.CompleteCommand(state, _probe, actor, OwnerA, renewGrace: true);

        _clock.Advance(InteractiveDesktopScheduler.IdleGraceMs - 1);
        _scheduler.Normalize(state, _probe);
        Assert.IsNotNull(state.Owner);
    }

    [TestMethod]
    public void Cancellation_DoesNotRenewTheGrace()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var actor = Participant(100);
        _scheduler.BeginParticipating(state, _probe, OwnerA, actor, UiTurnMode.DesktopExclusive);
        _probe.Alive.Remove((actor.ProcessId, actor.StartTicksUtc));

        _scheduler.CompleteCommand(state, _probe, actor, OwnerA, renewGrace: false);

        Assert.IsNull(state.Owner, "a cancelled command leaves no reservation behind");
    }

    [TestMethod]
    public void AnonymousOwner_ReceivesNoGraceAndHandsOffImmediately()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var anonymous = new UiOwnerIdentity(UiOwnerKind.Anonymous, "anon");
        var actor = Participant(100);
        _scheduler.BeginParticipating(state, _probe, anonymous, actor, UiTurnMode.DesktopExclusive);
        var waiter = Participant(200);
        _scheduler.BeginParticipating(state, _probe, OwnerB, waiter, UiTurnMode.DesktopExclusive);

        _probe.Alive.Remove((actor.ProcessId, actor.StartTicksUtc));
        _scheduler.CompleteCommand(state, _probe, actor, anonymous, renewGrace: true);

        Assert.AreEqual(OwnerB.Key, state.Owner!.Key,
            "a one-command owner has no shell that could issue a follow-up, so it hands off at once");
    }

    // ------------------------------------------------------------------------------ pruning rules

    [TestMethod]
    public void DeadParticipants_ArePrunedAndReleaseTheTurn()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var actor = Participant(100);
        _scheduler.BeginParticipating(state, _probe, OwnerA, actor, UiTurnMode.DesktopExclusive);

        // The process was killed: Windows deleted its DeleteOnClose lease, so it is provably gone.
        _probe.Alive.Remove((actor.ProcessId, actor.StartTicksUtc));
        _scheduler.Normalize(state, _probe);

        Assert.AreEqual(0, state.OwnerCommands.Count);
        Assert.IsNull(state.Owner, "a crash does not renew the grace, so the turn is released at once");
    }

    [TestMethod]
    public void SuspendedLiveWaiter_IsNeverPrunedAndKeepsTheHeadOfTheQueue()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var actor = Participant(100);
        _scheduler.BeginParticipating(state, _probe, OwnerA, actor, UiTurnMode.DesktopExclusive);

        // A suspended process still holds its lease. There are no heartbeats, so nothing can mistake it
        // for dead and nothing can overtake it (spec §19).
        var suspended = Participant(200);
        _scheduler.BeginParticipating(state, _probe, OwnerB, suspended, UiTurnMode.DesktopExclusive);
        var ownerC = new UiOwnerIdentity(UiOwnerKind.Workflow, "cccc");
        _scheduler.BeginParticipating(state, _probe, ownerC, Participant(300), UiTurnMode.DesktopExclusive);

        _probe.Alive.Remove((actor.ProcessId, actor.StartTicksUtc));
        _scheduler.CompleteCommand(state, _probe, actor, OwnerA, renewGrace: false);
        _clock.Advance(60_000);
        _scheduler.Normalize(state, _probe);

        Assert.AreEqual(OwnerB.Key, state.Owner!.Key,
            "the suspended waiter is alive, so it is promoted rather than skipped");
        Assert.AreEqual(1, state.Waiters.Count, "the later waiter stays queued behind it");
    }

    // -------------------------------------------------------------------------------- queue limits

    [TestMethod]
    public void QueueCap_AppliesOnlyAfterDeadWaitersArePruned()
    {
        var state = InteractiveDesktopState.CreateFresh();
        _scheduler.BeginParticipating(state, _probe, OwnerA, Participant(100), UiTurnMode.DesktopExclusive);

        var queued = new List<UiParticipantIdentity>();
        for (var i = 0; i < InteractiveDesktopScheduler.MaxGlobalWaiters; i++)
        {
            var waiter = Participant(1_000 + i);
            queued.Add(waiter);
            _scheduler.BeginParticipating(
                state, _probe, new UiOwnerIdentity(UiOwnerKind.Workflow, $"owner{i}"),
                waiter, UiTurnMode.DesktopExclusive);
        }

        Assert.ThrowsExactly<UiCoordinationException>(() => _scheduler.BeginParticipating(
            state, _probe, OwnerB, Participant(9_999), UiTurnMode.DesktopExclusive));

        // Once one waiter dies the cap frees up again, because it counts live waiters only.
        _probe.Alive.Remove((queued[0].ProcessId, queued[0].StartTicksUtc));
        var accepted = _scheduler.BeginParticipating(
            state, _probe, OwnerB, Participant(9_998), UiTurnMode.DesktopExclusive);
        Assert.AreEqual(UiAdmission.GlobalWaiter, accepted.Admission);
    }

    [TestMethod]
    public void QueueCapFailure_LeavesNoStateEntryBehind()
    {
        var state = InteractiveDesktopState.CreateFresh();
        _scheduler.BeginParticipating(state, _probe, OwnerA, Participant(100), UiTurnMode.DesktopExclusive);
        for (var i = 0; i < InteractiveDesktopScheduler.MaxGlobalWaiters; i++)
        {
            _scheduler.BeginParticipating(
                state, _probe, new UiOwnerIdentity(UiOwnerKind.Workflow, $"owner{i}"),
                Participant(1_000 + i), UiTurnMode.DesktopExclusive);
        }

        var rejected = Participant(9_999);
        try
        {
            _scheduler.BeginParticipating(state, _probe, OwnerB, rejected, UiTurnMode.DesktopExclusive);
        }
        catch (UiCoordinationException ex)
        {
            Assert.AreEqual(UiCoordinationErrorCodes.QueueCapacityExceeded, ex.Code);
        }

        Assert.IsNull(InteractiveDesktopScheduler.FindWaiter(state, rejected));
        Assert.IsNull(InteractiveDesktopScheduler.FindOwnerCommand(state, rejected));
    }

    // --------------------------------------------------------------- same-owner queue absorption

    [TestMethod]
    public void PromotedOwner_AbsorbsItsOtherQueuedCommandsInTicketOrder()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var actor = Participant(100);
        _scheduler.BeginParticipating(state, _probe, OwnerA, actor, UiTurnMode.DesktopExclusive);

        var firstB = Participant(200);
        var secondB = Participant(201);
        _scheduler.BeginParticipating(state, _probe, OwnerB, firstB, UiTurnMode.DesktopExclusive);
        _scheduler.BeginParticipating(state, _probe, OwnerB, secondB, UiTurnMode.DesktopExclusive);

        _probe.Alive.Remove((actor.ProcessId, actor.StartTicksUtc));
        _scheduler.CompleteCommand(state, _probe, actor, OwnerA, renewGrace: false);

        Assert.AreEqual(OwnerB.Key, state.Owner!.Key);
        Assert.AreEqual(0, state.Waiters.Count,
            "with no other owner queued, the whole prefix belongs to B and is absorbed");
        Assert.AreEqual(UiCommandStatus.Running,
            InteractiveDesktopScheduler.FindOwnerCommand(state, firstB)!.Status);
        Assert.AreEqual(UiCommandStatus.Waiting,
            InteractiveDesktopScheduler.FindOwnerCommand(state, secondB)!.Status,
            "the second command still queues behind its own owner's barrier");
    }

    // --------------------------------------------------------------- same-owner queue absorption

    [TestMethod]
    public void PromotedOwner_AbsorbsOnlyItsContiguousPrefixAtTheQueueHead()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var actor = Participant(100);
        _scheduler.BeginParticipating(state, _probe, OwnerA, actor, UiTurnMode.DesktopExclusive);

        // Queue order B, B, C: nothing separates the two B commands, so both may be absorbed.
        var firstB = Participant(200);
        var secondB = Participant(201);
        var ownerC = new UiOwnerIdentity(UiOwnerKind.Workflow, "cccc");
        var firstC = Participant(300);
        _scheduler.BeginParticipating(state, _probe, OwnerB, firstB, UiTurnMode.DesktopExclusive);
        _scheduler.BeginParticipating(state, _probe, OwnerB, secondB, UiTurnMode.DesktopExclusive);
        _scheduler.BeginParticipating(state, _probe, ownerC, firstC, UiTurnMode.DesktopExclusive);

        _probe.Alive.Remove((actor.ProcessId, actor.StartTicksUtc));
        _scheduler.CompleteCommand(state, _probe, actor, OwnerA, renewGrace: false);

        Assert.AreEqual(OwnerB.Key, state.Owner!.Key);
        Assert.IsNotNull(InteractiveDesktopScheduler.FindOwnerCommand(state, firstB));
        Assert.IsNotNull(InteractiveDesktopScheduler.FindOwnerCommand(state, secondB));
        Assert.IsNotNull(InteractiveDesktopScheduler.FindWaiter(state, firstC),
            "the other owner's command must stay in the global queue");
    }

    [TestMethod]
    public void PromotedOwner_StopsAbsorbingAtTheFirstDifferentOwner()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var actor = Participant(100);
        _scheduler.BeginParticipating(state, _probe, OwnerA, actor, UiTurnMode.DesktopExclusive);

        // Queue order B, C, B. Absorbing the trailing B would let it run before C even though C has the
        // older ticket, which would break strict global FIFO.
        var firstB = Participant(200);
        var ownerC = new UiOwnerIdentity(UiOwnerKind.Workflow, "cccc");
        var firstC = Participant(300);
        var secondB = Participant(201);
        _scheduler.BeginParticipating(state, _probe, OwnerB, firstB, UiTurnMode.DesktopExclusive);
        _scheduler.BeginParticipating(state, _probe, ownerC, firstC, UiTurnMode.DesktopExclusive);
        _scheduler.BeginParticipating(state, _probe, OwnerB, secondB, UiTurnMode.DesktopExclusive);

        _probe.Alive.Remove((actor.ProcessId, actor.StartTicksUtc));
        _scheduler.CompleteCommand(state, _probe, actor, OwnerA, renewGrace: false);

        Assert.AreEqual(OwnerB.Key, state.Owner!.Key);
        Assert.IsNotNull(InteractiveDesktopScheduler.FindOwnerCommand(state, firstB),
            "the head of the queue belongs to the promoted owner and is absorbed");
        Assert.IsNull(InteractiveDesktopScheduler.FindOwnerCommand(state, secondB),
            "absorption must stop at the first different owner so C keeps its earlier place");
        Assert.IsNotNull(InteractiveDesktopScheduler.FindWaiter(state, secondB));
        Assert.IsNotNull(InteractiveDesktopScheduler.FindWaiter(state, firstC));

        // And C really does get the turn next, ahead of B's second command.
        _probe.Alive.Remove((firstB.ProcessId, firstB.StartTicksUtc));
        _scheduler.CompleteCommand(state, _probe, firstB, OwnerA, renewGrace: false);
        Assert.AreEqual("cccc", state.Owner!.Key);
    }

    // -------------------------------------------------------------------------- ticket monotonicity

    [TestMethod]
    public void Tickets_AreGloballyMonotonicAcrossOwnersAndModes()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var seen = new List<long>();

        seen.Add(_scheduler.BeginParticipating(
            state, _probe, OwnerA, Participant(100), UiTurnMode.DesktopExclusive).Ticket!.Value);
        seen.Add(_scheduler.BeginParticipating(
            state, _probe, OwnerA, Participant(101, "ui record"), UiTurnMode.TurnShared).Ticket!.Value);
        seen.Add(_scheduler.BeginParticipating(
            state, _probe, OwnerB, Participant(200), UiTurnMode.DesktopExclusive).Ticket!.Value);

        CollectionAssert.AreEqual(seen.OrderBy(t => t).ToList(), seen);
        Assert.AreEqual(seen.Count, seen.Distinct().Count());
    }

    // ---------------------------------------------------------------- prior-boot deadline recovery

    [TestMethod]
    public void PriorBootDeadline_ExpiresImmediatelyInsteadOfStrandingTheTurn()
    {
        // Environment.TickCount64 restarts at reboot. A state file written after days of uptime carries
        // a deadline far beyond the new uptime; without clamping, the owner that died with the previous
        // boot would hold the desktop until the machine had been up just as long again.
        var state = InteractiveDesktopState.CreateFresh();
        state.Owner = new OwnerRecord { Kind = UiOwnerKind.Workflow, Key = OwnerA.Key };
        state.TurnId = 7;
        state.IdleExpiresTick64 = _clock.NowTicks64 + (long)TimeSpan.FromDays(5).TotalMilliseconds;

        var changed = _scheduler.Normalize(state, _probe);

        Assert.IsTrue(changed, "clamping a prior-boot deadline is a state change that must be published");
        Assert.IsNull(state.Owner, "the stranded turn must be released on the very next normalization");
    }

    [TestMethod]
    public void PriorBootDeadline_PromotesAWaitingOwnerImmediately()
    {
        var state = InteractiveDesktopState.CreateFresh();
        state.Owner = new OwnerRecord { Kind = UiOwnerKind.Workflow, Key = OwnerA.Key };
        state.TurnId = 7;
        state.NextTicket = 1;
        state.IdleExpiresTick64 = _clock.NowTicks64 + (long)TimeSpan.FromDays(5).TotalMilliseconds;

        var waiter = Participant(300);
        _scheduler.BeginParticipating(state, _probe, OwnerB, waiter, UiTurnMode.DesktopExclusive);

        Assert.AreEqual(OwnerB.Key, state.Owner!.Key, "the waiting owner takes the abandoned turn at once");
        Assert.AreEqual(UiCommandStatus.Running,
            InteractiveDesktopScheduler.FindOwnerCommand(state, waiter)!.Status);
    }

    [TestMethod]
    public void ADeadlineWithinTheGraceIsNotTreatedAsPriorBoot()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var actor = Participant(100);
        _scheduler.BeginParticipating(state, _probe, OwnerA, actor, UiTurnMode.DesktopExclusive);
        _probe.Alive.Remove((actor.ProcessId, actor.StartTicksUtc));
        _scheduler.CompleteCommand(state, _probe, actor, OwnerA, renewGrace: true);

        // Exactly the deadline a normal completion writes: now + IdleGraceMs. It must survive.
        _scheduler.Normalize(state, _probe);

        Assert.IsNotNull(state.Owner, "an ordinary in-grace deadline must never be mistaken for a stale one");
    }

    // -------------------------------------------------------- the idle deadline belongs to the owner

    [TestMethod]
    public void AForeignCompletionCannotRevokeTheCurrentOwnersGrace()
    {
        // An anonymous global waiter that gets cancelled or fails completes under its own identity.
        // Setting the deadline from that path would hand owner B's turn away instantly.
        var state = InteractiveDesktopState.CreateFresh();
        var ownerBActor = Participant(100);
        _scheduler.BeginParticipating(state, _probe, OwnerB, ownerBActor, UiTurnMode.DesktopExclusive);
        _probe.Alive.Remove((ownerBActor.ProcessId, ownerBActor.StartTicksUtc));
        _scheduler.CompleteCommand(state, _probe, ownerBActor, OwnerB, renewGrace: true);

        var graceBefore = state.IdleExpiresTick64;
        Assert.IsNotNull(state.Owner);

        var anonymous = new UiOwnerIdentity(UiOwnerKind.Anonymous, "anon");
        var stranger = Participant(200);
        _scheduler.BeginParticipating(state, _probe, anonymous, stranger, UiTurnMode.DesktopExclusive);
        _probe.Alive.Remove((stranger.ProcessId, stranger.StartTicksUtc));
        _scheduler.CompleteCommand(state, _probe, stranger, anonymous, renewGrace: false);

        Assert.AreEqual(OwnerB.Key, state.Owner!.Key, "owner B must still hold the turn");
        Assert.AreEqual(graceBefore, state.IdleExpiresTick64,
            "a non-owner completion must not touch the current owner's idle deadline");
    }

    [TestMethod]
    public void AForeignCompletionCannotExtendTheCurrentOwnersGrace()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var ownerBActor = Participant(100);
        _scheduler.BeginParticipating(state, _probe, OwnerB, ownerBActor, UiTurnMode.DesktopExclusive);
        _probe.Alive.Remove((ownerBActor.ProcessId, ownerBActor.StartTicksUtc));
        _scheduler.CompleteCommand(state, _probe, ownerBActor, OwnerB, renewGrace: true);
        var graceBefore = state.IdleExpiresTick64;

        _clock.Advance(1_000);

        // A queued command belonging to a *different* owner finishes normally.
        var stranger = Participant(200);
        _scheduler.BeginParticipating(state, _probe, OwnerA, stranger, UiTurnMode.DesktopExclusive);
        _probe.Alive.Remove((stranger.ProcessId, stranger.StartTicksUtc));
        _scheduler.CompleteCommand(state, _probe, stranger, OwnerA, renewGrace: true);

        Assert.AreEqual(graceBefore, state.IdleExpiresTick64,
            "only the current owner's own commands may extend its grace");
    }

    [TestMethod]
    public void TheCurrentOwnersOwnCompletionStillRenewsItsGrace()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var first = Participant(100);
        _scheduler.BeginParticipating(state, _probe, OwnerA, first, UiTurnMode.DesktopExclusive);
        _probe.Alive.Remove((first.ProcessId, first.StartTicksUtc));
        _scheduler.CompleteCommand(state, _probe, first, OwnerA, renewGrace: true);
        var firstDeadline = state.IdleExpiresTick64;

        _clock.Advance(1_000);
        var second = Participant(101);
        _scheduler.BeginParticipating(state, _probe, OwnerA, second, UiTurnMode.DesktopExclusive);
        _probe.Alive.Remove((second.ProcessId, second.StartTicksUtc));
        _scheduler.CompleteCommand(state, _probe, second, OwnerA, renewGrace: true);

        Assert.IsTrue(state.IdleExpiresTick64 > firstDeadline,
            "a burst from the owner keeps renewing its own grace");
    }


    [TestMethod]
    public void WorkflowOwner_KeepsFourSecondGraceAfterCompletion()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var actor = Participant(100);

        _scheduler.BeginParticipating(state, _probe, OwnerA, actor, UiTurnMode.DesktopExclusive);
        _probe.Alive.Remove((actor.ProcessId, actor.StartTicksUtc));
        _scheduler.CompleteCommand(state, _probe, actor, OwnerA, renewGrace: true);

        Assert.AreEqual(_clock.NowTicks64 + InteractiveDesktopScheduler.IdleGraceMs, state.IdleExpiresTick64);
        Assert.AreEqual(OwnerA.Key, state.Owner!.Key);
    }

    [TestMethod]
    public void AnonymousOwner_SetsDeadlineToNowOnCompletion()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var anonymous = new UiOwnerIdentity(UiOwnerKind.Anonymous, "anon-a");
        var actor = Participant(100);

        _scheduler.BeginParticipating(state, _probe, anonymous, actor, UiTurnMode.DesktopExclusive);
        _probe.Alive.Remove((actor.ProcessId, actor.StartTicksUtc));
        _scheduler.CompleteCommand(state, _probe, actor, anonymous, renewGrace: true);

        Assert.IsNull(state.Owner, "normalization should expire the now-deadline immediately");
        Assert.AreEqual(0, state.IdleExpiresTick64);
    }

    [TestMethod]
    public void UiOwnerResolver_NoWorkflowId_MintsDistinctAnonymousOwners()
    {
        var previous = Environment.GetEnvironmentVariable(UiOwnerResolver.WorkflowIdVariable);
        Environment.SetEnvironmentVariable(UiOwnerResolver.WorkflowIdVariable, null);
        try
        {
            var resolver = new UiOwnerResolver();
            var first = resolver.Resolve();
            var second = resolver.Resolve();

            Assert.AreEqual(UiOwnerKind.Anonymous, first.Kind);
            Assert.AreEqual(UiOwnerKind.Anonymous, second.Kind);
            Assert.AreNotEqual(first.Key, second.Key);
        }
        finally
        {
            Environment.SetEnvironmentVariable(UiOwnerResolver.WorkflowIdVariable, previous);
        }
    }

    [TestMethod]
    public void AnonymousTurnShared_BlocksDifferentAnonymousDesktopExclusive()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var recordingOwner = new UiOwnerIdentity(UiOwnerKind.Anonymous, "anon-record");
        var clickOwner = new UiOwnerIdentity(UiOwnerKind.Anonymous, "anon-click");

        _scheduler.BeginParticipating(state, _probe, recordingOwner, Participant(100, "ui record"), UiTurnMode.TurnShared);
        var click = _scheduler.BeginParticipating(
            state, _probe, clickOwner, Participant(200, "ui click"), UiTurnMode.DesktopExclusive);

        Assert.AreEqual(UiAdmission.GlobalWaiter, click.Admission);
        Assert.AreEqual(recordingOwner.Key, state.Owner!.Key);
    }

    [TestMethod]
    public void SameWorkflowTurnSharedAndDesktopExclusiveOverlap()
    {
        var state = InteractiveDesktopState.CreateFresh();

        var record = _scheduler.BeginParticipating(
            state, _probe, OwnerA, Participant(100, "ui record"), UiTurnMode.TurnShared);
        var click = _scheduler.BeginParticipating(
            state, _probe, OwnerA, Participant(101, "ui click"), UiTurnMode.DesktopExclusive);

        Assert.AreEqual(UiAdmission.OwnerCommandRunning, record.Admission);
        Assert.AreEqual(UiAdmission.OwnerCommandRunning, click.Admission);
        Assert.AreEqual(0, state.Waiters.Count);
    }

    [TestMethod]
    public void CurrentOwnerAffinityBeatsForeignWaiterUntilOwnerYields()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var first = Participant(100);
        _scheduler.BeginParticipating(state, _probe, OwnerA, first, UiTurnMode.DesktopExclusive);
        var foreignB = Participant(200);
        _scheduler.BeginParticipating(state, _probe, OwnerB, foreignB, UiTurnMode.DesktopExclusive);

        _probe.Alive.Remove((first.ProcessId, first.StartTicksUtc));
        _scheduler.CompleteCommand(state, _probe, first, OwnerA, renewGrace: true);

        var returning = _scheduler.BeginParticipating(
            state, _probe, OwnerA, Participant(101), UiTurnMode.DesktopExclusive);
        Assert.AreEqual(UiAdmission.OwnerCommandRunning, returning.Admission,
            "same workflow continuation joins during grace instead of queueing behind a foreign waiter");

        var ownerC = new UiOwnerIdentity(UiOwnerKind.Workflow, "cccc");
        var foreignC = Participant(300);
        _scheduler.BeginParticipating(state, _probe, ownerC, foreignC, UiTurnMode.DesktopExclusive);

        foreach (var command in state.OwnerCommands.ToArray())
        {
            _probe.Alive.Remove((command.Pid, command.ProcessStartTicksUtc));
            _scheduler.CompleteCommand(
                state,
                _probe,
                new UiParticipantIdentity(command.Pid, command.ProcessStartTicksUtc, command.Operation),
                OwnerA,
                renewGrace: false);
        }

        Assert.AreEqual(OwnerB.Key, state.Owner!.Key, "foreign waiters remain FIFO after the current owner yields");
        Assert.IsNotNull(InteractiveDesktopScheduler.FindWaiter(state, foreignC));
    }

    [TestMethod]
    public void PersistedGraceSurvivesWithoutLiveLease()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var actor = Participant(100);
        _scheduler.BeginParticipating(state, _probe, OwnerA, actor, UiTurnMode.DesktopExclusive);

        _probe.Alive.Remove((actor.ProcessId, actor.StartTicksUtc));
        _scheduler.CompleteCommand(state, _probe, actor, OwnerA, renewGrace: true);
        _scheduler.Normalize(state, _probe);

        Assert.AreEqual(OwnerA.Key, state.Owner!.Key);
        Assert.AreEqual(0, state.OwnerCommands.Count);
    }


    private sealed class FakeClock : IMonotonicClock
    {
        private long _ticks = 1_000_000;

        public long NowTicks64 => _ticks;

        public DateTimeOffset UtcNow { get; private set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public void Advance(long milliseconds)
        {
            _ticks += milliseconds;
            UtcNow = UtcNow.AddMilliseconds(milliseconds);
        }
    }

    /// <summary>
    /// Lease-backed liveness, faked. Membership in <see cref="Alive"/> stands in for "holds its
    /// DeleteOnClose lease"; there is deliberately no timestamp or heartbeat concept to fake.
    /// </summary>
    private sealed class FakeLivenessProbe : ICoordinationLivenessProbe
    {
        public HashSet<(int Pid, long Start)> Alive { get; } = [];

        public bool IsParticipantLive(int processId, long startTicksUtc)
            => Alive.Contains((processId, startTicksUtc));
    }
}

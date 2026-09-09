// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Tests;

/// <summary>
/// The scheduler half of push-based waiting: which participants a transition makes runnable, and how
/// many times it asks the OS whether a process is still alive.
/// </summary>
/// <remarks>
/// Waking the right processes is what replaces polling, and the set is derived from the state rather
/// than from each transition, so these tests pin the derivation rather than any one code path. The
/// probe counts matter for the same reason the polling did: at a deep queue the coordinator was
/// opening a process handle per waiter per transaction, which is work that scales with exactly the
/// thing that is already under pressure.
/// </remarks>
[TestClass]
public class InteractiveDesktopSignalSchedulingTests
{
    private readonly CountingLivenessProbe _probe = new();
    private InteractiveDesktopScheduler _scheduler = null!;
    private TestClock _clock = null!;

    private static readonly UiOwnerIdentity OwnerA = new(UiOwnerKind.Workflow, "aaaa");
    private static readonly UiOwnerIdentity OwnerB = new(UiOwnerKind.Workflow, "bbbb");

    [TestInitialize]
    public void Setup()
    {
        _clock = new TestClock();
        _scheduler = new InteractiveDesktopScheduler(_clock);
    }

    private UiParticipantIdentity Participant(int pid, string operation = "ui click")
    {
        _probe.Alive.Add((pid, pid));
        return new UiParticipantIdentity(pid, pid, operation);
    }

    // --------------------------------------------------------------- who a transition makes runnable

    [TestMethod]
    public void CompletingTheOwnersLastCommandMakesTheNextQueuedOwnerRunnable()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var a = Participant(100);
        var b = Participant(200);

        _scheduler.BeginParticipating(state, _probe, OwnerA, a, UiTurnMode.DesktopExclusive);
        _scheduler.BeginParticipating(state, _probe, OwnerB, b, UiTurnMode.DesktopExclusive);

        var before = InteractiveDesktopScheduler.RunnableParticipants(state);
        Assert.IsTrue(before.Contains((100, 100)), "the first owner runs immediately");
        Assert.IsFalse(before.Contains((200, 200)), "the second owner is queued behind it");

        _scheduler.CompleteCommand(state, _probe, a, OwnerA, renewGrace: false);

        var after = InteractiveDesktopScheduler.RunnableParticipants(state);
        var newlyRunnable = after.Except(before).ToList();

        Assert.HasCount(1, newlyRunnable, "exactly the promoted waiter becomes runnable");
        Assert.AreEqual((200, 200), newlyRunnable[0]);
    }

    [TestMethod]
    public void ReleasingABarrierMakesEveryBlockedCommandOfThatOwnerRunnable()
    {
        // The case a per-transition signal list would be most likely to get wrong: one completion can
        // unblock several commands at once, and waking only the first would leave the rest asleep until
        // their backstop fired.
        var state = InteractiveDesktopState.CreateFresh();
        var barrier = Participant(100);
        var shared1 = Participant(201, "ui record");
        var shared2 = Participant(202, "ui record");

        _scheduler.BeginParticipating(state, _probe, OwnerA, barrier, UiTurnMode.DesktopExclusive);
        _scheduler.BeginParticipating(state, _probe, OwnerA, shared1, UiTurnMode.TurnShared);
        _scheduler.BeginParticipating(state, _probe, OwnerA, shared2, UiTurnMode.TurnShared);

        var before = InteractiveDesktopScheduler.RunnableParticipants(state);
        Assert.IsFalse(before.Contains((201, 201)), "both shared commands wait behind the barrier");
        Assert.IsFalse(before.Contains((202, 202)));

        _scheduler.CompleteCommand(state, _probe, barrier, OwnerA, renewGrace: true);

        var newlyRunnable = InteractiveDesktopScheduler.RunnableParticipants(state).Except(before).ToList();
        CollectionAssert.AreEquivalent(
            new[] { (201, 201L), (202, 202L) },
            newlyRunnable,
            "every command the barrier released must be woken, not just the first");
    }

    [TestMethod]
    public void PruningADeadOwnerMakesTheQueuedWaiterRunnable()
    {
        // Nobody publishes anything when a process is killed, so the promotion happens inside whichever
        // participant normalizes next — and that participant must wake the winner.
        var state = InteractiveDesktopState.CreateFresh();
        var dead = Participant(100);
        var waiting = Participant(200);

        _scheduler.BeginParticipating(state, _probe, OwnerA, dead, UiTurnMode.DesktopExclusive);
        _scheduler.BeginParticipating(state, _probe, OwnerB, waiting, UiTurnMode.DesktopExclusive);

        var before = InteractiveDesktopScheduler.RunnableParticipants(state);
        _probe.Alive.Remove((100, 100));

        Assert.IsTrue(_scheduler.Normalize(state, _probe), "the dead owner must be pruned");

        var newlyRunnable = InteractiveDesktopScheduler.RunnableParticipants(state).Except(before).ToList();
        Assert.HasCount(1, newlyRunnable);
        Assert.AreEqual((200, 200), newlyRunnable[0]);
    }

    [TestMethod]
    public void AnIdleGraceExpiringMakesTheWaiterRunnable()
    {
        var state = InteractiveDesktopState.CreateFresh();
        var first = Participant(100);
        var waiting = Participant(200);

        _scheduler.BeginParticipating(state, _probe, OwnerA, first, UiTurnMode.DesktopExclusive);
        _scheduler.BeginParticipating(state, _probe, OwnerB, waiting, UiTurnMode.DesktopExclusive);
        _scheduler.CompleteCommand(state, _probe, first, OwnerA, renewGrace: true);

        // Grace still running: the waiter stays queued because owner affinity is deliberate.
        Assert.IsFalse(InteractiveDesktopScheduler.RunnableParticipants(state).Contains((200, 200)));

        var before = InteractiveDesktopScheduler.RunnableParticipants(state);
        _clock.Advance(InteractiveDesktopScheduler.IdleGraceMs + 1);
        _scheduler.Normalize(state, _probe);

        Assert.IsTrue(
            InteractiveDesktopScheduler.RunnableParticipants(state).Except(before).Contains((200, 200)),
            "an expired grace is a deadline nobody announces, so the waiter that wakes at it must find itself runnable");
    }

    [TestMethod]
    public void ARegisteringObservationIsReportedAsNewlyRunnable()
    {
        // An observation is Running the moment it registers, so it does appear in the difference. That
        // is correct rather than wasteful: the entry is a real change to who may act on the desktop.
        // Nobody is woken for it, because PublishAndSignal skips the participant doing the publishing —
        // which for a registration is always the observation itself.
        var state = InteractiveDesktopState.CreateFresh();
        var owner = Participant(100);
        _scheduler.BeginParticipating(state, _probe, OwnerA, owner, UiTurnMode.DesktopExclusive);

        var before = InteractiveDesktopScheduler.RunnableParticipants(state);
        var observer = Participant(300, "ui inspect");
        _scheduler.BeginObserve(state, _probe, OwnerA, observer);

        var newlyRunnable = InteractiveDesktopScheduler.RunnableParticipants(state).Except(before).ToList();
        Assert.HasCount(1, newlyRunnable, "the observation is the only change");
        Assert.AreEqual((300, 300), newlyRunnable[0]);
    }

    // ------------------------------------------------------------------------ liveness probe economy

    [TestMethod]
    public void AdmissionProbesEachParticipantOnceRatherThanOncePerCheck()
    {
        // Admission used to prune, then re-count live waiters, then compute a queue position — asking
        // the OS about the same processes three times inside one transaction. With 64 waiters that is
        // 192 process handles per admitted command.
        var state = InteractiveDesktopState.CreateFresh();
        _scheduler.BeginParticipating(state, _probe, OwnerA, Participant(100), UiTurnMode.DesktopExclusive);
        for (var i = 0; i < 20; i++)
        {
            _scheduler.BeginParticipating(
                state, _probe, new UiOwnerIdentity(UiOwnerKind.Workflow, $"owner{i}"),
                Participant(1_000 + i), UiTurnMode.DesktopExclusive);
        }

        _probe.Reset();
        var afterSetup = _probe.Calls;
        _scheduler.Normalize(state, _probe);
        var afterNormalize = _probe.Calls;
        _scheduler.BeginParticipating(state, _probe, OwnerB, Participant(9_999), UiTurnMode.DesktopExclusive);
        var afterAdmission = _probe.Calls;

        // 21 existing participants: one command entry plus 20 waiters, each asked about exactly once by
        // the prune inside Normalize. Everything after that reads the pruned lists.
        Assert.AreEqual(21, afterNormalize - afterSetup,
            $"one normalization must probe each participant once; it probed {afterNormalize - afterSetup}");
        Assert.AreEqual(21, afterAdmission - afterNormalize,
            $"admission must probe each participant once; it probed {afterAdmission - afterNormalize}");
    }

    [TestMethod]
    public void PromotionReusesThePrunedQueueRatherThanReprobingIt()
    {
        // Promotion is the path that re-walked the queue asking the OS about each waiter again. It runs
        // inside the completion that frees the turn, so that is the transaction to measure.
        var state = InteractiveDesktopState.CreateFresh();
        var owner = Participant(100);
        _scheduler.BeginParticipating(state, _probe, OwnerA, owner, UiTurnMode.DesktopExclusive);
        for (var i = 0; i < 10; i++)
        {
            _scheduler.BeginParticipating(
                state, _probe, new UiOwnerIdentity(UiOwnerKind.Workflow, $"owner{i}"),
                Participant(2_000 + i), UiTurnMode.DesktopExclusive);
        }

        _probe.Reset();
        _scheduler.CompleteCommand(state, _probe, owner, OwnerA, renewGrace: false);

        Assert.IsNotNull(state.Owner, "completing the only owner command must promote a waiting owner");
        Assert.AreEqual(10, _probe.Calls,
            $"promotion must reuse the list the prune just produced; it probed {_probe.Calls} times");
    }

    [TestMethod]
    public void QueuePositionStillProbesWhenNothingHasNormalized()
    {
        // Cancellation teardown reads a position without normalizing first, so there the probe is the
        // only thing that can tell a live queue from a stale one.
        var state = InteractiveDesktopState.CreateFresh();
        _scheduler.BeginParticipating(state, _probe, OwnerA, Participant(100), UiTurnMode.DesktopExclusive);
        var first = Participant(200);
        var second = Participant(300);
        _scheduler.BeginParticipating(state, _probe, OwnerB, first, UiTurnMode.DesktopExclusive);
        var admission = _scheduler.BeginParticipating(
            state, _probe, new UiOwnerIdentity(UiOwnerKind.Workflow, "cccc"), second, UiTurnMode.DesktopExclusive);

        var ticket = admission.Ticket!.Value;
        Assert.AreEqual(2, InteractiveDesktopScheduler.QueuePositionOf(state, _probe, ticket));

        // The waiter ahead dies without anyone normalizing: the probing overload must notice.
        _probe.Alive.Remove((200, 200));
        Assert.AreEqual(1, InteractiveDesktopScheduler.QueuePositionOf(state, _probe, ticket),
            "a stale queue must not inflate the reported position");

        // The non-probing overload is for callers that already normalized, and counts entries as-is.
        Assert.AreEqual(2, InteractiveDesktopScheduler.QueuePositionOf(state, ticket));
    }

    private sealed class CountingLivenessProbe : ICoordinationLivenessProbe
    {
        public HashSet<(int Pid, long Start)> Alive { get; } = [];

        public int Calls { get; private set; }

        public void Reset() => Calls = 0;

        public bool IsParticipantLive(int processId, long startTicksUtc)
        {
            Calls++;
            return Alive.Contains((processId, startTicksUtc));
        }
    }

    private sealed class TestClock : IMonotonicClock
    {
        private long _now = 1_000_000;

        public long NowTicks64 => _now;

        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch.AddMilliseconds(_now);

        public void Advance(long ms) => _now += ms;
    }
}

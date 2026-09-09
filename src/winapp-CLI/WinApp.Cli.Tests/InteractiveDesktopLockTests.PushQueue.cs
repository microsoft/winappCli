// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Tests;

/// <summary>
/// How a queued command sleeps and what wakes it, over the real store, leases and file locks.
/// </summary>
/// <remarks>
/// Waiting used to be a 50-75 ms poll, so every one of these questions had the same boring answer:
/// the command would notice within a poll. Now a waiter can sleep for seconds, so what wakes it — and
/// what happens when nothing does — is the behavior worth pinning down.
/// </remarks>
public partial class InteractiveDesktopLockTests
{
    /// <summary>Waits for a condition without pinning the schedule to wall-clock luck.</summary>
    private static async Task<bool> EventuallyAsync(Func<bool> condition, int timeoutMs = 15_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    // ----------------------------------------------------------------------------- what wakes a waiter

    [TestMethod]
    public async Task CompletingACommandWakesTheWaiterItPromoted()
    {
        // The ordinary handoff. Without this the waiter would sit until a recovery deadline, which is
        // the difference between a script that flows and one that stutters for half a second per step.
        using var foreignLease = OccupyTurnWithAnotherOwner();

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = RunAsyncWithToken(UiTurnMode.DesktopExclusive, "ui click", (_, _) =>
        {
            started.SetResult();
            return Task.FromResult(0);
        }, CancellationToken.None);

        Assert.IsTrue(
            await EventuallyAsync(() => _signals.RequestedTimeouts.Count > 0),
            "the command should be waiting on its signal rather than polling");
        Assert.IsFalse(started.Task.IsCompleted);

        // Releasing the foreign lease is what a crash looks like; the waiter recovers it itself.
        foreignLease.Dispose();

        Assert.AreEqual(0, await queued);
        Assert.IsTrue(started.Task.IsCompleted);
    }

    [TestMethod]
    public async Task AWakeUpDeliveredBeforeTheWaitIsNotLost()
    {
        // The race the auto-reset event exists to close: a promoter can publish and signal in the window
        // between a waiter reading state and actually waiting. A latch that only released waiters
        // already parked would strand that command until its backstop fired.
        var signal = _signals.Create(4242, 99);

        _signals.Signal(4242, 99);

        var woken = await signal.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);
        Assert.IsTrue(woken, "a signal delivered before the wait must still release it");

        // ...and it is consumed, not sticky: one signal is one wake-up.
        var again = await signal.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);
        Assert.IsFalse(again, "the latch must auto-reset so a stale signal cannot wake the next wait too");
    }

    [TestMethod]
    public async Task AStaleWakeUpCannotStartACommandThatIsStillQueued()
    {
        // Signals are hints, not authority. A duplicate or misdirected wake must cost one state read
        // and nothing else — never a command running out of turn on somebody else's desktop.
        using var foreignLease = OccupyTurnWithAnotherOwner();

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = RunAsyncWithToken(UiTurnMode.DesktopExclusive, "ui click", (_, _) =>
        {
            started.SetResult();
            return Task.FromResult(0);
        }, CancellationToken.None);

        Assert.IsTrue(await EventuallyAsync(() => _signals.RequestedTimeouts.Count > 0));

        var self = new UiParticipantIdentity(Environment.ProcessId, ProcessStartTicks(), "ui click");
        for (var i = 0; i < 5; i++)
        {
            _signals.SignalDirect(self);
            await Task.Delay(30);
        }

        Assert.IsFalse(
            started.Task.IsCompleted,
            "the turn is still held, so no number of wake-ups may let this command run");

        foreignLease.Dispose();
        Assert.AreEqual(0, await queued);
    }

    // --------------------------------------------------------------------------- the recovery schedule

    [TestMethod]
    public async Task TheQueueHeadRechecksBrisklyBecauseNobodyMayBeAliveToWakeIt()
    {
        using var foreignLease = OccupyTurnWithAnotherOwner();

        using var cts = new CancellationTokenSource();
        var queued = RunAsyncWithToken(
            UiTurnMode.DesktopExclusive, "ui click", (_, _) => Task.FromResult(0), cts.Token);

        Assert.IsTrue(await EventuallyAsync(() => _signals.RequestedTimeouts.Count > 0));

        var firstTimeout = _signals.RequestedTimeouts[0];
        Assert.AreEqual(
            InteractiveDesktopLock.HeadRecoveryMs,
            firstTimeout.TotalMilliseconds,
            $"the only waiter is the head and must take the short interval; it asked for {firstTimeout}");

        await cts.CancelAsync();
        Assert.AreEqual(InteractiveDesktopLock.CancelledExitCode, await queued);
    }

    [TestMethod]
    public async Task AWaiterBehindTheHeadSleepsOnTheLongBackstop()
    {
        // Someone else is the head, so this command's turn cannot come before theirs — and they will be
        // woken and will wake this one in turn. Its own deadline only covers a promoter that died
        // between publishing and signalling, which is why it is ten times longer.
        using var foreignLease = OccupyTurnWithAnotherOwner();
        using var headLease = QueueForeignWaiterAhead(foreignPid: 515151, foreignStart: 123123);

        using var cts = new CancellationTokenSource();
        var queued = RunAsyncWithToken(
            UiTurnMode.DesktopExclusive, "ui click", (_, _) => Task.FromResult(0), cts.Token);

        Assert.IsTrue(await EventuallyAsync(() => _signals.RequestedTimeouts.Count > 0));

        var firstTimeout = _signals.RequestedTimeouts[0];
        Assert.AreEqual(
            InteractiveDesktopLock.DeepRecoveryMs,
            firstTimeout.TotalMilliseconds,
            $"a waiter behind the head must not recheck at head cadence; it asked for {firstTimeout}");

        await cts.CancelAsync();
        Assert.AreEqual(InteractiveDesktopLock.CancelledExitCode, await queued);
    }

    [TestMethod]
    public async Task AQuietWaiterDoesNotWakeUpRepeatedlyToSayNothing()
    {
        // The measurable half of the change: with no status line to print and no signal to consume, a
        // deep waiter should be asleep, not spinning. Under the old poll this window held ~30 wake-ups.
        using var foreignLease = OccupyTurnWithAnotherOwner();
        using var headLease = QueueForeignWaiterAhead(foreignPid: 626262, foreignStart: 456456);

        using var cts = new CancellationTokenSource();
        var queued = RunAsyncWithToken(
            UiTurnMode.DesktopExclusive, "ui click", (_, _) => Task.FromResult(0), cts.Token);

        Assert.IsTrue(await EventuallyAsync(() => _signals.RequestedTimeouts.Count > 0));
        await Task.Delay(1_500);

        Assert.IsLessThanOrEqualTo(
            2,
            _signals.RequestedTimeouts.Count,
            $"a quiet deep waiter must sleep rather than poll; it woke {_signals.RequestedTimeouts.Count} times in 1.5s");

        await cts.CancelAsync();
        Assert.AreEqual(InteractiveDesktopLock.CancelledExitCode, await queued);
    }

    [TestMethod]
    public async Task CancellingAQueuedCommandWakesTheParticipantsItLeavesBehind()
    {
        // Cancellation removes an entry, which can be exactly what lets someone else through, so the
        // teardown path has to wake people just like an ordinary completion does.
        using var foreignLease = OccupyTurnWithAnotherOwner();

        using var cts = new CancellationTokenSource();
        var queued = RunAsyncWithToken(
            UiTurnMode.DesktopExclusive, "ui click", (_, _) => Task.FromResult(0), cts.Token);

        Assert.IsTrue(await EventuallyAsync(() => _signals.RequestedTimeouts.Count > 0));
        await cts.CancelAsync();
        Assert.AreEqual(InteractiveDesktopLock.CancelledExitCode, await queued);

        using var stateLock = _store.AcquireStateLock(CancellationToken.None);
        var state = _store.Read().State!;
        Assert.IsFalse(
            state.Waiters.Any(w => w.Pid == Environment.ProcessId),
            "a cancelled command must leave no queue entry behind for others to prune");
    }

    [TestMethod]
    public async Task AQuietWaiterStillRecordsQueueGrowthThatHappensAfterItRegisters()
    {
        // The telemetry summary reports the deepest queue a command ever saw. Sampling that next to the
        // status line made it depend on whether anyone was watching: under --json and --quiet no status
        // line is ever due, so the depth froze at whatever it was when the command registered and every
        // command that piled up behind it went unrecorded — exactly the runs where queue depth is worth
        // knowing.
        using var foreignLease = OccupyTurnWithAnotherOwner();

        UiCoordinationTelemetryScope.Begin();

        using var cts = new CancellationTokenSource();
        var queued = _coordinator.RunCoordinatedAsync(
            UiTurnMode.DesktopExclusive, "ui click", UiCoordinationTestParse.Quiet(),
            (_, _) => Task.FromResult(0), cts.Token);

        Assert.IsTrue(
            await EventuallyAsync(() => _signals.RequestedTimeouts.Count > 0),
            "the command must be waiting before the queue grows behind it");

        // Three more waiters arrive after this command registered, so a depth captured only at
        // registration would report one rather than four.
        using var second = QueueForeignWaiterAhead(foreignPid: 717171, foreignStart: 717);
        using var third = QueueForeignWaiterAhead(foreignPid: 727272, foreignStart: 727);
        using var fourth = QueueForeignWaiterAhead(foreignPid: 737373, foreignStart: 737);

        // Wake it so it takes another look; the wake is a hint, and the look is what samples the depth.
        _signals.SignalDirect(new UiParticipantIdentity(
            Environment.ProcessId, new ProcessInspector().CurrentProcessStartTicksUtc, "ui click"));

        Assert.IsTrue(
            await EventuallyAsync(() => _signals.RequestedTimeouts.Count > 1),
            "the waiter must have looked again after the queue grew");

        await cts.CancelAsync();
        Assert.AreEqual(InteractiveDesktopLock.CancelledExitCode, await queued);

        var summary = UiCoordinationTelemetryScope.Current;
        Assert.IsNotNull(summary);
        Assert.IsGreaterThanOrEqualTo(
            4,
            summary!.QueueDepth,
            $"a quiet waiter must still observe the queue growing behind it; it recorded {summary.QueueDepth}");
    }

    /// <summary>
    /// Adds a live foreign waiter ahead of this process, so the test process is deliberately not the
    /// head of the queue.
    /// </summary>
    private FileStream QueueForeignWaiterAhead(int foreignPid, long foreignStart)
    {
        _paths.EnsureDirectories();
        var leaseStream = new FileStream(
            _paths.LeasePath(foreignPid, foreignStart),
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.DeleteOnClose);

        using var stateLock = _store.AcquireStateLock(CancellationToken.None);
        var state = _store.Read().State!;

        // Tickets must be unique and at least 1, and nextTicket must stay above every ticket in use —
        // the store rejects state that breaks either, because ambiguous tickets would make both the
        // forward barrier and global FIFO order undefined. Allocating properly is also what puts this
        // waiter genuinely ahead of the command the test starts next.
        var ticket = state.NextTicket;
        state.NextTicket = ticket + 1;
        state.Waiters.Add(new WaiterEntry
        {
            Ticket = ticket,
            OwnerKey = "yet-another-workflow",
            OwnerKind = UiOwnerKind.Workflow,
            Pid = foreignPid,
            ProcessStartTicksUtc = foreignStart,
            Operation = "ui click",
            Mode = UiTurnMode.DesktopExclusive,
        });
        _store.Publish(state);

        return leaseStream;
    }

    private static long ProcessStartTicks() => new ProcessInspector().CurrentProcessStartTicksUtc;
}

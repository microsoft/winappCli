// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Tests;

using WinApp.Cli.Services.InteractiveDesktop;

/// <summary>
/// Recovery across real processes, where the wake-up nobody sends is the interesting case.
/// </summary>
/// <remarks>
/// <para>
/// Waiting is push-based, so an ordinary handoff is somebody publishing and then signalling. None of
/// that happens when a process is killed: there is no completion to publish and nobody left to send a
/// signal. These tests kill real <c>winapp.exe</c> processes inside the queue and require the
/// survivors to get the desktop anyway, which is what the recovery deadlines exist for.
/// </para>
/// <para>
/// The turn holder is this process rather than a child, because a child that can be killed also has
/// to be a child that stays in coordination, and a command that reaches its turn without a live target
/// window finishes immediately. The killed-<em>owner</em> case is covered where it can be made
/// deterministic instead: <c>InteractiveDesktopLockTests</c> closes an owner's lease, which is exactly
/// what Windows does when that process dies.
/// </para>
/// <para>
/// Timings are generous upper bounds. The claim under test is that recovery happens without anyone
/// being told, not that it happens on a particular schedule.
/// </para>
/// </remarks>
public partial class InteractiveDesktopMultiprocessTests
{
    [TestMethod]
    public async Task KillingTheQueueHeadLetsTheNextWaiterThrough()
    {
        // The head is what everyone behind it is effectively waiting on, and the one position where a
        // corpse blocks the whole queue until somebody prunes it.
        var holderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHolder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holder = _coordinator.RunCoordinatedAsync(
            UiTurnMode.DesktopExclusive, "ui click", UiCoordinationTestParse.Quiet(),
            async (_, _) =>
            {
                holderStarted.SetResult();
                await releaseHolder.Task;
                return 0;
            },
            CancellationToken.None);

        await holderStarted.Task;

        var head = StartQueuedClick("multiprocess-killed-head-first");
        Assert.IsTrue(
            await WaitForStateAsync(s => s.Waiters.Any(w => w.Pid == head.Id)),
            "the first child must be queued before the second arrives");

        var behind = StartQueuedClick("multiprocess-killed-head-second");
        Assert.IsTrue(
            await WaitForStateAsync(s => s.Waiters.Count == 2),
            "both children must be queued before the head is killed");

        head.Kill(entireProcessTree: true);
        releaseHolder.SetResult();
        await holder;

        Assert.IsTrue(
            behind.WaitForExit(45_000),
            "the surviving waiter must prune the dead head and take the turn without being told");

        // The app does not exist, so the command fails after acquiring its turn — reaching that failure
        // is the proof that it got the desktop.
        Assert.AreEqual(1, behind.ExitCode);
        Assert.IsTrue(
            await WaitForStateAsync(s => s.Waiters.Count == 0 && s.OwnerCommands.Count == 0),
            "neither the corpse nor the survivor may be left in the queue");
    }

    [TestMethod]
    public async Task ADeadWaiterDoesNotHoldQueueCapacityAgainstNewCommands()
    {
        // What the cap counts, demonstrated rather than read off the constant: live foreign waiters. A
        // process that started and died is not one, however recently its entry was written.
        var holderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHolder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holder = _coordinator.RunCoordinatedAsync(
            UiTurnMode.DesktopExclusive, "ui click", UiCoordinationTestParse.Quiet(),
            async (_, _) =>
            {
                holderStarted.SetResult();
                await releaseHolder.Task;
                return 0;
            },
            CancellationToken.None);

        await holderStarted.Task;

        var doomed = StartQueuedClick("multiprocess-cap-doomed");
        Assert.IsTrue(await WaitForStateAsync(s => s.Waiters.Any(w => w.Pid == doomed.Id)));

        doomed.Kill(entireProcessTree: true);

        var arriving = StartQueuedClick("multiprocess-cap-arriving");
        Assert.IsTrue(
            await WaitForStateAsync(s => s.Waiters.Any(w => w.Pid == arriving.Id)),
            "a new command must still be admitted while a dead entry is nominally in the queue");

        Assert.IsTrue(
            await WaitForStateAsync(s => s.Waiters.All(w => w.Pid != doomed.Id)),
            "the dead waiter's entry must be pruned rather than left occupying a slot");

        releaseHolder.SetResult();
        await holder;

        Assert.IsTrue(arriving.WaitForExit(45_000), "the live waiter must still get its turn");
        Assert.AreEqual(1, arriving.ExitCode);
    }
}

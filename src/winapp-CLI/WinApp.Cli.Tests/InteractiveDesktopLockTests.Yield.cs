// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Tests;

/// <summary>
/// Explicit early release (<c>winapp ui yield</c>) and the turn-sharing behaviour of background-safe
/// mutations, over the real store, leases and file locks.
/// </summary>
public partial class InteractiveDesktopLockTests
{
    // ------------------------------------------------- background-safe mutations take a shared turn

    [TestMethod]
    public async Task ABackgroundSafeMutationWaitsWhileAnotherWorkflowOwnsTheDesktop()
    {
        // set-value and scroll --direction never take active.lock, but they do change what the app
        // shows, so they must not run underneath another workflow's click.
        using var foreignLease = OccupyTurnWithAnotherOwner();

        using var cts = new CancellationTokenSource();
        var ran = false;
        var queued = RunAsyncWithToken(UiTurnMode.TurnShared, "ui set-value", (_, _) =>
        {
            ran = true;
            return Task.FromResult(0);
        }, cts.Token);

        await Task.Delay(250);
        Assert.IsFalse(ran, "a background-safe mutation must wait behind a foreign workflow's turn");

        using (var stateLock = _store.AcquireStateLock(CancellationToken.None))
        {
            Assert.AreEqual(1, _store.Read().State!.Waiters.Count,
                "it must be recorded as a global waiter, not run detached");
        }

        await cts.CancelAsync();
        await queued;
    }

    [TestMethod]
    public async Task ABackgroundSafeMutationOverlapsWithItsOwnWorkflowsRecording()
    {
        // The point of TurnShared rather than DesktopExclusive: a recording and the set-value it is
        // recording belong to one workflow and must run at the same time. The recording is seeded as a
        // live foreign process under this same workflow, because participant identity is
        // (pid, processStartTicks) and one test process cannot hold two leases.
        using var recordingLease = OccupyTurnWithThisWorkflow(UiTurnMode.TurnShared, "ui record");

        var mutationRan = false;
        var mutation = RunAsync(UiTurnMode.TurnShared, "ui set-value", (_, _) =>
        {
            mutationRan = true;
            return Task.FromResult(0);
        });

        Assert.AreEqual(0, await mutation.WaitAsync(TimeSpan.FromSeconds(5)),
            "the same workflow's mutation must not wait for its own recording to finish");
        Assert.IsTrue(mutationRan);
    }

    [TestMethod]
    public async Task ABackgroundSafeMutationStillWaitsBehindItsOwnWorkflowsExclusiveBarrier()
    {
        // Same workflow, but an exclusive command is already holding the barrier. Sharing the turn does
        // not mean ignoring the barrier: the mutation must queue behind it like any later command.
        using var clickLease = OccupyTurnWithThisWorkflow(UiTurnMode.DesktopExclusive, "ui click");

        using var cts = new CancellationTokenSource();
        var ran = false;
        var queued = RunAsyncWithToken(UiTurnMode.TurnShared, "ui set-value", (_, _) =>
        {
            ran = true;
            return Task.FromResult(0);
        }, cts.Token);

        await Task.Delay(250);
        Assert.IsFalse(ran, "an exclusive command of the same workflow is still a forward barrier");

        await cts.CancelAsync();
        await queued;
    }

    // ------------------------------------------------------------------------ ui yield: rejection

    [TestMethod]
    public void Yield_WithNoWorkflowId_IsRejectedWithoutReadingState()
    {
        Environment.SetEnvironmentVariable(UiOwnerResolver.WorkflowIdVariable, null);

        Assert.AreEqual(UiYieldResult.NotAWorkflow, _coordinator.ReleaseIdleTurn(CancellationToken.None));
        Assert.IsFalse(File.Exists(_paths.StatePath),
            "an anonymous caller holds no turn by construction, so state must not be touched to prove it");
    }

    [TestMethod]
    public void Yield_WithABlankWorkflowId_StillReportsTheMalformedValue()
    {
        // Absence and malformation are different mistakes. An unset variable means "I am a one-shot"
        // and is rejected locally as invalid_arguments; a variable that expanded to "" is a scripting
        // bug, and must keep reporting invalid_ui_workflow_id through the ordinary resolver path.
        Environment.SetEnvironmentVariable(UiOwnerResolver.WorkflowIdVariable, "   ");

        var ex = Assert.ThrowsExactly<UiCoordinationException>(
            () => _coordinator.ReleaseIdleTurn(CancellationToken.None));
        Assert.AreEqual(UiCoordinationErrorCodes.InvalidWorkflowId, ex.Code);
    }

    // ------------------------------------------------------------------------- ui yield: releasing

    [TestMethod]
    public async Task Yield_ReleasesThisWorkflowsIdleTurn()
    {
        // A completed command leaves the turn owned and idle for the grace; yield ends that early.
        await RunAsync(UiTurnMode.DesktopExclusive, "ui click", (_, _) => Task.FromResult(0));

        using (var beforeLock = _store.AcquireStateLock(CancellationToken.None))
        {
            var before = _store.Read().State!;
            Assert.IsNotNull(before.Owner, "the workflow keeps the turn through its idle grace");
            Assert.AreEqual(0, before.OwnerCommands.Count);
        }

        Assert.AreEqual(UiYieldResult.Released, _coordinator.ReleaseIdleTurn(CancellationToken.None));

        using var afterLock = _store.AcquireStateLock(CancellationToken.None);
        var after = _store.Read().State!;
        Assert.IsNull(after.Owner, "the turn must be free immediately, not at the end of the grace");
        Assert.AreEqual(0, after.IdleExpiresTick64);
    }

    [TestMethod]
    public async Task Yield_PromotesAndWakesTheWaitingWorkflow()
    {
        await RunAsync(UiTurnMode.DesktopExclusive, "ui click", (_, _) => Task.FromResult(0));

        // A foreign workflow queued behind this one's idle grace.
        const int WaitingPid = 515151;
        const long WaitingStart = 424242424;
        using var waitingLease = OpenForeignLease(WaitingPid, WaitingStart);
        using (var stateLock = _store.AcquireStateLock(CancellationToken.None))
        {
            var state = _store.Read().State!;
            state.Waiters.Add(new WaiterEntry
            {
                Ticket = state.AllocateTicket(),
                OwnerKey = "a-waiting-workflow",
                OwnerKind = UiOwnerKind.Workflow,
                Pid = WaitingPid,
                ProcessStartTicksUtc = WaitingStart,
                Operation = "ui click",
                Mode = UiTurnMode.DesktopExclusive,
            });
            _store.Publish(state);
        }

        _signals.Signalled.Clear();
        Assert.AreEqual(UiYieldResult.Released, _coordinator.ReleaseIdleTurn(CancellationToken.None));

        using var afterLock = _store.AcquireStateLock(CancellationToken.None);
        var after = _store.Read().State!;
        Assert.AreEqual("a-waiting-workflow", after.Owner!.Key, "the waiter must take the turn");
        Assert.AreEqual(1, after.OwnerCommands.Count);
        Assert.AreEqual(UiCommandStatus.Running, after.OwnerCommands[0].Status);
        CollectionAssert.Contains(
            _signals.Signalled, (WaitingPid, WaitingStart),
            "the promoted waiter must be woken rather than left to time out");
    }

    [TestMethod]
    public async Task Yield_Twice_IsIdempotent()
    {
        await RunAsync(UiTurnMode.DesktopExclusive, "ui click", (_, _) => Task.FromResult(0));

        Assert.AreEqual(UiYieldResult.Released, _coordinator.ReleaseIdleTurn(CancellationToken.None));
        Assert.AreEqual(UiYieldResult.NothingHeld, _coordinator.ReleaseIdleTurn(CancellationToken.None),
            "yielding an already-released turn is the normal end of a script, not a failure");
    }

    // ---------------------------------------------------------------------------- ui yield: no-ops

    [TestMethod]
    public void Yield_WhenNobodyOwnsTheTurn_IsANoOp()
        => Assert.AreEqual(UiYieldResult.NothingHeld, _coordinator.ReleaseIdleTurn(CancellationToken.None));

    [TestMethod]
    public void Yield_NeverReleasesAnotherWorkflowsTurn()
    {
        using var foreignLease = OccupyTurnWithAnotherOwner();

        Assert.AreEqual(UiYieldResult.NothingHeld, _coordinator.ReleaseIdleTurn(CancellationToken.None));

        using var stateLock = _store.AcquireStateLock(CancellationToken.None);
        var state = _store.Read().State!;
        Assert.AreEqual("some-other-workflow", state.Owner!.Key, "the other workflow must keep its turn");
        Assert.AreEqual(1, state.OwnerCommands.Count, "and keep its running command");
    }

    [TestMethod]
    public void Yield_PublishesPruningEvenWhenItReleasesNothing()
    {
        // A released:false branch that still changed state. The dead waiter's lease was never opened,
        // so normalization prunes it; if yield returned without publishing, that pruning would be
        // silently discarded and every later reader would keep paying for the phantom entry.
        using (var stateLock = _store.AcquireStateLock(CancellationToken.None))
        {
            var state = InteractiveDesktopState.CreateFresh();
            state.NextTicket = 2;
            state.Waiters.Add(new WaiterEntry
            {
                Ticket = 1,
                OwnerKey = "a-dead-workflow",
                OwnerKind = UiOwnerKind.Workflow,
                Pid = 818181,
                ProcessStartTicksUtc = 777888999,
                Operation = "ui click",
                Mode = UiTurnMode.DesktopExclusive,
            });
            _store.Publish(state);
        }

        Assert.AreEqual(UiYieldResult.NothingHeld, _coordinator.ReleaseIdleTurn(CancellationToken.None));

        using var afterLock = _store.AcquireStateLock(CancellationToken.None);
        var after = _store.Read().State!;
        Assert.AreEqual(0, after.Waiters.Count, "the pruning yield performed must be published, not dropped");
        Assert.IsNull(after.Owner, "a dead waiter must not be promoted into ownership");
    }

    // ------------------------------------------------------------------------------ ui yield: busy

    [TestMethod]
    public async Task Yield_WhileThisWorkflowStillHasALiveCommand_RefusesAsBusy()
    {
        var commandStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCommand = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var running = RunAsync(UiTurnMode.TurnShared, "ui record", async (_, _) =>
        {
            commandStarted.SetResult();
            await releaseCommand.Task;
            return 0;
        });

        await commandStarted.Task;

        Assert.AreEqual(UiYieldResult.Busy, _coordinator.ReleaseIdleTurn(CancellationToken.None),
            "the turn is not idle while this workflow is still driving it");

        using (var stateLock = _store.AcquireStateLock(CancellationToken.None))
        {
            var state = _store.Read().State!;
            Assert.IsNotNull(state.Owner, "a refused yield must leave the turn exactly as it was");
            Assert.AreEqual(1, state.OwnerCommands.Count);
        }

        releaseCommand.SetResult();
        Assert.AreEqual(0, await running);
    }

    // --------------------------------------------------------------------- ui yield: normalization

    [TestMethod]
    public void Yield_NormalizesACrashedOwnerAndPromotesTheNextWorkflow()
    {
        // The dead owner's lease is never opened, so it reads as crashed. Yield's own transaction has
        // to publish that normalization even though this workflow had nothing of its own to release.
        const int DeadPid = 606060;
        const long DeadStart = 111222333;
        const int WaitingPid = 707070;
        const long WaitingStart = 444555666;

        using var waitingLease = OpenForeignLease(WaitingPid, WaitingStart);
        using (var stateLock = _store.AcquireStateLock(CancellationToken.None))
        {
            var state = InteractiveDesktopState.CreateFresh();
            state.TurnId = 1;
            state.NextTicket = 3;
            state.Owner = new OwnerRecord { Kind = UiOwnerKind.Workflow, Key = "a-crashed-workflow" };
            state.OwnerCommands.Add(new OwnerCommandEntry
            {
                Ticket = 1,
                Pid = DeadPid,
                ProcessStartTicksUtc = DeadStart,
                Operation = "ui click",
                Mode = UiTurnMode.DesktopExclusive,
                Status = UiCommandStatus.Running,
            });
            state.Waiters.Add(new WaiterEntry
            {
                Ticket = 2,
                OwnerKey = "a-waiting-workflow",
                OwnerKind = UiOwnerKind.Workflow,
                Pid = WaitingPid,
                ProcessStartTicksUtc = WaitingStart,
                Operation = "ui click",
                Mode = UiTurnMode.DesktopExclusive,
            });
            _store.Publish(state);
        }

        _signals.Signalled.Clear();
        Assert.AreEqual(UiYieldResult.NothingHeld, _coordinator.ReleaseIdleTurn(CancellationToken.None),
            "the yielding workflow held nothing — the crashed owner did");

        using var afterLock = _store.AcquireStateLock(CancellationToken.None);
        var after = _store.Read().State!;
        Assert.AreEqual("a-waiting-workflow", after.Owner!.Key,
            "yield's normalization must publish the recovery it just performed");
        CollectionAssert.Contains(_signals.Signalled, (WaitingPid, WaitingStart));
    }

    /// <summary>
    /// Holds a foreign participant's lease <c>FileShare.None</c>, which is exactly what the liveness
    /// probe sees for a real second process.
    /// </summary>
    private FileStream OpenForeignLease(int pid, long startTicksUtc)
    {
        _paths.EnsureDirectories();
        return new FileStream(
            _paths.LeasePath(pid, startTicksUtc),
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.DeleteOnClose);
    }

    /// <summary>
    /// Publishes state in which <em>this</em> workflow already holds the turn through a live command
    /// running in another process, and holds that command's lease so it reads as live.
    /// </summary>
    /// <remarks>
    /// Participant identity is <c>(pid, processStartTicks)</c>, so one test process cannot hold two
    /// leases and cannot run two coordinated commands at once. Seeding the first command is how the
    /// same-workflow overlap rules — which are about two <em>processes</em> sharing a workflow id —
    /// stay testable in process.
    /// </remarks>
    private FileStream OccupyTurnWithThisWorkflow(
        UiTurnMode mode, string operation, int foreignPid = 313131, long foreignStart = 191919191)
    {
        var leaseStream = OpenForeignLease(foreignPid, foreignStart);

        using var stateLock = _store.AcquireStateLock(CancellationToken.None);
        var state = InteractiveDesktopState.CreateFresh();
        state.TurnId = 1;
        state.NextTicket = 2;
        state.Owner = new OwnerRecord
        {
            Kind = UiOwnerKind.Workflow,
            // The same key this test process resolves, so the seeded command really is "us".
            Key = new UiOwnerResolver().Resolve().Key,
        };
        state.OwnerCommands.Add(new OwnerCommandEntry
        {
            Ticket = 1,
            Pid = foreignPid,
            ProcessStartTicksUtc = foreignStart,
            Operation = operation,
            Mode = mode,
            Status = UiCommandStatus.Running,
        });
        _store.Publish(state);

        return leaseStream;
    }
}

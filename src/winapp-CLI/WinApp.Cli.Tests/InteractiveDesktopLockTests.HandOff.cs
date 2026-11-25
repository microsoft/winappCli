// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Tests;

/// <summary>
/// A command with no workflow id handing its turn off while it keeps running — how a window
/// recording lets other commands drive the app it is recording.
/// </summary>
public partial class InteractiveDesktopLockTests
{
    private const int HandOffWaitingPid = 616161;
    private const long HandOffWaitingStart = 525252525;

    private FileStream QueueForeignWorkflow()
    {
        var lease = OpenForeignLease(HandOffWaitingPid, HandOffWaitingStart);
        using var stateLock = _store.AcquireStateLock(CancellationToken.None);
        var state = _store.Read().State!;
        state.Waiters.Add(new WaiterEntry
        {
            Ticket = state.AllocateTicket(),
            OwnerKey = "a-waiting-workflow",
            OwnerKind = UiOwnerKind.Workflow,
            Pid = HandOffWaitingPid,
            ProcessStartTicksUtc = HandOffWaitingStart,
            Operation = "ui invoke",
            Mode = UiTurnMode.DesktopExclusive,
        });
        _store.Publish(state);
        return lease;
    }

    private InteractiveDesktopState ReadState()
    {
        using var stateLock = _store.AcquireStateLock(CancellationToken.None);
        return _store.Read().State!;
    }

    [TestMethod]
    public async Task HandOff_WithoutAWorkflowId_PromotesTheWaiterWhileTheCommandKeepsRunning()
    {
        Environment.SetEnvironmentVariable(UiOwnerResolver.WorkflowIdVariable, null);
        FileStream? waitingLease = null;
        try
        {
            var exit = await RunAsync(UiTurnMode.TurnShared, "ui record", async (turn, ct) =>
            {
                Assert.IsFalse(turn.HasWorkflowId);
                waitingLease = QueueForeignWorkflow();
                Assert.AreNotEqual("a-waiting-workflow", ReadState().Owner?.Key, "the waiter queues behind the recording");

                _signals.Signalled.Clear();
                Assert.IsTrue(await turn.HandOffAnonymousTurnAsync(ct));

                var during = ReadState();
                Assert.AreEqual("a-waiting-workflow", during.Owner?.Key, "the waiter must run while the recording continues");
                Assert.IsFalse(
                    during.OwnerCommands.Any(c => c.Pid == Environment.ProcessId),
                    "the recording no longer holds a place in the turn");
                Assert.Contains(
                    (HandOffWaitingPid, HandOffWaitingStart), _signals.Signalled,
                    "the promoted waiter must be woken rather than left to time out");

                await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                    () => turn.EnterAsync(ct),
                    "without a turn the command must not take the desktop");
                Assert.IsFalse(await turn.HandOffAnonymousTurnAsync(ct), "a second hand-off has nothing to give");
                return 0;
            });

            Assert.AreEqual(0, exit);
            var after = ReadState();
            Assert.AreEqual("a-waiting-workflow", after.Owner?.Key, "finishing later must not disturb the new owner");
            Assert.AreEqual(1, after.OwnerCommands.Count);
        }
        finally
        {
            waitingLease?.Dispose();
        }
    }

    [TestMethod]
    public async Task HandOff_UnderAWorkflowId_KeepsTheTurnSoOtherWorkflowsStillWait()
    {
        await RunAsync(UiTurnMode.TurnShared, "ui record", async (turn, ct) =>
        {
            Assert.IsTrue(turn.HasWorkflowId);
            using var waitingLease = QueueForeignWorkflow();

            Assert.IsFalse(await turn.HandOffAnonymousTurnAsync(ct));

            var during = ReadState();
            Assert.AreNotEqual("a-waiting-workflow", during.Owner?.Key, "the workflow keeps its turn");
            Assert.AreEqual(1, during.Waiters.Count, "the other workflow still waits");

            // The command still holds its turn, so it may still take the desktop.
            await using (await turn.EnterAsync(ct))
            {
            }

            return 0;
        });
    }
}

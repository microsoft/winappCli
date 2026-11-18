// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class FakeGuestProcessHostTests
{
    [TestMethod]
    [DataRow("exit")]
    [DataRow("stop")]
    [DataRow("dispose")]
    public async Task Exit_PublishesCallbackStateBeforeCompletion_EvenWithCompetingCompletion(string competingCompletion)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var callbackCount = 0;
        var registered = false;
        await using var host = new FakeGuestProcessHost(
            new GuestExecRequest { Executable = "winapp.exe", Arguments = [] },
            (_, _) => Task.CompletedTask,
            4000,
            (_, _) =>
            {
                Interlocked.Increment(ref callbackCount);
                entered.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("The test did not release the exit callback.");
                }
                registered = true;
            });
        var exit = Task.Run(() => host.Exit(0));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var wait = host.WaitForExitAsync(CancellationToken.None);
            Assert.IsFalse(wait.IsCompleted, "Registration must be visible before successful exit is observable.");

            switch (competingCompletion)
            {
                case "exit":
                    host.Exit(9);
                    break;
                case "stop":
                    await host.StopAsync(TimeSpan.Zero, CancellationToken.None);
                    break;
                case "dispose":
                    await host.DisposeAsync();
                    break;
            }

            Assert.IsFalse(wait.IsCompleted, "A competing completion must not bypass the winning callback.");
            release.Set();
            Assert.AreEqual(0, await wait.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(registered);
            Assert.AreEqual(1, callbackCount);
        }
        finally
        {
            release.Set();
            await exit;
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ForcedCompletion_DoesNotInvokeLaterExitCallback(bool dispose)
    {
        var callbackCount = 0;
        await using var host = new FakeGuestProcessHost(
            new GuestExecRequest { Executable = "winapp.exe", Arguments = [] },
            (_, _) => Task.CompletedTask,
            4000,
            (_, _) => callbackCount++);
        if (dispose)
        {
            await host.DisposeAsync();
        }
        else
        {
            await host.StopAsync(TimeSpan.Zero, CancellationToken.None);
        }
        host.Exit(0);

        Assert.AreEqual(-1, await host.WaitForExitAsync(CancellationToken.None));
        Assert.AreEqual(0, callbackCount);
    }

    [TestMethod]
    public async Task Exit_CallbackFailureFailsTheWaiterInsteadOfReportingSuccess()
    {
        await using var host = new FakeGuestProcessHost(
            new GuestExecRequest { Executable = "winapp.exe", Arguments = [] },
            (_, _) => Task.CompletedTask,
            4000,
            (_, _) => throw new IOException("controlled callback failure"));

        Assert.ThrowsExactly<IOException>(() => host.Exit(0));
        var error = await Assert.ThrowsExactlyAsync<IOException>(() => host.WaitForExitAsync(CancellationToken.None));
        Assert.AreEqual("controlled callback failure", error.Message);
    }
}

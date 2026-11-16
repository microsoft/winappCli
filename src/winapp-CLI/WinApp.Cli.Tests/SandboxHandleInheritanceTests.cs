// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Pipes;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// A child that outlives winapp must not hold its caller's captured output open.
/// </summary>
/// <remarks>
/// When a script captures winapp's output, the script reads until end of stream, which arrives only
/// when every handle to the pipe's write end has closed. The first Sandbox command starts a
/// <c>wsb exec</c> that hosts the guest agent for the life of the Sandbox. Callers such as a nested
/// PowerShell pipeline leave inheritable copies of their pipes in winapp beyond its three standard
/// handles, so if the agent launch lets the child inherit them, the caller waits until the Sandbox
/// stops even though winapp exited long before.
/// </remarks>
[TestClass]
[DoNotParallelize]
public class SandboxHandleInheritanceTests
{
    private static readonly string CmdExe = Path.Join(Environment.SystemDirectory, "cmd.exe");
    private static readonly string PingExe = Path.Join(Environment.SystemDirectory, "PING.EXE");

    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// The guest-agent launch — the one child that runs for the life of the Sandbox — is marked as
    /// outliving the caller.
    /// </summary>
    [TestMethod]
    public async Task AgentLaunch_IsMarkedAsOutlivingTheCaller()
    {
        var runner = new CapturingProcessRunner();
        var cli = new WindowsSandboxCli(runner);

        if (!cli.IsAvailable)
        {
            Assert.Inconclusive("wsb.exe is not present, so the launch path cannot be exercised.");
            return;
        }

        await cli.LaunchAgentAsync("sandbox-1", @"C:\WinAppBootstrap\winapp.exe guest-agent", TestContext.CancellationToken);

        var request = runner.Requests.Single(r => r.Arguments.Contains("exec"));

        Assert.IsTrue(
            request.OutlivesCaller,
            "The persistent guest-agent launch must not inherit the caller's handles.");
    }

    /// <summary>Short-lived Sandbox commands keep ordinary process startup.</summary>
    [TestMethod]
    public async Task ShortLivedCommands_KeepOrdinaryInheritance()
    {
        var runner = new CapturingProcessRunner();
        var cli = new WindowsSandboxCli(runner);

        if (!cli.IsAvailable)
        {
            Assert.Inconclusive("wsb.exe is not present, so the launch path cannot be exercised.");
            return;
        }

        await cli.ListAsync(TestContext.CancellationToken);

        Assert.IsFalse(
            runner.Requests.Single().OutlivesCaller,
            "A command winapp waits for cannot outlive it, so it needs no isolation.");
    }

    /// <summary>
    /// A caller's pipe that is inheritable but is not one of winapp's standard handles reaches end of
    /// stream while an outliving child is still running, and a later ordinary command still works.
    /// </summary>
    /// <remarks>
    /// The extra inheritable pipe is exactly what a nested PowerShell pipeline leaves in winapp.
    /// Before the fix, the outliving child inherited it and the read below never completed.
    /// </remarks>
    [TestMethod]
    public async Task OutlivingChild_DoesNotKeepCallersOtherPipesOpen()
    {
        var runner = new ProcessRunner();
        using var callerPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var stopChild = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var outliving = runner.RunAsync(
            new ProcessRunRequest(PingExe, ["-n", "60", "127.0.0.1"]) { OutlivesCaller = true },
            cancellationToken: stopChild.Token);

        try
        {
            // The child has been created by the time RunAsync returns. Closing our own copy of the
            // write end leaves the child's copy, if it inherited one, as the only thing keeping the
            // pipe open.
            callerPipe.DisposeLocalCopyOfClientHandle();

            var read = callerPipe.ReadAsync(new byte[1], TestContext.CancellationToken).AsTask();
            var completed = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5), TestContext.CancellationToken));

            Assert.AreSame(read, completed, "The caller's pipe must reach end of stream while the outliving child runs.");
            Assert.AreEqual(0, await read, "End of stream reads zero bytes.");
            Assert.IsFalse(outliving.IsCompleted, "The outliving child must still be running.");

            var ordinary = await runner.RunAsync(
                new ProcessRunRequest(CmdExe, ["/c", "echo", "after-outliving"]),
                cancellationToken: TestContext.CancellationToken);

            Assert.AreEqual(0, ordinary.ExitCode);
            StringAssert.Contains(ordinary.StandardOutput, "after-outliving");
        }
        finally
        {
            await stopChild.CancelAsync();
        }

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => outliving,
            "Cancellation must stop the outliving child.");
    }

    /// <summary>
    /// An outliving launch that fails at once still reports its exit code and both output streams,
    /// which is how a Sandbox that cannot run the agent yet is diagnosed.
    /// </summary>
    [TestMethod]
    public async Task OutlivingChild_ThatFailsImmediately_ReportsExitCodeAndOutput()
    {
        var runner = new ProcessRunner();

        var result = await runner.RunAsync(
            new ProcessRunRequest(CmdExe, ["/c", "echo out-line& echo err-line 1>&2& exit /b 3"]) { OutlivesCaller = true },
            cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(3, result.ExitCode);
        Assert.AreEqual("out-line", result.StandardOutput.Trim());
        Assert.AreEqual("err-line", result.StandardError.Trim());
    }

    /// <summary>Records the requests production built, without starting anything.</summary>
    private sealed class CapturingProcessRunner : IProcessRunner
    {
        public List<ProcessRunRequest> Requests { get; } = [];

        public Task<ProcessRunResult> RunAsync(
            ProcessRunRequest request,
            Action<string>? onOutputLine = null,
            Action<string>? onErrorLine = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ProcessRunResult(0, "{}", string.Empty));
        }
    }
}

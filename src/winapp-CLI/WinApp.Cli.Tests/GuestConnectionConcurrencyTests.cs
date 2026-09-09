// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// The guarantee that a running application never blocks another workflow, tested as behaviour.
/// </summary>
/// <remarks>
/// The agent used to serve one channel at a time, so a foreground <c>winapp run --on sandbox</c> held
/// the guest for as long as the application lived and a separate <c>winapp ui list-windows
/// --on sandbox</c> waited behind it indefinitely. Every test here would hang rather than fail under
/// that behaviour, which is what makes them a guard rather than a description.
/// <para>
/// Concurrency is only half of it. The other half is that nothing which made a single channel safe
/// was traded away for it, so these tests also pin isolation: identities, standard input,
/// cancellation, failure, and authentication are all per channel.
/// </para>
/// </remarks>
[TestClass]
public class GuestConnectionConcurrencyTests
{
    /// <summary>Ample for in-process work; short enough that a regression fails instead of hanging.</summary>
    private static readonly TimeSpan Promptly = TimeSpan.FromSeconds(20);

    private static readonly string[] RightArguments = ["right"];

    public TestContext TestContext { get; set; } = null!;

    private static GuestExecRequest Request(params string[] arguments) => new()
    {
        Executable = "winapp.exe",
        Arguments = [.. arguments],
    };

    [TestMethod]
    public async Task ForegroundApplication_DoesNotBlockInspectionOnOtherChannels()
    {
        await using var agent = new ConcurrentGuestAgentHarness();

        // The foreground case exactly: `winapp run --on sandbox` without --detach keeps its channel and
        // its operation for as long as the application is on screen.
        await using var application = await agent.ConnectAsync(TestContext.CancellationToken);
        var running = application.Channel.ExecuteAsync(
            Request("run", "."), callbacks: null, TestContext.CancellationToken);
        var applicationProcess = await agent.Processes.WaitForNextAsync(TestContext.CancellationToken);

        // Five separate winapp processes inspecting the guest while it runs.
        var inspections = new List<HostChannel>();
        try
        {
            for (var i = 0; i < 5; i++)
            {
                inspections.Add(await agent.ConnectAsync(TestContext.CancellationToken));
            }

            foreach (var inspection in inspections)
            {
                var capabilities = await inspection.Channel
                    .GetCapabilitiesAsync(TestContext.CancellationToken)
                    .WaitAsync(Promptly, TestContext.CancellationToken);

                Assert.IsTrue(capabilities.SupportsInteractiveDesktop);
            }

            // Not just capabilities: each inspection runs a real operation to completion while the
            // application is still running.
            var completions = new List<Task>();
            foreach (var inspection in inspections)
            {
                completions.Add(RunToCompletionAsync(agent, inspection, TestContext.CancellationToken));
            }

            await Task.WhenAll(completions).WaitAsync(Promptly, TestContext.CancellationToken);
        }
        finally
        {
            foreach (var inspection in inspections)
            {
                await inspection.DisposeAsync();
            }
        }

        Assert.IsFalse(running.IsCompleted, "The foreground application must still be running.");
        Assert.IsFalse(applicationProcess.StopRequested, "Inspection must not disturb the running application.");

        applicationProcess.Exit(0);
        Assert.AreEqual(0, (await running.WaitAsync(Promptly, TestContext.CancellationToken)).ExitCode);
    }

    [TestMethod]
    public async Task LongOperation_DoesNotDelayAShortOneOnAnotherChannel()
    {
        await using var agent = new ConcurrentGuestAgentHarness();

        await using var slow = await agent.ConnectAsync(TestContext.CancellationToken);
        var longRunning = slow.Channel.ExecuteAsync(
            Request("sandbox", "exec", "--", "long"), callbacks: null, TestContext.CancellationToken);
        var slowProcess = await agent.Processes.WaitForNextAsync(TestContext.CancellationToken);

        await using var quick = await agent.ConnectAsync(TestContext.CancellationToken);
        var shortRunning = quick.Channel.ExecuteAsync(
            Request("sandbox", "exec", "--", "short"), callbacks: null, TestContext.CancellationToken);
        var quickProcess = await agent.Processes.WaitForNextAsync(TestContext.CancellationToken);
        quickProcess.Exit(7);

        // The short command finishes while the long one is still going, rather than after it.
        Assert.AreEqual(7, (await shortRunning.WaitAsync(Promptly, TestContext.CancellationToken)).ExitCode);
        Assert.IsFalse(longRunning.IsCompleted);

        slowProcess.Exit(0);
        await longRunning.WaitAsync(Promptly, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task ChannelsPastTheBound_AreRefusedWithAnActionableError()
    {
        var limits = GuestConnectionLimits.Default with { MaxConnections = 2 };
        await using var agent = new ConcurrentGuestAgentHarness(limits);

        await using var first = await agent.ConnectAsync(TestContext.CancellationToken);
        await using var second = await agent.ConnectAsync(TestContext.CancellationToken);

        await first.Channel.GetCapabilitiesAsync(TestContext.CancellationToken);
        await second.Channel.GetCapabilitiesAsync(TestContext.CancellationToken);

        await using var third = await agent.ConnectAsync(TestContext.CancellationToken);

        var refusal = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => third.Channel.GetCapabilitiesAsync(TestContext.CancellationToken)
                .WaitAsync(Promptly, TestContext.CancellationToken));

        // A bound, not a hang: the third command is told immediately what is wrong and what to do.
        Assert.AreEqual(ExecutionTargetErrorCodes.AgentBusy, refusal.Error.Code);
        Assert.IsFalse(string.IsNullOrWhiteSpace(refusal.Error.UserAction));
    }

    [TestMethod]
    public async Task RefusedChannel_CannotStartAnOperation()
    {
        var limits = GuestConnectionLimits.Default with { MaxConnections = 1 };
        await using var agent = new ConcurrentGuestAgentHarness(limits);

        await using var admitted = await agent.ConnectAsync(TestContext.CancellationToken);
        await admitted.Channel.GetCapabilitiesAsync(TestContext.CancellationToken);

        await using var refused = await agent.ConnectAsync(TestContext.CancellationToken);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => refused.Channel.ExecuteAsync(Request("run", "."), callbacks: null, TestContext.CancellationToken)
                .WaitAsync(Promptly, TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.AgentBusy, failure.Error.Code);

        // Refusal is admission control, not a late error: nothing was started for it.
        Assert.IsTrue(agent.Processes.Started.IsEmpty, "A refused channel must never start a process.");
    }

    [TestMethod]
    public async Task BoundIsReleased_WhenAChannelCloses()
    {
        var limits = GuestConnectionLimits.Default with { MaxConnections = 1 };
        await using var agent = new ConcurrentGuestAgentHarness(limits);

        var first = await agent.ConnectAsync(TestContext.CancellationToken);
        await first.Channel.GetCapabilitiesAsync(TestContext.CancellationToken);
        await first.DisposeAsync();

        await WaitUntilAsync(() => agent.Acceptor.AdmittedConnections == 0, TestContext.CancellationToken);

        await using var second = await agent.ConnectAsync(TestContext.CancellationToken);

        // The slot the first channel held is genuinely returned, not leaked.
        var capabilities = await second.Channel.GetCapabilitiesAsync(TestContext.CancellationToken)
            .WaitAsync(Promptly, TestContext.CancellationToken);

        Assert.AreEqual("arm64", capabilities.Architecture);
    }

    [TestMethod]
    public async Task StalledPeers_DoNotConsumeTheAdmissionBound()
    {
        // Opening a socket proves nothing about holding the pre-shared key. If accepting one spent
        // an admission slot, anything able to connect and then say nothing could refuse every real
        // winapp command for the length of the handshake timeout, without knowing the key.
        var limits = GuestConnectionLimits.Default with { MaxConnections = 2 };
        await using var agent = new ConcurrentGuestAgentHarness(limits);

        var stalled = new List<Stream>();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                stalled.Add(agent.OfferStalledConnection());
            }

            await using var first = await agent.ConnectAsync(TestContext.CancellationToken);
            await using var second = await agent.ConnectAsync(TestContext.CancellationToken);

            // Both authenticate and are admitted, despite four stalled peers arriving first.
            Assert.AreEqual(
                "arm64",
                (await first.Channel.GetCapabilitiesAsync(TestContext.CancellationToken)
                    .WaitAsync(Promptly, TestContext.CancellationToken)).Architecture);

            Assert.AreEqual(
                "arm64",
                (await second.Channel.GetCapabilitiesAsync(TestContext.CancellationToken)
                    .WaitAsync(Promptly, TestContext.CancellationToken)).Architecture);

            Assert.AreEqual(2, agent.Acceptor.AdmittedConnections);
        }
        finally
        {
            foreach (var stream in stalled)
            {
                await stream.DisposeAsync();
            }
        }
    }

    [TestMethod]
    public async Task OperationsPastThePerChannelBound_AreRefused()
    {
        await using var agent = new ConcurrentGuestAgentHarness(maxOperationsPerConnection: 2);
        await using var channel = await agent.ConnectAsync(TestContext.CancellationToken);

        _ = channel.Channel.ExecuteAsync(Request("run", "a"), callbacks: null, TestContext.CancellationToken);
        await agent.Processes.WaitForNextAsync(TestContext.CancellationToken);
        _ = channel.Channel.ExecuteAsync(Request("run", "b"), callbacks: null, TestContext.CancellationToken);
        await agent.Processes.WaitForNextAsync(TestContext.CancellationToken);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => channel.Channel.ExecuteAsync(Request("run", "c"), callbacks: null, TestContext.CancellationToken)
                .WaitAsync(Promptly, TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.AgentBusy, failure.Error.Code);
    }

    [TestMethod]
    public async Task SameOperationId_OnTwoChannels_AreDifferentOperations()
    {
        await using var agent = new ConcurrentGuestAgentHarness();

        // Operation identity is per channel, so two hosts choosing the same GUID is legal and must
        // not make one host's cancel, input, or output reach the other's process.
        var shared = Guid.NewGuid();

        await using var left = await agent.ConnectRawAsync(TestContext.CancellationToken);
        await using var right = await agent.ConnectRawAsync(TestContext.CancellationToken);

        await left.SendAsync(Exec(shared, "left"), TestContext.CancellationToken);
        var leftProcess = await agent.Processes.WaitForNextAsync(TestContext.CancellationToken);

        await right.SendAsync(Exec(shared, "right"), TestContext.CancellationToken);
        var rightProcess = await agent.Processes.WaitForNextAsync(TestContext.CancellationToken);

        Assert.AreNotSame(leftProcess, rightProcess);
        CollectionAssert.AreEqual(RightArguments, rightProcess.Request.Arguments);

        // Standard input for the shared identity reaches only the channel that sent it.
        await left.SendStandardInputAsync(shared, Encoding.UTF8.GetBytes("to-left"), TestContext.CancellationToken);
        await WaitUntilAsync(() => leftProcess.StandardInput.Count == 1, TestContext.CancellationToken);

        Assert.AreEqual("to-left", Encoding.UTF8.GetString(leftProcess.StandardInput[0]));
        Assert.AreEqual(0, rightProcess.StandardInput.Count, "Input must not cross channels.");

        // Cancelling the shared identity on one channel stops only that channel's process.
        await left.SendAsync(Cancel(shared), TestContext.CancellationToken);
        await WaitUntilAsync(() => leftProcess.StopRequested, TestContext.CancellationToken);

        Assert.IsFalse(rightProcess.StopRequested, "Cancellation must not cross channels.");
    }

    [TestMethod]
    public async Task ChannelLoss_StopsOnlyItsOwnOperations()
    {
        await using var agent = new ConcurrentGuestAgentHarness();

        var lost = await agent.ConnectAsync(TestContext.CancellationToken);
        _ = lost.Channel.ExecuteAsync(Request("run", "lost"), callbacks: null, TestContext.CancellationToken);
        var lostProcess = await agent.Processes.WaitForNextAsync(TestContext.CancellationToken);

        await using var survivor = await agent.ConnectAsync(TestContext.CancellationToken);
        _ = survivor.Channel.ExecuteAsync(Request("run", "kept"), callbacks: null, TestContext.CancellationToken);
        var survivingProcess = await agent.Processes.WaitForNextAsync(TestContext.CancellationToken);

        // The blast radius of a dropped connection is that connection.
        await lost.DisposeAsync();
        await WaitUntilAsync(() => lostProcess.StopRequested, TestContext.CancellationToken);

        Assert.IsFalse(survivingProcess.StopRequested, "An unrelated channel's work must survive.");

        // And the survivor is still usable afterwards, not merely un-cancelled.
        await RunToCompletionAsync(agent, survivor, TestContext.CancellationToken);
        survivingProcess.Exit(0);
    }

    [TestMethod]
    public async Task TamperedFrame_ClosesOnlyThatChannel()
    {
        await using var agent = new ConcurrentGuestAgentHarness();

        await using var honest = await agent.ConnectAsync(TestContext.CancellationToken);
        await honest.Channel.GetCapabilitiesAsync(TestContext.CancellationToken);

        var tampered = await agent.ConnectAsync(TestContext.CancellationToken);
        await tampered.Channel.GetCapabilitiesAsync(TestContext.CancellationToken);

        // Bytes the channel never produced, written straight onto its stream.
        await tampered.Stream.InjectAsync(
            [0x00, 0x00, 0x00, 0x40, .. Enumerable.Repeat((byte)0xAB, 0x40)],
            TestContext.CancellationToken);

        await WaitUntilAsync(() => agent.ClosedConnections >= 1, TestContext.CancellationToken);
        await tampered.DisposeAsync();

        // Authentication failure is contained: the honest channel is unaffected.
        var capabilities = await honest.Channel.GetCapabilitiesAsync(TestContext.CancellationToken)
            .WaitAsync(Promptly, TestContext.CancellationToken);

        Assert.AreEqual("arm64", capabilities.Architecture);
    }

    [TestMethod]
    public async Task ReplayedFrame_ClosesOnlyThatChannel()
    {
        await using var agent = new ConcurrentGuestAgentHarness();

        await using var honest = await agent.ConnectRawAsync(TestContext.CancellationToken);
        await honest.SendAsync(Capabilities(), TestContext.CancellationToken);
        await honest.ReceiveMessageAsync(TestContext.CancellationToken);

        var replaying = await agent.ConnectRawAsync(TestContext.CancellationToken);
        await replaying.SendAsync(Capabilities(), TestContext.CancellationToken);
        await replaying.ReceiveMessageAsync(TestContext.CancellationToken);

        // A byte-for-byte valid frame, sent a second time. Sequence numbers are per direction and
        // never transmitted, so the guest decrypts it against the next sequence and it fails.
        await replaying.Stream.ReplayLastFrameAsync(TestContext.CancellationToken);

        await WaitUntilAsync(() => agent.ClosedConnections >= 1, TestContext.CancellationToken);
        await replaying.DisposeAsync();

        await honest.SendAsync(Capabilities(), TestContext.CancellationToken);
        var response = await honest.ReceiveMessageAsync(TestContext.CancellationToken)
            .WaitAsync(Promptly, TestContext.CancellationToken);

        Assert.AreEqual(GuestMessageTypes.CapabilitiesResponse, response.Type);
    }

    [TestMethod]
    public async Task Shutdown_StopsEveryChannelAndOperation()
    {
        var agent = new ConcurrentGuestAgentHarness();

        var channels = new List<HostChannel>();
        var processes = new List<FakeGuestProcessHost>();

        for (var i = 0; i < 3; i++)
        {
            var channel = await agent.ConnectAsync(TestContext.CancellationToken);
            channels.Add(channel);
            _ = channel.Channel.ExecuteAsync(
                Request("run", i.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                callbacks: null,
                TestContext.CancellationToken);
            processes.Add(await agent.Processes.WaitForNextAsync(TestContext.CancellationToken));
        }

        // Returns only once every connection has drained, so the assertions below need no polling.
        await agent.ShutdownAsync().WaitAsync(Promptly, TestContext.CancellationToken);

        foreach (var process in processes)
        {
            Assert.IsTrue(process.StopRequested, "Shutdown must stop every channel's operations.");
            Assert.IsTrue(process.Disposed, "Shutdown must dispose every operation deterministically.");
        }

        Assert.AreEqual(0, agent.Acceptor.AdmittedConnections);

        foreach (var channel in channels)
        {
            await channel.DisposeAsync();
        }

        await agent.DisposeAsync();
    }

    private static GuestMessage Capabilities() => new()
    {
        Type = GuestMessageTypes.CapabilitiesRequest,
        OperationId = Guid.NewGuid().ToString(),
        TargetEpoch = ConcurrentGuestAgentHarness.AgentEpoch.Value,
    };

    private static GuestMessage Exec(Guid operationId, string argument) => new()
    {
        Type = GuestMessageTypes.ExecRequest,
        OperationId = operationId.ToString(),
        TargetEpoch = ConcurrentGuestAgentHarness.AgentEpoch.Value,
        Exec = new GuestExecRequest { Executable = "winapp.exe", Arguments = [argument] },
    };

    private static GuestMessage Cancel(Guid operationId) => new()
    {
        Type = GuestMessageTypes.CancelRequest,
        OperationId = operationId.ToString(),
        TargetEpoch = ConcurrentGuestAgentHarness.AgentEpoch.Value,
    };

    /// <summary>Runs one operation to completion on a channel, proving it is actually serving.</summary>
    private static async Task RunToCompletionAsync(
        ConcurrentGuestAgentHarness agent,
        HostChannel channel,
        CancellationToken cancellationToken)
    {
        var execution = channel.Channel.ExecuteAsync(
            Request("ui", "list-windows"), callbacks: null, cancellationToken);

        var process = await agent.Processes.WaitForNextAsync(cancellationToken);
        process.Exit(0);

        Assert.AreEqual(0, (await execution.WaitAsync(Promptly, cancellationToken)).ExitCode);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + Promptly;

        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail("The expected state was never reached.");
            }

            await Task.Delay(10, cancellationToken);
        }
    }
}

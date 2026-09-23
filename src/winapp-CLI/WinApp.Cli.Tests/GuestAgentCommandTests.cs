// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

[TestClass]
public class GuestAgentCommandTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task DirectInvocation_DoesNotReadBinaryIdentity()
    {
        var handler = CreateHandler();
        handler.ReadIdentity = _ => throw new AssertFailedException("No agent will be started.");

        var result = await handler.InvokeAsync(new GuestAgentCommand().Parse([]), TestContext.CancellationToken);

        Assert.AreEqual(1, result);
    }

    [TestMethod]
    public async Task SelfTest_ReadsAndPublishesBinaryIdentity()
    {
        var handler = CreateHandler();
        var identity = new GuestAgentIdentity("1.2.3", new string('a', 64), "arm64",
            GuestProtocol.MinimumVersion, GuestProtocol.CurrentVersion);
        var reads = 0;
        handler.ReadIdentity = token =>
        {
            Assert.AreEqual(TestContext.CancellationToken, token);
            reads++;
            return Task.FromResult(identity);
        };
        using var output = new StringWriter();
        var parsed = new GuestAgentCommand().Parse(["--self-test"]);
        parsed.InvocationConfiguration.Output = output;

        Assert.AreEqual(0, await handler.InvokeAsync(parsed, TestContext.CancellationToken));

        Assert.AreEqual(1, reads);
        var heartbeat = GuestAgentHeartbeat.TryParse(output.ToString());
        Assert.IsNotNull(heartbeat);
        Assert.AreEqual(identity.BinaryHash, heartbeat.BinaryHash);
        Assert.AreEqual(identity.Version, heartbeat.Version);
        Assert.IsTrue(heartbeat.Ready);
    }

    private static GuestAgentCommand.Handler CreateHandler() =>
        new(new StaticGuestSessionProbe(new GuestSessionInfo(1, "WinSta0", HasInputDesktop: true)),
            new FakeGuestProcessHostFactory(), null!, null!);
}

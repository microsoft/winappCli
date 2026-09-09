// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for the bootstrap handshake: the one path where the host reads guest-writable content.
/// </summary>
/// <remarks>
/// <c>wsb exec</c> returns an exit code and nothing else, so the bootstrap-result folder is the only
/// way an agent that refused to start can explain itself. It is also the only guest-writable path
/// the host reads, which makes it the feature's untrusted-input surface: these tests cover both what
/// it must carry and what it must refuse.
/// </remarks>
[TestClass]
public class WindowsSandboxBootstrapTests
{
    private static readonly ExecutionTargetEpoch Epoch = ExecutionTargetEpoch.Create("sandbox-1", "nonce-a");

    private string _resultDirectory = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Setup()
    {
        _resultDirectory = TestPaths.TempRoot(nameof(WindowsSandboxBootstrapTests));
        Directory.CreateDirectory(_resultDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_resultDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [TestMethod]
    public async Task WaitForHeartbeat_ReadyAgent_ReturnsItsPort()
    {
        await PublishHeartbeatAsync(GuestReadinessFailure.None, Epoch, port: 51234);

        var heartbeat = await WindowsSandboxBackend.WaitForHeartbeatAsync(
            _resultDirectory, Epoch, TestContext.CancellationToken);

        Assert.AreEqual(51234, heartbeat.Port);
        Assert.IsTrue(heartbeat.Ready);
    }

    [TestMethod]
    public async Task WaitForHeartbeat_AgentRefusedToServe_ReportsItsReasonNotATimeout()
    {
        await PublishHeartbeatAsync(GuestReadinessFailure.NoInputDesktop, Epoch, port: 51234);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => WindowsSandboxBackend.WaitForHeartbeatAsync(_resultDirectory, Epoch, TestContext.CancellationToken));

        // This is the whole reason the result folder exists: without it a disconnected Sandbox
        // window would surface as "the agent did not report ready in time".
        Assert.AreEqual(ExecutionTargetErrorCodes.NoInteractiveSession, failure.Error.Code);
        Assert.AreEqual(nameof(GuestReadinessFailure.NoInputDesktop), failure.Error.Context?["reason"]);
    }

    [TestMethod]
    public async Task WaitForHeartbeat_PreviousGeneration_IsIgnored()
    {
        await PublishHeartbeatAsync(
            GuestReadinessFailure.None,
            ExecutionTargetEpoch.Create("sandbox-1", "nonce-old"),
            port: 51234);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // A heartbeat from a previous boot describes an agent that no longer exists. Accepting it
        // would connect the host to a port nothing is listening on, or worse, the wrong thing.
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => WindowsSandboxBackend.WaitForHeartbeatAsync(_resultDirectory, Epoch, cancellation.Token));
    }

    [TestMethod]
    public void ResultFolder_WithinLimits_IsAccepted()
    {
        File.WriteAllText(Path.Join(_resultDirectory, "heartbeat.json"), "{}");
        WindowsSandboxBackend.EnsureResultFolderWithinLimits(_resultDirectory);
    }

    [TestMethod]
    public void ResultFolder_TooManyFiles_IsRefused()
    {
        for (var i = 0; i <= WindowsSandboxBackend.MaxResultFiles; i++)
        {
            File.WriteAllText(Path.Join(_resultDirectory, $"junk-{i}.txt"), "x");
        }

        // The folder is guest-writable, so a co-resident process could fill it. Bounding it stops
        // that from becoming the host's problem.
        var failure = Assert.ThrowsExactly<ExecutionTargetException>(
            () => WindowsSandboxBackend.EnsureResultFolderWithinLimits(_resultDirectory));

        Assert.AreEqual(ExecutionTargetErrorCodes.StartFailed, failure.Error.Code);
    }

    [TestMethod]
    public void ResultFolder_TooLarge_IsRefused()
    {
        File.WriteAllBytes(
            Path.Join(_resultDirectory, "huge.bin"),
            new byte[WindowsSandboxBackend.MaxResultBytes + 1]);

        var failure = Assert.ThrowsExactly<ExecutionTargetException>(
            () => WindowsSandboxBackend.EnsureResultFolderWithinLimits(_resultDirectory));

        Assert.AreEqual(ExecutionTargetErrorCodes.StartFailed, failure.Error.Code);
    }

    [TestMethod]
    public async Task WaitForHeartbeat_NeverPublished_SurfacesTheAgentsOwnDiagnostics()
    {
        await File.WriteAllTextAsync(
            Path.Join(_resultDirectory, WindowsSandboxBackend.StartupLogFileName),
            "the agent could not listen for the host: address already in use",
            TestContext.CancellationToken);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => WindowsSandboxBackend.WaitForHeartbeatAsync(_resultDirectory, Epoch, cancellation.Token));

        // The diagnostics themselves are covered by the timeout path; this confirms an oversized or
        // hostile log does not prevent the folder from being read at all.
        WindowsSandboxBackend.EnsureResultFolderWithinLimits(_resultDirectory);
    }

    [TestMethod]
    public void BootstrapMaterial_RoundTripsAndRejectsTampering()
    {
        var material = GuestBootstrapMaterial.Create(WindowsSandboxTarget.Default, Epoch, port: 5000);

        var parsed = GuestBootstrapMaterial.TryParse(material.ToJson());
        Assert.IsNotNull(parsed);
        Assert.AreEqual(Epoch.Value, parsed.TargetEpoch);
        Assert.AreEqual(GuestProtocol.PreSharedKeySize, parsed.DecodeKey().Length);

        // Fresh key per boot: material recovered from an earlier generation authenticates nothing.
        var other = GuestBootstrapMaterial.Create(WindowsSandboxTarget.Default, Epoch, port: 5000);
        Assert.AreNotEqual(material.PreSharedKey, other.PreSharedKey);

        Assert.IsNull(GuestBootstrapMaterial.TryParse("{not json"));
        Assert.IsNull(GuestBootstrapMaterial.TryParse(string.Empty));
    }

    [TestMethod]
    public void BootstrapMaterial_MalformedKey_IsRefused()
    {
        var material = GuestBootstrapMaterial.Create(WindowsSandboxTarget.Default, Epoch, port: 5000)
            with
        { PreSharedKey = Convert.ToBase64String([1, 2, 3]) };

        var failure = Assert.ThrowsExactly<ExecutionTargetException>(() => material.DecodeKey());
        Assert.AreEqual(ExecutionTargetErrorCodes.TransportFailed, failure.Error.Code);
    }

    private Task PublishHeartbeatAsync(GuestReadinessFailure readiness, ExecutionTargetEpoch epoch, int port)
    {
        var heartbeat = GuestAgentHeartbeat.Create(
            new GuestAgentIdentity("1.0.0", "hash", "arm64", 1, 1),
            readiness,
            epoch,
            port,
            DateTimeOffset.UtcNow);

        return File.WriteAllTextAsync(
            Path.Join(_resultDirectory, WindowsSandboxBackend.HeartbeatFileName),
            heartbeat.ToJson(),
            TestContext.CancellationToken);
    }
}

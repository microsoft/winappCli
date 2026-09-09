// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for guest-agent version negotiation, staged update, self-test, activation, and rollback.
/// </summary>
/// <remarks>
/// The rules that matter most here are the refusals. Downgrading the guest would silently move a
/// Sandbox backwards to a build that may not understand its own persisted state, and activating a
/// binary that failed its self-test would leave a target that reports healthy while serving nothing.
/// Both are reachable only through deliberate construction, so they are tested directly.
/// </remarks>
[TestClass]
public class GuestAgentVersioningTests
{
    private const string HostHash = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string GuestHash = "2222222222222222222222222222222222222222222222222222222222222222";

    private static GuestAgentIdentity Host(string version = "1.2.0", string hash = HostHash) =>
        new(version, hash, "arm64", GuestProtocol.MinimumVersion, GuestProtocol.CurrentVersion);

    private static GuestAgentHeartbeat Guest(
        string version = "1.2.0",
        string hash = HostHash,
        string architecture = "arm64",
        int protocolMinimum = GuestProtocol.MinimumVersion,
        int protocolMaximum = GuestProtocol.CurrentVersion) => new()
        {
            SchemaVersion = GuestAgentHeartbeat.CurrentSchemaVersion,
            Version = version,
            BinaryHash = hash,
            Architecture = architecture,
            ProtocolMinimum = protocolMinimum,
            ProtocolMaximum = protocolMaximum,
            Ready = true,
            TargetEpoch = "sandbox-1:nonce",
            Port = 5000,
            PublishedUtc = DateTimeOffset.UtcNow,
        };

    [TestMethod]
    public void Plan_SameVersionAndHash_Reuses()
    {
        var plan = GuestAgentUpdatePlanner.Plan(Host(), Guest());

        Assert.AreEqual(GuestAgentAction.Reuse, plan.Action);
        Assert.IsFalse(plan.RequiresMutation);
    }

    [TestMethod]
    public void Plan_NoAgent_Installs()
    {
        var plan = GuestAgentUpdatePlanner.Plan(Host(), guest: null);

        Assert.AreEqual(GuestAgentAction.Install, plan.Action);
        Assert.IsTrue(plan.RequiresMutation);
    }

    [TestMethod]
    public void Plan_HostNewer_StagesAndActivates()
    {
        var plan = GuestAgentUpdatePlanner.Plan(Host("1.3.0"), Guest("1.2.0"));

        Assert.AreEqual(GuestAgentAction.StageAndActivate, plan.Action);
    }

    [TestMethod]
    public void Plan_SameVersionDifferentBinary_StagesAndActivates()
    {
        // A locally built winapp against a released guest. Replacing is not a downgrade, and the
        // host's binary is the one whose behaviour the caller expects.
        var plan = GuestAgentUpdatePlanner.Plan(Host("1.2.0", HostHash), Guest("1.2.0", GuestHash));

        Assert.AreEqual(GuestAgentAction.StageAndActivate, plan.Action);
    }

    [TestMethod]
    public void Plan_GuestNewerAndCompatible_ReusesTheNewerGuest()
    {
        var plan = GuestAgentUpdatePlanner.Plan(Host("1.2.0"), Guest("1.5.0", GuestHash));

        // Never downgrade: a newer guest that still speaks a protocol revision this host knows is
        // left exactly where it is.
        Assert.AreEqual(GuestAgentAction.Reuse, plan.Action);
    }

    [TestMethod]
    public void Plan_GuestNewerAndIncompatible_Fails()
    {
        var guest = Guest("2.0.0", GuestHash, protocolMinimum: 99, protocolMaximum: 100);
        var plan = GuestAgentUpdatePlanner.Plan(Host("1.2.0"), guest);

        Assert.AreEqual(GuestAgentAction.FailIncompatible, plan.Action);

        var failure = GuestAgentUpdatePlanner.Incompatible(Host("1.2.0"), guest);
        Assert.AreEqual(ExecutionTargetErrorCodes.AgentIncompatible, failure.Error.Code);

        // Updating the host is the only correct fix, so the recovery command says so and is safe to
        // run without judgement.
        Assert.AreEqual("winapp update", failure.Error.NextCommand?.Command);
        Assert.IsFalse(failure.Error.NextCommand?.Advisory);
    }

    [TestMethod]
    public void Plan_ArchitectureMismatch_Installs()
    {
        var plan = GuestAgentUpdatePlanner.Plan(Host(), Guest(architecture: "x64"));

        // A binary for the wrong architecture cannot run at all, so version ordering never applies.
        Assert.AreEqual(GuestAgentAction.Install, plan.Action);
    }

    [TestMethod]
    public void Plan_UnparseableGuestVersion_FailsClosed()
    {
        var plan = GuestAgentUpdatePlanner.Plan(Host("1.2.0"), Guest("not-a-version", GuestHash));

        // A version that cannot be ordered cannot be proven *older*, so replacing it might be a
        // downgrade. Failing is the only outcome that cannot silently move the guest backwards.
        Assert.AreEqual(GuestAgentAction.FailIncompatible, plan.Action);
    }

    [TestMethod]
    public void CanForceRepair_RefusesToMoveANewerGuestBackwards()
    {
        Assert.IsTrue(GuestAgentUpdatePlanner.CanForceRepair(Host("1.2.0"), guest: null));
        Assert.IsTrue(GuestAgentUpdatePlanner.CanForceRepair(Host("1.2.0"), Guest("1.2.0", GuestHash)));
        Assert.IsTrue(GuestAgentUpdatePlanner.CanForceRepair(Host("1.3.0"), Guest("1.2.0")));

        // Even an explicit repair may not downgrade.
        Assert.IsFalse(GuestAgentUpdatePlanner.CanForceRepair(Host("1.2.0"), Guest("1.9.0", GuestHash)));
    }

    [TestMethod]
    public void Heartbeat_RoundTripsAndRejectsUnknownSchema()
    {
        var heartbeat = GuestAgentHeartbeat.Create(
            Host(),
            GuestReadinessFailure.None,
            ExecutionTargetEpoch.Create("sandbox-1", "nonce"),
            port: 51234,
            DateTimeOffset.UtcNow);

        var parsed = GuestAgentHeartbeat.TryParse(heartbeat.ToJson());

        Assert.IsNotNull(parsed);
        Assert.AreEqual(heartbeat.BinaryHash, parsed.BinaryHash);
        Assert.AreEqual(51234, parsed.Port);
        Assert.IsTrue(parsed.Ready);

        // The heartbeat arrives through a guest-writable folder the spec treats as untrusted, so
        // parsing is total: garbage and unknown schemas produce "no usable heartbeat", never a throw.
        Assert.IsNull(GuestAgentHeartbeat.TryParse("{not json"));
        Assert.IsNull(GuestAgentHeartbeat.TryParse(string.Empty));
        Assert.IsNull(GuestAgentHeartbeat.TryParse(heartbeat.ToJson().Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Heartbeat_NotReady_StillPublishesTheReason()
    {
        var heartbeat = GuestAgentHeartbeat.Create(
            Host(),
            GuestReadinessFailure.Session0,
            ExecutionTargetEpoch.None,
            port: 0,
            DateTimeOffset.UtcNow);

        // Publishing the failure is what lets the host report why the agent refused instead of
        // timing out on silence.
        Assert.IsFalse(heartbeat.Ready);
        Assert.AreEqual(nameof(GuestReadinessFailure.Session0), heartbeat.NotReadyReason);
    }

    [TestMethod]
    public void Heartbeat_StaleTimestamp_IsNotFresh()
    {
        var now = DateTimeOffset.UtcNow;
        var heartbeat = GuestAgentHeartbeat.Create(Host(), GuestReadinessFailure.None, ExecutionTargetEpoch.None, 0, now);

        Assert.IsTrue(heartbeat.IsFresh(now));
        Assert.IsFalse(heartbeat.IsFresh(now + GuestAgentHeartbeat.MaximumAge + TimeSpan.FromSeconds(1)));
    }
}

/// <summary>
/// Tests for <see cref="GuestAgentInstaller"/>: staging, hash verification, activation, and the
/// last-known-good rollback that keeps a failed update from leaving a target unusable.
/// </summary>
[TestClass]
public class GuestAgentInstallerTests
{
    private string _root = null!;
    private string _source = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = TestPaths.TempRoot(nameof(GuestAgentInstallerTests));
        Directory.CreateDirectory(_root);

        _source = TestPaths.Under(_root, "source.bin");
        File.WriteAllText(_source, "agent-v2");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private string AgentRoot => TestPaths.Under(_root, "agent");

    private async Task<string> SourceHashAsync() =>
        await GuestAgentIdentity.ComputeBinaryHashAsync(_source, TestContext.CancellationToken);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Stage_VerifiesTheBinaryThatActuallyLanded()
    {
        var staged = await GuestAgentInstaller.StageAsync(
            AgentRoot, _source, await SourceHashAsync(), TestContext.CancellationToken);

        Assert.IsTrue(File.Exists(staged));
        Assert.AreEqual("agent-v2", await File.ReadAllTextAsync(staged, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Stage_HashMismatch_IsRejectedAndCleanedUp()
    {
        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => GuestAgentInstaller.StageAsync(
                AgentRoot, _source, new string('a', 64), TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.AgentUpgradeFailed, failure.Error.Code);

        // A binary that did not survive transfer intact must not be left where activation could
        // pick it up later.
        Assert.IsFalse(File.Exists(GuestAgentInstaller.StagedBinaryPath(AgentRoot)));
    }

    [TestMethod]
    public async Task Activate_PassingSelfTest_BecomesCurrentAndKeepsLastKnownGood()
    {
        var installer = new GuestAgentInstaller(new StubSelfTest(passes: true));

        var currentPath = GuestAgentInstaller.CurrentBinaryPath(AgentRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(currentPath)!);
        await File.WriteAllTextAsync(currentPath, "agent-v1", TestContext.CancellationToken);

        await GuestAgentInstaller.StageAsync(AgentRoot, _source, await SourceHashAsync(), TestContext.CancellationToken);
        await installer.ActivateAsync(AgentRoot, TestContext.CancellationToken);

        Assert.AreEqual("agent-v2", await File.ReadAllTextAsync(currentPath, TestContext.CancellationToken));

        // The previous binary is retained: it is the only thing to fall back to if the replacement
        // never reports ready.
        Assert.AreEqual(
            "agent-v1",
            await File.ReadAllTextAsync(GuestAgentInstaller.PreviousBinaryPath(AgentRoot), TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Activate_FailingSelfTest_LeavesTheWorkingAgentUntouched()
    {
        var installer = new GuestAgentInstaller(new StubSelfTest(passes: false));

        var currentPath = GuestAgentInstaller.CurrentBinaryPath(AgentRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(currentPath)!);
        await File.WriteAllTextAsync(currentPath, "agent-v1", TestContext.CancellationToken);

        await GuestAgentInstaller.StageAsync(AgentRoot, _source, await SourceHashAsync(), TestContext.CancellationToken);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => installer.ActivateAsync(AgentRoot, TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.AgentUpgradeFailed, failure.Error.Code);

        // The self-test runs before anything is swapped, so the common failure never needs recovery.
        Assert.AreEqual("agent-v1", await File.ReadAllTextAsync(currentPath, TestContext.CancellationToken));
        Assert.IsFalse(File.Exists(GuestAgentInstaller.StagedBinaryPath(AgentRoot)));
    }

    [TestMethod]
    public async Task RollBack_RestoresTheLastKnownGoodAgent()
    {
        var installer = new GuestAgentInstaller(new StubSelfTest(passes: true));

        var currentPath = GuestAgentInstaller.CurrentBinaryPath(AgentRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(currentPath)!);
        await File.WriteAllTextAsync(currentPath, "agent-v1", TestContext.CancellationToken);

        await GuestAgentInstaller.StageAsync(AgentRoot, _source, await SourceHashAsync(), TestContext.CancellationToken);
        await installer.ActivateAsync(AgentRoot, TestContext.CancellationToken);

        // The replacement activated but never reported ready — a failure activation itself cannot
        // observe, which is exactly why rollback is available to the caller.
        GuestAgentInstaller.RollBack(AgentRoot);

        Assert.AreEqual("agent-v1", await File.ReadAllTextAsync(currentPath, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Activate_WithNothingStaged_Fails()
    {
        var installer = new GuestAgentInstaller(new StubSelfTest(passes: true));

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => installer.ActivateAsync(AgentRoot, TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.AgentUpgradeFailed, failure.Error.Code);
    }

    [TestMethod]
    public async Task Install_FirstAgentIntoAFreshGuest_Succeeds()
    {
        var installer = new GuestAgentInstaller(new StubSelfTest(passes: true));

        await installer.InstallAsync(AgentRoot, _source, await SourceHashAsync(), TestContext.CancellationToken);

        Assert.AreEqual(
            "agent-v2",
            await File.ReadAllTextAsync(GuestAgentInstaller.CurrentBinaryPath(AgentRoot), TestContext.CancellationToken));
    }

    /// <summary>A self-test with a fixed outcome.</summary>
    private sealed class StubSelfTest(bool passes) : IGuestAgentSelfTest
    {
        public Task<bool> RunAsync(string binaryPath, CancellationToken cancellationToken) =>
            Task.FromResult(passes);
    }
}

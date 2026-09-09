// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for what happens to a Windows Sandbox winapp did not start.
/// </summary>
/// <remarks>
/// <c>--on sandbox</c> takes one over rather than refusing it, so these pin the two halves of that
/// decision: the guest really is prepared — which is a mutation, not a read — and the instance is
/// never stopped, on any path, including every failure path.
/// </remarks>
[TestClass]
public class SandboxAdoptionTests
{
    /// <summary>The instance a user started themselves, before winapp ran at all.</summary>
    private const string ManualInstanceId = "manually-started-sandbox";

    [TestMethod]
    public async Task AdoptedInstance_ThatAlreadyHasAClient_IsNotGivenASecondOne()
    {
        // Regression, measured on a live Sandbox: `wsb connect` against an instance whose client is
        // already attached starts a SECOND WindowsSandboxRemoteSession rather than reusing the
        // first, and that extra client is still running after `wsb stop`. Taking over a Sandbox the
        // user already has open is exactly the case that hits it.
        using var harness = new AdoptionHarness();
        harness.Cli.SetRunning(ManualInstanceId);
        harness.Cli.Session = GuestSessionAvailability.Ready;

        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        Assert.IsTrue(
            harness.Cli.Operations.Any(op => op.StartsWith("probe-session", StringComparison.Ordinal)),
            "Whether to connect must be decided by asking the guest.");
        Assert.IsFalse(
            harness.Cli.Operations.Any(op => op.StartsWith("connect:", StringComparison.Ordinal)),
            "A guest that already has an interactive session must not be handed another client.");
    }

    [TestMethod]
    public async Task AdoptedInstance_WithNoLoginSession_IsConnectedExactlyOnce()
    {
        // A Sandbox started headless by `wsb start` has no session until a client attaches, which is
        // what makes this the case that genuinely needs one.
        using var harness = new AdoptionHarness();
        harness.Cli.SetRunning(ManualInstanceId);
        harness.Cli.Session = GuestSessionAvailability.NoLoginSession;

        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        Assert.AreEqual(
            1,
            harness.Cli.Operations.Count(op => op.StartsWith("connect:", StringComparison.Ordinal)),
            "Exactly one client, for a guest that had none.");
    }

    [TestMethod]
    public async Task AdoptedInstance_WhoseSessionCannotBeDetermined_IsNotGivenAClientEither()
    {
        // `Unknown` means the probe drew no conclusion, not that there is no client. Treating it
        // like a confirmed absence would reintroduce the duplication this whole path exists to
        // prevent, because a client may well be attached. winapp prepares the guest anyway and lets
        // the agent's own readiness report settle it.
        using var harness = new AdoptionHarness();
        harness.Cli.SetRunning(ManualInstanceId);
        harness.Cli.Session = GuestSessionAvailability.Unknown;

        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        Assert.IsFalse(
            harness.Cli.Operations.Any(op => op.StartsWith("connect:", StringComparison.Ordinal)),
            "Only a confirmed absence of a login session may create a client.");
    }

    [TestMethod]
    public async Task AdoptedInstance_IsPreparedLikeAFreshGuest()
    {
        // A guest winapp did not start has none of winapp's setup in it, whatever else is running
        // there. Treating it as warm would connect to an agent that does not exist.
        using var harness = new AdoptionHarness();
        harness.Cli.SetRunning(ManualInstanceId);

        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        Assert.IsTrue(
            harness.Cli.Operations.Any(op => op.StartsWith($"connect:{ManualInstanceId}", StringComparison.Ordinal)),
            "An adopted guest needs a connected client to have an interactive session.");
        Assert.IsTrue(
            harness.Cli.Operations.Any(op => op.Contains("AllowDevelopmentWithoutDevLicense", StringComparison.Ordinal)),
            "Developer Mode is machine-wide and nothing has set it in a guest winapp did not start.");
        Assert.IsTrue(
            harness.Cli.Operations.Any(op => op.Contains("New-NetFirewallRule", StringComparison.Ordinal)),
            "The agent's inbound rule must be created in an adopted guest too.");
    }

    [TestMethod]
    public async Task AdoptedInstance_IsNeverStopped_OnAnyFailurePath()
    {
        using var harness = new AdoptionHarness();
        harness.Cli.SetRunning(ManualInstanceId);

        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        CollectionAssert.AreEqual(
            Array.Empty<string>(),
            harness.Cli.Stopped,
            "A Sandbox winapp did not start must survive whatever winapp fails at.");
    }

    [TestMethod]
    public async Task AdoptedInstance_IsReportedAsAdoptedInDiagnostics()
    {
        // A failure envelope has to say plainly that the Sandbox in play is one winapp took over, so
        // a reader knows why it is still running afterwards.
        using var harness = new AdoptionHarness();
        harness.Cli.SetRunning(ManualInstanceId);

        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        var diagnostics = harness.Backend.DescribeForDiagnostics();

        Assert.AreEqual(ManualInstanceId, diagnostics["sandboxId"]);
        Assert.AreEqual("true", diagnostics["sandboxAdopted"]);
    }

    [TestMethod]
    public async Task AdoptedInstance_GetsItsOwnBootstrapPathsRatherThanFixedOnes()
    {
        // A guest that has been managed before may already have a folder mapped at a fixed name, and
        // an agent still running out of it. A per-generation name is what makes "the folder winapp
        // just mapped" unambiguous.
        using var harness = new AdoptionHarness();
        harness.Cli.SetRunning(ManualInstanceId);

        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        var shares = GuestSharePaths(harness);

        Assert.AreEqual(2, shares.Count, "Exactly the read-only bootstrap and the writable result folder.");

        foreach (var share in shares)
        {
            Assert.AreNotEqual(@"C:\WinAppBootstrap", share, "Fixed guest paths can collide with a previous manager.");
            Assert.AreNotEqual(@"C:\WinAppBootstrapResult", share);
            StringAssert.StartsWith(share, @"C:\WinAppBootstrap", StringComparison.Ordinal);
        }

        Assert.AreEqual(2, shares.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [TestMethod]
    public async Task TwoGenerations_DoNotShareABootstrapPath()
    {
        using var first = new AdoptionHarness();
        first.Cli.SetRunning("sandbox-one");
        await first.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        using var second = new AdoptionHarness();
        second.Cli.SetRunning("sandbox-two");
        await second.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        var firstPaths = GuestSharePaths(first);
        var secondPaths = GuestSharePaths(second);

        CollectionAssert.AreNotEqual(
            firstPaths,
            secondPaths,
            "Each generation must map its own guest paths, so a stale mapping cannot be mistaken for the current one.");
    }

    [TestMethod]
    public async Task AgentLaunchCommand_PointsAtThisGenerationsFolders()
    {
        using var harness = new AdoptionHarness();
        harness.Cli.SetRunning(ManualInstanceId);

        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        var launch = harness.Cli.Operations.Single(op => op.StartsWith("launch-agent:", StringComparison.Ordinal));
        var shares = GuestSharePaths(harness);

        foreach (var share in shares)
        {
            StringAssert.Contains(
                launch,
                share,
                StringComparison.Ordinal,
                "The agent must be told the folders this generation actually mapped.");
        }
    }

    [TestMethod]
    public async Task FirewallRule_NamesThisGenerationsAgentPath()
    {
        // The rule is scoped to the agent program. Naming a stale path would authorise nothing, or
        // worse, authorise a binary from a previous generation.
        using var harness = new AdoptionHarness();
        harness.Cli.SetRunning(ManualInstanceId);

        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        var rule = harness.Cli.Operations.Single(op => op.Contains("New-NetFirewallRule", StringComparison.Ordinal));
        var bootstrapShare = GuestSharePaths(harness)
            .Single(path => !path.StartsWith(@"C:\WinAppBootstrapResult", StringComparison.Ordinal));

        StringAssert.Contains(rule, $@"{bootstrapShare}\{GuestAgentInstaller.BinaryName}", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task FirewallCleanup_CannotRemoveAnotherManagersRule()
    {
        // The rule cleanup removes existing rules for the agent program path. When that path was a
        // fixed name, a second winapp -- one whose state root was redirected, so it could not see
        // this one's ownership record -- would delete the live agent's rule and cut its connection.
        // Scoping the path to this generation is what makes that impossible.
        using var harness = new AdoptionHarness();
        harness.Cli.SetRunning(ManualInstanceId);

        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        var rule = harness.Cli.Operations.Single(op => op.Contains("New-NetFirewallRule", StringComparison.Ordinal));

        StringAssert.Contains(
            rule,
            ".Program -eq $agent",
            StringComparison.Ordinal,
            "Removal must be scoped to this generation's agent path, not to every winapp agent.");
        Assert.IsFalse(
            rule.Contains(@"'C:\WinAppBootstrap\", StringComparison.Ordinal),
            "A fixed agent path would match another generation's rule.");
    }

    [TestMethod]
    public async Task ManyGenerations_ArePrunedWithoutTouchingTheCurrentOne()
    {
        // Regression: `bootstrap-result-<token>` also starts with `bootstrap-`, so a single prefix
        // match counted result folders toward the read-only family's retention -- and could delete
        // the result folder this very run had just mapped into the guest.
        using var harness = new AdoptionHarness();
        harness.Cli.SetRunning(ManualInstanceId);

        var stateRoot = harness.TargetRoot;
        Directory.CreateDirectory(stateRoot);

        for (var generation = 0; generation < 12; generation++)
        {
            Directory.CreateDirectory(Path.Join(stateRoot, $"bootstrap-old{generation:00}0000000000000"));
            Directory.CreateDirectory(Path.Join(stateRoot, $"bootstrap-result-old{generation:00}0000000000000"));
        }

        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        var shares = GuestSharePaths(harness);
        var remaining = new DirectoryInfo(stateRoot).GetDirectories();

        foreach (var share in shares)
        {
            var name = share[@"C:\WinApp".Length..];
            var hostName = name.StartsWith("BootstrapResult-", StringComparison.Ordinal)
                ? "bootstrap-result-" + name["BootstrapResult-".Length..]
                : "bootstrap-" + name["Bootstrap-".Length..];

            Assert.IsTrue(
                remaining.Any(directory => string.Equals(directory.Name, hostName, StringComparison.OrdinalIgnoreCase)),
                $"The folder mapped into the guest as '{share}' must survive pruning.");
        }

        var bootstraps = remaining.Count(d =>
            d.Name.StartsWith("bootstrap-", StringComparison.Ordinal) &&
            !d.Name.StartsWith("bootstrap-result-", StringComparison.Ordinal));
        var results = remaining.Count(d => d.Name.StartsWith("bootstrap-result-", StringComparison.Ordinal));

        var ceiling = WindowsSandboxBackend.MaxRetainedBootstrapGenerations + 1;

        Assert.IsLessThanOrEqualTo(ceiling, bootstraps, "Stale read-only generations must be bounded.");
        Assert.IsLessThanOrEqualTo(ceiling, results, "Stale result generations must be bounded.");
    }

    /// <summary>
    /// A privileged bootstrap step that the guest ran and rejected must fail the command.
    /// </summary>
    /// <remarks>
    /// This branch was unreachable until the guest's own exit code was parsed: <c>wsb exec</c> exits
    /// 0 whenever it managed to launch the command, so reading its code reported a refused firewall
    /// rule as a success and let the bootstrap continue toward an agent nothing could reach.
    /// </remarks>
    [TestMethod]
    public async Task FirewallRuleRefusedByTheGuest_FailsAndDoesNotMarkTheTargetBootstrapped()
    {
        using var harness = new AdoptionHarness();
        harness.Cli.SetRunning(ManualInstanceId);
        harness.Cli.GuestExitCodes["New-NetFirewallRule"] = 1;

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => harness.Backend.EnsureConnectedAsync(
                new EnsureTargetOptions(true), TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.TransportFailed, failure.Error.Code);
        Assert.AreEqual("1", failure.Error.Context!["exitCode"]);

        Assert.IsFalse(
            harness.Cli.Operations.Any(op => op.StartsWith("launch-agent", StringComparison.Ordinal)),
            "The agent must not be launched onto a port nothing authorised.");
        Assert.IsNull(
            harness.ReadState()?.BootstrappedEpoch,
            "A bootstrap that failed must never be recorded as completed.");
    }

    /// <summary>
    /// Developer Mode is a registration prerequisite, so a guest refusal warns rather than fails.
    /// </summary>
    /// <remarks>
    /// Copying files and running commands do not need it. Refusing those because a packaged-run
    /// prerequisite could not be set would turn one broken capability into all of them; guest winapp
    /// reports the real problem when a packaged run is actually attempted.
    /// </remarks>
    [TestMethod]
    public async Task DeveloperModeRefusedByTheGuest_DoesNotFailTheBootstrap()
    {
        using var harness = new AdoptionHarness();
        harness.Cli.SetRunning(ManualInstanceId);
        harness.Cli.GuestExitCodes["AllowDevelopmentWithoutDevLicense"] = 5;

        // Reaching the agent launch at all is the assertion: the bootstrap kept going.
        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);
    }

    /// <summary>
    /// A closed Sandbox window is reconnected once, from the agent's own evidence.
    /// </summary>
    /// <remarks>
    /// A closed client leaves <c>ExistingLogin</c> working, so the session probe reports ready and
    /// nothing is connected; the agent then refuses with <c>NoInputDesktop</c>. That refusal is
    /// evidence rather than a guess, which is what makes reconnecting safe here and unsafe from the
    /// probe alone — a guest whose client is healthy never produces it.
    /// </remarks>
    [TestMethod]
    public async Task ClosedClient_IsReconnectedOnceAndThenWorks()
    {
        using var harness = new AdoptionHarness();
        harness.Cli.SetRunning(ManualInstanceId);
        harness.Cli.Session = GuestSessionAvailability.Ready;
        harness.Cli.AgentRefusesWithNoInputDesktop = true;
        harness.Cli.AgentReadyAfterReconnect = true;

        // Reaching the agent launch means the guest became usable without the user doing anything.
        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        Assert.AreEqual(
            1,
            harness.Cli.Operations.Count(op => op.StartsWith("connect:", StringComparison.Ordinal)),
            "Exactly one reconnect: --on sandbox fixes what it can, but never stacks clients.");
    }

    [TestMethod]
    public async Task ClosedClient_ThatStaysUnusable_FailsBoundedWithOneReconnect()
    {
        using var harness = new AdoptionHarness();
        harness.Cli.SetRunning(ManualInstanceId);
        harness.Cli.Session = GuestSessionAvailability.Ready;
        harness.Cli.AgentRefusesWithNoInputDesktop = true;

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => harness.Backend.EnsureConnectedAsync(
                new EnsureTargetOptions(true), TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.InputNotReady, failure.Error.Code);
        StringAssert.Contains(failure.Error.NextCommand!.Command, "wsb connect", StringComparison.Ordinal);
        Assert.IsTrue(failure.Error.NextCommand.Advisory);
        Assert.AreEqual(
            1,
            harness.Cli.Operations.Count(op => op.StartsWith("connect:", StringComparison.Ordinal)),
            "Recovery is one-shot: it must never reconnect repeatedly while spinning.");
        Assert.IsLessThan(
            WindowsSandboxBackend.HeartbeatTimeout,
            harness.Elapsed,
            "A reconnected client that never becomes ready must fail on the short bound, not the long one.");
    }

    [TestMethod]
    public async Task ClosedClient_WhoseReconnectFails_SurfacesThatFailure()
    {
        using var harness = new AdoptionHarness();
        harness.Cli.SetRunning(ManualInstanceId);
        harness.Cli.Session = GuestSessionAvailability.Ready;
        harness.Cli.AgentRefusesWithNoInputDesktop = true;
        harness.Cli.FailConnect = true;

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => harness.Backend.EnsureConnectedAsync(
                new EnsureTargetOptions(true), TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.NoInteractiveSession, failure.Error.Code);
    }

    [TestMethod]
    public async Task AdoptedInstance_WithAHealthyClient_IsNeverReconnected()
    {
        // The agent works, so no recovery is triggered and the user's own window is untouched.
        using var harness = new AdoptionHarness();
        harness.Cli.SetRunning(ManualInstanceId);
        harness.Cli.Session = GuestSessionAvailability.Ready;

        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        Assert.IsFalse(
            harness.Cli.Operations.Any(op => op.StartsWith("connect:", StringComparison.Ordinal)),
            "A healthy attached client must never be duplicated.");
    }

    /// <summary>
    /// An instance winapp started itself connects unless the guest positively says it has a session.
    /// </summary>
    /// <remarks>
    /// <c>wsb start</c> attaches no client, so for a Created or RecoveredStart instance the absence
    /// of one is known, not guessed. Skipping the connect on an inconclusive probe would leave the
    /// agent — which runs as <c>ExistingLogin</c> — with no session to launch into, and the whole
    /// heartbeat window would be spent discovering that.
    /// </remarks>
    [TestMethod]
    [DataRow((int)GuestSessionAvailability.Unknown, DisplayName = "probe could not answer")]
    [DataRow((int)GuestSessionAvailability.NoLoginSession, DisplayName = "probe confirmed no session")]
    public async Task CreatedInstance_ConnectsUnlessTheGuestSaysItHasASession(int session)
    {
        using var harness = new AdoptionHarness();
        harness.Cli.Session = (GuestSessionAvailability)session;

        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        Assert.AreEqual(
            1,
            harness.Cli.Operations.Count(op => op.StartsWith("connect:", StringComparison.Ordinal)),
            "A Sandbox winapp started headless needs exactly one client.");
    }

    [TestMethod]
    public async Task CreatedInstance_WhoseGuestAlreadyHasASession_IsNotConnected()
    {
        // The client installer can open a Sandbox that winapp then recovers; if a session already
        // exists, adding another client would duplicate it.
        using var harness = new AdoptionHarness();
        harness.Cli.Session = GuestSessionAvailability.Ready;

        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        Assert.IsFalse(
            harness.Cli.Operations.Any(op => op.StartsWith("connect:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task RecoveredInstance_WithAnInconclusiveProbe_IsConnected()
    {
        // Recovered from winapp's own unconfirmed start, so it was started headless too.
        using var harness = new AdoptionHarness();
        harness.Cli.SetRunning(RecoveredInstanceId);
        harness.MarkPendingStart(RecoveredInstanceId);
        harness.Cli.Session = GuestSessionAvailability.Unknown;

        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        Assert.AreEqual(
            nameof(SandboxInstanceOrigin.RecoveredStart),
            harness.ReadState()!.InstanceOrigin,
            "This test is only meaningful if the instance really was recovered.");
        Assert.AreEqual(
            1,
            harness.Cli.Operations.Count(op => op.StartsWith("connect:", StringComparison.Ordinal)));
    }

    /// <summary>The instance ID a recovered-start test claims.</summary>
    private const string RecoveredInstanceId = "winapp-started-this-one";

    private static List<string> GuestSharePaths(AdoptionHarness harness) =>
        [.. harness.Cli.Operations
            .Where(op => op.StartsWith("share|", StringComparison.Ordinal))
            .Select(op => op.Split('|')[1])
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Drives the real backend against a Sandbox it did not start, stopping at the agent launch.
    /// </summary>
    /// <remarks>
    /// The agent launch is the last step before the host would open a TCP connection to a guest that
    /// does not exist here, so the fake CLI throws a sentinel at exactly that point. Everything
    /// before it — take-over, sharing, connecting, Developer Mode, the firewall rule — is the real
    /// code path.
    /// </remarks>
    private sealed class AdoptionHarness : IDisposable
    {
        private readonly DirectoryInfo _root;
        private readonly TargetStateStore _stateStore;
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public AdoptionHarness()
        {
            _root = new DirectoryInfo(TestPaths.TempRoot(nameof(SandboxAdoptionTests)));
            _root.Create();

            var directories = new TargetStateDirectoryProvider(_root.FullName);
            _stateStore = new TargetStateStore(directories);
            var stateStore = _stateStore;
            Cli = new AdoptionSandboxCli();

            TargetRoot = directories
                .GetTargetRoot(WindowsSandboxTarget.Default, create: true)
                .FullName;

            Cli.CurrentTargetRoot = TargetRoot;

            var binary = new FileInfo(Path.Join(_root.FullName, "winapp.exe"));
            File.WriteAllText(binary.FullName, "agent");

            Backend = new WindowsSandboxBackend(
                Cli,
                new WindowsSandboxLifecycle(Cli, stateStore),
                directories,
                new StaticBinaryProvider(binary),
                new NoOpWindowController(),
                setup: null,
                stateStore)
            {
                UtcNow = () => _now,
            };

            // Agent-readiness bounds are minutes long. Advancing a fake clock inside the delay is
            // what lets them be asserted in milliseconds.
            Backend.Delay = (delay, _) =>
            {
                _now += delay;
                return Task.CompletedTask;
            };
        }

        public AdoptionSandboxCli Cli { get; }

        public WindowsSandboxBackend Backend { get; }

        /// <summary>Where this target's per-generation bootstrap folders live.</summary>
        public string TargetRoot { get; }

        /// <summary>The ownership record as another winapp process would read it.</summary>
        public TargetState? ReadState() => _stateStore.Read(WindowsSandboxTarget.Default);

        /// <summary>How much fake time the run consumed, for asserting which bound was hit.</summary>
        public TimeSpan Elapsed => _now - new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        /// <summary>Records an unconfirmed start, so the next prepare recovers rather than creates.</summary>
        public void MarkPendingStart(string instanceId) =>
            _stateStore.Commit(
                WindowsSandboxTarget.Default,
                new TargetState
                {
                    SchemaVersion = 0,
                    Revision = 0,
                    TargetKind = WindowsSandboxTarget.Default.Kind,
                    TargetId = WindowsSandboxTarget.Default.Id,
                    PendingInstanceId = instanceId,
                    PendingStartedUtc = _now,
                },
                expectedRevision: 0);

        public async Task RunUntilAgentLaunchAsync(CancellationToken cancellationToken)
        {
            await Assert.ThrowsExactlyAsync<AgentLaunchReached>(
                () => Backend.EnsureConnectedAsync(new EnsureTargetOptions(true), cancellationToken));
        }

        public void Dispose()
        {
            try
            {
                _root.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Temp cleanup is not worth failing a test over.
            }
        }
    }

    /// <summary>Marks the point the harness stops, rather than a failure under test.</summary>
    private sealed class AgentLaunchReached : Exception;

    /// <summary>A Sandbox CLI that records everything, including anything it was asked to stop.</summary>
    private sealed class AdoptionSandboxCli : IWindowsSandboxCli
    {
        private readonly List<string> _running = [];

        public bool IsAvailable => true;

        public List<string> Operations { get; } = [];

        public List<string> Stopped { get; } = [];

        public void UseExecutable(string executablePath)
        {
        }

        public void SetRunning(params string[] ids)
        {
            _running.Clear();
            _running.AddRange(ids);
        }

        public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([.. _running]);

        public Task<string> StartAsync(string instanceId, string? configuration, CancellationToken cancellationToken)
        {
            Operations.Add($"start:{instanceId}");
            _running.Add(instanceId);
            return Task.FromResult(instanceId);
        }

        public Task StopAsync(string id, CancellationToken cancellationToken)
        {
            Stopped.Add(id);
            _running.Remove(id);
            return Task.CompletedTask;
        }

        public Task<bool> IsResolvableAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(_running.Contains(id, StringComparer.OrdinalIgnoreCase));

        public Task<string> GetIpAddressAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult("172.27.0.2");

        /// <summary>What the guest reports when asked whether it already has a login session.</summary>
        public GuestSessionAvailability Session { get; set; } = GuestSessionAvailability.NoLoginSession;

        public Task<GuestSessionAvailability> ProbeInteractiveSessionAsync(
            string id,
            CancellationToken cancellationToken)
        {
            Operations.Add($"probe-session:{id}");
            return Task.FromResult(Session);
        }

        public Task ShareFolderAsync(
            string id,
            string hostPath,
            string sandboxPath,
            bool allowWrite,
            CancellationToken cancellationToken)
        {
            Operations.Add($"share|{sandboxPath}|{allowWrite}");
            return Task.CompletedTask;
        }

        public Task<SandboxConnectAttempt> ConnectAsync(
            string id,
            Action<SandboxConnectAttempt> onLaunched,
            CancellationToken cancellationToken)
        {
            Operations.Add($"connect:{id}");

            if (FailConnect)
            {
                throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.NoInteractiveSession,
                    "The Windows Sandbox client could not connect to the Sandbox.");
            }

            var attempt = SandboxConnectAttempt.ForLauncher(LauncherProcessId, LauncherStartTicks);
            onLaunched(attempt);
            return Task.FromResult(attempt);
        }

        /// <summary>The 'wsb connect' this fake reports having launched.</summary>
        public int LauncherProcessId { get; set; } = 4242;

        /// <summary>When that launcher started, which its client must not predate.</summary>
        public long LauncherStartTicks { get; set; } = 1_000_000;

        public Task<int> ExecuteAsync(
            string id,
            string command,
            string? workingDirectory,
            bool asSystem,
            CancellationToken cancellationToken)
        {
            Operations.Add(command);

            // Lets a test make one specific privileged bootstrap step fail the way a guest would:
            // dispatched fine, non-zero exit.
            foreach (var (fragment, exitCode) in GuestExitCodes)
            {
                if (command.Contains(fragment, StringComparison.Ordinal))
                {
                    return Task.FromResult(exitCode);
                }
            }

            return Task.FromResult(0);
        }

        /// <summary>Guest exit codes keyed by a fragment of the command that produces them.</summary>
        public Dictionary<string, int> GuestExitCodes { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// When set, the agent publishes a <c>NoInputDesktop</c> refusal instead of a heartbeat.
        /// </summary>
        /// <remarks>
        /// What a guest with a login session but no attached client actually does: it starts, finds
        /// no input desktop, says so, and exits.
        /// </remarks>
        public bool AgentRefusesWithNoInputDesktop { get; set; }

        public Task LaunchAgentAsync(string id, string command, CancellationToken cancellationToken)
        {
            Operations.Add($"launch-agent:{command}");

            var connects = Operations.Count(op => op.StartsWith("connect:", StringComparison.Ordinal));

            if (AgentRefusesWithNoInputDesktop && !(AgentReadyAfterReconnect && connects > 0))
            {
                PublishNoInputDesktopHeartbeat(command);
                return Task.CompletedTask;
            }

            // Everything after this point needs a guest that does not exist in a unit test.
            throw new AgentLaunchReached();
        }

        /// <summary>
        /// When set alongside <see cref="AgentRefusesWithNoInputDesktop"/>, the agent starts working
        /// once a client has been connected — which is what reconnecting a closed window achieves.
        /// </summary>
        public bool AgentReadyAfterReconnect { get; set; }

        /// <summary>When set, <see cref="ConnectAsync"/> fails the way a refused client would.</summary>
        public bool FailConnect { get; set; }

        /// <summary>Writes the refusal the real agent would publish into the result share.</summary>
        /// <remarks>
        /// The epoch is read back from the connection material the host just staged, exactly as the
        /// real agent does, because the boot nonce inside it is generated during the run and cannot
        /// be known in advance.
        /// </remarks>
        private void PublishNoInputDesktopHeartbeat(string command)
        {
            const string Flag = "--result-dir \"";

            var start = command.IndexOf(Flag, StringComparison.Ordinal) + Flag.Length;
            var guestResult = command[start..command.IndexOf('"', start)];
            var token = guestResult[(guestResult.LastIndexOf('-') + 1)..];

            var hostResult = Path.Join(CurrentTargetRoot!, "bootstrap-result-" + token);
            var material = GuestBootstrapMaterial.TryParse(
                File.ReadAllText(Path.Join(CurrentTargetRoot!, "bootstrap-" + token, GuestBootstrapMaterial.FileName)))!;

            var heartbeat = GuestAgentHeartbeat.Create(
                new GuestAgentIdentity("test", "hash", "arm64", 1, 1),
                GuestReadinessFailure.NoInputDesktop,
                new ExecutionTargetEpoch(material.TargetEpoch),
                port: 0,
                DateTimeOffset.UtcNow);

            File.WriteAllText(
                Path.Join(hostResult, WindowsSandboxBackend.HeartbeatFileName),
                heartbeat.ToJson());
        }

        /// <summary>Target root the harness is driving, so the heartbeat lands in the right share.</summary>
        public string? CurrentTargetRoot { get; set; }
    }

    private sealed class StaticBinaryProvider(FileInfo binary) : IHostWinappBinaryProvider
    {
        public FileInfo GetBinary() => binary;
    }

    private sealed class NoOpWindowController : IWindowsSandboxWindowController
    {
        public WindowsSandboxWindowSnapshot Capture() => new(default);

        public Task<SandboxClientWindow?> PlaceConnectedClientAsync(
            WindowsSandboxWindowSnapshot snapshot,
            SandboxConnectAttempt attempt,
            CancellationToken cancellationToken) => Task.FromResult<SandboxClientWindow?>(null);

        public SandboxClientWindow ResolveClient(SandboxClientWindow? remembered) =>
            throw new NotSupportedException("These tests never capture the target's desktop.");
    }

    /// <summary>MSTest injects this; used for per-test cancellation.</summary>
    public TestContext TestContext { get; set; } = null!;
}

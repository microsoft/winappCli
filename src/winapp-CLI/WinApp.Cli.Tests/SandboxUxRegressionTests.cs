// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

/// <summary>
/// Regressions found by running the feature against a real application, where each fault was a
/// usability failure rather than a wrong result.
/// </summary>
/// <remarks>
/// Every one of these passed the existing suite: the deterministic tests drove orchestration through
/// an in-memory transport, which is exactly the layer that hides a firewall prompt, a client
/// reconnect, a silent terminal, and a backend field that cannot survive process exit. These assert
/// the observable behaviour a user actually meets.
/// </remarks>
[TestClass]
public class SandboxUxRegressionTests
{
    public TestContext TestContext { get; set; } = null!;

    // ---- The guest agent must never trip the Windows Firewall consent dialog ----

    /// <summary>
    /// The inbound rule is created before the agent is launched, not after it reports a port.
    /// </summary>
    /// <remarks>
    /// Windows raises "Windows Firewall has blocked some features of this app" at the moment a
    /// program binds a listening socket with no matching rule. The rule therefore has to exist
    /// first; creating it afterwards — however correct the rule — cannot prevent a prompt that has
    /// already appeared. Ordering is the entire fix, so ordering is what this asserts.
    /// </remarks>
    [TestMethod]
    public async Task FirewallRule_IsCreatedBeforeTheAgentStartsListening()
    {
        using var harness = new BackendHarness();

        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        var firewall = harness.Cli.Operations.FindIndex(op => op.Contains("New-NetFirewallRule", StringComparison.Ordinal));
        var launch = harness.Cli.Operations.FindIndex(op => op.StartsWith("launch-agent", StringComparison.Ordinal));

        Assert.IsGreaterThanOrEqualTo(0, firewall, "The inbound allow rule must be created.");
        Assert.IsGreaterThanOrEqualTo(0, launch, "The agent must be launched.");
        Assert.IsLessThan(
            launch,
            firewall,
            "The firewall rule must exist before the agent binds its socket, or Windows prompts the user.");
    }

    /// <summary>
    /// The rule names the exact port the agent is told to use, and is scoped to the agent program.
    /// </summary>
    /// <remarks>
    /// The host now chooses the port instead of letting the agent bind port 0 and reporting back,
    /// because a rule cannot name a port nobody has chosen yet. Asserting that the material and the
    /// rule agree is what proves the two halves of that change stayed in step.
    /// </remarks>
    [TestMethod]
    public async Task FirewallRule_AuthorisesExactlyTheAssignedPortAndProgram()
    {
        using var harness = new BackendHarness();

        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        var material = harness.ReadStagedMaterial();

        Assert.IsNotNull(material, "The bootstrap material must be staged before the agent is launched.");
        Assert.IsGreaterThanOrEqualTo(
            WindowsSandboxBackend.MinAgentPort,
            material.Port,
            "The agent port must be assigned by the host, not left as 0.");
        Assert.IsLessThanOrEqualTo(WindowsSandboxBackend.MaxAgentPort, material.Port);

        var rule = harness.Cli.Operations.Single(op => op.Contains("New-NetFirewallRule", StringComparison.Ordinal));

        StringAssert.Contains(rule, $"-LocalPort {material.Port}");
        StringAssert.Contains(rule, "-Program $agent");
        StringAssert.Contains(rule, "-Direction Inbound");
        StringAssert.Contains(rule, "-Protocol TCP");
    }

    /// <summary>The assigned port stays inside the dynamic range.</summary>
    [TestMethod]
    public void AgentPort_IsAlwaysInsideTheDynamicRange()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var port = WindowsSandboxBackend.NextAgentPort();

            Assert.IsGreaterThanOrEqualTo(WindowsSandboxBackend.MinAgentPort, port);
            Assert.IsLessThanOrEqualTo(WindowsSandboxBackend.MaxAgentPort, port);
        }
    }

    // ---- A second command must not disconnect the Sandbox window the first one left up ----

    /// <summary>
    /// A read-only command against a reused instance never reconnects the Sandbox client.
    /// </summary>
    /// <remarks>
    /// <c>wsb connect</c> against an instance whose client is already up tears that session down and
    /// shows the user "the connection to the Windows Sandbox environment was lost. Do you want to
    /// reconnect?". That is what made every follow-up command disruptive, and it is why read-only
    /// verbs must not ask for an interactive desktop they do not need.
    /// </remarks>
    [TestMethod]
    public async Task ReadOnlyCommand_OnAReusedInstance_DoesNotReconnectTheClient()
    {
        using var harness = new BackendHarness();
        harness.MarkInstanceAlreadyRunning();

        await harness.RunUntilAgentLaunchAsync(
            TestContext.CancellationToken,
            requireInteractiveDesktop: false);

        Assert.IsFalse(
            harness.Cli.Operations.Any(op => op.StartsWith("connect", StringComparison.Ordinal)),
            "Reusing a running Sandbox for a read-only command must not reconnect its client.");
    }

    /// <summary>
    /// An interrupted first bootstrap must not make the next command think the guest is ready.
    /// </summary>
    /// <remarks>
    /// Ownership is committed before the guest is prepared, so a command killed part-way through
    /// leaves an instance that is owned and listed but has no connected client. Treating that as a
    /// warm reuse made the next read-only command skip <c>wsb connect</c> and then launch the agent
    /// as <c>ExistingLogin</c> into a session nothing had established.
    /// </remarks>
    [TestMethod]
    public async Task ReadOnlyCommand_AfterAnInterruptedFirstBootstrap_StillConnectsTheClient()
    {
        using var harness = new BackendHarness();
        harness.MarkInstanceOwnedButNeverBootstrapped();

        await harness.RunUntilAgentLaunchAsync(
            TestContext.CancellationToken,
            requireInteractiveDesktop: false);

        Assert.IsTrue(
            harness.Cli.Operations.Any(op => op.StartsWith("connect", StringComparison.Ordinal)),
            "A guest whose bootstrap never finished has no interactive session yet.");
    }

    /// <summary>A first bootstrap still connects the client, because there is no session yet.</summary>
    [TestMethod]
    public async Task FirstBootstrap_StillConnectsTheClient()
    {
        using var harness = new BackendHarness();

        await harness.RunUntilAgentLaunchAsync(
            TestContext.CancellationToken,
            requireInteractiveDesktop: false);

        Assert.IsTrue(
            harness.Cli.Operations.Any(op => op.StartsWith("connect", StringComparison.Ordinal)),
            "A brand-new instance has no interactive session until a client connects.");
    }

    [TestMethod]
    public async Task ConnectCancellationAfterPlacement_PersistsTheParkedClient()
    {
        var client = new SandboxClientWindow((nint)0x1234, 4321, 638_900_000_000_000_000);
        var windows = new PlacedWindowController(client);
        using var harness = new BackendHarness(windows);
        harness.Cli.ConnectFailure = new OperationCanceledException(new CancellationToken(canceled: true));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => harness.Backend.EnsureConnectedAsync(
                new EnsureTargetOptions(RequireInteractiveDesktop: true),
                TestContext.CancellationToken));

        Assert.AreEqual(client, harness.ReadClient());
        Assert.AreEqual(client, windows.Placed);
    }

    [TestMethod]
    public async Task ConnectFastFailureAfterPlacement_PersistsTheParkedClient()
    {
        var client = new SandboxClientWindow((nint)0x5678, 8765, 638_900_000_100_000_000);
        var windows = new PlacedWindowController(client);
        using var harness = new BackendHarness(windows);
        harness.Cli.ConnectFailure = ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.NoInteractiveSession,
            "The Windows Sandbox client could not connect.");

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => harness.Backend.EnsureConnectedAsync(
                new EnsureTargetOptions(RequireInteractiveDesktop: true),
                TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.NoInteractiveSession, failure.Error.Code);
        Assert.AreEqual(client, harness.ReadClient());
        Assert.AreEqual(client, windows.Placed);
    }

    /// <summary>
    /// Connection material survives process exit, so the next CLI process can reuse the agent.
    /// </summary>
    /// <remarks>
    /// The backend used to hold the address and material in instance fields. Every invocation builds
    /// a new backend, so those were always null on the second command: it re-staged and relaunched
    /// the agent every single time, which is what made each command feel like a cold start. Reading
    /// the material back off disk is what makes cross-process reuse possible at all.
    /// </remarks>
    [TestMethod]
    public async Task BootstrapMaterial_IsReadableByALaterProcess()
    {
        using var harness = new BackendHarness();

        await harness.RunUntilAgentLaunchAsync(TestContext.CancellationToken);

        // A second backend over the same state root is exactly what the next CLI process is.
        var material = harness.CreateSecondBackendView().ReadStagedMaterial();

        Assert.IsNotNull(material, "A later process must be able to read the connection material.");
        Assert.AreEqual(harness.Epoch.Value, material.TargetEpoch);
        Assert.AreNotEqual(0, material.Port);
        Assert.IsNotEmpty(material.PreSharedKey);
    }

    // ---- UI verbs must be classified by what they actually do ----

    /// <summary>Read-only verbs neither require an interactive desktop nor assert real input.</summary>
    /// <remarks>
    /// <c>ui inspect --on sandbox</c> reads the UI Automation tree. Routing it as a real-input command
    /// forced a client reconnect and gated it on an input desktop, which is what made it hang and
    /// then report a bare cancellation.
    /// </remarks>
    [TestMethod]
    [DataRow("inspect")]
    [DataRow("search")]
    [DataRow("get-property")]
    [DataRow("get-focused")]
    [DataRow("list-windows")]
    [DataRow("wait-for")]
    [DataRow("status")]
    public void ReadOnlyUiVerbs_AreNotGatedOnRealInput(string verb)
    {
        var requirements = TargetUiRequirements.For(ParseUi(verb));

        Assert.IsFalse(requirements.RequiresInteractiveDesktop, $"'{verb}' only reads UI Automation state.");
        Assert.IsFalse(requirements.RequiresRealInput, $"'{verb}' injects no input.");
    }

    /// <summary>Anything that injects input or captures pixels keeps the stricter treatment.</summary>
    [TestMethod]
    [DataRow("invoke")]
    [DataRow("set-value")]
    [DataRow("send-keys")]
    [DataRow("touch")]
    [DataRow("pen")]
    [DataRow("drag")]
    [DataRow("hover")]
    [DataRow("screenshot")]
    [DataRow("record")]
    [DataRow("focus")]
    [DataRow("scroll")]
    public void InputAndCaptureUiVerbs_StillRequireAnInteractiveDesktop(string verb)
    {
        var requirements = TargetUiRequirements.For(ParseUi(verb));

        Assert.IsTrue(requirements.RequiresInteractiveDesktop, $"'{verb}' needs a connected client.");
        Assert.IsTrue(requirements.RequiresRealInput, $"'{verb}' must re-probe input readiness.");
    }

    /// <summary>An unrecognized verb is treated strictly rather than assumed harmless.</summary>
    /// <remarks>
    /// A new verb added later defaults to the safe side: the cost of being wrong is a client
    /// reconnect, whereas the cost of wrongly assuming a verb is read-only is a command that reports
    /// input it never delivered.
    /// </remarks>
    [TestMethod]
    public void UnknownUiVerb_IsTreatedAsRequiringInput()
    {
        var command = new Command("ui");
        var future = new Command("some-future-verb");
        command.Subcommands.Add(future);

        var requirements = TargetUiRequirements.For(command.Parse(["some-future-verb"]));

        Assert.IsTrue(requirements.RequiresInteractiveDesktop);
        Assert.IsTrue(requirements.RequiresRealInput);
    }

    // ---- Slow phases must announce themselves ----

    /// <summary>Progress is reported to the error stream, never to standard output.</summary>
    /// <remarks>
    /// The stream is injected rather than redirected globally: replacing <see cref="Console.Out"/>
    /// would race every other test in this parallel run, and a flaky assertion about output would be
    /// worse than none.
    /// </remarks>
    [TestMethod]
    public void StandardErrorProgress_WritesToTheErrorStreamNeverStandardOutput()
    {
        using var error = new StringWriter();

        new StandardErrorTargetProgress(() => error).Report("Starting Windows Sandbox...");

        StringAssert.Contains(error.ToString(), "Starting Windows Sandbox...");
    }

    /// <summary>The production default targets standard error rather than standard output.</summary>
    /// <remarks>
    /// A progress line on stdout would corrupt the single JSON document a <c>--json</c> caller
    /// parses, so the default destination is asserted directly.
    /// </remarks>
    [TestMethod]
    public async Task StandardErrorProgress_DefaultsToConsoleError()
    {
        var source = await File.ReadAllTextAsync(
            Path.Join(
                FindRepositoryRoot(),
                "src", "winapp-CLI", "WinApp.Cli", "ExecutionTargets", "Abstractions", "ITargetProgress.cs"),
            TestContext.CancellationToken);

        StringAssert.Contains(source, "() => Console.Error");
        Assert.IsFalse(
            source.Contains("Console.Out", StringComparison.Ordinal),
            "Progress must never be written to standard output.");
    }

    /// <summary>An empty message is not written at all.</summary>
    [TestMethod]
    public void StandardErrorProgress_IgnoresEmptyMessages()
    {
        using var error = new StringWriter();

        new StandardErrorTargetProgress(() => error).Report("   ");

        Assert.IsEmpty(error.ToString());
    }

    /// <summary>
    /// The router actually consults the classification rather than hard-coding real input.
    /// </summary>
    /// <remarks>
    /// The classification above is only worth anything if the router uses it. The original defect
    /// was precisely this wiring: the router had a comment explaining that inspection needs no
    /// interactive desktop, and then passed <c>Interactive</c> and <c>RequiresRealInput = true</c>
    /// unconditionally anyway. A classifier unit test cannot see that, and a transport-level test
    /// cannot either, so the call site is asserted directly.
    /// </remarks>
    [TestMethod]
    public async Task Router_DerivesItsTargetOptionsAndInputFlagFromTheVerb()
    {
        var source = await File.ReadAllTextAsync(
            Path.Join(
                FindRepositoryRoot(),
                "src", "winapp-CLI", "WinApp.Cli", "Commands", "ExecutionTargetUiRouter.cs"),
            TestContext.CancellationToken);

        StringAssert.Contains(
            source,
            "requirements.RequiresRealInput",
            "The guest request must carry the verb's own input requirement, not a constant.");

        StringAssert.Contains(
            source,
            "PrepareTargetOptions.ReadOnly",
            "A read-only verb must be able to prepare the target without an interactive desktop.");

        Assert.IsFalse(
            source.Contains("RequiresRealInput = true,", StringComparison.Ordinal),
            "No routed verb may assert real input unconditionally.");
    }

    /// <summary>
    /// A cancellation the user never requested is reported as a target failure, not as
    /// "OperationCanceled".
    /// </summary>
    [TestMethod]
    public async Task Router_TranslatesInternalCancellationIntoASpecificError()
    {
        var source = await File.ReadAllTextAsync(
            Path.Join(
                FindRepositoryRoot(),
                "src", "winapp-CLI", "WinApp.Cli", "Commands", "ExecutionTargetUiRouter.cs"),
            TestContext.CancellationToken);

        StringAssert.Contains(
            source,
            "catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)",
            "An internal timeout must be distinguished from the user pressing Ctrl+C.");
    }

    /// <summary>
    /// Material from a build that let the agent pick its own port is not reused.
    /// </summary>
    /// <remarks>
    /// Earlier builds wrote <c>port: 0</c> and learned the real port from the heartbeat. Port 0 also
    /// happens to equal <see cref="System.Net.IPEndPoint.MinPort"/>, so a naive range check accepts
    /// it — and the host would then try to connect to a port nothing can listen on. Upgrading over a
    /// running Sandbox has to repair the agent instead.
    /// </remarks>
    [TestMethod]
    public async Task StaleMaterialWithoutAnAssignedPort_IsNotReused()
    {
        using var harness = new BackendHarness();
        harness.MarkInstanceAlreadyRunning();
        harness.WriteLegacyMaterial(port: 0);

        // Reaching the agent launch at all proves the backend repaired rather than trying to
        // reconnect to port 0, which would have blocked until the connect timeout elapsed.
        await harness.RunUntilAgentLaunchAsync(
            TestContext.CancellationToken,
            requireInteractiveDesktop: false);

        var material = harness.ReadStagedMaterial();

        Assert.IsNotNull(material);
        Assert.IsGreaterThanOrEqualTo(WindowsSandboxBackend.MinAgentPort, material.Port);
    }

    /// <summary>
    /// Committing target state preserves the guest address rather than silently dropping it.
    /// </summary>
    /// <remarks>
    /// <see cref="TargetStateStore.Commit"/> rebuilds the record field by field instead of copying
    /// it, so a field added to <see cref="TargetState"/> but not to that constructor is discarded on
    /// every write — silently, because the caller's own object still holds the value it just set.
    /// That is exactly what happened to the address cache: the code looked correct, the unit tests
    /// passed, and the field simply never reached disk. Live state was the only thing that showed it.
    /// </remarks>
    [TestMethod]
    public void CommittedTargetState_PreservesEveryPersistedField()
    {
        var root = new DirectoryInfo(TestPaths.TempRoot(nameof(CommittedTargetState_PreservesEveryPersistedField)));
        root.Create();

        try
        {
            var store = new TargetStateStore(new TargetStateDirectoryProvider(root.FullName));
            var target = WindowsSandboxTarget.Default;

            store.Commit(
                target,
                new TargetState
                {
                    SchemaVersion = 0,
                    Revision = 0,
                    TargetKind = target.Kind,
                    TargetId = target.Id,
                    InstanceId = "sandbox-1",
                    BootNonce = "nonce-1",
                    AgentVersion = "1.2.3",
                    AgentBinaryHash = "abc123",
                    GuestAddress = "172.27.0.9",
                },
                expectedRevision: 0);

            // Read back through the store, so this asserts what actually reached disk.
            var persisted = store.Read(target);

            Assert.IsNotNull(persisted);
            Assert.AreEqual("sandbox-1", persisted.InstanceId);
            Assert.AreEqual("nonce-1", persisted.BootNonce);
            Assert.AreEqual("1.2.3", persisted.AgentVersion);
            Assert.AreEqual("abc123", persisted.AgentBinaryHash);
            Assert.AreEqual("172.27.0.9", persisted.GuestAddress);
        }
        finally
        {
            try
            {
                root.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Temp cleanup is not worth failing a test over.
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "scripts", "build-cli.ps1")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the winapp repository root.");
    }

    private static ParseResult ParseUi(string verb)
    {
        var ui = new Command("ui");
        var child = new Command(verb);
        ui.Subcommands.Add(child);

        return ui.Parse([verb]);
    }

    /// <summary>
    /// Drives the real backend far enough to observe bootstrap ordering, then stops deterministically.
    /// </summary>
    /// <remarks>
    /// The agent launch is the last step before the host would open a TCP connection to a guest that
    /// does not exist here, so the fake CLI throws a sentinel at exactly that point. That keeps the
    /// test fast and deterministic while still exercising the real ordering, which a mock of the
    /// backend itself could not do.
    /// </remarks>
    private sealed class BackendHarness : IDisposable
    {
        private readonly DirectoryInfo _root;
        private readonly TargetStateDirectoryProvider _directories;
        private readonly TargetStateStore _stateStore;

        private readonly IWindowsSandboxWindowController _windowController;

        public BackendHarness(IWindowsSandboxWindowController? windowController = null)
        {
            _root = new DirectoryInfo(TestPaths.TempRoot(nameof(SandboxUxRegressionTests)));
            _root.Create();

            _directories = new TargetStateDirectoryProvider(_root.FullName);
            _stateStore = new TargetStateStore(_directories);
            Cli = new RecordingSandboxCli();
            _windowController = windowController ?? new NoOpWindowController();

            var binary = new FileInfo(Path.Join(_root.FullName, "winapp.exe"));
            File.WriteAllText(binary.FullName, "agent");

            Backend = Create(binary);
        }

        public RecordingSandboxCli Cli { get; }

        public WindowsSandboxBackend Backend { get; }

        public ExecutionTargetEpoch Epoch { get; private set; }

        public SandboxClientWindow? ReadClient()
        {
            var state = _stateStore.Read(WindowsSandboxTarget.Default);
            return state is
            {
                ClientOwnedByWinapp: true,
                ClientWindowHandle: { } handle,
                ClientProcessId: { } processId,
                ClientProcessStartTicksUtc: { } startTicksUtc,
            }
                ? new SandboxClientWindow((nint)handle, processId, startTicksUtc)
                : null;
        }

        /// <summary>Pretends a managed instance from a previous command is still running.</summary>
        /// <remarks>
        /// Records the completed-bootstrap marker as well as ownership, because that is what a
        /// previous command that actually finished would have left. Ownership alone now means
        /// "winapp's, but never prepared", which correctly is not a warm reuse.
        /// </remarks>
        public void MarkInstanceAlreadyRunning()
        {
            const string InstanceId = "sandbox-existing";

            Cli.SetRunning(InstanceId);

            // A previously bootstrapped instance has a client attached, so its guest already has an
            // interactive session -- which is what makes reconnecting it both unnecessary and
            // harmful.
            Cli.Session = GuestSessionAvailability.Ready;

            _stateStore.Commit(
                WindowsSandboxTarget.Default,
                new TargetState
                {
                    SchemaVersion = 0,
                    Revision = 0,
                    TargetKind = WindowsSandboxTarget.Default.Kind,
                    TargetId = WindowsSandboxTarget.Default.Id,
                    InstanceId = InstanceId,
                    BootNonce = "nonce-existing",
                    BootstrappedEpoch = ExecutionTargetEpoch.Create(InstanceId, "nonce-existing").Value,
                },
                expectedRevision: 0);
        }

        /// <summary>
        /// Pretends a previous command claimed an instance but was killed before it finished
        /// bootstrapping it.
        /// </summary>
        public void MarkInstanceOwnedButNeverBootstrapped()
        {
            const string InstanceId = "sandbox-existing";

            Cli.SetRunning(InstanceId);
            _stateStore.Commit(
                WindowsSandboxTarget.Default,
                new TargetState
                {
                    SchemaVersion = 0,
                    Revision = 0,
                    TargetKind = WindowsSandboxTarget.Default.Kind,
                    TargetId = WindowsSandboxTarget.Default.Id,
                    InstanceId = InstanceId,
                    BootNonce = "nonce-existing",
                },
                expectedRevision: 0);
        }

        public async Task RunUntilAgentLaunchAsync(
            CancellationToken cancellationToken,
            bool requireInteractiveDesktop = true)
        {
            await Assert.ThrowsExactlyAsync<AgentLaunchReached>(
                () => Backend.EnsureConnectedAsync(
                    new EnsureTargetOptions(requireInteractiveDesktop),
                    cancellationToken));

            Epoch = new ExecutionTargetEpoch(ReadStagedMaterial()!.TargetEpoch);
        }

        /// <summary>Writes material in the shape an older build produced, for upgrade coverage.</summary>
        public void WriteLegacyMaterial(int port)
        {
            var epoch = ExecutionTargetEpoch.Create("sandbox-existing", "nonce-existing");
            var bootstrap = Path.Join(
                _directories.GetTargetRoot(WindowsSandboxTarget.Default, create: true).FullName,
                "bootstrap-" + WindowsSandboxBackend.EpochToken(epoch));

            Directory.CreateDirectory(bootstrap);

            var material = GuestBootstrapMaterial.Create(WindowsSandboxTarget.Default, epoch, port);

            File.WriteAllText(Path.Join(bootstrap, GuestBootstrapMaterial.FileName), material.ToJson());
        }

        /// <summary>The material a later process would find on disk.</summary>
        /// <remarks>
        /// Located by searching rather than by a fixed name, because each generation now writes into
        /// its own epoch-named folder. Finding exactly one is itself part of what is asserted: a run
        /// that left two generations behind would be a real defect.
        /// </remarks>
        public GuestBootstrapMaterial? ReadStagedMaterial()
        {
            var root = _directories.GetTargetRoot(WindowsSandboxTarget.Default, create: false);

            if (!root.Exists)
            {
                return null;
            }

            var path = root
                .GetDirectories("bootstrap-*")
                .Where(directory => !directory.Name.StartsWith("bootstrap-result-", StringComparison.Ordinal))
                .Select(directory => Path.Join(directory.FullName, GuestBootstrapMaterial.FileName))
                .FirstOrDefault(File.Exists);

            return path is not null ? GuestBootstrapMaterial.TryParse(File.ReadAllText(path)) : null;
        }

        /// <summary>A second backend over the same state root, standing in for the next CLI process.</summary>
        public BackendHarness CreateSecondBackendView() => this;

        private WindowsSandboxBackend Create(FileInfo binary) =>
            new(
                Cli,
                new WindowsSandboxLifecycle(Cli, _stateStore),
                _directories,
                new StaticBinaryProvider(binary),
                _windowController,

                // No setup runner: this harness models a host where wsb.exe is already usable, so
                // the support probe short-circuits on IsAvailable before setup would be consulted.
                setup: null,
                _stateStore);

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

    /// <summary>A Sandbox CLI that records the order of everything it was asked to do.</summary>
    private sealed class RecordingSandboxCli : IWindowsSandboxCli
    {
        private readonly List<string> _running = [];

        public bool IsAvailable => true;

        public void UseExecutable(string executablePath)
        {
        }

        /// <summary>Every operation, in the order it happened.</summary>
        public List<string> Operations { get; } = [];

        public void SetRunning(params string[] ids)
        {
            _running.Clear();
            _running.AddRange(ids);
        }

        public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([.. _running]);

        public Task<string> StartAsync(string instanceId, string? configuration, CancellationToken cancellationToken)
        {
            Operations.Add("start");
            _running.Add(instanceId);
            return Task.FromResult(instanceId);
        }

        public Task<bool> IsResolvableAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(_running.Contains(id, StringComparer.OrdinalIgnoreCase));

        public Task StopAsync(string id, CancellationToken cancellationToken)
        {
            Operations.Add($"stop:{id}");
            _running.Remove(id);
            return Task.CompletedTask;
        }

        public Task<string> GetIpAddressAsync(string id, CancellationToken cancellationToken)
        {
            Operations.Add("get-ip");
            return Task.FromResult("172.27.0.2");
        }

        /// <summary>What the guest reports when asked whether it already has a login session.</summary>
        /// <remarks>
        /// Defaults to "nobody has connected yet", which is what a Sandbox started by <c>wsb start</c>
        /// actually reports until a client attaches.
        /// </remarks>
        public GuestSessionAvailability Session { get; set; } = GuestSessionAvailability.NoLoginSession;

        public Exception? ConnectFailure { get; set; }

        public Task<GuestSessionAvailability> ProbeInteractiveSessionAsync(
            string id,
            CancellationToken cancellationToken)
        {
            Operations.Add("probe-session");
            return Task.FromResult(Session);
        }

        public Task ShareFolderAsync(
            string id,
            string hostPath,
            string sandboxPath,
            bool allowWrite,
            CancellationToken cancellationToken)
        {
            Operations.Add($"share:{sandboxPath}:{allowWrite}");
            return Task.CompletedTask;
        }

        public Task<SandboxConnectAttempt> ConnectAsync(
            string id,
            Action<SandboxConnectAttempt> onLaunched,
            CancellationToken cancellationToken)
        {
            Operations.Add($"connect:{id}");
            var attempt = SandboxConnectAttempt.ForLauncher(4242, 1_000_000);
            onLaunched(attempt);
            return ConnectFailure is null
                ? Task.FromResult(attempt)
                : Task.FromException<SandboxConnectAttempt>(ConnectFailure);
        }

        public Task<int> ExecuteAsync(
            string id,
            string command,
            string? workingDirectory,
            bool asSystem,
            CancellationToken cancellationToken)
        {
            Operations.Add(command);
            return Task.FromResult(0);
        }

        public Task LaunchAgentAsync(string id, string command, CancellationToken cancellationToken)
        {
            Operations.Add($"launch-agent:{command}");

            // Everything after this point needs a guest that does not exist in a unit test.
            throw new AgentLaunchReached();
        }
    }

    private sealed class StaticBinaryProvider(FileInfo binary) : IHostWinappBinaryProvider
    {
        public FileInfo GetBinary() => binary;
    }

    private sealed class NoOpWindowController : IWindowsSandboxWindowController
    {
        public WindowsSandboxWindowSnapshot Capture() =>
            new(default);

        public Task<SandboxClientWindow?> PlaceConnectedClientAsync(
            WindowsSandboxWindowSnapshot snapshot,
            SandboxConnectAttempt attempt,
            CancellationToken cancellationToken) => Task.FromResult<SandboxClientWindow?>(null);

        public SandboxClientWindow ResolveClient(SandboxClientWindow? remembered) =>
            throw new NotSupportedException("These tests never capture the target's desktop.");
    }

    private sealed class PlacedWindowController(SandboxClientWindow client) : IWindowsSandboxWindowController
    {
        public SandboxClientWindow? Placed { get; private set; }

        public WindowsSandboxWindowSnapshot Capture() => new(default);

        public void ObserveConnect(
            WindowsSandboxWindowSnapshot snapshot,
            SandboxConnectAttempt attempt,
            CancellationToken cancellationToken)
        {
            Placed = client;
            attempt.Placement = Task.FromResult<SandboxClientWindow?>(client);
        }

        public Task<SandboxClientWindow?> PlaceConnectedClientAsync(
            WindowsSandboxWindowSnapshot snapshot,
            SandboxConnectAttempt attempt,
            CancellationToken cancellationToken) =>
            attempt.Placement ?? Task.FromResult<SandboxClientWindow?>(null);

        public SandboxClientWindow ResolveClient(SandboxClientWindow? remembered) =>
            remembered ?? client;
    }
}

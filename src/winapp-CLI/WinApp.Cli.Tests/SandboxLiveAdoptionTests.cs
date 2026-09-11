// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Live coverage for taking over a Windows Sandbox winapp did not start, gated on
/// <c>WINAPP_SANDBOX_E2E=1</c>.
/// </summary>
/// <remarks>
/// <para>
/// Take-over is the one behaviour that cannot be proven by a fake: the claim is that a guest with
/// somebody's work in it keeps that work, and only a real guest can be asked. So this test starts a
/// Sandbox the way a user would — directly through <c>wsb</c>, with no winapp state recording it —
/// leaves a file and a running process in it, then runs the real backend against it.
/// </para>
/// <para>
/// It stops only the instance it started itself, and it refuses to run at all if a Sandbox it did
/// not create is already up. Windows permits one at a time, and stopping somebody else's to make
/// room for a test would destroy exactly the thing the test exists to protect.
/// </para>
/// <para>
/// Nothing here enables a Windows feature or installs a package. Prerequisite setup is deliberately
/// untested live on a shared machine: it changes machine configuration, and proving it needs a
/// disposable host where Windows Sandbox has never been set up.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public class SandboxLiveAdoptionTests
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Marker file left in the guest before winapp touches it.</summary>
    private const string GuestMarkerPath = @"C:\Windows\Temp\winapp-live-adoption-marker.txt";

    /// <summary>The instance this test started, if any. Only this one is ever stopped.</summary>
    private string? _startedByThisTest;

    /// <summary>
    /// Sandbox clients that were already running when this test started.
    /// </summary>
    /// <remarks>
    /// Cleanup closes only clients absent from this set, resolved from a fresh name-filtered
    /// enumeration at the time of the kill. Holding a bare PID instead would be unsafe: a PID is
    /// reusable, so a recorded client that exits could see its number handed to an unrelated process,
    /// and closing that one is exactly the destructive act the rest of this fixture avoids.
    /// </remarks>
    private HashSet<int> _clientsBeforeTest = [];

    private string? _redirectedStateRoot;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public async Task RequireGateAsync()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(SandboxLiveE2ETests.GateVariable),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                $"Set {SandboxLiveE2ETests.GateVariable}=1 on a machine with Windows Sandbox to run live "
                + "take-over coverage.");
        }

        if (Environment.GetEnvironmentVariable(SandboxLiveE2ETests.BinaryVariable) is not { Length: > 0 } binary ||
            !File.Exists(binary))
        {
            Assert.Inconclusive(
                $"Set {SandboxLiveE2ETests.BinaryVariable} to the architecture-matched NativeAOT winapp.exe "
                + "built for the guest.");
        }

        // Acquired last, and only once the gate has admitted this test. See the same comment in
        // SandboxLiveE2ETests: a skipped test does not run cleanup, so it must never hold the lock.
        await LiveSandboxExclusion.AcquireAsync(TestContext.CancellationToken);

        // Taken before this test creates anything, so cleanup can tell the clients it caused from the
        // ones that were already here.
        _clientsBeforeTest = SandboxLiveE2ETests.CurrentClientProcessIds();
    }

    [TestCleanup]
    public async Task CleanupOnlyWhatThisTestCreated()
    {
        if (_startedByThisTest is { Length: > 0 } instanceId)
        {
            try
            {
                await CreateCli().StopAsync(instanceId, CancellationToken.None);
            }
            catch (Exception ex) when (ex is ExecutionTargetException or IOException)
            {
                Trace.TraceWarning("Could not stop the Sandbox this test created: {0}", ex.Message);
            }
        }

        // After the instance, because a client outlives the Sandbox it was attached to: stopping the
        // instance leaves the process running, so it has to be closed explicitly or it accumulates.
        // Every client that appeared during this test is one this test caused — the fixture refuses
        // to run when an instance it does not own is up, and Windows permits only one at a time.
        foreach (var clientProcessId in SandboxLiveE2ETests.CurrentClientProcessIds().Except(_clientsBeforeTest))
        {
            try
            {
                using var client = Process.GetProcessById(clientProcessId);
                client.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or SystemException)
            {
                // Already gone is the expected case once the Sandbox it served has stopped.
                Trace.TraceWarning("Could not close a Sandbox client this test caused: {0}", ex.Message);
            }
        }

        if (_redirectedStateRoot is { Length: > 0 } root && Directory.Exists(root))
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("Could not remove '{0}': {1}", root, ex.Message);
            }
        }

        // Last, so the next live test sees a machine this one has finished with.
        LiveSandboxExclusion.Release();
    }

    /// <summary>
    /// A Sandbox started by hand is taken over, keeps its contents, and is reused next time.
    /// </summary>
    [TestMethod]
    public async Task ManuallyStartedSandbox_IsAdoptedWithoutLosingWhatIsInIt()
    {
        await SkipUnlessTheMachineIsFreeAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(CommandTimeout);

        var cli = CreateCli();

        // Started exactly as a user would, and deliberately not through winapp: no state record
        // exists for it, so winapp must treat it as an instance it did not create.
        var manualId = WindowsSandboxLifecycle.GenerateInstanceId();
        var reportedId = await cli.StartAsync(manualId, configuration: null, timeout.Token);
        _startedByThisTest = manualId;

        Assert.AreEqual(manualId, reportedId, "wsb start must honour the caller-assigned instance ID.");
        CollectionAssert.Contains(
            (await cli.ListAsync(timeout.Token)).ToArray(),
            manualId,
            "The instance the test started must be listed under the ID it asked for.");

        await WaitUntilResolvableAsync(cli, manualId, timeout.Token);

        // A Sandbox a user started has a client attached to it — opening one from the Start menu is
        // what does that, and `wsb start` on its own deliberately does not. Without a client there is
        // no interactive logon session, so seeding the guest as the logged-in user cannot work:
        // `wsb exec --run-as ExistingLogin` fails with 0x80070520, "A specified logon session does
        // not exist". Connecting here is what makes this fixture reproduce the situation it claims
        // to, and it strengthens the test: adoption must now notice a session that already exists and
        // attach no client of its own.
        var clientsBeforeConnect = SandboxLiveE2ETests.CurrentClientProcessIds();
        await cli.ConnectAsync(manualId, timeout.Token);

        await WaitUntilInteractiveLoginWorksAsync(cli, manualId, timeout.Token);
        await LeaveWorkInTheGuestAsync(cli, manualId, timeout.Token);

        Assert.AreNotEqual(
            0,
            SandboxLiveE2ETests.CurrentClientProcessIds().Except(clientsBeforeConnect).Count(),
            "The client this test connected must be running, or the adoption assertion below proves nothing.");

        var clientsBeforeAdoption = SandboxLiveE2ETests.CurrentClientProcessIds();

        // winapp gets its own state root, so this test never disturbs the machine's real one and
        // cannot accidentally inherit an ownership record for the instance it just started.
        _redirectedStateRoot = TestPaths.TempRoot(nameof(SandboxLiveAdoptionTests));
        Directory.CreateDirectory(_redirectedStateRoot);

        var provider = new TargetStateDirectoryProvider(_redirectedStateRoot);
        var stateStore = new TargetStateStore(provider);

        var backend = CreateBackend(cli, provider, stateStore);
        var orchestrator = new ExecutionTargetOrchestrator(
            backend,
            new TargetMutationLock(provider),
            new TargetConnectionLock(provider));

        await using (var adopted = await orchestrator.PrepareAsync(PrepareTargetOptions.Mutating, timeout.Token))
        {
            adopted.ReleaseMutationLease();

            Assert.IsFalse(
                adopted.Reused,
                "A guest winapp did not start has nothing prepared under this epoch, so it is not a warm reuse.");

            var diagnostics = backend.DescribeForDiagnostics();
            Assert.AreEqual(manualId, diagnostics["sandboxId"], "winapp must take over the running instance.");
            Assert.AreEqual("true", diagnostics["sandboxAdopted"]);

            var persisted = stateStore.Read(WindowsSandboxTarget.Default);
            Assert.AreEqual(manualId, persisted!.InstanceId);
            Assert.AreEqual(nameof(SandboxInstanceOrigin.Adopted), persisted.InstanceOrigin);
        }

        // The measured reason adoption stays conservative: `wsb connect` against an instance that
        // already has a client starts a second WindowsSandboxRemoteSession, and that extra client
        // outlives `wsb stop`. A guest winapp did not start must never be given one.
        CollectionAssert.AreEquivalent(
            clientsBeforeAdoption.ToArray(),
            SandboxLiveE2ETests.CurrentClientProcessIds().ToArray(),
            "Adopting a Sandbox whose client is already attached must not start a second one.");

        // The whole point: the guest still has what it had before winapp arrived.
        Assert.AreEqual(
            0,
            await cli.ExecuteAsync(
                manualId,
                $@"cmd.exe /c if exist {GuestMarkerPath} (exit 0) else (exit 3)",
                workingDirectory: null,
                asSystem: false,
                timeout.Token),
            "Taking over a Sandbox must not remove files that were already in it.");

        Assert.AreEqual(
            0,
            await cli.ExecuteAsync(
                manualId,
                "powershell.exe -NoProfile -NonInteractive -Command "
                + "\"if (Get-Process -Name ping -ErrorAction SilentlyContinue) { exit 0 } else { exit 3 }\"",
                workingDirectory: null,
                asSystem: false,
                timeout.Token),
            "Taking over a Sandbox must not stop processes that were already running in it.");

        CollectionAssert.Contains(
            (await cli.ListAsync(timeout.Token)).ToArray(),
            manualId,
            "winapp must never stop a Sandbox it took over.");

        // A second process -- a fresh backend over the same state root -- must now find an instance
        // winapp owns and reuse it, rather than taking it over again under a new epoch.
        var second = CreateBackend(cli, provider, stateStore);
        var secondOrchestrator = new ExecutionTargetOrchestrator(
            second,
            new TargetMutationLock(provider),
            new TargetConnectionLock(provider));

        await using var reused = await secondOrchestrator.PrepareAsync(
            PrepareTargetOptions.ReadOnly, timeout.Token);

        Assert.IsTrue(reused.Reused, "The next command must reuse the instance winapp now owns.");
        Assert.AreEqual(manualId, second.DescribeForDiagnostics()["sandboxId"]);
    }

    /// <summary>
    /// <c>wsb start --id</c> honours the caller's GUID, which is what makes recovery possible.
    /// </summary>
    /// <remarks>
    /// The whole partial-start recovery design rests on this: winapp writes down an ID before
    /// starting, and reconciles that exact ID afterwards. If <c>wsb</c> ever stopped honouring it,
    /// recovery would silently degrade into guessing from a list, so it is verified against the real
    /// tool rather than assumed.
    /// </remarks>
    [TestMethod]
    public async Task WsbStart_HonoursTheCallerAssignedInstanceId()
    {
        await SkipUnlessTheMachineIsFreeAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(CommandTimeout);

        var cli = CreateCli();
        var assignedId = WindowsSandboxLifecycle.GenerateInstanceId();

        var reportedId = await cli.StartAsync(assignedId, configuration: null, timeout.Token);
        _startedByThisTest = assignedId;

        Assert.AreEqual(assignedId, reportedId);

        await WaitUntilResolvableAsync(cli, assignedId, timeout.Token);

        Assert.IsTrue(
            await cli.IsResolvableAsync(assignedId, timeout.Token),
            "An instance winapp claims must be reachable before anything is prepared in it.");
    }

    /// <summary>Puts a file and a running process into the guest before winapp sees it.</summary>
    private static async Task LeaveWorkInTheGuestAsync(
        WindowsSandboxCli cli,
        string instanceId,
        CancellationToken cancellationToken)
    {
        await cli.ExecuteAsync(
            instanceId,
            $@"cmd.exe /c echo pre-existing work > {GuestMarkerPath}",
            workingDirectory: null,
            asSystem: false,
            cancellationToken);

        await cli.ExecuteAsync(
            instanceId,
            @"cmd.exe /c start """" /min ping.exe -t 127.0.0.1",
            workingDirectory: null,
            asSystem: false,
            cancellationToken);
    }

    private static async Task WaitUntilResolvableAsync(
        WindowsSandboxCli cli,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(3);

        while (!await cli.IsResolvableAsync(instanceId, cancellationToken))
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Inconclusive("The Sandbox this test started never became reachable.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    /// <summary>
    /// Waits until the guest can actually run a command as the logged-in user.
    /// </summary>
    /// <remarks>
    /// A resolvable instance is not a usable one. <see cref="IWindowsSandboxCli.IsResolvableAsync"/>
    /// only proves the guest has an address; the interactive logon session a connected client
    /// establishes arrives later, and until it does every <c>ExistingLogin</c> command fails. The
    /// cheapest honest question is therefore the thing the caller is about to do — run one trivial
    /// command as that user — rather than any proxy for it.
    /// </remarks>
    private static async Task WaitUntilInteractiveLoginWorksAsync(
        WindowsSandboxCli cli,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(3);

        while (true)
        {
            try
            {
                if (await cli.ExecuteAsync(
                        instanceId,
                        "cmd.exe /c exit 0",
                        workingDirectory: null,
                        asSystem: false,
                        cancellationToken) == 0)
                {
                    return;
                }
            }
            catch (ExecutionTargetException)
            {
                // No logon session yet, which is the ordinary state until the client finishes.
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Inconclusive(
                    "The client this test connected never established an interactive logon session.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    /// <summary>
    /// Skips unless this machine can run Sandbox and no instance is already up.
    /// </summary>
    /// <remarks>
    /// A Sandbox that is already running is not a test failure. Windows allows one at a time, and
    /// this test would have to stop it to run — which is precisely the destructive act the feature
    /// is designed never to perform.
    /// </remarks>
    private async Task SkipUnlessTheMachineIsFreeAsync()
    {
        var cli = CreateCli();

        if (!cli.IsAvailable)
        {
            Assert.Inconclusive("Windows Sandbox is not set up on this machine.");
        }

        if ((await cli.ListAsync(TestContext.CancellationToken)).Count > 0)
        {
            Assert.Inconclusive(
                "A Windows Sandbox instance is already running. Windows permits only one, and this test "
                + "will not stop an instance it did not create.");
        }
    }

    private static WindowsSandboxCli CreateCli() => new(new ProcessRunner());

    private static WindowsSandboxBackend CreateBackend(
        IWindowsSandboxCli cli,
        ITargetStateDirectoryProvider provider,
        ITargetStateStore stateStore) =>
        new(
            cli,
            new WindowsSandboxLifecycle(cli, stateStore),
            provider,
            new FixedHostBinaryProvider(
                new FileInfo(Environment.GetEnvironmentVariable(SandboxLiveE2ETests.BinaryVariable)!)),
            new WindowsSandboxWindowController(),

            // No setup runner: this test must never change the machine's feature or package state.
            setup: null,
            stateStore);

    private sealed class FixedHostBinaryProvider(FileInfo binary) : IHostWinappBinaryProvider
    {
        public FileInfo GetBinary() => binary;
    }
}

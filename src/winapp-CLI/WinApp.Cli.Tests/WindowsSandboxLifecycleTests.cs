// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

/// <summary>
/// Deterministic stand-in for <c>wsb.exe</c>. Windows permits only one Sandbox and starting one is
/// slow and disruptive, so the singleton and ownership rules are verified here instead of against a
/// real instance.
/// </summary>
internal sealed class FakeWindowsSandboxCli : IWindowsSandboxCli
{
    private readonly List<string> _running = [];

    public bool IsAvailable { get; set; } = true;

    /// <summary>The absolute path <see cref="UseExecutable"/> was told to use, if any.</summary>
    public string? BoundExecutable { get; private set; }

    /// <summary>Every instance ID <see cref="StopAsync"/> was called with.</summary>
    public List<string> Stopped { get; } = [];

    /// <summary>How many times <see cref="LaunchAsync"/> was called.</summary>
    public int LaunchCount { get; private set; }

    /// <summary>
    /// The IDs Windows assigns to successive launches; once empty, <c>sandbox-N</c> is used.
    /// </summary>
    public Queue<string> LaunchIds { get; } = new();

    /// <summary>When set, <see cref="LaunchAsync"/> throws it without creating anything.</summary>
    public ExecutionTargetException? LaunchFailure { get; set; }

    /// <summary>
    /// Whether a launched client creates no Sandbox, as when it shows an error instead of a session.
    /// </summary>
    public bool LaunchCreatesNothing { get; set; }

    /// <summary>Invoked before each <see cref="ListAsync"/>, to simulate teardown completing.</summary>
    public Action? OnList { get; set; }

    /// <summary>IDs that are listed but refuse to resolve, modelling an instance still coming up.</summary>
    public HashSet<string> Unresolvable { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every ID <see cref="IsResolvableAsync"/> was asked about, in order.</summary>
    public List<string> ResolveProbes { get; } = [];

    public void UseExecutable(string executablePath) => BoundExecutable = executablePath;

    public void SetRunning(params string[] ids)
    {
        _running.Clear();
        _running.AddRange(ids);
    }

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken)
    {
        OnList?.Invoke();
        return Task.FromResult<IReadOnlyList<string>>([.. _running]);
    }

    /// <summary>The process ID the fake reports for a client it launched.</summary>
    public const int LaunchedClientProcessId = 4343;

    /// <summary>The start ticks the fake reports for a client it launched.</summary>
    public const long LaunchedClientStartTicks = 1_000_000;

    public Task<SandboxConnectAttempt> LaunchAsync(
        Action<SandboxConnectAttempt> onLaunched,
        CancellationToken cancellationToken)
    {
        LaunchCount++;

        if (LaunchFailure is { } failure)
        {
            throw failure;
        }

        if (!LaunchCreatesNothing)
        {
            _running.Add(LaunchIds.Count > 0 ? LaunchIds.Dequeue() : $"sandbox-{LaunchCount}");
        }

        var attempt = SandboxConnectAttempt.ForLauncher(LaunchedClientProcessId, LaunchedClientStartTicks);
        onLaunched(attempt);
        return Task.FromResult(attempt);
    }

    /// <summary>When true, <see cref="StopAsync"/> fails, exercising compensation failure paths.</summary>
    public bool FailStop { get; set; }

    public Task StopAsync(string id, CancellationToken cancellationToken)
    {
        if (FailStop)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.StartFailed,
                "The Windows Sandbox command line failed to stop the instance.");
        }

        Stopped.Add(id);
        _running.Remove(id);
        return Task.CompletedTask;
    }

    public Task<bool> IsResolvableAsync(string id, CancellationToken cancellationToken)
    {
        ResolveProbes.Add(id);

        return Task.FromResult(
            _running.Contains(id, StringComparer.OrdinalIgnoreCase) && !Unresolvable.Contains(id));
    }

    public Task<string> GetIpAddressAsync(string id, CancellationToken cancellationToken) =>
        Task.FromResult("172.27.0.2");

    public Task<GuestSessionAvailability> ProbeInteractiveSessionAsync(
        string id,
        CancellationToken cancellationToken) => Task.FromResult(GuestSessionAvailability.NoLoginSession);

    public Task ShareFolderAsync(
        string id,
        string hostPath,
        string sandboxPath,
        bool allowWrite,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<SandboxConnectAttempt> ConnectAsync(
        string id,
        Action<SandboxConnectAttempt> onLaunched,
        CancellationToken cancellationToken)
    {
        var attempt = SandboxConnectAttempt.ForLauncher(4242, 1_000_000);
        onLaunched(attempt);
        return Task.FromResult(attempt);
    }

    public Task<int> ExecuteAsync(
        string id,
        string command,
        string? workingDirectory,
        bool asSystem,
        CancellationToken cancellationToken) => Task.FromResult(0);

    public Task LaunchAgentAsync(
        string id,
        string command,
        CancellationToken cancellationToken) => Task.CompletedTask;
}


/// <summary>
/// Tests for <see cref="WindowsSandboxLifecycle"/>: singleton ownership, caller-assigned start IDs,
/// recovery from a start that half-succeeded, and automatic take-over of a Sandbox winapp did not
/// start.
/// </summary>
[TestClass]
public class WindowsSandboxLifecycleTests
{
    private DirectoryInfo _tempRoot = null!;
    private FakeWindowsSandboxCli _cli = null!;
    private TargetStateStore _stateStore = null!;
    private WindowsSandboxLifecycle _lifecycle = null!;
    private DateTimeOffset _now;
    private readonly RecordingProgress _progress = new();

    private sealed class RecordingProgress : ITargetProgress
    {
        public List<string> Messages { get; } = [];
        public void Report(string message) => Messages.Add(message);
    }

    [TestInitialize]
    public void Setup()
    {
        _tempRoot = new DirectoryInfo(TestPaths.TempRoot("SandboxLifecycle"));
        _tempRoot.Create();

        _cli = new FakeWindowsSandboxCli();
        _stateStore = new TargetStateStore(new TargetStateDirectoryProvider(_tempRoot.FullName));
        _lifecycle = NewLifecycle();
    }

    /// <summary>
    /// A lifecycle whose clock and delays are driven by the test rather than by real time.
    /// </summary>
    /// <remarks>
    /// Reconciliation polls for up to 45 seconds. Advancing a fake clock inside the delay is what
    /// lets a timeout be asserted in milliseconds instead of making the suite wait it out.
    /// </remarks>
    private WindowsSandboxLifecycle NewLifecycle(Queue<string>? instanceIds = null)
    {
        _now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var lifecycle = new WindowsSandboxLifecycle(_cli, _stateStore, _progress)
        {
            UtcNow = () => _now,
        };

        lifecycle.Delay = (delay, _) =>
        {
            _now += delay;
            return Task.CompletedTask;
        };

        while (instanceIds?.Count > 0)
        {
            _cli.LaunchIds.Enqueue(instanceIds.Dequeue());
        }

        return lifecycle;
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_tempRoot.Exists)
        {
            _tempRoot.Delete(recursive: true);
        }
    }

    /// <summary>The window a launched client shows once its Sandbox has a session.</summary>
    private static readonly SandboxClientWindow OwnWindow = new(
        0x5150,
        FakeWindowsSandboxCli.LaunchedClientProcessId,
        FakeWindowsSandboxCli.LaunchedClientStartTicks);

    /// <summary>
    /// What the opened window reached: <see cref="OwnWindow"/>, or null for a client that never
    /// showed a session, such as one showing the "only one instance" error.
    /// </summary>
    private SandboxClientWindow? _openedWindow = OwnWindow;

    /// <summary>Opens a Sandbox the way the backend does, minus window placement.</summary>
    private async Task<SandboxClientWindow?> OpenSandbox(CancellationToken cancellationToken)
    {
        using var attempt = await _cli.LaunchAsync(_ => { }, cancellationToken);

        return _openedWindow;
    }

    [TestMethod]
    public async Task Reconcile_NoStateAndNothingRunning_ReportsTerminated()
    {
        _cli.OnList = () => Assert.Fail("A first-run attach must not invoke the provider before setup.");
        var result = await _lifecycle.ReconcileAsync(TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(TargetLifecycleState.Terminated, result.State);
        Assert.IsNull(result.InstanceId);
        Assert.IsTrue(result.Epoch.IsNone);
    }

    [TestMethod]
    public async Task EnsureInstance_ColdStart_CreatesAndPersistsOwnership()
    {
        _lifecycle = NewLifecycle(new Queue<string>(["sandbox-a"]));

        var lease = await _lifecycle.EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual("sandbox-a", lease.InstanceId);
        Assert.AreEqual(SandboxInstanceOrigin.Created, lease.Origin);
        Assert.IsFalse(lease.IsWarm);
        Assert.IsFalse(lease.Epoch.IsNone);
        CollectionAssert.AreEqual(new[] { WindowsSandboxLifecycle.StartingMessage }, _progress.Messages);

        var persisted = _stateStore.Read(WindowsSandboxTarget.Default);
        Assert.AreEqual("sandbox-a", persisted!.InstanceId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(persisted.BootNonce), "A boot nonce is required to form an epoch.");
        Assert.IsNull(persisted.PendingInstanceId, "Opening a Sandbox in its own window records no pending start.");

        // The window that owns the Sandbox is recorded with it, so later commands restore and place
        // that window rather than treating it as someone else's.
        Assert.AreEqual(OwnWindow, lease.Client);
        Assert.IsTrue(persisted.ClientOwnedByWinapp);
        Assert.AreEqual((long)OwnWindow.Handle, persisted.ClientWindowHandle);
        Assert.AreEqual(OwnWindow.ProcessId, persisted.ClientProcessId);
        Assert.AreEqual(OwnWindow.StartTicksUtc, persisted.ClientProcessStartTicksUtc);
    }

    [TestMethod]
    public async Task EnsureInstance_WarmReuse_DoesNotStartASecondSandbox()
    {
        _lifecycle = NewLifecycle(new Queue<string>(["sandbox-a"]));
        var first = await _lifecycle.EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token);
        _progress.Messages.Clear();

        var second = await _lifecycle.EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(SandboxInstanceOrigin.Reused, second.Origin);
        Assert.AreEqual(first.InstanceId, second.InstanceId);
        Assert.AreEqual(first.Epoch, second.Epoch, "Reuse must preserve the epoch so live handles stay valid.");
        Assert.AreEqual(1, _cli.LaunchCount);
        Assert.IsEmpty(_progress.Messages, "An unchanged warm instance needs no preparation message.");

        // Warmth is a separate fact, recorded only once a bootstrap completes; nothing here did one.
        // EnsureInstance_AfterACompletedBootstrap_IsWarm covers that half.
        Assert.IsFalse(second.IsWarm);
    }

    [TestMethod]
    public async Task EnsureInstance_LaunchedWindowNeverShowedASession_UsesTheSandboxButNeverClaimsIt()
    {
        // Someone opened Windows Sandbox from Start in the same moment. Theirs won the singleton, so
        // winapp's window shows the "only one instance" error instead of a session. The one listed
        // Sandbox is theirs: it is used, as adopted, and no window is recorded as winapp's.
        _cli.LaunchCreatesNothing = true;
        _openedWindow = null;

        var listCalls = 0;
        _cli.OnList = () =>
        {
            if (++listCalls == 2)
            {
                _cli.SetRunning("someone-elses-sandbox");
            }
        };

        var lease = await _lifecycle.EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual("someone-elses-sandbox", lease.InstanceId);
        Assert.AreEqual(SandboxInstanceOrigin.Adopted, lease.Origin);
        Assert.IsNull(lease.Client);
        CollectionAssert.AreEqual(
            new[] { WindowsSandboxLifecycle.StartingMessage, WindowsSandboxLifecycle.AdoptingMessage },
            _progress.Messages);

        var persisted = _stateStore.Read(WindowsSandboxTarget.Default);
        Assert.IsFalse(persisted!.ClientOwnedByWinapp, "A window winapp cannot prove is its own is never recorded as owned.");
        Assert.IsNull(persisted.ClientWindowHandle);
    }

    [TestMethod]
    public async Task EnsureInstance_ListLagsTheNewSandbox_StillClaimsIt()
    {
        // wsb list can report nothing for a moment after the Sandbox's window shows a session. A
        // single check would conclude nothing started.
        _cli.LaunchCreatesNothing = true;

        var appearAfter = 3;
        _cli.OnList = () =>
        {
            if (--appearAfter == 0)
            {
                _cli.SetRunning("sandbox-late");
            }
        };

        var lease = await _lifecycle.EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual("sandbox-late", lease.InstanceId);
        Assert.AreEqual(SandboxInstanceOrigin.Created, lease.Origin);
    }

    [TestMethod]
    public async Task EnsureInstance_NothingStarts_ReportsStartFailedAndRecordsNothing()
    {
        _cli.LaunchCreatesNothing = true;
        _openedWindow = null;

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => _lifecycle.EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.StartFailed, failure.Error.Code);
        Assert.IsNull(_stateStore.Read(WindowsSandboxTarget.Default)?.InstanceId);
        Assert.AreEqual(1, _cli.LaunchCount, "One window is opened, never a second.");
    }

    [TestMethod]
    public async Task EnsureInstance_LaunchFails_PropagatesWithoutRecordingAnything()
    {
        _cli.LaunchFailure = ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.StartFailed,
            "Windows Sandbox could not start.");

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => _lifecycle.EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token));

        Assert.AreSame(_cli.LaunchFailure, failure);
        Assert.IsNull(_stateStore.Read(WindowsSandboxTarget.Default));
    }

    [TestMethod]
    public async Task EnsureInstance_PendingStartFromAnOlderVersion_IsRecoveredToThatExactInstance()
    {
        // winapp 0.7.1 recorded the ID it asked `wsb start` for before starting. A record its crash
        // left behind still names winapp's instance, so that exact one is claimed.
        CommitPendingStart("assigned-id");
        _cli.SetRunning("assigned-id");

        var lease = await _lifecycle.EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual("assigned-id", lease.InstanceId);
        Assert.AreEqual(SandboxInstanceOrigin.RecoveredStart, lease.Origin);
        Assert.IsFalse(lease.IsWarm, "A recovered instance has nothing bootstrapped under its new epoch.");
        Assert.AreEqual(0, _cli.LaunchCount, "The recovered instance must not be joined by a second one.");
        Assert.IsNull(_stateStore.Read(WindowsSandboxTarget.Default)!.PendingInstanceId);
    }

    [TestMethod]
    public async Task EnsureInstance_PendingStartFromAnOlderVersionThatNeverAppeared_OpensANewSandbox()
    {
        CommitPendingStart("assigned-id");

        var lease = await _lifecycle.EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(SandboxInstanceOrigin.Created, lease.Origin);
        Assert.AreEqual(1, _cli.LaunchCount);
        Assert.IsNull(_stateStore.Read(WindowsSandboxTarget.Default)!.PendingInstanceId);
    }

    /// <summary>Writes the pending-start record winapp 0.7.1 left when it died mid-start.</summary>
    private void CommitPendingStart(string instanceId) =>
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

    [TestMethod]
    public async Task EnsureInstance_ManualSandboxAlreadyRunning_IsAdoptedAutomatically()
    {
        // --on sandbox is explicit consent to make the one Sandbox Windows allows usable. Refusing
        // would make the flag unusable exactly when a Sandbox is available.
        _cli.SetRunning("someone-elses-sandbox");

        var lease = await _lifecycle.EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual("someone-elses-sandbox", lease.InstanceId);
        Assert.AreEqual(SandboxInstanceOrigin.Adopted, lease.Origin);
        Assert.IsTrue(lease.IsAdopted);
        Assert.IsFalse(lease.IsWarm, "An adopted guest has nothing prepared under this epoch.");
        Assert.AreEqual(0, _cli.LaunchCount, "A running Sandbox must be used, not joined by another.");
        CollectionAssert.AreEqual(Array.Empty<string>(), _cli.Stopped, "An adopted Sandbox is never stopped.");
    }

    [TestMethod]
    public async Task EnsureInstance_AdoptedSandbox_IsReusedByTheNextCommand()
    {
        _cli.SetRunning("someone-elses-sandbox");
        var first = await _lifecycle.EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token);

        var second = await NewLifecycle().EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(first.InstanceId, second.InstanceId);
        Assert.AreEqual(SandboxInstanceOrigin.Reused, second.Origin);
        Assert.AreEqual(first.Epoch, second.Epoch);
        Assert.AreEqual(0, _cli.LaunchCount);
    }

    [TestMethod]
    public async Task EnsureInstance_RunningSandboxNeverResolves_IsRefusedWithoutTouchingIt()
    {
        // Capability before mutation: an instance that cannot be resolved must not be claimed and
        // then bootstrapped into, and must not be stopped either.
        _cli.SetRunning("half-dead-sandbox");
        _cli.Unresolvable.Add("half-dead-sandbox");

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => _lifecycle.EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.UnmanagedInstance, failure.Error.Code);
        Assert.AreEqual("half-dead-sandbox", failure.Error.Context!["sandboxId"]);
        Assert.IsTrue(failure.Error.NextCommand!.Advisory, "Stopping an unowned Sandbox must be advisory.");
        CollectionAssert.AreEqual(Array.Empty<string>(), _cli.Stopped);
        Assert.IsNull(
            _stateStore.Read(WindowsSandboxTarget.Default)?.InstanceId,
            "An unusable instance must not be recorded as owned.");
    }

    [TestMethod]
    public async Task EnsureInstance_HalfWrittenOwnershipRecord_AdoptsTheOneRunningInstance()
    {
        // A record with an ID but no boot nonce names an instance winapp cannot form an epoch for,
        // so it establishes no ownership. Excluding that ID anyway would leave zero candidates and
        // report a single running Sandbox as "more than one".
        _stateStore.Commit(
            WindowsSandboxTarget.Default,
            new TargetState
            {
                SchemaVersion = 0,
                Revision = 0,
                TargetKind = WindowsSandboxTarget.Default.Kind,
                TargetId = WindowsSandboxTarget.Default.Id,
                InstanceId = "sandbox-a",
            },
            expectedRevision: 0);

        _cli.SetRunning("sandbox-a");

        var lease = await _lifecycle.EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual("sandbox-a", lease.InstanceId);
        Assert.AreEqual(SandboxInstanceOrigin.Adopted, lease.Origin);
        Assert.IsFalse(lease.Epoch.IsNone, "Taking it over is what gives it an epoch it did not have.");
        Assert.AreEqual(0, _cli.LaunchCount);
    }

    [TestMethod]
    public async Task EnsureInstance_SeveralSandboxesRunning_RefusesRatherThanGuessing()
    {
        _cli.SetRunning("sandbox-one", "sandbox-two");

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => _lifecycle.EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.UnmanagedInstance, failure.Error.Code);
        Assert.AreEqual("2", failure.Error.Context!["count"]);
        Assert.AreEqual(0, _cli.LaunchCount);
        CollectionAssert.AreEqual(Array.Empty<string>(), _cli.Stopped);
    }

    [TestMethod]
    public async Task EnsureInstance_ExternallyStopped_RecoversWithANewEpoch()
    {
        _lifecycle = NewLifecycle(new Queue<string>(["sandbox-a"]));
        var first = await _lifecycle.EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token);

        // The user closed the Sandbox or ran `wsb stop`.
        _cli.SetRunning();

        var reconciled = await _lifecycle.ReconcileAsync(TestContext.CancellationTokenSource.Token);
        Assert.AreEqual(TargetLifecycleState.Terminated, reconciled.State);

        var second = await NewLifecycle(new Queue<string>(["sandbox-b"]))
            .EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual("sandbox-b", second.InstanceId);
        Assert.AreEqual(SandboxInstanceOrigin.Created, second.Origin);

        // Handles captured against the old generation must not resolve against the new guest.
        Assert.AreNotEqual(first.Epoch, second.Epoch);
    }

    [TestMethod]
    public async Task EnsureInstance_SameIdReusedAfterReboot_StillProducesANewEpoch()
    {
        _lifecycle = NewLifecycle(new Queue<string>(["sandbox-a"]));
        var first = await _lifecycle.EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token);

        _cli.SetRunning();
        _lifecycle.InvalidateManagedInstance();

        // Windows could hand back an identical ID; the boot nonce is what guarantees a fresh epoch.
        var second = await NewLifecycle(new Queue<string>(["sandbox-a"]))
            .EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(first.InstanceId, second.InstanceId);
        Assert.AreNotEqual(first.Epoch, second.Epoch);
    }

    [TestMethod]
    public async Task EnsureInstance_OwnRecordedInstance_IsNotMisreportedAsUnmanaged()
    {
        _lifecycle = NewLifecycle(new Queue<string>(["sandbox-a"]));
        await _lifecycle.EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token);

        _cli.SetRunning("sandbox-a");
        var reused = await _lifecycle.EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(SandboxInstanceOrigin.Reused, reused.Origin);
        CollectionAssert.AreEqual(Array.Empty<string>(), _cli.Stopped);
    }

    [TestMethod]
    public async Task InvalidateManagedInstance_ClearsOwnership()
    {
        _lifecycle = NewLifecycle(new Queue<string>(["sandbox-a"]));
        await _lifecycle.EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token);

        _lifecycle.InvalidateManagedInstance();

        Assert.IsNull(_stateStore.Read(WindowsSandboxTarget.Default));
    }

    [TestMethod]
    public async Task EnsureInstance_CommitFails_LeavesTheSandboxRunningAndTheNextCommandUsesIt()
    {
        // Nothing is recorded before the Sandbox exists, so a command that fails to record it leaves
        // a Sandbox running in its own window and no claim on it. The next command must use that
        // Sandbox, not stop it or try to open a second one.
        var failingStore = new FailingCommitStateStore(_stateStore, failAfter: 0);
        var lifecycle = new WindowsSandboxLifecycle(_cli, failingStore)
        {
            UtcNow = () => _now,
        };
        lifecycle.Delay = (delay, _) =>
        {
            _now += delay;
            return Task.CompletedTask;
        };
        _cli.LaunchIds.Enqueue("sandbox-orphan");

        await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => lifecycle.EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token));

        CollectionAssert.AreEqual(Array.Empty<string>(), _cli.Stopped, "A live Sandbox must never be stopped.");

        var lease = await NewLifecycle().EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual("sandbox-orphan", lease.InstanceId);
        Assert.AreEqual(
            SandboxInstanceOrigin.Adopted,
            lease.Origin,
            "Without a record, nothing proves which window opened it, so it is used but not claimed.");
        Assert.AreEqual(1, _cli.LaunchCount);
    }

    [TestMethod]
    public async Task RedirectedStateRoot_AdoptsWithoutDisturbingTheOtherManagersGeneration()
    {
        // WINAPP_TARGET_STATE_ROOT gives a second winapp process its own ownership record, so it
        // cannot see that this one already owns the running Sandbox and will take it over. That
        // take-over must be additive: a fresh epoch, its own bootstrap folders, its own port and
        // material. Nothing belonging to the other generation may be reused or removed.
        _lifecycle = NewLifecycle(new Queue<string>(["sandbox-a"]));
        var owned = await _lifecycle.EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token);

        var otherRoot = new DirectoryInfo(TestPaths.TempRoot("SandboxLifecycleRedirected"));
        otherRoot.Create();

        try
        {
            var otherStore = new TargetStateStore(new TargetStateDirectoryProvider(otherRoot.FullName));
            var otherLifecycle = new WindowsSandboxLifecycle(_cli, otherStore);

            var adopted = await otherLifecycle.EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token);

            Assert.AreEqual(owned.InstanceId, adopted.InstanceId, "Windows allows only one Sandbox to take over.");
            Assert.AreEqual(SandboxInstanceOrigin.Adopted, adopted.Origin);
            Assert.AreNotEqual(
                owned.Epoch,
                adopted.Epoch,
                "A separate manager must get its own epoch, so neither reuses the other's material or paths.");
            CollectionAssert.AreEqual(
                Array.Empty<string>(),
                _cli.Stopped,
                "The other manager's live Sandbox must not be stopped.");

            // The first manager's own record is untouched, so its handles stay fenced on its epoch.
            var stillOwned = _stateStore.Read(WindowsSandboxTarget.Default);
            Assert.AreEqual(owned.InstanceId, stillOwned!.InstanceId);
            Assert.AreEqual(
                ExecutionTargetEpoch.Create(stillOwned.InstanceId!, stillOwned.BootNonce!),
                owned.Epoch);
        }
        finally
        {
            otherRoot.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task EnsureInstance_OwnedButNeverBootstrapped_IsNotTreatedAsWarm()
    {
        // Ownership is committed before the guest is prepared. A command killed between claiming an
        // instance and finishing its first bootstrap leaves one that is owned and listed but has no
        // connected client, no Developer Mode, and no agent. Calling that warm is what makes the
        // next command skip `wsb connect` and then launch the agent into a session no client has
        // established.
        _cli.SetRunning("someone-elses-sandbox");
        var adopted = await _lifecycle.EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token);

        Assert.IsFalse(adopted.IsWarm);

        var next = await NewLifecycle().EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(SandboxInstanceOrigin.Reused, next.Origin, "The instance is still winapp's.");
        Assert.AreEqual(adopted.Epoch, next.Epoch);
        Assert.IsFalse(
            next.IsWarm,
            "Nothing recorded a completed bootstrap, so the guest must be prepared rather than reconnected to.");
    }

    [TestMethod]
    public async Task EnsureInstance_AfterACompletedBootstrap_IsWarm()
    {
        _lifecycle = NewLifecycle(new Queue<string>(["sandbox-a"]));
        var created = await _lifecycle.EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token);

        // What the backend records once its authenticated agent connection succeeds.
        var state = _stateStore.Read(WindowsSandboxTarget.Default)!;
        _stateStore.Commit(
            WindowsSandboxTarget.Default,
            state with { BootstrappedEpoch = created.Epoch.Value },
            state.Revision);

        var next = await NewLifecycle().EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(SandboxInstanceOrigin.Reused, next.Origin);
        Assert.IsTrue(next.IsWarm, "A bootstrap that completed for this exact epoch is what makes reuse warm.");
    }

    [TestMethod]
    public async Task EnsureInstance_BootstrapMarkerFromAnotherEpoch_IsNotWarm()
    {
        // A marker left by a previous generation says nothing about this one.
        _lifecycle = NewLifecycle(new Queue<string>(["sandbox-a"]));
        await _lifecycle.EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token);

        var state = _stateStore.Read(WindowsSandboxTarget.Default)!;
        _stateStore.Commit(
            WindowsSandboxTarget.Default,
            state with { BootstrappedEpoch = "sandbox-a:SOMEOTHERNONCE" },
            state.Revision);

        var next = await NewLifecycle().EnsureInstanceAsync(OpenSandbox, TestContext.CancellationTokenSource.Token);

        Assert.IsFalse(next.IsWarm);
    }

    /// <summary>A store whose commits start failing after a given number of successes.</summary>
    private sealed class FailingCommitStateStore(ITargetStateStore inner, int failAfter) : ITargetStateStore
    {
        private int _commits;

        public TargetState? Read(ExecutionTargetRef target) => inner.Read(target);

        public TargetState Commit(ExecutionTargetRef target, TargetState state, long expectedRevision)
        {
            if (++_commits > failAfter)
            {
                throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.TargetAmbiguous,
                    "Windows Sandbox state changed while this command was running.");
            }

            return inner.Commit(target, state, expectedRevision);
        }

        public void Clear(ExecutionTargetRef target) => inner.Clear(target);
    }

    /// <summary>MSTest injects this; used for per-test cancellation.</summary>
    public TestContext TestContext { get; set; } = null!;
}

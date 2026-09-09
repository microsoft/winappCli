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

    /// <summary>Every instance ID <see cref="StartAsync"/> was asked to create, in order.</summary>
    public List<string> RequestedStartIds { get; } = [];

    /// <summary>Every instance ID <see cref="StopAsync"/> was called with.</summary>
    public List<string> Stopped { get; } = [];

    /// <summary>How many times <see cref="StartAsync"/> was called.</summary>
    public int StartCount { get; private set; }

    /// <summary>Invoked before each <see cref="ListAsync"/>, to simulate teardown completing.</summary>
    public Action? OnList { get; set; }

    /// <summary>
    /// When set, <see cref="StartAsync"/> throws it. The instance is still created first when
    /// <see cref="StartCreatesInstanceBeforeFailing"/> is set, modelling the observed
    /// <c>0x80070002</c> behaviour.
    /// </summary>
    public ExecutionTargetException? StartFailure { get; set; }

    /// <summary>Whether a failing start still leaves its instance listed.</summary>
    public bool StartCreatesInstanceBeforeFailing { get; set; }

    /// <summary>When set, <see cref="StartAsync"/> reports this ID instead of the requested one.</summary>
    public string? StartReportsId { get; set; }

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

    public Task<string> StartAsync(string instanceId, string? configuration, CancellationToken cancellationToken)
    {
        StartCount++;
        RequestedStartIds.Add(instanceId);

        if (StartFailure is { } failure)
        {
            if (StartCreatesInstanceBeforeFailing)
            {
                _running.Add(instanceId);
            }

            throw failure;
        }

        _running.Add(instanceId);
        return Task.FromResult(StartReportsId ?? instanceId);
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

        var lifecycle = new WindowsSandboxLifecycle(_cli, _stateStore)
        {
            UtcNow = () => _now,
        };

        lifecycle.Delay = (delay, _) =>
        {
            _now += delay;
            return Task.CompletedTask;
        };

        if (instanceIds is not null)
        {
            lifecycle.NewInstanceId = instanceIds.Dequeue;
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

    [TestMethod]
    public async Task Reconcile_NoStateAndNothingRunning_ReportsTerminated()
    {
        var result = await _lifecycle.ReconcileAsync(TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(TargetLifecycleState.Terminated, result.State);
        Assert.IsNull(result.InstanceId);
        Assert.IsTrue(result.Epoch.IsNone);
    }

    [TestMethod]
    public async Task EnsureInstance_ColdStart_CreatesAndPersistsOwnership()
    {
        _lifecycle = NewLifecycle(new Queue<string>(["sandbox-a"]));

        var lease = await _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        Assert.AreEqual("sandbox-a", lease.InstanceId);
        Assert.AreEqual(SandboxInstanceOrigin.Created, lease.Origin);
        Assert.IsFalse(lease.IsWarm);
        Assert.IsFalse(lease.Epoch.IsNone);

        var persisted = _stateStore.Read(WindowsSandboxTarget.Default);
        Assert.AreEqual("sandbox-a", persisted!.InstanceId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(persisted.BootNonce), "A boot nonce is required to form an epoch.");
        Assert.IsNull(persisted.PendingInstanceId, "A confirmed start must clear its pending marker.");
    }

    [TestMethod]
    public async Task EnsureInstance_WarmReuse_DoesNotStartASecondSandbox()
    {
        _lifecycle = NewLifecycle(new Queue<string>(["sandbox-a"]));
        var first = await _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        var second = await _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(SandboxInstanceOrigin.Reused, second.Origin);
        Assert.AreEqual(first.InstanceId, second.InstanceId);
        Assert.AreEqual(first.Epoch, second.Epoch, "Reuse must preserve the epoch so live handles stay valid.");
        Assert.AreEqual(1, _cli.StartCount);

        // Warmth is a separate fact, recorded only once a bootstrap completes; nothing here did one.
        // EnsureInstance_AfterACompletedBootstrap_IsWarm covers that half.
        Assert.IsFalse(second.IsWarm);
    }

    [TestMethod]
    public async Task EnsureInstance_AssignsTheIdItPersistedBeforeStarting()
    {
        // The ID is winapp's claim on the instance. It has to be chosen and written down first, or a
        // start that fails after creating something leaves nothing that identifies it.
        _lifecycle = NewLifecycle(new Queue<string>(["assigned-id"]));
        _cli.StartFailure = StartFailure(WsbHResult.FileNotFound);
        _cli.StartCreatesInstanceBeforeFailing = true;

        await _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        CollectionAssert.AreEqual(
            ExpectedAssignedIds,
            _cli.RequestedStartIds,
            "wsb start must be given the ID winapp assigned.");
    }

    /// <summary>The single assigned ID the caller-ID test expects, hoisted for CA1861.</summary>
    private static readonly string[] ExpectedAssignedIds = ["assigned-id"];

    [TestMethod]
    public async Task EnsureInstance_StartFailsAfterCreatingTheInstance_RecoversThatExactInstance()
    {
        // The live 0x80070002 failure: wsb reports an error but the instance it was asked to create
        // is listed and usable. Recovering it is what stops the next command from asking a singleton
        // to become two.
        _lifecycle = NewLifecycle(new Queue<string>(["assigned-id"]));
        _cli.StartFailure = StartFailure(WsbHResult.FileNotFound);
        _cli.StartCreatesInstanceBeforeFailing = true;

        var lease = await _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        Assert.AreEqual("assigned-id", lease.InstanceId);
        Assert.AreEqual(SandboxInstanceOrigin.RecoveredStart, lease.Origin);
        Assert.IsFalse(lease.IsWarm, "A recovered instance has nothing bootstrapped under its new epoch.");

        var persisted = _stateStore.Read(WindowsSandboxTarget.Default);
        Assert.AreEqual("assigned-id", persisted!.InstanceId);
        Assert.IsNull(persisted.PendingInstanceId);
    }

    [TestMethod]
    public async Task EnsureInstance_StartFailsAndProcessDies_NextProcessRecoversTheSameInstance()
    {
        // Modelled as two lifecycles over one state store, which is exactly what two winapp
        // invocations are. The second must find the first one's assigned ID rather than start again.
        _lifecycle = NewLifecycle(new Queue<string>(["assigned-id"]));
        _cli.StartFailure = StartFailure(WsbHResult.FileNotFound);
        _cli.StartCreatesInstanceBeforeFailing = true;
        _cli.Unresolvable.Add("assigned-id");

        await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token));

        var pending = _stateStore.Read(WindowsSandboxTarget.Default);
        Assert.AreEqual(
            "assigned-id",
            pending!.PendingInstanceId,
            "The pending marker must survive so the next process can finish the job.");

        // The guest finished coming up in the meantime.
        _cli.Unresolvable.Clear();
        _cli.StartFailure = null;

        var second = NewLifecycle(new Queue<string>(["would-be-second-start"]));
        var lease = await second.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        Assert.AreEqual("assigned-id", lease.InstanceId);
        Assert.AreEqual(SandboxInstanceOrigin.RecoveredStart, lease.Origin);
        Assert.AreEqual(1, _cli.StartCount, "The recovered instance must not be joined by a second one.");
    }

    [TestMethod]
    public async Task EnsureInstance_ListLagsTheNewInstance_StillRecoversIt()
    {
        // wsb list can report nothing for a moment after creating an instance. A single check would
        // conclude the start produced nothing.
        _lifecycle = NewLifecycle(new Queue<string>(["assigned-id"]));
        _cli.StartFailure = StartFailure(WsbHResult.FileNotFound);
        _cli.StartCreatesInstanceBeforeFailing = false;

        var appearAfter = 2;
        _cli.OnList = () =>
        {
            if (--appearAfter == 0)
            {
                _cli.SetRunning("assigned-id");
            }
        };

        var lease = await _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        Assert.AreEqual("assigned-id", lease.InstanceId);
        Assert.AreEqual(SandboxInstanceOrigin.RecoveredStart, lease.Origin);
    }

    [TestMethod]
    public async Task EnsureInstance_StartFailsAndNothingWasCreated_ReportsTheOriginalFailure()
    {
        _lifecycle = NewLifecycle(new Queue<string>(["assigned-id"]));
        _cli.StartFailure = StartFailure(WsbHResult.FileNotFound);
        _cli.StartCreatesInstanceBeforeFailing = false;

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.StartFailed, failure.Error.Code);
        Assert.AreEqual(
            WsbHResult.Format(WsbHResult.FileNotFound),
            failure.Error.Context![WsbHResult.ContextKey],
            "The HRESULT context must survive so the failure stays diagnosable.");
    }

    [TestMethod]
    public async Task EnsureInstance_AnotherProcessCreatedOneDuringOurStart_IsNeverAttributedToUs()
    {
        // The unattributable case: our start failed without creating anything, and a Sandbox that is
        // not the ID we asked for appeared while we were failing. Recovery must key on the assigned
        // ID, never on "one new item in the list".
        _lifecycle = NewLifecycle(new Queue<string>(["assigned-id"]));
        _cli.StartFailure = StartFailure(WsbHResult.FileNotFound);
        _cli.StartCreatesInstanceBeforeFailing = false;

        // Appears only after our start has already failed, so it cannot be confused with a Sandbox
        // that was there before winapp tried.
        var listCalls = 0;
        _cli.OnList = () =>
        {
            if (++listCalls == 2)
            {
                _cli.SetRunning("someone-elses-sandbox");
            }
        };

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.StartFailed, failure.Error.Code);

        var persisted = _stateStore.Read(WindowsSandboxTarget.Default);
        Assert.AreNotEqual(
            "someone-elses-sandbox",
            persisted!.InstanceId,
            "A Sandbox winapp did not ask for must never be recorded as the one it started.");
        Assert.AreEqual(
            "assigned-id",
            persisted.PendingInstanceId,
            "The unconfirmed start stays claimed by its own ID, not by whatever appeared.");
    }

    [TestMethod]
    public async Task EnsureInstance_StartReportsADifferentId_IsRefused()
    {
        _lifecycle = NewLifecycle(new Queue<string>(["assigned-id"]));
        _cli.StartReportsId = "something-else";

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.StartFailed, failure.Error.Code);
        Assert.AreEqual("assigned-id", failure.Error.Context!["requestedId"]);
        Assert.AreEqual("something-else", failure.Error.Context["reportedId"]);
        CollectionAssert.AreEqual(Array.Empty<string>(), _cli.Stopped, "Nothing may be stopped over a mismatch.");
    }

    [TestMethod]
    public async Task EnsureInstance_ManualSandboxAlreadyRunning_IsAdoptedAutomatically()
    {
        // --on sandbox is explicit consent to make the one Sandbox Windows allows usable. Refusing
        // would make the flag unusable exactly when a Sandbox is available.
        _cli.SetRunning("someone-elses-sandbox");

        var lease = await _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        Assert.AreEqual("someone-elses-sandbox", lease.InstanceId);
        Assert.AreEqual(SandboxInstanceOrigin.Adopted, lease.Origin);
        Assert.IsTrue(lease.IsAdopted);
        Assert.IsFalse(lease.IsWarm, "An adopted guest has nothing prepared under this epoch.");
        Assert.AreEqual(0, _cli.StartCount, "A running Sandbox must be used, not joined by another.");
        CollectionAssert.AreEqual(Array.Empty<string>(), _cli.Stopped, "An adopted Sandbox is never stopped.");
    }

    [TestMethod]
    public async Task EnsureInstance_AdoptedSandbox_IsReusedByTheNextCommand()
    {
        _cli.SetRunning("someone-elses-sandbox");
        var first = await _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        var second = await NewLifecycle().EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(first.InstanceId, second.InstanceId);
        Assert.AreEqual(SandboxInstanceOrigin.Reused, second.Origin);
        Assert.AreEqual(first.Epoch, second.Epoch);
        Assert.AreEqual(0, _cli.StartCount);
    }

    [TestMethod]
    public async Task EnsureInstance_RunningSandboxNeverResolves_IsRefusedWithoutTouchingIt()
    {
        // Capability before mutation: an instance that cannot be resolved must not be claimed and
        // then bootstrapped into, and must not be stopped either.
        _cli.SetRunning("half-dead-sandbox");
        _cli.Unresolvable.Add("half-dead-sandbox");

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token));

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

        var lease = await _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        Assert.AreEqual("sandbox-a", lease.InstanceId);
        Assert.AreEqual(SandboxInstanceOrigin.Adopted, lease.Origin);
        Assert.IsFalse(lease.Epoch.IsNone, "Taking it over is what gives it an epoch it did not have.");
        Assert.AreEqual(0, _cli.StartCount);
    }

    [TestMethod]
    public async Task EnsureInstance_SeveralSandboxesRunning_RefusesRatherThanGuessing()
    {
        _cli.SetRunning("sandbox-one", "sandbox-two");

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token));

        Assert.AreEqual(ExecutionTargetErrorCodes.UnmanagedInstance, failure.Error.Code);
        Assert.AreEqual("2", failure.Error.Context!["count"]);
        Assert.AreEqual(0, _cli.StartCount);
        CollectionAssert.AreEqual(Array.Empty<string>(), _cli.Stopped);
    }

    [TestMethod]
    public async Task EnsureInstance_SingletonInUse_ReusesTheRunningInstanceInsteadOfFailing()
    {
        // CO_E_APPSINGLEUSE says a Sandbox already exists. Reporting "restart the host" would send
        // the user somewhere useless.
        _lifecycle = NewLifecycle(new Queue<string>(["assigned-id"]));
        _cli.StartFailure = StartFailure(WsbHResult.AppSingleUse);
        _cli.StartCreatesInstanceBeforeFailing = false;
        _cli.SetRunning("the-existing-one");

        var lease = await _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        Assert.AreEqual("the-existing-one", lease.InstanceId);
        Assert.AreEqual(SandboxInstanceOrigin.Adopted, lease.Origin);
    }

    [TestMethod]
    public async Task EnsureInstance_ExternallyStopped_RecoversWithANewEpoch()
    {
        _lifecycle = NewLifecycle(new Queue<string>(["sandbox-a"]));
        var first = await _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        // The user closed the Sandbox or ran `wsb stop`.
        _cli.SetRunning();

        var reconciled = await _lifecycle.ReconcileAsync(TestContext.CancellationTokenSource.Token);
        Assert.AreEqual(TargetLifecycleState.Terminated, reconciled.State);

        var second = await NewLifecycle(new Queue<string>(["sandbox-b"]))
            .EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        Assert.AreEqual("sandbox-b", second.InstanceId);
        Assert.AreEqual(SandboxInstanceOrigin.Created, second.Origin);

        // Handles captured against the old generation must not resolve against the new guest.
        Assert.AreNotEqual(first.Epoch, second.Epoch);
    }

    [TestMethod]
    public async Task EnsureInstance_SameIdReusedAfterReboot_StillProducesANewEpoch()
    {
        _lifecycle = NewLifecycle(new Queue<string>(["sandbox-a"]));
        var first = await _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        _cli.SetRunning();
        _lifecycle.InvalidateManagedInstance();

        // Windows could hand back an identical ID; the boot nonce is what guarantees a fresh epoch.
        var second = await NewLifecycle(new Queue<string>(["sandbox-a"]))
            .EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(first.InstanceId, second.InstanceId);
        Assert.AreNotEqual(first.Epoch, second.Epoch);
    }

    [TestMethod]
    public async Task EnsureInstance_OwnRecordedInstance_IsNotMisreportedAsUnmanaged()
    {
        _lifecycle = NewLifecycle(new Queue<string>(["sandbox-a"]));
        await _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        _cli.SetRunning("sandbox-a");
        var reused = await _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(SandboxInstanceOrigin.Reused, reused.Origin);
        CollectionAssert.AreEqual(Array.Empty<string>(), _cli.Stopped);
    }

    [TestMethod]
    public async Task InvalidateManagedInstance_ClearsOwnership()
    {
        _lifecycle = NewLifecycle(new Queue<string>(["sandbox-a"]));
        await _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        _lifecycle.InvalidateManagedInstance();

        Assert.IsNull(_stateStore.Read(WindowsSandboxTarget.Default));
    }

    [TestMethod]
    public async Task EnsureInstance_CommitFails_LeavesTheInstanceRunningForTheNextCommand()
    {
        // Regression, inverted from the old behaviour on purpose. Stopping the instance used to be
        // the only way to avoid wedging the target, because an unrecorded Sandbox could never be
        // claimed. The pending marker is that proof now, so stopping would destroy a usable Sandbox
        // -- and whatever the user had running in it -- for nothing.
        var failingStore = new FailingCommitStateStore(_stateStore, failAfter: 1);
        var lifecycle = new WindowsSandboxLifecycle(_cli, failingStore)
        {
            NewInstanceId = () => "sandbox-orphan",
            UtcNow = () => _now,
        };
        lifecycle.Delay = (delay, _) =>
        {
            _now += delay;
            return Task.CompletedTask;
        };

        await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token));

        CollectionAssert.AreEqual(Array.Empty<string>(), _cli.Stopped, "A live Sandbox must never be stopped.");

        var persisted = _stateStore.Read(WindowsSandboxTarget.Default);
        Assert.AreEqual(
            "sandbox-orphan",
            persisted!.PendingInstanceId,
            "The pending marker is what lets the next command claim the instance that was created.");
    }

    [TestMethod]
    public async Task EnsureInstance_AfterAFailedCommit_TheNextCommandClaimsTheSameInstance()
    {
        var failingStore = new FailingCommitStateStore(_stateStore, failAfter: 1);
        var lifecycle = new WindowsSandboxLifecycle(_cli, failingStore) { NewInstanceId = () => "sandbox-orphan" };

        await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token));

        var lease = await NewLifecycle(new Queue<string>(["would-be-second-start"]))
            .EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        Assert.AreEqual("sandbox-orphan", lease.InstanceId);
        Assert.AreEqual(SandboxInstanceOrigin.RecoveredStart, lease.Origin);
        Assert.AreEqual(1, _cli.StartCount);
    }

    [TestMethod]
    public async Task RedirectedStateRoot_AdoptsWithoutDisturbingTheOtherManagersGeneration()
    {
        // WINAPP_TARGET_STATE_ROOT gives a second winapp process its own ownership record, so it
        // cannot see that this one already owns the running Sandbox and will take it over. That
        // take-over must be additive: a fresh epoch, its own bootstrap folders, its own port and
        // material. Nothing belonging to the other generation may be reused or removed.
        _lifecycle = NewLifecycle(new Queue<string>(["sandbox-a"]));
        var owned = await _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        var otherRoot = new DirectoryInfo(TestPaths.TempRoot("SandboxLifecycleRedirected"));
        otherRoot.Create();

        try
        {
            var otherStore = new TargetStateStore(new TargetStateDirectoryProvider(otherRoot.FullName));
            var otherLifecycle = new WindowsSandboxLifecycle(_cli, otherStore);

            var adopted = await otherLifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

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
        var adopted = await _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        Assert.IsFalse(adopted.IsWarm);

        var next = await NewLifecycle().EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

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
        var created = await _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        // What the backend records once its authenticated agent connection succeeds.
        var state = _stateStore.Read(WindowsSandboxTarget.Default)!;
        _stateStore.Commit(
            WindowsSandboxTarget.Default,
            state with { BootstrappedEpoch = created.Epoch.Value },
            state.Revision);

        var next = await NewLifecycle().EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(SandboxInstanceOrigin.Reused, next.Origin);
        Assert.IsTrue(next.IsWarm, "A bootstrap that completed for this exact epoch is what makes reuse warm.");
    }

    [TestMethod]
    public async Task EnsureInstance_BootstrapMarkerFromAnotherEpoch_IsNotWarm()
    {
        // A marker left by a previous generation says nothing about this one.
        _lifecycle = NewLifecycle(new Queue<string>(["sandbox-a"]));
        await _lifecycle.EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        var state = _stateStore.Read(WindowsSandboxTarget.Default)!;
        _stateStore.Commit(
            WindowsSandboxTarget.Default,
            state with { BootstrappedEpoch = "sandbox-a:SOMEOTHERNONCE" },
            state.Revision);

        var next = await NewLifecycle().EnsureInstanceAsync(TestContext.CancellationTokenSource.Token);

        Assert.IsFalse(next.IsWarm);
    }

    [TestMethod]
    public void GenerateInstanceId_IsAUniqueVersion4Uuid()
    {
        var ids = Enumerable.Range(0, 64).Select(_ => WindowsSandboxLifecycle.GenerateInstanceId()).ToList();

        Assert.AreEqual(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var id in ids)
        {
            Assert.IsTrue(Guid.TryParse(id, out var parsed), $"'{id}' must be a GUID.");
            Assert.AreEqual('4', id[14], "The ID must be shaped as a version-4 UUID.");
            Assert.AreNotEqual(Guid.Empty, parsed);
        }
    }

    /// <summary>A start failure carrying the HRESULT wsb reported.</summary>
    private static ExecutionTargetException StartFailure(int hresult) =>
        ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.StartFailed,
            "The Windows Sandbox command line failed.",
            context: new Dictionary<string, string>
            {
                ["wsbVerb"] = "start",
                [WsbHResult.ContextKey] = WsbHResult.Format(hresult),
            });

    /// <summary>A store whose commits start failing after a given number of successes.</summary>
    /// <remarks>
    /// The pending-start commit has to succeed for the failure under test to be the one that
    /// matters: it is the ownership commit, after the instance already exists, that used to trigger
    /// compensation.
    /// </remarks>
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

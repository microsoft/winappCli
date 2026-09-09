// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;

using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for the single entry point every <c>--on sandbox</c> command goes through.
/// </summary>
/// <remarks>
/// The ordering rules are the contract, and each is a failure mode rather than a preference:
/// probing before mutation is what keeps an unsupported host from failing only after a long build;
/// locking only for mutation is what keeps a read-only inspection from blocking behind a
/// deployment; and never falling back to local execution is what keeps <c>--on sandbox</c> from
/// silently running an application on the user's own desktop.
/// </remarks>
[TestClass]
public class ExecutionTargetOrchestratorTests
{
    private static readonly ExecutionTargetEpoch Epoch = ExecutionTargetEpoch.Create("sandbox-1", "nonce-a");

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Prepare_UnsupportedHost_FailsBeforeTouchingTheTarget()
    {
        var backend = new FakeBackend { Support = TargetSupportResult.Unsupported(new ExecutionTargetErrorInfo
        {
            Code = ExecutionTargetErrorCodes.Unsupported,
            Message = "Windows Sandbox is not installed.",
        }) };

        var orchestrator = new ExecutionTargetOrchestrator(
            backend,
            new FakeMutationLock(),
            new FakeConnectionLock());

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => orchestrator.PrepareAsync(PrepareTargetOptions.Mutating, TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.Unsupported, failure.Error.Code);

        // Nothing was started, and nothing fell back to running locally.
        Assert.AreEqual(0, backend.EnsureCalls);
    }

    [TestMethod]
    public async Task Prepare_MutatingCommand_TakesTheLock()
    {
        using var mutationLock = new FakeMutationLock();
        var orchestrator = new ExecutionTargetOrchestrator(
            new FakeBackend(),
            mutationLock,
            new FakeConnectionLock());

        await using var prepared = await orchestrator.PrepareAsync(
            PrepareTargetOptions.Mutating, TestContext.CancellationToken);

        Assert.AreEqual(1, mutationLock.AcquireCalls);
    }

    [TestMethod]
    public async Task Prepare_ReadOnlyCommand_DoesNotTakeTheLock()
    {
        using var mutationLock = new FakeMutationLock();
        var orchestrator = new ExecutionTargetOrchestrator(
            new FakeBackend(),
            mutationLock,
            new FakeConnectionLock());

        await using var prepared = await orchestrator.PrepareAsync(
            PrepareTargetOptions.ReadOnly, TestContext.CancellationToken);

        // Read-only UI Automation is explicitly outside the lock's scope, so an inspection never
        // waits behind a deployment and never makes one wait.
        Assert.AreEqual(0, mutationLock.AcquireCalls);
    }

    [TestMethod]
    public async Task Prepare_ReleasesTheConnectionLeaseOnceTheChannelExists()
    {
        var connectionLock = new FakeConnectionLock();
        var orchestrator = new ExecutionTargetOrchestrator(
            new FakeBackend(),
            new FakeMutationLock(),
            connectionLock);

        var prepared = await orchestrator.PrepareAsync(
            PrepareTargetOptions.ReadOnly,
            TestContext.CancellationToken);

        // The lock covers establishing a channel, not using one. Holding it for the prepared
        // target's lifetime is what made a foreground application block every other command:
        // `winapp run --on sandbox` keeps its prepared target for as long as the app runs.
        Assert.AreEqual(1, connectionLock.AcquireCalls);
        Assert.AreEqual(1, connectionLock.ReleaseCalls);

        await prepared.DisposeAsync();

        Assert.AreEqual(1, connectionLock.ReleaseCalls);
    }

    [TestMethod]
    public async Task Prepare_ConcurrentCommands_DoNotWaitOnEachOther()
    {
        // Two commands preparing while a third's target is still alive: the second must not be
        // serialized behind the first's channel, which is the whole contract SBX-009 restores.
        using var connectionLock = new SerializingConnectionLock();

        var first = await new ExecutionTargetOrchestrator(
            new FakeBackend(), new FakeMutationLock(), connectionLock)
            .PrepareAsync(PrepareTargetOptions.ReadOnly, TestContext.CancellationToken);

        await using (first)
        {
            // Would deadlock, not merely fail, if the first prepared target still held the lock.
            await using var second = await new ExecutionTargetOrchestrator(
                new FakeBackend(), new FakeMutationLock(), connectionLock)
                .PrepareAsync(PrepareTargetOptions.ReadOnly, TestContext.CancellationToken);

            Assert.IsNotNull(second.Operations);
        }
    }

    [TestMethod]
    public async Task Prepare_LockHeldByAnotherProcess_FailsWithGuidance()
    {
        using var mutationLock = new FakeMutationLock { Available = false };
        var orchestrator = new ExecutionTargetOrchestrator(
            new FakeBackend(),
            mutationLock,
            new FakeConnectionLock());

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => orchestrator.PrepareAsync(PrepareTargetOptions.Mutating, TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.TargetAmbiguous, failure.Error.Code);
        Assert.IsNotNull(failure.Error.UserAction);
    }

    [TestMethod]
    public async Task Prepare_DisconnectedClient_RefusesForegroundWork()
    {
        using var mutationLock = new FakeMutationLock();
        var backend = new FakeBackend { SupportsInteractiveDesktop = false };
        var orchestrator = new ExecutionTargetOrchestrator(
            backend,
            mutationLock,
            new FakeConnectionLock());

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => orchestrator.PrepareAsync(PrepareTargetOptions.Interactive, TestContext.CancellationToken));

        // A closed Sandbox client still reports an interactive desktop, because UI Automation keeps
        // working -- what stops is real input and screen capture. So the failure is "input not
        // ready", not "no interactive session", and gating on the wrong capability here would admit
        // commands that then report input they never delivered.
        Assert.AreEqual(ExecutionTargetErrorCodes.InputNotReady, failure.Error.Code);

        // Reconnecting changes what is on screen, so it is offered as advisory guidance rather than
        // performed automatically.
        Assert.IsTrue(failure.Error.NextCommand?.Advisory);
    }

    [TestMethod]
    public async Task Prepare_Session0_RefusesForegroundWorkAsNoInteractiveSession()
    {
        using var mutationLock = new FakeMutationLock();
        var backend = new FakeBackend { SessionId = 0 };
        var orchestrator = new ExecutionTargetOrchestrator(
            backend,
            mutationLock,
            new FakeConnectionLock());

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => orchestrator.PrepareAsync(PrepareTargetOptions.Interactive, TestContext.CancellationToken));

        // Session 0 has no desktop at all, which is a different failure from a disconnected client
        // and needs a different recovery.
        Assert.AreEqual(ExecutionTargetErrorCodes.NoInteractiveSession, failure.Error.Code);
    }

    [TestMethod]
    public async Task Prepare_DisconnectedClient_StillAllowsReadOnlyWork()
    {
        using var mutationLock = new FakeMutationLock();
        var backend = new FakeBackend { SupportsInteractiveDesktop = false };
        var orchestrator = new ExecutionTargetOrchestrator(
            backend,
            mutationLock,
            new FakeConnectionLock());

        // A disconnected client leaves UI Automation working, so inspection must stay available in
        // exactly the state where input has to be refused.
        await using var prepared = await orchestrator.PrepareAsync(
            PrepareTargetOptions.ReadOnly, TestContext.CancellationToken);

        Assert.AreEqual(Epoch, prepared.Epoch);
        Assert.IsTrue(prepared.Capabilities.SupportsInteractiveDesktop);
        Assert.IsFalse(prepared.Capabilities.SupportsRealInput);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Prepare_ReportsOneConsolidatedSetupPhase(bool reused)
    {
        var progress = new RecordingProgress();
        var orchestrator = new ExecutionTargetOrchestrator(
            new FakeBackend { Reused = reused },
            new FakeMutationLock(),
            new FakeConnectionLock(),
            progress);

        await using var prepared = await orchestrator.PrepareAsync(
            PrepareTargetOptions.ReadOnly, TestContext.CancellationToken);

        Assert.AreEqual(reused, prepared.Reused);
        CollectionAssert.AreEqual(
            new[] { ExecutionTargetOrchestrator.PrepareProgressMessage },
            progress.Messages);
    }

    [TestMethod]
    public async Task Prepare_MutatingCommand_StillHoldsTheLockAfterPrepareReturns()
    {
        // The lock protects everything PrepareAsync's caller still has to do -- runtime
        // provisioning, deployment reconciliation, package registration -- not just the probe/
        // connect/negotiate sequence inside PrepareAsync itself. Releasing it the moment
        // PrepareAsync returns (the previous behaviour) leaves every one of those guest mutations
        // unprotected, which is exactly the gap this test guards against.
        using var mutationLock = new FakeMutationLock();
        var orchestrator = new ExecutionTargetOrchestrator(
            new FakeBackend(),
            mutationLock,
            new FakeConnectionLock());

        await using var prepared = await orchestrator.PrepareAsync(
            PrepareTargetOptions.Mutating, TestContext.CancellationToken);

        Assert.AreEqual(1, mutationLock.AcquireCalls);
        Assert.AreEqual(0, mutationLock.ReleaseCalls, "The lease must still be held once Prepare returns.");

        prepared.ReleaseMutationLease();

        Assert.AreEqual(1, mutationLock.ReleaseCalls, "The caller must be able to release explicitly once done mutating.");
    }

    [TestMethod]
    public async Task Prepare_MutatingCommand_DisposeReleasesAnUnreleasedLeaseAsAFailSafe()
    {
        using var mutationLock = new FakeMutationLock();
        var orchestrator = new ExecutionTargetOrchestrator(
            new FakeBackend(),
            mutationLock,
            new FakeConnectionLock());

        var prepared = await orchestrator.PrepareAsync(
            PrepareTargetOptions.Mutating, TestContext.CancellationToken);

        Assert.AreEqual(0, mutationLock.ReleaseCalls);

        // A caller that forgot to call ReleaseMutationLease() -- or that failed before reaching it
        // -- must not leak the lock forever.
        await prepared.DisposeAsync();

        Assert.AreEqual(1, mutationLock.ReleaseCalls);
    }

    [TestMethod]
    public async Task RequireMutationLease_BeforeRelease_DoesNotThrow()
    {
        using var mutationLock = new FakeMutationLock();
        var orchestrator = new ExecutionTargetOrchestrator(
            new FakeBackend(),
            mutationLock,
            new FakeConnectionLock());

        await using var prepared = await orchestrator.PrepareAsync(
            PrepareTargetOptions.Mutating, TestContext.CancellationToken);

        // Does not throw: the lease is still held.
        prepared.RequireMutationLease();
    }

    [TestMethod]
    public async Task RequireMutationLease_AfterReleaseMutationLease_ThrowsRatherThanPassSilently()
    {
        // The bug this guards against: RequireMutationLease checking only "is the lease reference
        // non-null" would keep passing after ReleaseMutationLease() disposed it, because disposing
        // a lease does not null out the PreparedTarget's own reference to it. A mutation that ran
        // after release would then look "checked" while actually being completely unprotected.
        using var mutationLock = new FakeMutationLock();
        var orchestrator = new ExecutionTargetOrchestrator(
            new FakeBackend(),
            mutationLock,
            new FakeConnectionLock());

        await using var prepared = await orchestrator.PrepareAsync(
            PrepareTargetOptions.Mutating, TestContext.CancellationToken);

        prepared.ReleaseMutationLease();

        Assert.ThrowsExactly<InvalidOperationException>(prepared.RequireMutationLease);
    }

    [TestMethod]
    public async Task RequireMutationLease_AfterDisposeFailSafeRelease_ThrowsRatherThanPassSilently()
    {
        // Same as above, but released via the DisposeAsync fail-safe path instead of the caller's
        // own explicit ReleaseMutationLease() call -- both release paths must leave the lease
        // observably released, not just physically unlocked on disk.
        using var mutationLock = new FakeMutationLock();
        var orchestrator = new ExecutionTargetOrchestrator(
            new FakeBackend(),
            mutationLock,
            new FakeConnectionLock());

        var prepared = await orchestrator.PrepareAsync(
            PrepareTargetOptions.Mutating, TestContext.CancellationToken);

        await prepared.DisposeAsync();

        Assert.ThrowsExactly<InvalidOperationException>(prepared.RequireMutationLease);
    }

    [TestMethod]
    public async Task RequireMutationLease_ReadOnlyTarget_Throws()
    {
        using var mutationLock = new FakeMutationLock();
        var orchestrator = new ExecutionTargetOrchestrator(
            new FakeBackend(),
            mutationLock,
            new FakeConnectionLock());

        await using var prepared = await orchestrator.PrepareAsync(
            PrepareTargetOptions.ReadOnly, TestContext.CancellationToken);

        Assert.ThrowsExactly<InvalidOperationException>(prepared.RequireMutationLease);
    }

    /// <summary>
    /// Deterministic proof of the SBX-009 gap: two concurrent mutating commands against the same
    /// target must never have their guest-mutation work overlap.
    /// </summary>
    /// <remarks>
    /// This uses the real, file-backed <see cref="TargetMutationLock"/> rather than a fake, because
    /// the bug is specifically about *when* the real lock is released relative to the caller's own
    /// mutation work (runtime install, deployment reconciliation, package registration) -- a fake
    /// that hands out a fresh lock every call cannot observe that. Before the fix, PrepareAsync
    /// released the lock as soon as it returned, so both simulated "deployments" below ran their
    /// mutation window concurrently. After the fix, the lease survives until the caller explicitly
    /// releases it, so the windows are serialized and <c>maxConcurrency</c> is always 1.
    /// </remarks>
    [TestMethod]
    public async Task ConcurrentMutatingCommands_NeverOverlapGuestMutationWork()
    {
        var stateRoot = new DirectoryInfo(TestPaths.TempRoot("OrchestratorMutationRace"));
        stateRoot.Create();

        try
        {
            var mutationLock = new TargetMutationLock(new TargetStateDirectoryProvider(stateRoot.FullName));
            var observedConcurrency = 0;
            var maxConcurrency = 0;
            var gate = new Lock();

            async Task RunOneMutatingCommandAsync()
            {
                var orchestrator = new ExecutionTargetOrchestrator(
                    new FakeBackend(),
                    mutationLock,
                    new FakeConnectionLock());

                await using var target = await orchestrator.PrepareAsync(
                    PrepareTargetOptions.Mutating, TestContext.CancellationToken);

                try
                {
                    lock (gate)
                    {
                        observedConcurrency++;
                        maxConcurrency = Math.Max(maxConcurrency, observedConcurrency);
                    }

                    // Stands in for the caller's own mutation work after Prepare returns: runtime
                    // provisioning, deployment reconciliation, and package registration all happen
                    // here in production, none of it inside PrepareAsync.
                    await Task.Delay(50, TestContext.CancellationToken);

                    lock (gate)
                    {
                        observedConcurrency--;
                    }
                }
                finally
                {
                    target.ReleaseMutationLease();
                }
            }

            await Task.WhenAll(RunOneMutatingCommandAsync(), RunOneMutatingCommandAsync());

            Assert.AreEqual(
                1,
                maxConcurrency,
                "Two mutating commands against the same target must never overlap their guest mutation work.");
        }
        finally
        {
            stateRoot.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task Prepare_SimultaneousEstablishment_IsStillSerialized()
    {
        // Two hosts starting at once — one cold-starting the Sandbox, one reconnecting to it — must
        // not both bootstrap, rewrite connection material, or replace the agent binary. Narrowing
        // the lock to establishment must not lose that.
        using var connectionLock = new SerializingConnectionLock();
        var backend = new FakeBackend { EstablishDuration = TimeSpan.FromMilliseconds(150) };

        var prepares = Enumerable.Range(0, 4).Select(_ => Task.Run(
            () => new ExecutionTargetOrchestrator(backend, new FakeMutationLock(), connectionLock)
                .PrepareAsync(PrepareTargetOptions.ReadOnly, TestContext.CancellationToken),
            TestContext.CancellationToken));

        var prepared = await Task.WhenAll(prepares);

        try
        {
            Assert.IsFalse(backend.ObservedOverlap, "Establishment must never run concurrently.");
            Assert.AreEqual(4, backend.EnsureCalls);
        }
        finally
        {
            foreach (var target in prepared)
            {
                await target.DisposeAsync();
            }
        }
    }

    [TestMethod]
    public async Task Prepare_CompetingMutations_AreStillSerialized()
    {
        // The mutation lock's scope is unchanged: deployment, registration, and runtime installation
        // still take turns even though channels no longer do. Each task releases its own lease at
        // the end of its mutation window, which is where release happens in production too --
        // PrepareAsync now hands the lease to its caller instead of dropping it on return, so a test
        // that held four leases at once would be asserting against a contract that no longer exists.
        using var connectionLock = new SerializingConnectionLock();
        using var mutationLock = new CountingMutationLock();

        async Task PrepareMutateAndReleaseAsync()
        {
            await using var target = await new ExecutionTargetOrchestrator(
                    new FakeBackend(),
                    mutationLock,
                    connectionLock)
                .PrepareAsync(PrepareTargetOptions.Mutating, TestContext.CancellationToken);

            // Stands in for the caller's own mutation window, which the lease must span.
            await Task.Delay(20, TestContext.CancellationToken);

            target.ReleaseMutationLease();
        }

        await Task.WhenAll(Enumerable.Range(0, 4).Select(
            _ => Task.Run(PrepareMutateAndReleaseAsync, TestContext.CancellationToken)));

        Assert.IsFalse(mutationLock.ObservedOverlap, "Two mutations must never hold the lock at once.");
        Assert.AreEqual(4, mutationLock.AcquireCalls);
    }

    /// <summary>A backend whose responses are scripted, standing in for Windows Sandbox.</summary>
    private sealed class FakeBackend : IExecutionTargetBackend
    {
        private readonly List<GuestCommandServer> _servers = [];
        private int _inFlight;

        public ExecutionTargetRef Target => WindowsSandboxTarget.Default;

        public TargetSupportResult Support { get; init; } = TargetSupportResult.Supported;

        public bool SupportsInteractiveDesktop { get; init; } = true;

        public int SessionId { get; init; } = 1;

        public bool Reused { get; init; }

        public int EnsureCalls { get; private set; }

        /// <summary>How long establishing a connection takes, to widen the race window.</summary>
        public TimeSpan EstablishDuration { get; init; } = TimeSpan.Zero;

        /// <summary>True if two establishments were ever in flight at the same time.</summary>
        public bool ObservedOverlap { get; private set; }

        public Task<TargetSupportResult> ProbeSupportAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Support);

        public async Task<TargetConnection> EnsureConnectedAsync(
            EnsureTargetOptions options,
            CancellationToken cancellationToken)
        {
            // Bootstrap, material rewrite, and agent replacement all happen inside here, so two
            // hosts overlapping here is exactly the race the connection lock exists to prevent.
            if (Interlocked.Increment(ref _inFlight) > 1)
            {
                ObservedOverlap = true;
            }

            try
            {
                EnsureCalls++;

                if (EstablishDuration > TimeSpan.Zero)
                {
                    await Task.Delay(EstablishDuration, cancellationToken).ConfigureAwait(false);
                }

                var pair = new LoopbackTransportPair();

                // A real guest server rather than a scripted peer, so capability negotiation
                // exercises the same code path the orchestrator will meet in production.
                var server = new GuestCommandServer(
                    pair.Guest,
                    Epoch,
                    new FakeGuestProcessHostFactory(),
                    new StaticGuestSessionProbe(new GuestSessionInfo(
                        SessionId,
                        "WinSta0",
                        HasInputDesktop: SupportsInteractiveDesktop)),
                    new GuestAgentIdentity("1.0.0", "hash", "arm64", 1, 1));

                _servers.Add(server);
                _ = server.RunAsync(CancellationToken.None);

                return new TargetConnection(Epoch, pair.Host, Reused);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public IReadOnlyDictionary<string, string> DescribeForDiagnostics() =>
            new Dictionary<string, string> { ["sandboxId"] = "sandbox-1" };
    }

    private sealed class RecordingProgress : ITargetProgress
    {
        public List<string> Messages { get; } = [];

        public void Report(string message) => Messages.Add(message);
    }

    /// <summary>A lock that records use and can pretend another process holds it.</summary>
    private sealed class FakeMutationLock : ITargetMutationLock, IDisposable
    {
        private readonly List<FileStream> _streams = [];

        public bool Available { get; init; } = true;

        public int AcquireCalls { get; private set; }

        /// <summary>
        /// How many leases have been released, inferred from their handles being closed.
        /// </summary>
        /// <remarks>
        /// Observed through the handle rather than a callback because that is what actually
        /// releases the lock for other processes — a counter could be incremented while the handle
        /// stayed open, which is precisely the bug this assertion exists to catch.
        /// </remarks>
        public int ReleaseCalls => _streams.Count(s => !s.CanRead);

        public TargetMutationLease? TryAcquire(
            ExecutionTargetRef target,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            AcquireCalls++;

            if (!Available)
            {
                return null;
            }

            var path = TestPaths.TempFile("mutation-lock", ".lock");
            var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            _streams.Add(stream);

            return new TargetMutationLease(stream, wasAbandoned: false);
        }

        public void Dispose()
        {
            foreach (var stream in _streams)
            {
                stream.Dispose();
            }
        }
    }

    private sealed class FakeConnectionLock : ITargetConnectionLock
    {
        private readonly List<FileStream> _streams = [];

        public int AcquireCalls { get; private set; }

        public int ReleaseCalls => _streams.Count(stream => !stream.CanRead);

        public TargetConnectionLease? TryAcquire(
            ExecutionTargetRef target,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            AcquireCalls++;
            var path = TestPaths.TempFile("connection-lock", ".lock");
            var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            _streams.Add(stream);
            return new TargetConnectionLease(stream);
        }
    }

    /// <summary>
    /// A connection lock that behaves like the real file-backed one: exclusive, and unavailable to
    /// a second caller until the first releases.
    /// </summary>
    private sealed class SerializingConnectionLock : ITargetConnectionLock, IDisposable
    {
        private readonly string _path = TestPaths.TempFile("serializing-connection-lock", ".lock");

        public TargetConnectionLease? TryAcquire(
            ExecutionTargetRef target,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    return new TargetConnectionLease(new FileStream(
                        _path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                }
                catch (IOException)
                {
                    if (DateTimeOffset.UtcNow >= deadline)
                    {
                        return null;
                    }

                    Thread.Sleep(10);
                }
            }
        }

        public void Dispose()
        {
            try
            {
                File.Delete(_path);
            }
            catch (IOException)
            {
                // Cleanup noise is less useful than the assertion that already ran.
            }
        }
    }

    /// <summary>
    /// A mutually exclusive mutation lock that notices if two holders ever overlap.
    /// </summary>
    /// <remarks>
    /// Overlap is detected inside the lease rather than inferred from call counts, because the thing
    /// that matters is whether two mutations were ever simultaneously permitted — not how many times
    /// the lock was asked for.
    /// </remarks>
    private sealed class CountingMutationLock : ITargetMutationLock, IDisposable
    {
        private readonly string _path = TestPaths.TempFile("counting-mutation-lock", ".lock");
        private int _acquireCalls;
        private int _held;
        private int _overlapped;

        public int AcquireCalls => Volatile.Read(ref _acquireCalls);

        public bool ObservedOverlap => Volatile.Read(ref _overlapped) != 0;

        public TargetMutationLease? TryAcquire(
            ExecutionTargetRef target,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var stream = new ObservedFileStream(_path, () => Interlocked.Decrement(ref _held));

                    Interlocked.Increment(ref _acquireCalls);

                    if (Interlocked.Increment(ref _held) > 1)
                    {
                        Interlocked.Exchange(ref _overlapped, 1);
                    }

                    return new TargetMutationLease(stream, wasAbandoned: false);
                }
                catch (IOException)
                {
                    if (DateTimeOffset.UtcNow >= deadline)
                    {
                        return null;
                    }

                    Thread.Sleep(10);
                }
            }
        }

        public void Dispose()
        {
            try
            {
                File.Delete(_path);
            }
            catch (IOException)
            {
                // Cleanup noise is less useful than the assertion that already ran.
            }
        }

        /// <summary>A handle that reports when it is actually released.</summary>
        /// <remarks>
        /// The count drops <em>before</em> the exclusive handle closes. The handle is what actually
        /// serializes, so nothing can acquire while the count is already low — whereas decrementing
        /// afterwards leaves a window where the next acquirer opens the file and increments before
        /// the previous owner has decremented, which reads as an overlap that never happened.
        /// </remarks>
        private sealed class ObservedFileStream(string path, Action released)
            : FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
        {
            private bool _reported;

            protected override void Dispose(bool disposing)
            {
                if (disposing && !_reported)
                {
                    _reported = true;
                    released();
                }

                base.Dispose(disposing);
            }
        }
    }
}

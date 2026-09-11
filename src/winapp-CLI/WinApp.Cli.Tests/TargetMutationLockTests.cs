// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="TargetMutationLock"/>, the cross-process gate that serializes guest
/// mutation. Because there is no persistent host coordinator, this lock is the only thing
/// preventing two winapp processes from mutating the same guest environment concurrently.
/// </summary>
[TestClass]
public class TargetMutationLockTests
{
    private DirectoryInfo _tempRoot = null!;
    private ExecutionTargetRef _target = null!;
    private TargetMutationLock _lock = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempRoot = new DirectoryInfo(TestPaths.TempRoot("MutationLock"));
        _tempRoot.Create();

        _target = WindowsSandboxTarget.Default;
        _lock = new TargetMutationLock(new TargetStateDirectoryProvider(_tempRoot.FullName));
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
    public void TryAcquire_WhenFree_Succeeds()
    {
        using var lease = _lock.TryAcquire(_target, TimeSpan.FromSeconds(5));

        Assert.IsNotNull(lease);
        Assert.IsFalse(lease.WasAbandoned);
    }

    [TestMethod]
    public void TryAcquire_WhileHeld_TimesOut()
    {
        using var held = _lock.TryAcquire(_target, TimeSpan.FromSeconds(5));
        Assert.IsNotNull(held);

        var contended = _lock.TryAcquire(_target, TimeSpan.FromMilliseconds(200));

        Assert.IsNull(contended, "A held mutation lock must not be handed to a second caller.");
    }

    [TestMethod]
    public void TryAcquire_AfterRelease_SucceedsAndIsNotAbandoned()
    {
        using (var first = _lock.TryAcquire(_target, TimeSpan.FromSeconds(5)))
        {
            Assert.IsNotNull(first);
        }

        using var second = _lock.TryAcquire(_target, TimeSpan.FromSeconds(5));

        Assert.IsNotNull(second);
        Assert.IsFalse(second.WasAbandoned, "A cleanly released lock is not an abandoned one.");
    }

    [TestMethod]
    public void Lease_AcquiredAndReleasedOnDifferentThreads_ReleasesCleanly()
    {
        // Regression: the lock is held across awaits, so the continuation that disposes it usually
        // runs on a different thread-pool thread than the one that acquired it. A thread-affine
        // primitive fails to release there and stays held until the original thread exits, blocking
        // every other winapp process and later surfacing as a false abandonment.
        //
        // Acquisition runs on a dedicated thread that is then joined, rather than relying on an
        // await to move the continuation. Neither the thread pool nor an awaited long-running task
        // guarantees a different thread -- both resume inline on the completing thread -- so the
        // await-based versions of this test asserted their own premise and passed only by luck.
        int acquiringThread = 0;
        TargetMutationLease? lease = null;

        var acquirer = new Thread(() =>
        {
            acquiringThread = Environment.CurrentManagedThreadId;
            lease = _lock.TryAcquire(_target, TimeSpan.FromSeconds(5));
        });

        acquirer.Start();
        acquirer.Join();

        Assert.IsNotNull(lease);
        Assert.AreNotEqual(
            acquiringThread,
            Environment.CurrentManagedThreadId,
            "The acquiring thread has exited, so it cannot be the one disposing the lease.");

        lease.Dispose();

        // The decisive assertion: the lock is genuinely free afterwards.
        using var reacquired = _lock.TryAcquire(_target, TimeSpan.FromSeconds(5));
        Assert.IsNotNull(reacquired, "The lock must be released even when disposed on another thread.");
        Assert.IsFalse(reacquired.WasAbandoned, "A cross-thread release is still a clean release.");
    }

    [TestMethod]
    public async Task Lease_HeldAcrossManyAwaits_StillReleases()
    {
        var lease = _lock.TryAcquire(_target, TimeSpan.FromSeconds(5));
        Assert.IsNotNull(lease);

        for (var i = 0; i < 10; i++)
        {
            await Task.Run(() => Task.Delay(5));
        }

        lease.Dispose();

        using var reacquired = _lock.TryAcquire(_target, TimeSpan.FromSeconds(5));
        Assert.IsNotNull(reacquired);
    }

    [TestMethod]
    public void TryAcquire_AfterOwnerDiedWithoutReleasing_ReportsAbandonedAsRecoverySignal()
    {
        // A crashed owner leaves its record behind: the kernel closes the handle, but nothing
        // cleared the file. The next owner must be told so it reconciles a possibly half-mutated
        // guest rather than assuming the environment is clean.
        var path = _lock.GetLockFilePath(_target);
        File.WriteAllText(path, "4242 2026-08-20T00:00:00.0000000+00:00");

        using var lease = _lock.TryAcquire(_target, TimeSpan.FromSeconds(5));

        Assert.IsNotNull(lease);
        Assert.IsTrue(lease.WasAbandoned, "An owner record left behind must surface as a recovery signal.");
    }

    [TestMethod]
    public void Lease_RecordsOwnerWhileHeldAndClearsItOnRelease()
    {
        var path = _lock.GetLockFilePath(_target);

        var lease = _lock.TryAcquire(_target, TimeSpan.FromSeconds(5));
        Assert.IsNotNull(lease);

        // The record is what lets a later acquirer distinguish a crash from a clean handoff.
        Assert.IsTrue(new FileInfo(path).Length > 0, "An owner record must exist while the lock is held.");

        lease.Dispose();

        Assert.AreEqual(0, new FileInfo(path).Length, "A clean release must clear the owner record.");
    }

    [TestMethod]
    public void Dispose_IsIdempotent()
    {
        var lease = _lock.TryAcquire(_target, TimeSpan.FromSeconds(5));
        Assert.IsNotNull(lease);

        lease.Dispose();
        lease.Dispose();

        using var reacquired = _lock.TryAcquire(_target, TimeSpan.FromSeconds(5));
        Assert.IsNotNull(reacquired);
    }

    [TestMethod]
    public void TryAcquire_Cancelled_ThrowsWithoutTakingTheLock()
    {
        using var held = _lock.TryAcquire(_target, TimeSpan.FromSeconds(5));
        Assert.IsNotNull(held);

        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        Assert.ThrowsExactly<OperationCanceledException>(
            () => _lock.TryAcquire(_target, TimeSpan.FromSeconds(30), cancellation.Token));
    }

    [TestMethod]
    public void DifferentTargets_DoNotBlockEachOther()
    {
        var otherTarget = new ExecutionTargetRef("hyperv", "hyperv:winui-test");

        using var first = _lock.TryAcquire(_target, TimeSpan.FromSeconds(5));
        using var second = _lock.TryAcquire(otherTarget, TimeSpan.FromMilliseconds(500));

        Assert.IsNotNull(first);
        Assert.IsNotNull(second, "Independent targets must be able to mutate concurrently.");
    }

    [TestMethod]
    public void LockFile_LivesInTheTargetStateRoot()
    {
        var path = _lock.GetLockFilePath(_target);

        // Scoping the lock to the same root as the state it protects keeps both per-user, and lets
        // future targets serialize independently.
        StringAssert.Contains(path, _target.StateKey, StringComparison.Ordinal);
        Assert.AreEqual(TargetMutationLock.LockFileName, Path.GetFileName(path));
    }

    [TestMethod]
    public async Task ConcurrentAcquirers_AreSerialized()
    {
        var observedConcurrency = 0;
        var maxConcurrency = 0;
        var gate = new Lock();

        var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            using var lease = _lock.TryAcquire(_target, TimeSpan.FromSeconds(30));
            Assert.IsNotNull(lease);

            lock (gate)
            {
                observedConcurrency++;
                maxConcurrency = Math.Max(maxConcurrency, observedConcurrency);
            }

            await Task.Delay(20);

            lock (gate)
            {
                observedConcurrency--;
            }
        }));

        await Task.WhenAll(workers);

        Assert.AreEqual(1, maxConcurrency, "Only one holder may mutate the guest at a time.");
    }
}

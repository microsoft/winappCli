// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="TargetStateStore"/>. Every test redirects the state root to a temporary
/// directory so real user state under <c>%LOCALAPPDATA%</c> is never read or written.
/// </summary>
[TestClass]
public class TargetStateStoreTests
{
    private DirectoryInfo _tempRoot = null!;
    private TargetStateStore _store = null!;
    private ExecutionTargetRef _target = null!;

    [TestInitialize]
    public void Setup()
    {
        // An explicit per-test root keeps these tests isolated under the assembly's method-level
        // parallelism, and guarantees real state under %LOCALAPPDATA% is never touched.
        _tempRoot = new DirectoryInfo(TestPaths.TempRoot("TargetState"));
        _tempRoot.Create();

        _store = new TargetStateStore(new TargetStateDirectoryProvider(_tempRoot.FullName));
        _target = WindowsSandboxTarget.Default;
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_tempRoot.Exists)
        {
            _tempRoot.Delete(recursive: true);
        }
    }

    private static TargetState NewState(string? instanceId = "instance-1", string? nonce = "nonce-1") => new()
    {
        SchemaVersion = TargetStateStore.CurrentSchemaVersion,
        Revision = 0,
        TargetKind = ExecutionTargetRef.SandboxKind,
        TargetId = WindowsSandboxTarget.Default.Id,
        InstanceId = instanceId,
        BootNonce = nonce,
    };

    private string StateFilePath =>
        Path.Join(_tempRoot.FullName, _target.StateKey, TargetStateStore.StateFileName);

    [TestMethod]
    public void Read_NoState_ReturnsNull() => Assert.IsNull(_store.Read(_target));

    [TestMethod]
    public void Commit_FirstWrite_StartsAtRevisionOne()
    {
        var committed = _store.Commit(_target, NewState(), expectedRevision: 0);

        Assert.AreEqual(1, committed.Revision);
        Assert.AreEqual("instance-1", committed.InstanceId);
        Assert.AreEqual(TargetStateStore.CurrentSchemaVersion, committed.SchemaVersion);
        Assert.IsNotNull(committed.UpdatedUtc);
    }

    [TestMethod]
    public void Commit_UsesTargetStateKeyDirectory()
    {
        _store.Commit(_target, NewState(), expectedRevision: 0);

        // Readable enough to identify the target from the folder name, and unique enough that a
        // second target can never land in the same one. ExecutionTargetIdentityTests owns the
        // uniqueness rules; this only pins that the store actually uses the key.
        StringAssert.StartsWith(_target.StateKey, "sandbox-default-");
        Assert.IsTrue(File.Exists(StateFilePath), $"Expected state at {StateFilePath}.");
    }

    [TestMethod]
    public void ReadAfterCommit_RoundTripsEveryField()
    {
        var committed = _store.Commit(
            _target,
            NewState() with { AgentVersion = "1.2.3", AgentBinaryHash = "abc123" },
            expectedRevision: 0);

        var read = _store.Read(_target);

        Assert.IsNotNull(read);
        Assert.AreEqual(committed.Revision, read.Revision);
        Assert.AreEqual("instance-1", read.InstanceId);
        Assert.AreEqual("nonce-1", read.BootNonce);
        Assert.AreEqual("1.2.3", read.AgentVersion);
        Assert.AreEqual("abc123", read.AgentBinaryHash);
        Assert.AreEqual(ExecutionTargetRef.SandboxKind, read.TargetKind);
    }

    /// <summary>
    /// The client window winapp parked has to survive the process that parked it, because every
    /// winapp invocation builds a fresh backend and a capture minutes later must still know which
    /// window on this machine is the one it manages.
    /// </summary>
    [TestMethod]
    public void ReadAfterCommit_RoundTripsTheManagedClientWindow()
    {
        _store.Commit(
            _target,
            NewState() with
            {
                ClientWindowHandle = 0x2A2A,
                ClientProcessId = 4242,
                ClientProcessStartTicksUtc = 638_000_000_000_000_000,
                ClientOwnedByWinapp = true,
            },
            expectedRevision: 0);

        var read = _store.Read(_target);

        Assert.IsNotNull(read);
        Assert.AreEqual(0x2A2A, read.ClientWindowHandle);
        Assert.AreEqual(4242, read.ClientProcessId);

        // The start time is what distinguishes this window from whatever owns that handle and
        // process ID after Windows recycles them.
        Assert.AreEqual(638_000_000_000_000_000, read.ClientProcessStartTicksUtc);
        Assert.IsTrue(read.ClientOwnedByWinapp);
    }

    [TestMethod]
    public void Commit_IncrementsRevisionMonotonically()
    {
        var first = _store.Commit(_target, NewState(), expectedRevision: 0);
        var second = _store.Commit(_target, NewState(instanceId: "instance-2"), first.Revision);
        var third = _store.Commit(_target, NewState(instanceId: "instance-3"), second.Revision);

        Assert.AreEqual(1, first.Revision);
        Assert.AreEqual(2, second.Revision);
        Assert.AreEqual(3, third.Revision);
        Assert.AreEqual("instance-3", _store.Read(_target)!.InstanceId);
    }

    [TestMethod]
    public void Commit_StaleRevision_FailsClosedWithoutOverwriting()
    {
        var first = _store.Commit(_target, NewState(), expectedRevision: 0);
        _store.Commit(_target, NewState(instanceId: "winner"), first.Revision);

        // A second host process still holding the old revision must not clobber the winner.
        var exception = Assert.ThrowsExactly<ExecutionTargetException>(
            () => _store.Commit(_target, NewState(instanceId: "loser"), first.Revision));

        Assert.AreEqual(ExecutionTargetErrorCodes.TargetAmbiguous, exception.Error.Code);
        Assert.AreEqual("winner", _store.Read(_target)!.InstanceId, "The losing commit must not apply.");
    }

    [TestMethod]
    public void Read_CorruptState_FailsClosed()
    {
        _store.Commit(_target, NewState(), expectedRevision: 0);
        File.WriteAllText(StateFilePath, "{ this is not valid json");

        var exception = Assert.ThrowsExactly<ExecutionTargetException>(() => _store.Read(_target));

        Assert.AreEqual(ExecutionTargetErrorCodes.TargetAmbiguous, exception.Error.Code);
        Assert.IsNotNull(exception.Error.UserAction, "A corrupt-state failure must tell the user how to recover.");
    }

    [TestMethod]
    public void Read_NewerSchema_FailsClosedAndAsksForAnUpdate()
    {
        _store.Commit(_target, NewState(), expectedRevision: 0);
        var newer = File.ReadAllText(StateFilePath)
            .Replace($"\"schemaVersion\": {TargetStateStore.CurrentSchemaVersion}", "\"schemaVersion\": 9999", StringComparison.Ordinal);
        File.WriteAllText(StateFilePath, newer);

        var exception = Assert.ThrowsExactly<ExecutionTargetException>(() => _store.Read(_target));

        Assert.AreEqual(ExecutionTargetErrorCodes.TargetAmbiguous, exception.Error.Code);
        StringAssert.Contains(exception.Error.UserAction!, "Update winapp", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void Clear_RemovesState_AndIsIdempotent()
    {
        _store.Commit(_target, NewState(), expectedRevision: 0);

        _store.Clear(_target);
        Assert.IsNull(_store.Read(_target));

        _store.Clear(_target);
        Assert.IsNull(_store.Read(_target));
    }

    [TestMethod]
    public void Commit_LeavesNoTemporaryFilesBehind()
    {
        _store.Commit(_target, NewState(), expectedRevision: 0);

        var files = Directory.GetFiles(Path.Join(_tempRoot.FullName, _target.StateKey));

        Assert.AreEqual(1, files.Length, "Atomic replace must not leave temporary files behind.");
        Assert.AreEqual(TargetStateStore.StateFileName, Path.GetFileName(files[0]));
    }

    [TestMethod]
    public void StateKey_SanitizesSeparatorsForFilesystemAndKernelNames()
    {
        StringAssert.StartsWith(
            new ExecutionTargetRef("hyperv", "hyperv:winui/test").StateKey, "hyperv-hyperv-winui-test-");

        // Nothing survives sanitising here, so the readable half falls back to a placeholder and
        // the hash carries the whole identity.
        StringAssert.StartsWith(new ExecutionTargetRef("odd", ":::").StateKey, "odd-");
    }

    /// <summary>
    /// A record written for one target must never be read as another's, even if the two ever ended
    /// up in the same folder: it is the proof of which guest an epoch and an instance ID belong to.
    /// </summary>
    [TestMethod]
    public void Read_RecordForADifferentTarget_FailsClosed()
    {
        _store.Commit(_target, NewState(), expectedRevision: 0);

        var impostor = new ExecutionTargetRef("hyperv", "WinAppTest");
        var sameFolderStore = new TargetStateStore(new FixedRootProvider(
            Path.Join(_tempRoot.FullName, _target.StateKey)));

        var failure = Assert.ThrowsExactly<ExecutionTargetException>(() => sameFolderStore.Read(impostor));

        Assert.AreEqual(ExecutionTargetErrorCodes.TargetAmbiguous, failure.Error.Code);
    }

    /// <summary>Points every target at one directory, to reproduce a reused state folder.</summary>
    private sealed class FixedRootProvider(string root) : ITargetStateDirectoryProvider
    {
        public DirectoryInfo GetTargetRoot(ExecutionTargetRef target, bool create = true)
        {
            var directory = new DirectoryInfo(root);
            if (create && !directory.Exists)
            {
                directory.Create();
            }

            return directory;
        }
    }

    [TestMethod]
    public void Epoch_CombinesInstanceAndBootNonce()
    {
        var first = ExecutionTargetEpoch.Create("instance-1", "nonce-a");
        var rebooted = ExecutionTargetEpoch.Create("instance-1", "nonce-b");

        // A provider that reuses instance IDs must still produce a distinct epoch per boot,
        // otherwise stale PID/HWND rejection would silently pass.
        Assert.AreNotEqual(first, rebooted);
        Assert.IsFalse(first.IsNone);
        Assert.IsTrue(ExecutionTargetEpoch.None.IsNone);
    }
}

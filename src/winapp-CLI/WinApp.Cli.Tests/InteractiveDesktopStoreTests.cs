// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Tests;

/// <summary>
/// File-level coverage of the coordination store, participant leases, and lock-directory setup
/// (issue #764). Everything here runs against a throwaway directory supplied through
/// <c>WINAPP_UI_LOCK_DIRECTORY</c>, so a test never touches the developer's live coordination state.
/// </summary>
[TestClass]
[DoNotParallelize] // WINAPP_UI_LOCK_DIRECTORY is process-wide.
public class InteractiveDesktopStoreTests
{
    private string _lockDirectory = null!;
    private string? _previousOverride;
    private InteractiveDesktopPaths _paths = null!;
    private ParticipantRegistry _participants = null!;
    private InteractiveDesktopStateStore _store = null!;
    private FakeProcessInspector _inspector = null!;

    [TestInitialize]
    public void Setup()
    {
        _lockDirectory = Path.Combine(Path.GetTempPath(), $"winapp-locks-{Guid.NewGuid():N}");
        _previousOverride = Environment.GetEnvironmentVariable(
            InteractiveDesktopPaths.LockDirectoryOverrideVariable);
        Environment.SetEnvironmentVariable(
            InteractiveDesktopPaths.LockDirectoryOverrideVariable, _lockDirectory);

        _inspector = new FakeProcessInspector();
        _paths = new InteractiveDesktopPaths(_inspector);
        _participants = new ParticipantRegistry(_paths, _inspector, NullLogger<ParticipantRegistry>.Instance);
        _store = new InteractiveDesktopStateStore(
            _paths, _participants, new FixedClock(), NullLogger<InteractiveDesktopStateStore>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable(
            InteractiveDesktopPaths.LockDirectoryOverrideVariable, _previousOverride);
        try
        {
            if (Directory.Exists(_lockDirectory))
            {
                Directory.Delete(_lockDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leaked temp directory must never fail a test.
        }
    }

    // ------------------------------------------------------------------------------- fresh vs corrupt

    [TestMethod]
    public void Read_MissingFile_StartsFresh()
    {
        using var stateLock = _store.AcquireStateLock(CancellationToken.None);
        var result = _store.Read();

        Assert.IsFalse(result.RecoveredFromCorruption, "a missing file is an ordinary first run");
        Assert.IsNotNull(result.State);
        Assert.IsNull(result.State!.Owner);
    }

    [TestMethod]
    public void Read_EmptyFile_IsTreatedAsCorruptionNotFreshState()
    {
        // Atomic publication never produces an empty file, so one means a torn write or truncation.
        _paths.EnsureDirectories();
        File.WriteAllText(_paths.StatePath, string.Empty);

        using var stateLock = _store.AcquireStateLock(CancellationToken.None);
        var result = _store.Read();

        Assert.IsTrue(result.RecoveredFromCorruption,
            "an existing empty state file must take the guarded recovery path");
        Assert.IsTrue(
            Directory.EnumerateFiles(_paths.LockDirectory, "state.corrupt-*.json").Any(),
            "the unreadable file must be quarantined rather than silently discarded");
    }

    [TestMethod]
    public void Read_WhitespaceFile_IsTreatedAsCorruption()
    {
        _paths.EnsureDirectories();
        File.WriteAllText(_paths.StatePath, "   \r\n  ");

        using var stateLock = _store.AcquireStateLock(CancellationToken.None);
        Assert.IsTrue(_store.Read().RecoveredFromCorruption);
    }

    [TestMethod]
    public void Read_CorruptStateWithALiveParticipant_FailsClosed()
    {
        _paths.EnsureDirectories();
        File.WriteAllText(_paths.StatePath, "{ this is not json");

        // A live lease means some other winapp process is mid-workflow; rebuilding state under it would
        // strand its ownership and let two processes drive the desktop.
        using var lease = _participants.OpenLease(_inspector.CurrentProcessId, _inspector.CurrentProcessStartTicksUtc);

        using var stateLock = _store.AcquireStateLock(CancellationToken.None);
        var ex = Assert.ThrowsExactly<UiCoordinationException>(() => _store.Read());
        Assert.AreEqual(UiCoordinationErrorCodes.Unavailable, ex.Code);
    }

    [TestMethod]
    public void Read_EmptyStateWithALiveParticipant_FailsClosed()
    {
        _paths.EnsureDirectories();
        File.WriteAllText(_paths.StatePath, string.Empty);

        using var lease = _participants.OpenLease(_inspector.CurrentProcessId, _inspector.CurrentProcessStartTicksUtc);

        using var stateLock = _store.AcquireStateLock(CancellationToken.None);
        var ex = Assert.ThrowsExactly<UiCoordinationException>(() => _store.Read());
        Assert.AreEqual(UiCoordinationErrorCodes.Unavailable, ex.Code);
    }

    [TestMethod]
    public void Read_IncompleteObject_IsTreatedAsCorruption()
    {
        // An empty object is not a fresh state, it is a truncated one. Every property carried a
        // defaulting initializer, so `{}` bound to exactly what CreateFresh() produces — version 1,
        // ticket 1, no owner, no commands — and sailed through structural validation as if a real
        // coordinator had written it. A file truncated to `{}` by a crashed writer, a disk-full
        // rewrite or a stray editor therefore looked like "nobody is using the desktop".
        _paths.EnsureDirectories();
        File.WriteAllText(_paths.StatePath, "{}");

        using (var stateLock = _store.AcquireStateLock(CancellationToken.None))
        {
            var result = _store.Read();
            Assert.IsTrue(
                result.RecoveredFromCorruption,
                "a document missing every field the writer always emits must be corruption, not a fresh state");
            Assert.IsNotNull(result.State);
        }

        Assert.IsTrue(
            Directory.EnumerateFiles(_paths.LockDirectory, "state.corrupt-*.json").Any(),
            "the unusable document must be quarantined rather than silently believed");
    }

    [TestMethod]
    public void Read_IncompleteObjectWithALiveParticipant_FailsClosed()
    {
        // The consequence that makes this critical rather than untidy. Believing `{}` mints a fresh
        // owner while another process still holds its lease, so two workflows drive one desktop at
        // once. With a live lease the read must refuse instead.
        _paths.EnsureDirectories();
        File.WriteAllText(_paths.StatePath, "{}");

        using var lease = _participants.OpenLease(_inspector.CurrentProcessId, _inspector.CurrentProcessStartTicksUtc);

        using var stateLock = _store.AcquireStateLock(CancellationToken.None);
        var ex = Assert.ThrowsExactly<UiCoordinationException>(() => _store.Read());
        Assert.AreEqual(UiCoordinationErrorCodes.Unavailable, ex.Code);

        Assert.AreEqual("{}", File.ReadAllText(_paths.StatePath),
            "failing closed must leave the bytes alone rather than replacing them with a fresh state");
    }

    [TestMethod]
    public void Read_WaiterMissingARequiredField_IsTreatedAsCorruption()
    {
        // A partially written entry is worse than a missing one: an absent ownerKind would default to
        // whichever enum value is zero, so a truncated waiter would be promoted later with an owner
        // kind nobody ever wrote — deciding, among other things, whether it earns an idle grace.
        _paths.EnsureDirectories();
        const string truncatedWaiter = """
            {"version":1,"turnId":0,"turnStartedTick64":0,"nextTicket":2,"idleExpiresTick64":0,
             "ownerCommands":[],
             "waiters":[{"ticket":1,"ownerKey":"abc","pid":4242,"processStartTicksUtc":99,"operation":"ui click","mode":"desktop-exclusive"}]}
            """;
        File.WriteAllText(_paths.StatePath, truncatedWaiter);

        using var stateLock = _store.AcquireStateLock(CancellationToken.None);
        var result = _store.Read();

        Assert.IsTrue(
            result.RecoveredFromCorruption,
            "a waiter missing ownerKind must not default into valid scheduling semantics");
    }

    [TestMethod]
    public void Read_OwnerCommandMissingStatus_IsTreatedAsCorruption()
    {
        // Status decides whether a command is running or still blocked behind a barrier. Defaulting it
        // silently would let a truncated entry claim either.
        _paths.EnsureDirectories();
        const string truncatedCommand = """
            {"version":1,"turnId":1,"turnStartedTick64":5,"nextTicket":2,"idleExpiresTick64":0,
             "owner":{"kind":"workflow","key":"abc"},
             "ownerCommands":[{"ticket":1,"pid":4242,"processStartTicksUtc":99,"operation":"ui click","mode":"desktop-exclusive"}],
             "waiters":[]}
            """;
        File.WriteAllText(_paths.StatePath, truncatedCommand);

        using var stateLock = _store.AcquireStateLock(CancellationToken.None);
        var result = _store.Read();

        Assert.IsTrue(
            result.RecoveredFromCorruption,
            "an owner command missing status must not default into a runnable entry");
    }

    [TestMethod]
    public void Read_AFreshlyPublishedState_StillRoundTrips()
    {
        // The other half of the contract: requiring fields must not reject what this binary writes.
        _paths.EnsureDirectories();

        using var stateLock = _store.AcquireStateLock(CancellationToken.None);
        var published = InteractiveDesktopState.CreateFresh();
        published.Owner = new OwnerRecord { Kind = UiOwnerKind.Workflow, Key = "abc" };
        published.TurnId = 3;
        published.TurnStartedTick64 = 1_234;
        published.OwnerCommands.Add(new OwnerCommandEntry
        {
            Ticket = published.AllocateTicket(),
            Pid = 4242,
            ProcessStartTicksUtc = 99,
            Operation = "ui click",
            Mode = UiTurnMode.DesktopExclusive,
            Status = UiCommandStatus.Running,
        });
        published.Waiters.Add(new WaiterEntry
        {
            Ticket = published.AllocateTicket(),
            OwnerKey = "def",
            OwnerKind = UiOwnerKind.Anonymous,
            Pid = 5252,
            ProcessStartTicksUtc = 100,
            Operation = "ui screenshot",
            Mode = UiTurnMode.DesktopExclusive,
        });
        _store.Publish(published);

        var result = _store.Read();

        Assert.IsFalse(result.RecoveredFromCorruption, "this binary's own output must read back cleanly");
        Assert.IsFalse(result.UnknownNewerVersion);
        Assert.IsNotNull(result.State);
        Assert.AreEqual(3, result.State!.TurnId);
        Assert.AreEqual("abc", result.State.Owner!.Key);
        Assert.HasCount(1, result.State.OwnerCommands);
        Assert.HasCount(1, result.State.Waiters);
        Assert.AreEqual(UiOwnerKind.Anonymous, result.State.Waiters[0].OwnerKind);
        Assert.AreEqual(UiCommandStatus.Running, result.State.OwnerCommands[0].Status);
    }

    [TestMethod]
    public void Read_UnknownNewerVersion_IsNeverResetOrDowngraded()
    {
        _paths.EnsureDirectories();
        const string future = """{"version":99,"turnId":7,"nextTicket":3,"ownerCommands":[],"waiters":[]}""";
        File.WriteAllText(_paths.StatePath, future);

        using (var stateLock = _store.AcquireStateLock(CancellationToken.None))
        {
            var result = _store.Read();
            Assert.IsTrue(result.UnknownNewerVersion);
            Assert.IsNull(result.State);
            Assert.IsFalse(result.RecoveredFromCorruption, "a newer schema is not corruption");
        }

        Assert.AreEqual(future, File.ReadAllText(_paths.StatePath), "the newer document must be left alone");
    }

    [TestMethod]
    public void Read_UnknownNewerVersionWithIncompatibleFieldShapes_IsNotTreatedAsCorruption()
    {
        // A newer schema may change field *shapes*, not merely add fields. Here `owner` is a string
        // rather than an object, so strong deserialization throws — and the version check used to run
        // only AFTER that, so a perfectly valid v99 document was classified as corruption, quarantined,
        // and replaced with a v1 document. A published repro had `ui list-windows` silently downgrade
        // the file. The version must therefore be read from the raw document first.
        _paths.EnsureDirectories();
        const string future = """{"version":99,"owner":"a-newer-shape","turnId":7,"nextTicket":3}""";
        File.WriteAllText(_paths.StatePath, future);

        using (var stateLock = _store.AcquireStateLock(CancellationToken.None))
        {
            var result = _store.Read();
            Assert.IsTrue(result.UnknownNewerVersion, "a newer schema must be reported as such, not as corruption");
            Assert.IsNull(result.State);
            Assert.IsFalse(result.RecoveredFromCorruption);
        }

        Assert.AreEqual(future, File.ReadAllText(_paths.StatePath), "the newer document must be left byte-for-byte");
        Assert.AreEqual(
            0,
            Directory.GetFiles(_paths.LockDirectory, "state.corrupt-*.json").Length,
            "a newer document must never be quarantined");
    }

    [TestMethod]
    public void Read_MalformedJson_IsStillCorruptionNotAVersionDivert()
    {
        // The raw version probe must not swallow genuine corruption: a document that is not JSON at all
        // has no readable version and has to keep taking the guarded recovery path.
        WriteRawState("{this is not json");

        AssertRecovered();
    }

    [TestMethod]
    public void Read_VersionOfTheWrongJsonType_FallsThroughToCorruptionRecovery()
    {
        // `version` present but not a number is malformed rather than "newer", so it must not be
        // mistaken for a future schema and left in place forever.
        WriteRawState("""{"version":"ninety-nine","turnId":1,"nextTicket":2}""");

        AssertRecovered();
    }

    // ------------------------------------------------------------------- structural validation

    [TestMethod]
    public void Read_DuplicateTicketAcrossOwnerCommandsAndWaiters_IsCorrupt()
    {
        // Ticket 5 appearing in both lists would make two commands share one barrier position.
        WriteRawState("""
            {"version":1,"turnId":1,"nextTicket":9,"owner":{"kind":"workflow","key":"a"},
             "ownerCommands":[{"ticket":5,"pid":10,"processStartTicksUtc":1,"operation":"ui click","mode":"DesktopExclusive","status":"running"}],
             "waiters":[{"ticket":5,"ownerKey":"b","ownerKind":"workflow","pid":11,"processStartTicksUtc":2,"operation":"ui click","mode":"DesktopExclusive"}]}
            """);

        AssertRecovered();
    }

    [TestMethod]
    public void Read_NextTicketNotAheadOfEveryPersistedTicket_IsCorrupt()
    {
        // nextTicket 5 would re-issue ticket 5 and collide with the running command.
        WriteRawState("""
            {"version":1,"turnId":1,"nextTicket":5,"owner":{"kind":"workflow","key":"a"},
             "ownerCommands":[{"ticket":5,"pid":10,"processStartTicksUtc":1,"operation":"ui click","mode":"DesktopExclusive","status":"running"}],
             "waiters":[]}
            """);

        AssertRecovered();
    }

    [TestMethod]
    public void Read_OwnerCommandsWithoutAnOwner_IsCorrupt()
    {
        WriteRawState("""
            {"version":1,"turnId":1,"nextTicket":9,
             "ownerCommands":[{"ticket":5,"pid":10,"processStartTicksUtc":1,"operation":"ui click","mode":"DesktopExclusive","status":"running"}],
             "waiters":[]}
            """);

        AssertRecovered();
    }

    [TestMethod]
    public void Read_ObserveEntryCarryingATicket_IsCorrupt()
    {
        // Observations never serialize as barriers, so a ticket on one is meaningless and would be
        // compared against real barrier tickets.
        WriteRawState("""
            {"version":1,"turnId":1,"nextTicket":9,"owner":{"kind":"workflow","key":"a"},
             "ownerCommands":[{"ticket":5,"pid":10,"processStartTicksUtc":1,"operation":"ui inspect","mode":"Observe","status":"running"}],
             "waiters":[]}
            """);

        AssertRecovered();
    }

    [TestMethod]
    public void Read_OutOfRangeEnumValue_IsCorrupt()
    {
        WriteRawState("""
            {"version":1,"turnId":1,"nextTicket":9,"owner":{"kind":"workflow","key":"a"},
             "ownerCommands":[{"ticket":5,"pid":10,"processStartTicksUtc":1,"operation":"ui click","mode":42,"status":"running"}],
             "waiters":[]}
            """);

        AssertRecovered();
    }

    // ------------------------------------------------------------------- publication round-trip

    [TestMethod]
    public void Publish_RoundTripsStateAndPreservesUnknownFieldsFromANewerWriter()
    {
        // The fixture carries every field the v1 writer emits, because a document missing one of those
        // is now corruption rather than a partially populated state. Confirmed against a real published
        // file: version, turnId, turnStartedTick64, nextTicket, idleExpiresTick64, ownerCommands and
        // waiters are always written.
        _paths.EnsureDirectories();
        WriteRawState("""
            {"version":1,"turnId":3,"turnStartedTick64":100,"nextTicket":9,"idleExpiresTick64":0,
             "owner":{"kind":"workflow","key":"a","futureOwnerField":"keep-me"},
             "ownerCommands":[],"waiters":[],"futureRootField":{"nested":true}}
            """);

        using (var stateLock = _store.AcquireStateLock(CancellationToken.None))
        {
            var state = _store.Read().State!;
            state.TurnId = 4;
            _store.Publish(state);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(_paths.StatePath));
        Assert.AreEqual(4, document.RootElement.GetProperty("turnId").GetInt32());
        Assert.IsTrue(document.RootElement.TryGetProperty("futureRootField", out _),
            "unknown root fields from a newer writer must survive a rewrite");
        Assert.IsTrue(document.RootElement.GetProperty("owner").TryGetProperty("futureOwnerField", out _),
            "unknown owner fields from a newer writer must survive a rewrite");
    }

    [TestMethod]
    public void Publish_LeavesNoTemporaryFilesBehind()
    {
        using (var stateLock = _store.AcquireStateLock(CancellationToken.None))
        {
            _store.Publish(InteractiveDesktopState.CreateFresh());
        }

        Assert.AreEqual(0, Directory.EnumerateFiles(_paths.LockDirectory, "*.tmp").Count());
    }

    [TestMethod]
    public void StateRemainsReadableWhileTheActiveLockIsHeld()
    {
        // active.lock guards the desktop, not the metadata: a queued command must still be able to read
        // and update state while another process is mid-gesture.
        _paths.EnsureDirectories();

        // A process holding active.lock has necessarily registered first, so state already exists.
        // (Missing state while active.lock is held is a different case entirely — an external deletion —
        // and is deliberately fail-closed; see MissingStateWhileTheActiveLockIsHeldFailsClosed.)
        using (var seedLock = _store.AcquireStateLock(CancellationToken.None))
        {
            _store.Publish(InteractiveDesktopState.CreateFresh());
        }

        using var activeLock = new FileStream(
            _paths.ActiveLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        using var stateLock = _store.AcquireStateLock(CancellationToken.None);
        var state = _store.Read().State!;
        state.TurnId = 11;
        _store.Publish(state);

        Assert.IsFalse(_store.IsActiveLockFree());
    }

    // ------------------------------------------------------- missing state must not mint a new owner

    [TestMethod]
    public void MissingStateWithNoLivenessEvidenceIsATrueFirstUse()
    {
        _paths.EnsureDirectories();
        Assert.IsFalse(File.Exists(_paths.StatePath), "the test starts with no state file at all");

        using var stateLock = _store.AcquireStateLock(CancellationToken.None);
        var read = _store.Read();

        Assert.IsNotNull(read.State, "the ordinary first command on a desktop starts from a fresh document");
        Assert.IsNull(read.State!.Owner);
        Assert.IsFalse(read.RecoveredFromCorruption, "a first use is not a recovery");
    }

    [TestMethod]
    public void MissingStateWhileAParticipantIsLiveFailsClosed()
    {
        // An external deletion — AV, manual cleanup, a stray rmdir — while a recording or queued waiter
        // is still live. Rebuilding here would mint a second owner for the same desktop.
        _paths.EnsureDirectories();
        using var lease = _participants.OpenLease(
            _inspector.CurrentProcessId, _inspector.CurrentProcessStartTicksUtc);

        Assert.IsFalse(File.Exists(_paths.StatePath));

        using var stateLock = _store.AcquireStateLock(CancellationToken.None);
        var ex = Assert.ThrowsExactly<UiCoordinationException>(() => _store.Read());
        Assert.AreEqual(UiCoordinationErrorCodes.Unavailable, ex.Code);
    }

    [TestMethod]
    public void MissingStateWhileTheActiveLockIsHeldFailsClosed()
    {
        _paths.EnsureDirectories();
        using var activeLock = new FileStream(
            _paths.ActiveLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        Assert.IsFalse(File.Exists(_paths.StatePath));

        using var stateLock = _store.AcquireStateLock(CancellationToken.None);
        var ex = Assert.ThrowsExactly<UiCoordinationException>(() => _store.Read());
        Assert.AreEqual(UiCoordinationErrorCodes.Unavailable, ex.Code);
    }

    // ------------------------------------------------------------------ prior-boot deadline handling

    [TestMethod]
    public void PublishSurvivesAnUnrepresentableIdleDeadline()
    {
        // Environment.TickCount64 resets on reboot, so a state file written after long uptime can carry
        // a deadline far beyond the current uptime. Converting that delta to a UTC diagnostic overflows
        // DateTime; a diagnostic string must never be able to fail a publish.
        _paths.EnsureDirectories();
        var state = InteractiveDesktopState.CreateFresh();
        state.IdleExpiresTick64 = long.MaxValue;

        using var stateLock = _store.AcquireStateLock(CancellationToken.None);
        _store.Publish(state);

        Assert.IsNull(state.DiagnosticIdleExpiresUtc,
            "an unrepresentable deadline is omitted from diagnostics rather than throwing");
        Assert.IsTrue(File.Exists(_paths.StatePath), "the publish itself must still succeed");
    }

    // --------------------------------------------------------------------------- participant leases
    [TestMethod]
    public void Lease_IsHeldWhileOpenAndDeletedOnClose()
    {
        var leasePath = _paths.LeasePath(_inspector.CurrentProcessId, _inspector.CurrentProcessStartTicksUtc);

        var lease = _participants.OpenLease(_inspector.CurrentProcessId, _inspector.CurrentProcessStartTicksUtc);
        Assert.IsTrue(File.Exists(leasePath));
        Assert.IsTrue(_participants.IsParticipantLive(_inspector.CurrentProcessId, _inspector.CurrentProcessStartTicksUtc));
        Assert.IsTrue(_participants.AnyLiveParticipant());

        lease.Dispose();

        // DeleteOnClose means the OS removes the file, which is exactly what makes heartbeats unnecessary.
        Assert.IsFalse(File.Exists(leasePath));
        Assert.IsFalse(_participants.AnyLiveParticipant());
    }

    [TestMethod]
    public void Lease_OrphanedFileFromAPowerLossIsNotLiveAndIsCleanedUp()
    {
        _paths.EnsureDirectories();
        var orphan = _paths.LeasePath(999_999, 12_345);
        File.WriteAllText(orphan, string.Empty);

        Assert.IsFalse(_participants.IsParticipantLive(999_999, 12_345),
            "an openable lease proves its holder is gone");
        Assert.IsFalse(File.Exists(orphan), "the stale lease file must be removed while probing");
    }

    [TestMethod]
    public void Lease_FileNameRoundTripsThroughTheProcessIdentity()
    {
        var path = _paths.LeasePath(4242, 987_654_321);
        Assert.IsTrue(_paths.TryParseLeaseFileName(Path.GetFileName(path), out var pid, out var startTicks));
        Assert.AreEqual(4242, pid);
        Assert.AreEqual(987_654_321, startTicks);
    }

    // ------------------------------------------------------------------------- lock directory setup

    [TestMethod]
    public void EnsureDirectories_RepairsAnExistingDirectoryWithInheritedPermissions()
    {
        // A WINAPP_UI_LOCK_DIRECTORY pointed at a shared location can already exist with inherited
        // rules that let another user tamper with coordination state or hold a lease.
        Directory.CreateDirectory(_lockDirectory);
        var before = new DirectoryInfo(_lockDirectory).GetAccessControl();
        Assert.IsFalse(before.AreAccessRulesProtected, "precondition: the directory starts with inherited rules");

        _paths.EnsureDirectories();

        var after = new DirectoryInfo(_lockDirectory).GetAccessControl();
        Assert.IsTrue(after.AreAccessRulesProtected,
            "an existing coordination directory must not be left with inherited permissions");
    }

    [TestMethod]
    public void EnsureDirectories_IsSafeToCallRepeatedlyAndConcurrently()
    {
        // Every state-lock acquisition and lease open calls this, and several winapp processes can race.
        Parallel.For(0, 16, _ => _paths.EnsureDirectories());

        Assert.IsTrue(Directory.Exists(_paths.LockDirectory));
        Assert.IsTrue(Directory.Exists(_paths.ParticipantsDirectory));
    }

    [TestMethod]
    public void Paths_RejectARelativeOverrideDirectory()
    {
        Environment.SetEnvironmentVariable(
            InteractiveDesktopPaths.LockDirectoryOverrideVariable, "relative\\locks");

        // A relative path resolves against the caller's working directory, so two winapp processes
        // started in different folders would silently coordinate against different files.
        var ex = Assert.ThrowsExactly<UiCoordinationException>(() => new InteractiveDesktopPaths(_inspector));
        Assert.AreEqual(UiCoordinationErrorCodes.Unavailable, ex.Code);
    }

    [TestMethod]
    public void Paths_RejectANetworkOverrideDirectory()
    {
        Environment.SetEnvironmentVariable(
            InteractiveDesktopPaths.LockDirectoryOverrideVariable, @"\\server\share\locks");

        // SMB byte-range locking is advisory, so exclusive-share semantics would silently not exclude.
        var ex = Assert.ThrowsExactly<UiCoordinationException>(() => new InteractiveDesktopPaths(_inspector));
        Assert.AreEqual(UiCoordinationErrorCodes.Unavailable, ex.Code);
    }

    [TestMethod]
    public void Paths_ScopeEveryArtifactToTheWindowsSession()
    {
        // Two signed-in sessions have independent foreground/focus/input, so they must not queue
        // behind each other.
        var otherSession = new InteractiveDesktopPaths(new FakeProcessInspector { CurrentSessionId = 7 });

        Assert.AreNotEqual(_paths.StatePath, otherSession.StatePath);
        Assert.AreNotEqual(_paths.ActiveLockPath, otherSession.ActiveLockPath);
        Assert.AreNotEqual(_paths.StateLockPath, otherSession.StateLockPath);
    }

    private void WriteRawState(string json)
    {
        _paths.EnsureDirectories();
        File.WriteAllText(_paths.StatePath, json);
    }

    private void AssertRecovered()
    {
        using var stateLock = _store.AcquireStateLock(CancellationToken.None);
        var result = _store.Read();
        Assert.IsTrue(result.RecoveredFromCorruption,
            "structurally invalid scheduling state must be quarantined, not used");
        Assert.IsNotNull(result.State);
        Assert.IsNull(result.State!.Owner);
    }

    private sealed class FixedClock : IMonotonicClock
    {
        public long NowTicks64 => 1_000_000;

        public DateTimeOffset UtcNow => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    /// <summary>
    /// Reports this test process's real identity — the lease protocol needs a genuinely live process —
    /// while letting a test vary the Windows session id.
    /// </summary>
    private sealed class FakeProcessInspector : IProcessInspector
    {
        private readonly ProcessInspector _real = new();

        public int CurrentSessionId { get; init; } = 1;

        public int CurrentProcessId => _real.CurrentProcessId;

        public long CurrentProcessStartTicksUtc => _real.CurrentProcessStartTicksUtc;

        public bool? IsProcessAlive(int processId, long startTicksUtc) => _real.IsProcessAlive(processId, startTicksUtc);
    }

    // ------------------------------------------------------------------------ lock retryability

    [TestMethod]
    public void OnlySharingAndLockViolationsAreTreatedAsContention()
    {
        // Win32 codes arrive in the low word of HResult as 0x8007xxxx.
        Assert.IsTrue(CoordinationLockIo.IsContention(new IOException("busy", unchecked((int)0x80070020))),
            "ERROR_SHARING_VIOLATION means another process holds the file");
        Assert.IsTrue(CoordinationLockIo.IsContention(new IOException("locked", unchecked((int)0x80070021))),
            "ERROR_LOCK_VIOLATION means a byte-range lock is held");
    }

    [TestMethod]
    public void OtherIoFailuresAreNotContentionAndMustNotBeRetried()
    {
        // Retrying these forever would be indistinguishable from waiting on a real lock, so the command
        // would hang instead of reporting that coordination is unavailable.
        Assert.IsFalse(CoordinationLockIo.IsContention(new IOException("gone", unchecked((int)0x80070003))),
            "ERROR_PATH_NOT_FOUND will never clear by waiting");
        Assert.IsFalse(CoordinationLockIo.IsContention(new IOException("device", unchecked((int)0x8007001F))),
            "ERROR_GEN_FAILURE is a real device failure");
        Assert.IsFalse(CoordinationLockIo.IsContention(new IOException("handles", unchecked((int)0x80070004))),
            "ERROR_TOO_MANY_OPEN_FILES is a process-level failure");
        Assert.IsFalse(CoordinationLockIo.IsContention(new FileNotFoundException()),
            "a missing file is not contention");
    }

    [TestMethod]
    public void ANonContentionStateLockFailureReportsCoordinationUnavailable()
    {
        // A directory sitting where the lock file belongs makes FileStream fail with a non-sharing
        // error, which must fail closed rather than spin forever.
        _paths.EnsureDirectories();
        var occupied = Path.Combine(_paths.LockDirectory, "occupied.lock");
        Directory.CreateDirectory(occupied);

        var store = new InteractiveDesktopStateStore(
            new RedirectedStateLockPaths(_paths, occupied), _participants, new TickCountClock(),
            NullLogger<InteractiveDesktopStateStore>.Instance);

        var ex = Assert.ThrowsExactly<UiCoordinationException>(
            () => store.AcquireStateLock(CancellationToken.None).Dispose());
        Assert.AreEqual(UiCoordinationErrorCodes.Unavailable, ex.Code,
            "a real I/O failure must fail closed instead of retrying forever");
    }

    /// <summary>Redirects only <c>state.lock</c>, so a test can point it at an unusable path.</summary>
    private sealed class RedirectedStateLockPaths(IInteractiveDesktopPaths inner, string stateLockPath)
        : IInteractiveDesktopPaths
    {
        public string LockDirectory => inner.LockDirectory;

        public string ParticipantsDirectory => inner.ParticipantsDirectory;

        public string StatePath => inner.StatePath;

        public string StateLockPath => stateLockPath;

        public string ActiveLockPath => inner.ActiveLockPath;

        public string LeaseSearchPattern => inner.LeaseSearchPattern;

        public string LeasePath(int processId, long startTicksUtc) => inner.LeasePath(processId, startTicksUtc);

        public bool TryParseLeaseFileName(string fileName, out int processId, out long startTicksUtc)
            => inner.TryParseLeaseFileName(fileName, out processId, out startTicksUtc);

        public void EnsureDirectories() => inner.EnsureDirectories();
    }

    // ------------------------------------------------------------------------ directory ownership

    [TestMethod]
    public void ADirectoryOwnedByAnotherUserIsRejectedEvenWithACurrentUserOnlyDacl()
    {
        // The owner of an object implicitly holds WRITE_DAC, so a foreign owner can rewrite even a
        // protected, current-user-only DACL at any moment. Checking the DACL alone is not enough.
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var stranger = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        var security = new DirectorySecurity();
        security.SetOwner(stranger);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));

        Assert.IsFalse(InteractiveDesktopPaths.IsCurrentUserOnly(security, currentUser),
            "a foreign owner retains WRITE_DAC and can re-permission the directory behind our back");
    }

    [TestMethod]
    public void ADirectoryOwnedByTheCurrentUserWithAProtectedSelfOnlyDaclIsAccepted()
    {
        var currentUser = WindowsIdentity.GetCurrent().User!;

        var security = new DirectorySecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));

        Assert.IsTrue(InteractiveDesktopPaths.IsCurrentUserOnly(security, currentUser));
    }

    [TestMethod]
    public void ADirectoryGrantingAnotherIdentityIsRejectedEvenWhenOwnedByTheCurrentUser()
    {
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);

        var security = new DirectorySecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            everyone, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));

        Assert.IsFalse(InteractiveDesktopPaths.IsCurrentUserOnly(security, currentUser),
            "a world-writable coordination directory must never be accepted");
    }

    [TestMethod]
    public void ADirectoryWithInheritedRulesIsRejected()
    {
        var currentUser = WindowsIdentity.GetCurrent().User!;

        var security = new DirectorySecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);

        Assert.IsFalse(InteractiveDesktopPaths.IsCurrentUserOnly(security, currentUser),
            "inherited rules can grant whatever the parent grants, including other users");
    }
}

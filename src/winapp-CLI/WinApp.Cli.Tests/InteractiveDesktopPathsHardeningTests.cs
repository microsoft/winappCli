// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.AccessControl;
using System.Security.Principal;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Tests;

/// <summary>
/// What happens to coordination artifacts that were already sitting in a directory when its
/// permissions had to be repaired.
/// </summary>
/// <remarks>
/// Repairing the directory does not repair its contents. Windows keeps an explicit ACE on a child
/// file when a protected DACL is written to its parent — inheritance changes propagate only inherited
/// ACEs — so a file placed there beforehand stays writable by whoever placed it. Coordination would
/// then go on reading and trusting state another user can still rewrite, which is exactly the
/// isolation this hardening exists to provide.
/// </remarks>
[TestClass]
[DoNotParallelize] // WINAPP_UI_LOCK_DIRECTORY is process-wide.
public class InteractiveDesktopPathsHardeningTests
{
    private string _root = null!;
    private string? _previousOverride;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"winapp-acl-{Guid.NewGuid():N}");
        _previousOverride = Environment.GetEnvironmentVariable(
            InteractiveDesktopPaths.LockDirectoryOverrideVariable);
        Environment.SetEnvironmentVariable(
            InteractiveDesktopPaths.LockDirectoryOverrideVariable, _root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable(
            InteractiveDesktopPaths.LockDirectoryOverrideVariable, _previousOverride);

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leaked temp directory must never fail a test.
        }
    }

    /// <summary>Writes a file and grants <c>Everyone</c> full control on it, as a pre-seeder would.</summary>
    private static string SeedWorldWritable(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, "{\"version\":1,\"owner\":\"attacker\"}");

        var file = new FileInfo(path);
        var acl = file.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        file.SetAccessControl(acl);

        Assert.IsTrue(HasForeignGrant(path), "arrange failed: the Everyone ACE was not applied");
        return path;
    }

    private static bool HasForeignGrant(string path)
    {
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var rules = new FileInfo(path).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
        return rules.Cast<FileSystemAccessRule>()
            .Any(r => r.IdentityReference is SecurityIdentifier sid && sid != currentUser);
    }

    [TestMethod]
    public void APreSeededStateFileWithAnEveryoneGrantIsDiscarded()
    {
        // The reported repro. The directory's DACL is inherited, so it will be repaired — and the
        // explicit Everyone ACE on this file would survive that repair untouched.
        var seeded = SeedWorldWritable(_root, "interactive-desktop-1.state.json");

        new InteractiveDesktopPaths(new ProcessInspector()).EnsureDirectories();

        Assert.IsFalse(File.Exists(seeded),
            "a world-writable state file that predates the repair cannot be trusted or kept");
    }

    [TestMethod]
    public void APreSeededActiveLockWithAnEveryoneGrantIsDiscarded()
    {
        // The other half of the repro: holding active.lock would let another user block this desktop.
        var seeded = SeedWorldWritable(_root, "interactive-desktop-1.active.lock");

        new InteractiveDesktopPaths(new ProcessInspector()).EnsureDirectories();

        Assert.IsFalse(File.Exists(seeded));
    }

    [TestMethod]
    public void APreSeededLeaseIsDiscarded()
    {
        // A lease is liveness evidence. One written by somebody else is a claim about a participant
        // that never existed, and it would keep a phantom queue entry unprunable.
        var participants = Path.Combine(_root, "participants");
        var seeded = SeedWorldWritable(participants, "interactive-desktop-1-4242-638000000000000000.lease");

        new InteractiveDesktopPaths(new ProcessInspector()).EnsureDirectories();

        Assert.IsFalse(File.Exists(seeded));
    }

    [TestMethod]
    public void AQuarantinedCorruptStateCopyIsAlsoDiscarded()
    {
        var seeded = SeedWorldWritable(_root, "state.corrupt-20260101T000000.000Z.json");

        new InteractiveDesktopPaths(new ProcessInspector()).EnsureDirectories();

        Assert.IsFalse(File.Exists(seeded));
    }

    [TestMethod]
    public void AnArtifactThatSurvivesTheRepairFailsClosed()
    {
        // Held open, so it cannot be deleted. A process keeping a handle in a namespace that was just
        // proven untrusted is not liveness evidence worth acting on, so this must refuse to run rather
        // than coordinate through storage a third party can still write.
        Directory.CreateDirectory(_root);
        var held = Path.Combine(_root, "interactive-desktop-1.state.json");
        using var handle = new FileStream(held, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        var paths = new InteractiveDesktopPaths(new ProcessInspector());
        var ex = Assert.ThrowsExactly<UiCoordinationException>(() => paths.EnsureDirectories());

        Assert.AreEqual(UiCoordinationErrorCodes.Unavailable, ex.Code);
        StringAssert.Contains(ex.Message, "could not be removed");
        Assert.IsNotNull(ex.RecoveryHint, "a fail-closed error has to say what to do next");
    }

    [TestMethod]
    public void UnrelatedFilesInAnOverrideDirectoryAreLeftAlone()
    {
        // WINAPP_UI_LOCK_DIRECTORY can point at a path holding somebody's own files. Those are never
        // read by coordination, so they are not a trust question — and deleting them would be
        // destroying data that was not ours to touch.
        Directory.CreateDirectory(_root);
        var unrelated = Path.Combine(_root, "my-notes.txt");
        File.WriteAllText(unrelated, "keep me");

        new InteractiveDesktopPaths(new ProcessInspector()).EnsureDirectories();

        Assert.IsTrue(File.Exists(unrelated), "only this feature's own artifacts may be discarded");
        Assert.AreEqual("keep me", File.ReadAllText(unrelated));
    }

    [TestMethod]
    public void AnInsecureStateFileUnderAnAlreadySecuredDirectoryIsAlsoDiscarded()
    {
        // The case that is NOT covered by the directory-repair path. Here the directory is already
        // current-user-only, so the repair fast path returns early — but a state file inside it still
        // carries a foreign grant, exactly as it would if an earlier build had secured the directory
        // without clearing its contents.
        var paths = new InteractiveDesktopPaths(new ProcessInspector());
        paths.EnsureDirectories();
        Assert.IsTrue(
            InteractiveDesktopPaths.IsCurrentUserOnly(
                new DirectoryInfo(paths.LockDirectory).GetAccessControl(), WindowsIdentity.GetCurrent().User!),
            "arrange failed: the directory should already be secured");

        File.WriteAllText(paths.StatePath, "{\"version\":1,\"owner\":\"attacker\"}");
        var file = new FileInfo(paths.StatePath);
        var acl = file.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        file.SetAccessControl(acl);

        // A fresh instance repeats the whole check; the directory needs no repair this time.
        new InteractiveDesktopPaths(new ProcessInspector()).EnsureDirectories();

        Assert.IsFalse(File.Exists(paths.StatePath),
            "a world-writable state file must be discarded even when its directory is already secure");
    }

    [TestMethod]
    public void AnInsecureActiveLockUnderAnAlreadySecuredDirectoryIsAlsoDiscarded()
    {
        var paths = new InteractiveDesktopPaths(new ProcessInspector());
        paths.EnsureDirectories();

        File.WriteAllText(paths.ActiveLockPath, "x");
        var file = new FileInfo(paths.ActiveLockPath);
        var acl = file.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        file.SetAccessControl(acl);

        new InteractiveDesktopPaths(new ProcessInspector()).EnsureDirectories();

        Assert.IsFalse(File.Exists(paths.ActiveLockPath),
            "another user able to hold active.lock would be able to block this desktop");
    }

    [TestMethod]
    public void AnInsecureStateFileThatCannotBeReplacedFailsClosed()
    {
        var paths = new InteractiveDesktopPaths(new ProcessInspector());
        paths.EnsureDirectories();

        File.WriteAllText(paths.StatePath, "{}");
        var file = new FileInfo(paths.StatePath);
        var acl = file.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        file.SetAccessControl(acl);

        using var held = new FileStream(paths.StatePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var ex = Assert.ThrowsExactly<UiCoordinationException>(
            () => new InteractiveDesktopPaths(new ProcessInspector()).EnsureDirectories());

        Assert.AreEqual(UiCoordinationErrorCodes.Unavailable, ex.Code);
        StringAssert.Contains(ex.Message, "reachable by another user");
    }

    [TestMethod]
    public void AStateFileWithAdministratorsAndSystemAcesIsKept()
    {
        // Half of the CI regression this check caused, in the half reproducible without elevation.
        // Administrators and SYSTEM can already take ownership of anything, so their presence is not a
        // boundary this can defend and must not read as hostile. (The other half is a file *owned* by
        // BUILTIN\Administrators, which Windows assigns by default under an elevated process; setting
        // that owner needs SeRestorePrivilege, so it is covered by the same carve-out and exercised by
        // the elevated CI lane rather than arranged here.)
        var paths = new InteractiveDesktopPaths(new ProcessInspector());
        paths.EnsureDirectories();

        File.WriteAllText(paths.StatePath, "{\"version\":1}");
        var file = new FileInfo(paths.StatePath);
        var acl = file.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        file.SetAccessControl(acl);

        new InteractiveDesktopPaths(new ProcessInspector()).EnsureDirectories();

        Assert.IsTrue(File.Exists(paths.StatePath),
            "an Administrators/SYSTEM ACE is not another standard user and must not discard live state");
    }

    [TestMethod]
    public void AStateFileGrantedToAnotherStandardUserIsStillDiscarded()
    {
        // The boundary that does matter, and the one the Administrators carve-out must not widen:
        // an ACE naming an ordinary account that is not this user.
        var paths = new InteractiveDesktopPaths(new ProcessInspector());
        paths.EnsureDirectories();

        File.WriteAllText(paths.StatePath, "{\"version\":1}");
        var file = new FileInfo(paths.StatePath);
        var acl = file.GetAccessControl();
        // Guests is a well-known group, but not a privileged one — it stands in for any other account.
        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        file.SetAccessControl(acl);

        new InteractiveDesktopPaths(new ProcessInspector()).EnsureDirectories();

        Assert.IsFalse(File.Exists(paths.StatePath),
            "a grant to any non-privileged identity other than this user is still untrusted");
    }

    [TestMethod]
    public void AnAlreadySecuredDirectoryKeepsItsContents()
    {
        // The steady state, and the case that must not become destructive: a second winapp process
        // joining a directory this user already secured must not discard the first process's state.
        // This is also the every-command path, so it must stay free of per-file work beyond the three
        // trusted files.
        var paths = new InteractiveDesktopPaths(new ProcessInspector());
        paths.EnsureDirectories();

        File.WriteAllText(paths.StatePath, "{\"version\":1}");

        new InteractiveDesktopPaths(new ProcessInspector()).EnsureDirectories();

        Assert.IsTrue(File.Exists(paths.StatePath),
            "a state file this user owns, inheriting from a secured directory, is trustworthy");
        Assert.AreEqual("{\"version\":1}", File.ReadAllText(paths.StatePath));
    }

    [TestMethod]
    public void RepairLeavesBothDirectoriesReachableOnlyByThisUser()
    {
        Directory.CreateDirectory(_root);

        var paths = new InteractiveDesktopPaths(new ProcessInspector());
        paths.EnsureDirectories();

        var currentUser = WindowsIdentity.GetCurrent().User!;
        Assert.IsTrue(
            InteractiveDesktopPaths.IsCurrentUserOnly(
                new DirectoryInfo(paths.LockDirectory).GetAccessControl(), currentUser));
        Assert.IsTrue(
            InteractiveDesktopPaths.IsCurrentUserOnly(
                new DirectoryInfo(paths.ParticipantsDirectory).GetAccessControl(), currentUser),
            "the participants directory holds leases and needs the same protection");
    }
}

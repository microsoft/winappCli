// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

[TestClass]
public class TargetStateDirectorySecurityTests
{
    private string _root = null!;
    private SecurityIdentifier _user = null!;
    private static readonly SecurityIdentifier ForeignUser = new("S-1-5-21-111111111-222222222-333333333-1001");

    [TestInitialize]
    public void Setup()
    {
        using var identity = WindowsIdentity.GetCurrent();
        _user = identity.User!;
        _root = Path.Combine(Path.GetTempPath(), $"target-state-acl-tests-{Guid.NewGuid():N}");
        new DirectoryInfo(_root).Create(PrivateSecurity());
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public void NewNamespace_HasProtectedCurrentUserAndSystemPermissions()
    {
        var targets = Path.Join(_root, "targets");
        var result = Provider(targets).GetTargetRoot(WindowsSandboxTarget.Default);

        foreach (var path in new[] { targets, result.FullName })
        {
            var security = new DirectoryInfo(path).GetAccessControl();
            Assert.IsTrue(security.AreAccessRulesProtected);
            Assert.AreEqual(_user, security.GetOwner(typeof(SecurityIdentifier)));
            Assert.IsTrue(TargetStateDirectorySecurity.IsTrusted(security, _user));
            var allowed = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Where(rule => rule.AccessControlType == AccessControlType.Allow)
                .ToArray();
            Assert.AreEqual(2, allowed.Length);
            Assert.IsTrue(allowed.Any(rule => rule.IdentityReference.Equals(_user)));
            Assert.IsTrue(allowed.Any(rule => ((SecurityIdentifier)rule.IdentityReference)
                .IsWellKnown(WellKnownSidType.LocalSystemSid)));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InheritedTrustedState_IsPreservedWithoutRewritingPermissions(bool create)
    {
        var targets = Directory.CreateDirectory(Path.Join(_root, "targets"));
        var target = Directory.CreateDirectory(Path.Join(targets.FullName, WindowsSandboxTarget.Default.StateKey));
        var file = Path.Join(target.FullName, "target-state.json");
        File.WriteAllText(file, "keep");
        var before = Snapshot(targets.FullName, target.FullName, file);
        Assert.IsFalse(target.GetAccessControl().AreAccessRulesProtected);

        Provider(targets.FullName + Path.DirectorySeparatorChar).GetTargetRoot(WindowsSandboxTarget.Default, create);

        CollectionAssert.AreEqual(before, Snapshot(targets.FullName, target.FullName, file));
        Assert.AreEqual("keep", File.ReadAllText(file));
    }

    [TestMethod]
    public void ReadOnlyMissingTarget_DoesNotCreateOrChangeAnything()
    {
        var before = Snapshot(_root);
        var targets = Path.Join(_root, "missing", "targets");

        var result = Provider(targets).GetTargetRoot(WindowsSandboxTarget.Default, create: false);

        Assert.IsFalse(result.Exists);
        Assert.AreEqual(0, Directory.GetFileSystemEntries(_root).Length);
        CollectionAssert.AreEqual(before, Snapshot(_root));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExistingForeignDirectoryGrant_FailsWithoutRepairOrDeletion(bool create)
    {
        var targets = Directory.CreateDirectory(Path.Join(_root, "targets"));
        AddGrant(targets, ForeignUser, FileSystemRights.ReadAndExecute);
        var before = Snapshot(targets.FullName);

        AssertUnavailable(() => Provider(targets.FullName).GetTargetRoot(WindowsSandboxTarget.Default, create));

        CollectionAssert.AreEqual(before, Snapshot(targets.FullName));
        Assert.AreEqual(0, targets.GetFileSystemInfos().Length);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExistingExposedConnection_FailsEvenUnderPrivateDirectories(bool create)
    {
        var targets = Path.Join(_root, "targets");
        var target = Provider(targets).GetTargetRoot(WindowsSandboxTarget.Default);
        var bootstrap = Directory.CreateDirectory(Path.Join(target.FullName, "bootstrap-123"));
        var connection = new FileInfo(Path.Join(bootstrap.FullName, "connection.json"));
        File.WriteAllText(connection.FullName, "{\"sharedKey\":\"previously-exposed\"}");
        AddGrant(connection, ForeignUser, FileSystemRights.ReadData);
        var before = Snapshot(targets, target.FullName, bootstrap.FullName, connection.FullName);

        AssertUnavailable(() => Provider(targets).GetTargetRoot(WindowsSandboxTarget.Default, create));

        CollectionAssert.AreEqual(before, Snapshot(targets, target.FullName, bootstrap.FullName, connection.FullName));
        StringAssert.Contains(File.ReadAllText(connection.FullName), "previously-exposed");
    }

    [TestMethod]
    public void SharedWritableAncestor_CannotReplacePrivateNamespace()
    {
        var shared = Directory.CreateDirectory(Path.Join(_root, "shared"));
        var targets = Path.Join(shared.FullName, "targets");
        Provider(targets).GetTargetRoot(WindowsSandboxTarget.Default);
        AddGrant(shared, ForeignUser, FileSystemRights.DeleteSubdirectoriesAndFiles);
        var before = Snapshot(shared.FullName, targets);

        AssertUnavailable(() => Provider(targets).GetTargetRoot(WindowsSandboxTarget.Default, create: false));

        CollectionAssert.AreEqual(before, Snapshot(shared.FullName, targets));
    }

    [TestMethod]
    public void PublicReadOnlyAncestor_DoesNotExposeProtectedState()
    {
        AddGrant(new DirectoryInfo(_root), ForeignUser, FileSystemRights.ReadAndExecute);

        Assert.IsTrue(Provider(Path.Join(_root, "targets")).GetTargetRoot(WindowsSandboxTarget.Default).Exists);
    }

    [TestMethod]
    public void SecuringTargetState_LeavesCacheAndUiSiblingPermissionsUnchanged()
    {
        var cache = Directory.CreateDirectory(Path.Join(_root, ".winapp"));
        var cacheData = Directory.CreateDirectory(Path.Join(cache.FullName, "packages"));
        AddGrant(cacheData, ForeignUser, FileSystemRights.FullControl);
        var state = Directory.CreateDirectory(Path.Join(cache.FullName, "state"));
        var ui = Directory.CreateDirectory(Path.Join(state.FullName, "ui"));
        var marker = Path.Join(cacheData.FullName, "keep.txt");
        File.WriteAllText(marker, "cache");
        var before = Snapshot(cache.FullName, cacheData.FullName, state.FullName, ui.FullName, marker);

        Provider(Path.Join(state.FullName, "targets")).GetTargetRoot(WindowsSandboxTarget.Default);

        CollectionAssert.AreEqual(before, Snapshot(cache.FullName, cacheData.FullName, state.FullName, ui.FullName, marker));
        Assert.AreEqual("cache", File.ReadAllText(marker));
    }

    [TestMethod]
    public void LocalJunction_IsRejectedWithoutTouchingItsDestination()
    {
        var destination = Directory.CreateDirectory(Path.Join(_root, "shared"));
        AddGrant(destination, ForeignUser, FileSystemRights.FullControl);
        var junction = Path.Join(_root, ".winapp");
        var before = Snapshot(destination.FullName);
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "/c", "mklink", "/J", junction, destination.FullName },
        })!;
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, "The test-owned junction must be created.");

        try
        {
            AssertUnavailable(() => Provider(Path.Join(junction, "state", "targets"))
                .GetTargetRoot(WindowsSandboxTarget.Default));
            Assert.AreEqual(0, destination.GetFileSystemInfos().Length);
            CollectionAssert.AreEqual(before, Snapshot(destination.FullName));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    [TestMethod]
    public void ReparsePointInsideState_IsRejected()
    {
        var targets = Path.Join(_root, "targets");
        var target = Provider(targets).GetTargetRoot(WindowsSandboxTarget.Default);
        var destination = Directory.CreateDirectory(Path.Join(_root, "outside"));
        var junction = Path.Join(target.FullName, "bootstrap-123");
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "/c", "mklink", "/J", junction, destination.FullName },
        })!;
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode);

        try
        {
            AssertUnavailable(() => Provider(targets).GetTargetRoot(WindowsSandboxTarget.Default, create: false));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    [TestMethod]
    public void ConcurrentCreators_VerifyTheSamePrivateNamespace()
    {
        var targets = Path.Join(_root, "targets");

        Parallel.For(0, 12, _ =>
            Assert.IsTrue(Provider(targets).GetTargetRoot(WindowsSandboxTarget.Default).Exists));

        Assert.IsTrue(TargetStateDirectorySecurity.IsTrusted(new DirectoryInfo(targets).GetAccessControl(), _user));
    }

    [TestMethod]
    public void SystemAndAdministratorsGrants_DoNotInvalidateState()
    {
        var targets = Path.Join(_root, "targets");
        var target = Provider(targets).GetTargetRoot(WindowsSandboxTarget.Default);
        var file = new FileInfo(Path.Join(target.FullName, "target-state.json"));
        File.WriteAllText(file.FullName, "keep");
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            AddGrant(target, new SecurityIdentifier(sid, null), FileSystemRights.FullControl);
            AddGrant(file, new SecurityIdentifier(sid, null), FileSystemRights.FullControl);
        }

        Provider(targets).GetTargetRoot(WindowsSandboxTarget.Default, create: false);

        Assert.AreEqual("keep", File.ReadAllText(file.FullName));
    }

    [TestMethod]
    public void ForeignOwner_IsUntrustedEvenWithPrivateDacl()
    {
        var security = PrivateSecurity();
        security.SetOwner(ForeignUser);

        Assert.IsFalse(TargetStateDirectorySecurity.IsTrusted(security, _user));
        Assert.IsFalse(TargetStateDirectorySecurity.IsTrusted(security, _user, allowAncestorAccess: true));
    }

    [TestMethod]
    public void ExistingForeignOwner_FailsWithoutRepairWhenOwnershipCanBeAssigned()
    {
        var targets = Path.Join(_root, "targets");
        var target = Provider(targets).GetTargetRoot(WindowsSandboxTarget.Default);
        var security = target.GetAccessControl();
        security.SetOwner(ForeignUser);
        try
        {
            target.SetAccessControl(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException or InvalidOperationException)
        {
            Assert.Inconclusive($"Windows did not permit assigning the test's foreign owner: {ex.Message}");
        }

        try
        {
            Assert.AreEqual(ForeignUser, target.GetAccessControl().GetOwner(typeof(SecurityIdentifier)));
            AssertUnavailable(() => Provider(targets).GetTargetRoot(WindowsSandboxTarget.Default, create: false));
            Assert.AreEqual(ForeignUser, target.GetAccessControl().GetOwner(typeof(SecurityIdentifier)));
        }
        finally
        {
            security.SetOwner(_user);
            target.SetAccessControl(security);
        }
    }

    [TestMethod]
    [DataRow(WellKnownSidType.LocalSystemSid)]
    [DataRow(WellKnownSidType.BuiltinAdministratorsSid)]
    public void PrivilegedOwner_IsTrusted(WellKnownSidType owner)
    {
        var security = PrivateSecurity();
        security.SetOwner(new SecurityIdentifier(owner, null));

        Assert.IsTrue(TargetStateDirectorySecurity.IsTrusted(security, _user));
    }

    [TestMethod]
    public void NullDacl_IsNotPrivate()
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm($"O:{_user.Value}D:NO_ACCESS_CONTROL");

        Assert.IsFalse(TargetStateDirectorySecurity.IsTrusted(security, _user));
    }

    private static TargetStateDirectoryProvider Provider(string targets) => new(targets)
    {
        UserProfileProvider = () => throw new AssertFailedException("Tests must never consult the live user profile."),
    };

    private DirectorySecurity PrivateSecurity()
    {
        var security = new DirectorySecurity();
        security.SetOwner(_user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            _user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static void AssertUnavailable(Action action)
    {
        var exception = Assert.ThrowsExactly<ExecutionTargetException>(action);
        Assert.AreEqual(ExecutionTargetErrorCodes.StateUnavailable, exception.Error.Code);
        StringAssert.Contains(exception.Error.UserAction, "secure");
        StringAssert.Contains(exception.Error.UserAction, "connection keys");
        Assert.IsNull(exception.Error.NextCommand);
    }

    private static string[] Snapshot(params string[] paths) =>
        paths.Select(path =>
        {
            FileSystemSecurity security = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl()
                : new FileInfo(path).GetAccessControl();
            return security.GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
        }).ToArray();

    private static void AddGrant(FileSystemInfo item, SecurityIdentifier sid, FileSystemRights rights)
    {
        if (item is DirectoryInfo directory)
        {
            var security = directory.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(sid, rights, AccessControlType.Allow));
            directory.SetAccessControl(security);
        }
        else
        {
            var file = (FileInfo)item;
            var security = file.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(sid, rights, AccessControlType.Allow));
            file.SetAccessControl(security);
        }
    }
}

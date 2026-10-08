// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.AccessControl;
using System.Security.Principal;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

/// <summary>
/// Host folders handed to <c>wsb share</c> must be openable by SYSTEM, or Windows Sandbox refuses
/// them with <c>E_ACCESSDENIED</c>.
/// </summary>
[TestClass]
public class SandboxShareAccessTests
{
    private static readonly SecurityIdentifier s_system = new(WellKnownSidType.LocalSystemSid, null);

    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = new DirectoryInfo(TestPaths.TempRoot(nameof(SandboxShareAccessTests)));
        _root.Create();
        RestrictToCurrentUser(_root);
    }

    [TestCleanup]
    public void Cleanup()
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

    [TestMethod]
    public void AFolderUnderACurrentUserOnlyParent_IsGrantedSystem()
    {
        var folder = _root.CreateSubdirectory("bootstrap-0123");
        Assert.IsFalse(
            SandboxShareAccess.GrantsHostService(folder.GetAccessControl()),
            "arrange failed: the folder should inherit a DACL without SYSTEM");

        SandboxShareAccess.EnsureHostServiceAccess(folder.FullName);

        folder.Refresh();
        Assert.IsTrue(SandboxShareAccess.GrantsHostService(folder.GetAccessControl()));
        Assert.IsTrue(
            Rules(folder).Any(rule => rule.IdentityReference.Equals(WindowsIdentity.GetCurrent().User)),
            "the current user's own access must be kept");
    }

    [TestMethod]
    public void AFolderThatAlreadyGrantsSystem_IsLeftUnchanged()
    {
        var folder = _root.CreateSubdirectory("bootstrap-0123");
        SandboxShareAccess.EnsureHostServiceAccess(folder.FullName);
        folder.Refresh();
        var before = Rules(folder).Count(rule => s_system.Equals(rule.IdentityReference));

        SandboxShareAccess.EnsureHostServiceAccess(folder.FullName);

        folder.Refresh();
        Assert.AreEqual(before, Rules(folder).Count(rule => s_system.Equals(rule.IdentityReference)));
    }

    [TestMethod]
    public void AnInheritOnlyGrant_DoesNotCountAsAccessToTheFolder()
    {
        var security = new DirectorySecurity();
        security.AddAccessRule(new FileSystemAccessRule(
            s_system,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.InheritOnly,
            AccessControlType.Allow));

        Assert.IsFalse(SandboxShareAccess.GrantsHostService(security));
    }

    private static List<FileSystemAccessRule> Rules(DirectoryInfo folder) =>
        [.. folder.GetAccessControl()
            .GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()];

    private static void RestrictToCurrentUser(DirectoryInfo directory)
    {
        var user = WindowsIdentity.GetCurrent().User!;
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        directory.SetAccessControl(security);
    }
}

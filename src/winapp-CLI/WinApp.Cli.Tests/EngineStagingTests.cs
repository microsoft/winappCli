// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WinApp.Cli.Tests;

[TestClass]
public class EngineStagingTests
{
    [TestMethod]
    [DataRow(FileSystemRights.ReadAndExecute, true)]
    [DataRow(FileSystemRights.WriteData, false)]
    [DataRow(FileSystemRights.AppendData, false)]
    [DataRow(FileSystemRights.DeleteSubdirectoriesAndFiles, false)]
    [DataRow(FileSystemRights.ChangePermissions, false)]
    [DataRow(FileSystemRights.TakeOwnership, false)]
    [DataRow(FileSystemRights.Delete, false)]
    public void Correction_StagingAcl_RejectsForeignMutationNotRead(FileSystemRights rights, bool trusted)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetOwner(identity.User!);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            rights, AccessControlType.Allow));
        Assert.AreEqual(trusted, EngineStaging.IsTrustedSecurity(security, identity.User!, directory: true));
    }

    [TestMethod]
    public void Correction_StagingAcl_HandlesSystemAncestorsAndInheritanceOnly()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetOwner(new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.AppendData, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null),
            FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.InheritOnly, AccessControlType.Allow));
        Assert.IsTrue(EngineStaging.IsTrustedSecurity(security, identity.User!, true, true, true));
        Assert.IsFalse(EngineStaging.IsTrustedSecurity(security, identity.User!, true, false, true));
        Assert.IsFalse(EngineStaging.IsTrustedSecurity(security, identity.User!, true, true, false));
    }

    [TestMethod]
    public void Correction_StagingAcl_ForeignOwnerAndNullDaclAreRefused()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetOwner(new SecurityIdentifier("S-1-5-21-1-2-3-1001"));
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        Assert.IsFalse(EngineStaging.IsTrustedSecurity(security, identity.User!, true));
        var nullDacl = new DirectorySecurity();
        nullDacl.SetSecurityDescriptorSddlForm($"O:{identity.User!.Value}D:NO_ACCESS_CONTROL");
        Assert.IsFalse(EngineStaging.IsTrustedSecurity(nullDacl, identity.User!, true));
    }

    private string _root = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "winapp-engine-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public void IdentityAndRoot_UseUnpackagedHostAndNonVirtualizedRoot()
    {
        Assert.IsFalse(EngineStaging.IsPackaged);
        Assert.AreEqual(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".winapp", "engine"),
            EngineStaging.EngineRoot);
    }

    [TestMethod]
    public void StageForForeignLoad_UnpackagedUsesExistingSource()
    {
        var source = Source("WinApp.DevTools.Native.dll", [1, 2, 3]);
        Assert.AreEqual(source, EngineStaging.StageForForeignLoad(source));
    }

    [TestMethod]
    public void Stage_MissingSourceFailsWithoutCreatingDestination()
    {
        var missing = Path.Combine(_root, "missing.dll");
        var destination = Path.Combine(_root, "engine");
        Assert.ThrowsExactly<FileNotFoundException>(() => EngineStaging.StageForForeignLoad(missing));
        Assert.ThrowsExactly<FileNotFoundException>(() => EngineStaging.StageTo(missing, destination));
        Assert.IsFalse(Directory.Exists(destination));
    }

    [TestMethod]
    public void Stage_CopiesExactBytesAndRetainsOwnedCopy()
    {
        byte[] bytes = [10, 20, 30, 40];
        var source = Source("WinApp.DevTools.Native.dll", bytes);
        var destination = Path.Combine(_root, "engine");
        var staged = EngineStaging.StageTo(source, destination);
        Assert.AreNotEqual(source, staged);
        Assert.AreEqual("WinApp.DevTools.Native.dll", Path.GetFileName(staged));
        Assert.IsTrue(staged.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(staged));
        Assert.AreEqual(staged, EngineStaging.StageTo(source, destination));
        Assert.AreEqual(1, Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(source));
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectoryInfo(Path.GetDirectoryName(staged)!).GetAccessControl();
        Assert.IsTrue(security.AreAccessRulesProtected);
        Assert.AreEqual(identity.User, security.GetOwner(typeof(SecurityIdentifier)));
    }

    [TestMethod]
    public void Stage_SeparateRootsAndSourcesCannotReuseWrongCopy()
    {
        var firstSource = Source("first.dll", [1]);
        var secondSource = Source("second.dll", [2]);
        var root = Path.Combine(_root, "engine");
        var first = EngineStaging.StageTo(firstSource, root);
        var second = EngineStaging.StageTo(secondSource, root);
        var otherRoot = EngineStaging.StageTo(firstSource, Path.Combine(_root, "other-engine"));
        Assert.AreNotEqual(first, second);
        Assert.AreNotEqual(first, otherRoot);
        CollectionAssert.AreEqual(new byte[] { 2 }, File.ReadAllBytes(second));
    }

    [TestMethod]
    public void Stage_CopyFailureNeverReturnsSourceFallback()
    {
        var source = Source("WinApp.DevTools.Native.dll", [1]);
        var invalidRoot = Source("not-a-directory", [2]);
        Assert.Throws<IOException>(() => EngineStaging.StageTo(source, invalidRoot));
        CollectionAssert.AreEqual(new byte[] { 1 }, File.ReadAllBytes(source));
    }

    [TestMethod]
    public void Stage_ParallelCallersShareOneCompleteCopy()
    {
        var source = Source("WinApp.DevTools.Native.dll", [1, 2, 3]);
        var destination = Path.Combine(_root, "engine");
        var paths = Enumerable.Range(0, 12).AsParallel().Select(_ => EngineStaging.StageTo(source, destination)).ToArray();
        Assert.AreEqual(1, paths.Distinct().Count());
        foreach (var path in paths)
        {
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        }
    }

    [TestMethod]
    public void Correction_StagingReparseRefusal_PreservesTargetBytesAndPermissions()
    {
        var source = Source("WinApp.DevTools.Native.dll", [1, 2, 3]);
        var target = Directory.CreateDirectory(Path.Combine(_root, "owned-target"));
        var originalAcl = target.GetAccessControl().GetSecurityDescriptorBinaryForm();
        var link = Path.Combine(_root, "engine-link");
        try
        {
            Directory.CreateSymbolicLink(link, target.FullName);
        }
        catch (UnauthorizedAccessException ex)
        {
            Assert.Inconclusive("Symlink creation privilege is unavailable: " + ex.Message);
        }
        try
        {
            Assert.Throws<IOException>(() => EngineStaging.StageTo(source, link));
            CollectionAssert.AreEqual(originalAcl, target.GetAccessControl().GetSecurityDescriptorBinaryForm());
            Assert.AreEqual(0, target.GetFileSystemInfos().Length);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(source));
        }
        finally { Directory.Delete(link); }
    }

    [TestMethod]
    public void Correction_CachedReparseFile_IsRevalidatedAndRefused()
    {
        var source = Source("WinApp.DevTools.Native.dll", [1, 2, 3]);
        var root = Path.Combine(_root, "engine");
        var staged = EngineStaging.StageTo(source, root);
        File.Delete(staged);
        try
        {
            File.CreateSymbolicLink(staged, source);
        }
        catch (UnauthorizedAccessException ex)
        {
            Assert.Inconclusive("Symlink creation privilege is unavailable: " + ex.Message);
        }
        try
        {
            Assert.Throws<IOException>(() => EngineStaging.StageTo(source, root));
            Assert.IsTrue((File.GetAttributes(staged) & FileAttributes.ReparsePoint) != 0);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(source));
        }
        finally { File.Delete(staged); }
    }

    private string Source(string name, byte[] bytes)
    {
        var source = Path.Combine(_root, name);
        File.WriteAllBytes(source, bytes);
        return source;
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class LayoutLockExclusionTests
{
    private DirectoryInfo _root = null!;
    private TaskContext _taskContext = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, $"layout-exclusion-tests-{Guid.NewGuid():N}"));
        _taskContext = new TaskContext(new GroupableTask("layout exclusions", null), null,
            new TestConsole(), NullLogger.Instance, new Lock());
    }

    [TestCleanup]
    public void Cleanup() => _root.Delete(recursive: true);

    [TestMethod]
    [DataRow("AppX", "Exact")]
    [DataRow("AppX", "Additive")]
    [DataRow(@"publish\custom-layout", "Exact")]
    [DataRow(@"publish\custom-layout", "Additive")]
    public void LooseLayout_DefaultAndExplicitNestedOutputs_NeverCopyLocks(
        string relativeLayout, string mode)
    {
        var reconciliation = Enum.Parse<LayoutReconciliation>(mode);
        var source = _root.CreateSubdirectory("source");
        WritePayload(source);
        var manifest = WriteFile(_root, "Package.appxmanifest", "<Package />");
        var layout = new DirectoryInfo(Path.Combine(source.FullName, relativeLayout));
        using var lease = LayoutLease.Acquire(layout, TestContext.CancellationToken);
        using var nestedLease = LayoutLease.Acquire(
            new DirectoryInfo(Path.Combine(source.FullName, "nested", "another-layout")), TestContext.CancellationToken);

        Sync(source, layout, manifest, reconciliation);
        var obsolete = WriteFile(layout, "obsolete.dll", "old build");
        Sync(source, layout, manifest, reconciliation);

        AssertPayloadOnly(layout);
        Assert.AreEqual(reconciliation == LayoutReconciliation.Additive, File.Exists(obsolete.FullName));
        Assert.IsTrue(File.Exists(LayoutLease.GetLockPath(layout)), "Copying must not consume the live lease.");
    }

    [TestMethod]
    public void SharedMsixAndBundleStaging_ExcludesLocksAtEveryDepth_NotWinappOrNestedAppX()
    {
        var source = _root.CreateSubdirectory("source");
        WritePayload(source);
        WriteFile(source, @"AppX\not-packaged.txt", "old generated layout");
        WriteFile(source, @"ordinary\AppX\keep.txt", "nested payload, not the top-level exclusion");
        var destination = new DirectoryInfo(Path.Combine(_root.FullName, "staging"));
        using var lease = LayoutLease.Acquire(new DirectoryInfo(Path.Combine(source.FullName, "AppX")),
            TestContext.CancellationToken);
        using var nestedLease = LayoutLease.Acquire(
            new DirectoryInfo(Path.Combine(source.FullName, "nested", "other")), TestContext.CancellationToken);

        Invoke("CopyDirectoryRecursive", source, destination,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AppX" });

        AssertPayloadOnly(destination);
        Assert.IsFalse(Directory.Exists(Path.Combine(destination.FullName, "AppX")));
        Assert.AreEqual("nested payload, not the top-level exclusion",
            File.ReadAllText(Path.Combine(destination.FullName, @"ordinary\AppX\keep.txt")));
    }

    [TestMethod]
    public async Task DeploymentSnapshot_DoesNotReadOrShipLiveLocks_ButIncludesBindings()
    {
        var source = _root.CreateSubdirectory("source");
        WritePayload(source);
        using var lease = LayoutLease.Acquire(new DirectoryInfo(Path.Combine(source.FullName, "AppX")),
            TestContext.CancellationToken);
        using var nestedLease = LayoutLease.Acquire(
            new DirectoryInfo(Path.Combine(source.FullName, "nested", "other")), TestContext.CancellationToken);

        var snapshot = await DeploymentPlanner.CreateSnapshotAsync(source, "deployment", TestContext.CancellationToken);

        var expected = new[] { "app.exe", "ordinary.lock", @".winapp\bindings\addon.node", @".winapp-layout-locks-backup\keep.txt" };
        CollectionAssert.AreEquivalent(expected, snapshot.Files.Select(file => file.RelativePath).ToArray());
    }

    [TestMethod]
    [DataRow("Skip")]
    [DataRow("Reject")]
    public void DeploymentAndPush_WalksExcludeReservedDirectories(string policy)
    {
        var source = _root.CreateSubdirectory("source");
        WritePayload(source);
        WriteFile(source, @".WINAPP-LAYOUT-LOCKS\ignore.lock", "bookkeeping");
        WriteFile(source, @"nested\.winapp-layout-locks\ignore.lock", "bookkeeping");

        var files = HostSourceWalker.EnumerateFiles(source.FullName, Enum.Parse<HostReparsePolicy>(policy), TestContext.CancellationToken);

        Assert.AreEqual(4, files.Count);
        Assert.IsFalse(files.Any(file => LayoutLease.IsArtifactPath(file.FullName)));
    }

    [TestMethod]
    [DataRow("Exact")]
    [DataRow("Additive")]
    [DataRow("None")]
    public void ExistingLockStateInsideLayout_IsRefusedBeforeCopyingOrPruning(string mode)
    {
        var reconciliation = Enum.Parse<LayoutReconciliation>(mode);
        var source = _root.CreateSubdirectory("source");
        WriteFile(source, "app.exe", "new app");
        var manifest = WriteFile(_root, "Package.appxmanifest", "<Package />");
        var layout = _root.CreateSubdirectory("layout");
        WriteFile(layout, "app.exe", "old app");
        var unrelated = WriteFile(layout, @"nested\.winapp-layout-locks\personal.txt", "do not delete");
        using var otherLease = LayoutLease.Acquire(
            new DirectoryInfo(Path.Combine(layout.FullName, "nested", "other")), TestContext.CancellationToken);

        var failure = Assert.ThrowsExactly<InvalidOperationException>(() =>
            Sync(source, layout, manifest, reconciliation));

        StringAssert.Contains(failure.Message, LayoutLease.LockDirectoryName, StringComparison.Ordinal);
        Assert.AreEqual("old app", File.ReadAllText(Path.Combine(layout.FullName, "app.exe")));
        Assert.AreEqual("do not delete", File.ReadAllText(unrelated.FullName));
        Assert.IsFalse(File.Exists(Path.Combine(layout.FullName, "appxmanifest.xml")));
    }

    [TestMethod]
    [DataRow("Exact")]
    [DataRow("Additive")]
    [DataRow("None")]
    public async Task Recipe_ReusedLayoutWithReservedState_IsNeverPruned(string mode)
    {
        var reconciliation = Enum.Parse<LayoutReconciliation>(mode);
        var layout = _root.CreateSubdirectory("layout");
        var source = WriteFile(_root, "app.exe", "new app");
        var old = WriteFile(layout, "app.exe", "old app");
        var state = WriteFile(layout, @".winapp-layout-locks\unrelated.txt", "keep");
        var recipe = WriteRecipe((source.FullName, "app.exe"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => CopyRecipe(recipe, layout, reconciliation));

        Assert.AreEqual("old app", File.ReadAllText(old.FullName));
        Assert.AreEqual("keep", File.ReadAllText(state.FullName));
    }

    [TestMethod]
    [DataRow(false, "Exact")]
    [DataRow(true, "Exact")]
    [DataRow(false, "Additive")]
    [DataRow(true, "Additive")]
    [DataRow(false, "None")]
    [DataRow(true, "None")]
    public async Task ExplicitRecipeLockReference_IsRejectedBeforeAnyCopy(
        bool reservedSource, string mode)
    {
        var reconciliation = Enum.Parse<LayoutReconciliation>(mode);
        var layout = new DirectoryInfo(Path.Combine(_root.FullName, "layout"));
        var ordinary = WriteFile(_root, "ordinary.txt", "payload");
        var reserved = WriteFile(_root, @".winapp-layout-locks\state.lock", "bookkeeping");
        var recipe = WriteRecipe(
            (ordinary.FullName, "ordinary.txt"),
            (reservedSource ? reserved.FullName : ordinary.FullName,
                reservedSource ? "renamed.txt" : @"nested\.WINAPP-LAYOUT-LOCKS\state.lock"));

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            CopyRecipe(recipe, layout, reconciliation));

        StringAssert.Contains(failure.Message, LayoutLease.LockDirectoryName, StringComparison.Ordinal);
        Assert.IsFalse(Directory.Exists(layout.FullName));
    }

    [TestMethod]
    public async Task Recipe_OrdinaryWinappBindingsArePayload()
    {
        var layout = new DirectoryInfo(Path.Combine(_root.FullName, "layout"));
        var binding = WriteFile(_root, @".winapp\bindings\addon.node", "binding");
        var recipe = WriteRecipe((binding.FullName, @".winapp\bindings\addon.node"));

        await CopyRecipe(recipe, layout, LayoutReconciliation.Exact);

        Assert.AreEqual("binding", File.ReadAllText(Path.Combine(layout.FullName, @".winapp\bindings\addon.node")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ManifestAssetCopy_RejectsExplicitLockPathsBeforeAnyCopy(bool reservedSource)
    {
        var source = _root.CreateSubdirectory("source");
        var destination = new DirectoryInfo(Path.Combine(_root.FullName, "destination"));
        var binding = WriteFile(source, @".winapp\bindings\addon.node", "binding");
        var reserved = WriteFile(source, @".winapp-layout-locks\state.lock", "bookkeeping");
        var entries = new List<(FileInfo, string)>
        {
            (binding, @".winapp\bindings\addon.node"),
            (reservedSource ? reserved : binding,
                reservedSource ? "renamed.txt" : @"nested\.winapp-layout-locks\state.lock"),
        };

        Assert.ThrowsExactly<InvalidOperationException>(() => IncrementalCopyHelper.CopyFiles(entries, destination));
        Assert.IsFalse(Directory.Exists(destination.FullName));
        IncrementalCopyHelper.CopyFiles([(binding, @".winapp\bindings\addon.node")], destination);
        Assert.AreEqual("binding", File.ReadAllText(Path.Combine(destination.FullName, @".winapp\bindings\addon.node")));
    }

    [TestMethod]
    public void NonImageManifestReference_CannotAddAReservedArtifactBackToStaging()
    {
        var source = _root.CreateSubdirectory("source");
        var destination = _root.CreateSubdirectory("destination");
        const string reservedPath = @"nested\.winapp-layout-locks\state.lock";
        WriteFile(source, reservedPath, "bookkeeping");

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            Invoke("CopyManifestReferencedFiles", new HashSet<string> { reservedPath },
                source, source, destination, _taskContext, TestContext.CancellationToken));
        Assert.IsEmpty(destination.GetFileSystemInfos());
    }

    [TestMethod]
    public void ExplicitReservedSourceRoot_IsRejectedRatherThanRenamedIntoPayload()
    {
        var source = _root.CreateSubdirectory(LayoutLease.LockDirectoryName);
        WriteFile(source, "state.lock", "bookkeeping");
        var destination = new DirectoryInfo(Path.Combine(_root.FullName, "destination"));

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            Invoke("CopyDirectoryRecursive", source, destination, null));
        Assert.ThrowsExactly<ExecutionTargetException>(() =>
            HostSourceWalker.EnumerateFiles(source.FullName, HostReparsePolicy.Reject, TestContext.CancellationToken));
        Assert.IsFalse(Directory.Exists(destination.FullName));
    }

    [TestMethod]
    public void ExplicitReservedFile_CannotBypassTheDeploymentWalk()
    {
        var source = _root.CreateSubdirectory("source");
        var file = WriteFile(source, @".winapp-layout-locks\state.lock", "bookkeeping");

        var failure = Assert.ThrowsExactly<ExecutionTargetException>(() =>
            HostSourceWalker.EnsureNoLinkOnPath(source.FullName, file.FullName));

        Assert.AreEqual(ExecutionTargetErrorCodes.DeploymentDirty, failure.Error.Code);
    }

    private void Sync(DirectoryInfo source, DirectoryInfo layout, FileInfo manifest, LayoutReconciliation reconciliation) =>
        Invoke("SyncFilesToOutputDirectory", source, layout, manifest, _taskContext, reconciliation);

    private Task CopyRecipe(FileInfo recipe, DirectoryInfo layout, LayoutReconciliation reconciliation) =>
        (Task)Invoke("CopyFilesFromRecipeAsync", recipe, layout, _taskContext, reconciliation, TestContext.CancellationToken)!;

    private static object? Invoke(string name, params object?[] arguments)
    {
        try
        {
            return typeof(MsixService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private FileInfo WriteRecipe(params (string Source, string Destination)[] entries)
    {
        XNamespace ns = "http://schemas.microsoft.com/developer/msbuild/2003";
        var doc = new XDocument(new XElement(ns + "Project",
            new XElement(ns + "ItemGroup", entries.Select(entry =>
                new XElement(ns + "AppxPackagedFile",
                    new XAttribute("Include", entry.Source),
                    new XElement(ns + "PackagePath", entry.Destination))))));
        return WriteFile(_root, "app.build.appxrecipe", doc.ToString());
    }

    private static FileInfo WriteFile(DirectoryInfo root, string relativePath, string contents)
    {
        var path = Path.Combine(root.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return new FileInfo(path);
    }

    private static void WritePayload(DirectoryInfo root)
    {
        WriteFile(root, "app.exe", "app");
        WriteFile(root, "ordinary.lock", "payload with a lock extension");
        WriteFile(root, @".winapp\bindings\addon.node", "binding");
        WriteFile(root, @".winapp-layout-locks-backup\keep.txt", "similarly named payload");
    }

    private static void AssertPayloadOnly(DirectoryInfo layout)
    {
        Assert.AreEqual("app", File.ReadAllText(Path.Combine(layout.FullName, "app.exe")));
        Assert.AreEqual("payload with a lock extension", File.ReadAllText(Path.Combine(layout.FullName, "ordinary.lock")));
        Assert.AreEqual("binding", File.ReadAllText(Path.Combine(layout.FullName, @".winapp\bindings\addon.node")));
        Assert.AreEqual("similarly named payload",
            File.ReadAllText(Path.Combine(layout.FullName, @".winapp-layout-locks-backup\keep.txt")));
        Assert.IsFalse(layout.EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
            .Any(entry => LayoutLease.IsArtifactPath(entry.FullName)));
    }
}

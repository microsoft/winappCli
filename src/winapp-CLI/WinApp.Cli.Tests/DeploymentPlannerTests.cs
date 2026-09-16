// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="DeploymentPlanner"/> — desired-state capture, exact reconciliation, and the
/// path-safety rules that keep a deployment inside its own managed folder.
/// </summary>
[TestClass]
public class DeploymentPlannerTests
{
    // CA1861: hoisted so repeated assertions do not allocate a fresh array each call.
    private static readonly string[] ExpectedRemoved = [@"old\stale.dll"];

    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = new DirectoryInfo(TestPaths.TempRoot("Deploy"));
        _root.Create();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_root.Exists)
        {
            _root.Delete(recursive: true);
        }
    }

    private string Write(string relativePath, string contents)
    {
        var path = TestPaths.Under(_root.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    private Task<DeploymentSnapshot> SnapshotAsync() =>
        DeploymentPlanner.CreateSnapshotAsync(_root, "deployment-1", TestContext.CancellationTokenSource.Token);

    [TestMethod]
    public void CreateDeploymentId_IsStableAndPathCaseInsensitive()
    {
        var lower = DeploymentPlanner.CreateDeploymentId(@"c:\work\myapp", null);
        var upper = DeploymentPlanner.CreateDeploymentId(@"C:\WORK\MYAPP", null);

        // Windows paths are case-insensitive, so the same project must not produce two deployments.
        Assert.AreEqual(lower, upper);
        Assert.AreEqual(16, lower.Length);
    }

    [TestMethod]
    public void CreateDeploymentId_DistinguishesPathsAndIdentities()
    {
        var first = DeploymentPlanner.CreateDeploymentId(@"C:\work\a", "Contoso.App");
        var second = DeploymentPlanner.CreateDeploymentId(@"C:\work\b", "Contoso.App");
        var third = DeploymentPlanner.CreateDeploymentId(@"C:\work\a", "Contoso.Other");
        var withoutIdentity = DeploymentPlanner.CreateDeploymentId(@"C:\work\a", null);

        // Two projects sharing a package identity must still be distinct deployments.
        Assert.AreNotEqual(first, second);
        Assert.AreNotEqual(first, third);
        Assert.AreNotEqual(first, withoutIdentity);
    }

    [TestMethod]
    public async Task CreateSnapshot_CapturesEveryFileWithHashAndSize()
    {
        Write("app.exe", "binary");
        Write(@"sub\data.txt", "hello");

        var snapshot = await SnapshotAsync();

        Assert.AreEqual(2, snapshot.Files.Count);
        var data = snapshot.Files.Single(f => f.RelativePath.EndsWith("data.txt", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(@"sub\data.txt", data.RelativePath);
        Assert.AreEqual(5, data.Size);
        Assert.AreEqual(64, data.Sha256.Length, "SHA-256 renders as 64 hex characters.");
    }

    [TestMethod]
    public async Task CreateSnapshot_OrdersDeterministically()
    {
        Write("z.txt", "z");
        Write("a.txt", "a");
        Write(@"m\b.txt", "b");

        var first = await SnapshotAsync();
        var second = await SnapshotAsync();

        CollectionAssert.AreEqual(
            first.Files.Select(f => f.RelativePath).ToArray(),
            second.Files.Select(f => f.RelativePath).ToArray());
    }

    [TestMethod]
    public async Task CreatePlan_EmptyGuest_AddsEverything()
    {
        Write("app.exe", "binary");
        Write(@"sub\data.txt", "hello");
        var snapshot = await SnapshotAsync();

        var plan = DeploymentPlanner.CreatePlan(snapshot, []);

        Assert.AreEqual(2, plan.Added.Count);
        Assert.AreEqual(0, plan.Changed.Count);
        Assert.AreEqual(0, plan.Removed.Count);
        Assert.AreEqual(11, plan.TransferBytes);
    }

    [TestMethod]
    public async Task CreatePlan_IdenticalGuest_IsEmpty()
    {
        Write("app.exe", "binary");
        var snapshot = await SnapshotAsync();

        var plan = DeploymentPlanner.CreatePlan(snapshot, snapshot.Files);

        // A warm rerun with no changes must transfer nothing.
        Assert.IsTrue(plan.IsEmpty);
        Assert.AreEqual(0, plan.TransferBytes);
    }

    [TestMethod]
    public async Task CreatePlan_ChangedContent_IsDetectedByHashNotTimestamp()
    {
        Write("app.exe", "new content");
        var snapshot = await SnapshotAsync();

        // Same path, same length, same timestamp, different content: only the hash reveals it, and
        // build tools produce exactly this more often than one would like.
        var stale = snapshot.Files
            .Select(f => f with { Sha256 = new string('0', 64) })
            .ToList();

        var plan = DeploymentPlanner.CreatePlan(snapshot, stale);

        Assert.AreEqual(1, plan.Changed.Count);
        Assert.AreEqual(0, plan.Added.Count);
    }

    [TestMethod]
    public async Task CreatePlan_FileRemovedFromSource_IsDeletedFromGuest()
    {
        Write("app.exe", "binary");
        var snapshot = await SnapshotAsync();

        var actual = snapshot.Files
            .Append(new DeploymentFile(@"old\stale.dll", 10, DateTimeOffset.UnixEpoch, new string('a', 64)))
            .ToList();

        var plan = DeploymentPlanner.CreatePlan(snapshot, actual);

        // Leaving a stale binary behind is how a rerun silently keeps executing removed code.
        CollectionAssert.AreEqual(ExpectedRemoved, plan.Removed.ToArray());
    }

    [TestMethod]
    public async Task CreatePlan_PathComparisonIsCaseInsensitive()
    {
        Write("App.exe", "binary");
        var snapshot = await SnapshotAsync();

        var actual = snapshot.Files.Select(f => f with { RelativePath = f.RelativePath.ToUpperInvariant() }).ToList();

        var plan = DeploymentPlanner.CreatePlan(snapshot, actual);

        // Treating APP.EXE and App.exe as different would delete and re-copy the file every run.
        Assert.IsTrue(plan.IsEmpty);
    }

    [TestMethod]
    public async Task VerifyUnchanged_SourceRewrittenDuringCapture_AbortsAndAsksForARebuild()
    {
        var path = Write("app.exe", "original");
        var snapshot = await SnapshotAsync();

        // A concurrent build rewrote the output after it was hashed. Deploying that mixture would
        // produce a guest matching no build at all.
        File.WriteAllText(path, "rewritten to a different length");

        var failure = Assert.ThrowsExactly<ExecutionTargetException>(
            () => DeploymentPlanner.VerifyUnchanged(_root.FullName, snapshot.Files));

        Assert.AreEqual(ExecutionTargetErrorCodes.DeploymentDirty, failure.Error.Code);
        StringAssert.Contains(failure.Error.UserAction!, "Rebuild", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task VerifyUnchanged_SourceDeletedDuringCapture_Aborts()
    {
        var path = Write("app.exe", "original");
        var snapshot = await SnapshotAsync();

        File.Delete(path);

        var failure = Assert.ThrowsExactly<ExecutionTargetException>(
            () => DeploymentPlanner.VerifyUnchanged(_root.FullName, snapshot.Files));

        Assert.AreEqual(ExecutionTargetErrorCodes.DeploymentDirty, failure.Error.Code);
    }

    [TestMethod]
    public async Task VerifyUnchanged_StableSource_Passes()
    {
        Write("app.exe", "original");
        var snapshot = await SnapshotAsync();

        DeploymentPlanner.VerifyUnchanged(_root.FullName, snapshot.Files);
    }

    [TestMethod]
    public void ResolveContainedPath_KeepsRelativePathsInsideTheRoot()
    {
        var resolved = DeploymentPlanner.ResolveContainedPath(@"C:\guest\deploy", @"sub\app.exe");

        Assert.AreEqual(@"C:\guest\deploy\sub\app.exe", resolved);
    }

    [TestMethod]
    public void ResolveContainedPath_RejectsTraversal()
    {
        // Silently normalizing an escape attempt into something inside the root would hide an
        // attack, so it is rejected instead.
        var failure = Assert.ThrowsExactly<ExecutionTargetException>(
            () => DeploymentPlanner.ResolveContainedPath(@"C:\guest\deploy", @"..\..\Windows\System32\evil.dll"));

        Assert.AreEqual(ExecutionTargetErrorCodes.DeploymentDirty, failure.Error.Code);
    }

    [TestMethod]
    public void ResolveContainedPath_RejectsRootedAndEmptyPaths()
    {
        foreach (var candidate in new[] { @"C:\Windows\System32\evil.dll", @"\absolute\path", "", "   " })
        {
            Assert.ThrowsExactly<ExecutionTargetException>(
                () => DeploymentPlanner.ResolveContainedPath(@"C:\guest\deploy", candidate),
                $"'{candidate}' should not resolve inside the deployment root.");
        }
    }

    [TestMethod]
    public void ResolveContainedPath_RejectsSiblingWithSharedPrefix()
    {
        // C:\guest\deploy-2 must not count as inside C:\guest\deploy.
        Assert.ThrowsExactly<ExecutionTargetException>(
            () => DeploymentPlanner.ResolveContainedPath(@"C:\guest\deploy", @"..\deploy-2\app.exe"));
    }

    [TestMethod]
    public void ResolveContainedPath_AllowsTraversalThatStaysInside()
    {
        var resolved = DeploymentPlanner.ResolveContainedPath(@"C:\guest\deploy", @"sub\..\app.exe");

        Assert.AreEqual(@"C:\guest\deploy\app.exe", resolved);
    }

    /// <summary>MSTest injects this; used for per-test cancellation.</summary>
    public TestContext TestContext { get; set; } = null!;
}

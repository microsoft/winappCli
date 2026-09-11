// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="TargetPathSafety"/>, the single fail-closed rule every managed path in the
/// execution-target code is built through.
/// </summary>
/// <remarks>
/// Centralising this matters because <see cref="Path.Combine"/> silently discards everything before
/// a rooted segment, and <see cref="Path.Join"/> — while it avoids that — validates nothing at all.
/// Neither is sufficient alone, so these tests pin the combined behaviour.
/// </remarks>
[TestClass]
public class TargetPathSafetyTests
{
    private const string Root = @"C:\guest\managed";

    [TestMethod]
    public void EnsureSafeSegment_AcceptsPlainNames()
    {
        Assert.AreEqual("target-state.json", TargetPathSafety.EnsureSafeSegment("target-state.json"));
        Assert.AreEqual("windows-sandbox-default", TargetPathSafety.EnsureSafeSegment("windows-sandbox-default"));
    }

    [TestMethod]
    public void EnsureSafeSegment_RejectsEverythingThatIsNotAPlainName()
    {
        var rejected = new[]
        {
            null,
            string.Empty,
            "   ",
            ".",
            "..",
            @"C:\Windows",
            @"\rooted",
            "/rooted",
            @"sub\file",
            "sub/file",
            "C:relative",
            "bad|name",
            "bad\0name",
        };

        foreach (var candidate in rejected)
        {
            Assert.ThrowsExactly<ExecutionTargetException>(
                () => TargetPathSafety.EnsureSafeSegment(candidate),
                $"'{candidate ?? "<null>"}' must be rejected as a path segment.");
        }
    }

    [TestMethod]
    public void CombineInsideRoot_BuildsNestedPaths()
    {
        var combined = TargetPathSafety.CombineInsideRoot(Root, "Microsoft", "WinApp", "Targets");

        Assert.AreEqual(@"C:\guest\managed\Microsoft\WinApp\Targets", combined);
    }

    [TestMethod]
    public void CombineInsideRoot_RootedSegmentCannotReplaceTheRoot()
    {
        // The exact Path.Combine hazard: Combine(root, @"C:\Windows") returns C:\Windows, silently
        // discarding the managed root.
        var failure = Assert.ThrowsExactly<ExecutionTargetException>(
            () => TargetPathSafety.CombineInsideRoot(Root, @"C:\Windows\System32"));

        Assert.AreEqual(ExecutionTargetErrorCodes.TargetAmbiguous, failure.Error.Code);
    }

    [TestMethod]
    public void CombineInsideRoot_TraversalSegmentIsRejected()
    {
        Assert.ThrowsExactly<ExecutionTargetException>(
            () => TargetPathSafety.CombineInsideRoot(Root, "..", "..", "Windows"));
    }

    [TestMethod]
    public void CombineInsideRoot_SeparatorInsideASegmentIsRejected()
    {
        // Path.Join would happily accept this; validation is what stops it.
        Assert.ThrowsExactly<ExecutionTargetException>(
            () => TargetPathSafety.CombineInsideRoot(Root, @"..\..\Windows"));
    }

    [TestMethod]
    public void IsInsideRoot_RejectsSiblingSharingANamePrefix()
    {
        Assert.IsTrue(TargetPathSafety.IsInsideRoot(@"C:\work", @"C:\work"));
        Assert.IsTrue(TargetPathSafety.IsInsideRoot(@"C:\work", @"C:\work\sub\file.txt"));

        // C:\work-2 must not count as inside C:\work.
        Assert.IsFalse(TargetPathSafety.IsInsideRoot(@"C:\work", @"C:\work-2\file.txt"));
        Assert.IsFalse(TargetPathSafety.IsInsideRoot(@"C:\work", @"C:\other"));
    }

    [TestMethod]
    public void IsInsideRoot_IsCaseInsensitive()
    {
        Assert.IsTrue(TargetPathSafety.IsInsideRoot(@"C:\Work", @"c:\work\file.txt"));
    }

    [TestMethod]
    public void StateAndLockFileNames_AreValidSegments()
    {
        // These constants flow into CombineInsideRoot, so a change that made one unsafe should fail
        // here rather than at runtime.
        TargetPathSafety.EnsureSafeSegment(TargetStateStore.StateFileName);
        TargetPathSafety.EnsureSafeSegment(TargetMutationLock.LockFileName);
        TargetPathSafety.EnsureSafeSegment(WindowsSandboxTarget.Default.StateKey);
    }
}

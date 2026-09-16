// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

/// <summary>
/// Two targets must never share a state root or a lock, whatever they are called.
/// </summary>
/// <remarks>
/// The state key separates one target's ownership record, mutation lock, and connection lock from
/// another's. Sanitising a name into a filesystem-safe string cannot carry that on its own: two
/// different names can sanitise to the same thing, and then two targets would fence each other's
/// epochs and adopt each other's instances. These tests pin the cases where a sanitiser alone
/// would collide.
/// </remarks>
[TestClass]
public class ExecutionTargetIdentityTests
{
    [TestMethod]
    public void SameKindDifferentId_AreDistinct()
    {
        Assert.AreNotEqual(
            new ExecutionTargetRef("hyperv", "alpha").StateKey,
            new ExecutionTargetRef("hyperv", "beta").StateKey);
    }

    [TestMethod]
    public void SameIdDifferentKind_AreDistinct()
    {
        // 'local/default' and 'sandbox/default' are the pair that exists today, and the reserved
        // 'desktop/default' is the one that will exist next.
        var keys = (string[])
        [
            ExecutionTargetRef.Local.StateKey,
            WindowsSandboxTarget.Default.StateKey,
            new ExecutionTargetRef("desktop", ExecutionTargetRef.DefaultId).StateKey,
        ];

        Assert.AreEqual(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The case a sanitiser gets wrong. Neither name has an ASCII letter in it, so the readable half
    /// of both keys is identical; only the hash tells them apart.
    /// </summary>
    [TestMethod]
    public void IdsThatSanitiseToTheSameText_AreStillDistinct()
    {
        Assert.AreNotEqual(
            new ExecutionTargetRef("hyperv", "α").StateKey,
            new ExecutionTargetRef("hyperv", "β").StateKey);

        Assert.AreNotEqual(
            new ExecutionTargetRef("hyperv", "a b").StateKey,
            new ExecutionTargetRef("hyperv", "a-b").StateKey);
    }

    /// <summary>
    /// A provider whose identities are case-sensitive — a desktop name, a VM name — must not have
    /// two of its targets folded into one.
    /// </summary>
    [TestMethod]
    public void IdsThatDifferOnlyByCase_AreDistinct()
    {
        Assert.AreNotEqual(
            new ExecutionTargetRef("hyperv", "Build").StateKey,
            new ExecutionTargetRef("hyperv", "build").StateKey);
    }

    /// <summary>
    /// Splitting the pair at a delimiter would let two different pairs produce one key.
    /// </summary>
    [TestMethod]
    public void PairsThatConcatenateIdentically_AreDistinct()
    {
        Assert.AreNotEqual(
            new ExecutionTargetRef("hyperv", "a-b").StateKey,
            new ExecutionTargetRef("hyperv-a", "b").StateKey);
    }

    [TestMethod]
    public void KindMatchingIsCaseInsensitiveAndNormalised()
    {
        var upper = new ExecutionTargetRef("SANDBOX", ExecutionTargetRef.DefaultId);

        Assert.AreEqual(ExecutionTargetRef.SandboxKind, upper.Kind);
        Assert.AreEqual(WindowsSandboxTarget.Default.StateKey, upper.StateKey);
    }

    /// <summary>
    /// The key is used directly as a directory name and as a kernel object name, so it has to be a
    /// plain safe segment however hostile the ID was.
    /// </summary>
    [TestMethod]
    [DataRow("..")]
    [DataRow(@"..\..\Windows")]
    [DataRow("C:/Windows/System32")]
    [DataRow(@"\\server\share")]
    [DataRow("con")]
    [DataRow("a:b*c?d")]
    [DataRow("α β γ")]
    public void HostileIdsStillProduceASafePathSegment(string id)
    {
        var key = new ExecutionTargetRef("hyperv", id).StateKey;

        Assert.AreEqual(key, TargetPathSafety.EnsureSafeSegment(key));
        Assert.DoesNotContain("..", key);
    }

    /// <summary>
    /// A long ID must not produce a key that pushes the state path or the lock name past what
    /// Windows accepts.
    /// </summary>
    [TestMethod]
    public void VeryLongIdsAreBounded()
    {
        var key = new ExecutionTargetRef("hyperv", new string('x', 4096)).StateKey;

        Assert.IsLessThan(96, key.Length);
        Assert.AreEqual(key, TargetPathSafety.EnsureSafeSegment(key));
    }

    /// <summary>
    /// A human looking at the state folder should be able to tell which target it belongs to
    /// without decoding anything.
    /// </summary>
    [TestMethod]
    public void KeyStaysReadable()
    {
        StringAssert.StartsWith(WindowsSandboxTarget.Default.StateKey, "sandbox-default-");
        StringAssert.StartsWith(ExecutionTargetRef.Local.StateKey, "local-default-");
    }

    /// <summary>The key is a pure function of the pair, so it is stable across processes.</summary>
    [TestMethod]
    public void KeyIsStableForTheSamePair()
    {
        Assert.AreEqual(
            new ExecutionTargetRef("sandbox", "default").StateKey,
            new ExecutionTargetRef("sandbox", "default").StateKey);
    }

    [TestMethod]
    public void SelectorEchoesWhatAUserWouldType()
    {
        Assert.AreEqual("sandbox", WindowsSandboxTarget.Default.Selector);
        Assert.AreEqual("hyperv:WinAppTest", new ExecutionTargetRef("hyperv", "WinAppTest").Selector);
    }

    [TestMethod]
    public void MatchesComparesKindLooselyAndIdExactly()
    {
        var target = new ExecutionTargetRef("hyperv", "Build");

        Assert.IsTrue(target.Matches("HYPERV", "Build"));
        Assert.IsFalse(target.Matches("hyperv", "build"));
        Assert.IsFalse(target.Matches("desktop", "Build"));
        Assert.IsFalse(target.Matches(null, "Build"));
    }

    /// <summary>
    /// A scope travels with any identifier that is only meaningful inside one target incarnation,
    /// and offers the selector rather than a bare number.
    /// </summary>
    [TestMethod]
    public void ScopeCarriesTheSelectorNotABareIdentifier()
    {
        var scope = ExecutionTargetScope.For(
            WindowsSandboxTarget.Default, ExecutionTargetEpoch.Create("sandbox-1", "nonce"));

        Assert.AreEqual("sandbox", scope.Kind);
        Assert.AreEqual("default", scope.Id);
        Assert.AreEqual("sandbox-1:nonce", scope.Epoch);
        Assert.AreEqual("sandbox", scope.SelectorHint);

        Assert.IsEmpty(ExecutionTargetScope.ForLocal().SelectorHint);
        Assert.IsNull(ExecutionTargetScope.ForLocal().Epoch);
    }
}

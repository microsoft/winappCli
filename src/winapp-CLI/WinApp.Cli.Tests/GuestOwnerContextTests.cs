// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for Cooperative UI Turns owner-context forwarding
/// (spec §"Owner-context forwarding", acceptance criterion 14).
/// </summary>
/// <remarks>
/// The property under test is not "a token is produced" but that the <em>grouping</em> survives the
/// hop into the guest. Every guest child shares one agent parent, so without this the guest would
/// see a single owner and commands that must queue against each other would instead be treated as
/// cooperating — silently allowing two workflows to drive the same desktop at once.
/// </remarks>
[TestClass]
public class GuestOwnerContextTests
{
    private const string TargetId = "sandbox-default-6b0d287c0c51bc40";
    private const string Epoch = "sandbox-1:nonce-a";

    private static Dictionary<string, string?> WithOwnerVariable(string? value) =>
        new() { [GuestOwnerContext.OwnerVariable] = value };

    private static Dictionary<string, string?> NoOwnerVariable() => [];

    [TestMethod]
    public void ExplicitOwner_TakesPrecedence()
    {
        Assert.AreEqual("workflow-7", GuestOwnerContext.ResolveHostOwner(WithOwnerVariable("workflow-7")));
    }

    [TestMethod]
    public void ExplicitOwner_IsPreservedExactly()
    {
        Assert.AreEqual("workflow-7", GuestOwnerContext.ResolveHostOwner(WithOwnerVariable("workflow-7")));

        // Not trimmed. Whitespace is significant to the guest's own resolver, so normalizing it here
        // would make two values that are distinct locally into one workflow in the guest -- the
        // exact divergence forwarding exists to prevent.
        Assert.AreEqual("  workflow-7  ", GuestOwnerContext.ResolveHostOwner(WithOwnerVariable("  workflow-7  ")));
    }

    [TestMethod]
    public void OversizedExplicitOwner_IsRefusedRatherThanTruncated()
    {
        var oversized = new string('x', GuestOwnerContext.MaximumOwnerLength + 50);

        // Truncating would silently merge two distinct long owners into one workflow, so an
        // oversized value is refused instead.
        Assert.ThrowsExactly<WinApp.Cli.ExecutionTargets.Abstractions.ExecutionTargetException>(
            () => GuestOwnerContext.ResolveHostOwner(WithOwnerVariable(oversized)));
    }

    [TestMethod]
    public void NoExplicitOwner_FallsBackToParentOrAnonymous()
    {
        var resolved = GuestOwnerContext.ResolveHostOwner(WithOwnerVariable(null));

        Assert.IsFalse(string.IsNullOrWhiteSpace(resolved));

        // Either shape is correct depending on whether this process's parent is still observable;
        // what matters is that neither is empty and neither is the raw variable.
        Assert.IsTrue(
            resolved.StartsWith("parent:", StringComparison.Ordinal) ||
            resolved.StartsWith("anonymous:", StringComparison.Ordinal),
            $"Unexpected fallback owner shape: {resolved}");
    }

    [TestMethod]
    public void Anonymous_OwnersAreUniquePerInvocation()
    {
        var blank = NoOwnerVariable();

        // Two invocations with no owner must not accidentally cooperate. Resolution can only be
        // compared when the parent fallback is unavailable, so compare derived tokens instead: for
        // a parent-derived owner they are equal by design, which is also correct.
        var first = GuestOwnerContext.ResolveHostOwner(blank);
        var second = GuestOwnerContext.ResolveHostOwner(blank);

        if (first.StartsWith("anonymous:", StringComparison.Ordinal))
        {
            Assert.AreNotEqual(first, second);
        }
        else
        {
            Assert.AreEqual(first, second, "A parent-derived owner must be stable across invocations.");
        }
    }

    [TestMethod]
    public void Token_IsOpaqueAndNeverContainsTheRawOwner()
    {
        const string Secret = "corp-workflow-secret-id";
        var token = GuestOwnerContext.DeriveGuestToken(Secret, TargetId, Epoch);

        // The raw explicit owner must never reach state, output, logs, protocol events, or
        // telemetry — and the token is the only thing that ever leaves the host.
        StringAssert.StartsWith(token, "gt1_");
        Assert.IsFalse(token.Contains(Secret, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Token_SameOwnerCooperates()
    {
        // Two host commands that would cooperate locally must cooperate in the guest.
        Assert.AreEqual(
            GuestOwnerContext.DeriveGuestToken("workflow-a", TargetId, Epoch),
            GuestOwnerContext.DeriveGuestToken("workflow-a", TargetId, Epoch));
    }

    [TestMethod]
    public void Token_DifferentOwnersStaySeparate()
    {
        // Two host workflows with different owners must remain different guest owners even though
        // their child commands share the same guest agent parent.
        Assert.AreNotEqual(
            GuestOwnerContext.DeriveGuestToken("workflow-a", TargetId, Epoch),
            GuestOwnerContext.DeriveGuestToken("workflow-b", TargetId, Epoch));
    }

    [TestMethod]
    public void Token_IsScopedToTargetAndEpoch()
    {
        var baseline = GuestOwnerContext.DeriveGuestToken("workflow-a", TargetId, Epoch);

        // A recreated environment must not inherit grouping from the previous one.
        Assert.AreNotEqual(baseline, GuestOwnerContext.DeriveGuestToken("workflow-a", TargetId, "sandbox-1:nonce-b"));
        Assert.AreNotEqual(baseline, GuestOwnerContext.DeriveGuestToken("workflow-a", "hyperv:other", Epoch));
    }

    [TestMethod]
    public void Token_FieldsCannotBeRearranged()
    {
        // Without unambiguous separation, ("ab", "c") and ("a", "bc") would hash identically and two
        // unrelated workflows could collide into one guest owner.
        Assert.AreNotEqual(
            GuestOwnerContext.DeriveGuestToken("ab", "c", Epoch),
            GuestOwnerContext.DeriveGuestToken("a", "bc", Epoch));
    }

    [TestMethod]
    public void WithOwner_SetsTheVariableTheGuestAlreadyReads()
    {
        var token = GuestOwnerContext.DeriveGuestToken("workflow-a", TargetId, Epoch);
        var environment = GuestOwnerContext.WithOwner(
            new Dictionary<string, string> { ["EXISTING"] = "kept" },
            token);

        // The guest agent sets the ordinary variable, so guest-side owner resolution and scheduling
        // stay completely unchanged.
        Assert.AreEqual(token, environment[GuestOwnerContext.OwnerVariable]);
        Assert.AreEqual("kept", environment["EXISTING"]);
    }

    [TestMethod]
    public void WithOwner_AcceptsNoExistingEnvironment()
    {
        var environment = GuestOwnerContext.WithOwner(environment: null, "gt1_abc");

        Assert.AreEqual(1, environment.Count);
        Assert.AreEqual("gt1_abc", environment[GuestOwnerContext.OwnerVariable]);
    }
}

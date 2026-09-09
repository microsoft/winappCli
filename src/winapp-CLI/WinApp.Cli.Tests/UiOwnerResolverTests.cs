// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Tests;

/// <summary>
/// How a workflow id becomes an owner key (<see cref="UiOwnerResolver"/>).
/// </summary>
/// <remarks>
/// The key is the only thing that decides whether two commands share the desktop, so two different
/// ids producing one key is a correctness failure, not a hashing nicety: the workflows would take
/// each other's turns and interleave on the same desktop while each believed it was alone.
/// </remarks>
[TestClass]
[DoNotParallelize] // WINAPP_UI_WORKFLOW_ID is process-wide, so these must not race other classes.
public class UiOwnerResolverTests : IDisposable
{
    private string? _previousWorkflowId;

    [TestInitialize]
    public void Setup()
        => _previousWorkflowId = Environment.GetEnvironmentVariable(UiOwnerResolver.WorkflowIdVariable);

    [TestCleanup]
    public void Cleanup()
        => Environment.SetEnvironmentVariable(UiOwnerResolver.WorkflowIdVariable, _previousWorkflowId);

    public void Dispose() => GC.SuppressFinalize(this);

    private static UiOwnerIdentity ResolveWith(string? workflowId)
    {
        Environment.SetEnvironmentVariable(UiOwnerResolver.WorkflowIdVariable, workflowId);
        return new UiOwnerResolver().Resolve();
    }

    // ------------------------------------------------------------------ ill-formed UTF-16 is not text

    /// <remarks>
    /// Built from char values in code rather than passed as <c>[DataRow]</c> constants: attribute
    /// arguments are stored as UTF-8 in assembly metadata, so a lone surrogate written there is
    /// substituted before the test ever runs and the case silently tests nothing.
    /// </remarks>
    private static IEnumerable<(string Value, string Description)> IllFormedWorkflowIds()
    {
        const char high = '\ud800';
        const char low = '\udc00';
        yield return (high.ToString(), "lone high surrogate");
        yield return (low.ToString(), "lone low surrogate");
        yield return ("wf-" + '\ud801' + "-tail", "lone high surrogate inside a longer id");
        yield return ("lead" + '\udfff', "lone low surrogate at the end");
    }

    [TestMethod]
    public void AnUnpairedSurrogateIsRefusedInsteadOfBeingSubstituted()
    {
        // Encoding these the ordinary way substitutes U+FFFD, which is why they must be refused: the
        // substitution is lossy in exactly the direction that matters, mapping distinct ids onto one
        // owner rather than onto distinct owners.
        foreach (var (value, description) in IllFormedWorkflowIds())
        {
            var ex = Assert.ThrowsExactly<UiCoordinationException>(
                () => ResolveWith(value), $"{description} must be refused");

            Assert.AreEqual(UiCoordinationErrorCodes.InvalidWorkflowId, ex.Code, description);
            StringAssert.Contains(ex.Message, "surrogate", description);
        }
    }

    [TestMethod]
    public void DistinctIllFormedIdsWouldOtherwiseCollideOnOneKey()
    {
        // The specific failure the refusal prevents. Under replacement encoding all three of these
        // become the same bytes, so all three become the same owner — two unrelated workflows and one
        // caller who legitimately used U+FFFD, sharing one turn on one desktop.
        Assert.ThrowsExactly<UiCoordinationException>(() => ResolveWith('\ud800'.ToString()));
        Assert.ThrowsExactly<UiCoordinationException>(() => ResolveWith('\ud801'.ToString()));

        // U+FFFD itself is an ordinary character and stays valid, so the two above cannot reach it.
        var replacementChar = ResolveWith("\ufffd");
        Assert.AreEqual(UiOwnerKind.Workflow, replacementChar.Kind);
    }

    // ------------------------------------------------------------------------- well-formed stays valid

    [TestMethod]
    public void WellFormedIdsIncludingSurrogatePairsResolveToDistinctWorkflowOwners()
    {
        string[] ids =
        [
            "plain-workflow",
            "550e8400-e29b-41d4-a716-446655440000",
            "\ud83d\ude80",     // one astral character: a PAIR, not a lone surrogate
            "\ufffd",
        ];

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var owner = ResolveWith(id);
            Assert.AreEqual(UiOwnerKind.Workflow, owner.Kind, $"'{id}' must resolve to a workflow owner");
            Assert.IsTrue(keys.Add(owner.Key), $"'{id}' must not collide with another id");
        }
    }

    [TestMethod]
    public void TheKeyForAnOrdinaryIdIsUnchanged()
    {
        // Golden values: the strict encoding must not alter the key for text that was always valid, or
        // every running workflow would be re-homed on upgrade. These are SHA-256 of
        // "winapp-ui-workflow-v1\0" + the id, computed independently of this code.
        //
        // Computed directly rather than through the environment variable, which is process-wide and
        // therefore the one input a parallel test could change underneath this assertion.
        Assert.AreEqual(
            "af0babd78c14cae807477f0a3085bfd1ad91a9b37d89733766dbf32af0dcc328",
            UiOwnerResolver.ComputeWorkflowKey("550e8400-e29b-41d4-a716-446655440000"));

        Assert.AreEqual(
            "99c43e974bc265169b8fac2a24c905b019dfa8e1aea94eeb47dafb3e9d24d929",
            UiOwnerResolver.ComputeWorkflowKey("plain-workflow"));
    }
}

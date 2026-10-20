// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
public sealed class UiAuditSnapshotTraversalTests
{
    private static readonly string[] ThreeNodeIds = ["n1", "n2", "n3"];

    [TestMethod]
    public void DefaultsAndInclusiveMaximumOptionsAreBounded()
    {
        var defaults = new UiAuditSnapshotOptions();
        Assert.AreEqual(20, defaults.MaxDepth);
        Assert.AreEqual(2000, defaults.MaxElements);
        Assert.AreEqual(16, defaults.MaxTextRunsPerNode);
        Assert.AreEqual(5000, defaults.TimeoutMs);
        NativeUiAuditSnapshotService.ValidateOptions(defaults);
        NativeUiAuditSnapshotService.ValidateOptions(new() { MaxDepth = 1, MaxElements = 1, MaxTextRunsPerNode = 1, TimeoutMs = 1 });
        NativeUiAuditSnapshotService.ValidateOptions(new() { MaxDepth = 50, MaxElements = 10000, MaxTextRunsPerNode = 32, TimeoutMs = 120000 });
    }

    [TestMethod]
    public void InvalidOptionsAreRejectedBeforeNativeWork()
    {
        var defaults = new UiAuditSnapshotOptions();
        foreach (var options in new[]
        {
            defaults with { MaxDepth = 0 }, defaults with { MaxDepth = 51 },
            defaults with { MaxElements = 0 }, defaults with { MaxElements = 10001 },
            defaults with { MaxTextRunsPerNode = 0 }, defaults with { MaxTextRunsPerNode = 33 },
            defaults with { TimeoutMs = 0 }, defaults with { TimeoutMs = 120001 },
        })
        {
            var error = Assert.ThrowsExactly<UiAuditSnapshotException>(() => NativeUiAuditSnapshotService.ValidateOptions(options));
            Assert.AreEqual("invalid_arguments", error.Code);
        }
    }

    [TestMethod]
    [DataRow(0L, 0)]
    [DataRow(42L, 0)]
    [DataRow(0L, 123)]
    [DataRow(-1L, 123)]
    [DataRow(42L, -1)]
    public void EmptyTargetsNeverFallBackToProcessOrForeground(long hwnd, int pid)
    {
        var service = new NativeUiAuditSnapshotService();
        var target = new UiTarget { WindowHandle = hwnd, ProcessId = pid, ProcessName = "DoNotSearchForMe" };
        var error = Assert.ThrowsExactly<UiAuditSnapshotException>(() => service.CaptureAsync(target, new()));

        Assert.AreEqual("invalid_target", error.Code);
    }

    [TestMethod]
    public void MissingIdentifiersDoNotPreventScanLocalIdentity()
    {
        var root = Node(Node(), Node());
        var (state, _) = Capture(root);

        Assert.AreEqual(3, state.Nodes.Count);
        CollectionAssert.AreEqual(ThreeNodeIds, state.Nodes.Select(node => node.Id).ToArray());
        Assert.IsNull(state.Nodes[0].ParentId);
        Assert.AreEqual("n1", state.Nodes[1].ParentId);
        Assert.AreEqual("n1", state.Nodes[2].ParentId);
        Assert.IsTrue(state.Nodes.All(node => node.RuntimeId is null && node.AutomationId is null));
        Assert.IsFalse(state.Partial);
    }

    [TestMethod]
    public void DepthLimitIsReportedOnlyWhenAChildWasOmitted()
    {
        var (state, tree) = Capture(Node(Node(Node())), new() { MaxDepth = 1 });

        Assert.AreEqual(2, state.Nodes.Count);
        Assert.AreEqual(0, state.Nodes[0].Depth);
        Assert.AreEqual(1, state.Nodes[1].Depth);
        Assert.IsTrue(state.DepthLimitReached);
        Assert.IsFalse(state.ElementLimitReached);
        Assert.IsTrue(state.Partial);
        Assert.IsTrue(state.Nodes[1].Issues.Any(issue => issue.Code == "depth_limit_reached"));
        Assert.AreEqual(2, tree.Releases);
    }

    [TestMethod]
    public void ExactDepthAndElementBoundsDoNotInventTruncation()
    {
        var (state, _) = Capture(Node(Node()), new() { MaxDepth = 1, MaxElements = 2 });

        Assert.AreEqual(2, state.Nodes.Count);
        Assert.IsFalse(state.DepthLimitReached);
        Assert.IsFalse(state.ElementLimitReached);
        Assert.IsFalse(state.Partial);
    }

    [TestMethod]
    public void ElementLimitBoundsBreadthAndPreservesParentLinks()
    {
        var (state, tree) = Capture(Node(Node(), Node(), Node()), new() { MaxElements = 2 });

        Assert.AreEqual(2, state.Nodes.Count);
        Assert.AreEqual(2, tree.Reads);
        Assert.AreEqual("n1", state.Nodes[1].ParentId);
        Assert.IsTrue(state.ElementLimitReached);
        Assert.IsTrue(state.Partial);
        Assert.IsTrue(state.Nodes[0].Issues.Any(issue => issue.Code == "element_limit_reached"));
    }

    [TestMethod]
    public void RejectedNodesStillConsumeTheExaminationBudget()
    {
        var root = Node(Node(), Node(), Node(), Node());
        foreach (var child in root.Children) { child.Reject = true; }
        var (state, tree) = Capture(root, new() { MaxElements = 3 });

        Assert.AreEqual(1, state.Nodes.Count);
        Assert.AreEqual(3, tree.Reads);
        Assert.IsTrue(state.ElementLimitReached);
        Assert.IsTrue(state.Partial);
    }

    [TestMethod]
    public void RepeatedRuntimeIdsAreExplicitRatherThanDuplicated()
    {
        var root = Node(Node());
        root.RuntimeId = root.Children[0].RuntimeId = "[2A,4]";
        var (state, _) = Capture(root);

        Assert.AreEqual(1, state.Nodes.Count);
        Assert.IsTrue(state.Issues.Any(issue => issue.Code == "repeated_runtime_id"));
        Assert.IsTrue(state.Partial);
    }

    [TestMethod]
    public void ATreeCycleWithoutRuntimeIdsIsStillBounded()
    {
        var root = Node();
        root.Children.Add(root);
        root.Parent = root;
        var (state, tree) = Capture(root, new() { MaxElements = 3 });

        Assert.AreEqual(3, state.Nodes.Count);
        Assert.AreEqual(3, tree.Reads);
        Assert.IsTrue(state.ElementLimitReached);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(null)]
    public void PasswordOrUnknownAncestorWithholdsDescendantNames(bool? password)
    {
        var root = Node(Node());
        root.Password = password;
        var (state, _) = Capture(root);

        Assert.IsTrue(state.Nodes.All(node => node.Name is null));
        Assert.AreEqual((bool?)false, state.Nodes[1].IsPassword, "The child remains known false, while inherited withholding stays in force.");
    }

    [TestMethod]
    public void OnlyKnownNonPasswordPrivacyAllowsNames()
    {
        Assert.IsFalse(UiAuditSnapshotTraversal.ShouldWithhold(false, false));
        Assert.IsTrue(UiAuditSnapshotTraversal.ShouldWithhold(false, true));
        Assert.IsTrue(UiAuditSnapshotTraversal.ShouldWithhold(false, null));
        Assert.IsTrue(UiAuditSnapshotTraversal.ShouldWithhold(true, false));
        var (state, _) = Capture(Node());
        Assert.AreEqual("Accessible name", state.Nodes[0].Name);
    }

    [TestMethod]
    public void NativeWindowAnchorUsesTheChildHwndWithoutReplacingItWithAnOwner()
    {
        var child = Node();
        child.NativeWindow = 42;
        var nearest = UiAuditSnapshotTraversal.NearestNativeWindow(child, 2, element => element.NativeWindow,
            _ => throw new AssertFailedException("An existing native HWND must be checked with GA_ROOT, not replaced by a parent or owner."),
            () => { });

        Assert.AreEqual((long?)42, nearest);
    }

    [TestMethod]
    public void NativeWindowAnchorStopsAtTheNearestNonzeroAncestor()
    {
        var main = Node(Node(Node()));
        main.NativeWindow = 10;
        main.Children[0].NativeWindow = 20;
        var virtualChild = main.Children[0].Children[0];
        var parentsRead = 0;
        var nearest = UiAuditSnapshotTraversal.NearestNativeWindow(virtualChild, 2, element => element.NativeWindow,
            element =>
            {
                parentsRead++;
                return element.Parent;
            },
            () => { });

        Assert.AreEqual((long?)20, nearest, "A virtual node must use its nearest native ancestor, not continue past an owned dialog to the selected main HWND.");
        Assert.AreEqual(1, parentsRead);
    }

    [TestMethod]
    public void NativeWindowAnchorIsBoundedAndCannotGuessPastMissingHandles()
    {
        var root = Node(Node(Node()));
        root.NativeWindow = 10;
        var parentsRead = 0;
        var nearest = UiAuditSnapshotTraversal.NearestNativeWindow(root.Children[0].Children[0], 1,
            element => element.NativeWindow,
            element =>
            {
                parentsRead++;
                return element.Parent;
            },
            () => { });

        Assert.IsNull(nearest);
        Assert.AreEqual(1, parentsRead);
    }

    [TestMethod]
    public void NativeWindowAnchorChecksCancellationBeforeProviderCalls()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var options = new UiAuditSnapshotOptions();
        var budget = new UiAuditSnapshotBudget(options, cancelled.Token, () => TimeSpan.Zero);

        var error = Assert.ThrowsExactly<UiAuditSnapshotStoppedException>(() =>
            UiAuditSnapshotTraversal.NearestNativeWindow(Node(), 2,
                _ => throw new AssertFailedException("No provider property may be read after cancellation."),
                _ => throw new AssertFailedException("No provider parent may be read after cancellation."),
                budget.Check));

        Assert.AreEqual("cancelled", error.Code);
    }

    [TestMethod]
    public void CancellationStopsBeforeAnotherProviderOperationAndKeepsCompletedNodes()
    {
        using var cancelled = new CancellationTokenSource();
        var options = new UiAuditSnapshotOptions();
        var state = new UiAuditSnapshotState(options, new(options, cancelled.Token, () => TimeSpan.Zero));
        var tree = new Tree { OnRead = () => cancelled.Cancel() };

        var stop = Assert.ThrowsExactly<UiAuditSnapshotStoppedException>(() => UiAuditSnapshotTraversal.Walk(Node(Node()), tree, state));

        Assert.AreEqual("cancelled", stop.Code);
        Assert.AreEqual(1, state.Nodes.Count);
        Assert.AreEqual(1, tree.Reads);
        Assert.AreEqual(0, tree.NavigationCalls);
    }

    [TestMethod]
    public void DeadlineIsInclusiveAndDoesNotDependOnWallClockOrSleep()
    {
        var options = new UiAuditSnapshotOptions { TimeoutMs = 5 };
        var elapsed = TimeSpan.FromMilliseconds(4);
        var budget = new UiAuditSnapshotBudget(options, CancellationToken.None, () => elapsed);
        budget.Check();
        elapsed = TimeSpan.FromMilliseconds(5);

        var stop = Assert.ThrowsExactly<UiAuditSnapshotStoppedException>(budget.Check);
        Assert.AreEqual("deadline_reached", stop.Code);
    }

    [TestMethod]
    public void BoundsPreserveZeroAndNegativeScreenCoordinatesButRejectInvalidGeometry()
    {
        Assert.AreEqual(new UiAuditNodeBounds(-20, -10, 0, 0), UiAuditSnapshotTraversal.Bounds(-20, -10, 0, 0));
        Assert.IsNull(UiAuditSnapshotTraversal.Bounds(0, 0, -1, 2));
        Assert.IsNull(UiAuditSnapshotTraversal.Bounds(0, 0, 2, -1));
        Assert.IsNull(UiAuditSnapshotTraversal.Bounds(double.NaN, 0, 2, 2));
        Assert.IsNull(UiAuditSnapshotTraversal.Bounds(0, double.PositiveInfinity, 2, 2));
        Assert.IsNull(UiAuditSnapshotTraversal.Bounds(0, 0, double.NegativeInfinity, 2));
        Assert.IsNull(UiAuditSnapshotTraversal.Bounds(double.MaxValue, 0, double.MaxValue, 2));
    }

    [TestMethod]
    public void RuntimeIdsUseAxeHexNotationWithoutChangingTheGlobalSlugFormat()
    {
        Assert.AreEqual("[2A,7705FE,4,6]", UiAuditSnapshotTraversal.RuntimeId([42, 0x7705fe, 4, 6]));
        Assert.AreEqual("[0,FFFFFFFF]", UiAuditSnapshotTraversal.RuntimeId([0, -1]));
        Assert.IsNull(UiAuditSnapshotTraversal.RuntimeId([]));
        Assert.IsNotNull(UiAuditSnapshotTraversal.RuntimeId(new int[128]));
        Assert.IsNull(UiAuditSnapshotTraversal.RuntimeId(new int[129]));
    }

    private static (UiAuditSnapshotState State, Tree Tree) Capture(Element root, UiAuditSnapshotOptions? options = null)
    {
        options ??= new();
        var state = new UiAuditSnapshotState(options, new(options, CancellationToken.None, () => TimeSpan.Zero));
        var tree = new Tree();
        UiAuditSnapshotTraversal.Walk(root, tree, state);
        return (state, tree);
    }

    private static Element Node(params Element[] children)
    {
        var element = new Element();
        foreach (var child in children)
        {
            element.Children.Add(child);
            child.Parent = element;
        }
        return element;
    }

    private sealed class Element
    {
        internal List<Element> Children { get; } = [];
        internal Element? Parent { get; set; }
        internal long NativeWindow { get; set; }
        internal string? RuntimeId { get; set; }
        internal bool? Password { get; set; } = false;
        internal bool Reject { get; set; }
    }

    private sealed class Tree : UiAuditSnapshotTree<Element>
    {
        internal int Reads { get; private set; }
        internal int Releases { get; private set; }
        internal int NavigationCalls { get; private set; }
        internal Action? OnRead { get; init; }

        internal override Element? FirstChild(Element parent)
        {
            NavigationCalls++;
            return parent.Children.FirstOrDefault();
        }

        internal override Element? NextSibling(Element node)
        {
            NavigationCalls++;
            if (node.Parent is null) { return null; }
            var index = node.Parent.Children.IndexOf(node) + 1;
            return index < node.Parent.Children.Count ? node.Parent.Children[index] : null;
        }

        internal override UiAuditNode? ReadNode(Element node, Element? parent, UiAuditNode header, bool inheritedWithholding)
        {
            Reads++;
            OnRead?.Invoke();
            return node.Reject ? null : header with
            {
                RuntimeId = node.RuntimeId,
                IsPassword = node.Password,
                Name = UiAuditSnapshotTraversal.ShouldWithhold(inheritedWithholding, node.Password) ? null : "Accessible name",
            };
        }

        internal override void Release(Element node) => Releases++;
    }
}

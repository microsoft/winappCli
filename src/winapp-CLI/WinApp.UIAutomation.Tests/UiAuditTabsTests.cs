// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
public sealed class UiAuditTabsTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void DiscoveryIsReadOnlyAndDoesNotNeedNamesOrAutomationIds()
    {
        var tree = StandardTree();
        var result = Capture(tree);
        Assert.AreEqual("captured", result.Status);
        Assert.AreEqual(123, result.ProcessId);
        Assert.AreEqual(42L, result.WindowHandle);
        Assert.AreEqual("[2A,1]", result.RootRuntimeId);
        Assert.IsNull(result.Selection);
        Assert.HasCount(1, result.Containers);
        Assert.IsTrue(result.Containers[0].Supported);
        Assert.IsNull(result.Containers[0].AutomationId);
        Assert.IsNull(result.Containers[0].ParentContainerRuntimeId);
        Assert.HasCount(2, result.Containers[0].Items);
        Assert.IsTrue(result.Containers[0].Items.All(item => item.Supported && item.AutomationId is null));
        Assert.AreEqual(0, tree.SelectCalls);
        Assert.AreEqual(0, tree.ConfirmCalls);
    }

    [TestMethod]
    public void TreeSignatureIsDeterministicSha256WithoutOptionalAutomationIds()
    {
        var first = Capture(StandardTree());
        var changedIds = StandardTree();
        changedIds.Root.Children[0].Data = changedIds.Root.Children[0].Data with { AutomationId = "optional label" };
        changedIds.Root.Children[0].Children[1].Data = changedIds.Root.Children[0].Children[1].Data with { AutomationId = "another optional label" };
        var second = Capture(changedIds, new() { MaxDepth = 50, MaxElements = 10000 });
        Assert.AreEqual(first.TreeSignature, second.TreeSignature);
        Assert.HasCount(64, first.TreeSignature);
        Assert.IsTrue(first.TreeSignature.All(char.IsAsciiHexDigit));
        Assert.AreEqual(first.TreeSignature.ToUpperInvariant(), first.TreeSignature);
        var canonical = "0:4:root:50033:10?\n1:3:tab:50018:10?\n2:5:first:50019:100\n2:6:second:50019:100\n";
        Assert.AreEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))),
            first.TreeSignature);
    }

    [TestMethod]
    public void TreeSignatureChangesWithEveryRequestedObservedFieldIncludingUnknownStates()
    {
        var original = Capture(StandardTree()).TreeSignature;
        Func<UiAuditTabRead, UiAuditTabRead>[] changes =
        [
            node => node with { RuntimeId = "changed" },
            node => node with { ControlType = 50025 },
            node => node with { Enabled = false },
            node => node with { Enabled = null },
            node => node with { Offscreen = true },
            node => node with { Offscreen = null },
            node => node with { Selected = true },
            node => node with { Selected = null },
        ];
        foreach (var change in changes)
        {
            var tree = StandardTree();
            var item = tree.Root.Children[0].Children[1];
            item.Data = change(item.Data);
            Assert.AreNotEqual(original, Capture(tree).TreeSignature);
        }
        var nonTab = StandardTree();
        nonTab.Root.Data = nonTab.Root.Data with { Enabled = false, Offscreen = true };
        Assert.AreNotEqual(original, Capture(nonTab).TreeSignature, "Non-tab structural nodes must participate too.");
    }

    [TestMethod]
    public void TreeSignatureDetectsOrderingAndReparentingWithoutDependingOnReadbackTiming()
    {
        var original = Capture(StandardTree()).TreeSignature;
        var reordered = StandardTree();
        reordered.Root.Children[0].Children.Reverse();
        Assert.AreNotEqual(original, Capture(reordered).TreeSignature);
        var reparented = StandardTree();
        var tab = reparented.Root.Children[0];
        var item = tab.Children[1];
        tab.Children.Remove(item);
        Add(tab.Children[0], item);
        Assert.AreNotEqual(original, Capture(reparented).TreeSignature, "The same runtime-ID preorder at a new depth must differ.");
        Assert.AreEqual(original, Capture(StandardTree(), Selection()).TreeSignature,
            "A selection response hashes its observed pre-selection inventory, not fabricated post-selection content.");
    }

    [TestMethod]
    public void TreeSignatureCoversOnlyVisitedNodesAndCannotUpgradePartialEvidence()
    {
        var options = new UiAuditTabsOptions { MaxElements = 2 };
        var baseline = Capture(StandardTree(), options);
        var hiddenByBound = StandardTree();
        hiddenByBound.Root.Children[0].Children[1].Data = hiddenByBound.Root.Children[0].Children[1].Data with { RuntimeId = "unvisited" };
        var limited = Capture(hiddenByBound, options);
        Assert.AreEqual(baseline.TreeSignature, limited.TreeSignature);
        Assert.AreEqual("partial", limited.Status);
        Assert.IsTrue(limited.ElementLimitReached);
        Assert.AreNotEqual(Capture(StandardTree()).TreeSignature, limited.TreeSignature);
    }

    [TestMethod]
    public void InnerListSelectionOwnerIsAcceptedWithoutChangingThePublicTabIdentity()
    {
        var tree = StandardTree();
        var tab = tree.Root.Children[0];
        var list = Node("list");
        var items = tab.Children.ToArray();
        tab.Children.Clear();
        Add(tab, list);
        foreach (var item in items) { Add(list, item); item.Owner = list; }
        var result = Capture(tree, Selection());
        Assert.AreEqual("captured", result.Status);
        Assert.AreEqual("selected", result.Selection?.Status);
        Assert.AreEqual("tab", result.Selection?.ContainerRuntimeId);
        Assert.AreEqual("second", tree.Selected?.Id);
        Assert.AreEqual(1, tree.SelectCalls);
    }

    [TestMethod]
    public void NestedGroupParentFollowsNearestTabNotHeaderTabItemAncestry()
    {
        var tree = StandardTree();
        var outer = tree.Root.Children[0];
        var content = Node("content");
        var nested = Tab("nested");
        var nestedItem = Item("nested-item", nested);
        Add(outer, content);
        Add(content, nested);
        Add(nested, nestedItem);
        var result = Capture(tree);
        var child = result.Containers.Single(container => container.RuntimeId == "nested");
        Assert.AreEqual("tab", child.ParentContainerRuntimeId);
        Assert.AreEqual("nested-item", child.Items.Single().RuntimeId);
        Assert.HasCount(2, result.Containers.Single(container => container.RuntimeId == "tab").Items);
        Assert.AreEqual("nested", tree.NearestTabs["nested-item"]);
    }

    [TestMethod]
    public void SelectingAChildGroupItemThroughItsOuterGroupIsRejected()
    {
        var tree = StandardTree();
        var outer = tree.Root.Children[0];
        var nested = Tab("nested");
        Add(outer, nested);
        Add(nested, Item("nested-item", nested));
        var result = Capture(tree, Selection() with { Select = "nested-item" });
        Assert.AreEqual("blocked", result.Status);
        Assert.AreEqual("blocked", result.Selection?.Status);
        Assert.AreEqual(0, tree.SelectCalls);
    }

    [TestMethod]
    public void NonTabTargetsAndInnerListIdsAreNotPromotedToAncestorTabs()
    {
        foreach (var request in new[]
        {
            Selection() with { Select = "root" },
            Selection() with { Container = "root" },
            Selection() with { Select = "tab" },
            Selection() with { Container = "not-a-tab" },
        })
        {
            var tree = StandardTree();
            var result = Capture(tree, request);
            Assert.AreEqual("blocked", result.Status);
            Assert.AreEqual(0, tree.SelectCalls);
            Assert.AreEqual(request.Container, result.Selection?.ContainerRuntimeId);
            Assert.AreEqual(request.Select, result.Selection?.ItemRuntimeId);
        }
    }

    [TestMethod]
    public void DisabledOffscreenUnrealizedAndUnsupportedItemsRemainExplicit()
    {
        var mutations = new (Func<UiAuditTabRead, UiAuditTabRead> Change, string Reason)[]
        {
            (data => data with { Enabled = false }, "disabled"),
            (data => data with { Offscreen = true }, "offscreen"),
            (data => data with { Realized = false }, "not_realized_or_hidden"),
            (data => data with { SelectionItem = false }, "selection_item_pattern_required"),
            (data => data with { Selected = null }, "selection_state_unavailable"),
        };
        foreach (var (change, reason) in mutations)
        {
            var tree = StandardTree();
            var item = tree.Root.Children[0].Children[1];
            item.Data = change(item.Data);
            var discovery = Capture(tree);
            Assert.AreEqual(reason == "selection_state_unavailable" ? "partial" : "captured", discovery.Status,
                "Known skipped controls do not make discovery partial; unreadable metadata does.");
            Assert.IsNull(discovery.Selection);
            var result = Capture(tree, Selection());
            Assert.AreEqual("blocked", result.Status);
            var observed = result.Containers[0].Items.Single(entry => entry.RuntimeId == "second");
            Assert.IsFalse(observed.Supported);
            Assert.AreEqual(reason, observed.Reason);
            Assert.AreEqual(0, tree.SelectCalls);
        }
    }

    [TestMethod]
    public void DisabledOrMultiSelectTabContainerCannotAuthorizeItems()
    {
        foreach (var data in new[]
        {
            Tab("tab").Data with { Enabled = false },
            Tab("tab").Data with { SingleSelection = false },
            Tab("tab").Data with { Offscreen = true },
        })
        {
            var tree = StandardTree();
            tree.Root.Children[0].Data = data;
            var discovery = Capture(tree);
            Assert.AreEqual("captured", discovery.Status);
            Assert.IsFalse(discovery.Containers[0].Supported);
            Assert.IsNull(discovery.Selection);
            var result = Capture(tree, Selection());
            Assert.IsFalse(result.Containers[0].Supported);
            Assert.AreEqual("tab_container_unsupported", result.Containers[0].Items[0].Reason);
            Assert.AreEqual(0, tree.SelectCalls);
        }
    }

    [TestMethod]
    public void WrongSelectionContainerDoesNotBecomeAnAncestorFallback()
    {
        var tree = StandardTree();
        var other = Tab("other");
        Add(tree.Root, other);
        tree.Root.Children[0].Children[1].Owner = other;
        var result = Capture(tree, Selection());
        Assert.AreEqual("selection_container_mismatch", result.Containers[0].Items[1].Reason);
        Assert.AreEqual("blocked", result.Status);
        Assert.AreEqual(0, tree.SelectCalls);
    }

    [TestMethod]
    public void ExpectedRootMismatchStopsBeforeTraversingOrSelecting()
    {
        var tree = StandardTree();
        var result = Capture(tree, Selection() with { RootRuntimeId = "[2A,DIFFERENT]" });
        Assert.AreEqual("blocked", result.Status);
        CollectionAssert.Contains(result.Issues.ToArray(), "root_runtime_id_mismatch");
        Assert.AreEqual(0, tree.ReadCalls);
        Assert.AreEqual(0, tree.SelectCalls);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InTreeModalOrVisibleOwnedWindowBlocksMutation(bool owned)
    {
        var tree = StandardTree();
        if (owned) { tree.VisibleOwnedWindow = true; }
        else { Add(tree.Root, Node("dialog") with { Data = new() { RuntimeId = "dialog", Modal = true } }); }
        var result = Capture(tree, Selection());
        Assert.AreEqual("blocked", result.Status);
        Assert.AreEqual("blocked", result.Selection?.Status);
        CollectionAssert.Contains(result.Issues.ToArray(), "modal_or_visible_owned_window");
        Assert.AreEqual(0, tree.SelectCalls);
    }

    [TestMethod]
    public void ANewModalBetweenDiscoveryAndSelectionIsRechecked()
    {
        var tree = StandardTree();
        tree.ModalRecheckFailure = true;
        var result = Capture(tree, Selection());
        Assert.AreEqual("blocked", result.Status);
        CollectionAssert.Contains(result.Issues.ToArray(), "in_tree_modal_or_popup");
        Assert.AreEqual(0, tree.SelectCalls);
    }

    [TestMethod]
    public void DepthAndElementTruncationArePartialAndNeverAuthorizeSelection()
    {
        foreach (var limited in new[] { new UiAuditTabsOptions { MaxDepth = 1 }, new UiAuditTabsOptions { MaxElements = 2 } })
        {
            var tree = StandardTree();
            var result = Capture(tree, limited);
            Assert.AreEqual("partial", result.Status);
            Assert.IsTrue(result.DepthLimitReached || result.ElementLimitReached);
            Assert.IsTrue(tree.ReadCalls <= limited.MaxElements);
            result = Capture(StandardTree(), Selection() with { MaxDepth = limited.MaxDepth, MaxElements = limited.MaxElements });
            Assert.AreEqual("blocked", result.Status);
            Assert.AreEqual("blocked", result.Selection?.Status);
        }
    }

    [TestMethod]
    public void ExactBoundsAndExplicitUnsupportedControlsDoNotInventTruncation()
    {
        var result = Capture(StandardTree(), new() { MaxDepth = 2, MaxElements = 4 });
        Assert.AreEqual("captured", result.Status);
        Assert.IsFalse(result.DepthLimitReached);
        Assert.IsFalse(result.ElementLimitReached);
    }

    [TestMethod]
    public void MissingRuntimeIdsUnreadableNodesAndCyclesCannotClaimComplete()
    {
        var missing = StandardTree();
        missing.Root.Children[0].Children[0].Data = missing.Root.Children[0].Children[0].Data with { RuntimeId = "" };
        var result = Capture(missing);
        Assert.AreEqual("partial", result.Status);
        Assert.AreEqual("runtime_id_unavailable", result.Containers[0].Items[0].Reason);
        var unreadable = StandardTree();
        unreadable.Root.Children[0].Children[0].Data = unreadable.Root.Children[0].Children[0].Data with { Issues = ["read_failed"] };
        Assert.AreEqual("partial", Capture(unreadable).Status);
        var cycle = StandardTree();
        cycle.Root.Children.Add(cycle.Root);
        result = Capture(cycle);
        Assert.AreEqual("partial", result.Status);
        CollectionAssert.Contains(result.Issues.ToArray(), "repeated_runtime_id");
    }

    [TestMethod]
    public void OrphanTabItemsAreExplicitUnsupportedEvidence()
    {
        var tree = StandardTree();
        Add(tree.Root, Item("orphan", tree.Root));
        var result = Capture(tree);
        Assert.AreEqual("partial", result.Status);
        CollectionAssert.Contains(result.Issues.ToArray(), "orphan_tab_item:orphan");
    }

    [TestMethod]
    public void ConfirmedSelectionRequiresTwoReadbacksAtLeastFiftyMillisecondsApart()
    {
        var tree = StandardTree();
        var elapsed = TimeSpan.Zero;
        var delays = 0;
        var result = new UiAuditTabsCapture<NodeState>(tree, 42, Selection(), () => { },
            milliseconds =>
            {
                Assert.AreEqual(50, milliseconds);
                delays++;
                elapsed += TimeSpan.FromMilliseconds(30);
            }, () => elapsed).Run();
        Assert.AreEqual("selected", result.Selection?.Status);
        Assert.AreEqual(2, delays, "A 30ms clock advance cannot satisfy the required 50ms gap.");
        Assert.IsTrue(result.Containers[0].Items.Single(item => item.RuntimeId == "second").IsSelected);
    }

    [TestMethod]
    public void AlreadySelectedStillRequiresSettledReadbackButDoesNotSelectAgain()
    {
        var tree = StandardTree();
        tree.Selected = tree.Root.Children[0].Children[1];
        var result = Capture(tree, Selection());
        Assert.AreEqual("already-selected", result.Selection?.Status);
        Assert.AreEqual(0, tree.SelectCalls);
        Assert.AreEqual(3, tree.ConfirmCalls);
        Assert.IsTrue(tree.ModalChecks >= 3);
    }

    [TestMethod]
    public void TransientSelectionAndIdentityChangesDoNotReportSuccess()
    {
        var tree = StandardTree();
        tree.NeverConfirms = true;
        var result = Capture(tree, Selection());
        Assert.AreEqual("blocked", result.Selection?.Status);
        Assert.AreEqual("blocked", result.Status);
        CollectionAssert.Contains(result.Issues.ToArray(), "selection_not_confirmed_twice");
        tree = StandardTree();
        tree.RootFailureAfterReads = true;
        result = Capture(tree, Selection());
        Assert.AreEqual("blocked", result.Status);
        Assert.AreEqual(0, tree.SelectCalls);
    }

    [TestMethod]
    public void CancellationAndDeadlineStopBeforeAnyProviderWork()
    {
        foreach (var code in new[] { "cancelled", "deadline_reached" })
        {
            var tree = StandardTree();
            var result = new UiAuditTabsCapture<NodeState>(tree, 42, Selection(),
                () => throw new UiAuditSnapshotStoppedException(code, code), _ => Assert.Fail("No delay expected.")).Run();
            Assert.AreEqual("blocked", result.Status);
            CollectionAssert.Contains(result.Issues.ToArray(), code);
            Assert.AreEqual(0, tree.ReadCalls);
            Assert.AreEqual(0, tree.SelectCalls);
        }
    }

    [TestMethod]
    public void NativeServiceValidatesConsentAndBoundsBeforeStartingAThread()
    {
        NativeUiAuditTabsService.Validate(42, new());
        NativeUiAuditTabsService.Validate(42, Selection());
        foreach (var invalid in new[]
        {
            new UiAuditTabsOptions { MaxDepth = 0 }, new UiAuditTabsOptions { MaxDepth = 51 },
            new UiAuditTabsOptions { MaxElements = 0 }, new UiAuditTabsOptions { MaxElements = 10001 },
            new UiAuditTabsOptions { TimeoutMs = 999 }, new UiAuditTabsOptions { TimeoutMs = 120001 },
            new UiAuditTabsOptions { AcceptNavigation = true }, new UiAuditTabsOptions { RootRuntimeId = "root" },
            Selection() with { AcceptNavigation = false }, Selection() with { Container = null },
            Selection() with { Select = "" }, Selection() with { RootRuntimeId = null },
        })
        {
            Assert.ThrowsExactly<ArgumentException>(() => NativeUiAuditTabsService.Validate(42, invalid));
        }
    }

    [TestMethod]
    public async Task MissingNativeWindowProducesTypedBlockedEvidence()
    {
        var result = await new NativeUiAuditTabsService().CaptureAsync(long.MaxValue, new(), TestContext.CancellationToken);
        Assert.AreEqual("blocked", result.Status);
        Assert.AreEqual(long.MaxValue, result.WindowHandle);
        Assert.AreEqual("", result.RootRuntimeId);
        Assert.AreEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Array.Empty<byte>())), result.TreeSignature);
        Assert.AreEqual(0, result.ProcessId);
        Assert.IsNotEmpty(result.Issues);
    }

    [TestMethod]
    public void TransientComScopeCannotReleaseAnObjectRetainedForExactSelection()
    {
        var value = new DisposeCounter();
        using (var retained = new NativeUiAuditSnapshotComScope())
        {
            retained.Add(value);
            using (var borrowed = new NativeUiAuditSnapshotComScope(retained)) { borrowed.Add(value); }
            Assert.AreEqual(0, value.Count);
        }
        Assert.AreEqual(1, value.Count);
    }

    private static UiAuditTabsOptions Selection() => new()
    {
        Select = "second", Container = "tab", RootRuntimeId = "[2A,1]", AcceptNavigation = true,
    };

    private static UiAuditTabsResult Capture(Tree tree, UiAuditTabsOptions? options = null)
    {
        var elapsed = TimeSpan.Zero;
        return new UiAuditTabsCapture<NodeState>(tree, 42, options ?? new(), () => { },
            milliseconds => elapsed += TimeSpan.FromMilliseconds(milliseconds), () => elapsed).Run();
    }

    private static Tree StandardTree()
    {
        var root = Node("root");
        var tab = Tab("tab");
        Add(root, tab);
        Add(tab, Item("first", tab));
        Add(tab, Item("second", tab));
        return new(root);
    }

    private static NodeState Node(string id) => new(id)
    {
        Data = new() { RuntimeId = id, ControlType = 50033, Enabled = true, Offscreen = false, Realized = true },
    };
    private static NodeState Tab(string id) => Node(id) with
    {
        Data = new() { RuntimeId = id, Kind = UiAuditTabKind.Tab, ControlType = 50018, Enabled = true, Offscreen = false, Realized = true, SingleSelection = true },
    };
    private static NodeState Item(string id, NodeState owner) => Node(id) with
    {
        Owner = owner,
        Data = new() { RuntimeId = id, Kind = UiAuditTabKind.TabItem, ControlType = 50019, Enabled = true, Offscreen = false, Realized = true, SelectionItem = true, Selected = false },
    };
    private static void Add(NodeState parent, NodeState child) { parent.Children.Add(child); child.Parent = parent; }

    private sealed record NodeState(string Id)
    {
        internal UiAuditTabRead Data { get; set; } = new();
        internal NodeState? Parent { get; set; }
        internal NodeState? Owner { get; set; }
        internal List<NodeState> Children { get; } = [];
    }

    private sealed class Tree(NodeState root) : UiAuditTabsTree<NodeState>
    {
        internal override NodeState Root => root;
        internal override int ProcessId => 123;
        internal override string RootRuntimeId => "[2A,1]";
        internal int ReadCalls { get; private set; }
        internal int SelectCalls { get; private set; }
        internal int ConfirmCalls { get; private set; }
        internal int ModalChecks { get; private set; }
        internal bool VisibleOwnedWindow { get; set; }
        internal bool ModalRecheckFailure { get; set; }
        internal bool RootFailureAfterReads { get; set; }
        internal bool NeverConfirms { get; set; }
        internal NodeState? Selected { get; set; }
        internal Dictionary<string, string?> NearestTabs { get; } = [];
        internal override NodeState? FirstChild(NodeState node) => node.Children.FirstOrDefault();
        internal override NodeState? NextSibling(NodeState node) => node.Parent?.Children.ElementAtOrDefault(node.Parent.Children.IndexOf(node) + 1);
        internal override UiAuditTabRead Read(NodeState node, NodeState? parent, NodeState? nearestTab)
        {
            ReadCalls++;
            NearestTabs[node.Id] = nearestTab?.Id;
            var owner = node.Owner;
            while (owner is not null && owner.Data.Kind != UiAuditTabKind.Tab) { owner = owner.Parent; }
            return node.Data with { SelectionContainerMatches = owner is not null && ReferenceEquals(owner, nearestTab) };
        }
        internal override void VerifyRoot()
        {
            if (RootFailureAfterReads && ReadCalls > 0) { throw new UiAuditSnapshotException("root_identity_changed", "Root changed."); }
        }
        internal override bool HasVisibleOwnedWindow() => VisibleOwnedWindow;
        internal override void VerifyNoModal()
        {
            ModalChecks++;
            if (ModalRecheckFailure) { throw new UiAuditSnapshotException("in_tree_modal_or_popup", "Modal appeared."); }
        }
        internal override void Select(NodeState tab, NodeState item, string tabId, string itemId) { SelectCalls++; Selected = item; }
        internal override bool ConfirmSelected(NodeState tab, NodeState item, string tabId, string itemId)
        {
            ConfirmCalls++;
            return !NeverConfirms && ReferenceEquals(Selected, item);
        }
    }

    private sealed class DisposeCounter : IDisposable
    {
        internal int Count { get; private set; }
        public void Dispose() => Count++;
    }
}

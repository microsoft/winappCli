// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

public partial class RealUiAutomationTests
{
    [TestMethod]
    public async Task ExplicitAction_AllRetainedActions_UseSameProviderWithoutResolution()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        var button = await ResolveAsync(svc, target, "btnInvoke");
        var check = await ResolveAsync(svc, target, "chkToggle");
        var elements = await svc.InspectAsync(target, "treeView", 3, CancellationToken.None);
        var treeItem = elements.First(e => e.Type == "TreeItem" && e.Name == "Root");
        var requests = new[]
        {
            (button, UiInvokeAction.Invoke, "InvokePattern", "invoke"),
            (check, UiInvokeAction.Toggle, "TogglePattern", "toggle"),
            (check, UiInvokeAction.ToggleOn, "TogglePattern", "none"),
            (check, UiInvokeAction.ToggleOff, "TogglePattern", "toggle"),
            (check, UiInvokeAction.ToggleOff, "TogglePattern", "none"),
            (check, UiInvokeAction.ToggleOn, "TogglePattern", "toggle"),
            (treeItem, UiInvokeAction.Select, "SelectionItemPattern", "select"),
            (treeItem, UiInvokeAction.Collapse, "ExpandCollapsePattern", "collapse"),
            (treeItem, UiInvokeAction.Expand, "ExpandCollapsePattern", "expand"),
        };
        Assert.AreEqual(0, svc.SerializedElementResolutionCount);
        foreach (var (element, action, pattern, performed) in requests)
        {
            Assert.IsNotNull(element.Context);
            Assert.AreEqual(new UiInvokeActionResult(pattern, performed),
                await svc.InvokeAsync(target, element, action, CancellationToken.None));
            Assert.AreEqual(0, svc.SerializedElementResolutionCount, $"{action} re-resolved its retained provider.");
        }
        Assert.IsTrue(fx.OnUiThread(() => fx.ToggleCheck.Checked));
        Assert.AreEqual("Root", fx.OnUiThread(() => fx.Tree.SelectedNode?.Text));
        Assert.IsTrue(fx.OnUiThread(() => fx.Tree.Nodes[0].IsExpanded));
        await WaitForAsync(() => Task.FromResult(fx.OnUiThread(() => fx.ResultBox.Text == "clicked")),
            "retained explicit invoke did not click the selected button");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExplicitAction_RemovedRetainedButton_DoesNotInvokeReplacement(bool sameAutomationId)
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        var selected = await ResolveAsync(svc, target, "btnInvoke");
        Assert.IsNotNull(selected.Context);
        var replacementClicks = 0;
        fx.OnUiThread(() =>
        {
            fx.InvokeButton.Dispose();
            var replacement = new Button
            {
                Name = sameAutomationId ? "btnInvoke" : "replacement",
                AccessibleName = "Click Me",
                Text = "Click Me",
                Bounds = new System.Drawing.Rectangle(10, 10, 120, 30),
            };
            replacement.Click += (_, _) => replacementClicks++;
            fx.Form.Controls.Add(replacement);
            replacement.CreateControl();
        });
        var replacementModel = await ResolveAsync(svc, target, sameAutomationId ? "btnInvoke" : "replacement");
        Assert.IsNotNull(replacementModel.Context);
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.InvokeAsync(target, selected, UiInvokeAction.Invoke, CancellationToken.None));
        StringAssert.Contains(error.Message, "stale");
        Assert.AreEqual(0, svc.SerializedElementResolutionCount);
        Assert.AreEqual(0, fx.OnUiThread(() => replacementClicks));
    }

    [TestMethod]
    public async Task ExplicitAction_RetainedValidationFailure_DoesNotResolveAgain()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        var selected = await ResolveAsync(svc, target, "btnInvoke");
        var failure = new System.Runtime.InteropServices.COMException("Provider access denied.", unchecked((int)0x80070005));
        UiAutomationService.s_getElementProcessId = _ => throw failure;
        var error = await Assert.ThrowsExactlyAsync<System.Runtime.InteropServices.COMException>(
            () => svc.InvokeAsync(target, selected, UiInvokeAction.Invoke, CancellationToken.None));
        Assert.AreSame(failure, error);
        Assert.AreEqual(0, svc.SerializedElementResolutionCount);
        Assert.AreEqual("unclicked", fx.OnUiThread(() => fx.ResultBox.Text));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExplicitAction_RemovedSelectedButton_DoesNotRebindToSurvivingSameNameSibling(bool promotedAutomationId)
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        var secondaryClicks = 0;
        fx.OnUiThread(() =>
        {
            fx.InvokeButton.AccessibleName = "Save";
            fx.ParentInvokeButton.AccessibleName = "Save";
            fx.ParentInvokeButton.Click += (_, _) => secondaryClicks++;
        });
        var selected = await ResolveAsync(svc, target, "btnInvoke");
        selected.Context = null; // Exercise the serialized-model route, not the retained provider.
        if (promotedAutomationId)
        {
            selected.Selector = selected.AutomationId;
        }
        else
        {
            Assert.IsNotNull(SlugGenerator.ParseSlug(selected.Selector!));
        }
        fx.OnUiThread(() => fx.InvokeButton.Dispose());

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.InvokeAsync(target, selected, UiInvokeAction.Invoke, CancellationToken.None));
        Assert.AreEqual(0, fx.OnUiThread(() => secondaryClicks));

        // Compatibility: only the explicit overload is strict. Legacy invocation still rebinds.
        Assert.AreEqual("InvokePattern", await svc.InvokeAsync(target, selected, CancellationToken.None));
        await WaitForAsync(() => Task.FromResult(fx.OnUiThread(() => secondaryClicks == 1)),
            "legacy same-name fallback no longer invoked the surviving sibling");
    }

    [TestMethod]
    public async Task ExplicitAction_MissingRuntimeSlug_DoesNotTryAlternateAutomationId()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        var selected = await ResolveAsync(svc, target, "btnInvoke");
        // The caller holds a stale runtime selector alongside an AutomationId which still exists.
        selected.Context = null;
        selected.Selector = "btn-removed-primary-1234";
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.InvokeAsync(target, selected, UiInvokeAction.Invoke, CancellationToken.None));
        Assert.AreEqual("unclicked", fx.OnUiThread(() => fx.ResultBox.Text));
    }

    [TestMethod]
    public async Task ExplicitAction_AutomationIdNowAmbiguous_DoesNotChooseFirstMatch()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        var selected = await ResolveAsync(svc, target, "btnInvoke");
        selected.Context = null;
        selected.Selector = selected.AutomationId;
        fx.OnUiThread(() => fx.ParentInvokeButton.Name = "btnInvoke");
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.InvokeAsync(target, selected, UiInvokeAction.Invoke, CancellationToken.None));
        StringAssert.Contains(error.Message, "no longer unique");
        Assert.AreEqual("unclicked", fx.OnUiThread(() => fx.ResultBox.Text));
    }

    [TestMethod]
    public async Task ExplicitAction_NameOnlyExternalModel_FailsRatherThanGuessing()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        var model = new UiElement { Name = "Click Me", Type = "Button" };
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.InvokeAsync(target, model, UiInvokeAction.Invoke, CancellationToken.None));
        StringAssert.Contains(error.Message, "cannot identify an exact element");
        Assert.AreEqual("unclicked", fx.OnUiThread(() => fx.ResultBox.Text));
    }

    [TestMethod]
    public async Task ExplicitAction_Invoke_UsesInvokePatternOnSelectedButton()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        var button = await ResolveAsync(svc, target, "btnInvoke");

        Assert.AreEqual(new UiInvokeActionResult("InvokePattern", "invoke"),
            await svc.InvokeAsync(target, button, UiInvokeAction.Invoke, CancellationToken.None));
        await WaitForAsync(() => Task.FromResult(fx.OnUiThread(() => fx.ResultBox.Text == "clicked")),
            "explicit invoke did not click the button");
    }

    [TestMethod]
    public async Task ExplicitAction_MultiPatternCheckbox_ToggleDoesNotUseInvokeAndFlipsOnce()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        var check = await ResolveAsync(svc, target, "chkToggle");
        var changes = 0;
        fx.OnUiThread(() => fx.ToggleCheck.CheckStateChanged += (_, _) => changes++);

        Assert.AreEqual(new UiInvokeActionResult("TogglePattern", "toggle"),
            await svc.InvokeAsync(target, check, UiInvokeAction.Toggle, CancellationToken.None));
        Assert.AreEqual(CheckState.Checked, fx.OnUiThread(() => fx.ToggleCheck.CheckState));
        Assert.AreEqual(1, fx.OnUiThread(() => changes));

        // The same real control also exposes InvokePattern: omitted action retains its old priority.
        Assert.AreEqual("InvokePattern", await svc.InvokeAsync(target, check, CancellationToken.None));
        await WaitForAsync(() => Task.FromResult(fx.OnUiThread(() => fx.ToggleCheck.CheckState == CheckState.Unchecked)),
            "legacy InvokePattern did not toggle the checkbox");
    }

    [TestMethod]
    [DataRow(UiInvokeAction.ToggleOn, CheckState.Unchecked, CheckState.Checked, 1)]
    [DataRow(UiInvokeAction.ToggleOn, CheckState.Checked, CheckState.Checked, 0)]
    [DataRow(UiInvokeAction.ToggleOff, CheckState.Unchecked, CheckState.Unchecked, 0)]
    [DataRow(UiInvokeAction.ToggleOff, CheckState.Checked, CheckState.Unchecked, 1)]
    public async Task ExplicitAction_ToggleOnOff_IsIdempotent(
        UiInvokeAction action, CheckState initial, CheckState desired, int expectedChanges)
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        var changes = 0;
        fx.OnUiThread(() =>
        {
            fx.ToggleCheck.CheckState = initial;
            fx.ToggleCheck.CheckStateChanged += (_, _) => changes++;
        });
        var check = await ResolveAsync(svc, target, "chkToggle");

        Assert.AreEqual(new UiInvokeActionResult("TogglePattern", expectedChanges == 0 ? "none" : "toggle"),
            await svc.InvokeAsync(target, check, action, CancellationToken.None));
        Assert.AreEqual(desired, fx.OnUiThread(() => fx.ToggleCheck.CheckState));
        Assert.AreEqual(expectedChanges, fx.OnUiThread(() => changes));
        Assert.AreEqual(new UiInvokeActionResult("TogglePattern", "none"),
            await svc.InvokeAsync(target, check, action, CancellationToken.None));
        Assert.AreEqual(expectedChanges, fx.OnUiThread(() => changes));
    }

    [TestMethod]
    [DataRow(UiInvokeAction.ToggleOn, CheckState.Checked, 2)]
    [DataRow(UiInvokeAction.ToggleOff, CheckState.Unchecked, 1)]
    public async Task ExplicitAction_IndeterminateCheckbox_ReachesStateWithinTwoToggles(
        UiInvokeAction action, CheckState desired, int expectedChanges)
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        var changes = 0;
        fx.OnUiThread(() => fx.TriCheck.CheckStateChanged += (_, _) => changes++);
        var check = await ResolveAsync(svc, target, "chkTri");

        Assert.AreEqual(new UiInvokeActionResult("TogglePattern", "toggle"),
            await svc.InvokeAsync(target, check, action, CancellationToken.None));
        Assert.AreEqual(desired, fx.OnUiThread(() => fx.TriCheck.CheckState));
        Assert.AreEqual(expectedChanges, fx.OnUiThread(() => changes));
        Assert.AreEqual("none", (await svc.InvokeAsync(target, check, action, CancellationToken.None)).PerformedAction);
        Assert.AreEqual(expectedChanges, fx.OnUiThread(() => changes));
    }

    [TestMethod]
    public async Task ExplicitAction_DeterminateThreeStateCheckbox_DoesNotToggleAgainAfterFailedVerification()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        var changes = 0;
        fx.OnUiThread(() =>
        {
            fx.TriCheck.CheckState = CheckState.Checked;
            fx.TriCheck.CheckStateChanged += (_, _) => changes++;
        });
        var check = await ResolveAsync(svc, target, "chkTri");

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.InvokeAsync(target, check, UiInvokeAction.ToggleOff, CancellationToken.None));
        StringAssert.Contains(error.Message, "did not reach");
        Assert.AreEqual(CheckState.Indeterminate, fx.OnUiThread(() => fx.TriCheck.CheckState));
        Assert.AreEqual(1, fx.OnUiThread(() => changes));
    }

    [TestMethod]
    public async Task ExplicitAction_IndeterminateCheckboxThatRejectsChanges_FailsAfterTwoToggles()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        var attempts = 0;
        fx.OnUiThread(() => fx.TriCheck.CheckStateChanged += (_, _) =>
        {
            if (fx.TriCheck.CheckState != CheckState.Indeterminate)
            {
                attempts++;
                fx.TriCheck.CheckState = CheckState.Indeterminate;
            }
        });
        var check = await ResolveAsync(svc, target, "chkTri");

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.InvokeAsync(target, check, UiInvokeAction.ToggleOn, CancellationToken.None));
        StringAssert.Contains(error.Message, "after 2 toggle(s)");
        Assert.AreEqual(2, fx.OnUiThread(() => attempts));
        Assert.AreEqual(CheckState.Indeterminate, fx.OnUiThread(() => fx.TriCheck.CheckState));
    }

    [TestMethod]
    public async Task ExplicitAction_Select_SelectsExactlyRequestedListItem()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        await ResolveAsync(svc, target, "lstItems");
        var items = await svc.InspectAsync(target, "lstItems", 2, CancellationToken.None);
        var selected = items.First(e => e.Type == "ListItem" && e.Name == "Item 04");

        Assert.AreEqual(new UiInvokeActionResult("SelectionItemPattern", "select"),
            await svc.InvokeAsync(target, selected, UiInvokeAction.Select, CancellationToken.None));
        Assert.AreEqual("Item 04", fx.OnUiThread(() => fx.ItemsList.SelectedItem));
        Assert.IsTrue(await IsSelectedAsync(svc, target, selected));
    }

    [TestMethod]
    public async Task ExplicitAction_MultiPatternTreeItem_SelectExpandCollapseRemainIndependent()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        await ResolveAsync(svc, target, "treeView");
        var elements = await svc.InspectAsync(target, "treeView", 3, CancellationToken.None);
        var root = elements.First(e => e.Type == "TreeItem" && e.Name == "Root");
        fx.OnUiThread(() =>
        {
            fx.Tree.SelectedNode = fx.Tree.Nodes[0].Nodes[0];
            fx.Tree.Nodes[0].Collapse();
        });

        Assert.AreEqual(new UiInvokeActionResult("SelectionItemPattern", "select"),
            await svc.InvokeAsync(target, root, UiInvokeAction.Select, CancellationToken.None));
        Assert.AreEqual("Root", fx.OnUiThread(() => fx.Tree.SelectedNode?.Text));
        Assert.IsFalse(fx.OnUiThread(() => fx.Tree.Nodes[0].IsExpanded), "Select must not expand.");

        Assert.AreEqual(new UiInvokeActionResult("ExpandCollapsePattern", "expand"),
            await svc.InvokeAsync(target, root, UiInvokeAction.Expand, CancellationToken.None));
        Assert.IsTrue(fx.OnUiThread(() => fx.Tree.Nodes[0].IsExpanded));
        Assert.AreEqual(new UiInvokeActionResult("ExpandCollapsePattern", "collapse"),
            await svc.InvokeAsync(target, root, UiInvokeAction.Collapse, CancellationToken.None));
        Assert.IsFalse(fx.OnUiThread(() => fx.Tree.Nodes[0].IsExpanded));
        Assert.AreEqual("Root", fx.OnUiThread(() => fx.Tree.SelectedNode?.Text));
    }

    [TestMethod]
    [DataRow(UiInvokeAction.Select, "SelectionItemPattern")]
    [DataRow(UiInvokeAction.Toggle, "TogglePattern")]
    [DataRow(UiInvokeAction.ToggleOn, "TogglePattern")]
    [DataRow(UiInvokeAction.ToggleOff, "TogglePattern")]
    [DataRow(UiInvokeAction.Expand, "ExpandCollapsePattern")]
    [DataRow(UiInvokeAction.Collapse, "ExpandCollapsePattern")]
    public async Task ExplicitAction_ButtonMissingMatchingPattern_DoesNotInvoke(UiInvokeAction action, string pattern)
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        var button = await ResolveAsync(svc, target, "btnInvoke");
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.InvokeAsync(target, button, action, CancellationToken.None));
        StringAssert.Contains(error.Message, pattern);
        Assert.AreEqual("unclicked", fx.OnUiThread(() => fx.ResultBox.Text));
    }

    [TestMethod]
    public async Task ExplicitAction_DisplayOnlyChild_DoesNotInvokeItsAncestor()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        var clicks = 0;
        fx.OnUiThread(() => fx.ParentInvokeButton.Click += (_, _) => clicks++);
        // Inspect returns the exact child; FindSingle's convenience selection must not choose it for us.
        var elements = await svc.InspectAsync(target, null, 8, CancellationToken.None);
        var child = elements.First(e => e.AutomationId == "lblInsideInvoke");
        child.InvokableAncestor = await ResolveAsync(svc, target, "btnParentInvoke");

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.InvokeAsync(target, child, UiInvokeAction.Invoke, CancellationToken.None));
        StringAssert.Contains(error.Message, "InvokePattern");
        Assert.AreEqual(0, fx.OnUiThread(() => clicks));
    }

    [TestMethod]
    public async Task ExplicitAction_InvalidEnumAndCancellation_AreRejectedBeforeResolution()
    {
        var svc = NewService();
        var target = new UiTarget { ProcessId = -1, ProcessName = "nonexistent" };
        var element = new UiElement { Id = "nonexistent" };
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => svc.InvokeAsync(target, element, (UiInvokeAction)99, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => svc.InvokeAsync(target, element, UiInvokeAction.Invoke, new CancellationToken(true)));
    }

    [TestMethod]
    public async Task ExplicitAction_StaleElement_ReportsErrorWithoutFallback()
    {
        UiElement element;
        UiTarget target;
        var svc = NewService();
        using (var fx = new UiaTestFixture())
        {
            target = SessionFor(fx);
            element = await ResolveAsync(svc, target, "btnInvoke");
        }
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.InvokeAsync(target, element, UiInvokeAction.Invoke, CancellationToken.None));
        StringAssert.Contains(error.Message, "stale");
    }
}

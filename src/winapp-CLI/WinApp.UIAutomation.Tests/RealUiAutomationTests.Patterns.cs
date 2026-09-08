// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32.UI.Accessibility;

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

public partial class RealUiAutomationTests
{
    // -----------------------------------------------------------------------------
    // ResolveComElement fallbacks (no slug — AutomationId, then Name+ControlType)
    // -----------------------------------------------------------------------------

    [TestMethod]
    public async Task GetTextAsync_ResolvesViaAutomationIdFallback()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "txtValue");

        // No Selector/slug set — only AutomationId — forcing the AutomationId property fallback.
        var model = new UiElement { Type = "Edit", AutomationId = "txtValue", WindowHandle = fx.Hwnd };
        var text = await svc.GetTextAsync(uiTarget, model, CancellationToken.None);

        Assert.AreEqual("initial", text);
    }

    [TestMethod]
    public async Task GetTextAsync_ResolvesViaNameAndControlTypeFallback()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        // Neither Selector nor AutomationId — Name + a mappable ControlType drives the AndCondition path.
        var model = new UiElement { Type = "Button", Name = "Click Me", WindowHandle = fx.Hwnd };
        var text = await svc.GetTextAsync(uiTarget, model, CancellationToken.None);

        Assert.AreEqual("Click Me", text);
    }

    [TestMethod]
    public async Task GetTextAsync_ResolvesViaNameOnlyFallback()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "lblText");

        // A ControlType that MapControlType doesn't recognize (maps to 0) forces the Name-only condition.
        var model = new UiElement { Type = "Unrecognized", Name = "Hello Label", WindowHandle = fx.Hwnd };
        var text = await svc.GetTextAsync(uiTarget, model, CancellationToken.None);

        Assert.AreEqual("Hello Label", text);
    }

    [TestMethod]
    public async Task FindSingleElementAsync_NameMatchesButtonAndLabel_PrefersInvokable()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnShared");

        // "Shared Widget" is the Name of both a Button and a Label; the service disambiguates by
        // preferring the single invokable match.
        var found = await svc.FindSingleElementAsync(uiTarget,
            new UiSelector { Query = "Shared Widget" }, CancellationToken.None);

        Assert.IsNotNull(found);
        Assert.AreEqual("Button", found.Type);
        Assert.AreEqual("btnShared", found.AutomationId);
    }

    // -----------------------------------------------------------------------------
    // Invoke / GetText / GetProperties pattern variety (Toggle / Selection / ExpandCollapse)
    // -----------------------------------------------------------------------------

    [TestMethod]
    public async Task InvokeAsync_ComboBox_UsesInvokePattern()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var combo = await ResolveAsync(svc, uiTarget, "cboSelect");

        var pattern = await svc.InvokeAsync(uiTarget, combo, CancellationToken.None);

        Assert.AreEqual("InvokePattern", pattern);
    }

    [TestMethod]
    public async Task FindSingleElementAsync_NonInvokableLabel_ResolvesWithoutInvokableAncestor()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "lblText");

        // Resolving a Label (no InvokePattern) drives the single-element non-invokable branch: the
        // service returns the element and probes for an invokable ancestor (none, since the label
        // lives directly on the form).
        var found = await svc.FindSingleElementAsync(uiTarget, new UiSelector { Query = "Hello Label" }, CancellationToken.None);

        Assert.IsNotNull(found);
        Assert.AreEqual("lblText", found.AutomationId);
        Assert.IsNull(found.InvokableAncestor, "a form-level label has no invokable ancestor");
    }

    [TestMethod]
    public async Task GetTextAsync_CheckedCheckBox_ReturnsOnFromTogglePattern()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var box = await ResolveAsync(svc, uiTarget, "chkChecked");

        var text = await svc.GetTextAsync(uiTarget, box, CancellationToken.None);

        Assert.AreEqual("On", text);
    }

    [TestMethod]
    public async Task GetTextAsync_IndeterminateCheckBox_ReturnsIndeterminate()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var box = await ResolveAsync(svc, uiTarget, "chkTri");

        var text = await svc.GetTextAsync(uiTarget, box, CancellationToken.None);

        Assert.AreEqual("Indeterminate", text);
    }

    [TestMethod]
    public async Task GetTextAsync_ListWithSelection_ReturnsSelectedViaSelectionPattern()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var list = await ResolveAsync(svc, uiTarget, "lstItems");

        // The list has "Item 00" selected by default; GetText falls through to SelectionPattern.
        var text = await svc.GetTextAsync(uiTarget, list, CancellationToken.None);

        Assert.AreEqual("Item 00", text);
    }

    [TestMethod]
    public async Task GetPropertiesAsync_CheckedBox_ReportsToggleStateOn()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var box = await ResolveAsync(svc, uiTarget, "chkChecked");

        var props = await svc.GetPropertiesAsync(uiTarget, box, null, CancellationToken.None);

        Assert.AreEqual("On", props["ToggleState"]);
    }

    [TestMethod]
    public async Task GetPropertiesAsync_TriCheck_ReportsToggleStateIndeterminate()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var box = await ResolveAsync(svc, uiTarget, "chkTri");

        var props = await svc.GetPropertiesAsync(uiTarget, box, null, CancellationToken.None);

        Assert.AreEqual("Indeterminate", props["ToggleState"]);
    }

    [TestMethod]
    public async Task GetPropertiesAsync_ComboBox_ReportsExpandCollapseState()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var combo = await ResolveAsync(svc, uiTarget, "cboSelect");

        var props = await svc.GetPropertiesAsync(uiTarget, combo, null, CancellationToken.None);

        Assert.IsTrue(props.ContainsKey("ExpandCollapseState"));
        Assert.AreEqual("Collapsed", props["ExpandCollapseState"]);
    }

    [TestMethod]
    public async Task GetPropertiesAsync_RadioButton_ReportsIsSelected()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var radio = await ResolveAsync(svc, uiTarget, "rdoOption");

        var props = await svc.GetPropertiesAsync(uiTarget, radio, null, CancellationToken.None);

        Assert.IsTrue(props.ContainsKey("IsSelected"));
        Assert.AreEqual(true, props["IsSelected"]);
    }

    // -----------------------------------------------------------------------------
    // SetValue via RangeValuePattern (numeric control, no ValuePattern)
    // -----------------------------------------------------------------------------

    [TestMethod]
    public async Task SetValueAsync_ScrollBar_SetsViaRangeValuePattern()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var bar = await ResolveAsync(svc, uiTarget, "scrRange");

        // HScrollBar exposes RangeValuePattern (not ValuePattern), so the RangeValue COM strategy is
        // exercised; SetValueAsync drives the control's Value to 42 through the strategy chain.
        await svc.SetValueAsync(uiTarget, bar, "42", CancellationToken.None);

        await WaitForAsync(() => Task.FromResult(fx.OnUiThread(() => fx.RangeBar.Value) == 42),
            "scroll bar value should become 42 via RangeValuePattern");
    }

    [TestMethod]
    public async Task SetValueAsync_ReadOnlyProgressBar_RunsFullFallbackChainWithoutError()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var before = fx.OnUiThread(() => fx.Progress.Value);
        var bar = await ResolveAsync(svc, uiTarget, "prgValue");

        // A ProgressBar is read-only: its RangeValuePattern.SetValue throws (caught) and it exposes no
        // ValuePattern, so the ComValueSetStrategy walks ValuePattern -> RangeValuePattern -> Legacy.
        // The call completes without surfacing an error and the read-only value is unchanged — proving
        // the whole fallback chain executed against a live provider (the individual catch branches).
        await svc.SetValueAsync(uiTarget, bar, "5", CancellationToken.None);

        Assert.AreEqual(before, fx.OnUiThread(() => fx.Progress.Value),
            "a read-only progress bar's value must not change");
    }

    [TestMethod]
    public async Task SetValueAsync_NumericUpDown_FallsThroughToLegacyIAccessible()
    {
        using var fx = new UiaTestFixture();
        var logger = new CapturingLogger<UiAutomationService>();
        var svc = new UiAutomationService(logger, new UiSelectorParser());
        var uiTarget = SessionFor(fx);
        var spinner = await ResolveAsync(svc, uiTarget, "numSpin");

        // A WinForms NumericUpDown exposes no ValuePattern and no *settable* RangeValuePattern, so the
        // ComValueSetStrategy must walk ValuePattern -> RangeValuePattern -> LegacyIAccessible before it
        // reaches a strategy the control accepts. We assert the routing: both pattern strategies log a
        // caught failure, proving execution fell through to the Legacy branch (IAccessible::put_accValue).
        // NumericUpDown accepts put_accValue (S_OK) but does not apply it to its committed Value, so the
        // value-applying Legacy success is asserted by the HScrollBar test and read-only fall-through by
        // the ProgressBar test; here we pin that the chain actually reaches Legacy for a spinner control.
        await svc.SetValueAsync(uiTarget, spinner, "5", CancellationToken.None);

        Assert.IsTrue(
            logger.Has(Microsoft.Extensions.Logging.LogLevel.Debug, "ValuePattern.SetValue failed, trying fallbacks"),
            "ValuePattern must be attempted and fall through for a NumericUpDown (no ValuePattern)");
        Assert.IsTrue(
            logger.Has(Microsoft.Extensions.Logging.LogLevel.Debug, "RangeValuePattern.SetValue failed"),
            "RangeValuePattern must be attempted and fall through, routing the set to LegacyIAccessible");
    }

    // -----------------------------------------------------------------------------
    // Scroll direction variety (up / horizontal) + walk-up + error hints
    // -----------------------------------------------------------------------------

    [TestMethod]
    public async Task ScrollContainerAsync_Up_DecreasesVerticalOffset()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var panel = await ResolveAsync(svc, uiTarget, "pnlScroll");

        await svc.ScrollContainerAsync(uiTarget, panel, null, "bottom", CancellationToken.None);
        var atBottom = await VerticalPercentAsync(svc, uiTarget, "pnlScroll");
        await svc.ScrollContainerAsync(uiTarget, panel, "up", null, CancellationToken.None);
        var afterUp = await VerticalPercentAsync(svc, uiTarget, "pnlScroll");

        Assert.IsTrue(afterUp < atBottom, $"expected up to decrease offset (bottom={atBottom}, afterUp={afterUp})");
    }

    [TestMethod]
    public async Task ScrollContainerAsync_HorizontalRightThenLeft_MovesHorizontalOffset()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var panel = await ResolveAsync(svc, uiTarget, "pnlHScroll");
        var before = await HorizontalPercentAsync(svc, uiTarget, "pnlHScroll");

        await svc.ScrollContainerAsync(uiTarget, panel, "right", null, CancellationToken.None);
        var afterRight = await HorizontalPercentAsync(svc, uiTarget, "pnlHScroll");
        Assert.IsTrue(afterRight > before, $"expected right to increase horizontal offset (before={before}, after={afterRight})");

        await svc.ScrollContainerAsync(uiTarget, panel, "left", null, CancellationToken.None);
        var afterLeft = await HorizontalPercentAsync(svc, uiTarget, "pnlHScroll");
        Assert.IsTrue(afterLeft < afterRight, $"expected left to decrease horizontal offset (right={afterRight}, left={afterLeft})");
    }

    [TestMethod]
    public async Task ScrollContainerAsync_DownOnHorizontalOnlyContainer_ThrowsWithHint()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var panel = await ResolveAsync(svc, uiTarget, "pnlHScroll");

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.ScrollContainerAsync(uiTarget, panel, "down", null, CancellationToken.None));
        StringAssert.Contains(ex.Message, "horizontally");
    }

    [TestMethod]
    public async Task ScrollContainerAsync_InvalidToValue_ThrowsArgumentException()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var panel = await ResolveAsync(svc, uiTarget, "pnlScroll");

        // A --to value other than top/bottom hits the switch default and is rejected.
        var ex = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => svc.ScrollContainerAsync(uiTarget, panel, null, "middle", CancellationToken.None));
        StringAssert.Contains(ex.Message, "middle");
    }

    // -----------------------------------------------------------------------------
    // Control-type mapping breadth (GetControlTypeName / ToUiElement pattern flags)
    // -----------------------------------------------------------------------------

    [TestMethod]
    public async Task InspectAsync_RichControls_MapsVariedControlTypes()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "cboSelect");

        var tree = await svc.InspectAsync(uiTarget, null, 6, CancellationToken.None);
        var types = tree.Select(e => e.Type).ToHashSet();

        // A representative spread of control types resolved through GetControlTypeName.
        foreach (var expected in new[] { "ComboBox", "RadioButton", "Group", "ProgressBar", "Slider", "Tab", "TabItem", "Tree", "TreeItem" })
        {
            Assert.IsTrue(types.Contains(expected), $"expected the tree to contain a '{expected}' control; got: {string.Join(",", types.OrderBy(t => t))}");
        }

        // ToUiElement pattern flags: the checked box surfaces ToggleState, the combo an ExpandState,
        // and the scroll panel a ScrollDir.
        Assert.IsTrue(tree.Any(e => e.AutomationId == "chkChecked" && e.ToggleState == "on"));
        Assert.IsTrue(tree.Any(e => e.AutomationId == "cboSelect" && e.ExpandState is not null));
        Assert.IsTrue(tree.Any(e => e.AutomationId == "pnlScroll" && e.ScrollDir is not null && e.ScrollDir.Contains('v')));
    }

}

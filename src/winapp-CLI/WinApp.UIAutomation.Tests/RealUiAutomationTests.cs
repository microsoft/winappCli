// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32.UI.Accessibility;

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

/// <summary>
/// End-to-end coverage of the real <see cref="UiAutomationService"/> (and its Screenshot /
/// ValueSetStrategy partials) driven against a live in-process WinForms window
/// (<see cref="UiaTestFixture"/>). These are meaningful workflow tests: every method is exercised
/// against genuine UIA providers and asserts a real, observable result — invoking a button flips a
/// text box, setting a value round-trips through UIA, scrolling changes the real scroll offset,
/// screenshots produce non-blank correctly-sized frames, and stale elements raise the real error.
///
/// The whole class is <see cref="DoNotParallelizeAttribute"/> because several tests move the
/// foreground window and/or capture the screen — process-global state that must not race with other
/// tests. Each test owns its fixture window via <c>using</c> so there is no shared mutable state.
/// </summary>
/// <remarks>
/// Host-dependent GPU/Media Foundation ceilings are documented on the innermost native product
/// methods themselves. These tests deliberately avoid line-numbered ceiling notes so future edits
/// cannot leave stale ranges behind.
/// </remarks>
[TestClass]
[DoNotParallelize]
public partial class RealUiAutomationTests
{
    private const int ReadyTimeoutMs = 10_000;
    private const int EffectTimeoutMs = 5_000;

    /// <summary>
    /// Real UIA requires an interactive desktop to host the fixture window and drive providers.
    /// On an interactive host (the coverage host, and any dev box) every test runs for real and
    /// counts toward coverage; on a headless/service (non-interactive) CI agent the whole class
    /// skips cleanly via <see cref="Assert.Inconclusive(string)"/> instead of hard-failing, so it
    /// never blocks CI. This is a safety gate only — it does not suppress or fake any assertion.
    /// </summary>
    [TestInitialize]
    public void RequireInteractiveDesktop()
    {
        if (!Environment.UserInteractive)
        {
            Assert.Inconclusive("Skipped: real UI Automation needs an interactive desktop session (none present on this host).");
        }
    }

    [TestCleanup]
    public void ResetNativeSeams()
    {
        UiAutomationService.ResetNativeSeams();
        WgcCapture.s_isSupported = global::Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported;
        WgcCapture.s_startGrabber = (hwnd, logger, fps) => WgcCapture.StartGrabber(hwnd, logger, fps);
    }

    private static UiAutomationService NewService()
        => new(NullLogger<UiAutomationService>.Instance, new UiSelectorParser());

    private static UiTarget SessionFor(UiaTestFixture fx, bool explicitWindow = true) => new()
    {
        ProcessId = fx.ProcessId,
        ProcessName = "WinApp.Cli.Tests",
        WindowHandle = fx.Hwnd,
        WindowTitle = fx.Title,
        IsExplicitWindow = explicitWindow,
    };

    /// <summary>Polls until the given AutomationId is resolvable via the real service (UIA ready).</summary>
    private static async Task<UiElement> ResolveAsync(UiAutomationService svc, UiTarget uiTarget, string automationId)
    {
        var deadline = Environment.TickCount64 + ReadyTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var el = await svc.FindSingleElementAsync(uiTarget, new UiSelector { Query = automationId }, CancellationToken.None);
            if (el is not null)
            {
                return el;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"Element '{automationId}' never became resolvable via UIA.");
    }

    // -----------------------------------------------------------------------------
    // Window enumeration
    // -----------------------------------------------------------------------------

    [TestMethod]
    public void FindWindowsByTitle_FindsFixtureWindow()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();

        var windows = svc.FindWindowsByTitle(fx.Title);

        Assert.IsTrue(windows.Any(w => w.Hwnd == fx.Hwnd),
            $"Expected FindWindowsByTitle('{fx.Title}') to include the fixture HWND {fx.Hwnd}.");
        Assert.IsTrue(windows.All(w => w.Title.Contains(fx.Title, StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void FindWindowsByPid_FindsFixtureWindow()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();

        var windows = svc.FindWindowsByPid(fx.ProcessId);

        Assert.IsTrue(windows.Any(w => w.Hwnd == fx.Hwnd && w.Pid == fx.ProcessId),
            "Expected FindWindowsByPid to include the fixture window.");
    }

    [TestMethod]
    public void FindWindowsByTitle_EmptyQuery_ReturnsAllVisible()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();

        var windows = svc.FindWindowsByTitle(string.Empty);

        Assert.IsTrue(windows.Any(w => w.Hwnd == fx.Hwnd),
            "An empty title query must match all visible windows, including the fixture.");
    }

    [TestMethod]
    public void TryGetWindowRect_ReturnsTopLevelBoundsForFixture()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();

        Assert.IsFalse(svc.TryGetWindowRect(0, out _), "zero HWND should be rejected");
        Assert.IsTrue(svc.TryGetWindowRect(fx.Hwnd, out var rect), "fixture HWND should have a window rect");
        Assert.IsTrue(rect.Right > rect.Left);
        Assert.IsTrue(rect.Bottom > rect.Top);
    }

    // -----------------------------------------------------------------------------
    // Inspect
    // -----------------------------------------------------------------------------

    [TestMethod]
    public async Task InspectAsync_ReturnsKnownControls()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var tree = await svc.InspectAsync(uiTarget, null, 4, CancellationToken.None);

        Assert.IsTrue(tree.Any(e => e.Type == "Window" && e.Depth == 0), "root Window expected at depth 0");
        Assert.IsTrue(tree.Any(e => e.AutomationId == "btnInvoke" && e.Type == "Button" && e.Depth == 1));
        Assert.IsTrue(tree.Any(e => e.AutomationId == "txtValue" && e.Type == "Edit" && e.Depth == 1));
        Assert.IsTrue(tree.Any(e => e.AutomationId == "chkToggle" && e.Type == "CheckBox" && e.Depth == 1));
        Assert.IsTrue(tree.Any(e => e.AutomationId == "lstItems" && e.Type == "List"));
        // Nested list items live at depth 2 under the list.
        Assert.IsTrue(tree.Any(e => e.Type == "ListItem" && e.Name == "Item 05" && e.Depth == 2));
    }

    [TestMethod]
    public async Task InspectAsync_DepthZero_SetsHasMoreChildren()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var tree = await svc.InspectAsync(uiTarget, null, 0, CancellationToken.None);

        Assert.AreEqual(1, tree.Length, "depth 0 must return only the root element");
        Assert.AreEqual("Window", tree[0].Type);
        Assert.IsTrue(tree[0].HasMoreChildren == true, "root has children, so HasMoreChildren must be set");
    }

    [TestMethod]
    public async Task InspectAsync_ScopedToElement_ReturnsSubtree()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "pnlScroll");

        var tree = await svc.InspectAsync(uiTarget, "pnlScroll", 3, CancellationToken.None);

        Assert.AreEqual("Pane", tree[0].Type, "scoped inspect should root at the panel");
        Assert.AreEqual("pnlScroll", tree[0].AutomationId);
        Assert.IsTrue(tree.Any(e => e.AutomationId == "pnlChild00"), "panel children should be present");
        Assert.IsFalse(tree.Any(e => e.AutomationId == "btnInvoke"), "controls outside the panel must be excluded");
    }

    [TestMethod]
    public async Task InspectAncestorsAsync_ReturnsRootToTargetChain()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var chain = await svc.InspectAncestorsAsync(uiTarget, "btnInvoke", CancellationToken.None);

        Assert.IsTrue(chain.Length >= 2, "expected at least the window and the button");
        Assert.AreEqual("btnInvoke", chain[^1].AutomationId, "target should be last (deepest) in the chain");
        Assert.IsTrue(chain.Any(e => e.Type == "Window"), "the window ancestor should be present");
    }

    [TestMethod]
    public async Task InspectAncestorsAsync_NotFound_Throws()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.InspectAncestorsAsync(uiTarget, "no-such-element-xyz", CancellationToken.None));
    }

    [TestMethod]
    public async Task InspectAsync_NoWindow_ReturnsEmpty()
    {
        var svc = NewService();
        // A session pointing at a non-existent window/process yields no root -> empty result.
        var uiTarget = new UiTarget { ProcessId = 0x7FFFFFFE, WindowHandle = 0, IsExplicitWindow = true };

        var tree = await svc.InspectAsync(uiTarget, null, 3, CancellationToken.None);

        Assert.AreEqual(0, tree.Length);
    }

    [TestMethod]
    public async Task InspectAsync_ScopedToSlug_ReturnsSubtreeOfThatElement()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);

        // First discover the scroll panel's slug from a full inspect, then scope a second inspect to
        // that slug. Passing a slug (not a legacy selector) exercises the slug-scope branch of
        // InspectAsync (ParseSlug -> FindElementBySlug -> ResolveComElement).
        var full = await svc.InspectAsync(uiTarget, null, 4, CancellationToken.None);
        var panel = full.First(e => e.AutomationId == "pnlScroll");
        Assert.IsNotNull(panel.Selector, "expected a promoted slug/selector on the scroll panel");

        var subtree = await svc.InspectAsync(uiTarget, panel.Selector, 3, CancellationToken.None);

        // The scoped walk starts at the panel: its scrollable children are present, but unrelated
        // top-level controls (the invoke button) are not part of this subtree.
        Assert.IsTrue(subtree.Any(e => e.AutomationId != null && e.AutomationId.StartsWith("pnlChild", StringComparison.Ordinal)),
            "scoped subtree should contain the panel's children");
        Assert.IsFalse(subtree.Any(e => e.AutomationId == "btnInvoke"),
            "scoped subtree should not contain controls outside the panel");
    }

    [TestMethod]
    public async Task InspectAsync_NonExplicitSession_IncludesIndependentTopLevelWindow()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = NonExplicitSession(fx);
        fx.OpenOwnedWindow("Fixture Sibling Window");

        // With a non-explicit session and a second independent (non-owned) top-level window open, a
        // full-tree inspect walks the popup/other windows too: it inserts a main-window header
        // separator and a separator + subtree for the sibling window (GetAllAppWindows,
        // GetRootElementForHwnd, the WalkTree over popup elements). Poll until UIA registers the
        // sibling window, then assert both the separator and the sibling's own control are present.
        var deadline = Environment.TickCount64 + ReadyTimeoutMs;
        UiElement[] tree = [];
        while (Environment.TickCount64 < deadline)
        {
            tree = await svc.InspectAsync(uiTarget, null, 6, CancellationToken.None);
            if (tree.Any(e => e.AutomationId == "btnOwned"))
            {
                break;
            }
            await Task.Delay(150);
        }

        Assert.IsTrue(tree.Any(e => e.AutomationId == "btnOwned"),
            "the sibling window's button should appear in the multi-window inspect tree");
        Assert.IsTrue(tree.Any(e => e.Type == "---" && e.Name != null && e.Name.Contains("Fixture Sibling Window", StringComparison.Ordinal)),
            "a separator element naming the sibling window should be inserted");
        Assert.IsTrue(tree.Any(e => e.Type == "---" && e.Id != null && e.Id.StartsWith("--- HWND", StringComparison.Ordinal)),
            "a main-window header separator should be inserted when other windows exist");
    }

    // -----------------------------------------------------------------------------
    // Search / FindSingle
    // -----------------------------------------------------------------------------

    [TestMethod]
    public async Task SearchAsync_ByAutomationId_FindsButton()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var results = await svc.SearchAsync(uiTarget, new UiSelector { Query = "btnInvoke" }, 10, CancellationToken.None);

        Assert.AreEqual(1, results.Length);
        Assert.AreEqual("Button", results[0].Type);
        Assert.AreEqual("btnInvoke", results[0].AutomationId);
    }

    [TestMethod]
    public async Task SearchAsync_BySubstring_FindsManyChildren()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "pnlChild00");

        var results = await svc.SearchAsync(uiTarget, new UiSelector { Query = "Child 1" }, 50, CancellationToken.None);

        // "Child 1" matches Child 10..19 by name (10 controls).
        Assert.IsTrue(results.Length >= 10, $"expected >=10 matches, got {results.Length}");
        Assert.IsTrue(results.All(r => r.Type == "Button"));
    }

    [TestMethod]
    public async Task SearchAsync_NoMatch_ReturnsEmpty()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var results = await svc.SearchAsync(uiTarget, new UiSelector { Query = "zzz-does-not-exist" }, 10, CancellationToken.None);

        Assert.AreEqual(0, results.Length);
    }

    [TestMethod]
    public async Task SearchAsync_NonInvokableLabel_ReturnsElementWithoutInvokableAncestor()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "lblText");

        // A Label supports no InvokePattern, so the FindAll loop takes the !IsInvokable branch and
        // looks for an invokable ancestor. The label sits directly on the form (no invokable parent),
        // so the element is returned with a null InvokableAncestor.
        var results = await svc.SearchAsync(uiTarget, new UiSelector { Query = "Hello Label" }, 10, CancellationToken.None);

        var label = results.SingleOrDefault(r => r.AutomationId == "lblText");
        Assert.IsNotNull(label, "expected the non-invokable label to be returned by search");
        Assert.IsNull(label.InvokableAncestor, "a form-level label has no invokable ancestor");
    }

    [TestMethod]
    public async Task SearchAsync_NonExplicitSession_FindsElementByNameOnOwnedWindow()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        fx.OpenOwnedWindow("Fixture Owned Window");
        var uiTarget = NonExplicitSession(fx);

        // "Owned Button" is the accessible Name (not the AutomationId), so the exact-AutomationId probe
        // on the owned window misses and the search falls through to the substring BuildCondition
        // branch of the owned-window search loop.
        var results = await PollSearchAsync(svc, uiTarget, "Owned Button");

        Assert.IsTrue(results.Any(r => r.AutomationId == "btnOwned"),
            "expected the owned window's button to be found via the substring branch");
    }

    [TestMethod]
    public async Task FindSingleElementAsync_BySlug_ResolvesElement()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        // Get a slug from a list item (list items have no AutomationId, so they get slug selectors).
        await ResolveAsync(svc, uiTarget, "lstItems");
        var tree = await svc.InspectAsync(uiTarget, "lstItems", 2, CancellationToken.None);
        var item = tree.First(e => e.Type == "ListItem" && e.Name == "Item 07");
        Assert.IsNotNull(item.Selector);

        var found = await svc.FindSingleElementAsync(uiTarget, new UiSelector { Slug = item.Selector }, CancellationToken.None);

        Assert.IsNotNull(found);
        Assert.AreEqual("Item 07", found.Name);
    }

    [TestMethod]
    public async Task SearchAsync_BySlug_ResolvesElement()
    {
        // A slug is a valid selector, but every branch of the search matches on Query, so one used to
        // come back empty even when the element existed -- `winapp ui search <slug>` found nothing.
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "lstItems");
        var tree = await svc.InspectAsync(uiTarget, "lstItems", 2, CancellationToken.None);
        var item = tree.First(e => e.Type == "ListItem" && e.Name == "Item 07");
        Assert.IsNotNull(item.Selector);

        var results = await svc.SearchAsync(uiTarget, new UiSelector { Slug = item.Selector }, 10, CancellationToken.None);

        Assert.AreEqual(1, results.Length, "a slug names exactly one element");
        Assert.AreEqual("Item 07", results[0].Name);
        Assert.AreEqual(fx.Hwnd, (nint)results[0].WindowHandle!.Value);
    }

    [TestMethod]
    public async Task SearchAsync_BySlug_NoMatch_ReturnsEmpty()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var results = await svc.SearchAsync(
            uiTarget, new UiSelector { Slug = "btn-definitely-missing-0000" }, 10, CancellationToken.None);

        Assert.AreEqual(0, results.Length);
    }

    [TestMethod]
    public async Task PublicMethods_AlreadyCancelledToken_ThrowBeforeWalkingTheTree()
    {
        // The interface documents `ct` as cancelling each call, so an already-cancelled token must
        // not quietly complete an expensive UIA traversal and return results.
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var element = await ResolveAsync(svc, uiTarget, "btnInvoke");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var ct = cts.Token;

        var selector = new UiSelector { Query = "btnInvoke" };
        Func<Task>[] calls =
        [
            () => svc.InspectAsync(uiTarget, null, 1, ct),
            () => svc.InspectAncestorsAsync(uiTarget, element.Selector!, ct),
            () => svc.SearchAsync(uiTarget, selector, 5, ct),
            () => svc.FindSingleElementAsync(uiTarget, selector, ct),
            () => svc.GetPropertiesAsync(uiTarget, element, "Name", ct),
            () => svc.ScreenshotAsync(uiTarget, null, false, false, ct),
            () => svc.InvokeAsync(uiTarget, element, ct),
            () => svc.SetValueAsync(uiTarget, element, "x", ct),
            () => svc.FocusAsync(uiTarget, element, ct),
            () => svc.ScrollIntoViewAsync(uiTarget, element, ct),
            () => svc.ScrollContainerAsync(uiTarget, element, "down", null, ct),
            () => svc.GetFocusedElementAsync(uiTarget, ct),
            () => svc.GetTextAsync(uiTarget, element, ct),
        ];

        foreach (var call in calls)
        {
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(call);
        }
    }

    [TestMethod]
    public async Task FindSingleElementAsync_NotFound_ReturnsNull()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var found = await svc.FindSingleElementAsync(uiTarget, new UiSelector { Query = "nope-xyz-123" }, CancellationToken.None);

        Assert.IsNull(found);
    }

    [TestMethod]
    public async Task FindSingleElementAsync_Ambiguous_Throws()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "pnlChild00");

        // "Child" matches 40 buttons by name; all are invokable so it cannot be disambiguated.
        var ex = await Assert.ThrowsExactlyAsync<UiAmbiguousSelectorException>(
            () => svc.FindSingleElementAsync(uiTarget, new UiSelector { Query = "Child" }, CancellationToken.None));
        StringAssert.Contains(ex.Message, "matched");
    }

    // -----------------------------------------------------------------------------
    // GetProperties
    // -----------------------------------------------------------------------------

    [TestMethod]
    public async Task GetPropertiesAsync_ReturnsRealProperties()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var check = await ResolveAsync(svc, uiTarget, "chkToggle");

        var props = await svc.GetPropertiesAsync(uiTarget, check, null, CancellationToken.None);

        Assert.AreEqual("CheckBox", props["ControlType"]);
        Assert.AreEqual("Toggle Me", props["Name"]);
        Assert.AreEqual("chkToggle", props["AutomationId"]);
        Assert.AreEqual("Off", props["ToggleState"]);
        Assert.IsTrue((bool)props["IsEnabled"]!);
    }

    [TestMethod]
    public async Task GetPropertiesAsync_SpecificProperty_ReturnsOnlyThat()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var btn = await ResolveAsync(svc, uiTarget, "btnInvoke");

        var props = await svc.GetPropertiesAsync(uiTarget, btn, "Name", CancellationToken.None);

        Assert.AreEqual(1, props.Count);
        Assert.AreEqual("Click Me", props["Name"]);
    }

    [TestMethod]
    public async Task GetPropertiesAsync_UnknownProperty_ReturnsNull()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var btn = await ResolveAsync(svc, uiTarget, "btnInvoke");

        var props = await svc.GetPropertiesAsync(uiTarget, btn, "NoSuchProperty", CancellationToken.None);

        Assert.AreEqual(1, props.Count);
        Assert.IsNull(props["NoSuchProperty"]);
    }

    [TestMethod]
    public async Task GetPropertiesAsync_ScrollableContainer_ReportsScrollProps()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var panel = await ResolveAsync(svc, uiTarget, "pnlScroll");

        var props = await svc.GetPropertiesAsync(uiTarget, panel, null, CancellationToken.None);

        Assert.IsTrue(props.ContainsKey("VerticallyScrollable"));
        Assert.IsTrue(props.ContainsKey("ScrollVerticalPercent"));
        Assert.AreEqual(0.0,
            Convert.ToDouble(props["ScrollVerticalPercent"], System.Globalization.CultureInfo.InvariantCulture),
            0.001,
            "panel should start scrolled to the top");
    }

}

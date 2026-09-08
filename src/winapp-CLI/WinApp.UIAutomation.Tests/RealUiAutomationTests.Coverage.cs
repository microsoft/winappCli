// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Reflection;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

public partial class RealUiAutomationTests
{
    [TestMethod]
    public async Task NullRootPaths_ReturnEmptyNullOrStaleErrors()
    {
        var svc = NewService();
        UiAutomationService.s_getRootElement = (_, _) => null;
        var uiTarget = new UiTarget { ProcessId = int.MaxValue, ProcessName = "missing", WindowTitle = "missing" };
        var element = new UiElement { Id = "dead", Type = "Text", Name = "Dead", AutomationId = "dead" };

        Assert.AreEqual(0, (await svc.InspectAsync(uiTarget, null, 1, CancellationToken.None)).Length);
        Assert.AreEqual(0, (await svc.InspectAncestorsAsync(uiTarget, "dead", CancellationToken.None)).Length);
        Assert.AreEqual(0, (await svc.SearchAsync(uiTarget, new UiSelector { Query = "dead" }, 5, CancellationToken.None)).Length);
        Assert.IsNull(await svc.FindSingleElementAsync(uiTarget, new UiSelector { Query = "dead" }, CancellationToken.None));
        Assert.AreEqual("Dead", (await svc.GetPropertiesAsync(uiTarget, element, "Name", CancellationToken.None))["Name"]);

        foreach (var ex in new[]
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => svc.InvokeAsync(uiTarget, element, CancellationToken.None)),
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => svc.SetValueAsync(uiTarget, element, "x", CancellationToken.None)),
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => svc.FocusAsync(uiTarget, element, CancellationToken.None)),
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => svc.GetTextAsync(uiTarget, element, CancellationToken.None)),
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => svc.ScrollIntoViewAsync(uiTarget, element, CancellationToken.None)),
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => svc.ScrollContainerAsync(uiTarget, element, "down", null, CancellationToken.None)),
        })
        {
            StringAssert.Contains(ex.Message, "stale");
        }
    }

    [TestMethod]
    public void TryGetWindowRect_InvalidNonZeroHwnd_ReturnsFalse()
    {
        var svc = NewService();

        var ok = svc.TryGetWindowRect(123, out var rect);

        Assert.IsFalse(ok);
        Assert.AreEqual(default, rect);
    }

    [TestMethod]
    public async Task SlugScopedInspectAndAncestors_ResolveListItemAndDetectStaleHash()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var tree = await svc.InspectAsync(uiTarget, "lstItems", 3, CancellationToken.None);
        var item = tree.First(e => e.Type == "ListItem" && e.Name == "Item 04" && e.Selector is not null);

        var scoped = await svc.InspectAsync(uiTarget, item.Selector, 0, CancellationToken.None);
        var ancestors = await svc.InspectAncestorsAsync(uiTarget, item.Selector!, CancellationToken.None);
        var replacementHash = item.Selector!.EndsWith("ffff", StringComparison.Ordinal) ? "0000" : "ffff";
        var badSlug = item.Selector[..^4] + replacementHash;
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.FindSingleElementAsync(uiTarget, new UiSelector { Slug = badSlug }, CancellationToken.None));

        Assert.AreEqual(1, scoped.Length);
        Assert.AreEqual("Item 04", scoped[0].Name);
        Assert.IsTrue(ancestors.Any(a => a.AutomationId == "lstItems"), "ancestor chain should include the list");
        Assert.AreEqual("Item 04", ancestors.Last().Name);
        StringAssert.Contains(ex.Message, "RuntimeId hash");
    }

    [TestMethod]
    public async Task ManualTreeFallbackSeam_ReturnsRealRootForSearchAndFindSingle()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        UiAutomationService.s_manualTreeSearch = (_, root, query, maxResults) =>
        {
            Assert.AreEqual("manual-only", query);
            Assert.IsTrue(maxResults > 0);
            return [root];
        };
        UiAutomationService.s_findInvokableAncestor = (_, _, root) => root;

        var results = await svc.SearchAsync(uiTarget, new UiSelector { Query = "manual-only" }, 3, CancellationToken.None);
        var single = await svc.FindSingleElementAsync(uiTarget, new UiSelector { Query = "manual-only" }, CancellationToken.None);

        Assert.AreEqual(1, results.Length);
        Assert.AreEqual("Window", results[0].Type);
        Assert.IsNotNull(results[0].InvokableAncestor);
        Assert.IsNotNull(single);
        Assert.AreEqual("Window", single!.Type);
    }

    [TestMethod]
    public async Task OtherWindowSubstringDisambiguation_PrefersOnlyInvokableMatch()
    {
        using var fx = new UiaTestFixture();
        var (_, title) = fx.OpenOwnedWindow("Owned_" + Guid.NewGuid().ToString("N")[..6]);
        var svc = NewService();
        var uiTarget = NonExplicitSession(fx);

        var found = await PollFindOtherWindowAsync(svc, uiTarget, "Owned Shared Widget");

        Assert.AreEqual("btnOwnedShared", found.AutomationId);
        Assert.AreNotEqual(fx.Hwnd, (nint)found.WindowHandle!.Value);
        StringAssert.StartsWith(title, "Owned_");
    }

    [TestMethod]
    public async Task InvokeAndProperties_TreeItemsUseExpandCollapseStates()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var tree = await svc.InspectAsync(uiTarget, "treeView", 3, CancellationToken.None);
        var root = tree.First(e => e.Type == "TreeItem" && e.Name == "Root");
        var leaf = tree.First(e => e.Type == "TreeItem" && e.Name == "Leaf");

        var pattern = await svc.InvokeAsync(uiTarget, root, CancellationToken.None);
        var rootProps = await svc.GetPropertiesAsync(uiTarget, root, "ExpandCollapseState", CancellationToken.None);
        var leafProps = await svc.GetPropertiesAsync(uiTarget, leaf, "ExpandCollapseState", CancellationToken.None);

        Assert.IsFalse(string.IsNullOrEmpty(pattern), "tree item invocation should report the UIA pattern used");
        Assert.IsTrue(rootProps["ExpandCollapseState"] is "Expanded" or "Collapsed");
        Assert.IsTrue(leafProps.ContainsKey("ExpandCollapseState"));
    }

    [TestMethod]
    public async Task GetTextAsync_UncheckedCheckBox_ReturnsOff()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var box = await ResolveAsync(svc, uiTarget, "chkToggle");

        var text = await svc.GetTextAsync(uiTarget, box, CancellationToken.None);

        Assert.AreEqual("Off", text);
    }

    [TestMethod]
    public async Task ScrollContainerAsync_InvalidAxisRequests_ReportSpecificHints()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var vertical = await ResolveAsync(svc, uiTarget, "pnlScroll");
        var horizontal = await ResolveAsync(svc, uiTarget, "pnlHScroll");

        var toEx = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.ScrollContainerAsync(uiTarget, horizontal, null, "bottom", CancellationToken.None));
        var rightEx = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.ScrollContainerAsync(uiTarget, vertical, "right", null, CancellationToken.None));
        var leftEx = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.ScrollContainerAsync(uiTarget, vertical, "left", null, CancellationToken.None));

        StringAssert.Contains(toEx.Message, "cannot scroll vertically");
        StringAssert.Contains(rightEx.Message, "cannot scroll horizontally");
        StringAssert.Contains(leftEx.Message, "try --direction up");
    }

    [TestMethod]
    public async Task GetFocusedElementAsync_SeamsCoverNativeFailureAndNullArms()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);

        UiAutomationService.s_getFocusedElement = _ => throw new COMException("focus failed");
        Assert.IsNull(await svc.GetFocusedElementAsync(uiTarget, CancellationToken.None));

        UiAutomationService.s_getFocusedElement = _ => null;
        Assert.IsNull(await svc.GetFocusedElementAsync(uiTarget, CancellationToken.None));

        UiAutomationService.s_getFocusedElement = _ =>
            CUIAutomation8.CreateInstance<IUIAutomation>().ElementFromHandle(new HWND(fx.Hwnd));
        UiAutomationService.s_getElementProcessId = _ => throw new COMException("pid failed");
        Assert.IsNull(await svc.GetFocusedElementAsync(uiTarget, CancellationToken.None));
    }

    [TestMethod]
    public async Task FindSingleElementAsync_EmptySelectorAndSlugOtherWindow_ReturnNullOrOtherWindow()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var explicitSession = SessionFor(fx);
        var nonExplicit = NonExplicitSession(fx);
        var expected = new UiElement { Id = "other", Type = "Button", Name = "Other" };
        UiAutomationService.s_findElementOnOtherWindows = (_, _, selector) =>
            selector.IsSlug ? expected : null;

        var empty = await svc.FindSingleElementAsync(explicitSession, new UiSelector(), CancellationToken.None);
        var fromOther = await svc.FindSingleElementAsync(nonExplicit, new UiSelector { Slug = "btn-notthere-0000" }, CancellationToken.None);
        var missing = await svc.FindSingleElementAsync(nonExplicit, new UiSelector { Query = "definitely-not-present" }, CancellationToken.None);

        Assert.IsNull(empty);
        Assert.AreSame(expected, fromOther);
        Assert.IsNull(missing);
    }

    [TestMethod]
    public async Task GetPropertiesAsync_PropertyNameFiltersPresentAndMissingValues()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var box = await ResolveAsync(svc, uiTarget, "txtValue");

        var present = await svc.GetPropertiesAsync(uiTarget, box, "Name", CancellationToken.None);
        var missing = await svc.GetPropertiesAsync(uiTarget, box, "DefinitelyMissing", CancellationToken.None);

        Assert.AreEqual(1, present.Count);
        Assert.IsTrue(present.ContainsKey("Name"));
        Assert.AreEqual("Value", present["Name"]);
        Assert.IsTrue(missing.ContainsKey("DefinitelyMissing"));
        Assert.IsNull(missing["DefinitelyMissing"]);
    }

    [TestMethod]
    public async Task GetTextAsync_ImageWithNoModelName_ReturnsNull()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "picBox");
        var image = new UiElement { Id = "image", Type = "Image", AutomationId = "picBox", Name = "" };

        var text = await svc.GetTextAsync(uiTarget, image, CancellationToken.None);

        Assert.IsNull(text);
    }

    [TestMethod]
    public async Task ScrollErrors_NonScrollableLabelReportsNoSupportedPattern()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var label = await ResolveAsync(svc, uiTarget, "lblText");

        var container = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.ScrollContainerAsync(uiTarget, label, "down", null, CancellationToken.None));

        StringAssert.Contains(container.Message, "ancestors do not support ScrollPattern");
    }

    [TestMethod]
    public async Task InspectAsync_NonExplicitSessionSkipsElementsAlreadyInMainTree()
    {
        using var fx = new UiaTestFixture();
        var logger = new CapturingLogger<UiAutomationService>();
        var svc = new UiAutomationService(logger, new UiSelectorParser());
        var uiTarget = NonExplicitSession(fx);
        var childHwnd = fx.OnUiThread(() => (nint)fx.InvokeButton.Handle);
        UiAutomationService.s_getAllAppWindows = (_, _) => [(fx.Hwnd, fx.ProcessId, fx.Title), (childHwnd, fx.ProcessId, "child")];

        var elements = await svc.InspectAsync(uiTarget, null, 1, CancellationToken.None);

        Assert.IsTrue(elements.Any(e => e.AutomationId == "btnInvoke"));
        Assert.IsTrue(logger.Has(Microsoft.Extensions.Logging.LogLevel.Debug, "already in main window tree"));
    }

    [TestMethod]
    public async Task PidOnlySessionWithMultipleWindowsFallsBackToLargestBounds()
    {
        using var fx = new UiaTestFixture();
        fx.OpenOwnedWindow("LargestFallback_" + Guid.NewGuid().ToString("N")[..6]);
        var svc = NewService();
        var uiTarget = PidOnlySession(fx);
        uiTarget.WindowTitle = "12345";

        var elements = await svc.InspectAsync(uiTarget, null, 0, CancellationToken.None);

        Assert.IsTrue(elements.Length > 0);
        Assert.IsTrue(elements.Any(e => e.Name == fx.Title), "PID-only largest fallback should inspect the fixture window tree");
    }

    [TestMethod]
    public async Task NamelessSlug_ResolvesPrefixHashSelector()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var tree = await svc.InspectAsync(uiTarget, null, 2, CancellationToken.None);
        var nameless = tree.First(e => e.Type == "Pane" && e.Name is null && e.AutomationId is null && e.Selector is not null);

        var found = await svc.FindSingleElementAsync(uiTarget, new UiSelector { Slug = nameless.Selector }, CancellationToken.None);

        Assert.IsNotNull(found);
        Assert.AreEqual(nameless.Selector, found!.Selector);
        Assert.IsNull(found.Name);
    }

    [TestMethod]
    public async Task OwnedWindowOnlyButton_ExercisesOwnedEnumerationAndOtherWindowSlug()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = NonExplicitSession(fx);
        var (_, title) = fx.OpenOwnedWindow("OwnedOnly_" + Guid.NewGuid().ToString("N")[..6], ownedByMain: true);

        var byName = await PollFindOtherWindowAsync(svc, uiTarget, "OwnedOnly");
        var bySearch = (await PollSearchAsync(svc, uiTarget, "btnOwnedOnly")).Single(e => e.AutomationId == "btnOwnedOnly");
        var missingSlug = await svc.FindSingleElementAsync(uiTarget, new UiSelector { Slug = "btn-definitely-missing-0000" }, CancellationToken.None);

        Assert.AreEqual("btnOwnedOnly", byName.AutomationId);
        Assert.AreEqual("btnOwnedOnly", bySearch.AutomationId);
        Assert.IsTrue(bySearch.WindowHandle.GetValueOrDefault() != 0);
        Assert.IsNull(missingSlug);
        StringAssert.StartsWith(title, "OwnedOnly_");
    }

    [TestMethod]
    public async Task FindSingleElementAsync_LabelInsideButtonSurfacesInvokableAncestor()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);

        var found = await svc.FindSingleElementAsync(uiTarget, new UiSelector { Query = "Inside Invoke" }, CancellationToken.None);

        Assert.IsNotNull(found);
        Assert.AreEqual("lblInsideInvoke", found!.AutomationId);
        Assert.IsNotNull(found.InvokableAncestor);
        Assert.AreEqual("btnParentInvoke", found.InvokableAncestor!.AutomationId);

        var bySlug = await svc.FindSingleElementAsync(uiTarget, new UiSelector { Slug = found.Selector! }, CancellationToken.None);
        Assert.IsNotNull(bySlug!.InvokableAncestor);
        Assert.AreEqual("btnParentInvoke", bySlug.InvokableAncestor!.AutomationId);
    }

    [TestMethod]
    public async Task SearchAsync_LabelInsideButtonSurfacesInvokableAncestor()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);

        var results = await svc.SearchAsync(uiTarget, new UiSelector { Query = "Inside Invoke" }, 5, CancellationToken.None);
        var label = results.Single(e => e.AutomationId == "lblInsideInvoke");

        Assert.IsNotNull(label.InvokableAncestor);
        Assert.AreEqual("btnParentInvoke", label.InvokableAncestor!.AutomationId);
    }

    [TestMethod]
    public async Task MalformedSlugSelector_ReturnsStaleElementError()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var malformed = new UiElement { Id = "bad-slug", Type = "Button", Selector = "not-a-valid-slug" };

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.GetTextAsync(uiTarget, malformed, CancellationToken.None));

        StringAssert.Contains(ex.Message, "stale");
    }

    [TestMethod]
    public async Task InvalidStoredHwndFallsBackAndReturnsEmpty()
    {
        var logger = new CapturingLogger<UiAutomationService>();
        var svc = new UiAutomationService(logger, new UiSelectorParser());
        var uiTarget = new UiTarget
        {
            ProcessId = int.MaxValue,
            ProcessName = "missing",
            WindowHandle = 123,
            IsExplicitWindow = true,
        };

        var tree = await svc.InspectAsync(uiTarget, null, 1, CancellationToken.None);

        Assert.AreEqual(0, tree.Length);
        Assert.IsTrue(logger.Has(Microsoft.Extensions.Logging.LogLevel.Debug, "Stored HWND 123 failed"));
    }

    [TestMethod]
    public async Task GetTextAsync_ComboAndTabUseValueAndSelectionPatterns()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var combo = await ResolveAsync(svc, uiTarget, "cboSelect");
        var tabs = await ResolveAsync(svc, uiTarget, "tabMain");

        var comboText = await svc.GetTextAsync(uiTarget, combo, CancellationToken.None);
        var tabText = await svc.GetTextAsync(uiTarget, tabs, CancellationToken.None);

        Assert.AreEqual("Beta", comboText);
        Assert.AreEqual("One", tabText);
    }

    [TestMethod]
    public async Task ScrollIntoViewAsync_TopLevelButtonWithoutScrollableAncestorThrows()
    {
        var svc = NewService();
        var uiTarget = new UiTarget { ProcessId = Environment.ProcessId, ProcessName = "fake" };
        var model = new UiElement { Id = "no-scroll", Type = "Text", AutomationId = "noScroll" };
        var target = ComProxy<IUIAutomationElement>((method, _) => method.Name switch
        {
            "get_CurrentBoundingRectangle" => new RECT { left = 0, top = 0, right = 10, bottom = 10 },
            "GetCurrentPattern" => ThrowCom(),
            _ => ThrowCom(),
        });
        var root = ComProxy<IUIAutomationElement>((method, _) => method.Name == "FindFirst" ? target : ThrowCom());
        UiAutomationService.s_getRootElement = (_, _) => root;

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.ScrollIntoViewAsync(uiTarget, model, CancellationToken.None));

        StringAssert.Contains(ex.Message, "does not support ScrollItemPattern and no scrollable ancestor found");
    }

    [TestMethod]
    public async Task ScrollContainerAsync_ChildButtonWalksUpToScrollablePanel()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var child = await ResolveAsync(svc, uiTarget, "pnlChild18");
        var before = await VerticalPercentAsync(svc, uiTarget, "pnlScroll");

        await svc.ScrollContainerAsync(uiTarget, child, "down", null, CancellationToken.None);

        await WaitForAsync(async () => await VerticalPercentAsync(svc, uiTarget, "pnlScroll") > before,
            "scrolling a panel child should move its scrollable ancestor");
    }

    [TestMethod]
    public async Task OtherWindowSearch_ComFailureIsLoggedAndIgnored()
    {
        using var fx = new UiaTestFixture();
        var logger = new CapturingLogger<UiAutomationService>();
        var svc = new UiAutomationService(logger, new UiSelectorParser());
        var uiTarget = NonExplicitSession(fx);
        var otherHwnd = fx.Hwnd + 1000;
        UiAutomationService.s_getAllAppWindows = (_, _) => [(fx.Hwnd, fx.ProcessId, fx.Title), (otherHwnd, fx.ProcessId, "faulty")];
        UiAutomationService.s_getRootElementForHwnd = (_, hwnd) =>
            hwnd == otherHwnd ? throw new COMException("simulated HWND failure") : null;

        var search = await svc.SearchAsync(uiTarget, new UiSelector { Query = "not-on-main-window" }, 5, CancellationToken.None);
        var single = await svc.FindSingleElementAsync(uiTarget, new UiSelector { Query = "not-on-main-window" }, CancellationToken.None);

        Assert.AreEqual(0, search.Length);
        Assert.IsNull(single);
        Assert.IsTrue(logger.Has(Microsoft.Extensions.Logging.LogLevel.Debug, "simulated HWND failure"));
    }

    [TestMethod]
    public async Task FaultInjectedComProxies_CoverPatternBranches()
    {
        var logger = new CapturingLogger<UiAutomationService>();
        var svc = new UiAutomationService(logger, new UiSelectorParser());
        var uiTarget = new UiTarget { ProcessId = Environment.ProcessId, ProcessName = "fake", WindowHandle = 0 };
        var model = new UiElement { Id = "fake", Type = "Custom", AutomationId = "fakeAid", Selector = null };
        var rect = new RECT { left = 1, top = 2, right = 11, bottom = 12 };

        var toggle = ComProxy<IUIAutomationTogglePattern>((method, _) =>
            method.Name == "get_CurrentToggleState" ? (ToggleState)999 : ThrowCom());
        var expand = ComProxy<IUIAutomationExpandCollapsePattern>((method, _) =>
        {
            if (method.Name == "Expand") { return null; }
            return method.Name == "get_CurrentExpandCollapseState" ? (ExpandCollapseState)999 : ThrowCom();
        });
        var scrollItem = ComProxy<IUIAutomationScrollItemPattern>((method, _) =>
            method.Name == "ScrollIntoView" ? null : ThrowCom());

        var target = ComProxy<IUIAutomationElement>((method, args) =>
        {
            if (method.Name == "GetCurrentPattern")
            {
                var id = (UIA_PATTERN_ID)args![0]!;
                if (id == UIA_PATTERN_ID.UIA_TogglePatternId) { return toggle; }
                if (id == UIA_PATTERN_ID.UIA_ExpandCollapsePatternId) { return expand; }
                if (id == UIA_PATTERN_ID.UIA_ScrollItemPatternId) { return scrollItem; }
                throw new COMException("pattern unavailable");
            }
            return method.Name switch
            {
                "get_CurrentBoundingRectangle" => rect,
                "get_CurrentName" => EmptyBstr(),
                "get_CurrentAutomationId" => EmptyBstr(),
                "get_CurrentClassName" => EmptyBstr(),
                "get_CurrentControlType" => UIA_CONTROLTYPE_ID.UIA_CustomControlTypeId,
                "get_CurrentIsEnabled" => new BOOL(true),
                "get_CurrentIsOffscreen" => new BOOL(false),
                "SetFocus" => null,
                _ => ThrowCom(),
            };
        });
        var root = ComProxy<IUIAutomationElement>((method, _) => method.Name == "FindFirst" ? target : ThrowCom());
        UiAutomationService.s_getRootElement = (_, _) => root;

        var invokePattern = await svc.InvokeAsync(uiTarget, model, CancellationToken.None);
        var props = await svc.GetPropertiesAsync(uiTarget, model, null, CancellationToken.None);
        await svc.ScrollIntoViewAsync(uiTarget, model, CancellationToken.None);

        Assert.AreEqual("ExpandCollapsePattern", invokePattern);
        Assert.AreEqual("999", props["ToggleState"]);
        Assert.AreEqual("999", props["ExpandCollapseState"]);
        Assert.IsTrue(logger.Has(Microsoft.Extensions.Logging.LogLevel.Warning, "Element position unchanged"));
    }

    [TestMethod]
    public async Task FaultInjectedComProxies_CoverAmbiguousPatternCatchesAndFallbackSlug()
    {
        var svc = NewService();
        var uiTarget = new UiTarget { ProcessId = Environment.ProcessId, ProcessName = "fake" };
        IUIAutomationElement MakeMatch(string name) => ComProxy<IUIAutomationElement>((method, _) => method.Name switch
        {
            "get_CurrentBoundingRectangle" => new RECT { left = 1, top = 2, right = 11, bottom = 12 },
            "get_CurrentName" => StringBstr(name),
            "get_CurrentAutomationId" => EmptyBstr(),
            "get_CurrentClassName" => EmptyBstr(),
            "get_CurrentControlType" => UIA_CONTROLTYPE_ID.UIA_TextControlTypeId,
            "GetRuntimeId" => ThrowCom(),
            "GetCurrentPattern" => ThrowCom(),
            _ => ThrowCom(),
        });
        var first = MakeMatch("Ambiguous Proxy");
        var second = MakeMatch("Ambiguous Proxy");
        var matches = ComProxy<IUIAutomationElementArray>((method, args) => method.Name switch
        {
            "get_Length" => 2,
            "GetElement" => (int)args![0]! == 0 ? first : second,
            _ => ThrowCom(),
        });
        var root = ComProxy<IUIAutomationElement>((method, _) => method.Name switch
        {
            "FindFirst" => null,
            "FindAll" => matches,
            _ => ThrowCom(),
        });
        UiAutomationService.s_getRootElement = (_, _) => root;

        var ex = await Assert.ThrowsExactlyAsync<UiAmbiguousSelectorException>(
            () => svc.FindSingleElementAsync(uiTarget, new UiSelector { Query = "Ambiguous" }, CancellationToken.None));

        StringAssert.Contains(ex.Message, "lbl[0]");
    }

    [TestMethod]
    public async Task FaultInjectedComProxies_CoverExpandCollapsePropertyStates()
    {
        var svc = NewService();
        var uiTarget = new UiTarget { ProcessId = Environment.ProcessId, ProcessName = "fake" };
        var model = new UiElement { Id = "fake", Type = "Custom", AutomationId = "fakeAid" };
        foreach (var (state, expected) in new[]
        {
            (ExpandCollapseState.ExpandCollapseState_Expanded, "Expanded"),
            (ExpandCollapseState.ExpandCollapseState_PartiallyExpanded, "PartiallyExpanded"),
            (ExpandCollapseState.ExpandCollapseState_LeafNode, "LeafNode"),
        })
        {
            var expand = ComProxy<IUIAutomationExpandCollapsePattern>((method, _) =>
                method.Name == "get_CurrentExpandCollapseState" ? state : ThrowCom());
            var target = ComProxy<IUIAutomationElement>((method, args) =>
            {
                if (method.Name == "GetCurrentPattern" && (UIA_PATTERN_ID)args![0]! == UIA_PATTERN_ID.UIA_ExpandCollapsePatternId)
                {
                    return expand;
                }
                return method.Name == "FindFirst" ? null : ThrowCom();
            });
            var root = ComProxy<IUIAutomationElement>((method, _) => method.Name == "FindFirst" ? target : ThrowCom());
            UiAutomationService.s_getRootElement = (_, _) => root;

            var props = await svc.GetPropertiesAsync(uiTarget, model, "ExpandCollapseState", CancellationToken.None);
            Assert.AreEqual(expected, props["ExpandCollapseState"]);
        }
    }

    [TestMethod]
    public async Task FaultInjectedComProxies_CoverPromoteFailureAndPatternCatches()
    {
        var logger = new CapturingLogger<UiAutomationService>();
        var svc = new UiAutomationService(logger, new UiSelectorParser());
        var uiTarget = new UiTarget { ProcessId = Environment.ProcessId, ProcessName = "fake", WindowHandle = 111 };
        var target = ComProxy<IUIAutomationElement>((method, _) => method.Name switch
        {
            "get_CurrentBoundingRectangle" => new RECT { left = 1, top = 2, right = 31, bottom = 42 },
            "get_CurrentName" => StringBstr("Proxy Target"),
            "get_CurrentAutomationId" => StringBstr("proxyAid"),
            "get_CurrentClassName" => StringBstr("ProxyClass"),
            "get_CurrentControlType" => UIA_CONTROLTYPE_ID.UIA_CustomControlTypeId,
            "get_CurrentIsEnabled" => new BOOL(true),
            "get_CurrentIsOffscreen" => new BOOL(false),
            "GetRuntimeId" => ThrowCom(),
            "GetCurrentPattern" => ThrowCom(),
            _ => ThrowCom(),
        });
        var array = ComProxy<IUIAutomationElementArray>((method, _) => method.Name switch
        {
            "get_Length" => 1,
            "GetElement" => target,
            _ => ThrowCom(),
        });
        var findAllCalls = 0;
        var root = ComProxy<IUIAutomationElement>((method, _) =>
        {
            if (method.Name == "FindAll")
            {
                findAllCalls++;
                return findAllCalls == 1 ? array : throw new COMException("uniqueness failed");
            }
            return ThrowCom();
        });
        UiAutomationService.s_getRootElement = (_, _) => root;

        var results = await svc.SearchAsync(uiTarget, new UiSelector { Query = "proxyAid" }, 5, CancellationToken.None);

        Assert.AreEqual(1, results.Length);
        Assert.AreEqual("proxyAid", results[0].AutomationId);
        Assert.IsNull(results[0].Selector);
        Assert.IsTrue(logger.Has(Microsoft.Extensions.Logging.LogLevel.Debug, "uniqueness failed"));
    }

    [TestMethod]
    public async Task FaultInjectedComProxies_CoverPromoteInnerElementFailure()
    {
        var svc = NewService();
        var uiTarget = new UiTarget { ProcessId = Environment.ProcessId, ProcessName = "fake", WindowHandle = 222 };
        var target = ComProxy<IUIAutomationElement>((method, _) => method.Name switch
        {
            "get_CurrentBoundingRectangle" => new RECT { left = 1, top = 2, right = 31, bottom = 42 },
            "get_CurrentName" => StringBstr("Promote Target"),
            "get_CurrentAutomationId" => StringBstr("promoteAid"),
            "get_CurrentClassName" => EmptyBstr(),
            "get_CurrentControlType" => UIA_CONTROLTYPE_ID.UIA_ButtonControlTypeId,
            "get_CurrentIsEnabled" => new BOOL(true),
            "get_CurrentIsOffscreen" => new BOOL(false),
            "GetRuntimeId" => ThrowCom(),
            "GetCurrentPattern" => ThrowCom(),
            _ => ThrowCom(),
        });
        var exact = ComProxy<IUIAutomationElementArray>((method, _) => method.Name switch
        {
            "get_Length" => 1,
            "GetElement" => target,
            _ => ThrowCom(),
        });
        var all = ComProxy<IUIAutomationElementArray>((method, args) => method.Name switch
        {
            "get_Length" => 2,
            "GetElement" => (int)args![0]! == 0 ? ThrowCom() : target,
            _ => ThrowCom(),
        });
        var findAllCalls = 0;
        var root = ComProxy<IUIAutomationElement>((method, _) =>
        {
            if (method.Name == "FindAll") { return ++findAllCalls == 1 ? exact : all; }
            return ThrowCom();
        });
        UiAutomationService.s_getRootElement = (_, _) => root;

        var results = await svc.SearchAsync(uiTarget, new UiSelector { Query = "promoteAid" }, 5, CancellationToken.None);

        Assert.AreEqual("promoteAid", results.Single().Selector);
    }

    [TestMethod]
    public async Task NativeSeams_CoverRootElementFallbacks()
    {
        var logger = new CapturingLogger<UiAutomationService>();
        var svc = new UiAutomationService(logger, new UiSelectorParser());
        var uiTarget = new UiTarget { ProcessId = Environment.ProcessId, ProcessName = "fake", WindowHandle = 0 };
        var target = ComProxy<IUIAutomationElement>((method, _) => method.Name switch
        {
            "get_CurrentBoundingRectangle" => new RECT { left = 1, top = 2, right = 101, bottom = 82 },
            "get_CurrentName" => StringBstr("Fallback Window"),
            "get_CurrentAutomationId" => StringBstr("fallbackWindow"),
            "get_CurrentClassName" => EmptyBstr(),
            "get_CurrentControlType" => UIA_CONTROLTYPE_ID.UIA_WindowControlTypeId,
            "get_CurrentIsEnabled" => new BOOL(true),
            "get_CurrentIsOffscreen" => new BOOL(false),
            "GetRuntimeId" => ThrowCom(),
            "GetCurrentPattern" => ThrowCom(),
            _ => ThrowCom(),
        });
        var one = ComProxy<IUIAutomationElementArray>((method, _) => method.Name switch
        {
            "get_Length" => 1,
            "GetElement" => target,
            _ => ThrowCom(),
        });
        var empty = ComProxy<IUIAutomationElementArray>((method, _) => method.Name switch
        {
            "get_Length" => 0,
            _ => ThrowCom(),
        });

        UiAutomationService.s_getDesktopRootElement = _ => null;
        Assert.AreEqual(0, (await svc.InspectAsync(uiTarget, null, 0, CancellationToken.None)).Length);

        UiAutomationService.s_getDesktopRootElement = _ =>
            ComProxy<IUIAutomationElement>((method, _) => method.Name == "FindAll" ? one : ThrowCom());
        var single = await svc.InspectAsync(uiTarget, null, 0, CancellationToken.None);
        Assert.AreEqual("Fallback Window", single.Single().Name);

        UiAutomationService.s_getDesktopRootElement = _ =>
            ComProxy<IUIAutomationElement>((method, _) => method.Name == "FindAll" ? empty : ThrowCom());
        UiAutomationService.s_getMainWindowHandleForProcessId = _ => 456;
        UiAutomationService.s_elementFromHandle = (_, hwnd) => hwnd == 456 ? target : throw new COMException("bad hwnd");
        var fallback = await svc.InspectAsync(uiTarget, null, 0, CancellationToken.None);
        Assert.AreEqual("Fallback Window", fallback.Single().Name);

        var otherWindowElement = new UiElement { Id = "other", Type = "Button", WindowHandle = 999, AutomationId = "missing" };
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.GetTextAsync(uiTarget, otherWindowElement, CancellationToken.None));
        StringAssert.Contains(ex.Message, "stale");
        Assert.IsTrue(logger.Has(Microsoft.Extensions.Logging.LogLevel.Debug, "ElementFromHandle found"));
    }

    [TestMethod]
    public async Task FaultInjectedComProxies_CoverDirectSelectionItemProperties()
    {
        var svc = NewService();
        var uiTarget = new UiTarget { ProcessId = Environment.ProcessId, ProcessName = "fake" };
        var model = new UiElement { Id = "sel", Type = "ListItem", AutomationId = "selAid" };
        var target = ComProxy<ISelectionItemElement>((method, _) => method.Name switch
        {
            "get_CurrentIsSelected" => new BOOL(true),
            _ => ThrowCom(),
        });
        var root = ComProxy<IUIAutomationElement>((method, _) => method.Name == "FindFirst" ? target : ThrowCom());
        UiAutomationService.s_getRootElement = (_, _) => root;

        var props = await svc.GetPropertiesAsync(uiTarget, model, "IsSelected", CancellationToken.None);

        Assert.AreEqual(true, props["IsSelected"]);
    }

    [TestMethod]
    public async Task FaultInjectedComProxies_CoverTextInvokeAndScrollVariants()
    {
        var svc = NewService();
        var uiTarget = new UiTarget { ProcessId = Environment.ProcessId, ProcessName = "fake" };
        var model = new UiElement { Id = "fake", Type = "Custom", AutomationId = "fakeAid" };
        var selected = ComProxy<IUIAutomationElement>((method, _) =>
            method.Name == "get_CurrentName" ? StringBstr("Selected From Proxy") : ThrowCom());
        var selection = ComProxy<IUIAutomationElementArray>((method, _) => method.Name switch
        {
            "get_Length" => 1,
            "GetElement" => selected,
            _ => ThrowCom(),
        });
        var selectionPattern = ComProxy<IUIAutomationSelectionPattern>((method, _) =>
            method.Name == "GetCurrentSelection" ? selection : ThrowCom());
        var selectionItemPattern = ComProxy<IUIAutomationSelectionItemPattern>((method, _) =>
            method.Name == "Select" ? null : ThrowCom());
        var togglePattern = ComProxy<IUIAutomationTogglePattern>((method, _) =>
        {
            if (method.Name == "Toggle") { return null; }
            return method.Name == "get_CurrentToggleState" ? ToggleState.ToggleState_Off : ThrowCom();
        });
        var scrollPattern = ComProxy<IUIAutomationScrollPattern>((method, args) => method.Name switch
        {
            "get_CurrentVerticallyScrollable" => new global::Windows.Win32.Foundation.BOOL(false),
            "get_CurrentHorizontallyScrollable" => new global::Windows.Win32.Foundation.BOOL(true),
            "get_CurrentVerticalScrollPercent" => 30.0,
            "get_CurrentHorizontalScrollPercent" => 40.0,
            "SetScrollPercent" => null,
            _ => ThrowCom(),
        });

        IUIAutomationElement MakeTarget(Func<UIA_PATTERN_ID, object> patternFor) => ComProxy<IUIAutomationElement>((method, args) =>
        {
            if (method.Name == "GetCurrentPattern") { return patternFor((UIA_PATTERN_ID)args![0]!); }
            return method.Name switch
            {
                "get_CurrentBoundingRectangle" => new RECT { left = 0, top = 0, right = 10, bottom = 10 },
                "get_CurrentName" => EmptyBstr(),
                "get_CurrentAutomationId" => EmptyBstr(),
                "get_CurrentClassName" => EmptyBstr(),
                "get_CurrentControlType" => UIA_CONTROLTYPE_ID.UIA_CustomControlTypeId,
                "get_CurrentIsEnabled" => true,
                "get_CurrentIsOffscreen" => false,
                _ => ThrowCom(),
            };
        });

        IUIAutomationElement? current = null;
        UiAutomationService.s_getRootElement = (_, _) =>
            ComProxy<IUIAutomationElement>((method, _) => method.Name == "FindFirst" ? current! : ThrowCom());

        current = MakeTarget(id => id == UIA_PATTERN_ID.UIA_TogglePatternId ? togglePattern : ThrowCom());
        Assert.AreEqual("TogglePattern", await svc.InvokeAsync(uiTarget, model, CancellationToken.None));
        Assert.AreEqual("Off", await svc.GetTextAsync(uiTarget, model, CancellationToken.None));

        current = MakeTarget(id => id == UIA_PATTERN_ID.UIA_SelectionItemPatternId ? selectionItemPattern : ThrowCom());
        Assert.AreEqual("SelectionItemPattern", await svc.InvokeAsync(uiTarget, model, CancellationToken.None));

        current = MakeTarget(id => id == UIA_PATTERN_ID.UIA_SelectionPatternId ? selectionPattern : ThrowCom());
        Assert.AreEqual("Selected From Proxy", await svc.GetTextAsync(uiTarget, model, CancellationToken.None));

        current = MakeTarget(id => id == UIA_PATTERN_ID.UIA_ScrollPatternId ? scrollPattern : ThrowCom());
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.ScrollContainerAsync(uiTarget, model, "up", null, CancellationToken.None));
        StringAssert.Contains(ex.Message, "try --direction left");
    }

    [TestMethod]
    public async Task FaultInjectedComProxies_CoverEmptyTextAndValueFallthrough()
    {
        var svc = NewService();
        var uiTarget = new UiTarget { ProcessId = Environment.ProcessId, ProcessName = "fake" };
        var model = new UiElement { Id = "text-fallback", Type = "Custom", AutomationId = "textAid", Name = "Fallback Name" };
        var textRange = ComProxy<IUIAutomationTextRange>((method, _) =>
            method.Name == "GetText" ? EmptyBstr() : ThrowCom());
        var textPattern = ComProxy<IUIAutomationTextPattern>((method, _) =>
            method.Name == "get_DocumentRange" ? textRange : ThrowCom());
        var valuePattern = ComProxy<IUIAutomationValuePattern>((method, _) =>
            method.Name == "get_CurrentValue" ? EmptyBstr() : ThrowCom());
        var selected = ComProxy<IUIAutomationElement>((method, _) =>
            method.Name == "get_CurrentName" ? EmptyBstr() : ThrowCom());
        var selection = ComProxy<IUIAutomationElementArray>((method, _) => method.Name switch
        {
            "get_Length" => 1,
            "GetElement" => selected,
            _ => ThrowCom(),
        });
        var selectionPattern = ComProxy<IUIAutomationSelectionPattern>((method, _) =>
            method.Name == "GetCurrentSelection" ? selection : ThrowCom());
        var target = ComProxy<IUIAutomationElement>((method, args) =>
        {
            if (method.Name == "GetCurrentPattern")
            {
                return (UIA_PATTERN_ID)args![0]! switch
                {
                    UIA_PATTERN_ID.UIA_TextPatternId => textPattern,
                    UIA_PATTERN_ID.UIA_ValuePatternId => valuePattern,
                    UIA_PATTERN_ID.UIA_SelectionPatternId => selectionPattern,
                    _ => ThrowCom(),
                };
            }
            return ThrowCom();
        });
        var root = ComProxy<IUIAutomationElement>((method, _) => method.Name == "FindFirst" ? target : ThrowCom());
        UiAutomationService.s_getRootElement = (_, _) => root;

        var text = await svc.GetTextAsync(uiTarget, model, CancellationToken.None);

        Assert.AreEqual("Fallback Name", text);
    }

    [TestMethod]
    public async Task FaultInjectedComProxies_CoverToUiElementUnknownToggleAndCollapsedExpand()
    {
        var svc = NewService();
        var uiTarget = new UiTarget { ProcessId = Environment.ProcessId, ProcessName = "fake", WindowHandle = 333 };
        var toggle = ComProxy<IUIAutomationTogglePattern>((method, _) =>
            method.Name == "get_CurrentToggleState" ? (ToggleState)999 : ThrowCom());
        var expand = ComProxy<IUIAutomationExpandCollapsePattern>((method, _) =>
            method.Name == "get_CurrentExpandCollapseState" ? ExpandCollapseState.ExpandCollapseState_Collapsed : ThrowCom());
        var target = ComProxy<IUIAutomationElement>((method, args) =>
        {
            if (method.Name == "GetCurrentPattern")
            {
                return (UIA_PATTERN_ID)args![0]! switch
                {
                    UIA_PATTERN_ID.UIA_TogglePatternId => toggle,
                    UIA_PATTERN_ID.UIA_ExpandCollapsePatternId => expand,
                    _ => ThrowCom(),
                };
            }
            return method.Name switch
            {
                "get_CurrentBoundingRectangle" => new RECT { left = 1, top = 2, right = 31, bottom = 42 },
                "get_CurrentName" => StringBstr("State Proxy"),
                "get_CurrentAutomationId" => StringBstr("stateAid"),
                "get_CurrentClassName" => EmptyBstr(),
                "get_CurrentControlType" => UIA_CONTROLTYPE_ID.UIA_ButtonControlTypeId,
                "get_CurrentIsEnabled" => new BOOL(true),
                "get_CurrentIsOffscreen" => new BOOL(false),
                "GetRuntimeId" => ThrowCom(),
                _ => ThrowCom(),
            };
        });
        var array = ComProxy<IUIAutomationElementArray>((method, _) => method.Name switch
        {
            "get_Length" => 1,
            "GetElement" => target,
            _ => ThrowCom(),
        });
        var root = ComProxy<IUIAutomationElement>((method, _) => method.Name == "FindAll" ? array : ThrowCom());
        UiAutomationService.s_getRootElement = (_, _) => root;

        var result = (await svc.SearchAsync(uiTarget, new UiSelector { Query = "stateAid" }, 1, CancellationToken.None)).Single();

        Assert.IsNull(result.ToggleState);
        Assert.AreEqual("collapsed", result.ExpandState);
    }

    [TestMethod]
    public async Task SetValueAsync_RangeValuePatternSucceedsWhenValuePatternUnavailable()
    {
        var svc = NewService();
        var uiTarget = new UiTarget { ProcessId = Environment.ProcessId, ProcessName = "fake" };
        var model = new UiElement { Id = "range", Type = "Slider", AutomationId = "rangeAid" };
        var setValues = new List<double>();
        var rangePattern = ComProxy<IUIAutomationRangeValuePattern>((method, args) =>
        {
            if (method.Name == "SetValue")
            {
                setValues.Add((double)args![0]!);
                return null;
            }
            return ThrowCom();
        });
        var target = ComProxy<IUIAutomationElement>((method, args) =>
        {
            if (method.Name == "GetCurrentPattern")
            {
                return (UIA_PATTERN_ID)args![0]! == UIA_PATTERN_ID.UIA_RangeValuePatternId
                    ? rangePattern
                    : ThrowCom();
            }
            return ThrowCom();
        });
        var root = ComProxy<IUIAutomationElement>((method, _) => method.Name == "FindFirst" ? target : ThrowCom());
        UiAutomationService.s_getRootElement = (_, _) => root;

        await svc.SetValueAsync(uiTarget, model, "42", CancellationToken.None);

        Assert.AreEqual(42d, setValues.Single());
    }

    [TestMethod]
    public async Task SetValueAsync_LegacyIAccessibleComFailureIsLoggedAndThrows()
    {
        var logger = new CapturingLogger<UiAutomationService>();
        var svc = new UiAutomationService(logger, new UiSelectorParser());
        var uiTarget = new UiTarget { ProcessId = Environment.ProcessId, ProcessName = "fake" };
        var model = new UiElement { Id = "legacy", Type = "Edit", AutomationId = "legacyAid" };
        var legacyPattern = ComProxy<IUIAutomationLegacyIAccessiblePattern>((_, _) => ThrowCom());
        var target = ComProxy<IUIAutomationElement>((method, args) =>
        {
            if (method.Name == "GetCurrentPattern")
            {
                return (UIA_PATTERN_ID)args![0]! == UIA_PATTERN_ID.UIA_LegacyIAccessiblePatternId
                    ? legacyPattern
                    : ThrowCom();
            }
            return ThrowCom();
        });
        var root = ComProxy<IUIAutomationElement>((method, _) => method.Name == "FindFirst" ? target : ThrowCom());
        UiAutomationService.s_getRootElement = (_, _) => root;

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.SetValueAsync(uiTarget, model, "hello", CancellationToken.None));

        StringAssert.Contains(ex.Message, "could not be set via ValuePattern");
        Assert.IsTrue(logger.Has(Microsoft.Extensions.Logging.LogLevel.Debug, "LegacyIAccessible.SetValue failed"));
    }

    private static object ThrowCom() => throw new COMException("simulated COM failure");

    private static unsafe BSTR EmptyBstr() => new((char*)Marshal.StringToBSTR(string.Empty));

    private static unsafe BSTR StringBstr(string value) => new((char*)Marshal.StringToBSTR(value));

    private static T ComProxy<T>(Func<MethodInfo, object?[]?, object?> handler)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, ComDispatchProxy>();
        ((ComDispatchProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private class ComDispatchProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = (_, _) => null;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => Handler(targetMethod!, args);
    }

    private interface ISelectionItemElement : IUIAutomationElement, IUIAutomationSelectionItemPattern
    {
    }
}

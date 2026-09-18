// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Windows.Forms;
using System.Runtime.InteropServices;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;
using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

public partial class RealUiAutomationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExplicitQuery_UnconstrainedSlugBindsSourceAcrossIndependentWindows(bool oppositeWindow)
    {
        using var main = new ExplicitIdentityFixture(duplicate: false);
        using var source = new ExplicitIdentityFixture(duplicate: true);
        var svc = NewService();
        var inspected = (await svc.InspectAsync(source.Target, null, 3, default))
            .Single(element => element.Name == "Secondary Save" && element.Type == "Button");
        Assert.IsNotNull(SlugGenerator.ParseSlug(inspected.Selector!));
        UiAutomationService.s_getAllAppWindows = (_, _) =>
            [((nint)main.Target.WindowHandle, Environment.ProcessId, "Main"),
             ((nint)source.Target.WindowHandle, Environment.ProcessId, "Source")];
        var selector = new UiSelector { Slug = inspected.Selector };
        var target = oppositeWindow ? main.Target : main.AppTarget;

        // Omitted auto keeps its existing same-name/hash-mismatch behavior.
        var legacyError = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            svc.FindSingleElementAsync(target, selector, default));
        StringAssert.Contains(legacyError.Message, "RuntimeId hash");

        var selected = await svc.FindSingleElementAsync(target, selector, requireUnique: true, default);
        if (oppositeWindow)
        {
            Assert.IsNull(selected, "An explicit HWND must not search the source window.");
            Assert.AreEqual(0, source.ClickCount);
        }
        else
        {
            Assert.IsNotNull(selected);
            Assert.AreEqual(source.Target.WindowHandle, selected.WindowHandle);
            Assert.AreEqual(inspected.Selector, selected.Selector);
            Assert.IsNotNull(selected.Context);
            Assert.IsNull(selected.InvokableAncestor);
            Assert.IsTrue(CUIAutomation8.CreateInstance<IUIAutomation>().CompareElements(
                inspected.Context!.AutomationElement, selected.Context.AutomationElement));
            await svc.InvokeAsync(target, selected, UiInvokeAction.Invoke, default);
            await WaitForAsync(() => Task.FromResult(source.SecondaryClicks == 1),
                "The source window's retained provider was not invoked.");
            Assert.AreEqual(1, source.ClickCount);
        }
        Assert.AreEqual(0, main.ClickCount);
        Assert.AreEqual(0, svc.SerializedElementResolutionCount);
    }

    [TestMethod]
    public async Task ExplicitQuery_UnconstrainedSlugPropagatesStaleSourceFailure()
    {
        using var main = new ExplicitIdentityFixture(duplicate: false);
        using var source = new ExplicitIdentityFixture(duplicate: true);
        var svc = NewService();
        var inspected = (await svc.InspectAsync(source.Target, null, 3, default))
            .Single(element => element.Name == "Secondary Save" && element.Type == "Button");
        Assert.IsNotNull(SlugGenerator.ParseSlug(inspected.Selector!));
        UiAutomationService.s_getAllAppWindows = (_, _) =>
            [((nint)main.Target.WindowHandle, Environment.ProcessId, "Main"),
             ((nint)source.Target.WindowHandle, Environment.ProcessId, "Source")];
        var fromHandle = UiAutomationService.s_elementFromHandle;
        var failure = new COMException("Requested source window is stale.", unchecked((int)0x80040201));
        UiAutomationService.s_elementFromHandle = (service, hwnd) =>
            hwnd == source.Target.WindowHandle ? throw failure : fromHandle(service, hwnd);

        Assert.AreSame(failure, await Assert.ThrowsExactlyAsync<COMException>(() =>
            svc.FindSingleElementAsync(main.AppTarget, new UiSelector { Slug = inspected.Selector },
                requireUnique: true, default)));
        Assert.AreEqual(0, main.ClickCount);
        Assert.AreEqual(0, source.ClickCount);
    }

    [TestMethod]
    [DataRow("type")]
    [DataRow("class")]
    [DataRow("root")]
    [DataRow("predicate-only")]
    public async Task ExplicitQuery_PredicatesAndScopeSelectOnlyMatchingProvider(string constraint)
    {
        using var fx = new UiaTestFixture();
        Assert.IsInstanceOfType<NonActivatingTestForm>(fx.Form);
        fx.OnUiThread(() =>
        {
            fx.DupButton.Name = "SharedAction";
            fx.DupLabel.Name = "SharedAction";
        });
        var svc = NewService();
        var target = SessionFor(fx);
        var button = await svc.FindSingleElementAsync(target,
            new UiSelector { Query = "SharedAction", ControlType = "Button" }, default);
        Assert.IsNotNull(button);
        var className = (string)(await svc.GetPropertiesAsync(target, button, "ClassName", default))["ClassName"]!;
        var root = new UiSelector { Query = "fixtureForm", ControlType = "Window" };
        var selector = constraint switch
        {
            "type" => new UiSelector { Query = "SharedAction", ControlType = "Button" },
            "class" => new UiSelector { Query = "SharedAction", ClassName = className.ToUpperInvariant() },
            "root" => new UiSelector { Query = "SharedAction", Root = root, ControlType = "Button" },
            _ => new UiSelector { Root = new() { Query = "grpOptions" }, ControlType = "RadioButton" },
        };
        if (constraint == "predicate-only")
        {
            fx.OnUiThread(() => fx.OptionGroup.Name = "grpOptions");
        }
        var selected = await svc.FindSingleElementAsync(target, selector, requireUnique: true, default);
        Assert.IsNotNull(selected);
        Assert.AreEqual(constraint == "predicate-only" ? "RadioButton" : "Button", selected.Type);
        Assert.IsNotNull(selected.Context);
        Assert.IsNull(selected.InvokableAncestor);
        Assert.AreEqual((long)fx.Hwnd, selected.WindowHandle);
        Assert.AreEqual(0, svc.SerializedElementResolutionCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExplicitQuery_EmptyRootDoesNotSelectOutsideItsScope(bool slug)
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        var button = await svc.FindSingleElementAsync(target,
            new UiSelector { Query = "btnInvoke", ControlType = "Button" }, default);
        Assert.IsNotNull(button);
        Assert.IsNotNull(SlugGenerator.ParseSlug(button.Selector!));
        var selector = new UiSelector
        {
            Query = slug ? null : "btnInvoke",
            Slug = slug ? button.Selector : null,
            Root = new() { Query = "txtValue" },
            ControlType = "Button",
        };
        Assert.IsNull(await svc.FindSingleElementAsync(target, selector, requireUnique: true, default));
        var insideWindow = selector with { Root = new() { Query = "fixtureForm" } };
        var selected = await svc.FindSingleElementAsync(target, insideWindow, requireUnique: true, default);
        Assert.IsNotNull(selected);
        Assert.AreEqual(button.Selector, selected.Selector);
        Assert.IsNull(await svc.FindSingleElementAsync(target,
            insideWindow with { ControlType = "Text" }, requireUnique: true, default));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExplicitQuery_AppWideExactPrecedenceAndUniqueness(bool duplicateExact)
    {
        using var main = new UiaTestFixture();
        using var other = new UiaTestFixture();
        main.OnUiThread(() =>
        {
            main.InvokeButton.Name = duplicateExact ? "GlobalAction" : "GlobalActionSubstring";
            main.InvokeButton.AccessibleName = "GlobalAction";
        });
        other.OnUiThread(() => other.InvokeButton.Name = "GlobalAction");
        UiAutomationService.s_getAllAppWindows = (_, _) =>
            [(main.Hwnd, main.ProcessId, main.Title), (other.Hwnd, other.ProcessId, other.Title)];
        var svc = NewService();
        var target = SessionFor(main, explicitWindow: false);
        var selector = new UiSelector { Query = "GlobalAction", ControlType = "Button" };
        if (duplicateExact)
        {
            await Assert.ThrowsExactlyAsync<UiAmbiguousSelectorException>(() =>
                svc.FindSingleElementAsync(target, selector, requireUnique: true, default));
        }
        else
        {
            var selected = await svc.FindSingleElementAsync(target, selector, requireUnique: true, default);
            Assert.IsNotNull(selected);
            Assert.AreEqual("GlobalAction", selected.AutomationId);
            Assert.AreEqual((long)other.Hwnd, selected.WindowHandle);
            Assert.IsNotNull(selected.Context);
            Assert.IsNull(selected.InvokableAncestor);
        }
        // Omitted auto retains the incoming main-window-first contract.
        var automatic = await svc.FindSingleElementAsync(target, selector, default);
        Assert.IsNotNull(automatic);
        Assert.AreEqual((long)main.Hwnd, automatic.WindowHandle);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExplicitQuery_RootExactPrecedenceBindsDescendantsToItsWindow(bool duplicateRoot)
    {
        using var main = new UiaTestFixture();
        using var other = new UiaTestFixture();
        main.OnUiThread(() => main.Form.Name = duplicateRoot ? "ActionRoot" : "ActionRootSubstring");
        other.OnUiThread(() => other.Form.Name = "ActionRoot");
        UiAutomationService.s_getAllAppWindows = (_, _) =>
            [(main.Hwnd, main.ProcessId, main.Title), (other.Hwnd, other.ProcessId, other.Title)];
        var svc = NewService();
        var target = SessionFor(main, explicitWindow: false);
        var selector = new UiSelector
        {
            Query = "btnInvoke", ControlType = "Button",
            Root = new() { Query = "ActionRoot", ControlType = "Window" },
        };
        if (duplicateRoot)
        {
            await Assert.ThrowsExactlyAsync<UiAmbiguousSelectorException>(() =>
                svc.FindSingleElementAsync(target, selector, requireUnique: true, default));
        }
        else
        {
            var selected = await svc.FindSingleElementAsync(target, selector, requireUnique: true, default);
            Assert.IsNotNull(selected);
            Assert.AreEqual((long)other.Hwnd, selected.WindowHandle);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExplicitQuery_InvalidConstraintsRejectBeforeResolvingTarget(bool nestedRoot)
    {
        var svc = NewService();
        UiAutomationService.s_getRootElement = (_, _, _) =>
            throw new AssertFailedException("Invalid selectors must be rejected before target resolution.");
        var selector = nestedRoot
            ? new UiSelector { Root = new() { Query = "Panel", Root = new() { Query = "Window" } } }
            : new UiSelector { ControlType = "Buton" };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            svc.FindSingleElementAsync(new UiTarget(), selector, requireUnique: true, default));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExplicitQuery_IncompleteTraversalAfterMatchNeverReturnsCandidate(bool cancel)
    {
        using var fx = new UiaTestFixture();
        using var cancellation = new CancellationTokenSource();
        var svc = NewService();
        var target = SessionFor(fx);
        var sawMatch = false;
        var interrupted = false;
        var getBstr = UiAutomationService.s_getCurrentBstr;
        UiAutomationService.s_getCurrentBstr = (element, property) =>
        {
            var value = getBstr(element, property);
            if (property == UIA_PROPERTY_ID.UIA_AutomationIdPropertyId && value.ToString() == "btnInvoke")
            {
                sawMatch = true;
            }
            return value;
        };
        var walker = CUIAutomation8.CreateInstance<IUIAutomation>().get_ControlViewWalker();
        var failure = new COMException("Provider changed after the first match.");
        UiAutomationService.s_getControlViewWalker = _ => ComProxy<IUIAutomationTreeWalker>((method, args) =>
        {
            if (sawMatch)
            {
                interrupted = true;
                if (cancel) { cancellation.Cancel(); }
                else { throw failure; }
            }
            return method.Invoke(walker, args);
        });
        var selector = new UiSelector { Query = "btnInvoke", ControlType = "Button" };
        if (cancel)
        {
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                svc.FindSingleElementAsync(target, selector, requireUnique: true, cancellation.Token));
        }
        else
        {
            Assert.AreSame(failure, await Assert.ThrowsExactlyAsync<COMException>(() =>
                svc.FindSingleElementAsync(target, selector, requireUnique: true, default)));
        }
        Assert.IsTrue(sawMatch);
        Assert.IsTrue(interrupted);
        Assert.AreEqual("unclicked", fx.OnUiThread(() => fx.ResultBox.Text));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExplicitQuery_MissingHwndNeverRecoversOrProvesUniqueness(bool otherWindow)
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx, explicitWindow: !otherWindow);
        var fromHandle = UiAutomationService.s_elementFromHandle;
        UiAutomationService.s_elementFromHandle = (service, hwnd) =>
            otherWindow && hwnd == fx.Hwnd ? fromHandle(service, hwnd) : null;
        UiAutomationService.s_getRootElement = (_, _, _) =>
            throw new AssertFailedException("Explicit selection must not recover a missing HWND.");
        UiAutomationService.s_getAllAppWindows = (_, _) => [(42, Environment.ProcessId, "Missing")];
        var selector = new UiSelector { Query = "btnInvoke", ControlType = "Button" };
        if (otherWindow)
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                svc.FindSingleElementAsync(target, selector, requireUnique: true, default));
        }
        else
        {
            Assert.IsNull(await svc.FindSingleElementAsync(target, selector, requireUnique: true, default));
        }
    }

    [TestMethod]
    public async Task ExplicitQuery_SelectedProviderStaysPinnedAfterDuplicateAppears()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        var id = fx.OnUiThread(() => fx.ToggleCheck.Name);
        var selected = await svc.FindSingleElementAsync(target,
            new UiSelector { Query = id, Root = new() { Query = "fixtureForm" }, ControlType = "CheckBox" },
            requireUnique: true, default);
        Assert.IsNotNull(selected);
        CheckBox duplicate = null!;
        fx.OnUiThread(() =>
        {
            fx.ToggleCheck.Checked = false;
            duplicate = new CheckBox { Name = id, Top = 550 };
            fx.Form.Controls.Add(duplicate);
        });
        await svc.InvokeAsync(target, selected, UiInvokeAction.Toggle, default);
        Assert.IsTrue(fx.OnUiThread(() => fx.ToggleCheck.Checked));
        Assert.IsFalse(fx.OnUiThread(() => duplicate.Checked));
        Assert.AreEqual(0, svc.SerializedElementResolutionCount);
    }
}

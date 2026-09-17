// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;
using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

public partial class RealUiAutomationTests
{
    [TestMethod]
    [DataRow(0, "save", true, false, "save", true)]
    [DataRow(1, "save", true, false, "save", true)]
    [DataRow(0, "", false, true, "Primary", true)]
    [DataRow(1, "", false, true, "Primary", true)]
    [DataRow(0, "save", true, false, "PRIMARY", false)]
    [DataRow(1, "save", true, false, "PRIMARY", false)]
    [DataRow(0, "save", false, true, "save", false)]
    [DataRow(1, "save", false, true, "save", false)]
    public async Task ExplicitSelection_PartialBulkResults_UsesCompleteControlView(
        int bulkCount, string automationId, bool duplicate, bool sameName, string query, bool ambiguous)
    {
        var calls = new List<string>();
        var retained = ConfigureExplicitIdentityTree(calls, bulkCount, duplicate, 2048,
            automationId: automationId, sameName: sameName);
        UiAutomationService.s_getElementProcessId = _ => Environment.ProcessId;
        var svc = NewService();
        var target = new UiTarget { ProcessId = Environment.ProcessId, ProcessName = "fake", IsExplicitWindow = true };
        if (ambiguous)
        {
            var error = await Assert.ThrowsExactlyAsync<UiAmbiguousSelectorException>(
                () => svc.FindSingleElementAsync(target, new UiSelector { Query = query }, requireUnique: true, CancellationToken.None));
            StringAssert.Contains(error.Message, "Selector matched 2 elements");
            StringAssert.Contains(error.Message, "inspect");
            Assert.IsFalse(calls.Contains("invoke"));
        }
        else
        {
            var selected = await svc.FindSingleElementAsync(target, new UiSelector { Query = query }, requireUnique: true, CancellationToken.None);
            Assert.IsNotNull(selected);
            Assert.AreSame(retained, selected.Context!.AutomationElement);
            Assert.IsNull(selected.InvokableAncestor);
            Assert.AreEqual(new UiInvokeActionResult("InvokePattern", "invoke"),
                await svc.InvokeAsync(target, selected, UiInvokeAction.Invoke, CancellationToken.None));
            Assert.AreEqual(1, calls.Count(c => c == "invoke"));
            Assert.AreEqual(0, svc.SerializedElementResolutionCount);
        }
        Assert.IsFalse(calls.Contains("bulk"), "Neither zero nor nonzero partial bulk results can prove uniqueness.");
        Assert.IsTrue(calls.Contains("identity:2049"), "The complete tree must be examined even after finding a match.");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ExplicitSelection_SameNameParentAndChild_NeverPrefersInvokable(bool childInvokable)
    {
        var calls = new List<string>();
        ConfigureExplicitIdentityTree(calls, 1, false, 0, automationId: "", sameName: true, lastInvokable: childInvokable);
        var svc = NewService();
        var target = new UiTarget { ProcessId = Environment.ProcessId, ProcessName = "fake" };
        await Assert.ThrowsExactlyAsync<UiAmbiguousSelectorException>(
            () => svc.FindSingleElementAsync(target, new UiSelector { Query = "Primary" }, requireUnique: true, CancellationToken.None));
        Assert.IsFalse(calls.Contains("invoke"));
    }

    [TestMethod]
    [DataRow("first")]
    [DataRow("sibling")]
    [DataRow("identity")]
    [DataRow("name")]
    [DataRow("stale")]
    public async Task ExplicitSelection_IncompleteControlView_NeverReturnsCandidate(string failure)
    {
        var calls = new List<string>();
        ConfigureExplicitIdentityTree(calls, 1, false, 3, failure);
        var svc = NewService();
        var target = new UiTarget { ProcessId = Environment.ProcessId, ProcessName = "fake" };
        await Assert.ThrowsExactlyAsync<COMException>(
            () => svc.FindSingleElementAsync(target, new UiSelector { Query = "Primary" }, requireUnique: true, CancellationToken.None));
        Assert.IsFalse(calls.Contains("invoke"));
    }

    [TestMethod]
    public async Task ExplicitSelection_EmptyMissingAndCanceled_DoNotAct()
    {
        var calls = new List<string>();
        ConfigureExplicitIdentityTree(calls, 1, false, 3);
        var svc = NewService();
        var target = new UiTarget { ProcessId = Environment.ProcessId, ProcessName = "fake", IsExplicitWindow = true };
        Assert.IsNull(await svc.FindSingleElementAsync(target, new UiSelector(), requireUnique: true, CancellationToken.None));
        Assert.IsNull(await svc.FindSingleElementAsync(target, new UiSelector { Query = "absent" }, requireUnique: true, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => svc.FindSingleElementAsync(target, new UiSelector { Query = "save" }, requireUnique: true, new CancellationToken(true)));
        Assert.IsFalse(calls.Contains("invoke"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExplicitSelection_WindowProviderFailure_IsNotIgnored(bool ownedWindow)
    {
        var calls = new List<string>();
        ConfigureExplicitIdentityTree(calls, 1, false, 0);
        UiAutomationService.s_getAllAppWindows = (_, _) => [(42, Environment.ProcessId, "Owned")];
        UiAutomationService.s_elementFromHandle = (_, _) => throw new COMException("Provider failed.");
        var svc = NewService();
        var target = new UiTarget
        {
            ProcessId = Environment.ProcessId, ProcessName = "fake", WindowHandle = ownedWindow ? 0 : 42,
        };
        await Assert.ThrowsExactlyAsync<COMException>(
            () => svc.FindSingleElementAsync(target, new UiSelector { Query = "absent" }, requireUnique: true, CancellationToken.None));
        Assert.IsFalse(calls.Contains("invoke"));
    }

    [TestMethod]
    [DataRow(true, true, true)]
    [DataRow(false, true, true)]
    [DataRow(false, true, false)]
    [DataRow(true, false, true)]
    public async Task ExplicitAction_RetainedIdentity_ValidatesWithoutReplacingProvider(
        bool duplicate, bool automationIdSelector, bool sameProvider)
    {
        var calls = new List<string>();
        var retained = ConfigureExplicitIdentityTree(calls, 1, duplicate, 3);
        UiAutomationService.s_getElementProcessId = _ => Environment.ProcessId;
        UiAutomationService.s_compareElements = (_, original, candidate) =>
        {
            calls.Add("compare");
            Assert.AreSame(retained, original);
            Assert.AreSame(retained, candidate);
            return sameProvider;
        };
        var svc = NewService();
        var element = new UiElement
        {
            Context = new UiElementContext(retained),
            AutomationId = "save",
            Selector = automationIdSelector ? "save" : "btn-save-1234",
        };
        var target = new UiTarget { ProcessId = Environment.ProcessId, ProcessName = "fake" };
        if (automationIdSelector && (duplicate || !sameProvider))
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => svc.InvokeAsync(target, element, UiInvokeAction.Invoke, CancellationToken.None));
            Assert.IsFalse(calls.Contains("invoke"));
        }
        else
        {
            Assert.AreEqual(new UiInvokeActionResult("InvokePattern", "invoke"),
                await svc.InvokeAsync(target, element, UiInvokeAction.Invoke, CancellationToken.None));
            Assert.AreEqual(1, calls.Count(c => c == "invoke"));
        }
        Assert.AreEqual(automationIdSelector ? 1 : 0, svc.SerializedElementResolutionCount);
        Assert.AreEqual(automationIdSelector && !duplicate, calls.Contains("compare"));
        Assert.IsFalse(calls.Contains("bulk"));
    }

    [TestMethod]
    [DataRow("first")]
    [DataRow("sibling")]
    [DataRow("identity")]
    [DataRow("stale")]
    [DataRow("missing")]
    public async Task ExplicitAction_RetainedIdentityValidationFailure_NeverInvokes(string failure)
    {
        var calls = new List<string>();
        var retained = ConfigureExplicitIdentityTree(calls, 1, false, 3, failure);
        UiAutomationService.s_getElementProcessId = _ => Environment.ProcessId;
        UiAutomationService.s_compareElements = (_, _, _) => throw new AssertFailedException("Cannot compare before complete uniqueness validation.");
        var svc = NewService();
        var aid = failure == "missing" ? "missing" : "save";
        var element = new UiElement { Context = new UiElementContext(retained), AutomationId = aid, Selector = aid };
        var target = new UiTarget { ProcessId = Environment.ProcessId, ProcessName = "fake" };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.InvokeAsync(target, element, UiInvokeAction.Invoke, CancellationToken.None));
        Assert.IsFalse(calls.Contains("invoke"));
    }

    [TestMethod]
    [DataRow(0, false, 0)]
    [DataRow(1, false, 0)]
    [DataRow(0, true, 0)]
    [DataRow(1, true, 0)]
    [DataRow(0, false, 2048)]
    [DataRow(1, false, 2048)]
    [DataRow(0, true, 2048)]
    [DataRow(1, true, 2048)]
    public async Task ExplicitAction_PartialBulkIdentityResults_NeverEstablishUniqueness(
        int bulkCount, bool duplicate, int depth)
    {
        var calls = new List<string>();
        ConfigureExplicitIdentityTree(calls, bulkCount, duplicate, depth);
        var svc = NewService();
        var target = new UiTarget { ProcessId = Environment.ProcessId, ProcessName = "fake" };
        var element = new UiElement { AutomationId = "save", Selector = "save", Type = "Button" };

        if (duplicate)
        {
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => svc.InvokeAsync(target, element, UiInvokeAction.Invoke, CancellationToken.None));
            StringAssert.Contains(error.Message, "no longer unique");
            Assert.IsFalse(calls.Contains("invoke"));
        }
        else
        {
            Assert.AreEqual(new UiInvokeActionResult("InvokePattern", "invoke"),
                await svc.InvokeAsync(target, element, UiInvokeAction.Invoke, CancellationToken.None));
            Assert.AreEqual(1, calls.Count(c => c == "invoke"));
        }
        Assert.IsFalse(calls.Contains("bulk"), "Neither empty nor nonempty bulk results can establish uniqueness.");
        Assert.IsTrue(calls.Contains($"identity:{depth + 1}"), "The walk must reach the final subtree without a depth cutoff.");
    }

    [TestMethod]
    [DataRow("first")]
    [DataRow("sibling")]
    [DataRow("identity")]
    [DataRow("stale")]
    public async Task ExplicitAction_IncompleteControlView_FailsClosedAfterFindingCandidate(string failure)
    {
        var calls = new List<string>();
        ConfigureExplicitIdentityTree(calls, 1, false, 3, failure);
        var svc = NewService();
        var target = new UiTarget { ProcessId = Environment.ProcessId, ProcessName = "fake" };
        var element = new UiElement { AutomationId = "save", Type = "Button" };

        if (failure == "stale")
        {
            var error = await Assert.ThrowsExactlyAsync<COMException>(
                () => svc.InvokeAsync(target, element, UiInvokeAction.Invoke, CancellationToken.None));
            Assert.AreEqual(unchecked((int)0x80040201), error.HResult);
        }
        else
        {
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => svc.InvokeAsync(target, element, UiInvokeAction.Invoke, CancellationToken.None));
            StringAssert.Contains(error.Message, "could not be read completely");
        }
        Assert.IsFalse(calls.Contains("invoke"));
        Assert.IsFalse(calls.Contains("bulk"));
    }

    // The bulk provider deliberately omits the tail: a one-element result looks unique even when
    // a duplicate exists. A synthetic ControlView allows arbitrary depth and deterministic faults.
    private static IUIAutomationElement ConfigureExplicitIdentityTree(
        List<string> calls, int bulkCount, bool duplicate, int depth, string? failure = null,
        string automationId = "save", bool sameName = false, bool lastInvokable = true)
    {
        var invoke = ComProxy<IUIAutomationInvokePattern>((method, _) =>
        {
            Assert.AreEqual("Invoke", method.Name);
            calls.Add("invoke");
            return null;
        });
        var nodes = new IUIAutomationElement[depth + 2];
        for (var i = 0; i < nodes.Length; i++)
        {
            var index = i;
            nodes[i] = ComProxy<IUIAutomationElement>((method, _) =>
            {
                if (method.Name == "GetCurrentPattern")
                {
                    return index == 0 || (index == nodes.Length - 1 && lastInvokable) ? invoke : null;
                }
                if (method.Name == "get_CurrentAutomationId")
                {
                    calls.Add($"identity:{index}");
                    if (failure == "identity" && index == nodes.Length - 1) { return ThrowCom(); }
                    return StringBstr(index == 0 || ((duplicate || automationId.Length == 0) && index == nodes.Length - 1)
                        ? automationId : "container");
                }
                if (method.Name == "get_CurrentName")
                {
                    if (failure == "name" && index == nodes.Length - 1) { return ThrowCom(); }
                    return StringBstr(index == 0 || (sameName && index == nodes.Length - 1)
                        ? "Primary Save" : index == nodes.Length - 1 ? "Secondary Save" : "Container");
                }
                if (method.Name == "get_CurrentControlType") { return UIA_CONTROLTYPE_ID.UIA_ButtonControlTypeId; }
                if (method.Name == "get_CurrentBoundingRectangle") { return new global::Windows.Win32.Foundation.RECT(); }
                if (method.Name == "get_CurrentIsEnabled") { return new global::Windows.Win32.Foundation.BOOL(true); }
                if (method.Name == "get_CurrentIsOffscreen") { return new global::Windows.Win32.Foundation.BOOL(false); }
                if (method.Name == "get_CurrentNativeWindowHandle") { return new global::Windows.Win32.Foundation.HWND(42); }
                return ThrowCom();
            });
        }
        var bulk = ComProxy<IUIAutomationElementArray>((method, _) => method.Name switch
        {
            "get_Length" => bulkCount,
            "GetElement" => nodes[0],
            _ => ThrowCom(),
        });
        var root = ComProxy<IUIAutomationElement>((method, _) =>
        {
            if (method.Name == "FindAll") { calls.Add("bulk"); return bulk; }
            return ThrowCom();
        });
        var indexes = new Dictionary<IUIAutomationElement, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < nodes.Length; i++) { indexes.Add(nodes[i], i); }
        var walker = ComProxy<IUIAutomationTreeWalker>((method, args) =>
        {
            var node = (IUIAutomationElement)args![0]!;
            var index = ReferenceEquals(node, root) ? -1 : indexes[node];
            if (method.Name == "GetFirstChildElement")
            {
                calls.Add($"child:{index}");
                if (index == nodes.Length - 1)
                {
                    if (failure == "first") { return ThrowCom(); }
                    if (failure == "stale") { throw new COMException("gone", unchecked((int)0x80040201)); }
                    return null;
                }
                return nodes[index + 1];
            }
            if (method.Name == "GetNextSiblingElement")
            {
                if (failure == "sibling" && index == nodes.Length - 1) { return ThrowCom(); }
                return null;
            }
            return ThrowCom();
        });
        UiAutomationService.s_getRootElement = (_, _) => root;
        UiAutomationService.s_getExplicitIdentityWalker = _ => walker;
        return nodes[0];
    }

    [TestMethod]
    public async Task ExplicitAction_RealDuplicateInNestedControlView_RejectsPromotedIdentity()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        var model = await ResolveAsync(svc, target, "btnInvoke");
        model.Context = null;
        model.Selector = model.AutomationId;
        var duplicateClicks = 0;
        fx.OnUiThread(() =>
        {
            var duplicate = new Button { Name = "btnInvoke", AccessibleName = "Other Save" };
            duplicate.Click += (_, _) => duplicateClicks++;
            fx.InvokableMiddlePanel.Controls.Add(duplicate);
            duplicate.CreateControl();
        });
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.InvokeAsync(target, model, UiInvokeAction.Invoke, CancellationToken.None));
        StringAssert.Contains(error.Message, "no longer unique");
        Assert.AreEqual("unclicked", fx.OnUiThread(() => fx.ResultBox.Text));
        Assert.AreEqual(0, fx.OnUiThread(() => duplicateClicks));
    }
}

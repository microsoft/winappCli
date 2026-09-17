// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;
using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

public partial class RealUiAutomationTests
{
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
        List<string> calls, int bulkCount, bool duplicate, int depth, string? failure = null)
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
                if (method.Name == "GetCurrentPattern") { return invoke; }
                if (method.Name == "get_CurrentAutomationId")
                {
                    calls.Add($"identity:{index}");
                    if (failure == "identity" && index == nodes.Length - 1) { return ThrowCom(); }
                    return StringBstr(index == 0 || (duplicate && index == nodes.Length - 1) ? "save" : "container");
                }
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

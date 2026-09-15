// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;
using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

public partial class RealUiAutomationTests
{
    [TestMethod]
    [DataRow(false, unchecked((int)0x80040201))]
    [DataRow(true, unchecked((int)0x80040201))]
    [DataRow(false, unchecked((int)0x80004005))]
    [DataRow(true, unchecked((int)0x80004005))]
    public async Task Query_InterruptedSlugHashCannotReportAbsence(bool nameless, int hresult)
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        var slug = (await svc.InspectAsync(target, null, 0, CancellationToken.None))[0].Selector!;
        if (nameless)
        {
            var parsed = SlugGenerator.ParseSlug(slug)!.Value;
            slug = $"{parsed.Prefix}-{parsed.Hash}";
        }
        var field = typeof(UiAutomationService).GetField("_automation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var automation = (IUIAutomation)field.GetValue(svc)!;
        var realRoot = automation.ElementFromHandle(new((nint)fx.Hwnd));
        var failure = new COMException("Identity read interrupted.", hresult);
        var root = ComProxy<IUIAutomationElement>((method, args) =>
            method.Name == "GetRuntimeId" ? throw failure : method.Invoke(realRoot, args));
        var realWalker = automation.get_ControlViewWalker();
        var walker = ComProxy<IUIAutomationTreeWalker>((method, args) =>
            method.Invoke(realWalker, args?.Select(arg => ReferenceEquals(arg, root) ? realRoot : arg).ToArray()));
        field.SetValue(svc, ComProxy<IUIAutomation>((method, args) =>
            method.Name == "get_ControlViewWalker" ? walker : method.Invoke(automation, args)));
        UiAutomationService.s_getRootElement = (_, _) => root;

        var actual = await Assert.ThrowsExactlyAsync<COMException>(() => svc.FindSingleElementAsync(target,
            new UiSelector { Root = new() { Slug = slug }, ControlType = "Button" }, CancellationToken.None));

        Assert.AreSame(failure, actual);
    }

    [TestMethod]
    [DataRow("GetFirstChildElement", false, unchecked((int)0x80040201))]
    [DataRow("GetNextSiblingElement", false, unchecked((int)0x80040201))]
    [DataRow("GetFirstChildElement", true, unchecked((int)0x80040201))]
    [DataRow("GetNextSiblingElement", true, unchecked((int)0x80040201))]
    [DataRow("GetFirstChildElement", false, unchecked((int)0x80004005))]
    [DataRow("GetNextSiblingElement", false, unchecked((int)0x80004005))]
    [DataRow("GetFirstChildElement", true, unchecked((int)0x80004005))]
    [DataRow("GetNextSiblingElement", true, unchecked((int)0x80004005))]
    public async Task Query_InterruptedWalkCannotReportAbsence(string operation, bool slug, int hresult)
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var field = typeof(UiAutomationService).GetField("_automation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var automation = (IUIAutomation)field.GetValue(svc)!;
        var realWalker = automation.get_ControlViewWalker();
        var failure = new COMException("Traversal interrupted.", hresult);
        var walker = ComProxy<IUIAutomationTreeWalker>((method, args) =>
            method.Name == operation ? throw failure : method.Invoke(realWalker, args));
        field.SetValue(svc, ComProxy<IUIAutomation>((method, args) =>
            method.Name == "get_ControlViewWalker" ? walker : method.Invoke(automation, args)));
        var query = slug
            ? new UiSelector { Slug = "btn-missing-a123", ControlType = "Button" }
            : new UiSelector { Query = "missing", ControlType = "Button" };

        var actual = await Assert.ThrowsExactlyAsync<COMException>(() =>
            svc.FindSingleElementAsync(SessionFor(fx), query, CancellationToken.None));

        Assert.AreSame(failure, actual);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Query_ReadReplacedIdentitySignalsRetry(bool property, bool replace)
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        var root = fx.OnUiThread(() =>
        {
            var panel = new Panel { Name = "readRoot", Width = 300, Height = 100 };
            panel.Controls.Add(new TextBox { Name = "readValue", Text = "old" });
            fx.Form.Controls.Add(panel);
            return panel;
        });
        var query = new UiSelector { Root = new() { Query = "readRoot" }, Query = "readValue", ControlType = "Edit" };
        var old = await svc.FindSingleElementAsync(target, query, CancellationToken.None);
        Assert.IsNotNull(old);
        fx.OnUiThread(() =>
        {
            if (replace)
            {
                var panel = new Panel { Name = "readRoot", Width = 300, Height = 100 };
                panel.Controls.Add(new TextBox { Name = "readValue", Text = "ready" });
                fx.Form.Controls.Add(panel);
                _ = panel.Controls[0].Handle;
            }
            root.Dispose();
        });

        var failure = await Assert.ThrowsExactlyAsync<UiElementNotFoundException>(async () =>
        {
            if (property) { await svc.GetPropertiesAsync(target, old, "Value", CancellationToken.None); }
            else { await svc.GetTextAsync(target, old, CancellationToken.None); }
        });
        Assert.AreEqual(old.Selector, failure.Selector);
        var current = await svc.FindSingleElementAsync(target, query, CancellationToken.None);
        if (replace)
        {
            Assert.IsNotNull(current);
            Assert.AreNotEqual(old.Selector, current.Selector);
            Assert.AreEqual("ready", await svc.GetTextAsync(target, current, CancellationToken.None));
        }
        else { Assert.IsNull(current); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Query_PartialNonzeroBulkResults_CannotHideDuplicateRoots(bool exactId)
    {
        using var fx = new UiaTestFixture();
        fx.OnUiThread(() =>
        {
            for (var i = 0; i < 2; i++)
            {
                var panel = new Panel { Name = exactId ? "duplicateRoot" : $"root{i}", AccessibleName = "duplicateRoot" };
                fx.Form.Controls.Add(panel);
            }
        });
        var bulkCounts = new List<int>();
        var svc = ServiceWithPartialQueryProvider(fx, onBulkResult: bulkCounts.Add);
        await Assert.ThrowsAsync<UiAmbiguousSelectorException>(() => svc.SearchAsync(SessionFor(fx),
            new UiSelector { Root = new() { Query = "duplicateRoot" }, ControlType = "Button" },
            1, CancellationToken.None));
        CollectionAssert.Contains(bulkCounts, 1, "Uniqueness must complete a nonempty partial bulk result.");
    }

    [TestMethod]
    [DataRow("GetFirstChildElement", false)]
    [DataRow("GetNextSiblingElement", false)]
    [DataRow("GetFirstChildElement", true)]
    [DataRow("GetNextSiblingElement", true)]
    public async Task Query_PartialNonzeroBulkResults_InterruptedCompletionCannotProveUniqueRoot(
        string operation, bool invalidCast)
    {
        using var fx = new UiaTestFixture();
        fx.OnUiThread(() =>
        {
            fx.Form.Controls.Add(new Panel { Name = "duplicateRoot" });
            fx.Form.Controls.Add(new Panel { Name = "duplicateRoot" });
        });
        var bulkCounts = new List<int>();
        var svc = ServiceWithPartialQueryProvider(fx, onBulkResult: bulkCounts.Add);
        var realWalker = UiAutomationService.s_getControlViewWalker(svc);
        Exception failure = invalidCast
            ? new InvalidCastException("Provider interrupted completion.")
            : new COMException("Provider interrupted completion.", unchecked((int)0x80040201));
        UiAutomationService.s_getControlViewWalker = _ => ComProxy<IUIAutomationTreeWalker>((method, args) =>
            method.Name == operation ? throw failure : method.Invoke(realWalker, args));

        var actual = await Assert.ThrowsAsync<Exception>(() => svc.SearchAsync(SessionFor(fx),
            new UiSelector { Root = new() { Query = "duplicateRoot" }, ControlType = "Button" },
            1, CancellationToken.None));

        Assert.AreSame(failure, actual);
        CollectionAssert.Contains(bulkCounts, 1, "A partial root match must not establish uniqueness.");
    }

    [TestMethod]
    public async Task Query_PartialNonzeroBulkResults_CannotHidePredicateMatches()
    {
        using var fx = new UiaTestFixture();
        fx.OnUiThread(() => fx.Form.Controls.Add(new Button { Name = "secondButton", Text = "Second" }));
        var bulkCounts = new List<int>();
        var svc = ServiceWithPartialQueryProvider(fx, onBulkResult: bulkCounts.Add);
        var matches = await svc.SearchAsync(SessionFor(fx), new UiSelector { ControlType = "Button" },
            1000, CancellationToken.None);
        Assert.IsTrue(matches.Any(m => m.AutomationId == "btnInvoke"));
        Assert.IsTrue(matches.Any(m => m.AutomationId == "secondButton"),
            $"Actual matches: {string.Join(", ", matches.Select(m => m.AutomationId))}");
        Assert.IsTrue(matches.Length < 1000, "Exercise completion below the requested cap, not capped ordering.");
        CollectionAssert.Contains(bulkCounts, 1, "The shared completion path must receive a nonempty partial result.");
        await Assert.ThrowsAsync<UiAmbiguousSelectorException>(() => svc.FindSingleElementAsync(SessionFor(fx),
            new UiSelector { ControlType = "Button" }, CancellationToken.None));
    }

    [TestMethod]
    public async Task Query_PartialNonzeroBulkResults_ExactIdWinsOverEarlierNameAtResultCap()
    {
        using var fx = new UiaTestFixture();
        fx.OnUiThread(() =>
        {
            fx.InvokeButton.AccessibleName = "lateExact";
            fx.Form.Controls.Add(new Button { Name = "lateExact", Text = "Exact" });
        });
        var svc = ServiceWithPartialQueryProvider(fx, hiddenId: "lateExact");
        var matches = await svc.SearchAsync(SessionFor(fx),
            new UiSelector { Query = "lateExact", ControlType = "Button" }, 1, CancellationToken.None);
        Assert.HasCount(1, matches);
        Assert.AreEqual("lateExact", matches[0].AutomationId,
            "A capped Name match cannot stop the search for an exact AutomationId.");
    }

    private static UiAutomationService ServiceWithPartialQueryProvider(UiaTestFixture fx, string? hiddenId = null,
        Action<int>? onBulkResult = null)
    {
        var svc = NewService();
        var field = typeof(UiAutomationService).GetField("_automation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var automation = (IUIAutomation)field.GetValue(svc)!;
        var realRoot = automation.ElementFromHandle(new((nint)fx.Hwnd));
        var root = ComProxy<IUIAutomationElement>((method, args) =>
        {
            if (method.Name != "FindAll") { return method.Invoke(realRoot, args); }
            var all = (IUIAutomationElementArray)method.Invoke(realRoot, args)!;
            var visible = new List<IUIAutomationElement>();
            for (var i = 0; i < all.get_Length(); i++)
            {
                var element = all.GetElement(i);
                var value = element.get_CurrentAutomationId().ToString();
                if (value != hiddenId) { visible.Add(element); }
            }
            // Model a provider stopping early, not one returning no matches at all.
            onBulkResult?.Invoke(Math.Min(1, visible.Count));
            return ComProxy<IUIAutomationElementArray>((arrayMethod, _) => arrayMethod.Name switch
            {
                "get_Length" => Math.Min(1, visible.Count),
                "GetElement" => visible[0],
                _ => throw new NotSupportedException(arrayMethod.Name),
            });
        });
        var realWalker = automation.get_ControlViewWalker();
        var walker = ComProxy<IUIAutomationTreeWalker>((method, args) =>
        {
            var translated = args?.Select(arg => ReferenceEquals(arg, root) ? realRoot : arg).ToArray();
            return method.Invoke(realWalker, translated);
        });
        field.SetValue(svc, ComProxy<IUIAutomation>((method, args) =>
            method.Name == "get_ControlViewWalker" ? walker : method.Invoke(automation, args)));
        UiAutomationService.s_getRootElement = (_, _) => root;
        return svc;
    }

    [TestMethod]
    public async Task Query_DuplicateNames_TypeAndLiteralClassAreAnded()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        var button = await ResolveAsync(svc, target, "btnInvoke");
        fx.OnUiThread(() =>
        {
            fx.ValueBox.AccessibleName = "Click Me";
            fx.TextLabel.AccessibleName = "Click Me";
        });
        var query = new UiSelector { Query = "Click Me", ControlType = "bUtToN", ClassName = button.ClassName!.ToUpperInvariant() };
        var matches = await svc.SearchAsync(target, query, 50, CancellationToken.None);
        Assert.HasCount(1, matches);
        Assert.AreEqual("btnInvoke", matches[0].AutomationId);
        Assert.IsEmpty(await svc.SearchAsync(target, query with { ControlType = "Text" }, 50, CancellationToken.None));
        Assert.IsEmpty(await svc.SearchAsync(target, query with { ClassName = button.ClassName[..^1] }, 50, CancellationToken.None));
        Assert.IsEmpty(await svc.SearchAsync(target, query with { ClassName = "*" }, 50, CancellationToken.None));
        var edit = await svc.FindSingleElementAsync(target, query with { ControlType = "TextBox", ClassName = null }, CancellationToken.None);
        Assert.AreEqual("txtValue", edit?.AutomationId);
        var text = await svc.FindSingleElementAsync(target, query with { ControlType = "TextBlock", ClassName = null }, CancellationToken.None);
        Assert.AreEqual("lblText", text?.AutomationId);
    }

    [TestMethod]
    public async Task Query_ExactAutomationIdPrecedence_AppliesAfterPredicates()
    {
        using var fx = new UiaTestFixture();
        fx.OnUiThread(() => fx.ValueBox.AccessibleName = "btnInvoke");
        var svc = NewService();
        var target = SessionFor(fx);
        var button = await svc.FindSingleElementAsync(target,
            new UiSelector { Query = "btnInvoke", ControlType = "Button" }, CancellationToken.None);
        Assert.AreEqual("btnInvoke", button?.AutomationId);
        var edit = await svc.FindSingleElementAsync(target,
            new UiSelector { Query = "btnInvoke", ControlType = "Edit" }, CancellationToken.None);
        Assert.AreEqual("txtValue", edit?.AutomationId,
            "An exact ID on an excluded type must not suppress a matching Name on the requested type.");
    }

    [TestMethod]
    public async Task Query_RootExcludesItselfAndSiblings_AndRequiresUniqueRoot()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        await ResolveAsync(svc, target, "lblInsideInvoke");
        var query = new UiSelector { Query = "Inside", Root = new UiSelector { Query = "pnlInsideInvoke" } };
        var matches = await svc.SearchAsync(target, query, 50, CancellationToken.None);
        Assert.HasCount(1, matches);
        Assert.AreEqual("lblInsideInvoke", matches[0].AutomationId);
        Assert.IsEmpty(await svc.SearchAsync(target, query with { Query = "pnlInsideInvoke" }, 50, CancellationToken.None));
        Assert.IsEmpty(await svc.SearchAsync(target, query with { Query = "txtValue" }, 50, CancellationToken.None));
        Assert.IsEmpty(await svc.SearchAsync(target,
            query with { Root = new UiSelector { Query = "missingRoot" } }, 50, CancellationToken.None));
        var root = await ResolveAsync(svc, target, "pnlInsideInvoke");
        var byRootSlug = await svc.SearchAsync(target,
            query with { Root = new UiSelector { Slug = root.Selector } }, 50, CancellationToken.None);
        Assert.HasCount(1, byRootSlug);
        Assert.AreEqual("lblInsideInvoke", byRootSlug[0].AutomationId);
        Assert.IsEmpty(await svc.SearchAsync(target, query with { Query = null, Slug = root.Selector }, 50, CancellationToken.None));
        fx.OnUiThread(() =>
        {
            fx.ValueBox.AccessibleName = "DuplicateRoot";
            fx.InvokeButton.AccessibleName = "DuplicateRoot";
        });
        await Assert.ThrowsAsync<UiAmbiguousSelectorException>(() => svc.SearchAsync(target,
            query with { Root = new UiSelector { Query = "DuplicateRoot" } }, 50, CancellationToken.None));
    }

    [TestMethod]
    public async Task Query_ReplacedRootSlugDoesNotBindReplacement_ButNamedRootResolvesAgain()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        var root = await ResolveAsync(svc, target, "pnlInsideInvoke");
        var query = new UiSelector
        {
            Query = "Inside",
            Root = new UiSelector { Slug = root.Selector },
            ControlType = "Text",
        };
        Assert.HasCount(1, await svc.SearchAsync(target, query, 50, CancellationToken.None));
        fx.OnUiThread(() =>
        {
            var parent = fx.InvokableMiddlePanel.Parent!;
            parent.Controls.Remove(fx.InvokableMiddlePanel);
            var replacement = new System.Windows.Forms.Panel
            {
                Name = "pnlInsideInvoke", Width = 200, Height = 50,
            };
            replacement.Controls.Add(new System.Windows.Forms.Label
            {
                Name = "lblInsideInvoke", Text = "Inside replacement",
            });
            parent.Controls.Add(replacement);
            fx.InvokableMiddlePanel.Dispose();
        });

        Assert.IsEmpty(await svc.SearchAsync(target, query, 50, CancellationToken.None));
        Assert.IsNull(await svc.FindSingleElementAsync(target, query, CancellationToken.None));
        var replaced = await svc.SearchAsync(target,
            query with { Root = new UiSelector { Query = "pnlInsideInvoke" } }, 50, CancellationToken.None);
        Assert.HasCount(1, replaced);
        Assert.AreEqual("Inside replacement", replaced[0].Name);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.FindSingleElementAsync(target,
            new UiSelector { Slug = root.Selector }, CancellationToken.None));
    }

    [TestMethod]
    public async Task Query_DeepDescendantAndSlug_ReadSameDuplicateId()
    {
        using var fx = new UiaTestFixture();
        fx.OnUiThread(() =>
        {
            Control parent = fx.Form;
            // Windows limits nested HWNDs; 35 still exceeds both inspect and the old
            // manual query fallback's 25-level cap.
            for (var i = 0; i < 35; i++)
            {
                var panel = new Panel { Name = $"deep{i}", Width = 200, Height = 100 };
                parent.Controls.Add(panel);
                parent = panel;
            }
            parent.Controls.Add(new TextBox { Name = "txtValue", Text = "deep-value", Width = 100 });
        });
        var svc = NewService();
        var target = SessionFor(fx);
        var query = new UiSelector { Query = "txtValue", Root = new UiSelector { Query = "deep0" }, ControlType = "Edit" };
        var found = await svc.FindSingleElementAsync(target, query, CancellationToken.None);
        Assert.IsNotNull(found);
        Assert.AreEqual("deep-value", await svc.GetTextAsync(target, found, CancellationToken.None));
        var bySlug = await svc.SearchAsync(target, query with { Query = null, Slug = found.Selector }, 5, CancellationToken.None);
        Assert.HasCount(1, bySlug);
        Assert.AreEqual(found.Selector, bySlug[0].Selector);
    }

    [TestMethod]
    public async Task Query_PopupBoundaryAndExplicitWindow()
    {
        using var fx = new UiaTestFixture();
        Form popup = null!;
        fx.OnUiThread(() =>
        {
            popup = new Form { Name = "queryPopup", Text = "Query Popup" };
            popup.Controls.Add(new TextBox { Name = "popupValue", Text = "popup" });
            // A separate top-level UIA tree, not an owned Form embedded by the provider.
            popup.Show();
        });
        try
        {
            var svc = NewService();
            var target = SessionFor(fx, explicitWindow: false);
            var query = new UiSelector { Query = "popupValue", Root = new UiSelector { Query = "pnlInsideInvoke" }, ControlType = "Edit" };
            Assert.IsEmpty(await svc.SearchAsync(target, query, 10, CancellationToken.None));
            Assert.IsEmpty(await svc.SearchAsync(SessionFor(fx), query with { Root = null }, 10, CancellationToken.None));
            var matches = await svc.SearchAsync(target, query with { Root = new UiSelector { Query = "queryPopup" } }, 10, CancellationToken.None);
            Assert.HasCount(1, matches);
            Assert.AreEqual("popup", await svc.GetTextAsync(target, matches[0], CancellationToken.None));
        }
        finally { fx.OnUiThread(() => popup.Dispose()); }
    }

    [TestMethod]
    [DataRow(1, false)]
    [DataRow(3, true)]
    public async Task Query_RootExactIdInLaterWindowWinsOverEarlierSubstringMatches(int mainMatches, bool earlierPopup)
    {
        using var fx = new UiaTestFixture();
        Form popup = null!;
        Form? firstPopup = null;
        var windows = new List<(nint Hwnd, int Pid, string Title)>();
        fx.OnUiThread(() =>
        {
            for (var i = 0; i < mainMatches; i++)
            {
                fx.Form.Controls.Add(new Panel { Name = $"mainContainer{i}", AccessibleName = "ScopeRoot" });
            }
            windows.Add(((nint)fx.Hwnd, fx.ProcessId, fx.Title));
            if (earlierPopup)
            {
                firstPopup = new Form { Name = "earlierPopup", Text = "Earlier Popup" };
                for (var i = 0; i < 3; i++)
                {
                    firstPopup.Controls.Add(new Panel { Name = $"popupContainer{i}", AccessibleName = "ScopeRoot" });
                }
                firstPopup.Show();
                windows.Add((firstPopup.Handle, fx.ProcessId, firstPopup.Text));
            }
            popup = new Form { Name = "ScopeRoot", Text = "Exact Root Popup" };
            popup.Controls.Add(new TextBox { Name = "popupValue", Text = "exact-root-value" });
            popup.Show();
            windows.Add((popup.Handle, fx.ProcessId, popup.Text));
        });
        try
        {
            // Fix window order for the capped case; all elements still use live UIA providers.
            if (earlierPopup) { UiAutomationService.s_getAllAppWindows = (_, _) => windows; }
            var svc = NewService();
            var target = SessionFor(fx, explicitWindow: false);
            var selector = new UiSelector { Root = new() { Query = "ScopeRoot" }, ControlType = "Edit" };
            var matches = await svc.SearchAsync(target, selector, 1, CancellationToken.None);
            Assert.HasCount(1, matches);
            Assert.AreEqual("popupValue", matches[0].AutomationId);
            Assert.AreEqual((long)windows[^1].Hwnd, matches[0].WindowHandle);
            Assert.AreEqual("exact-root-value", await svc.GetTextAsync(target, matches[0], CancellationToken.None));
            if (mainMatches == 1)
            {
                Assert.IsEmpty(await svc.SearchAsync(SessionFor(fx), selector, 1, CancellationToken.None));
            }
            else
            {
                await Assert.ThrowsAsync<UiAmbiguousSelectorException>(() =>
                    svc.SearchAsync(SessionFor(fx), selector, 1, CancellationToken.None));
            }
        }
        finally
        {
            fx.OnUiThread(() =>
            {
                popup.Dispose();
                firstPopup?.Dispose();
            });
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Query_ExactRootsAcrossWindowsRemainAmbiguous(bool mainExact)
    {
        using var fx = new UiaTestFixture();
        Form popup = null!;
        Form? secondPopup = null;
        fx.OnUiThread(() =>
        {
            fx.Form.Controls.Add(new Panel { Name = mainExact ? "ScopeRoot" : "mainContainer", AccessibleName = "ScopeRoot" });
            popup = new Form { Name = "ScopeRoot", Text = "Exact Root Popup" };
            popup.Controls.Add(new TextBox { Name = "popupValue", Text = "popup" });
            popup.Show();
            if (!mainExact)
            {
                secondPopup = new Form { Name = "ScopeRoot", Text = "Second Exact Root Popup" };
                secondPopup.Show();
            }
        });
        try
        {
            var svc = NewService();
            await Assert.ThrowsAsync<UiAmbiguousSelectorException>(() => svc.SearchAsync(
                SessionFor(fx, explicitWindow: false),
                new UiSelector { Root = new() { Query = "ScopeRoot" }, ControlType = "Edit" },
                1, CancellationToken.None));
        }
        finally
        {
            fx.OnUiThread(() =>
            {
                popup.Dispose();
                secondPopup?.Dispose();
            });
        }
    }

    [TestMethod]
    public async Task Query_Performance_RealProviderBaselineAndPredicates()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        var value = await ResolveAsync(svc, target, "lblInsideInvoke");
        var cases = new (string Name, UiSelector Selector)[]
        {
            ("baseline", new() { Query = "lblInsideInvoke" }),
            ("type", new() { Query = "lblInsideInvoke", ControlType = "Text" }),
            ("class", new() { Query = "lblInsideInvoke", ClassName = value.ClassName }),
            ("root+type+class", new() { Query = "lblInsideInvoke", Root = new() { Query = "pnlInsideInvoke" }, ControlType = "Text", ClassName = value.ClassName }),
        };
        foreach (var (name, selector) in cases)
        {
            for (var i = 0; i < 3; i++) { await svc.SearchAsync(target, selector, 50, CancellationToken.None); }
            var times = new List<double>();
            for (var i = 0; i < 20; i++)
            {
                var sw = Stopwatch.StartNew();
                var results = await svc.SearchAsync(target, selector, 50, CancellationToken.None);
                sw.Stop();
                Assert.HasCount(1, results);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            Console.WriteLine($"QUERY PERF {name}: n=20 warmup=3 median={times[10]:F3}ms p95={times[18]:F3}ms mean={times.Average():F3}ms");
        }
    }
}

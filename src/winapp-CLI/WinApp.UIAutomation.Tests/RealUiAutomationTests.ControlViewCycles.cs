// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

public partial class RealUiAutomationTests
{
    [TestMethod]
    [DataRow("zzznotthere", 0)]
    [DataRow("SendButton", 1)]
    public async Task SearchAsync_ControlViewCycleThroughWebView2_Terminates(string query, int expected)
    {
        var tree = FakeControlView.WebView2Cycle();
        var svc = NewService();

        // Without cycle protection the walk never ends; the deadline turns that into a failure.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var results = await svc.SearchAsync(tree.Target, new UiSelector { Query = query }, 50, deadline.Token);

        Assert.HasCount(expected, results);
        tree.AssertEachElementExpandedOnce();
    }

    [TestMethod]
    public async Task FindSingleElementAsync_RequireUnique_ControlViewCycleThroughWebView2_Terminates()
    {
        var tree = FakeControlView.WebView2Cycle();
        var svc = NewService();

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await svc.FindSingleElementAsync(
            tree.Target, new UiSelector { Query = "zzznotthere" }, requireUnique: true, deadline.Token);

        Assert.IsNull(result);
        tree.AssertEachElementExpandedOnce();
    }

    [TestMethod]
    public async Task InspectAsync_ControlViewCycleThroughWebView2_ListsEachElementOnce()
    {
        var tree = FakeControlView.WebView2Cycle();
        var svc = NewService();

        var elements = await svc.InspectAsync(tree.Target, null, 40, CancellationToken.None);

        CollectionAssert.AreEquivalent(
            tree.AutomationIds,
            elements.Select(element => element.AutomationId).ToArray());
    }

    [TestMethod]
    public async Task SearchAsync_WindowlessRoot_KeepsSiblingsThatReportTheHostWindow()
    {
        // A scoped walk starts at a windowless pane whose two controls report the host window's handle.
        var tree = new FakeControlView();
        var window = tree.Element("window", 0x100);
        var pane = tree.Element("pane", 0);
        var first = tree.Element("match-first", 0x100);
        var second = tree.Element("match-second", 0x100);
        tree.Link(window, pane);
        tree.Link(pane, first, second);
        tree.Install(pane);
        var svc = NewService();

        var results = await svc.SearchAsync(tree.Target, new UiSelector { Query = "match" }, 50, CancellationToken.None);

        Assert.AreEqual(
            "match-first,match-second",
            string.Join(",", results.Select(result => result.AutomationId).Order()));
    }

    [TestMethod]
    public async Task InspectAsync_WindowFirstReachedAtDepthLimit_ExpandsThroughShorterPath()
    {
        // The browser window is reached at depth 3 through a pane chain, and later at depth 1.
        var tree = new FakeControlView();
        var window = tree.Element("window", 0x100);
        var outer = tree.Element("outer", 0);
        var inner = tree.Element("inner", 0);
        var browser = tree.Element("browser", 0x300);
        var page = tree.Element("page", 0);
        tree.Link(window, outer, browser);
        tree.Link(outer, inner);
        tree.Link(inner, browser);
        tree.Link(browser, page);
        tree.Install(window);
        var svc = NewService();

        var elements = await svc.InspectAsync(tree.Target, null, 3, CancellationToken.None);

        var expanded = elements.Single(element => element.AutomationId == "page");
        Assert.AreEqual(2, expanded.Depth);
    }

    /// <summary>
    /// A synthetic Control View. Navigation depends only on the element, as in UIA, so one element
    /// can be listed under more than one parent.
    /// </summary>
    private sealed class FakeControlView
    {
        private readonly Dictionary<IUIAutomationElement, IUIAutomationElement> _firstChild = [];
        private readonly Dictionary<IUIAutomationElement, IUIAutomationElement> _nextSibling = [];
        private readonly Dictionary<IUIAutomationElement, IUIAutomationElement> _parent = [];
        private readonly Dictionary<IUIAutomationElement, int> _expansions = [];
        private readonly List<(string AutomationId, IUIAutomationElement Element)> _elements = [];
        private IUIAutomationElement? _root;

        public UiTarget Target { get; } = new()
        {
            ProcessId = Environment.ProcessId,
            ProcessName = "fake-control-view",
            WindowHandle = 0x100,
            IsExplicitWindow = true,
        };

        public string[] AutomationIds => _elements.Select(element => element.AutomationId).ToArray();

        /// <summary>
        /// A WinUI 3 window with a WebView2: the WebView2's children include the window's XAML
        /// input site, which contains the WebView2 again.
        /// </summary>
        public static FakeControlView WebView2Cycle()
        {
            var tree = new FakeControlView();
            var window = tree.Element("window", 0x100);
            var bridge = tree.Element("bridge", 0x200);
            var browser = tree.Element("browser", 0x300);
            var inputSite = tree.Element("inputSite", 0x400);
            var webView = tree.Element("webView", 0x500);
            var send = tree.Element("SendButton", 0);
            tree.Link(window, bridge);
            tree.Link(bridge, browser, inputSite);
            tree.Link(inputSite, webView, send);
            // The WebView2 lists the browser window, whose next sibling is the input site.
            tree._firstChild[webView] = browser;
            tree.Install(window);
            return tree;
        }

        public IUIAutomationElement Element(string automationId, nint hwnd)
        {
            var element = ComProxy<IUIAutomationElement>((method, _) => method.Name switch
            {
                "FindAll" => ElementArray(),
                "get_CurrentNativeWindowHandle" => new HWND(hwnd),
                "get_CurrentAutomationId" => StringBstr(automationId),
                "get_CurrentName" => StringBstr(automationId),
                "get_CurrentClassName" => EmptyBstr(),
                "get_CurrentControlType" => UIA_CONTROLTYPE_ID.UIA_PaneControlTypeId,
                "get_CurrentBoundingRectangle" => new RECT { left = 1, top = 2, right = 11, bottom = 12 },
                "get_CurrentIsEnabled" => new BOOL(true),
                "get_CurrentIsOffscreen" => new BOOL(false),
                "GetCurrentPattern" => null,
                _ => ThrowCom(),
            });
            _elements.Add((automationId, element));
            return element;
        }

        public void Link(IUIAutomationElement parent, params IUIAutomationElement[] children)
        {
            _firstChild[parent] = children[0];
            for (var i = 0; i < children.Length; i++)
            {
                _parent[children[i]] = parent;
                if (i + 1 < children.Length)
                {
                    _nextSibling[children[i]] = children[i + 1];
                }
            }
        }

        public void Install(IUIAutomationElement root)
        {
            _root = root;
            var walker = ComProxy<IUIAutomationTreeWalker>((method, args) =>
            {
                var element = (IUIAutomationElement)args![0]!;
                switch (method.Name)
                {
                    case "GetFirstChildElement":
                        _expansions[element] = _expansions.GetValueOrDefault(element) + 1;
                        return _firstChild.GetValueOrDefault(element);
                    case "GetNextSiblingElement":
                        return _nextSibling.GetValueOrDefault(element);
                    case "GetParentElement":
                        return _parent.GetValueOrDefault(element);
                    default:
                        return ThrowCom();
                }
            });

            UiAutomationService.s_getRootElement = (_, _, _) => root;
            UiAutomationService.s_findAllDescendants = (_, _) => ElementArray();
            UiAutomationService.s_getControlViewWalker = _ => walker;
            UiAutomationService.s_getExplicitIdentityWalker = _ => walker;
            UiAutomationService.s_elementFromHandle = (_, _) => root;
            UiAutomationService.s_findInvokableAncestor = (_, _, _) => null;
        }

        public void AssertEachElementExpandedOnce()
        {
            foreach (var (automationId, element) in _elements.Where(entry => entry.Element != _root))
            {
                Assert.IsTrue(_expansions.GetValueOrDefault(element) <= 1, $"{automationId} children were read more than once.");
            }
        }
    }
}

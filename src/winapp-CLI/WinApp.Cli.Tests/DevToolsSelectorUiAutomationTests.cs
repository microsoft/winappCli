// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public class DevToolsSelectorUiAutomationTests
{
    [TestMethod]
    public void IsDevToolsSelector_RecognisesOnlyDevToolsForms()
    {
        Assert.IsTrue(DevToolsSelectorUiAutomation.IsDevToolsSelector("<Counter>/Button"));
        Assert.IsTrue(DevToolsSelectorUiAutomation.IsDevToolsSelector("key=save"));
        Assert.IsTrue(DevToolsSelectorUiAutomation.IsDevToolsSelector("App.cs:35"));
        Assert.IsTrue(DevToolsSelectorUiAutomation.IsDevToolsSelector("#incrementButton"));
        Assert.IsTrue(DevToolsSelectorUiAutomation.IsDevToolsSelector("button-baacfbdb21"), "a DevTools slug, as an ambiguity error lists it");
        Assert.IsFalse(DevToolsSelectorUiAutomation.IsDevToolsSelector("Save"));
        Assert.IsFalse(DevToolsSelectorUiAutomation.IsDevToolsSelector("btn-save-c4b9"), "a UI Automation slug");
        Assert.IsFalse(DevToolsSelectorUiAutomation.IsDevToolsSelector("#"));
        Assert.IsFalse(DevToolsSelectorUiAutomation.IsDevToolsSelector(null));

        Assert.IsTrue(DevToolsSelectorUiAutomation.IsCSharpName("incrementButton"));
        Assert.IsFalse(DevToolsSelectorUiAutomation.IsCSharpName("Save As"));
        Assert.IsFalse(DevToolsSelectorUiAutomation.IsCSharpName("btn-save-c4b9"));
    }

    [TestMethod]
    public void Match_TheOnlyElementWithTheAutomationId_IsTheMatch_EvenScrolledOutOfView()
    {
        // Scrolled out of its ScrollViewer, UI Automation reports no rectangle for the element, so geometry cannot find it.
        var candidates = new[]
        {
            new UiElement { Selector = "btn-other-1111", AutomationId = "other", X = 615, Y = 489, Width = 84, Height = 48 },
            new UiElement { Selector = "btn-save-2222", AutomationId = "save", IsOffscreen = true },
        };

        Assert.AreEqual("btn-save-2222", DevToolsSelectorUiAutomation.Match(candidates, "save", null, (615, 1489, 84, 48))?.Selector);
        Assert.AreEqual("btn-other-1111", DevToolsSelectorUiAutomation.Match(candidates, null, null, (615, 489, 84, 48))?.Selector,
            "CONTROL: without an AutomationId, the element at the rectangle is the match");
    }

    [TestMethod]
    public void Match_ScrolledOutOfView_TheOnlyElementWithItsNameAndNoRectangle_IsTheMatch()
    {
        // A chart below the fold: DevTools knows where it is, UI Automation reports it offscreen with an empty rectangle.
        var candidates = new[]
        {
            new UiElement { Selector = "grp-regions-1111", Name = "Regions", X = 137, Y = 300, Width = 220, Height = 220 },
            new UiElement { Selector = "grp-regions-2222", Name = "Regions", IsOffscreen = true },
            new UiElement { Selector = "grp-sales-3333", Name = "Sales", IsOffscreen = true },
        };

        Assert.AreEqual("grp-regions-2222", DevToolsSelectorUiAutomation.Match(candidates, null, "Regions", (137, 1161, 220, 220))?.Selector);
        Assert.IsNull(DevToolsSelectorUiAutomation.Match(candidates, null, null, (137, 1161, 220, 220)), "CONTROL: without a name there is nothing to go on");

        var twoHidden = candidates.Append(new UiElement { Selector = "grp-regions-4444", Name = "Regions", IsOffscreen = true }).ToArray();
        Assert.IsNull(DevToolsSelectorUiAutomation.Match(twoHidden, null, "Regions", (137, 1161, 220, 220)), "two hidden with the name is no match, not the first");
    }

    [TestMethod]
    public void Match_PartlyScrolledOutOfView_TheElementShowingPartOfTheRectangle_IsTheMatch()
    {
        // UI Automation reports only the part of the chart still inside its scroll viewer (and a few pixels wider).
        var candidates = new[] { new UiElement { Selector = "grp-sales-1111", Name = "Sales", X = 175, Y = 875, Width = 424, Height = 27 } };

        Assert.AreEqual("grp-sales-1111", DevToolsSelectorUiAutomation.Match(candidates, null, "Sales", (175, 875, 420, 200))?.Selector);
        Assert.IsNull(DevToolsSelectorUiAutomation.Match(candidates, null, "Sales", (700, 875, 420, 200)), "CONTROL: elsewhere on screen is not it");
    }

    /// <summary>
    /// A Reactor AppBarButton("Copy", ...) in a collapsed CommandBar: DevTools reports its full 68x64 box, UI Automation
    /// the 68x48 part the bar shows. Its UI Automation name comes from its Label, not AutomationProperties.Name.
    /// </summary>
    [TestMethod]
    public void Match_CommandClippedByItsBar_IsFoundByTheNameItsLabelGives()
    {
        static DevToolsPropertyRow Row(string name, string value) => new(name, value, "String", null, null, null, null, null, null);
        var name = DevToolsSelectorUiAutomation.UiAutomationName([Row("AutomationProperties.Name", ""), Row("Label", "Copy")]);
        Assert.AreEqual("Copy", name);
        Assert.AreEqual("Alpha", DevToolsSelectorUiAutomation.UiAutomationName([Row("Text", "Alpha")]), "a menu item's Text");
        Assert.AreEqual("Save", DevToolsSelectorUiAutomation.UiAutomationName([Row("AutomationProperties.Name", "Save"), Row("Label", "Copy")]),
            "an explicit AutomationProperties.Name wins");

        var candidates = new[]
        {
            new UiElement { Selector = "btn-cut-53c2", Name = "Cut", X = 836, Y = 270, Width = 68, Height = 48 },
            new UiElement { Selector = "btn-copy-53c4", Name = "Copy", X = 904, Y = 270, Width = 68, Height = 48 },
        };
        Assert.AreEqual("btn-copy-53c4", DevToolsSelectorUiAutomation.Match(candidates, null, name, (904, 270, 68, 64))?.Selector);
        Assert.IsNull(DevToolsSelectorUiAutomation.Match(candidates, null, null, (904, 270, 68, 64)),
            "CONTROL: without the name, the clipped rectangle alone does not match");
    }

    /// <summary>
    /// A Reactor MenuItem("Alpha", ...) in an open MenuFlyout: UI Automation's rectangle is a few pixels inside the
    /// element's, and the item's own label TextBlock is also named "Alpha". The item is the nearer of the two.
    /// </summary>
    [TestMethod]
    public void Match_MenuItemAndItsLabelShareTheName_TheNearerRectangleIsTheItem()
    {
        var candidates = new[]
        {
            new UiElement { Selector = "lbl-textblock-0673", Name = "Alpha", X = 254, Y = 521, Width = 66, Height = 19 },
            new UiElement { Selector = "mnu-alpha-0674", Name = "Alpha", X = 243, Y = 517, Width = 88, Height = 28 },
        };
        Assert.AreEqual("mnu-alpha-0674", DevToolsSelectorUiAutomation.Match(candidates, null, "Alpha", (239, 515, 96, 32))?.Selector);

        var oneOffscreen = candidates.Append(new UiElement { Selector = "mnu-alpha-9999", Name = "Alpha", IsOffscreen = true }).ToArray();
        Assert.IsNull(DevToolsSelectorUiAutomation.Match(oneOffscreen, null, "Alpha", (239, 515, 96, 32)),
            "CONTROL: an off-screen element with the name could be it, so the nearer rectangle does not decide");
    }

    [TestMethod]
    public void Match_SeveralWithTheAutomationId_TheRectangleDecides()
    {
        var candidates = new[]
        {
            new UiElement { Selector = "btn-row-1111", AutomationId = "row", X = 10, Y = 10, Width = 80, Height = 30 },
            new UiElement { Selector = "btn-row-2222", AutomationId = "row", X = 10, Y = 50, Width = 80, Height = 30 },
        };

        Assert.AreEqual("btn-row-2222", DevToolsSelectorUiAutomation.Match(candidates, "row", null, (10, 50, 80, 30))?.Selector);
        Assert.IsNull(DevToolsSelectorUiAutomation.Match(candidates, "row", null, null), "two with the id and no rectangle is no match, not the first");
    }

    [TestMethod]
    public void Closest_PicksTheElementWhoseBoundsMatch()
    {
        UiElement At(string slug, double x, double y, double w, double h) => new() { Selector = slug, X = x, Y = y, Width = w, Height = h };
        var candidates = new[] { At("btn-a", 10, 10, 40, 32), At("btn-b", 60, 10, 40, 32), At("btn-c", 61, 11, 41, 32) };

        Assert.AreEqual("btn-c", DevToolsSelectorUiAutomation.Closest(candidates, (61, 11, 40.5, 32))?.Selector);
        Assert.AreEqual("btn-a", DevToolsSelectorUiAutomation.Closest(candidates, (11, 9, 40, 32))?.Selector);
        Assert.IsNull(DevToolsSelectorUiAutomation.Closest(candidates, (200, 200, 40, 32)), "nothing within tolerance is no match, not the nearest");
    }

    [TestMethod]
    public void ScreenRect_UsesTheMonitorScale_NotTheWindowsDpi()
    {
        // A DPI-unaware window on a 150% monitor reports 96 DPI, but Windows stretches it 1.5x: DevTools' 56x32 DIP
        // button at (110,56) is the 84x48 UI Automation element at (645,599) when the client origin is (480,515).
        Assert.AreEqual((645.0, 599.0, 84.0, 48.0), DevToolsSelectorUiAutomation.ScreenRect((480, 515), 144, (110, 56, 56, 32)));
        Assert.AreEqual((120.0, 66.0, 56.0, 32.0), DevToolsSelectorUiAutomation.ScreenRect((10, 10), 96, (110, 56, 56, 32)));
    }

    /// <summary>
    /// An element inside a XAML island (or a ReactorHostControl) has an offset relative to the island, not the window.
    /// The engine reports its rectangle on screen, so the UI Automation element there is the match, not the one at
    /// the island offset from the window's corner.
    /// </summary>
    [TestMethod]
    public async Task ReactorSelector_MatchesTheElementAtItsScreenRect_WhenItsRootIsOffsetInTheWindow()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("Reactor.describe", """{"state":"ok","nodes":[{"handle":"42","element":"Button","key":"save","file":0,"line":9}],"files":["C:\\src\\App.cs"],"unmapped":0,"truncated":false}""")
            .Answer("Layout.get", """{"handle":"42","render":{"w":56,"h":32},"offset":{"x":16,"y":42.67},"screen":{"x":615,"y":489,"w":84,"h":48}}""")
            .Answer("Property.get", """{"handle":"42","props":[]}""");
        var inner = Proxy((method, args) => method.Name switch
        {
            nameof(IUiAutomation.FindWindowsByPid) => new List<(nint, int, string)> { (4242, Environment.ProcessId, "Form") },
            nameof(IUiAutomation.InspectAsync) => Task.FromResult(new[]
            {
                new UiElement { Selector = "btn-wrong-1111", X = 24, Y = 64, Width = 84, Height = 48 },
                new UiElement { Selector = "btn-save-2222", X = 615, Y = 489, Width = 84, Height = 48 },
            }),
            nameof(IUiAutomation.FindSingleElementAsync) => Task.FromResult<UiElement?>(new UiElement { Selector = ((UiSelector)args![1]!).Slug }),
            _ => throw new NotSupportedException(method.Name),
        });
        var bridge = new DevToolsSelectorUiAutomation(inner);
        var target = new UiTarget { ProcessId = Environment.ProcessId, WindowHandle = 0 };

        var found = await bridge.FindSingleElementAsync(target, new UiSelector { Query = "key=save" }, CancellationToken.None);

        Assert.AreEqual("btn-save-2222", found?.Selector);
    }

    [TestMethod]
    public async Task CSharpName_SelectsTheReactorElementAssignedToIt_ByItsAutomationId()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.find", """{"matches":[{"handle":"42","name":"","type":"Microsoft.UI.Xaml.Controls.Button","reactor":{"element":"Button","name":"incrementButton"}},{"handle":"43","name":"","type":"Microsoft.UI.Xaml.Controls.TextBlock","reactor":{"element":"TextBlock","name":"incrementButtonLabel"}}]}""")
            .Answer("Layout.get", """{"handle":"42","render":{"w":56,"h":32},"offset":{"x":16,"y":900},"screen":{"x":615,"y":1489,"w":84,"h":48}}""")
            .Answer("Property.get", """{"handle":"42","props":[{"name":"AutomationProperties.AutomationId","value":"inc","valueType":"String"}]}""");
        UiSelector? searched = null;
        var inner = Proxy((method, args) => method.Name switch
        {
            nameof(IUiAutomation.FindWindowsByPid) => new List<(nint, int, string)> { (4242, Environment.ProcessId, "Form") },
            nameof(IUiAutomation.SearchAsync) => Task.FromResult(new[]
            {
                new UiElement { Selector = "btn-incoming-1111", AutomationId = "incoming", X = 10, Y = 10, Width = 84, Height = 48 },
                new UiElement { Selector = "btn-inc-2222", AutomationId = "inc", IsOffscreen = true },
            }),
            nameof(IUiAutomation.FindSingleElementAsync) => Task.FromResult<UiElement?>(new UiElement { Selector = (searched = (UiSelector)args![1]!).Slug }),
            _ => throw new NotSupportedException(method.Name),
        });
        var bridge = new DevToolsSelectorUiAutomation(inner);
        var target = new UiTarget { ProcessId = Environment.ProcessId, WindowHandle = 0 };

        var found = await bridge.FindSingleElementAsync(target, new UiSelector { Query = "incrementButton" }, CancellationToken.None);

        Assert.AreEqual("btn-inc-2222", found?.Selector);
        Assert.IsNull(searched?.Query);
    }

    [TestMethod]
    public async Task HashName_IsTheReactorElementsCSharpName()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.find", """{"matches":[{"handle":"42","name":"","type":"Microsoft.UI.Xaml.Controls.Button","reactor":{"element":"Button","name":"incrementButton"}}]}""")
            .Answer("Layout.get", """{"handle":"42","render":{"w":56,"h":32},"offset":{"x":16,"y":42},"screen":{"x":615,"y":489,"w":84,"h":48}}""")
            .Answer("Property.get", """{"handle":"42","props":[]}""");
        var inner = Proxy((method, args) => method.Name switch
        {
            nameof(IUiAutomation.FindWindowsByPid) => new List<(nint, int, string)> { (4242, Environment.ProcessId, "Form") },
            nameof(IUiAutomation.InspectAsync) => Task.FromResult(new[] { new UiElement { Selector = "btn-plus1-2222", X = 615, Y = 489, Width = 84, Height = 48 } }),
            nameof(IUiAutomation.FindSingleElementAsync) => Task.FromResult<UiElement?>(new UiElement { Selector = ((UiSelector)args![1]!).Slug }),
            _ => throw new NotSupportedException(method.Name),
        });
        var bridge = new DevToolsSelectorUiAutomation(inner);
        var target = new UiTarget { ProcessId = Environment.ProcessId, WindowHandle = 0 };

        var found = await bridge.FindSingleElementAsync(target, new UiSelector { Query = "#incrementButton" }, CancellationToken.None);

        Assert.AreEqual("btn-plus1-2222", found?.Selector);
    }

    [TestMethod]
    public async Task AWordThatIsNoReactorElementsCSharpName_KeepsItsUiMeaning()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.find", """{"matches":[{"handle":"42","name":"Save","type":"Microsoft.UI.Xaml.Controls.Button"}]}""");
        UiSelector? seen = null;
        var inner = Proxy((method, args) =>
        {
            if (method.Name == nameof(IUiAutomation.FindSingleElementAsync))
            {
                seen = (UiSelector)args![1]!;
                return Task.FromResult<UiElement?>(null);
            }
            throw new NotSupportedException(method.Name);
        });
        var bridge = new DevToolsSelectorUiAutomation(inner);
        var target = new UiTarget { ProcessId = Environment.ProcessId, WindowHandle = 0 };

        await bridge.FindSingleElementAsync(target, new UiSelector { Query = "Save" }, CancellationToken.None);

        Assert.AreEqual("Save", seen?.Query, "an x:Name is already an AutomationId to UI Automation");
        Assert.IsNull(seen?.Slug);
    }

    [TestMethod]
    public async Task Inspect_LooksInTheWindowDevToolsFoundTheElementIn()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("Reactor.describe", """{"state":"ok","nodes":[{"handle":"42","element":"Button","key":"tools","file":0,"line":9}],"files":["C:\\src\\App.cs"],"unmapped":0,"truncated":false}""")
            .Answer("Layout.get", """{"handle":"42","render":{"w":122,"h":32},"offset":{"x":12,"y":328},"screen":{"x":150,"y":489,"w":122,"h":32}}""")
            .Answer("Property.get", """{"handle":"42","props":[]}""");
        var inspected = new List<(long Window, string? Id)>();
        var inner = Proxy((method, args) =>
        {
            switch (method.Name)
            {
                case nameof(IUiAutomation.FindWindowsByPid):
                    return new List<(nint, int, string)> { (1111, Environment.ProcessId, "Main"), (2222, Environment.ProcessId, "Tools") };
                case nameof(IUiAutomation.InspectAsync):
                    var t = (UiTarget)args![0]!;
                    inspected.Add((t.WindowHandle, (string?)args[1]));
                    return Task.FromResult(t.WindowHandle == 2222
                        ? new[] { new UiElement { Selector = "btn-tools-2222", X = 150, Y = 489, Width = 122, Height = 32 } }
                        : new[] { new UiElement { Selector = "win-main-1111", X = 104, Y = 104, Width = 1400, Height = 900 } });
                default:
                    throw new NotSupportedException(method.Name);
            }
        });
        var bridge = new DevToolsSelectorUiAutomation(inner);
        var target = new UiTarget { ProcessId = Environment.ProcessId, WindowHandle = 1111 };

        await bridge.InspectAsync(target, "key=tools", 0, CancellationToken.None);

        Assert.AreEqual((2222L, "btn-tools-2222"), inspected[^1], "the auto-selected main window does not hold the Tools window's button");
    }

    [TestMethod]
    public async Task WithoutDevTools_TheSelectorKeepsItsUiMeaning()
    {
        UiSelector? seen = null;
        var inner = Proxy((method, args) =>
        {
            if (method.Name == nameof(IUiAutomation.FindSingleElementAsync))
            {
                seen = (UiSelector)args![1]!;
                return Task.FromResult<UiElement?>(null);
            }
            throw new NotSupportedException(method.Name);
        });
        var bridge = new DevToolsSelectorUiAutomation(inner);
        // A process with no DevTools pipe: "App.cs:35" is still a text search.
        var target = new UiTarget { ProcessId = int.MaxValue - 7, WindowHandle = 0 };

        await bridge.FindSingleElementAsync(target, new UiSelector { Query = "App.cs:35" }, CancellationToken.None);

        Assert.AreEqual("App.cs:35", seen?.Query);
        Assert.IsNull(seen?.Slug);
    }

    private static IUiAutomation Proxy(Func<MethodInfo, object?[]?, object?> handler)
    {
        var proxy = DispatchProxy.Create<IUiAutomation, Dispatch>();
        ((Dispatch)(object)proxy).Handler = handler;
        return proxy;
    }

    private class Dispatch : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = (_, _) => null;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }
}

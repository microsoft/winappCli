// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Windows.Forms;

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

// WinUI DevTools draws its toolbar into the app, under one pane whose AutomationId is DevToolsOverlay. A plain
// `winapp ui inspect` or `search` describes the app, so that subtree is left out; inspecting the pane itself still
// shows it. Screen readers are unaffected: only winapp ui filters it.
public partial class RealUiAutomationTests
{
    private static Panel AddDevToolsChrome(UiaTestFixture fx) => fx.OnUiThread(() =>
    {
        var pane = new Panel { Name = UiAutomationService.DevToolsChromeAutomationId, Width = 120, Height = 40, Left = 0, Top = 0 };
        pane.Controls.Add(new Button { Name = "ChromeButton", Text = "Chrome Button", Width = 100, Height = 30 });
        fx.Form.Controls.Add(pane);
        pane.BringToFront();
        return pane;
    });

    private static string Describe(IEnumerable<UiElement> elements) =>
        string.Join("\n", elements.Select(e => $"{e.Depth} {e.Selector ?? e.Id} {e.Type} {e.Name} {e.AutomationId}"));

    [TestMethod]
    public async Task Inspect_LeavesOutTheDevToolsChrome_SoTheAppsOwnElementsAreUnchanged()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        await ResolveAsync(svc, target, "btnInvoke");
        var without = await svc.InspectAsync(target, null, 40, CancellationToken.None);

        AddDevToolsChrome(fx);
        await ResolveAsync(svc, target, "ChromeButton");
        var with = await svc.InspectAsync(target, null, 40, CancellationToken.None);

        Assert.IsFalse(with.Any(e => e.AutomationId is "ChromeButton" or UiAutomationService.DevToolsChromeAutomationId),
            "the DevTools chrome is not part of the app's tree");
        Assert.AreEqual(Describe(without), Describe(with), "the app's own elements, selectors and depths are identical");

        var chrome = await svc.InspectAsync(target, UiAutomationService.DevToolsChromeAutomationId, 5, CancellationToken.None);
        Assert.IsTrue(chrome.Any(e => e.AutomationId == "ChromeButton"), "inspecting the chrome itself still shows it");
    }

    [TestMethod]
    public async Task Search_LeavesOutTheDevToolsChrome_ButActionsStillReachIt()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        await ResolveAsync(svc, target, "btnInvoke");
        var before = await svc.SearchAsync(target, new UiSelector { Query = "Button" }, 50, CancellationToken.None);

        AddDevToolsChrome(fx);
        await ResolveAsync(svc, target, "ChromeButton");

        Assert.IsEmpty(await svc.SearchAsync(target, new UiSelector { Query = "Chrome" }, 50, CancellationToken.None),
            "search does not report DevTools chrome");
        var after = await svc.SearchAsync(target, new UiSelector { Query = "Button" }, 50, CancellationToken.None);
        Assert.AreEqual(Describe(before), Describe(after), "search results for the app are unchanged");
        Assert.IsNotNull(await svc.FindSingleElementAsync(target, new UiSelector { Query = "ChromeButton" }, CancellationToken.None),
            "an explicit selector still reaches the chrome, so it can be clicked");
    }
}

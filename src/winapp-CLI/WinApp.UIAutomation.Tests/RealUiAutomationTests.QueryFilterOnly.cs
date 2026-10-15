// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;
using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

public partial class RealUiAutomationTests
{
    [TestMethod]
    public async Task QueryFilterOnly_ClassNameAlone_MatchesEveryElementOfThatClass()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        var slider = await ResolveAsync(svc, target, "trkSlider");
        var button = await ResolveAsync(svc, target, "btnInvoke");

        var unique = new UiSelector { ClassName = slider.ClassName!.ToUpperInvariant() };
        var sliderMatches = await svc.SearchAsync(target, unique, 50, CancellationToken.None);
        Assert.HasCount(1, sliderMatches);
        Assert.AreEqual("trkSlider", sliderMatches[0].AutomationId);
        Assert.AreEqual("trkSlider",
            (await svc.FindSingleElementAsync(target, unique, requireUnique: true, CancellationToken.None))?.AutomationId);

        var shared = new UiSelector { ClassName = button.ClassName };
        var buttonMatches = await svc.SearchAsync(target, shared, 50, CancellationToken.None);
        Assert.IsTrue(buttonMatches.Length > 1, "The fixture has several buttons with the same class.");
        Assert.IsTrue(buttonMatches.All(m => string.Equals(m.ClassName, button.ClassName, StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(buttonMatches.Any(m => m.AutomationId == "btnInvoke"));
        await Assert.ThrowsAsync<UiAmbiguousSelectorException>(() =>
            svc.FindSingleElementAsync(target, shared, requireUnique: true, CancellationToken.None));
        await Assert.ThrowsAsync<UiAmbiguousSelectorException>(() =>
            svc.FindSingleElementAsync(target, shared, CancellationToken.None));

        Assert.IsEmpty(await svc.SearchAsync(target, new UiSelector { ClassName = "*" }, 50, CancellationToken.None));
    }

    [TestMethod]
    public async Task QueryFilterOnly_AmbiguousAction_ListsTheFirstMatchesWithSelectors()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        var query = new UiSelector { ControlType = "Button" };
        var all = await svc.SearchAsync(target, query, 1000, CancellationToken.None);
        Assert.IsTrue(all.Length > 5, "The fixture needs more than five buttons to exercise the overflow line.");

        var error = await Assert.ThrowsAsync<UiAmbiguousSelectorException>(() =>
            svc.FindSingleElementAsync(target, query, requireUnique: true, CancellationToken.None));

        var lines = error.Message.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.AreEqual($"Selector matched {all.Length} elements:", lines[0]);
        var listed = lines.Skip(1).Take(5).ToArray();
        Assert.IsTrue(listed.All(l => l.StartsWith("  Button \"", StringComparison.Ordinal) && l.Contains("  -> ")),
            error.Message);
        Assert.AreEqual($"  ... and {all.Length - 5} more", lines[6]);
        Assert.AreEqual("Use a unique slug from 'inspect' or narrow --root, --type, or --class-name.", lines[7]);

        // Each listed selector must be one the user can pass straight back.
        var slug = listed[0][(listed[0].LastIndexOf("-> ", StringComparison.Ordinal) + 3)..];
        var picked = await svc.FindSingleElementAsync(target, new UiSelector { Slug = slug }, requireUnique: true, CancellationToken.None);
        Assert.AreEqual("Button", picked?.Type);
    }

    [TestMethod]
    public async Task QueryFilterOnly_AmbiguousRead_ListsEveryMatchCountLikeActions()
    {
        // get-value, get-property and wait-for use the read lookup, which stops at a second match.
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        var query = new UiSelector { ControlType = "Button" };
        var all = await svc.SearchAsync(target, query, 1000, CancellationToken.None);

        var read = await Assert.ThrowsAsync<UiAmbiguousSelectorException>(() =>
            svc.FindSingleElementAsync(target, query, CancellationToken.None));
        var action = await Assert.ThrowsAsync<UiAmbiguousSelectorException>(() =>
            svc.FindSingleElementAsync(target, query, requireUnique: true, CancellationToken.None));

        StringAssert.StartsWith(read.Message, $"Selector matched {all.Length} elements:");
        StringAssert.Contains(read.Message, $"  ... and {all.Length - 5} more");
        var readLines = read.Message.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var actionLines = action.Message.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.AreEqual(actionLines.Length, readLines.Length, read.Message);
        Assert.IsTrue(readLines.Skip(1).Take(5).All(l => l.StartsWith("  Button \"", StringComparison.Ordinal) && l.Contains("  -> ")),
            read.Message);
        Assert.AreEqual(actionLines[^1], readLines[^1], "reads and actions give the same advice");
    }

    [TestMethod]
    public async Task QueryFilterOnly_TypedText_ExactNameWinsBeforeTheAmbiguityListing()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        var query = new UiSelector { Query = "Child 1", ControlType = "Button" };

        // No element is named exactly "Child 1", so every substring match is listed for reads and actions.
        var read = await Assert.ThrowsAsync<UiAmbiguousSelectorException>(() =>
            svc.FindSingleElementAsync(target, query, CancellationToken.None));
        var action = await Assert.ThrowsAsync<UiAmbiguousSelectorException>(() =>
            svc.FindSingleElementAsync(target, query, requireUnique: true, CancellationToken.None));
        StringAssert.Contains(read.Message, "  ... and ");
        Assert.AreEqual(action.Message.Split('\n')[0], read.Message.Split('\n')[0], "reads and actions count the same matches");

        // One exact Name match is preferred before any ambiguity is reported.
        fx.OnUiThread(() => fx.ScrollPanel.Controls["pnlChild10"]!.AccessibleName = "Child 1");
        Assert.AreEqual("pnlChild10", (await svc.FindSingleElementAsync(target, query, CancellationToken.None))?.AutomationId);
        Assert.AreEqual("pnlChild10",
            (await svc.FindSingleElementAsync(target, query, requireUnique: true, CancellationToken.None))?.AutomationId);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task QueryFilterOnly_AmbiguousRead_ListingFailureKeepsTheAmbiguityError(bool invalidCast)
    {
        // The read finds two buttons in bulk, so only the uncapped listing walks the tree.
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var realWalker = UiAutomationService.s_getControlViewWalker(svc);
        Exception failure = invalidCast
            ? new InvalidCastException("Provider interrupted the walk.")
            : new System.Runtime.InteropServices.COMException("Provider interrupted the walk.", unchecked((int)0x80040201));
        UiAutomationService.s_getControlViewWalker = _ => ComProxy<IUIAutomationTreeWalker>(
            (method, args) => method.Name == "GetFirstChildElement" ? throw failure : method.Invoke(realWalker, args));

        var error = await Assert.ThrowsAsync<UiAmbiguousSelectorException>(() =>
            svc.FindSingleElementAsync(SessionFor(fx), new UiSelector { ControlType = "Button" }, CancellationToken.None));

        StringAssert.StartsWith(error.Message, "Selector matched 2 elements:");
    }

    [TestMethod]
    public async Task QueryFilterOnly_TypeAloneUnderRoot_MatchesOnlyThatSubtree()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var target = SessionFor(fx);
        await ResolveAsync(svc, target, "lblInsideInvoke");

        var query = new UiSelector { Root = new UiSelector { Query = "pnlInsideInvoke" }, ControlType = "TextBlock" };
        var matches = await svc.SearchAsync(target, query, 50, CancellationToken.None);
        Assert.HasCount(1, matches);
        Assert.AreEqual("lblInsideInvoke", matches[0].AutomationId);
        Assert.AreEqual("lblInsideInvoke",
            (await svc.FindSingleElementAsync(target, query, requireUnique: true, CancellationToken.None))?.AutomationId);
        Assert.IsEmpty(await svc.SearchAsync(target, query with { ControlType = "Button" }, 50, CancellationToken.None));
    }
}

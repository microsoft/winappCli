// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Windows.Forms;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

public partial class RealUiAutomationTests
{
    private const string ExactNameQuery = "Allodia Mail & Calendar";
    private static readonly string[] ExactNameListing = ["btnShared", "lblShared"];

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Query_ExactNameWinsOverLongerNameMatches(bool filtered, bool requireUnique)
    {
        using var fx = new UiaTestFixture();
        // The longer-named element is an invokable Button, so without the exact-Name preference the
        // unfiltered lookup would pick it, and the filtered or unique lookups would be ambiguous.
        fx.OnUiThread(() =>
        {
            fx.DupButton.AccessibleName = "Welcome to " + ExactNameQuery;
            fx.DupLabel.AccessibleName = ExactNameQuery;
        });
        var svc = NewService();
        var target = SessionFor(fx);
        var selector = new UiSelector
        {
            Query = ExactNameQuery.ToLowerInvariant(),
            Root = filtered ? new() { Query = "fixtureForm" } : null,
        };

        var found = await svc.FindSingleElementAsync(target, selector, requireUnique, CancellationToken.None);

        Assert.IsNotNull(found);
        Assert.AreEqual("lblShared", found.AutomationId);
        var listed = await svc.SearchAsync(target, selector, 10, CancellationToken.None);
        CollectionAssert.AreEquivalent(ExactNameListing, listed.Select(e => e.AutomationId).ToArray(),
            "search keeps listing every substring match.");
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Query_SeveralExactNameMatches_KeepAmbiguityRules(bool filtered, bool requireUnique)
    {
        using var fx = new UiaTestFixture();
        // Two labels have the exact Name, so neither wins; the only invokable match still does.
        fx.OnUiThread(() =>
        {
            fx.DupLabel.AccessibleName = ExactNameQuery;
            fx.TextLabel.AccessibleName = ExactNameQuery;
            fx.DupButton.AccessibleName = "Welcome to " + ExactNameQuery;
        });
        var svc = NewService();
        var target = SessionFor(fx);
        var selector = new UiSelector
        {
            Query = ExactNameQuery,
            Root = filtered ? new() { Query = "fixtureForm" } : null,
        };

        if (filtered || requireUnique)
        {
            await Assert.ThrowsExactlyAsync<UiAmbiguousSelectorException>(() =>
                svc.FindSingleElementAsync(target, selector, requireUnique, CancellationToken.None));
            return;
        }

        // Same as before this preference existed: the unfiltered lookup picks the only invokable match.
        var found = await svc.FindSingleElementAsync(target, selector, requireUnique, CancellationToken.None);
        Assert.IsNotNull(found);
        Assert.AreEqual("btnShared", found.AutomationId);
    }

    [TestMethod]
    public async Task Query_ExactNameWinsOnOwnedWindow()
    {
        using var fx = new UiaTestFixture();
        var (ownedHwnd, _) = fx.OpenOwnedWindow("Fixture Owned Window");
        fx.OnUiThread(() =>
        {
            var owned = Control.FromHandle(ownedHwnd)!;
            owned.Controls["btnOwnedShared"]!.AccessibleName = "Welcome to Owned Exact";
            owned.Controls["lblOwnedShared"]!.AccessibleName = "Owned Exact";
        });
        var svc = NewService();

        // Nothing on the main window matches, so this exercises the other-windows lookup.
        var found = await PollFindOtherWindowAsync(svc, NonExplicitSession(fx), "owned exact");

        Assert.AreEqual("lblOwnedShared", found.AutomationId);
        Assert.AreEqual((long)ownedHwnd, found.WindowHandle);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Query_UniqueLookupPrefersExactNameAcrossWindows(bool filtered)
    {
        using var fx = new UiaTestFixture();
        var (ownedHwnd, _) = fx.OpenOwnedWindow("Fixture Owned Window");
        fx.OnUiThread(() =>
        {
            fx.DupLabel.AccessibleName = "Welcome to Owned Exact";
            Control.FromHandle(ownedHwnd)!.Controls["lblOwnedShared"]!.AccessibleName = "Owned Exact";
        });
        var svc = NewService();
        var target = NonExplicitSession(fx);
        await PollSearchAsync(svc, target, "lblOwnedShared");
        var selector = new UiSelector { Query = "Owned Exact", ControlType = filtered ? "Text" : null };

        // A unique lookup counts matches in every app window, so the exact match in the owned
        // window wins over the longer name in the main window instead of making them ambiguous.
        var found = await svc.FindSingleElementAsync(target, selector, requireUnique: true, CancellationToken.None);

        Assert.IsNotNull(found);
        Assert.AreEqual("lblOwnedShared", found.AutomationId);
        Assert.AreEqual((long)ownedHwnd, found.WindowHandle);
    }
}

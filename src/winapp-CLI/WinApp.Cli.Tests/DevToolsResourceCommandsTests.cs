// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class DevToolsResourceCommandsTests
{
    private const string Ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

    private const string AppXaml =
        "<Application " + Ns + ">\n" +                                                   // 1
        "  <Application.Resources>\n" +                                                  // 2
        "    <ResourceDictionary>\n" +                                                   // 3
        "      <ResourceDictionary.ThemeDictionaries>\n" +                               // 4
        "        <ResourceDictionary x:Key=\"Light\">\n" +                               // 5
        "          <SolidColorBrush x:Key=\"CardBrush\" Color=\"White\" />\n" +          // 6
        "        </ResourceDictionary>\n" +                                              // 7
        "        <ResourceDictionary x:Key=\"Dark\">\n" +                                // 8
        "          <SolidColorBrush x:Key=\"CardBrush\" Color=\"Black\" />\n" +          // 9
        "        </ResourceDictionary>\n" +                                              // 10
        "      </ResourceDictionary.ThemeDictionaries>\n" +                              // 11
        "      <SolidColorBrush x:Key=\"AccentBrush\" Color=\"#FF0078D4\" />\n" +        // 12
        "    </ResourceDictionary>\n" +                                                  // 13
        "  </Application.Resources>\n" +                                                 // 14
        "</Application>";

    private const string PageXaml =
        "<Page " + Ns + ">\n" +
        "  <Page.Resources>\n" +
        "    <SolidColorBrush x:Key=\"AccentBrush\" Color=\"Red\" />\n" +
        "  </Page.Resources>\n" +
        "</Page>";

    private static XamlResourceIndex Index() => XamlResourceIndex.FromSources(
    [
        ("Pages/HomePage.xaml", PageXaml),
        ("App.xaml", AppXaml),
    ]);

    [TestMethod]
    public void ListParse_ReadsEntriesTotalsAndFlags()
    {
        var listing = DevToolsResourcesListCommand.Parse("""
            {"theme":"Light","resources":[
              {"key":"AccentBrush","value":"#FF0078D4","valueType":"SolidColorBrush","dictionary":"Application.Resources","framework":false,"overridden":true},
              {"key":"CardBrush","value":"#FFFFFFFF","valueType":"SolidColorBrush","dictionary":"Application.Resources/ThemeDictionaries[Light]","themeDictionary":"Light","framework":false,"overridden":false},
              {"key":"","value":"x"},
              {"key":"AccentFillColorDefaultBrush","value":"#FF005FB8","valueType":"SolidColorBrush","dictionary":"Application.Resources/MergedDictionaries[0] (XamlControlsResources)","framework":true}
            ],"total":2917,"truncated":true}
            """);

        Assert.IsNotNull(listing);
        Assert.AreEqual("Light", listing.Theme);
        Assert.AreEqual(2917, listing.Total);
        Assert.IsTrue(listing.Truncated);
        Assert.HasCount(3, listing.Entries, "Entries without a key are skipped.");
        Assert.IsTrue(listing.Entries[0].Overridden);
        Assert.AreEqual("Light", listing.Entries[1].ThemeDictionary);
        Assert.IsTrue(listing.Entries[2].Framework);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("not json")]
    [DataRow("[]")]
    [DataRow("{\"theme\":\"Light\"}")]
    public void ListParse_RejectsUnreadableResults(string? json) => Assert.IsNull(DevToolsResourcesListCommand.Parse(json));

    [TestMethod]
    public void DefinitionFor_MatchesThemeBranchAndPrefersAppScope()
    {
        var index = Index();

        var accent = DevToolsResourcesListCommand.DefinitionFor(index, "AccentBrush", null);
        Assert.AreEqual("App.xaml:12", DevToolsResourcesListCommand.Location(accent), "App.xaml wins over a page-local key.");

        Assert.AreEqual("App.xaml:6", DevToolsResourcesListCommand.Location(DevToolsResourcesListCommand.DefinitionFor(index, "CardBrush", "Light")));
        Assert.AreEqual("App.xaml:9", DevToolsResourcesListCommand.Location(DevToolsResourcesListCommand.DefinitionFor(index, "CardBrush", "dark")));
        Assert.IsNull(DevToolsResourcesListCommand.DefinitionFor(index, "CardBrush", null), "A theme key has no theme-less definition.");
        Assert.IsNull(DevToolsResourcesListCommand.DefinitionFor(index, "Missing", null));
        Assert.IsNull(DevToolsResourcesListCommand.DefinitionFor(null, "AccentBrush", null));
        Assert.IsNull(DevToolsResourcesListCommand.Location(null));
    }

    [TestMethod]
    public void Shown_ShortensLongValuesAndDropsTypeOnlyValues()
    {
        static DevToolsResourcesListCommand.Entry Entry(string value, string type) => new("K", value, type, "Application.Resources", null, false, false);

        Assert.AreEqual("#FF0078D4 ", DevToolsResourcesListCommand.Shown(Entry("#FF0078D4", "SolidColorBrush")));
        Assert.AreEqual(string.Empty, DevToolsResourcesListCommand.Shown(Entry("{Style}", "Style")));
        var path = DevToolsResourcesListCommand.Shown(Entry("M48.854 0C21.839 0 0 22 0 49.217c0 21.756 13.993 40.172 33.405 46.69", "String"));
        Assert.AreEqual(DevToolsResourcesListCommand.MaxShownValue + 1, path.Length);
        StringAssert.EndsWith(path, "… ");
        Assert.AreEqual("a b ", DevToolsResourcesListCommand.Shown(Entry("a\nb", "String")));
    }

    [TestMethod]
    public void SetParse_ReadsOutcome()
    {
        var outcome = DevToolsResourcesSetCommand.Parse("""
            {"key":"CardBrush","theme":"Light","dictionary":"Application.Resources/ThemeDictionaries[Dark]","themeDictionary":"Dark",
             "previous":"#FF000000","original":"#FF000000","value":"#FF00AA00","valueType":"SolidColorBrush","verified":true,"liveUpdate":true}
            """);

        Assert.IsNotNull(outcome);
        Assert.AreEqual("CardBrush", outcome.Key);
        Assert.AreEqual("Dark", outcome.ThemeDictionary);
        Assert.AreEqual("#FF00AA00", outcome.Value);
        Assert.IsTrue(outcome.Verified);
        Assert.IsTrue(outcome.LiveUpdate);
        Assert.IsNull(DevToolsResourcesSetCommand.Parse("{\"value\":\"1\"}"), "A result without a key is unreadable.");
        Assert.IsNull(DevToolsResourcesSetCommand.Parse("{"));
    }

    [TestMethod]
    public void SetNotes_ExplainLiveUpdateAndHowToKeepTheChange()
    {
        var outcome = new DevToolsResourcesSetCommand.Outcome(
            "AccentBrush", "Light", "Application.Resources", null, "#FF0078D4", "#FF0078D4", "#FFFF0000", "SolidColorBrush", true, true);

        var notes = DevToolsResourcesSetCommand.Notes(outcome, themeRequested: false, "App.xaml:12");

        Assert.HasCount(2, notes);
        StringAssert.Contains(notes[0], "{ThemeResource} or {StaticResource} now show the new value");
        StringAssert.Contains(notes[1], "To keep it, edit App.xaml:12.");
    }

    [TestMethod]
    public void SetNotes_WarnWithoutLiveUpdateAndNameTheOriginal()
    {
        var outcome = new DevToolsResourcesSetCommand.Outcome(
            "CardBrush", "Light", "Application.Resources/ThemeDictionaries[Dark]", "Dark", "#FF00AA00", "#FF000000", "#FF0000FF",
            "SolidColorBrush", true, false);

        var notes = DevToolsResourcesSetCommand.Notes(outcome, themeRequested: true, definedAt: null);

        StringAssert.Contains(notes[0], "Dark theme dictionary");
        StringAssert.Contains(notes[1], "winapp run --devtools");
        StringAssert.Contains(notes[2], "The app's own value is #FF000000");
        StringAssert.Contains(notes[3], "App.xaml is unchanged");
    }

    [TestMethod]
    public void ResetParse_SplitsRestoredAndFailed()
    {
        var parsed = DevToolsResourcesResetCommand.Parse("""
            {"restored":[{"key":"AccentBrush","value":"#FF0078D4"},{"value":"no key"}],
             "failed":[{"key":"CardBrush","value":"#FF000000","hresult":"0x800F0902"}]}
            """);

        Assert.IsNotNull(parsed);
        var (restored, failed) = parsed.Value;
        Assert.HasCount(1, restored);
        Assert.AreEqual("AccentBrush", restored[0].Key);
        Assert.HasCount(1, failed);
        Assert.AreEqual("0x800F0902", failed[0].HResult);

        var empty = DevToolsResourcesResetCommand.Parse("{}");
        Assert.IsNotNull(empty);
        Assert.IsEmpty(empty.Value.Restored);
        Assert.IsNull(DevToolsResourcesResetCommand.Parse("[]"));
    }
}

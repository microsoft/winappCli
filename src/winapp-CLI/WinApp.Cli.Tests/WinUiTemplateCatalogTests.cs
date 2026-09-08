// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// Unit tests for <see cref="WinUiTemplateCatalog"/>, which scrapes the fixed-width tables emitted by
/// <c>dotnet new list</c> and <c>dotnet new update --check-only</c> (there is no machine-readable
/// output). These pin the brittle table parsing independently of the command orchestration.
/// </summary>
[TestClass]
public class WinUiTemplateCatalogTests
{
    private const string ListOutput =
        "These templates matched your input: 'winui'.\n" +
        "\n" +
        "Template Name             Short Name                                     Language  Type     Author     Tags                      \n" +
        "------------------------  ---------------------------------------------  --------  -------  ---------  --------------------------\n" +
        "WinUI Blank App           winui,winui3,wasdk-single                      [C#]      project  Microsoft  Windows/WinUI/Desktop/XAML\n" +
        "WinUI Class Library       winui-lib,winui3-lib,wasdk-classlib            [C#]      project  Microsoft  Windows/WinUI/Library     \n" +
        "WinUI Blank Page          winui-page                                     [C#]      item     Microsoft  Windows/WinUI/Item/Page   \n";

    [TestMethod]
    public void ParseList_ReturnsEveryTemplateRow()
    {
        var entries = WinUiTemplateCatalog.ParseList(ListOutput);

        Assert.AreEqual(3, entries.Count, "Every data row under the header must be parsed.");
        Assert.AreEqual("winui", entries[0].ShortName);
        Assert.AreEqual("WinUI Blank App", entries[0].DisplayName);
        Assert.AreEqual("project", entries[0].Type);
        Assert.AreEqual("Windows/WinUI/Desktop/XAML", entries[0].Tags);
    }

    [TestMethod]
    public void ParseList_CanonicalShortNameIsFirstAlias_AndAllAliasesMatch()
    {
        var lib = WinUiTemplateCatalog.ParseList(ListOutput)[1];

        Assert.AreEqual("winui-lib", lib.ShortName, "The canonical short name is the first listed alias.");
        Assert.IsTrue(lib.MatchesShortName("wasdk-classlib"), "Any listed alias must match.");
        Assert.IsTrue(lib.MatchesShortName("WINUI-LIB"), "Matching is case-insensitive.");
        Assert.IsFalse(lib.MatchesShortName("winui"), "A different template's short name must not match.");
    }

    [TestMethod]
    public void ParseList_ClassifiesProjectAndItemTemplates()
    {
        var entries = WinUiTemplateCatalog.ParseList(ListOutput);

        Assert.IsTrue(entries[0].IsProject);
        Assert.IsFalse(entries[0].IsItem);
        Assert.IsTrue(entries[2].IsItem, "An item-typed row must be classified as an item template.");
        Assert.IsFalse(entries[2].IsProject);
    }

    [TestMethod]
    public void ParseList_NoTable_ReturnsEmpty()
    {
        Assert.AreEqual(0, WinUiTemplateCatalog.ParseList("No templates found.").Count);
        Assert.AreEqual(0, WinUiTemplateCatalog.ParseList(string.Empty).Count);
    }

    [TestMethod]
    public void ParseUpdateCheck_ReturnsCurrentAndLatestForPackage()
    {
        const string output =
            "Package                                          Current      Latest\n" +
            "-----------------------------------------------  -----------  -----------\n" +
            "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates   0.0.5-alpha  0.0.6-alpha\n";

        var (outcome, current, latest) = WinUiTemplateCatalog.ParseUpdateCheck(
            output, "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates");

        Assert.AreEqual(UpdateCheckOutcome.UpdateAvailable, outcome);
        Assert.AreEqual("0.0.5-alpha", current);
        Assert.AreEqual("0.0.6-alpha", latest);
    }

    [TestMethod]
    public void ParseUpdateCheck_UpToDate_ReturnsNulls()
    {
        var (outcome, current, latest) = WinUiTemplateCatalog.ParseUpdateCheck(
            "All template packages are up-to-date.", "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates");

        Assert.AreEqual(UpdateCheckOutcome.UpToDate, outcome);
        Assert.IsNull(current);
        Assert.IsNull(latest);
    }

    [TestMethod]
    public void ParseUpdateCheck_DifferentPackage_IsIgnored()
    {
        const string output =
            "Package        Current  Latest\n" +
            "-------------  -------  ------\n" +
            "Some.Other.Id  1.0.0    2.0.0\n";

        var (outcome, current, latest) = WinUiTemplateCatalog.ParseUpdateCheck(
            output, "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates");

        Assert.AreEqual(UpdateCheckOutcome.UpToDate, outcome,
            "A recognizable table listing only other packages means our pack is authoritatively up-to-date.");
        Assert.IsNull(current, "Only the requested package's row must be considered.");
        Assert.IsNull(latest);
    }

    [TestMethod]
    public void ParseUpdateCheck_UnrecognizedOutput_ReturnsUnrecognized()
    {
        // Exit 0 but output we can't interpret (an SDK format change or truncated stdout) must not be
        // mistaken for "up-to-date", otherwise the throttle would cache a non-result for a day.
        var (outcome, current, latest) = WinUiTemplateCatalog.ParseUpdateCheck(
            "Determining projects to restore...\nSome unexpected banner.", "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates");

        Assert.AreEqual(UpdateCheckOutcome.Unrecognized, outcome);
        Assert.IsNull(current);
        Assert.IsNull(latest);
    }

    private const string UninstallOutput =
        "Currently installed items:\n" +
        "   Microsoft.WindowsAppSDK.WinUI.CSharp.Templates\n" +
        "      Version: 0.0.6-alpha\n" +
        "      Details:\n" +
        "         Author: Microsoft\n" +
        "      Templates:\n" +
        "         WinUI Blank App (winui,winui3,wasdk-single) C#\n" +
        "         WinUI Blank Page (Item) (winui-page,winui3-page) C#\n" +
        "         Reactor NavigationView App (Experimental) (reactor-navview,winui-reactor-navview) C#\n" +
        "      Uninstall Command:\n" +
        "         dotnet new uninstall Microsoft.WindowsAppSDK.WinUI.CSharp.Templates\n" +
        "   Contoso.WinUI.Extras\n" +
        "      Version: 1.0.0\n" +
        "      Templates:\n" +
        "         Contoso WinUI Widget (winui-widget) C#\n" +
        "      Uninstall Command:\n" +
        "         dotnet new uninstall Contoso.WinUI.Extras\n";

    private static readonly string[] ExpectedMicrosoftAliases =
        ["winui", "winui3", "wasdk-single", "winui-page", "winui3-page", "reactor-navview", "winui-reactor-navview"];

    [TestMethod]
    public void ParsePackTemplates_ReturnsOnlyTheRequestedPackTemplates()
    {
        var rows = WinUiTemplateCatalog.ParsePackTemplates(
            UninstallOutput, "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates");

        // Every alias of the Microsoft pack's templates, including the "(Item)"/"(Experimental)" rows
        // whose display names themselves contain parentheses (only the last group is the alias list).
        CollectionAssert.AreEquivalent(ExpectedMicrosoftAliases, rows.SelectMany(r => r.Aliases).ToArray());
        Assert.IsFalse(rows.Any(r => r.Aliases.Contains("winui-widget")), "A different pack's template must not be included.");

        // The display name is everything before the alias group, so the parenthesised suffixes survive.
        CollectionAssert.AreEquivalent(
            ExpectedMicrosoftDisplayNames,
            rows.Select(r => r.DisplayName).ToArray());
    }

    private static readonly string[] ExpectedMicrosoftDisplayNames =
        ["WinUI Blank App", "WinUI Blank Page (Item)", "Reactor NavigationView App (Experimental)"];

    [TestMethod]
    public void ParsePackTemplates_MissingPackage_ReturnsEmpty()
    {
        Assert.AreEqual(0, WinUiTemplateCatalog.ParsePackTemplates(UninstallOutput, "Not.Installed").Count);
        Assert.AreEqual(0, WinUiTemplateCatalog.ParsePackTemplates(string.Empty, "Anything").Count);
    }

    private const string PackageId = "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates";

    /// <summary>
    /// A minimal templatecache.json with a WinUI project template (offering net8/9/10 via a
    /// <c>dotnetVersion</c> symbol mapped to the <c>dotnet-version</c> CLI option) and an item template
    /// with no framework choice, both mounted on the Microsoft pack's nupkg.
    /// </summary>
    private const string CacheJson =
        "{\"TemplateInfo\":[" +
        "{\"MountPointUri\":\"C:\\\\Users\\\\me\\\\.templateengine\\\\packages\\\\Microsoft.WindowsAppSDK.WinUI.CSharp.Templates.0.0.6-alpha.nupkg\"," +
        "\"ShortNameList\":[\"winui\",\"winui3\"]," +
        "\"Parameters\":[" +
        "{\"Name\":\"name\",\"DataType\":\"string\"}," +
        "{\"Name\":\"dotnetVersion\",\"DataType\":\"choice\",\"Choices\":{\"net8.0\":{},\"net9.0\":{},\"net10.0\":{}}}]," +
        "\"HostData\":\"{\\\"symbolInfo\\\":{\\\"dotnetVersion\\\":{\\\"longName\\\":\\\"dotnet-version\\\",\\\"shortName\\\":\\\"tfm\\\"}}}\"}," +
        "{\"MountPointUri\":\"C:\\\\Users\\\\me\\\\.templateengine\\\\packages\\\\Microsoft.WindowsAppSDK.WinUI.CSharp.Templates.0.0.6-alpha.nupkg\"," +
        "\"ShortNameList\":[\"winui-page\"]," +
        "\"Parameters\":[{\"Name\":\"name\",\"DataType\":\"string\"}]," +
        "\"HostData\":\"{\\\"symbolInfo\\\":{}}\"}]}";

    [TestMethod]
    public void DeriveTfmOption_ExactSdkTfmOffered_PinsThatFramework()
    {
        var (found, option, tfm, _) = WinUiTemplateCatalog.DeriveTfmOption(CacheJson, PackageId, "winui", 8);

        Assert.IsTrue(found, "The template belongs to the pack and is present in the cache.");
        Assert.AreEqual("dotnet-version", option, "The CLI option must come from the host longName, not a hard-coded name.");
        Assert.AreEqual("net8.0", tfm, "When the SDK's own TFM is offered, pin exactly that.");
    }

    [TestMethod]
    public void DeriveTfmOption_SdkNewerThanEveryChoice_PinsHighestOfferedFramework()
    {
        // SDK 11 with a pack that only offers up to net10.0: pin the highest supported TFM (net10.0)
        // instead of silently omitting the option and inheriting whatever the template defaults to.
        var (found, option, tfm, _) = WinUiTemplateCatalog.DeriveTfmOption(CacheJson, PackageId, "winui", 11);

        Assert.IsTrue(found);
        Assert.AreEqual("dotnet-version", option);
        Assert.AreEqual("net10.0", tfm);
    }

    [TestMethod]
    public void DeriveTfmOption_NoHostMapping_FallsBackToRawSymbolName()
    {
        // A pack that exposes the symbol with no host longName mapping: dotnet surfaces it under the raw
        // symbol name (--dotnetVersion), which is exactly the older-pack case a hard-coded option missed.
        const string noHostMapping =
            "{\"TemplateInfo\":[{" +
            "\"MountPointUri\":\"x\\\\Microsoft.WindowsAppSDK.WinUI.CSharp.Templates.0.0.5-alpha.nupkg\"," +
            "\"ShortNameList\":[\"winui-mvvm\"]," +
            "\"Parameters\":[{\"Name\":\"dotnetVersion\",\"DataType\":\"choice\",\"Choices\":{\"net8.0\":{},\"net9.0\":{},\"net10.0\":{}}}]," +
            "\"HostData\":\"{\\\"symbolInfo\\\":{}}\"}]}";

        var (found, option, tfm, _) = WinUiTemplateCatalog.DeriveTfmOption(noHostMapping, PackageId, "winui-mvvm", 9);

        Assert.IsTrue(found);
        Assert.AreEqual("dotnetVersion", option, "With no host mapping the raw symbol name is the option dotnet exposes.");
        Assert.AreEqual("net9.0", tfm);
    }

    [TestMethod]
    public void DeriveTfmOption_TemplateWithoutFrameworkChoice_FoundButNothingToPin()
    {
        var (found, option, tfm, _) = WinUiTemplateCatalog.DeriveTfmOption(CacheJson, PackageId, "winui-page", 8);

        Assert.IsTrue(found, "The item template is present in the cache.");
        Assert.IsNull(option, "An item template declares no framework, so there is no option to pass.");
        Assert.IsNull(tfm);
    }

    private static readonly string[] MinNineChoices = ["net9.0", "net10.0"];

    [TestMethod]
    public void DeriveTfmOption_SdkOlderThanEveryChoice_FoundButNoTfm()
    {
        // Pack offers only net9/net10 but SDK is 8: no offered framework is buildable by this SDK, so
        // there is nothing safe to pin. Choices come back so the caller can tell this apart from a
        // template that simply has no framework knob, and report the minimum SDK the template needs.
        const string minNine =
            "{\"TemplateInfo\":[{" +
            "\"MountPointUri\":\"x\\\\Microsoft.WindowsAppSDK.WinUI.CSharp.Templates.9.9.9.nupkg\"," +
            "\"ShortNameList\":[\"winui\"]," +
            "\"Parameters\":[{\"Name\":\"dotnetVersion\",\"DataType\":\"choice\",\"Choices\":{\"net9.0\":{},\"net10.0\":{}}}]," +
            "\"HostData\":\"{\\\"symbolInfo\\\":{\\\"dotnetVersion\\\":{\\\"longName\\\":\\\"dotnet-version\\\"}}}\"}]}";

        var (found, option, tfm, choices) = WinUiTemplateCatalog.DeriveTfmOption(minNine, PackageId, "winui", 8);

        Assert.IsTrue(found);
        Assert.AreEqual("dotnet-version", option);
        Assert.IsNull(tfm, "No offered framework is <= the SDK major, so nothing is pinned.");
        CollectionAssert.AreEquivalent(MinNineChoices, choices.ToArray());
        Assert.AreEqual(9, WinUiTemplateCatalog.MinimumSdkMajor(choices));
    }

    [TestMethod]
    public void DeriveTfmOption_TemplateWithoutFrameworkChoice_ReportsNoChoices()
    {
        // An item template has no framework knob at all. Distinguishing it from "the SDK is too old"
        // is what stops the command from claiming a newer SDK is required when none is.
        var (_, _, _, choices) = WinUiTemplateCatalog.DeriveTfmOption(CacheJson, PackageId, "winui-page", 8);

        Assert.AreEqual(0, choices.Count);
        Assert.IsNull(WinUiTemplateCatalog.MinimumSdkMajor(choices));
    }

    [TestMethod]
    public void MinimumSdkMajor_IgnoresValuesThatAreNotFrameworkMonikers()
    {
        Assert.AreEqual(10, WinUiTemplateCatalog.MinimumSdkMajor(["net10.0"]));
        Assert.AreEqual(8, WinUiTemplateCatalog.MinimumSdkMajor(["net10.0", "net8.0", "net9.0"]));
        Assert.IsNull(WinUiTemplateCatalog.MinimumSdkMajor(["latest", "netstandard2.0x"]));
        Assert.IsNull(WinUiTemplateCatalog.MinimumSdkMajor([]));
    }

    private static WinUiTemplateEntry Entry(string displayName, string shortNames, string tags = "Windows/WinUI/Desktop")
        => new(displayName, shortNames.Split(','), "C#", "project", tags);

    [TestMethod]
    public void IsExperimental_DetectedFromEitherTheTagPathOrTheDisplayName()
    {
        // `dotnet new list` truncates over-wide columns, so either signal can be the only one that
        // survives: the Reactor rows carry both an Experimental tag and an "(Experimental)" name.
        Assert.IsTrue(Entry("Reactor Blank App", "reactor", "Windows/WinUI/Desktop/Reactor/Experimental").IsExperimental);
        Assert.IsTrue(Entry("Reactor Blank App (Experimental)", "reactor", "Windows/WinUI/Desktop/Reactor").IsExperimental);
        Assert.IsFalse(Entry("WinUI Blank App", "winui", "Windows/WinUI/Desktop/XAML").IsExperimental);
    }

    private static readonly string[] ExpectedKeptDisplayNames =
        ["Reactor NavigationView App (Experimental)", "WinUI Blank App"];

    [TestMethod]
    public void RestrictToPack_RepairsTruncatedDisplayNamesAndDropsForeignTemplates()
    {
        // `dotnet new list` cut the Reactor name off mid-word to fit its column, and matched a
        // different pack's "WinUI 3 Desktop App" because that template reuses the `winui` short name.
        var listed = new List<WinUiTemplateEntry>
        {
            Entry("Reactor NavigationView App (Experim...", "reactor-navview,winui-reactor-navview"),
            Entry("WinUI 3 Desktop App", "winui"),
            Entry("WinUI Blank App", "winui,winui3,wasdk-single"),
        };
        var packRows = WinUiTemplateCatalog.ParsePackTemplates(UninstallOutput, PackageId);

        var kept = WinUiTemplateCatalog.RestrictToPack(listed, packRows);

        CollectionAssert.AreEqual(
            ExpectedKeptDisplayNames,
            kept.Select(t => t.DisplayName).ToArray());
    }

    private static readonly string[] OfficialBlankAppAliases = ["winui", "winui3", "wasdk-single"];

    [TestMethod]
    public void RestrictToPack_ForeignTemplateBorrowingAnOfficialAlias_IsDropped()
    {
        // A third-party pack can publish a template that copies the official name and appends an
        // official alias to its own. `dotnet new list` matches it (it starts with `winui`) and gives
        // no column saying which pack it came from, so ownership has to be decided on the canonical
        // short name — the one that would be passed to `dotnet new` — not on any shared alias.
        var listed = new List<WinUiTemplateEntry>
        {
            Entry("WinUI Blank App", "evil,winui"),
            Entry("WinUI Blank App", "winui,winui3,wasdk-single"),
        };

        var kept = WinUiTemplateCatalog.RestrictToPack(listed, WinUiTemplateCatalog.ParsePackTemplates(UninstallOutput, PackageId));

        Assert.AreEqual(1, kept.Count, "Only the pack's own template may be offered.");
        Assert.AreEqual("winui", kept[0].ShortName, "`dotnet new` must never be invoked with a foreign short name.");
        Assert.IsFalse(kept[0].MatchesShortName("evil"), "A foreign alias must not resolve via --template.");
    }

    [TestMethod]
    public void RestrictToPack_TakesAliasesFromThePackNotTheTruncatedListing()
    {
        // The Short Name column is fixed-width too, so a long alias list can be clipped. The pack's
        // own listing is authoritative, which keeps `--template wasdk-single` working.
        var listed = new List<WinUiTemplateEntry> { Entry("WinUI Blank App", "winui,winui3") };

        var kept = WinUiTemplateCatalog.RestrictToPack(listed, WinUiTemplateCatalog.ParsePackTemplates(UninstallOutput, PackageId));

        CollectionAssert.AreEqual(OfficialBlankAppAliases, kept[0].ShortNames.ToArray());
    }

    [TestMethod]
    public void RestrictToPack_NoPackRows_KeepsNothing()
    {
        // Ownership could not be established, so no template may be presented as official. The caller
        // turns the empty result into an enumeration failure rather than offering unverified rows.
        var listed = new List<WinUiTemplateEntry> { Entry("WinUI Blank App", "winui") };

        Assert.AreEqual(0, WinUiTemplateCatalog.RestrictToPack(listed, []).Count);
    }

    [TestMethod]
    public void DeriveTfmOption_TemplateNotInThisCache_ReturnsNotFound()
    {
        // Wrong package (mount point isn't the Microsoft pack) and unknown short name both mean "keep
        // looking" — the caller then tries the next cache file or the heuristic.
        var (foundWrongPackage, _, _, _) = WinUiTemplateCatalog.DeriveTfmOption(CacheJson, "Some.Other.Pack", "winui", 9);
        Assert.IsFalse(foundWrongPackage);

        var (foundWrongShort, _, _, _) = WinUiTemplateCatalog.DeriveTfmOption(CacheJson, PackageId, "winui-nope", 9);
        Assert.IsFalse(foundWrongShort);

        var (foundEmpty, _, _, _) = WinUiTemplateCatalog.DeriveTfmOption(string.Empty, PackageId, "winui", 9);
        Assert.IsFalse(foundEmpty);

        var (foundMalformed, _, _, _) = WinUiTemplateCatalog.DeriveTfmOption("{ not json", PackageId, "winui", 9);
        Assert.IsFalse(foundMalformed);
    }
}

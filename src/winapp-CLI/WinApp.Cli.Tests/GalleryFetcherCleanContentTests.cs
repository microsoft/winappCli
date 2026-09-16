// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Tests;

/// <summary>
/// Hermetic tests for <see cref="GalleryFetcher.CleanGalleryContent"/> — the demo-cleanup
/// pass. The invariant that matters to a user: the emitted XAML must not reference an
/// event handler the accompanying C# never defines, because the Gallery's code-behind
/// extractor does not surface <c>*_Loaded</c> methods and an agent pastes the snippet
/// verbatim. A dangling <c>Loaded="X_Loaded"</c> is a compile break in the user's project.
/// </summary>
[TestClass]
public class GalleryFetcherCleanContentTests
{
    [TestMethod]
    public void CleanGalleryContent_InlineLoadedHandler_IsRemoved()
    {
        // The real gallery-tabview-1 shape: the demo handler sits INLINE, on a line that
        // also carries '<' and '>'. The line-level filter deliberately keeps such lines,
        // so the attribute has to be stripped before it runs.
        var xaml = "<TabView AddTabButtonClick=\"TabView_AddButtonClick\" "
                 + "TabCloseRequested=\"TabView_TabCloseRequested\" Loaded=\"TabView_Loaded\" />";

        var cleaned = GalleryFetcher.CleanGalleryContent(xaml);

        Assert.IsFalse(cleaned.Contains("TabView_Loaded"), "dangling Loaded handler must be stripped");
        StringAssert.Contains(cleaned, "TabView_AddButtonClick", "real handlers must survive");
        StringAssert.Contains(cleaned, "TabView_TabCloseRequested", "real handlers must survive");
        StringAssert.Contains(cleaned, "/>", "the tag must stay well-formed");
    }

    [TestMethod]
    public void CleanGalleryContent_OwnLineLoadedHandler_IsRemovedWithoutBreakingTheTag()
    {
        var xaml = "<TabView x:Name=\"Tabs\"\n"
                 + "         Loaded=\"TabView_Loaded\"\n"
                 + "         TabWidthMode=\"Equal\">\n"
                 + "</TabView>";

        var cleaned = GalleryFetcher.CleanGalleryContent(xaml);

        Assert.IsFalse(cleaned.Contains("TabView_Loaded"), "dangling Loaded handler must be stripped");
        StringAssert.Contains(cleaned, "TabWidthMode=\"Equal\"", "sibling attributes must survive");
        Assert.IsTrue(
            ScenarioSanitizer.XamlIsWellFormed(cleaned),
            $"cleanup must leave well-formed XAML. Got:\n{cleaned}");
    }

    [TestMethod]
    public void CleanGalleryContent_NonDemoLoadedHandler_IsKept()
    {
        // Only the "*_Loaded" demo convention is stripped. A handler named anything else
        // is the sample's own logic and the code-behind extractor does surface it.
        var xaml = "<Grid Loaded=\"OnGridReady\" />";

        var cleaned = GalleryFetcher.CleanGalleryContent(xaml);

        StringAssert.Contains(cleaned, "OnGridReady", "non-demo handlers must not be stripped");
    }

    [TestMethod]
    public void CleanGalleryContent_DemoLayoutAttributesOnOwnLine_StillRemoved()
    {
        // Guards the pre-existing line-level filter against regression from the new
        // attribute-level pass running ahead of it.
        var xaml = "<Button\n"
                 + "    Width=\"300\"\n"
                 + "    Margin=\"-8\"\n"
                 + "    Content=\"Go\">\n"
                 + "</Button>";

        var cleaned = GalleryFetcher.CleanGalleryContent(xaml);

        Assert.IsFalse(cleaned.Contains("Width=\"300\""), "own-line demo width must still be dropped");
        Assert.IsFalse(cleaned.Contains("Margin=\"-8\""), "own-line negative margin must still be dropped");
        StringAssert.Contains(cleaned, "Content=\"Go\"");
    }

    [TestMethod]
    public void CleanGalleryContent_ValuePositionToken_DropsTheAttributeRatherThanFlatteningIt()
    {
        // "..." is not a double, so StrokeThickness="..." is a compile error on paste. Dropping
        // the attribute leaves the property at its own default, which always compiles.
        var cleaned = GalleryFetcher.CleanGalleryContent(
            "<Line X1=\"0\" StrokeThickness=\"$(Slider1)\" Stroke=\"Red\" />");

        Assert.IsFalse(cleaned.Contains("..."), $"token must not be flattened: {cleaned}");
        Assert.IsFalse(cleaned.Contains("StrokeThickness"), $"attribute must be dropped: {cleaned}");
        StringAssert.Contains(cleaned, "X1=\"0\"");
        StringAssert.Contains(cleaned, "Stroke=\"Red\"");
    }

    [TestMethod]
    public void CleanGalleryContent_ValuePositionToken_LeavesAWellFormedTag()
    {
        string[] fragments =
        [
            "<Line StrokeThickness=\"$(Slider1)\" />",
            "<StackPanel Orientation=\"$(Orientation)\"><TextBlock Text=\"Hi\" /></StackPanel>",
            "<Rectangle\n    RadiusX=\"$(RadiusX)\"\n    RadiusY=\"$(RadiusY)\"\n    Fill=\"Blue\" />",
            "<CheckBox IsChecked=\"$(IsChecked)\" Content=\"Go\" />",
        ];

        foreach (var fragment in fragments)
        {
            var cleaned = GalleryFetcher.CleanGalleryContent(fragment);

            Assert.IsFalse(cleaned.Contains("$("), $"token survived: {cleaned}");
            Assert.IsFalse(cleaned.Contains("..."), $"token was flattened: {cleaned}");
            Assert.IsTrue(ScenarioSanitizer.XamlIsWellFormed(cleaned), $"tag was corrupted: {cleaned}");
        }
    }

    [TestMethod]
    public void CleanGalleryContent_KnownValueRewrites_StillWinOverAttributeDropping()
    {
        // These must be applied before the position-aware pass: an InfoBar that loses IsOpen
        // defaults to collapsed, so the sample would render as nothing.
        var cleaned = GalleryFetcher.CleanGalleryContent(
            "<InfoBar IsOpen=\"$(IsOpen)\" Severity=\"$(Severity)\" Title=\"Hi\" />");

        StringAssert.Contains(cleaned, "IsOpen=\"True\"");
        StringAssert.Contains(cleaned, "Severity=\"Informational\"");
    }
}

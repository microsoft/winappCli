// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Tests;

/// <summary>
/// Hermetic tests for <see cref="GalleryProvider.NormalizeForPaste"/> — the pass that makes an
/// index sample pasteable by rewriting the things that resolve only inside the Gallery app.
///
/// These exist alongside the corpus guards in <c>EmbeddedSnapshotTests</c> rather than instead
/// of them. A corpus guard proves the committed data is clean, but it reads whatever upstream
/// currently publishes: the day Gallery stops shipping a <c>SampleMedia</c> path, the guard
/// passes whether or not the rewrite still works. These pin the rule itself.
/// </summary>
[TestClass]
public class GalleryProviderNormalizeTests
{
    private static Scenario NewScenario(string? xaml = null, string? csharp = null, string[]? xmlns = null) => new()
    {
        Id = "gallery-test-1",
        ControlId = "test",
        ControlName = "Test",
        HeaderText = "Test",
        Source = "gallery",
        Xaml = xaml,
        CSharp = csharp,
        XmlnsImports = xmlns ?? [],
    };

    // --- Gallery CLR namespaces ---------------------------------------------------

    [TestMethod]
    public void XmlnsImport_PointingAtGalleryNamespace_BecomesYourApp()
    {
        // SearchEngine renders XmlnsImports as the "Setup:" line, so this is printed to the
        // user as an instruction. "using:WinUIGallery.ControlPages" instructs them to declare
        // a prefix for a namespace that does not exist in their app.
        var s = NewScenario(xmlns: ["xmlns:local=\"using:WinUIGallery.ControlPages\""]);

        GalleryProvider.NormalizeForPaste(s);

        Assert.AreEqual("xmlns:local=\"using:YourApp\"", s.XmlnsImports[0]);
    }

    [TestMethod]
    public void XmlnsImport_KeepsPrefixName()
    {
        // Only the mapped namespace is a Gallery detail. The prefix is referenced by the
        // markup we serve, so renaming it would break the snippet it belongs to.
        var s = NewScenario(
            xaml: "<samplepages:YourPage />",
            xmlns: ["xmlns:samplepages=\"using:WinUIGallery.SamplePages\""]);

        GalleryProvider.NormalizeForPaste(s);

        Assert.AreEqual("xmlns:samplepages=\"using:YourApp\"", s.XmlnsImports[0]);
        StringAssert.Contains(s.Xaml!, "<samplepages:YourPage />");
    }

    [TestMethod]
    public void InlineXmlnsInXaml_PointingAtGalleryNamespace_BecomesYourApp()
    {
        var s = NewScenario(xaml: "<Grid xmlns:cond=\"using:WinUIGallery.ControlPages\" />");

        GalleryProvider.NormalizeForPaste(s);

        StringAssert.Contains(s.Xaml!, "xmlns:cond=\"using:YourApp\"");
    }

    [TestMethod]
    public void QualifiedGalleryType_InCSharp_LosesTheGalleryRoot()
    {
        var s = NewScenario(csharp: "var x = WinUIGallery.ControlPages.CustomDataObject.GetDataObjects();");

        GalleryProvider.NormalizeForPaste(s);

        StringAssert.Contains(s.CSharp!, "YourApp.ControlPages.CustomDataObject.GetDataObjects()");
    }

    [TestMethod]
    public void SourceLinkNamingGalleryFolder_IsLeftAlone()
    {
        // The rewrite is anchored on the dot that starts a CLR qualifier, so a comment
        // pointing at the sample's real source keeps working as a link.
        const string link = "// https://github.com/microsoft/WinUI-Gallery/blob/main/WinUIGallery/ButtonPage.xaml.cs";
        var s = NewScenario(csharp: link);

        GalleryProvider.NormalizeForPaste(s);

        Assert.AreEqual(link, s.CSharp);
    }

    // --- Gallery package assets ---------------------------------------------------

    [TestMethod]
    [DataRow("<Image Source=\"ms-appx:///Assets/SampleMedia/cliff.jpg\" />", "ms-appx:///Assets/YourImage.jpg")]
    [DataRow("<Image Source=\"/Assets/SampleMedia/rainier.jpg\" />", "/Assets/YourImage.jpg")]
    [DataRow("<IconSource ImageSource=\"/Assets/Tiles/GalleryIcon.ico\" />", "/Assets/YourImage.ico")]
    public void GalleryAssetPath_InXaml_BecomesPlaceholder_PreservingUriForm(string xaml, string expected)
    {
        // The three forms are not interchangeable, so the prefix is preserved rather than
        // normalized to one of them.
        var s = NewScenario(xaml: xaml);

        GalleryProvider.NormalizeForPaste(s);

        StringAssert.Contains(s.Xaml!, expected);
    }

    [TestMethod]
    public void PackageRelativeAssetPath_InCSharp_StaysPackageRelative()
    {
        // AppWindow.SetIcon takes a package-relative path, not a ms-appx URI: rewriting the
        // form rather than just the file would turn a missing icon into a wrong API call.
        var s = NewScenario(csharp: "AppWindow.SetIcon(\"Assets/Tiles/GalleryIcon.ico\");");

        GalleryProvider.NormalizeForPaste(s);

        Assert.AreEqual("AppWindow.SetIcon(\"Assets/YourImage.ico\");", s.CSharp);
    }

    [TestMethod]
    public void NonImageAsset_KeepsItsExtension_AndReadsAsAnAsset()
    {
        // A MediaPlayerElement pointed at a .png is a different kind of broken than one
        // pointed at a video the user has yet to add.
        var s = NewScenario(xaml: "<MediaPlayerElement Source=\"/Assets/SampleMedia/fishes.wmv\" />");

        GalleryProvider.NormalizeForPaste(s);

        StringAssert.Contains(s.Xaml!, "/Assets/YourAsset.wmv");
    }

    [TestMethod]
    public void UnrelatedAssetPath_IsLeftAlone()
    {
        // Only Gallery's own two package folders are rewritten. A sample naming an asset the
        // user is expected to supply is describing their app, not Gallery's.
        const string xaml = "<Image Source=\"ms-appx:///Assets/StoreLogo.png\" />";
        var s = NewScenario(xaml: xaml);

        GalleryProvider.NormalizeForPaste(s);

        Assert.AreEqual(xaml, s.Xaml);
    }
}

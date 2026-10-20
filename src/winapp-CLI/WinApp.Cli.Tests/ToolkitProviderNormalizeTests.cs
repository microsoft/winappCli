// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Tests;

/// <summary>
/// Hermetic tests for <see cref="ToolkitProvider.NormalizeForPaste"/>, the last pass over a
/// Toolkit sample before a user copies it.
///
/// <para>The sample index upstream publishes already removes the sample app's scaffolding,
/// so what is tested here is the part only a consumer can do: give the sample the name the
/// user's own page will have, without touching the helper types the sample legitimately
/// declares beside itself.</para>
/// </summary>
[TestClass]
public class ToolkitProviderNormalizeTests
{
    private static Scenario Sample(string csharp, string xaml = "<Grid />") =>
        new() { Id = "x-1", ControlId = "x", ControlName = "X", CSharp = csharp, Xaml = xaml };

    [TestMethod]
    public void NormalizeForPaste_RenamesTheConstructorLeftBehindByTheIndex()
    {
        // The index publishes a sample's members without the class that held them, so the
        // constructor is the only place the old type name survives -- and the only member
        // that would not compile once pasted into a page with a different name.
        var scenario = Sample("""
            public AdvancedCollectionViewSample()
            {
                Load();
            }
            """);

        ToolkitProvider.NormalizeForPaste(scenario);

        StringAssert.Contains(scenario.CSharp, "public YourPage()",
            "a constructor still named after the sample class does not compile in the user's page");
        Assert.IsFalse(scenario.CSharp!.Contains("AdvancedCollectionViewSample"),
            "the sample class name should not survive anywhere in the pasted code");
    }

    [TestMethod]
    public void NormalizeForPaste_LeavesHelperTypesDeclaredBesideTheSampleAlone()
    {
        // AdvancedCollectionViewSample really does ship an Employee class that its binding
        // needs. Renaming the first class in the file renamed *that* and left the sample
        // class untouched, producing code that both fails to compile and silently renames
        // a type the user is meant to keep.
        var scenario = Sample("""
            public class Employee
            {
                public string Name { get; set; }
            }

            public AdvancedCollectionViewSample()
            {
                Load();
            }
            """);

        ToolkitProvider.NormalizeForPaste(scenario);

        StringAssert.Contains(scenario.CSharp, "public class Employee",
            "a helper type the sample declares is part of the sample, not scaffolding to rename");
        StringAssert.Contains(scenario.CSharp, "public YourPage()",
            "the sample's own constructor is still the thing that needs renaming");
    }

    [TestMethod]
    public void NormalizeForPaste_RenamesThroughTheClassDeclarationWhenOneIsPresent()
    {
        // Nothing in the contract promises the index will always drop the declaration, and
        // a sample class that is present should still be renamed -- but only it.
        var scenario = Sample("""
            public sealed partial class ButtonsSample : Page
            {
                public ButtonsSample() => Setup();
            }
            """);

        ToolkitProvider.NormalizeForPaste(scenario);

        StringAssert.Contains(scenario.CSharp, "class YourPage");
        StringAssert.Contains(scenario.CSharp, "public YourPage()");
    }

    [TestMethod]
    public void NormalizeForPaste_StripsScaffoldingIfTheIndexEverStopsDoingIt()
    {
        // These removals are expected to find nothing now. They stay because a change on
        // the other side of the index would otherwise reach a user's clipboard first.
        var scenario = Sample("""
            // Licensed to the .NET Foundation under one or more agreements.
            // The .NET Foundation licenses this file to you under the MIT license.

            namespace CommunityToolkit.WinUI.Controls.ButtonsExperiment.Samples;

            [ToolkitSample(id: nameof(ButtonsSample), "Buttons", description: "A button.")]
            public sealed partial class ButtonsSample : Page
            {
            }
            """);

        ToolkitProvider.NormalizeForPaste(scenario);

        Assert.IsFalse(scenario.CSharp!.Contains("ToolkitSample"), "build-time sample metadata must not ship");
        Assert.IsFalse(scenario.CSharp.Contains(".NET Foundation"), "upstream's license header is not part of the snippet");
        StringAssert.Contains(scenario.CSharp, "namespace YourApp;",
            "the sample must land in the user's app, not upstream's namespace");
    }

    [TestMethod]
    public void NormalizeForPaste_DropsEventHandlersTheServedCodeDoesNotDeclare()
    {
        // A scenario split out of a multi-instance sample carries no code at all, so every
        // handler attribute in its markup is dangling.
        var scenario = Sample(csharp: "", xaml: """<Button Click="Move_Click" Content="Move" />""");

        ToolkitProvider.NormalizeForPaste(scenario);

        Assert.IsFalse(scenario.Xaml!.Contains("Move_Click"),
            "markup wired to a handler we do not serve fails at load, not at paste");
        StringAssert.Contains(scenario.Xaml, "Content=\"Move\"", "only the handler should be dropped");
    }

    [TestMethod]
    public void NormalizeForPaste_KeepsEventHandlersTheServedCodeDeclares()
    {
        var scenario = Sample(
            csharp: "private void Move_Click(object sender, RoutedEventArgs e) { }",
            xaml: """<Button Click="Move_Click" Content="Move" />""");

        ToolkitProvider.NormalizeForPaste(scenario);

        StringAssert.Contains(scenario.Xaml, "Click=\"Move_Click\"",
            "a handler the snippet declares is exactly what the sample is teaching");
    }

    [TestMethod]
    public void NormalizeForPaste_RewritesNamespacesThatOnlyExistInTheToolkitSampleApp()
    {
        // Rendered as the "Setup:" line, so this is printed to the user as an instruction.
        // Every Toolkit component's sample project is named <Component>Experiment, and those
        // namespaces ship in no NuGet package -- telling the user to map one is telling them
        // to add a mapping that cannot resolve.
        var scenario = new Scenario
        {
            Id = "richsuggestbox-1",
            ControlId = "richsuggestbox",
            ControlName = "RichSuggestBox",
            Xaml = """<Grid xmlns:local="using:RichSuggestBoxExperiment.Samples"><local:SuggestionTemplateSelector /></Grid>""",
            XmlnsImports =
            [
                "xmlns:controls=\"using:CommunityToolkit.WinUI.Controls\"",
                "xmlns:local=\"using:RichSuggestBoxExperiment.Samples\"",
            ],
        };

        ToolkitProvider.NormalizeForPaste(scenario);

        Assert.AreEqual("xmlns:controls=\"using:CommunityToolkit.WinUI.Controls\"", scenario.XmlnsImports[0],
            "the shipping namespace of the package the user is told to install must survive untouched");
        Assert.AreEqual("xmlns:local=\"using:YourApp\"", scenario.XmlnsImports[1],
            "the emitted C# declares namespace YourApp, so the prefix should name that");
        Assert.IsFalse(scenario.Xaml!.Contains("RichSuggestBoxExperiment"),
            "the sample app's namespace should not survive in the markup either");
    }

    [TestMethod]
    public void NormalizeForPaste_RewritesASampleAppNamespaceWithTrailingSegments()
    {
        // PrimitivesExperiment.Samples.SwitchPresenter -- the match has to consume the whole
        // namespace, not stop at "Samples" and leave ".SwitchPresenter" dangling.
        var scenario = new Scenario
        {
            Id = "switchpresenter-1",
            ControlId = "switchpresenter",
            ControlName = "SwitchPresenter",
            Xaml = "<Grid />",
            XmlnsImports = ["xmlns:local=\"using:PrimitivesExperiment.Samples.SwitchPresenter\""],
        };

        ToolkitProvider.NormalizeForPaste(scenario);

        Assert.AreEqual("xmlns:local=\"using:YourApp\"", scenario.XmlnsImports[0]);
    }

    [TestMethod]
    public void NormalizeForPaste_ReplacesASampleAppImageWithAPlaceholder()
    {
        // ImageCropper's published C# really does load "ms-appx:///Assets/Owl.jpg", a file that
        // exists only inside the Toolkit's own package. Pasted as-is it compiles and then shows
        // an empty cropper, with nothing on screen to say why.
        var scenario = Sample("""
            var file = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///Assets/Owl.jpg"));
            """);

        ToolkitProvider.NormalizeForPaste(scenario);

        StringAssert.Contains(scenario.CSharp, "ms-appx:///Assets/YourImage.jpg",
            "the placeholder should keep the URI form and the extension so the failure is visible");
        Assert.IsFalse(scenario.CSharp!.Contains("Owl.jpg"),
            "a path into the Toolkit's own package should not survive into the user's app");
    }

    [TestMethod]
    public void NormalizeForPaste_ReplacesAnAssetInsideAMarkupExtension()
    {
        // SettingsCard writes "{ui:BitmapIcon Source=ms-appx:///Assets/AppTitleBar.scale-200.png}".
        // A rewrite that swallowed the closing brace would produce "YourImage.png}" and leave
        // markup that no longer parses -- worse than the path it replaced.
        var scenario = Sample(
            "",
            """<ui:SettingsCard HeaderIcon="{ui:BitmapIcon Source=ms-appx:///Assets/AppTitleBar.scale-200.png}" />""");

        ToolkitProvider.NormalizeForPaste(scenario);

        StringAssert.Contains(scenario.Xaml, "{ui:BitmapIcon Source=ms-appx:///Assets/YourImage.png}",
            "the markup extension must still be terminated after the path is replaced");
    }

    [TestMethod]
    public void NormalizeForPaste_NamesANonImageAssetGenerically()
    {
        // MediaPlayerElement points at Assets/Llama.mp3. Calling that "YourImage" would be a
        // different kind of wrong, so the placeholder is named after what it actually is.
        var scenario = Sample("", """<MediaPlayerElement Source="Assets/Llama.mp3" />""");

        ToolkitProvider.NormalizeForPaste(scenario);

        StringAssert.Contains(scenario.Xaml, "Assets/YourAsset.mp3");
    }

    [TestMethod]
    public void NormalizeForPaste_FlattensAnAssetSubfolderAndKeepsEachPrefixForm()
    {
        // Toolkit assets sit directly under Assets/ except for Assets/BrushAssets/, which has
        // to collapse to the same placeholder -- a subfolder the user has no reason to create
        // is as broken as the file itself. The three prefix forms are not interchangeable
        // (a package-relative path is not a URI), so each is preserved as written.
        var scenario = Sample("", """
            <Image Source="ms-appx:///Assets/BrushAssets/Trex.png" />
            <Image Source="/Assets/Converters.png" />
            """);

        ToolkitProvider.NormalizeForPaste(scenario);

        StringAssert.Contains(scenario.Xaml, "ms-appx:///Assets/YourImage.png");
        StringAssert.Contains(scenario.Xaml, "\"/Assets/YourImage.png\"");
        Assert.IsFalse(scenario.Xaml!.Contains("BrushAssets"),
            "the placeholder should not sit in a subfolder the user is not told to create");
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Tests;

/// <summary>
/// Hermetic tests for <see cref="ToolkitFetcher.CleanXaml"/> and
/// <see cref="ToolkitFetcher.DetectXmlnsImports"/> — the demo-cleanup + namespace
/// detection that must never emit a snippet that fails XAML parsing (missing xmlns,
/// a dropped-but-referenced resource, or a dropped-but-bound element).
/// </summary>
[TestClass]
public class ToolkitFetcherCleanXamlTests
{
    // --- Finding 1: muxc namespace must be advertised when the snippet uses it -----

    [TestMethod]
    public void DetectXmlnsImports_EmitsMuxc_WhenSnippetUsesMuxcPrefix()
    {
        var xaml = "<controls:WrapLayout>\n  <muxc:ItemsRepeater />\n</controls:WrapLayout>";
        var imports = ToolkitFetcher.DetectXmlnsImports(xaml);
        Assert.IsTrue(imports.Any(i => i.Contains("xmlns:muxc=\"using:Microsoft.UI.Xaml.Controls\"")),
            "a snippet using muxc: must advertise the muxc namespace or it won't parse");
    }

    [TestMethod]
    public void DetectXmlnsImports_OmitsMuxc_WhenUnused()
    {
        var xaml = "<controls:DockPanel>\n  <TextBox />\n</controls:DockPanel>";
        var imports = ToolkitFetcher.DetectXmlnsImports(xaml);
        Assert.IsFalse(imports.Any(i => i.Contains("xmlns:muxc")),
            "don't advertise muxc when the snippet doesn't use it");
        Assert.IsTrue(imports.Any(i => i.Contains("xmlns:controls")), "controls is always required");
    }

    // --- Finding 2: a referenced resource definition must survive cleanup ----------

    [TestMethod]
    public void CleanXaml_KeepsResourceBlock_WhenKeyStillReferenced()
    {
        var xaml =
            "<controls:WrapLayout>\n" +
            "  <controls:WrapLayout.Resources>\n" +
            "    <DataTemplate x:Key=\"WrapTemplate\"><TextBlock Text=\"{x:Bind}\" /></DataTemplate>\n" +
            "  </controls:WrapLayout.Resources>\n" +
            "  <muxc:ItemsRepeater ItemTemplate=\"{StaticResource WrapTemplate}\" />\n" +
            "</controls:WrapLayout>";

        var cleaned = ToolkitFetcher.CleanXaml(xaml);

        StringAssert.Contains(cleaned, "x:Key=\"WrapTemplate\"",
            "a DataTemplate referenced by {StaticResource} must not be dropped");
        StringAssert.Contains(cleaned, "{StaticResource WrapTemplate}", "the reference itself survives");
    }

    [TestMethod]
    public void CleanXaml_DropsResourceBlock_WhenNothingReferencesIt()
    {
        var xaml =
            "<controls:DockPanel>\n" +
            "  <controls:DockPanel.Resources>\n" +
            "    <DataTemplate x:Key=\"UnusedTemplate\"><TextBlock /></DataTemplate>\n" +
            "  </controls:DockPanel.Resources>\n" +
            "  <TextBox />\n" +
            "</controls:DockPanel>";

        var cleaned = ToolkitFetcher.CleanXaml(xaml);

        Assert.IsFalse(cleaned.Contains("UnusedTemplate", StringComparison.Ordinal),
            "an unreferenced demo resource block is still cleaned away");
    }

    [TestMethod]
    public void CleanXaml_DropsResourceBlock_WithNoKeyedResources()
    {
        // Implicit-style-only Resources (no x:Key) never dangle a reference → safe to drop.
        var xaml =
            "<controls:DockPanel>\n" +
            "  <controls:DockPanel.Resources>\n" +
            "    <Style TargetType=\"TextBlock\"><Setter Property=\"FontSize\" Value=\"14\" /></Style>\n" +
            "  </controls:DockPanel.Resources>\n" +
            "  <TextBox />\n" +
            "</controls:DockPanel>";

        var cleaned = ToolkitFetcher.CleanXaml(xaml);

        Assert.IsFalse(cleaned.Contains("<controls:DockPanel.Resources>", StringComparison.Ordinal),
            "an implicit-style-only Resources block is safe to drop");
    }

    // --- Finding 3: a Border bound to by name must survive cleanup ------------------

    [TestMethod]
    public void CleanXaml_KeepsNamedBorder_WhenReferencedByBinding()
    {
        var xaml =
            "<controls:ContentSizer TargetControl=\"{x:Bind SomeContent}\">\n" +
            "  <Border x:Name=\"SomeContent\" Background=\"Gray\" />\n" +
            "</controls:ContentSizer>";

        var cleaned = ToolkitFetcher.CleanXaml(xaml);

        StringAssert.Contains(cleaned, "x:Name=\"SomeContent\"",
            "a Border referenced by {x:Bind} must keep its name");
        StringAssert.Contains(cleaned, "<Border", "the referenced Border element must not be removed");
        StringAssert.Contains(cleaned, "{x:Bind SomeContent}", "the binding survives and now resolves");
    }

    [TestMethod]
    public void CleanXaml_DropsUnnamedEmptyBorderPlaceholder()
    {
        var xaml =
            "<controls:DockPanel>\n" +
            "  <Border Background=\"Gray\" />\n" +
            "  <TextBox />\n" +
            "</controls:DockPanel>";

        var cleaned = ToolkitFetcher.CleanXaml(xaml);

        Assert.IsFalse(cleaned.Contains("<Border", StringComparison.Ordinal),
            "an unnamed empty Border placeholder is still cleaned away");
    }

    // --- Split/no-code-behind scenarios must not reference missing handlers --------

    [TestMethod]
    public void CleanXaml_StripsUnreferencedNames_ButKeepsReferencedOnes()
    {
        var xaml =
            "<StackPanel>\n" +
            "  <TextBox x:Name=\"NoiseName\" />\n" +
            "  <controls:ContentSizer TargetControl=\"{x:Bind Keep}\" />\n" +
            "  <Border x:Name=\"Keep\" Background=\"Gray\" />\n" +
            "</StackPanel>";

        var cleaned = ToolkitFetcher.CleanXaml(xaml);

        Assert.IsFalse(cleaned.Contains("NoiseName", StringComparison.Ordinal),
            "an unreferenced x:Name is still stripped as demo noise");
        StringAssert.Contains(cleaned, "x:Name=\"Keep\"", "a referenced x:Name is preserved");
    }

    [TestMethod]
    public void NormalizeForPaste_RemovesBareMethodHandlers_WhenScenarioHasNoCodeBehind()
    {
        var xaml = "<controls:SettingsCard Header=\"A\" Click=\"OnCardClicked\" IsClickEnabled=\"True\" />";
        var stripped = NormalizeXamlWithoutCodeBehind(xaml);

        Assert.IsFalse(stripped.Contains("Click=\"OnCardClicked\"", StringComparison.Ordinal),
            "a bare-method Click handler with no code-behind must be stripped");
        StringAssert.Contains(stripped, "Header=\"A\"", "non-event attributes are preserved");
        StringAssert.Contains(stripped, "IsClickEnabled=\"True\"", "non-event attributes are preserved");
    }

    [TestMethod]
    public void NormalizeForPaste_KeepsCommandBindings()
    {
        var xaml = "<Button Click=\"{x:Bind SaveCommand}\" Content=\"Save\" />";
        var stripped = NormalizeXamlWithoutCodeBehind(xaml);

        StringAssert.Contains(stripped, "Click=\"{x:Bind SaveCommand}\"",
            "a command binding is not a dangling handler and must be kept");
    }

    [TestMethod]
    public void NormalizeForPaste_DoesNotTouchLookalikeAttributes()
    {
        // Attributes that aren't events must survive even with identifier-like values.
        var xaml = "<AppBarButton Icon=\"Add\" Label=\"Add\" Symbol=\"Edit\" />";
        var stripped = NormalizeXamlWithoutCodeBehind(xaml);

        Assert.AreEqual(xaml, stripped, "non-event attributes with identifier values must be untouched");
    }

    /// <summary>Run provider normalization over a scenario split out of a multi-instance
    /// sample — the case that carries XAML and no code-behind at all, so every bare-method
    /// handler in it is unbacked by construction.</summary>
    private static string NormalizeXamlWithoutCodeBehind(string xaml)
    {
        var scenario = new Scenario { Xaml = xaml, CSharp = null, Source = "toolkit" };
        ToolkitProvider.NormalizeForPaste(scenario);
        return scenario.Xaml!;
    }

    // --- Sample-option bindings: XAML must not reference docs-generated members ------

    // Real ColorPicker sample shape (CommunityToolkit/Windows): class-level
    // [ToolkitSample*Option("Name", …)] attributes back generated members the sample
    // XAML x:Binds to. ToolkitProvider.NormalizeForPaste strips those attributes, so the
    // members never exist in emitted C# — the bindings must be removed or the snippet
    // won't compile.
    private const string ColorPickerCs = """
        namespace ColorPickerExperiment.Samples;
        [ToolkitSampleBoolOption("AccentColors", true, Title = "ShowAccentColors")]
        [ToolkitSampleBoolOption("AlphaEnabled", true, Title = "IsAlphaEnabled")]
        [ToolkitSampleMultiChoiceOption("SpectrumShape", "Box", "Ring", Title = "ColorSpectrumShape")]
        [ToolkitSample(id: nameof(ColorPickerSample), "ColorPicker", description: "…")]
        public sealed partial class ColorPickerSample : Page { }
        """;

    private const string ColorPickerXaml = """
        <controls:ColorPicker HorizontalAlignment="Center"
                              ColorSpectrumShape="{x:Bind local:ColorPickerSample.ConvertStringToColorSpectrumShape(SpectrumShape), Mode=OneWay}"
                              IsAlphaEnabled="{x:Bind AlphaEnabled, Mode=OneWay}"
                              ShowAccentColors="{x:Bind AccentColors, Mode=OneWay}"
                              Color="LightBlue" />
        """;

    [TestMethod]
    public void ExtractSampleOptionNames_CapturesEveryOptionMember()
    {
        var names = ToolkitFetcher.ExtractSampleOptionNames(ColorPickerCs);
        string[] expected = ["AccentColors", "AlphaEnabled", "SpectrumShape"];
        CollectionAssert.AreEquivalent(
            expected,
            names.ToArray(),
            "every [ToolkitSample*Option] first-arg member name must be captured");
    }

    [TestMethod]
    public void ExtractSampleOptionNames_IgnoresPlainToolkitSample()
    {
        // [ToolkitSample(id:, …)] registers the sample; it generates no bound member.
        var names = ToolkitFetcher.ExtractSampleOptionNames(
            "[ToolkitSample(id: nameof(X), \"X\", description: \"y\")] class X {}");
        Assert.AreEqual(0, names.Count, "plain ToolkitSample must not be treated as an option member");
    }

    [TestMethod]
    public void StripSampleOptionBindings_RemovesBindingsToGeneratedMembers()
    {
        var names = ToolkitFetcher.ExtractSampleOptionNames(ColorPickerCs);
        var stripped = ToolkitFetcher.StripSampleOptionBindings(ColorPickerXaml, names);

        Assert.IsFalse(stripped.Contains("SpectrumShape", StringComparison.Ordinal),
            "the converter binding referencing SpectrumShape must be removed");
        Assert.IsFalse(stripped.Contains("AlphaEnabled", StringComparison.Ordinal),
            "the x:Bind to AlphaEnabled must be removed");
        Assert.IsFalse(stripped.Contains("AccentColors", StringComparison.Ordinal),
            "the x:Bind to AccentColors must be removed");
    }

    [TestMethod]
    public void StripSampleOptionBindings_KeepsNonBindingAttributes()
    {
        var names = ToolkitFetcher.ExtractSampleOptionNames(ColorPickerCs);
        var stripped = ToolkitFetcher.StripSampleOptionBindings(ColorPickerXaml, names);

        StringAssert.Contains(stripped, "HorizontalAlignment=\"Center\"", "literal attributes must survive");
        StringAssert.Contains(stripped, "Color=\"LightBlue\"", "literal attributes must survive");
        StringAssert.Contains(stripped, "<controls:ColorPicker", "the control element itself must survive");
    }

    [TestMethod]
    public void StripSampleOptionBindings_LeavesBindingsToRealMembersAlone()
    {
        // A binding to a member that is NOT a stripped sample option must be preserved.
        var names = ToolkitFetcher.ExtractSampleOptionNames(ColorPickerCs);
        var xaml = "<TextBlock Text=\"{x:Bind ViewModel.Title, Mode=OneWay}\" />";
        var stripped = ToolkitFetcher.StripSampleOptionBindings(xaml, names);
        Assert.AreEqual(xaml, stripped, "bindings to real members must be untouched");
    }

    [TestMethod]
    public void StripSampleOptionBindings_WholeWordMatch_DoesNotStripSimilarNames()
    {
        var xaml = "<Ctl Foo=\"{x:Bind AlphaEnabledExtra, Mode=OneWay}\" />";
        string[] names = ["AlphaEnabled"];
        var stripped = ToolkitFetcher.StripSampleOptionBindings(xaml, names);
        Assert.AreEqual(xaml, stripped, "a partial name (AlphaEnabledExtra) must not be stripped by 'AlphaEnabled'");
    }

    [TestMethod]
    public void StripSampleOptionBindings_NoOptions_ReturnsInputUnchanged()
    {
        Assert.AreEqual(ColorPickerXaml,
            ToolkitFetcher.StripSampleOptionBindings(ColorPickerXaml, System.Array.Empty<string>()));
    }
}

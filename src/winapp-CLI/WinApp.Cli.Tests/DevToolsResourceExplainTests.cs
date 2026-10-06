// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Commands;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class DevToolsResourceExplainTests
{
    private const string Ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

    private const string AppXaml =
        "<Application " + Ns + ">\n" +                                                          // 1
        "  <Application.Resources>\n" +                                                         // 2
        "    <ResourceDictionary>\n" +                                                          // 3
        "      <ResourceDictionary.MergedDictionaries>\n" +                                     // 4
        "        <ResourceDictionary Source=\"ms-appx:///Styles/Tokens.xaml\" />\n" +           // 5
        "        <ResourceDictionary Source=\"Styles/Buttons.xaml\" />\n" +                     // 6
        "      </ResourceDictionary.MergedDictionaries>\n" +                                    // 7
        "      <SolidColorBrush x:Key=\"AccentBrush\" Color=\"#FF0078D4\" />\n" +               // 8
        "    </ResourceDictionary>\n" +                                                         // 9
        "  </Application.Resources>\n" +                                                        // 10
        "</Application>";

    private const string TokensXaml =
        "<ResourceDictionary " + Ns + ">\n" +                                                   // 1
        "  <ResourceDictionary.ThemeDictionaries>\n" +                                          // 2
        "    <ResourceDictionary x:Key=\"Default\">\n" +                                        // 3
        "      <StaticResource x:Key=\"CardBackground\" ResourceKey=\"ControlFillColorDefaultBrush\" />\n" + // 4
        "    </ResourceDictionary>\n" +                                                         // 5
        "    <ResourceDictionary x:Key=\"Light\">\n" +                                          // 6
        "      <SolidColorBrush x:Key=\"CardBackground\" Color=\"#FFFFFFFF\" />\n" +            // 7
        "    </ResourceDictionary>\n" +                                                         // 8
        "    <ResourceDictionary x:Key=\"HighContrast\">\n" +                                   // 9
        "      <SolidColorBrush x:Key=\"CardBackground\" Color=\"{ThemeResource SystemColorWindowColor}\" />\n" + // 10
        "    </ResourceDictionary>\n" +                                                         // 11
        "  </ResourceDictionary.ThemeDictionaries>\n" +                                         // 12
        "</ResourceDictionary>";

    private const string ButtonsXaml =
        "<ResourceDictionary " + Ns + ">\n" +                                                   // 1
        "  <Style x:Key=\"BaseCardButton\" TargetType=\"Button\">\n" +                          // 2
        "    <Setter Property=\"Background\" Value=\"{ThemeResource CardBackground}\" />\n" +   // 3
        "    <Setter Property=\"Foreground\" Value=\"{StaticResource AccentBrush}\" />\n" +     // 4
        "  </Style>\n" +                                                                        // 5
        "  <Style x:Key=\"CardButton\" TargetType=\"Button\" BasedOn=\"{StaticResource BaseCardButton}\">\n" + // 6
        "    <Setter Property=\"Padding\" Value=\"8\" />\n" +                                   // 7
        "    <Setter Property=\"Template\">\n" +                                                // 8
        "      <Setter.Value>\n" +                                                              // 9
        "        <ControlTemplate TargetType=\"Button\" />\n" +                                 // 10
        "      </Setter.Value>\n" +                                                             // 11
        "    </Setter>\n" +                                                                     // 12
        "  </Style>\n" +                                                                        // 13
        "  <Style TargetType=\"TextBlock\">\n" +                                                // 14
        "    <Setter Property=\"FontSize\" Value=\"14\" />\n" +                                 // 15
        "  </Style>\n" +                                                                        // 16
        "</ResourceDictionary>";

    private const string PageXaml =
        "<Page " + Ns + ">\n" +                                                                 // 1
        "  <Page.Resources>\n" +                                                                // 2
        "    <SolidColorBrush x:Key=\"AccentBrush\" Color=\"Red\" />\n" +                      // 3
        "  </Page.Resources>\n" +                                                               // 4
        "  <Grid Background=\"{ThemeResource CardBackground}\" />\n" +                          // 5
        "</Page>";

    private const string OrphanXaml =
        "<ResourceDictionary " + Ns + ">\n" +
        "  <SolidColorBrush x:Key=\"CardBackground\" Color=\"Green\" />\n" +
        "</ResourceDictionary>";

    private static XamlResourceIndex Index() => XamlResourceIndex.FromSources(
    [
        ("App.xaml", AppXaml),
        ("Styles\\Tokens.xaml", TokensXaml),
        ("Styles/Buttons.xaml", ButtonsXaml),
        ("Pages/HomePage.xaml", PageXaml),
        ("Unused/Orphan.xaml", OrphanXaml),
        ("Broken.xaml", "<Page"),
        ("Unreadable.xaml", null),
    ]);

    [TestMethod]
    public void Index_RecordsThemeBranches_AppScope_AndAliases()
    {
        var index = Index();
        var card = index.Find("CardBackground");
        Assert.HasCount(4, card);

        var byTheme = card.Where(d => d.File == "Styles/Tokens.xaml").ToDictionary(d => d.Theme!);
        Assert.AreEqual(4, byTheme["Default"].Line);
        Assert.AreEqual("ControlFillColorDefaultBrush", byTheme["Default"].AliasOf);
        Assert.AreEqual(7, byTheme["Light"].Line);
        Assert.AreEqual("#FFFFFFFF", byTheme["Light"].Value);
        Assert.AreEqual(10, byTheme["HighContrast"].Line);
        Assert.IsTrue(byTheme.Values.All(d => d.AppScope), "Merged through App.xaml with an ms-appx:/// source.");

        var orphan = card.Single(d => d.File == "Unused/Orphan.xaml");
        Assert.IsFalse(orphan.AppScope, "Nothing merges Orphan.xaml.");
        Assert.IsNull(orphan.Theme);

        Assert.IsTrue(index.Find("AccentBrush").Single(d => d.File == "App.xaml").AppScope);
        Assert.IsFalse(index.Find("AccentBrush").Single(d => d.File == "Pages/HomePage.xaml").AppScope);
        Assert.IsEmpty(index.Find("Default"), "ThemeDictionaries branch keys are containers, not resources.");
        CollectionAssert.AreEquivalent(SkippedFiles, index.SkippedFiles.ToArray());
    }

    [TestMethod]
    public void Index_RecordsStyles_SettersAndBasedOn()
    {
        var index = Index();
        var card = index.StyleAt("Styles\\Buttons.xaml", 6);
        Assert.IsNotNull(card);
        Assert.AreEqual("CardButton", card.Key);
        Assert.AreEqual("BaseCardButton", card.BasedOn);
        Assert.IsTrue(card.AppScope, "Merged by a relative Source from App.xaml.");
        Assert.AreEqual(7, card.Setters.Single(s => s.Property == "Padding").Line);
        Assert.AreEqual("<ControlTemplate>", card.Setters.Single(s => s.Property == "Template").Value);

        var implicitStyle = index.StyleAt("Styles/Buttons.xaml", 14);
        Assert.IsNotNull(implicitStyle);
        Assert.IsNull(implicitStyle.Key);
        Assert.AreEqual("TextBlock", implicitStyle.TargetType);

        Assert.IsNull(index.StyleAt("Styles/Buttons.xaml", 3), "Line 3 is a Setter, not a Style.");
        Assert.AreEqual(2, index.StylesWithKey("BaseCardButton", null).Single().Line);
    }

    [TestMethod]
    public void Index_ResolvesCodeBehindMergedDictionaryByClass()
    {
        var index = XamlResourceIndex.FromSources(
        [
            ("App.xaml", "<Application " + Ns + " xmlns:local=\"using:Demo\"><Application.Resources><ResourceDictionary>" +
                "<ResourceDictionary.MergedDictionaries><local:Templates /></ResourceDictionary.MergedDictionaries>" +
                "</ResourceDictionary></Application.Resources></Application>"),
            ("Styles/Templates.xaml", "<ResourceDictionary " + Ns + " x:Class=\"Demo.Templates\">" +
                "<x:Double x:Key=\"CardWidth\">240</x:Double></ResourceDictionary>"),
        ]);
        var width = index.Find("CardWidth").Single();
        Assert.IsTrue(width.AppScope);
        Assert.AreEqual("240", width.Value);
    }

    [TestMethod]
    [DataRow("{ThemeResource ButtonForeground}", "themeResource", "ButtonForeground")]
    [DataRow("{StaticResource  AccentBrush }", "staticResource", "AccentBrush")]
    [DataRow("{StaticResource ResourceKey=AccentBrush}", "staticResource", "AccentBrush")]
    [DataRow("{Binding Foo}", null, null)]
    [DataRow("{x:Bind Foo}", null, null)]
    [DataRow("{}{ThemeResource Escaped}", null, null)]
    [DataRow("Red", null, null)]
    [DataRow("{ThemeResource}", null, null)]
    public void ResourceReference_ReadsOnlyThemeAndStaticResource(string markup, string? kind, string? key)
    {
        var reference = XamlResourceIndex.ResourceReference(markup);
        Assert.AreEqual(kind, reference?.Kind);
        Assert.AreEqual(key, reference?.Key);
    }

    [TestMethod]
    public void StyleSetter_FollowsBasedOn_ThenDefaultBranchAlias_ToWinUI()
    {
        var explanation = Explain(
            Row("Background", "#0FFFFFFF", "Style", Chain("Style", "Styles/Buttons.xaml", 6)),
            style: StyleRow("CardButton"), theme: "Dark");

        Assert.AreEqual("style", explanation.Origin.Kind);
        Assert.AreEqual("explicit", explanation.Origin.StyleKind);
        Assert.AreEqual("CardButton", explanation.Origin.StyleKey);
        Assert.AreEqual("app", explanation.Origin.StyleDefinedIn);
        Assert.AreEqual(new ExplainLocation("Styles/Buttons.xaml", 3), explanation.Origin.At);
        CollectionAssert.AreEqual(CardButtonBasedOn, explanation.Origin.BasedOn!.ToArray());
        Assert.AreEqual("{ThemeResource CardBackground}", explanation.Origin.Authored);

        Assert.HasCount(2, explanation.Resources);
        var card = explanation.Resources[0];
        Assert.AreEqual("themeResource", card.Kind);
        var applied = card.Definitions.Single(d => d.Applies);
        Assert.AreEqual("Default", applied.Theme, "Tokens.xaml has no Dark branch, so Default applies for Dark.");
        Assert.AreEqual(new ExplainLocation("Styles/Tokens.xaml", 4), applied.At);
        Assert.IsFalse(card.Definitions.Single(d => d.At.File == "Unused/Orphan.xaml").InScope);

        var alias = explanation.Resources[1];
        Assert.AreEqual(("alias", "ControlFillColorDefaultBrush", true), (alias.Kind, alias.Key, alias.Framework));
        Assert.AreEqual(new ExplainLocation("Styles/Buttons.xaml", 3), explanation.ChangeAt,
            "The chain ends in WinUI, so the nearest edit is the setter.");
    }

    [TestMethod]
    public void LightTheme_PicksTheLightBranch()
    {
        var explanation = Explain(
            Row("Background", "#FFFFFFFF", "Style", Chain("Style", "Styles/Buttons.xaml", 2)),
            style: StyleRow("BaseCardButton"), theme: "Light");
        var applied = explanation.Resources.Single().Definitions.Single(d => d.Applies);
        Assert.AreEqual(("Light", 7), (applied.Theme, applied.At.Line));
        Assert.IsFalse(explanation.Resources.Single().Framework);
    }

    [TestMethod]
    public void LocalStaticResource_PrefersThePageDictionaryOverAppScope()
    {
        var row = Row("Foreground", "#FFFF0000", "Local", Chain("Local", "Pages/HomePage.xaml", 5),
            authored: "{StaticResource AccentBrush}", authoredKind: "staticResource", authoredKey: "AccentBrush");
        var explanation = Explain(row);

        Assert.AreEqual("local", explanation.Origin.Kind);
        var resource = explanation.Resources.Single();
        Assert.AreEqual("staticResource", resource.Kind);
        Assert.AreEqual(new ExplainLocation("Pages/HomePage.xaml", 3), resource.Definitions[0].At);
        Assert.IsTrue(resource.Definitions.All(d => d.Applies), "Both are in scope; the page's own entry is listed first.");
        StringAssert.Contains(string.Join("\n", explanation.Notes), "more than one dictionary");
    }

    [TestMethod]
    public void WinUIStyle_WithoutSourceInfo_IsLabelledAndOffersBasedOn()
    {
        var explanation = Explain(
            Row("FontWeight", "SemiBold", "Style", Chain("Style", runtimeFile: "ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml")),
            style: StyleRow("BodyStrongTextBlockStyle"), theme: "Dark");

        Assert.AreEqual(("style", "winui", "BodyStrongTextBlockStyle"),
            (explanation.Origin.Kind, explanation.Origin.StyleDefinedIn, explanation.Origin.StyleKey));
        Assert.IsNull(explanation.ChangeAt);
        StringAssert.Contains(explanation.Notes.Single(), "BasedOn=\"{StaticResource BodyStrongTextBlockStyle}\"");

        var lines = DevToolsResourcesExplainCommand.Render(explanation, explanation.Notes).ToList();
        StringAssert.Contains(lines[1], "WinUI resources");
        Assert.IsNull(DevToolsResourcesExplainCommand.ChangeIt(explanation));
    }

    [TestMethod]
    public void BuiltInStyle_AndDefault_HaveNoResources()
    {
        var builtIn = Explain(Row("Background", "#00000000", "Built-in style",
            Chain("Built-in style", runtimeFile: "ms-resource:///Files/Microsoft.UI.Xaml;component/themes/generic.xaml")));
        Assert.AreEqual("defaultStyle", builtIn.Origin.Kind);
        Assert.IsEmpty(builtIn.Resources);

        var unset = Explain(Row("Margin", "0,0,0,0", "Default", Chain("Default")));
        Assert.AreEqual("default", unset.Origin.Kind);
        Assert.IsNull(DevToolsResourcesExplainCommand.ChangeIt(unset));
    }

    [TestMethod]
    public void UnknownProjectFolder_StillReportsTheKey()
    {
        var row = Row("Background", "#FF000000", "Local", Chain("Local", "Pages/HomePage.xaml", 5),
            authored: "{ThemeResource CardBackground}", authoredKind: "themeResource", authoredKey: "CardBackground");
        var explanation = ExplainWithoutIndex(row);
        Assert.AreEqual("CardBackground", explanation.Resources.Single().Key);
        StringAssert.Contains(string.Join("\n", explanation.Notes), "project folder is unknown");
    }

    [TestMethod]
    public void ChangeIt_NamesWhatTheEditAffects()
    {
        var style = Explain(Row("Background", "#0FFFFFFF", "Style", Chain("Style", "Styles/Buttons.xaml", 6)),
            style: StyleRow("CardButton"), theme: "Dark");
        Assert.AreEqual("edit Styles/Buttons.xaml:3 (affects elements that use this Style), or override " +
            "ControlFillColorDefaultBrush in App.xaml's resources (affects every control that uses it)",
            DevToolsResourcesExplainCommand.ChangeIt(style));

        var token = Explain(Row("Background", "#FFFFFFFF", "Style", Chain("Style", "Styles/Buttons.xaml", 2)),
            style: StyleRow("BaseCardButton"), theme: "Light");
        Assert.AreEqual("edit Styles/Tokens.xaml:7 (changes CardBackground everywhere it is used)",
            DevToolsResourcesExplainCommand.ChangeIt(token));

        var local = Explain(Row("Background", "#FFFFFFFF", "Local", Chain("Local", "Pages/HomePage.xaml", 5),
            authored: "{ThemeResource Missing}", authoredKind: "themeResource", authoredKey: "Missing"));
        Assert.AreEqual("edit Pages/HomePage.xaml:5 (affects this element only), or override Missing in App.xaml's " +
            "resources (affects every control that uses it)", DevToolsResourcesExplainCommand.ChangeIt(local));
    }

    [TestMethod]
    public void Render_PrintsTheChainInOrder()
    {
        var explanation = Explain(Row("Background", "#0FFFFFFF", "Style", Chain("Style", "Styles/Buttons.xaml", 6)),
            style: StyleRow("CardButton"), theme: "Dark");
        var text = string.Join("\n", DevToolsResourcesExplainCommand.Render(explanation, explanation.Notes));
        var order = new[]
        {
            "Background = #0FFFFFFF",
            "CardButton (explicit)  [grey]Styles/Buttons.xaml:3",
            "via BasedOn BaseCardButton",
            "ThemeResource  CardBackground",
            "element theme: Dark",
            "Styles/Tokens.xaml:4[/] [grey][[Default]]",
            "also in Styles/Tokens.xaml:7 (Light theme only)",
            "alias of       ControlFillColorDefaultBrush",
            "WinUI default resources",
            "Change it:",
        };
        var at = 0;
        foreach (var expected in order)
        {
            var found = text.IndexOf(expected, at, StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, found, $"Missing or out of order: '{expected}'\n{text}");
            at = found + expected.Length;
        }
        Assert.IsFalse(text.Split('\n').Any(l => l.EndsWith(' ')), "No trailing spaces.");
    }

    private static ResourceExplanation Explain(JsonElement row, DevToolsPropertyRow? style = null, string? theme = "Dark") =>
        DevToolsResourceExplainer.Explain(row, DevToolsPropertyRow.FromElement(row)!, style, theme, Index());

    private static ResourceExplanation ExplainWithoutIndex(JsonElement row) =>
        DevToolsResourceExplainer.Explain(row, DevToolsPropertyRow.FromElement(row)!, null, "Dark", null);

    private static DevToolsPropertyRow StyleRow(string key) =>
        new("Style", "Microsoft.UI.Xaml.Style", "Microsoft.UI.Xaml.Style", "Local", null,
            $"{{StaticResource {key}}}", "staticResource", key, null, false);

    private static readonly string[] SkippedFiles = ["Broken.xaml", "Unreadable.xaml"];
    private static readonly string[] CardButtonBasedOn = ["BaseCardButton"];

    private static Dictionary<string, object?> Chain(string source, string? file = null, int line = 0, string? runtimeFile = null) =>
        new Dictionary<string, object?>
        {
            ["source"] = source,
            ["winner"] = true,
            ["targetType"] = "Microsoft.UI.Xaml.Controls.Button",
            ["file"] = runtimeFile ?? (file is null ? null : "ms-appx:///" + file),
            ["authoredFileName"] = file,
            ["authoredLineNumber"] = file is null ? null : line,
        };

    private static JsonElement Row(
        string name, string value, string source, object chain,
        string? authored = null, string? authoredKind = null, string? authoredKey = null) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["name"] = name,
            ["value"] = value,
            ["valueType"] = "Microsoft.UI.Xaml.Media.SolidColorBrush",
            ["valueSource"] = source,
            ["authored"] = authored,
            ["authoredKind"] = authoredKind,
            ["authoredKey"] = authoredKey,
            ["chain"] = new[] { chain },
        });
}

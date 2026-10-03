// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Services.DevTools;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Tests;

[TestClass]
public class CommentElementContextTests
{
    private const string AppXaml = """
        <Application xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Application.Resources>
                <Style x:Key="FocusButtonStyle" TargetType="Button">
                    <Setter Property="Background" Value="{ThemeResource AccentFillColorDefaultBrush}" />
                    <Setter Property="Foreground" Value="{StaticResource TextOnAccentFillColorPrimaryBrush}" />
                </Style>
                <Style TargetType="TextBlock">
                    <Setter Property="Foreground" Value="Red" />
                </Style>
            </Application.Resources>
        </Application>
        """;

    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Directory.CreateTempSubdirectory("winapp-comment-context-");
        File.WriteAllText(Path.Combine(_root.FullName, "App.xaml"), AppXaml);
    }

    [TestCleanup]
    public void Cleanup() => _root.Delete(recursive: true);

    private static object Row(string name, string value, string source, object? chain = null, string? authored = null,
        string? kind = null, string? key = null, bool redacted = false) => new
        {
            name, value, valueType = "Microsoft.UI.Xaml.Media.SolidColorBrush", valueSource = source,
            authored, authoredKind = kind, authoredKey = key, redacted, chain,
        };

    private static object[] Winner(string source, string file, int? line = null) =>
        [new { source, value = "", winner = true, file, line }];

    private static string Props(params object[] rows) => JsonSerializer.Serialize(new { props = rows });

    [TestMethod]
    public void AppStyleSetter_ReportsItsResourceKeyFileAndLine()
    {
        var (style, brushes) = CommentElementContext.Read(Props(
            Row("Style", "{Style}", "Local", authored: "{StaticResource FocusButtonStyle}", kind: "staticResource", key: "FocusButtonStyle"),
            Row("Background", "#FF0067C0", "Style", Winner("Style", "ms-appx:///App.xaml", 4)),
            Row("Foreground", "#FFFFFFFF", "Style", Winner("Style", "ms-appx:///App.xaml", 4))), _root.FullName);

        Assert.AreEqual("FocusButtonStyle", style!.Key);
        Assert.AreEqual("staticResource", style.Kind);
        Assert.AreEqual("Button", style.TargetType);
        Assert.AreEqual("App.xaml", style.File);
        Assert.AreEqual(4, style.Line);
        var background = brushes!.Single(b => b.Property == "Background");
        Assert.AreEqual("#FF0067C0", background.Value);
        Assert.AreEqual("Style", background.Source);
        Assert.AreEqual("AccentFillColorDefaultBrush", background.ResourceKey);
        Assert.AreEqual("themeResource", background.ResourceKind);
        Assert.AreEqual("App.xaml", background.File);
        Assert.AreEqual(5, background.Line, "The line is the setter's.");
        var foreground = brushes!.Single(b => b.Property == "Foreground");
        Assert.AreEqual("TextOnAccentFillColorPrimaryBrush", foreground.ResourceKey);
        Assert.AreEqual("staticResource", foreground.ResourceKind);
    }

    [TestMethod]
    public void PageLevelStyle_ReportsItsFileAndLine()
    {
        Directory.CreateDirectory(Path.Combine(_root.FullName, "Pages"));
        File.WriteAllText(Path.Combine(_root.FullName, "Pages", "SettingsPage.xaml"), """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Page.Resources>
                    <Style x:Key="SectionHeaderStyle" TargetType="TextBlock">
                        <Setter Property="FontSize" Value="20" />
                    </Style>
                </Page.Resources>
            </Page>
            """);

        var (style, _) = CommentElementContext.Read(Props(
            Row("Style", "{Style}", "Local", authored: "{StaticResource SectionHeaderStyle}", kind: "staticResource", key: "SectionHeaderStyle")),
            _root.FullName);

        Assert.AreEqual("SectionHeaderStyle", style!.Key);
        Assert.AreEqual(Path.Combine("Pages", "SettingsPage.xaml"), style.File);
        Assert.AreEqual(4, style.Line);
        Assert.AreEqual("TextBlock", style.TargetType);
    }

    [TestMethod]
    public void LocalResourceReference_ReportsTheAuthoredKey()
    {
        var (style, brushes) = CommentElementContext.Read(Props(
            Row("Foreground", "#9E000000", "Local", Winner("Local", "ms-appx:///MainWindow.xaml", 40),
                "{ThemeResource TextFillColorSecondaryBrush}", "themeResource", "TextFillColorSecondaryBrush")), _root.FullName);

        Assert.IsNull(style);
        var brush = brushes!.Single();
        Assert.AreEqual("TextFillColorSecondaryBrush", brush.ResourceKey);
        Assert.AreEqual("themeResource", brush.ResourceKind);
        Assert.AreEqual("#9E000000", brush.Value);
        Assert.IsNull(brush.File, "A local value belongs to the element the comment is anchored to.");
    }

    [TestMethod]
    public void ImplicitAppStyle_IsReportedAsImplicit()
    {
        var (style, brushes) = CommentElementContext.Read(Props(
            Row("Foreground", "#FFFF0000", "Style", Winner("Style", "ms-appx:///App.xaml", 8))), _root.FullName);

        Assert.AreEqual("implicit", style!.Kind);
        Assert.IsNull(style.Key);
        Assert.AreEqual("TextBlock", style.TargetType);
        var brush = brushes!.Single();
        Assert.IsNull(brush.ResourceKey, "A literal setter value has no resource.");
        Assert.AreEqual(9, brush.Line);
    }

    [TestMethod]
    public void FrameworkStylesDefaultsAndRedactedValues_StayMinimal()
    {
        var (style, brushes) = CommentElementContext.Read(Props(
            Row("Background", "#B3FFFFFF", "Style", Winner("Style", "ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml")),
            Row("BorderBrush", "", "Default"),
            Row("Fill", "#FF000000", "Local", redacted: true),
            Row("Width", "10", "Local")), _root.FullName);

        Assert.IsNull(style);
        Assert.HasCount(2, brushes!);
        var background = brushes!.Single(b => b.Property == "Background");
        Assert.AreEqual("Style", background.Source);
        Assert.IsNull(background.ResourceKey);
        Assert.IsNull(background.File, "A framework style is not a project file.");
        Assert.IsNull(brushes!.Single(b => b.Property == "Fill").Value, "A redacted value is not stored.");
    }

    [TestMethod]
    public void Lines_SummarizeStyleAndBrushes()
    {
        var context = new CommentContext
        {
            Style = new() { Key = "FocusButtonStyle", Kind = "staticResource", File = "App.xaml", Line = 4 },
            Brushes = [new() { Property = "Background", Value = "#FF0067C0", Source = "Style",
                ResourceKey = "AccentFillColorDefaultBrush", ResourceKind = "themeResource", File = "App.xaml", Line = 5 }],
        };

        var lines = CommentElementContext.Lines(context).ToArray();
        Assert.HasCount(2, lines);
        Assert.AreEqual("Style: FocusButtonStyle, App.xaml:4", lines[0]);
        Assert.AreEqual("Background: #FF0067C0 from ThemeResource AccentFillColorDefaultBrush (Style, App.xaml:5)", lines[1]);
    }

    [TestMethod]
    public void CaptureElement_StoresTheContextOnTheComment()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", """[{"handle":"2","name":"","type":"Button","children":[]}]""")
            .Answer("Property.get", Props(Row("Background", "#FF0067C0", "Style", Winner("Style", "ms-appx:///App.xaml", 4))))
            .Answer("Source.get", """{"fileName":""}""")
            .Answer("Internal.elementAnchor", """{"anchor":"","unique":false}""")
            .Answer("Internal.sourceRoot", JsonSerializer.Serialize(new { sourceRoot = _root.FullName }));

        var captured = CommentSelectionCapture.CaptureElement((uint)agent.Pid, "2").Element!;

        Assert.AreEqual("FocusButtonStyle", captured.Style!.Key);
        Assert.AreEqual("AccentFillColorDefaultBrush", captured.Brushes!.Single().ResourceKey);
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Xml.Linq;
using WinApp.Cli.Commands;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class XamlStyleCopierTests
{
    private const string Ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

    private const string Generic =
        "<ResourceDictionary " + Ns + " xmlns:primitives=\"using:Microsoft.UI.Xaml.Controls.Primitives\">\n" + // 1
        "    <Style TargetType=\"Button\" BasedOn=\"{StaticResource DefaultButtonStyle}\" />\n" +                // 2
        "    <Style x:Key=\"DefaultButtonStyle\" TargetType=\"Button\">\n" +                                     // 3
        "        <Setter Property=\"Background\" Value=\"{ThemeResource ButtonBackground}\" />\n" +              // 4
        "        <Setter Property=\"Template\">\n" +                                                             // 5
        "            <Setter.Value>\n" +                                                                         // 6
        "                <ControlTemplate TargetType=\"Button\">\n" +                                            // 7
        "                    <primitives:ContentPresenter x:Name=\"ContentPresenter\" />\n" +                    // 8
        "                </ControlTemplate>\n" +                                                                 // 9
        "            </Setter.Value>\n" +                                                                        // 10
        "        </Setter>\n" +                                                                                  // 11
        "    </Style>\n" +                                                                                       // 12
        "    <Style x:Key=\"AccentButtonStyle\" TargetType=\"Button\" BasedOn=\"{StaticResource DefaultButtonStyle}\">\n" + // 13
        "        <Setter Property=\"Foreground\" Value=\"{ThemeResource AccentButtonForeground}\" />\n" +       // 14
        "    </Style>\n" +                                                                                       // 15
        "    <Style TargetType=\"CheckBox\">\n" +                                                                // 16
        "        <Setter Property=\"Padding\" Value=\"8,5,0,0\" />\n" +                                          // 17
        "    </Style>\n" +                                                                                       // 18
        "</ResourceDictionary>";

    private static (XDocument Document, XamlStyleCopier.SourceText Source) Load(string text)
    {
        var source = new XamlStyleCopier.SourceText(text, false);
        return (XamlStyleCopier.Parse(text, 1_000_000)!, source);
    }

    private static XamlStyleCopier.FoundStyle Find(string? key, string type)
    {
        var (doc, _) = Load(Generic);
        return XamlStyleCopier.FindWinUIStyle(doc, Generic, key, type)!;
    }

    [TestMethod]
    public void FindWinUIStyle_FollowsTheEmptyDefaultStyleToTheKeyedStyle()
    {
        var found = Find(null, "Button");

        Assert.AreEqual("DefaultButtonStyle", found.Key);
        Assert.AreEqual("DefaultButtonStyle", found.FollowedFrom);
        Assert.AreEqual(3, found.Line);
    }

    [TestMethod]
    public void FindWinUIStyle_ByKeyAndImplicitWithSetters()
    {
        Assert.AreEqual(13, Find("AccentButtonStyle", "Button").Line);
        var checkBox = Find(null, "CheckBox");
        Assert.AreEqual(16, checkBox.Line);
        Assert.IsNull(checkBox.FollowedFrom);
        var (doc, _) = Load(Generic);
        Assert.IsNull(XamlStyleCopier.FindWinUIStyle(doc, Generic, "Missing", "Button"));
        Assert.IsNull(XamlStyleCopier.FindWinUIStyle(doc, Generic, null, "Slider"));
    }

    [TestMethod]
    public void Render_RenamesReindentsAndDeclaresMissingPrefixes()
    {
        var destination = new Dictionary<string, string>
        {
            [""] = "http://schemas.microsoft.com/winfx/2006/xaml/presentation",
            ["x"] = "http://schemas.microsoft.com/winfx/2006/xaml",
        };

        var block = XamlStyleCopier.Render(Find(null, "Button"), "ButtonStyle1", destination, "        ");

        var lines = block.Split('\n');
        Assert.AreEqual(
            "        <Style x:Key=\"ButtonStyle1\" TargetType=\"Button\" xmlns:primitives=\"using:Microsoft.UI.Xaml.Controls.Primitives\">",
            lines[0]);
        Assert.AreEqual("            <Setter Property=\"Background\" Value=\"{ThemeResource ButtonBackground}\" />", lines[1]);
        Assert.AreEqual("                        <primitives:ContentPresenter x:Name=\"ContentPresenter\" />", lines[5]);
        Assert.AreEqual("        </Style>", lines[^1]);
        Assert.AreEqual(10, lines.Length);
    }

    [TestMethod]
    public void Render_ImplicitCopyHasNoKeyAndKeepsBasedOn()
    {
        var destination = XamlStyleCopier.RootNamespaces(Load(Generic).Document);

        var block = XamlStyleCopier.Render(Find("AccentButtonStyle", "Button"), null, destination, "  ");

        Assert.IsTrue(block.StartsWith("  <Style TargetType=\"Button\" BasedOn=\"{StaticResource DefaultButtonStyle}\">", StringComparison.Ordinal), block);
        Assert.IsFalse(block.Contains("x:Key", StringComparison.Ordinal));
        Assert.IsFalse(block.Contains("xmlns", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Render_RetargetsAnImplicitCopy()
    {
        var destination = XamlStyleCopier.RootNamespaces(Load(Generic).Document);

        var block = XamlStyleCopier.Render(Find(null, "CheckBox"), null, destination, string.Empty, targetType: "RadioButton");

        StringAssert.StartsWith(block, "<Style TargetType=\"RadioButton\">");
    }

    [TestMethod]
    public void Render_DeclaresXWhenTheDestinationLacksIt()
    {
        var destination = new Dictionary<string, string> { [""] = "http://schemas.microsoft.com/winfx/2006/xaml/presentation" };

        var block = XamlStyleCopier.Render(Find(null, "CheckBox"), "CheckBoxStyle1", destination, string.Empty);

        StringAssert.StartsWith(block, "<Style x:Key=\"CheckBoxStyle1\" TargetType=\"CheckBox\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">");
    }

    [TestMethod]
    public void UniqueKeyAndIsValidKey()
    {
        var taken = new HashSet<string> { "ButtonStyle1", "ButtonStyle2" };
        Assert.AreEqual("ButtonStyle3", XamlStyleCopier.UniqueKey("Button", taken.Contains));
        Assert.IsTrue(XamlStyleCopier.IsValidKey("My.Button_Style-2"));
        Assert.IsFalse(XamlStyleCopier.IsValidKey("Bad Key"));
        Assert.IsFalse(XamlStyleCopier.IsValidKey("Bad}"));
        Assert.AreEqual("Button", XamlStyleCopier.ShortType("Microsoft.UI.Xaml.Controls.Button"));
        Assert.AreEqual("Thumb", XamlStyleCopier.ShortType("primitives:Thumb"));
    }

    private static string AddTo(string text, string block = "<Style x:Key=\"K\" />")
    {
        var (doc, source) = Load(text);
        var (insertion, error) = XamlStyleCopier.FindInsertion(source, doc);
        Assert.IsNotNull(insertion, error);
        var rendered = string.Concat(block.Split('\n').Select((l, i) => (i == 0 ? insertion.Indent : string.Empty) + l));
        var result = XamlStyleCopier.Apply(text, [XamlStyleCopier.Insert(insertion, rendered, source.Newline)]);
        Assert.IsNotNull(XamlStyleCopier.Parse(result, 1_000_000), result);
        Assert.AreEqual(insertion.Line, result.Split('\n').ToList().FindIndex(l => l.Contains("x:Key=\"K\"", StringComparison.Ordinal)) + 1, result);
        return result;
    }

    [TestMethod]
    public void FindInsertion_AppendsToTheApplicationResourceDictionary()
    {
        var app =
            "<Application " + Ns + ">\r\n" +
            "    <Application.Resources>\r\n" +
            "        <ResourceDictionary>\r\n" +
            "            <ResourceDictionary.MergedDictionaries>\r\n" +
            "                <XamlControlsResources xmlns=\"using:Microsoft.UI.Xaml.Controls\" />\r\n" +
            "            </ResourceDictionary.MergedDictionaries>\r\n" +
            "        </ResourceDictionary>\r\n" +
            "    </Application.Resources>\r\n" +
            "</Application>";

        var result = AddTo(app);

        StringAssert.Contains(result,
            "            </ResourceDictionary.MergedDictionaries>\r\n            <Style x:Key=\"K\" />\r\n        </ResourceDictionary>\r\n");
    }

    [TestMethod]
    public void FindInsertion_AppendsToARootDictionaryAndDirectResources()
    {
        StringAssert.Contains(AddTo("<ResourceDictionary " + Ns + ">\n  <Color x:Key=\"A\">Red</Color>\n</ResourceDictionary>"),
            "  <Color x:Key=\"A\">Red</Color>\n  <Style x:Key=\"K\" />\n</ResourceDictionary>");
        StringAssert.Contains(AddTo("<Page " + Ns + ">\n\t<Page.Resources>\n\t\t<Color x:Key=\"A\">Red</Color>\n\t\t<Color x:Key=\"B\">Blue</Color>\n\t</Page.Resources>\n</Page>"),
            "\t\t<Color x:Key=\"B\">Blue</Color>\n\t\t<Style x:Key=\"K\" />\n\t</Page.Resources>");
        StringAssert.Contains(AddTo("<ResourceDictionary " + Ns + "><Color x:Key=\"A\">Red</Color></ResourceDictionary>"),
            "<Color x:Key=\"A\">Red</Color>\n    <Style x:Key=\"K\" />\n</ResourceDictionary>");
    }

    [TestMethod]
    public void FindInsertion_CreatesResourcesWhenMissing()
    {
        var result = AddTo("<Application " + Ns + ">\n</Application>");

        Assert.AreEqual(
            "<Application " + Ns + ">\n    <Application.Resources>\n        <Style x:Key=\"K\" />\n    </Application.Resources>\n</Application>",
            result);
    }

    [TestMethod]
    public void FindInsertion_RefusesASingleNonDictionaryResource()
    {
        var (doc, source) = Load("<Application " + Ns + ">\n  <Application.Resources>\n    <XamlControlsResources xmlns=\"using:Microsoft.UI.Xaml.Controls\" />\n  </Application.Resources>\n</Application>");

        var (insertion, error) = XamlStyleCopier.FindInsertion(source, doc);

        Assert.IsNull(insertion);
        StringAssert.Contains(error, "XamlControlsResources");
    }

    private const string PageXaml =
        "<Page " + Ns + ">\n" +                                             // 1
        "  <StackPanel>\n" +                                                // 2
        "    <Button Content=\"A\" />\n" +                                  // 3
        "    <Button\n" +                                                   // 4
        "        x:Name=\"B\"\n" +                                          // 5
        "        Content=\"B\" />\n" +                                      // 6
        "    <Button Content=\"C\" Style=\"{StaticResource Old}\" />\n" +   // 7
        "    <Button>\n" +                                                  // 8
        "      <Button.Style><Style TargetType=\"Button\" /></Button.Style>\n" + // 9
        "    </Button>\n" +                                                 // 10
        "    <Button></Button>\n" +                                         // 11
        "  </StackPanel>\n" +                                               // 12
        "</Page>";

    private static (string? Result, XamlStyleCopier.StyleAttributeEdit? Edit, string? Error) SetOn(int line)
    {
        var (doc, source) = Load(PageXaml);
        var (edit, error) = XamlStyleCopier.SetStyleAttribute(source, doc, line, "Button", "Mine");
        return (edit is null ? null : XamlStyleCopier.Apply(PageXaml, [edit.Splice]), edit, error);
    }

    [TestMethod]
    public void SetStyleAttribute_AppendsInlineOrOnItsOwnLine()
    {
        StringAssert.Contains(SetOn(3).Result, "<Button Content=\"A\" Style=\"{StaticResource Mine}\" />");
        StringAssert.Contains(SetOn(4).Result, "        Content=\"B\"\n        Style=\"{StaticResource Mine}\" />");
        StringAssert.Contains(SetOn(11).Result, "<Button Style=\"{StaticResource Mine}\"></Button>");
    }

    [TestMethod]
    public void SetStyleAttribute_ReplacesAnExistingStyleAndReportsIt()
    {
        var (result, edit, _) = SetOn(7);

        StringAssert.Contains(result, "<Button Content=\"C\" Style=\"{StaticResource Mine}\" />");
        Assert.AreEqual("{StaticResource Old}", edit!.Previous);
    }

    [TestMethod]
    public void SetStyleAttribute_RefusesAPropertyElementOrAWrongLine()
    {
        StringAssert.Contains(SetOn(8).Error, "Button.Style");
        StringAssert.Contains(SetOn(2).Error, "No <Button> starts on line 2");
    }

    [TestMethod]
    public void Apply_CombinesAnAddAndASetInOneFileAndTracksLines()
    {
        var page = PageXaml.Replace("<Page " + Ns + ">\n", "<Page " + Ns + ">\n  <Page.Resources>\n    <Color x:Key=\"A\">Red</Color>\n  </Page.Resources>\n");
        var (doc, source) = Load(page);
        var (insertion, _) = XamlStyleCopier.FindInsertion(source, doc);
        var add = XamlStyleCopier.Insert(insertion!, "    <Style x:Key=\"Mine\" TargetType=\"Button\">\n    </Style>", "\n");
        var (set, _) = XamlStyleCopier.SetStyleAttribute(source, doc, 6, "Button", "Mine");

        var result = XamlStyleCopier.Apply(page, [add, set!.Splice]);

        var lines = result.Split('\n');
        Assert.AreEqual("    <Style x:Key=\"Mine\" TargetType=\"Button\">", lines[3]);
        Assert.AreEqual("    <Button Content=\"A\" Style=\"{StaticResource Mine}\" />", lines[7]);
        Assert.AreEqual(8, XamlStyleCopier.LineAfter(page, [add, set.Splice], set.Splice.Offset));
        Assert.AreEqual(4, XamlStyleCopier.LineAfter(page, [add, set.Splice], add.Offset));
    }

    [TestMethod]
    public void EncodeKeepsTheByteOrderMark()
    {
        var path = Path.Combine(Path.GetTempPath(), $"winapp-style-{Guid.NewGuid():N}.xaml");
        try
        {
            File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, (byte)'<', (byte)'a', (byte)'/', (byte)'>']);
            var text = XamlStyleCopier.ReadText(path, 1000)!;
            Assert.IsTrue(text.Bom);
            Assert.AreEqual("<a/>", text.Text);
            CollectionAssert.AreEqual(File.ReadAllBytes(path), XamlStyleCopier.Encode(text));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void FromAssets_FindsTheRestoredWinUIPackage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"winapp-assets-{Guid.NewGuid():N}");
        try
        {
            var packages = Path.Combine(root, "packages");
            var themes = Path.Combine(packages, "microsoft.windowsappsdk.winui", "1.8.1", "lib", "native", "Microsoft.UI", "Themes");
            var other = Path.Combine(packages, "microsoft.windowsappsdk.winui", "1.8.1", "lib", "net6.0", "Microsoft.WinUI", "Themes");
            Directory.CreateDirectory(themes);
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(themes, "generic.xaml"), "<x/>");
            File.WriteAllText(Path.Combine(other, "generic.xaml"), "<x/>");
            var obj = Path.Combine(root, "app", "obj");
            Directory.CreateDirectory(obj);
            File.WriteAllText(Path.Combine(obj, "project.assets.json"),
                "{\"libraries\":{\"Microsoft.WindowsAppSDK/1.8.1\":{\"path\":\"microsoft.windowsappsdk/1.8.1\"}," +
                "\"Microsoft.WindowsAppSDK.WinUI/1.8.1\":{\"path\":\"microsoft.windowsappsdk.winui/1.8.1\"}}," +
                "\"packageFolders\":{\"" + (Path.Combine(root, "missing") + "\\").Replace("\\", "\\\\") + "\":{},\"" +
                (packages + "\\").Replace("\\", "\\\\") + "\":{}}}");

            var generic = XamlStyleCopier.FindGenericXaml(Path.Combine(root, "app"));

            Assert.IsNotNull(generic);
            Assert.AreEqual(Path.Combine(themes, "generic.xaml"), generic.Path, ignoreCase: true);
            Assert.AreEqual("Microsoft.WindowsAppSDK.WinUI", generic.Package);
            Assert.AreEqual("1.8.1", generic.Version);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void RenderPreview_DescribesTheCopyAndTheElementEdit()
    {
        var source = new DevToolsResourcesCopyStyleCommand.CopySource("winui", "DefaultButtonStyle", true, "Button", "generic.xaml",
            @"C:\p\generic.xaml", 27424, "Microsoft.WindowsAppSDK.WinUI 1.8.1");
        var edits = new List<DevToolsResourcesCopyStyleCommand.PlannedEdit>
        {
            new("addStyle", "App.xaml", @"C:\app\App.xaml", 12, string.Join('\n', Enumerable.Range(1, 12).Select(i => $"<L{i}/>")), null, 12),
            new("setStyle", "MainPage.xaml", @"C:\app\MainPage.xaml", 20, "Style=\"{StaticResource ButtonStyle1}\"", "{StaticResource Old}", 1),
        };

        var lines = DevToolsResourcesCopyStyleCommand.Render(source, "ButtonStyle1", edits, [], written: false).ToList();

        Assert.AreEqual("Copies the WinUI default style for Button (DefaultButtonStyle), from Microsoft.WindowsAppSDK.WinUI 1.8.1 generic.xaml:27424", lines[0]);
        StringAssert.Contains(lines[1], "App.xaml:12");
        StringAssert.Contains(lines[1], "ButtonStyle1");
        Assert.IsTrue(lines.Any(l => l.Contains("4 more lines", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Any(l => l.Contains("MainPage.xaml:20", StringComparison.Ordinal) && l.Contains("was Style", StringComparison.Ordinal)));
        StringAssert.StartsWith(lines[^1], "Preview only");
    }
}

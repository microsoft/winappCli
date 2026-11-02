using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class StateDemoNameChecksContractTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [TestMethod]
    public void ManualCasesDoNotChangeTheExistingPresetRoute()
    {
        var (directory, page) = Read();
        var main = XElement.Load(Path.Combine(directory, "StateDemo", "MainWindow.xaml"));
        var tabs = main.Descendants(Presentation + "TabViewItem").ToArray();
        CollectionAssert.AreEqual(new[] { "NavBasics", "NavScrolling", "NavDialogs", "NavNameChecks" },
            tabs.Select(tab => (string)tab.Attribute("AutomationProperties.AutomationId")!).ToArray());
        var profile = LoopInput.Read(Path.Combine(directory, "loop-demo.json")).Profile;
        CollectionAssert.AreEqual(new[] { "NavBasics", "NavScrolling", "NavDialogs" },
            profile.Pages.Select(item => item.NavigationId).ToArray());
        CollectionAssert.AreEqual(new[] { "A", "B", "C", "D", "E", "F" },
            page.Descendants(Presentation + "Button").Select(button => (string)button.Attribute("Tag")!).ToArray());
        Assert.IsTrue(page.Descendants(Presentation + "CheckBox")
            .Any(control => (string?)control.Attribute("IsChecked") == "False"));
    }

    [TestMethod]
    public void FirstIconPairDiffersOnlyByAccessibleNameAndSourceIdentity()
    {
        var (_, page) = Read();
        var named = Button(page, "A");
        var unnamed = Button(page, "B");
        Assert.AreEqual("Refresh preview", (string?)named.Attribute("AutomationProperties.Name"));
        Assert.IsNull(unnamed.Attribute("AutomationProperties.Name"));
        foreach (var attribute in new[] { "Content", "AutomationProperties.LabeledBy", "ToolTipService.ToolTip" })
        {
            Assert.IsNull(unnamed.Attribute(attribute), attribute);
        }
        Assert.IsFalse(unnamed.Descendants(Presentation + "TextBlock").Any());
        Assert.IsTrue(unnamed.Descendants(Presentation + "Path").All(path =>
            (string?)path.Attribute("AutomationProperties.AccessibilityView") == "Raw"));
        foreach (var button in new[] { named, unnamed })
        {
            button.Attribute("AutomationProperties.Name")?.Remove();
            button.Attribute("AutomationProperties.AutomationId")?.Remove();
            button.Attribute(Xaml + "Name")?.Remove();
            button.Attribute("Tag")?.Remove();
        }
        Assert.IsTrue(XNode.DeepEquals(named, unnamed));
    }

    [TestMethod]
    public void SecondIconPairDiffersOnlyByAutomationId()
    {
        var (_, page) = Read();
        var identified = Button(page, "C");
        var unidentified = Button(page, "D");
        Assert.AreEqual("CaseC", (string?)identified.Attribute("AutomationProperties.AutomationId"));
        Assert.IsNull(unidentified.Attribute("AutomationProperties.AutomationId"));
        Assert.IsNull(unidentified.Attribute(Xaml + "Name"));
        Assert.AreEqual("Refresh preview", (string?)unidentified.Attribute("AutomationProperties.Name"));
        foreach (var button in new[] { identified, unidentified })
        {
            button.Attribute("AutomationProperties.AutomationId")?.Remove();
            button.Attribute("Tag")?.Remove();
        }
        Assert.IsTrue(XNode.DeepEquals(identified, unidentified));
    }

    [TestMethod]
    public void TextPairIncludesANonEmptyButIncorrectName()
    {
        var (_, page) = Read();
        var inherited = Button(page, "E");
        var overridden = Button(page, "F");
        Assert.AreEqual("Refresh preview", (string?)inherited.Attribute("Content"));
        Assert.IsNull(inherited.Attribute("AutomationProperties.Name"));
        Assert.AreEqual((string?)inherited.Attribute("Content"), (string?)overridden.Attribute("Content"));
        Assert.AreEqual("Delete layout", (string?)overridden.Attribute("AutomationProperties.Name"));
        Assert.AreEqual("Refresh_Click", (string?)overridden.Attribute("Click"));
    }

    private static XElement Button(XElement page, string tag) =>
        page.Descendants(Presentation + "Button").Single(button => (string?)button.Attribute("Tag") == tag);

    private static (string Directory, XElement Page) Read()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
            !File.Exists(Path.Combine(directory.FullName, "StateDemo", "NameChecksPage.xaml")))
        {
            directory = directory.Parent;
        }
        Assert.IsNotNull(directory, "The shipped StateDemo sample is required.");
        return (directory.FullName, XElement.Load(Path.Combine(directory.FullName, "StateDemo", "NameChecksPage.xaml")));
    }
}

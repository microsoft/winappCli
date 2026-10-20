using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class StateDemoContractTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [TestMethod]
    public void EveryPresetTargetStillHasOneLiteralSourceIdentity()
    {
        var (profile, main, dialog) = Read();
        var controls = main.Descendants().Concat(dialog.DescendantsAndSelf())
            .Where(element => element.Attribute("AutomationProperties.AutomationId") is not null)
            .ToLookup(element => (string)element.Attribute("AutomationProperties.AutomationId")!, StringComparer.Ordinal);
        var ids = profile.Pages.SelectMany(page => new[] { page.NavigationId, page.ReadyId })
            .Concat(profile.Steps.SelectMany(step => step.RequiredIds.Concat(step.Expectations.Select(check => check.TargetId))
                .Concat(step.TargetId is null ? [] : new[] { step.TargetId })))
            .Distinct(StringComparer.Ordinal);
        foreach (var id in ids)
            Assert.AreEqual(1, controls[id].Count(), id);
    }

    [TestMethod]
    public void VisualPolishKeepsTheTwoRealStaticNameDefectsRepairable()
    {
        var (profile, main, _) = Read();
        Assert.AreEqual(2, profile.RepairTargets.Count);
        foreach (var target in profile.RepairTargets)
        {
            var button = Control(main, target.AutomationId);
            Assert.AreEqual(Presentation + "Button", button.Name);
            Assert.IsNull(button.Attribute("AutomationProperties.Name"), target.AutomationId);
            Assert.IsNull(button.Attribute("AutomationProperties.LabeledBy"), target.AutomationId);
            Assert.IsNull(button.Attribute("ToolTipService.ToolTip"), target.AutomationId);
            Assert.IsNull(button.Attribute("Content"), target.AutomationId);
            Assert.IsFalse(button.Descendants(Presentation + "TextBlock").Any(), "The visible caption must remain outside the intentionally unnamed button.");
            Assert.IsTrue(button.Descendants(Presentation + "Path")
                .All(path => (string?)path.Attribute("AutomationProperties.AccessibilityView") == "Raw"));
            Assert.IsTrue(main.Descendants(Presentation + "TextBlock")
                .Any(text => (string?)text.Attribute("Text") == target.ExpectedName));
            Assert.AreEqual(1, SourceRepair.Locate(profile.Project, target.AutomationId).Count);
        }
    }

    [TestMethod]
    public void RealTabsAndDeclaredKeyboardStopsRemainIntact()
    {
        var (profile, main, dialog) = Read();
        var tabs = Control(main, "MainNavigation");
        Assert.AreEqual(Presentation + "TabView", tabs.Name);
        Assert.AreEqual("0", (string?)tabs.Attribute("SelectedIndex"));
        Assert.AreEqual("False", (string?)tabs.Attribute("IsAddTabButtonVisible"));
        Assert.AreEqual("False", (string?)tabs.Attribute("CanDragTabs"));
        Assert.AreEqual("False", (string?)tabs.Attribute("CanReorderTabs"));
        foreach (var page in profile.Pages)
        {
            var tab = Control(main, page.NavigationId);
            Assert.AreEqual(Presentation + "TabViewItem", tab.Name);
            Assert.IsTrue(tab.Ancestors().Contains(tabs));
            Assert.AreEqual("False", (string?)tab.Attribute("IsClosable"));
            Assert.IsTrue(Control(main, page.ReadyId).Ancestors().Contains(tab));
        }
        foreach (var (id, index) in new[]
        {
            ("PreviewGuidesToggle", "0"), ("DensityCombo", "1"), ("BasicIconButton", "2"),
            ("ContentScroller", "0"), ("ScrollBottomButton", "1"),
            ("OpenInfoDialog", "0"), ("OpenScopeProbe", "1")
        })
            Assert.AreEqual(index, (string?)Control(main, id).Attribute("TabIndex"), id);
        Assert.AreEqual("0", (string?)Control(dialog, "DialogIconButton").Attribute("TabIndex"));
        Assert.AreEqual("1", (string?)Control(dialog, "CloseInfoDialog").Attribute("TabIndex"));
        Assert.AreEqual("False", (string?)Control(main, "PreviewGuidesToggle").Attribute("IsChecked"));
        Assert.AreEqual("0", (string?)Control(main, "DensityCombo").Attribute("SelectedIndex"));
    }

    [TestMethod]
    public void TheLongPageRetainsOneKeyboardAccessibleBodyAndExternalReadyHeading()
    {
        var (_, main, _) = Read();
        var body = Control(main, "ContentScroller");
        Assert.AreEqual(Presentation + "ScrollViewer", body.Name);
        Assert.AreEqual("True", (string?)body.Attribute("IsTabStop"));
        Assert.AreEqual("Disabled", (string?)body.Attribute("HorizontalScrollMode"));
        Assert.AreEqual("True", (string?)body.Attribute("ScrollViewer.BringIntoViewOnFocusChange"));
        Assert.IsFalse(body.Descendants(Presentation + "ScrollViewer").Any());
        foreach (var id in new[] { "ScrollTopAnchor", "ScrollBottomAnchor", "ScrollBottomButton" })
            Assert.IsTrue(Control(main, id).Ancestors().Contains(body), id);
        Assert.IsFalse(Control(main, "ScrollingReady").Ancestors().Contains(body));
        Assert.IsTrue(body.Descendants().Any(element => (string?)element.Attribute(Xaml + "Name") == "ScrollContent"));
        Assert.IsTrue(main.Descendants(Presentation + "StackPanel").Any(element => (string?)element.Attribute(Xaml + "Name") == "PreviewRows"));
        Assert.IsTrue(main.Descendants(Presentation + "Rectangle").Any(element => (string?)element.Attribute(Xaml + "Name") == "PreviewGuidesOverlay"));
    }

    private static XElement Control(XElement root, string id) =>
        root.DescendantsAndSelf().Single(element => (string?)element.Attribute("AutomationProperties.AutomationId") == id);

    private static (LoopProfile Profile, XElement Main, XElement Dialog) Read()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "loop-demo.json")))
            directory = directory.Parent;
        Assert.IsNotNull(directory, "The shipped StateDemo sample is required.");
        var input = LoopInput.Read(Path.Combine(directory.FullName, "loop-demo.json"));
        var root = Path.GetDirectoryName(input.Profile.Project)!;
        return (input.Profile, XElement.Load(Path.Combine(root, "MainWindow.xaml")),
            XElement.Load(Path.Combine(root, "InfoDialog.xaml")));
    }
}

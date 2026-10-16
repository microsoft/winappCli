// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinApp.DevTools.Managed.Tests;

[TestClass]
public sealed class ReactorElementPropsTests
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    private sealed class WrapAliasAttribute(string name, string controlProperty) : Attribute
    {
        public string Name { get; } = name;
        public string ControlProperty { get; } = controlProperty;
    }

    private abstract record Element
    {
        public string? Key { get; init; }
        public Modifiers? Modifiers { get; init; }
    }

    private sealed record Modifiers
    {
        public double? Width { get; init; }
        public bool? IsVisible { get; init; }
        public string? AutomationName { get; init; }
        public Action? OnTapped { get; init; }
    }

    [WrapAlias("Content", "Text")]
    private sealed record TextBlockElement(string Content) : Element
    {
        public double? FontSize { get; init; }
        public int MaxLines { get; init; }
        public bool IsTextSelectionEnabled { get; init; } = true;
    }

    private sealed record ButtonElement(string Label, Action? OnClick = null) : Element
    {
        public bool IsEnabled { get; init; } = true;
        public Element? ContentElement { get; init; }
    }

    private sealed record StackElement(IReadOnlyList<Element> Children) : Element
    {
        public double Spacing { get; init; }
    }

    private sealed record PasswordBoxElement(string Password, string? Header = null) : Element;

    private sealed record ComponentElement(Type ComponentType, object? Props = null) : Element;

    private sealed record LoginProps(string User, string UserPassword, Action Submit);

    private readonly record struct SourceLocation(string FilePath, int LineNumber, int ColumnNumber);

    private readonly record struct OldSourceLocation(string FilePath, int LineNumber);

    [TestMethod]
    public void RecordProps_ReportsFactoryArgumentsUnderTheirWinUINames()
    {
        var props = ReactorElementProps.RecordProps(new TextBlockElement("hi") { FontSize = 20 }, typeof(Element));

        CollectionAssert.AreEquivalent(new[] { "Content→Text=hi", "FontSize→FontSize=20" },
            props.Select(p => $"{p.Name}→{p.Property}={p.Value}").ToArray());
    }

    [TestMethod]
    public void RecordProps_IgnoresInitializerDefaultsAndHandlers()
    {
        var unchanged = ReactorElementProps.RecordProps(new ButtonElement("+1", () => { }), typeof(Element));
        CollectionAssert.AreEqual(new[] { "Label→Content" }, unchanged.Select(p => $"{p.Name}→{p.Property}").ToArray());

        var disabled = ReactorElementProps.RecordProps(new ButtonElement("+1") { IsEnabled = false }, typeof(Element));
        Assert.AreEqual("False", disabled.Single(p => p.Name == "IsEnabled").Value);
    }

    [TestMethod]
    public void RecordProps_SkipsChildElements()
    {
        var props = ReactorElementProps.RecordProps(
            new StackElement([new TextBlockElement("a")]) { Spacing = 8 }, typeof(Element));

        CollectionAssert.AreEqual(new[] { "Spacing" }, props.Select(p => p.Name).ToArray());
    }

    [TestMethod]
    public void RecordProps_RedactsPasswords()
    {
        var props = ReactorElementProps.RecordProps(new PasswordBoxElement("hunter2", "Sign in"), typeof(Element));

        var password = props.Single(p => p.Name == "Password");
        Assert.IsTrue(password.Redacted);
        Assert.AreEqual(ReactorElementProps.Redacted, password.Value);
        Assert.IsFalse(props.Any(p => p.Value.Contains("hunter2")));
    }

    [TestMethod]
    public void RecordProps_DoNotReportWhatAComponentElementMounts()
    {
        Assert.AreEqual(0, ReactorElementProps.RecordProps(new ComponentElement(typeof(LoginProps), 3), typeof(Element)).Count);
    }

    [TestMethod]
    public void Modifiers_NameTheWinUIPropertiesByConvention()
    {
        var modifiers = ReactorElementProps.Modifiers(new TextBlockElement("x")
        {
            Modifiers = new Modifiers { Width = 200, IsVisible = false, AutomationName = "Title", OnTapped = () => { } },
        });

        CollectionAssert.AreEquivalent(new[] { "Width", "Visibility", "AutomationProperties.Name" },
            modifiers.Select(m => m.Property).ToArray());
    }

    [TestMethod]
    public void PropertyName_DropsBaseClassOwnersButKeepsAttachedOwners()
    {
        Assert.AreEqual("Width", ReactorElementProps.PropertyName("FrameworkElement.Width"));
        Assert.AreEqual("Visibility", ReactorElementProps.PropertyName("UIElement.Visibility"));
        Assert.AreEqual("AutomationProperties.HelpText", ReactorElementProps.PropertyName("AutomationProperties.HelpText"));
    }

    [TestMethod]
    public void ComponentProps_ListsValuesAndRedactsPasswords()
    {
        var rows = ReactorElementProps.ComponentProps(new LoginProps("ada", "s3cret", () => { }));

        Assert.AreEqual("ada", rows.Single(r => r.Name == "User").Value);
        Assert.AreEqual(ReactorElementProps.Redacted, rows.Single(r => r.Name == "UserPassword").Value);
        StringAssert.StartsWith(rows.Single(r => r.Name == "Submit").Value, "handler ");
        CollectionAssert.AreEqual(new[] { "Props=3" },
            ReactorElementProps.ComponentProps(3).Select(r => $"{r.Name}={r.Value}").ToArray());
    }

    [TestMethod]
    public void Location_ReadsTheColumnWhenReactorRecordsOne()
    {
        Assert.AreEqual(("App.cs", 35, 13), ReactorApi.Location(new SourceLocation("App.cs", 35, 13)));
        Assert.AreEqual(("App.cs", 35, 0), ReactorApi.Location(new OldSourceLocation("App.cs", 35)));
        Assert.IsNull(ReactorApi.Location(new OldSourceLocation("", 1)));
    }
}

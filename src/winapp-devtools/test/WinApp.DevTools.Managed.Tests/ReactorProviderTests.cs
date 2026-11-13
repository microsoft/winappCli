// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinApp.DevTools.Managed.Tests;

[TestClass]
public sealed class ReactorProviderTests
{
    private sealed record ButtonElement;
    private sealed record ComponentElement<T>;
    private sealed record Element;

    private sealed class App
    {
        public Delegate Lambda() => (Func<int>)(() => 1);
        public static int Header() => 0;
    }

    // ReactorHostInfo as Reactor reports it now: the root by name only.
    private sealed record NamedHost(string Kind, string? RootComponentName, string? RootRenderFunctionName);

    // Reactor's built-in flyout decorators, as records shaped like Reactor's own.
    private sealed record KeyedElement(string? Key);
    private sealed record MenuFlyoutElement(object Target, string? Key = null);
    private sealed record FlyoutElement(object Target, string? Key = null);
    private sealed record TeachingTipElement(object Target, string? Key = null);

    private static string? KeyOf(object tag) => tag switch
    {
        KeyedElement k => k.Key,
        MenuFlyoutElement m => m.Key,
        FlyoutElement f => f.Key,
        TeachingTipElement t => t.Key,
        _ => null,
    };

    [TestMethod]
    public void Key_OfAFlyoutDecorator_IsItsTargetsKey()
    {
        var menu = new MenuFlyoutElement(new KeyedElement("menuTarget"));

        Assert.AreEqual("menuTarget", ReactorApi.KeyThroughDecorators(menu, KeyOf, ReactorApi.DecoratorTarget),
            "MenuFlyout(Button(...).WithKey(\"menuTarget\")) reports the button's key");
        Assert.AreEqual("outer", ReactorApi.KeyThroughDecorators(new FlyoutElement(new KeyedElement("inner"), "outer"), KeyOf, ReactorApi.DecoratorTarget),
            "a key on the decorator itself wins");
        Assert.AreEqual("deep", ReactorApi.KeyThroughDecorators(new FlyoutElement(new MenuFlyoutElement(new KeyedElement("deep"))), KeyOf, ReactorApi.DecoratorTarget),
            "nested decorators resolve to the innermost target");
        Assert.IsNull(ReactorApi.KeyThroughDecorators(new TeachingTipElement(new KeyedElement("anchor")), KeyOf, ReactorApi.DecoratorTarget),
            "an element that only points at another one does not take its key");
        Assert.IsNull(ReactorApi.DecoratorTarget(new KeyedElement("k")), "a plain element is not a decorator");
    }

    [TestMethod]
    public void HostRoot_ReadsTheRootsComponentOrRenderFunctionName()
    {
        Assert.AreEqual(("class", (string?)null, "Counter"), ReactorApi.HostRoot(new NamedHost("Window", "Counter", null)));
        Assert.AreEqual(("function", (string?)null, "Program.Main"), ReactorApi.HostRoot(new NamedHost("Window", null, "Program.Main")));
        Assert.AreEqual(("function", (string?)null, "Program.Main"), ReactorApi.HostRoot(new NamedHost("Window", "", "Program.Main")),
            "an empty component name is no name");
        Assert.IsNull(ReactorApi.HostRoot(new NamedHost("Window", null, null)), "a host with no root yet");
    }

    [TestMethod]
    public void ElementKind_DropsTheElementSuffixAndGenericArity()
    {
        Assert.AreEqual("Button", ReactorApi.ElementKind(typeof(ButtonElement)));
        Assert.AreEqual("Component", ReactorApi.ElementKind(typeof(ComponentElement<int>)));
        Assert.AreEqual("Element", ReactorApi.ElementKind(typeof(Element)));
    }

    [TestMethod]
    public void DelegateName_NamesALambdaAfterItsComponentAndMethod()
    {
        Assert.AreEqual("Memo in App.Lambda", ReactorApi.DelegateName(new App().Lambda(), "Memo"));
        Assert.AreEqual("App.Header", ReactorApi.DelegateName((Func<int>)App.Header, "Memo"));
        Assert.AreEqual("RenderEachTime", ReactorApi.DelegateName(null, "RenderEachTime"));
    }

    [TestMethod]
    public void TryLoad_IsAbsentWithoutReactor()
    {
        Assert.IsNull(ReactorApi.TryLoad([typeof(object).Assembly, typeof(ReactorProviderTests).Assembly]));
    }

    [TestMethod]
    public void Probe_AnswersAbsentWithoutTouchingTheUiThread()
    {
        Assert.AreEqual("{\"state\":\"absent\"}", ReactorProvider.Probe());
    }

    [TestMethod]
    public void DelegateName_NamesARootRenderFunction()
    {
        Assert.AreEqual("render in App.Lambda", ReactorApi.DelegateName(new App().Lambda(), "render"));
    }

    [TestMethod]
    public void TypeName_DropsGenericArity()
    {
        Assert.AreEqual("ComponentElement", ReactorApi.TypeName(typeof(ComponentElement<int>)));
    }

    [TestMethod]
    public void IsDefaultAutomationName_IsReactorsOwnModifierOnly()
    {
        Assert.IsTrue(ReactorApi.IsDefaultAutomationName(
            [new ReactorAppliedProperty("DefaultAutomationName", "AutomationProperties.Name", "New task")]));
        Assert.IsFalse(ReactorApi.IsDefaultAutomationName(
            [new ReactorAppliedProperty("AutomationName", "AutomationProperties.Name", "Add a task")]),
            "a name the app set with .AutomationName(...) is its own");
        Assert.IsFalse(ReactorApi.IsDefaultAutomationName([]));
        Assert.IsFalse(ReactorApi.IsDefaultAutomationName(null), "a Reactor without GetAppliedProperties says nothing");
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public class DevToolsSelectorUiAutomationTests
{
    [TestMethod]
    public void IsDevToolsSelector_RecognisesOnlyReactorForms()
    {
        Assert.IsTrue(DevToolsSelectorUiAutomation.IsDevToolsSelector("<Counter>/Button"));
        Assert.IsTrue(DevToolsSelectorUiAutomation.IsDevToolsSelector("key=save"));
        Assert.IsTrue(DevToolsSelectorUiAutomation.IsDevToolsSelector("App.cs:35"));
        Assert.IsFalse(DevToolsSelectorUiAutomation.IsDevToolsSelector("Save"));
        Assert.IsFalse(DevToolsSelectorUiAutomation.IsDevToolsSelector("btn-save-c4b9"));
        Assert.IsFalse(DevToolsSelectorUiAutomation.IsDevToolsSelector(null));
    }

    [TestMethod]
    public void Closest_PicksTheElementWhoseBoundsMatch()
    {
        UiElement At(string slug, double x, double y, double w, double h) => new() { Selector = slug, X = x, Y = y, Width = w, Height = h };
        var candidates = new[] { At("btn-a", 10, 10, 40, 32), At("btn-b", 60, 10, 40, 32), At("btn-c", 61, 11, 41, 32) };

        Assert.AreEqual("btn-c", DevToolsSelectorUiAutomation.Closest(candidates, (61, 11, 40.5, 32))?.Selector);
        Assert.AreEqual("btn-a", DevToolsSelectorUiAutomation.Closest(candidates, (11, 9, 40, 32))?.Selector);
        Assert.IsNull(DevToolsSelectorUiAutomation.Closest(candidates, (200, 200, 40, 32)), "nothing within tolerance is no match, not the nearest");
    }

    [TestMethod]
    public async Task WithoutDevTools_TheSelectorKeepsItsUiMeaning()
    {
        UiSelector? seen = null;
        var inner = Proxy((method, args) =>
        {
            if (method.Name == nameof(IUiAutomation.FindSingleElementAsync))
            {
                seen = (UiSelector)args![1]!;
                return Task.FromResult<UiElement?>(null);
            }
            throw new NotSupportedException(method.Name);
        });
        var bridge = new DevToolsSelectorUiAutomation(inner);
        // A process with no DevTools pipe: "App.cs:35" is still a text search.
        var target = new UiTarget { ProcessId = int.MaxValue - 7, WindowHandle = 0 };

        await bridge.FindSingleElementAsync(target, new UiSelector { Query = "App.cs:35" }, CancellationToken.None);

        Assert.AreEqual("App.cs:35", seen?.Query);
        Assert.IsNull(seen?.Slug);
    }

    private static IUiAutomation Proxy(Func<MethodInfo, object?[]?, object?> handler)
    {
        var proxy = DispatchProxy.Create<IUiAutomation, Dispatch>();
        ((Dispatch)(object)proxy).Handler = handler;
        return proxy;
    }

    private class Dispatch : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = (_, _) => null;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }
}

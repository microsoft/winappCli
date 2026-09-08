// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

/// <summary>
/// <see cref="UiTarget.FromWindowHandle"/> is the entry point for callers that already have a
/// window handle — notably MSTest's <c>WindowTest</c>, which exposes the launched app's main window
/// as a UIA2 <c>AutomationElement</c> whose <c>NativeWindowHandle</c> feeds straight in.
/// </summary>
[TestClass]
public class UiTargetTests
{
    [TestMethod]
    public void FromWindowHandle_ResolvesProcessAndTitle()
    {
        using var fx = new UiaTestFixture();

        var target = UiTarget.FromWindowHandle(fx.Hwnd);

        Assert.AreEqual(fx.Hwnd, (nint)target.WindowHandle);
        Assert.AreEqual(fx.ProcessId, target.ProcessId, "the owning process must be resolved from the handle");
        Assert.AreEqual(fx.Title, target.WindowTitle);
        Assert.IsTrue(target.IsExplicitWindow,
            "a caller-supplied handle is an explicit target, so lookups must not expand to sibling windows");
    }

    [TestMethod]
    public async Task FromWindowHandle_TargetIsUsableForElementLookup()
    {
        using var fx = new UiaTestFixture();
        var svc = new UiAutomationService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UiAutomationService>.Instance,
            new UiSelectorParser());

        // The whole point of the bridge: a bare HWND is enough to drive the element tree.
        var target = UiTarget.FromWindowHandle(fx.Hwnd);

        var deadline = Environment.TickCount64 + 10_000;
        UiElement? element = null;
        while (Environment.TickCount64 < deadline && element is null)
        {
            element = await svc.FindSingleElementAsync(
                target, new UiSelector { Query = "btnInvoke" }, CancellationToken.None);
            if (element is null) { await Task.Delay(50); }
        }

        Assert.IsNotNull(element, "an element must be resolvable through a target built from a window handle");
    }

    [TestMethod]
    public void FromWindowHandle_ZeroHandle_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => UiTarget.FromWindowHandle(0));
    }
}

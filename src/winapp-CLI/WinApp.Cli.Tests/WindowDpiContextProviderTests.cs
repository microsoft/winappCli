// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class WindowDpiContextProviderTests
{
    [TestMethod]
    [DataRow(0, "unaware")]
    [DataRow(1, "system-aware")]
    [DataRow(2, "per-monitor-aware")]
    public void GetForWindow_MapsAwarenessAndCalculatesScale(int awareness, string expected)
    {
        var provider = new WindowDpiContextProvider(_ => 144, _ => awareness);

        var result = provider.GetForWindow(123);

        Assert.AreEqual((uint)144, result.WindowDpi);
        Assert.AreEqual(1.5, result.Scale);
        Assert.AreEqual(expected, result.DpiAwareness);
        Assert.AreEqual("physical-screen-pixels", result.CoordinateSpace);
    }
    [TestMethod]
    public void GetForWindow_ZeroHwnd_ThrowsExplicitError()
    {
        var provider = new WindowDpiContextProvider(_ => 96, _ => 0);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => provider.GetForWindow(0));

        StringAssert.Contains(exception.Message, "HWND is zero");
    }

    [TestMethod]
    public void GetForWindow_ZeroDpi_ThrowsExplicitError()
    {
        var provider = new WindowDpiContextProvider(_ => 0, _ => 0);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => provider.GetForWindow(123));

        StringAssert.Contains(exception.Message, "GetDpiForWindow failed");
        StringAssert.Contains(exception.Message, "123");
    }

    [TestMethod]
    public void GetForWindow_InvalidAwareness_ThrowsExplicitError()
    {
        var provider = new WindowDpiContextProvider(_ => 96, _ => -1);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => provider.GetForWindow(123));

        StringAssert.Contains(exception.Message, "invalid value (-1)");
        StringAssert.Contains(exception.Message, "123");
    }
}

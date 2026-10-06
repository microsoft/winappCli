// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

public partial class RealUiAutomationTests
{
    // -----------------------------------------------------------------------------
    // Windowed popups (issue #646): content drawn in an owned window whose UIA ancestors lead back
    // to an island in the main window, as for a XAML flyout backed by Xaml_WindowedPopupClass.
    // -----------------------------------------------------------------------------

    private static readonly System.Drawing.Color PopupColor = System.Drawing.Color.FromArgb(255, 0, 200);

    [TestMethod]
    public async Task ResolveElementTopLevelWindow_WindowedPopupContent_ReturnsThePopupWindow()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var popup = fx.OpenWindowedPopup(PopupColor);
        var item = await ResolveAsync(svc, uiTarget, UiaTestFixture.WindowedPopupItemId);

        Assert.AreEqual(
            (long)fx.Hwnd,
            item.WindowHandle,
            "Precondition: the item's UIA ancestors must lead to the main window, as they do for XAML popups.");
        Assert.AreEqual(popup, svc.ResolveElementTopLevelWindow(uiTarget, item));
    }

    [TestMethod]
    public async Task ResolveElementTopLevelWindow_MainWindowContentWhilePopupIsOpen_StaysOnTheMainWindow()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        fx.OpenWindowedPopup(PopupColor);
        var islandItem = await ResolveAsync(svc, uiTarget, UiaTestFixture.IslandItemId);

        Assert.AreEqual(fx.Hwnd, svc.ResolveElementTopLevelWindow(uiTarget, islandItem));
    }

    [TestMethod]
    public async Task ResolveElementTopLevelWindow_TwoWindowsHostTheContent_StaysOnTheMainWindow()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        fx.OpenWindowedPopup(PopupColor);
        var item = await ResolveAsync(svc, uiTarget, UiaTestFixture.WindowedPopupItemId);
        fx.OpenWindowedPopup(PopupColor);

        Assert.AreEqual(fx.Hwnd, svc.ResolveElementTopLevelWindow(uiTarget, item));
    }

    [TestMethod]
    public async Task ResolveElementTopLevelWindow_HostWindowInAnotherProcess_StaysOnTheMainWindow()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        var popup = fx.OpenWindowedPopup(PopupColor);
        var item = await ResolveAsync(svc, uiTarget, UiaTestFixture.WindowedPopupItemId);
        RealOwnedWindowFinder.s_getWindowProcessId = hwnd =>
            (nint)hwnd == popup ? fx.ProcessId + 1 : RealOwnedWindowFinder.NativeGetWindowProcessId(hwnd);
        try
        {
            Assert.AreEqual(fx.Hwnd, svc.ResolveElementTopLevelWindow(uiTarget, item));
        }
        finally
        {
            RealOwnedWindowFinder.ResetNativeSeams();
        }
    }

    [TestMethod]
    public async Task ScreenshotAsync_WindowedPopupElement_CapturesThePopupWindow()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var uiTarget = SessionFor(fx);
        Foreground(fx);
        var popup = fx.OpenWindowedPopup(PopupColor);
        var item = await ResolveAsync(svc, uiTarget, UiaTestFixture.WindowedPopupItemId);
        Assert.AreEqual(
            popup,
            svc.ResolveElementTopLevelWindow(uiTarget, item),
            "The popup item must resolve to the popup window; otherwise the capture targets the window behind it.");
        await WaitForPopupOnScreenAsync(popup);

        var (pixels, width, height) = await svc.ScreenshotAsync(
            uiTarget, item.Selector ?? UiaTestFixture.WindowedPopupItemId, captureScreen: false, focus: false, CancellationToken.None);

        if (pixels.All(b => b == 0))
        {
            Assert.Inconclusive("Window capture produced a black frame on this host.");
        }

        Assert.AreEqual(width * height * 4, pixels.Length);
        var center = ((height / 2 * width) + (width / 2)) * 4;
        var (blue, green, red) = (pixels[center], pixels[center + 1], pixels[center + 2]);
        Assert.IsTrue(
            IsPopupColor(System.Drawing.Color.FromArgb(red, green, blue)),
            $"Expected the popup window's color, got R={red} G={green} B={blue}: the popup was selected and on screen, but the window capture returned other pixels.");
    }

    private static bool IsPopupColor(System.Drawing.Color color)
        => Math.Abs(color.R - PopupColor.R) <= 8 && Math.Abs(color.G - PopupColor.G) <= 8 && Math.Abs(color.B - PopupColor.B) <= 8;

    /// <summary>
    /// Waits until the desktop shows the popup's color, so the popup has been composited before it is
    /// captured.
    /// </summary>
    /// <remarks>
    /// Window capture reads the compositor's copy of the window, which a just-shown window may not
    /// have yet. On the software-rendered CI desktop a capture started that early can return opaque
    /// black instead of the popup's pixels.
    /// </remarks>
    private static async Task WaitForPopupOnScreenAsync(nint popup)
    {
        var deadline = Environment.TickCount64 + ReadyTimeoutMs;
        System.Drawing.Color? shown = null;
        while (Environment.TickCount64 < deadline)
        {
            shown = DesktopTestHelpers.ScreenColorAtWindowCenter(popup);
            if (shown is { } color && IsPopupColor(color))
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Inconclusive(
            $"The desktop never showed the popup window (last read: {shown?.ToString() ?? "unreadable"}), so this host cannot validate the capture.");
    }
}

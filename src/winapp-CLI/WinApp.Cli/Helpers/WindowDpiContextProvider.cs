// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Windows.Win32.Foundation;

namespace WinApp.Cli.Helpers;

internal sealed class WindowDpiContextProvider : IWindowDpiContextProvider
{
    internal const string PhysicalScreenPixels = "physical-screen-pixels";

    private readonly Func<long, uint> _getDpiForWindow;
    private readonly Func<long, int> _getAwarenessForWindow;

    public WindowDpiContextProvider()
        : this(GetDpiForWindow, GetAwarenessForWindow)
    {
    }

    internal WindowDpiContextProvider(
        Func<long, uint> getDpiForWindow,
        Func<long, int> getAwarenessForWindow)
    {
        _getDpiForWindow = getDpiForWindow;
        _getAwarenessForWindow = getAwarenessForWindow;
    }

    public WindowDpiContext GetForWindow(long hwnd)
    {
        if (hwnd == 0)
        {
            throw new InvalidOperationException("Cannot read the target window DPI context because its HWND is zero.");
        }

        var windowDpi = _getDpiForWindow(hwnd);
        if (windowDpi == 0)
        {
            throw new InvalidOperationException($"GetDpiForWindow failed for HWND {hwnd}; the handle may no longer be valid.");
        }

        var dpiAwareness = _getAwarenessForWindow(hwnd) switch
        {
            0 => "unaware",
            1 => "system-aware",
            2 => "per-monitor-aware",
            var value => throw new InvalidOperationException(
                $"GetAwarenessFromDpiAwarenessContext returned an invalid value ({value}) for HWND {hwnd}."),
        };

        return new WindowDpiContext(
            windowDpi,
            windowDpi / 96d,
            dpiAwareness,
            PhysicalScreenPixels);
    }

    private static uint GetDpiForWindow(long hwnd)
        => Windows.Win32.PInvoke.GetDpiForWindow(new HWND((nint)hwnd));

    private static int GetAwarenessForWindow(long hwnd)
    {
        var context = Windows.Win32.PInvoke.GetWindowDpiAwarenessContext(new HWND((nint)hwnd));
        return (int)Windows.Win32.PInvoke.GetAwarenessFromDpiAwarenessContext(context);
    }
}


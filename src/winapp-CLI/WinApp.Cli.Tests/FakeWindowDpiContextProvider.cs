// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

internal sealed class FakeWindowDpiContextProvider : IWindowDpiContextProvider
{
    public WindowDpiContext Result { get; set; } =
        new(144, 1.5, "per-monitor-aware", WindowDpiContextProvider.PhysicalScreenPixels);

    public Dictionary<long, WindowDpiContext> ResultsByHwnd { get; } = [];
    public Dictionary<long, Exception> ThrowsByHwnd { get; } = [];

    public Exception? Throw { get; set; }

    public List<long> RequestedHwnds { get; } = [];

    public WindowDpiContext GetForWindow(long hwnd)
    {
        RequestedHwnds.Add(hwnd);
        if (ThrowsByHwnd.TryGetValue(hwnd, out var hwndException))
        {
            throw hwndException;
        }
        if (Throw is not null)
        {
            throw Throw;
        }

        return ResultsByHwnd.TryGetValue(hwnd, out var result) ? result : Result;
    }
}

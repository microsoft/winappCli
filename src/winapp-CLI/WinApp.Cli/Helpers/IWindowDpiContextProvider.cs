// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers;

internal interface IWindowDpiContextProvider
{
    WindowDpiContext GetForWindow(long hwnd);
}

internal sealed record WindowDpiContext(
    uint WindowDpi,
    double Scale,
    string DpiAwareness,
    string CoordinateSpace);

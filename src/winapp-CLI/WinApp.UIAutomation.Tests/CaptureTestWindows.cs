// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;
using Windows.Win32.Foundation;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

internal static class CaptureTestWindows
{
    internal static void WithOwnedPopup(Action<HWND, HWND, int> test)
    {
        if (ForegroundGuard.NoInteractiveDesktop())
        {
            Assert.Inconclusive("Native owned-popup discovery requires an interactive desktop.");
        }
        using var fixture = new UiaTestFixture();
        PopupWindow? popup = null;
        try
        {
            nint handle = 0;
            fixture.OnUiThread(() =>
            {
                popup = new PopupWindow();
                popup.Show(fixture.Form);
                handle = popup.Handle;
            });
            var root = new HWND(fixture.Hwnd);
            var child = new HWND(handle);
            RealOwnedWindowFinder.s_findNextTopLevelWindow = after => after.IsNull ? child : HWND.Null;
            test(root, child, RealOwnedWindowFinder.s_getWindowProcessId(root));
        }
        finally
        {
            RealOwnedWindowFinder.ResetNativeSeams();
            fixture.OnUiThread(() => popup?.Dispose());
        }
    }

    internal sealed class PopupWindow : Form
    {
        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.Style = unchecked((int)0x80000000);
                parameters.ExStyle |= 0x08000080;
                return parameters;
            }
        }
    }
}

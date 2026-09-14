// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinApp.Cli.Services.Performance;

internal interface IWindowResponseProbe
{
    TimeSpan Timeout { get; }

    bool IsResponsive(long windowHandle);
}

internal sealed class WindowResponseProbe : IWindowResponseProbe
{
    private const uint WmNull = 0;

    public TimeSpan Timeout { get; } = TimeSpan.FromMilliseconds(100);

    public unsafe bool IsResponsive(long windowHandle)
    {
        nuint result = 0;
        var succeeded = global::Windows.Win32.PInvoke.SendMessageTimeout(
            new HWND((nint)windowHandle),
            WmNull,
            default,
            default,
            SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG
                | SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_BLOCK,
            checked((uint)Timeout.TotalMilliseconds),
            &result);
        return succeeded != 0;
    }
}

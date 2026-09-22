// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using System.Runtime.InteropServices;

namespace WinApp.Cli.Services.Performance;

internal enum WindowResponseProbeOutcome
{
    Responsive,
    Timeout,
    InvalidWindowHandle,
    AccessDenied,
    Win32Failure,
}

internal readonly record struct WindowResponseProbeResult(
    WindowResponseProbeOutcome Outcome,
    int? Win32ErrorCode = null);

internal interface IWindowResponseProbe
{
    TimeSpan Timeout { get; }

    WindowResponseProbeResult Probe(long windowHandle);
}

internal readonly record struct WindowMessageProbeResult(
    bool Succeeded,
    int Win32ErrorCode);

internal interface IWindowResponseNative
{
    WindowMessageProbeResult SendNullMessage(long windowHandle, TimeSpan timeout);
}

internal sealed class WindowResponseNative : IWindowResponseNative
{
    public unsafe WindowMessageProbeResult SendNullMessage(long windowHandle, TimeSpan timeout)
    {
        nuint result = 0;
        Marshal.SetLastPInvokeError(0);
        var succeeded = global::Windows.Win32.PInvoke.SendMessageTimeout(
            new HWND((nint)windowHandle),
            0,
            default,
            default,
            SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG
                | SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_BLOCK,
            checked((uint)timeout.TotalMilliseconds),
            &result);
        return new(succeeded != 0, Marshal.GetLastPInvokeError());
    }
}

internal sealed class WindowResponseProbe(IWindowResponseNative native) : IWindowResponseProbe
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidWindowHandle = 1400;
    private const int ErrorTimeout = 1460;

    public TimeSpan Timeout { get; } = TimeSpan.FromMilliseconds(100);

    public WindowResponseProbeResult Probe(long windowHandle)
    {
        var result = native.SendNullMessage(windowHandle, Timeout);
        if (result.Succeeded)
        {
            return new(WindowResponseProbeOutcome.Responsive);
        }

        return result.Win32ErrorCode switch
        {
            ErrorTimeout => new(WindowResponseProbeOutcome.Timeout, ErrorTimeout),
            ErrorInvalidWindowHandle => new(
                WindowResponseProbeOutcome.InvalidWindowHandle,
                ErrorInvalidWindowHandle),
            ErrorAccessDenied => new(WindowResponseProbeOutcome.AccessDenied, ErrorAccessDenied),
            _ => new(WindowResponseProbeOutcome.Win32Failure, result.Win32ErrorCode),
        };
    }
}

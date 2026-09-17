// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.StationsAndDesktops;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>Measures the calling process's active desktop without moving or activating windows.</summary>
internal static class DesktopCapture
{
    /// <summary>
    /// Returns the virtual desktop's physical pixel bounds, including a possibly negative origin.
    /// Refuses a non-interactive process or a desktop other than the current input desktop.
    /// Call before and after capture; discard pixels if the bounds changed.
    /// </summary>
    public static PointerRect GetBounds()
    {
        var bounds = ReadNativeBounds();
        if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
        {
            throw new InvalidOperationException("The input desktop has no capturable display area.");
        }
        return bounds;
    }

    private static unsafe PointerRect ReadNativeBounds()
    {
        using var process = Process.GetCurrentProcess();
        using var input = PInvoke.OpenInputDesktop_SafeHandle(0, false, DESKTOP_ACCESS_FLAGS.DESKTOP_READOBJECTS);
        if (process.SessionId == 0 || input.IsInvalid)
        {
            throw new InvalidOperationException("The current process has no accessible input desktop.");
        }

        var station = PInvoke.GetProcessWindowStation();
        var current = PInvoke.GetThreadDesktop(PInvoke.GetCurrentThreadId());
        if (!string.Equals(ReadName(new HANDLE(station.Value)), "WinSta0", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(ReadName(new HANDLE(current.Value)),
                ReadName(new HANDLE(input.DangerousGetHandle())), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The process is not attached to the current interactive input desktop.");
        }

        var metrics = MouseInput.ReadVirtualDesktopMetrics();
        return new PointerRect(metrics.X, metrics.Y,
            checked(metrics.X + metrics.Width), checked(metrics.Y + metrics.Height));
    }

    private static unsafe string ReadName(HANDLE handle)
    {
        const int Capacity = 256;
        var buffer = stackalloc char[Capacity];
        uint needed;
        if (!PInvoke.GetUserObjectInformation(handle, USER_OBJECT_INFORMATION_INDEX.UOI_NAME,
                buffer, Capacity * sizeof(char), &needed))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not identify the capture desktop.");
        }
        var count = checked((int)(needed / sizeof(char))) - 1;
        if (count <= 0 || count >= Capacity)
        {
            throw new InvalidOperationException("Windows returned an invalid desktop identity.");
        }
        return new string(buffer, 0, count);
    }
}

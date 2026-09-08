// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

/// <summary>
/// Native desktop helpers shared by the real-input / real-UIA tests: reliably forcing a
/// background-owned window to the foreground (so system-wide focus and OS input injection land on
/// the fixture) and saving/restoring the cursor so tests never leave persistent machine state.
/// </summary>
public static class DesktopTestHelpers
{
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;
    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>
    /// Reliably brings <paramref name="hwnd"/> to the foreground. Minimize+restore forces a genuine
    /// activation (a bare SetForegroundWindow from a background thread is routinely downgraded to a
    /// taskbar flash by the focus-stealing-prevention policy), a benign ALT tap nudges the input
    /// queue, and an AttachThreadInput bridge lets SetForegroundWindow be honoured.
    /// </summary>
    public static void ForceForeground(nint hwnd)
    {
        if (hwnd == 0)
        {
            return;
        }

        var foreground = GetForegroundWindow();
        uint targetThread = GetWindowThreadProcessId(hwnd, out _);
        uint foregroundThread = foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, out _);
        uint thisThread = GetCurrentThreadId();

        bool attachedToForeground = foregroundThread != 0 && foregroundThread != thisThread
            && AttachThreadInput(thisThread, foregroundThread, true);
        bool attachedToTarget = targetThread != 0 && targetThread != thisThread
            && AttachThreadInput(thisThread, targetThread, true);
        try
        {
            ShowWindow(hwnd, SW_MINIMIZE);
            ShowWindow(hwnd, SW_RESTORE);
            BringWindowToTop(hwnd);
            keybd_event(VK_MENU, 0, 0, nint.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, nint.Zero);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attachedToTarget)
            {
                AttachThreadInput(thisThread, targetThread, false);
            }
            if (attachedToForeground)
            {
                AttachThreadInput(thisThread, foregroundThread, false);
            }
        }
    }

    /// <summary>
    /// Current cursor position in screen coordinates. Paired with <see cref="SetCursor"/> so mouse
    /// tests can restore the pointer and leave no persistent machine state.
    /// </summary>
    public static (int X, int Y) GetCursor()
    {
        GetCursorPos(out var pt);
        return (pt.X, pt.Y);
    }

    public static void SetCursor(int x, int y) => SetCursorPos(x, y);

    public static nint DesktopWindow() => GetDesktopWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nint dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();
}

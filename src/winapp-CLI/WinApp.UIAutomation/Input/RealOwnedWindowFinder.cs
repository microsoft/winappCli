// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Production <see cref="IOwnedWindowFinder"/> — enumerates the real desktop for visible top-level
/// windows owned by one of the given app windows. This is a thin, un-runnable-in-CI native (Win32)
/// wrapper; the interface exists so <c>UiScreenshotCommand</c>'s discovery logic is
/// testable without a live desktop. Moved verbatim from the command; behavior is unchanged.
/// </summary>
internal sealed class RealOwnedWindowFinder : IOwnedWindowFinder
{
    /// <remarks>
    /// Native adapter seam for issue #630: the default body enumerates real top-level windows via
    /// <c>FindWindowEx</c>. Tests replace it to cover filtering/ownership logic without touching the
    /// live desktop.
    /// </remarks>
    internal static Func<global::Windows.Win32.Foundation.HWND, global::Windows.Win32.Foundation.HWND> s_findNextTopLevelWindow =
        NativeFindNextTopLevelWindow;

    internal static global::Windows.Win32.Foundation.HWND NativeFindNextTopLevelWindow(global::Windows.Win32.Foundation.HWND after)
        => global::Windows.Win32.PInvoke.FindWindowEx(
            global::Windows.Win32.Foundation.HWND.Null, after, null, (string?)null);

    /// <remarks>
    /// Native adapter seam for issue #630: the default body queries live HWND visibility. Tests inject
    /// deterministic visibility.
    /// </remarks>
    internal static Func<global::Windows.Win32.Foundation.HWND, bool> s_isWindowVisible =
        NativeIsWindowVisible;

    internal static bool NativeIsWindowVisible(global::Windows.Win32.Foundation.HWND hwnd)
        => global::Windows.Win32.PInvoke.IsWindowVisible(hwnd);

    /// <remarks>
    /// Native adapter seam for issue #630: the default body queries live HWND ownership. Tests inject
    /// deterministic owners.
    /// </remarks>
    internal static Func<global::Windows.Win32.Foundation.HWND, global::Windows.Win32.Foundation.HWND> s_getWindowOwner =
        NativeGetWindowOwner;

    internal static global::Windows.Win32.Foundation.HWND NativeGetWindowOwner(global::Windows.Win32.Foundation.HWND hwnd)
        => global::Windows.Win32.PInvoke.GetWindow(
            hwnd, global::Windows.Win32.UI.WindowsAndMessaging.GET_WINDOW_CMD.GW_OWNER);

    /// <remarks>
    /// Native adapter seam for issue #630: the default body reads the owning PID from a live HWND.
    /// Tests inject deterministic PIDs.
    /// </remarks>
    internal static Func<global::Windows.Win32.Foundation.HWND, int> s_getWindowProcessId = NativeGetWindowProcessId;

    internal static int NativeGetWindowProcessId(global::Windows.Win32.Foundation.HWND hwnd)
    {
        unsafe
        {
            uint pid = 0;
            global::Windows.Win32.PInvoke.GetWindowThreadProcessId(hwnd, &pid);
            return (int)pid;
        }
    }

    /// <remarks>
    /// Native adapter seam for issue #630: the default body reads title text from a live HWND. Tests
    /// inject deterministic titles.
    /// </remarks>
    internal static Func<global::Windows.Win32.Foundation.HWND, string> s_getWindowText = NativeGetWindowText;

    internal static string NativeGetWindowText(global::Windows.Win32.Foundation.HWND hwnd)
    {
        unsafe
        {
            var titleChars = new char[512];
            fixed (char* buffer = titleChars)
            {
                var len = global::Windows.Win32.PInvoke.GetWindowText(hwnd, buffer, 512);
                return len > 0 ? new string(buffer, 0, len) : "";
            }
        }
    }

    internal static void ResetNativeSeams()
    {
        s_findNextTopLevelWindow = NativeFindNextTopLevelWindow;
        s_isWindowVisible = NativeIsWindowVisible;
        s_getWindowOwner = NativeGetWindowOwner;
        s_getWindowProcessId = NativeGetWindowProcessId;
        s_getWindowText = NativeGetWindowText;
    }

    public List<(nint Hwnd, int Pid, string Title)> FindOwnedWindows(List<(nint Hwnd, int Pid, string Title)> appWindows)
    {
        var appHwnds = new HashSet<nint>(appWindows.Select(w => w.Hwnd));
        var owned = new List<(nint Hwnd, int Pid, string Title)>();

        // Enumerate all visible windows and check ownership
        var hwnd = global::Windows.Win32.Foundation.HWND.Null;
        while (true)
        {
            hwnd = s_findNextTopLevelWindow(hwnd);
            if (hwnd.IsNull) { break; }
            if (!s_isWindowVisible(hwnd)) { continue; }

            // Skip windows already in the list
            if (appHwnds.Contains((nint)hwnd)) { continue; }

            // Check if this window is owned by one of our app windows
            var owner = s_getWindowOwner(hwnd);
            if (!owner.IsNull && appHwnds.Contains((nint)owner))
            {
                owned.Add(((nint)hwnd, s_getWindowProcessId(hwnd), s_getWindowText(hwnd)));
            }
        }

        return owned;
    }
}

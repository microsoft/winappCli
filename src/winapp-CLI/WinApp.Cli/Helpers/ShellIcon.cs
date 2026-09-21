// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Drawing;
using Windows.Win32;
using Windows.Win32.UI.Controls;
using Windows.Win32.UI.Shell;

namespace WinApp.Cli.Helpers;

public static class ShellIcon
{
    /// <summary>
    /// Gets the "Jumbo" (typically 256x256) shell icon for a file path (exe/dll/anything).
    /// Returns null if no icon can be resolved.
    /// </summary>
    public static Icon? GetJumboIcon(string path) =>
        GetJumboIconCore(path, GetSystemIconIndex, index => GetIconFromJumboImageList(index, AcquireJumboImageList));

    /// <summary>
    /// Pure orchestration seam for <see cref="GetJumboIcon"/>: resolves the system icon index for
    /// <paramref name="path"/>, returns null when the shell reports no icon, otherwise materializes
    /// the jumbo icon. Any exception from the resolvers is swallowed and reported as null. The
    /// resolver delegates are injected so tests can drive the no-icon, success, and failure paths
    /// without invoking the native shell APIs.
    /// </summary>
    internal static Icon? GetJumboIconCore(
        string path,
        Func<string, int?> getSystemIconIndex,
        Func<int, Icon?> getIconFromImageList)
    {
        try
        {
            // 1) Get the system image list index for this file (Explorer uses this)
            int? iconIndex = getSystemIconIndex(path);
            if (iconIndex is null)
            {
                return null;
            }

            // 2+3) Resolve an HICON for that index from the Jumbo image list and clone it
            return getIconFromImageList(iconIndex.Value);
        }
        catch
        {
            // Swallow all exceptions and return null if anything goes wrong
            return null;
        }
    }

    /// <summary>
    /// Returns the system image-list icon index for <paramref name="path"/>, or null when the shell
    /// cannot resolve one (SHGetFileInfo returns 0).
    /// </summary>
    private static int? GetSystemIconIndex(string path)
    {
        SHFILEINFOW sfi = new();
        // SHGFI_SYSICONINDEX gives us sfi.iIcon
        var result = PInvoke.SHGetFileInfo(path, 0, ref sfi, SHGFI_FLAGS.SHGFI_SYSICONINDEX);
        return result == 0 ? null : sfi.iIcon;
    }

    /// <summary>
    /// Materializes a standalone <see cref="Icon"/> for the given system image-list index from the
    /// Jumbo image list, or null when the image list cannot be obtained. The <paramref name="acquire"/>
    /// delegate is a behavior-preserving seam over the native <c>SHGetImageList</c> call so tests can
    /// drive the otherwise-unreachable "image list unavailable" guard without a broken shell.
    /// </summary>
    internal static Icon? GetIconFromJumboImageList(int iconIndex, Func<(bool Failed, object? PpvObj)> acquire)
    {
        // CsWin32 exposes SHGetImageList and IImageList
        var (failed, ppvObj) = acquire();
        try
        {
            var imageList = ppvObj as IImageList2;
            // Defensive guard: the system Jumbo image list is always available on a real desktop, so this
            // failure branch is not reachable from the real acquirer without a broken shell. Kept for safety.
            if (failed || imageList is null)
            {
                return null;
            }

            // Use ILD_IMAGE to preserve full color depth and alpha channel
            DestroyIconSafeHandle? hIcon = null;
            try
            {
                imageList.GetIcon(iconIndex, (uint)IMAGE_LIST_DRAW_STYLE.ILD_IMAGE, out hIcon);

                // Clone the icon to create a copy that owns its own data
                // Icon.FromHandle doesn't own the handle, so we must clone before disposing hIcon
                using var tempIcon = Icon.FromHandle(hIcon.DangerousGetHandle());
                return (Icon)tempIcon.Clone();
            }
            finally
            {
                hIcon?.Dispose();
            }
        }
        finally
        {
            // Release the acquired COM object on every path, including the guard's early return
            // above (otherwise a non-null image list obtained in a failure/mismatch case would leak).
            if (ppvObj is System.Runtime.InteropServices.Marshalling.ComObject comObj)
            {
                comObj.FinalRelease();
            }
        }
    }

    /// <summary>
    /// Default acquirer for <see cref="GetIconFromJumboImageList"/>: obtains the system Jumbo image
    /// list via <c>SHGetImageList</c>, reporting whether the call failed alongside the raw COM object
    /// (so the caller can release it). This is the exact native path used in production.
    /// </summary>
    private static (bool Failed, object? PpvObj) AcquireJumboImageList()
    {
        var hr = PInvoke.SHGetImageList((int)PInvoke.SHIL_JUMBO, out IImageList2 ppvObj);
        return (hr.Failed, ppvObj);
    }
}

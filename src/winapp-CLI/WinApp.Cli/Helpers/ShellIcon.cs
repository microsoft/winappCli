// Copyright (c) Microsoft Corporation. All rights reserved.
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
    public static Icon? GetJumboIcon(string path)
    {
        try
        {
            // 1) Get the system image list index for this file (Explorer uses this)
            SHFILEINFOW sfi = new();
            // SHGFI_SYSICONINDEX gives us sfi.iIcon
            var flags = SHGFI_FLAGS.SHGFI_SYSICONINDEX;
            var result = PInvoke.SHGetFileInfo(path, 0, ref sfi, flags);
            if (result == 0)
            {
                return null;
            }

            // 2) Ask for the Jumbo image list
            // CsWin32 exposes SHGetImageList and IImageList
            var hr = PInvoke.SHGetImageList((int)PInvoke.SHIL_JUMBO, typeof(IImageList2).GUID, out object ppvObj);
            IImageList2 imageList = (IImageList2)ppvObj;
            if (hr.Failed || imageList is null)
            {
                return null;
            }

            // 3) Get an HICON for that index
            // Use ILD_IMAGE to preserve full color depth and alpha channel
            DestroyIconSafeHandle? hIcon = null;
            try
            {
                imageList.GetIcon(sfi.iIcon, (uint)IMAGE_LIST_DRAW_STYLE.ILD_IMAGE, out hIcon);

                // Clone the icon to create a copy that owns its own data
                // Icon.FromHandle doesn't own the handle, so we must clone before disposing hIcon
                using var tempIcon = Icon.FromHandle(hIcon.DangerousGetHandle());
                return (Icon)tempIcon.Clone();
            }
            finally
            {
                if (ppvObj is System.Runtime.InteropServices.Marshalling.ComObject comObj)
                {
                    comObj.FinalRelease();
                }
                hIcon?.Dispose();
            }
        }
        catch
        {
            // Swallow all exceptions and return null if anything goes wrong
            return null;
        }
    }
}

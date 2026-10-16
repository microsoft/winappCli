// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

internal static partial class PrintWindowCaptureHelper
{
    internal static byte[] CaptureValidatedPixels(HWND hwnd, int width, int height,
        Func<HWND, HDC, bool>? print = null)
    {
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("The window has empty capture bounds.");
        }
        return CapturePixels(hwnd, width, height, strict: true, print);
    }

    /// <remarks>
    /// Coverage ceiling (issue #630): this is the innermost GDI/PrintWindow capture boundary. Tests
    /// cover the blank-retry and caller orchestration through seams; the native DC/bitmap handles are
    /// only safe to exercise against a real visible window.
    /// </remarks>
    internal static byte[] CapturePixels(HWND hwnd, int width, int height)
        => CapturePixels(hwnd, width, height, strict: false);

    private static unsafe byte[] CapturePixels(HWND hwnd, int width, int height,
        bool strict, Func<HWND, HDC, bool>? print = null)
    {
        var hdcWindow = global::Windows.Win32.PInvoke.GetDC(hwnd);
        if (strict && hdcWindow.IsNull)
        {
            throw CreateCaptureException("GetDC");
        }
        try
        {
            var hdcMem = global::Windows.Win32.PInvoke.CreateCompatibleDC(hdcWindow);
            if (strict && hdcMem.IsNull)
            {
                throw CreateCaptureException("CreateCompatibleDC");
            }
            try
            {
                var hBitmap = global::Windows.Win32.PInvoke.CreateCompatibleBitmap(hdcWindow, width, height);
                if (strict && hBitmap.IsNull)
                {
                    throw CreateCaptureException("CreateCompatibleBitmap");
                }
                try
                {
                    byte[]? firstPass = null;
                    for (var pass = 0; pass < (strict ? 2 : 1); pass++)
                    {
                        var marker = pass == 0 ? (byte)0x5A : (byte)0xA5;
                        if (strict)
                        {
                            SeedBitmap(hdcWindow, hBitmap, width, height, marker);
                        }
                        var hOld = global::Windows.Win32.PInvoke.SelectObject(hdcMem, *(global::Windows.Win32.Graphics.Gdi.HGDIOBJ*)&hBitmap);
                        if (strict && (hOld.IsNull || (nint)hOld.Value == -1))
                        {
                            throw CreateCaptureException("SelectObject");
                        }
                        try
                        {
                            // PW_RENDERFULLCONTENT = 2
                            var success = print is null
                                ? (bool)global::Windows.Win32.PInvoke.PrintWindow(hwnd, hdcMem, (global::Windows.Win32.Storage.Xps.PRINT_WINDOW_FLAGS)2)
                                : print(hwnd, hdcMem);
                            if (strict && !success)
                            {
                                throw CreateCaptureException("PrintWindow");
                            }
                        }
                        finally
                        {
                            global::Windows.Win32.PInvoke.SelectObject(hdcMem, hOld);
                        }
                        var pixels = ReadBitmapPixels(hdcWindow, hBitmap, width, height);
                        if (!strict)
                        {
                            return pixels;
                        }
                        if (!HasUnpaintedPixels(pixels, marker, firstPass))
                        {
                            for (var i = 3; i < pixels.Length; i += 4)
                            {
                                pixels[i] = 255;
                            }
                            return pixels;
                        }
                        firstPass = pixels;
                    }
                    throw new InvalidOperationException($"PrintWindow did not paint all pixels of window HWND {(nint)hwnd}.");
                }
                finally
                {
                    global::Windows.Win32.PInvoke.DeleteObject(*(global::Windows.Win32.Graphics.Gdi.HGDIOBJ*)&hBitmap);
                }
            }
            finally
            {
                global::Windows.Win32.PInvoke.DeleteDC(hdcMem);
            }
        }
        finally
        {
            global::Windows.Win32.PInvoke.ReleaseDC(hwnd, hdcWindow);
        }
    }

    /// <remarks>
    /// Coverage ceiling (issue #630): this is the innermost GetDIBits extraction from a native HBITMAP.
    /// It is covered indirectly by real screenshot attempts and cannot be executed with managed-only
    /// fakes without fabricating native GDI handles.
    /// </remarks>
    internal static unsafe byte[] ReadBitmapPixels(HDC hdc, HBITMAP hBitmap, int width, int height)
    {
        var bmi = new global::Windows.Win32.Graphics.Gdi.BITMAPINFO
        {
            bmiHeader = new global::Windows.Win32.Graphics.Gdi.BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(global::Windows.Win32.Graphics.Gdi.BITMAPINFOHEADER),
                biWidth = width,
                biHeight = -height, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0 // BI_RGB
            }
        };

        var pixelData = new byte[checked(width * height * 4)];
        fixed (byte* pPixels = pixelData)
        {
            var rows = global::Windows.Win32.PInvoke.GetDIBits(hdc, hBitmap, 0, (uint)height, pPixels, &bmi,
                global::Windows.Win32.Graphics.Gdi.DIB_USAGE.DIB_RGB_COLORS);
            if (rows != height)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetDIBits failed while reading bitmap pixels.");
            }
        }

        return pixelData;
    }

    private static unsafe void SeedBitmap(HDC dc, HBITMAP bitmap, int width, int height, byte marker)
    {
        // Distinct initial colors distinguish unpainted pixels from legitimate black.
        var seed = new byte[checked(width * height * 4)];
        Array.Fill(seed, marker);
        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = width, biHeight = -height,
                biPlanes = 1, biBitCount = 32
            }
        };
        fixed (byte* data = seed)
        {
            if (SetDIBits(dc, bitmap, 0, (uint)height, data, &info, 0) != height)
            {
                throw CreateCaptureException("SetDIBits");
            }
        }
    }

    private static bool HasUnpaintedPixels(byte[] pixels, byte marker, byte[]? firstPass)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] == marker && pixels[i + 1] == marker && pixels[i + 2] == marker &&
                (firstPass is null ||
                    (firstPass[i] == 0x5A && firstPass[i + 1] == 0x5A && firstPass[i + 2] == 0x5A)))
            {
                return true;
            }
        }
        return false;
    }

    private static Win32Exception CreateCaptureException(string operation)
        => new(Marshal.GetLastPInvokeError(), $"{operation} failed during window-only bitmap capture.");

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static unsafe partial int SetDIBits(HDC dc, HBITMAP bitmap, uint start, uint lines,
        byte* pixels, BITMAPINFO* info, uint usage);
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording;

/// <summary>Draws the system cursor image into a BGRA frame.</summary>
/// <remarks>
/// Windows draws the cursor itself (<c>DrawIconEx</c>) into a small memory bitmap holding a copy of
/// the frame pixels under the cursor, which is then copied back. That keeps every cursor shape
/// right, including the inverting I-beam, without decoding cursor bitmaps by hand.
/// Coverage ceiling (issue #630): needs a real cursor handle from the desktop session.
/// </remarks>
internal sealed unsafe class CursorRenderer : IDisposable
{
    private readonly Dictionary<nint, (int HotX, int HotY, int Width, int Height)> _shapes = [];
    private HDC _dc;
    private HBITMAP _bitmap;
    private HGDIOBJ _previous;
    private uint* _bits;
    private int _bitmapWidth;
    private int _bitmapHeight;

    /// <summary>
    /// Draws <paramref name="cursor"/> into a BGRA <paramref name="frame"/>, scaled by
    /// <paramref name="scale"/>, with its tip at (<paramref name="tipX"/>, <paramref name="tipY"/>).
    /// Only pixels inside <paramref name="clip"/> and the <paramref name="frameWidth"/> by
    /// <paramref name="frameHeight"/> frame are changed.
    /// </summary>
    public void Draw(
        byte[] frame, int frameWidth, int frameHeight,
        nint cursor, int tipX, int tipY, double scale,
        (int X, int Y, int Width, int Height) clip)
    {
        if (cursor == 0 || scale <= 0 || !TryGetShape(cursor, out var shape))
        {
            return;
        }

        var width = Math.Max(1, (int)Math.Round(shape.Width * scale));
        var height = Math.Max(1, (int)Math.Round(shape.Height * scale));
        var left = tipX - (int)Math.Round(shape.HotX * scale);
        var top = tipY - (int)Math.Round(shape.HotY * scale);

        var x0 = Math.Max(left, Math.Max(clip.X, 0));
        var y0 = Math.Max(top, Math.Max(clip.Y, 0));
        var x1 = Math.Min(left + width, Math.Min(clip.X + clip.Width, frameWidth));
        var y1 = Math.Min(top + height, Math.Min(clip.Y + clip.Height, frameHeight));
        if (x0 >= x1 || y0 >= y1 || frame.Length < frameWidth * frameHeight * 4 || !EnsureBitmap(width, height))
        {
            return;
        }

        fixed (byte* framePtr = frame)
        {
            var pixels = (uint*)framePtr;
            for (var y = y0; y < y1; y++)
            {
                new ReadOnlySpan<uint>(pixels + (y * frameWidth) + x0, x1 - x0)
                    .CopyTo(new Span<uint>(_bits + ((y - top) * _bitmapWidth) + (x0 - left), x1 - x0));
            }

            var icon = new HICON((void*)cursor);
            if (!PInvoke.DrawIconEx(_dc, 0, 0, icon, width, height, 0, default, DI_FLAGS.DI_NORMAL))
            {
                return;
            }
            PInvoke.GdiFlush();

            for (var y = y0; y < y1; y++)
            {
                var source = _bits + ((y - top) * _bitmapWidth) + (x0 - left);
                var target = pixels + (y * frameWidth) + x0;
                for (var x = 0; x < x1 - x0; x++)
                {
                    // GDI leaves the alpha byte undefined; the encoder expects opaque pixels.
                    target[x] = source[x] | 0xFF000000u;
                }
            }
        }
    }

    public void Dispose()
    {
        ReleaseBitmap();
        if (!_dc.IsNull)
        {
            PInvoke.DeleteDC(_dc);
            _dc = default;
        }
    }

    private bool TryGetShape(nint cursor, out (int HotX, int HotY, int Width, int Height) shape)
    {
        if (_shapes.TryGetValue(cursor, out shape))
        {
            return true;
        }

        ICONINFO info;
        if (!PInvoke.GetIconInfo(new HICON((void*)cursor), &info))
        {
            return false;
        }

        try
        {
            // A monochrome cursor has no color bitmap; its mask stacks the AND and XOR halves.
            var hasColor = !info.hbmColor.IsNull;
            var measured = hasColor ? info.hbmColor : info.hbmMask;
            BITMAP bitmap;
            if (measured.IsNull || PInvoke.GetObject((HGDIOBJ)measured.Value, sizeof(BITMAP), &bitmap) == 0)
            {
                return false;
            }

            var height = hasColor ? bitmap.bmHeight : bitmap.bmHeight / 2;
            shape = ((int)info.xHotspot, (int)info.yHotspot, bitmap.bmWidth, height);
            if (shape.Width <= 0 || shape.Height <= 0)
            {
                return false;
            }
            _shapes[cursor] = shape;
            return true;
        }
        finally
        {
            if (!info.hbmMask.IsNull)
            {
                PInvoke.DeleteObject((HGDIOBJ)info.hbmMask.Value);
            }
            if (!info.hbmColor.IsNull)
            {
                PInvoke.DeleteObject((HGDIOBJ)info.hbmColor.Value);
            }
        }
    }

    private bool EnsureBitmap(int width, int height)
    {
        if (_dc.IsNull)
        {
            _dc = PInvoke.CreateCompatibleDC(default);
            if (_dc.IsNull)
            {
                return false;
            }
        }

        if (_bits != null && width <= _bitmapWidth && height <= _bitmapHeight)
        {
            return true;
        }

        ReleaseBitmap();
        var newWidth = Math.Max(width, 64);
        var newHeight = Math.Max(height, 64);
        var header = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = newWidth,
                biHeight = -newHeight,
                biPlanes = 1,
                biBitCount = 32,
            },
        };
        void* bits;
        var bitmap = PInvoke.CreateDIBSection(_dc, &header, DIB_USAGE.DIB_RGB_COLORS, &bits, default, 0);
        if (bitmap.IsNull || bits == null)
        {
            return false;
        }

        _bitmap = bitmap;
        _bits = (uint*)bits;
        _bitmapWidth = newWidth;
        _bitmapHeight = newHeight;
        _previous = PInvoke.SelectObject(_dc, (HGDIOBJ)bitmap.Value);
        return true;
    }

    private void ReleaseBitmap()
    {
        if (_bitmap.IsNull)
        {
            return;
        }

        PInvoke.SelectObject(_dc, _previous);
        PInvoke.DeleteObject((HGDIOBJ)_bitmap.Value);
        _bitmap = default;
        _bits = null;
        _bitmapWidth = 0;
        _bitmapHeight = 0;
    }
}

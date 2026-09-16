// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using SkiaSharp;

namespace WinApp.Cli.Helpers;

/// <summary>Turns captured BGRA pixels into a PNG file's bytes.</summary>
/// <remarks>
/// Shared by every command that writes a screenshot, so there is exactly one place that decides how
/// winapp encodes one — a second copy would be free to drift in colour type, premultiplication, or
/// quality and produce images that differ depending on which verb made them.
/// </remarks>
internal static class PngImage
{
    /// <summary>Encodes a BGRA8888 premultiplied buffer as PNG.</summary>
    public static byte[] Encode(byte[] bgraPixels, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bgraPixels);

        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        unsafe
        {
            var ptr = (byte*)bitmap.GetPixels().ToPointer();
            Marshal.Copy(bgraPixels, 0, (nint)ptr, bgraPixels.Length);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

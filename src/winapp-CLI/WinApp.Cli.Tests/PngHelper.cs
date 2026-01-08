// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Drawing;

namespace WinApp.Cli.Tests;

internal static class PngHelper
{
    internal static void CreateTestImage(string path)
    {
        // Create a minimal valid PNG file (1x1 pixel transparent image)
        var pngData = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG signature
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, // IHDR chunk
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, // 1x1 dimensions
            0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, // RGBA, no compression
            0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, // IDAT chunk
            0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00, // Image data
            0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, // Image data
            0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, // IEND chunk
            0x42, 0x60, 0x82
        };
        File.WriteAllBytes(path, pngData);
    }

    /// <summary>
    /// Verifies that all pixels in the image are fully transparent (alpha = 0).
    /// </summary>
    /// <param name="imagePath">Path to the image file to check.</param>
    /// <returns>True if all pixels are transparent, false otherwise.</returns>
    internal static bool IsFullyTransparent(string imagePath)
    {
        using var bitmap = new Bitmap(imagePath);
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A != 0)
                {
                    return false;
                }
            }
        }
        return true;
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Shared blank-buffer detection for window screenshot capture.
/// </summary>
internal static class CapturedFrame
{
    /// <summary>
    /// Whether every byte in a captured BGRA buffer is zero.
    /// </summary>
    /// <param name="pixels">The captured buffer. An empty buffer is blank.</param>
    /// <returns><see langword="true"/> when every byte is zero.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pixels"/> is <see langword="null"/>.</exception>
    public static bool IsBlank(byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);

        // Long-sized chunks: these buffers are megabytes, and the answer is usually "not blank" on
        // the very first non-zero pixel.
        var chunks = MemoryMarshal.Cast<byte, long>(pixels.AsSpan());
        foreach (var chunk in chunks)
        {
            if (chunk != 0)
            {
                return false;
            }
        }

        for (var i = chunks.Length * sizeof(long); i < pixels.Length; i++)
        {
            if (pixels[i] != 0)
            {
                return false;
            }
        }

        return true;
    }
}

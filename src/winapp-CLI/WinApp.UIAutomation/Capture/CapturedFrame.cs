// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Pure inspection of captured pixels. Shared by the screenshot path, the frame-capture backend, and
/// video recording, all of which have to answer the same question: did this capture actually come
/// back with anything on it?
/// </summary>
public static class CapturedFrame
{
    /// <summary>
    /// Whether a captured BGRA buffer came back entirely black, which is what a window that was not
    /// rendered — or was captured through a pipeline that produced nothing — looks like.
    /// </summary>
    /// <remarks>
    /// A caller that hands a blank capture on as a picture or a video frame reports success for an
    /// image of nothing, so this is the check that turns that into an honest failure. It cannot tell a
    /// genuinely black window apart from an unrendered one; callers that can afford to retry should,
    /// and callers that promised not to activate the window should report the blank instead.
    /// </remarks>
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

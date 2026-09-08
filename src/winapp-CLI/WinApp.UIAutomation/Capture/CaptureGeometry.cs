// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Pure geometry for fitting a captured region into a fixed output surface. Shared by the
/// screenshot scaler and by video recording, which must letterbox each frame into a constant
/// encoder size.
/// </summary>
public static class CaptureGeometry
{
    /// <summary>
    /// Scales a <paramref name="cropW"/>×<paramref name="cropH"/> region to fit inside
    /// <paramref name="displayWidth"/>×<paramref name="displayHeight"/> while preserving aspect ratio,
    /// then centers it within an <paramref name="encoderWidth"/>×<paramref name="encoderHeight"/>
    /// surface. Returns the centered offset and the fitted size.
    /// </summary>
    /// <param name="cropW">Width of the captured region. Must be positive.</param>
    /// <param name="cropH">Height of the captured region. Must be positive.</param>
    /// <param name="encoderWidth">Width of the surface the content is centered in. Must be positive.</param>
    /// <param name="encoderHeight">Height of the surface the content is centered in. Must be positive.</param>
    /// <param name="displayWidth">Width the content is scaled to fit. Must be positive and no larger than <paramref name="encoderWidth"/>.</param>
    /// <param name="displayHeight">Height the content is scaled to fit. Must be positive and no larger than <paramref name="encoderHeight"/>.</param>
    /// <returns>Where to place the content within the surface, and how big it ends up.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Any dimension is zero or negative, or a display dimension exceeds its encoder dimension.
    /// </exception>
    public static (int OffsetX, int OffsetY, int FitW, int FitH) ComputeFittedContentRect(
        int cropW, int cropH, int encoderWidth, int encoderHeight, int displayWidth, int displayHeight)
    {
        // Every argument is a pixel dimension, so none of them can be zero or negative. Without this
        // a zero crop divides in floating point rather than throwing, and the caller silently gets a
        // one-pixel rect instead of an error.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cropW);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cropH);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(encoderWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(encoderHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(displayWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(displayHeight);

        // The content is centered inside the encoder surface, so it has to fit: a display dimension
        // larger than its encoder dimension centers to a negative offset, which a caller then hands
        // to Buffer.BlockCopy. ComputeTargetSize never produces that, but this is public API.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(displayWidth, encoderWidth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(displayHeight, encoderHeight);

        var scale = Math.Min(displayWidth / (double)cropW, displayHeight / (double)cropH);
        var fitW = Math.Clamp((int)Math.Round(cropW * scale), 1, displayWidth);
        var fitH = Math.Clamp((int)Math.Round(cropH * scale), 1, displayHeight);
        var offsetX = (encoderWidth - fitW) / 2;
        var offsetY = (encoderHeight - fitH) / 2;
        return (offsetX, offsetY, fitW, fitH);
    }
}

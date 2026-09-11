// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>Maps image pixels to the physical screen coordinates used by UI input.</summary>
public sealed class CaptureCoordinates
{
    /// <summary>The coordinate convention; bounds use exclusive right and bottom edges.</summary>
    public string Space { get; } = "screen-physical-pixels";

    /// <summary>The captured region in the source machine's virtual desktop.</summary>
    public required PointerRect SourceBounds { get; init; }

    /// <summary>The image rectangle containing source pixels, excluding encoder padding.</summary>
    public required PointerRect ContentRect { get; init; }

    /// <summary>
    /// Maps an image pixel's center to a source pixel, rounding down. Throws for padding or an
    /// invalid rectangle. Downscaled images necessarily lose precision; use a native PNG for exact input.
    /// </summary>
    public PointerPoint ToScreenPoint(PointerPoint imagePoint)
    {
        var sourceWidth = checked(SourceBounds.Right - SourceBounds.Left);
        var sourceHeight = checked(SourceBounds.Bottom - SourceBounds.Top);
        var contentWidth = checked(ContentRect.Right - ContentRect.Left);
        var contentHeight = checked(ContentRect.Bottom - ContentRect.Top);
        if (sourceWidth <= 0 || sourceHeight <= 0 || contentWidth <= 0 || contentHeight <= 0)
        {
            throw new InvalidOperationException("Capture coordinate rectangles must have positive dimensions.");
        }
        if (!ContentRect.Contains(imagePoint))
        {
            throw new ArgumentOutOfRangeException(nameof(imagePoint), "The image point is outside the captured content.");
        }
        return new PointerPoint(
            SourceBounds.Left + (int)Math.Floor((imagePoint.X - ContentRect.Left + 0.5) * sourceWidth / contentWidth),
            SourceBounds.Top + (int)Math.Floor((imagePoint.Y - ContentRect.Top + 0.5) * sourceHeight / contentHeight));
    }
}

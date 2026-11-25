// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording;

/// <summary>
/// How a finished recording is cut down to the area that showed the window or its popups: which part of
/// each recorded frame to keep (<c>Crop*</c>), and how big the final frames are (<c>Output*</c>/<c>Display*</c>).
/// </summary>
internal sealed record RecordTrimPlan(
    int SourceWidth, int SourceHeight,
    int CropX, int CropY, int CropWidth, int CropHeight,
    int OutputWidth, int OutputHeight, int DisplayWidth, int DisplayHeight)
{
    /// <summary>Cuts one recorded BGRA frame down to the final frame.</summary>
    public byte[] Apply(byte[] recordedFrame)
    {
        if (CropWidth == OutputWidth && CropHeight == OutputHeight &&
            DisplayWidth == OutputWidth && DisplayHeight == OutputHeight)
        {
            // No scaling: copy the kept rows directly, much faster than resampling.
            var output = new byte[checked(OutputWidth * OutputHeight * 4)];
            var rowBytes = OutputWidth * 4;
            for (var y = 0; y < OutputHeight; y++)
            {
                Buffer.BlockCopy(recordedFrame, ((CropY + y) * SourceWidth + CropX) * 4, output, y * rowBytes, rowBytes);
            }
            return output;
        }
        return UiRecordingService.ProcessFrame(
            recordedFrame, SourceWidth, SourceHeight, CropX, CropY, CropWidth, CropHeight,
            OutputWidth, OutputHeight, DisplayWidth, DisplayHeight);
    }

    /// <summary>Moves a point in a recorded frame to the same spot in the final frame.</summary>
    /// <returns>The new position, and whether it is inside the kept area.</returns>
    public (int X, int Y, bool Inside) MapPoint(int x, int y)
    {
        var (offsetX, offsetY, fitWidth, fitHeight) = CaptureGeometry.ComputeFittedContentRect(
            CropWidth, CropHeight, OutputWidth, OutputHeight, DisplayWidth, DisplayHeight);
        return (
            offsetX + (int)Math.Floor((x - CropX) * (double)fitWidth / CropWidth),
            offsetY + (int)Math.Floor((y - CropY) * (double)fitHeight / CropHeight),
            x >= CropX && x < CropX + CropWidth && y >= CropY && y < CropY + CropHeight);
    }
}

internal sealed partial class UiRecordingService
{
    /// <summary>
    /// With automatic padding, how far past each window edge a recording leaves room for menus and flyouts.
    /// Typical WinUI menus, dropdowns and teaching tips are 100-400 pixels tall and open next to the control
    /// that shows them. The capture also stops at the monitor's edge, where no popup can open.
    /// </summary>
    internal const int AutoPopupMargin = 400;
    /// <summary>Test seam: re-encodes <c>source</c> to <c>destination</c> with the plan applied to every frame.</summary>
    /// <summary>Test seam: joins the sources into <c>destination</c>, with the plan applied to every frame.</summary>
    internal static Action<RecordTrimSources, string, RecordTrimPlan, int> s_trimVideo = Mp4Trimmer.Trim;

    internal static uint VideoBitrate(int width, int height, int fps)
        => (uint)Math.Clamp((long)width * height * fps / 8, 1_000_000, 24_000_000);

    internal static uint IntermediateBitrate(int width, int height, int fps)
        => (uint)Math.Clamp((long)width * height * fps / 2, 4_000_000, 48_000_000);

    /// <summary>
    /// Video size for a window frame with an empty border around it. <paramref name="maxEdge"/> is applied to
    /// the window rather than the whole frame, so the border doesn't make the window smaller in the video.
    /// </summary>
    internal static (int EncoderW, int EncoderH, int DisplayW, int DisplayH) ComputePaddedTargetSize(
        int frameWidth, int frameHeight, PointerRect window, int maxEdge)
    {
        var windowLongest = Math.Max(window.Right - window.Left, window.Bottom - window.Top);
        if (maxEdge <= 0 || windowLongest <= maxEdge)
        {
            return ComputeTargetSize(frameWidth, frameHeight, 0);
        }
        var scale = (double)maxEdge / windowLongest;
        return ComputeTargetSize(
            Math.Max(1, (int)Math.Round(frameWidth * scale)), Math.Max(1, (int)Math.Round(frameHeight * scale)), 0);
    }

    /// <summary>Maps a rectangle in a recorded frame to video pixels, rounding outward.</summary>
    internal static PointerRect MapToVideo(PointerRect area, int sourceWidth, int sourceHeight,
        int encoderWidth, int encoderHeight, int displayWidth, int displayHeight)
    {
        var (offsetX, offsetY, fitWidth, fitHeight) = CaptureGeometry.ComputeFittedContentRect(
            sourceWidth, sourceHeight, encoderWidth, encoderHeight, displayWidth, displayHeight);
        var scaleX = (double)fitWidth / sourceWidth;
        var scaleY = (double)fitHeight / sourceHeight;
        return new(
            offsetX + (int)Math.Floor(area.Left * scaleX),
            offsetY + (int)Math.Floor(area.Top * scaleY),
            offsetX + (int)Math.Ceiling(area.Right * scaleX),
            offsetY + (int)Math.Ceiling(area.Bottom * scaleY));
    }

    internal static PointerRect Union(PointerRect a, PointerRect b)
        => new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

    /// <summary>
    /// Plans the cut from a <paramref name="videoWidth"/>×<paramref name="videoHeight"/> recording down to
    /// <paramref name="usedArea"/>, or returns <see langword="null"/> when the area is empty.
    /// </summary>
    internal static RecordTrimPlan? PlanTrim(PointerRect usedArea, int videoWidth, int videoHeight, int maxEdge)
    {
        // Even edges keep the H.264 chroma planes aligned.
        var left = Math.Clamp(usedArea.Left & ~1, 0, videoWidth);
        var top = Math.Clamp(usedArea.Top & ~1, 0, videoHeight);
        var right = Math.Clamp((usedArea.Right + 1) & ~1, left, videoWidth);
        var bottom = Math.Clamp((usedArea.Bottom + 1) & ~1, top, videoHeight);
        if (right - left < 2 || bottom - top < 2)
        {
            return null;
        }
        var (outputWidth, outputHeight, displayWidth, displayHeight) = ComputeTargetSize(right - left, bottom - top, maxEdge);
        return new(videoWidth, videoHeight, left, top, right - left, bottom - top,
            outputWidth, outputHeight, displayWidth, displayHeight);
    }

    /// <summary>
    /// Plans the cut from a padded frame down to just the window, at the window's own size so the cut is a
    /// plain copy. <paramref name="window"/> is the window's area in the padded frame.
    /// </summary>
    internal static RecordTrimPlan PlanWindowCrop(PointerRect window, int paddedWidth, int paddedHeight)
    {
        var left = Math.Clamp(window.Left, 0, paddedWidth - 2);
        var top = Math.Clamp(window.Top, 0, paddedHeight - 2);
        // Even sizes, inside the window, so no border shows along its edge.
        var width = Math.Max(2, Math.Min(window.Right, paddedWidth) - left) & ~1;
        var height = Math.Max(2, Math.Min(window.Bottom, paddedHeight) - top) & ~1;
        var (outputWidth, outputHeight, displayWidth, displayHeight) = ComputeTargetSize(width, height, 0);
        return new(paddedWidth, paddedHeight, left, top, width, height, outputWidth, outputHeight, displayWidth, displayHeight);
    }

    internal static bool Contains(PointerRect outer, PointerRect inner)
        => inner.Left >= outer.Left && inner.Top >= outer.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    /// <summary>Replaces the finished MP4 at <paramref name="path"/> with its trimmed version.</summary>
    private static void TrimRecording(string path, RecordTrimPlan plan, int fps, RecordTrimSources sources)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var trimmed = Path.Join(directory, $".{Guid.NewGuid():N}.trimmed.mp4");
        try
        {
            s_trimVideo(sources with { WindowVideo = path }, trimmed, plan, fps);
            File.Move(trimmed, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(trimmed))
            {
                File.Delete(trimmed);
            }
        }
    }
}

/// <summary>
/// The two recordings a trimmed video is made from: the window alone, for the frames before any popup reached
/// past it, and the padded frames from <see cref="PaddedStartFrame"/> on.
/// </summary>
/// <param name="PaddedVideo">Padded frames, starting at <see cref="PaddedStartFrame"/>.</param>
/// <param name="PaddedStartFrame">Index of the first frame in <see cref="PaddedVideo"/>.</param>
/// <param name="WindowCrop">Where the window recording sits in a padded frame.</param>
/// <param name="Background">0xRRGGBB for the empty space around the window in the earlier frames.</param>
internal sealed record RecordTrimSources(string PaddedVideo, int PaddedStartFrame, RecordTrimPlan WindowCrop, int Background)
{
    /// <summary>The window recording; every frame, though only those before <see cref="PaddedStartFrame"/> are used.</summary>
    public string WindowVideo { get; init; } = "";

    /// <summary>Puts a frame of the window recording back where it sits in a padded frame.</summary>
    public byte[] ToPadded(byte[] windowFrame)
    {
        var crop = WindowCrop;
        var padded = new byte[checked(crop.SourceWidth * crop.SourceHeight * 4)];
        MemoryMarshal.Cast<byte, uint>(padded.AsSpan()).Fill(0xFF000000 | (uint)Background);
        if (crop.CropWidth == crop.OutputWidth && crop.CropHeight == crop.OutputHeight)
        {
            var rowBytes = crop.CropWidth * 4;
            for (var y = 0; y < crop.CropHeight; y++)
            {
                Buffer.BlockCopy(windowFrame, y * rowBytes, padded, ((crop.CropY + y) * crop.SourceWidth + crop.CropX) * 4, rowBytes);
            }
            return padded;
        }
        // A tiny window was letterboxed up to the encoder's minimum; scale its content back into place.
        var (x, y0, w, h) = CaptureGeometry.ComputeFittedContentRect(
            crop.CropWidth, crop.CropHeight, crop.OutputWidth, crop.OutputHeight, crop.DisplayWidth, crop.DisplayHeight);
        var window = UiRecordingService.ProcessFrame(windowFrame, crop.OutputWidth, crop.OutputHeight, x, y0, w, h,
            crop.CropWidth, crop.CropHeight, crop.CropWidth, crop.CropHeight);
        var windowRow = crop.CropWidth * 4;
        for (var y = 0; y < crop.CropHeight; y++)
        {
            Buffer.BlockCopy(window, y * windowRow, padded, ((crop.CropY + y) * crop.SourceWidth + crop.CropX) * 4, windowRow);
        }
        return padded;
    }
}

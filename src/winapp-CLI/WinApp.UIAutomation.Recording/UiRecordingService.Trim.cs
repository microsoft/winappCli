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

    /// <summary>Test seam: joins the sources into <c>destination</c>, with the plan applied to every frame.</summary>
    internal static Action<RecordTrimSources, string, RecordTrimPlan, int> s_trimVideo = Mp4Trimmer.Trim;

    internal static uint VideoBitrate(int width, int height, int fps)
        => (uint)Math.Clamp((long)width * height * fps / 8, 1_000_000, 24_000_000);

    internal static uint IntermediateBitrate(int width, int height, int fps)
        => (uint)Math.Clamp((long)width * height * fps / 2, 4_000_000, 48_000_000);

    /// <summary>
    /// Largest frame, in 16x16 macroblocks, the Media Foundation H.264 encoder accepts (H.264 level 5.1/5.2,
    /// for example 4096x2304). Larger frames fail with MF_E_INVALIDMEDIATYPE.
    /// </summary>
    internal const int MaxEncoderMacroblocks = 36_864;

    /// <summary>
    /// Like <see cref="ComputeTargetSize"/>, but also scales down a frame the H.264 encoder would reject.
    /// A window with a wide border can be much larger than any monitor.
    /// </summary>
    internal static (int EncoderW, int EncoderH, int DisplayW, int DisplayH) ComputeEncodableTargetSize(
        int width, int height, int maxEdge)
    {
        var size = ComputeTargetSize(width, height, maxEdge);
        static long Macroblocks(int w, int h) => (long)((w + 15) / 16) * ((h + 15) / 16);
        if (Macroblocks(size.EncoderW, size.EncoderH) <= MaxEncoderMacroblocks)
        {
            return size;
        }
        var scale = Math.Sqrt(MaxEncoderMacroblocks * 256.0 / ((double)size.EncoderW * size.EncoderH));
        // Shrink a little past the exact scale, so rounding each edge up to a whole macroblock still fits.
        return ComputeTargetSize(width, height, Math.Max(64, (int)(Math.Max(size.EncoderW, size.EncoderH) * scale) - 32));
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
    /// Plans the cut from a padded frame down to just the window. <paramref name="window"/> is the window's
    /// area in the padded frame; it is rounded inward so no border shows along the window's edge. The output
    /// is <paramref name="windowWidth"/>×<paramref name="windowHeight"/> sized for <paramref name="maxEdge"/>,
    /// exactly as a recording with no border.
    /// </summary>
    internal static RecordTrimPlan PlanWindowCrop(
        (double Left, double Top, double Right, double Bottom) window, int paddedWidth, int paddedHeight,
        int windowWidth, int windowHeight, int maxEdge)
    {
        var left = Math.Clamp((int)Math.Ceiling(window.Left - 1e-6), 0, paddedWidth - 1);
        var top = Math.Clamp((int)Math.Ceiling(window.Top - 1e-6), 0, paddedHeight - 1);
        var width = Math.Max(1, Math.Min((int)Math.Floor(window.Right + 1e-6), paddedWidth) - left);
        var height = Math.Max(1, Math.Min((int)Math.Floor(window.Bottom + 1e-6), paddedHeight) - top);
        var (outputWidth, outputHeight, displayWidth, displayHeight) = ComputeTargetSize(windowWidth, windowHeight, maxEdge);
        return new(paddedWidth, paddedHeight, left, top, width, height, outputWidth, outputHeight, displayWidth, displayHeight);
    }

    /// <summary>Where a rectangle in a recorded frame lands in the video, in fractional video pixels.</summary>
    internal static (double Left, double Top, double Right, double Bottom) MapToVideoExact(PointerRect area,
        int sourceWidth, int sourceHeight, int encoderWidth, int encoderHeight, int displayWidth, int displayHeight)
    {
        var (offsetX, offsetY, fitWidth, fitHeight) = CaptureGeometry.ComputeFittedContentRect(
            sourceWidth, sourceHeight, encoderWidth, encoderHeight, displayWidth, displayHeight);
        var scaleX = (double)fitWidth / sourceWidth;
        var scaleY = (double)fitHeight / sourceHeight;
        return (offsetX + area.Left * scaleX, offsetY + area.Top * scaleY, offsetX + area.Right * scaleX, offsetY + area.Bottom * scaleY);
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

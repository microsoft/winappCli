// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

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
    /// <summary>Test seam: re-encodes <c>source</c> to <c>destination</c> with the plan applied to every frame.</summary>
    internal static Action<string, string, RecordTrimPlan, int> s_trimVideo = Mp4Trimmer.Trim;

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
    /// <paramref name="usedArea"/>, or returns <see langword="null"/> when the whole recording was used.
    /// </summary>
    internal static RecordTrimPlan? PlanTrim(PointerRect usedArea, int videoWidth, int videoHeight, int maxEdge)
    {
        // Even edges keep the H.264 chroma planes aligned.
        var left = Math.Clamp(usedArea.Left & ~1, 0, videoWidth);
        var top = Math.Clamp(usedArea.Top & ~1, 0, videoHeight);
        var right = Math.Clamp((usedArea.Right + 1) & ~1, left, videoWidth);
        var bottom = Math.Clamp((usedArea.Bottom + 1) & ~1, top, videoHeight);
        if (right - left < 2 || bottom - top < 2 ||
            (left == 0 && top == 0 && right == videoWidth && bottom == videoHeight))
        {
            return null;
        }
        var (outputWidth, outputHeight, displayWidth, displayHeight) = ComputeTargetSize(right - left, bottom - top, maxEdge);
        return new(videoWidth, videoHeight, left, top, right - left, bottom - top,
            outputWidth, outputHeight, displayWidth, displayHeight);
    }

    /// <summary>Replaces the finished MP4 at <paramref name="path"/> with its trimmed version.</summary>
    private static void TrimRecording(string path, RecordTrimPlan plan, int fps)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var trimmed = Path.Join(directory, $".{Guid.NewGuid():N}.trimmed.mp4");
        try
        {
            s_trimVideo(path, trimmed, plan, fps);
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

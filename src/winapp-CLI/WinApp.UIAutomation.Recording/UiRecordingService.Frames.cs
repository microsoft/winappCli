// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using SkiaSharp;

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording;

internal sealed partial class UiRecordingService
{
    // Media Foundation rejects H.264 frames smaller than 64 pixels.
    private const int MfH264MinWidth = 64;
    private const int MfH264MinHeight = 64;

    private sealed class RecordFrameArtifactSetup
    {
        public required RecordOptions Options { get; init; }
        public required DateTimeOffset StartedUtc { get; init; }
        public required int EncoderWidth { get; init; }
        public required int EncoderHeight { get; init; }
    }

    private RecordFrameArtifactCoordinator CreateRecordFrameArtifactCoordinator(
        RecordFrameArtifactSetup setup)
    {
        return RecordFrameArtifactCoordinator.Create(new RecordFrameBundleConfiguration
        {
            FinalDirectory = setup.Options.FramesDirectory!,
            VideoPath = setup.Options.OutputPath,
            StartedUtc = setup.StartedUtc,
            Width = setup.EncoderWidth,
            Height = setup.EncoderHeight,
            Requested = new RecordFrameRequestManifest
            {
                DurationSec = setup.Options.DurationSec,
                Fps = setup.Options.Fps,
                MaxEdge = setup.Options.MaxEdge,
            },
            Logger = _logger,
        });
    }

    /// <summary>Computes even content and encoder sizes, padding to Media Foundation's minimum.</summary>
    internal static (int EncoderW, int EncoderH, int DisplayW, int DisplayH) ComputeTargetSize(int width, int height, int maxEdge)
    {
        var scale = 1.0;
        var longest = Math.Max(width, height);
        if (maxEdge > 0 && longest > maxEdge)
        {
            scale = (double)maxEdge / longest;
        }

        int displayW, displayH;
        if (maxEdge > 0 && longest > maxEdge)
        {
            var evenMaxEdge = EvenFloor(maxEdge);
            if (width >= height)
            {
                displayW = evenMaxEdge;
                displayH = Math.Min(EvenRound(height * scale), evenMaxEdge);
            }
            else
            {
                displayH = evenMaxEdge;
                displayW = Math.Min(EvenRound(width * scale), evenMaxEdge);
            }
        }
        else
        {
            displayW = EvenRound(width * scale);
            displayH = EvenRound(height * scale);
        }

        var encoderW = Math.Max(displayW, MfH264MinWidth);
        var encoderH = Math.Max(displayH, MfH264MinHeight);
        return (encoderW, encoderH, displayW, displayH);

        static int EvenRound(double value)
            => Math.Max(2, (int)(Math.Round(value / 2.0, MidpointRounding.AwayFromZero) * 2));

        static int EvenFloor(int value)
            => Math.Max(2, value % 2 == 0 ? value : value - 1);
    }

    /// <summary>Crops and scales BGRA pixels, centering content when padding is required.</summary>
    internal static byte[] ProcessFrame(
        byte[] source, int sourceWidth, int sourceHeight,
        int cropX, int cropY, int cropW, int cropH,
        int encoderWidth, int encoderHeight,
        int displayWidth, int displayHeight)
    {
        if (!RequiresFrameTransform(
                sourceWidth,
                sourceHeight,
                cropX,
                cropY,
                cropW,
                cropH,
                encoderWidth,
                encoderHeight,
                displayWidth,
                displayHeight)
            && source.Length == encoderWidth * encoderHeight * 4)
        {
            return source;
        }

        (cropX, cropY, cropW, cropH) = ClampCropRect(
            cropX,
            cropY,
            cropW,
            cropH,
            sourceWidth,
            sourceHeight);

        var srcInfo = new SKImageInfo(sourceWidth, sourceHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var srcBitmap = new SKBitmap(srcInfo);
        Marshal.Copy(source, 0, srcBitmap.GetPixels(), Math.Min(source.Length, srcInfo.BytesSize));

        var dstInfo = new SKImageInfo(encoderWidth, encoderHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var dstBitmap = new SKBitmap(dstInfo);
        using (var canvas = new SKCanvas(dstBitmap))
        {
            canvas.Clear(SKColors.Black);
            var (offsetX, offsetY, fitW, fitH) = CaptureGeometry.ComputeFittedContentRect(
                cropW, cropH, encoderWidth, encoderHeight, displayWidth, displayHeight);
            var srcRect = SKRect.Create(cropX, cropY, cropW, cropH);
            var dstRect = SKRect.Create(offsetX, offsetY, fitW, fitH);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium, IsAntialias = false };
            canvas.DrawBitmap(srcBitmap, srcRect, dstRect, paint);
        }

        var output = new byte[dstInfo.BytesSize];
        Marshal.Copy(dstBitmap.GetPixels(), output, 0, output.Length);
        return output;
    }

    internal static bool RequiresFrameTransform(
        int sourceWidth,
        int sourceHeight,
        int cropX,
        int cropY,
        int cropW,
        int cropH,
        int encoderWidth,
        int encoderHeight,
        int displayWidth,
        int displayHeight)
        => cropX != 0
            || cropY != 0
            || cropW != sourceWidth
            || cropH != sourceHeight
            || encoderWidth != sourceWidth
            || encoderHeight != sourceHeight
            || displayWidth != encoderWidth
            || displayHeight != encoderHeight;

    internal static (int X, int Y, int Width, int Height) ClampCropRect(
        int cropX,
        int cropY,
        int cropW,
        int cropH,
        int sourceWidth,
        int sourceHeight)
    {
        cropX = Math.Clamp(cropX, 0, Math.Max(0, sourceWidth - 1));
        cropY = Math.Clamp(cropY, 0, Math.Max(0, sourceHeight - 1));
        cropW = Math.Clamp(cropW, 1, sourceWidth - cropX);
        cropH = Math.Clamp(cropH, 1, sourceHeight - cropY);
        return (cropX, cropY, cropW, cropH);
    }
}

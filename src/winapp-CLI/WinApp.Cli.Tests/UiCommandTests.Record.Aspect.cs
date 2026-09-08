// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    private static (byte B, byte G, byte R, byte A) GetPixel(byte[] frame, int width, int x, int y)
    {
        var offset = (y * width + x) * 4;
        return (frame[offset], frame[offset + 1], frame[offset + 2], frame[offset + 3]);
    }

    private static byte[] MakeSolidFrame(int width, int height, byte b, byte g, byte r, byte a = 255)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = a;
        }
        return pixels;
    }

    [TestMethod]
    public void Record_ProcessFrame_MismatchedCropAspect_LetterboxesInsteadOfStretching()
    {
        const int srcW = 100, srcH = 50;
        const int encW = 100, encH = 100;
        const int dispW = 100, dispH = 100;
        var source = MakeSolidFrame(srcW, srcH, b: 0, g: 200, r: 0); // green content

        var output = UiRecordingService.ProcessFrame(
            source, srcW, srcH,
            cropX: 0, cropY: 0, cropW: srcW, cropH: srcH,
            encoderWidth: encW, encoderHeight: encH,
            displayWidth: dispW, displayHeight: dispH);

        Assert.AreEqual(encW * encH * 4, output.Length);

        var topBar = GetPixel(output, encW, encW / 2, 24);
        Assert.AreEqual((byte)0, topBar.B, "top letterbox bar must remain black");
        Assert.AreEqual((byte)0, topBar.G, "top letterbox bar must remain black");
        Assert.AreEqual((byte)0, topBar.R, "top letterbox bar must remain black");

        var bottomBar = GetPixel(output, encW, encW / 2, 75);
        Assert.AreEqual((byte)0, bottomBar.B, "bottom letterbox bar must remain black");
        Assert.AreEqual((byte)0, bottomBar.G, "bottom letterbox bar must remain black");
        Assert.AreEqual((byte)0, bottomBar.R, "bottom letterbox bar must remain black");

        var center = GetPixel(output, encW, encW / 2, encH / 2);
        Assert.IsTrue(center.G > 128, $"center content band should contain the green source; got B={center.B} G={center.G} R={center.R}");

        var leftEdge = GetPixel(output, encW, 0, encH / 2);
        var rightEdge = GetPixel(output, encW, encW - 1, encH / 2);
        Assert.IsTrue(leftEdge.G > 128, "content band should span the full fitted width");
        Assert.IsTrue(rightEdge.G > 128, "content band should span the full fitted width");
    }

    [TestMethod]
    public void ProcessFrame_SmallContent_CenteredWithBlackPadding()
    {
        // A tiny 32×32 red content frame placed inside an 80×80 encoder frame should be
        // centered and the padding region should be black.
        const int contentW = 32, contentH = 32;
        const int encoderW = 80, encoderH = 80;

        var source = MakeSolidFrame(contentW, contentH, b: 0, g: 0, r: 255); // red
        var output = UiRecordingService.ProcessFrame(
            source, contentW, contentH,
            cropX: 0, cropY: 0, cropW: contentW, cropH: contentH,
            encoderWidth: encoderW, encoderHeight: encoderH,
            displayWidth: contentW, displayHeight: contentH);

        Assert.AreEqual(encoderW * encoderH * 4, output.Length, "output must be encoder-sized");

        // Corner pixels (outside the content area) must be black (padding).
        var topLeft = GetPixel(output, encoderW, 0, 0);
        Assert.AreEqual((byte)0, topLeft.R, "top-left corner must be black (padding)");
        Assert.AreEqual((byte)0, topLeft.G, "top-left corner must be black (padding)");
        Assert.AreEqual((byte)0, topLeft.B, "top-left corner must be black (padding)");

        // Center pixel (inside content area) must be non-black (red content).
        var centerX = (encoderW - contentW) / 2 + contentW / 2;
        var centerY = (encoderH - contentH) / 2 + contentH / 2;
        var center = GetPixel(output, encoderW, centerX, centerY);
        Assert.IsTrue(center.R > 128 && center.G < 32 && center.B < 32,
            $"center pixel should be red content; got B={center.B} G={center.G} R={center.R}");
    }

    [TestMethod]
    public void ProcessFrame_FullSizeNoLetterbox_FastPath()
    {
        // When source == encoder size and crop covers the whole frame, ProcessFrame must
        // return the original pixel array (fast path — no copy).
        const int w = 640, h = 480;
        var source = MakeSolidFrame(w, h, b: 0, g: 255, r: 0); // green

        var output = UiRecordingService.ProcessFrame(
            source, w, h,
            cropX: 0, cropY: 0, cropW: w, cropH: h,
            encoderWidth: w, encoderHeight: h,
            displayWidth: w, displayHeight: h);

        Assert.AreSame(source, output, "fast path must return the original array without copying");
    }

    [TestMethod]
    public void ProcessFrame_CropExtractsSubregion_ContentCentered()
    {
        // A 100×100 source with a 20×20 blue crop at (40,40); encoder is 80×80.
        // The blue subregion should appear centered in the output; the rest is black.
        const int srcW = 100, srcH = 100;
        const int cropX = 40, cropY = 40, cropW = 20, cropH = 20;
        const int encW = 80, encH = 80;

        // Source: black background except the crop region which is blue.
        var source = new byte[srcW * srcH * 4]; // all black
        for (var y = cropY; y < cropY + cropH; y++)
        {
            for (var x = cropX; x < cropX + cropW; x++)
            {
                var offset = (y * srcW + x) * 4;
                source[offset] = 255; // B
                source[offset + 1] = 0; // G
                source[offset + 2] = 0; // R
                source[offset + 3] = 255;
            }
        }

        var output = UiRecordingService.ProcessFrame(
            source, srcW, srcH,
            cropX, cropY, cropW, cropH,
            encoderWidth: encW, encoderHeight: encH,
            displayWidth: cropW, displayHeight: cropH);

        Assert.AreEqual(encW * encH * 4, output.Length);

        // Corners must be black (padding).
        var corner = GetPixel(output, encW, 0, 0);
        Assert.AreEqual((byte)0, corner.R);
        Assert.AreEqual((byte)0, corner.G);
        Assert.AreEqual((byte)0, corner.B);
    }

    [TestMethod]
    public void ProcessFrame_CropOutOfBounds_Clamped()
    {
        // If crop + cropW would exceed sourceWidth, the frame must clamp rather than
        // throw an exception or produce garbage.
        const int srcW = 50, srcH = 50;
        var source = MakeSolidFrame(srcW, srcH, b: 0, g: 0, r: 128); // dark red

        // Intentionally over-wide crop — must not throw.
        Exception? ex = null;
        try
        {
            UiRecordingService.ProcessFrame(
                source, srcW, srcH,
                cropX: 40, cropY: 40, cropW: 30, cropH: 30, // 40+30=70 > 50 — clamped
                encoderWidth: 64, encoderHeight: 64,
                displayWidth: 20, displayHeight: 20);
        }
        catch (Exception caught)
        {
            ex = caught;
        }
        Assert.IsNull(ex, $"out-of-bounds crop must be clamped, not throw; got: {ex?.Message}");
    }

    [TestMethod]
    public void ProcessFrame_ThinAspect_PaddingIsBlack()
    {
        // A very wide (80×8) source letterboxed into 80×64 encoder.
        // Padding rows above and below the content must be black.
        const int srcW = 80, srcH = 8;
        const int encW = 80, encH = 64;
        const int dispW = 80, dispH = 8;

        var source = MakeSolidFrame(srcW, srcH, b: 255, g: 0, r: 0); // blue content

        var output = UiRecordingService.ProcessFrame(
            source, srcW, srcH,
            cropX: 0, cropY: 0, cropW: srcW, cropH: srcH,
            encoderWidth: encW, encoderHeight: encH,
            displayWidth: dispW, displayHeight: dispH);

        Assert.AreEqual(encW * encH * 4, output.Length);

        // Top row (padding) must be black.
        var topRow = GetPixel(output, encW, encW / 2, 0);
        Assert.AreEqual((byte)0, topRow.B, "top padding must be black (B)");
        Assert.AreEqual((byte)0, topRow.G, "top padding must be black (G)");
        Assert.AreEqual((byte)0, topRow.R, "top padding must be black (R)");
    }

    [TestMethod]
    public void ProcessFrame_WholWindowWgc_GrownFrame_FullFrameVsStaleSubrect()
    {
        // H2: for whole-window WGC, after a window resize the crop source must be the
        // FULL current frame (sw×sh), not the stale initial srcWidth×srcHeight sub-rect.
        // We demonstrate by putting content ONLY in the grown region (outside the initial
        // bounds) and verifying that the fixed (full-frame) crop captures it.
        const int initialW = 100, initialH = 100;
        const int grownW = 200, grownH = 200;
        var (encW, encH, dispW, dispH) = UiRecordingService.ComputeTargetSize(initialW, initialH, 0);

        // Source: black in the top-left 100×100 region, blue in the grown (>100) region.
        var source = new byte[grownW * grownH * 4];
        for (var y = 0; y < grownH; y++)
        {
            for (var x = 0; x < grownW; x++)
            {
                if (x >= initialW || y >= initialH)
                {
                    var off = (y * grownW + x) * 4;
                    source[off] = 255; // B
                    source[off + 3] = 255;
                }
            }
        }

        // Stale crop (the bug): only the black top-left 100×100 sub-rect.
        var staleOutput = UiRecordingService.ProcessFrame(
            source, grownW, grownH,
            0, 0, initialW, initialH,
            encW, encH, dispW, dispH);

        // Fixed crop (H2 fix): full 200×200 current frame.
        var fixedOutput = UiRecordingService.ProcessFrame(
            source, grownW, grownH,
            0, 0, grownW, grownH,
            encW, encH, dispW, dispH);

        // Stale crop: the content (blue) is in the grown region — entirely missed.
        var staleCenter = GetPixel(staleOutput, encW, encW / 2, encH / 2);
        Assert.AreEqual((byte)0, staleCenter.B,
            "stale-crop output must be all black (grown content missed by stale sub-rect)");

        // Fixed crop: full frame scaled into encoder → blue from grown region must appear.
        var hasBlue = false;
        for (var i = 0; i < fixedOutput.Length; i += 4)
        {
            if (fixedOutput[i] > 128) { hasBlue = true; break; }
        }
        Assert.IsTrue(hasBlue,
            "fixed crop must include blue content from the grown frame region");
    }

    [TestMethod]
    public void ProcessFrame_ClosedItemDrain_ProducesValidEncoderSizeOutput()
    {
        // M8: when IsClosed fires and the cached frame is drained before break,
        // ProcessFrame must produce valid encoder-sized output (not empty/zero).
        const int srcW = 64, srcH = 64;
        var (encW, encH, dispW, dispH) = UiRecordingService.ComputeTargetSize(srcW, srcH, 0);
        var source = MakeSolidFrame(srcW, srcH, b: 0, g: 180, r: 0); // green

        var output = UiRecordingService.ProcessFrame(
            source, srcW, srcH, 0, 0, srcW, srcH,
            encW, encH, dispW, dispH);

        Assert.AreEqual(encW * encH * 4, output.Length,
            "drained frame must produce full encoder-sized output, not 0 bytes");
        // Output must contain source content (green channel), not be all-zero.
        var hasContent = false;
        for (var i = 0; i < output.Length; i += 4)
        {
            if (output[i + 1] > 0) { hasContent = true; break; }
        }
        Assert.IsTrue(hasContent, "drained frame output must contain source content");
    }
}

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
    [TestMethod]
    [DataRow(0, 100, 640, 480, 640, 480, "cropW")]
    [DataRow(100, 0, 640, 480, 640, 480, "cropH")]
    [DataRow(-5, 100, 640, 480, 640, 480, "cropW")]
    [DataRow(100, 100, 0, 480, 640, 480, "encoderWidth")]
    [DataRow(100, 100, 640, 0, 640, 480, "encoderHeight")]
    [DataRow(100, 100, 640, 480, 0, 480, "displayWidth")]
    [DataRow(100, 100, 640, 480, 640, 0, "displayHeight")]
    [DataRow(100, 100, 64, 64, 128, 64, "displayWidth")]
    [DataRow(100, 100, 64, 64, 64, 128, "displayHeight")]
    public void ComputeFittedContentRect_InvalidDimensions_Throws(
        int cropW, int cropH, int encW, int encH, int dispW, int dispH, string expectedParameter)
    {
        // These are pixel dimensions, so none can be zero. The crop values are divisors, and because
        // the division is floating point a zero used to yield Infinity rather than an exception --
        // the caller silently got a one-pixel rect back instead of an error. A display dimension
        // larger than its encoder centers to a negative offset, which the caller then hands to
        // Buffer.BlockCopy.
        var exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => CaptureGeometry.ComputeFittedContentRect(cropW, cropH, encW, encH, dispW, dispH));

        Assert.AreEqual(expectedParameter, exception.ParamName);
    }

    [TestMethod]
    public void ComputeTargetSize_TinyInput_PadsToEncoderMinimum()
    {
        // A 32×24 element region is below the MF H.264 encoder minimum (64×64).
        // The encoder dimensions must be ≥ the minimum; the display dimensions are the
        // natural (aspect-preserved) size. Both dims must be even.
        var (encW, encH, dispW, dispH) = UiRecordingService.ComputeTargetSize(32, 24, 0);
        Assert.IsTrue(encW >= 64, $"encoder width ({encW}) must be ≥ 64 (MF H.264 minimum)");
        Assert.IsTrue(encH >= 64, $"encoder height ({encH}) must be ≥ 64 (MF H.264 minimum)");
        Assert.AreEqual(0, encW % 2, "encoder width must be even");
        Assert.AreEqual(0, encH % 2, "encoder height must be even");
        Assert.IsTrue(dispW <= encW, "display width must not exceed encoder width");
        Assert.IsTrue(dispH <= encH, "display height must not exceed encoder height");
        // Aspect ratio of display region should match input (32/24 ≈ 1.333).
        var inputRatio = 32.0 / 24.0;
        var displayRatio = (double)dispW / dispH;
        Assert.IsTrue(Math.Abs(inputRatio - displayRatio) < 0.15, $"display aspect ratio ({displayRatio:F3}) must be close to input ({inputRatio:F3})");
    }

    [TestMethod]
    public void ComputeTargetSize_LargeInput_NoUnnecessaryPadding()
    {
        // A large element (800×600) must pass through without letterbox inflation.
        var (encW, encH, dispW, dispH) = UiRecordingService.ComputeTargetSize(800, 600, 0);
        Assert.AreEqual(dispW, encW, "large frame must not be padded (encoder == display)");
        Assert.AreEqual(dispH, encH, "large frame must not be padded (encoder == display)");
    }

    [TestMethod]
    public void ScreenRecord_MaxEdge_UsesDownscaledCaptureAllocation()
    {
        var (encW, encH, dispW, dispH) = UiRecordingService.ComputeTargetSize(7680, 2160, 1280);
        var (_, _, fitW, fitH) = CaptureGeometry.ComputeFittedContentRect(
            cropW: 7680,
            cropH: 2160,
            encoderWidth: encW,
            encoderHeight: encH,
            displayWidth: dispW,
            displayHeight: dispH);

        Assert.IsTrue(fitW <= 1280, "screen capture readback width must honor --max-edge before allocating pixels");
        Assert.IsTrue(fitH <= 1280, "screen capture readback height must honor --max-edge before allocating pixels");
        Assert.IsTrue(fitW * fitH < 7680 * 2160,
            "screen capture readback must be bounded by the downscaled frame rather than the native desktop frame");
    }

    [TestMethod]
    public void ComputeTargetSize_EvenDimensions_Always()
    {
        // Odd input values must always yield even encoder and display dimensions.
        var (encW, encH, dispW, dispH) = UiRecordingService.ComputeTargetSize(33, 25, 0);
        Assert.AreEqual(0, encW % 2, "encoder width must always be even");
        Assert.AreEqual(0, encH % 2, "encoder height must always be even");
        Assert.AreEqual(0, dispW % 2, "display width must always be even");
        Assert.AreEqual(0, dispH % 2, "display height must always be even");
    }

    [TestMethod]
    public void ComputeTargetSize_ThinAspect_DownscaleRoundsNotFloors()
    {
        // 300×10 with maxEdge=100: scale=0.333, ideal displayH=3.33.
        // Floor would give 2 (50:1 aspect — huge distortion from 30:1).
        // Nearest-even round gives 4 (25:1 aspect — much closer to 30:1).
        var (encW, encH, dispW, dispH) = UiRecordingService.ComputeTargetSize(300, 10, 100);
        Assert.AreEqual(0, dispW % 2, "display width must be even");
        Assert.AreEqual(0, dispH % 2, "display height must be even");
        Assert.IsTrue(dispH >= 4, $"nearest-even round of 3.33 must be 4, not floored to 2; got {dispH}");

        var inputRatio = 300.0 / 10.0;
        var displayRatio = (double)dispW / dispH;
        var aspectError = Math.Abs(displayRatio - inputRatio) / inputRatio;
        Assert.IsTrue(aspectError < 0.20,
            $"aspect error ({aspectError:P1}) must be < 20% with nearest-even rounding; got {dispW}×{dispH}");
    }

    [TestMethod]
    public void ComputeTargetSize_ThinAspect_DownscaleDimsAreEvenAndAboveMinimum()
    {
        // Verify encoder dims are at or above the H.264 minimum and all dims are even.
        var (encW, encH, dispW, dispH) = UiRecordingService.ComputeTargetSize(300, 10, 100);
        Assert.IsTrue(encW >= 64, $"encoder width ({encW}) must be ≥ 64 (MF H.264 minimum)");
        Assert.IsTrue(encH >= 64, $"encoder height ({encH}) must be ≥ 64 (MF H.264 minimum)");
        Assert.AreEqual(0, encW % 2, "encoder width must be even");
        Assert.AreEqual(0, encH % 2, "encoder height must be even");
        Assert.AreEqual(0, dispW % 2, "display width must be even");
        Assert.AreEqual(0, dispH % 2, "display height must be even");
    }

    [TestMethod]
    public async Task Record_MaxEdgeOne_ReturnsInvalidArguments()
    {
        // --max-edge 1 is non-zero and below the 64px encoder minimum — must be rejected.
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--max-edge", "1", "--json"]);
        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "--max-edge must be 0");
        StringAssert.Contains(ConsoleStdErr.ToString(), "64");
    }

    [TestMethod]
    public async Task Record_MaxEdge64_IsValid()
    {
        // --max-edge 64 equals the encoder minimum and must be accepted.
        var outputPath = Path.Combine(_tempDirectory.FullName, "maxedge64.mp4");
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--max-edge", "64", "--duration-sec", "1", "-o", outputPath, "--json"]);
        Assert.AreEqual(0, exitCode, "--max-edge 64 (encoder minimum) must be accepted");
    }

    [TestMethod]
    public async Task Record_MaxEdgeZero_IsUnbounded()
    {
        // --max-edge 0 means no downscale (unbounded) and must be accepted.
        var outputPath = Path.Combine(_tempDirectory.FullName, "maxedge0.mp4");
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--max-edge", "0", "--duration-sec", "1", "-o", outputPath, "--json"]);
        Assert.AreEqual(0, exitCode, "--max-edge 0 (unbounded) must be accepted");
    }

    [TestMethod]
    public async Task Record_MaxEdgeOne_NoTarget_ReturnsInvalidArguments()
    {
        // --max-edge 1 (non-zero, below 64) with NO -a/-w must yield invalid_arguments,
        // not missing_app. Before the fix, the missing-target check ran first.
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--max-edge", "1", "--json"]);
        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "--max-edge must be 0",
            "error must be invalid_arguments, not missing_app");
        StringAssert.Contains(ConsoleStdErr.ToString(), "64");
    }

    [TestMethod]
    public async Task Record_MaxEdge63_NoTarget_ReturnsInvalidArguments()
    {
        // --max-edge 63 (non-zero, one below the 64px minimum) with NO target must yield
        // invalid_arguments — the numeric validation must fire before the target check.
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--max-edge", "63", "--json"]);
        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "--max-edge must be 0",
            "error must be invalid_arguments, not missing_app");
    }

    [TestMethod]
    public async Task Record_MaxEdge64_NoTarget_ReturnsMissingApp()
    {
        // --max-edge 64 (valid — encoder minimum) with NO target must pass numeric validation
        // and then fail with missing_app, not invalid_arguments.
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--max-edge", "64", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.IsFalse(ConsoleStdErr.ToString().Contains("--max-edge must be 0"),
            "--max-edge 64 is valid; error must be missing_app, not invalid_arguments");
    }

    [TestMethod]
    public async Task Record_MaxEdgeZero_NoTarget_ReturnsMissingApp()
    {
        // --max-edge 0 (unbounded) with NO target must pass numeric validation and then fail
        // with missing_app, not invalid_arguments.
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--max-edge", "0", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.IsFalse(ConsoleStdErr.ToString().Contains("--max-edge must be 0"),
            "--max-edge 0 is valid; error must be missing_app, not invalid_arguments");
    }

    [TestMethod]
    public void ComputeTargetSize_MaxEdgeOdd_LongEdgeDoesNotExceedCap()
    {
        // L1: if --max-edge=99, the longest display edge must be ≤ 99.
        // EvenFloor(99) = 98, so display long edge is 98, not 100.
        var (_, _, dispW, dispH) = UiRecordingService.ComputeTargetSize(300, 100, 99);
        var longest = Math.Max(dispW, dispH);
        Assert.IsTrue(longest <= 99, $"longest display edge ({longest}) must be ≤ maxEdge (99)");
        Assert.AreEqual(0, dispW % 2, "displayW must be even");
        Assert.AreEqual(0, dispH % 2, "displayH must be even");
    }

    [TestMethod]
    public void ComputeTargetSize_MaxEdgeEven_LongEdgeExactlyCap()
    {
        // Even max-edge: the long edge should land exactly on (or below) the cap.
        var (_, _, dispW, dispH) = UiRecordingService.ComputeTargetSize(400, 300, 100);
        var longest = Math.Max(dispW, dispH);
        Assert.IsTrue(longest <= 100, $"longest display edge ({longest}) must be ≤ 100");
        Assert.AreEqual(0, dispW % 2);
        Assert.AreEqual(0, dispH % 2);
    }

    [TestMethod]
    public void ComputeTargetSize_ThinAspect_LongEdgeNeverExceedsMaxEdge()
    {
        // 300×10 with maxEdge=100: long edge is 300. After scale = 100/300 ≈ 0.333,
        // displayW must be ≤ 100 (not 100 rounded up).
        var (_, _, dispW, dispH) = UiRecordingService.ComputeTargetSize(300, 10, 100);
        Assert.IsTrue(dispW <= 100, $"displayW ({dispW}) must be ≤ maxEdge (100)");
        Assert.IsTrue(dispH <= 100, $"displayH ({dispH}) must be ≤ maxEdge (100)");
        Assert.AreEqual(0, dispW % 2);
        Assert.AreEqual(0, dispH % 2);
    }

    [TestMethod]
    public void ComputeTargetSize_ExactSquare_NearMaxEdge_BothEdgesWithinCap()
    {
        // M9: 100×100 with maxEdge=99. scale=0.99, EvenRound(99)=100 would exceed the cap.
        // Both display edges must be ≤ 99 after the fix clamps the short edge too.
        var (_, _, dispW, dispH) = UiRecordingService.ComputeTargetSize(100, 100, 99);
        Assert.IsTrue(dispW <= 99, $"dispW ({dispW}) must be ≤ maxEdge (99) for exact-square input");
        Assert.IsTrue(dispH <= 99, $"dispH ({dispH}) must be ≤ maxEdge (99) for exact-square input");
        Assert.AreEqual(0, dispW % 2, "dispW must be even");
        Assert.AreEqual(0, dispH % 2, "dispH must be even");
    }

    [TestMethod]
    public void ComputeTargetSize_NearSquare_MaxEdge_ShortEdgeDoesNotExceedCap()
    {
        // M9: 100×98 with maxEdge=99. Long edge=100 → scale=0.99; short edge EvenRound(97.02)=98.
        // Short edge 98 ≤ 99 with fix; long edge EvenFloor(99)=98 ≤ 99. Both must be ≤ 99.
        var (_, _, dispW, dispH) = UiRecordingService.ComputeTargetSize(100, 98, 99);
        Assert.IsTrue(dispW <= 99, $"dispW ({dispW}) must be ≤ 99");
        Assert.IsTrue(dispH <= 99, $"dispH ({dispH}) must be ≤ 99");
        Assert.AreEqual(0, dispW % 2);
        Assert.AreEqual(0, dispH % 2);
    }

    [TestMethod]
    public void ComputeTargetSize_ExactSquarePlusTen_OddMaxEdge_BothEdgesWithinCap()
    {
        // M9: Broader invariant: max(dispW, dispH) ≤ maxEdge for ALL inputs with a capped maxEdge.
        // Test several square and near-square sizes with odd maxEdge values.
        int[] sizes = [50, 100, 101, 200, 255, 1000];
        int[] caps = [49, 98, 99, 100, 199, 253];
        for (var i = 0; i < sizes.Length; i++)
        {
            var (_, _, dispW, dispH) = UiRecordingService.ComputeTargetSize(sizes[i], sizes[i], caps[i]);
            var longest = Math.Max(dispW, dispH);
            Assert.IsTrue(longest <= caps[i],
                $"square {sizes[i]}×{sizes[i]} maxEdge={caps[i]}: longest ({longest}) must be ≤ {caps[i]}");
            Assert.AreEqual(0, dispW % 2, "dispW must be even");
            Assert.AreEqual(0, dispH % 2, "dispH must be even");
        }
    }
}

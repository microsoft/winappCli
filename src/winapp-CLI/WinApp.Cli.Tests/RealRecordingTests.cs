// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

extern alias uia;

using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;
using HWND = uia::Windows.Win32.Foundation.HWND;
using WinApp.Cli.Services;

using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Tests;

/// <summary>
/// Real-recording coverage: drives the CLI recording service against a live in-process window,
/// with frame capture and the video encoder swapped for deterministic fakes.
/// </summary>
/// <remarks>
/// Not parallelized: these tests drive a live desktop window and mutate the process-wide encoder
/// and frame-writer seams, so concurrent cases would interfere with each other.
/// </remarks>
[TestClass]
[DoNotParallelize]
public partial class RealRecordingTests
{
    /// <summary>
    /// Restores the process-wide encoder and frame-writer seams after every test. These are static,
    /// so a test that leaves a fake installed corrupts every later test in the assembly — including
    /// ones in other classes that expect the real encoder.
    /// </summary>
    [TestCleanup]
    public void ResetRecordingSeams()
    {
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, fps, bitrate)
            => new Mp4SinkWriterEncoder(path, width, height, fps, bitrate, overwriteExisting: false);
        RecordFrameBundleWriter.ResetTestSeams();
        UiRecordingService.ResetWindowStateSeams();
        UiRecordingService.s_trimVideo = Mp4Trimmer.Trim;
    }

    /// <summary>Opaque BGRA pixels of one gray level.</summary>
    private static byte[] OpaqueGray(int width, int height, byte level)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (level, level, level, 255);
        }
        return pixels;
    }

    /// <summary>
    /// A 64x64 window whose frames carry a 16-pixel border, and, once the first frame is recorded when
    /// <paramref name="menuOpens"/>, a 64x16 menu directly above the window, inside the border.
    /// </summary>
    private static WindowCaptureSession BorderedWindow(Func<bool> menuOpen, int margin = 16)
        => new(new FakeFrameGrabber(OpaqueGray(64, 64, 0x44), 64, 64), () => new(0, 0, 64, 64),
            () => menuOpen() ? [new(42, new(0, -16, 64, 0))] : [],
            _ => new FakeFrameGrabber(OpaqueGray(64, 16, 0xEE), 64, 16), margins: new(margin, margin, margin, margin));

    [TestMethod]
    public async Task RecordAsync_AutoPadding_MenuMidRecording_JoinsWindowAndPaddedFramesThenTrims()
    {
        using var fx = new UiaTestFixture();
        var encoders = new List<FakeVideoEncoder>();
        using var grabber = BorderedWindow(() => encoders.Count > 0 && encoders[0].FramesWritten > 0);
        var capture = new FakeWindowCapture { Supported = true, StartGrabberCallback = (_, _) => grabber };
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
        {
            var encoder = new FakeVideoEncoder(path, width, height);
            encoders.Add(encoder);
            return encoder;
        };
        (RecordTrimSources Sources, RecordTrimPlan Plan)? trim = null;
        UiRecordingService.s_trimVideo = (sources, destination, plan, _) =>
        {
            trim = (sources, plan);
            File.WriteAllBytes(destination, [9, 9]);
        };

        var result = await NewRecordingService(NewAutomation(), capture).RecordAsync(SessionFor(fx), null, new RecordOptions
        {
            OutputPath = ScratchOutput("auto-menu.mp4"),
            DurationSec = 2,
            Fps = 2,
        }, CancellationToken.None);

        Assert.AreEqual((UiRecordingService.AutoPopupMargin, false), capture.RequestedBorder);
        Assert.HasCount(2, encoders);
        Assert.AreEqual((64, 64), (encoders[0].Width, encoders[0].Height), "the window alone, for frames before the menu");
        Assert.AreEqual((96, 96), (encoders[1].Width, encoders[1].Height), "the padded frames, once the menu opened");
        Assert.IsNotNull(trim);
        Assert.AreEqual(1, trim.Value.Sources.PaddedStartFrame);
        Assert.AreEqual(encoders[0].FramesWritten - 1, encoders[1].FramesWritten);
        Assert.AreEqual((16, 0, 64, 80), (trim.Value.Plan.CropX, trim.Value.Plan.CropY, trim.Value.Plan.CropWidth, trim.Value.Plan.CropHeight));
        Assert.AreEqual((64, 80), (result.Width, result.Height), "the window plus the menu above it");
        Assert.AreEqual(2, result.FileSize, "the joined video replaces the window recording");
    }

    [TestMethod]
    public async Task RecordAsync_AutoPadding_NoMenu_RecordsExactlyTheWindowWithoutReencoding()
    {
        using var fx = new UiaTestFixture();
        var encoders = new List<FakeVideoEncoder>();
        using var grabber = BorderedWindow(() => false);
        var capture = new FakeWindowCapture { Supported = true, StartGrabberCallback = (_, _) => grabber };
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
        {
            var encoder = new FakeVideoEncoder(path, width, height);
            encoders.Add(encoder);
            return encoder;
        };
        UiRecordingService.s_trimVideo = (_, _, _, _) => throw new AssertFailedException("nothing needs re-encoding");

        var result = await NewRecordingService(NewAutomation(), capture).RecordAsync(SessionFor(fx), null, new RecordOptions
        {
            OutputPath = ScratchOutput("auto-no-menu.mp4"),
            DurationSec = 2,
            Fps = 2,
        }, CancellationToken.None);

        Assert.AreEqual((64, 64), (result.Width, result.Height));
        var encoder = encoders.Single();
        Assert.IsTrue(encoder.LastFrame!.Chunk(4).All(p => p[0] == 0x44), "every pixel is the window, none is border");
    }

    [TestMethod]
    public async Task RecordAsync_FixedPadding_KeepsTheBorderInTheBackgroundColor()
    {
        // --padding 16 --background #00FF00: every frame is the window plus 16 green pixels on each side, and the
        // menu that opens above the window is cut off at the top edge of that border.
        using var fx = new UiaTestFixture();
        var encoders = new List<FakeVideoEncoder>();
        using var grabber = BorderedWindow(() => encoders.Count > 0 && encoders[0].FramesWritten > 0);
        var capture = new FakeWindowCapture { Supported = true, StartGrabberCallback = (_, _) => grabber };
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
        {
            var encoder = new FakeVideoEncoder(path, width, height);
            encoders.Add(encoder);
            return encoder;
        };
        UiRecordingService.s_trimVideo = (_, _, _, _) => throw new AssertFailedException("fixed padding is never re-encoded");

        var result = await NewRecordingService(NewAutomation(), capture).RecordAsync(SessionFor(fx), null, new RecordOptions
        {
            OutputPath = ScratchOutput("green-border.mp4"),
            DurationSec = 2,
            Fps = 2,
            Padding = 16,
            BackgroundColor = 0x00FF00,
        }, CancellationToken.None);

        Assert.AreEqual((16, true), capture.RequestedBorder);
        Assert.AreEqual((96, 96), (result.Width, result.Height));
        var last = encoders.Single().LastFrame!;
        Assert.AreSequenceEqual(new byte[] { 0, 255, 0, 255 }, last[0..4], "corner is background");
        Assert.AreEqual((byte)0xEE, last[(8 * 96 + 40) * 4], "menu, inside the border above the window");
        Assert.AreEqual((byte)0x44, last[(48 * 96 + 48) * 4], "window");
    }

    [TestMethod]
    public async Task RecordAsync_PaddingZero_RecordsOnlyTheWindow()
    {
        using var fx = new UiaTestFixture();
        using var grabber = new WindowCaptureSession(new FakeFrameGrabber(OpaqueGray(64, 64, 0x44), 64, 64),
            () => new(0, 0, 64, 64), () => [], _ => throw new AssertFailedException("no popups"));
        var capture = new FakeWindowCapture { Supported = true, StartGrabberCallback = (_, _) => grabber };
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) => new FakeVideoEncoder(path, width, height);

        var result = await NewRecordingService(NewAutomation(), capture).RecordAsync(SessionFor(fx), null, new RecordOptions
        {
            OutputPath = ScratchOutput("window-only.mp4"),
            DurationSec = 1,
            Fps = 1,
            Padding = 0,
        }, CancellationToken.None);

        Assert.IsNull(capture.RequestedBorder, "no border is asked for");
        Assert.AreEqual((64, 64), (result.Width, result.Height));
    }

    [TestMethod]
    public async Task RecordAsync_AutoPadding_JoinFails_KeepsTheWindowRecordingAndWarns()
    {
        using var fx = new UiaTestFixture();
        var encoders = new List<FakeVideoEncoder>();
        using var grabber = BorderedWindow(() => encoders.Count > 0 && encoders[0].FramesWritten > 0);
        var capture = new FakeWindowCapture { Supported = true, StartGrabberCallback = (_, _) => grabber };
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
        {
            var encoder = new FakeVideoEncoder(path, width, height);
            encoders.Add(encoder);
            return encoder;
        };
        UiRecordingService.s_trimVideo = (_, _, _, _) => throw new InvalidOperationException("decoder unavailable");
        var output = ScratchOutput("join-failed.mp4");

        var result = await NewRecordingService(NewAutomation(), capture).RecordAsync(SessionFor(fx), null, new RecordOptions
        {
            OutputPath = output,
            DurationSec = 2,
            Fps = 2,
        }, CancellationToken.None);

        Assert.AreEqual((64, 64), (result.Width, result.Height));
        Assert.IsTrue(File.Exists(output));
        Assert.IsNotNull(result.Warnings);
        Assert.Contains("could not be widened", result.Warnings.Single());
        Assert.Contains("decoder unavailable", result.Warnings.Single());
        Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(output)!, "*.padded.mp4"), "the padded recording is cleaned up");
    }

    [TestMethod]
    public async Task RecordAsync_AutoPadding_WindowResized_RecordsThePaddedFramesSoNothingIsCut()
    {
        // The window grows from 64x64 to 96x64 after the first frame. The window-only crop from the first
        // frame would cut off its right side, so the recorder must also record padded frames and join them.
        using var fx = new UiaTestFixture();
        var encoders = new List<FakeVideoEncoder>();
        var grown = new ResizingFrameGrabber(OpaqueGray(64, 64, 0x44), 64, OpaqueGray(96, 64, 0x44), 96, 64,
            () => encoders.Count > 0 && encoders[0].FramesWritten > 0);
        using var grabber = new WindowCaptureSession(grown, () => grown.Grown ? new(0, 0, 96, 64) : new(0, 0, 64, 64),
            () => [], _ => throw new AssertFailedException("no popups"), margins: new(16, 16, 16, 16));
        var capture = new FakeWindowCapture { Supported = true, StartGrabberCallback = (_, _) => grabber };
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
        {
            var encoder = new FakeVideoEncoder(path, width, height);
            encoders.Add(encoder);
            return encoder;
        };
        RecordTrimSources? sources = null;
        UiRecordingService.s_trimVideo = (s, destination, _, _) =>
        {
            sources = s;
            File.WriteAllBytes(destination, [9, 9]);
        };

        await NewRecordingService(NewAutomation(), capture).RecordAsync(SessionFor(fx), null, new RecordOptions
        {
            OutputPath = ScratchOutput("auto-resize.mp4"),
            DurationSec = 2,
            Fps = 2,
        }, CancellationToken.None);

        Assert.HasCount(2, encoders, "a resize must start the padded recording");
        Assert.IsNotNull(sources);
        Assert.AreEqual(1, sources.PaddedStartFrame);
    }

    [TestMethod]
    public async Task RecordAsync_AutoPadding_PaddedEncoderCannotStart_KeepsTheWindowRecording()
    {
        using var fx = new UiaTestFixture();
        var encoders = new List<FakeVideoEncoder>();
        using var grabber = BorderedWindow(() => encoders.Count > 0 && encoders[0].FramesWritten > 0);
        var capture = new FakeWindowCapture { Supported = true, StartGrabberCallback = (_, _) => grabber };
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
        {
            if (encoders.Count > 0)
            {
                throw new InvalidOperationException("frame too large");
            }
            var encoder = new FakeVideoEncoder(path, width, height);
            encoders.Add(encoder);
            return encoder;
        };
        UiRecordingService.s_trimVideo = (_, _, _, _) => throw new AssertFailedException("nothing to join");

        var result = await NewRecordingService(NewAutomation(), capture).RecordAsync(SessionFor(fx), null, new RecordOptions
        {
            OutputPath = ScratchOutput("padded-cannot-start.mp4"),
            DurationSec = 2,
            Fps = 2,
        }, CancellationToken.None);

        Assert.AreEqual((64, 64), (result.Width, result.Height));
        Assert.AreEqual(4, encoders.Single().FramesWritten, "the window kept recording");
        Assert.Contains("could not be widened", result.Warnings!.Single());
    }

    [TestMethod]
    [DataRow(null, 96, DisplayName = "auto: window crop sized like --padding 0")]
    [DataRow(16, 96, DisplayName = "fixed: --max-edge covers the border too")]
    public async Task RecordAsync_MaxEdge_IsTheLongestEdgeOfTheFinalVideo(int? padding, int maxEdge)
    {
        // A 128x96 window with a 16 px border and --max-edge 64. With auto padding and no popup, the video is
        // the window scaled to 64x48 with no border pixels; with a fixed border, the whole frame fits in 64.
        using var fx = new UiaTestFixture();
        var encoders = new List<FakeVideoEncoder>();
        using var grabber = new WindowCaptureSession(new FakeFrameGrabber(OpaqueGray(128, 96, 0x44), 128, 96),
            () => new(0, 0, 128, 96), () => [], _ => throw new AssertFailedException("no popups"),
            margins: new(16, 16, 16, 16));
        var capture = new FakeWindowCapture { Supported = true, StartGrabberCallback = (_, _) => grabber };
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
        {
            var encoder = new FakeVideoEncoder(path, width, height);
            encoders.Add(encoder);
            return encoder;
        };

        var result = await NewRecordingService(NewAutomation(), capture).RecordAsync(SessionFor(fx), null, new RecordOptions
        {
            OutputPath = ScratchOutput("max-edge.mp4"),
            DurationSec = 1,
            Fps = 1,
            MaxEdge = maxEdge,
            Padding = padding,
            BackgroundColor = 0x00FF00,
        }, CancellationToken.None);

        Assert.IsLessThanOrEqualTo(maxEdge, Math.Max(result.Width, result.Height));
        if (padding is null)
        {
            Assert.AreEqual((96, 72), (result.Width, result.Height));
            Assert.IsTrue(encoders.Single().LastFrame!.Chunk(4).All(p => p[1] < 0x80),
                "no green border pixel in a window-only video");
        }
    }

    [TestMethod]
    public void ComputeEncodableTargetSize_ScalesDownFramesTheEncoderWouldReject()
    {
        // A 1366x874 window with --padding 1000 is 3366x2874, which H.264 rejects.
        var (w, h, _, _) = UiRecordingService.ComputeEncodableTargetSize(3366, 2874, 0);
        Assert.IsLessThanOrEqualTo(UiRecordingService.MaxEncoderMacroblocks, ((w + 15) / 16) * ((h + 15) / 16));
        Assert.AreEqual(3366.0 / 2874, (double)w / h, 0.01, "aspect ratio is kept");
        var (smallW, smallH, _, _) = UiRecordingService.ComputeEncodableTargetSize(1446, 1034, 0);
        Assert.AreEqual((1446, 1034), (smallW, smallH));

        // A wide frame where a single size-reduction step still rounds up past the limit.
        var (wideW, wideH, _, _) = UiRecordingService.ComputeEncodableTargetSize(5724, 1636, 0);
        Assert.IsLessThanOrEqualTo(UiRecordingService.MaxEncoderMacroblocks, ((wideW + 15) / 16) * ((wideH + 15) / 16));
    }

    [TestMethod]
    public async Task RecordAsync_ElementCrop_IgnoresTheBorderAroundTheWindow()
    {
        // Recording the top-left 64x64 of a 96x96 window whose frames have a 16-pixel border above it. The crop
        // must start at the element, 16 rows down in the frame, not in the empty border.
        using var fx = new UiaTestFixture();
        Assert.IsTrue(NewAutomation().TryGetWindowRect(fx.Hwnd, out var bounds));
        var automation = new FakeUiAutomationService
        {
            FindSingleResult = new UiElement { WindowHandle = fx.Hwnd, X = bounds.Left, Y = bounds.Top, Width = 64, Height = 64 },
        };
        using var grabber = new WindowCaptureSession(
            new FakeFrameGrabber(Enumerable.Repeat((byte)0x44, 96 * 96 * 4).ToArray(), 96, 96), () => new(0, 0, 96, 96),
            () => [], _ => throw new AssertFailedException("no popups"), margins: new(0, 16, 0, 0));
        var capture = new FakeWindowCapture { Supported = true, StartGrabberCallback = (_, _) => grabber };
        FakeVideoEncoder? encoder = null;
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) => encoder = new FakeVideoEncoder(path, width, height);
        UiRecordingService.s_trimVideo = (_, _, _, _) => throw new AssertFailedException("element recordings are not trimmed");

        var result = await NewRecordingService(automation, capture).RecordAsync(SessionFor(fx), "element", new RecordOptions
        {
            OutputPath = ScratchOutput("element-with-border.mp4"),
            DurationSec = 1,
            Fps = 1,
        }, CancellationToken.None);

        Assert.AreEqual((64, 64), (result.Width, result.Height));
        Assert.IsNull(capture.RequestedBorder, "element recordings don't ask for a border");
        Assert.IsNotNull(encoder?.LastFrame);
        Assert.IsTrue(encoder.LastFrame.All(b => b == 0x44), "every pixel is the element, none is border");
    }

    [TestMethod]
    [DataRow(-1, 15, 0, nameof(RecordOptions.DurationSec))]
    [DataRow(10, 0, 0, nameof(RecordOptions.Fps))]
    [DataRow(10, -1, 0, nameof(RecordOptions.Fps))]
    [DataRow(10, 15, -1, nameof(RecordOptions.MaxEdge))]
    [DataRow(10, 15, 1, nameof(RecordOptions.MaxEdge))]
    [DataRow(10, 15, 63, nameof(RecordOptions.MaxEdge))]
    [DataRow(10, 15, 0, nameof(RecordOptions.Padding), -1)]
    [DataRow(10, 15, 0, nameof(RecordOptions.Padding), 10_000)]
    public async Task RecordAsync_InvalidOptions_ThrowsBeforeTouchingTheWindow(
        int durationSec, int fps, int maxEdge, string expectedParameter, int? padding = null)
    {
        // Direct library calls skip the CLI's command-layer validation. Without this guard fps 0
        // divided by zero when computing the frame interval, and a negative duration recorded
        // without end.
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var recording = NewRecordingService(NewAutomation(), capture);
        var output = Path.Join(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"), "invalid.mp4");

        var exception = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => recording.RecordAsync(SessionFor(fx), null, new RecordOptions
            {
                OutputPath = output,
                DurationSec = durationSec,
                Fps = fps,
                MaxEdge = maxEdge,
                Padding = padding,
            }, CancellationToken.None));

        Assert.IsNotNull(exception.ParamName);
        Assert.Contains(expectedParameter, exception.ParamName);
        Assert.IsFalse(File.Exists(output), "Validation must run before any output file is created.");
    }

    [TestMethod]
    public async Task RecordAsync_ExistingOutput_IsNotOverwritten()
    {
        // Video-only recording used to take the clobbering encoder, so running the readme sample
        // twice at the same OutputPath destroyed the first take. The CLI refuses up front, but that
        // guard does not travel with the package.
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var recording = NewRecordingService(NewAutomation(), capture);
        var root = Path.Join(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var output = Path.Join(root, "already-there.mp4");
        File.WriteAllText(output, "the first recording");

        capture.Supported = true;
        capture.StartGrabberCallback = (_, _) => new FakeFrameGrabber(new byte[64 * 64 * 4], 64, 64);

        await Assert.ThrowsExactlyAsync<IOException>(
            () => recording.RecordAsync(SessionFor(fx), null, new RecordOptions
            {
                OutputPath = output,
                DurationSec = 1,
                Fps = 1,
                MaxEdge = 64,
            }, CancellationToken.None));

        Assert.AreEqual("the first recording", File.ReadAllText(output), "the existing recording must survive");
    }

    [TestMethod]
    [DataRow(true, "printwindow")]
    [DataRow(false, "wgc")]
    public async Task RecordAsync_MenuFallback_ReportsOnlyTheRootBackend(bool fallbackRoot, string expectedMode)
    {
        using var fx = new UiaTestFixture();
        var pixels = Enumerable.Repeat((byte)0x44, 64 * 64 * 4).ToArray();
        IFrameGrabber root = fallbackRoot
            ? new WindowCaptureFallback(new HWND(fx.Hwnd), () => (pixels, 64, 64), expectedPid: fx.ProcessId)
            : new FakeFrameGrabber(pixels, 64, 64);
        using var grabber = new WindowCaptureSession(root, () => new(0, 0, 64, 64),
            () => [new(fx.Hwnd, new(0, 0, 64, 64))],
            _ => new WindowCaptureFallback(new HWND(fx.Hwnd), () => (pixels, 64, 64), expectedPid: fx.ProcessId));
        var capture = new FakeWindowCapture { Supported = true, StartGrabberCallback = (_, _) => grabber };
        var recording = NewRecordingService(NewAutomation(), capture);
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) => new FakeVideoEncoder(path, width, height);

        var result = await recording.RecordAsync(SessionFor(fx), null, new RecordOptions
        {
            OutputPath = ScratchOutput("menu-backend.mp4"),
            DurationSec = 1,
            Fps = 1,
            MaxEdge = 64,
        }, CancellationToken.None);

        Assert.AreEqual(expectedMode, result.Mode);
        Assert.AreEqual(1, result.Frames);
        Assert.AreEqual(0, capture.CapturedWithBlankRetry.Count);
    }

    [TestMethod]
    [DataRow(true, "printwindow")]
    [DataRow(false, "wgc")]
    public async Task RecordAsync_RetargetedGrabber_ReportsItsBackend(bool fallbackPopup, string expectedMode)
    {
        using var fx = new UiaTestFixture();
        using var popup = new UiaTestFixture();
        Assert.IsTrue(NewAutomation().TryGetWindowRect(popup.Hwnd, out var bounds));
        var automation = new FakeUiAutomationService
        {
            FindSingleResult = new UiElement
            {
                WindowHandle = popup.Hwnd,
                X = bounds.Left + 1,
                Y = bounds.Top + 1,
                Width = 32,
                Height = 32,
            },
        };
        var pixels = Enumerable.Repeat((byte)0x44, 64 * 64 * 4).ToArray();
        var started = new List<nint>();
        var capture = new FakeWindowCapture
        {
            Supported = true,
            StartGrabberCallback = (hwnd, _) =>
            {
                started.Add(hwnd);
                IFrameGrabber root = (hwnd == popup.Hwnd) == fallbackPopup
                    ? new WindowCaptureFallback(new HWND(hwnd), () => (pixels, 64, 64), expectedPid: fx.ProcessId)
                    : new FakeFrameGrabber(pixels, 64, 64);
                return new WindowCaptureSession(root, () => new(0, 0, 64, 64),
                    () => [], _ => throw new AssertFailedException("No child capture expected."));
            },
        };
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) => new FakeVideoEncoder(path, width, height);

        var result = await NewRecordingService(automation, capture).RecordAsync(SessionFor(fx), "popup", new RecordOptions
        {
            OutputPath = ScratchOutput("retargeted-backend.mp4"),
            DurationSec = 1,
            Fps = 1,
            MaxEdge = 64,
        }, CancellationToken.None);

        Assert.AreSequenceEqual(new nint[] { fx.Hwnd, popup.Hwnd }, started);
        Assert.AreEqual(expectedMode, result.Mode);
        Assert.AreEqual(1, result.Frames);
    }

    [TestMethod]
    public async Task RecordAsync_PopupThatCannotBeCaptured_IsReportedInWarnings()
    {
        using var fx = new UiaTestFixture();
        var pixels = Enumerable.Repeat((byte)0x44, 64 * 64 * 4).ToArray();
        var grabber = new WindowCaptureSession(new FakeFrameGrabber(pixels, 64, 64), () => new(0, 0, 64, 64),
            () => [new(42, new(0, 0, 64, 64))], _ => throw new InvalidOperationException("Popup capture rejected."));
        var capture = new FakeWindowCapture { Supported = true, StartGrabberCallback = (_, _) => grabber };
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) => new FakeVideoEncoder(path, width, height);

        var result = await NewRecordingService(NewAutomation(), capture).RecordAsync(SessionFor(fx), null, new RecordOptions
        {
            OutputPath = ScratchOutput("missing-popup.mp4"),
            DurationSec = 1,
            Fps = 1,
            MaxEdge = 64,
        }, CancellationToken.None);

        Assert.AreEqual("wgc", result.Mode);
        Assert.AreEqual(1, result.Frames);
        Assert.IsNotNull(result.Warnings);
        Assert.HasCount(1, result.Warnings);
        Assert.Contains("HWND 42", result.Warnings[0]);
        Assert.Contains("missing from the recording", result.Warnings[0]);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RecordAsync_StartupReadTemporarilyUnavailable_WaitsForFrame(bool retarget)
    {
        using var fx = new UiaTestFixture();
        using var popup = new UiaTestFixture();
        Assert.IsTrue(NewAutomation().TryGetWindowRect(popup.Hwnd, out var bounds));
        var automation = new FakeUiAutomationService
        {
            FindSingleResult = new UiElement
            {
                WindowHandle = popup.Hwnd,
                X = bounds.Left + 1,
                Y = bounds.Top + 1,
                Width = 32,
                Height = 32,
            },
        };
        var pixels = Enumerable.Repeat((byte)0x44, 64 * 64 * 4).ToArray();
        var racingGrabber = new StartupRaceFrameGrabber(pixels);
        var started = new List<nint>();
        var capture = new FakeWindowCapture
        {
            Supported = true,
            StartGrabberCallback = (hwnd, _) =>
            {
                started.Add(hwnd);
                return !retarget || hwnd == popup.Hwnd
                    ? racingGrabber : new FakeFrameGrabber(pixels, 64, 64);
            },
        };
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) => new FakeVideoEncoder(path, width, height);

        var result = await NewRecordingService(automation, capture).RecordAsync(SessionFor(fx),
            retarget ? "popup" : null, new RecordOptions
            {
                OutputPath = ScratchOutput("ready-frame.mp4"),
                DurationSec = 1,
                Fps = 1,
                MaxEdge = 64,
            }, CancellationToken.None);

        Assert.AreSequenceEqual(retarget ? new nint[] { fx.Hwnd, popup.Hwnd } : [fx.Hwnd], started);
        Assert.AreEqual("wgc", result.Mode);
        Assert.AreEqual(1, result.Frames);
        Assert.IsTrue(racingGrabber.Reads >= 3);
        Assert.IsTrue(racingGrabber.Disposed);
        Assert.AreEqual(0, capture.CapturedWithBlankRetry.Count);
    }

    private sealed class StartupRaceFrameGrabber(byte[] pixels) : IFrameGrabber
    {
        public bool IsClosed => false;
        internal int Reads { get; private set; }
        internal bool Disposed { get; private set; }
        public Task<bool> WaitForFirstFrameAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);
        public (byte[] Pixels, int Width, int Height, long Version)? TryGetLatest()
            => ++Reads <= 2 ? null : (pixels, 64, 64, Reads);
        public void Dispose() => Disposed = true;
    }

    [TestMethod]
    public async Task RecordAsync_WgcSeams_EncodesTimedFramesAndReportsResult()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var output = Path.Combine(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"), "record.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var frame = Enumerable.Repeat((byte)0x44, 80 * 60 * 4).ToArray();
        var grabber = new FakeFrameGrabber(frame, 80, 60);
        FakeVideoEncoder? encoder = null;
        var started = 0;

        capture.Supported = true;
        capture.StartGrabberCallback = (hwnd, fps) =>
        {
            Assert.AreEqual(fx.Hwnd, (nint)hwnd);
            Assert.AreEqual(2, fps);
            return grabber;
        };
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, fps, bitrate) =>
        {
            encoder = new FakeVideoEncoder(path, width, height);
            return encoder;
        };

        var result = await recording.RecordAsync(uiTarget, null, new RecordOptions
        {
            OutputPath = output,
            DurationSec = 1,
            Fps = 2,
            MaxEdge = 64,
            CaptureScreen = false,
        }, CancellationToken.None, _ => started++);

        Assert.AreEqual("wgc", result.Mode);
        Assert.AreEqual(2, result.Frames);
        Assert.AreEqual(64, result.Width);
        Assert.AreEqual(64, result.Height);
        Assert.AreEqual(1, started, "recording should signal readiness exactly once after the first frame");
        Assert.IsNotNull(encoder);
        Assert.AreEqual(2, encoder!.FramesWritten);
        Assert.IsTrue(encoder.Completed);
        Assert.IsTrue(grabber.Disposed);
        Assert.IsTrue(new FileInfo(output).Length > 0);
    }

    [TestMethod]
    public async Task RecordAsync_WgcClosedDrainsLatestFrameBeforeStopping()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var output = Path.Combine(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"), "closed.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var grabber = new FakeFrameGrabber(Enumerable.Repeat((byte)0x55, 64 * 64 * 4).ToArray(), 64, 64)
        {
            IsClosed = true,
        };

        capture.Supported = true;
        capture.StartGrabberCallback = (_, _) => grabber;
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) => new FakeVideoEncoder(path, width, height);

        var result = await recording.RecordAsync(uiTarget, null, new RecordOptions
        {
            OutputPath = output,
            DurationSec = 10,
            Fps = 5,
            MaxEdge = 0,
            CaptureScreen = false,
        }, CancellationToken.None);

        Assert.AreEqual(1, result.Frames, "closed WGC items should drain the cached frame once before finalizing");
        Assert.AreEqual("wgc", result.Mode);
        Assert.IsTrue(grabber.Disposed);
    }

    [TestMethod]
    public async Task RecordAsync_CaptureScreen_UsesConsentedScreenPath()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        // A screen-DC recording captures whatever is genuinely in front, so the engine now refuses to
        // start one unless the target actually reached the foreground. That is a real precondition of
        // this capture mode, not test scaffolding — satisfy it the same way a user would.
        DesktopTestHelpers.ForceForeground(fx.Hwnd);
        if (!ForegroundBelongsToFixture(fx))
        {
            Assert.Inconclusive(
                "The fixture window could not be brought to the foreground on this session, so the "
                + "consented screen path cannot be exercised. Foreground refusal itself is covered by "
                + "CaptureForegroundSafetyTests, which drives the check directly.");
        }

        var output = Path.Combine(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"), "screen.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var screenCalls = 0;
        capture.CaptureScreenOverride = (_, _, _, _, tw, th, _, _) =>
        {
            screenCalls++;
            return Enumerable.Repeat((byte)0x66, tw * th * 4).ToArray();
        };
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) => new FakeVideoEncoder(path, width, height);

        var result = await recording.RecordAsync(uiTarget, null, new RecordOptions
        {
            OutputPath = output,
            DurationSec = 1,
            Fps = 1,
            MaxEdge = 64,
            CaptureScreen = true,
        }, CancellationToken.None);

        Assert.AreEqual("screen", result.Mode);
        Assert.AreEqual(1, result.Frames);
        Assert.AreEqual(1, screenCalls);
    }

    [TestMethod]
    public async Task RecordAsync_PrintWindowFallback_UsesBlankRetryCapture()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var output = Path.Combine(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"), "printwindow.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var windowCalls = 0;
        capture.Supported = false;
        capture.CaptureWindowOverride = (_, width, height) =>
        {
            windowCalls++;
            return Enumerable.Repeat((byte)0x77, width * height * 4).ToArray();
        };
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) => new FakeVideoEncoder(path, width, height);

        var result = await recording.RecordAsync(uiTarget, null, new RecordOptions
        {
            OutputPath = output,
            DurationSec = 1,
            Fps = 1,
            MaxEdge = 64,
            CaptureScreen = false,
        }, CancellationToken.None);

        Assert.AreEqual("printwindow", result.Mode);
        Assert.AreEqual(1, result.Frames);
        Assert.AreEqual(1, windowCalls);
    }

    [TestMethod]
    public async Task RecordAsync_FrameArtifacts_UseTheProcessedMp4FrameStream()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var root = Path.Join(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var output = Path.Join(root, "record.mp4");
        var framesDirectory = Path.Join(root, "record.frames");
        var frame = Enumerable.Repeat((byte)0x88, 80 * 60 * 4).ToArray();

        capture.Supported = true;
        capture.StartGrabberCallback = (_, _) => new FakeFrameGrabber(frame, 80, 60);
        Mp4SinkWriterEncoder.s_createNoClobber =
            (path, width, height, _, _) => new FakeVideoEncoder(path, width, height);

        var result = await recording.RecordAsync(uiTarget, null, new RecordOptions
        {
            OutputPath = output,
            FramesDirectory = framesDirectory,
            DurationSec = 1,
            Fps = 2,
            MaxEdge = 64,
            CaptureScreen = false,
        }, CancellationToken.None);

        Assert.AreEqual(2, result.Frames);
        Assert.IsNotNull(result.FrameArtifacts);
        Assert.AreEqual(2, result.FrameArtifacts.Samples);
        Assert.AreEqual(1, result.FrameArtifacts.Images, "identical processed BGRA frames should share one JPEG");
        Assert.AreEqual(1, result.FrameArtifacts.RepeatedSamples);
        Assert.IsTrue(File.Exists(Path.Join(framesDirectory, "manifest.json")));
        Assert.IsTrue(File.Exists(Path.Join(framesDirectory, "frames.ndjson")));
        Assert.AreEqual(1, Directory.GetFiles(Path.Join(framesDirectory, "frames"), "*.jpg").Length);
    }

    [TestMethod]
    public async Task RecordAsync_FirstMp4WriteFailure_PreservesAcceptedFrameArtifact()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var root = Path.Join(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var framesDirectory = Path.Join(root, "partial.frames");
        capture.Supported = true;
        capture.StartGrabberCallback = (_, _) =>
            new FakeFrameGrabber(new byte[64 * 64 * 4], 64, 64);
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            new FakeVideoEncoder(path, width, height)
            {
                WriteFrameException = new IOException("simulated encoder failure"),
            };

        var exception = await Assert.ThrowsExactlyAsync<RecordPartialOutputException>(
            () => recording.RecordAsync(uiTarget, null, new RecordOptions
            {
                OutputPath = Path.Join(root, "partial.mp4"),
                FramesDirectory = framesDirectory,
                DurationSec = 1,
                Fps = 1,
                MaxEdge = 64,
            }, CancellationToken.None));

        Assert.IsNotNull(exception.FramesDirectory);
        Assert.StartsWith(framesDirectory + ".partial-", exception.FramesDirectory, exception.RecoveryHint);
        var partialDirectory = exception.FramesDirectory;
        var manifest = JsonSerializer.Deserialize<JsonElement>(
            await File.ReadAllTextAsync(Path.Join(partialDirectory, "manifest.json")));
        Assert.AreEqual("partial", manifest.GetProperty("status").GetString());
        Assert.AreEqual(1, manifest.GetProperty("timing").GetProperty("sampleCount").GetInt32());
        Assert.AreEqual(0, manifest.GetProperty("video").GetProperty("frameCount").GetInt32());
        Assert.IsFalse(Directory.Exists(framesDirectory));
    }

    [TestMethod]
    public async Task RecordAsync_Mp4PublicationRaceDoesNotPublishMismatchedFrames()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var root = Path.Join(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var output = Path.Join(root, "winner.mp4");
        var framesDirectory = Path.Join(root, "loser.frames");
        capture.Supported = true;
        capture.StartGrabberCallback = (_, _) =>
            new FakeFrameGrabber(new byte[64 * 64 * 4], 64, 64);
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            new FakeVideoEncoder(path, width, height)
            {
                OnComplete = () =>
                {
                    File.WriteAllText(output, "winning recording");
                    throw new IOException("simulated publication race");
                },
            };

        var exception = await Assert.ThrowsExactlyAsync<RecordPartialOutputException>(
            () => recording.RecordAsync(uiTarget, null, new RecordOptions
            {
                OutputPath = output,
                FramesDirectory = framesDirectory,
                DurationSec = 1,
                Fps = 1,
                MaxEdge = 64,
            }, CancellationToken.None));

        Assert.AreEqual("winning recording", await File.ReadAllTextAsync(output));
        Assert.IsFalse(Directory.Exists(framesDirectory));
        Assert.IsNotNull(exception.FramesDirectory);
        Assert.StartsWith(framesDirectory + ".partial-", exception.FramesDirectory, exception.RecoveryHint);
        Assert.IsTrue(Directory.Exists(exception.FramesDirectory));
    }

    [TestMethod]
    public async Task RecordAsync_FrameAndMp4FailurePreservesNeitherArtifact()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var root = Path.Join(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        capture.Supported = true;
        capture.StartGrabberCallback = (_, _) =>
            new FakeFrameGrabber(new byte[64 * 64 * 4], 64, 64);
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            new FakeVideoEncoder(path, width, height)
            {
                WriteFrameException = new IOException("simulated MP4 failure"),
            };
        RecordFrameBundleWriter.s_create = configuration => new FakeFrameSink
        {
            Configuration = configuration,
            WriteException = new IOException("simulated frame failure"),
        };

        var exception = await Assert.ThrowsExactlyAsync<RecordFrameOutputException>(
            () => recording.RecordAsync(uiTarget, null, new RecordOptions
            {
                OutputPath = Path.Join(root, "failed.mp4"),
                FramesDirectory = Path.Join(root, "failed.frames"),
                DurationSec = 1,
                Fps = 1,
                MaxEdge = 64,
            }, CancellationToken.None));

        Assert.Contains("frame failure", exception.InnerException!.Message);
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(root).Any());
    }

    [TestMethod]
    public async Task RecordAsync_CancellationAfterProcessedFrameCommitsSampleBeforeStopping()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var root = Path.Join(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var output = Path.Join(root, "drained.mp4");
        using var cts = new CancellationTokenSource();
        var frameSink = new FakeFrameSink
        {
            OnWrite = cancellationToken =>
            {
                cts.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            },
        };
        FakeVideoEncoder? encoder = null;
        capture.Supported = true;
        capture.StartGrabberCallback = (_, _) =>
            new FakeFrameGrabber(new byte[64 * 64 * 4], 64, 64);
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            encoder = new FakeVideoEncoder(path, width, height);
        RecordFrameBundleWriter.s_create = configuration =>
        {
            frameSink.Configuration = configuration;
            return frameSink;
        };

        var result = await recording.RecordAsync(uiTarget, null, new RecordOptions
        {
            OutputPath = output,
            FramesDirectory = Path.Join(root, "drained.frames"),
            DurationSec = 10,
            Fps = 1,
            MaxEdge = 64,
        }, cts.Token);

        Assert.AreEqual(1, result.Frames);
        Assert.AreEqual(1, frameSink.SampleCount);
        Assert.AreEqual("cancelled", result.StopReason);
        Assert.IsNotNull(encoder);
        Assert.AreEqual(1, encoder!.FramesWritten);
        Assert.IsTrue(encoder.Completed);
    }

    [TestMethod]
    public async Task RecordAsync_JpegWorkerFailurePreservesMp4AndRemovesFrameStaging()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var root = Path.Join(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var output = Path.Join(root, "jpeg-failure.mp4");
        var framesDirectory = Path.Join(root, "jpeg-failure.frames");
        capture.Supported = true;
        capture.StartGrabberCallback = (_, _) =>
            new FakeFrameGrabber(new byte[64 * 64 * 4], 64, 64);
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            new FakeVideoEncoder(path, width, height);
        RecordFrameBundleWriter.s_encodeJpeg = (_, _, _) =>
            throw new IOException("simulated JPEG worker failure");

        var exception = await Assert.ThrowsExactlyAsync<RecordPartialOutputException>(
            () => recording.RecordAsync(uiTarget, null, new RecordOptions
            {
                OutputPath = output,
                FramesDirectory = framesDirectory,
                DurationSec = 1,
                Fps = 1,
                MaxEdge = 64,
            }, CancellationToken.None));

        Assert.AreEqual(output, exception.VideoPath);
        Assert.IsTrue(File.Exists(output));
        Assert.IsFalse(Directory.Exists(framesDirectory));
        Assert.IsFalse(Directory.EnumerateDirectories(
            root,
            ".*.staging",
            SearchOption.TopDirectoryOnly).Any());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RecordAsync_NonIoFrameFailurePreservesMp4(bool failOnComplete)
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var root = Path.Join(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var output = Path.Join(root, "non-io-frame-failure.mp4");
        var framesDirectory = Path.Join(root, "non-io-frame-failure.frames");
        var frameFailure = new TypeInitializationException(
            "SkiaSharp",
            new DllNotFoundException("simulated native dependency failure"));

        capture.Supported = true;
        capture.StartGrabberCallback = (_, _) =>
            new FakeFrameGrabber(new byte[64 * 64 * 4], 64, 64);
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            new FakeVideoEncoder(path, width, height);
        RecordFrameBundleWriter.s_create = configuration => new FakeFrameSink
        {
            Configuration = configuration,
            WriteException = failOnComplete ? null : frameFailure,
            CompleteException = failOnComplete ? frameFailure : null,
        };

        var exception = await Assert.ThrowsExactlyAsync<RecordPartialOutputException>(
            () => recording.RecordAsync(uiTarget, null, new RecordOptions
            {
                OutputPath = output,
                FramesDirectory = framesDirectory,
                DurationSec = 1,
                Fps = 1,
                MaxEdge = 64,
            }, CancellationToken.None));

        Assert.AreEqual(output, exception.VideoPath);
        Assert.IsTrue(File.Exists(output));
        Assert.IsFalse(Directory.Exists(framesDirectory));
    }

    [TestMethod]
    public async Task RecordAsync_TruncatedFrameBundleReturnsActionableWarning()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var root = Path.Join(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        capture.Supported = true;
        capture.StartGrabberCallback = (_, _) =>
            new FakeFrameGrabber(new byte[64 * 64 * 4], 64, 64);
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            new FakeVideoEncoder(path, width, height);
        RecordFrameBundleWriter.s_create = configuration => new FakeFrameSink
        {
            Configuration = configuration,
            IsTruncated = true,
            ByteLimit = RecordFrameBundleConfiguration.DefaultMaximumBundleBytes,
        };

        var result = await recording.RecordAsync(uiTarget, null, new RecordOptions
        {
            OutputPath = Path.Join(root, "truncated.mp4"),
            FramesDirectory = Path.Join(root, "truncated.frames"),
            DurationSec = 1,
            Fps = 1,
            MaxEdge = 64,
        }, CancellationToken.None);

        Assert.IsNotNull(result.FrameArtifacts);
        Assert.IsTrue(result.FrameArtifacts.Truncated);
        Assert.IsNotNull(result.Warnings);
        Assert.IsTrue(result.Warnings.Any(
            warning => warning.Contains("only the indexed frame prefix was retained", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task RecordAsync_FirstFrameArrivesAfterTheRequestedDuration_StillCapturesAFrame()
    {
        // The elapsed-duration check used to run before any frame had been committed, so a capture
        // source that took longer than --duration to deliver its first frame into the capture loop
        // ended the recording with zero frames: an empty MP4 and, with --frames-dir, a zero-frame
        // manifest. Elapsed time may only end a recording that has already captured something.
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var root = Path.Join(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var output = Path.Join(root, "slow-first-frame.mp4");
        var frame = Enumerable.Repeat((byte)0x33, 64 * 64 * 4).ToArray();

        capture.Supported = true;
        capture.StartGrabberCallback = (_, _) =>
            new SlowStartingFrameGrabber(frame, 64, 64, TimeSpan.FromMilliseconds(1_500));
        Mp4SinkWriterEncoder.s_createNoClobber =
            (path, width, height, _, _) => new FakeVideoEncoder(path, width, height);

        var result = await recording.RecordAsync(uiTarget, null, new RecordOptions
        {
            OutputPath = output,
            DurationSec = 1,
            Fps = 2,
            MaxEdge = 64,
        }, CancellationToken.None);

        Assert.IsTrue(
            result.Frames >= 1,
            $"a recording whose capture source starts late must still capture a frame, got {result.Frames}");
    }

    [TestMethod]
    public async Task RecordAsync_SlowFrameArtifactSetup_IsNotChargedToTheRequestedDuration()
    {
        // Frame artifact setup creates a staging directory and opens the manifest and index writers.
        // The capture clock used to start before that work, so on a loaded machine a short recording
        // could spend its whole --duration budget on filesystem setup and capture almost nothing.
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture();
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        await ResolveAsync(svc, uiTarget, "btnInvoke");

        var root = Path.Join(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var output = Path.Join(root, "slow-setup.mp4");
        var setupDelay = TimeSpan.FromMilliseconds(1_500);

        capture.Supported = true;
        capture.StartGrabberCallback = (_, _) =>
            new FakeFrameGrabber(new byte[64 * 64 * 4], 64, 64);
        Mp4SinkWriterEncoder.s_createNoClobber =
            (path, width, height, _, _) => new FakeVideoEncoder(path, width, height);
        RecordFrameBundleWriter.s_create = configuration =>
        {
            Thread.Sleep(setupDelay);
            return new FakeFrameSink { Configuration = configuration };
        };

        var wallClock = Stopwatch.StartNew();
        var result = await recording.RecordAsync(uiTarget, null, new RecordOptions
        {
            OutputPath = output,
            FramesDirectory = Path.Join(root, "slow-setup.frames"),
            DurationSec = 1,
            Fps = 4,
            MaxEdge = 64,
        }, CancellationToken.None);
        wallClock.Stop();

        Assert.IsTrue(result.Frames >= 1, "the recording must capture something");

        // Wall time covers setup and capture; the reported elapsed time must cover capture only, so
        // the setup delay has to show up as the difference. Comparing the two rather than asserting
        // an absolute elapsed time keeps this immune to a slow machine stretching the capture loop.
        var excluded = wallClock.ElapsedMilliseconds - result.ElapsedMs;
        Assert.IsTrue(
            excluded >= setupDelay.TotalMilliseconds * 0.8,
            $"reported elapsed time must measure capture, not setup: only {excluded}ms of the " +
            $"{setupDelay.TotalMilliseconds}ms setup was excluded from {result.ElapsedMs}ms elapsed");
    }

    /// <summary>Returns the first frame until <c>grow</c> is true, then a larger one, like a window being resized.</summary>
    private sealed class ResizingFrameGrabber(byte[] small, int smallWidth, byte[] large, int largeWidth, int height, Func<bool> grow)
        : IFrameGrabber
    {
        private long _version;

        public bool Grown { get; private set; }

        public bool IsClosed => false;

        public (byte[] Pixels, int Width, int Height, long Version)? TryGetLatest()
        {
            Grown |= grow();
            return Grown
                ? (large, largeWidth, height, Interlocked.Increment(ref _version))
                : (small, smallWidth, height, Interlocked.Increment(ref _version));
        }

        public Task<bool> WaitForFirstFrameAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);

        public void Dispose()
        {
        }
    }

    private sealed class FakeFrameGrabber(byte[] pixels, int width, int height) : IFrameGrabber
    {
        private long _version;

        public bool IsClosed { get; init; }

        public bool Disposed { get; private set; }

        public (byte[] Pixels, int Width, int Height, long Version)? TryGetLatest()
            => (pixels, width, height, Interlocked.Increment(ref _version));

        public Task<bool> WaitForFirstFrameAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);

        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// Reports one frame to the recorder's setup read, then yields nothing for the given silence
    /// window before delivering frames again — the shape of a capture source that is slow to start
    /// feeding the capture loop.
    /// </summary>
    private sealed class SlowStartingFrameGrabber(
        byte[] pixels, int width, int height, TimeSpan silence) : IFrameGrabber
    {
        private readonly Stopwatch _silenceSince = new();
        private int _reads;
        private long _version;

        public bool IsClosed => false;

        public (byte[] Pixels, int Width, int Height, long Version)? TryGetLatest()
        {
            // Recorder setup dereferences its read without a null check, so it always gets a frame.
            if (Interlocked.Increment(ref _reads) == 1)
            {
                return Latest();
            }

            // Time the silence from the capture loop's first read, so the window the recorder sees
            // does not shrink by however long the recorder spent between the two.
            if (!_silenceSince.IsRunning)
            {
                _silenceSince.Start();
            }

            return _silenceSince.Elapsed < silence ? null : Latest();
        }

        public Task<bool> WaitForFirstFrameAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);

        public void Dispose()
        {
        }

        private (byte[] Pixels, int Width, int Height, long Version) Latest()
            => (pixels, width, height, Interlocked.Increment(ref _version));
    }

    private sealed class FakeVideoEncoder(string path, int width, int height) : IVideoEncoder
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public int FramesWritten { get; private set; }

        public byte[]? LastFrame { get; private set; }

        public bool Completed { get; private set; }

        public bool Disposed { get; private set; }

        public Exception? WriteFrameException { get; init; }

        public Action? OnComplete { get; init; }

        public void WriteFrame(ReadOnlySpan<byte> bgra, long sampleTimeHns, long sampleDurationHns)
        {
            if (WriteFrameException is not null)
            {
                throw WriteFrameException;
            }

            Assert.AreEqual(Width * Height * 4, bgra.Length);
            Assert.IsTrue(sampleDurationHns > 0);
            FramesWritten++;
            LastFrame = bgra.ToArray();
        }

        public void Complete()
        {
            Completed = true;
            OnComplete?.Invoke();
            File.WriteAllBytes(path, [1, 2, 3, 4, 5]);
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class FakeFrameSink : IRecordFrameSink
    {
        public RecordFrameBundleConfiguration? Configuration { get; set; }

        public int SampleCount { get; private set; }

        public int ImageCount => SampleCount;
        public bool IsTruncated { get; init; }

        public long ByteLimit { get; init; }

        public bool Aborted { get; private set; }

        public Action<CancellationToken>? OnWrite { get; init; }

        public Exception? WriteException { get; init; }

        public Exception? CompleteException { get; init; }

        public Exception? AbortException { get; init; }

        public ValueTask WriteAsync(
            ReadOnlyMemory<byte> bgra,
            RecordFrameSample sample,
            CancellationToken cancellationToken)
        {
            OnWrite?.Invoke(cancellationToken);
            if (WriteException is not null)
            {
                throw WriteException;
            }
            SampleCount++;
            return ValueTask.CompletedTask;
        }

        public Task<RecordFrameArtifactResult> CompleteAsync(RecordFrameCompletion completion)
        {
            if (CompleteException is not null)
            {
                throw CompleteException;
            }

            var configuration = Configuration ?? throw new InvalidOperationException("Frame sink was not configured.");
            var directory = completion.PublicationDirectory ?? configuration.FinalDirectory;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Join(directory, "frames.ndjson"), "");
            File.WriteAllText(Path.Join(directory, "manifest.json"),
                JsonSerializer.Serialize(new RecordFrameBundleManifest
                {
                    Video = new RecordFrameVideoManifest { Path = configuration.VideoPath },
                }, RecordingJsonContext.Default.RecordFrameBundleManifest));
            return Task.FromResult(new RecordFrameArtifactResult
            {
                Directory = directory,
                Manifest = Path.Join(directory, "manifest.json"),
                Index = Path.Join(directory, "frames.ndjson"),
                Samples = SampleCount,
                Images = ImageCount,
                Truncated = IsTruncated,
                ByteLimit = ByteLimit,
            });
        }

        public Task AbortAsync()
        {
            Aborted = true;
            if (AbortException is not null)
            {
                throw AbortException;
            }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

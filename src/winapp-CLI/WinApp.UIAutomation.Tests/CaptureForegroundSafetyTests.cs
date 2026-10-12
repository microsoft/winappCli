// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;
using Windows.Win32.Foundation;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
[DoNotParallelize]
public class CaptureForegroundSafetyTests
{
    [TestCleanup]
    public void Cleanup()
    {
        ForegroundGuard.ResetNativeSeams();
        UiAutomationService.ResetNativeSeams();
        WgcCapture.s_isSupported = global::Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported;
        WgcCapture.s_startGrabber = (hwnd, logger, fps) => WgcCapture.StartGrabber(hwnd, logger, fps);
    }

    [TestMethod]
    public async Task ScreenshotAsync_MenuOpeningDuringFrameAcquisitionNeverFallsBackToRootOnlyCapture()
    {
        using var fixture = new UiaTestFixture();
        var service = NewAutomationService();
        var discoveries = 0;
        WgcCapture.s_isSupported = () => true;
        WgcCapture.s_startGrabber = (_, logger, _) => new WindowCaptureSession(
            new ScreenshotGrabber(), () => new(0, 0, 1, 1),
            () => ++discoveries <= 1 ? [] : [new(42, new(0, 0, 1, 1))],
            _ => new ScreenshotGrabber { HasFrame = false }, logger: logger);
        var rootCaptures = 0;
        UiAutomationService.s_captureFromWindow = (_, width, height) =>
        {
            rootCaptures++;
            var pixels = new byte[width * height * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i + 2] = 255;
                pixels[i + 3] = 255;
            }
            return pixels;
        };
        UiAutomationService.s_foregroundWindowForBlankRetry = _ => Assert.Fail("Menu failure must not foreground the root.");
        UiAutomationService.s_sleepForBlankRetry = _ => Assert.Fail("Menu failure must not trigger blank retry.");

        var failure = await Assert.ThrowsExactlyAsync<WindowCaptureException>(() =>
            service.ScreenshotAsync(TargetFor(fixture), null, false, false, CancellationToken.None));

        Assert.IsInstanceOfType<TimeoutException>(failure.InnerException);
        Assert.AreEqual(0, rootCaptures);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task ScreenshotAsync_ChildCaptureFailureNeverFallsBackToRootOnlyCapture(int boundary)
    {
        using var fixture = new UiaTestFixture();
        var service = NewAutomationService();
        Exception cause = boundary switch
        {
            0 => new COMException("Child startup rejected"),
            1 => new Win32Exception(5, "Strict child GDI failed"),
            2 => new InvalidOperationException("Child is protected from capture"),
            _ => new TimeoutException("Child has no frame")
        };
        long clocks = 0;
        WgcCapture.s_isSupported = () => true;
        WgcCapture.s_startGrabber = (_, logger, _) => new WindowCaptureSession(
            new ScreenshotGrabber(), () => new(0, 0, 1, 1),
            () => [new(42, new(0, 0, 1, 1))],
            _ => boundary == 0 ? throw cause : new ScreenshotGrabber
            {
                Failure = boundary == 3 ? null : cause,
                HasFrame = boundary != 3
            }, clock: () => clocks++ == 0 ? 100 : 3000, logger: logger);
        var rootCaptures = 0;
        var foregrounds = 0;
        UiAutomationService.s_captureFromWindow = (_, _, _) =>
        {
            rootCaptures++;
            throw new AssertFailedException("A failed child must not become a root-only screenshot.");
        };
        UiAutomationService.s_foregroundWindowForBlankRetry = _ => foregrounds++;
        UiAutomationService.s_sleepForBlankRetry = _ => Assert.Fail("A failed child must not trigger blank retry.");

        var failure = await Assert.ThrowsExactlyAsync<WindowCaptureException>(() =>
            service.ScreenshotAsync(TargetFor(fixture), null, false, false, CancellationToken.None));
        if (boundary == 3)
        {
            Assert.IsInstanceOfType<TimeoutException>(failure.InnerException);
        }
        else
        {
            Assert.AreSame(cause, failure.InnerException);
        }
        Assert.AreEqual(0, rootCaptures);
        Assert.AreEqual(0, foregrounds);
    }

    [TestMethod]
    public async Task ScreenshotAsync_RootWgcFailureRetainsGdiFallbackAndBlankRetry()
    {
        using var fixture = new UiaTestFixture();
        var service = NewAutomationService();
        WgcCapture.s_isSupported = () => true;
        WgcCapture.s_startGrabber = (_, _, _) => throw new COMException("Root WGC initialization failed");
        var rootCaptures = 0;
        var foregrounds = 0;
        byte[]? expected = null;
        UiAutomationService.s_captureFromWindow = (_, width, height) =>
        {
            var pixels = new byte[width * height * 4];
            if (++rootCaptures == 2)
            {
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    pixels[i + 2] = 255;
                    pixels[i + 3] = 255;
                }
                expected = pixels;
            }
            return pixels;
        };
        UiAutomationService.s_foregroundWindowForBlankRetry = _ => foregrounds++;
        UiAutomationService.s_sleepForBlankRetry = _ => { };

        var result = await service.ScreenshotAsync(TargetFor(fixture), null, false, false, CancellationToken.None);
        Assert.AreSame(expected, result.Pixels);
        Assert.AreEqual(result.Width * result.Height * 4, result.Pixels.Length);
        Assert.AreEqual(2, rootCaptures);
        Assert.AreEqual(1, foregrounds);
    }

    private sealed class ScreenshotGrabber : IFrameGrabber
    {
        internal Exception? Failure { get; init; }
        internal bool HasFrame { get; init; } = true;
        public bool IsClosed => false;
        public (byte[] Pixels, int Width, int Height, long Version)? TryGetLatest()
        {
            if (Failure is not null)
            {
                throw Failure;
            }
            return HasFrame ? (new byte[] { 0, 0, 255, 255 }, 1, 1, 1) : null;
        }
        public Task<bool> WaitForFirstFrameAsync(TimeSpan timeout, CancellationToken ct)
            => Task.FromResult(TryGetLatest() is not null);
        public void Dispose() { }
    }

    [TestMethod]
    public async Task ScreenshotAsync_CaptureScreen_ThrowsWhenTargetIsNotForeground()
    {
        using var fx = new UiaTestFixture();
        var service = NewAutomationService();
        var target = TargetFor(fx);
        ForegroundGuard.s_getForegroundWindow = () => new HWND(0);

        await Assert.ThrowsExactlyAsync<ForegroundLostException>(
            () => service.ScreenshotAsync(target, null, captureScreen: true, focus: false, CancellationToken.None));
    }

    [TestMethod]
    public async Task RecordAsync_FirstScreenFrame_ThrowsWhenTargetIsNotForeground()
    {
        using var fx = new UiaTestFixture();
        var recorder = NewRecordingService();
        var target = TargetFor(fx);
        ForegroundGuard.s_getForegroundWindow = () => new HWND(0);

        await Assert.ThrowsExactlyAsync<ForegroundLostException>(
            () => recorder.RecordAsync(target, null, new RecordOptions
            {
                OutputPath = Path.Combine(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"), "foreground.mp4"),
                CaptureScreen = true,
                DurationSec = 1,
                Fps = 1,
                MaxEdge = 64,
            }, CancellationToken.None));
    }

    // ------------------------------------------- an owned dialog in front is the capturable case

    [TestMethod]
    public async Task ScreenshotAsync_CaptureScreen_AcceptsAModalDialogTheTargetOwns()
    {
        // The reported regression, at the service boundary. `-w <main> --capture-screen` while the
        // app's own modal dialog holds the foreground must capture, not throw: the dialog is the
        // overlay --capture-screen exists to record, and it is sitting on the pixels being read.
        using var fx = new UiaTestFixture();
        var service = NewAutomationService();
        var target = TargetFor(fx);

        var dialog = new HWND(0x7F7F);
        ForegroundGuard.s_getForegroundWindow = () => dialog;
        ForegroundGuard.s_getRootAncestor = h => h;   // both are top-level, as real windows are
        ForegroundGuard.s_getOwner = h => h == dialog ? new HWND((nint)fx.Hwnd) : new HWND(0);

        // Reaching the capture at all is the assertion: the old predicate threw before this point.
        var (pixels, width, height) = await service.ScreenshotAsync(
            target, null, captureScreen: true, focus: false, CancellationToken.None);

        Assert.IsTrue(pixels.Length > 0);
        Assert.IsTrue(width > 0 && height > 0);
    }

    [TestMethod]
    public async Task ScreenshotAsync_CaptureScreen_StillRefusesAnUnrelatedForegroundWindow()
    {
        // The guard has to keep doing its job: an unrelated window in front means the pixels would be
        // somebody else's, and a PNG of the wrong app is worse than no PNG.
        using var fx = new UiaTestFixture();
        var service = NewAutomationService();
        var target = TargetFor(fx);

        ForegroundGuard.s_getForegroundWindow = () => new HWND(0x6060);
        ForegroundGuard.s_getRootAncestor = h => h;
        ForegroundGuard.s_getOwner = _ => new HWND(0);

        await Assert.ThrowsExactlyAsync<ForegroundLostException>(
            () => service.ScreenshotAsync(target, null, captureScreen: true, focus: false, CancellationToken.None));
    }

    [TestMethod]
    public async Task RecordAsync_FirstScreenFrame_AcceptsAModalDialogTheTargetOwns()
    {
        // Recording had the same defect and the same fix: `record -w <main> --capture-screen` must
        // accept its own modal foreground.
        //
        // Success is measured by what it does NOT throw. SafeFakeWindowCapture refuses to produce
        // screen pixels precisely so these tests can prove the foreground gate ran first, so reaching
        // it is the assertion: the gate accepted the owned dialog and let the recording proceed.
        using var fx = new UiaTestFixture();
        var recorder = NewRecordingService();
        var target = TargetFor(fx);
        var output = Path.Combine(
            AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"), "owned.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        var dialog = new HWND(0x7E7E);
        ForegroundGuard.s_getForegroundWindow = () => dialog;
        ForegroundGuard.s_getRootAncestor = h => h;
        ForegroundGuard.s_getOwner = h => h == dialog ? new HWND((nint)fx.Hwnd) : new HWND(0);

        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => recorder.RecordAsync(target, null, new RecordOptions
            {
                OutputPath = output,
                CaptureScreen = true,
                DurationSec = 1,
                Fps = 1,
                MaxEdge = 64,
            }, CancellationToken.None));

        StringAssert.Contains(thrown.Message, "should fail before screen capture",
            "the recording reached pixel capture, which means the owned-dialog foreground was accepted");
        Assert.IsNotInstanceOfType<ForegroundLostException>(thrown,
            "an owned modal dialog must not be treated as a lost foreground");
    }

    [TestMethod]
    public async Task RecordAsync_RefusingAnUnrelatedForeground_WritesNoArtifact()
    {
        // A refusal must leave nothing behind: a truncated MP4 is worse than none, because the caller
        // cannot tell it apart from a real recording of the wrong window.
        using var fx = new UiaTestFixture();
        var recorder = NewRecordingService();
        var target = TargetFor(fx);
        var output = Path.Combine(
            AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"), "refused.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        ForegroundGuard.s_getForegroundWindow = () => new HWND(0x5050);
        ForegroundGuard.s_getRootAncestor = h => h;
        ForegroundGuard.s_getOwner = _ => new HWND(0);

        await Assert.ThrowsExactlyAsync<ForegroundLostException>(
            () => recorder.RecordAsync(target, null, new RecordOptions
            {
                OutputPath = output,
                CaptureScreen = true,
                DurationSec = 1,
                Fps = 1,
                MaxEdge = 64,
            }, CancellationToken.None));

        Assert.IsFalse(File.Exists(output), "a refused recording must produce no file");
    }

    private static IUiAutomation NewAutomationService()
        => new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddWinAppUiAutomation()
            .BuildServiceProvider()
            .GetRequiredService<IUiAutomation>();

    private static IUiRecordingService NewRecordingService()
        => new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddWinAppUiAutomation()
            .AddSingleton<IWindowCapture, SafeFakeWindowCapture>()
            .AddWinAppUiRecording()
            .BuildServiceProvider()
            .GetRequiredService<IUiRecordingService>();

    private static UiTarget TargetFor(UiaTestFixture fx) => new()
    {
        ProcessId = fx.ProcessId,
        ProcessName = "WinApp.UIAutomation.Tests",
        WindowHandle = fx.Hwnd,
        WindowTitle = fx.Title,
        IsExplicitWindow = true,
    };

    private sealed class SafeFakeWindowCapture : IWindowCapture
    {
        public bool IsFrameCaptureSupported => false;

        public IFrameGrabber StartFrameGrabber(nint hwnd, int fps = 0)
            => throw new InvalidOperationException("Foreground safety should fail before WGC starts.");

        public byte[] CaptureWindowPixels(nint hwnd, int width, int height)
            => new byte[Math.Max(0, width * height * 4)];

        public byte[] CaptureScreenPixels(
            int x, int y, int cropWidth, int cropHeight,
            int encoderWidth, int encoderHeight,
            int displayWidth, int displayHeight)
            => throw new InvalidOperationException("Foreground safety should fail before screen capture.");
    }
}

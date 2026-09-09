// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;
using Windows.Win32.Foundation;

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

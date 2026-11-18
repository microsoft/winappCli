// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32.Foundation;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
[DoNotParallelize]
public class WgcScreenshotFreshnessTests
{
    [TestCleanup]
    public void Cleanup()
        => WgcCapture.s_startScreenshotGrabber = (hwnd, logger, fps) => WindowCaptureSession.Start(hwnd, logger, fps, includeSecondaryWindows: false);

    [TestMethod]
    public async Task Capture_AvailableFrameDoesNotPerformSeparateReadinessWait()
    {
        byte[] pixels = [0, 255, 0, 255];
        var grabber = new Grabber(() => (pixels, 1, 1, 1L))
        {
            FailOnReadinessWait = true
        };
        WgcCapture.s_startScreenshotGrabber = (_, _, _) => grabber;

        var result = await WgcCapture.CaptureAsync(new HWND(1), NullLogger.Instance, CancellationToken.None);

        Assert.AreSame(pixels, result.Pixels);
        Assert.AreEqual(1, grabber.Reads);
    }

    [TestMethod]
    public async Task Capture_WaitsForFreshFrameRatherThanCountingCachedPolls()
    {
        var reads = 0;
        byte[] rendered = [0, 255, 0, 255];
        var grabber = new Grabber(() => ++reads < 7
            ? (new byte[4], 1, 1, 1L) : (rendered, 1, 1, 2L));
        WgcCapture.s_startScreenshotGrabber = (_, _, _) => grabber;

        var result = await WgcCapture.CaptureAsync(new HWND(1), NullLogger.Instance, CancellationToken.None);

        Assert.AreSame(rendered, result.Pixels);
        Assert.AreEqual(7, grabber.Reads);
        Assert.IsTrue(grabber.Disposed);
    }

    [TestMethod]
    public async Task Capture_StaticBlankFrameFailsAtBoundedDeadline()
    {
        var timer = Stopwatch.StartNew();
        var grabber = new Grabber(() => (new byte[4], 1, 1, 1L));
        WgcCapture.s_startScreenshotGrabber = (_, _, _) => grabber;

        await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
            WgcCapture.CaptureAsync(new HWND(1), NullLogger.Instance, CancellationToken.None));

        Assert.IsLessThan(5000L, timer.ElapsedMilliseconds);
        Assert.IsTrue(grabber.Disposed);
    }

    [TestMethod]
    public async Task Capture_FiveFreshBlankFramesFailRatherThanPublishingBlankPixels()
    {
        var version = 0L;
        var grabber = new Grabber(() => (new byte[4], 1, 1, ++version));
        WgcCapture.s_startScreenshotGrabber = (_, _, _) => grabber;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            WgcCapture.CaptureAsync(new HWND(1), NullLogger.Instance, CancellationToken.None));

        Assert.AreEqual(5, grabber.Reads);
        Assert.IsTrue(grabber.Disposed);
    }

    [TestMethod]
    public async Task Capture_OpaqueBlackIsValidAndReturnedWithoutRetry()
    {
        byte[] black = [0, 0, 0, 255];
        var grabber = new Grabber(() => (black, 1, 1, 1L));
        WgcCapture.s_startScreenshotGrabber = (_, _, _) => grabber;

        var result = await WgcCapture.CaptureAsync(new HWND(1), NullLogger.Instance, CancellationToken.None);

        Assert.AreSame(black, result.Pixels);
        Assert.AreEqual(1, grabber.Reads);
    }

    [TestMethod]
    public async Task Capture_CancellationWhileWaitingDisposesGrabber()
    {
        var grabber = new Grabber(() => (new byte[4], 1, 1, 1L));
        WgcCapture.s_startScreenshotGrabber = (_, _, _) => grabber;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
            WgcCapture.CaptureAsync(new HWND(1), NullLogger.Instance, cancellation.Token));

        Assert.IsTrue(grabber.Disposed);
    }

    private sealed class Grabber(Func<(byte[] Pixels, int Width, int Height, long Version)?> read) : IFrameGrabber
    {
        internal int Reads { get; private set; }
        internal bool Disposed { get; private set; }
        internal bool FailOnReadinessWait { get; init; }
        public bool IsClosed => false;
        public (byte[] Pixels, int Width, int Height, long Version)? TryGetLatest()
        {
            Reads++;
            return read();
        }
        public Task<bool> WaitForFirstFrameAsync(TimeSpan timeout, CancellationToken ct)
            => FailOnReadinessWait
                ? throw new AssertFailedException("Frame acquisition must not begin with a separate readiness wait.")
                : Task.FromResult(true);
        public void Dispose() => Disposed = true;
    }
}

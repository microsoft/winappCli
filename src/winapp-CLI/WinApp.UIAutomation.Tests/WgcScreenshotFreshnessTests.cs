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
        => WgcCapture.s_startGrabber = (hwnd, logger, fps) => WgcCapture.StartGrabber(hwnd, logger, fps);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Capture_MenuOpeningDuringFrameAcquisitionReturnsCombinedImageOrTypedFailure(bool arrives)
    {
        var discoveries = 0;
        var childReads = 0;
        byte[] menuPixels = [0, 255, 0, 255];
        var root = new Grabber(() => (new byte[] { 0, 0, 255, 255 }, 1, 1, 1L));
        var secondaryCapture = new Grabber(() => arrives && ++childReads >= 3 ? (menuPixels, 1, 1, 1L) : null);
        var combined = new WindowCaptureSession(root, () => new(0, 0, 1, 1),
            () => ++discoveries <= 1 ? [] : [new(42, new(0, 0, 1, 1))], _ => secondaryCapture);
        WgcCapture.s_startGrabber = (_, _, _) => combined;
        var timer = Stopwatch.StartNew();

        if (arrives)
        {
            var result = await WgcCapture.CaptureAsync(new HWND(1), NullLogger.Instance, CancellationToken.None);
            CollectionAssert.AreEqual(menuPixels, result.Pixels);
            Assert.AreEqual(1, result.Width);
            Assert.AreEqual(1, result.Height);
        }
        else
        {
            var error = await Assert.ThrowsExactlyAsync<WindowCaptureException>(() =>
                WgcCapture.CaptureAsync(new HWND(1), NullLogger.Instance, CancellationToken.None));
            Assert.IsInstanceOfType<TimeoutException>(error.InnerException);
            StringAssert.Contains(error.Message, "42");
        }
        Assert.IsLessThan(3000L, timer.ElapsedMilliseconds);
        Assert.IsTrue(root.Disposed);
        Assert.IsTrue(secondaryCapture.Disposed);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Capture_BlankSecondaryFrameWaitsForPaintedPixelsOrTypedFailure(bool arrives, bool black)
    {
        long now = 100;
        byte[] painted = black ? [0, 0, 0, 255] : [0, 255, 0, 255];
        var root = new Grabber(() => (new byte[] { 0, 0, 255, 255 }, 1, 1, 1L));
        var reads = 0;
        var secondaryCapture = new Grabber(() =>
        {
            reads++;
            now = reads == 1 ? 100 : reads == 2 ? 2099 : 2100;
            return (arrives && reads >= 3 ? painted : new byte[4], 1, 1, (long)reads);
        });
        var combined = new WindowCaptureSession(root, () => new(0, 0, 1, 1),
            () => [new(42, new(0, 0, 1, 1))], _ => secondaryCapture, () => now);
        WgcCapture.s_startGrabber = (_, _, _) => combined;

        if (arrives)
        {
            var result = await WgcCapture.CaptureAsync(new HWND(1), NullLogger.Instance, CancellationToken.None);
            CollectionAssert.AreEqual(painted, result.Pixels);
        }
        else
        {
            var error = await Assert.ThrowsExactlyAsync<WindowCaptureException>(() =>
                WgcCapture.CaptureAsync(new HWND(1), NullLogger.Instance, CancellationToken.None));
            Assert.IsInstanceOfType<TimeoutException>(error.InnerException);
            StringAssert.Contains(error.Message, "42");
        }
        Assert.AreEqual(3, reads);
        Assert.IsTrue(root.Disposed);
        Assert.IsTrue(secondaryCapture.Disposed);
    }

    [TestMethod]
    public async Task Capture_CancellationWhileMenuFrameIsPendingDisposesBothGrabbers()
    {
        var discoveries = 0;
        var root = new Grabber(() => (new byte[] { 0, 0, 255, 255 }, 1, 1, 1L));
        var secondaryCapture = new Grabber(() => null);
        var combined = new WindowCaptureSession(root, () => new(0, 0, 1, 1),
            () => ++discoveries <= 1 ? [] : [new(42, new(0, 0, 1, 1))], _ => secondaryCapture);
        WgcCapture.s_startGrabber = (_, _, _) => combined;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
            WgcCapture.CaptureAsync(new HWND(1), NullLogger.Instance, cancellation.Token));

        Assert.IsTrue(root.Disposed);
        Assert.IsTrue(secondaryCapture.Disposed);
    }

    [TestMethod]
    public async Task Capture_AvailableFrameDoesNotPerformSeparateReadinessWait()
    {
        byte[] pixels = [0, 255, 0, 255];
        var grabber = new Grabber(() => (pixels, 1, 1, 1L))
        {
            FailOnReadinessWait = true
        };
        WgcCapture.s_startGrabber = (_, _, _) => grabber;

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
        WgcCapture.s_startGrabber = (_, _, _) => grabber;

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
        WgcCapture.s_startGrabber = (_, _, _) => grabber;

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
        WgcCapture.s_startGrabber = (_, _, _) => grabber;

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
        WgcCapture.s_startGrabber = (_, _, _) => grabber;

        var result = await WgcCapture.CaptureAsync(new HWND(1), NullLogger.Instance, CancellationToken.None);

        Assert.AreSame(black, result.Pixels);
        Assert.AreEqual(1, grabber.Reads);
    }

    [TestMethod]
    public async Task Capture_CancellationWhileWaitingDisposesGrabber()
    {
        var grabber = new Grabber(() => (new byte[4], 1, 1, 1L));
        WgcCapture.s_startGrabber = (_, _, _) => grabber;
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

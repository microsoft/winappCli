// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;
using Windows.Win32.Foundation;
using static Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests.CaptureTestWindows;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
[DoNotParallelize]
public class WindowCaptureFallbackTests
{
    [TestMethod]
    [DataRow(1u)]
    [DataRow(0x11u)]
    public void EnsureCaptureAllowed_DisplayAffinityProtectionIsDenied(uint affinity)
    {
        WithOwnedPopup((_, child, _) =>
        {
            Assert.IsTrue(SetWindowDisplayAffinity(child, affinity), $"Cannot set test affinity: {Marshal.GetLastPInvokeError()}.");
            try
            {
                var failure = Assert.ThrowsExactly<InvalidOperationException>(() =>
                    WindowCaptureFallback.EnsureCaptureAllowed(child));
                StringAssert.Contains(failure.Message, "protected from capture");
            }
            finally
            {
                Assert.IsTrue(SetWindowDisplayAffinity(child, 0));
            }
        });
    }

    [TestMethod]
    public void TryGetLatest_ConsumedFrameStartsNextCaptureWithoutAnotherPoll()
    {
        WithOwnedPopup((_, child, _) =>
        {
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var finished = new ManualResetEventSlim();
            long now = 0;
            var calls = 0;
            using var grabber = new WindowCaptureFallback(child, () =>
            {
                var value = Interlocked.Increment(ref calls);
                if (value == 2)
                {
                    started.Set();
                    try
                    {
                        Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(5)));
                    }
                    finally
                    {
                        finished.Set();
                    }
                }
                return (new byte[] { (byte)value, 0, 0, 255 }, 1, 1);
            }, () => now);
            try
            {
                var deadline = Environment.TickCount64 + 2000;
                var frame = grabber.TryGetLatest();
                while (frame is null && Environment.TickCount64 < deadline)
                {
                    Thread.Sleep(5);
                    frame = grabber.TryGetLatest();
                }
                Assert.IsNotNull(frame);
                Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(2)),
                    "Publishing the first frame must start the replacement capture without another sample.");
                Assert.AreEqual(2, calls);
                now = 1999;
                Assert.AreEqual(frame.Value.Version, grabber.TryGetLatest()!.Value.Version);
                Assert.AreEqual(2, calls, "Sampling must not start a second concurrent capture.");
                now = 2000;
                Assert.ThrowsExactly<TimeoutException>(() => grabber.TryGetLatest());
            }
            finally
            {
                release.Set();
                if (started.IsSet)
                {
                    Assert.IsTrue(finished.Wait(TimeSpan.FromSeconds(2)));
                }
            }
        });
    }

    [TestMethod]
    public void TryGetLatest_SlowNativeCallDoesNotBlockSamplingAndTimesOut()
    {
        WithOwnedPopup((_, child, _) =>
        {
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var finished = new ManualResetEventSlim();
            long now = 0;
            var calls = 0;
            using var grabber = new WindowCaptureFallback(child, () =>
            {
                Interlocked.Increment(ref calls);
                started.Set();
                release.Wait();
                finished.Set();
                return (new byte[] { 0, 0, 0, 255 }, 1, 1);
            }, () => now);
            try
            {
                Assert.IsNull(grabber.TryGetLatest());
                Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(2)));
                now = 1999;
                Assert.IsNull(grabber.TryGetLatest());
                now = 2000;
                Assert.ThrowsExactly<TimeoutException>(() => grabber.TryGetLatest());
                Assert.AreEqual(1, calls);
            }
            finally
            {
                release.Set();
                Assert.IsTrue(finished.Wait(TimeSpan.FromSeconds(2)));
            }
        });
    }

    [TestMethod]
    public async Task TryGetLatest_FramedWindowUsesDwmDimensionsAndCorrectClientPixelPosition()
    {
        if (ForegroundGuard.NoInteractiveDesktop())
        {
            Assert.Inconclusive("Native PrintWindow requires an interactive desktop.");
        }
        using var fixture = new UiaTestFixture();
        Form? popup = null;
        try
        {
            nint handle = 0;
            fixture.OnUiThread(() =>
            {
                popup = new Form
                {
                    BackColor = System.Drawing.Color.Lime,
                    Size = new(160, 140),
                    ShowInTaskbar = false
                };
                popup.Show(fixture.Form);
                popup.Refresh();
                handle = popup.Handle;
            });
            using var grabber = new WindowCaptureFallback(new HWND(handle));
            Assert.IsTrue(await grabber.WaitForFirstFrameAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
            var frame = grabber.TryGetLatest()!.Value;
            var captureDeadline = Environment.TickCount64 + 2000;
            while (!HasGreenPixels(frame.Pixels) && Environment.TickCount64 < captureDeadline)
            {
                await Task.Delay(20);
                frame = grabber.TryGetLatest()!.Value;
            }
            var previous = SetThreadDpiAwarenessContext(-4);
            Assert.AreNotEqual((nint)0, previous);
            try
            {
                var bounds = WindowCaptureSession.GetBounds(new HWND(handle));
                Assert.AreEqual(bounds.Right - bounds.Left, frame.Width);
                Assert.AreEqual(bounds.Bottom - bounds.Top, frame.Height);
                var clientOrigin = new System.Drawing.Point();
                Assert.IsTrue(ClientToScreen(new HWND(handle), ref clientOrigin));
                var offset = ((clientOrigin.Y - bounds.Top + 10) * frame.Width +
                    clientOrigin.X - bounds.Left + 10) * 4;
                CollectionAssert.AreEqual(new byte[] { 0, 255, 0, 255 }, frame.Pixels[offset..(offset + 4)],
                    $"bounds={bounds}, client={clientOrigin}, size={frame.Width}x{frame.Height}");
            }
            finally
            {
                SetThreadDpiAwarenessContext(previous);
            }
        }
        finally
        {
            fixture.OnUiThread(() => popup?.Dispose());
        }

        static bool HasGreenPixels(byte[] pixels)
        {
            for (var i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i] == 0 && pixels[i + 1] == 255 && pixels[i + 2] == 0)
                {
                    return true;
                }
            }
            return false;
        }
    }

    [TestMethod]
    public void TryGetLatest_CompletedResultFromChangedPidIsDiscardedWithoutAdvancingCache()
    {
        WithOwnedPopup((_, child, pid) =>
        {
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var finished = new ManualResetEventSlim();
            var calls = 0;
            var getPid = RealOwnedWindowFinder.s_getWindowProcessId;
            using var root = new WindowCaptureFallback(child, () =>
            {
                var value = Interlocked.Increment(ref calls);
                if (value == 2)
                {
                    started.Set();
                    release.Wait();
                    finished.Set();
                }
                return (new byte[] { (byte)value, 0, 0, 255 }, 1, 1);
            });
            try
            {
                var deadline = Environment.TickCount64 + 2000;
                var healthy = root.TryGetLatest();
                while (healthy is null && Environment.TickCount64 < deadline)
                {
                    Thread.Sleep(5);
                    healthy = root.TryGetLatest();
                }
                Assert.IsNotNull(healthy);
                root.TryGetLatest();
                Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(2)));
                RealOwnedWindowFinder.s_getWindowProcessId = window => window == child ? pid + 1 : getPid(window);
                release.Set();
                Assert.IsTrue(finished.Wait(TimeSpan.FromSeconds(2)));
                var final = root.TryGetLatest()!.Value;
                Assert.IsTrue(root.IsClosed);
                Assert.AreEqual(healthy.Value.Version, final.Version);
                Assert.AreSame(healthy.Value.Pixels, final.Pixels);
                Assert.AreEqual((byte)1, final.Pixels[0]);
                root.TryGetLatest();
                Assert.AreEqual(2, calls);
            }
            finally
            {
                release.Set();
                if (started.IsSet)
                {
                    Assert.IsTrue(finished.Wait(TimeSpan.FromSeconds(2)));
                }
                RealOwnedWindowFinder.s_getWindowProcessId = getPid;
            }
        });
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(HWND window, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(HWND window, ref System.Drawing.Point point);
}

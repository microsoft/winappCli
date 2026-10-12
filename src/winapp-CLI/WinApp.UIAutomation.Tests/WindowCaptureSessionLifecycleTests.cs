// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;
using Windows.Win32.Foundation;
using static Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests.CaptureTestWindows;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
[DoNotParallelize]
public class WindowCaptureSessionLifecycleTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void FirstFrame_DeadlineIsBoundedAndDelayedFrameCanSucceed(bool arrives, bool blank)
    {
        long now = 100;
        var root = new Grabber();
        var secondaryCapture = new Grabber { HasFrame = blank, Pixels = blank ? new byte[4] : [0, 0, 255, 255] };
        var popups = new List<WindowCaptureSession.SecondaryWindow>();
        var logger = new CaptureLogger();
        using var composite = new WindowCaptureSession(root, () => new(0, 0, 1, 1),
            () => popups.ToList(), _ => secondaryCapture, () => now, logger: logger);
        var healthy = composite.TryGetLatest()!.Value;
        popups.Add(new(42, new(0, 0, 1, 1)));
        Assert.IsNull(composite.TryGetLatest());
        now = 2099;
        Assert.IsNull(composite.TryGetLatest());
        if (arrives)
        {
            secondaryCapture.HasFrame = true;
            secondaryCapture.Pixels = [0, 255, 0, 255];
            var frame = composite.TryGetLatest()!.Value;
            CollectionAssert.AreEqual(secondaryCapture.Pixels, frame.Pixels);
            Assert.AreEqual(healthy.Version + 1, frame.Version);
            now = 10000;
            Assert.AreEqual(frame.Version, composite.TryGetLatest()!.Value.Version);
        }
        else
        {
            now = 2100;
            var error = Assert.ThrowsExactly<WindowCaptureException>(() => composite.TryGetLatest());
            Assert.IsInstanceOfType<TimeoutException>(error.InnerException);
            StringAssert.Contains(error.Message, "42");
            Assert.IsTrue(logger.Messages.Any(m => m.Level == LogLevel.Error && m.Text.Contains("42")));
            popups.Clear();
            Assert.AreSame(root.Pixels, composite.TryGetLatest()!.Value.Pixels);
            Assert.IsTrue(secondaryCapture.Disposed);
        }

        composite.Dispose();
        Assert.IsTrue(root.Disposed);
        Assert.IsTrue(secondaryCapture.Disposed);
    }

    [TestMethod]
    public void Sampling_TransientBlankSecondaryFramePreservesHealthyImageUntilPaintedFrameReturns()
    {
        var root = new Grabber();
        var secondaryCapture = new Grabber { Pixels = [0, 255, 0, 255] };
        using var composite = new WindowCaptureSession(root, () => new(0, 0, 1, 1),
            () => [new(42, new(0, 0, 1, 1))], _ => secondaryCapture);
        var healthy = composite.TryGetLatest()!.Value;
        secondaryCapture.Pixels = new byte[4];
        secondaryCapture.Version++;
        Assert.IsNull(composite.TryGetLatest());
        CollectionAssert.AreEqual(new byte[] { 0, 255, 0, 255 }, healthy.Pixels);
        secondaryCapture.Pixels = [0, 0, 0, 255];
        secondaryCapture.Version++;
        var painted = composite.TryGetLatest()!.Value;
        CollectionAssert.AreEqual(secondaryCapture.Pixels, painted.Pixels);
        Assert.AreEqual(healthy.Version + 1, painted.Version);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Startup_DisappearingPopupIsSkippedBeforeOrDuringWgcStartup(bool duringStart)
    {
        var visible = true;
        var discoveries = 0;
        var starts = 0;
        var logger = new CaptureLogger();
        var root = new Grabber();
        using var composite = new WindowCaptureSession(root, () => new(0, 0, 1, 1),
            () =>
            {
                if (!duringStart && ++discoveries > 1)
                {
                    visible = false;
                }
                return visible ? [new(42, new(0, 0, 1, 1))] : [];
            },
            _ =>
            {
                starts++;
                visible = false;
                throw new COMException("CreateForWindow", unchecked((int)0x80070057));
            }, logger: logger);
        Assert.AreSame(root.Pixels, composite.TryGetLatest()!.Value.Pixels);
        Assert.AreEqual(duringStart ? 1 : 0, starts);
        Assert.IsTrue(logger.Messages.Any(m => m.Level == LogLevel.Debug && m.Text.Contains("42")));
    }

    [TestMethod]
    [DataRow(unchecked((int)0x80004005))]
    [DataRow(unchecked((int)0x80070005))]
    [DataRow(unchecked((int)0x80070057))]
    public void Startup_StillQualifyingPopupFailurePropagatesAndIsLogged(int hresult)
    {
        var failure = new COMException("Non-classified WGC startup failure", hresult);
        var logger = new CaptureLogger();
        using var composite = new WindowCaptureSession(new Grabber(), () => new(0, 0, 1, 1),
            () => [new(42, new(0, 0, 1, 1))], _ => throw failure, logger: logger);
        Assert.AreSame(failure, Assert.ThrowsExactly<WindowCaptureException>(() => composite.TryGetLatest()).InnerException);
        Assert.IsTrue(logger.Messages.Any(m => m.Level == LogLevel.Error));
    }

    [TestMethod]
    [DataRow(0, 255, 0)]
    [DataRow(0, 0, 0)]
    public async Task Startup_InvalidArgumentUsesNativeWindowOnlyCaptureIncludingLegitimateBlack(int red, int green, int blue)
    {
        if (ForegroundGuard.NoInteractiveDesktop())
        {
            Assert.Inconclusive("Native PrintWindow requires an interactive desktop.");
        }
        using var fixture = new UiaTestFixture();
        PopupWindow? popup = null;
        var screen = UiAutomationService.s_captureFromScreenScaled;
        var foreground = UiAutomationService.s_foregroundWindowForBlankRetry;
        var sleep = UiAutomationService.s_sleepForBlankRetry;
        try
        {
            nint handle = 0;
            fixture.OnUiThread(() =>
            {
                popup = new PopupWindow
                {
                    FormBorderStyle = FormBorderStyle.None,
                    BackColor = System.Drawing.Color.FromArgb(red, green, blue),
                    Size = new(32, 32)
                };
                popup.Show(fixture.Form);
                popup.Refresh();
                handle = popup.Handle;
            });
            UiAutomationService.s_captureFromScreenScaled = (_, _, _, _, _, _) => throw new AssertFailedException("Screen capture is forbidden.");
            UiAutomationService.s_foregroundWindowForBlankRetry = _ => Assert.Fail("Foreground retry is forbidden.");
            UiAutomationService.s_sleepForBlankRetry = _ => Assert.Fail("Blank retry is forbidden.");
            var visible = true;
            var starts = 0;
            var logger = new CaptureLogger();
            var bounds = WindowCaptureSession.GetBounds(new HWND(handle));
            var width = bounds.Right - bounds.Left;
            var height = bounds.Bottom - bounds.Top;
            var root = new Grabber { Width = width, Height = height, Pixels = new byte[width * height * 4] };
            for (var i = 0; i < root.Pixels.Length; i += 4)
            {
                root.Pixels[i + 2] = 255;
                root.Pixels[i + 3] = 255;
            }
            using var composite = new WindowCaptureSession(root, () => bounds,
                () => visible ? [new(handle, bounds)] : [],
                _ =>
                {
                    starts++;
                    WgcCapture.CheckCreateForWindowResult(new HWND(handle), unchecked((int)0x80070057));
                    throw new AssertFailedException("CreateForWindow failure must throw.");
                }, logger: logger);
            Assert.IsTrue(await composite.WaitForFirstFrameAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
            var frame = await WaitForColor((byte)blue, (byte)green, (byte)red, -1);
            Assert.AreEqual(frame.Version, composite.TryGetLatest()!.Value.Version);
            Assert.AreEqual(1, starts);
            Assert.IsTrue(logger.Messages.Any(m => m.Level == LogLevel.Warning && m.Text.Contains("PrintWindow")));
            fixture.OnUiThread(() =>
            {
                popup!.BackColor = System.Drawing.Color.Blue;
                popup.Refresh();
            });
            var changed = await WaitForColor(255, 0, 0, frame.Version);
            Assert.IsTrue(changed.Version > frame.Version);
            visible = false;
            CollectionAssert.AreEqual(root.Pixels, composite.TryGetLatest()!.Value.Pixels);

            async Task<(byte[] Pixels, int Width, int Height, long Version)> WaitForColor(byte b, byte g, byte r, long after)
            {
                var deadline = Environment.TickCount64 + 2000;
                (byte[] Pixels, int Width, int Height, long Version) result = default;
                var offset = ((height / 2) * width + width / 2) * 4;
                do
                {
                    result = composite.TryGetLatest()!.Value;
                    if (result.Version > after && InteriorMatches(result.Pixels))
                    {
                        Assert.AreEqual(width, result.Width);
                        Assert.AreEqual(height, result.Height);
                        return result;
                    }
                    await Task.Delay(20);
                } while (Environment.TickCount64 < deadline);
                CollectionAssert.AreEqual(new byte[] { b, g, r, 255 }, result.Pixels[offset..(offset + 4)]);
                Assert.IsTrue(InteriorMatches(result.Pixels), "The central 8x8 physical popup pixels must all match the expected opaque color.");
                Assert.Fail("Popup frame version did not advance.");
                return result;

                bool InteriorMatches(byte[] pixels)
                {
                    for (var y = height / 2 - 4; y < height / 2 + 4; y++)
                    {
                        for (var x = width / 2 - 4; x < width / 2 + 4; x++)
                        {
                            var index = (y * width + x) * 4;
                            if (pixels[index] != b || pixels[index + 1] != g ||
                                pixels[index + 2] != r || pixels[index + 3] != 255)
                            {
                                return false;
                            }
                        }
                    }
                    return true;
                }
            }
        }
        finally
        {
            UiAutomationService.s_captureFromScreenScaled = screen;
            UiAutomationService.s_foregroundWindowForBlankRetry = foreground;
            UiAutomationService.s_sleepForBlankRetry = sleep;
            fixture.OnUiThread(() => popup?.Dispose());
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Sampling_ChildFailureIsHarmlessOnlyIfChildDisappeared(bool disappears)
    {
        var visible = true;
        var failure = new Win32Exception(1400);
        using var composite = new WindowCaptureSession(new Grabber(), () => new(0, 0, 1, 1),
            () => visible ? [new(42, new(0, 0, 1, 1))] : [],
            _ => new Grabber
            {
                OnSample = () =>
                {
                    visible = !disappears;
                    throw failure;
                }
            });
        if (disappears)
        {
            Assert.IsNull(composite.TryGetLatest());
            CollectionAssert.AreEqual(new byte[] { 0, 0, 255, 255 }, composite.TryGetLatest()!.Value.Pixels);
        }
        else
        {
            Assert.AreSame(failure, Assert.ThrowsExactly<WindowCaptureException>(() => composite.TryGetLatest()).InnerException);
        }
    }

    [TestMethod]
    public void Startup_ActualHwndDestructionDuringWgcInitializationContinuesRootCapture()
    {
        if (!WgcCapture.IsSupported() || ForegroundGuard.NoInteractiveDesktop())
        {
            Assert.Inconclusive("Native WGC startup requires an interactive desktop.");
        }
        using var fixture = new UiaTestFixture();
        PopupWindow? popup = null;
        try
        {
            nint handle = 0;
            fixture.OnUiThread(() =>
            {
                popup = new PopupWindow();
                popup.Show(fixture.Form);
                handle = popup.Handle;
            });
            var rootHandle = new HWND(fixture.Hwnd);
            var childHandle = new HWND(handle);
            var pid = RealOwnedWindowFinder.s_getWindowProcessId(rootHandle);
            var logger = new CaptureLogger();
            var root = new Grabber();
            RealOwnedWindowFinder.s_findNextTopLevelWindow = after => after.IsNull ? childHandle : HWND.Null;
            using var composite = new WindowCaptureSession(root, () => new(0, 0, 1, 1),
                () => WindowCaptureSession.Discover(rootHandle, pid, logger, _ => new(0, 0, 10, 10)),
                hwnd =>
                {
                    fixture.OnUiThread(() => popup!.Dispose());
                    return WgcCapture.StartSingleWindowGrabber(new HWND(hwnd), NullLogger.Instance);
                }, logger: logger);
            Assert.AreSame(root.Pixels, composite.TryGetLatest()!.Value.Pixels);
            Assert.IsFalse(composite.IsClosed);
            Assert.IsTrue(logger.Messages.Any(m => m.Level == LogLevel.Debug && m.Text.Contains("during capture startup")));
        }
        finally
        {
            RealOwnedWindowFinder.ResetNativeSeams();
            fixture.OnUiThread(() => popup?.Dispose());
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RootClosure_DuringDiscoveryDrainsBeforeOrAfterWgcClosedEvent(bool nativeEvent)
    {
        var root = new Grabber();
        var secondaryCapture = new Grabber();
        var valid = true;
        var closeOnDiscovery = false;
        var logger = new CaptureLogger();
        using var composite = new WindowCaptureSession(root, () => new(0, 0, 1, 1),
            () =>
            {
                if (closeOnDiscovery)
                {
                    valid = false;
                    root.IsClosed = nativeEvent;
                    return WindowCaptureSession.Discover(HWND.Null, 0, logger);
                }
                return [new(42, new(0, 0, 1, 1))];
            }, _ => secondaryCapture, isCaptureTargetValid: () => valid, logger: logger);
        composite.TryGetLatest();
        secondaryCapture.Version++;
        var healthy = composite.TryGetLatest()!.Value;
        closeOnDiscovery = true;
        var final = composite.TryGetLatest()!.Value;
        Assert.IsTrue(composite.IsClosed);
        Assert.IsTrue(secondaryCapture.Disposed);
        Assert.AreEqual(healthy.Version, final.Version);
        Assert.AreSame(healthy.Pixels, final.Pixels);
        Assert.AreEqual(final.Version, composite.TryGetLatest()!.Value.Version);
        Assert.IsTrue(logger.Messages.Any(m => m.Level == LogLevel.Debug));
    }

    [TestMethod]
    public void RootClosure_InvalidBeforeSamplingDoesNotRequireWgcClosedEvent()
    {
        var valid = true;
        var root = new Grabber();
        using var composite = new WindowCaptureSession(root, () => new(0, 0, 1, 1),
            () => [], _ => throw new AssertFailedException(), isCaptureTargetValid: () => valid);
        var first = composite.TryGetLatest()!.Value;
        valid = false;
        Assert.IsTrue(composite.IsClosed);
        var final = composite.TryGetLatest()!.Value;
        Assert.AreEqual(first.Version, final.Version);
        Assert.AreSame(first.Pixels, final.Pixels);
    }

    [TestMethod]
    public void RootClosure_DuringRootBoundsQueryDrainsNativeQueryFailure()
    {
        var root = new Grabber();
        var valid = true;
        var failBounds = false;
        using var composite = new WindowCaptureSession(root, () =>
            {
                if (failBounds)
                {
                    valid = false;
                    throw new Win32Exception(1400);
                }
                return new(0, 0, 1, 1);
            }, () => [], _ => throw new AssertFailedException(), isCaptureTargetValid: () => valid);
        var first = composite.TryGetLatest()!.Value;
        failBounds = true;
        var final = composite.TryGetLatest()!.Value;
        Assert.IsTrue(composite.IsClosed);
        Assert.AreEqual(first.Version, final.Version);
        Assert.AreSame(first.Pixels, final.Pixels);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Discovery_UnexpectedErrorIsNotSuppressedEvenWhenRootCloses(bool close)
    {
        var root = new Grabber();
        var failure = new InvalidOperationException("Unexpected discovery defect");
        using var composite = new WindowCaptureSession(root, () => new(0, 0, 1, 1),
            () =>
            {
                root.IsClosed = close;
                throw failure;
            }, _ => throw new AssertFailedException());
        Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(() => composite.TryGetLatest()));
    }

    [TestMethod]
    public void Discovery_UnrelatedComFailureIsNotSuppressedWhenRootCloses()
    {
        var root = new Grabber();
        var failure = new COMException("Unrelated discovery failure");
        using var composite = new WindowCaptureSession(root, () => new(0, 0, 1, 1),
            () =>
            {
                root.IsClosed = true;
                throw failure;
            }, _ => throw new AssertFailedException());
        Assert.AreSame(failure, Assert.ThrowsExactly<COMException>(() => composite.TryGetLatest()));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Discovery_EmptyChildIsExcludedButEmptyRootIsFatal(bool emptyRoot)
    {
        WithOwnedPopup((root, popup, pid) =>
        {
            var results = new Func<List<WindowCaptureSession.SecondaryWindow>>(() =>
                WindowCaptureSession.Discover(root, pid, NullLogger.Instance,
                    hwnd => hwnd == (emptyRoot ? root : popup) ? new(0, 0, 0, 0) : new(0, 0, 10, 10)));
            if (emptyRoot)
            {
                Assert.ThrowsExactly<InvalidOperationException>(() => results());
            }
            else
            {
                Assert.AreEqual(0, results().Count);
            }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Discovery_FailedChildBoundsSkipsOnlyWhenChildDisappeared(bool disappears)
    {
        WithOwnedPopup((root, popup, pid) =>
        {
            var logger = new CaptureLogger();
            var failure = new Win32Exception(1400);
            var results = new Func<List<WindowCaptureSession.SecondaryWindow>>(() =>
                WindowCaptureSession.Discover(root, pid, logger, hwnd =>
                {
                    if (hwnd == popup)
                    {
                        if (disappears)
                        {
                            RealOwnedWindowFinder.s_isWindowVisible = _ => false;
                        }
                        throw failure;
                    }
                    return new(0, 0, 10, 10);
                }));
            if (disappears)
            {
                Assert.AreEqual(0, results().Count);
                Assert.IsTrue(logger.Messages.Any(m => m.Level == LogLevel.Debug));
            }
            else
            {
                Assert.AreSame(failure, Assert.ThrowsExactly<WindowCaptureException>(() => results()).InnerException);
            }
        });
    }

    [TestMethod]
    public async Task FirstFrame_OverallDeadlineDistinguishesPendingPopupFromMissingRootFrame()
    {
        using var composite = new WindowCaptureSession(new Grabber(), () => new(0, 0, 1, 1),
            () => [new(42, new(0, 0, 1, 1))], _ => new Grabber { HasFrame = false }, () => 100);
        var failure = await Assert.ThrowsExactlyAsync<WindowCaptureException>(() =>
            composite.WaitForFirstFrameAsync(TimeSpan.Zero, CancellationToken.None));
        Assert.IsInstanceOfType<TimeoutException>(failure.InnerException);

        using var missingRoot = new WindowCaptureSession(new Grabber { HasFrame = false },
            () => new(0, 0, 1, 1), () => [], _ => throw new AssertFailedException());
        Assert.IsFalse(await missingRoot.WaitForFirstFrameAsync(TimeSpan.Zero, CancellationToken.None));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TargetedOwnedToolPopup_ItemRejectionCapturesExactPixelsForScreenshotAndRecordingStartup(bool recording)
    {
        if (ForegroundGuard.NoInteractiveDesktop())
        {
            Assert.Inconclusive("Native popup capture requires an interactive desktop.");
        }
        using var fixture = new UiaTestFixture();
        PopupWindow? popup = null;
        var start = WgcCapture.s_startGrabber;
        var screen = UiAutomationService.s_captureFromScreenScaled;
        var foreground = UiAutomationService.s_foregroundWindowForBlankRetry;
        var blankRetry = UiAutomationService.s_sleepForBlankRetry;
        IFrameGrabber? grabber = null;
        try
        {
            nint handle = 0;
            fixture.OnUiThread(() =>
            {
                popup = new PopupWindow
                {
                    FormBorderStyle = FormBorderStyle.None,
                    Size = new(48, 48),
                    BackColor = System.Drawing.Color.Lime
                };
                popup.Show(fixture.Form);
                popup.Refresh();
                handle = popup.Handle;
            });
            var starts = 0;
            WgcCapture.s_startGrabber = (window, logger, fps) => WindowCaptureSession.Start(window, logger, fps,
                (candidate, _, rate) =>
                {
                    Assert.AreEqual(handle, (nint)candidate);
                    Assert.AreEqual(recording ? 15 : 0, rate);
                    starts++;
                    WgcCapture.CheckCreateForWindowResult(candidate, unchecked((int)0x80070057));
                    throw new AssertFailedException();
                });
            UiAutomationService.s_captureFromScreenScaled = (_, _, _, _, _, _) => throw new AssertFailedException("Screen capture is forbidden.");
            UiAutomationService.s_foregroundWindowForBlankRetry = _ => Assert.Fail("Foreground retry is forbidden.");
            UiAutomationService.s_sleepForBlankRetry = _ => Assert.Fail("Blank retry is forbidden.");
            if (recording)
            {
                var backend = new WgcWindowCapture(NullLogger<WgcWindowCapture>.Instance);
                grabber = backend.StartFrameGrabber(handle, 15);
                Assert.IsTrue(await grabber.WaitForFirstFrameAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
            }
            var bounds = WindowCaptureSession.GetBounds(new HWND(handle));
            var deadline = Environment.TickCount64 + 2000;
            byte[] pixels;
            int width;
            int height;
            do
            {
                if (grabber is null)
                {
                    (pixels, width, height) = await WgcCapture.CaptureAsync(new HWND(handle), NullLogger.Instance, CancellationToken.None);
                }
                else
                {
                    var frame = grabber.TryGetLatest()!.Value;
                    (pixels, width, height) = (frame.Pixels, frame.Width, frame.Height);
                }
                if (InteriorIsGreen(pixels, width, height))
                {
                    break;
                }
                await Task.Delay(20);
            } while (Environment.TickCount64 < deadline);
            Assert.AreEqual(bounds.Right - bounds.Left, width);
            Assert.AreEqual(bounds.Bottom - bounds.Top, height);
            Assert.IsTrue(InteriorIsGreen(pixels, width, height), "All central 8x8 physical pixels must be opaque lime.");
            Assert.IsTrue(starts > 0);
            if (recording)
            {
                Assert.AreEqual(1, starts);
                fixture.OnUiThread(() => popup!.Close());
                Assert.IsTrue(grabber!.IsClosed);
                Assert.IsNotNull(grabber.TryGetLatest());
            }
        }
        finally
        {
            grabber?.Dispose();
            WgcCapture.s_startGrabber = start;
            UiAutomationService.s_captureFromScreenScaled = screen;
            UiAutomationService.s_foregroundWindowForBlankRetry = foreground;
            UiAutomationService.s_sleepForBlankRetry = blankRetry;
            fixture.OnUiThread(() => popup?.Dispose());
        }

        static bool InteriorIsGreen(byte[] pixels, int width, int height)
        {
            for (var y = height / 2 - 4; y < height / 2 + 4; y++)
            {
                for (var x = width / 2 - 4; x < width / 2 + 4; x++)
                {
                    var index = (y * width + x) * 4;
                    if (pixels[index] != 0 || pixels[index + 1] != 255 || pixels[index + 2] != 0 || pixels[index + 3] != 255)
                    {
                        return false;
                    }
                }
            }
            return true;
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void TargetedRoot_OrdinaryWindowRejectionAndGenericPopupInvalidArgumentStillPropagate(int kind)
    {
        if (ForegroundGuard.NoInteractiveDesktop())
        {
            Assert.Inconclusive("Native popup classification requires an interactive desktop.");
        }
        using var fixture = new UiaTestFixture();
        PopupWindow? popup = null;
        try
        {
            var window = new HWND(fixture.Hwnd);
            if (kind != 0)
            {
                fixture.OnUiThread(() =>
                {
                    popup = new PopupWindow { FormBorderStyle = FormBorderStyle.None };
                    if (kind == 2)
                    {
                        popup.Show(fixture.Form);
                    }
                    else
                    {
                        popup.Show();
                    }
                    window = new HWND(popup.Handle);
                });
            }
            COMException failure = kind == 2
                ? new COMException("Device conversion E_INVALIDARG", unchecked((int)0x80070057))
                : new WgcCapture.UnsupportedCaptureWindowException(window);
            if (kind == 2)
            {
                Assert.AreSame(failure, Assert.ThrowsExactly<COMException>(() =>
                    WindowCaptureSession.Start(window, NullLogger.Instance, 0, (_, _, _) => throw failure)));
            }
            else
            {
                Assert.AreSame(failure, Assert.ThrowsExactly<WgcCapture.UnsupportedCaptureWindowException>(() =>
                    WindowCaptureSession.Start(window, NullLogger.Instance, 0, (_, _, _) => throw failure)));
            }
        }
        finally
        {
            fixture.OnUiThread(() => popup?.Dispose());
        }
    }

    [TestMethod]
    public void RootClosure_StrictGdiDrainsCachedFrameWithoutSchedulingAnotherCapture()
    {
        WithOwnedPopup((_, secondaryCapture, _) =>
        {
            var calls = 0;
            var valid = true;
            using var root = new WindowCaptureFallback(secondaryCapture,
                () => (new byte[] { (byte)Interlocked.Increment(ref calls), 0, 0, 255 }, 1, 1));
            using var composite = new WindowCaptureSession(root, () => new(0, 0, 1, 1),
                () => [], _ => throw new AssertFailedException(), isCaptureTargetValid: () => valid);
            var deadline = Environment.TickCount64 + 2000;
            var healthy = composite.TryGetLatest();
            while (healthy is null && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(5);
                healthy = composite.TryGetLatest();
            }
            Assert.IsNotNull(healthy);
            Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 2, TimeSpan.FromSeconds(2)),
                "One replacement render is already scheduled while the root is still valid.");
            Assert.IsFalse(root.IsClosed, "The native HWND remains visible while the compositor identity is invalidated.");
            valid = false;
            Assert.IsTrue(composite.IsClosed);
            var final = composite.TryGetLatest()!.Value;
            Assert.AreEqual(2, calls);
            Assert.AreEqual(healthy.Value.Version, final.Version);
            Assert.AreSame(healthy.Value.Pixels, final.Pixels);
            Assert.AreEqual(final.Version, composite.TryGetLatest()!.Value.Version);
            Assert.AreEqual(2, calls);
        });
    }

    [TestMethod]
    public void RootClosure_WgcStyleFinalFrameStillDrainsAndAdvancesVersion()
    {
        var reads = 0;
        var valid = true;
        var root = new Grabber { OnSample = () => reads++ };
        using var composite = new WindowCaptureSession(root, () => new(0, 0, 1, 1),
            () => [], _ => throw new AssertFailedException(), isCaptureTargetValid: () => valid);
        var healthy = composite.TryGetLatest()!.Value;
        Assert.AreEqual(1, reads);
        root.Version++;
        root.Pixels[0] = 42;
        valid = false;
        var final = composite.TryGetLatest()!.Value;
        Assert.AreEqual(2, reads);
        Assert.AreEqual(healthy.Version + 1, final.Version);
        Assert.AreEqual((byte)42, final.Pixels[0]);
    }

    private sealed class Grabber : IFrameGrabber
    {
        internal byte[] Pixels { get; set; } = [0, 0, 255, 255];
        internal int Width { get; init; } = 1;
        internal int Height { get; init; } = 1;
        internal bool HasFrame { get; set; } = true;
        internal bool Disposed { get; private set; }
        internal long Version { get; set; } = 1;
        internal Action? OnSample { get; init; }
        public bool IsClosed { get; set; }
        public (byte[] Pixels, int Width, int Height, long Version)? TryGetLatest()
        {
            OnSample?.Invoke();
            return HasFrame ? (Pixels, Width, Height, Version) : null;
        }
        public Task<bool> WaitForFirstFrameAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(HasFrame);
        public void Dispose() => Disposed = true;
    }

    private sealed class CaptureLogger : ILogger
    {
        internal List<(LogLevel Level, string Text)> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add((logLevel, formatter(state, exception)));
    }
}

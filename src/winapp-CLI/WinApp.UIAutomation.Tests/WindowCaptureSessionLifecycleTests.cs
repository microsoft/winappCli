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
    [DataRow(false)]
    [DataRow(true)]
    public void Sampling_PopupWithoutPaintedFrameDoesNotHoldBackTheWindow(bool blank)
    {
        var root = new Grabber();
        var secondaryCapture = new Grabber { HasFrame = blank, Pixels = blank ? new byte[4] : [0, 0, 255, 255] };
        var popups = new List<WindowCaptureSession.SecondaryWindow>();
        using var composite = new WindowCaptureSession(root, () => new(0, 0, 1, 1),
            () => popups.ToList(), _ => secondaryCapture);
        var healthy = composite.TryGetLatest()!.Value;
        popups.Add(new(42, new(0, 0, 1, 1)));
        root.Pixels = [255, 0, 0, 255];
        root.Version++;
        var updated = composite.TryGetLatest()!.Value;
        CollectionAssert.AreEqual(root.Pixels, updated.Pixels, "The window keeps updating while the popup has nothing to draw.");
        Assert.AreEqual(healthy.Version + 1, updated.Version);

        secondaryCapture.HasFrame = true;
        secondaryCapture.Pixels = [0, 255, 0, 255];
        secondaryCapture.Version++;
        CollectionAssert.AreEqual(secondaryCapture.Pixels, composite.TryGetLatest()!.Value.Pixels);

        popups.Clear();
        CollectionAssert.AreEqual(root.Pixels, composite.TryGetLatest()!.Value.Pixels);
        Assert.IsTrue(secondaryCapture.Disposed);
        composite.Dispose();
        Assert.IsTrue(root.Disposed);
    }
    [TestMethod]
    public void Sampling_PaintedPopupThatTurnsTransparentNoLongerCoversTheWindow()
    {
        var root = new Grabber();
        var secondaryCapture = new Grabber { Pixels = [0, 255, 0, 255] };
        using var composite = new WindowCaptureSession(root, () => new(0, 0, 1, 1),
            () => [new(42, new(0, 0, 1, 1))], _ => secondaryCapture);
        var healthy = composite.TryGetLatest()!.Value;
        CollectionAssert.AreEqual(new byte[] { 0, 255, 0, 255 }, healthy.Pixels);
        secondaryCapture.Pixels = new byte[4];
        secondaryCapture.Version++;
        var transparent = composite.TryGetLatest()!.Value;
        CollectionAssert.AreEqual(root.Pixels, transparent.Pixels);
        Assert.AreEqual(healthy.Version + 1, transparent.Version);
        secondaryCapture.Pixels = [0, 0, 0, 255];
        secondaryCapture.Version++;
        var painted = composite.TryGetLatest()!.Value;
        CollectionAssert.AreEqual(secondaryCapture.Pixels, painted.Pixels);
        Assert.AreEqual(transparent.Version + 1, painted.Version);
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
    public void Startup_StillQualifyingPopupFailureIsSkippedAndReported(int hresult)
    {
        var failure = new COMException("Non-classified WGC startup failure", hresult);
        var starts = 0;
        var root = new Grabber();
        using var composite = new WindowCaptureSession(root, () => new(0, 0, 1, 1),
            () => [new(42, new(0, 0, 1, 1))], _ => { starts++; throw failure; });
        Assert.AreSame(root.Pixels, composite.TryGetLatest()!.Value.Pixels);
        Assert.AreSame(root.Pixels, composite.TryGetLatest()!.Value.Pixels);
        Assert.AreEqual(1, starts, "A popup that failed to start is not retried every frame.");
        Assert.HasCount(1, composite.Warnings);
        StringAssert.Contains(composite.Warnings.Single(), "HWND 42");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Sampling_ChildFailureLeavesThePopupOutOfLaterFrames(bool disappears)
    {
        var visible = true;
        var failure = new Win32Exception(1400);
        var popup = new Grabber
        {
            OnSample = () =>
            {
                visible = !disappears;
                throw failure;
            }
        };
        using var composite = new WindowCaptureSession(new Grabber(), () => new(0, 0, 1, 1),
            () => visible ? [new(42, new(0, 0, 1, 1))] : [],
            _ => popup);
        Assert.IsNull(composite.TryGetLatest());
        CollectionAssert.AreEqual(new byte[] { 0, 0, 255, 255 }, composite.TryGetLatest()!.Value.Pixels);
        Assert.IsTrue(popup.Disposed);
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
    public void Discovery_FailedChildBoundsLeavesOnlyThatChildOut(bool disappears)
    {
        WithOwnedPopup((root, popup, pid) =>
        {
            var logger = new CaptureLogger();
            var failure = new Win32Exception(1400);
            var results = WindowCaptureSession.Discover(root, pid, logger, hwnd =>
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
            });
            Assert.AreEqual(0, results.Count);
            Assert.IsTrue(logger.Messages.Any(m => m.Level == (disappears ? LogLevel.Debug : LogLevel.Warning)));
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Discovery_PopupThatIsNotDrawnOnScreenIsLeftOut(bool cloak)
    {
        WithOwnedPopup((root, popup, pid) =>
        {
            Assert.HasCount(1, WindowCaptureSession.Discover(root, pid, NullLogger.Instance, _ => new(0, 0, 10, 10)));
            if (cloak)
            {
                var on = 1;
                Assert.AreEqual(0, DwmSetWindowAttribute(popup, 13 /* DWMWA_CLOAK */, ref on, sizeof(int)));
            }
            else
            {
                _ = SetWindowLongW(popup, -20, GetWindowLongW(popup, -20) | 0x00080000 /* WS_EX_LAYERED */);
                Assert.IsTrue(SetLayeredWindowAttributes(popup, 0, 0, 0x2 /* LWA_ALPHA */));
            }
            Assert.IsEmpty(WindowCaptureSession.Discover(root, pid, NullLogger.Instance, _ => new(0, 0, 10, 10)));
        });
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(nint hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);

    [TestMethod]
    public async Task GetFrame_PopupWithoutFrameReturnsWindowButNotAMissingRootFrame()
    {
        var root = new Grabber();
        using var composite = new WindowCaptureSession(root, () => new(0, 0, 1, 1),
            () => [new(42, new(0, 0, 1, 1))], _ => new Grabber { HasFrame = false });
        var frame = await composite.GetFrameAsync(TimeSpan.Zero, CancellationToken.None);
        CollectionAssert.AreEqual(root.Pixels, frame!.Value.Pixels);

        using var missingRoot = new WindowCaptureSession(new Grabber { HasFrame = false },
            () => new(0, 0, 1, 1), () => [], _ => throw new AssertFailedException());
        Assert.IsFalse(await missingRoot.WaitForFirstFrameAsync(TimeSpan.Zero, CancellationToken.None));
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

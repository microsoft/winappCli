// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Owns capture of the selected window and combines its image with separately rendered UI,
/// such as menus, flyouts, tooltips, and floating tool windows. Only visible, intersecting secondaryWindow or tool windows
/// in the same process whose Win32 owner chain leads to the selected window are included.
/// Uses Windows graphics capture first and <see cref="WindowCaptureFallback"/> when
/// Windows rejects a secondary window. Does not create or display UI.
/// </summary>
internal sealed partial class WindowCaptureSession : IFrameGrabber
{
    internal readonly record struct SecondaryWindow(nint Handle, PointerRect Bounds);
    private readonly record struct SecondaryWindowFrameInput(SecondaryWindow SecondaryWindow, IFrameGrabber Grabber, long Version, int Width, int Height);
    private readonly record struct SecondaryWindowSession(IFrameGrabber Capture, long? FirstFrameDeadline);
    private readonly IFrameGrabber _targetWindowCapture;
    private readonly Func<PointerRect> _targetWindowBounds;
    private readonly Func<List<SecondaryWindow>> _discover;
    private readonly Func<nint, IFrameGrabber> _start;
    private readonly Func<long> _clock;
    private readonly Func<bool> _isCaptureTargetValid;
    private readonly ILogger _logger;
    private readonly Dictionary<nint, SecondaryWindowSession> _secondaryWindowSessions = [];
    private readonly Lock _lock = new();
    private bool _disposed;
    private bool _closed;
    private long _combinedFrameVersion;
    private long _lastComposedTargetWindowVersion;
    private PointerRect _lastComposedTargetWindowBounds;
    private SecondaryWindowFrameInput[] _lastComposedSecondaryInputs = [];
    private (byte[] Pixels, int Width, int Height, long Version)? _latestCombinedFrame;

    internal WindowCaptureSession(IFrameGrabber targetCapture, Func<PointerRect> targetWindowBounds,
        Func<List<SecondaryWindow>> discover, Func<nint, IFrameGrabber> start,
        Func<long>? clock = null, Func<bool>? isCaptureTargetValid = null, ILogger? logger = null, int? expectedPid = null)
    {
        _targetWindowCapture = targetCapture;
        _targetWindowBounds = targetWindowBounds;
        _discover = discover;
        _clock = clock ?? (() => Environment.TickCount64);
        _isCaptureTargetValid = isCaptureTargetValid ?? (() => true);
        _logger = logger ?? NullLogger.Instance;
        // Injected discovery, like native discovery, supplies already-classified eligible windows.
        _start = handle => StartWindowCapture(new HWND(handle), _logger, 0,
            (window, _, _) => start((nint)window),
            () => _discover().Any(p => p.Handle == handle), expectedPid);
    }

    internal static WindowCaptureSession Start(HWND hwnd, ILogger logger, int fps,
        Func<HWND, ILogger, int, IFrameGrabber>? startWindow = null)
    {
        startWindow ??= (window, log, rate) => WgcCapture.StartSingleWindowGrabber(window, log, rate);
        var pid = RealOwnedWindowFinder.s_getWindowProcessId(hwnd);
        var targetWindow = StartWindowCapture(hwnd, logger, fps, startWindow,
            () => IsEligibleSecondaryWindow(hwnd, pid), pid);
        return new(targetWindow,
            () => GetBounds(hwnd), () => Discover(hwnd, pid, logger),
            secondaryHandle => startWindow(new HWND(secondaryHandle), logger, fps),
            isCaptureTargetValid: () => IsWindowFromExpectedProcess(hwnd, pid), logger: logger, expectedPid: pid);
    }

    internal static IFrameGrabber StartWindowCapture(HWND hwnd, ILogger logger, int fps,
        Func<HWND, ILogger, int, IFrameGrabber> startWindow, Func<bool> isFallbackEligible, int? expectedPid = null)
    {
        try
        {
            return startWindow(hwnd, logger, fps);
        }
        catch (WgcCapture.UnsupportedCaptureWindowException ex) when (isFallbackEligible())
        {
            logger.LogWarning(ex, "Windows graphics capture rejected window {Hwnd}; using window-only rendering (PrintWindow).", (nint)hwnd);
            return new WindowCaptureFallback(hwnd, logger: logger, expectedPid: expectedPid);
        }
    }

    private static bool IsEligibleSecondaryWindow(HWND hwnd, int pid)
    {
        if (pid == 0 || !IsWindowFromExpectedProcess(hwnd, pid) || !RealOwnedWindowFinder.s_isWindowVisible(hwnd))
        {
            return false;
        }
        var owner = RealOwnedWindowFinder.s_getWindowOwner(hwnd);
        if (owner.IsNull || !IsWindowFromExpectedProcess(owner, pid))
        {
            return false;
        }
        return ((uint)GetWindowLong((nint)hwnd, -16) & 0x80000000) != 0 ||
            ((uint)GetWindowLong((nint)hwnd, -20) & 0x00000080) != 0;
    }

    private static (byte[] Pixels, int Width, int Height, long Version)? ReadWindowFrame(
        IFrameGrabber capture, nint? secondaryHandle = null)
    {
        try
        {
            return capture.TryGetLatest();
        }
        catch (Exception ex) when (ex is not OperationCanceledException &&
            (secondaryHandle.HasValue || capture is WindowCaptureFallback))
        {
            throw new WindowCaptureException(
                secondaryHandle ?? ((WindowCaptureFallback)capture).WindowHandle, ex);
        }
    }

    public string RootCaptureBackend => _targetWindowCapture.RootCaptureBackend;

    public bool IsClosed => _closed || _targetWindowCapture.IsClosed || !_isCaptureTargetValid();

    public (byte[] Pixels, int Width, int Height, long Version)? TryGetLatest()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsClosed)
            {
                return DrainClosedTargetWindow();
            }

            try
            {
                return Sample();
            }
            catch (Exception ex) when (
                (ex is CaptureTargetClosedException or Win32Exception) && IsClosed)
            {
                _logger.LogDebug(ex, "Selected capture window closed during secondary-window discovery; draining the final frame.");
                return DrainClosedTargetWindow();
            }
        }
    }

    private (byte[] Pixels, int Width, int Height, long Version)? DrainClosedTargetWindow()
    {
        if (!_closed)
        {
            _logger.LogDebug("Selected capture window is no longer valid; releasing secondary-window capture sessions.");
        }
        _closed = true;
        DisposeSecondaryWindowSessions();
        if (_targetWindowCapture is WindowCaptureFallback)
        {
            return _latestCombinedFrame;
        }
        var finalTargetFrame = ReadWindowFrame(_targetWindowCapture);
        if (finalTargetFrame is not null && (_latestCombinedFrame is null || finalTargetFrame.Value.Version != _lastComposedTargetWindowVersion))
        {
            _lastComposedTargetWindowVersion = finalTargetFrame.Value.Version;
            _latestCombinedFrame = (finalTargetFrame.Value.Pixels, finalTargetFrame.Value.Width, finalTargetFrame.Value.Height, ++_combinedFrameVersion);
        }
        return _latestCombinedFrame;
    }

    private (byte[] Pixels, int Width, int Height, long Version)? Sample()
    {
        var secondaryWindows = _discover();
        var handles = secondaryWindows.Select(p => p.Handle).ToHashSet();
        foreach (var handle in _secondaryWindowSessions.Keys.ToArray())
        {
            if (!handles.Contains(handle) || _secondaryWindowSessions[handle].Capture.IsClosed)
            {
                _secondaryWindowSessions[handle].Capture.Dispose();
                _secondaryWindowSessions.Remove(handle);
            }
        }
        for (var i = secondaryWindows.Count - 1; i >= 0; i--)
        {
            var secondaryWindow = secondaryWindows[i];
            if (!_secondaryWindowSessions.ContainsKey(secondaryWindow.Handle))
            {
                if (!_discover().Any(p => p.Handle == secondaryWindow.Handle))
                {
                    _logger.LogDebug("Secondary window {Hwnd} disappeared before capture startup.", secondaryWindow.Handle);
                    secondaryWindows.RemoveAt(i);
                    continue;
                }
                try
                {
                    _secondaryWindowSessions.Add(secondaryWindow.Handle, new(_start(secondaryWindow.Handle), _clock() + 2000));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (_discover().Any(p => p.Handle == secondaryWindow.Handle))
                    {
                        _logger.LogError(ex, "Windows graphics capture startup failed for still-visible secondary window {Hwnd}.", secondaryWindow.Handle);
                        throw new WindowCaptureException(secondaryWindow.Handle, ex);
                    }
                    _logger.LogDebug(ex, "Secondary window {Hwnd} disappeared during capture startup.", secondaryWindow.Handle);
                    secondaryWindows.RemoveAt(i);
                }
            }
        }

        var targetFrame = ReadWindowFrame(_targetWindowCapture);
        if (targetFrame is null)
        {
            return null;
        }
        var bounds = _targetWindowBounds();
        var result = targetFrame.Value;
        var inputs = new SecondaryWindowFrameInput[secondaryWindows.Count];
        var secondaryWindowFrames = new (byte[] Pixels, int Width, int Height, long Version)[secondaryWindows.Count];
        for (var i = 0; i < secondaryWindows.Count; i++)
        {
            var session = _secondaryWindowSessions[secondaryWindows[i].Handle];
            var grabber = session.Capture;
            (byte[] Pixels, int Width, int Height, long Version)? secondaryFrame;
            try
            {
                secondaryFrame = ReadWindowFrame(grabber, secondaryWindows[i].Handle);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!_discover().Any(p => p.Handle == secondaryWindows[i].Handle))
                {
                    _logger.LogDebug(ex, "Secondary window {Hwnd} disappeared during capture.", secondaryWindows[i].Handle);
                    return null;
                }
                _logger.LogError(ex, "Capture failed for still-visible secondary window {Hwnd}.", secondaryWindows[i].Handle);
                throw;
            }
            if (secondaryFrame is { } captured && CapturedFrame.IsBlank(captured.Pixels))
            {
                if (session.FirstFrameDeadline is null)
                {
                    return null;
                }
                secondaryFrame = null;
            }
            if (secondaryFrame is null)
            {
                if (session.FirstFrameDeadline is not { } deadline ||
                    _clock() >= deadline)
                {
                    _logger.LogError("Secondary window {Hwnd} did not produce a capture frame within 2 seconds.", secondaryWindows[i].Handle);
                    throw new WindowCaptureException(secondaryWindows[i].Handle,
                        new TimeoutException($"Secondary window HWND {secondaryWindows[i].Handle} did not produce a capture frame within 2 seconds."));
                }
                return null;
            }
            if (session.FirstFrameDeadline is not null)
            {
                _secondaryWindowSessions[secondaryWindows[i].Handle] = session with { FirstFrameDeadline = null };
            }
            secondaryWindowFrames[i] = secondaryFrame.Value;
            inputs[i] = new(secondaryWindows[i], grabber, secondaryFrame.Value.Version, secondaryFrame.Value.Width, secondaryFrame.Value.Height);
        }
        // Revalidate ownership, handles, bounds and order before publishing cached or new pixels.
        if (!secondaryWindows.SequenceEqual(_discover()))
        {
            return null;
        }
        if (_latestCombinedFrame is not null && result.Version == _lastComposedTargetWindowVersion &&
            result.Width == _latestCombinedFrame.Value.Width && result.Height == _latestCombinedFrame.Value.Height &&
            bounds == _lastComposedTargetWindowBounds && inputs.SequenceEqual(_lastComposedSecondaryInputs))
        {
            return _latestCombinedFrame;
        }

        var pixels = secondaryWindows.Count == 0 ? result.Pixels : (byte[])result.Pixels.Clone();
        // Discovery is topmost-first. Paint the lowest owned secondaryWindow first.
        for (var i = secondaryWindows.Count - 1; i >= 0; i--)
        {
            var secondaryWindow = secondaryWindows[i];
            var secondaryFrame = secondaryWindowFrames[i];
            IncludeSecondaryWindowInWindowImage(pixels, result.Width, result.Height,
                secondaryFrame.Pixels, secondaryFrame.Width, secondaryFrame.Height,
                secondaryWindow.Bounds.Left - bounds.Left, secondaryWindow.Bounds.Top - bounds.Top);
        }
        _lastComposedTargetWindowVersion = result.Version;
        _lastComposedTargetWindowBounds = bounds;
        _lastComposedSecondaryInputs = inputs;
        _latestCombinedFrame = (pixels, result.Width, result.Height, ++_combinedFrameVersion);
        return _latestCombinedFrame;
    }

    public async Task<bool> WaitForFirstFrameAsync(TimeSpan timeout, CancellationToken ct)
        => await GetFrameAsync(timeout, ct).ConfigureAwait(false) is not null;

    public async Task<(byte[] Pixels, int Width, int Height, long Version)?> GetFrameAsync(
        TimeSpan timeout, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        do
        {
            ct.ThrowIfCancellationRequested();
            var frame = TryGetLatest();
            if (frame is not null)
            {
                return frame;
            }
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                break;
            }
            await Task.Delay((int)Math.Min(30, remaining), ct).ConfigureAwait(false);
        }
        while (Environment.TickCount64 < deadline);
        ct.ThrowIfCancellationRequested();
        var finalFrame = TryGetLatest();
        if (finalFrame is not null)
        {
            return finalFrame;
        }
        lock (_lock)
        {
            if (!IsClosed && _targetWindowCapture is WindowCaptureFallback targetFallback && ReadWindowFrame(_targetWindowCapture) is null)
            {
                throw new WindowCaptureException(targetFallback.WindowHandle,
                    new TimeoutException("The selected capture window did not produce a frame before the capture deadline."));
            }
            if (!IsClosed && ReadWindowFrame(_targetWindowCapture) is not null)
            {
                var secondaryWindows = _discover();
                if (secondaryWindows.Count != 0)
                {
                    var error = new TimeoutException("The secondary window did not produce a stable combined image before the capture deadline.");
                    _logger.LogError(error, "Secondary window {Hwnd} prevented the first combined image.", secondaryWindows[0].Handle);
                    throw new WindowCaptureException(secondaryWindows[0].Handle, error);
                }
            }
        }
        return null;
    }

    internal static bool IsOwnedBy(HWND candidate, HWND targetWindow, int pid)
    {
        var seen = new HashSet<nint>();
        while (!candidate.IsNull && seen.Add((nint)candidate))
        {
            if (RealOwnedWindowFinder.s_getWindowProcessId(candidate) != pid)
            {
                return false;
            }
            candidate = RealOwnedWindowFinder.s_getWindowOwner(candidate);
            if (candidate == targetWindow)
            {
                return true;
            }
        }
        return false;
    }

    internal static bool IsWindowFromExpectedProcess(HWND window, int pid)
        => IsWindow((nint)window) && RealOwnedWindowFinder.s_getWindowProcessId(window) == pid;

    internal static List<SecondaryWindow> Discover(HWND targetWindow, int pid, ILogger logger,
        Func<HWND, PointerRect>? getBounds = null)
    {
        if (!IsWindowFromExpectedProcess(targetWindow, pid))
        {
            throw new CaptureTargetClosedException();
        }
        getBounds ??= hwnd => GetBounds(hwnd, requireNonEmpty: false);
        var targetBounds = getBounds(targetWindow);
        if (targetBounds.Right <= targetBounds.Left || targetBounds.Bottom <= targetBounds.Top)
        {
            throw new InvalidOperationException("The selected capture window has empty bounds.");
        }
        var result = new List<SecondaryWindow>();
        var window = HWND.Null;
        while (!(window = RealOwnedWindowFinder.s_findNextTopLevelWindow(window)).IsNull)
        {
            if (window == targetWindow || !IsEligibleSecondaryWindow(window, pid) || !IsOwnedBy(window, targetWindow, pid))
            {
                continue;
            }
            PointerRect bounds;
            try
            {
                bounds = getBounds(window);
            }
            catch (Win32Exception ex)
            {
                if (!IsWindow((nint)window) ||
                    !RealOwnedWindowFinder.s_isWindowVisible(window) || !IsOwnedBy(window, targetWindow, pid))
                {
                    logger.LogDebug(ex, "Secondary window {Hwnd} disappeared during its bounds query.", (nint)window);
                    continue;
                }
                logger.LogError(ex, "Cannot query bounds for still-visible secondary window {Hwnd}.", (nint)window);
                throw new WindowCaptureException((nint)window, ex);
            }
            if (bounds.Right > bounds.Left && bounds.Bottom > bounds.Top &&
                bounds.Left < targetBounds.Right && bounds.Right > targetBounds.Left &&
                bounds.Top < targetBounds.Bottom && bounds.Bottom > targetBounds.Top)
            {
                result.Add(new((nint)window, bounds));
            }
        }
        return result;
    }

    internal static unsafe PointerRect GetBounds(HWND hwnd, bool requireNonEmpty = true)
    {
        if (!PInvoke.GetWindowRect(hwnd, out var rect))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot query capture bounds for HWND {(nint)hwnd}.");
        }
        var visible = rect;
        var hr = PInvoke.DwmGetWindowAttribute(hwnd,
            global::Windows.Win32.Graphics.Dwm.DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS,
            &visible, (uint)sizeof(RECT));
        if (hr.Succeeded)
        {
            rect = visible;
        }
        if (requireNonEmpty && (rect.right <= rect.left || rect.bottom <= rect.top))
        {
            throw new InvalidOperationException("The capture window has empty bounds.");
        }
        return new(rect.left, rect.top, rect.right, rect.bottom);
    }

    // Preserve premultiplied BGRA transparency while clipping the UI image to the captured window.
    internal static void IncludeSecondaryWindowInWindowImage(byte[] targetPixels, int width, int height, byte[] secondaryWindow,
        int secondaryWindowWidth, int secondaryWindowHeight, int left, int top)
    {
        if (targetPixels.Length != checked(width * height * 4) ||
            secondaryWindow.Length != checked(secondaryWindowWidth * secondaryWindowHeight * 4))
        {
            throw new ArgumentException("Capture dimensions do not match the pixel buffers.");
        }

        var startX = Math.Max(0L, left);
        var startY = Math.Max(0L, top);
        var endX = Math.Min((long)width, (long)left + secondaryWindowWidth);
        var endY = Math.Min((long)height, (long)top + secondaryWindowHeight);
        for (var y = startY; y < endY; y++)
        {
            for (var x = startX; x < endX; x++)
            {
                var destination = checked((int)((y * width + x) * 4));
                var source = checked((int)(((y - top) * secondaryWindowWidth + x - left) * 4));
                var inverseAlpha = 255 - secondaryWindow[source + 3];
                for (var channel = 0; channel < 4; channel++)
                {
                    targetPixels[destination + channel] = (byte)Math.Min(255,
                        secondaryWindow[source + channel] + (targetPixels[destination + channel] * inverseAlpha + 127) / 255);
                }
            }
        }
    }

    private void DisposeSecondaryWindowSessions()
    {
        foreach (var session in _secondaryWindowSessions.Values)
        {
            session.Capture.Dispose();
        }

        _secondaryWindowSessions.Clear();
    }

    private sealed class CaptureTargetClosedException() : InvalidOperationException("The selected capture window is no longer valid.");

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong(nint hwnd, int index);

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            DisposeSecondaryWindowSessions();
            _targetWindowCapture.Dispose();
        }
    }
}

internal sealed class WindowCaptureException(nint hwnd, Exception cause)
    : InvalidOperationException($"Capture failed for window HWND {hwnd}: {cause.Message}", cause);

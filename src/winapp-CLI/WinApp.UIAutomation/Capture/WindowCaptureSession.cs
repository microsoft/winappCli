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
/// such as menus, flyouts, tooltips, and floating tool windows. Only visible, intersecting popup or tool windows
/// in the same process whose Win32 owner chain leads to the selected window are included.
/// Uses Windows graphics capture first and <see cref="WindowCaptureFallback"/> when Windows rejects a popup.
/// A popup that cannot be captured is left out and reported in <see cref="Warnings"/>. Does not create or display UI.
/// When started with margins, every frame is the window plus a fixed empty border, so a popup that reaches past
/// the window's edge is drawn in full without the frame ever changing size. <see cref="WindowBounds"/> and
/// <see cref="ContentBounds"/> say which part of the latest frame holds the window and which part holds anything at all.
/// </summary>
internal sealed partial class WindowCaptureSession : IFrameGrabber
{
    internal readonly record struct SecondaryWindow(nint Handle, PointerRect Bounds);
    private readonly record struct SecondaryWindowFrameInput(SecondaryWindow SecondaryWindow, IFrameGrabber Grabber, long Version, int Width, int Height);
    private readonly IFrameGrabber _targetWindowCapture;
    private readonly Func<PointerRect> _targetWindowBounds;
    private readonly Func<List<SecondaryWindow>> _discover;
    private readonly Func<nint, IFrameGrabber> _start;
    private readonly Func<bool> _isCaptureTargetValid;
    private readonly ILogger _logger;
    private readonly Dictionary<nint, IFrameGrabber> _secondaryWindowSessions = [];
    private readonly HashSet<nint> _failedSecondaryWindows = [];
    private readonly HashSet<nint> _reportedSecondaryWindows = [];
    private readonly HashSet<nint> _reportedClippedWindows = [];
    private readonly List<string> _warnings = [];
    private readonly Lock _lock = new();
    private bool _disposed;
    private bool _closed;
    private long _combinedFrameVersion;
    private long _lastComposedTargetWindowVersion;
    private PointerRect _lastComposedTargetWindowBounds;
    private SecondaryWindowFrameInput[] _lastComposedSecondaryInputs = [];
    private (byte[] Pixels, int Width, int Height, long Version)? _latestCombinedFrame;
    // Empty space kept around the window so popups that reach past its edges fit. Fixed for the session.
    private readonly CanvasMargins _margins;
    private PointerRect? _latestWindowBounds;
    private PointerRect? _latestContentBounds;

    internal readonly record struct CanvasMargins(int Left, int Top, int Right, int Bottom)
    {
        internal bool IsEmpty => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;
    }

    internal WindowCaptureSession(IFrameGrabber targetCapture, Func<PointerRect> targetWindowBounds,
        Func<List<SecondaryWindow>> discover, Func<nint, IFrameGrabber> start,
        Func<bool>? isCaptureTargetValid = null, ILogger? logger = null, int? expectedPid = null,
        CanvasMargins margins = default)
    {
        _targetWindowCapture = targetCapture;
        _targetWindowBounds = targetWindowBounds;
        _discover = discover;
        _isCaptureTargetValid = isCaptureTargetValid ?? (() => true);
        _logger = logger ?? NullLogger.Instance;
        _margins = margins;
        // Injected discovery, like native discovery, supplies already-classified eligible windows.
        _start = handle => StartWindowCapture(new HWND(handle), _logger, 0,
            (window, _, _) => start((nint)window),
            () => _discover().Any(p => p.Handle == handle), expectedPid);
    }

    // popupMargin: empty space kept past each window edge for popups (0 for frames that are exactly the window).
    // fixedMargin: keep that space on every side even past the monitor edge, so every session has the same border.
    internal static WindowCaptureSession Start(HWND hwnd, ILogger logger, int fps,
        Func<HWND, ILogger, int, IFrameGrabber>? startWindow = null, bool includeSecondaryWindows = true,
        int popupMargin = 0, bool fixedMargin = false)
    {
        startWindow ??= (window, log, rate) => WgcCapture.StartSingleWindowGrabber(window, log, rate);
        var pid = RealOwnedWindowFinder.s_getWindowProcessId(hwnd);
        var targetWindow = StartWindowCapture(hwnd, logger, fps, startWindow,
            () => IsEligibleSecondaryWindow(hwnd, pid), pid);
        var margins = popupMargin <= 0 ? default
            : fixedMargin ? new CanvasMargins(popupMargin, popupMargin, popupMargin, popupMargin)
            : MarginsWithinMonitor(hwnd, popupMargin);
        return new(targetWindow,
            () => GetBounds(hwnd), includeSecondaryWindows ? () => Discover(hwnd, pid, logger, margins: margins) : () => [],
            secondaryHandle => startWindow(new HWND(secondaryHandle), logger, fps),
            isCaptureTargetValid: () => IsWindowFromExpectedProcess(hwnd, pid), logger: logger, expectedPid: pid,
            margins: margins);
    }

    private static unsafe CanvasMargins MarginsWithinMonitor(HWND hwnd, int margin)
    {
        try
        {
            var window = GetBounds(hwnd);
            var monitor = PInvoke.MonitorFromWindow(hwnd, global::Windows.Win32.Graphics.Gdi.MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
            var info = new global::Windows.Win32.Graphics.Gdi.MONITORINFO { cbSize = (uint)sizeof(global::Windows.Win32.Graphics.Gdi.MONITORINFO) };
            if (monitor.IsNull || !PInvoke.GetMonitorInfo(monitor, ref info))
            {
                return new(margin, margin, margin, margin);
            }
            var screen = info.rcMonitor;
            return ComputeMargins(window, new(screen.left, screen.top, screen.right, screen.bottom), margin);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return default;
        }
    }

    /// <summary>
    /// Room for popups past each edge of <paramref name="window"/>: at most <paramref name="margin"/>, and no
    /// further than the edge of <paramref name="monitor"/>.
    /// </summary>
    internal static CanvasMargins ComputeMargins(PointerRect window, PointerRect monitor, int margin)
        => new(
            Math.Clamp(window.Left - monitor.Left, 0, margin),
            Math.Clamp(window.Top - monitor.Top, 0, margin),
            Math.Clamp(monitor.Right - window.Right, 0, margin),
            Math.Clamp(monitor.Bottom - window.Bottom, 0, margin));

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
        if (pid == 0 || !IsWindowFromExpectedProcess(hwnd, pid) || !RealOwnedWindowFinder.s_isWindowVisible(hwnd) ||
            IsHiddenFromScreen(hwnd))
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

    // Visible to Win32 but not drawn: cloaked by DWM, or a layered window with zero opacity.
    private static unsafe bool IsHiddenFromScreen(HWND hwnd)
    {
        uint cloaked = 0;
        if (PInvoke.DwmGetWindowAttribute(hwnd, global::Windows.Win32.Graphics.Dwm.DWMWINDOWATTRIBUTE.DWMWA_CLOAKED,
                &cloaked, sizeof(uint)).Succeeded && cloaked != 0)
        {
            return true;
        }
        const uint WS_EX_LAYERED = 0x00080000;
        const uint LWA_ALPHA = 0x2;
        return ((uint)GetWindowLong((nint)hwnd, -20) & WS_EX_LAYERED) != 0 &&
            GetLayeredWindowAttributes((nint)hwnd, out _, out var alpha, out var flags) &&
            (flags & LWA_ALPHA) != 0 && alpha == 0;
    }

    private static (byte[] Pixels, int Width, int Height, long Version)? ReadWindowFrame(IFrameGrabber capture)
    {
        try
        {
            return capture.TryGetLatest();
        }
        catch (Exception ex) when (ex is not OperationCanceledException && capture is WindowCaptureFallback fallback)
        {
            throw new WindowCaptureException(fallback.WindowHandle, ex);
        }
    }

    public string RootCaptureBackend => _targetWindowCapture.RootCaptureBackend;

    public IReadOnlyCollection<string> Warnings
    {
        get
        {
            lock (_lock)
            {
                return [.. _warnings];
            }
        }
    }

    public PointerRect? WindowBounds
    {
        get
        {
            lock (_lock)
            {
                return _latestWindowBounds;
            }
        }
    }

    public PointerRect? ContentBounds
    {
        get
        {
            lock (_lock)
            {
                return _latestContentBounds;
            }
        }
    }

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
            PublishWindowOnly(finalTargetFrame.Value);
        }
        return _latestCombinedFrame;
    }

    // Publishes the window without popups, on the same canvas as every other frame.
    private void PublishWindowOnly((byte[] Pixels, int Width, int Height, long Version) frame)
    {
        var (pixels, width, height) = PlaceOnCanvas(frame.Pixels, frame.Width, frame.Height, _margins);
        var window = WindowRectOnCanvas(frame.Width, frame.Height);
        PublishBounds(window, window);
        _latestCombinedFrame = (pixels, width, height, ++_combinedFrameVersion);
    }

    private PointerRect WindowRectOnCanvas(int width, int height)
        => new(_margins.Left, _margins.Top, _margins.Left + width, _margins.Top + height);

    // Bounds are only reported when the frame has margins; a window-only frame is all window.
    private void PublishBounds(PointerRect window, PointerRect content)
    {
        _latestWindowBounds = _margins.IsEmpty ? null : window;
        _latestContentBounds = _margins.IsEmpty ? null : content;
    }

    internal static PointerRect Union(PointerRect a, PointerRect b)
        => new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

    /// <summary>
    /// Copies the window image onto an empty (transparent black) canvas with <paramref name="margins"/> around it.
    /// Returns the window's own buffer when there are no margins.
    /// </summary>
    internal static (byte[] Pixels, int Width, int Height) PlaceOnCanvas(byte[] window, int width, int height, CanvasMargins margins)
    {
        if (margins.IsEmpty)
        {
            return (window, width, height);
        }
        var canvasWidth = checked(width + margins.Left + margins.Right);
        var canvasHeight = checked(height + margins.Top + margins.Bottom);
        var canvas = new byte[checked(canvasWidth * canvasHeight * 4)];
        var rowBytes = width * 4;
        for (var y = 0; y < height; y++)
        {
            Buffer.BlockCopy(window, y * rowBytes, canvas, ((y + margins.Top) * canvasWidth + margins.Left) * 4, rowBytes);
        }
        return (canvas, canvasWidth, canvasHeight);
    }

    private (byte[] Pixels, int Width, int Height, long Version)? Sample()
    {
        var discovered = _discover();
        // Forget failures for windows that closed, so a reused handle gets a fresh attempt.
        _failedSecondaryWindows.IntersectWith(discovered.Select(p => p.Handle));
        var secondaryWindows = discovered.Where(p => !_failedSecondaryWindows.Contains(p.Handle)).ToList();
        var handles = secondaryWindows.Select(p => p.Handle).ToHashSet();
        foreach (var handle in _secondaryWindowSessions.Keys.ToArray())
        {
            if (!handles.Contains(handle) || _secondaryWindowSessions[handle].IsClosed)
            {
                _secondaryWindowSessions[handle].Dispose();
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
                    _secondaryWindowSessions.Add(secondaryWindow.Handle, _start(secondaryWindow.Handle));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (_discover().Any(p => p.Handle == secondaryWindow.Handle))
                    {
                        SkipFailedSecondaryWindow(secondaryWindow.Handle, ex);
                    }
                    else
                    {
                        _logger.LogDebug(ex, "Secondary window {Hwnd} disappeared during capture startup.", secondaryWindow.Handle);
                    }
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
            var grabber = _secondaryWindowSessions[secondaryWindows[i].Handle];
            (byte[] Pixels, int Width, int Height, long Version)? secondaryFrame;
            try
            {
                secondaryFrame = grabber.TryGetLatest();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!_discover().Any(p => p.Handle == secondaryWindows[i].Handle))
                {
                    _logger.LogDebug(ex, "Secondary window {Hwnd} disappeared during capture.", secondaryWindows[i].Handle);
                    return null;
                }
                SkipFailedSecondaryWindow(secondaryWindows[i].Handle, ex);
                grabber.Dispose();
                _secondaryWindowSessions.Remove(secondaryWindows[i].Handle);
                return null;
            }
            // A popup that hasn't produced a frame yet is drawn once it has one, so the selected window keeps
            // recording meanwhile. A fully transparent frame draws nothing, as on screen.
            if (secondaryFrame is null)
            {
                inputs[i] = new(secondaryWindows[i], grabber, -1, 0, 0);
                continue;
            }
            secondaryWindowFrames[i] = secondaryFrame.Value;
            inputs[i] = new(secondaryWindows[i], grabber, secondaryFrame.Value.Version, secondaryFrame.Value.Width, secondaryFrame.Value.Height);
        }
        // Revalidate ownership, handles, bounds and order before publishing cached or new pixels.
        if (!secondaryWindows.SequenceEqual(_discover().Where(p => !_failedSecondaryWindows.Contains(p.Handle))))
        {
            return null;
        }
        if (_latestCombinedFrame is not null && result.Version == _lastComposedTargetWindowVersion &&
            bounds == _lastComposedTargetWindowBounds && inputs.SequenceEqual(_lastComposedSecondaryInputs))
        {
            return _latestCombinedFrame;
        }

        var (pixels, canvasWidth, canvasHeight) = PlaceOnCanvas(result.Pixels, result.Width, result.Height, _margins);
        if (secondaryWindows.Count > 0 && ReferenceEquals(pixels, result.Pixels))
        {
            pixels = (byte[])result.Pixels.Clone();
        }
        var windowOnCanvas = WindowRectOnCanvas(result.Width, result.Height);
        var content = windowOnCanvas;
        // Discovery is topmost-first. Paint the lowest owned secondaryWindow first.
        for (var i = secondaryWindows.Count - 1; i >= 0; i--)
        {
            var secondaryWindow = secondaryWindows[i];
            var secondaryFrame = secondaryWindowFrames[i];
            if (secondaryFrame.Pixels is null)
            {
                continue;
            }
            var left = secondaryWindow.Bounds.Left - bounds.Left + _margins.Left;
            var top = secondaryWindow.Bounds.Top - bounds.Top + _margins.Top;
            IncludeSecondaryWindowInWindowImage(pixels, canvasWidth, canvasHeight,
                secondaryFrame.Pixels, secondaryFrame.Width, secondaryFrame.Height, left, top);
            var popup = new PointerRect(left, top, left + secondaryFrame.Width, top + secondaryFrame.Height);
            var visible = new PointerRect(Math.Max(0, popup.Left), Math.Max(0, popup.Top),
                Math.Min(canvasWidth, popup.Right), Math.Min(canvasHeight, popup.Bottom));
            if (visible.Right > visible.Left && visible.Bottom > visible.Top)
            {
                content = Union(content, visible);
            }
            if (visible != popup && _reportedClippedWindows.Add(secondaryWindow.Handle))
            {
                _warnings.Add($"A popup window (HWND {secondaryWindow.Handle}) reached past the recorded area and is partly cut off.");
            }
        }
        _lastComposedTargetWindowVersion = result.Version;
        _lastComposedTargetWindowBounds = bounds;
        _lastComposedSecondaryInputs = inputs;
        PublishBounds(windowOnCanvas, content);
        _latestCombinedFrame = (pixels, canvasWidth, canvasHeight, ++_combinedFrameVersion);
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
            if (!IsClosed && ReadWindowFrame(_targetWindowCapture) is { } targetOnly)
            {
                // Popups kept moving or appearing until the deadline; the selected window is still worth returning.
                _logger.LogWarning("Secondary windows did not settle before the capture deadline; capturing the selected window without them.");
                _lastComposedSecondaryInputs = [];
                PublishWindowOnly(targetOnly);
                return _latestCombinedFrame;
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
        Func<HWND, PointerRect>? getBounds = null, CanvasMargins margins = default)
    {
        if (!IsWindowFromExpectedProcess(targetWindow, pid))
        {
            throw new CaptureTargetClosedException();
        }
        getBounds ??= hwnd => GetBounds(hwnd, requireNonEmpty: false);
        var windowBounds = getBounds(targetWindow);
        if (windowBounds.Right <= windowBounds.Left || windowBounds.Bottom <= windowBounds.Top)
        {
            throw new InvalidOperationException("The selected capture window has empty bounds.");
        }
        // A popup only needs to touch the recorded area, which includes the margins around the window.
        var targetBounds = new PointerRect(windowBounds.Left - margins.Left, windowBounds.Top - margins.Top,
            windowBounds.Right + margins.Right, windowBounds.Bottom + margins.Bottom);
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
                logger.LogWarning(ex, "Cannot query bounds for secondary window {Hwnd}; capturing the selected window without it.", (nint)window);
                continue;
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

    // Preserve premultiplied BGRA transparency while clipping the UI image to the canvas.
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

    // A popup that cannot be captured is left out rather than failing capture of the selected window.
    private void SkipFailedSecondaryWindow(nint handle, Exception ex)
    {
        _logger.LogDebug(ex, "Cannot capture secondary window {Hwnd}; capturing the selected window without it.", handle);
        _failedSecondaryWindows.Add(handle);
        if (_reportedSecondaryWindows.Add(handle))
        {
            _warnings.Add($"A popup window (HWND {handle}) could not be captured and is missing from the recording: {ex.Message}");
        }
    }

    private void DisposeSecondaryWindowSessions()
    {
        foreach (var session in _secondaryWindowSessions.Values)
        {
            session.Dispose();
        }

        _secondaryWindowSessions.Clear();
    }

    private sealed class CaptureTargetClosedException() : InvalidOperationException("The selected capture window is no longer valid.");

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong(nint hwnd, int index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLayeredWindowAttributes(nint hwnd, out uint key, out byte alpha, out uint flags);

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

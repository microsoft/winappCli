// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Windows.Win32;
using Windows.Win32.Foundation;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Provides window-only rendering for eligible windows rejected by Windows graphics capture.
/// Renders the existing UI window through PrintWindow without reading the screen or moving focus.
/// </summary>
internal sealed partial class WindowCaptureFallback(HWND hwnd,
    Func<(byte[] Pixels, int Width, int Height)>? capture = null,
    Func<long>? clock = null, ILogger? logger = null, int? expectedPid = null) : IFrameGrabber
{
    private bool _disposed;
    private long _version;
    private (byte[] Pixels, int Width, int Height, long Version)? _latest;
    private Task<(byte[] Pixels, int Width, int Height)>? _pending;
    private long _started;
    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly int _expectedPid = expectedPid ?? RealOwnedWindowFinder.s_getWindowProcessId(hwnd);
    private volatile bool _closed;

    public string RootCaptureBackend => "printwindow";

    public bool IsClosed
    {
        get
        {
            if (!_closed && (_expectedPid == 0 ||
                !WindowCaptureSession.IsWindowFromExpectedProcess(hwnd, _expectedPid) ||
                !RealOwnedWindowFinder.s_isWindowVisible(hwnd)))
            {
                _closed = true;
            }
            return _closed;
        }
    }
    internal nint WindowHandle => (nint)hwnd;

    public (byte[] Pixels, int Width, int Height, long Version)? TryGetLatest()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsClosed)
        {
            return _latest;
        }
        EnsureCaptureAllowed(hwnd);
        if (_pending is null)
        {
            _pending = StartNextCapture();
        }
        if (!_pending.IsCompleted)
        {
            if (_clock() - _started >= 2000)
            {
                throw new TimeoutException($"PrintWindow capture did not complete for window HWND {(nint)hwnd} within 2 seconds.");
            }
            return _latest;
        }
        var frame = _pending.GetAwaiter().GetResult();
        _pending = null;
        if (IsClosed)
        {
            return _latest;
        }
        _pending = StartNextCapture();
        if (_latest is not null && _latest.Value.Width == frame.Width &&
            _latest.Value.Height == frame.Height && frame.Pixels.AsSpan().SequenceEqual(_latest.Value.Pixels))
        {
            return _latest;
        }
        _latest = (frame.Pixels, frame.Width, frame.Height, ++_version);
        return _latest;
    }

    private Task<(byte[] Pixels, int Width, int Height)> StartNextCapture()
    {
        _started = _clock();
        var pending = Task.Run(() =>
        {
            EnsureCurrentTarget();
            var frame = (capture ?? CaptureWithPhysicalCoordinates)();
            EnsureCurrentTarget();
            return frame;
        });
        _ = pending.ContinueWith(task =>
            _logger.LogError(task.Exception, "Window-only capture failed for window {Hwnd}.", (nint)hwnd),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return pending;
    }

    private (byte[] Pixels, int Width, int Height) CaptureWithPhysicalCoordinates()
    {
        EnsureCurrentTarget();
        EnsureCaptureAllowed(hwnd);
        var previous = SetThreadDpiAwarenessContext(-4); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        if (previous == 0)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(),
                "Cannot establish physical window capture coordinates.");
        }
        try
        {
            return Capture();
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }
    }

    private (byte[] Pixels, int Width, int Height) Capture()
    {
        // PrintWindow includes the nonclient frame; crop it to the same DWM bounds as WGC.
        if (!PInvoke.GetWindowRect(hwnd, out var rect))
        {
            throw new System.ComponentModel.Win32Exception(
                System.Runtime.InteropServices.Marshal.GetLastPInvokeError(), "Cannot query window capture bounds.");
        }
        var width = checked(rect.right - rect.left);
        var height = checked(rect.bottom - rect.top);
        var context = GetWindowDpiAwarenessContext(hwnd);
        if (context == 0)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Cannot query window DPI awareness.");
        }
        var previous = SetThreadDpiAwarenessContext(context);
        if (previous == 0)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Cannot establish window rendering coordinates.");
        }
        byte[] source;
        int sourceWidth;
        int sourceHeight;
        try
        {
            if (!PInvoke.GetWindowRect(hwnd, out var logical))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Cannot query window rendering bounds.");
            }
            sourceWidth = checked(logical.right - logical.left);
            sourceHeight = checked(logical.bottom - logical.top);
            EnsureCurrentTarget();
            source = PrintWindowCaptureHelper.CaptureValidatedPixels(hwnd, sourceWidth, sourceHeight);
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }
        // WM_PRINT handlers render in the target's DPI coordinate space, unlike DWM bounds.
        var pixels = source;
        if (sourceWidth != width || sourceHeight != height)
        {
            pixels = new byte[checked(width * height * 4)];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var input = (((long)y * sourceHeight / height) * sourceWidth + (long)x * sourceWidth / width) * 4;
                    Buffer.BlockCopy(source, checked((int)input), pixels, (y * width + x) * 4, 4);
                }
            }
        }
        EnsureCurrentTarget();
        EnsureCaptureAllowed(hwnd);
        var bounds = WindowCaptureSession.GetBounds(hwnd);
        var left = bounds.Left - rect.left;
        var top = bounds.Top - rect.top;
        var croppedWidth = bounds.Right - bounds.Left;
        var croppedHeight = bounds.Bottom - bounds.Top;
        if (left < 0 || top < 0 || left + croppedWidth > width || top + croppedHeight > height)
        {
            throw new InvalidOperationException("Window bounds changed during PrintWindow capture.");
        }
        var cropped = new byte[checked(croppedWidth * croppedHeight * 4)];
        for (var row = 0; row < croppedHeight; row++)
        {
            Buffer.BlockCopy(pixels, ((top + row) * width + left) * 4,
                cropped, row * croppedWidth * 4, croppedWidth * 4);
        }
        return (cropped, croppedWidth, croppedHeight);
    }

    public async Task<bool> WaitForFirstFrameAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        do
        {
            ct.ThrowIfCancellationRequested();
            if (TryGetLatest() is not null)
            {
                return true;
            }
            await Task.Delay(30, ct).ConfigureAwait(false);
        }
        while (Environment.TickCount64 < deadline);
        return TryGetLatest() is not null;
    }

    public void Dispose() => _disposed = true;

    private void EnsureCurrentTarget()
    {
        if (IsClosed)
        {
            throw new InvalidOperationException(            $"Window HWND {(nint)hwnd} is no longer the visible window of expected PID {_expectedPid}.");
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetThreadDpiAwarenessContext(nint context);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetWindowDpiAwarenessContext(HWND window);

    internal static void EnsureCaptureAllowed(HWND window)
    {
        if (!GetWindowDisplayAffinity(window, out var affinity))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(),
                $"Cannot establish display-affinity protection for window HWND {(nint)window}.");
        }
        if (affinity != 0)
        {
            throw new InvalidOperationException($"Window HWND {(nint)window} is protected from capture (display affinity 0x{affinity:X}).");
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowDisplayAffinity(HWND window, out uint affinity);
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// A live capture session over a single window that keeps the most recently arrived frame available
/// on demand. Used to sample frames at a fixed cadence without re-initializing the graphics pipeline
/// per frame.
/// </summary>
public interface IFrameGrabber : IDisposable
{
    /// <summary>
    /// The effective root capture backend: <c>wgc</c> or <c>printwindow</c>.
    /// Including menus or tooltips rendered by another backend does not change this value.
    /// Defaults to <c>wgc</c> for existing graphics-capture implementations.
    /// </summary>
    string RootCaptureBackend => "wgc";

    /// <summary>
    /// <see langword="true"/> once the capture session has ended — typically because the captured
    /// window closed. Callers should stop sampling and finish up.
    /// </summary>
    bool IsClosed { get; }

    /// <summary>
    /// Returns the most recent frame as BGRA pixels, or <see langword="null"/> when no frame has
    /// arrived yet. <c>Version</c> increments per frame so callers can tell a fresh frame from a
    /// repeat of the previous one.
    /// </summary>
    (byte[] Pixels, int Width, int Height, long Version)? TryGetLatest();

    /// <summary>
    /// Waits for the first frame to arrive. Returns <see langword="false"/> if
    /// <paramref name="timeout"/> elapses first.
    /// </summary>
    Task<bool> WaitForFirstFrameAsync(TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// Gets an available BGRA frame, waiting within <paramref name="timeout"/> when necessary.
    /// May return a cached frame; does not require a new arrival. Returns <see langword="null"/>
    /// on timeout. The returned frame remains usable independently of subsequent reads.
    /// </summary>
    async Task<(byte[] Pixels, int Width, int Height, long Version)?> GetFrameAsync(
        TimeSpan timeout, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        ct.ThrowIfCancellationRequested();
        if (!await WaitForFirstFrameAsync(timeout, ct).ConfigureAwait(false))
        {
            return null;
        }
        while (true)
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
                return null;
            }
            await Task.Delay((int)Math.Min(30, remaining), ct).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Window frame capture, backed by Windows Graphics Capture. Injected rather than called statically
/// so callers that orchestrate capture (such as video recording) can be tested without a GPU or a
/// live desktop.
/// </summary>
public interface IWindowCapture
{
    /// <summary>
    /// Measures the current interactive virtual desktop in physical pixels without activation.
    /// Throws when the process is not on the input desktop. Call before and after desktop capture
    /// and reject a frame if the bounds changed. The calling process must be per-monitor DPI aware.
    /// </summary>
    PointerRect GetDesktopBounds() => DesktopCapture.GetBounds();

    /// <summary>
    /// Whether continuous frame capture is available on this system. When <see langword="false"/>,
    /// <see cref="StartFrameGrabber"/> throws and callers must fall back to another capture path.
    /// </summary>
    bool IsFrameCaptureSupported { get; }

    /// <summary>
    /// Opens a persistent capture session for <paramref name="hwnd"/>. Pass <paramref name="fps"/> to
    /// hint the expected sampling rate (0 lets the implementation choose).
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">
    /// Frame capture is not available on this system.
    /// </exception>
    IFrameGrabber StartFrameGrabber(nint hwnd, int fps = 0);

    /// <summary>
    /// Captures a window's pixels (BGRA) via <c>PrintWindow</c>, foregrounding and retrying once when
    /// the first attempt comes back blank. Always available, including when
    /// <see cref="IsFrameCaptureSupported"/> is <see langword="false"/>.
    /// </summary>
    byte[] CaptureWindowPixels(nint hwnd, int width, int height);

    /// <summary>
    /// Captures a screen region (BGRA), scaling it to fit
    /// <paramref name="displayWidth"/>×<paramref name="displayHeight"/> and centering it within an
    /// <paramref name="encoderWidth"/>×<paramref name="encoderHeight"/> surface. The surface size is
    /// separate from the content size so a caller feeding a fixed-size video encoder can letterbox
    /// content whose aspect ratio does not match.
    /// </summary>
    byte[] CaptureScreenPixels(
        int x, int y, int cropWidth, int cropHeight,
        int encoderWidth, int encoderHeight,
        int displayWidth, int displayHeight);
}

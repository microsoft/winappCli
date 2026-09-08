// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake <see cref="IWindowCapture"/> — lets the recording tests drive frame capture without a GPU
/// or a live Windows Graphics Capture session.
/// </summary>
internal sealed class FakeWindowCapture : IWindowCapture
{
    public bool Supported { get; set; } = true;

    public Func<nint, int, IFrameGrabber>? StartGrabberCallback { get; set; }

    /// <summary>
    /// Substitutes the raw PrintWindow capture so a test can drive the printwindow recording path
    /// without a live, foregrounded window.
    /// </summary>
    public Func<nint, int, int, byte[]>? CaptureWindowOverride { get; set; }

    /// <summary>
    /// Substitutes the raw screen-DC capture so a test can drive the screen recording path without
    /// reading the shared desktop.
    /// </summary>
    public Func<int, int, int, int, int, int, int, int, byte[]>? CaptureScreenOverride { get; set; }

    public bool IsFrameCaptureSupported => Supported;

    public IFrameGrabber StartFrameGrabber(nint hwnd, int fps = 0)
        => StartGrabberCallback is not null
            ? StartGrabberCallback(hwnd, fps)
            : throw new PlatformNotSupportedException("No frame grabber configured for this test.");

    public byte[] CaptureWindowPixels(nint hwnd, int width, int height)
        => CaptureWindowOverride is not null
            ? CaptureWindowOverride(hwnd, width, height)
            : new byte[Math.Max(0, width * height * 4)];

    public byte[] CaptureScreenPixels(
        int x, int y, int cropWidth, int cropHeight,
        int encoderWidth, int encoderHeight,
        int displayWidth, int displayHeight)
        => CaptureScreenOverride is not null
            ? CaptureScreenOverride(x, y, cropWidth, cropHeight, encoderWidth, encoderHeight, displayWidth, displayHeight)
            : new byte[Math.Max(0, encoderWidth * encoderHeight * 4)];
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// <see cref="IWindowCapture"/> for target frameworks without the Windows 10 SDK projection.
/// Windows Graphics Capture is unavailable there, so screenshots fall back to the GDI
/// <c>PrintWindow</c> path and continuous frame capture is not offered at all.
/// </summary>
/// <remarks>
/// Compiled only into the plain <c>net10.0-windows</c> target. Consumers that need occluded-window
/// screenshots or frame capture should target <c>net10.0-windows10.0.19041.0</c> or later, which
/// gets the Graphics Capture implementation of this same interface.
/// </remarks>
internal sealed class GdiWindowCapture(ILogger<GdiWindowCapture> logger) : IWindowCapture
{
    public bool IsFrameCaptureSupported => false;

    public IFrameGrabber StartFrameGrabber(nint hwnd, int fps = 0)
    {
        logger.LogDebug("Frame capture requested but this build targets a framework without Windows Graphics Capture.");
        throw new PlatformNotSupportedException(
            "Continuous frame capture requires Windows Graphics Capture. Target net10.0-windows10.0.19041.0 or later to use it.");
    }

    public byte[] CaptureWindowPixels(nint hwnd, int width, int height)
        => UiAutomationService.CaptureFromWindowWithBlankRetry(
            new global::Windows.Win32.Foundation.HWND(hwnd), width, height, logger);

    public byte[] CaptureScreenPixels(
        int x, int y, int cropWidth, int cropHeight,
        int encoderWidth, int encoderHeight,
        int displayWidth, int displayHeight)
        => UiAutomationService.CaptureScreenFrame(
            x, y, cropWidth, cropHeight, encoderWidth, encoderHeight, displayWidth, displayHeight);
}

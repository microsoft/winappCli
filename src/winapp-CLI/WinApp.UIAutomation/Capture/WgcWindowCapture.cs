// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Production <see cref="IWindowCapture"/> — delegates to the Windows Graphics Capture helpers so
/// the capture pipeline stays the single source of truth.
/// </summary>
internal sealed class WgcWindowCapture(ILogger<WgcWindowCapture> logger) : IWindowCapture
{
    public bool IsFrameCaptureSupported => WgcCapture.IsSupported();

    public IFrameGrabber StartFrameGrabber(nint hwnd, int fps = 0)
        => WgcCapture.s_startGrabber(new global::Windows.Win32.Foundation.HWND(hwnd), logger, fps);

    public byte[] CaptureWindowPixels(nint hwnd, int width, int height)
        => UiAutomationService.CaptureFromWindowWithBlankRetry(
            new global::Windows.Win32.Foundation.HWND(hwnd), width, height, logger);

    public Task<(byte[] Pixels, int Width, int Height)?> TryCaptureWindowWithoutActivationAsync(
        nint hwnd, CancellationToken cancellationToken)
        => UiAutomationService.CaptureWithoutActivationAsync(
            new global::Windows.Win32.Foundation.HWND(hwnd), allowFrameCapture: true, logger, cancellationToken);

    public byte[] CaptureScreenPixels(
        int x, int y, int cropWidth, int cropHeight,
        int encoderWidth, int encoderHeight,
        int displayWidth, int displayHeight)
        => UiAutomationService.CaptureScreenFrame(
            x, y, cropWidth, cropHeight, encoderWidth, encoderHeight, displayWidth, displayHeight);
}

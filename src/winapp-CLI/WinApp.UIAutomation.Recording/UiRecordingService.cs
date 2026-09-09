// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording;

/// <summary>
/// Video recording: captures window/element frames at a fixed cadence and encodes
/// them incrementally to an H.264 MP4 via Media Foundation (<see cref="Mp4SinkWriterEncoder"/>).
/// </summary>
/// <remarks>
/// Capture and element resolution come from the UI Automation library through
/// <see cref="IUiAutomation"/> and <see cref="IWindowCapture"/>; this service owns only the
/// recording loop, encoding, and frame-artifact output, which is why they ship separately from
/// the base package.
/// </remarks>
internal sealed partial class UiRecordingService(
    IUiAutomation uiAutomation,
    IWindowCapture windowCapture,
    IUiSelectorParser selectorParser,
    ILogger<UiRecordingService> logger) : IUiRecordingService
{
    private readonly IUiAutomation _uiAutomation = uiAutomation;
    private readonly IWindowCapture _windowCapture = windowCapture;
    private readonly IUiSelectorParser _selectorParser = selectorParser;
    private readonly ILogger<UiRecordingService> _logger = logger;

    private static PointerRect ToPointerRect(RECT rect)
        => new(rect.left, rect.top, rect.right, rect.bottom);

    /// <summary>Whether a window is minimized, through the seam or from Windows.</summary>
    /// <remarks>
    /// A seam rather than a direct call because the activation steps are the thing
    /// <see cref="RecordOptions.NoActivation"/> promises not to take, and a promise that cannot be
    /// observed in a test is not a promise. Tests drive a minimized window without minimizing one.
    /// </remarks>
    internal static Func<nint, bool> s_isWindowMinimized = static hwnd
        => global::Windows.Win32.PInvoke.IsIconic(new HWND(hwnd));

    /// <summary>Restores a minimized window, through the seam or from Windows.</summary>
    internal static Action<nint> s_restoreWindow = static hwnd
        => global::Windows.Win32.PInvoke.ShowWindow(
            new HWND(hwnd), global::Windows.Win32.UI.WindowsAndMessaging.SHOW_WINDOW_CMD.SW_RESTORE);

    /// <summary>Brings a window to the foreground, through the seam or from Windows.</summary>
    internal static Action<nint> s_bringToForeground = static hwnd
        => global::Windows.Win32.PInvoke.SetForegroundWindow(new HWND(hwnd));

    /// <summary>Restores the window seams to the behavior production uses.</summary>
    internal static void ResetWindowStateSeams()
    {
        s_isWindowMinimized = static hwnd
            => global::Windows.Win32.PInvoke.IsIconic(new HWND(hwnd));
        s_restoreWindow = static hwnd
            => global::Windows.Win32.PInvoke.ShowWindow(
                new HWND(hwnd), global::Windows.Win32.UI.WindowsAndMessaging.SHOW_WINDOW_CMD.SW_RESTORE);
        s_bringToForeground = static hwnd
            => global::Windows.Win32.PInvoke.SetForegroundWindow(new HWND(hwnd));
    }

    /// <remarks>
    /// Coverage ceiling (issue #630): deterministic tests cover the frame-loop orchestration through
    /// WGC/screen/PrintWindow seams. The remaining lines in this method are native window-state arms
    /// (minimized/zero-size HWNDs), popup retargeting against real top-level HWND ownership, WGC init
    /// fault arms, and cancellation timing races that require mutating real desktop windows or native
    /// WGC failures and are not safe to trigger on the shared coverage host.
    /// </remarks>
    public async Task<RecordCaptureResult> RecordAsync(UiTarget uiTarget, string? elementId, RecordOptions options, CancellationToken ct, Action<bool>? onRecordingStarted = null)
    {
        // Validate before touching a window or creating output. The CLI rejects these at the command
        // layer, but that guard does not travel with the package: a direct library caller passing
        // Fps = 0 divided by zero computing the frame interval, and a negative DurationSec recorded
        // without end.
        ArgumentOutOfRangeException.ThrowIfNegative(options.DurationSec, nameof(options.DurationSec));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Fps, nameof(options.Fps));
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxEdge, nameof(options.MaxEdge));

        // The encoder cannot go below 64 pixels, so a smaller cap does not shrink the video - it
        // produces a 64x64 one holding a few pixels of content, which is not what "at most this many
        // pixels" promises. Refuse instead of returning something misleading.
        if (options.MaxEdge > 0)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxEdge, MfH264MinWidth, nameof(options.MaxEdge));
        }

        // Two ways of asking for incompatible things, rejected here rather than silently resolved.
        // Screen capture reads the region of the screen the window occupies, which requires the
        // window to be visible there and foregrounded to be on top -- the opposite of what a
        // no-activation recording promises. An element selector crops inside a window, and the
        // strict capture path is defined for a whole window only.
        if (options.NoActivation && options.CaptureScreen)
        {
            throw new ArgumentException(
                "A recording cannot both capture the screen and leave the window unactivated: screen " +
                "capture reads where the window is drawn on the user's display.",
                nameof(options));
        }

        if (options.NoActivation && !string.IsNullOrEmpty(elementId))
        {
            throw new ArgumentException(
                "A no-activation recording captures a whole window, so it cannot crop to an element.",
                nameof(elementId));
        }

        _logger.LogDebug("Recording process {Pid} (duration={Dur}s, fps={Fps}, maxEdge={MaxEdge}, captureScreen={Screen})",
            uiTarget.ProcessId, options.DurationSec, options.Fps, options.MaxEdge, options.CaptureScreen);

        if (!_uiAutomation.TryResolveRootWindow(uiTarget, out var rootHwnd, out var rootName))
        {
            throw new InvalidOperationException($"No UIA window found for {uiTarget.ProcessName} (PID {uiTarget.ProcessId}).");
        }

        if (rootName is not null)
        {
            uiTarget.WindowTitle = rootName;
        }

        if (rootHwnd == 0 && uiTarget.WindowHandle != 0)
        {
            rootHwnd = (nint)uiTarget.WindowHandle;
        }
        if (rootHwnd == 0)
        {
            throw new InvalidOperationException($"No native window handle for {uiTarget.ProcessName}. Is the window visible?");
        }

        var hwnd = new HWND(rootHwnd);

        if (s_isWindowMinimized(rootHwnd))
        {
            if (options.NoActivation)
            {
                // Restoring is an activation, so the only honest answers are "record nothing" or
                // "put the user's window back on screen without being asked". This picks the first.
                throw new InvalidOperationException(
                    "The window is minimized, and restoring it would bring it onto the user's screen, " +
                    "which this recording promised not to do. Restore the window yourself and retry.");
            }

            s_restoreWindow(rootHwnd);
            await Task.Delay(300, ct).ConfigureAwait(false);
        }

        // Bring to foreground for screen-DC capture.
        if (options.CaptureScreen)
        {
            s_bringToForeground(rootHwnd);
            await Task.Delay(150, ct).ConfigureAwait(false);

            // SetForegroundWindow is advisory. If it was refused, every screen-DC frame would record
            // whichever window is really in front and the caller would get a perfectly playable MP4 of
            // the wrong app. Verify after the activation delay and before any frame is captured. Capture
            // safety, not coordination: it says nothing about who else may be driving the desktop.
            //
            // The capture predicate, not the injection one: a modal dialog the target owns is part of
            // its UI and is sitting on the pixels being recorded, which is the reason to record the
            // screen rather than the window. An unrelated foreground window is still refused, and a
            // refusal still produces no artifact.
            if (!ForegroundGuard.ForegroundIsCapturableFor((long)rootHwnd))
            {
                throw new ForegroundLostException(
                    "The target window is not in the foreground, so a screen recording would capture " +
                    "whatever window is actually in front. Bring the window to the foreground and retry, " +
                    "or record the window directly instead of the screen.");
            }
        }

        global::Windows.Win32.PInvoke.GetWindowRect(hwnd, out var rect);
        if (rect.right - rect.left <= 0 || rect.bottom - rect.top <= 0)
        {
            throw new InvalidOperationException("Window has zero size. Is it minimized?");
        }

        // Prove the window can be captured as it stands before creating any output for it. This is
        // the same strict path the no-focus screenshot uses -- frame capture, then one non-activating
        // PrintWindow, with a blank result reported as a failure to capture rather than written out
        // as a black picture -- so a window that could only have been recorded by activating it
        // fails here, with nothing on disk to clean up.
        if (options.NoActivation &&
            await _windowCapture.TryCaptureWindowWithoutActivationAsync(rootHwnd, ct).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException(
                "The window could not be captured where it stands, and this recording promised not to " +
                "activate it. Nothing was recorded.");
        }

        var useScreen = options.CaptureScreen;
        var useWgc = !useScreen && _windowCapture.IsFrameCaptureSupported;

        IFrameGrabber? grabber = null;
        RecordFrameArtifactCoordinator? frameOutput = null;
        var mode = useScreen ? "screen" : (useWgc ? "wgc" : "printwindow");

        try
        {
            int srcWidth;
            int srcHeight;
            int captureOriginLeft;
            int captureOriginTop;

            if (useWgc)
            {
                try
                {
                    grabber = _windowCapture.StartFrameGrabber(rootHwnd, options.Fps);
                    if (!await grabber.WaitForFirstFrameAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException("Timed out waiting for the first captured frame.");
                    }
                    var first = grabber.TryGetLatest()!.Value;
                    srcWidth = first.Width;
                    srcHeight = first.Height;
                    var visibleRect = _uiAutomation.GetVisibleWindowBounds(rootHwnd, ToPointerRect(rect));
                    captureOriginLeft = visibleRect.Left;
                    captureOriginTop = visibleRect.Top;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "WGC recorder init failed; throwing (no silent screen fallback without --capture-screen)");
                    grabber?.Dispose();
                    grabber = null;

                    // Screen capture is not a fallback available to a no-activation recording at any
                    // consent level: it reads the user's display, where a window recorded in place is
                    // deliberately not visible. Stated here rather than left to the CaptureScreen
                    // guard below, so the invariant survives a change to what consent means.
                    if (options.NoActivation)
                    {
                        throw;
                    }

                    // Screen capture requires explicit consent because it can include overlapping windows.
                    EnsureWgcFallbackConsented(ex, options.CaptureScreen, _logger);
                    useScreen = true;
                    useWgc = false;
                    srcWidth = rect.right - rect.left;
                    srcHeight = rect.bottom - rect.top;
                    captureOriginLeft = rect.left;
                    captureOriginTop = rect.top;
                }
            }
            else
            {
                srcWidth = rect.right - rect.left;
                srcHeight = rect.bottom - rect.top;
                captureOriginLeft = rect.left;
                captureOriginTop = rect.top;
            }

            // Whole-window WGC dimensions can change while recording.
            var isWholeWindowWgc = useWgc && string.IsNullOrEmpty(elementId);

            // Determine the crop rectangle (in capture-space) for an element selector.
            var cropX = 0;
            var cropY = 0;
            var cropW = srcWidth;
            var cropH = srcHeight;
            if (!string.IsNullOrEmpty(elementId))
            {
                // Use the canonical single-element resolution path (same as ui click/hover) so that:
                //   - An ambiguous plain-text selector (multiple matches) → structured error with slug
                //     suggestions, not a silent first-match recording.
                //   - Element not found → UiElementNotFoundException (element_not_found error code).
                //   - Popup/child-window searching and slug validation are preserved.
                // No selector (elementId == null) → whole-window recording (by design, unchanged).
                var selectorExpr = _selectorParser.Parse(elementId);
                var selectorElement = await _uiAutomation.FindSingleElementAsync(uiTarget, selectorExpr, ct).ConfigureAwait(false);
                if (selectorElement is null)
                {
                    throw new UiElementNotFoundException(elementId);
                }

                // Capture against the element's top-level window, including popups and dialogs.
                var captureTargetHwnd = ResolvePopupCaptureHwnd(
                    selectorElement.WindowHandle, (nint)hwnd,
                    ref captureOriginLeft, ref captureOriginTop,
                    ref srcWidth, ref srcHeight);

                // Main-tree elements inherit the session HWND, so confirm it from the UIA ancestor chain.
                if (captureTargetHwnd == (nint)hwnd)
                {
                    var derivedHwnd = DeriveElementCaptureHwnd(
                        (nint)hwnd,
                        ref captureOriginLeft, ref captureOriginTop,
                        ref srcWidth, ref srcHeight,
                        getElementTopLevelHwnd: () => _uiAutomation.ResolveElementTopLevelWindow(uiTarget, selectorElement));
                    if (derivedHwnd != (nint)hwnd)
                    {
                        captureTargetHwnd = derivedHwnd;
                        _logger.LogDebug(
                            "Selector '{Sel}' element resolved to top-level window HWND 0x{Hwnd:X} via UIA ancestor walk; retargeting capture",
                            elementId, captureTargetHwnd);
                    }
                }

                if (captureTargetHwnd != (nint)hwnd)
                {
                    var popupHwnd = new global::Windows.Win32.Foundation.HWND(captureTargetHwnd);
                    _logger.LogDebug(
                        "Selector '{Sel}' resolved to popup window HWND 0x{Hwnd:X}; retargeting capture",
                        elementId, captureTargetHwnd);

                    if (useWgc)
                    {
                        // Restart the WGC grabber on the element's owning top-level window.
                        grabber?.Dispose();
                        grabber = null;
                        try
                        {
                            grabber = _windowCapture.StartFrameGrabber(captureTargetHwnd, options.Fps);
                            if (!await grabber.WaitForFirstFrameAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false))
                            {
                                throw new InvalidOperationException("Timed out waiting for the first captured frame.");
                            }
                            var retargetFirst = grabber.TryGetLatest()!.Value;
                            srcWidth = retargetFirst.Width;
                            srcHeight = retargetFirst.Height;
                            global::Windows.Win32.PInvoke.GetWindowRect(popupHwnd, out var popupWinRect);
                            var visiblePopupRect = _uiAutomation.GetVisibleWindowBounds(
                                captureTargetHwnd, ToPointerRect(popupWinRect));
                            captureOriginLeft = visiblePopupRect.Left;
                            captureOriginTop = visiblePopupRect.Top;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogDebug(ex, "WGC retarget to popup HWND 0x{Hwnd:X} failed", captureTargetHwnd);
                            grabber?.Dispose();
                            grabber = null;
                            // If the user did not pass --capture-screen, EnsureWgcFallbackConsented throws
                            // (same privacy guard as for main-window WGC init failure).
                            // captureOriginLeft/Top/srcWidth/srcHeight already reflect the popup window rect
                            // (set by ResolvePopupCaptureHwnd), so any screen-DC fallback uses the right region.
                            EnsureWgcFallbackConsented(ex, options.CaptureScreen, _logger);
                            useScreen = true;
                            useWgc = false;
                            mode = "screen";
                        }
                    }
                    else if (!useScreen)
                    {
                        // PrintWindow path: retarget the HWND to the popup window.
                        // captureOriginLeft/Top/srcWidth/srcHeight already updated by ResolvePopupCaptureHwnd.
                        hwnd = popupHwnd;
                    }
                    // For useScreen: captureOriginLeft/Top/srcWidth/srcHeight already updated; no further action.
                }

                // Reject elements outside the capture surface instead of recording a clamped edge.
                if (IsElementOffscreen(selectorElement.X, selectorElement.Y,
                    selectorElement.Width, selectorElement.Height,
                    captureOriginLeft, captureOriginTop, srcWidth, srcHeight))
                {
                    throw new UiElementOffscreenException(elementId!);
                }

                // Compute crop: element screen coords relative to the (possibly retargeted) capture origin.
                cropX = Math.Clamp((int)selectorElement.X - captureOriginLeft, 0, Math.Max(0, srcWidth - 1));
                cropY = Math.Clamp((int)selectorElement.Y - captureOriginTop, 0, Math.Max(0, srcHeight - 1));
                cropW = Math.Clamp((int)selectorElement.Width, 1, srcWidth - cropX);
                cropH = Math.Clamp((int)selectorElement.Height, 1, srcHeight - cropY);
            }

            var (encoderW, encoderH, displayW, displayH) = ComputeTargetSize(cropW, cropH, options.MaxEdge);
            var bitrate = (uint)Math.Clamp((long)encoderW * encoderH * options.Fps / 8, 1_000_000, 24_000_000);

            // Last foreground check before any file exists. The earlier one ran right after the
            // activation delay; selector resolution between the two can take long enough for another
            // window to steal the foreground, and every screen-DC frame would then be of that window.
            // It deliberately sits above the encoder rather than next to the first frame read: creating
            // the encoder creates OutputPath, so refusing after that point would leave an empty MP4 and
            // break the "no artifact on refusal" contract the CLI states for foreground_not_target.
            //
            // Same capture predicate as the first check — the two must agree, or a modal dialog the
            // target owns would pass one and fail the other.
            if (options.CaptureScreen && !ForegroundGuard.ForegroundIsCapturableFor((long)rootHwnd))
            {
                throw new ForegroundLostException(
                    "The target window lost the foreground while the recording was being prepared, so a " +
                    "screen recording would capture whatever window is actually in front. Bring the " +
                    "window to the foreground and retry, or record the window directly instead of the screen.");
            }

            // Never replace an existing recording. The CLI refuses up front ("recording never
            // replaces existing artifacts"), but that guard does not travel with the package, and
            // the previous video-only path silently overwrote OutputPath - running the readme
            // sample twice destroyed the first take. This also covers a file that appears while
            // recording is already in progress.
            using var encoder = Mp4SinkWriterEncoder.s_createNoClobber(
                options.OutputPath, encoderW, encoderH, options.Fps, bitrate);

            var frameDurationHns = 10_000_000L / options.Fps;
            var totalFrames = options.DurationSec > 0 ? (long)options.DurationSec * options.Fps : (long?)null;
            var stopwatch = Stopwatch.StartNew();
            var startedUtc = DateTimeOffset.UtcNow;
            var frameIndex = 0;
            long lastEncodedVersion = -1;
            var startedSignaled = false;
            var targetClosed = false;
            var captureUnavailable = false;
            var sawBlankFrame = false;
            RecordFrameArtifactResult? frameArtifacts = null;

            if (options.FramesDirectory is not null)
            {
                frameOutput = CreateRecordFrameArtifactCoordinator(new RecordFrameArtifactSetup
                {
                    Options = options,
                    StartedUtc = startedUtc,
                    EncoderWidth = encoderW,
                    EncoderHeight = encoderH,
                });
            }

            async ValueTask CommitFrameAsync(byte[] processedFrame)
            {
                var sampleIndex = frameIndex;
                var elapsedMs = Math.Max(0, (long)Math.Round(stopwatch.Elapsed.TotalMilliseconds));

                if (frameOutput is not null)
                {
                    await frameOutput.WriteAsync(processedFrame, new RecordFrameSample
                    {
                        SampleIndex = sampleIndex,
                        ElapsedMs = elapsedMs,
                        MediaTimeMs = sampleIndex * 1000.0 / options.Fps,
                    }).ConfigureAwait(false);
                }

                encoder.WriteFrame(
                    processedFrame,
                    sampleIndex * frameDurationHns,
                    frameDurationHns);
                frameIndex++;
                if (!startedSignaled)
                {
                    startedSignaled = true;
                    onRecordingStarted?.Invoke(frameOutput?.IsActive == true);
                }

            }

            Exception? mp4Failure = null;
            long captureElapsedMs;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (totalFrames.HasValue && frameIndex >= totalFrames.Value)
                    {
                        break;
                    }

                    if (totalFrames.HasValue && stopwatch.Elapsed.TotalSeconds >= options.DurationSec)
                    {
                        break;
                    }

                    if (useWgc && grabber!.IsClosed)
                    {
                        var closedLatest = grabber.TryGetLatest();
                        if (closedLatest is not null)
                        {
                            var (closedSrc, closedSw, closedSh, closedVersion) = closedLatest.Value;

                            // The last frame a closing capture session leaves behind gets the same
                            // scrutiny as any other. Without this, a window that only ever produced
                            // blank warm-up frames and then closed publishes a one-frame black MP4 --
                            // the same false success the frame loop refuses, arriving by the one door
                            // that skipped the check.
                            var closedFrameIsBlank = options.NoActivation && CapturedFrame.IsBlank(closedSrc);

                            if (closedFrameIsBlank)
                            {
                                sawBlankFrame = true;
                            }
                            else if (ShouldEncodeClosedDrainFrame(closedVersion, lastEncodedVersion))
                            {
                                var closedCropW = isWholeWindowWgc ? closedSw : cropW;
                                var closedCropH = isWholeWindowWgc ? closedSh : cropH;
                                var closedFrame = ProcessFrame(
                                    closedSrc, closedSw, closedSh,
                                    cropX, cropY, closedCropW, closedCropH,
                                    encoderW, encoderH, displayW, displayH);
                                await CommitFrameAsync(closedFrame).ConfigureAwait(false);
                            }
                        }
                        targetClosed = true;
                        _logger.LogDebug(
                            "WGC capture item closed mid-recording; finalizing {Frames} frames captured so far",
                            frameIndex);
                        break;
                    }

                    byte[] frame;
                    long sourceVersion = -1;
                    if (useWgc)
                    {
                        var latest = grabber!.TryGetLatest();
                        if (latest is null)
                        {
                            await Task.Delay(5, ct).ConfigureAwait(false);
                            continue;
                        }
                        var (source, sw, sh, version) = latest.Value;

                        // Frame capture reports success for a surface it produced nothing on, so a
                        // window it cannot really see yields black frames that encode perfectly well.
                        // The setup probe does not rule this out: it accepts a window that frame
                        // capture could not see but a non-activating PrintWindow could, and the take
                        // then runs on frame capture anyway. Unchecked, that is an all-black MP4
                        // reported as a successful recording -- the exact failure a caller who cannot
                        // watch the screen has no way to notice.
                        if (options.NoActivation && CapturedFrame.IsBlank(source))
                        {
                            if (frameIndex == 0)
                            {
                                // Nothing usable yet. Keep polling rather than giving up on the first
                                // frame: capture sessions can hand out an empty surface before the
                                // first real one arrives. If none ever does, the take ends on its own
                                // deadline and fails below, having published nothing.
                                sawBlankFrame = true;
                                await Task.Delay(5, ct).ConfigureAwait(false);
                                continue;
                            }

                            // The window was rendering and stopped. Same as losing it outright: keep
                            // the frames already captured and say why the take ended early.
                            captureUnavailable = true;
                            _logger.LogDebug(
                                "Frame capture returned a blank frame and this recording cannot activate " +
                                "the window to recover it; finalizing {Frames} frames captured so far",
                                frameIndex);
                            break;
                        }

                        sourceVersion = version;
                        frame = ProcessFrame(
                            source, sw, sh, cropX, cropY,
                            isWholeWindowWgc ? sw : cropW,
                            isWholeWindowWgc ? sh : cropH,
                            encoderW, encoderH, displayW, displayH);
                    }
                    else if (useScreen)
                    {
                        frame = _windowCapture.CaptureScreenPixels(
                            captureOriginLeft + cropX,
                            captureOriginTop + cropY,
                            cropW,
                            cropH,
                            encoderW,
                            encoderH,
                            displayW,
                            displayH);
                    }
                    else
                    {
                        if (options.NoActivation)
                        {
                            // No frame capture on this system, so PrintWindow is the only path left.
                            // The strict variant is the one that keeps the promise: it never
                            // foregrounds the window to recover a blank frame, it reports the blank
                            // instead.
                            var captured = await _windowCapture
                                .TryCaptureWindowWithoutActivationAsync((nint)hwnd, ct)
                                .ConfigureAwait(false);

                            if (captured is not { } strict)
                            {
                                // The window stopped being capturable where it stands. Ending the
                                // take keeps whatever was already recorded, which is more use than
                                // discarding good frames over a window that went blank at second 9
                                // of 10. A take that never got a frame at all fails outright instead,
                                // through the same path as any other capture failure.
                                captureUnavailable = true;
                                _logger.LogDebug(
                                    "Window could not be captured without activating it; finalizing " +
                                    "{Frames} frames captured so far",
                                    frameIndex);
                                break;
                            }

                            frame = ProcessFrame(
                                strict.Pixels, strict.Width, strict.Height,
                                0, 0, strict.Width, strict.Height,
                                encoderW, encoderH, displayW, displayH);
                        }
                        else
                        {
                            var source = _windowCapture.CaptureWindowPixels((nint)hwnd, srcWidth, srcHeight);
                            frame = ProcessFrame(
                                source, srcWidth, srcHeight,
                                cropX, cropY, cropW, cropH,
                                encoderW, encoderH, displayW, displayH);
                        }
                    }

                    await CommitFrameAsync(frame).ConfigureAwait(false);
                    if (useWgc)
                    {
                        lastEncodedVersion = sourceVersion;
                    }

                    var targetMs = frameIndex * 1000.0 / options.Fps;
                    var delayMs = targetMs - stopwatch.Elapsed.TotalMilliseconds;
                    if (delayMs > 1)
                    {
                        try
                        {
                            await Task.Delay((int)delayMs, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }

                captureElapsedMs = Math.Max(1, (long)Math.Round(stopwatch.Elapsed.TotalMilliseconds));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                captureElapsedMs = Math.Max(1, (long)Math.Round(stopwatch.Elapsed.TotalMilliseconds));
            }
            catch (Exception ex)
            {
                captureElapsedMs = Math.Max(1, (long)Math.Round(stopwatch.Elapsed.TotalMilliseconds));
                mp4Failure = ex;
            }

            if (frameIndex == 0 && ct.IsCancellationRequested)
            {
                if (frameOutput is not null)
                {
                    await frameOutput.AbortAsync().ConfigureAwait(false);
                }
                ct.ThrowIfCancellationRequested();
            }

            if (frameIndex == 0 && sawBlankFrame)
            {
                // The take ended -- on its own deadline, or because the window closed -- and frame
                // capture never produced anything but black. Treated as the window not being
                // capturable at all, because that is what it means: the alternative is publishing a
                // video of nothing and calling it a recording.
                captureUnavailable = true;
            }

            if (frameIndex == 0 && captureUnavailable)
            {
                // The window was capturable when the recording was set up and stopped being so before
                // a single frame landed. Publishing an empty video would report success for a
                // recording of nothing.
                if (frameOutput is not null)
                {
                    await frameOutput.AbortAsync().ConfigureAwait(false);
                }

                throw new InvalidOperationException(
                    "The window could not be captured where it stands, and this recording promised not " +
                    "to activate it. Nothing was recorded.");
            }

            if (mp4Failure is null)
            {
                try
                {
                    encoder.Complete();
                }
                catch (Exception ex) when (IsRecoverableVideoOutputFailure(ex))
                {
                    mp4Failure = ex;
                }
            }

            var elapsedMs = captureElapsedMs;
            var achievedFps = frameIndex * 1000.0 / elapsedMs;
            var cadenceRatio = achievedFps / options.Fps;
            var frameSamplesAccepted = frameOutput?.SamplesAccepted ?? 0;
            var frameAchievedFps = frameSamplesAccepted * 1000.0 / elapsedMs;
            var frameCadenceRatio = frameAchievedFps / options.Fps;
            var stopReason = mp4Failure is not null
                ? "mp4_failed"
                : targetClosed
                    ? "target_closed"
                    : captureUnavailable
                        ? "capture_unavailable"
                        : ct.IsCancellationRequested ? "cancelled" : "duration_elapsed";

            if (mp4Failure is not null)
            {
                if (frameOutput is not null)
                {
                    frameArtifacts = await frameOutput.CompleteAfterVideoFailureAsync(
                        new RecordFrameCompletion
                        {
                            Status = "partial",
                            StopReason = stopReason,
                            ElapsedMs = elapsedMs,
                            AchievedFps = frameAchievedFps,
                            CadenceRatio = frameCadenceRatio,
                            VideoStatus = "failed",
                            VideoFrameCount = frameIndex,
                            // Reserve canonical .frames for successful MP4 pairs.
                            PublicationDirectory = $"{options.FramesDirectory}.partial-{Guid.NewGuid():N}",
                        }).ConfigureAwait(false);
                }

                if (frameArtifacts is not null)
                {
                    throw new RecordPartialOutputException(
                        "MP4 recording failed, but partial frame artifacts were preserved.",
                        videoPath: null,
                        framesDirectory: frameArtifacts.Directory,
                        "Inspect the partial frame bundle, then retry the recording to produce the MP4.",
                        mp4Failure);
                }

                if (frameOutput?.Failure is not null)
                {
                    throw new RecordFrameOutputException(
                        "Frame artifact output failed and no recording artifact could be preserved.",
                        GetFrameOutputRecoveryHint(videoPreserved: false),
                        frameOutput.Failure);
                }

                ExceptionDispatchInfo.Capture(mp4Failure).Throw();
            }

            var fileSize = new FileInfo(options.OutputPath).Length;
            if (frameOutput is not null)
            {
                frameArtifacts = await frameOutput.CompleteAfterVideoSuccessAsync(
                    new RecordFrameCompletion
                    {
                        Status = "complete",
                        StopReason = stopReason,
                        ElapsedMs = elapsedMs,
                        AchievedFps = achievedFps,
                        CadenceRatio = cadenceRatio,
                        VideoStatus = "complete",
                        VideoFrameCount = frameIndex,
                        VideoFileSize = fileSize,
                    }).ConfigureAwait(false);
            }

            if (frameOutput?.Failure is not null)
            {
                throw new RecordPartialOutputException(
                    "The MP4 was recorded successfully, but frame artifact output failed.",
                    options.OutputPath,
                    framesDirectory: null,
                    GetFrameOutputRecoveryHint(videoPreserved: true),
                    frameOutput.Failure);
            }

            List<string>? warnings = null;
            if (cadenceRatio < 0.90)
            {
                warnings = [$"Capture cadence was {cadenceRatio:P0} of the requested {options.Fps} fps."];
            }
            if (frameArtifacts?.Truncated == true)
            {
                warnings ??= [];
                warnings.Add(
                    $"Frame artifacts reached the {frameArtifacts.ByteLimit / 1024 / 1024} MiB bundle limit; " +
                    "the MP4 is complete, but only the indexed frame prefix was retained. " +
                    "Retry with a shorter duration, lower fps, or lower max-edge for complete frame evidence.");
            }
            return new RecordCaptureResult
            {
                Frames = frameIndex,
                Width = encoderW,
                Height = encoderH,
                FileSize = fileSize,
                Mode = mode,
                ElapsedMs = elapsedMs,
                AchievedFps = achievedFps,
                CadenceRatio = cadenceRatio,
                StopReason = stopReason,
                FrameArtifacts = frameArtifacts,
                Warnings = warnings?.ToArray(),
            };
        }
        finally
        {
            if (frameOutput is not null)
            {
                try
                {
                    await frameOutput.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ObjectDisposedException
                    or InvalidOperationException
                    or IOException
                    or UnauthorizedAccessException
                    or COMException)
                {
                    _logger.LogDebug(ex, "Could not clean up unpublished frame artifacts");
                }
            }
            grabber?.Dispose();
        }
    }

    private static bool IsRecoverableVideoOutputFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ExternalException;

    private static string GetFrameOutputRecoveryHint(bool videoPreserved)
        => videoPreserved
            ? "Use the preserved MP4. Check available disk space and permissions, then retry with --frames and a new --output path."
            : "Check available disk space and permissions, then retry with --frames and a new --output path.";

    /// <summary>
    /// Called when Windows Graphics Capture fails to initialize. If the user did NOT explicitly
    /// pass <c>--capture-screen</c>, this throws an <see cref="InvalidOperationException"/> with a
    /// clear, actionable message — screen-DC capture is NOT a silent privacy-safe fallback.
    /// If <paramref name="captureScreenRequested"/> is <see langword="true"/>, a warning is emitted
    /// and the caller proceeds with the screen-DC path that the user consented to.
    /// </summary>
    /// <param name="inner">The original WGC init exception (used as inner exception).</param>
    /// <param name="captureScreenRequested">Whether the user passed <c>--capture-screen</c>.</param>
    /// <param name="logger">Logger for the consent-granted warning.</param>
    internal static void EnsureWgcFallbackConsented(Exception inner, bool captureScreenRequested, ILogger logger)
    {
        if (!captureScreenRequested)
        {
            throw new InvalidOperationException(
                "Windows Graphics Capture is unavailable on this system/session. " +
                "Re-run with --capture-screen to record via screen capture " +
                "(note: this may include other windows overlapping the target).", inner);
        }
        logger.LogWarning(
            "WGC init failed; falling back to screen-DC capture as requested by --capture-screen. " +
            "Overlapping windows may be captured.");
    }

    internal static bool ShouldEncodeClosedDrainFrame(long cachedVersion, long lastEncodedVersion) => cachedVersion != lastEncodedVersion;

}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Commands;

internal sealed class UiAuditScreenshotCommand : Command, IShortDescription
{
    public string ShortDescription => "Capture one audit window without activation or window expansion";

    public UiAuditScreenshotCommand()
        : base("audit-screenshot", "Capture exactly the selected window through Windows Graphics Capture. " +
            "Never activates or restores a window, captures the desktop, or enumerates additional owned windows. " +
            "Requires an explicit --app or --window and a new --output PNG path. " +
            "Unavailable capture is an error, not a foreground-changing fallback. " +
            "JSON records physical screen bounds before and after capture; coordinate mapping is available only when " +
            "the bounds stay unchanged and exactly match the captured pixel dimensions.")
    {
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(SharedUiOptions.OutputOption);
        Options.Add(SharedUiOptions.TimeoutOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(
        IUiTargetResolver resolver,
        ISystemUiQuery system,
        IWindowCapture capture,
        IAnsiConsole console,
        ILogger<UiAuditScreenshotCommand> logger) : AsynchronousCommandLineAction
    {
        internal Func<long, UiAuditCaptureBounds?> ReadCaptureBounds { get; set; } = ReadDwmBounds;

        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            int Fail(string code, string message)
            {
                UiJsonError.Emit(json, code, message, errorOut: parseResult.InvocationConfiguration.Error);
                if (!json)
                {
                    logger.LogError("{Message}", message);
                }
                return 1;
            }

            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var output = parseResult.GetValue(SharedUiOptions.OutputOption);
            var timeout = parseResult.GetValue(SharedUiOptions.TimeoutOption);
            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                return Fail("missing_app", "Select an audit window with --app or --window.");
            }
            if (window is <= 0 || string.IsNullOrWhiteSpace(output) || timeout is < 100 or > 120000)
            {
                return Fail("invalid_arguments", "Provide a positive window, a new --output path and a --timeout of 100..120000 ms.");
            }
            try
            {
                var path = Path.GetFullPath(output);
                if (Path.Exists(path))
                {
                    return Fail("output_exists", "Audit screenshots never overwrite existing paths.");
                }
                if (!capture.IsFrameCaptureSupported)
                {
                    return Fail("capture_unsupported", "Passive audit screenshots require Windows Graphics Capture; no foreground-changing fallback is used.");
                }
                cancellationToken.ThrowIfCancellationRequested();
                var target = await resolver.ResolveAsync(app, window, cancellationToken);
                VerifyTarget(target, system);
                var foregroundBefore = system.GetForegroundWindow();
                var boundsBefore = ReadCaptureBounds(target.WindowHandle);
                var startedAt = DateTimeOffset.UtcNow;
                using var grabber = capture.StartFrameGrabber((nint)target.WindowHandle);
                if (!await grabber.WaitForFirstFrameAsync(TimeSpan.FromMilliseconds(timeout), cancellationToken))
                {
                    return Fail("capture_timeout", "No frame arrived within the passive capture budget.");
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (grabber.IsClosed)
                {
                    return Fail("target_closed", "The capture window closed before a frame could be retained.");
                }
                var frame = grabber.TryGetLatest();
                if (frame is null)
                {
                    return Fail("capture_empty", "Capture signaled readiness but returned no frame.");
                }
                var (pixels, width, height, _) = frame.Value;
                var capturedAt = DateTimeOffset.UtcNow;
                var boundsAfter = ReadCaptureBounds(target.WindowHandle);
                if (width <= 0 || height <= 0 || (long)width * height > 16_000_000
                    || pixels.LongLength != (long)width * height * 4)
                {
                    return Fail("invalid_frame", "The capture frame is invalid or exceeds the 16-million-pixel budget.");
                }
                var png = UiScreenshotCommand.Handler.EncodePng(pixels, width, height);
                VerifyTarget(target, system);
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await stream.WriteAsync(png, cancellationToken);
                }
                var result = new UiAuditScreenshotResult
                {
                    ProcessId = target.ProcessId,
                    WindowHandle = target.WindowHandle,
                    FilePath = path,
                    Width = width,
                    Height = height,
                    CaptureStartedAtUtc = startedAt,
                    CapturedAtUtc = capturedAt,
                    CaptureBoundsBefore = boundsBefore,
                    CaptureBoundsAfter = boundsAfter,
                    CoordinateMappingStatus = MappingProblem(boundsBefore, boundsAfter, width, height) is null ? "verified" : "unavailable",
                    CoordinateMappingReason = MappingProblem(boundsBefore, boundsAfter, width, height),
                    ForegroundBefore = (long)foregroundBefore,
                    ForegroundAfter = (long)system.GetForegroundWindow()
                };
                if (json)
                {
                    console.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(result, UiJsonContext.Default.UiAuditScreenshotResult));
                }
                else
                {
                    console.Profile.Out.Writer.WriteLine($"Saved passive window screenshot: {path} ({width}x{height}).");
                }
                return 0;
            }
            catch (OperationCanceledException)
            {
                return Fail("cancelled", "Passive screenshot capture was cancelled.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException)
            {
                return Fail("capture_failed", $"Passive screenshot capture failed: {ex.Message}");
            }
        }

        internal static void VerifyTarget(UiTarget target, ISystemUiQuery system)
        {
            if (target.ProcessId <= 0 || target.WindowHandle <= 0
                || system.GetProcessIdForWindow(target.WindowHandle) != target.ProcessId
                || system.GetRootWindow(target.WindowHandle) != target.WindowHandle)
            {
                throw new InvalidOperationException("The exact audit window/process identity could not be verified.");
            }
        }

        internal static string? MappingProblem(UiAuditCaptureBounds? before, UiAuditCaptureBounds? after, int width, int height)
        {
            if (before is null || after is null)
            {
                return "DWM physical frame bounds are unavailable; no screenshot origin is assumed.";
            }
            if (before != after)
            {
                return "The window moved or resized during capture; element coordinates are not mapped.";
            }
            if (width <= 0 || height <= 0 || before.Width != width || before.Height != height)
            {
                return "The physical window frame does not match the image dimensions; no scale factor is guessed.";
            }
            return null;
        }

        private static unsafe UiAuditCaptureBounds? ReadDwmBounds(long windowHandle)
        {
            Windows.Win32.Foundation.RECT bounds;
            var result = Windows.Win32.PInvoke.DwmGetWindowAttribute(
                new Windows.Win32.Foundation.HWND((nint)windowHandle),
                Windows.Win32.Graphics.Dwm.DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS,
                &bounds, (uint)sizeof(Windows.Win32.Foundation.RECT));
            if (result.Failed || bounds.right <= bounds.left || bounds.bottom <= bounds.top)
            {
                return null;
            }
            return new(bounds.left, bounds.top, bounds.right - bounds.left, bounds.bottom - bounds.top);
        }
    }
}

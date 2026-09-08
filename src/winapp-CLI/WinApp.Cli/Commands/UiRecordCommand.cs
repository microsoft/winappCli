// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class UiRecordCommand : Command, IShortDescription
{
    private const int DefaultFrameArtifactMaxEdge = 1280;
    private const int MaximumFrameArtifactMaxEdge = 4096;

    internal static readonly Option<bool> FramesOption = new("--frames")
    {
        Description = "Write timestamped JPEGs, frames.ndjson, and manifest.json to <output-name>.frames. Supports 1-30 fps and max-edge 64-4096 (default 1280), with a 1 GiB frame-data cap.",
    };

    public string ShortDescription => "Record a window or element region to an MP4 (H.264) video";

    public UiRecordCommand()
        : base("record", "Record the target window (or an element's region) to an H.264 MP4 video. " +
               "By default records until Ctrl+C or redirected-stdin newline/EOF. " +
               "Use --duration-sec for a timed run, --frames for timestamped JPEG evidence, " +
               "and --capture-screen for overlays and popups.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(SharedUiOptions.DurationSecOption);
        Options.Add(SharedUiOptions.FpsOption);
        Options.Add(SharedUiOptions.MaxEdgeOption);
        Options.Add(SharedUiOptions.CaptureScreenOption);
        Options.Add(SharedUiOptions.OutputOption);
        Options.Add(FramesOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        IUiTargetResolver targetResolver,
        IUiRecordingService recordingService,
        IAnsiConsole ansiConsole,
        ILogger<UiRecordCommand> logger) : AsynchronousCommandLineAction
    {
        // Test seams: override Console.IsInputRedirected and Console.In without process-level side effects.
        internal static Func<bool>? s_isInputRedirectedOverride;
        internal static TextReader? s_stdinOverride;

        // Prevents the stdin monitor from racing disposal of its cancellation source.
        private volatile bool _stdinMonitorStopped;

        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var quiet = parseResult.GetValue(WinAppRootCommand.QuietOption);
            var selector = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var durationSec = parseResult.GetValue(SharedUiOptions.DurationSecOption);
            var fps = parseResult.GetValue(SharedUiOptions.FpsOption);
            var maxEdge = parseResult.GetValue(SharedUiOptions.MaxEdgeOption);
            var maxEdgeExplicit = parseResult.GetResult(SharedUiOptions.MaxEdgeOption)?.Implicit == false;
            var captureScreen = parseResult.GetValue(SharedUiOptions.CaptureScreenOption);
            var output = parseResult.GetValue(SharedUiOptions.OutputOption);
            var frames = parseResult.GetValue(FramesOption);

            // Validate options before resolving the target.
            if (durationSec < 0)
            {
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, "--duration-sec must be 0 or greater.");
                logger.LogError("{Symbol} --duration-sec must be 0 or greater.", UiSymbols.Error);
                return 1;
            }
            if (durationSec > 86400)
            {
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, "--duration-sec must not exceed 86400 (24 hours).");
                logger.LogError("{Symbol} --duration-sec must not exceed 86400 (24 hours).", UiSymbols.Error);
                return 1;
            }
            if (fps < 1)
            {
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, "--fps must be at least 1.");
                logger.LogError("{Symbol} --fps must be at least 1.", UiSymbols.Error);
                return 1;
            }
            if (!frames && maxEdge != 0 && maxEdge < 64)
            {
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, "--max-edge must be 0 (unbounded) or >= 64 (encoder minimum).");
                logger.LogError("{Symbol} --max-edge must be 0 (unbounded) or >= 64 (encoder minimum).", UiSymbols.Error);
                return 1;
            }
            if (frames)
            {
                if (fps > 30)
                {
                    UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, "--frames supports --fps values from 1 through 30.");
                    logger.LogError("{Symbol} --frames supports --fps values from 1 through 30.", UiSymbols.Error);
                    return 1;
                }
                if (maxEdgeExplicit && (maxEdge < 64 || maxEdge > MaximumFrameArtifactMaxEdge))
                {
                    const string message = "--frames supports --max-edge values from 64 through 4096; omit --max-edge to use 1280.";
                    UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, message);
                    logger.LogError("{Symbol} {Message}", UiSymbols.Error, message);
                    return 1;
                }

                if (!maxEdgeExplicit)
                {
                    maxEdge = DefaultFrameArtifactMaxEdge;
                }
            }

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }

            // Set _stdinMonitorStopped before disposing this source.
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _stdinMonitorStopped = false;
            try
            {
                // Resolve output path inside error handling so path errors produce structured output.
                string filePath;
                string? framesDirectory = null;
                try
                {
                    // Avoid collisions between concurrent recordings using the default path.
                    filePath = Path.GetFullPath(
                        output ?? $"recording-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.mp4");
                    framesDirectory = frames ? GetFramesDirectory(filePath) : null;

                    if (framesDirectory is not null)
                    {
                        if (Path.Exists(filePath))
                        {
                            UiJsonError.Emit(
                                json,
                                UiJsonError.CodeOutputExists,
                                $"MP4 output already exists: {filePath}",
                                errorOut: parseResult.InvocationConfiguration.Error,
                                recoveryHint: "Choose a new --output path; recording never replaces existing artifacts.");
                            logger.LogError("{Symbol} MP4 output already exists: {Path}", UiSymbols.Error, filePath);
                            return 1;
                        }
                        if (Path.Exists(framesDirectory))
                        {
                            UiJsonError.Emit(
                                json,
                                UiJsonError.CodeOutputExists,
                                $"Frame artifact output already exists: {framesDirectory}",
                                errorOut: parseResult.InvocationConfiguration.Error,
                                recoveryHint: "Choose a new --output path; the derived frame directory already exists and is never replaced.");
                            logger.LogError("{Symbol} Frame artifact output already exists: {Path}", UiSymbols.Error, framesDirectory);
                            return 1;
                        }
                    }

                    var dir = Path.GetDirectoryName(filePath);
                    if (dir is not null)
                    {
                        Directory.CreateDirectory(dir);
                    }
                }
                catch (Exception pathEx)
                {
                    UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, $"Invalid output path: {pathEx.Message}");
                    logger.LogError("{Symbol} Invalid output path: {Message}", UiSymbols.Error, pathEx.Message);
                    return 1;
                }

                var uiTarget = await targetResolver.ResolveAsync(app, window, cancellationToken);

                var isStdinRedirected = s_isInputRedirectedOverride?.Invoke() ?? Console.IsInputRedirected;

                if (!json && !quiet)
                {
                    var until = durationSec > 0
                        ? $"{durationSec}s"
                        : isStdinRedirected ? "Ctrl+C, newline/EOF on stdin" : "Ctrl+C";
                    var destinations = framesDirectory is null
                        ? filePath
                        : $"{filePath}; frame artifacts: {framesDirectory}";
                    ansiConsole.MarkupLine($"[grey]Recording \"{Markup.Escape(uiTarget.WindowTitle ?? "")}\" (PID {uiTarget.ProcessId}) to {Markup.Escape(destinations)} — until {until}, {fps} fps…[/]");
                }

                // Stops received before the first frame wait for encoder readiness.
                var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                // Only unbounded recordings use redirected stdin as a stop signal.
                if (isStdinRedirected && durationSec == 0)
                {
                    var stdinReader = s_stdinOverride ?? Console.In;
                    StdinStopMonitor.Start(stdinReader, readyTcs.Task, () => CancelFromStdinMonitor(linkedCts));
                }

                void OnRecordingStarted(bool frameArtifactsActive)
                {
                    readyTcs.TrySetResult();

                    if (json)
                    {
                        var startedEvent = new UiRecordStartedEvent
                        {
                            Path = filePath,
                            Fps = fps,
                            DurationSec = durationSec,
                            FramesDirectory = frameArtifactsActive ? framesDirectory : null,
                            FramesManifest = frameArtifactsActive ? Path.Join(framesDirectory!, "manifest.json") : null,
                            FramesIndex = frameArtifactsActive ? Path.Join(framesDirectory!, "frames.ndjson") : null,
                        };
                        parseResult.InvocationConfiguration.Error.WriteLine(
                            JsonSerializer.Serialize(startedEvent, UiJsonLineContext.Default.UiRecordStartedEvent));
                    }
                }

                var options = new RecordOptions
                {
                    OutputPath = filePath,
                    DurationSec = durationSec,
                    Fps = fps,
                    MaxEdge = maxEdge,
                    CaptureScreen = captureScreen,
                    FramesDirectory = framesDirectory,
                };

                var result = await recordingService.RecordAsync(uiTarget, selector, options, linkedCts.Token, OnRecordingStarted);

                if (json)
                {
                    var payload = new UiRecordResult
                    {
                        Path = filePath,
                        DurationSec = durationSec,
                        Fps = fps,
                        Frames = result.Frames,
                        Width = result.Width,
                        Height = result.Height,
                        FileSize = result.FileSize,
                        Codec = "h264",
                        Mode = result.Mode,
                        ElapsedMs = result.ElapsedMs,
                        AchievedFps = result.AchievedFps,
                        CadenceRatio = result.CadenceRatio,
                        StopReason = result.StopReason,
                        FrameArtifacts = result.FrameArtifacts,
                        Warnings = result.Warnings,
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(payload, UiJsonContext.Default.UiRecordResult));
                    return 0;
                }
                logger.LogInformation(
                    "Recorded {Frames} frames ({Width}x{Height}, h264) to {Path} ({Size}KB)",
                    result.Frames, result.Width, result.Height, filePath, result.FileSize / 1024);
                foreach (var warning in result.Warnings ?? [])
                {
                    logger.LogWarning("{Symbol} {Warning}", UiSymbols.Warning, warning);
                }
                return 0;
            }
            catch (RecordPartialOutputException partialEx)
            {
                UiJsonError.Emit(
                    json,
                    UiJsonError.CodePartialOutput,
                    partialEx.Message,
                    details: partialEx.InnerException?.GetType().Name,
                    errorOut: parseResult.InvocationConfiguration.Error,
                    recoveryHint: partialEx.RecoveryHint,
                    partialOutput: new UiPartialOutputInfo
                    {
                        VideoPath = partialEx.VideoPath,
                        FramesDirectory = partialEx.FramesDirectory,
                    });
                logger.LogError("{Symbol} {Message}", UiSymbols.Error, partialEx.Message);
                if (!json)
                {
                    if (partialEx.VideoPath is not null)
                    {
                        logger.LogError("{Symbol} Preserved MP4: {Path}", UiSymbols.Error, partialEx.VideoPath);
                    }
                    if (partialEx.FramesDirectory is not null)
                    {
                        logger.LogError(
                            "{Symbol} Preserved frame artifacts: {Path}",
                            UiSymbols.Error,
                            partialEx.FramesDirectory);
                    }
                    logger.LogError("{Symbol} Recovery: {RecoveryHint}", UiSymbols.Error, partialEx.RecoveryHint);
                }
                return 1;
            }
            catch (RecordFrameOutputException frameEx)
            {
                UiJsonError.Emit(
                    json,
                    UiJsonError.CodeFrameOutputFailed,
                    frameEx.Message,
                    details: frameEx.InnerException?.GetType().Name,
                    errorOut: parseResult.InvocationConfiguration.Error,
                    recoveryHint: frameEx.RecoveryHint);
                logger.LogError("{Symbol} {Message}", UiSymbols.Error, frameEx.Message);
                if (!json)
                {
                    logger.LogError("{Symbol} Recovery: {RecoveryHint}", UiSymbols.Error, frameEx.RecoveryHint);
                }
                return 1;
            }
            catch (UiAmbiguousSelectorException ambiguousEx)
            {
                UiErrors.AmbiguousSelector(logger, ambiguousEx.Message, json);
                return 1;
            }
            catch (UiElementOffscreenException offscreenEx)
            {
                const string offscreenMsg = "Element is entirely offscreen / has no visible area to capture; nothing to record. Bring the window/element into view or pass a different selector.";
                logger.LogError("{Symbol} {Message}", UiSymbols.Error, offscreenMsg);
                UiJsonError.Emit(json, UiJsonError.CodeElementNotFound, offscreenMsg, offscreenEx.Selector);
                return 1;
            }
            catch (UiElementNotFoundException notFoundEx)
            {
                UiErrors.ElementNotFound(logger, notFoundEx.Selector, json);
                return 1;
            }
            catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
            {
                // In-loop cancellation returns a finalized recording instead.
                logger.LogDebug("Recording cancelled before capture started.");
                return 1;
            }
            catch (System.Runtime.InteropServices.COMException comEx)
            {
                logger.LogDebug("COM error: {HResult} {StackTrace}", comEx.HResult, comEx.StackTrace);
                UiErrors.GenericError(logger, comEx, json);
                return 1;
            }
            catch (Exception ex)
            {
                UiErrors.GenericError(logger, ex, json);
                return 1;
            }
            finally
            {
                _stdinMonitorStopped = true;
                linkedCts.Dispose();
            }
        }

        internal void CancelFromStdinMonitor(CancellationTokenSource linkedCts)
        {
            if (!_stdinMonitorStopped)
            {
                try { linkedCts.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }

        internal static string GetFramesDirectory(string outputPath)
        {
            var framesDirectory = Path.ChangeExtension(outputPath, ".frames");
            if (string.Equals(framesDirectory, outputPath, StringComparison.OrdinalIgnoreCase))
            {
                framesDirectory += ".frames";
            }
            return framesDirectory;
        }
    }
}

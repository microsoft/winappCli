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
        IWindowCapture windowCapture,
        ISystemUiQuery systemQuery,
        IAnsiConsole ansiConsole,
        IInteractiveDesktopLock desktopLock,
        ILogger<UiRecordCommand> logger) : UiCoordinatedAction(desktopLock, logger)
    {
        // Test seams: override Console.IsInputRedirected and Console.In without process-level side effects.
        internal static Func<bool>? s_isInputRedirectedOverride;
        internal static TextReader? s_stdinOverride;

        // Prevents the stdin monitor from racing disposal of its cancellation source.
        private volatile bool _stdinMonitorStopped;

        protected override string Operation => "ui record";

        /// <summary>
        /// Recording shares the turn: it pins its owner for the whole capture, but same-workflow input
        /// may interleave so a workflow can record itself driving the app.
        /// </summary>
        /// <remarks>
        /// With no <c>WINAPP_UI_WORKFLOW_ID</c> the recording is an anonymous one-command owner, so it
        /// blocks every other owner for its full duration — to record and click concurrently, both
        /// commands must name the same workflow.
        /// </remarks>
        protected override UiTurnMode ResolveMode(ParseResult parseResult) => UiTurnMode.TurnShared;

        protected override int? Preflight(ParseResult parseResult)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var durationSec = parseResult.GetValue(SharedUiOptions.DurationSecOption);
            var fps = parseResult.GetValue(SharedUiOptions.FpsOption);
            var maxEdge = parseResult.GetValue(SharedUiOptions.MaxEdgeOption);
            var maxEdgeExplicit = parseResult.GetResult(SharedUiOptions.MaxEdgeOption)?.Implicit == false;
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
            }

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }

            // The output path is knowable now, so a recording that can never be written must not first
            // queue for — and then occupy — a desktop turn. Only an explicit --output is checked here:
            // the generated default carries a timestamp and a GUID, so it cannot collide, and resolving
            // it now would produce a different name than the one Execute goes on to use.
            if (parseResult.GetValue(SharedUiOptions.OutputOption) is { } explicitOutput)
            {
                return ValidateRecordingOutput(
                    explicitOutput, frames, json, parseResult.InvocationConfiguration.Error, out _, out _);
            }

            return null;
        }

        /// <summary>
        /// Resolves and validates where a recording will be written: a usable path, no collision with an
        /// existing artifact, and a parent directory that exists and can be written to.
        /// </summary>
        /// <remarks>
        /// Shared by <c>Preflight</c> and <c>ExecuteAsync</c> rather than moved wholesale, because the two
        /// answer different questions. Preflight rejects a doomed command before it takes a turn; the
        /// re-check under the turn still matters because the file system can change while the command
        /// queues, and the engine's own no-clobber check remains the final word.
        /// </remarks>
        /// <returns><see langword="null"/> when the path is usable, otherwise the exit code to return.</returns>
        private int? ValidateRecordingOutput(
            string candidate,
            bool frames,
            bool json,
            TextWriter errorOut,
            out string fullPath,
            out string? framesDirectory)
        {
            fullPath = "";
            framesDirectory = null;

            try
            {
                // A path ending in a separator names a directory, and a recording is a file. Left to
                // GetFullPath it resolves to a directory path that only fails much later, mid-capture.
                if (candidate.Length > 0
                    && (candidate.EndsWith(Path.DirectorySeparatorChar) || candidate.EndsWith(Path.AltDirectorySeparatorChar)))
                {
                    UiJsonError.Emit(
                        json,
                        UiJsonError.CodeInvalidArguments,
                        $"Invalid output path: '{candidate}' names a directory, not a file.",
                        errorOut: errorOut,
                        recoveryHint: "Pass --output a file path ending in .mp4.");
                    logger.LogError("{Symbol} Invalid output path: '{Path}' names a directory, not a file.", UiSymbols.Error, candidate);
                    return 1;
                }

                fullPath = Path.GetFullPath(candidate);
                framesDirectory = frames ? GetFramesDirectory(fullPath) : null;

                if (Directory.Exists(fullPath))
                {
                    UiJsonError.Emit(
                        json,
                        UiJsonError.CodeInvalidArguments,
                        $"Invalid output path: '{fullPath}' is an existing directory.",
                        errorOut: errorOut,
                        recoveryHint: "Pass --output a file path ending in .mp4.");
                    logger.LogError("{Symbol} Invalid output path: '{Path}' is an existing directory.", UiSymbols.Error, fullPath);
                    return 1;
                }

                // Applies to every recording mode, not only --frames: replacing a take that already
                // exists loses it, and the loss is silent because the command still reports success.
                if (Path.Exists(fullPath))
                {
                    UiJsonError.Emit(
                        json,
                        UiJsonError.CodeOutputExists,
                        $"MP4 output already exists: {fullPath}",
                        errorOut: errorOut,
                        recoveryHint: "Choose a new --output path; recording never replaces existing artifacts.");
                    logger.LogError("{Symbol} MP4 output already exists: {Path}", UiSymbols.Error, fullPath);
                    return 1;
                }

                if (framesDirectory is not null && Path.Exists(framesDirectory))
                {
                    UiJsonError.Emit(
                        json,
                        UiJsonError.CodeOutputExists,
                        $"Frame artifact output already exists: {framesDirectory}",
                        errorOut: errorOut,
                        recoveryHint: "Choose a new --output path; the derived frame directory already exists and is never replaced.");
                    logger.LogError("{Symbol} Frame artifact output already exists: {Path}", UiSymbols.Error, framesDirectory);
                    return 1;
                }

                var dir = Path.GetDirectoryName(fullPath);
                if (dir is not null)
                {
                    Directory.CreateDirectory(dir);
                }

                return null;
            }
            catch (Exception pathEx) when (pathEx is ArgumentException
                or NotSupportedException
                or PathTooLongException
                or IOException
                or UnauthorizedAccessException)
            {
                UiJsonError.Emit(
                    json,
                    UiJsonError.CodeInvalidArguments,
                    $"Invalid output path: {pathEx.Message}",
                    errorOut: errorOut);
                logger.LogError("{Symbol} Invalid output path: {Message}", UiSymbols.Error, pathEx.Message);
                return 1;
            }
        }

        protected override async Task<int> ExecuteAsync(ParseResult parseResult, IUiTurn turn, CancellationToken cancellationToken)
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

            // Preflight already validated every option above; only the default-derivation remains.
            if (frames && !maxEdgeExplicit)
            {
                maxEdge = DefaultFrameArtifactMaxEdge;
            }

            // Set _stdinMonitorStopped before disposing this source.
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _stdinMonitorStopped = false;
            try
            {
                // Re-resolved under the turn. Preflight already rejected a doomed explicit path before
                // this command queued; this pass covers the generated default and anything that changed
                // on disk while waiting, and the engine's no-clobber check is still the final word.
                string filePath;
                string? framesDirectory;
                if (ValidateRecordingOutput(
                        output ?? $"recording-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.mp4",
                        frames,
                        json,
                        parseResult.InvocationConfiguration.Error,
                        out filePath,
                        out framesDirectory) is { } outputFailure)
                {
                    return outputFailure;
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

                var (result, coordinationWarning) = await RecordUnderTurnAsync(
                    turn, uiTarget, selector, options, captureScreen, json, quiet,
                    OnRecordingStarted, parseResult.InvocationConfiguration.Error, linkedCts.Token).ConfigureAwait(false);

                if (result is null)
                {
                    // The target was refused from inside the section and the reason already reported.
                    return 1;
                }

                // Merged here rather than inside the helper because RecordCaptureResult is engine-owned
                // and immutable; the coordination note is a CLI concern layered on top of it.
                var allWarnings = coordinationWarning is null
                    ? result.Warnings
                    : [.. result.Warnings ?? [], coordinationWarning];

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
                        Warnings = allWarnings,
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(payload, UiJsonContext.Default.UiRecordResult));
                    return 0;
                }
                logger.LogInformation(
                    "Recorded {Frames} frames ({Width}x{Height}, h264) to {Path} ({Size}KB)",
                    result.Frames, result.Width, result.Height, filePath, result.FileSize / 1024);
                foreach (var warning in allWarnings ?? [])
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
            catch (ForegroundLostException foregroundEx)
            {
                // The engine refused to record because the target never reached the foreground, so every
                // screen frame would have been of the wrong window. Same precise contract as the
                // pre-injection foreground guard, and no MP4 is produced.
                logger.LogError("{Symbol} {Message}", UiSymbols.Error, foregroundEx.Message);
                UiJsonError.Emit(json, UiJsonError.CodeForegroundNotTarget, foregroundEx.Message,
                    errorOut: parseResult.InvocationConfiguration.Error);
                return 1;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Native Ctrl+C / coordinator cancellation before capture started. There is no finalized
                // MP4 to preserve, so this is NOT a completed command: swallowing it would make the
                // coordinator see a normal body return and renew the owner's idle grace for a command that
                // produced nothing. An ACTIVE recording that observes cancellation instead finalizes its
                // MP4 and RETURNS success, so it never reaches this catch and still renews.
                logger.LogDebug("Recording cancelled before capture started; propagating to coordination.");
                throw;
            }
            catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
            {
                // Defensive: the stdin stop-monitor only arms after encoder readiness, so this is the
                // narrow race where it fires between readiness and the first frame. The workflow asked its
                // own recording to stop rather than abandoning the command, so it keeps its turn.
                logger.LogDebug("Recording stopped via stdin before capture started.");
                return 1;
            }
            catch (System.Runtime.InteropServices.COMException comEx)
            {
                logger.LogDebug("COM error: {HResult} {StackTrace}", comEx.HResult, comEx.StackTrace);
                UiErrors.GenericError(logger, comEx, json);
                return 1;
            }
            catch (Exception ex) when (!UiCoordinatedAction.IsCoordinationFault(ex))
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

        /// <summary>
        /// Runs the recording, holding <c>active.lock</c> only for as long as the capture mode actually
        /// needs the desktop to itself.
        /// </summary>
        /// <remarks>
        /// <para>
        /// WGC and screen-DC recording touch the desktop only while starting up — restoring a minimized
        /// window and taking the foreground — so the section is released as soon as the first frame is
        /// committed. That is what lets a workflow record itself typing: the recording keeps the turn
        /// (<see cref="UiTurnMode.TurnShared"/>) while same-workflow input takes the section between
        /// frames.
        /// </para>
        /// <para>
        /// PrintWindow is different. When the host has no frame-capture support, <em>any</em> frame can
        /// hit the engine's blank-frame retry, which foregrounds the window to recover — mid-recording,
        /// with no warning. Releasing the section there would let that retry fight another command for
        /// the foreground, so the section is held for the whole recording and the caller is told plainly
        /// that input cannot interleave on this host.
        /// </para>
        /// </remarks>
        private async Task<(RecordCaptureResult? Result, string? CoordinationWarning)> RecordUnderTurnAsync(
            IUiTurn turn,
            UiTarget uiTarget,
            string? selector,
            RecordOptions options,
            bool captureScreen,
            bool json,
            bool quiet,
            Action<bool> onRecordingStarted,
            TextWriter errorOut,
            CancellationToken ct)
        {
            // The target was resolved before this command queued for the desktop, so re-confirm it from
            // inside the section: the window could have closed while waiting and had its handle reused,
            // and a recording of the wrong application is indistinguishable from a correct one.
            bool TargetStillValid() => DesktopTargetValidation.TryConfirmTargetWindow(
                systemQuery, uiTarget.WindowHandle, uiTarget.ProcessId, logger, json, "record", errorOut);

            // Mirrors the engine's own selection in UiRecordingService: screen DC when asked for, else WGC
            // when the host supports frame capture, else PrintWindow. Asserted against the result below so
            // this prediction cannot silently drift away from the engine.
            var predictedMode = captureScreen
                ? "screen"
                : windowCapture.IsFrameCaptureSupported ? "wgc" : "printwindow";
            var holdForWholeRecording = predictedMode == "printwindow";

            if (holdForWholeRecording)
            {
                const string warning =
                    "This host has no frame-capture support, so recording falls back to PrintWindow, whose " +
                    "blank-frame recovery can foreground the window at any point. The desktop is therefore " +
                    "held for the whole recording and other winapp ui commands — including ones sharing this " +
                    "workflow id — will wait until it finishes.";
                if (!json && !quiet)
                {
                    logger.LogWarning("{Symbol} {Message}", UiSymbols.Warning, warning);
                }

                RecordCaptureResult heldResult;
                await using (await turn.EnterAsync(ct).ConfigureAwait(false))
                {
                    if (!TargetStillValid())
                    {
                        return (null, null);
                    }

                    heldResult = await recordingService
                        .RecordAsync(uiTarget, selector, options, ct, onRecordingStarted).ConfigureAwait(false);
                }

                AssertPredictedMode(predictedMode, heldResult);
                return (heldResult, warning);
            }

            // RunContinuationsAsynchronously is mandatory: without it, TrySetResult below would run this
            // method's continuation ON the engine's capture thread, inside its first-frame callback, and
            // the section disposal would happen there too.
            var startedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnStarted(bool frameArtifactsActive)
            {
                // Signal FIRST. The outer notification writes the "recording started" JSON, and a caller
                // reading stderr slowly can block that write for an unbounded time. Setting the result
                // first means the awaiting continuation (scheduled asynchronously, see above) can release
                // the desktop section even while this engine thread is still stuck in that write —
                // otherwise one slow reader would hold the desktop lock for every other command.
                // The callback never disposes the section itself: disposal is async and belongs to this
                // method, and doing it here would block the engine's capture loop on a file lock.
                startedTcs.TrySetResult();
                onRecordingStarted(frameArtifactsActive);
            }

            var section = await turn.EnterAsync(ct).ConfigureAwait(false);
            var released = false;

            async Task ReleaseOnceAsync()
            {
                // Exactly once, whichever of the two outcomes below happens first — and again from the
                // finally, in case neither did (a synchronous throw before any frame).
                if (released)
                {
                    return;
                }

                released = true;
                await section.DisposeAsync().ConfigureAwait(false);
            }

            try
            {
                if (!TargetStillValid())
                {
                    // The finally below releases the section.
                    return (null, null);
                }

                var recordTask = recordingService.RecordAsync(uiTarget, selector, options, ct, OnStarted);

                // Race the two ways the desktop stops being needed: the first frame landed, or the
                // recording ended before producing one (fault, cancellation, or a zero-frame run). Waiting
                // only on the started signal would hang forever on the second case.
                await Task.WhenAny(startedTcs.Task, recordTask).ConfigureAwait(false);
                await ReleaseOnceAsync().ConfigureAwait(false);

                var result = await recordTask.ConfigureAwait(false);
                AssertPredictedMode(predictedMode, result);
                return (result, null);
            }
            finally
            {
                await ReleaseOnceAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Fails loudly when the engine chose a different capture mode than this command predicted.
        /// </summary>
        /// <remarks>
        /// The section-holding decision above is derived from a prediction, so a future engine change that
        /// altered mode selection would silently release the desktop mid-PrintWindow recording. Comparing
        /// against the reported mode turns that into a visible failure instead.
        /// </remarks>
        private void AssertPredictedMode(string predictedMode, RecordCaptureResult result)
        {
            if (result.Mode is { } actual && !string.Equals(actual, predictedMode, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "{Symbol} Recording used capture mode '{Actual}' but coordination planned for '{Predicted}'. Desktop coordination for this recording may have been wider or narrower than needed.",
                    UiSymbols.Warning, actual, predictedMode);
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

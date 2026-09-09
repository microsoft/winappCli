// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Commands;

internal class UiScreenshotCommand : Command, IShortDescription
{
    public string ShortDescription => "Capture a screenshot of a window or element";

    public UiScreenshotCommand()
        : base("screenshot", "Capture the target window or element as a PNG image. " +
               "When multiple windows exist (e.g., dialogs), captures each to a separate file. " +
               "With --json, returns file path and dimensions. Use --capture-screen for popup overlays.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);

        Options.Add(WinAppRootCommand.JsonOption);
        Options.Add(SharedUiOptions.OutputOption);
        Options.Add(SharedUiOptions.CaptureScreenOption);
        Options.Add(SharedUiOptions.FocusOption);
    }

    public class Handler(
        IUiTargetResolver targetResolver,
        IUiAutomation uiAutomation,
        IOwnedWindowFinder ownedWindowFinder,
        ISystemUiQuery systemQuery,
        IAnsiConsole ansiConsole,
        IInteractiveDesktopLock desktopLock,
        ILogger<UiScreenshotCommand> logger) : UiCoordinatedAction(desktopLock, logger)
    {
        protected override string Operation => "ui screenshot";

        /// <summary>
        /// Always exclusive.
        /// </summary>
        /// <remarks>
        /// Not because every capture foregrounds something — an ordinary visible window captured through
        /// Windows Graphics Capture does not. It is because the engine may restore a minimized target, and
        /// falls back to foregrounding it when frame capture is unavailable or <c>--capture-screen</c>
        /// reads the live screen. Those needs surface only once capture is under way, and the package
        /// deliberately exposes no coordination hook for the CLI to react to them, so the lean policy is
        /// to classify the whole command exclusive rather than to guess per invocation.
        /// <para>
        /// An earlier design started observationally and escalated on discovering it needed the
        /// foreground, which cost an entire discard-and-recapture pass, a second scheduler transition, and
        /// a mode that could change mid-command — all to avoid queueing for a command that virtually
        /// always ended up queueing anyway.
        /// </para>
        /// <para>
        /// A multi-window capture runs every window inside the one section, so the composite is a single
        /// consistent moment; encoding and writing the file happen after it is released.
        /// </para>
        /// </remarks>
        protected override UiTurnMode ResolveMode(ParseResult parseResult) => UiTurnMode.DesktopExclusive;

        protected override int? Preflight(ParseResult parseResult)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }

            // Where the PNG goes is knowable now. Screenshot takes the desktop exclusively, so a command
            // that cannot possibly write its file must not first queue for a turn, foreground a window
            // and capture pixels only to fail at the last step — the user waited and every other
            // workflow waited too, for nothing.
            return ValidateScreenshotOutput(
                parseResult.GetValue(SharedUiOptions.OutputOption) ?? DefaultOutputFileName,
                json,
                parseResult.InvocationConfiguration.Error);
        }

        private const string DefaultOutputFileName = "screenshot.png";

        /// <summary>
        /// Checks that the screenshot can actually be written: a file-shaped path, not an existing
        /// directory, and a parent directory that exists or can be created.
        /// </summary>
        /// <remarks>
        /// Overwriting an existing <em>file</em> stays deliberate — unlike a recording, a screenshot is
        /// cheap to retake and callers rely on writing to a fixed name repeatedly. The write in
        /// <c>PublishAsync</c> keeps its own directory creation and error handling, because the file
        /// system can change while the command waits for its turn.
        /// </remarks>
        /// <returns><see langword="null"/> when the path is usable, otherwise the exit code to return.</returns>
        private int? ValidateScreenshotOutput(string candidate, bool json, TextWriter errorOut)
        {
            try
            {
                if (candidate.Length > 0
                    && (candidate.EndsWith(Path.DirectorySeparatorChar) || candidate.EndsWith(Path.AltDirectorySeparatorChar)))
                {
                    return InvalidOutput($"'{candidate}' names a directory, not a file.");
                }

                var fullPath = Path.GetFullPath(candidate);

                if (Directory.Exists(fullPath))
                {
                    return InvalidOutput($"'{fullPath}' is an existing directory.");
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
                return InvalidOutput(pathEx.Message);
            }

            int InvalidOutput(string detail)
            {
                UiJsonError.Emit(
                    json,
                    UiJsonError.CodeInvalidArguments,
                    $"Invalid output path: {detail}",
                    errorOut: errorOut,
                    recoveryHint: "Pass --output a writable file path ending in .png.");
                logger.LogError("{Symbol} Invalid output path: {Detail}", UiSymbols.Error, detail);
                return 1;
            }
        }

        protected override async Task<int> ExecuteAsync(ParseResult parseResult, IUiTurn turn, CancellationToken cancellationToken)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var selector = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var output = parseResult.GetValue(SharedUiOptions.OutputOption);
            var captureScreen = parseResult.GetValue(SharedUiOptions.CaptureScreenOption);
            var focus = parseResult.GetValue(SharedUiOptions.FocusOption);

            try
            {
                CapturePass pass;

                // One section spans the WHOLE pixel-capture pass, not one per window. Compositing several
                // windows only makes sense if they were all captured against the same desktop state; a
                // per-window section would let another workflow foreground something between two frames
                // and produce a composite that never existed on screen. Window discovery and target
                // revalidation are inside for the same reason — a command may have queued for an
                // unbounded time, so anything resolved before the wait is advisory.
                await using (await turn.EnterAsync(cancellationToken).ConfigureAwait(false))
                {
                    pass = await CaptureUnderSectionAsync(
                        selector, app, window, json, captureScreen, focus,
                        parseResult.InvocationConfiguration.Error, cancellationToken).ConfigureAwait(false);
                }

                // Deliberately outside the section: composing, PNG encoding and writing to disk are pure
                // CPU and file I/O that touch no shared desktop state, and they are the slowest part of
                // the command. Holding active.lock across them would block every other workflow for no
                // safety benefit.
                if (pass.ExitCode is { } earlyExit)
                {
                    return earlyExit;
                }

                return await PublishAsync(pass, output, json, cancellationToken).ConfigureAwait(false);
            }
            catch (ForegroundLostException foregroundEx)
            {
                // The engine refused to capture because the target never reached the foreground. Report
                // the same precise contract as the pre-injection foreground guard rather than a generic
                // internal error, and write no artifact — a PNG of the wrong window is worse than none,
                // because the caller cannot tell.
                logger.LogError("{Symbol} {Message}", UiSymbols.Error, foregroundEx.Message);
                UiJsonError.Emit(json, UiJsonError.CodeForegroundNotTarget, foregroundEx.Message,
                    errorOut: parseResult.InvocationConfiguration.Error);
                return 1;
            }
            catch (System.Runtime.InteropServices.COMException comEx)
            {
                logger.LogDebug("COM error: {HResult} {StackTrace}", comEx.HResult, comEx.StackTrace);
                UiErrors.StaleElement(logger, json);
                return 1;
            }
            catch (Exception ex) when (!UiCoordinatedAction.IsCoordinationFault(ex))
            {
                UiErrors.GenericError(logger, ex, json);
                return 1;
            }
        }

        /// <summary>Pixels captured by one pass, plus everything needed to publish them.</summary>
        /// <summary>
        /// One window the pass intends to capture, together with where it came from.
        /// </summary>
        /// <param name="ActualPid">
        /// The process that currently owns the HWND. This is what the engine is handed, because it is
        /// what the window really belongs to.
        /// </param>
        /// <param name="ExpectedAppPid">
        /// The process of the application this window was discovered *for*. For an app's own window that
        /// is the same process; for a cross-process owned dialog — a common-item file picker, a system
        /// print dialog — it is the app that owns the dialog, not the host that runs it.
        /// </param>
        /// <remarks>
        /// The distinction is the whole point. Validating an owned dialog against its own current PID is
        /// close to vacuous: any live window passes, including a recycled handle that now belongs to a
        /// different dialog in the same system host and has no relationship to this app at all.
        /// Validating against <paramref name="ExpectedAppPid"/> makes the owner chain prove the window
        /// still belongs to the application being captured.
        /// </remarks>
        private readonly record struct CaptureCandidate(nint Hwnd, int ActualPid, int ExpectedAppPid, string Title);

        /// <summary>
        /// The <c>-a</c> fragment to put in a recovery hint, so the suggested command is one the caller
        /// can paste. Falls back to the PID when there is no process name to name.
        /// </summary>
        private static string DescribeApp(UiTarget target)
            => string.IsNullOrWhiteSpace(target.ProcessName)
                ? $" -a {target.ProcessId}"
                : $" -a {target.ProcessName}";

        /// <param name="ExitCode">Set when the pass already reported a failure and produced no pixels.</param>
        private sealed record CapturePass(
            int? ExitCode,
            UiTarget Target,
            string? Selector,
            List<(byte[] Pixels, int Width, int Height, nint Hwnd, string Title, string Label)> Captures,
            List<UiScreenshotWindowInfo> WindowDetails,
            bool IsComposite);

        /// <summary>
        /// Everything that reads the shared desktop. Runs entirely inside the caller's active section.
        /// </summary>
        private async Task<CapturePass> CaptureUnderSectionAsync(
            string? selector,
            string? app,
            long? window,
            bool json,
            bool captureScreen,
            bool focus,
            TextWriter errorOut,
            CancellationToken ct)
        {
            // A live-screen capture of an EXPLICITLY chosen window is exactly one region: the pixels
            // inside that window's bounds. Anything visibly on top of it — including a dialog it owns —
            // is already in those pixels, which is the whole reason to reach for --capture-screen. So
            // the multi-window expansion below is skipped: running it would send `-w <hwnd>` into the
            // ambiguity guard, and `-w <hwnd>` is the documented way OUT of that ambiguity. Without a
            // window the app may still resolve to several candidates, and that stays an error.
            var expandsToSeveralWindows = selector is null && !(captureScreen && window is not null and > 0);

            // Screenshot handles multi-window discovery itself (avoids duplicate warning from session resolution)
            if (expandsToSeveralWindows)
            {
                var allWindows = DiscoverAllWindows(app, window);
                if (allWindows is not null && allWindows.Count > 1)
                {
                    // Resolve session using the largest window's HWND (suppresses session multi-window warning)
                    var main = allWindows.OrderByDescending(w =>
                    {
                        var info = UiTargetResolver.GetWindowInfo(w.Hwnd);
                        return (long)info.Width * info.Height;
                    }).First();
                    var multiTarget = await targetResolver.ResolveAsync(null, main.Hwnd, ct).ConfigureAwait(false);
                    return await CaptureWindowsAsync(
                        allWindows, multiTarget, json, captureScreen, focus, errorOut, ct).ConfigureAwait(false);
                }
            }

            var singleTarget = await targetResolver.ResolveAsync(app, window, ct).ConfigureAwait(false);

            // Even for a single-window session, check for owned dialogs.
            if (expandsToSeveralWindows)
            {
                var targetWindowHwnd = (nint)singleTarget.WindowHandle;
                var appWindows = new List<(nint Hwnd, int Pid, string Title)>
                {
                    (targetWindowHwnd, singleTarget.ProcessId, singleTarget.WindowTitle ?? ""),
                };
                var ownedWindows = ownedWindowFinder.FindOwnedWindows(appWindows);
                if (ownedWindows.Count > 0)
                {
                    var allWindows = ToCandidates(appWindows, ownedWindows);
                    return await CaptureWindowsAsync(
                        allWindows, singleTarget, json, captureScreen, focus, errorOut, ct).ConfigureAwait(false);
                }
            }

            // The window this command is about to capture must still be the one it resolved: while it
            // waited, the original could have closed and Windows reused its handle for another process.
            if (!DesktopTargetValidation.TryConfirmTargetWindow(
                    systemQuery, singleTarget.WindowHandle, singleTarget.ProcessId, logger, json, "screenshot"))
            {
                return new CapturePass(1, singleTarget, selector, [], [], IsComposite: false);
            }

            var (pixels, w, h) = await uiAutomation
                .ScreenshotAsync(singleTarget, selector, captureScreen, focus, ct).ConfigureAwait(false);

            return new CapturePass(
                null,
                singleTarget,
                selector,
                [(pixels, w, h, (nint)singleTarget.WindowHandle, singleTarget.WindowTitle ?? "", "")],
                [],
                IsComposite: false);
        }

        private async Task<CapturePass> CaptureWindowsAsync(
            List<CaptureCandidate> windows,
            UiTarget uiTarget,
            bool json,
            bool captureScreen,
            bool focus,
            TextWriter errorOut,
            CancellationToken ct)
        {
            if (captureScreen && windows.Count > 1)
            {
                // Live-screen capture reads pixels off the screen, so it can only ever record the one
                // window that is actually in front. Compositing several of them would mean activating
                // each in turn: at best a picture stitched from moments where each window covered the
                // others, and in practice a foreground_not_target failure the moment Windows refuses the
                // second activation. Refuse here, before any window is foregrounded or captured, so the
                // caller gets the fix instead of a generic activation error.
                var message =
                    $"--capture-screen needs exactly one window, but {windows.Count} windows matched. " +
                    "Live-screen capture reads whatever is in front, so several windows cannot be captured together.";
                var hint =
                    $"Run 'winapp ui list-windows{DescribeApp(uiTarget)}' and retry with '-w <hwnd>' for the window you want, " +
                    "or drop --capture-screen to composite all of them from their own window contents.";

                logger.LogError("{Symbol} {Message}", UiSymbols.Error, message);
                logger.LogError("{Symbol} {Hint}", UiSymbols.Error, hint);
                UiJsonError.Emit(
                    json, UiJsonError.CodeInvalidArguments, message, errorOut: errorOut, recoveryHint: hint);
                return new CapturePass(1, uiTarget, null, [], [], IsComposite: true);
            }

            // Sort: main window first (largest), then others
            var sorted = windows.OrderByDescending(w =>
            {
                var info = UiTargetResolver.GetWindowInfo(w.Hwnd);
                return (long)info.Width * info.Height;
            }).ToList();

            if (!json)
            {
                ansiConsole.MarkupLine($"[yellow]⚠  {windows.Count} windows detected. Compositing into single image.[/]");
            }

            var captures = new List<(byte[] Pixels, int Width, int Height, nint Hwnd, string Title, string Label)>();
            var windowDetails = new List<UiScreenshotWindowInfo>();
            foreach (var w in sorted)
            {
                var info = UiTargetResolver.GetWindowInfo(w.Hwnd);
                var title = string.IsNullOrEmpty(w.Title) ? "(no title)" : w.Title;

                // Each handle was discovered before this command waited for the desktop, so any of them
                // could have closed and had its handle reused since. The check is against the process the
                // window was discovered FOR, not the one that currently owns it: for a cross-process
                // owned dialog those differ, and comparing it with its own PID would accept any live
                // window in the same system host — including a recycled handle with no connection to
                // this app. Validating against the originating process makes the owner chain prove the
                // dialog still belongs to it.
                var state = DesktopTargetValidation.ClassifyTargetWindow(systemQuery, w.Hwnd, w.ExpectedAppPid);
                if (state != DesktopTargetValidation.TargetWindowState.Valid)
                {
                    var reason = state == DesktopTargetValidation.TargetWindowState.Gone
                        ? "The window closed while this command was waiting for the desktop."
                        : "The window handle now belongs to a different process.";
                    logger.LogDebug("Skipping HWND {Hwnd}: {Reason}", w.Hwnd, reason);
                    windowDetails.Add(new UiScreenshotWindowInfo
                    {
                        Hwnd = w.Hwnd,
                        Title = string.IsNullOrEmpty(w.Title) ? null : w.Title,
                        Label = info.Label,
                        Captured = false,
                        Error = reason,
                    });
                    if (!json)
                    {
                        ansiConsole.MarkupLine($"  [red]✗[/] HWND {w.Hwnd}: \"{Markup.Escape(title)}\" — {Markup.Escape(reason)}");
                    }

                    continue;
                }

                try
                {
                    var windowTarget = new UiTarget
                    {
                        // The engine is handed the process that really owns the window, which for an
                        // owned dialog is its host rather than the app it was discovered for.
                        ProcessId = w.ActualPid,
                        ProcessName = uiTarget.ProcessName,
                        WindowTitle = title,
                        WindowHandle = w.Hwnd,
                    };
                    var (pixels, width, height) = await uiAutomation
                        .ScreenshotAsync(windowTarget, null, captureScreen, focus, ct).ConfigureAwait(false);
                    captures.Add((pixels, width, height, w.Hwnd, title, info.Label));
                    windowDetails.Add(new UiScreenshotWindowInfo
                    {
                        Hwnd = w.Hwnd,
                        Title = string.IsNullOrEmpty(w.Title) ? null : w.Title,
                        Label = info.Label,
                        Width = width,
                        Height = height,
                        Captured = true,
                    });

                    if (!json)
                    {
                        var owner = info.OwnerHwnd != 0 ? $", owner: HWND {info.OwnerHwnd}" : "";
                        ansiConsole.MarkupLine($"  [green]✓[/] HWND [cyan]{w.Hwnd}[/]: \"{Markup.Escape(title)}\" [grey]({info.Label}, {width}x{height}{owner})[/]");
                    }
                }
                catch (ForegroundLostException)
                {
                    // The foreground is a property of the desktop, not of this one window, so a refused
                    // activation is not a per-window failure. Recording it as one and continuing would end
                    // with "No windows could be captured" and bury the real, actionable cause.
                    throw;
                }
                catch (Exception ex) when (!UiCoordinatedAction.IsCoordinationFault(ex))
                {
                    logger.LogDebug("Failed to capture HWND {Hwnd}: {Error}", w.Hwnd, ex.Message);
                    windowDetails.Add(new UiScreenshotWindowInfo
                    {
                        Hwnd = w.Hwnd,
                        Title = string.IsNullOrEmpty(w.Title) ? null : w.Title,
                        Label = info.Label,
                        Captured = false,
                        Error = ex.Message,
                    });
                    if (!json)
                    {
                        ansiConsole.MarkupLine($"  [red]✗[/] HWND {w.Hwnd}: \"{Markup.Escape(title)}\" — {Markup.Escape(ex.Message)}");
                    }
                }
            }

            if (captures.Count == 0)
            {
                logger.LogError("No windows could be captured.");
                UiJsonError.Emit(json, UiJsonError.CodeInternalError, "No windows could be captured.");
                return new CapturePass(1, uiTarget, null, captures, windowDetails, IsComposite: true);
            }

            return new CapturePass(null, uiTarget, null, captures, windowDetails, IsComposite: true);
        }

        /// <summary>
        /// Encodes and writes what the pass captured. Runs after the active section has been released.
        /// </summary>
        private async Task<int> PublishAsync(CapturePass pass, string? output, bool json, CancellationToken ct)
        {
            var filePath = output ?? DefaultOutputFileName;
            var captures = pass.Captures;

            var pngBytes = pass.IsComposite
                ? ComposeSideBySide(captures)
                : PngImage.Encode(captures[0].Pixels, captures[0].Width, captures[0].Height);

            var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllBytesAsync(filePath, pngBytes, ct).ConfigureAwait(false);
            var absolutePath = Path.GetFullPath(filePath);

            var width = pass.IsComposite
                ? captures.Sum(c => c.Width) + WindowGap * (captures.Count - 1)
                : captures[0].Width;
            var height = pass.IsComposite
                ? captures.Max(c => c.Height) + LabelBarHeight
                : captures[0].Height;

            if (pass.IsComposite && !json)
            {
                ansiConsole.MarkupLine($"  [green]✓[/] Saved composite: {absolutePath}");
            }

            if (json)
            {
                var result = new UiScreenshotResult
                {
                    ElementId = pass.Selector,
                    FilePath = absolutePath,
                    Width = width,
                    Height = height,
                    ProcessId = pass.Target.ProcessId,
                    WindowTitle = pass.Target.WindowTitle,
                    Hwnd = pass.Target.WindowHandle,
                    Windows = pass.IsComposite ? pass.WindowDetails.ToArray() : null,
                };
                ansiConsole.Profile.Out.Writer.WriteLine(
                    JsonSerializer.Serialize(result, UiJsonContext.Default.UiScreenshotResult));
                return 0;
            }

            if (!pass.IsComposite)
            {
                logger.LogInformation(
                    "Screenshot of \"{WindowTitle}\" (PID {ProcessId}) saved to {Path} ({Width}x{Height}, {Size}KB)",
                    pass.Target.WindowTitle, pass.Target.ProcessId, absolutePath, width, height, pngBytes.Length / 1024);
            }

            return 0;
        }

        private const int LabelBarHeight = 28;
        private const int WindowGap = 8;

        private static byte[] ComposeSideBySide(List<(byte[] Pixels, int Width, int Height, nint Hwnd, string Title, string Label)> captures)
        {
            // Calculate composite dimensions
            var totalWidth = captures.Sum(c => c.Width) + WindowGap * (captures.Count - 1);
            var maxHeight = captures.Max(c => c.Height);
            var compositeHeight = maxHeight + LabelBarHeight;

            using var composite = new SKBitmap(totalWidth, compositeHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(composite);

            // Dark background
            canvas.Clear(new SKColor(30, 30, 30));

            using var labelPaint = new SKPaint
            {
                Color = SKColors.White,
                TextSize = 14,
                IsAntialias = true,
                Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal)
            };
            using var typeface = labelPaint.Typeface;
            using var labelBgPaint = new SKPaint { Color = new SKColor(50, 50, 50) };

            var x = 0;
            foreach (var (pixels, width, height, hwnd, title, label) in captures)
            {
                // Draw label bar
                canvas.DrawRect(x, 0, width, LabelBarHeight, labelBgPaint);
                var labelText = $"HWND {hwnd} ({label})  {title}";
                if (labelText.Length > 60) { labelText = labelText[..57] + "..."; }
                canvas.DrawText(labelText, x + 6, LabelBarHeight - 8, labelPaint);

                // Draw window capture
                using var windowBitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                unsafe
                {
                    var ptr = (byte*)windowBitmap.GetPixels().ToPointer();
                    System.Runtime.InteropServices.Marshal.Copy(pixels, 0, (nint)ptr, pixels.Length);
                }
                canvas.DrawBitmap(windowBitmap, x, LabelBarHeight);

                x += width + WindowGap;
            }

            using var image = SKImage.FromBitmap(composite);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        /// <summary>
        /// Discover all windows for the target app, including cross-process owned windows.
        /// Returns null if we can't determine the app's windows (e.g., no --app provided).
        /// </summary>
        private List<CaptureCandidate>? DiscoverAllWindows(string? app, long? window)
        {
            List<(nint Hwnd, int Pid, string Title)> appWindows;

            if (window is not null and > 0)
            {
                // Direct HWND — only find windows owned by THIS window (not all process windows).
                // The PID/title reads route through ISystemUiQuery so a valid live handle can be
                // supplied by a fake (real handles resolve identically through the seam).
                var hwndVal = (nint)window.Value;
                uint pid = systemQuery.GetProcessIdForWindow(window.Value);
                if (pid == 0) { return null; }

                var title = systemQuery.GetWindowText(window.Value) ?? "";
                appWindows = [(hwndVal, (int)pid, title)];
            }
            else if (!string.IsNullOrWhiteSpace(app))
            {
                // Find by app name — get all windows for matching processes
                if (int.TryParse(app, out var pid))
                {
                    appWindows = uiAutomation.FindWindowsByPid(pid);
                }
                else
                {
                    var processes = System.Diagnostics.Process.GetProcessesByName(app);
                    if (processes.Length == 0)
                    {
                        // Try partial match
                        processes = System.Diagnostics.Process.GetProcesses()
                            .Where(p => { try { return p.ProcessName.Contains(app, StringComparison.OrdinalIgnoreCase); } catch { return false; } })
                            .ToArray();
                    }
                    appWindows = [];
                    foreach (var p in processes)
                    {
                        appWindows.AddRange(uiAutomation.FindWindowsByPid(p.Id));
                    }
                }
            }
            else
            {
                return null;
            }

            // Also find cross-process owned windows
            var ownedWindows = ownedWindowFinder.FindOwnedWindows(appWindows);

            return ToCandidates(appWindows, ownedWindows) is { Count: > 1 } candidates ? candidates : null;
        }

        /// <summary>
        /// Combines an app's own windows with the dialogs they own, recording for each the application
        /// process it was discovered for.
        /// </summary>
        /// <remarks>
        /// The finder reports only <em>direct</em> owners — it admits a window when a single
        /// <c>GW_OWNER</c> hop lands inside the app window set — so the owner is re-read here and matched
        /// back to that set. A dialog whose owner cannot be matched is dropped rather than guessed at:
        /// without provenance there is nothing to validate it against later, and capturing it would put
        /// an unattributed window into an image labelled as this application's.
        /// </remarks>
        private List<CaptureCandidate> ToCandidates(
            List<(nint Hwnd, int Pid, string Title)> appWindows,
            List<(nint Hwnd, int Pid, string Title)> ownedWindows)
        {
            var appPidByHwnd = new Dictionary<nint, int>();
            foreach (var w in appWindows)
            {
                appPidByHwnd[w.Hwnd] = w.Pid;
            }

            // An app window stands for itself, so its expected process is its own.
            var candidates = appWindows
                .Select(w => new CaptureCandidate(w.Hwnd, w.Pid, w.Pid, w.Title))
                .ToList();

            foreach (var owned in ownedWindows)
            {
                var ownerHwnd = systemQuery.GetWindowOwner(owned.Hwnd);
                if (ownerHwnd != 0 && appPidByHwnd.TryGetValue(ownerHwnd, out var expectedAppPid))
                {
                    candidates.Add(new CaptureCandidate(owned.Hwnd, owned.Pid, expectedAppPid, owned.Title));
                }
                else
                {
                    logger.LogDebug(
                        "Skipping owned window {Hwnd}: its owner is no longer one of the application's windows.",
                        owned.Hwnd);
                }
            }

            return candidates;
        }
    }
}

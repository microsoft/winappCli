// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Commands;

internal class UiPenCommand : Command, IShortDescription
{
    private const int MaxDelayMs = 60_000;

    public string ShortDescription => "Inject synthetic pen/stylus input (taps and ink strokes with pressure and tilt)";

    public static Option<string?> AtOption { get; } = new("--at")
    {
        Description = "Pen contact point as screen coordinates x,y (as reported by 'ui inspect'). " +
                      "Defaults to the selector's element center. Ignored when --path is given."
    };

    public static Option<string?> PathOption { get; } = new("--path")
    {
        Description = "Ink stroke path as a whitespace-separated list of x,y pairs, e.g. \"10,10 20,30 40,50\"."
    };

    public static Option<float> PressureOption { get; } = new("--pressure")
    {
        Description = "Pen pressure from 0.0 to 1.0 (default: 0.5).",
        DefaultValueFactory = _ => 0.5f
    };

    public static Option<int> TiltXOption { get; } = new("--tilt-x")
    {
        Description = "Pen tilt along the x-axis in degrees (-90 to 90, default: 0).",
        DefaultValueFactory = _ => 0
    };

    public static Option<int> TiltYOption { get; } = new("--tilt-y")
    {
        Description = "Pen tilt along the y-axis in degrees (-90 to 90, default: 0).",
        DefaultValueFactory = _ => 0
    };

    public static Option<bool> EraserOption { get; } = new("--eraser")
    {
        Description = "Use the eraser end of the pen instead of the tip."
    };

    public static Option<int> DurationOption { get; } = new("--duration-ms")
    {
        Description = "Total glide time in milliseconds distributed across the stroke path segments (default: ~10 ms per segment).",
        DefaultValueFactory = _ => 0
    };

    public UiPenCommand()
        : base("pen", "Inject synthetic pen/stylus input using the Windows synthetic-pointer API. " +
               "Taps or draws ink strokes with configurable pressure, tilt and eraser mode, at an element's " +
               "center or explicit screen x,y coordinates. Requires an unlocked, interactive desktop with the " +
               "target window foregroundable (Windows 10 1809+).")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(AtOption);
        Options.Add(PathOption);
        Options.Add(PressureOption);
        Options.Add(TiltXOption);
        Options.Add(TiltYOption);
        Options.Add(EraserOption);
        Options.Add(DurationOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        IUiTargetResolver targetResolver,
        IUiAutomation uiAutomation,
        IUiSelectorParser selectorParser,
        IPointerInput pointerInput,
        IForegroundGuard foregroundGuard,
        IDesktopForegroundService desktopForeground,
        ISystemUiQuery systemQuery,
        IAnsiConsole ansiConsole,
        IInteractiveDesktopLock desktopLock,
        ILogger<UiPenCommand> logger) : UiCoordinatedAction(desktopLock, logger)
    {
        protected override string Operation => "ui pen";

        /// <summary>Synthetic pen injection is OS-wide and lands wherever the desktop points.</summary>
        protected override UiTurnMode ResolveMode(ParseResult parseResult) => UiTurnMode.DesktopExclusive;

        protected override int? Preflight(ParseResult parseResult)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var selectorStr = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var atStr = parseResult.GetValue(AtOption);
            var pathStr = parseResult.GetValue(PathOption);
            var pressure = parseResult.GetValue(PressureOption);
            var tiltX = parseResult.GetValue(TiltXOption);
            var tiltY = parseResult.GetValue(TiltYOption);
            var eraser = parseResult.GetValue(EraserOption);
            var durationMs = parseResult.GetValue(DurationOption);
            bool atWasSupplied = (parseResult.GetResult(AtOption)?.Tokens.Count ?? 0) > 0;
            bool pathWasSupplied = (parseResult.GetResult(PathOption)?.Tokens.Count ?? 0) > 0;
            bool durationWasSupplied = (parseResult.GetResult(DurationOption)?.Tokens.Count ?? 0) > 0;

            // Semantic validation runs BEFORE the missing-app check so a malformed value
            // produces invalid_arguments rather than missing_app (M5 root-cause fix).
            if (!float.IsFinite(pressure) || pressure < 0f || pressure > 1f)
            {
                logger.LogError("{Symbol} --pressure must be a finite number between 0.0 and 1.0. Got '{Pressure}'.", UiSymbols.Error, pressure);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                    $"--pressure must be a finite number between 0.0 and 1.0. Got '{pressure}'.",
                    errorOut: parseResult.InvocationConfiguration.Error);
                return 1;
            }

            if (durationMs < 0)
            {
                logger.LogError("{Symbol} --duration-ms must be zero or positive.", UiSymbols.Error);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, "--duration-ms must be zero or positive.");
                return 1;
            }

            if (durationMs > MaxDelayMs)
            {
                return RejectInvalidArguments(parseResult, json, $"--duration-ms must be {MaxDelayMs} ms or less (60 seconds). Got '{durationMs}'.");
            }

            if (tiltX < -90 || tiltX > 90 || tiltY < -90 || tiltY > 90)
            {
                logger.LogError("{Symbol} --tilt-x and --tilt-y must be between -90 and 90 degrees.", UiSymbols.Error);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, "--tilt-x and --tilt-y must be between -90 and 90 degrees.");
                return 1;
            }

            // --path and --at parsing are app-independent and run before the missing-app check
            // so invalid path/point values return invalid_arguments, not missing_app (M5).
            List<PointerPoint>? path = null;
            if (!string.IsNullOrWhiteSpace(pathStr))
            {
                if (!PointerGesturePlanner.TryParsePath(pathStr, out path))
                {
                    logger.LogError("{Symbol} --path must be whitespace-separated x,y pairs (e.g. \"10,10 20,30\"). Got '{Path}'.", UiSymbols.Error, pathStr);
                    UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, $"--path must be whitespace-separated x,y pairs (e.g. \"10,10 20,30\"). Got '{pathStr}'.", pathStr);
                    return 1;
                }
            }

            if (pathWasSupplied && atWasSupplied)
            {
                return RejectInvalidArguments(parseResult, json, "--at is only valid for pen taps and cannot be combined with --path.");
            }

            if (durationWasSupplied && path is null)
            {
                return RejectInvalidArguments(parseResult, json, "--duration-ms is only valid with --path (got a pen tap).");
            }

            PointerPoint? at = null;
            if (path is null && !string.IsNullOrWhiteSpace(atStr))
            {
                if (!PointerGesturePlanner.TryParsePoint(atStr, out var atPoint))
                {
                    logger.LogError("{Symbol} --at must be a valid x,y pair (e.g. 100,200). Got '{At}'.", UiSymbols.Error, atStr);
                    UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, $"--at must be a valid x,y pair (e.g. 100,200). Got '{atStr}'.", atStr);
                    return 1;
                }
                at = atPoint;
            }

            if (path is null && at is null && string.IsNullOrWhiteSpace(selectorStr))
            {
                logger.LogError("{Symbol} Provide a target: a selector, --at x,y, or --path.", UiSymbols.Error);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, "Provide a target: a selector, --at x,y, or --path.");
                return 1;
            }

            // Missing-app check runs after all argument validation so invalid arg values return
            // invalid_arguments rather than missing_app.
            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }

            return null;
        }

        protected override async Task<int> ExecuteAsync(ParseResult parseResult, IUiTurn turn, CancellationToken cancellationToken)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var selectorStr = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var atStr = parseResult.GetValue(AtOption);
            var pathStr = parseResult.GetValue(PathOption);
            var pressure = parseResult.GetValue(PressureOption);
            var tiltX = parseResult.GetValue(TiltXOption);
            var tiltY = parseResult.GetValue(TiltYOption);
            var eraser = parseResult.GetValue(EraserOption);
            var durationMs = parseResult.GetValue(DurationOption);

            List<PointerPoint>? path = null;
            if (!string.IsNullOrWhiteSpace(pathStr))
            {
                _ = PointerGesturePlanner.TryParsePath(pathStr, out path);
            }

            PointerPoint? at = null;
            if (path is null && !string.IsNullOrWhiteSpace(atStr))
            {
                _ = PointerGesturePlanner.TryParsePoint(atStr, out var atPoint);
                at = atPoint;
            }

            try
            {
                var uiTarget = await targetResolver.ResolveAsync(app, window, cancellationToken);

                long targetHwnd = uiTarget.WindowHandle;
                // L1: report the effective target — pathStr when --path was given, atStr when --at
                // was given, or selectorStr when the selector resolved the contact point.
                var targetLabel = pathStr ?? (at is not null ? atStr : selectorStr);
                PointerCommandSupport.InjectionPreparation prep;

                await using (await turn.EnterAsync(cancellationToken).ConfigureAwait(false))
                {
                    // Build the ink path: explicit --path wins; else --at; else the selector's center.
                    if (path is null)
                    {
                        var target = await PointerCommandSupport.ResolvePointAsync(
                            uiAutomation, selectorParser, uiTarget, selectorStr, at, atStr,
                            "pen", "pen point", logger, json, cancellationToken);
                        if (!target.Ok)
                        {
                            return 1;
                        }

                        targetHwnd = target.TargetHwnd;
                        path = [target.Point];
                    }

                    if (!DesktopTargetValidation.TryConfirmTargetWindow(
                            systemQuery, targetHwnd, uiTarget.ProcessId, logger, json, "pen",
                            parseResult.InvocationConfiguration.Error))
                    {
                        return 1;
                    }

                    await PointerCommandSupport.SetForegroundAsync(desktopForeground, targetHwnd, cancellationToken);

                    prep = PointerCommandSupport.TryPrepareInjection(
                        uiAutomation, foregroundGuard, targetHwnd, path, "pen", "pen input", logger, json);
                    if (!prep.Ok)
                    {
                        return 1;
                    }

                    // M6: narrow the injection_unsupported catch to only the actual injection call so that
                    // pre-injection failures (element not found, etc.) are NOT mis-classified as
                    // injection_unsupported. Session resolution failures surface as missing_app (outer catch).
                    if (!PointerCommandSupport.TryInject(
                        () => pointerInput.Pen(path, pressure, tiltX, tiltY, eraser, durationMs),
                        logger, json, parseResult.InvocationConfiguration.Error))
                    {
                        return 1;
                    }
                }

                var action = eraser ? "erase" : (path.Count > 1 ? "draw" : "tap");

                // id27/id28: synthetic pen injection frequently reports success without reaching the
                // target in a remote session (RDP) — pen routing especially does not survive Remote
                // Desktop. Attach an honest delivery-uncertainty advisory so a ✅ / exit 0 is not
                // mistaken for confirmed delivery.
                var deliveryWarning = PointerCommandSupport.RemoteInjectionWarning(foregroundGuard, "pen");

                // #661: out-of-window point (prep.OutOfWindowWarning) is a non-fatal advisory; surface it
                // alongside any delivery-uncertainty warning rather than failing the command.
                var warnings = new List<string>();
                if (prep.OutOfWindowWarning is not null)
                {
                    warnings.Add(prep.OutOfWindowWarning);
                }
                if (deliveryWarning is not null)
                {
                    warnings.Add(deliveryWarning);
                }

                if (json)
                {
                    var result = new UiPenResult
                    {
                        Action = action,
                        Target = targetLabel,
                        Points = path.Select(p => new UiPointResult { X = p.X, Y = p.Y }).ToArray(),
                        Pressure = pressure,
                        TiltX = tiltX,
                        TiltY = tiltY,
                        Eraser = eraser,
                        DurationMs = durationMs,
                        Hwnd = targetHwnd,
                        Warnings = warnings.Count == 0 ? null : warnings.ToArray()
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiPenResult));
                }
                else
                {
                    logger.LogInformation("{Symbol} pen {Action} with {Count} point(s), pressure {Pressure:0.00}",
                        UiSymbols.Check, action, path.Count, pressure);
                    foreach (var warning in warnings)
                    {
                        logger.LogWarning("{Symbol} {Warning}", UiSymbols.Warning, warning);
                    }
                }

                return 0;
            }
            catch (System.Runtime.InteropServices.COMException comEx)
            {
                logger.LogDebug("COM error: {HResult} {StackTrace}", comEx.HResult, comEx.StackTrace);
                UiErrors.StaleElement(logger, json, parseResult.InvocationConfiguration.Error);
                return 1;
            }
            catch (AppNotFoundException ioEx)
            {
                // Session resolution failure — the requested app was not found.
                // Injection IOE is already caught by the inner try/catch (returns 1 without
                // re-throwing), so AppNotFoundException can only come from ResolveAsync.
                logger.LogError("{Symbol} {Message}", UiSymbols.Error, ioEx.Message);
                UiJsonError.Emit(json, UiJsonError.CodeMissingApp, ioEx.Message,
                    errorOut: parseResult.InvocationConfiguration.Error);
                return 1;
            }
            catch (InvalidOperationException ioEx)
            {
                // A non-app-not-found InvalidOperationException (e.g. selector ambiguity from
                // FindSingleElementAsync: "Selector matched N elements") reaches here. Report it
                // as invalid_arguments so consumers distinguish it from both missing_app and
                // internal_error.
                logger.LogError("{Symbol} {Message}", UiSymbols.Error, ioEx.Message);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, ioEx.Message,
                    errorOut: parseResult.InvocationConfiguration.Error);
                return 1;
            }
            catch (Exception ex) when (!UiCoordinatedAction.IsCoordinationFault(ex))
            {
                UiErrors.GenericError(logger, ex, json, parseResult.InvocationConfiguration.Error);
                return 1;
            }

        }

        private int RejectInvalidArguments(ParseResult parseResult, bool json, string message)
        {
            logger.LogError("{Symbol} {Message}", UiSymbols.Error, message);
            UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, message,
                errorOut: parseResult.InvocationConfiguration.Error);
            return 1;
        }
    }
}

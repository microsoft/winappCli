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

internal class UiTouchCommand : Command, IShortDescription
{
    private const int MaxDelayMs = 60_000;

    public string ShortDescription => "Inject synthetic touch gestures (tap, swipe, pinch, stretch, long-press)";

    private static readonly Dictionary<string, TouchGesture> Gestures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tap"] = TouchGesture.Tap,
        ["double-tap"] = TouchGesture.DoubleTap,
        ["long-press"] = TouchGesture.LongPress,
        ["swipe"] = TouchGesture.Swipe,
        ["pinch"] = TouchGesture.Pinch,
        ["stretch"] = TouchGesture.Stretch,
    };

    public static Option<string?> GestureOption { get; } = new("--gesture", "-g")
    {
        Description = "Gesture to perform: tap, double-tap, long-press, swipe, pinch, stretch (default: tap).",
        DefaultValueFactory = _ => "tap"
    };

    public static Option<string?> AtOption { get; } = new("--at")
    {
        Description = "Explicit start point as screen coordinates x,y (as reported by 'ui inspect'). " +
                      "Defaults to the selector's element center."
    };

    public static Option<string?> ToPointOption { get; } = new("--to-point")
    {
        Description = "End point x,y for a swipe (screen coordinates). Takes precedence over --direction."
    };

    public static Option<int> DistanceOption { get; } = new("--distance")
    {
        Description = "Distance in pixels for pinch/stretch (finger spread) or swipe.",
        DefaultValueFactory = _ => 0
    };

    public static Option<string?> DirectionOption { get; } = new("--direction")
    {
        Description = "Swipe direction: right (default), left, up, or down. Combined with --distance to compute the end point when --to-point is not given.",
        DefaultValueFactory = _ => "right"
    };

    public static Option<int> HoldOption { get; } = new("--hold-ms")
    {
        Description = "Milliseconds to hold contacts down before lifting (long-press hold time). " +
                      "Defaults to 500 ms when --gesture long-press is used and this option is not set.",
        DefaultValueFactory = _ => 0
    };

    public static Option<int> DurationOption { get; } = new("--duration-ms")
    {
        Description = "Glide time in milliseconds for moving gestures (swipe/pinch/stretch).",
        DefaultValueFactory = _ => 300
    };

    public static Option<int> FingersOption { get; } = new("--fingers")
    {
        Description = "Number of touch contacts (default: 1). Pinch/stretch always use 2.",
        DefaultValueFactory = _ => 1
    };

    public UiTouchCommand()
        : base("touch", "Inject synthetic touch input using the Windows touch-injection API. " +
               "Supports tap, double-tap, long-press, swipe, pinch and stretch gestures at an element's " +
               "center or explicit screen x,y coordinates. Requires an unlocked, interactive desktop with the " +
               "target window foregroundable.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(GestureOption);
        Options.Add(AtOption);
        Options.Add(ToPointOption);
        Options.Add(DistanceOption);
        Options.Add(DirectionOption);
        Options.Add(HoldOption);
        Options.Add(DurationOption);
        Options.Add(FingersOption);
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
        ILogger<UiTouchCommand> logger) : UiCoordinatedAction(desktopLock, logger)
    {
        protected override string Operation => "ui touch";

        /// <summary>Synthetic touch injection is OS-wide and lands wherever the desktop points.</summary>
        protected override UiTurnMode ResolveMode(ParseResult parseResult) => UiTurnMode.DesktopExclusive;

        protected override int? Preflight(ParseResult parseResult)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var selectorStr = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var gestureStr = parseResult.GetValue(GestureOption) ?? "tap";
            var atStr = parseResult.GetValue(AtOption);
            var toStr = parseResult.GetValue(ToPointOption);
            var distance = parseResult.GetValue(DistanceOption);
            var direction = parseResult.GetValue(DirectionOption);
            var holdMs = parseResult.GetValue(HoldOption);
            var durationMs = parseResult.GetValue(DurationOption);
            var fingers = parseResult.GetValue(FingersOption);
            bool toPointWasSupplied = (parseResult.GetResult(ToPointOption)?.Tokens.Count ?? 0) > 0;
            bool distanceWasSupplied = (parseResult.GetResult(DistanceOption)?.Tokens.Count ?? 0) > 0;
            bool directionWasSupplied = (parseResult.GetResult(DirectionOption)?.Tokens.Count ?? 0) > 0;
            bool durationWasSupplied = (parseResult.GetResult(DurationOption)?.Tokens.Count ?? 0) > 0;
            bool fingersWasSupplied = (parseResult.GetResult(FingersOption)?.Tokens.Count ?? 0) > 0;

            // All app-independent semantic validation runs BEFORE the missing-app check so that
            // malformed argument values return invalid_arguments, not missing_app (M4 root-cause fix).

            if (!Gestures.TryGetValue(gestureStr, out var gesture))
            {
                logger.LogError("{Symbol} Unknown gesture '{Gesture}'. Allowed: tap, double-tap, long-press, swipe, pinch, stretch.",
                    UiSymbols.Error, gestureStr);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                    $"Unknown gesture '{gestureStr}'. Allowed: tap, double-tap, long-press, swipe, pinch, stretch.");
                return 1;
            }

            if (holdMs < 0 || durationMs < 0 || distance < 0 || fingers < 1)
            {
                logger.LogError("{Symbol} --hold-ms/--duration-ms/--distance must be zero or positive and --fingers >= 1.", UiSymbols.Error);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                    "--hold-ms/--duration-ms/--distance must be zero or positive and --fingers >= 1.");
                return 1;
            }

            if (holdMs > MaxDelayMs)
            {
                return RejectInvalidArguments(parseResult, json, $"--hold-ms must be {MaxDelayMs} ms or less (60 seconds). Got '{holdMs}'.");
            }

            if (durationMs > MaxDelayMs)
            {
                return RejectInvalidArguments(parseResult, json, $"--duration-ms must be {MaxDelayMs} ms or less (60 seconds). Got '{durationMs}'.");
            }

            // Validate --direction value up front.
            var validDirections = new[] { "right", "left", "up", "down" };
            if (!string.IsNullOrEmpty(direction) && !validDirections.Contains(direction.ToLowerInvariant()))
            {
                logger.LogError("{Symbol} --direction must be one of: right, left, up, down. Got '{Direction}'.", UiSymbols.Error, direction);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                    $"--direction must be one of: right, left, up, down. Got '{direction}'.");
                return 1;
            }

            var isSwipe = gesture is TouchGesture.Swipe;
            var isStationaryGesture = gesture is TouchGesture.Tap or TouchGesture.DoubleTap or TouchGesture.LongPress;

            if (toPointWasSupplied && !isSwipe)
            {
                return RejectInvalidArguments(parseResult, json, $"--to-point is only valid with --gesture swipe (got {gestureStr}).");
            }

            if (directionWasSupplied && !isSwipe)
            {
                return RejectInvalidArguments(parseResult, json, $"--direction is only valid with --gesture swipe (got {gestureStr}).");
            }

            if (distanceWasSupplied && isStationaryGesture)
            {
                return RejectInvalidArguments(parseResult, json, $"--distance is only valid with --gesture swipe, pinch, or stretch (got {gestureStr}).");
            }

            if (durationWasSupplied && isStationaryGesture)
            {
                return RejectInvalidArguments(parseResult, json, $"--duration-ms is only valid with moving gestures: swipe, pinch, or stretch (got {gestureStr}).");
            }

            // Long-press with no explicit --hold-ms defaults to 500 ms (a real long-press).
            // An explicit --hold-ms 0 is a degenerate long-press (indistinguishable from a tap)
            // and is rejected clearly rather than silently rewritten to 500.
            if (gesture is TouchGesture.LongPress)
            {
                bool holdWasSupplied = (parseResult.GetResult(HoldOption)?.Tokens.Count ?? 0) > 0;
                if (!holdWasSupplied)
                {
                    holdMs = 500;
                }
                else if (holdMs == 0)
                {
                    logger.LogError("{Symbol} --hold-ms 0 is invalid with --gesture long-press (degenerate hold). " +
                        "Omit --hold-ms to get the 500 ms default or supply a positive value.", UiSymbols.Error);
                    UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                        "--hold-ms 0 is invalid with --gesture long-press. Omit --hold-ms to use the 500 ms default or supply a positive value.");
                    return 1;
                }
            }

            if (fingers > PointerGesturePlanner.MaxContacts)
            {
                logger.LogError("{Symbol} --fingers must be between 1 and {Max} (the touch-injection contact limit).",
                    UiSymbols.Error, PointerGesturePlanner.MaxContacts);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                    $"--fingers must be between 1 and {PointerGesturePlanner.MaxContacts} (the touch-injection contact limit).");
                return 1;
            }

            if (fingersWasSupplied && gesture is TouchGesture.Pinch or TouchGesture.Stretch && fingers != 2)
            {
                return RejectInvalidArguments(parseResult, json, $"--fingers must be 2 with --gesture {gestureStr}; pinch/stretch always use 2 contacts.");
            }

            // Parse an explicit start point up front (mutually independent of selector resolution).
            PointerPoint? at = null;
            if (!string.IsNullOrWhiteSpace(atStr))
            {
                if (!PointerGesturePlanner.TryParsePoint(atStr, out var atPoint))
                {
                    logger.LogError("{Symbol} --at must be a valid x,y pair (e.g. 100,200). Got '{At}'.", UiSymbols.Error, atStr);
                    UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, $"--at must be a valid x,y pair (e.g. 100,200). Got '{atStr}'.", atStr);
                    return 1;
                }
                at = atPoint;
            }

            PointerPoint? to = null;
            if (!string.IsNullOrWhiteSpace(toStr))
            {
                if (!PointerGesturePlanner.TryParsePoint(toStr, out var toPoint))
                {
                    logger.LogError("{Symbol} --to-point must be a valid x,y pair (e.g. 300,400). Got '{To}'.", UiSymbols.Error, toStr);
                    UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, $"--to-point must be a valid x,y pair (e.g. 300,400). Got '{toStr}'.", toStr);
                    return 1;
                }
                to = toPoint;
            }

            if (at is null && string.IsNullOrWhiteSpace(selectorStr))
            {
                logger.LogError("{Symbol} Provide a target: a selector, or --at x,y.", UiSymbols.Error);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, "Provide a target: a selector, or --at x,y.");
                return 1;
            }

            if (gesture is TouchGesture.Pinch or TouchGesture.Stretch && distance <= 0)
            {
                logger.LogError("{Symbol} {Gesture} requires --distance (finger spread in pixels).", UiSymbols.Error, gestureStr);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, $"{gestureStr} requires --distance (finger spread in pixels).");
                return 1;
            }

            if (gesture is TouchGesture.Swipe && to is null && distance <= 0)
            {
                logger.LogError("{Symbol} swipe requires --to-point x,y or --distance (combined with optional --direction).", UiSymbols.Error);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, "swipe requires --to-point x,y or --distance (combined with optional --direction).");
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
            var gestureStr = parseResult.GetValue(GestureOption) ?? "tap";
            var atStr = parseResult.GetValue(AtOption);
            var toStr = parseResult.GetValue(ToPointOption);
            var distance = parseResult.GetValue(DistanceOption);
            var direction = parseResult.GetValue(DirectionOption);
            var holdMs = parseResult.GetValue(HoldOption);
            var durationMs = parseResult.GetValue(DurationOption);
            var fingers = parseResult.GetValue(FingersOption);

            _ = Gestures.TryGetValue(gestureStr, out var gesture);

            if (gesture is TouchGesture.LongPress
                && (parseResult.GetResult(HoldOption)?.Tokens.Count ?? 0) == 0)
            {
                holdMs = 500;
            }

            PointerPoint? at = null;
            if (!string.IsNullOrWhiteSpace(atStr))
            {
                _ = PointerGesturePlanner.TryParsePoint(atStr, out var atPoint);
                at = atPoint;
            }

            PointerPoint? to = null;
            if (!string.IsNullOrWhiteSpace(toStr))
            {
                _ = PointerGesturePlanner.TryParsePoint(toStr, out var toPoint);
                to = toPoint;
            }

            try
            {
                var uiTarget = await targetResolver.ResolveAsync(app, window, cancellationToken);

                long targetHwnd;
                PointerPoint start;
                string? targetLabel;
                IReadOnlyList<IReadOnlyList<PointerPoint>> contactPaths;
                IReadOnlyList<PointerPoint> points;
                int effectiveFingers;
                PointerCommandSupport.InjectionPreparation prep;

                await using (await turn.EnterAsync(cancellationToken).ConfigureAwait(false))
                {
                    var target = await PointerCommandSupport.ResolvePointAsync(
                        uiAutomation, selectorParser, uiTarget, selectorStr, at, atStr,
                        "touch", "touch point", logger, json, cancellationToken);
                    if (!target.Ok)
                    {
                        return 1;
                    }

                    targetHwnd = target.TargetHwnd;
                    start = target.Point;
                    targetLabel = target.TargetLabel;

                    if (!DesktopTargetValidation.TryConfirmTargetWindow(
                            systemQuery, targetHwnd, uiTarget.ProcessId, logger, json, "touch",
                            parseResult.InvocationConfiguration.Error))
                    {
                        return 1;
                    }

                    await PointerCommandSupport.SetForegroundAsync(desktopForeground, targetHwnd, cancellationToken);

                    (contactPaths, points, effectiveFingers) =
                        PointerGesturePlanner.PlanTouch(gesture, start, to, distance, fingers, direction);

                    prep = PointerCommandSupport.TryPrepareInjection(
                        uiAutomation, foregroundGuard, targetHwnd, points, "touch", "touch", logger, json);
                    if (!prep.Ok)
                    {
                        return 1;
                    }

                    // M8: narrow the injection_unsupported catch to only the actual injection call so that
                    // pre-injection failures (element not found, etc.) are NOT mis-classified as
                    // injection_unsupported. Session resolution failures surface as missing_app (outer catch).
                    if (!PointerCommandSupport.TryInject(
                        () => pointerInput.Touch(gesture, contactPaths, holdMs, durationMs),
                        logger, json, parseResult.InvocationConfiguration.Error))
                    {
                        return 1;
                    }
                }

                // id27/id28: synthetic touch injection can report success without actually reaching the
                // target in a remote session (RDP). Attach an honest delivery-uncertainty advisory so a
                // ✅ / exit 0 is not mistaken for confirmed delivery.
                var deliveryWarning = PointerCommandSupport.RemoteInjectionWarning(foregroundGuard, "touch");

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
                    var result = new UiTouchResult
                    {
                        Gesture = gestureStr.ToLowerInvariant(),
                        Target = targetLabel,
                        Points = points.Select(p => new UiPointResult { X = p.X, Y = p.Y }).ToArray(),
                        Fingers = effectiveFingers,
                        DurationMs = durationMs,
                        HoldMs = holdMs,
                        Hwnd = targetHwnd,
                        Warnings = warnings.Count == 0 ? null : warnings.ToArray()
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiTouchResult));
                }
                else
                {
                    logger.LogInformation("{Symbol} {Gesture} at ({X}, {Y}) with {Fingers} finger(s)",
                        UiSymbols.Check, gestureStr, start.X, start.Y, effectiveFingers);
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

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

namespace WinApp.Cli.Commands;

internal class UiDragCommand : Command, IShortDescription
{
    public string ShortDescription => "Drag from one element/point to another element/point";

    // drag <from> <to> — each endpoint is an element selector (drags from/to the element's center)
    // or screen x,y coordinates in the same space 'ui inspect' reports.
    public static Argument<string?> FromArgument { get; } = new("from")
    {
        Description = "Start point — an element selector (drags from its center) or screen coordinates x,y as reported by " +
                      "'ui inspect' (e.g. pn-list-d736 or 100,200).",
        Arity = ArgumentArity.ZeroOrOne
    };

    public static Argument<string?> ToArgument { get; } = new("to")
    {
        Description = "End point — an element selector (drops at its center) or screen coordinates x,y as reported by " +
                      "'ui inspect' (e.g. pn-target-d746 or 300,400).",
        Arity = ArgumentArity.ZeroOrOne
    };

    public static Option<bool> RightButtonOption { get; } = new("--right")
    {
        Description = "Drag with the right mouse button instead of the left button"
    };

    public static Option<int> HoldOption { get; } = new("--hold-ms")
    {
        Description = "Milliseconds to hold the button down at the start before moving (default: 0). " +
                      "With <from> == <to> (no movement) this performs a press-and-hold / long-press gesture.",
        DefaultValueFactory = _ => 0
    };

    public static Option<int> DwellOption { get; } = new("--dwell-ms")
    {
        Description = "Milliseconds to dwell at the destination after moving, before releasing (default: 0). " +
                      "Lets drop targets / merge overlays that arm from a sustained hover latch before release.",
        DefaultValueFactory = _ => 0
    };

    public UiDragCommand()
        : base("drag", "Press the mouse button at one point, move to another, then release. " +
               "'drag <from> <to>', where <from>/<to> are each an element selector (uses the element's center) or " +
               "screen x,y coordinates as reported by 'ui inspect'. Useful for reorder/resize/slider gestures " +
               "and drag-and-drop. Use --right for a right-button drag, --hold-ms for press-and-hold/long-press, and " +
               "--dwell-ms to settle on a drop target before releasing.")
    {
        Arguments.Add(FromArgument);
        Arguments.Add(ToArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(RightButtonOption);
        Options.Add(HoldOption);
        Options.Add(DwellOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        IUiTargetResolver targetResolver,
        IUiAutomation uiAutomation,
        IUiSelectorParser selectorParser,
        IMouseInput mouseInput,
        IForegroundGuard foregroundGuard,
        IAnsiConsole ansiConsole,
        ILogger<UiDragCommand> logger) : AsynchronousCommandLineAction
    {
        // Cursor-settle pause (ms) after positioning on the from-point, before the confirm read + press.
        private const int CursorSettleMs = 50;

        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var arg0 = parseResult.GetValue(FromArgument);
            var arg1 = parseResult.GetValue(ToArgument);
            var rightButton = parseResult.GetValue(RightButtonOption);
            var holdMs = parseResult.GetValue(HoldOption);
            var dwellMs = parseResult.GetValue(DwellOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }

            if (holdMs < 0 || dwellMs < 0)
            {
                logger.LogError("{Symbol} --hold-ms and --dwell-ms must be zero or positive.", UiSymbols.Error);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                    "--hold-ms and --dwell-ms must be zero or positive.");
                return 1;
            }

            try
            {
                var uiTarget = await targetResolver.ResolveAsync(app, window, cancellationToken);

                if (string.IsNullOrWhiteSpace(arg0) || string.IsNullOrWhiteSpace(arg1))
                {
                    logger.LogError("{Symbol} Specify both <from> and <to> — each is an element selector or x,y coordinates.", UiSymbols.Error);
                    UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                        "Specify both <from> and <to> — each is an element selector or x,y coordinates.");
                    return 1;
                }

                var from = await ResolveEndpointAsync(arg0, "from", uiTarget, json, cancellationToken);
                if (!from.Ok)
                {
                    return 1;
                }

                var to = await ResolveEndpointAsync(arg1, "to", uiTarget, json, cancellationToken);
                if (!to.Ok)
                {
                    return 1;
                }

                int fromX = from.X;
                int fromY = from.Y;
                int toX = to.X;
                int toY = to.Y;
                // Prefer the HWND of whichever endpoint resolved from a real element; fall back to the
                // session window when both endpoints are bare coordinates.
                long targetHwnd = from.Hwnd != 0 ? from.Hwnd : (to.Hwnd != 0 ? to.Hwnd : uiTarget.WindowHandle);

                if (targetHwnd != 0)
                {
                    Windows.Win32.PInvoke.SetForegroundWindow(
                        new Windows.Win32.Foundation.HWND((nint)targetHwnd));
                    await Task.Delay(100, cancellationToken);
                }

                // Foregrounding can shift/animate the window (restore, snap, layout settle); re-resolve any
                // element endpoint so we drag where it is *now*, and refuse rather than hit empty space if
                // it's still moving. Raw-coordinate endpoints can't be verified, so they pass through.
                var fromStable = await StabilizeAsync(from, uiTarget, "from", json, cancellationToken);
                if (!fromStable.Ok)
                {
                    return 1;
                }

                var toStable = await StabilizeAsync(to, uiTarget, "to", json, cancellationToken);
                if (!toStable.Ok)
                {
                    return 1;
                }

                fromX = fromStable.X;
                fromY = fromStable.Y;
                toX = toStable.X;
                toY = toStable.Y;

                // Verify the target STILL holds the foreground as the first gate before the OS-wide drag.
                // The stabilize re-resolve above performs awaited UIA reads (with delays); another window
                // could steal focus during that gap, so we check here — after the awaits, not before them.
                // Also distinguishes a locked/secure desktop from a wrong-window foreground. (For an element
                // from-point a second, final gate runs below, after the cursor-settle confirm read.)
                if (!foregroundGuard.TryEnsureForeground(targetHwnd, logger, json, "drag"))
                {
                    return 1;
                }

                // Close the residual re-resolve→button-down race for the from-point (mirrors click's F3
                // fix): the button-down happens at <from>, and MouseInput.Drag's own pre-press settle is an
                // unguarded window in which a still-animating from-element could drift, so the press grabs
                // empty space yet the drag reports success. When <from> is an element, position the cursor
                // on it, let it settle, confirm it hasn't moved, re-check the foreground, then press with
                // settleMs: 0 — so a reported ✅ means the button went down on the element. A raw-coordinate
                // from-point has nothing to confirm and keeps MouseInput.Drag's internal settle.
                int dragSettleMs = 50;
                if (from.Selector is not null && fromStable.StableElement is not null)
                {
                    mouseInput.MoveCursor(fromX, fromY);
                    await Task.Delay(CursorSettleMs, cancellationToken);

                    var confirmed = await GestureTargeting.ConfirmStillAsync(
                        uiAutomation, uiTarget, from.Selector, fromStable.StableElement, cancellationToken);
                    if (!UiInjectionReporting.TryReport(confirmed, logger, json, from.Token ?? "from", "drag"))
                    {
                        return 1;
                    }
                    fromX = confirmed.CenterX;
                    fromY = confirmed.CenterY;

                    // Final foreground gate after the awaited confirm read (focus could shift during it).
                    if (!foregroundGuard.TryEnsureForeground(targetHwnd, logger, json, "drag"))
                    {
                        return 1;
                    }

                    // Cursor already positioned on the just-confirmed from-point; press without re-settling.
                    dragSettleMs = 0;
                }

                mouseInput.Drag(fromX, fromY, toX, toY, rightButton, holdMs, dwellMs, settleMs: dragSettleMs);

                var button = rightButton ? "right" : "left";

                if (json)
                {
                    var result = new UiDragResult
                    {
                        From = arg0,
                        To = arg1,
                        FromX = fromX,
                        FromY = fromY,
                        ToX = toX,
                        ToY = toY,
                        Button = button,
                        HoldMs = holdMs,
                        DwellMs = dwellMs,
                        Hwnd = targetHwnd
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiDragResult));
                }
                else
                {
                    logger.LogInformation("{Symbol} {Button}-dragged from ({FromX}, {FromY}) to ({ToX}, {ToY})",
                        UiSymbols.Check, button, fromX, fromY, toX, toY);
                }

                return 0;
            }
            catch (System.Runtime.InteropServices.COMException comEx)
            {
                logger.LogDebug("COM error: {HResult} {StackTrace}", comEx.HResult, comEx.StackTrace);
                UiErrors.StaleElement(logger, json);
                return 1;
            }
            catch (Exception ex)
            {
                UiErrors.GenericError(logger, ex, json);
                return 1;
            }
        }

        private readonly record struct Endpoint(bool Ok, int X, int Y, long Hwnd, UiSelector? Selector, UiElement? Element, string? Token);

        /// <summary>
        /// Re-resolves an element endpoint's bounds immediately before injection (N5) so the drag uses the
        /// element's current location, not a rectangle captured before the window was foregrounded. A
        /// raw-coordinate endpoint has no element to verify and is returned unchanged. On a vanished or
        /// never-settling target it emits <c>target_moved</c> and returns Ok = <see langword="false"/>.
        /// </summary>
        private async Task<(bool Ok, int X, int Y, UiElement? StableElement)> StabilizeAsync(
            Endpoint endpoint, UiTarget uiTarget, string label, bool json, CancellationToken cancellationToken)
        {
            if (endpoint.Selector is null || endpoint.Element is null)
            {
                return (true, endpoint.X, endpoint.Y, null);
            }

            var stable = await GestureTargeting.ResolveStableAsync(
                uiAutomation, uiTarget, endpoint.Selector, endpoint.Element,
                GestureTargeting.DefaultMaxReads, GestureTargeting.DefaultReadDelayMs, null, cancellationToken);

            // Report the actual selector token the caller passed (e.g. "row-1") rather than the internal
            // "from"/"to" endpoint label, so a target_moved error's selector field is actionable.
            if (!UiInjectionReporting.TryReport(stable, logger, json, endpoint.Token ?? label, "drag"))
            {
                return (false, 0, 0, null);
            }

            return (true, stable.CenterX, stable.CenterY, stable.Element);
        }

        /// <summary>
        /// Resolves a drag endpoint token into screen coordinates. A token of the form <c>x,y</c> is taken as
        /// screen coordinates (the same space <c>ui inspect</c> reports); anything else is treated as an element
        /// selector and resolves to that element's center. Emits the appropriate error and returns
        /// <see cref="Endpoint.Ok"/> = <see langword="false"/> on failure.
        /// </summary>
        private async Task<Endpoint> ResolveEndpointAsync(
            string token, string label, UiTarget uiTarget, bool json, CancellationToken cancellationToken)
        {
            if (CoordinateParser.TryParsePoint(token, out int px, out int py))
            {
                return new Endpoint(true, px, py, 0, null, null, null);
            }

            // A comma-separated token whose first field is an integer was meant as x,y coordinates but
            // didn't parse (a trailing/extra field or a non-numeric one — "100,", "100,200,300", "100,x").
            // Surface a precise "expected x,y" error instead of falling through to a selector lookup that
            // reports a misleading "element not found".
            if (CoordinateParser.LooksLikeCoordinates(token))
            {
                logger.LogError("{Symbol} <{Label}> looks like coordinates but isn't a valid x,y pair: '{Token}'. Use two integers, e.g. 100,200.",
                    UiSymbols.Error, label, token);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                    $"<{label}> looks like coordinates but isn't a valid x,y pair: '{token}'. Use two integers, e.g. 100,200.", token);
                return new Endpoint(false, 0, 0, 0, null, null, null);
            }

            var selector = selectorParser.Parse(token);
            var element = await uiAutomation.FindSingleElementAsync(uiTarget, selector, cancellationToken);
            if (element is null)
            {
                UiErrors.ElementNotFound(logger, token, json);
                return new Endpoint(false, 0, 0, 0, null, null, null);
            }

            if (element.Width == 0 || element.Height == 0)
            {
                logger.LogError("{Symbol} Element for <{Label}> has zero size — cannot use its center as a drag point.", UiSymbols.Error, label);
                UiJsonError.Emit(json, UiJsonError.CodeZeroSize,
                    $"Element for <{label}> has zero size — cannot use its center as a drag point.", token);
                return new Endpoint(false, 0, 0, 0, null, null, null);
            }

            int centerX = (int)(element.X + element.Width / 2.0);
            int centerY = (int)(element.Y + element.Height / 2.0);
            return new Endpoint(true, centerX, centerY, element.WindowHandle ?? 0, selector, element, token);
        }

    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class UiScrollCommand : Command, IShortDescription
{
    public string ShortDescription => "Scroll a container element";

    /// <summary>One mouse-wheel detent in WHEEL_DELTA units, the granularity SendInput's wheel expects.</summary>
    private const int WheelDelta = 120;

    public static Option<string?> DirectionOption { get; }
    public static Option<string?> ToOption { get; }
    public static Option<int?> WheelOption { get; }

    static UiScrollCommand()
    {
        DirectionOption = new Option<string?>("--direction")
        {
            Description = "Scroll direction: up, down, left, right"
        };

        ToOption = new Option<string?>("--to")
        {
            Description = "Scroll to position: top, bottom"
        };

        WheelOption = new Option<int?>("--wheel")
        {
            Description = "Rotate the mouse wheel over the element by this many notches (1 = one notch up, -1 = one notch down). " +
                          "Synthesizes real wheel input instead of using ScrollPattern."
        };
    }

    public UiScrollCommand()
        : base("scroll", "Scroll a container element using ScrollPattern. " +
               "Use --direction to scroll incrementally, --to to jump to top/bottom, or --wheel to synthesize mouse-wheel input.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(WinAppRootCommand.JsonOption);
        Options.Add(DirectionOption);
        Options.Add(ToOption);
        Options.Add(WheelOption);
    }

    public class Handler(
        IUiTargetResolver targetResolver,
        IUiAutomation uiAutomation,
        IUiSelectorParser selectorParser,
        IMouseInput mouseInput,
        IForegroundGuard foregroundGuard,
        IAnsiConsole ansiConsole,
        ILogger<UiScrollCommand> logger) : AsynchronousCommandLineAction
    {
        // Cursor-settle pause (ms) after positioning over the target, before the confirm read + wheel.
        private const int CursorSettleMs = 30;

        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var selectorStr = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }

            var direction = parseResult.GetValue(DirectionOption);
            var to = parseResult.GetValue(ToOption);
            var wheel = parseResult.GetValue(WheelOption);

            if (string.IsNullOrWhiteSpace(selectorStr))
            {
                UiErrors.MissingSelector(logger, "scroll", json);
                return 1;
            }

            if (direction is null && to is null && wheel is null)
            {
                logger.LogError("{Symbol} Specify --direction (up/down/left/right), --to (top/bottom), or --wheel (delta).", UiSymbols.Error);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                    "Specify --direction (up/down/left/right), --to (top/bottom), or --wheel (delta).");
                return 1;
            }

            int modeCount = (direction is not null ? 1 : 0) + (to is not null ? 1 : 0) + (wheel is not null ? 1 : 0);
            if (modeCount > 1)
            {
                logger.LogError("{Symbol} Specify only one of --direction, --to, or --wheel.", UiSymbols.Error);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                    "Specify only one of --direction, --to, or --wheel — they are mutually exclusive.");
                return 1;
            }

            try
            {
                var uiTarget = await targetResolver.ResolveAsync(app, window, cancellationToken);
                var selector = selectorParser.Parse(selectorStr);
                var element = await uiAutomation.FindSingleElementAsync(uiTarget, selector, cancellationToken);

                if (element is null)
                {
                    UiErrors.ElementNotFound(logger, selectorStr, json);
                    return 1;
                }

                var targetHwnd = element.WindowHandle ?? uiTarget.WindowHandle;

                if (wheel is int notches)
                {
                    int centerX = (int)(element.X + element.Width / 2.0);
                    int centerY = (int)(element.Y + element.Height / 2.0);

                    if (element.Width == 0 || element.Height == 0)
                    {
                        logger.LogError("{Symbol} Element has zero size — cannot scroll-wheel over it.", UiSymbols.Error);
                        UiJsonError.Emit(json, UiJsonError.CodeZeroSize, "Element has zero size — cannot scroll-wheel over it.", selectorStr);
                        return 1;
                    }

                    if (targetHwnd != 0)
                    {
                        Windows.Win32.PInvoke.SetForegroundWindow(
                            new Windows.Win32.Foundation.HWND((nint)targetHwnd));
                        await Task.Delay(100, cancellationToken);
                    }

                    // Re-resolve just before scrolling (N5): foregrounding can restore/animate the window,
                    // so the captured rect may be stale. Refuse rather than scroll empty space if it's
                    // still moving.
                    var stable = await GestureTargeting.ResolveStableAsync(
                        uiAutomation, uiTarget, selector, element,
                        GestureTargeting.DefaultMaxReads, GestureTargeting.DefaultReadDelayMs, null, cancellationToken);
                    if (!UiInjectionReporting.TryReport(stable, logger, json, selectorStr, "scroll --wheel"))
                    {
                        return 1;
                    }
                    centerX = stable.CenterX;
                    centerY = stable.CenterY;

                    // Verify the target STILL holds the foreground as the first gate before the OS-wide
                    // wheel injection. The re-resolve above awaits UIA reads (with delays) during which
                    // another window could steal focus, so we check here — after the awaits, not before
                    // them. Also distinguishes a locked/secure desktop from a wrong-window foreground.
                    if (!foregroundGuard.TryEnsureForeground(targetHwnd, logger, json, "scroll --wheel"))
                    {
                        return 1;
                    }

                    // Close the residual re-resolve→wheel race (mirrors click/drag): ScrollWheel positions
                    // the cursor and settles before injecting, which is an unguarded window in which a
                    // still-animating target could drift, routing the wheel to whatever is now under the
                    // pointer. Position the cursor, let it settle, confirm the target hasn't moved, re-check
                    // the foreground, then inject with settleMs: 0 — so a reported ✅ means the wheel went to
                    // the element.
                    mouseInput.MoveCursor(centerX, centerY);
                    await Task.Delay(CursorSettleMs, cancellationToken);

                    var confirmed = await GestureTargeting.ConfirmStillAsync(
                        uiAutomation, uiTarget, selector, stable.Element, cancellationToken);
                    if (!UiInjectionReporting.TryReport(confirmed, logger, json, selectorStr, "scroll --wheel"))
                    {
                        return 1;
                    }
                    centerX = confirmed.CenterX;
                    centerY = confirmed.CenterY;

                    // Final foreground gate after the awaited confirm read (focus could shift during it).
                    if (!foregroundGuard.TryEnsureForeground(targetHwnd, logger, json, "scroll --wheel"))
                    {
                        return 1;
                    }

                    // --wheel is expressed in notches for ergonomics; SendInput's mouse wheel works in
                    // WHEEL_DELTA units (120 per detent), so scale up to the raw delta the OS expects. The
                    // cursor is already positioned and the target just confirmed, so skip the inner settle.
                    mouseInput.ScrollWheel(centerX, centerY, notches * WheelDelta, settleMs: 0);
                }
                else
                {
                    await uiAutomation.ScrollContainerAsync(uiTarget, element, direction, to, cancellationToken);
                }

                if (json)
                {
                    var result = new UiScrollResult
                    {
                        ElementId = (element.Selector ?? element.Id ?? ""),
                        Direction = direction,
                        To = to,
                        Wheel = wheel,
                        Hwnd = targetHwnd
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiScrollResult));
                }
                else
                {
                    logger.LogInformation("Scrolled {Selector}", selectorStr);
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
    }
}

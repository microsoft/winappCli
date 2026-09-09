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
using WinApp.Cli.Services.InteractiveDesktop;

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
        IDesktopForegroundService desktopForeground,
        ISystemUiQuery systemQuery,
        IAnsiConsole ansiConsole,
        IInteractiveDesktopLock desktopLock,
        ILogger<UiScrollCommand> logger) : UiCoordinatedAction(desktopLock, logger)
    {
        // Cursor-settle pause (ms) after positioning over the target, before the confirm read + wheel.
        private const int CursorSettleMs = 30;

        protected override string Operation => "ui scroll";

        /// <remarks>
        /// Two different commands share one name. <c>--wheel</c> synthesizes real mouse input at screen
        /// coordinates, so it foregrounds the target and needs the desktop exclusively. <c>--direction</c>
        /// and <c>--to</c> drive UIA <c>ScrollPattern</c> in the background: they never take
        /// <c>active.lock</c> and stay usable on a locked or headless session, but they do move the
        /// container, so they wait behind a foreign workflow rather than scrolling content out from under
        /// somebody else's click.
        /// </remarks>
        protected override UiTurnMode ResolveMode(ParseResult parseResult)
            => parseResult.GetValue(WheelOption) is not null ? UiTurnMode.DesktopExclusive : UiTurnMode.TurnShared;

        protected override int? Preflight(ParseResult parseResult)
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

            return null;
        }

        protected override async Task<int> ExecuteAsync(ParseResult parseResult, IUiTurn turn, CancellationToken cancellationToken)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            // Preflight rejected a missing selector, so this is non-null by construction.
            var selectorStr = parseResult.GetValue(SharedUiOptions.SelectorArgument)!;
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var direction = parseResult.GetValue(DirectionOption);
            var to = parseResult.GetValue(ToOption);
            var wheel = parseResult.GetValue(WheelOption);

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
                    if (element.Width == 0 || element.Height == 0)
                    {
                        logger.LogError("{Symbol} Element has zero size — cannot scroll-wheel over it.", UiSymbols.Error);
                        UiJsonError.Emit(json, UiJsonError.CodeZeroSize, "Element has zero size — cannot scroll-wheel over it.", selectorStr);
                        return 1;
                    }

                    int centerX;
                    int centerY;

                    await using (await turn.EnterAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var stable = await GestureTargeting.ResolveStableAsync(
                            uiAutomation, uiTarget, selector, element,
                            GestureTargeting.DefaultMaxReads, GestureTargeting.DefaultReadDelayMs, null, cancellationToken);
                        if (!UiInjectionReporting.TryReport(stable, logger, json, selectorStr, "scroll --wheel"))
                        {
                            return 1;
                        }
                        targetHwnd = stable.Element.WindowHandle ?? uiTarget.WindowHandle;
                        centerX = stable.CenterX;
                        centerY = stable.CenterY;

                        if (!DesktopTargetValidation.TryConfirmTargetWindow(
                                systemQuery, targetHwnd, uiTarget.ProcessId, logger, json, "scroll --wheel", parseResult.InvocationConfiguration.Error))
                        {
                            return 1;
                        }

                        if (targetHwnd != 0)
                        {
                            desktopForeground.RequestForeground(targetHwnd);
                            await Task.Delay(100, cancellationToken);
                        }

                        if (!foregroundGuard.TryEnsureForeground(targetHwnd, logger, json, "scroll --wheel"))
                        {
                            return 1;
                        }

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

                        if (!foregroundGuard.TryEnsureForeground(targetHwnd, logger, json, "scroll --wheel"))
                        {
                            return 1;
                        }

                        // --wheel is expressed in notches for ergonomics; SendInput's mouse wheel works in
                        // WHEEL_DELTA units (120 per detent), so scale up to the raw delta the OS expects.
                        mouseInput.ScrollWheel(centerX, centerY, notches * WheelDelta, settleMs: 0);
                    }
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
            catch (Exception ex) when (!UiCoordinatedAction.IsCoordinationFault(ex))
            {
                UiErrors.GenericError(logger, ex, json);
                return 1;
            }
        }
    }
}

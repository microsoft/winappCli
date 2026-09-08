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

internal class UiHoverCommand : Command, IShortDescription
{
    public string ShortDescription => "Move the mouse to an element to trigger hover effects like tooltips";

    public static Option<int> DwellTimeOption { get; } = new("--dwell-time")
    {
        Description = "Time in milliseconds to wait after hovering for hover effects to appear (default: 800)",
        DefaultValueFactory = _ => 800
    };

    public UiHoverCommand()
        : base("hover", "Move the mouse to an element's center to trigger hover effects (tooltips, flyouts, visual states). " +
               "Uses SendInput for realistic mouse movement and waits for a configurable dwell time.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(DwellTimeOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        IUiTargetResolver targetResolver,
        IUiAutomation uiAutomation,
        IUiSelectorParser selectorParser,
        IMouseInput mouseInput,
        IForegroundGuard foregroundGuard,
        IAnsiConsole ansiConsole,
        ILogger<UiHoverCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var selectorStr = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var dwellTime = parseResult.GetValue(DwellTimeOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }

            if (string.IsNullOrWhiteSpace(selectorStr))
            {
                UiErrors.MissingSelector(logger, "hover", json);
                return 1;
            }

            if (dwellTime < 0 || dwellTime > 10_000)
            {
                logger.LogError("{Symbol} --dwell-time must be between 0 and 10000 ms.", UiSymbols.Error);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, "--dwell-time must be between 0 and 10000 ms.");
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

                int centerX = (int)(element.X + element.Width / 2.0);
                int centerY = (int)(element.Y + element.Height / 2.0);

                if (element.Width == 0 || element.Height == 0)
                {
                    logger.LogError("{Symbol} Element has zero size — cannot hover.", UiSymbols.Error);
                    UiJsonError.Emit(json, UiJsonError.CodeZeroSize, "Element has zero size — cannot hover.", selectorStr);
                    return 1;
                }

                // Use the element's own window handle if available, otherwise fall back to session
                var targetHwnd = element.WindowHandle ?? uiTarget.WindowHandle;

                // Bring target window to foreground
                if (targetHwnd != 0)
                {
                    Windows.Win32.PInvoke.SetForegroundWindow(
                        new Windows.Win32.Foundation.HWND((nint)targetHwnd));
                    await Task.Delay(100, cancellationToken);
                }

                // Re-resolve just before hovering (N5): foregrounding can restore/animate the window, so
                // the captured rect may be stale. Refuse rather than hover empty space if it's still moving.
                var stable = await GestureTargeting.ResolveStableAsync(
                    uiAutomation, uiTarget, selector, element,
                    GestureTargeting.DefaultMaxReads, GestureTargeting.DefaultReadDelayMs, null, cancellationToken);
                if (!UiInjectionReporting.TryReport(stable, logger, json, selectorStr, "hover"))
                {
                    return 1;
                }
                centerX = stable.CenterX;
                centerY = stable.CenterY;

                // Verify the target STILL holds the foreground as the final gate before the OS-wide hover
                // (F1) — matches click / drag / scroll --wheel. Checked here, after the awaited re-resolve,
                // to close the focus-steal race; also yields a clean no_interactive_desktop error on a
                // locked session instead of a misleading SendInput failure, and refuses to move the pointer
                // over whatever window grabbed the foreground.
                if (!foregroundGuard.TryEnsureForeground(targetHwnd, logger, json, "hover"))
                {
                    return 1;
                }

                // Move mouse to element center with a small wiggle to trigger hover detection
                mouseInput.Hover(centerX, centerY);

                // Wait for dwell time to allow hover effects to appear
                await Task.Delay(dwellTime, cancellationToken);

                var elementId = element.Selector ?? element.Id ?? "";

                if (json)
                {
                    var result = new UiHoverResult
                    {
                        ElementId = elementId,
                        X = centerX,
                        Y = centerY,
                        DwellTimeMs = dwellTime,
                        Hwnd = targetHwnd
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiHoverResult));
                }
                else
                {
                    logger.LogInformation("{Symbol} Hovered on {ElementId} at ({X}, {Y}) — dwelled {DwellTime}ms",
                        UiSymbols.Check, elementId, centerX, centerY, dwellTime);
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

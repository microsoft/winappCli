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
        IDesktopForegroundService desktopForeground,
        ISystemUiQuery systemQuery,
        IAnsiConsole ansiConsole,
        IInteractiveDesktopLock desktopLock,
        ILogger<UiHoverCommand> logger) : UiCoordinatedAction(desktopLock, logger)
    {
        protected override string Operation => "ui hover";

        /// <summary>Hovering moves the shared cursor and holds it there for the dwell.</summary>
        protected override UiTurnMode ResolveMode(ParseResult parseResult) => UiTurnMode.DesktopExclusive;

        protected override int? Preflight(ParseResult parseResult)
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

            return null;
        }

        protected override async Task<int> ExecuteAsync(ParseResult parseResult, IUiTurn turn, CancellationToken cancellationToken)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            // Preflight rejected a missing selector, so this is non-null by construction.
            var selectorStr = parseResult.GetValue(SharedUiOptions.SelectorArgument)!;
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var dwellTime = parseResult.GetValue(DwellTimeOption);

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

                if (element.Width == 0 || element.Height == 0)
                {
                    logger.LogError("{Symbol} Element has zero size — cannot hover.", UiSymbols.Error);
                    UiJsonError.Emit(json, UiJsonError.CodeZeroSize, "Element has zero size — cannot hover.", selectorStr);
                    return 1;
                }

                // Use the element's own window handle if available, otherwise fall back to session
                var targetHwnd = element.WindowHandle ?? uiTarget.WindowHandle;
                int centerX;
                int centerY;

                await using (await turn.EnterAsync(cancellationToken).ConfigureAwait(false))
                {
                    // Re-resolve just before hovering so the captured rect is current after any wait.
                    var stable = await GestureTargeting.ResolveStableAsync(
                        uiAutomation, uiTarget, selector, element,
                        GestureTargeting.DefaultMaxReads, GestureTargeting.DefaultReadDelayMs, null, cancellationToken);
                    if (!UiInjectionReporting.TryReport(stable, logger, json, selectorStr, "hover"))
                    {
                        return 1;
                    }
                    targetHwnd = stable.Element.WindowHandle ?? uiTarget.WindowHandle;
                    centerX = stable.CenterX;
                    centerY = stable.CenterY;

                    if (!DesktopTargetValidation.TryConfirmTargetWindow(
                            systemQuery, targetHwnd, uiTarget.ProcessId, logger, json, "hover", parseResult.InvocationConfiguration.Error))
                    {
                        return 1;
                    }

                    // Bring target window to foreground
                    if (targetHwnd != 0)
                    {
                        desktopForeground.RequestForeground(targetHwnd);
                        await Task.Delay(100, cancellationToken);
                    }

                    if (!foregroundGuard.TryEnsureForeground(targetHwnd, logger, json, "hover"))
                    {
                        return 1;
                    }

                    // Move mouse to element center with a small wiggle to trigger hover detection
                    mouseInput.Hover(centerX, centerY);

                    // Wait for dwell time to allow hover effects to appear
                    await Task.Delay(dwellTime, cancellationToken);
                }

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
            catch (Exception ex) when (!UiCoordinatedAction.IsCoordinationFault(ex))
            {
                UiErrors.GenericError(logger, ex, json);
                return 1;
            }
        }
    }
}

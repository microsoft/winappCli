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

internal class UiClickCommand : Command, IShortDescription
{
    public string ShortDescription => "Click an element at its screen coordinates using mouse simulation";

    public static Option<bool> DoubleClickOption { get; } = new("--double")
    {
        Description = "Perform a double-click instead of a single click"
    };

    public static Option<bool> RightClickOption { get; } = new("--right")
    {
        Description = "Perform a right-click instead of a left click"
    };

    public UiClickCommand()
        : base("click", "Click an element by slug or text search using mouse simulation. " +
               "Works on elements that don't support InvokePattern (e.g., column headers, list items). " +
               "Use --double for double-click, --right for right-click.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(DoubleClickOption);
        Options.Add(RightClickOption);
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
        ILogger<UiClickCommand> logger) : UiCoordinatedAction(desktopLock, logger)
    {
        /// <summary>Cursor-settle pause (ms) before the final confirm read and button-down.</summary>
        private const int CursorSettleMs = 50;

        protected override string Operation => "ui click";

        /// <summary>A click drives the shared cursor and OS-wide <c>SendInput</c> stream.</summary>
        protected override UiTurnMode ResolveMode(ParseResult parseResult) => UiTurnMode.DesktopExclusive;

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

            if (string.IsNullOrWhiteSpace(selectorStr))
            {
                UiErrors.MissingSelector(logger, "click", json);
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
            var doubleClick = parseResult.GetValue(DoubleClickOption);
            var rightClick = parseResult.GetValue(RightClickOption);

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

                var clickType = doubleClick ? "double-click" : rightClick ? "right-click" : "click";

                if (element.Width == 0 || element.Height == 0)
                {
                    logger.LogError("{Symbol} Element has zero size — cannot click.", UiSymbols.Error);
                    UiJsonError.Emit(json, UiJsonError.CodeZeroSize, "Element has zero size — cannot click.", selectorStr);
                    return 1;
                }

                // Use the element's own window handle if available, otherwise fall back to session.
                // Advisory only — refreshed from the re-resolved element inside the section below.
                var targetHwnd = element.WindowHandle ?? uiTarget.WindowHandle;
                int centerX;
                int centerY;

                // Everything that touches the shared desktop — foreground, cursor, SendInput — happens
                // inside one section, and so does the resolution whose result is acted upon.
                await using (await turn.EnterAsync(cancellationToken).ConfigureAwait(false))
                {
                    // Re-resolve before anything else so the HWND we foreground and validate is current.
                    var stable = await GestureTargeting.ResolveStableAsync(
                        uiAutomation, uiTarget, selector, element,
                        GestureTargeting.DefaultMaxReads, GestureTargeting.DefaultReadDelayMs, null, cancellationToken);
                    if (!UiInjectionReporting.TryReport(stable, logger, json, selectorStr, clickType))
                    {
                        return 1;
                    }
                    targetHwnd = stable.Element.WindowHandle ?? uiTarget.WindowHandle;

                    if (!DesktopTargetValidation.TryConfirmTargetWindow(
                            systemQuery, targetHwnd, uiTarget.ProcessId, logger, json, clickType, parseResult.InvocationConfiguration.Error))
                    {
                        return 1;
                    }

                    // Bring target window to foreground
                    if (targetHwnd != 0)
                    {
                        desktopForeground.RequestForeground(targetHwnd);
                        await Task.Delay(100, cancellationToken); // let window activate
                    }

                    // Verify the target STILL holds the foreground as the first gate before the OS-wide click.
                    if (!foregroundGuard.TryEnsureForeground(targetHwnd, logger, json, clickType))
                    {
                        return 1;
                    }

                    // Close the residual re-resolve→button-down race.
                    mouseInput.MoveCursor(stable.CenterX, stable.CenterY);
                    await Task.Delay(CursorSettleMs, cancellationToken);

                    var confirmed = await GestureTargeting.ConfirmStillAsync(
                        uiAutomation, uiTarget, selector, stable.Element, cancellationToken);
                    if (!UiInjectionReporting.TryReport(confirmed, logger, json, selectorStr, clickType))
                    {
                        return 1;
                    }
                    centerX = confirmed.CenterX;
                    centerY = confirmed.CenterY;

                    // Final foreground gate after the awaited confirm read.
                    if (!foregroundGuard.TryEnsureForeground(targetHwnd, logger, json, clickType))
                    {
                        return 1;
                    }

                    // Perform the click via SendInput — no extra settle, the cursor is already positioned.
                    mouseInput.Click(centerX, centerY, doubleClick, rightClick, settleMs: 0);
                }

                var elementId = (element.Selector ?? element.Id ?? "");

                if (json)
                {
                    var result = new UiClickResult
                    {
                        ElementId = elementId,
                        ClickType = clickType,
                        X = centerX,
                        Y = centerY,
                        Hwnd = targetHwnd
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiClickResult));
                }
                else
                {
                    logger.LogInformation("{Symbol} {ClickType} on {ElementId} at ({X}, {Y})",
                        UiSymbols.Check, clickType, elementId, centerX, centerY);
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

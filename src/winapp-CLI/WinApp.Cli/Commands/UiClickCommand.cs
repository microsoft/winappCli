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

internal class UiClickCommand : Command, IShortDescription, IHelpExamples
{
    public string ShortDescription => "Mouse click, when invoke is not supported";

    public IReadOnlyList<string> Examples { get; } =
    [
        "winapp ui click \"Name\" --type HeaderItem -a <app>",
        "winapp ui click <selector> -a <app> --right",
        "winapp ui click --at 1520,780 -a <app>",
    ];

    public string? Usage => "winapp ui click (<selector> | --at <x,y>) (-a <app> | -w <hwnd>) [options]";

    public static Option<string?> AtOption { get; } = new("--at")
    {
        Description = "Click at screen coordinates x,y (as reported by 'ui inspect') instead of an element. " +
                      "Cannot be combined with a selector, --root, --type, or --class-name."
    };

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
               "Use --at x,y to click a screen point instead of an element. " +
               "Use --double for double-click, --right for right-click.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(AtOption);
        Options.Add(DoubleClickOption);
        Options.Add(RightClickOption);
        Options.Add(WinAppRootCommand.JsonOption);
        UiQueryOptions.AddTo(this);
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
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }

            return PointerCommandSupport.ValidateSelectorOrPoint(parseResult, AtOption, "click", logger, json);
        }

        protected override async Task<int> ExecuteAsync(ParseResult parseResult, IUiTurn turn, CancellationToken cancellationToken)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            // Preflight required exactly one of a selector or a valid --at point.
            var selectorStr = parseResult.GetValue(SharedUiOptions.SelectorArgument)!;
            var atStr = parseResult.GetValue(AtOption);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var doubleClick = parseResult.GetValue(DoubleClickOption);
            var rightClick = parseResult.GetValue(RightClickOption);
            var clickType = doubleClick ? "double-click" : rightClick ? "right-click" : "click";

            try
            {
                var uiTarget = await targetResolver.ResolveAsync(app, window, cancellationToken);

                if (CoordinateParser.TryParsePoint(atStr, out PointerPoint at))
                {
                    PointerCommandSupport.InjectionPreparation prep;
                    await using (await turn.EnterAsync(cancellationToken).ConfigureAwait(false))
                    {
                        prep = await PointerCommandSupport.PrepareExplicitPointAsync(
                            uiAutomation, foregroundGuard, desktopForeground, systemQuery, uiTarget, at, clickType,
                            logger, json, parseResult.InvocationConfiguration.Error, cancellationToken);
                        if (!prep.Ok)
                        {
                            return 1;
                        }

                        // Same settle + final foreground gate as the selector path, so focus can't move
                        // between the check and the button-down.
                        mouseInput.MoveCursor(at.X, at.Y);
                        await Task.Delay(CursorSettleMs, cancellationToken);
                        if (!foregroundGuard.TryEnsureForeground(
                                uiTarget.WindowHandle, logger, json, clickType, parseResult.InvocationConfiguration.Error))
                        {
                            return 1;
                        }

                        mouseInput.Click(at.X, at.Y, doubleClick, rightClick, settleMs: 0);
                    }

                    WriteResult(json, elementId: null, clickType, at.X, at.Y, uiTarget.WindowHandle, prep.OutOfWindowWarning);
                    return 0;
                }

                var selector = UiQueryOptions.Parse(parseResult, selectorParser, selectorStr);
                var element = await UiQueryOptions.FindTargetAsync(parseResult, uiAutomation, uiTarget, selector, cancellationToken);

                if (element is null)
                {
                    UiErrors.ElementNotFound(logger, selectorStr, json, target: uiTarget);
                    return 1;
                }

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
                        GestureTargeting.DefaultMaxReads, GestureTargeting.DefaultReadDelayMs, null, cancellationToken,
                        requireUnique: UiQueryOptions.HasFilters(parseResult));
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
                        uiAutomation, uiTarget, selector, stable.Element, cancellationToken,
                        requireUnique: UiQueryOptions.HasFilters(parseResult));
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
                WriteResult(json, elementId, clickType, centerX, centerY, targetHwnd, warning: null);
                return 0;
            }
            catch (UiAmbiguousSelectorException ex)
            {
                UiErrors.AmbiguousSelector(logger, ex.Message, json, parseResult.InvocationConfiguration.Error);
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

        private void WriteResult(bool json, string? elementId, string clickType, int x, int y, long hwnd, string? warning)
        {
            if (json)
            {
                var result = new UiClickResult
                {
                    ElementId = elementId ?? "",
                    ClickType = clickType,
                    X = x,
                    Y = y,
                    Hwnd = hwnd,
                    Warnings = warning is null ? null : [warning]
                };
                ansiConsole.Profile.Out.Writer.WriteLine(
                    JsonSerializer.Serialize(result, UiJsonContext.Default.UiClickResult));
                return;
            }

            if (elementId is null)
            {
                logger.LogInformation("{Symbol} {ClickType} at ({X}, {Y})", UiSymbols.Check, clickType, x, y);
            }
            else
            {
                logger.LogInformation("{Symbol} {ClickType} on {ElementId} at ({X}, {Y})",
                    UiSymbols.Check, clickType, elementId, x, y);
            }

            if (warning is not null)
            {
                logger.LogWarning("{Symbol} {Warning}", UiSymbols.Warning, warning);
            }
        }
    }
}

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
        IAnsiConsole ansiConsole,
        ILogger<UiClickCommand> logger) : AsynchronousCommandLineAction
    {
        /// <summary>Cursor-settle pause (ms) before the final confirm read and button-down.</summary>
        private const int CursorSettleMs = 50;
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

            if (string.IsNullOrWhiteSpace(selectorStr))
            {
                UiErrors.MissingSelector(logger, "click", json);
                return 1;
            }

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

                // Get element center from bounding rect
                int centerX = (int)(element.X + element.Width / 2.0);
                int centerY = (int)(element.Y + element.Height / 2.0);

                if (element.Width == 0 || element.Height == 0)
                {
                    logger.LogError("{Symbol} Element has zero size — cannot click.", UiSymbols.Error);
                    UiJsonError.Emit(json, UiJsonError.CodeZeroSize, "Element has zero size — cannot click.", selectorStr);
                    return 1;
                }

                // Use the element's own window handle if available, otherwise fall back to session
                var targetHwnd = element.WindowHandle ?? uiTarget.WindowHandle;

                // Bring target window to foreground
                if (targetHwnd != 0)
                {
                    Windows.Win32.PInvoke.SetForegroundWindow(
                        new Windows.Win32.Foundation.HWND((nint)targetHwnd));
                    await Task.Delay(100, cancellationToken); // let window activate
                }

                // Re-resolve the element just before clicking (N5): foregrounding can restore/animate the
                // window, so the rect captured above may be stale. Refuse rather than click empty space if
                // the target is still moving.
                var stable = await GestureTargeting.ResolveStableAsync(
                    uiAutomation, uiTarget, selector, element,
                    GestureTargeting.DefaultMaxReads, GestureTargeting.DefaultReadDelayMs, null, cancellationToken);
                if (!UiInjectionReporting.TryReport(stable, logger, json, selectorStr, clickType))
                {
                    return 1;
                }
                centerX = stable.CenterX;
                centerY = stable.CenterY;

                // Verify the target STILL holds the foreground as the first gate before the OS-wide click
                // (F1) — matches drag / scroll --wheel. The re-resolve above awaits UIA reads during which
                // focus could shift, so we check here, after the awaits. Also yields a clean
                // no_interactive_desktop error on a locked session instead of a misleading SendInput failure.
                // (A second, final gate runs below, after the cursor-settle confirm read.)
                if (!foregroundGuard.TryEnsureForeground(targetHwnd, logger, json, clickType))
                {
                    return 1;
                }

                // Close the residual re-resolve→button-down race (F3/N5): position the cursor, let it
                // settle, then re-confirm the target hasn't drifted during that settle window before
                // pressing. ResolveStableAsync can read a continuously-animating target as "settled" by
                // chance and the element then moves during the ~50 ms cursor settle, landing the click on
                // empty space yet reporting success. By doing the settle here and a fresh confirm read
                // immediately before the button-down (which itself uses settleMs: 0), a reported ✅ means
                // the target was still in place when the button went down.
                mouseInput.MoveCursor(centerX, centerY);
                await Task.Delay(CursorSettleMs, cancellationToken);

                var confirmed = await GestureTargeting.ConfirmStillAsync(
                    uiAutomation, uiTarget, selector, stable.Element, cancellationToken);
                if (!UiInjectionReporting.TryReport(confirmed, logger, json, selectorStr, clickType))
                {
                    return 1;
                }
                centerX = confirmed.CenterX;
                centerY = confirmed.CenterY;

                // Final foreground gate after the awaited confirm read — the true last check before the
                // OS-wide button-down (M3). Focus could have shifted during the cursor-settle + confirm
                // read above, which the first gate (before those awaits) couldn't see.
                if (!foregroundGuard.TryEnsureForeground(targetHwnd, logger, json, clickType))
                {
                    return 1;
                }

                // Perform the click via SendInput — no extra settle, the cursor is already positioned and
                // the target just confirmed in place.
                mouseInput.Click(centerX, centerY, doubleClick, rightClick, settleMs: 0);

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
            catch (Exception ex)
            {
                UiErrors.GenericError(logger, ex, json);
                return 1;
            }
        }
    }
}

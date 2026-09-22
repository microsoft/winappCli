// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Commands;

internal class UiFocusCommand : Command, IShortDescription
{
    public string ShortDescription => "Activate the target window and verify keyboard focus";

    public static Argument<string> SelectorArgument { get; } = new("selector")
    {
        Description = SharedUiOptions.SelectorArgument.Description,
        Arity = ArgumentArity.ExactlyOne
    };

    public UiFocusCommand()
        : base("focus", "Activate the specified element's window, focus the element, and verify foreground and keyboard focus. " +
               "Fails if Windows refuses activation or focus cannot be confirmed.")
    {
        Arguments.Add(SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);

        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        IUiTargetResolver targetResolver,
        IUiAutomation uiAutomation,
        IUiSelectorParser selectorParser,
        ISystemUiQuery systemQuery,
        IDesktopForegroundService desktopForeground,
        IForegroundGuard foregroundGuard,
        IPollDelay pollDelay,
        IAnsiConsole ansiConsole,
        IInteractiveDesktopLock desktopLock,
        ILogger<UiFocusCommand> logger) : UiCoordinatedAction(desktopLock, logger)
    {
        private const int ActivationSettleMs = 100;
        private const int FocusVerificationTimeoutMs = 500;
        private const int FocusPollIntervalMs = 50;

        protected override string Operation => "ui focus";

        /// <summary>SetFocus changes the interactive desktop focus and must run exclusively.</summary>
        protected override UiTurnMode ResolveMode(ParseResult parseResult) => UiTurnMode.DesktopExclusive;

        protected override int? Preflight(ParseResult parseResult)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var selectorStr = parseResult.GetValue(SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }

            if (string.IsNullOrWhiteSpace(selectorStr))
            {
                UiErrors.MissingSelector(logger, "focus", json);
                return 1;
            }

            return null;
        }

        protected override async Task<int> ExecuteAsync(ParseResult parseResult, IUiTurn turn, CancellationToken cancellationToken)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var selectorStr = parseResult.GetValue(SelectorArgument)!;
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);

            try
            {
                var errorOut = parseResult.InvocationConfiguration.Error;
                var uiTarget = await targetResolver.ResolveAsync(app, window, cancellationToken);
                var selector = selectorParser.Parse(selectorStr);
                var element = await uiAutomation.FindSingleElementAsync(uiTarget, selector, cancellationToken);

                if (element is null)
                {
                    UiErrors.ElementNotFound(logger, selectorStr, json);
                    return 1;
                }

                long targetHwnd;
                await using (await turn.EnterAsync(cancellationToken).ConfigureAwait(false))
                {
                    element = await uiAutomation.FindSingleElementAsync(uiTarget, selector, cancellationToken);
                    if (element is null)
                    {
                        UiErrors.ElementNotFound(logger, selectorStr, json);
                        return 1;
                    }

                    var elementHwnd = element.WindowHandle ?? uiTarget.WindowHandle;
                    // Activate the control's top-level window, not a child HWND or an arbitrary
                    // owned popup. Capture's more permissive owner-chain foreground test is unsafe here.
                    targetHwnd = systemQuery.GetRootWindow(elementHwnd);
                    bool ConfirmTarget()
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (targetHwnd == 0 || systemQuery.GetRootWindow(elementHwnd) != targetHwnd
                            || (uiTarget.IsExplicitWindow && systemQuery.GetRootWindow(uiTarget.WindowHandle) != targetHwnd))
                        {
                            UiErrors.StaleElement(logger, json, errorOut);
                            return false;
                        }
                        return DesktopTargetValidation.TryConfirmTargetWindow(
                            systemQuery, elementHwnd, uiTarget.ProcessId, logger, json, "focus",
                            parseResult.InvocationConfiguration.Error)
                            && DesktopTargetValidation.TryConfirmTargetWindow(
                                systemQuery, targetHwnd, uiTarget.ProcessId, logger, json, "focus",
                                parseResult.InvocationConfiguration.Error);
                    }

                    if (!ConfirmTarget())
                    {
                        return 1;
                    }

                    if (!desktopForeground.IsForeground(targetHwnd))
                    {
                        if (desktopForeground.IsMinimized(targetHwnd))
                        {
                            desktopForeground.Restore(targetHwnd);
                        }
                        if (!ConfirmTarget())
                        {
                            return 1;
                        }
                        desktopForeground.RequestForeground(targetHwnd);
                        await pollDelay.DelayAsync(ActivationSettleMs, cancellationToken);
                    }

                    if (!ConfirmTarget() || !foregroundGuard.TryEnsureForeground(targetHwnd, logger, json, "focus", errorOut))
                    {
                        return 1;
                    }
                    await uiAutomation.FocusAsync(uiTarget, element, cancellationToken);
                    if (!ConfirmTarget() || !foregroundGuard.TryEnsureForeground(targetHwnd, logger, json, "focus", errorOut))
                    {
                        return 1;
                    }

                    var verification = Stopwatch.StartNew();
                    var focusConfirmed = false;
                    // Providers can publish focus asynchronously. Observe the retained control only;
                    // never refocus or reactivate after another window/user takes over.
                    for (var attempt = 0; attempt <= FocusVerificationTimeoutMs / FocusPollIntervalMs; attempt++)
                    {
                        if (!ConfirmTarget() || !foregroundGuard.TryEnsureForeground(targetHwnd, logger, json, "focus", errorOut))
                        {
                            return 1;
                        }
                        if (verification.ElapsedMilliseconds > FocusVerificationTimeoutMs)
                        {
                            break;
                        }

                        var properties = await uiAutomation.GetPropertiesAsync(
                            uiTarget, element, "HasKeyboardFocus", cancellationToken);
                        if (!ConfirmTarget() || !foregroundGuard.TryEnsureForeground(targetHwnd, logger, json, "focus", errorOut))
                        {
                            return 1;
                        }
                        var remainingMs = FocusVerificationTimeoutMs - verification.ElapsedMilliseconds;
                        if (remainingMs < 0)
                        {
                            break;
                        }
                        if (properties.TryGetValue("HasKeyboardFocus", out var hasFocus) && hasFocus is true)
                        {
                            focusConfirmed = true;
                            break;
                        }
                        if (remainingMs == 0 || attempt == FocusVerificationTimeoutMs / FocusPollIntervalMs)
                        {
                            break;
                        }
                        await pollDelay.DelayAsync((int)Math.Min(FocusPollIntervalMs, remainingMs), cancellationToken);
                    }
                    if (!focusConfirmed)
                    {
                        var message = $"The selected control did not confirm keyboard focus within {FocusVerificationTimeoutMs} ms. " +
                            "Inspect the target for a blocking dialog or a non-focusable control, then retry with its current selector.";
                        logger.LogError("{Symbol} {Message}", UiSymbols.Error, message);
                        UiJsonError.Emit(json, UiJsonError.CodeFocusNotAcquired, message, selectorStr, errorOut: errorOut);
                        return 1;
                    }
                }

                if (json)
                {
                    var result = new UiFocusResult { ElementId = (element.Selector ?? element.Id ?? ""), Hwnd = targetHwnd };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiFocusResult));
                }
                else
                {
                    logger.LogInformation("Focused {ElementId}", (element.Selector ?? element.Id ?? ""));
                }
                return 0;
            }
            catch (System.Runtime.InteropServices.COMException comEx)
            {
                logger.LogDebug("COM error: {HResult} {StackTrace}", comEx.HResult, comEx.StackTrace);
                UiErrors.StaleElement(logger, json, parseResult.InvocationConfiguration.Error);
                return 1;
            }
            catch (Exception ex) when (!UiCoordinatedAction.IsCoordinationFault(ex))
            {
                UiErrors.GenericError(logger, ex, json, parseResult.InvocationConfiguration.Error);
                return 1;
            }
        }
    }
}

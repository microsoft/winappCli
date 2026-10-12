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

internal class UiFocusCommand : Command, IShortDescription, IHelpExamples
{
    public string ShortDescription => "Move keyboard focus to an element or window";

    public IReadOnlyList<string> Examples { get; } =
    [
        "winapp ui focus \"Search\" -a <app>",
        "winapp ui focus \"Search\" --type Edit -a <app>",
        "winapp ui focus -a <app>",
    ];

    public static Argument<string?> SelectorArgument { get; } = new("selector")
    {
        Description = SharedUiOptions.SelectorArgument.Description + " Omit to restore and activate the target window itself.",
        Arity = ArgumentArity.ZeroOrOne
    };

    public UiFocusCommand()
        : base("focus", "Activate the specified element's window, focus the element, and verify foreground and keyboard focus. " +
               "A minimized window is restored first. Without a selector, restores and activates the target window. " +
               "Fails if Windows refuses activation or focus cannot be confirmed.")
    {
        Arguments.Add(SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);

        Options.Add(WinAppRootCommand.JsonOption);
        UiQueryOptions.AddTo(this);
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
        private const int RestoreContentTimeoutMs = 2000;
        private const int RestorePollIntervalMs = 100;

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

            if (selectorStr is not null && string.IsNullOrWhiteSpace(selectorStr))
            {
                UiErrors.MissingSelector(logger, "focus", json);
                return 1;
            }

            return UiQueryOptions.ValidateWithOptionalSelector(parseResult, selectorStr, logger, json);
        }

        protected override async Task<int> ExecuteAsync(ParseResult parseResult, IUiTurn turn, CancellationToken cancellationToken)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var selectorStr = parseResult.GetValue(SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);

            try
            {
                var errorOut = parseResult.InvocationConfiguration.Error;
                var uiTarget = await targetResolver.ResolveAsync(app, window, cancellationToken);
                if (selectorStr is null)
                {
                    return await FocusWindowAsync(uiTarget, turn, json, errorOut, cancellationToken);
                }

                var selector = UiQueryOptions.Parse(parseResult, selectorParser, selectorStr);
                var element = await UiQueryOptions.FindTargetAsync(parseResult, uiAutomation, uiTarget, selector, cancellationToken);

                // Some apps (packaged apps hosted in ApplicationFrameHost, such as Calculator) expose
                // no content while minimized, so a miss in a minimized window is retried after restoring it.
                var restoreRoot = element is null ? MinimizedRoot(uiTarget) : 0;
                if (element is null && restoreRoot == 0)
                {
                    UiErrors.ElementNotFound(logger, selectorStr, json, errorOut, target: uiTarget);
                    return 1;
                }

                long targetHwnd;
                await using (await turn.EnterAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (restoreRoot != 0)
                    {
                        if (!DesktopTargetValidation.TryConfirmTargetWindow(
                                systemQuery, restoreRoot, uiTarget.ProcessId, logger, json, "focus", errorOut))
                        {
                            return 1;
                        }
                        if (desktopForeground.IsMinimized(restoreRoot))
                        {
                            desktopForeground.Restore(restoreRoot);
                        }
                    }

                    element = await UiQueryOptions.FindTargetAsync(parseResult, uiAutomation, uiTarget, selector, cancellationToken);
                    // Restored content can take a moment to reattach to its frame.
                    for (var waited = 0; element is null && restoreRoot != 0 && waited < RestoreContentTimeoutMs; waited += RestorePollIntervalMs)
                    {
                        await pollDelay.DelayAsync(RestorePollIntervalMs, cancellationToken);
                        element = await UiQueryOptions.FindTargetAsync(parseResult, uiAutomation, uiTarget, selector, cancellationToken);
                    }
                    if (element is null)
                    {
                        UiErrors.ElementNotFound(logger, selectorStr, json, errorOut, target: uiTarget);
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

                    // A window element has keyboard focus once it is the foreground window; its root
                    // element never reports HasKeyboardFocus, because focus lands on a descendant.
                    var focusConfirmed = string.Equals(element.Type, "Window", StringComparison.OrdinalIgnoreCase);
                    var verification = Stopwatch.StartNew();
                    // Providers can publish focus asynchronously. Observe the retained control only;
                    // never refocus or reactivate after another window/user takes over.
                    for (var attempt = 0; !focusConfirmed && attempt <= FocusVerificationTimeoutMs / FocusPollIntervalMs; attempt++)
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
            catch (UiAmbiguousSelectorException ex)
            {
                UiErrors.AmbiguousSelector(logger, ex.Message, json, parseResult.InvocationConfiguration.Error);
                return 1;
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

        private long MinimizedRoot(UiTarget target)
        {
            if (target.WindowHandle == 0)
            {
                return 0;
            }
            var root = systemQuery.GetRootWindow(target.WindowHandle);
            return root != 0 && desktopForeground.IsMinimized(root) ? root : 0;
        }

        /// <summary>Restores (when minimized) and activates the target window, with no element involved.</summary>
        private async Task<int> FocusWindowAsync(UiTarget uiTarget, IUiTurn turn, bool json, TextWriter errorOut, CancellationToken cancellationToken)
        {
            if (uiTarget.WindowHandle == 0)
            {
                var message = $"The target has no window to focus. Run '{UiCommandAdvice.Command("list-windows -a <app>")}' to see its windows.";
                logger.LogError("{Symbol} {Message}", UiSymbols.Error, message);
                UiJsonError.Emit(json, UiJsonError.CodeNoTarget, message, errorOut: errorOut);
                return 1;
            }

            long targetHwnd;
            await using (await turn.EnterAsync(cancellationToken).ConfigureAwait(false))
            {
                targetHwnd = systemQuery.GetRootWindow(uiTarget.WindowHandle);
                if (targetHwnd == 0)
                {
                    targetHwnd = uiTarget.WindowHandle;
                }
                bool ConfirmTarget() => DesktopTargetValidation.TryConfirmTargetWindow(
                    systemQuery, targetHwnd, uiTarget.ProcessId, logger, json, "focus", errorOut);

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
            }

            if (json)
            {
                var result = new UiFocusResult { ElementId = "", Hwnd = targetHwnd };
                ansiConsole.Profile.Out.Writer.WriteLine(
                    JsonSerializer.Serialize(result, UiJsonContext.Default.UiFocusResult));
            }
            else
            {
                var title = string.IsNullOrEmpty(uiTarget.WindowTitle) ? "" : $"\"{uiTarget.WindowTitle}\" ";
                logger.LogInformation("Focused window {Title}(HWND {Hwnd})", title, targetHwnd);
            }
            return 0;
        }
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Commands;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Helpers;

internal static class PointerCommandSupport
{
    public readonly record struct ResolvedPoint(bool Ok, PointerPoint Point, long TargetHwnd, string? TargetLabel);

    public static async Task<ResolvedPoint> ResolvePointAsync(
        IUiAutomation uiAutomation,
        IUiSelectorParser selectorParser,
        ParseResult parseResult,
        UiTarget uiTarget,
        string? selectorStr,
        PointerPoint? explicitPoint,
        string? explicitLabel,
        string action,
        string pointKind,
        ILogger logger,
        bool json,
        CancellationToken cancellationToken)
    {
        if (explicitPoint is not null)
        {
            return new ResolvedPoint(true, explicitPoint.Value, uiTarget.WindowHandle, explicitLabel);
        }

        var selector = UiQueryOptions.Parse(parseResult, selectorParser, selectorStr);
        var label = UiQueryOptions.Describe(parseResult, selectorStr);
        var element = await UiQueryOptions.FindTargetAsync(parseResult, uiAutomation, uiTarget, selector, cancellationToken);
        if (element is null)
        {
            UiErrors.ElementNotFound(logger, label, json, target: uiTarget);
            return default;
        }

        if (element.Width == 0 || element.Height == 0)
        {
            logger.LogError("{Symbol} Element has zero size — cannot use its center as a {PointKind}.",
                UiSymbols.Error, pointKind);
            UiJsonError.Emit(json, UiJsonError.CodeZeroSize,
                $"Element has zero size — cannot use its center as a {pointKind}.", label);
            return default;
        }

        long targetHwnd = element.WindowHandle ?? uiTarget.WindowHandle;

        var stable = await GestureTargeting.ResolveStableAsync(
            uiAutomation, uiTarget, selector, element,
            GestureTargeting.DefaultMaxReads, GestureTargeting.DefaultReadDelayMs, null, cancellationToken,
            requireUnique: UiQueryOptions.HasFilters(parseResult));
        if (!UiInjectionReporting.TryReport(stable, logger, json, label, action))
        {
            return default;
        }

        return new ResolvedPoint(true, new PointerPoint(stable.CenterX, stable.CenterY), targetHwnd, label);
    }

    public static async Task SetForegroundAsync(
        IDesktopForegroundService desktopForeground, long targetHwnd, CancellationToken cancellationToken)
    {
        if (targetHwnd != 0)
        {
            desktopForeground.RequestForeground(targetHwnd);
            await Task.Delay(100, cancellationToken);
        }
    }

    /// <summary>
    /// Outcome of <see cref="TryPrepareInjection"/>. <paramref name="Ok"/> is <c>false</c> when a hard
    /// pre-injection gate failed (no target window, unreadable window rect, or foreground could not be
    /// secured) and the caller should return non-zero. <paramref name="OutOfWindowWarning"/> is a
    /// non-fatal advisory (issue #661): injection proceeds, but the caller should surface this string to
    /// the user (stdout for text output — <see cref="ILogger.LogWarning"/> routes non-error levels to
    /// stdout — and <c>warnings[]</c> for <c>--json</c>).
    /// </summary>
    public readonly record struct InjectionPreparation(bool Ok, string? OutOfWindowWarning);

    public static InjectionPreparation TryPrepareInjection(
        IUiAutomation uiAutomation,
        IForegroundGuard foregroundGuard,
        long targetHwnd,
        IEnumerable<PointerPoint> points,
        string action,
        string inputNoun,
        ILogger logger,
        bool json,
        TextWriter? errorOut = null)
    {
        if (targetHwnd == 0)
        {
            logger.LogError("{Symbol} No target window could be resolved — refusing to inject {InputNoun} (it could hit the wrong window).",
                UiSymbols.Error, inputNoun);
            UiJsonError.Emit(json, UiJsonError.CodeNoTarget,
                $"No target window could be resolved — refusing to inject {inputNoun}. Target an app window (via --app/--window) whose element resolves to a window handle.",
                errorOut: errorOut);
            return new InjectionPreparation(false, null);
        }

        if (!uiAutomation.TryGetWindowRect(targetHwnd, out var windowRect))
        {
            logger.LogError("{Symbol} Could not read the target window rectangle — refusing to inject {InputNoun}.",
                UiSymbols.Error, inputNoun);
            UiJsonError.Emit(json, UiJsonError.CodeNoTarget,
                $"Could not read the target window rectangle — refusing to inject {inputNoun}.",
                errorOut: errorOut);
            return new InjectionPreparation(false, null);
        }

        // #661: a point outside the target window is a non-fatal advisory, not a hard failure.
        // The mouse verbs (click/drag/hover/scroll) already inject at out-of-window coordinates, so
        // touch/pen warn and inject too. Emit nothing here — the caller decides text-vs-json (mirrors
        // the RemoteInjectionWarning discipline).
        string? outOfWindowWarning = null;
        var outOfBounds = PointerGesturePlanner.FirstOutOfBounds(windowRect, points);
        if (outOfBounds is not null)
        {
            outOfWindowWarning =
                $"Point ({outOfBounds.Value.X},{outOfBounds.Value.Y}) is outside the target window " +
                $"({windowRect.Left},{windowRect.Top})-({windowRect.Right},{windowRect.Bottom}) — injecting anyway.";
        }

        return foregroundGuard.TryEnsureForeground(targetHwnd, logger, json, action, errorOut)
            ? new InjectionPreparation(true, outOfWindowWarning)
            : new InjectionPreparation(false, null);
    }

    /// <summary>
    /// Preflight for verbs that act on either a selector or an explicit <c>--at x,y</c> point (click,
    /// hover). The target is an element (a selector, or <c>--type</c>/<c>--class-name</c> on their own) or a
    /// point, never both: <c>--at</c> excludes a selector and every element filter.
    /// Returns <see langword="null"/> when valid; otherwise emits the error and returns 1.
    /// </summary>
    public static int? ValidateSelectorOrPoint(
        ParseResult parseResult, Option<string?> atOption, string commandName, ILogger logger, bool json)
    {
        var selectorStr = parseResult.GetValue(SharedUiOptions.SelectorArgument);
        var errorOut = parseResult.InvocationConfiguration.Error;

        if (parseResult.GetResult(atOption) is null)
        {
            if (!UiQueryOptions.HasQuery(parseResult, selectorStr))
            {
                var msg = $"A selector, --type, --class-name, or --at x,y is required. Usage: {UiCommandAdvice.Command($"{commandName} <selector> -a <app>")}, " +
                          $"{UiCommandAdvice.Command($"{commandName} --type <type> -a <app>")}, " +
                          $"or {UiCommandAdvice.Command($"{commandName} --at <x,y> -a <app>")}. " +
                          $"Use '{UiCommandAdvice.Command("search <text> -a <app>")}' to find elements.";
                logger.LogError("{Symbol} {Message}", UiSymbols.Error, msg);
                UiJsonError.Emit(json, UiJsonError.CodeMissingSelector, msg, errorOut: errorOut);
                return 1;
            }

            return UiQueryOptions.Validate(parseResult, logger, json);
        }

        var atStr = parseResult.GetValue(atOption);
        string? error = !CoordinateParser.TryParsePoint(atStr, out PointerPoint _)
            ? $"--at must be a valid x,y pair (e.g. 100,200). Got '{atStr}'."
            : !string.IsNullOrWhiteSpace(selectorStr)
                ? $"Use either a selector or --at x,y, not both. Got selector '{selectorStr}' and --at {atStr}."
                : UiQueryOptions.HasFilters(parseResult)
                    ? "--type, --root, and --class-name pick an element and cannot be combined with --at."
                    : null;
        if (error is null)
        {
            return null;
        }

        logger.LogError("{Symbol} {Message}", UiSymbols.Error, error);
        UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, error, errorOut: errorOut);
        return 1;
    }

    /// <summary>
    /// Readies an explicit-point mouse action inside the desktop turn: confirms the target window still
    /// belongs to the resolved app, brings it to the foreground, then applies the same gates as touch and
    /// pen (<see cref="TryPrepareInjection"/>). The caller injects only when the result is <c>Ok</c>.
    /// </summary>
    public static async Task<InjectionPreparation> PrepareExplicitPointAsync(
        IUiAutomation uiAutomation,
        IForegroundGuard foregroundGuard,
        IDesktopForegroundService desktopForeground,
        ISystemUiQuery systemQuery,
        UiTarget uiTarget,
        PointerPoint point,
        string action,
        ILogger logger,
        bool json,
        TextWriter? errorOut,
        CancellationToken cancellationToken)
    {
        var targetHwnd = uiTarget.WindowHandle;
        if (!DesktopTargetValidation.TryConfirmTargetWindow(
                systemQuery, targetHwnd, uiTarget.ProcessId, logger, json, action, errorOut))
        {
            return new InjectionPreparation(false, null);
        }

        await SetForegroundAsync(desktopForeground, targetHwnd, cancellationToken);
        return TryPrepareInjection(uiAutomation, foregroundGuard, targetHwnd, [point], action, "mouse input", logger, json, errorOut);
    }

    public static bool TryInject(Action inject, ILogger logger, bool json, TextWriter? errorOut)
    {
        try
        {
            inject();
            return true;
        }
        catch (InvalidOperationException injectEx)
        {
            logger.LogError("{Symbol} {Message}", UiSymbols.Error, injectEx.Message);
            UiJsonError.Emit(json, UiJsonError.CodeInjectionUnsupported, injectEx.Message, errorOut: errorOut);
            return false;
        }
    }

    public static string? RemoteInjectionWarning(IForegroundGuard foregroundGuard, string inputKind)
        => ForegroundGuard.RemoteInjectionWarning(foregroundGuard.IsRemoteSession(), inputKind);
}

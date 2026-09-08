// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Helpers;

internal static class PointerCommandSupport
{
    public readonly record struct ResolvedPoint(bool Ok, PointerPoint Point, long TargetHwnd, string? TargetLabel);

    public static async Task<ResolvedPoint> ResolvePointAsync(
        IUiAutomation uiAutomation,
        IUiSelectorParser selectorParser,
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

        var selector = selectorParser.Parse(selectorStr!);
        var element = await uiAutomation.FindSingleElementAsync(uiTarget, selector, cancellationToken);
        if (element is null)
        {
            UiErrors.ElementNotFound(logger, selectorStr!, json);
            return default;
        }

        if (element.Width == 0 || element.Height == 0)
        {
            logger.LogError("{Symbol} Element has zero size — cannot use its center as a {PointKind}.",
                UiSymbols.Error, pointKind);
            UiJsonError.Emit(json, UiJsonError.CodeZeroSize,
                $"Element has zero size — cannot use its center as a {pointKind}.", selectorStr);
            return default;
        }

        long targetHwnd = element.WindowHandle ?? uiTarget.WindowHandle;

        if (targetHwnd != 0)
        {
            Windows.Win32.PInvoke.SetForegroundWindow(new Windows.Win32.Foundation.HWND((nint)targetHwnd));
            await Task.Delay(100, cancellationToken);
        }

        var stable = await GestureTargeting.ResolveStableAsync(
            uiAutomation, uiTarget, selector, element,
            GestureTargeting.DefaultMaxReads, GestureTargeting.DefaultReadDelayMs, null, cancellationToken);
        if (!UiInjectionReporting.TryReport(stable, logger, json, selectorStr!, action))
        {
            return default;
        }

        return new ResolvedPoint(true, new PointerPoint(stable.CenterX, stable.CenterY), targetHwnd, selectorStr);
    }

    public static async Task SetForegroundAsync(long targetHwnd, CancellationToken cancellationToken)
    {
        if (targetHwnd != 0)
        {
            Windows.Win32.PInvoke.SetForegroundWindow(new Windows.Win32.Foundation.HWND((nint)targetHwnd));
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
        bool json)
    {
        if (targetHwnd == 0)
        {
            logger.LogError("{Symbol} No target window could be resolved — refusing to inject {InputNoun} (it could hit the wrong window).",
                UiSymbols.Error, inputNoun);
            UiJsonError.Emit(json, UiJsonError.CodeNoTarget,
                $"No target window could be resolved — refusing to inject {inputNoun}. Target an app window (via --app/--window) whose element resolves to a window handle.");
            return new InjectionPreparation(false, null);
        }

        if (!uiAutomation.TryGetWindowRect(targetHwnd, out var windowRect))
        {
            logger.LogError("{Symbol} Could not read the target window rectangle — refusing to inject {InputNoun}.",
                UiSymbols.Error, inputNoun);
            UiJsonError.Emit(json, UiJsonError.CodeNoTarget,
                $"Could not read the target window rectangle — refusing to inject {inputNoun}.");
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

        return foregroundGuard.TryEnsureForeground(targetHwnd, logger, json, action)
            ? new InjectionPreparation(true, outOfWindowWarning)
            : new InjectionPreparation(false, null);
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

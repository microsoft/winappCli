// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace WinApp.Cli.Helpers;

/// <summary>
/// CLI presentation for the UI Automation library's pre-injection guards. The library decides
/// whether a gesture may proceed; these helpers turn that decision into the CLI's text and
/// <c>--json</c> error output.
/// </summary>
internal static class UiInjectionReporting
{
    /// <summary>
    /// Verifies the target window is in the foreground before an OS-wide input injection and emits the
    /// appropriate error when it isn't. Returns <see langword="true"/> to proceed,
    /// <see langword="false"/> to abort. A <paramref name="targetHwnd"/> of 0 means there is no window
    /// to verify against (e.g. a bare coordinate target) and is allowed through. Distinguishes a
    /// locked / secure desktop (<c>no_interactive_desktop</c>) from another window holding the
    /// foreground (<c>foreground_not_target</c>), so callers never report the misleading "target may be
    /// elevated" cause for a simply-locked session.
    /// </summary>
    /// <param name="action">Verb used in the message, e.g. "click", "drag", "scroll --wheel".</param>
    public static bool TryEnsureForeground(
        this IForegroundGuard guard, long targetHwnd, ILogger logger, bool json, string action)
    {
        switch (guard.CheckForeground(targetHwnd))
        {
            case ForegroundCheck.Proceed:
                return true;

            case ForegroundCheck.NoInteractiveDesktop:
                logger.LogError(
                    "{Symbol} No interactive desktop is available — the session is locked or on a secure desktop, so input can't be injected. Unlock the session and retry, or use a UIA-pattern verb (invoke, set-value, scroll --direction/--to) which doesn't need the desktop.",
                    UiSymbols.Error);
                UiJsonError.Emit(json, UiJsonError.CodeNoInteractiveDesktop,
                    "No interactive desktop is available (session locked or on a secure desktop) — cannot inject input. Unlock the session, or use a UIA-pattern verb.");
                return false;

            case ForegroundCheck.ForegroundNotTarget:
            default:
                logger.LogError(
                    "{Symbol} Target window is not in the foreground — refusing to {Action} to avoid acting on the wrong window. Focus or click the window first.",
                    UiSymbols.Error, action);
                UiJsonError.Emit(json, UiJsonError.CodeForegroundNotTarget,
                    $"Target window is not in the foreground — refusing to {action} to avoid injecting into the wrong window. Bring it to the foreground first.");
                return false;
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when the target settled (proceed with the gesture). Otherwise logs
    /// and emits the matching JSON error (<c>target_moved</c> for a vanished/animating element,
    /// <c>zero_size_element</c> for a collapsed one) and returns <see langword="false"/> so the caller
    /// aborts with a non-zero exit — converting the old "silent miss reported as success" into a clear,
    /// machine-readable signal.
    /// </summary>
    public static bool TryReport(StableTarget target, ILogger logger, bool json, string selector, string action)
    {
        switch (target.Status)
        {
            case TargetStatus.Ok:
                return true;

            case TargetStatus.ZeroSize:
                logger.LogError("{Symbol} Element collapsed to zero size before the {Action} — gesture not sent.",
                    UiSymbols.Error, action);
                UiJsonError.Emit(json, UiJsonError.CodeZeroSize,
                    $"Element collapsed to zero size before the {action} — gesture not sent.", selector);
                return false;

            case TargetStatus.NotFound:
                logger.LogError(
                    "{Symbol} Element could not be re-resolved just before the {Action} — it moved or was removed. Gesture not sent (it would have hit empty space). Retry, or use a UIA-pattern verb (invoke/set-value/scroll --direction) for changing UI.",
                    UiSymbols.Error, action);
                UiJsonError.Emit(json, UiJsonError.CodeTargetMoved,
                    $"Element could not be re-resolved just before the {action} — it moved or was removed; gesture not sent.", selector);
                return false;

            case TargetStatus.Moving:
            default:
                logger.LogError(
                    "{Symbol} Element is still moving/resizing — {Action} not sent to avoid hitting empty space (the target never settled). Retry once the UI is static, or use a UIA-pattern verb (invoke/set-value/scroll --direction) which doesn't depend on screen coordinates.",
                    UiSymbols.Error, action);
                UiJsonError.Emit(json, UiJsonError.CodeTargetMoved,
                    $"Element is still moving/resizing — {action} not sent because the target never settled. Retry once static, or use a UIA-pattern verb.", selector);
                return false;
        }
    }
}

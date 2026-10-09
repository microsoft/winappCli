// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Consistent error messages across all winapp ui commands.
/// In --json mode the logger is silenced; these helpers also emit a structured
/// JSON error envelope to stderr via <see cref="UiJsonError"/> so JSON consumers
/// always get something parseable on failure.
/// </summary>
internal static class UiErrors
{
    public static void MissingApp(ILogger logger, bool json = false)
    {
        var msg = $"Target app required. Use --app <name|title|PID> or --window <HWND>. Run '{UiCommandAdvice.Command("list-windows")}' to find running apps.";
        logger.LogError("{Symbol} {Message}", UiSymbols.Error, msg);
        UiJsonError.Emit(json, UiJsonError.CodeMissingApp, msg);
    }

    public static void MissingSelector(ILogger logger, string commandName, bool json = false)
    {
        var msg = $"A selector is required. Usage: {UiCommandAdvice.Command($"{commandName} <selector> -a <app>")}. Use '{UiCommandAdvice.Command("search <text> -a <app>")}' to find elements.";
        logger.LogError("{Symbol} {Message}", UiSymbols.Error, msg);
        UiJsonError.Emit(json, UiJsonError.CodeMissingSelector, msg);
    }

    public static void ElementNotFound(ILogger logger, string selector, bool json = false, TextWriter? errorOut = null, UiTarget? target = null)
    {
        var minimized = MinimizedWindowHint(target);
        var msg = minimized is not null
            ? $"No element found matching '{selector}'. {minimized}"
            : $"No element found matching '{selector}'. The UI may have changed — re-run '{UiCommandAdvice.Command("inspect")}' or '{UiCommandAdvice.Command("search")}' to find current elements. Prefer targeting by AutomationId (set via AutomationProperties.AutomationId in XAML) — these survive layout changes.";
        logger.LogError("{Symbol} {Message}", UiSymbols.Error, msg);
        UiJsonError.Emit(json, UiJsonError.CodeElementNotFound, $"No element found matching '{selector}'", selector,
            errorOut: errorOut, recoveryHint: minimized);
    }

    /// <remarks>
    /// Process-global seam: the default reads the live window state. Tests replace it to model a
    /// minimized target without a real window, so they must not run in parallel.
    /// </remarks>
    internal static Func<long, bool> s_isWindowMinimized = hwnd =>
        hwnd != 0 && Windows.Win32.PInvoke.IsIconic(new Windows.Win32.Foundation.HWND((nint)hwnd));

    /// <summary>
    /// Advice for a lookup that came back empty because the target window is minimized, or
    /// <see langword="null"/> when it is not. Packaged apps such as Calculator expose no content
    /// while minimized, so the fix is to restore the window rather than change the selector.
    /// </summary>
    public static string? MinimizedWindowHint(UiTarget? target)
    {
        if (target is null || target.WindowHandle == 0 || !s_isWindowMinimized(target.WindowHandle))
        {
            return null;
        }

        var title = string.IsNullOrEmpty(target.WindowTitle) ? "" : $" \"{target.WindowTitle}\"";
        return $"The target window{title} (HWND {target.WindowHandle}) is minimized, and some apps hide their UI until it is restored. " +
            $"Run '{UiCommandAdvice.Command($"focus -w {target.WindowHandle}")}' to restore it, then retry.";
    }

    public static void StaleElement(ILogger logger, bool json = false, TextWriter? errorOut = null)
    {
        var msg = $"Element is no longer accessible — the app may have navigated or the element was removed. Re-run '{UiCommandAdvice.Command("inspect")}' to refresh the element tree. Prefer targeting by AutomationId — these are stable across layout changes.";
        logger.LogError("{Symbol} {Message}", UiSymbols.Error, msg);
        UiJsonError.Emit(json, UiJsonError.CodeStaleElement, "Element is no longer accessible", errorOut: errorOut);
    }

    public static void AmbiguousSelector(ILogger logger, string message, bool json = false, TextWriter? errorOut = null)
    {
        logger.LogError("{Symbol} {Message}", UiSymbols.Error, message);
        UiJsonError.Emit(json, UiJsonError.CodeAmbiguousSelector, message, errorOut: errorOut);
    }

    public static void GenericError(ILogger logger, Exception ex, bool json = false, TextWriter? errorOut = null)
    {
        var message = ex is UiValueSetException valueSet
            ? valueSet.FormatMessage(UiCommandAdvice.Format)
            : ex.Message;
        logger.LogDebug("Stack trace: {StackTrace}", ex.StackTrace);
        logger.LogError("{Symbol} {Message}", UiSymbols.Error, message);
        // Keep the existing JSON details value for this failure, including local invocations.
        var details = ex is UiValueSetException ? nameof(InvalidOperationException) : ex.GetType().Name;
        UiJsonError.Emit(json, UiJsonError.CodeInternalError, message, details: details, errorOut: errorOut);
    }
}

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

    public static void ElementNotFound(ILogger logger, string selector, bool json = false)
    {
        var msg = $"No element found matching '{selector}'. The UI may have changed — re-run '{UiCommandAdvice.Command("inspect")}' or '{UiCommandAdvice.Command("search")}' to find current elements. Prefer targeting by AutomationId (set via AutomationProperties.AutomationId in XAML) — these survive layout changes.";
        logger.LogError("{Symbol} {Message}", UiSymbols.Error, msg);
        UiJsonError.Emit(json, UiJsonError.CodeElementNotFound, $"No element found matching '{selector}'", selector);
    }

    public static void StaleElement(ILogger logger, bool json = false, TextWriter? errorOut = null)
    {
        var msg = $"Element is no longer accessible — the app may have navigated or the element was removed. Re-run '{UiCommandAdvice.Command("inspect")}' to refresh the element tree. Prefer targeting by AutomationId — these are stable across layout changes.";
        logger.LogError("{Symbol} {Message}", UiSymbols.Error, msg);
        UiJsonError.Emit(json, UiJsonError.CodeStaleElement, "Element is no longer accessible", errorOut: errorOut);
    }

    public static void AmbiguousSelector(ILogger logger, string message, bool json = false)
    {
        logger.LogError("{Symbol} {Message}", UiSymbols.Error, message);
        UiJsonError.Emit(json, UiJsonError.CodeAmbiguousSelector, message);
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

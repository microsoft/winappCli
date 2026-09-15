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
        const string msg = "Target app required. Use --app <name|title|PID> or --window <HWND>. Run 'winapp ui list-windows' to find running apps.";
        logger.LogError("{Symbol} {Message}", UiSymbols.Error, msg);
        UiJsonError.Emit(json, UiJsonError.CodeMissingApp, msg);
    }

    public static void MissingSelector(ILogger logger, string commandName, bool json = false)
    {
        var msg = $"A selector is required. Usage: winapp ui {commandName} <selector> -a <app>. Use 'winapp ui search <text> -a <app>' to find elements.";
        logger.LogError("{Symbol} {Message}", UiSymbols.Error, msg);
        UiJsonError.Emit(json, UiJsonError.CodeMissingSelector, msg);
    }

    public static void ElementNotFound(ILogger logger, string selector, bool json = false)
    {
        var msg = $"No element found matching '{selector}'. The UI may have changed — re-run 'winapp ui inspect' or 'winapp ui search' to find current elements. Prefer targeting by AutomationId (set via AutomationProperties.AutomationId in XAML) — these survive layout changes.";
        logger.LogError("{Symbol} {Message}", UiSymbols.Error, msg);
        UiJsonError.Emit(json, UiJsonError.CodeElementNotFound, $"No element found matching '{selector}'", selector);
    }

    public static void StaleElement(ILogger logger, bool json = false, TextWriter? errorOut = null)
    {
        const string msg = "Element is no longer accessible — the app may have navigated or the element was removed. Re-run 'winapp ui inspect' to refresh the element tree. Prefer targeting by AutomationId — these are stable across layout changes.";
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
        logger.LogDebug("Stack trace: {StackTrace}", ex.StackTrace);
        logger.LogError("{Symbol} {Message}", UiSymbols.Error, ex.Message);
        UiJsonError.Emit(json, UiJsonError.CodeInternalError, ex.Message, details: ex.GetType().Name, errorOut: errorOut);
    }
}

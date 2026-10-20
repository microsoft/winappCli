// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Globalization;

namespace WinApp.Cli.Commands;

internal sealed class UiAuditCommand : UiAccessibilityOperationCommand
{
    private readonly Option<bool> _currentPage;

    public UiAuditCommand()
        : base("audit", "Audit accessibility in an already-running Windows app. By default, discover and select " +
            "supported standard Tab pages, capture each page and write a combined report; no agent or route profile is needed. " +
            "Tab selection can cause application side effects. If no standard tabs are found, audit the current page. " +
            "Use --current-page for a read-only single-page audit. Never builds, restarts or closes the app. " +
            "Attempts to restore tab selection only while the original target remains eligible. " +
            "Exit codes: 0 complete without rule failures, 1 complete with failures, 2 blocked or partial; not a WCAG compliance verdict.")
    {
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Output().Aliases.Add("-o");
        Number("--depth", "Maximum descendant depth (1-50).", 1, 50, 20).Aliases.Add("-d");
        Number("--max-elements", "Maximum elements examined (1-10000).", 1, 10000, 2000);
        Number("--timeout-ms", "Per-capture timeout in milliseconds (1000-120000).", 1000, 120000, 15000);
        Flag("--no-screenshot", "Do not capture screenshots.");
        _currentPage = Flag("--current-page",
            "Audit only the current page without selecting tabs. Read-only unless a keyboard probe is separately requested and approved.",
            forward: false);
        var maxPages = Number("--max-pages", "Maximum page captures for automatic standard-tab navigation (1-100). Not used with --current-page.", 1, 100, 20);
        var observe = Number("--observe-seconds",
            "Passive observation budget per page (0-60 seconds). Defaults to 0, or 10 with a keyboard probe.", 0, 60);
        var steps = Number("--tab-steps", "Keyboard focus probe steps (0-30); requires --current-page and --accept-input when positive.", 0, 30, 0);
        var accept = Flag("--accept-input", "Explicitly approve the requested Tab and Shift+Tab keyboard probe; even Tab may cause app side effects.");
        Json();

        ValidateOptions(result =>
        {
            var app = result.GetValue(SharedUiOptions.AppOption);
            var window = result.GetValue(SharedUiOptions.WindowOption);
            if (window is null && (string.IsNullOrWhiteSpace(app) || app.StartsWith("--", StringComparison.Ordinal)))
            {
                result.AddError("Select the target with --app <name|title|PID> or --window <HWND>.");
            }
            if (window is <= 0)
            {
                result.AddError("--window must be a positive HWND from 'winapp ui list-windows'.");
            }
            if (result.GetValue(_currentPage) && result.GetResult(maxPages) is { Implicit: false })
            {
                result.AddError("--max-pages cannot be combined with --current-page.");
            }
            var count = result.GetValue(steps) ?? 0;
            if (count > 0 && !result.GetValue(_currentPage))
            {
                result.AddError("--tab-steps requires --current-page. Keyboard probes are separate from automatic tab-page navigation.");
            }
            if (count > 0 && !result.GetValue(accept))
            {
                result.AddError("--tab-steps requires --accept-input. Even Tab can cause app side effects.");
            }
            if (count == 0 && result.GetValue(accept))
            {
                result.AddError("--accept-input requires a positive --tab-steps value.");
            }
            if (count > 0 && result.GetValue(observe) == 0)
            {
                result.AddError("Keyboard probes require a positive --observe-seconds budget.");
            }
        });
    }

    internal override IReadOnlyList<string> CreateWorkerArguments(ParseResult result)
    {
        var arguments = base.CreateWorkerArguments(result).ToList();
        arguments[0] = "scan";
        if (result.GetValue(SharedUiOptions.WindowOption) is { } window)
        {
            arguments.AddRange(["--window", window.ToString(CultureInfo.InvariantCulture)]);
        }
        else
        {
            arguments.AddRange(["--app", result.GetValue(SharedUiOptions.AppOption)!]);
        }
        if (!result.GetValue(_currentPage))
        {
            arguments.AddRange(["--navigation", "standard-tabs", "--accept-navigation"]);
        }
        return arguments;
    }
}

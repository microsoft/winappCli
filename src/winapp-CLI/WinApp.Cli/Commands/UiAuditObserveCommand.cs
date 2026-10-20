// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Commands;

internal sealed class UiAuditObserveCommand : Command, IShortDescription
{
    public static Option<string?> AppOption { get; } = new("--app", "-a")
    {
        Description = "Target running process by numeric PID. Use --window instead to choose a particular top-level window.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public static Option<long?> WindowOption { get; } = new("--window", "-w")
    {
        Description = "Target one top-level window by positive numeric HWND. Use --app or --window, not both.",
        Arity = ArgumentArity.ZeroOrOne,
        CustomParser = result => result.Tokens.Count == 1
            && long.TryParse(result.Tokens[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value : long.MinValue,
    };

    public static Option<int> DurationSecOption { get; } = NumericOption("--duration-sec",
        "Total observation and keyboard-probe budget in seconds (1-60), including setup.", 5);

    public static Option<int> TabStepsOption { get; } = NumericOption("--tab-steps",
        "Maximum Tab presses per direction (0-30). Zero observes without activation or input.", 0);

    public static Option<bool> AcceptInputOption { get; } = new("--accept-input")
    {
        Description = "Authorize initial target activation and real Tab/Shift+Tab input. Even Tab can change business state; no state is restored.",
    };

    public static Option<int> MaxEventsOption { get; } = NumericOption("--max-events",
        "Maximum retained native events (1-5000). Reaching this limit returns a partial result.", 500);

    public string ShortDescription => "Observe native events and an optional bounded Tab focus path";

    public UiAuditObserveCommand()
        : base("audit-observe",
            "Observe native focus, selected non-text property, structure, live-region, and notification events in one running window. " +
            "Default: read-only for 5 seconds, with no activation or input. --tab-steps requires --accept-input and sends up to N Tab " +
            "then N Shift+Tab chords, with no control invocation, value changes, or focus-setting between steps. " +
            "Tab itself can change business state. Keyboard probing requires an unlocked interactive desktop; read-only event observation does not require input focus. " +
            "Review the target state after keyboard probing; nothing is restored. " +
            "Focus samples are separate from native events. Repeats are observations, not keyboard-trap or accessibility verdicts. " +
            "Input values and notification text are not captured; password, text-bearing, and privacy-unknown names are withheld. " +
            "Cancellation, event limits, environment failures, or incomplete coverage return partial JSON with a nonzero exit code.")
    {
        Options.Add(AppOption);
        Options.Add(WindowOption);
        Options.Add(DurationSecOption);
        Options.Add(TabStepsOption);
        Options.Add(AcceptInputOption);
        Options.Add(MaxEventsOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    private static Option<int> NumericOption(string name, string description, int defaultValue) => new(name)
    {
        Description = description,
        Arity = ArgumentArity.ZeroOrOne,
        DefaultValueFactory = _ => defaultValue,
        // Let the handler emit the same content-free UiJsonError for missing, malformed, and out-of-range numbers.
        CustomParser = result => result.Tokens.Count == 1
            && int.TryParse(result.Tokens[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value : int.MinValue,
    };

    public sealed class Handler(
        IUiTargetResolver targetResolver,
        IUiAuditObservationService observation,
        IAnsiConsole ansiConsole,
        ILogger<UiAuditObserveCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var quiet = parseResult.GetValue(WinAppRootCommand.QuietOption);
            var steps = parseResult.GetValue(TabStepsOption);
            var accepted = parseResult.GetValue(AcceptInputOption);
            var duration = parseResult.GetValue(DurationSecOption);
            var maxEvents = parseResult.GetValue(MaxEventsOption);
            var app = parseResult.GetValue(AppOption);
            var window = parseResult.GetValue(WindowOption);

            int Fail(string code, string message)
            {
                UiJsonError.Emit(json, code, message, errorOut: parseResult.InvocationConfiguration.Error);
                if (!json)
                {
                    logger.LogError("{Symbol} {Message}", UiSymbols.Error, message);
                }
                return 1;
            }

            if (steps > 0 && !accepted)
            {
                return Fail("input_opt_in_required",
                    "--tab-steps requires --accept-input before resolving or activating an app. Tab can change application state.");
            }
            if (duration is < 1 or > 60 || steps is < 0 or > 30 || maxEvents is < 1 or > 5000 ||
                parseResult.GetResult(DurationSecOption) is { Implicit: false, Tokens.Count: 0 } ||
                parseResult.GetResult(TabStepsOption) is { Implicit: false, Tokens.Count: 0 } ||
                parseResult.GetResult(MaxEventsOption) is { Implicit: false, Tokens.Count: 0 })
            {
                return Fail(UiJsonError.CodeInvalidArguments,
                    "--duration-sec must be 1-60, --tab-steps 0-30 per direction, and --max-events 1-5000.");
            }
            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                return Fail(UiJsonError.CodeMissingApp,
                    "Select a running window with --app <PID> or --window <HWND>. Use 'winapp ui list-windows' to find it.");
            }
            if ((parseResult.GetResult(AppOption) is { Implicit: false } && window is not null) || window is <= 0 ||
                (app is not null && (!int.TryParse(app, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)))
            {
                return Fail(UiJsonError.CodeInvalidArguments,
                    "Use exactly one positive numeric --app <PID> or --window <HWND>.");
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                UiTarget target;
                try
                {
                    target = await targetResolver.ResolveAsync(app, window, cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    return Fail(UiJsonError.CodeNoTarget,
                        "The selected window could not be resolved. Choose an HWND from 'winapp ui list-windows'.");
                }

                if (steps > 0 && !json && !quiet)
                {
                    parseResult.InvocationConfiguration.Error.WriteLine(
                        "Consent: activating the selected window and sending Tab/Shift+Tab can change business state. No state will be restored.");
                }
                var result = await observation.ObserveAsync(target, new UiAuditObservationOptions
                {
                    DurationSec = duration,
                    TabSteps = steps,
                    AcceptInput = accepted,
                    MaxEvents = maxEvents,
                }, cancellationToken);

                if (json)
                {
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiAuditObservationResult));
                }
                else
                {
                    foreach (var issue in result.Issues)
                    {
                        parseResult.InvocationConfiguration.Error.WriteLine($"{issue.Code}: {issue.Message}");
                    }
                    if (!quiet)
                    {
                        ansiConsole.Profile.Out.Writer.WriteLine(
                            $"Observation {result.Status}: {result.Events.Length} native events, " +
                            $"{result.FocusTrace.Length} focus samples, {result.CompletedTabSteps}/{2 * result.RequestedTabSteps} Tab chords (stop: {result.StopReason}).");
                        ansiConsole.Profile.Out.Writer.WriteLine(
                            "These observations do not establish an accessibility verdict, a keyboard trap, or complete keyboard reachability.");
                    }
                }
                return result.Status == "completed" ? 0 : 1;
            }
            catch (AppNotFoundException)
            {
                return Fail(UiJsonError.CodeNoTarget,
                    "The selected app or window was not found. Choose a running window from 'winapp ui list-windows'.");
            }
            catch (UiAuditObservationException exception)
            {
                return Fail(exception.Code, exception.Message);
            }
            catch (OperationCanceledException)
            {
                return Fail("cancelled", "Observation was cancelled before collection started.");
            }
            catch (Exception exception)
            {
                logger.LogDebug("UI audit observation failed ({ExceptionType}).", exception.GetType().Name);
                return Fail(UiJsonError.CodeInternalError, "Observation could not start. Check the selected window and interactive desktop.");
            }
        }
    }
}

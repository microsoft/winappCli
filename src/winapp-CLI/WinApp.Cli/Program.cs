// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Telemetry;
using WinApp.Cli.Telemetry.Events;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace WinApp.Cli;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        // Hidden internal verb: the WinUI DbgEng triage pass runs in this isolated child process so
        // its modern dbgeng.dll is not poisoned by the system32 dbghelp.dll the parent already loaded.
        // Intercept before any host/service setup to keep the loader state clean and output noise-free.
        if (args.Length > 0 && args[0] == Services.XamlTriageRunner.InternalVerb)
        {
            return Services.XamlTriageRunner.Run(args);
        }

        // Ensure UTF-8 I/O for emoji-capable terminals; fall back silently if not supported
        ConfigureConsoleEncoding(static () =>
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.InputEncoding = Encoding.UTF8;
        });

        var loggingMode = ResolveLoggingMode(args);
        if (loggingMode.ConflictError is not null)
        {
            Console.Error.WriteLine(loggingMode.ConflictError);
            return 1;
        }

        var minimumLogLevel = loggingMode.MinimumLevel;
        bool quiet = loggingMode.Quiet;
        bool json = loggingMode.Json;

        // Check if --cli-schema is specified - this outputs machine-readable JSON
        // and should not display any interactive messages like first-run notices
        bool isCliSchemaMode = GlobalOptionPreScan.IsFlagPresent(
            args, WinAppRootCommand.CliSchemaOption.Name, []);

        // Check if this is a completion request - completions must be fast and silent
        bool isCompleteMode = args.Length > 0 && args[0] == "complete";

        var services = new ServiceCollection()
            .ConfigureServices()
            .ConfigureCommands()
            .AddLogging(b =>
            {
                b.ClearProviders();
                b.AddTextWriterLogger(Console.Out, Console.Error);
                b.SetMinimumLevel(minimumLogLevel);
            });

        using var serviceProvider = services.BuildServiceProvider();

        var rootCommand = serviceProvider.GetRequiredService<WinAppRootCommand>();
        System.CommandLine.ParseResult? parseResult = null;

        if (args.Length > 0)
        {
            parseResult = rootCommand.Parse(args, WinAppParserConfiguration.Default);

            // Set WINAPP_CLI_CALLER env var from --caller option so telemetry and update checks can use it
            var caller = parseResult.GetValue(WinAppRootCommand.CallerOption);
            if (!string.IsNullOrWhiteSpace(caller))
            {
                Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", caller);
            }

            // Reject an invalid '='-attached value on ANY boolean option reachable by the selected
            // command — a global flag like --json=bogus or a command flag like --eraser=bogus.
            // System.CommandLine silently coerces such a value to TRUE with no parse error, which
            // would (a) silently enable a flag the user never set (e.g. the pen eraser) and (b) for
            // --json leave logging un-suppressed, so the human ❌ line and the JSON envelope BOTH hit
            // stderr and corrupt machine-readable output. Fail fast HERE — before the first-run /
            // update notice can print anything (M1) — with a single clean invalid_arguments error,
            // mirroring how the parser already rejects other malformed values (e.g. --pressure nope).
            if (TryFindInvalidBooleanOption(parseResult, args, out var invalidBoolOption, out var invalidBoolValue))
            {
                var message =
                    $"Cannot parse argument '{invalidBoolValue}' for option '{invalidBoolOption}' as expected type 'System.Boolean'.";
                if (IsNewCommandJson(parseResult))
                {
                    // `new` owns a bespoke NewCommandResult envelope; emit it (and use its
                    // invalid-argument exit code) so machine callers get structured output here too.
                    NewCommand.EmitParseErrorJson(message);
                    return NewCommand.ExitInvalidArgs;
                }
                if (ResolveEffectiveJson(parseResult) && IsUiDescendant(parseResult))
                {
                    UiJsonError.Emit(true, UiJsonError.CodeInvalidArguments, message);
                }
                else if (ResolveEffectiveJson(parseResult) && IsFindUi(parseResult))
                {
                    EmitFindUiJsonError(message);
                }
                else
                {
                    Console.Error.WriteLine(message);
                }

                return 1;
            }
        }

        // Skip first-run notice for machine-readable output modes and completions
        var didShowFirstRunNotice = false;
        if (!isCliSchemaMode && !isCompleteMode && !json)
        {
            var firstRunService = serviceProvider.GetRequiredService<IFirstRunService>();
            didShowFirstRunNotice = firstRunService.CheckAndDisplayFirstRunNotice();

            // Check for CLI updates — shows cached notice instantly (no network),
            // and starts a background refresh if the cache is stale (fire-and-forget).
            if (!quiet)
            {
                var updateNotificationService = serviceProvider.GetRequiredService<IUpdateNotificationService>();
                updateNotificationService.CheckAndNotify();
            }
        }

        // If no arguments provided, display banner and show help
        if (args.Length == 0)
        {
            if (!didShowFirstRunNotice)
            {
                BannerHelper.DisplayBanner();
            }

            // Show help by invoking with --help
            await rootCommand.Parse(["--help"], WinAppParserConfiguration.Default).InvokeAsync();
            return 0;
        }

        var parsedArgs = parseResult!;

        // Derive the effective JSON mode from the SELECTED command's PARSED --json value (M1).
        // The pre-scan (json) is value-aware but still a heuristic; the parsed value is the truth.
        // Reading the parsed bool works even when a different option (e.g. --pressure) failed parse.
        bool effectiveJson = ResolveEffectiveJson(parsedArgs);

        // Catch single-dash typos like "-app" before invocation so the user gets a clear
        // "Did you mean --app?" message instead of System.CommandLine's confusing
        // "Unrecognized command or argument" pointing at the wrong token (issue #467).
        // Only run when parsing already failed — otherwise a command that legitimately
        // accepts a "-foo"-shaped positional value would get a false-positive typo error.
        if (parsedArgs.Errors.Count > 0)
        {
            var typo = OptionTypoValidator.FindLikelyLongOptionTypo(args, parsedArgs);
            if (typo is not null)
            {
                var suggested = "-" + typo;
                var typoMessage = $"Unknown option '{typo}'. Did you mean '{suggested}'?";
                if (IsNewCommandJson(parsedArgs))
                {
                    // Route `new --json` typo failures through its structured envelope with paired
                    // telemetry, mirroring the ui branch below and the main parse-error bridge.
                    if (!isCompleteMode)
                    {
                        CommandInvokedEvent.Log(parsedArgs.CommandResult);
                    }
                    NewCommand.EmitParseErrorJson(typoMessage);
                    if (!isCompleteMode)
                    {
                        CommandCompletedEvent.Log(parsedArgs.CommandResult, NewCommand.ExitInvalidArgs);
                    }
                    return NewCommand.ExitInvalidArgs;
                }
                if (effectiveJson && IsUiDescendant(parsedArgs))
                {
                    // Gate on IsUiDescendant so that non-ui commands (e.g. cert info) fall through
                    // to default error handling instead of receiving the nested UI schema (M2).
                    if (!isCompleteMode)
                    {
                        CommandInvokedEvent.Log(parsedArgs.CommandResult);
                    }
                    UiJsonError.Emit(true, UiJsonError.CodeInvalidArguments, typoMessage);
                    if (!isCompleteMode)
                    {
                        CommandCompletedEvent.Log(parsedArgs.CommandResult, 1);
                    }
                }
                else if (effectiveJson && IsFindUi(parsedArgs))
                {
                    // find-ui emits all its JSON (results and errors) on stdout as a flat
                    // {"error":"..."} object — keep parser-level failures on that same contract.
                    if (!isCompleteMode)
                    {
                        CommandInvokedEvent.Log(parsedArgs.CommandResult);
                    }
                    EmitFindUiJsonError($"Unknown option '{typo}'. Did you mean '{suggested}'?");
                    if (!isCompleteMode)
                    {
                        CommandCompletedEvent.Log(parsedArgs.CommandResult, 1);
                    }
                }
                else
                {
                    Console.Error.WriteLine(typoMessage);
                    Console.Error.WriteLine(
                        "(Single-dash flags are reserved for short aliases like '-a'. Long options use a double dash.)");
                }
                return 1;
            }
        }

        return await RunWithTelemetryAsync(parsedArgs, isCompleteMode, () => parsedArgs.InvokeAsync());
    }

    /// <summary>
    /// Applies the given console-encoding mutation, swallowing the platform exception that occurs
    /// when the standard stream encoding cannot be changed (e.g. redirected or unsupported handles).
    /// UTF-8 I/O is a best-effort nicety, so a failure here must never abort startup.
    /// </summary>
    internal static void ConfigureConsoleEncoding(Action apply)
    {
        try
        {
            apply();
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Runs the parsed command via <paramref name="invoke"/>, emitting command-invoked/-completed
    /// telemetry (unless in completion mode) and converting any unhandled exception into a logged
    /// error plus exit code 1. The <paramref name="invoke"/> seam lets tests exercise both the
    /// success and top-level-failure paths without needing a real, throwing command.
    /// </summary>
    internal static Task<int> RunWithTelemetryAsync(
        System.CommandLine.ParseResult parsedArgs, bool isCompleteMode, Func<Task<int>> invoke) =>
        RunWithTelemetryAsync(parsedArgs, isCompleteMode, invoke, CommandInvokedEvent.Log, CommandCompletedEvent.Log);

    /// <summary>
    /// Core of <see cref="RunWithTelemetryAsync(System.CommandLine.ParseResult, bool, Func{Task{int}})"/>
    /// with the telemetry sinks injected. <paramref name="logCommandInvoked"/> and
    /// <paramref name="logCommandCompleted"/> default (via the public overload) to the production
    /// <see cref="CommandInvokedEvent.Log"/>/<see cref="CommandCompletedEvent.Log"/> events; the seam
    /// lets tests assert that the invoked/completed events fire around the invocation — in order, with
    /// the parsed command result and real exit code, and only when not in completion mode.
    /// </summary>
    internal static async Task<int> RunWithTelemetryAsync(
        System.CommandLine.ParseResult parsedArgs, bool isCompleteMode, Func<Task<int>> invoke,
        Action<System.CommandLine.Parsing.CommandResult> logCommandInvoked,
        Action<System.CommandLine.Parsing.CommandResult, int> logCommandCompleted)
    {
        try
        {
            // Start a correlation scope so the CommandInvoked/CommandCompleted events and any
            // command-specific event a handler emits (e.g. NewCommand_Event) share one
            // relatedActivityId for this invocation.
            TelemetryCorrelation.Begin();

            // Open the coordination scope in the same async flow that reads it below. A `winapp ui`
            // command sets its cooperative-turn summary from deep inside invoke(), and
            // CommandCompletedEvent picks it up here (issue #764).
            Services.InteractiveDesktop.UiCoordinationTelemetryScope.Begin();

            if (!isCompleteMode)
            {
                logCommandInvoked(parsedArgs.CommandResult);
            }

            bool effectiveJson = ResolveEffectiveJson(parsedArgs);

            // Parse-error → JSON bridge: activated only when the SELECTED command exposes --json,
            // its parsed value is true (effectiveJson), AND the command is a ui descendant (M3).
            // Non-ui commands (e.g. cert info) use a flat {"error":"..."} schema — do not impose
            // the UI nested contract on them; let SCL's default parse-error handling run instead.
            // M3: emit CommandCompletedEvent before the early return to keep telemetry paired.
            if (effectiveJson && parsedArgs.Errors.Count > 0 && IsUiDescendant(parsedArgs))
            {
                var errorMsg = string.Join("; ", parsedArgs.Errors.Select(e => e.Message));
                UiJsonError.Emit(true, UiJsonError.CodeInvalidArguments, errorMsg);
                if (!isCompleteMode)
                {
                    logCommandCompleted(parsedArgs.CommandResult, 1);
                }
                return 1;
            }

            // Parse-error → JSON bridge for `winapp new`: it owns a bespoke NewCommandResult schema
            // (not the ui error envelope), so a parse failure like `--template bogus --json` must be
            // reported in that shape rather than falling through to SCL's help/error text on stdout.
            // Return ExitInvalidArgs (2) — the same code the handler uses for invalid names/versions —
            // so agents can branch on the invalid-argument stage regardless of where it was caught.
            if (effectiveJson && parsedArgs.Errors.Count > 0
                && parsedArgs.CommandResult.Command.Name == "new")
            {
                var errorMsg = string.Join("; ", parsedArgs.Errors.Select(e => e.Message));
                NewCommand.EmitParseErrorJson(errorMsg);
                if (!isCompleteMode)
                {
                    logCommandCompleted(parsedArgs.CommandResult, NewCommand.ExitInvalidArgs);
                }
                return NewCommand.ExitInvalidArgs;
            }

            // find-ui parse-error → JSON bridge. find-ui documents a --json contract
            // (search/--id/--list results and in-handler validation errors all emit JSON on
            // stdout), but a type/parser error such as `--max abc` fails before the handler
            // runs, so System.CommandLine would otherwise print human help text. Emit the same
            // flat {"error":"..."} object here so every --json path stays machine-readable (#719).
            if (effectiveJson && parsedArgs.Errors.Count > 0 && IsFindUi(parsedArgs))
            {
                var errorMsg = string.Join("; ", parsedArgs.Errors.Select(e => e.Message));
                EmitFindUiJsonError(errorMsg);
                if (!isCompleteMode)
                {
                    logCommandCompleted(parsedArgs.CommandResult, 1);
                }
                return 1;
            }

            var returnCode = await invoke();

            if (!isCompleteMode)
            {
                logCommandCompleted(parsedArgs.CommandResult, returnCode);
            }

            return returnCode;
        }
        catch (Exception ex)
        {
            TelemetryFactory.Get<ITelemetry>().LogException(parsedArgs.CommandResult.Command.Name, ex);
            Console.Error.WriteLine($"An unexpected error occurred: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Derives the effective JSON mode from the selected command's PARSED <c>--json</c> option.
    /// Reading the parsed value is reliable even when a different option caused a parse error,
    /// because System.CommandLine parses each option independently (M1).
    /// </summary>
    private static bool ResolveEffectiveJson(System.CommandLine.ParseResult parsedArgs)
    {
        // Only engage when the selected (innermost) command actually owns --json.
        var selectedCmd = parsedArgs.CommandResult.Command;
        if (!selectedCmd.Options.Contains(WinAppRootCommand.JsonOption))
        {
            return false;
        }
        try
        {
            return parsedArgs.GetValue(WinAppRootCommand.JsonOption);
        }
        catch
        {
            // If reading the parsed value fails for any reason, do not fire the bridge.
            return false;
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when the selected command is <c>new</c> with <c>--json</c> in
    /// effect. Callers use this to route an early parse failure (invalid boolean value, single-dash
    /// typo) through <see cref="NewCommand.EmitParseErrorJson(string, System.IO.TextWriter?)"/> and
    /// return <see cref="NewCommand.ExitInvalidArgs"/>, so <c>new --json</c> yields a machine-readable
    /// <c>NewCommandResult</c> — with the same invalid-argument exit code the handler uses — for
    /// EVERY parse error, not only those reaching the main bridge in <see cref="RunWithTelemetryAsync(System.CommandLine.ParseResult, bool, Func{Task{int}})"/>.
    /// </summary>
    private static bool IsNewCommandJson(System.CommandLine.ParseResult parseResult) =>
        ResolveEffectiveJson(parseResult) && parseResult.CommandResult.Command.Name == "new";

    /// <summary>
    /// Returns <see langword="true"/> when the selected command is the <c>ui</c> command
    /// or any of its descendants. Used to scope the parse-error JSON bridge to ui commands
    /// only, leaving non-ui commands (e.g. <c>cert info</c>) with their own flat error schema (M3).
    /// </summary>
    private static bool IsUiDescendant(System.CommandLine.ParseResult parseResult)
    {
        var cmd = parseResult.CommandResult.Command;
        while (cmd is not null)
        {
            if (cmd.Name == "ui")
            {
                return true;
            }
            cmd = cmd.Parents.OfType<System.CommandLine.Command>().FirstOrDefault();
        }
        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the selected command is <c>find-ui</c>. Used to route
    /// parser-level failures to find-ui's flat <c>{"error":"..."}</c> JSON contract (#719) instead
    /// of System.CommandLine's human help text, mirroring the ui bridge above.
    /// </summary>
    private static bool IsFindUi(System.CommandLine.ParseResult parseResult) =>
        parseResult.CommandResult.Command.Name == "find-ui";

    /// <summary>
    /// Writes a flat <c>{"error":"..."}</c> object to stdout — the same schema and sink
    /// <c>find-ui</c> uses for in-handler errors — so a parse failure under <c>--json</c> stays
    /// machine-readable. stdout (not stderr) matches where find-ui emits all its other JSON.
    /// </summary>
    private static void EmitFindUiJsonError(string message)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(
            new JsonErrorOutput { Error = message },
            WinAppJsonContext.Default.JsonErrorOutput);
        Console.Out.WriteLine(payload);
    }

    /// <summary>
    /// Scans argv for an invalid <c>=</c>-attached value on any boolean option reachable by the
    /// selected command — the command's own <see cref="System.CommandLine.Option{T}"/> boolean
    /// options plus inherited/global ones on ancestor commands (e.g. <c>--json</c>/<c>--verbose</c>/
    /// <c>--quiet</c> on the root, or <c>--eraser</c> on <c>ui pen</c>). System.CommandLine silently
    /// coerces a non-boolean attached value (e.g. <c>--eraser=bogus</c>) to <see langword="true"/>;
    /// the caller uses this to reject it with a clean error instead (#600 H1/M1/M2).
    /// </summary>
    private static bool TryFindInvalidBooleanOption(
        System.CommandLine.ParseResult parseResult,
        string[] args,
        out string optionName,
        out string invalidValue)
    {
        optionName = string.Empty;
        invalidValue = string.Empty;

        // Walk the selected command up through its ancestors so both command-level bool options
        // and inherited/global ones are covered. Dedupe by name in case an option appears twice.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cmd = parseResult.CommandResult.Command;
        while (cmd is not null)
        {
            foreach (var option in cmd.Options)
            {
                if (option is not System.CommandLine.Option<bool>)
                {
                    continue;
                }
                if (!seen.Add(option.Name))
                {
                    continue;
                }
                if (GlobalOptionPreScan.TryFindInvalidBooleanValue(args, option.Name, option.Aliases, out var badValue))
                {
                    optionName = option.Name;
                    invalidValue = badValue;
                    return true;
                }
            }

            cmd = cmd.Parents.OfType<System.CommandLine.Command>().FirstOrDefault();
        }

        return false;
    }

    /// <summary>
    /// The global logging mode resolved from argv: the minimum log level plus the individual
    /// <c>--quiet</c>/<c>--verbose</c>/<c>--json</c> flags, and a non-null <see cref="ConflictError"/>
    /// message when a mutually-exclusive combination was requested.
    /// </summary>
    internal readonly record struct LoggingMode(LogLevel MinimumLevel, bool Quiet, bool Verbose, bool Json, string? ConflictError);

    /// <summary>
    /// Pre-scans argv for the global logging-mode flags and resolves the effective
    /// <see cref="LoggingMode"/>. The scan (via <see cref="GlobalOptionPreScan"/>) stops at the first
    /// standalone <c>--</c> separator so passthrough payload (e.g. <c>winapp run . -- --json</c>) is
    /// not misread as a winapp global flag. Extracted from <see cref="Main"/> so the flag precedence
    /// and mutual-exclusion rules are unit testable without invoking the full entrypoint.
    /// </summary>
    internal static LoggingMode ResolveLoggingMode(string[] args)
    {
        var minimumLogLevel = LogLevel.Information;
        bool quiet = false;
        bool verbose = false;
        bool json = false;

        verbose = GlobalOptionPreScan.GetBooleanFlagValue(args, WinAppRootCommand.VerboseOption.Name, WinAppRootCommand.VerboseOption.Aliases);
        if (verbose)
        {
            minimumLogLevel = LogLevel.Debug;
        }
        quiet = GlobalOptionPreScan.GetBooleanFlagValue(args, WinAppRootCommand.QuietOption.Name, WinAppRootCommand.QuietOption.Aliases);
        if (quiet)
        {
            minimumLogLevel = LogLevel.Warning;
        }
        json = GlobalOptionPreScan.GetBooleanFlagValue(args, WinAppRootCommand.JsonOption.Name, WinAppRootCommand.JsonOption.Aliases);
        if (json)
        {
            minimumLogLevel = LogLevel.None;
        }

        string? conflictError = null;
        if (quiet && verbose)
        {
            conflictError = "Cannot specify both --quiet and --verbose options together.";
        }
        else if (quiet && json)
        {
            conflictError = "Cannot specify both --quiet and --json options together.";
        }
        else if (verbose && json)
        {
            conflictError = "Cannot specify both --verbose and --json options together.";
        }

        return new LoggingMode(minimumLogLevel, quiet, verbose, json, conflictError);
    }
}

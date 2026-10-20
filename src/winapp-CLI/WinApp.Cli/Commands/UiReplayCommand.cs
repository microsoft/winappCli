// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Commands;

internal sealed class UiReplayCommand : Command, IShortDescription
{
    public static Argument<string?> FlowArgument { get; } = new("flow")
    {
        Description = "Path to a reviewed schema-1 action JSON file. Incomplete recordings cannot be replayed.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public static Option<bool> AcceptRecordingOption { get; } = new("--accept-recording")
    {
        Description = "Required: confirm you reviewed the draft and approve injecting its inputs into the explicitly selected window.",
    };

    public static Option<string[]> ParameterOption { get; } = new("--parameter")
    {
        Description = "Supply a reviewed text parameter as NAME=VALUE. Repeat for each name; NAME= clears a field. Values are literal, never shell commands or environment-variable substitutions.",
        Arity = ArgumentArity.ZeroOrMore,
        AllowMultipleArgumentsPerToken = false,
    };

    public string ShortDescription => "Replay reviewed action inputs in one explicitly selected window";

    public UiReplayCommand()
        : base("replay", "Validate and replay a reviewed action-file draft using real mouse and keyboard input. " +
               "--accept-recording and an explicit --app or --window are required. " +
               "The selected process name must match the recording (case-insensitive); a new PID is allowed. " +
               "Supply every text parameter with --parameter NAME=VALUE. " +
               "Success means inputs completed and any manually authored assertions matched, not that the app or its accessibility passed.")
    {
        Arguments.Add(FlowArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(AcceptRecordingOption);
        Options.Add(ParameterOption);
        Options.Add(SharedUiOptions.TimeoutOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(
        IUiTargetResolver targetResolver,
        IUiActionService actionService,
        IAnsiConsole ansiConsole,
        ILogger<UiReplayCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var quiet = parseResult.GetValue(WinAppRootCommand.QuietOption);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var filePath = parseResult.GetValue(FlowArgument);
            var timeout = parseResult.GetValue(SharedUiOptions.TimeoutOption);
            var accepted = parseResult.GetValue(AcceptRecordingOption);

            int Fail(string code, string message)
            {
                UiJsonError.Emit(json, code, message, errorOut: parseResult.InvocationConfiguration.Error);
                if (!json)
                {
                    logger.LogError("{Symbol} {Message}", UiSymbols.Error, message);
                }
                return 1;
            }

            if (string.IsNullOrWhiteSpace(filePath))
            {
                return Fail(UiJsonError.CodeInvalidArguments, "An action file is required. Use 'winapp ui replay <flow.json> -a <app> --accept-recording'.");
            }
            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                return Fail(UiJsonError.CodeMissingApp,
                    "Target app required. Use --app <name|title|PID> or --window <HWND>. The action file never launches or selects an app.");
            }
            if (window is <= 0)
            {
                return Fail(UiJsonError.CodeInvalidArguments, "--window must be a positive HWND from 'winapp ui list-windows'.");
            }
            if (!accepted)
            {
                return Fail("review_required", "Review the action file and pass --accept-recording before replaying input.");
            }
            if (timeout is < 100 or > 120000)
            {
                return Fail(UiJsonError.CodeInvalidArguments, "--timeout must be between 100 and 120000 milliseconds.");
            }

            try
            {
                var parameterResult = parseResult.GetResult(ParameterOption);
                if (parameterResult is not null && parameterResult.IdentifierTokenCount > parameterResult.Tokens.Count)
                {
                    return Fail(UiJsonError.CodeInvalidArguments, "--parameter requires NAME=VALUE. The value may be empty to clear a field.");
                }
                var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var parameter in parseResult.GetValue(ParameterOption) ?? [])
                {
                    var separator = parameter.IndexOf('=');
                    if (separator <= 0)
                    {
                        return Fail(UiJsonError.CodeInvalidArguments, "--parameter requires NAME=VALUE. The value may be empty to clear a field.");
                    }
                    if (!parameters.TryAdd(parameter[..separator], parameter[(separator + 1)..]))
                    {
                        return Fail(UiJsonError.CodeInvalidArguments, "Each --parameter must use a distinct name; duplicate parameters are not accepted.");
                    }
                }

                UiActionFlow flow;
                try
                {
                    flow = UiActionFlowDocument.Read(filePath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    return Fail("invalid_flow", "The action file could not be read. Check its path, permissions, and supported JSON format.");
                }
                var options = new UiActionReplayOptions
                {
                    AcceptRecording = accepted,
                    TimeoutMs = timeout,
                    Parameters = parameters,
                };
                // Even the final action and every parameter must be valid before selecting an app.
                UiActionFlowDocument.ValidateReplay(flow, options);

                cancellationToken.ThrowIfCancellationRequested();
                UiTarget target;
                try
                {
                    target = await targetResolver.ResolveAsync(app, window, cancellationToken);
                }
                catch (InvalidOperationException ex) when (ex is not UiActionException)
                {
                    return Fail(UiJsonError.CodeNoTarget, "The selected window could not be resolved for replay. Use --window with an HWND from 'winapp ui list-windows'.");
                }
                var result = await actionService.ReplayAsync(target, flow, options, cancellationToken);

                if (json)
                {
                    var payload = new UiActionsReplayResult
                    {
                        CompletedSteps = result.CompletedSteps,
                        AssertionsChecked = result.AssertionsChecked,
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(payload, UiJsonContext.Default.UiActionsReplayResult));
                }
                else if (!quiet)
                {
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        $"Replay inputs completed: {result.CompletedSteps} steps; {result.AssertionsChecked} manually authored assertions checked.");
                    ansiConsole.Profile.Out.Writer.WriteLine("This is not an automatic correctness or accessibility verdict.");
                }
                return 0;
            }
            catch (UiActionException ex)
            {
                return Fail(ex.Code, ex.Message);
            }
            catch (AppNotFoundException)
            {
                return Fail(UiJsonError.CodeNoTarget, "The selected app or window was not found. Run 'winapp ui list-windows' and choose a running window.");
            }
            catch (UiAmbiguousSelectorException)
            {
                return Fail(UiJsonError.CodeAmbiguousSelector, "A recorded target matches more than one element. Review its locator and use a unique AutomationId.");
            }
            catch (UiElementNotFoundException)
            {
                return Fail(UiJsonError.CodeElementNotFound, "A recorded target could not be found. Inspect the selected window and review the flow.");
            }
            catch (ForegroundLostException)
            {
                return Fail(UiJsonError.CodeForegroundNotTarget, "Replay stopped because focus left the selected window. Restore the app's starting state before retrying.");
            }
            catch (OperationCanceledException)
            {
                return Fail("cancelled", "Action replay was cancelled. Some earlier inputs may already have completed; restore the app's starting state before retrying.");
            }
            catch (Exception ex)
            {
                logger.LogDebug("Action replay failed ({ExceptionType}).", ex.GetType().Name);
                return Fail(UiJsonError.CodeInternalError, "Action replay failed. Some earlier inputs may already have completed; restore the app's starting state before retrying.");
            }
        }
    }
}

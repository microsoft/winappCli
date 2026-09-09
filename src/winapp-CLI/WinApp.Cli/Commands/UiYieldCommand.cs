// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Commands;

internal class UiYieldCommand : Command, IShortDescription
{
    public string ShortDescription => "Release this workflow's UI turn immediately instead of waiting out the idle grace";

    public UiYieldCommand()
        : base("yield", "Release the current workflow's idle UI turn early. " +
               "A workflow with WINAPP_UI_WORKFLOW_ID keeps the desktop for a few seconds after each command so a burst " +
               "of commands reads as one workflow; run this after the final command of a workflow to hand the desktop to " +
               "waiting workflows straight away. Requires WINAPP_UI_WORKFLOW_ID; targets no app and takes no selector.")
    {
        Options.Add(WinAppRootCommand.JsonOption);
    }

    /// <summary>
    /// Handler for <c>ui yield</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately not a <see cref="UiCoordinatedAction"/>. Every other <c>ui</c> command asks
    /// coordination for a turn; this one gives one back, so registering it as a participant would make
    /// it the very live command that stops a turn being idle — it would always find itself busy.
    /// </remarks>
    public class Handler(
        IAnsiConsole ansiConsole,
        IInteractiveDesktopLock desktopLock,
        ILogger<UiYieldCommand> logger) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);

            try
            {
                return Task.FromResult(Report(desktopLock.ReleaseIdleTurn(cancellationToken), json, parseResult));
            }
            catch (UiCoordinationException ex)
            {
                logger.LogError("{Symbol} {Message}", UiSymbols.Error, ex.Message);
                if (ex.RecoveryHint is { } hint)
                {
                    logger.LogError("{Symbol} {Hint}", UiSymbols.Error, hint);
                }

                UiJsonError.Emit(
                    json,
                    ex.Code,
                    ex.Message,
                    errorOut: parseResult.InvocationConfiguration.Error,
                    recoveryHint: ex.RecoveryHint);
                return Task.FromResult(1);
            }
        }

        private int Report(UiYieldResult result, bool json, ParseResult parseResult)
        {
            switch (result)
            {
                case UiYieldResult.NotAWorkflow:
                    // Not invalid_ui_workflow_id: that code means the variable is present but malformed,
                    // and conflating the two would send someone hunting for a bad value they never set.
                    logger.LogError(
                        "{Symbol} Set {Variable} before running 'winapp ui yield' — without it each command is its own one-shot workflow that already releases the desktop when it finishes, so there is no turn to yield.",
                        UiSymbols.Error,
                        UiOwnerResolver.WorkflowIdVariable);
                    UiJsonError.Emit(
                        json,
                        UiJsonError.CodeInvalidArguments,
                        $"'winapp ui yield' requires {UiOwnerResolver.WorkflowIdVariable}. Without it each command is its own one-shot workflow and releases the desktop as soon as it finishes.",
                        errorOut: parseResult.InvocationConfiguration.Error);
                    return 1;

                case UiYieldResult.Busy:
                    throw new UiCoordinationException(
                        UiCoordinationErrorCodes.TurnBusy,
                        "This workflow still has a winapp ui command running or waiting, so its turn is not idle and was not released.",
                        "Wait for this workflow's other winapp ui commands to finish — or stop them, for example a recording started with the same WINAPP_UI_WORKFLOW_ID — then run 'winapp ui yield' again.");

                case UiYieldResult.Released:
                    EmitReleased(json, released: true);
                    if (!json)
                    {
                        logger.LogInformation("Released the UI turn.");
                    }

                    return 0;

                default:
                    // Idempotent by design: yielding twice, or after the grace already lapsed, is the
                    // normal end of a script and must not look like a failure.
                    EmitReleased(json, released: false);
                    if (!json)
                    {
                        logger.LogInformation("Nothing to release — this workflow does not hold the UI turn.");
                    }

                    return 0;
            }
        }

        private void EmitReleased(bool json, bool released)
        {
            if (!json)
            {
                return;
            }

            ansiConsole.Profile.Out.Writer.WriteLine(
                JsonSerializer.Serialize(
                    new UiYieldResultJson { Released = released }, UiJsonContext.Default.UiYieldResultJson));
        }
    }
}

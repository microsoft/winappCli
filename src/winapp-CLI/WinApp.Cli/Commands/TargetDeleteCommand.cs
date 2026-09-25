// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using Spectre.Console;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Commands;

internal sealed class TargetDeleteCommand : Command, IShortDescription
{
    public string ShortDescription => "Delete a named MXC execution target";
    public static Argument<string> SelectorArgument { get; } = TargetVerb.NewSelectorArgument();

    public TargetDeleteCommand() : base("delete",
        "Delete a named MXC target and its saved state. Never creates or prepares a target. " +
        "Windows Sandbox deletion is not supported.")
    {
        Arguments.Add(SelectorArgument);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(ExecutionTargetOrchestrator orchestrator, IAnsiConsole console)
        : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(parseResult);
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            ExecutionTargetOrchestrator selected;
            try
            {
                selected = TargetVerb.Resolve(orchestrator, parseResult.GetValue(SelectorArgument));
            }
            catch (ExecutionTargetException ex)
            {
                return TargetOutput.RejectSelection(console, json, ex.Error);
            }

            try
            {
                await selected.DeleteAsync(cancellationToken).ConfigureAwait(false);
                if (json)
                {
                    console.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(
                        new TargetDeleteOutput
                        {
                            ExecutionTarget = ExecutionTargetScope.For(selected.Target, ExecutionTargetEpoch.None),
                            Deleted = true,
                        },
                        TargetJsonContext.Default.TargetDeleteOutput));
                }
                else
                {
                    console.MarkupLineInterpolated($"Deleted {selected.Target.Selector}.");
                }

                return 0;
            }
            catch (ExecutionTargetException ex)
            {
                return TargetOutput.Fail(console, json, ex.Error);
            }
        }
    }
}

internal sealed class TargetDeleteOutput
{
    public required ExecutionTargetScope ExecutionTarget { get; init; }
    public bool Deleted { get; init; }
}

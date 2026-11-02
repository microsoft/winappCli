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

internal sealed class UiExpandCollapseCommand : Command, IShortDescription
{
    public string ShortDescription => "Set an element's expanded or collapsed state";

    public static Option<string?> StateOption { get; } = new("--state")
    {
        Description = "Required state: expanded or collapsed. An already matching state is left unchanged.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public UiExpandCollapseCommand()
        : base("expand-collapse", "Set an element's explicit expanded or collapsed state using only its own ExpandCollapsePattern. " +
            "Confirms the state by readback; never invokes, toggles, injects input, or tries an ancestor.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(StateOption);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(WinAppRootCommand.JsonOption);
        UiQueryOptions.AddTo(this);
    }

    public sealed class Handler(
        IUiTargetResolver targetResolver,
        IUiAutomation uiAutomation,
        IUiSelectorParser selectorParser,
        IAnsiConsole ansiConsole,
        IInteractiveDesktopLock desktopLock,
        ILogger<UiExpandCollapseCommand> logger) : UiCoordinatedAction(desktopLock, logger)
    {
        protected override string Operation => "ui expand-collapse";

        protected override UiTurnMode ResolveMode(ParseResult parseResult) => UiTurnMode.DesktopExclusive;

        protected override int? Preflight(ParseResult parseResult)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var selectorArg = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var selectorStr = UiQueryOptions.Describe(parseResult, selectorArg);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }
            if (!UiQueryOptions.HasQuery(parseResult, selectorArg))
            {
                UiErrors.MissingSelector(logger, "expand-collapse", json);
                return 1;
            }

            var stateText = parseResult.GetValue(StateOption);
            UiExpandCollapseState? state = stateText?.ToLowerInvariant() switch
            {
                "expanded" => UiExpandCollapseState.Expanded,
                "collapsed" => UiExpandCollapseState.Collapsed,
                _ => null,
            };
            if (state is null)
            {
                const string message = "Specify --state expanded or --state collapsed.";
                logger.LogError("{Symbol} {Message}", UiSymbols.Error, message);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, message, selectorStr,
                    errorOut: parseResult.InvocationConfiguration.Error);
                return 1;
            }

            if (UiQueryOptions.Validate(parseResult, logger, json) is { } validationExit)
            {
                return validationExit;
            }

            return null;
        }

        protected override async Task<int> ExecuteAsync(ParseResult parseResult, IUiTurn turn, CancellationToken cancellationToken)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var selectorArg = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var selectorStr = UiQueryOptions.Describe(parseResult, selectorArg);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var state = parseResult.GetValue(StateOption)!.Equals("expanded", StringComparison.OrdinalIgnoreCase)
                ? UiExpandCollapseState.Expanded : UiExpandCollapseState.Collapsed;
            try
            {
                await using var section = await turn.EnterAsync(cancellationToken).ConfigureAwait(false);
                var target = await targetResolver.ResolveAsync(app, window, cancellationToken);
                var selector = UiQueryOptions.Parse(parseResult, selectorParser, selectorArg);
                var element = await UiQueryOptions.FindTargetAsync(parseResult, uiAutomation, target, selector, cancellationToken);
                if (element is null)
                {
                    UiErrors.ElementNotFound(logger, selectorStr, json);
                    return 1;
                }

                var actual = await uiAutomation.SetExpandCollapseStateAsync(target, element, state, cancellationToken);
                if (json)
                {
                    var result = new UiExpandCollapseResult
                    {
                        ElementId = element.Selector ?? element.Id ?? "",
                        Hwnd = target.WindowHandle,
                        State = actual.ToString(),
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiExpandCollapseResult));
                }
                else
                {
                    logger.LogInformation("{ElementId}: {State} via ExpandCollapsePattern", element.Selector ?? element.Id, actual);
                }
                return 0;
            }
            catch (NotSupportedException ex)
            {
                logger.LogError("{Symbol} {Message}", UiSymbols.Error, ex.Message);
                UiJsonError.Emit(json, UiJsonError.CodePatternUnsupported, ex.Message, selectorStr,
                    errorOut: parseResult.InvocationConfiguration.Error);
                return 1;
            }
            catch (UiAmbiguousSelectorException ex)
            {
                UiErrors.AmbiguousSelector(logger, ex.Message, json);
                return 1;
            }
            catch (Exception ex) when (!IsCoordinationFault(ex))
            {
                UiErrors.GenericError(logger, ex, json, errorOut: parseResult.InvocationConfiguration.Error);
                return 1;
            }
        }
    }
}

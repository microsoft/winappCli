// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Commands;

internal class UiInvokeCommand : Command, IShortDescription
{
    public string ShortDescription => "Activate an element via UIA patterns (Invoke, Toggle, etc.)";

    public static Option<string?> ActionOption { get; } = new("--action")
    {
        Description = "Perform exactly this action on the selected element, without pattern or ancestor fallback: invoke, select, toggle, toggle-on, toggle-off, expand, collapse."
    };

    private static UiInvokeAction? ParseAction(string? action) => action switch
    {
        "invoke" => UiInvokeAction.Invoke,
        "select" => UiInvokeAction.Select,
        "toggle" => UiInvokeAction.Toggle,
        "toggle-on" => UiInvokeAction.ToggleOn,
        "toggle-off" => UiInvokeAction.ToggleOff,
        "expand" => UiInvokeAction.Expand,
        "collapse" => UiInvokeAction.Collapse,
        _ => null
    };

    public UiInvokeCommand()
        : base("invoke", "Activate an element by slug or text search. " +
               "Without --action, tries InvokePattern, TogglePattern, SelectionItemPattern, and ExpandCollapsePattern in order, then an invokable ancestor. " +
               "Use --action for an exact operation on only the selected element; --root, --type and --class-name require --action.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(ActionOption);

        Options.Add(WinAppRootCommand.JsonOption);
        UiQueryOptions.AddTo(this);
        Validators.Add(result =>
        {
            if (result.GetResult(ActionOption) is { Tokens.Count: 1 } action &&
                ParseAction(action.Tokens[0].Value) is null)
            {
                result.AddError("--action must be invoke, select, toggle, toggle-on, toggle-off, expand, or collapse.");
            }
            if (result.GetResult(ActionOption) is null &&
                (result.GetResult(UiQueryOptions.Root) is not null ||
                 result.GetResult(UiQueryOptions.Type) is not null ||
                 result.GetResult(UiQueryOptions.ClassName) is not null))
            {
                result.AddError("--root, --type, and --class-name require --action on ui invoke.");
            }
        });
    }

    public class Handler(
        IUiTargetResolver targetResolver,
        IUiAutomation uiAutomation,
        IUiSelectorParser selectorParser,
        ISystemUiQuery systemQuery,
        IAnsiConsole ansiConsole,
        IInteractiveDesktopLock desktopLock,
        ILogger<UiInvokeCommand> logger) : UiCoordinatedAction(desktopLock, logger)
    {
        protected override string Operation => "ui invoke";

        /// <summary>InvokePattern and related actions can mutate UI and must run as a desktop turn.</summary>
        protected override UiTurnMode ResolveMode(ParseResult parseResult) => UiTurnMode.DesktopExclusive;

        protected override int? Preflight(ParseResult parseResult)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var selectorStr = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }

            if (string.IsNullOrWhiteSpace(selectorStr))
            {
                UiErrors.MissingSelector(logger, "invoke", json);
                return 1;
            }

            return UiQueryOptions.Validate(parseResult, logger, json);
        }

        protected override async Task<int> ExecuteAsync(ParseResult parseResult, IUiTurn turn, CancellationToken cancellationToken)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            // Preflight rejected a missing selector, so this is non-null by construction.
            var selectorStr = parseResult.GetValue(SharedUiOptions.SelectorArgument)!;
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var requestedAction = parseResult.GetValue(ActionOption);
            var action = ParseAction(requestedAction);

            try
            {
                var uiTarget = await targetResolver.ResolveAsync(app, window, cancellationToken);
                var selector = action is null
                    ? selectorParser.Parse(selectorStr)
                    : UiQueryOptions.Parse(parseResult, selectorParser, selectorStr);
                var filteredAction = action is not null && selector.HasConstraints;
                var element = await uiAutomation.FindSingleElementAsync(
                    uiTarget, selector, requireUnique: action is not null, cancellationToken);

                if (element is null)
                {
                    UiErrors.ElementNotFound(logger, selectorStr, json, parseResult.InvocationConfiguration.Error);
                    return 1;
                }

                string pattern;
                string performedAction;
                UiElement invokedElement;

                await using (await turn.EnterAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (filteredAction)
                    {
                        // Keep the identity selected before the wait, while proving the same
                        // filtered query still has exactly that one match under the turn.
                        var current = await uiAutomation.FindSingleElementAsync(
                            uiTarget, selector, requireUnique: true, cancellationToken);
                        if (current is null || current.WindowHandle != element.WindowHandle ||
                            !uiAutomation.IsSameElement(element, current, cancellationToken))
                        {
                            UiErrors.StaleElement(logger, json, parseResult.InvocationConfiguration.Error);
                            return 1;
                        }
                    }
                    // Explicit mode commits to the initial identity. Its service overload resolves
                    // that identity inside the turn, rather than rerunning a possibly broad text query.
                    else if (action is null)
                    {
                        element = await uiAutomation.FindSingleElementAsync(uiTarget, selector, cancellationToken);
                        if (element is null)
                        {
                            UiErrors.ElementNotFound(logger, selectorStr, json, parseResult.InvocationConfiguration.Error);
                            return 1;
                        }
                    }

                    // All branches above have resolved an element or returned.
                    if (!DesktopTargetValidation.TryConfirmTargetWindow(
                            systemQuery, element!.WindowHandle ?? uiTarget.WindowHandle, uiTarget.ProcessId,
                            logger, json, "invoke", parseResult.InvocationConfiguration.Error))
                    {
                        return 1;
                    }

                    try
                    {
                        if (action is { } explicitAction)
                        {
                            var outcome = await uiAutomation.InvokeAsync(uiTarget, element, explicitAction, cancellationToken);
                            pattern = outcome.Pattern;
                            performedAction = outcome.PerformedAction;
                        }
                        else
                        {
                            pattern = await uiAutomation.InvokeAsync(uiTarget, element, cancellationToken);
                            performedAction = AutomaticAction(pattern);
                        }
                        invokedElement = element;
                    }
                    catch (InvalidOperationException) when (action is null && element.InvokableAncestor is { } ancestor)
                    {
                        // Element isn't invokable but has an invokable ancestor — invoke that instead
                        if (!DesktopTargetValidation.TryConfirmTargetWindow(
                                systemQuery, ancestor.WindowHandle ?? uiTarget.WindowHandle, uiTarget.ProcessId,
                                logger, json, "invoke", parseResult.InvocationConfiguration.Error))
                        {
                            return 1;
                        }

                        pattern = await uiAutomation.InvokeAsync(uiTarget, ancestor, cancellationToken);
                        performedAction = AutomaticAction(pattern);
                        invokedElement = ancestor;
                    }
                }

                if (json)
                {
                    var result = new UiInvokeResult
                    {
                        ElementId = invokedElement.Selector ?? invokedElement.Id ?? "",
                        Pattern = pattern,
                        RequestedAction = requestedAction ?? "auto",
                        PerformedAction = performedAction,
                        Hwnd = invokedElement.WindowHandle ?? uiTarget.WindowHandle
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiInvokeResult));
                }
                else
                {
                    if (action is not null)
                    {
                        logger.LogInformation("Requested {RequestedAction} on {ElementId}; performed {PerformedAction} via {Pattern}",
                            requestedAction, invokedElement.Selector ?? invokedElement.Id ?? "", performedAction, pattern);
                    }
                    else if (ReferenceEquals(invokedElement, element))
                    {
                        logger.LogInformation("Invoked {ElementId} via {Pattern}", (element.Selector ?? element.Id ?? ""), pattern);
                    }
                    else
                    {
                        logger.LogInformation("Invoked ancestor {Selector} \"{Name}\" via {Pattern} (matched text element was not invokable)",
                            invokedElement.Selector ?? invokedElement.Id, invokedElement.Name, pattern);
                    }
                }

                return 0;
            }
            catch (UiAmbiguousSelectorException ex) when (action is not null)
            {
                UiErrors.AmbiguousSelector(logger, ex.Message, json, parseResult.InvocationConfiguration.Error);
                return 1;
            }
            catch (System.Runtime.InteropServices.COMException comEx)
                when (action is null || comEx.HResult == unchecked((int)0x80040201))
            {
                logger.LogDebug("COM error: {HResult} {StackTrace}", comEx.HResult, comEx.StackTrace);
                UiErrors.StaleElement(logger, json, parseResult.InvocationConfiguration.Error);
                return 1;
            }
            catch (Exception ex) when (!UiCoordinatedAction.IsCoordinationFault(ex))
            {
                UiErrors.GenericError(logger, ex, json, parseResult.InvocationConfiguration.Error);
                return 1;
            }
        }

        private static string AutomaticAction(string pattern) => pattern switch
        {
            "InvokePattern" => "invoke",
            "TogglePattern" => "toggle",
            "SelectionItemPattern" => "select",
            "ExpandCollapsePattern" => "expand",
            _ => throw new InvalidOperationException($"Unknown invoke pattern '{pattern}'.")
        };
    }
}

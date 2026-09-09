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

    public UiInvokeCommand()
        : base("invoke", "Activate an element by slug or text search. " +
               "Tries InvokePattern, TogglePattern, SelectionItemPattern, and ExpandCollapsePattern in order.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);

        Options.Add(WinAppRootCommand.JsonOption);
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

            return null;
        }

        protected override async Task<int> ExecuteAsync(ParseResult parseResult, IUiTurn turn, CancellationToken cancellationToken)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            // Preflight rejected a missing selector, so this is non-null by construction.
            var selectorStr = parseResult.GetValue(SharedUiOptions.SelectorArgument)!;
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);

            try
            {
                var uiTarget = await targetResolver.ResolveAsync(app, window, cancellationToken);
                var selector = selectorParser.Parse(selectorStr);
                var element = await uiAutomation.FindSingleElementAsync(uiTarget, selector, cancellationToken);

                if (element is null)
                {
                    UiErrors.ElementNotFound(logger, selectorStr, json);
                    return 1;
                }

                string pattern;
                UiElement invokedElement = element;

                await using (await turn.EnterAsync(cancellationToken).ConfigureAwait(false))
                {
                    element = await uiAutomation.FindSingleElementAsync(uiTarget, selector, cancellationToken);
                    if (element is null)
                    {
                        UiErrors.ElementNotFound(logger, selectorStr, json);
                        return 1;
                    }

                    if (!DesktopTargetValidation.TryConfirmTargetWindow(
                            systemQuery, element.WindowHandle ?? uiTarget.WindowHandle, uiTarget.ProcessId,
                            logger, json, "invoke", parseResult.InvocationConfiguration.Error))
                    {
                        return 1;
                    }

                    try
                    {
                        pattern = await uiAutomation.InvokeAsync(uiTarget, element, cancellationToken);
                        invokedElement = element;
                    }
                    catch (InvalidOperationException) when (element.InvokableAncestor is { } ancestor)
                    {
                        // Element isn't invokable but has an invokable ancestor — invoke that instead
                        if (!DesktopTargetValidation.TryConfirmTargetWindow(
                                systemQuery, ancestor.WindowHandle ?? uiTarget.WindowHandle, uiTarget.ProcessId,
                                logger, json, "invoke", parseResult.InvocationConfiguration.Error))
                        {
                            return 1;
                        }

                        pattern = await uiAutomation.InvokeAsync(uiTarget, ancestor, cancellationToken);
                        invokedElement = ancestor;
                    }
                }

                if (json)
                {
                    var result = new UiInvokeResult { ElementId = (invokedElement.Selector ?? invokedElement.Id ?? ""), Pattern = pattern, Hwnd = uiTarget.WindowHandle };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiInvokeResult));
                }
                else
                {
                    if (ReferenceEquals(invokedElement, element))
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
            catch (System.Runtime.InteropServices.COMException comEx)
            {
                logger.LogDebug("COM error: {HResult} {StackTrace}", comEx.HResult, comEx.StackTrace);
                UiErrors.StaleElement(logger, json);
                return 1;
            }
            catch (Exception ex) when (!UiCoordinatedAction.IsCoordinationFault(ex))
            {
                UiErrors.GenericError(logger, ex, json);
                return 1;
            }
        }
    }
}

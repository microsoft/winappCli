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
        IAnsiConsole ansiConsole,
        ILogger<UiInvokeCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
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
                try
                {
                    pattern = await uiAutomation.InvokeAsync(uiTarget, element, cancellationToken);
                }
                catch (InvalidOperationException) when (element.InvokableAncestor is { } ancestor)
                {
                    // Element isn't invokable but has an invokable ancestor — invoke that instead
                    pattern = await uiAutomation.InvokeAsync(uiTarget, ancestor, cancellationToken);
                    if (json)
                    {
                        var result = new UiInvokeResult { ElementId = ancestor.Selector ?? ancestor.Id ?? "", Pattern = pattern, Hwnd = uiTarget.WindowHandle };
                        ansiConsole.Profile.Out.Writer.WriteLine(
                            JsonSerializer.Serialize(result, UiJsonContext.Default.UiInvokeResult));
                    }
                    else
                    {
                        logger.LogInformation("Invoked ancestor {Selector} \"{Name}\" via {Pattern} (matched text element was not invokable)",
                            ancestor.Selector ?? ancestor.Id, ancestor.Name, pattern);
                    }
                    return 0;
                }

                if (json)
                {
                    var result = new UiInvokeResult { ElementId = (element.Selector ?? element.Id ?? ""), Pattern = pattern, Hwnd = uiTarget.WindowHandle };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiInvokeResult));
                }
                else
                {
                    logger.LogInformation("Invoked {ElementId} via {Pattern}", (element.Selector ?? element.Id ?? ""), pattern);
                }

                return 0;
            }
            catch (System.Runtime.InteropServices.COMException comEx)
            {
                logger.LogDebug("COM error: {HResult} {StackTrace}", comEx.HResult, comEx.StackTrace);
                UiErrors.StaleElement(logger, json);
                return 1;
            }
            catch (Exception ex)
            {
                UiErrors.GenericError(logger, ex, json);
                return 1;
            }
        }
    }
}

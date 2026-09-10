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

internal class UiGetPropertyCommand : Command, IShortDescription
{
    public string ShortDescription => "Read property values from an element";

    public UiGetPropertyCommand()
        : base("get-property", "Read UIA property values from an element. Specify --property for a single property or omit for all.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);

        Options.Add(WinAppRootCommand.JsonOption);
        Options.Add(SharedUiOptions.PropertyOption);
    }

    public class Handler(
        IUiTargetResolver targetResolver,
        IUiAutomation uiAutomation,
        IUiSelectorParser selectorParser,
        IAnsiConsole ansiConsole,
        IInteractiveDesktopLock desktopLock,
        ILogger<UiGetPropertyCommand> logger) : UiCoordinatedAction(desktopLock, logger)
    {
        protected override string Operation => "ui get-property";

        /// <summary>Reading properties is a background-safe UIA read that never touches the desktop.</summary>
        protected override UiTurnMode ResolveMode(ParseResult parseResult) => UiTurnMode.Observe;

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
                UiErrors.MissingSelector(logger, "get-property", json);
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
            var propertyName = parseResult.GetValue(SharedUiOptions.PropertyOption);

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

                var props = await uiAutomation.GetPropertiesAsync(uiTarget, element, propertyName, cancellationToken);

                if (json)
                {
                    // Convert to string values for JSON serialization (source-gen can't handle object?)
                    var stringProps = new Dictionary<string, string?>();
                    foreach (var kvp in props)
                    {
                        stringProps[kvp.Key] = kvp.Value?.ToString();
                    }
                    var result = new UiPropertyResult { ElementId = (element.Selector ?? element.Id ?? ""), Properties = stringProps };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiPropertyResult));
                }
                else
                {
                    foreach (var kvp in props)
                    {
                        var value = kvp.Value?.ToString() ?? "(null)";
                        // Sanitize control characters for single-line display
                        value = value.Replace("\r\n", "↵").Replace("\r", "↵").Replace("\n", "↵").Replace("\t", "→");
                        ansiConsole.WriteLine($"  {kvp.Key}: {value}");
                    }
                }

                if (!json)
                {
                    logger.LogInformation("{ElementId}: {Count} properties", (element.Selector ?? element.Id ?? ""), props.Count);
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

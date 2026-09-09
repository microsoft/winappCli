// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Commands;

internal class UiGetValueCommand : Command, IShortDescription
{
    public string ShortDescription => "Read the current value from an element";

    public UiGetValueCommand()
        : base("get-value", "Read the current value from an element. " +
               "Tries TextPattern (RichEditBox, Document), ValuePattern (TextBox, ComboBox, Slider), then Name (labels). " +
               "Usage: winapp ui get-value <selector> -a <app>")
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
        IInteractiveDesktopLock desktopLock,
        ILogger<UiGetValueCommand> logger) : UiCoordinatedAction(desktopLock, logger)
    {
        protected override string Operation => "ui get-value";

        /// <summary>Reading a value is a background-safe UIA read that never touches the desktop.</summary>
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
                UiErrors.MissingSelector(logger, "get-value", json);
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

                var text = await uiAutomation.GetTextAsync(uiTarget, element, cancellationToken);

                if (json)
                {
                    var result = new UiGetValueResult
                    {
                        ElementId = (element.Selector ?? element.Id ?? ""),
                        Text = text
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiGetValueResult));
                    return 0;
                }

                if (text is null)
                {
                    logger.LogInformation("No value found on {ElementId}", (element.Selector ?? element.Id ?? ""));
                }
                else
                {
                    // Strip all carriage returns (Windows line endings → Unix) but preserve newlines
                    ansiConsole.WriteLine(text.Replace("\r", "").TrimEnd('\n'));
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

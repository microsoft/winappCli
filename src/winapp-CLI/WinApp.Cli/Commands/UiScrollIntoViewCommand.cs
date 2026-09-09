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

internal class UiScrollIntoViewCommand : Command, IShortDescription
{
    public string ShortDescription => "Scroll an element into the visible area";

    public UiScrollIntoViewCommand()
        : base("scroll-into-view", "Scroll the specified element into the visible area using UIA ScrollItemPattern.")
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
        ILogger<UiScrollIntoViewCommand> logger) : UiCoordinatedAction(desktopLock, logger)
    {
        protected override string Operation => "ui scroll-into-view";

        /// <remarks>
        /// UIA <c>ScrollItemPattern</c> works in the background and never takes the foreground, so this
        /// never takes <c>active.lock</c> and stays usable on a locked or headless session. It does move
        /// the container, though, so it waits behind a foreign workflow's turn rather than scrolling
        /// content out from under somebody else's click.
        /// </remarks>
        protected override UiTurnMode ResolveMode(ParseResult parseResult) => UiTurnMode.TurnShared;

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
                UiErrors.MissingSelector(logger, "scroll-into-view", json);
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

                await uiAutomation.ScrollIntoViewAsync(uiTarget, element, cancellationToken);
                if (json)
                {
                    var result = new UiScrollIntoViewResult { ElementId = (element.Selector ?? element.Id ?? ""), Hwnd = uiTarget.WindowHandle };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiScrollIntoViewResult));
                }
                else
                {
                    logger.LogInformation("Scrolled {ElementId} into view", (element.Selector ?? element.Id ?? ""));
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

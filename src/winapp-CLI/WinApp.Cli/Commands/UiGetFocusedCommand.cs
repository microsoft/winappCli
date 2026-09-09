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

internal class UiGetFocusedCommand : Command, IShortDescription
{
    public string ShortDescription => "Show the element that currently has keyboard focus";

    public UiGetFocusedCommand()
        : base("get-focused", "Show the element that currently has keyboard focus in the target app.")
    {
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        IUiTargetResolver targetResolver,
        IUiAutomation uiAutomation,
        IAnsiConsole ansiConsole,
        IInteractiveDesktopLock desktopLock,
        ILogger<UiGetFocusedCommand> logger) : UiCoordinatedAction(desktopLock, logger)
    {
        protected override string Operation => "ui get-focused";

        /// <summary>Reading which element has focus never changes it, so no turn is claimed.</summary>
        protected override UiTurnMode ResolveMode(ParseResult parseResult) => UiTurnMode.Observe;

        protected override int? Preflight(ParseResult parseResult)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }

            return null;
        }

        protected override async Task<int> ExecuteAsync(ParseResult parseResult, IUiTurn turn, CancellationToken cancellationToken)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);

            try
            {
                var uiTarget = await targetResolver.ResolveAsync(app, window, cancellationToken);
                var element = await uiAutomation.GetFocusedElementAsync(uiTarget, cancellationToken);

                if (json)
                {
                    UiElementScrubber.Scrub(element);
                    var result = new UiFocusedResult { HasFocus = element is not null, Element = element };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiFocusedResult));
                    return 0;
                }

                if (element is null)
                {
                    logger.LogInformation("No element has keyboard focus in this app");
                    return 0;
                }

                var sel = element.Selector ?? element.Id ?? "";
                var displayName = element.Name ?? element.AutomationId;
                var name = displayName is not null && displayName != sel
                    ? $" [green]\"{Markup.Escape(displayName)}\"[/]" : "";
                var value = element.Value is not null && element.Value != element.Name
                    ? $" [yellow]value=\"{Markup.Escape(element.Value)}\"[/]" : "";
                var bounds = element.Width > 0 ? $" [grey]({element.X},{element.Y} {element.Width}x{element.Height})[/]" : "";
                ansiConsole.MarkupLine($"[bold cyan]{Markup.Escape(sel)}[/] {element.Type}{name}{value}{bounds}");

                logger.LogInformation("Focused: {Type} {Name}", element.Type, element.Name ?? "(unnamed)");
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

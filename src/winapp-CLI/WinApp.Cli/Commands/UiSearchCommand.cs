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

internal class UiSearchCommand : Command, IShortDescription
{
    public string ShortDescription => "Find elements by text";

    public UiSearchCommand()
        : base("search", "Search the element tree for elements matching a text query. Returns all matches with semantic slugs.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);

        Options.Add(WinAppRootCommand.JsonOption);
        Options.Add(SharedUiOptions.MaxResultsOption);
    }

    public class Handler(
        IUiTargetResolver targetResolver,
        IUiAutomation uiAutomation,
        IUiSelectorParser selectorParser,
        IAnsiConsole ansiConsole,
        IInteractiveDesktopLock desktopLock,
        ILogger<UiSearchCommand> logger) : UiCoordinatedAction(desktopLock, logger)
    {
        protected override string Operation => "ui search";

        /// <summary>Searching the element tree is a background-safe UIA read.</summary>
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
                UiErrors.MissingSelector(logger, "search", json);
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
            var maxResults = parseResult.GetRequiredValue(SharedUiOptions.MaxResultsOption);

            try
            {
                var uiTarget = await targetResolver.ResolveAsync(app, window, cancellationToken);
                var selector = selectorParser.Parse(selectorStr);
                var matches = await uiAutomation.SearchAsync(uiTarget, selector, maxResults + 1, cancellationToken);

                var hasMore = matches.Length > maxResults;
                if (hasMore)
                {
                    matches = matches[..maxResults];
                }

                if (json)
                {
                    // Strip internal/redundant fields — selectors are the stable handle for consumers.
                    // Also flattens InvokableAncestor to a hint (no cycle, no nested children).
                    UiElementScrubber.ScrubAll(matches);
                    var result = new UiSearchResult
                    {
                        MatchCount = matches.Length,
                        HasMore = hasMore,
                        Matches = matches
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiSearchResult));
                }
                else
                {
                    foreach (var el in matches)
                    {
                        var elSelector = el.Selector ?? el.Id ?? "";
                        var displayName = el.Name ?? el.AutomationId;
                        var name = displayName is not null && displayName != elSelector
                            ? $" [green]\"{EscapeMarkup(Truncate(displayName, 80))}\"[/]" : "";
                        var value = el.Value is not null && el.Value != el.Name
                            ? $" [yellow]value=\"{EscapeMarkup(Truncate(el.Value, 60))}\"[/]" : "";
                        var toggle = el.ToggleState is not null ? $" [grey][[{el.ToggleState}]][/]" : "";
                        var expand = el.ExpandState is not null ? $" [grey][[{el.ExpandState}]][/]" : "";
                        var scroll = el.ScrollDir is not null ? $" [grey][[scroll:{el.ScrollDir}]][/]" : "";
                        var bounds = el.Width > 0 ? $" [grey]({el.X},{el.Y} {el.Width}x{el.Height})[/]" : "";
                        ansiConsole.MarkupLine($"  [bold cyan]{EscapeMarkup(elSelector)}[/] {el.Type}{name}{value}{toggle}{expand}{scroll}{bounds}");

                        if (el.InvokableAncestor is { } ancestor)
                        {
                            var ancestorSel = ancestor.Selector ?? ancestor.Id ?? "";
                            var aName = ancestor.Name is not null ? $" \"{ancestor.Name}\"" : "";
                            ansiConsole.MarkupLine($"        ^ invoke via: [bold cyan]{EscapeMarkup(ancestorSel)}[/]{aName}");
                        }
                    }
                }

                if (!json)
                {
                    var moreText = hasMore ? $" (showing first {maxResults})" : "";
                    logger.LogInformation("Found {Count} matches{MoreText}", matches.Length, moreText);
                }
                return matches.Length > 0 ? 0 : 1;
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

        private static string EscapeMarkup(string text) => Markup.Escape(text.Replace("\r\n", "↵").Replace("\r", "↵").Replace("\n", "↵").Replace("\t", "→"));

        private static string Truncate(string text, int maxLength)
        {
            if (text.Length <= maxLength) { return text; }
            return string.Concat(text.AsSpan(0, maxLength), "…");
        }
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services.Controls;
using WinApp.Cli.Services.Learn;

namespace WinApp.Cli.Commands;

internal sealed class AskCommand : Command, IShortDescription
{
    public string ShortDescription => "Search WinUI samples and Microsoft Learn docs for a Windows dev question";

    public static Argument<string> QuestionArgument { get; } = new("question")
    {
        Description = "A Windows development question, e.g. \"how do I add a jump list?\". Sent to Microsoft Learn.",
        Arity = ArgumentArity.ExactlyOne
    };

    public AskCommand() : base("ask",
        "Search WinUI samples and Microsoft Learn documentation together. Shows up to three sample matches, then up to three docs with links and short excerpts; it does not generate an answer. Every question is sent to the public Microsoft Learn service. Docs search has a three-second timeout; if it fails, sample results are kept and the failure is reported. Use find-ui to search samples without sending your question to Learn.")
    {
        Arguments.Add(QuestionArgument);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(
        IControlsSearchService searchService,
        ILearnSearchClient learnClient,
        IAnsiConsole console,
        ILogger<AskCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var question = parseResult.GetValue(QuestionArgument);
            if (string.IsNullOrWhiteSpace(question))
            {
                const string message = "Provide a question, e.g. winapp ask \"how do I add a jump list?\".";
                if (json)
                {
                    return JsonErrorOutput.Write(console, message);
                }
                logger.LogError("{Message}", message);
                return 1;
            }
            cancellationToken.ThrowIfCancellationRequested();

            var output = new AskJsonOutput { Query = question, Matches = [], Docs = [] };
            List<SearchEngine.GroupedResult> groups = [];
            try
            {
                var engine = await searchService.GetEngineAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                groups = engine.SearchGrouped(question, maxControls: 3, maxScenariosPerControl: 3);
                output.Corpus = FindUiCommand.Handler.CorpusLabel(searchService.LoadedOrigin);
                output.Matches = FindUiCommand.ToJsonMatches(groups);
            }
            catch (Exception ex) when (ex is ControlsDataUnavailableException or IOException)
            {
                logger.LogDebug(ex, "ask could not load WinUI samples");
                output.SamplesError = "WinUI samples are unavailable. Run winapp find-ui --list --refresh while online to reload them.";
            }

            if (!json)
            {
                console.MarkupLine("[bold]WinUI samples[/]");
                if (output.SamplesError is not null)
                {
                    console.MarkupLine($"[yellow]{Markup.Escape(output.SamplesError)}[/]");
                }
                else if (groups.Count == 0)
                {
                    console.WriteLine("No WinUI samples matched.");
                }
                else
                {
                    if (searchService.LoadedOrigin == CorpusOrigin.Embedded && logger.IsEnabled(LogLevel.Information))
                    {
                        console.MarkupLine("[grey]Using samples built into the CLI; they may lag upstream.[/]");
                    }
                    FindUiCommand.WriteMatches(console, groups);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var docs = await learnClient.SearchAsync(question, cancellationToken).ConfigureAwait(false);
                output.Docs = docs.Select(d => new AskDocJson
                {
                    Title = d.Title, Url = d.Url.AbsoluteUri, Excerpt = d.Excerpt
                }).ToList();
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug(ex, "ask documentation search timed out");
                output.DocsError = DocsTimeoutMessage;
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or McpException or IOException or InvalidDataException or JsonException)
            {
                logger.LogDebug(ex, "ask documentation search failed");
                output.DocsError = ex is TimeoutException ? DocsTimeoutMessage
                    : "Docs unavailable: Microsoft Learn could not be reached or returned an invalid response. Retry, or use winapp find-ui to search samples without Learn.";
            }
            cancellationToken.ThrowIfCancellationRequested();

            if (json)
            {
                console.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(output, WinAppJsonContext.Default.AskJsonOutput));
            }
            else
            {
                console.WriteLine();
                console.MarkupLine("[bold]Docs[/]");
                if (output.DocsError is not null)
                {
                    console.MarkupLine($"[yellow]{Markup.Escape(output.DocsError)}[/]");
                }
                else if (output.Docs.Count == 0)
                {
                    console.WriteLine("No docs matched.");
                }
                else
                {
                    foreach (var doc in output.Docs)
                    {
                        console.MarkupLine($"[bold cyan]{Markup.Escape(doc.Title)}[/]");
                        console.Profile.Out.Writer.WriteLine(doc.Url);
                        console.WriteLine(doc.Excerpt);
                        console.WriteLine();
                    }
                }
            }
            return output.Matches.Count > 0 || output.Docs.Count > 0 ? 0 : 1;
        }

        private const string DocsTimeoutMessage =
            "Docs unavailable: Microsoft Learn timed out. Retry, or use winapp find-ui to search samples without Learn.";
    }
}

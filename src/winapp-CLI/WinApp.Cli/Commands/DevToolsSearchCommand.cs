// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Commands;

internal class DevToolsSearchCommand : DevToolsLiveCommand
{
    public override string ShortDescription => "Find elements in a running app's XAML visual tree";

    public static Argument<string?> QueryArgument { get; } = new("query")
    {
        Description = "Match text content, type, x:Name, or source file (case-insensitive).",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public static Option<int> MaxOption { get; } = new("--max")
    {
        Description = "Maximum matches to print (default 50).",
        DefaultValueFactory = _ => 50,
    };

    public DevToolsSearchCommand()
        : base("search", "Find live XAML elements and print their reusable selectors.")
    {
        Arguments.Add(QueryArgument);
        Options.Add(MaxOption);
        Options.Add(SharedDevToolsOptions.AllOption);
        DevToolsQueryOptions.Add(this, fields: true);
    }

    public class Handler(
        IDevToolsTargetResolver resolver,
        IAnsiConsole ansiConsole) : LiveHandler(resolver, ansiConsole)
    {
        protected override Task<int> RunAsync(
            DevToolsTarget target,
            ParseResult parseResult,
            bool json,
            CancellationToken cancellationToken)
        {
            var query = parseResult.GetValue(QueryArgument) ?? string.Empty;
            var max = parseResult.GetRequiredValue(MaxOption);
            var all = parseResult.GetValue(SharedDevToolsOptions.AllOption);

            if (string.IsNullOrWhiteSpace(query) && !DevToolsQueryOptions.HasCriteria(parseResult))
            {
                return Task.FromResult(Fail(json, target.Pid, "Provide a query, e.g. `winapp devtools search Button`."));
            }

            if (max <= 0)
            {
                return Task.FromResult(Fail(json, target.Pid, "--max must be 1 or more."));
            }
            if (DevToolsQueryOptions.HasReadQuery(parseResult))
            {
                return Task.FromResult(DevToolsQueryOptions.Run(target, parseResult, Console, json, cancellationToken,
                    authored: !all, text: query, max: max));
            }

            var appAuthored = !all;
            var (result, error) = DevToolsSelector.Find(target.Tap!, query, appAuthored, cancellationToken);
            if (error is not null)
            {
                return Task.FromResult(Fail(json, target.Pid, error));
            }

            // Nothing of the app's own XAML matched: search the whole tree before giving up. Items created from
            // data or code (a NavigationViewItem added from a model list) have no XAML source of their own.
            string? fallback = null;
            if (appAuthored && result!.Matches.Count == 0)
            {
                var why = result.ExplainIfNothingAuthored();
                appAuthored = false;
                (result, error) = DevToolsSelector.Find(target.Tap!, query, appAuthoredOnly: false, cancellationToken);
                if (error is not null)
                {
                    return Task.FromResult(Fail(json, target.Pid, error));
                }
                fallback = why ?? (result!.Matches.Count > 0
                    ? $"Nothing in your XAML matches \"{query}\"; these are generated or framework elements, such as items created from data."
                    : null);
            }

            var matches = result!.Matches;
            var shown = matches.Count > max ? matches.Take(max).ToArray() : matches;

            // ONE verdict for both shapes. `--json` used to emit ok:true with matchCount:0 while the human
            // path (and the exit code) called the same run a failure.
            var found = matches.Count > 0;

            if (json)
            {
                WriteJson(DevToolsJson.Serialize(writer =>
                {
                    writer.WriteBoolean("ok", found);
                    writer.WriteNumber("processId", target.Pid);
                    writer.WriteString("query", query);
                    writer.WriteBoolean("appAuthored", appAuthored);
                    if (fallback is not null)
                    {
                        writer.WriteBoolean("fallback", true);
                        writer.WriteString("fallbackReason", fallback);
                    }

                    writer.WriteNumber("matchCount", matches.Count);
                    writer.WriteBoolean("hasMore", matches.Count > shown.Count);
                    writer.WriteNumber("censusNodes", result.CensusNodes);
                    writer.WriteBoolean("truncated", result.Truncated);
                    if (appAuthored)
                    {
                        writer.WriteBoolean("classificationTruncated", result.ClassificationTruncated);
                    }
                    if (!found)
                    {
                        // The same sentence the human path prints, so a --json caller is told WHY rather than
                        // being left to infer it from an empty array.
                        DevToolsJson.WriteError(writer, "no-match", DescribeNoMatches(target, query, appAuthored, result.ClassificationTruncated));
                    }

                    writer.WriteStartArray("matches");
                    foreach (var match in shown)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("selector", match.Selector);
                        writer.WriteString("handle", match.Handle);
                        if (match.Id is not null)
                        {
                            writer.WriteString("id", match.Id);
                        }

                        if (match.Name.Length > 0)
                        {
                            writer.WriteString("name", match.Name);
                            writer.WriteBoolean("uniqueName", match.UniqueName);
                        }

                        writer.WriteString("type", match.Type);
                        if (match.File is not null)
                        {
                            writer.WriteString("file", match.File);
                        }

                        writer.WriteNumber("depth", match.Depth);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }));
                return Task.FromResult(found ? 0 : 1);
            }

            if (fallback is not null)
            {
                Console.MarkupLineInterpolated($"{UiSymbols.Warning} {fallback}");
                Console.WriteLine();
            }

            foreach (var match in shown)
            {
                DevToolsRender.WriteMarkupLine(Console, DevToolsRender.Match(match));
            }

            Console.WriteLine();
            if (!found)
            {
                Console.MarkupLineInterpolated($"[yellow]{DescribeNoMatches(target, query, appAuthored, result.ClassificationTruncated)}[/]");
                return Task.FromResult(1);
            }

            var more = matches.Count > shown.Count ? $" (showing the first {shown.Count})" : string.Empty;
            var scope = appAuthored ? " match(es) in your XAML" : " match(es)";
            Console.MarkupLine($"[grey]Found {matches.Count}{scope}{more}.[/]");
            if (appAuthored)
            {
                Console.MarkupLine("[grey]Framework and control-template elements are not searched — pass --all to include them.[/]");
            }

            // The census walk the search runs over is itself node-capped. Saying so is the difference between
            // "there are no more" and "we did not look at everything".
            if (result.Truncated)
            {
                Console.MarkupLine(
                    $"{UiSymbols.Warning} The search reached its element limit; some elements were not examined.");
            }

            // A SECOND, independent incompleteness: the classifier resolves missing source info in bounded
            // batches, so on a freshly attached app some of the app's own elements are not classified YET and
            // are absent from this scope. Left unsaid, a partial answer reads as the whole truth and a second
            // run mysteriously finds more.
            if (result.ClassificationTruncated)
            {
                Console.MarkupLine(
                    $"{UiSymbols.Warning} Source detection is incomplete, so some of your " +
                    "own elements may be missing. Run the command again to let it finish, or pass --all.");
            }

            return Task.FromResult(0);
        }

        /// <summary>
        /// Why a search found nothing, once the scope itself was viable. An unfinished classification is
        /// stated HERE as well as in the footer, because the footer is not reached on an empty result — and
        /// an empty answer is exactly when "the classifier has not looked at everything yet" is the fact that
        /// explains it. Without it, a fresh attach reports "no element matches" and the next run finds one.
        /// </summary>
        private static string DescribeNoMatches(
            DevToolsTarget target, string query, bool appAuthored, bool classificationTruncated)
        {
            var scope = appAuthored
                ? " in your XAML — pass --all to search framework and template elements too"
                : string.Empty;
            var why = $"No element matches \"{query}\" in {target.Describe()}{scope}. " +
                      "Matching covers type, x:Name, source file, and displayed text.";
            return classificationTruncated
                ? why + " The agent has also not finished classifying this app's XAML, so run the command " +
                        "again in a moment."
                : why;
        }
    }
}

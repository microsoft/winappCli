// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Commands;

internal class DevToolsSearchCommand : DevToolsLiveCommand, IHelpExamples
{
    public override string ShortDescription => "Find elements in a running app's XAML or Reactor tree";

    public IReadOnlyList<string> Examples { get; } =
    [
        "winapp devtools search <text> -a <app>",
        "winapp devtools search --of-type TextBlock --fields Text,FontSize -a <app>",
        "winapp devtools search --component Counter -a <app>",
    ];

    public static Argument<string?> QueryArgument { get; } = new("query")
    {
        Description = "Match text content, type, x:Name, or source file (case-insensitive). In Reactor apps it also matches component names, element kinds, keys and C# call sites such as App.cs:35, and lists what a path such as <Counter>/Button selects.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public static Option<string?> ComponentOption { get; } = new("--component")
    {
        Description = "List every rendered instance of this Microsoft.UI.Reactor component (for example Counter). With a query, search only inside those instances.",
    };

    public static Option<int> MaxOption { get; } = new("--max")
    {
        Description = "Maximum matches to print (default 50).",
        DefaultValueFactory = _ => 50,
    };

    public DevToolsSearchCommand()
        : base("search", "Find live elements (XAML or Reactor) and print their reusable selectors.")
    {
        Arguments.Add(QueryArgument);
        Options.Add(ComponentOption);
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

            var component = parseResult.GetValue(ComponentOption);
            if (string.IsNullOrWhiteSpace(query) && !DevToolsQueryOptions.HasCriteria(parseResult) && string.IsNullOrWhiteSpace(component))
            {
                return Task.FromResult(Fail(json, target.Pid, "Provide a query, e.g. `winapp devtools search Button`."));
            }

            if (max <= 0)
            {
                return Task.FromResult(Fail(json, target.Pid, "--max must be 1 or more."));
            }
            HashSet<string>? inside = null;
            ReactorDescription? reactor = null;
            IReadOnlyList<ReactorNode>? instances = null;
            if (!string.IsNullOrWhiteSpace(component))
            {
                component = component.Trim().TrimStart('<').TrimEnd('>');
                var described = target.Tap!.RequestReactorDescribe(cancellationToken);
                if (!described.Ok)
                {
                    return Task.FromResult(Fail(json, target.Pid, described.Error!));
                }
                reactor = ReactorDescription.Parse(described.ResultJson);
                if (reactor is not { IsReactor: true })
                {
                    return Task.FromResult(Fail(json, target.Pid, new DevToolsProtocolError(0, reactor?.Token ?? ReactorDescription.UnavailableToken,
                        reactor is { State: "absent" }
                            ? "--component searches Microsoft.UI.Reactor components, but this app does not use Reactor."
                            : reactor?.ExplainUnavailable() ?? "The DevTools agent returned an unreadable Reactor description.")));
                }
                instances = DevToolsSelector.ComponentMatches(reactor, component, StringComparison.OrdinalIgnoreCase);
                if (instances.Count == 0)
                {
                    return Task.FromResult(Fail(json, target.Pid, new DevToolsProtocolError(0, reactor.Complete ? "no-match" : "reactor-incomplete",
                        reactor.ExplainNoComponent(component))));
                }
                if (DevToolsQueryOptions.HasReadQuery(parseResult))
                {
                    // --root takes a handle, and only one: name each instance's, so the next command is a copy-paste.
                    var roots = string.Join(" or ", instances.Take(5).Select(instance => $"--root {instance.Handle}"));
                    return Task.FromResult(Fail(json, target.Pid,
                        $"--component cannot be combined with --of-type, --with or --fields. Use a text query, or scope the query to " +
                        $"{(instances.Count == 1 ? "the" : "one")} <{component}> with --root instead of --component: {roots}.",
                        "bad-args"));
                }
                if (string.IsNullOrWhiteSpace(query))
                {
                    var listed = DevToolsSelector.ReactorCandidates(target.Tap!, reactor, instances, cancellationToken);
                    return Task.FromResult(WriteMatches(target, $"<{component}>", listed, max, json, appAuthored: true, fallback: null,
                        new DevToolsSelector.FindResult(listed, listed.Length, listed.Length, false), cancellationToken));
                }
                inside = new HashSet<string>(StringComparer.Ordinal);
                foreach (var roots in instances
                    .Select(instance => target.Tap!.RequestEnumerate(instance.Handle, null, cancellationToken: cancellationToken))
                    .Where(subtree => subtree.Ok).Select(subtree => VisualTreeNode.ParseForest(subtree.ResultJson))
                    .OfType<IReadOnlyList<VisualTreeNode>>())
                {
                    Collect(roots, inside);
                }
            }

            if (DevToolsQueryOptions.HasReadQuery(parseResult))
            {
                return Task.FromResult(DevToolsQueryOptions.Run(target, parseResult, Console, json, cancellationToken,
                    authored: !all, text: query, max: max));
            }

            // A Reactor selector (key=v1, <Counter>/Button, App.cs:35) is not text: list exactly what it selects, in
            // every window, as get-state would resolve it. A text search would let key=v1 match key=v10.
            if (inside is null && ReactorSelector.Parse(query) is { } selector)
            {
                var described = target.Tap!.RequestReactorDescribe(cancellationToken);
                if (described.Ok && ReactorDescription.Parse(described.ResultJson) is { IsReactor: true } description)
                {
                    var selected = selector.Match(description);
                    var listed = selected.Count == 0 ? [] : DevToolsSelector.ReactorCandidates(target.Tap!, description, selected, cancellationToken);
                    return Task.FromResult(WriteMatches(target, query, listed, max, json, appAuthored: true, fallback: null,
                        new DevToolsSelector.FindResult(listed, listed.Length, listed.Length, false), cancellationToken,
                        noMatch: description.Complete
                            ? $"No rendered Reactor element matches {query}. Run `winapp devtools inspect` to list the components and elements that are rendered now."
                            : $"No rendered Reactor element matches {query}, but the app was still changing and not every window answered. Run the command again."));
                }
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
                    ? $"Nothing in {ReactorDescription.YourCode(target.Tap!, cancellationToken)} matches \"{query}\"; these are generated or framework elements, such as items created from data."
                    : null);
            }

            if (inside is not null)
            {
                result = result! with { Matches = [.. result.Matches.Where(match => inside.Contains(match.Handle))] };
            }
            return Task.FromResult(WriteMatches(target, query, result!.Matches, max, json, appAuthored, fallback, result, cancellationToken));
        }

        private static void Collect(IEnumerable<VisualTreeNode> nodes, HashSet<string> handles)
        {
            foreach (var node in nodes)
            {
                handles.Add(node.Handle);
                Collect(node.Children, handles);
            }
        }

        private int WriteMatches(DevToolsTarget target, string query, IReadOnlyList<DevToolsSelector.VisualTreeMatch> matches,
            int max, bool json, bool appAuthored, string? fallback, DevToolsSelector.FindResult result, CancellationToken cancellationToken,
            string? noMatch = null)
        {
            IReadOnlyList<DevToolsSelector.VisualTreeMatch> shown = matches.Count > max ? matches.Take(max).ToArray() : matches;
            if (shown.Count > 0)
            {
                var facts = DevToolsPreviews.Fetch(target.Tap!, shown.Select(match => match.Handle), cancellationToken).Facts;
                shown = [.. shown.Select(match => match with { Facts = facts.GetValueOrDefault(match.Handle) })];
            }

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
                        DevToolsJson.WriteError(writer, "no-match", noMatch ?? DescribeNoMatches(target, query, appAuthored, result.ClassificationTruncated));
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
                        if (match.Label is not null)
                        {
                            writer.WriteString("reactor", match.Label);
                        }

                        if (match.Detail is not null)
                        {
                            writer.WriteString("detail", match.Detail);
                        }

                        DevToolsJson.WriteLocation(writer, match.ShortFile, match.Facts);

                        writer.WriteNumber("depth", match.Depth);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }));
                return found ? 0 : 1;
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
                Console.MarkupLineInterpolated($"[yellow]{noMatch ?? DescribeNoMatches(target, query, appAuthored, result.ClassificationTruncated)}[/]");
                return 1;
            }

            var more = matches.Count > shown.Count ? $" (showing the first {shown.Count})" : string.Empty;
            var scope = appAuthored
                ? $" match(es) in {ReactorDescription.YourCode(matches.Any(match => !match.IsReactor && match.ShortFile?.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) == true), matches.Any(match => match.IsReactor))}"
                : " match(es)";
            Console.MarkupLine($"[grey]Found {matches.Count}{scope}{more}.[/]");
            if (appAuthored)
            {
                Console.MarkupLine("[grey]Framework and control-template elements are not searched. Pass --all to include them.[/]");
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

            return 0;
        }

        /// <summary>
        /// Why a search found nothing, once the scope itself was viable. An unfinished classification is
        /// stated HERE as well as in the footer, because the footer is not reached on an empty result, and
        /// an empty answer is exactly when "the classifier has not looked at everything yet" is the fact that
        /// explains it. Without it, a fresh attach reports "no element matches" and the next run finds one.
        /// </summary>
        private static string DescribeNoMatches(
            DevToolsTarget target, string query, bool appAuthored, bool classificationTruncated)
        {
            var scope = appAuthored
                ? $" in {ReactorDescription.YourCode(target.Tap!, CancellationToken.None)}. Pass --all to search framework and template elements too"
                : string.Empty;
            var why = $"No element matches \"{query}\" in {target.Describe()}{scope}. " +
                      "Matching covers type, x:Name, AutomationId, source file, displayed text and, in Reactor apps, component, key and C# call site.";
            return classificationTruncated
                ? why + " The agent has also not finished classifying this app's XAML, so run the command " +
                        "again in a moment."
                : why;
        }
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Commands;

internal class DevToolsInspectCommand : DevToolsLiveCommand, IHelpExamples
{
    public override string ShortDescription => "View a running app's XAML visual tree";

    public IReadOnlyList<string> Examples { get; } =
    [
        "winapp devtools inspect -a <app>",
        "winapp devtools inspect <selector> --depth 2 -a <app>",
        "winapp devtools inspect --filter <text> -a <app>",
    ];

    public static Option<int> DepthOption { get; } = new("--depth", "-d")
    {
        Description = "Levels to expand in your XAML (--all counts framework levels too).",
        DefaultValueFactory = _ => 4,
    };


    public static Option<bool> AncestorsOption { get; } = new("--ancestors")
    {
        Description = "Show the path from the selected element up to the tree root instead of its subtree.",
    };

    public static Argument<string?> SelectorArgument { get; } = new("selector")
    {
        Description = "Element to inspect: the selector printed in brackets, an x:Name, or a handle. Defaults to the whole tree.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public DevToolsInspectCommand()
        : base("inspect", "Show the app's live XAML tree and reusable element selectors.")
    {
        Arguments.Add(SelectorArgument);
        Options.Add(DepthOption);
        Options.Add(SharedDevToolsOptions.AllOption);
        Options.Add(AncestorsOption);
        Options.Add(SharedDevToolsOptions.FilterOption);
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
            var tap = target.Tap!;
            var selector = parseResult.GetValue(SelectorArgument);
            var depth = parseResult.GetRequiredValue(DepthOption);
            var all = parseResult.GetValue(SharedDevToolsOptions.AllOption);
            var ancestors = parseResult.GetValue(AncestorsOption);
            var filter = parseResult.GetValue(SharedDevToolsOptions.FilterOption);

            if (depth <= 0)
            {
                return Task.FromResult(Fail(json, target.Pid, "--depth must be 1 or more."));
            }

            string? rootHandle = null;
            if (!string.IsNullOrWhiteSpace(selector))
            {
                rootHandle = RequireHandle(target, selector, json, string.Empty, out var exitCode, cancellationToken);
                if (rootHandle is null)
                {
                    return Task.FromResult(exitCode);
                }
            }
            else if (ancestors)
            {
                return Task.FromResult(Fail(json, target.Pid, "--ancestors needs a selector: the element to walk up from."));
            }

            if (ancestors)
            {
                if (DevToolsQueryOptions.HasReadQuery(parseResult))
                {
                    return Task.FromResult(Fail(json, target.Pid, "Query options cannot be combined with --ancestors.", "bad-args"));
                }

                return Task.FromResult(RenderAncestors(target, rootHandle!, json, cancellationToken));
            }
            if (DevToolsQueryOptions.HasReadQuery(parseResult))
            {
                return Task.FromResult(DevToolsQueryOptions.Run(target, parseResult, Console, json, cancellationToken,
                    tree: true, rootHandle: rootHandle, depth: depth, authored: !all, text: filter));
            }

            // The tap prunes breadth-first, so a bounded request degrades to a SHALLOWER tree rather than one
            // with whole branches missing. Ask for exactly the depth the user wanted.
            // In the default (authored) view the tap projects onto the app's own XAML BEFORE applying that
            // depth, which is the whole point: filtering an already-depth-limited raw tree can only remove
            // what the depth fetched, and a default depth-4 raw request never reaches an app Button buried
            // under 12 levels of control template.
            var authored = !all;
            var response = tap.RequestEnumerate(rootHandle, depth, authored, cancellationToken);
            if (!response.Ok)
            {
                return Task.FromResult(Fail(json, target.Pid, response.Error!));
            }

            IReadOnlyList<VisualTreeNode>? roots;
            string? fallback = null;
            var classificationTruncated = false;

            if (authored)
            {
                // The authored reply is an envelope: the nodes AND the state of the classifier pass that
                // produced them. Reading that state from a SECOND pass would let a later backfill answer
                // "complete" about a tree the first pass had already rendered incomplete.
                var forest = AuthoredForest.Parse(response.ResultJson);
                if (forest is null)
                {
                    return Task.FromResult(Fail(json, target.Pid, "The DevTools agent returned an unreadable visual tree."));
                }

                classificationTruncated = forest.ClassificationTruncated;
                roots = forest.Roots;

                // Nothing to show is not one fact but three — no source information, not finished
                // classifying, or genuinely all-framework — and the envelope tells them apart. Fall back to
                // the raw tree and say which one it was; an empty view alone reads as "your app has no
                // elements" and sends someone to debug markup that is fine.
                if (forest.ExplainIfEmpty() is string why)
                {
                    fallback = why;
                    authored = false;
                    classificationTruncated = false;
                    response = tap.RequestEnumerate(rootHandle, depth, authored: false, cancellationToken);
                    if (!response.Ok)
                    {
                        return Task.FromResult(Fail(json, target.Pid, response.Error!));
                    }

                    roots = VisualTreeNode.ParseForest(response.ResultJson);
                }
            }
            else
            {
                roots = VisualTreeNode.ParseForest(response.ResultJson);
            }

            if (roots is null)
            {
                return Task.FromResult(Fail(json, target.Pid, "The DevTools agent returned an unreadable visual tree."));
            }

            var normalizedFilter = DevToolsFormat.NormalizeQuery(filter);
            var unfiltered = new VisualTreeSnapshot(roots, depth);
            var returned = unfiltered.Count;
            var kept = FilterForest(roots, normalizedFilter);
            var previews = DevToolsPreviews.Read(target.Tap!, kept, cancellationToken);
            kept = previews.Roots;
            var snapshot = new VisualTreeSnapshot(kept, depth);

            // ONE verdict for both shapes: an empty tree is a failure in the human path, and `--json` used to
            // report ok:true with count:0 and exit 0 for the same run.
            var found = kept.Count > 0;
            var emptyReason = found ? null : DescribeEmpty(normalizedFilter, classificationTruncated,
                unfiltered.DepthLimitedElements > 0 ? depth : null);

            if (json)
            {
                WriteJson(DevToolsJson.Serialize(writer =>
                {
                    writer.WriteBoolean("ok", found);
                    if (previews.Error is not null)
                    {
                        writer.WriteString("previewWarning", previews.Error);
                    }
                    writer.WriteNumber("processId", target.Pid);
                    if (rootHandle is not null)
                    {
                        writer.WriteString("rootHandle", rootHandle);
                    }

                    // A truncated x:Name lookup resolved this root. Without the caveat, JSON hands back an
                    // apparently unambiguous handle that a caller would then write through — routing around
                    // the very refusal the mutation path added.
                    WriteWarning(writer);
                    writer.WriteNumber("depth", depth);
                    writer.WriteNumber("count", snapshot.Count);
                    // What this view IS, so a caller never has to infer it from the element list. `fallback`
                    // is the reason `appAuthored` is false despite not being asked for.
                    writer.WriteBoolean("appAuthored", authored);
                    if (authored)
                    {
                        writer.WriteBoolean("classificationTruncated", classificationTruncated);
                    }

                    if (fallback is not null)
                    {
                        writer.WriteBoolean("fallback", true);
                        writer.WriteString("fallbackReason", fallback);
                    }

                    if (normalizedFilter.Length > 0)
                    {
                        writer.WriteString("filter", normalizedFilter);
                    }

                    writer.WriteNumber("hiddenByFilter", Math.Max(0, returned - snapshot.Count));
                    writer.WriteBoolean("truncated", snapshot.HardTruncated);
                    writer.WriteNumber("depthLimitedElements", snapshot.DepthLimitedElements);
                    if (emptyReason is not null)
                    {
                        DevToolsJson.WriteError(writer, "no-match", emptyReason);
                    }

                    writer.WriteStartArray("elements");
                    foreach (var root in kept)
                    {
                        DevToolsJson.WriteNode(writer, root);
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

            RenderTree(kept);
            if (previews.Error is not null)
            {
                Console.MarkupLineInterpolated($"[yellow]{previews.Error}[/]");
            }
            RenderFooter(snapshot, depth, authored, normalizedFilter, target, returned, emptyReason, classificationTruncated);
            return Task.FromResult(found ? 0 : 1);
        }

        /// <summary>
        /// Why a tree view came back empty once the agent did return one. An unfinished classification is
        /// stated HERE rather than only in the footer, because the footer is not reached on an empty result —
        /// and an empty answer is exactly when "the classifier has not looked at everything yet" is the fact
        /// that explains it.
        /// </summary>
        private static string DescribeEmpty(string filter, bool classificationTruncated, int? cutAtDepth = null)
        {
            var why = filter.Length == 0 ? "The app has no visual tree yet."
                : cutAtDepth is int depth
                    ? $"No elements matched within --depth {depth}; deeper elements were not searched. " +
                      $"Rerun with --depth {depth * 2}, or use `winapp devtools search \"{filter}\"`."
                    : "No elements matched. Drop --filter to see the whole tree.";
            return classificationTruncated
                ? why + " The agent has also not finished classifying this app's XAML, so run the command " +
                        "again in a moment, or pass --all."
                : why;
        }

        private int RenderAncestors(DevToolsTarget target, string handle, bool json, CancellationToken cancellationToken)
        {
            var response = target.Tap!.RequestEnumerate(null, null, cancellationToken: cancellationToken);
            if (!response.Ok)
            {
                return Fail(json, target.Pid, response.Error!);
            }

            var roots = VisualTreeNode.ParseForest(response.ResultJson);
            if (roots is null)
            {
                return Fail(json, target.Pid, "The DevTools agent returned an unreadable visual tree.");
            }

            var path = new List<VisualTreeNode>();
            if (!roots.Any(root => TryFindPath(root, handle, path)))
            {
                return Fail(
                    json,
                    target.Pid,
                    "That element is not in the live visual tree (handles change when the tree rebuilds).");
            }

            if (json)
            {
                WriteJson(DevToolsJson.Serialize(writer =>
                {
                    writer.WriteBoolean("ok", true);
                    writer.WriteNumber("processId", target.Pid);
                    writer.WriteString("handle", handle);
                    WriteWarning(writer);
                    writer.WriteNumber("count", path.Count);
                    writer.WriteStartArray("ancestors");
                    foreach (var node in path)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("selector", node.Selector);
                        writer.WriteString("handle", node.Handle);
                        if (node.Name.Length > 0)
                        {
                            writer.WriteString("name", node.Name);
                        }

                        writer.WriteString("type", node.Type);
                        if (node.File is not null)
                        {
                            writer.WriteString("file", node.File);
                        }

                        writer.WriteNumber("childCount", node.ChildCount);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }));
                return 0;
            }

            for (var i = 0; i < path.Count; i++)
            {
                DevToolsRender.WriteMarkupLine(Console, new string(' ', i * 2) + DevToolsRender.Node(path[i]));
            }

            Console.WriteLine();
            Console.MarkupLineInterpolated($"[grey]{path.Count} element(s) from the root to the selection.[/]");
            return 0;
        }

        private static bool TryFindPath(VisualTreeNode node, string handle, List<VisualTreeNode> path)
        {
            path.Add(node);
            if (node.Handle == handle)
            {
                return true;
            }

            foreach (var child in node.Children)
            {
                if (TryFindPath(child, handle, path))
                {
                    return true;
                }
            }

            path.RemoveAt(path.Count - 1);
            return false;
        }

        /// <summary>
        /// Applies <c>--filter</c>. A node survives when it matches or when a descendant does, so the path
        /// down to a match stays readable instead of the match arriving with no context. Ancestors kept only
        /// for context still print, because dropping them would move an element to a nesting level it does
        /// not occupy.
        /// </summary>
        private static IReadOnlyList<VisualTreeNode> FilterForest(
            IReadOnlyList<VisualTreeNode> roots,
            string normalizedFilter)
        {
            if (normalizedFilter.Length == 0)
            {
                return roots;
            }

            var kept = new List<VisualTreeNode>();
            foreach (var root in roots)
            {
                if (FilterNode(root, normalizedFilter) is VisualTreeNode filtered)
                {
                    kept.Add(filtered);
                }
            }

            return kept;
        }

        private static VisualTreeNode? FilterNode(VisualTreeNode node, string normalizedFilter)
        {
            var children = new List<VisualTreeNode>();
            foreach (var child in node.Children)
            {
                if (FilterNode(child, normalizedFilter) is VisualTreeNode filtered)
                {
                    children.Add(filtered);
                }
            }

            if (!DevToolsFormat.Matches(node, normalizedFilter) && children.Count == 0)
            {
                return null;
            }

            // childCount stays the element's TRUE child count in this view. Filtering hides children from the
            // display; it does not change how many the element has, and claiming otherwise would turn a
            // filtered view into a false "+more" (or hide a real one).
            return node with { Children = children };
        }

        private void RenderTree(IReadOnlyList<VisualTreeNode> roots)
        {
            foreach (var root in roots)
            {
                RenderNode(root, 0);
            }
        }

        private void RenderNode(VisualTreeNode node, int indent)
        {
            var pad = new string(' ', indent * 2);
            var preview = DevToolsPreviews.Format(node.Preview);
            DevToolsRender.WriteMarkupLine(Console, pad + DevToolsRender.Node(node) +
                (preview.Length == 0 ? "" : " " + Markup.Escape(preview)));
            foreach (var child in node.Children)
            {
                RenderNode(child, indent + 1);
            }

            if (node.HiddenChildren > 0)
            {
                Console.MarkupLineInterpolated($"{pad}  [grey]+{node.HiddenChildren} more[/]");
            }
        }

        private void RenderFooter(
            VisualTreeSnapshot snapshot,
            int depth,
            bool authored,
            string filter,
            DevToolsTarget target,
            int returnedByAgent,
            string? emptyReason,
            bool classificationTruncated)
        {
            Console.WriteLine();
            if (emptyReason is not null)
            {
                Console.MarkupLineInterpolated($"[yellow]{emptyReason}[/]");
                return;
            }

            var flags = new List<string> { $"--depth {depth}" };
            flags.Add(authored ? "your XAML" : "all elements");
            if (filter.Length > 0)
            {
                flags.Add($"filter \"{filter}\"");
            }

            Console.MarkupLineInterpolated(
                $"[grey]Found {snapshot.Count} element(s) in {target.Describe()} ({string.Join(", ", flags)}).[/]");

            // Elements the CLI's own filter removed, stated separately from the agent's "+more": a reader who
            // sees a parent with fewer children than it has should know which of the two did it.
            var hiddenByFilter = returnedByAgent - snapshot.Count;
            if (hiddenByFilter > 0)
            {
                Console.MarkupLineInterpolated(
                    $"[grey]{hiddenByFilter} element(s) hidden by the filter, not by the agent.[/]");
            }

            if (snapshot.DepthLimitedElements > 0)
            {
                Console.MarkupLineInterpolated(
                    $"[grey]{snapshot.DepthLimitedElements} element(s) have children beyond this depth — rerun with --depth {depth * 2} to expand.[/]");
            }

            if (authored)
            {
                Console.MarkupLine("[grey]Framework and control-template elements are hidden — pass --all to see them.[/]");
            }

            // A SECOND, independent incompleteness from the agent's node limit below: the classifier resolves
            // missing source info in bounded batches, so on a freshly attached app some of the app's OWN
            // elements are not classified yet and are simply absent from this view. Left unsaid, a partial
            // answer reads as the whole truth and a second run mysteriously shows more.
            if (classificationTruncated)
            {
                Console.MarkupLine(
                    $"{UiSymbols.Warning} The agent has not finished classifying this app's XAML, so some of your " +
                    "own elements may be missing. Run the command again to let it finish, or pass --all.");
            }

            // Hard truncation is the tap's node-walk limit, not the depth this command asked for: a bigger
            // --depth will NOT reveal these. Say that, rather than suggesting a flag that cannot work.
            if (snapshot.HardTruncated)
            {
                Console.MarkupLine(
                    $"{UiSymbols.Warning} The agent's node limit truncated this walk, so the tree above is incomplete. " +
                    "Inspect a subtree by selector to see the rest.");
            }
        }
    }
}

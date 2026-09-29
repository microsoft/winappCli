// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Services;
using WinApp.Cli.Services.DevTools;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Commands;

internal class DevToolsCommentsListCommand : Command, IShortDescription
{
    public string ShortDescription => "List saved UI comments and current source matches";

    public static Option<string?> StatusOption { get; } = new("--status") { Description = "Filter by status: open | resolved | stale | dismissed (default: open for human output; all statuses with --json)." };

    public static Option<bool> AllOption { get; } = new("--all") { Description = "Include all statuses (the default for --json)." };

    public static Option<string?> ProjectOption { get; } = new("--project") { Description = "Filter by the captured project root." };

    public DevToolsCommentsListCommand()
        : base("list", "List locally saved UI comments and their current source matches; choose the project with --source-root.")
    {
        Options.Add(StatusOption);
        Options.Add(AllOption);
        Options.Add(ProjectOption);
        Options.Add(CommentsSharedOptions.SourceRootOption);
        Options.Add(CommentsSharedOptions.AppTitleOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        ICommentStore store,
        ICommentAnchorResolver resolver,
        ICurrentDirectoryProvider currentDirectory,
        IAnsiConsole ansiConsole,
        ILogger<DevToolsCommentsListCommand> logger) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var status = parseResult.GetValue(StatusOption);
            var all = parseResult.GetValue(AllOption);
            var project = DevToolsCommentsAddCommand.Nullify(parseResult.GetValue(ProjectOption));

            if (status is not null && !CommentStatus.IsValid(status))
            {
                return Task.FromResult(DevToolsCommentsAddCommand.Fail(ansiConsole, json,
                    $"Invalid --status '{status}'. Allowed: {string.Join(", ", CommentStatus.All)}."));
            }

            if (status is not null && all)
            {
                return Task.FromResult(DevToolsCommentsAddCommand.Fail(ansiConsole, json,
                    "--status and --all are alternatives; pass one."));
            }

            var sourceRoot = parseResult.GetValue(CommentsSharedOptions.SourceRootOption) ?? currentDirectory.GetCurrentDirectory();
            var appTitle = parseResult.GetValue(CommentsSharedOptions.AppTitleOption);

            try
            {
                var location = store.Locate(sourceRoot);

                // A miss must never look like an empty store: "No comments." is indistinguishable from "your
                // store is one directory up".
                if (!store.Exists(location.StorePath))
                {
                    return Task.FromResult(NoStore(ansiConsole, json, location));
                }

                var doc = store.Load(location.StorePath);
                var inProject = project is null
                    ? doc.Comments
                    : doc.Comments.FindAll(c => CommentStoreLocator.SameProject(c.ProjectRoot, project));

                // --json is the agent contract: an explicit --status filters it, but the OPEN-BY-DEFAULT filter
                // does not. An agent pulling the backlog wants every row with its status attached, and silently
                // dropping rows from a machine-readable payload is the same defect in a worse place.
                var selected = status is not null
                    ? inProject.FindAll(c => c.Status == status)
                    : all || json ? inProject : inProject.FindAll(c => c.Status == CommentStatus.Open);

                var payload = CommentViewBuilder.BuildPayload(selected, resolver, appTitle, sourceRoot, location.StorePath);

                if (json)
                {
                    ansiConsole.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(payload, CommentsJsonContext.Output.CommentsListPayload));
                    return Task.FromResult(0);
                }

                if (payload.Comments.Count == 0)
                {
                    DevToolsRender.WriteMarkupLine(ansiConsole, $"[grey]No comments in {Markup.Escape(location.StorePath)}.[/]");
                    WriteHiddenFootnote(ansiConsole, inProject, selected);
                    return Task.FromResult(0);
                }

                foreach (var c in payload.Comments)
                {
                    var id = c.Anchor.Identity;
                    var typeName = string.IsNullOrEmpty(id.Type) ? "element" : id.Type;
                    var name = string.IsNullOrEmpty(id.Name) ? string.Empty : $" [blue]#{Markup.Escape(id.Name!)}[/]";
                    var where = c.Hits.Count > 0
                        ? $" [grey]({(c.AnchorConfirmed ? "current" : "candidate")}: {Markup.Escape(c.Hits[0].File)}:{c.Hits[0].Line})[/]"
                        : string.Empty;
                    DevToolsRender.WriteMarkupLine(ansiConsole, $"[cyan]{Markup.Escape(c.Id)}[/] [grey]{c.Status}[/]  [teal]{Markup.Escape(typeName)}[/]{name}{where}");
                    if (!string.IsNullOrWhiteSpace(c.ProjectRoot))
                    {
                        DevToolsRender.WriteMarkupLine(ansiConsole, $"   [grey]Project root: {Markup.Escape(c.ProjectRoot)}[/]");
                    }
                    DevToolsRender.WriteMarkupLine(ansiConsole, $"   {Markup.Escape(c.Text)}");
                    if (CommentViewBuilder.HistoricalLocation(c) is { } historical)
                    {
                        DevToolsRender.WriteMarkupLine(ansiConsole, $"   [grey]Created at (historical): {Markup.Escape(historical)}[/]");
                    }
                    if (c.Resolution?.Note is { Length: > 0 } note)
                    {
                        DevToolsRender.WriteMarkupLine(ansiConsole, $"   [grey]{CommentsSharedOptions.NoteLabel(c.Status)}: {Markup.Escape(note)}[/]");
                    }

                    var candidates = CommentViewBuilder.AmbiguousCandidates(c);
                    if (candidates.Count > 1)
                    {
                        DevToolsRender.WriteMarkupLine(ansiConsole, $"   [yellow]⚠ {Markup.Escape(CommentViewBuilder.CandidatesReason(c))} — {candidates.Count} candidates:[/] [grey]{Markup.Escape(string.Join(", ", candidates))}[/]");
                    }
                    else if (CommentViewBuilder.AnchorHealthWarning(c) is string warning)
                    {
                        DevToolsRender.WriteMarkupLine(ansiConsole, $"   [yellow]⚠ {Markup.Escape(warning)}[/]");
                    }
                }

                WriteHiddenFootnote(ansiConsole, inProject, selected);
                logger.LogDebug("Listed {Count} comments from {Store}", payload.Comments.Count, location.StorePath);
                return Task.FromResult(0);
            }
            catch (Exception ex)
            {
                return Task.FromResult(DevToolsCommentsAddCommand.Fail(ansiConsole, json, ex.Message));
            }
        }

        private static void WriteHiddenFootnote(IAnsiConsole ansiConsole, List<Comment> all, List<Comment> shown)
        {
            var breakdown = all.Except(shown).GroupBy(c => c.Status)
                .OrderBy(group => Array.IndexOf(CommentStatus.All, group.Key))
                .Select(group => $"{group.Count()} {group.Key}").ToList();
            if (breakdown.Count == 0)
            {
                return;
            }

            var noun = all.Count == 1 ? "comment" : "comments";
            DevToolsRender.WriteMarkupLine(ansiConsole, $"[grey]{all.Count} {noun} total ({Markup.Escape(string.Join(", ", breakdown))} hidden; use --all).[/]");
        }

        private static int NoStore(IAnsiConsole ansiConsole, bool json, CommentStoreLocation location)
        {
            if (json)
            {
                var payload = new CommentsListPayload { App = new CommentAppInfo { SourceRoot = location.StartDirectory, StorePath = location.StorePath } };
                ansiConsole.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(payload, CommentsJsonContext.Output.CommentsListPayload));
                return 0;
            }

            DevToolsRender.WriteMarkupLine(ansiConsole, $"[grey]{Markup.Escape(CommentsSharedOptions.NoStoreMessage(location))}[/]");
            return 0;
        }
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;
using System.Text.Json;
using Spectre.Console;
using WinApp.Cli.Services;
using WinApp.Cli.Services.DevTools;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Commands;

internal class DevToolsCommentsGetCommand : Command, IShortDescription, IHelpExamples
{
    public string ShortDescription => "Show one UI comment and current source matches";

    public IReadOnlyList<string> Examples { get; } =
    [
        "winapp devtools comments get <id>",
    ];

    public static Argument<string> IdArgument { get; } = new("id") { Description = "The comment id." };

    public DevToolsCommentsGetCommand()
        : base("get", "Show one locally saved UI comment and its current source matches; choose the project with --source-root.")
    {
        Arguments.Add(IdArgument);
        Options.Add(CommentsSharedOptions.ReadAppOption);
        Options.Add(CommentsSharedOptions.SourceRootOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        ICommentStore store,
        ICommentAnchorResolver resolver,
        ICurrentDirectoryProvider currentDirectory,
        IAnsiConsole ansiConsole,
        IUiTargetResolver targets) : AsynchronousCommandLineAction
    {
        internal Func<uint, CancellationToken, string?> ReadSourceRoot { get; init; } = CommentsSharedOptions.ReadTapSourceRoot;

        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var id = parseResult.GetValue(IdArgument);
            if (!CommentsSharedOptions.TryReadAppRoot(targets, parseResult.GetValue(CommentsSharedOptions.ReadAppOption), ReadSourceRoot,
                cancellationToken, out var appRoot, out var appError))
            {
                return Task.FromResult(DevToolsCommentsAddCommand.Fail(ansiConsole, json, appError!));
            }
            var sourceRoot = parseResult.GetValue(CommentsSharedOptions.SourceRootOption) ?? appRoot ?? currentDirectory.GetCurrentDirectory();

            if (string.IsNullOrWhiteSpace(id))
            {
                return Task.FromResult(DevToolsCommentsAddCommand.Fail(ansiConsole, json, "A comment id is required."));
            }

            try
            {
                var location = store.Locate(sourceRoot);
                var comment = store.Get(location.StorePath, id);
                if (comment is null)
                {
                    return Task.FromResult(DevToolsCommentsAddCommand.Fail(ansiConsole, json, CommentsSharedOptions.NotFoundMessage(id, location)));
                }

                var view = CommentViewBuilder.ToView(comment, resolver, sourceRoot);
                if (json)
                {
                    var payload = new CommentResultPayload { Ok = true, Comment = view };
                    ansiConsole.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(payload, CommentsJsonContext.Output.CommentResultPayload));
                }
                else
                {
                    DevToolsRender.WriteMarkupLine(ansiConsole, $"[cyan]{Markup.Escape(view.Id)}[/] [grey]{view.Status}[/]");
                    DevToolsRender.WriteMarkupLine(ansiConsole, $"  {Markup.Escape(view.Text)}");
                    if (view.Resolution?.Note is { Length: > 0 } note)
                    {
                        DevToolsRender.WriteMarkupLine(ansiConsole, $"  [grey]{CommentsSharedOptions.NoteLabel(view.Status)}: {Markup.Escape(note)}[/]");
                    }
                    var idn = view.Anchor.Identity;
                    DevToolsRender.WriteMarkupLine(ansiConsole, $"  [teal]{Markup.Escape(idn.Type ?? "element")}[/] {Markup.Escape(idn.Name ?? string.Empty)}");
                    if (CommentViewBuilder.HistoricalLocation(view) is { } historical)
                    {
                        DevToolsRender.WriteMarkupLine(ansiConsole, $"  [grey]Created at (historical): {Markup.Escape(historical)}[/]");
                    }
                    if (view.Anchor.Authored is { } authored)
                    {
                        DevToolsRender.WriteMarkupLine(ansiConsole, $"  [grey]Captured declaration:[/] {Markup.Escape(authored.Declaration)}");
                    }
                    if (idn.Content is { Length: > 0 } content)
                    {
                        DevToolsRender.WriteMarkupLine(ansiConsole, $"  [grey]Captured runtime text (context):[/] {Markup.Escape(content)}");
                    }
                    foreach (var h in view.Hits)
                    {
                        var label = view.AnchorConfirmed ? "Current match" : "Candidate";
                        DevToolsRender.WriteMarkupLine(ansiConsole, $"  [grey]{label}: {Markup.Escape(h.File)}:{h.Line}:{h.Column} ({Markup.Escape(h.Confidence)}, via {Markup.Escape(h.Via)})[/] {Markup.Escape(h.Text)}");
                    }

                    var candidates = CommentViewBuilder.AmbiguousCandidates(view);
                    if (candidates.Count > 1)
                    {
                        DevToolsRender.WriteMarkupLine(ansiConsole, $"  [yellow]⚠ {Markup.Escape(CommentViewBuilder.CandidatesReason(view))} — {candidates.Count} candidates:[/] [grey]{Markup.Escape(string.Join(", ", candidates))}[/]");
                    }
                    else if (CommentViewBuilder.AnchorHealthWarning(view) is string warning)
                    {
                        DevToolsRender.WriteMarkupLine(ansiConsole, $"  [yellow]⚠ {Markup.Escape(warning)}[/]");
                    }
                }

                return Task.FromResult(0);
            }
            catch (Exception ex)
            {
                return Task.FromResult(DevToolsCommentsAddCommand.Fail(ansiConsole, json, ex.Message));
            }
        }
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;
using Spectre.Console;
using WinApp.Cli.Services;
using WinApp.Cli.Services.DevTools;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Commands;

/// <summary>
/// Updates a comment's explicit status and resolution metadata through the atomic store.
/// </summary>
internal class DevToolsCommentsUpdateCommand : Command, IShortDescription
{
    public string ShortDescription => "Update a UI comment's status and note";

    public static Argument<string> IdArgument { get; } = new("id") { Description = "The comment id." };
    public static Option<string> StatusOption { get; } = new("--status") { Description = "New status: open | resolved | stale | dismissed.", Required = true };
    public static Option<string?> NoteOption { get; } = new("--note") { Description = "What changed, or why work is deferred." };
    public static Option<string?> ByOption { get; } = new("--by") { Description = "Who made the update (default: current user)." };

    public static Option<string?> AppOption { get; } = new("--app", "-a")
    {
        Description = "Optional marker refresh target: PID, process name, or window title.",
    };

    public DevToolsCommentsUpdateCommand()
        : base("update", "Update a saved UI comment's status and optional note.")
    {
        Arguments.Add(IdArgument);
        Options.Add(StatusOption);
        Options.Add(NoteOption);
        Options.Add(ByOption);
        Options.Add(AppOption);
        Options.Add(CommentsSharedOptions.SourceRootOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        ICommentStore store,
        ICommentAnchorResolver resolver,
        ICommentPusher pusher,
        IUiTargetResolver targetResolver,
        ICurrentDirectoryProvider currentDirectory,
        IAnsiConsole ansiConsole,
        ILogger<DevToolsCommentsUpdateCommand> logger,
        ExecutionTargets.GuestAgent.GuestCommentContext? guestComments = null) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var id = parseResult.GetValue(IdArgument);
            var status = parseResult.GetValue(StatusOption);
            var note = DevToolsCommentsAddCommand.Nullify(parseResult.GetValue(NoteOption));
            var author = DevToolsCommentsAddCommand.Nullify(parseResult.GetValue(ByOption));
            var by = author ?? Environment.UserName;
            var sourceRoot = parseResult.GetValue(CommentsSharedOptions.SourceRootOption) ?? currentDirectory.GetCurrentDirectory();

            if (string.IsNullOrWhiteSpace(id))
            {
                return Task.FromResult(DevToolsCommentsAddCommand.Fail(ansiConsole, json, "A comment id is required."));
            }
            if (status is null || !CommentStatus.IsValid(status))
            {
                return Task.FromResult(DevToolsCommentsAddCommand.Fail(ansiConsole, json,
                    $"Invalid --status '{status}'. Allowed: open, resolved, stale, dismissed."));
            }

            try
            {
                var app = DevToolsCommentsAddCommand.Nullify(parseResult.GetValue(AppOption));
                uint pid = 0;
                if (app is not null && !DevToolsCommentsAddCommand.TryResolvePid(targetResolver, app, cancellationToken, out pid, out var error))
                {
                    return Task.FromResult(DevToolsCommentsAddCommand.Fail(ansiConsole, json, error!));
                }
                var location = store.Locate(sourceRoot);
                cancellationToken.ThrowIfCancellationRequested();
                var updated = store.Update(location.StorePath, id, c =>
                {
                    var wasResolved = c.Status == CommentStatus.Resolved;
                    c.Status = status;
                    if (status == CommentStatus.Resolved)
                    {
                        c.Resolution = new CommentResolution
                        {
                            ResolvedAt = CommentTimestamps.Now(),
                            ResolvedBy = by,
                            Note = note ?? (wasResolved ? c.Resolution?.Note : null),
                        };
                    }
                    else if (note is not null || author is not null)
                    {
                        c.Resolution ??= new CommentResolution();
                        c.Resolution.ResolvedAt = CommentTimestamps.Now();
                        c.Resolution.ResolvedBy = by;
                        if (note is not null)
                        {
                            c.Resolution.Note = note;
                        }
                    }
                });

                if (updated is null)
                {
                    return Task.FromResult(DevToolsCommentsAddCommand.Fail(ansiConsole, json, CommentsSharedOptions.NotFoundMessage(id, location)));
                }

                logger.LogDebug("Updated comment {Id} to {Status}", id, status);

                var warning = CommentsSharedOptions.RefreshViews(pusher, app is null ? null : pid, sourceRoot, location.StorePath,
                    parseResult.GetValue(CommentsSharedOptions.SourceRootOption), cancellationToken);

                Emit(ansiConsole, json, resolver, sourceRoot, updated, "Updated", warning);
                return Task.FromResult(guestComments?.WriterExitCode(warning is not null,
                    updated.Status != status) ?? 0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Task.FromResult(DevToolsCommentsAddCommand.Fail(ansiConsole, json, ex.Message));
            }
        }

        internal static int Emit(IAnsiConsole ansiConsole, bool json, ICommentAnchorResolver resolver, string sourceRoot, Comment updated, string verb, string? warning = null)
        {
            if (json)
            {
                var view = CommentViewBuilder.ToView(updated, resolver, sourceRoot);
                var payload = new CommentResultPayload { Ok = true, Comment = view, Warning = warning };
                ansiConsole.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(payload, CommentsJsonContext.Output.CommentResultPayload));
            }
            else
            {
                DevToolsRender.WriteMarkupLine(ansiConsole, $"[green]{verb}[/] [cyan]{Markup.Escape(updated.Id)}[/] → [grey]{updated.Status}[/]");
                if (warning is not null)
                {
                    DevToolsRender.WriteMarkupLine(ansiConsole, $"[yellow]{Markup.Escape(warning)}[/]");
                }
            }

            return 0;
        }
    }
}

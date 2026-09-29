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
/// <c>winapp devtools comments delete &lt;id&gt;</c> — removes a comment from
/// <c>.winapp/ui-comments.json</c> outright.
///
/// Deliberately NOT <c>resolve</c>: resolve records that a comment was acted on and keeps the row (an agent's
/// work queue needs its history), delete says the comment should never have been there and leaves nothing
/// behind. The store is under the gitignored <c>.winapp</c>, so this is permanent — hence "not found" is an
/// error rather than a silent success, and the removed row is echoed so a caller can see what it destroyed.
/// </summary>
internal class DevToolsCommentsDeleteCommand : Command, IShortDescription
{
    public string ShortDescription => "Delete a UI comment outright (not the same as resolve)";

    public static Argument<string> IdArgument { get; } = new("id") { Description = "The comment id." };

    public static Option<string?> AppOption { get; } = new("--app", "-a")
    {
        Description = "Optional marker refresh target: PID, process name, or window title.",
    };

    public DevToolsCommentsDeleteCommand()
        : base("delete", "Delete a UI comment from .winapp/ui-comments.json permanently.")
    {
        Arguments.Add(IdArgument);
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
        ILogger<DevToolsCommentsDeleteCommand> logger,
        ExecutionTargets.GuestAgent.GuestCommentContext? guestComments = null) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var id = parseResult.GetValue(IdArgument);
            var sourceRoot = parseResult.GetValue(CommentsSharedOptions.SourceRootOption) ?? currentDirectory.GetCurrentDirectory();

            if (string.IsNullOrWhiteSpace(id))
            {
                return Task.FromResult(DevToolsCommentsAddCommand.Fail(ansiConsole, json, "A comment id is required."));
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
                var storePath = location.StorePath;
                cancellationToken.ThrowIfCancellationRequested();
                var removed = store.Delete(storePath, id);
                if (removed is null)
                {
                    return Task.FromResult(DevToolsCommentsAddCommand.Fail(ansiConsole, json, CommentsSharedOptions.NotFoundMessage(id, location)));
                }

                logger.LogDebug("Deleted comment {Id} from {Store}", id, storePath);

                var warning = CommentsSharedOptions.RefreshViews(pusher, app is null ? null : pid, sourceRoot, storePath,
                    parseResult.GetValue(CommentsSharedOptions.SourceRootOption), cancellationToken);

                if (json)
                {
                    var view = CommentViewBuilder.ToView(removed, resolver, sourceRoot);
                    var payload = new CommentResultPayload { Ok = true, Comment = view, Warning = warning };
                    ansiConsole.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(payload, CommentsJsonContext.Output.CommentResultPayload));
                }
                else
                {
                    DevToolsRender.WriteMarkupLine(ansiConsole, $"[green]Deleted[/] [cyan]{Markup.Escape(removed.Id)}[/] → [grey]{Markup.Escape(Path.GetFileName(storePath))}[/]");
                    if (warning is not null)
                    {
                        DevToolsRender.WriteMarkupLine(ansiConsole, $"[yellow]{Markup.Escape(warning)}[/]");
                    }
                }

                return Task.FromResult(guestComments?.WriterExitCode(warning is not null) ?? 0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Task.FromResult(DevToolsCommentsAddCommand.Fail(ansiConsole, json, ex.Message));
            }
        }
    }
}

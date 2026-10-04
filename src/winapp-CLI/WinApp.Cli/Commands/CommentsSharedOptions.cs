// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Commands;

internal static class CommentsSharedOptions
{
    internal const string MarkerRefreshWarning = "Comment changes were saved, but a running app's markers could not be refreshed. Reattach DevTools to refresh them.";

    /// <summary>
    /// After a durable write, refreshes the app named by <c>--app</c> and every other running DevTools app of this
    /// user whose project uses the same store. Returns the warning to report, or null when every view refreshed.
    /// </summary>
    internal static string? RefreshViews(ICommentPusher pusher, uint? appPid, string sourceRoot, string storePath,
        string? sourceRootOverride, CancellationToken cancellationToken)
    {
        var appFailed = appPid is uint pid && pusher.Push(pid, sourceRoot, sourceRootOverride, cancellationToken) is null;
        var (_, failed) = pusher.PushStore(storePath, appPid, cancellationToken);
        return appFailed || failed > 0 ? MarkerRefreshWarning : null;
    }

    // The note is kept across status changes; its label says what it was recorded for.
    internal static string NoteLabel(string status) => status switch
    {
        CommentStatus.Resolved => "Resolution note",
        CommentStatus.Open => "Previous status note",
        _ => "Reason",
    };

    public static Option<string?> SourceRootOption { get; } = new("--source-root")
    {
        Description = "Project directory (default: current directory); comments are stored at the repository root."
    };

    public static Option<string?> AppTitleOption { get; } = new("--app-title")
    {
        Description = "App display name included in the output."
    };

    public static Option<string?> ReadAppOption { get; } = new("--app", "-a")
    {
        Description = "Read the comments of this running DevTools app's project: PID or process name. Default: the current directory's project."
    };

    internal static string? ReadTapSourceRoot(uint pid, CancellationToken cancellationToken) =>
        Services.DevTools.CommentSelectionCapture.ReadCommentRoot(
            new Services.DevTools.VisualTreeTap(pid).GetSourceRoot(cancellationToken));

    /// <summary>Resolves <c>--app</c> to the running app's project directory. Without <c>--app</c>, returns true and null.</summary>
    internal static bool TryReadAppRoot(IUiTargetResolver targets, string? app, Func<uint, CancellationToken, string?> readSourceRoot,
        CancellationToken cancellationToken, out string? root, out string? error)
    {
        root = null;
        if (string.IsNullOrWhiteSpace(app))
        {
            error = null;
            return true;
        }
        if (!DevToolsCommentsAddCommand.TryResolvePid(targets, app, cancellationToken, out var pid, out error))
        {
            return false;
        }
        root = readSourceRoot(pid, cancellationToken);
        if (string.IsNullOrWhiteSpace(root))
        {
            error = $"Process {pid} did not report a project folder. Launch it with 'winapp run <project> --devtools', or pass --source-root.";
            return false;
        }
        return true;
    }

    public static string NoStoreMessage(CommentStoreLocation location)
        => $"No comment store found. Looked for {location.Explain()}. "
           + "Comments are stored at the repository root; pass --source-root <dir> if the app's source lives outside this git working tree.";

    /// <summary>
    /// The message for an id lookup that found nothing — which is "no store here" or "not in this store", never
    /// the ambiguous middle. Both name the file, so the user can see which store was consulted.
    /// </summary>
    public static string NotFoundMessage(string id, CommentStoreLocation location)
        => File.Exists(location.StorePath)
            ? $"Comment '{id}' not found in {location.StorePath}."
            : NoStoreMessage(location);
}

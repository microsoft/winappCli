// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.DevTools.Comments;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

internal sealed record TapComment(
    string Id,
    string Text,
    string? Anchor,
    string Status = CommentStatus.Open,
    string? Element = null,
    string? File = null,
    string? Uri = null,
    int Line = 0,
    string? Revision = null);

/// <summary>
/// Pushes the open comment set into a running <c>--devtools</c> app so its markers and toolbar count cover every
/// persisted comment, not just the ones typed since the overlay attached.
/// </summary>
/// <remarks>
/// The CLI stays the single reader/writer of <c>.winapp/ui-comments.json</c>; the app parses no file and keeps
/// no store. Every call is best-effort: no running app, no tap, or a tap that doesn't answer all mean "nothing
/// to show right now", never a failed command — you can author and hand off comments with the app closed.
/// </remarks>
internal interface ICommentPusher
{
    (int Total, int Placed)? Push(uint pid, string fallbackRoot, string? sourceRootOverride = null,
        CancellationToken cancellationToken = default);

    /// <summary>Refreshes every running DevTools app of this user whose project uses <paramref name="storePath"/>.</summary>
    /// <returns>How many apps were refreshed and how many matched but could not be refreshed.</returns>
    (int Refreshed, int Failed) PushStore(string storePath, uint? skipPid, CancellationToken cancellationToken = default);
}

internal sealed class CommentPusher(ICommentStore store, ILogger<CommentPusher>? logger = null) : ICommentPusher
{
    internal Func<IReadOnlyList<int>> ListTaps { get; init; } = () => DevToolsPipeDiscovery.EnumerateInjectedPids();

    internal Func<uint, CancellationToken, string?> ReadSourceRoot { get; init; } = (pid, cancellationToken) =>
        CommentSelectionCapture.ReadStringResult(new VisualTreeTap(pid).GetSourceRoot(cancellationToken), "sourceRoot");

    public (int Refreshed, int Failed) PushStore(string storePath, uint? skipPid, CancellationToken cancellationToken = default)
    {
        int refreshed = 0, failed = 0;
        foreach (var tapPid in ListTaps())
        {
            var pid = (uint)tapPid;
            if (pid == skipPid)
            {
                continue;
            }
            string? appRoot;
            try
            {
                // Tap pipes are owner-only, so only this user's apps answer; one that exited or never
                // bound a project is simply not a view of this store.
                appRoot = ReadSourceRoot(pid, cancellationToken);
                if (string.IsNullOrWhiteSpace(appRoot) ||
                    !string.Equals(Path.GetFullPath(store.GetStorePath(new DirectoryInfo(appRoot))), Path.GetFullPath(storePath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
                ArgumentException or InvalidOperationException or TimeoutException)
            {
                continue;
            }
            if (Push(pid, appRoot, appRoot, cancellationToken) is null) { failed++; } else { refreshed++; }
        }
        return (refreshed, failed);
    }

    public (int Total, int Placed)? Push(uint pid, string fallbackRoot, string? sourceRootOverride = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tap = new VisualTreeTap(pid);

            // Ask the APP where its sources are before falling back to our own cwd: `winapp run samples/foo`
            // from a repo root would otherwise push against a different .winapp than the one the in-app
            // composer writes through, and the two views of "my comments" would silently disagree.
            var appRoot = sourceRootOverride ??
                CommentSelectionCapture.ReadStringResult(tap.GetSourceRoot(cancellationToken), "sourceRoot");
            var project = string.IsNullOrWhiteSpace(appRoot) ? null : appRoot;
            var root = project ?? fallbackRoot;

            var doc = store.Load(store.GetStorePath(new DirectoryInfo(root)));
            var scoped = new CommentStoreDocument
            {
                Generation = doc.Generation,
                Comments = project is null ? [] :
                    doc.Comments.FindAll(c => CommentStoreLocator.SameProject(c.ProjectRoot, project)),
            };
            var comments = ToTapComments(scoped, store.GetRevision);
            using var reply = JsonDocument.Parse(tap.SetComments(comments, scoped.Generation, cancellationToken: cancellationToken).RequireResult(), TapWireJson.DocumentOptions);
            if (reply.RootElement.ValueKind != JsonValueKind.Object ||
                !reply.RootElement.TryGetProperty("total", out var total) || total.ValueKind != JsonValueKind.Number ||
                !total.TryGetInt32(out var count) ||
                !reply.RootElement.TryGetProperty("placed", out var placed) || placed.ValueKind != JsonValueKind.Number ||
                !placed.TryGetInt32(out var landed) || count != comments.Count || landed < 0 || landed > count)
            {
                throw new InvalidDataException("The DevTools marker response has no valid total/placed counts.");
            }
            return (count, landed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            (logger ?? NullLogger<CommentPusher>.Instance).LogWarning(
                "Comment marker refresh for process {Pid} was cancelled. Persisted comments are unchanged.", pid);
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or CommentStoreCorruptException)
        {
            (logger ?? NullLogger<CommentPusher>.Instance).LogWarning(ex,
                "Could not refresh DevTools comments for process {Pid}. Persisted comments are unchanged.", pid);
            return null;
        }
    }

    internal static List<TapComment> ToTapComments(CommentStoreDocument doc, Func<Comment, string?>? revision = null)
        => doc.Comments
            .FindAll(c => c.Status == CommentStatus.Open)
            .ConvertAll(c => new TapComment(
                c.Id,
                c.Text,
                c.Anchor.ElementPath,
                c.Status,
                DescribeElement(c.Anchor.Identity),
                c.Anchor.SourceFile,
                c.Anchor.SourceUri,
                c.Anchor.Line ?? 0,
                revision?.Invoke(c)));

    private static string DescribeElement(CommentIdentity identity)
    {
        var type = string.IsNullOrWhiteSpace(identity.Type) ? "element" : identity.Type!;
        return string.IsNullOrWhiteSpace(identity.Name) ? type : $"{type} #{identity.Name}";
    }
}

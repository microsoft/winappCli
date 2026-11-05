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
/// to show right now", never a failed command. You can author and hand off comments with the app closed.
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
        CommentSelectionCapture.ReadCommentRoot(new VisualTreeTap(pid).GetSourceRoot(cancellationToken));

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
            // comment writer uses, and the two views of "my comments" would silently disagree.
            var appRoot = sourceRootOverride ??
                CommentSelectionCapture.ReadCommentRoot(tap.GetSourceRoot(cancellationToken));
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
            ResolveSourceAnchors(scoped, comments, pid, root,
                message => (logger ?? NullLogger<CommentPusher>.Instance).LogWarning("{Message}", message), cancellationToken);
            using var reply = JsonDocument.Parse(tap.SetComments(comments, scoped.Generation, ResolvedCount(scoped.Comments),
                cancellationToken: cancellationToken).RequireResult(), TapWireJson.DocumentOptions);
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

    internal static int ResolvedCount(IEnumerable<Comment> comments) => comments.Count(c => c.Status == CommentStatus.Resolved);

    internal static void ResolveSourceAnchors(CommentStoreDocument doc, List<TapComment> comments, uint pid,
        string? sourceRoot, Action<string> diagnostic, CancellationToken cancellationToken = default)
    {
        var tap = new VisualTreeTap(pid);
        // Share the startup wait across all comments in this refresh.
        var loadingDeadline = Environment.TickCount64 + 5000;
        for (var index = 0; index < comments.Count; index++)
        {
            if (!string.IsNullOrEmpty(comments[index].Anchor))
            {
                continue;
            }
            var comment = doc.Comments.Single(c => c.Id == comments[index].Id);
            try
            {
                var (anchor, reason) = ResolveSourceAnchor(comment, pid, tap, sourceRoot, loadingDeadline, cancellationToken);
                if (anchor is not null)
                {
                    // This belongs to this refresh, not to the stored comment or its revision.
                    comments[index] = comments[index] with { Anchor = anchor };
                }
                else if (reason is not null)
                {
                    diagnostic($"Comment '{comment.Id}' remains unplaced: {reason} Persisted comments are unchanged.");
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
                JsonException or System.Xml.XmlException)
            {
                diagnostic($"Comment '{comment.Id}' remains unplaced: {ex.Message} Persisted comments are unchanged.");
            }
        }
    }

    private static (string? Anchor, string? Reason) ResolveSourceAnchor(Comment comment, uint pid, VisualTreeTap tap,
        string? sourceRoot, long loadingDeadline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var anchor = comment.Anchor;
        if (anchor.Authored is not { } authored || anchor.Weak ||
            string.IsNullOrEmpty(anchor.Identity.Name) || sourceRoot is null ||
            !CommentStoreLocator.SameProject(authored.ProjectRoot, comment.ProjectRoot))
        {
            return (null, null);
        }
        if (anchor.Templated || authored.Templated)
        {
            return (null, "A template declaration does not identify one live instance.");
        }
        var relativeSource = CommentAnchorResolver.RelativeSourcePath(authored.SourceFile, root: null);
        var path = relativeSource is null ? null : CommentAnchorResolver.ResolveKnownSourcePath(sourceRoot,
            new CommentAnchor { SourceFile = relativeSource });
        if (path is null || !Path.GetRelativePath(sourceRoot, path).Equals(authored.SourceFile, StringComparison.OrdinalIgnoreCase) ||
            CommentAuthoredIdentity.Read(path, sourceRoot).Count(declaration =>
                CommentAuthoredIdentity.Name(declaration.Element) == anchor.Identity.Name &&
                CommentAuthoredIdentity.Signature(declaration.Element) == authored.Signature &&
                CommentAuthoredIdentity.Scope(declaration.Element) == authored.Scope &&
                CommentAuthoredIdentity.TypeIdentity(declaration.Element) == authored.Type) != 1)
        {
            return (null, "The captured source declaration is missing, changed or ambiguous. Review the source and re-add the comment.");
        }
        var (found, error) = DevToolsSelector.Find(tap, anchor.Identity.Name, appAuthoredOnly: false, cancellationToken);
        if (error is not null)
        {
            return (null, error.Message);
        }
        if (found!.Truncated || found.ClassificationTruncated)
        {
            return (null, "The live search is incomplete. Refresh again after the app finishes loading.");
        }
        var matches = found.Matches.Where(match => match.Name == anchor.Identity.Name).DistinctBy(match => match.Handle).Take(2).ToArray();
        if (matches is not [var match])
        {
            return (null, "The source name does not identify one live element. Keep the intended control visible and refresh again.");
        }
        var capture = CommentSelectionCapture.CaptureElement(pid, match.Handle, cancellationToken);
        while (capture.Element?.Authored?.UniquenessReason == "incomplete-source-census")
        {
            var remaining = loadingDeadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                return (null, "The live search is incomplete. Refresh again after the app finishes loading.");
            }
            Task.Delay((int)Math.Min(250, remaining), cancellationToken).GetAwaiter().GetResult();
            capture = CommentSelectionCapture.CaptureElement(pid, match.Handle, cancellationToken);
        }
        if (capture.Status != CaptureStatus.Ok)
        {
            return (null, capture.Error?.Message ?? "The live element could not be verified.");
        }
        var live = capture.Element!;
        if (live.Authored is not { UniqueInstance: true, Templated: false } current ||
            live.SourceProvenance == "likely-source-line" || string.IsNullOrEmpty(live.ElementPath) ||
            !CommentStoreLocator.SameProject(live.SourceRoot, sourceRoot) ||
            !current.SourceFile.Equals(authored.SourceFile, StringComparison.OrdinalIgnoreCase) ||
            current.Signature != authored.Signature || current.Scope != authored.Scope || current.Type != authored.Type ||
            live.Name != anchor.Identity.Name)
        {
            return (null, "The live element has no unique, current source match. Rebuild and relaunch, then refresh again.");
        }
        return (live.ElementPath, null);
    }

    internal static List<TapComment> ToTapComments(CommentStoreDocument doc, Func<Comment, string?>? revision = null)
        => doc.Comments
            .FindAll(c => c.Status == CommentStatus.Open)
            .ConvertAll(c => new TapComment(
                c.Id,
                c.Text,
                c.Anchor.ElementPath ?? ReactorMarkerAnchor(c.Anchor.CSharp),
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

    /// <summary>
    /// The in-app marker anchor of a C# call site captured without the app (<c>comments add --file App.cs --line 35</c>),
    /// in the format the app gives live captures (DevToolsReactorAnchor.h). The app matches it to the element that
    /// call site created, ignoring the line and column when the app was built from different source.
    /// </summary>
    internal static string? ReactorMarkerAnchor(CommentCSharpAnchor? cs)
    {
        if (cs is null || string.IsNullOrEmpty(cs.SourceFile) || cs.Exact is false)
        {
            return null;
        }
        var anchor = new System.Text.StringBuilder("reactor:");
        void Field(string value) => anchor.Append(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(value);
        // The app names a file outside the project (a referenced project's, a linked one) by its leaf.
        Field(cs.SourceFile.Replace('/', '\\').Split('\\').Contains("..") ? Path.GetFileName(cs.SourceFile) : cs.SourceFile);
        Field(cs.Line.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Field(cs.Column.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Field(cs.Element ?? cs.Factory ?? string.Empty);
        Field(cs.Key ?? string.Empty);
        Field(cs.Component ?? string.Empty);
        Field(cs.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Field("0");
        return anchor.ToString();
    }
}

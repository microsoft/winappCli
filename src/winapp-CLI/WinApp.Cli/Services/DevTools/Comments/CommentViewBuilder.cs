// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.DevTools.Comments;

internal static class CommentViewBuilder
{
    internal static MappedSourceCandidate? FindMappedCandidate(CommentView view,
        ExecutionTargets.Orchestration.GuestSourceManifest sources)
    {
        if (sources.Coordinates is null || view.Anchor.Line is not > 0 || view.Anchor.Column is not > 0 ||
            string.IsNullOrWhiteSpace(view.Anchor.Identity.Type) ||
            !CommentStoreLocator.SameProject(view.ProjectRoot, Path.GetDirectoryName(sources.ProjectPath)))
        {
            return null;
        }
        var resource = view.Anchor.SourceUri ?? view.Anchor.SourceFile ?? "";
        if (resource.StartsWith("ms-appx:///", StringComparison.Ordinal)) { resource = resource[11..]; }
        resource = resource.Replace('/', '\\');
        var files = sources.Coordinates.Where(file => file.Resource.Replace('/', '\\').Equals(resource, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (files.Length != 1) { return null; }
        var map = files[0];
        try
        {
            var root = Path.GetDirectoryName(sources.ProjectPath)!;
            var path = Path.Combine(root, ExecutionTargets.Orchestration.GuestCommentBinding.ValidateRelativeSource(map.Source));
            if (Helpers.PathSafety.HasReparsePointOnPath(path, root)) { return null; }
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (source.Length > ExecutionTargets.Orchestration.GuestSourceSnapshot.MaximumFileBytes ||
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source)) != map.SourceHash) { return null; }
            var line = view.Anchor.Line.Value;
            var elements = map.Elements.Where(element =>
                line >= element.Line && line <= element.EndLine).ToArray();
            if (elements.Length != 1) { return null; }
            var selected = elements[0];
            if (selected.Type != view.Anchor.Identity.Type.Split('.').Last() && selected.RuntimeClass != view.Anchor.Identity.Type ||
                (selected.Name ?? "") != (view.Anchor.Identity.Name ?? "")) { return null; }
            return new(map.Source, selected.Line, selected.Column, "unique-source-line");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    public static CommentView ToView(Comment c, ICommentAnchorResolver resolver, string? sourceRoot)
    {
        // Re-anchor under the project the comment was captured against, not under wherever the user happens to
        // be standing. The store moved to the repo root, so listing from there would otherwise scan
        // EVERY same-named .xaml in the repo — five other MainWindow.xaml files turn one element into ten
        // candidates, and the answer changes depending on which directory you ran from. This is what the
        // per-comment project root is for.
        var searchRoot = c.ProjectRoot is { Length: > 0 } captured && Directory.Exists(captured)
            ? captured
            : sourceRoot;

        var view = new CommentView
        {
            Id = c.Id,
            Text = c.Text,
            Status = c.Status,
            Kind = c.Kind,
            CreatedAt = c.CreatedAt,
            UpdatedAt = c.UpdatedAt,
            Anchor = c.Anchor,
            ProjectRoot = c.ProjectRoot,
            Context = c.Context,
            Resolution = c.Resolution,
            Hits = [.. resolver.ReAnchor(c.Anchor, searchRoot)],
        };

        var confirmed = FindStrongHit(view, searchRoot);
        if (confirmed is not null)
        {
            view.AnchorConfirmed = true;
            view.Hits = [confirmed];
            return view;
        }
        view.RequiresConfirmation = view.Hits.Count > 0;

        // Surface the same-via ambiguity signal structurally so --json consumers don't have to
        // re-derive it. AmbiguousCandidates returns [] unless the strongest facet re-anchored to >1 distinct place.
        var candidates = AmbiguousCandidates(view);
        if (candidates.Count > 0)
        {
            view.Ambiguous = true;
            view.Candidates = [.. candidates];
            view.CandidatesReason = CandidatesReason(view);
        }

        return view;
    }

    /// <summary>
    /// A unique qualified authored match can move lines. A historical position cannot promote a weak hit.
    /// </summary>
    private static SourceHit? FindStrongHit(CommentView c, string? sourceRoot)
    {
        try
        {
            if (sourceRoot is null || !Directory.Exists(sourceRoot))
            {
                return null;
            }
            var known = CommentAnchorResolver.ResolveKnownSourcePath(sourceRoot, c.Anchor);
            c.Hits.RemoveAll(hit =>
                CommentAnchorResolver.RelativeSourcePath(hit.File, sourceRoot) is not string relative ||
                Helpers.PathSafety.HasReparsePointOnPath(Path.Combine(sourceRoot, relative), sourceRoot) ||
                !File.Exists(Path.Combine(sourceRoot, relative)));
            var strong = c.Hits.Where(hit => hit.Confidence == "strong").Take(2).ToArray();
            if (strong.Length != 1 || c.Anchor.Authored is not { } authored || known is null ||
                !CommentStoreLocator.SameProject(authored.ProjectRoot, sourceRoot))
            {
                return null;
            }
            var relative = Path.GetRelativePath(sourceRoot, known);
            return strong[0].File.Equals(relative, StringComparison.OrdinalIgnoreCase) ? strong[0] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Source access changed after re-anchoring. Discard stale hits so the existing
            // unconfirmed-source warning applies, without failing an already committed write.
            c.Hits.Clear();
            return null;
        }
    }

    public static string CandidatesReason(CommentView c)
        => (c.Anchor.SourceFile is { Length: > 0 } file && c.Anchor.Line is int line && line > 0
            ? $"creation location {file}:{line} is historical; current candidates require confirmation"
            : "no location was captured for this element, so this is a source-wide search") + UniquenessNote(c);

    private static string UniquenessNote(CommentView c)
        => c.Anchor.Authored is { UniqueInstance: false, UniquenessReason: { Length: > 0 } reason }
            ? $"; captured instance uniqueness: {reason}" : "";

    public static CommentsListPayload BuildPayload(
        IEnumerable<Comment> comments,
        ICommentAnchorResolver resolver,
        string? appTitle,
        string? sourceRoot,
        string? storePath = null)
    {
        var payload = new CommentsListPayload
        {
            App = new CommentAppInfo { Title = appTitle, SourceRoot = sourceRoot, StorePath = storePath },
        };

        foreach (var c in comments)
        {
            payload.Comments.Add(ToView(c, resolver, sourceRoot));
        }

        return payload;
    }

    public static IReadOnlyList<string> AmbiguousCandidates(CommentView c)
    {
        if (c.Hits.Count < 2)
        {
            return [];
        }

        // Hits are returned strongest-facet-first; competition only exists among hits found the SAME way.
        var topVia = c.Hits[0].Via;
        var strong = c.Hits[0].Confidence == "strong";
        var competing = new List<string>();
        foreach (var h in c.Hits)
        {
            if (strong ? h.Confidence == "strong" : string.Equals(h.Via, topVia, StringComparison.Ordinal))
            {
                competing.Add(h.Column > 0 ? $"{h.File}:{h.Line}:{h.Column}" : $"{h.File}:{h.Line}");
            }
        }

        var distinct = competing.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return distinct.Length > 1 ? distinct : [];
    }

    public static bool HasResolvableIdentity(CommentAnchor anchor)
    {
        var id = anchor.Identity;
        return !string.IsNullOrEmpty(id.Name)
            || (!string.IsNullOrEmpty(id.AutomationId) && id.AutomationId != "0" && !string.Equals(id.AutomationId, id.Name, StringComparison.Ordinal))
            || (!string.IsNullOrWhiteSpace(id.Content) && id.Content!.Trim().Length >= 2)
            || (!string.IsNullOrEmpty(id.Type) && !string.IsNullOrEmpty(anchor.SourceFile));
    }

    /// <summary>
    /// True when the comment was stored without any identity the resolver can match on — so it cannot be
    /// re-anchored at all. Distinct from <see cref="IsMaybeStale"/>: this is a fact about the STORED anchor,
    /// not about the current source, and reporting it as "the element may be renamed or removed" asserts a
    /// cause that was never established.
    /// </summary>
    public static bool IsUnanchorable(CommentView c)
        => c.Hits.Count == 0 && !HasResolvableIdentity(c.Anchor);

    /// <summary>
    /// True when the comment carries an anchor the resolver COULD have matched, and re-anchoring still found no
    /// current match — the element was plausibly renamed or removed. A comment that was never source-anchored
    /// (no <c>SourceFile</c>), or whose identity bundle is empty (<see cref="IsUnanchorable"/>), is not "stale":
    /// there was nothing to match in the first place.
    /// </summary>
    public static bool IsMaybeStale(CommentView c)
        => c.Hits.Count == 0 && !string.IsNullOrEmpty(c.Anchor.SourceFile) && HasResolvableIdentity(c.Anchor);

    public static string? AnchorHealthWarning(CommentView c)
        => c.RequiresConfirmation
            ? "current source candidates require explicit confirmation; the creation location is historical" +
                UniquenessNote(c)
            : IsUnanchorable(c)
            ? "saved without source identity — cannot be re-anchored (re-add the comment on the running app)"
            : IsMaybeStale(c) ? "no current source match — source may be unavailable, or element may be renamed or removed (stale)" : null;

}

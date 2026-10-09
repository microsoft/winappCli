// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.DevTools.Comments;

using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Helpers;

/// <summary>
/// Finds current declarations by captured authored identity, then ranks weaker suggestions.
/// Runtime text and historical coordinates never establish source identity.
/// </summary>
internal interface ICommentAnchorResolver
{
    IReadOnlyList<SourceHit> ReAnchor(CommentAnchor anchor, string? sourceRoot);
}

internal sealed class CommentAnchorResolver(ILogger<CommentAnchorResolver>? logger = null) : ICommentAnchorResolver
{
    private const int MaxHits = 10;

    public IReadOnlyList<SourceHit> ReAnchor(CommentAnchor anchor, string? sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
        {
            return [];
        }

        var identity = anchor.Identity;
        List<string> files;
        try
        {
            if (PathSafety.HasReparsePointOnPath(sourceRoot, Path.GetPathRoot(Path.GetFullPath(sourceRoot))!))
            {
                throw new IOException($"The source directory is redirected or inaccessible: {sourceRoot}");
            }
            files = GetCandidateFiles(sourceRoot, anchor);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            (logger ?? NullLogger<CommentAnchorResolver>.Instance).LogWarning(ex,
                "Could not search comment source locations under {SourceRoot}.", sourceRoot);
            return [];
        }
        if (files.Count == 0)
        {
            return [];
        }

        var hits = new List<SourceHit>();
        // Same-type declarations in the captured file, scored against the captured declaration.
        var scored = new List<(SourceHit Hit, int Similarity, bool TreePathCandidate)>();

        foreach (var file in files)
        {
            try
            {
                var relative = Path.GetRelativePath(sourceRoot, file);
                XElement? captured = null;
                foreach (var declaration in CommentAuthoredIdentity.Read(file, sourceRoot))
                {
                    var element = declaration.Element;
                    if (element.Name.LocalName.Contains('.') || !CommentAuthoredIdentity.MatchesType(element, identity.Type))
                    {
                        continue;
                    }
                    var authored = anchor.Authored;
                    var exact = authored is not null && CommentAuthoredIdentity.Signature(element) == authored.Signature;
                    var scope = authored is not null && CommentAuthoredIdentity.Scope(element) == authored.Scope;
                    var named = !string.IsNullOrEmpty(identity.Name) && CommentAuthoredIdentity.Name(element) == identity.Name;
                    var automationId = CommentAuthoredIdentity.AutomationId(element);
                    var automated = IsMeaningfulAutomationId(identity.AutomationId, null) && automationId == identity.AutomationId;
                    var automationSuggestion = automated || !string.IsNullOrEmpty(identity.Name) && automationId == identity.Name;
                    var structural = authored is not null && CommentAuthoredIdentity.Structure(element) == authored.Structure;
                    var content = IsMeaningfulContent(identity.Content) &&
                        (element.Attributes().Any(attribute => attribute.Name.LocalName is "Text" or "Content" or "Header" &&
                            attribute.Value == identity.Content) ||
                         !element.HasElements && element.Value == identity.Content);
                    var via = exact ? "declaration" : named ? "x:Name" : automationSuggestion ? "AutomationId" :
                        structural ? "treePath" : content ? "content" : "type";
                    if (via == "type" && (string.IsNullOrEmpty(identity.Type) || anchor.SourceFile is null))
                    {
                        continue;
                    }
                    // Runtime instance counts do not matter here: one matching declaration is one place to edit.
                    var qualified = authored is not null && !authored.Templated &&
                        !anchor.Templated && !anchor.Weak &&
                        authored.Type == CommentAuthoredIdentity.TypeIdentity(element) &&
                        CommentStoreLocator.SameProject(authored.ProjectRoot, sourceRoot) &&
                        relative.Equals(authored.SourceFile, StringComparison.OrdinalIgnoreCase) && scope;
                    var strong = qualified && (exact || named || automated);
                    var hit = new SourceHit
                    {
                        File = relative,
                        Line = declaration.Line,
                        Column = declaration.Column,
                        Via = via,
                        Confidence = strong ? "strong" : "weak",
                        Rank = strong ? 0 : exact ? 1 : named ? 2 : automationSuggestion ? 3 : structural ? 4 : content ? 5 : 6,
                        Text = DevToolsSecrets.RedactXaml(declaration.Text),
                    };
                    hits.Add(hit);
                    if (authored is not null && relative.Equals(authored.SourceFile, StringComparison.OrdinalIgnoreCase))
                    {
                        captured ??= ParseCaptured(authored.Declaration, element);
                        scored.Add((hit, captured is null ? 0 : CommentAuthoredIdentity.Similarity(captured, element),
                            qualified && structural && !exact && string.IsNullOrEmpty(identity.Name) && !automated));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                (logger ?? NullLogger<CommentAnchorResolver>.Instance).LogWarning(ex, "Could not read comment source file {File}.", file);
            }
        }

        // An edited unnamed declaration keeps its tree position. It is the same element when nothing matches
        // the captured declaration exactly and it keeps more of the captured attributes than any other candidate.
        if (!hits.Exists(hit => hit.Via == "declaration") &&
            scored.Where(item => item.TreePathCandidate).Take(2).ToArray() is [var moved] &&
            moved.Similarity > 0 && scored.All(item => ReferenceEquals(item.Hit, moved.Hit) || item.Similarity < moved.Similarity))
        {
            moved.Hit.Confidence = "strong";
            moved.Hit.Rank = 0;
        }

        // A type-only match is noise once the declaration, x:Name or a strong tree position identified candidates.
        if (hits.Exists(hit => hit.Via is "declaration" or "x:Name" || hit.Confidence == "strong"))
        {
            hits.RemoveAll(hit => hit.Via == "type");
        }

        // Two equally good matches (identical declarations in one file) identify neither: each is a candidate.
        if (hits.Count(hit => hit.Confidence == "strong") > 1)
        {
            foreach (var hit in hits.Where(hit => hit.Confidence == "strong"))
            {
                hit.Confidence = "weak";
                hit.Rank = 1;
            }
        }

        var pivot = anchor.Line ?? 0;
        hits.Sort((a, b) =>
        {
            var facet = a.Rank.CompareTo(b.Rank);
            if (facet != 0)
            {
                return facet;
            }
            var da = Math.Abs(a.Line - pivot);
            var db = Math.Abs(b.Line - pivot);
            if (da != db)
            {
                return da.CompareTo(db);
            }

            var f = string.CompareOrdinal(a.File, b.File);
            return f != 0 ? f : a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Column.CompareTo(b.Column);
        });

        return hits.Count > MaxHits ? hits.GetRange(0, MaxHits) : hits;
    }

    private static List<string> GetCandidateFiles(string sourceRoot, CommentAnchor anchor)
    {
        if (ResolveKnownSourcePath(sourceRoot, anchor) is string exact)
        {
            return [exact];
        }
        var all = new List<string>();
        foreach (var path in Directory.EnumerateFiles(sourceRoot, "*.xaml",
            new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
        {
            if (IsBuildOutput(path))
            {
                continue;
            }

            all.Add(path);
        }

        if (!string.IsNullOrEmpty(anchor.SourceFile))
        {
            var leaf = Path.GetFileName(anchor.SourceFile);
            var preferred = all.FindAll(p => string.Equals(Path.GetFileName(p), leaf, StringComparison.OrdinalIgnoreCase));
            if (preferred.Count > 0)
            {
                return preferred;
            }
            // Named file not found (moved/renamed) → fall back to the full set rather than returning nothing.
        }

        return all;
    }

    internal static string? RelativeSourcePath(string source, string? root)
    {
        if (Uri.UnescapeDataString(source).Replace('/', '\\').Split('\\').Any(part => part is ".." or "."))
        {
            return null;
        }
        var path = source.Replace('/', '\\');
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme == "ms-appx" && uri.Host.Length == 0)
            {
                path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/').Replace('/', '\\');
            }
            else if (uri.IsFile && !uri.IsUnc)
            {
                path = uri.LocalPath;
            }
            else
            {
                return null;
            }
        }
        if (path.Split('\\').Any(part => part is ".." or "."))
        {
            return null;
        }
        if (Path.IsPathRooted(path))
        {
            if (root is null)
            {
                return null;
            }
            path = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        }
        return path.Length == 0 || Path.IsPathRooted(path) || path.Split('\\').Any(part => part == "..") ||
            path.Contains(':') ? null : path;
    }

    internal static string? ResolveKnownSourcePath(string root, CommentAnchor anchor)
    {
        if (PathSafety.HasReparsePointOnPath(root, Path.GetPathRoot(Path.GetFullPath(root))!))
        {
            throw new IOException($"The source directory is redirected or inaccessible: {root}");
        }
        var relative = anchor.SourceFile is string file ? RelativeSourcePath(file, root) : null;
        var fromUri = anchor.SourceUri is string uri ? RelativeSourcePath(uri, root) : null;
        if (!string.IsNullOrEmpty(anchor.SourceUri) && fromUri is null)
        {
            return null;
        }
        relative = fromUri ?? relative;
        if (relative is null)
        {
            return null;
        }
        if (fromUri is null && !relative.Contains('\\'))
        {
            var matches = Directory.EnumerateFiles(root, "*.xaml",
                new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false })
                .Where(p => !IsBuildOutput(p) && Path.GetFileName(p).Equals(relative, StringComparison.OrdinalIgnoreCase))
                .Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
        var candidate = Path.GetFullPath(Path.Combine(root, relative));
        return !PathSafety.HasReparsePointOnPath(candidate, Path.GetFullPath(root)) && File.Exists(candidate)
            ? candidate : null;
    }

    private static XElement? ParseCaptured(string declaration, XElement context)
    {
        try
        {
            return CommentAuthoredIdentity.ParseOpeningTag(declaration, context);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
        {
            return null;
        }
    }

    internal static bool IsBuildOutput(string path)
    {
        var normalized = path.Replace('/', '\\');
        return normalized.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMeaningfulAutomationId(string? automationId, string? name)
        => !string.IsNullOrEmpty(automationId)
           && automationId != "0"
           && !string.Equals(automationId, name, StringComparison.Ordinal);

    private static bool IsMeaningfulContent(string? content)
        => !string.IsNullOrWhiteSpace(content) && content!.Trim().Length >= 2;
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.DevTools.Comments;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Helpers;

/// <summary>
/// Finds the current location of a Microsoft.UI.Reactor call site a comment was left on, without a compiler.
/// The stored line still holding the same text and factory call is the call site (strong). When it moved, a unique
/// line with the same text in the component that rendered it is too; anything else is a ranked candidate that needs
/// confirmation.
/// </summary>
internal static class CSharpAnchorResolver
{
    private const int MaxHits = 10;
    private const int MaxFiles = 2048;

    internal static IReadOnlyList<SourceHit> ReAnchor(CommentAnchor anchor, string sourceRoot, ILogger? logger = null)
    {
        var cs = anchor.CSharp!;
        List<string> files;
        bool broad;
        try
        {
            (files, broad) = CandidateFiles(sourceRoot, cs.SourceFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            (logger ?? NullLogger.Instance).LogWarning(ex, "Could not search C# comment sources under {SourceRoot}.", sourceRoot);
            return [];
        }
        var sameProject = CommentStoreLocator.SameProject(cs.ProjectRoot, sourceRoot);
        var hits = new List<(SourceHit Hit, int Similarity, bool Keyed)>();
        foreach (var file in files)
        {
            try
            {
                var relative = Path.GetRelativePath(sourceRoot, file);
                var sameFile = sameProject && relative.Equals(cs.SourceFile, StringComparison.OrdinalIgnoreCase);
                var lines = CSharpSourceText.ReadLines(file, sourceRoot);
                var masked = CSharpSourceText.Mask(lines);
                // Compared and shown as stored: with the strings a password parameter receives redacted.
                var shown = CSharpSourceText.RedactLines(lines, CSharpDeclarations.ForSource(file));
                if (sameFile && cs.Line >= 1 && cs.Line <= lines.Length &&
                    CSharpSourceText.LineHash(shown[cs.Line - 1]) == cs.LineHash &&
                    FactoryColumn(masked[cs.Line - 1], cs) is int column)
                {
                    return [Hit(relative, cs.Line, column, "strong", 0, "call-site", shown[cs.Line - 1])];
                }
                var span = CSharpSourceText.DeclarationSpan(masked, cs.Component);
                var from = span?.Start ?? 1;
                var to = span?.End ?? lines.Length;
                for (var line = from; line <= to && line <= lines.Length; line++)
                {
                    if (FactoryColumn(masked[line - 1], cs) is not int at)
                    {
                        continue;
                    }
                    var sameText = CSharpSourceText.LineHash(shown[line - 1]) == cs.LineHash;
                    // A project-wide search (the captured file is gone) looks for the captured line only.
                    if (!sameText && broad)
                    {
                        continue;
                    }
                    var text = CSharpSourceText.StoredText(shown[line - 1]);
                    var keyed = !string.IsNullOrEmpty(cs.Key) && lines[line - 1].Contains(cs.Key, StringComparison.Ordinal);
                    hits.Add((Hit(relative, line, at, "weak", sameText ? 1 : 2, sameText ? "line-text" : "factory", shown[line - 1]),
                        sameText ? 100 : Similarity(cs.LineText, text), keyed));
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                (logger ?? NullLogger.Instance).LogWarning(ex, "Could not read C# comment source {File}.", file);
            }
        }

        // The same line text, once, in the file the comment was left in: the call site moved.
        var moved = hits.Where(item => item.Hit.Via == "line-text").ToList();
        if (moved is [var only] && sameProject && only.Hit.File.Equals(cs.SourceFile, StringComparison.OrdinalIgnoreCase))
        {
            only.Hit.Confidence = "strong";
            only.Hit.Rank = 0;
        }
        // A factory-only match is noise once a line with the captured text was found.
        if (moved.Count > 0)
        {
            hits.RemoveAll(item => item.Hit.Via == "factory");
        }
        hits.Sort((a, b) =>
        {
            var rank = a.Hit.Rank.CompareTo(b.Hit.Rank);
            if (rank != 0) { return rank; }
            var keyed = b.Keyed.CompareTo(a.Keyed);
            if (keyed != 0) { return keyed; }
            var similarity = b.Similarity.CompareTo(a.Similarity);
            if (similarity != 0) { return similarity; }
            var distance = Math.Abs(a.Hit.Line - cs.Line).CompareTo(Math.Abs(b.Hit.Line - cs.Line));
            if (distance != 0) { return distance; }
            var file = string.CompareOrdinal(a.Hit.File, b.Hit.File);
            return file != 0 ? file : a.Hit.Line.CompareTo(b.Hit.Line);
        });
        return hits.Take(MaxHits).Select(item => item.Hit).ToList();
    }

    private static SourceHit Hit(string file, int line, int column, string confidence, int rank, string via, string text) => new()
    {
        File = file,
        Line = line,
        Column = column,
        Confidence = confidence,
        Rank = rank,
        Via = via,
        Text = CSharpSourceText.Redact(text.Trim()),
    };

    /// <summary>
    /// The column of the call this anchor names on a masked line: the factory at the stored column, else the nearest
    /// call to that factory. Null when the line makes no such call.
    /// </summary>
    internal static int? FactoryColumn(string maskedLine, CommentCSharpAnchor cs)
    {
        var name = cs.Factory ?? cs.Element;
        var sites = CSharpSourceText.CallSites(maskedLine);
        if (name is null)
        {
            return sites.Count == 1 ? sites[0].Column : null;
        }
        var matching = sites.Where(site => site.Name == name).ToList();
        if (matching.Count == 0)
        {
            return null;
        }
        return matching.OrderBy(site => Math.Abs(site.Column - cs.Column)).First().Column;
    }

    /// <summary>How much of the captured line survives in a candidate, 0-99, by shared tokens.</summary>
    internal static int Similarity(string? captured, string current)
    {
        if (string.IsNullOrEmpty(captured))
        {
            return 0;
        }
        var a = Tokens(captured);
        var b = Tokens(current);
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }
        var shared = a.Count(token => b.Contains(token));
        return Math.Min(99, 200 * shared / (a.Count + b.Count));
    }

    private static HashSet<string> Tokens(string text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var word = i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_');
            if (word && start < 0) { start = i; }
            else if (!word && start >= 0) { tokens.Add(text[start..i]); start = -1; }
        }
        return tokens;
    }

    private static (List<string> Files, bool Broad) CandidateFiles(string sourceRoot, string sourceFile)
    {
        if (PathSafety.HasReparsePointOnPath(sourceRoot, Path.GetPathRoot(Path.GetFullPath(sourceRoot))!))
        {
            throw new IOException($"The source directory is redirected or inaccessible: {sourceRoot}");
        }
        if (CommentAnchorResolver.RelativeSourcePath(sourceFile, sourceRoot) is { } relative)
        {
            var known = Path.GetFullPath(Path.Combine(sourceRoot, relative));
            if (!PathSafety.HasReparsePointOnPath(known, Path.GetFullPath(sourceRoot)) && File.Exists(known))
            {
                return ([known], false);
            }
        }
        var all = Directory.EnumerateFiles(sourceRoot, "*.cs",
                new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false })
            .Where(path => !CommentAnchorResolver.IsSkipped(sourceRoot, path)).Take(MaxFiles).ToList();
        var leaf = Path.GetFileName(sourceFile);
        var preferred = all.FindAll(path => Path.GetFileName(path).Equals(leaf, StringComparison.OrdinalIgnoreCase));
        return preferred.Count > 0 ? (preferred, false) : (all, true);
    }
}

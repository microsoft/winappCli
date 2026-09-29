// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;

namespace WinApp.Cli.Services.DevTools;

internal static class DevToolsSelector
{
    internal sealed record Resolution(string? Handle, string? Error, VisualTreeMatch[] Candidates, string? Warning = null,
        DevToolsProtocolError? RemoteError = null)
    {
        public bool Ok => Handle is not null;

        public static Resolution Found(string handle, string? warning = null) => new(handle, null, [], warning);

        public static Resolution Fail(string error, VisualTreeMatch[]? candidates = null) =>
            new(null, error, candidates ?? []);

        public static Resolution Fail(DevToolsProtocolError error) =>
            new(null, DevToolsErrors.Describe(error), [], RemoteError: error);
    }

    internal sealed record VisualTreeMatch(
        string Handle,
        string Name,
        string Type,
        string? File,
        string? Id,
        bool UniqueName,
        int Depth)
    {
        public string ShortType => DevToolsFormat.ShortTypeName(Type);

        public string? ShortFile => DevToolsFormat.ShortFileName(File);

        public string Selector => Display(Handle, Type, Name, Id, UniqueName);
    }

    internal sealed record FindResult(
        IReadOnlyList<VisualTreeMatch> Matches,
        int SearchedNodes,
        int CensusNodes,
        bool Truncated,
        bool ClassificationTruncated = false,
        bool SourceInstrumented = false)
    {
        public string? ExplainIfNothingAuthored()
        {
            if (!SourceInstrumented)
            {
                var why = "This app reports no XAML source information, so its own elements cannot be told " +
                          "apart from the framework's. Searching the whole tree instead. Launch it with " +
                          "`winapp run --devtools`, which turns source information on.";
                return ClassificationTruncated
                    ? why + " (The agent is still classifying, so run the command again if you did launch it that way.)"
                    : why;
            }

            // Instrumented and finished: an empty result is an honest "no such element of yours".
            return ClassificationTruncated
                ? "The agent has not finished classifying this app's XAML yet, so it cannot yet say which " +
                  "elements are yours. Searching the whole tree instead; run the command again in a moment."
                : null;
        }
    }

    public static bool IsHandle(string selector) =>
        selector.Length > 0
        && selector.All(char.IsAsciiDigit)
        && ulong.TryParse(selector, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && value != 0;

    /// <summary>
    /// The selector to PRINT for an element. A census-unique <c>x:Name</c> wins because it is what the
    /// developer wrote and will recognise; otherwise the semantic slug, which is readable AND checkable; and
    /// only when the tap gave the element no identity does the bare handle survive as the printed form.
    /// </summary>
    public static string Display(string handle, string type, string? name, string? id, bool uniqueName)
    {
        if (uniqueName && !string.IsNullOrEmpty(name))
        {
            return name;
        }

        return DevToolsSlug.For(type, name, id) ?? handle;
    }

    public static Resolution Resolve(VisualTreeTap tap, string selector, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(selector))
        {
            return Resolution.Fail("Provide a selector: the one printed in brackets, an x:Name, or a handle.");
        }

        selector = selector.Trim();
        if (IsHandle(selector))
        {
            return Resolution.Found(selector);
        }

        return DevToolsSlug.Parse(selector) is DevToolsSlug.Parsed slug
            ? ResolveSlug(tap, selector, slug, cancellationToken)
            : ResolveName(tap, selector, cancellationToken);
    }

    /// <summary>
    /// Re-derives slug identity from the live tree. Type-token search is only an optimization;
    /// a miss falls back to a full scan so "no match" does not mean "token could not substring-match".
    /// </summary>
    private static Resolution ResolveSlug(VisualTreeTap tap, string selector, DevToolsSlug.Parsed slug, CancellationToken cancellationToken)
    {
        var (result, error) = Find(tap, slug.TypeToken, appAuthoredOnly: false, cancellationToken);
        if (error is not null)
        {
            return Resolution.Fail(error);
        }

        var candidates = result!.Matches;
        var exact = MatchSlug(candidates, slug);
        var nearMisses = NearMissSlug(candidates, slug);

        // Re-scan the whole live tree when the narrowed search did not produce a match. Two ways it can miss:
        // the token cannot substring-match the element's type (above), or the search itself was node-capped.
        // A near miss does NOT excuse skipping the rescan when the search was truncated — the element the
        // selector actually names could be outside the cap, and reporting "stale" on the strength of a
        // similar element we did happen to see is a confident wrong answer about a live element.
        if (exact.Length == 0 && (nearMisses.Length == 0 || result.Truncated))
        {
            var (whole, scanError) = ScanTree(tap, cancellationToken);
            if (scanError is not null)
            {
                return Resolution.Fail(scanError);
            }
            if (whole is null)
            {
                return Resolution.Fail("The DevTools agent returned an unreadable visual tree.");
            }
            candidates = whole;
            exact = MatchSlug(candidates, slug);
            nearMisses = NearMissSlug(candidates, slug);
        }

        if (exact.Length == 1)
        {
            // A slug carries its own proof, so a node-capped search cannot have resolved it to the wrong
            // element: the identity either matched or it did not. No incompleteness caveat is warranted here,
            // and adding one would refuse writes that are provably safe.
            return Resolution.Found(exact[0].Handle);
        }

        if (exact.Length > 1)
        {
            // Two live elements re-derived the SAME identity: a hash collision. Vanishingly unlikely at 40
            // bits, and still never resolved by picking one — that is the whole reason the width was raised
            // above the UI Automation slug's 16.
            return Resolution.Fail(
                $"'{selector}' matches {exact.Length} elements (their identities collide). Use one of their handles:",
                exact);
        }

        if (nearMisses.Length > 0)
        {
            return Resolution.Fail(
                $"'{selector}' is stale: an element of that type and name is live, but at a different place in " +
                "the tree, so it is not the one this selector named. Re-run `winapp devtools inspect` or " +
                "`winapp devtools search` for current selectors:",
                nearMisses);
        }

        return Resolution.Fail(
            $"No element matches the selector '{selector}'. Selectors are minted from the tree's shape — " +
            "re-run `winapp devtools inspect` or `winapp devtools search` to get current ones.");
    }

    private static VisualTreeMatch[] MatchSlug(IReadOnlyList<VisualTreeMatch> candidates, DevToolsSlug.Parsed slug) =>
        [.. candidates.Where(c => DevToolsSlug.Matches(slug, c.Type, c.Name, c.Id))];

    private static VisualTreeMatch[] NearMissSlug(IReadOnlyList<VisualTreeMatch> candidates, DevToolsSlug.Parsed slug) =>
        [.. candidates.Where(c => DevToolsSlug.IsNearMiss(slug, c.Type, c.Name, c.Id))];

    private static (VisualTreeMatch[]? Matches, DevToolsProtocolError? Error) ScanTree(VisualTreeTap tap, CancellationToken cancellationToken)
    {
        var response = tap.RequestEnumerate(null, null, cancellationToken: cancellationToken);
        if (!response.Ok)
        {
            return (null, response.Error);
        }
        if (VisualTreeNode.ParseForest(response.ResultJson) is not IReadOnlyList<VisualTreeNode> roots)
        {
            return (null, null);
        }

        var rows = new List<VisualTreeMatch>();
        void Walk(VisualTreeNode node, int depth)
        {
            rows.Add(new VisualTreeMatch(node.Handle, node.Name, node.Type, node.File, node.Id, node.UniqueName, depth));
            foreach (var child in node.Children)
            {
                Walk(child, depth + 1);
            }
        }

        foreach (var root in roots)
        {
            Walk(root, 0);
        }

        return ([.. rows], null);
    }

    private static Resolution ResolveName(VisualTreeTap tap, string selector, CancellationToken cancellationToken)
    {
        var found = Find(tap, selector, appAuthoredOnly: false, cancellationToken);
        if (found.Error is not null)
        {
            return Resolution.Fail(found.Error);
        }

        // find() is a substring search over type/#name/file, so narrow to an EXACT x:Name before deciding.
        var exact = found.Result!.Matches
            .Where(m => string.Equals(m.Name, selector, StringComparison.Ordinal))
            .ToArray();

        // The search itself is node-capped. On a tree above that cap, "exactly one match" can mean "exactly
        // one in the part we looked at" — and this is the path that decides which single element a later
        // set-property MUTATES. Resolve, but never silently: say the search was incomplete.
        var incomplete = found.Result.Truncated
            ? "The agent's node limit truncated the search, so another element could share this x:Name. " +
              "Use the selector `winapp devtools inspect` printed to be certain."
            : null;

        if (exact.Length == 1)
        {
            return Resolution.Found(exact[0].Handle, incomplete);
        }

        if (exact.Length > 1)
        {
            // Duplicate x:Names are ordinary (one DataTemplate, many rows). Each one has its own semantic
            // selector, so the candidate list is directly usable rather than a dead end.
            return Resolution.Fail(
                $"{exact.Length} elements are named '{selector}'. Use one of their selectors instead:",
                exact);
        }

        var caseInsensitive = found.Result.Matches
            .Where(m => string.Equals(m.Name, selector, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (caseInsensitive.Length == 1)
        {
            return Resolution.Found(caseInsensitive[0].Handle, incomplete);
        }

        var notFound = $"No element has x:Name '{selector}'. Run `winapp devtools search {selector}` to find it by type or text.";
        return Resolution.Fail(
            found.Result.Truncated
                ? notFound + " The agent's node limit truncated the search, so it may exist but not have been examined."
                : notFound,
            found.Result.Matches.Take(10).ToArray());
    }

    public static (FindResult? Result, DevToolsProtocolError? Error) Find(VisualTreeTap tap, string query, bool appAuthoredOnly,
        CancellationToken cancellationToken = default)
    {
        var response = tap.RequestFind(query, appAuthoredOnly, cancellationToken);
        if (!response.Ok)
        {
            return (null, response.Error!);
        }

        using var doc = response.TryParseResult();
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return (null, new(0, "cli", "The DevTools agent returned an unreadable search result."));
        }

        var root = doc.RootElement;
        var matches = new List<VisualTreeMatch>();
        if (root.TryGetProperty("matches", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in array.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var handle = ReadString(m, "handle");
                if (handle.Length == 0)
                {
                    continue;
                }

                var file = ReadString(m, "file");
                var id = ReadString(m, "id");
                matches.Add(new VisualTreeMatch(
                    handle,
                    ReadString(m, "name"),
                    ReadString(m, "type"),
                    file.Length == 0 ? null : file,
                    id.Length == 0 ? null : id,
                    m.TryGetProperty("uniqueName", out var unique) && unique.ValueKind == JsonValueKind.True,
                    m.TryGetProperty("depth", out var d) && d.TryGetInt32(out var depth) ? depth : 0));
            }
        }

        return (new FindResult(
            matches,
            ReadInt(root, "searchedNodes"),
            ReadInt(root, "censusNodes"),
            root.TryGetProperty("truncated", out var t) && t.ValueKind == JsonValueKind.True,
            root.TryGetProperty("classificationTruncated", out var ct) && ct.ValueKind == JsonValueKind.True,
            root.TryGetProperty("sourceInstrumented", out var si) && si.ValueKind == JsonValueKind.True), null);
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
}

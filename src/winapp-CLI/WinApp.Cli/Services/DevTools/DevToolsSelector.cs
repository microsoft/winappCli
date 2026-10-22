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
        public DevToolsElementFacts? Facts { get; init; }

        public string ShortType => DevToolsFormat.ShortTypeName(Type);

        public string? ShortFile => Facts?.File ?? DevToolsFormat.ShortFileName(File);

        public string Selector => Display(Handle, Type, Name, Id, UniqueName);

        /// <summary>Extra context printed after the candidate, such as the window it is in.</summary>
        public string? Detail { get; init; }

        /// <summary>The HWND of the window the candidate is in, when known.</summary>
        public string? Window { get; init; }

        /// <summary>What to show instead of the WinUI type: a Reactor component (<c>&lt;Counter&gt;</c>) or element kind.</summary>
        public string? Label { get; init; }

        /// <summary>The C# variable a Reactor element was assigned to, which a selector names like an x:Name.</summary>
        public string? ReactorName { get; init; }

        /// <summary>Whether Reactor created this element, so it comes from the app's C#, not its XAML.</summary>
        public bool IsReactor { get; init; }

        /// <summary>Whether the selector is this element's x:Name or C# name.</summary>
        public bool IsNamed(string name, StringComparison comparison) =>
            string.Equals(Name, name, comparison) || string.Equals(ReactorName, name, comparison);
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
                          "`winapp run --devtools on`, which turns source information on.";
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
            return Resolution.Fail("Provide a selector: the one printed in brackets, an x:Name, an AutomationId, or a handle.");
        }

        selector = selector.Trim();
        // `#incrementButton` names an element the way the tree prints it: an x:Name or a Reactor element's C# name.
        if (selector.Length > 1 && selector[0] == '#' && IsIdentifier(selector[1..]))
        {
            return ResolveName(tap, selector[1..], cancellationToken);
        }
        if (IsHandle(selector))
        {
            return Resolution.Found(selector);
        }

        if (ReactorSelector.Parse(selector) is { } reactor)
        {
            return ResolveReactor(tap, selector, reactor, cancellationToken);
        }

        return DevToolsSlug.Parse(selector) is DevToolsSlug.Parsed slug
            ? ResolveSlug(tap, selector, slug, cancellationToken)
            : ResolveName(tap, selector, cancellationToken);
    }

    /// <summary>The component name in a <c>&lt;Counter&gt;</c> selector; null for any other selector.</summary>
    public static string? ComponentName(string selector) =>
        selector.Length > 2 && selector[0] == '<' && selector[^1] == '>' && selector[1..^1].Trim() is { Length: > 0 } name
            ? name
            : null;

    /// <summary>
    /// Resolves a Reactor selector (<c>&lt;Counter&gt;</c>, <c>&lt;Counter&gt;/Button</c>, <c>key=t1</c>,
    /// <c>App.cs:35</c>) to the control the matching Reactor element produced. Several matches (the same component
    /// twice in one window, or once in each of two windows) are ambiguous: the resolution fails and lists every
    /// match with its selector and window, so <c>-w</c> or one of those selectors picks one.
    /// </summary>
    private static Resolution ResolveReactor(VisualTreeTap tap, string selector, ReactorSelector parsed, CancellationToken cancellationToken)
    {
        var response = tap.RequestReactorDescribe(cancellationToken);
        if (!response.Ok)
        {
            return Resolution.Fail(response.Error!);
        }
        if (ReactorDescription.Parse(response.ResultJson) is not { } reactor)
        {
            return Resolution.Fail("The DevTools agent returned an unreadable Reactor description.");
        }
        if (!reactor.IsReactor)
        {
            return Resolution.Fail(new DevToolsProtocolError(0, reactor.Token, reactor.State == "absent"
                ? $"'{selector}' names a Reactor element, but this app does not use Microsoft.UI.Reactor. Use an x:Name or a selector from `winapp devtools inspect`."
                : reactor.ExplainUnavailable()));
        }

        var matches = parsed.Match(reactor);
        if (matches.Count == 1)
        {
            return Resolution.Found(matches[0].Handle);
        }
        if (matches.Count == 0)
        {
            return Resolution.Fail(reactor.Complete
                ? $"No rendered Reactor element matches {selector}. Run `winapp devtools inspect` to list the components and elements that are rendered now."
                : $"No rendered Reactor element matches {selector}, but the app was still changing and not every window answered. Run the command again.");
        }

        var candidates = ReactorCandidates(tap, reactor, matches, cancellationToken);
        var windows = candidates.Select(candidate => candidate.Window).Where(window => window is not null).Distinct().Count();
        // -w only helps when the matches are in different windows and no window was picked yet.
        return Resolution.Fail(
            $"{selector} matches {candidates.Length} rendered elements{(windows > 1 ? $" in {windows} windows" : "")}. " +
            (windows > 1 && tap.Window is null ? "Pass -w <window> to pick a window, or use" : "Use") + " one of their selectors:",
            candidates);
    }

    /// <summary>The Reactor elements that mount a component of this name, or that a host's root component of this name rendered.</summary>
    internal static IReadOnlyList<ReactorNode> ComponentMatches(ReactorDescription reactor, string name, StringComparison comparison)
    {
        bool Named(ReactorComponent? component) =>
            component is not null && (string.Equals(component.Name, name, comparison) || string.Equals(component.Type, name, comparison));
        return [.. reactor.ByHandle.Values.Where(node => Named(node.Component) || Named(node.Host?.Root))];
    }

    /// <summary>
    /// Candidate lines for Reactor matches: each with the selector the tree prints for it (a slug, so it can be
    /// passed straight back), its Reactor label, key, call site and window.
    /// </summary>
    internal static VisualTreeMatch[] ReactorCandidates(VisualTreeTap tap, ReactorDescription reactor,
        IReadOnlyList<ReactorNode> matches, CancellationToken cancellationToken)
    {
        var tree = new Dictionary<string, (VisualTreeNode Node, int Depth)>(StringComparer.Ordinal);
        var response = tap.RequestEnumerate(null, null, cancellationToken: cancellationToken);
        if (response.Ok && VisualTreeNode.ParseForest(response.ResultJson) is { } roots)
        {
            void Walk(VisualTreeNode node, int depth)
            {
                tree.TryAdd(node.Handle, (node, depth));
                foreach (var child in node.Children)
                {
                    Walk(child, depth + 1);
                }
            }
            foreach (var root in roots)
            {
                Walk(root, 0);
            }
        }
        return [.. matches.Select(node => ReactorCandidate(reactor, node, tree.TryGetValue(node.Handle, out var live) ? live : null))];
    }

    internal static VisualTreeMatch ReactorCandidate(ReactorDescription reactor, ReactorNode node, (VisualTreeNode Node, int Depth)? live)
    {
        // The window is recorded on the host a component's tree hangs from.
        ReactorHost? host = null;
        for (var current = node; current is not null && host is null;
             current = current.Parent is { } parent ? reactor.ByHandle.GetValueOrDefault(parent) : null)
        {
            host = current.Host;
        }
        var label = node.Component is { } component ? $"<{component.Name}>"
            : node.Host?.Root is { } root ? $"<{root.Name}>" : node.Element;
        var details = new List<string>();
        if (node.Name is not null) { details.Add("#" + node.Name); }
        if (node.Key is not null) { details.Add($"key={node.Key}"); }
        if (node.ShortSource is { } site) { details.Add(site); }
        if (host?.Window is { } hwnd)
        {
            details.Add(host.Title is { Length: > 0 } title ? $"window \"{title}\" (-w {hwnd})" : $"window -w {hwnd}");
        }
        return new VisualTreeMatch(node.Handle, live?.Node.Name ?? "", live?.Node.Type ?? label, null, live?.Node.Id,
            live?.Node.UniqueName ?? false, live?.Depth ?? 0)
        { Detail = details.Count == 0 ? null : string.Join(" ", details), Window = host?.Window, Label = label, ReactorName = node.Name, IsReactor = true };
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
        // A near miss does NOT excuse skipping the rescan when the search was truncated: the element the
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
            // bits, and still never resolved by picking one. That is the whole reason the width was raised
            // above the UI Automation slug's 16.
            return Resolution.Fail(
                $"'{selector}' matches {exact.Length} elements (their identities collide). Use one of their handles:",
                exact);
        }

        if (nearMisses.Length > 0)
        {
            // In a Reactor app the usual cause is navigation, a tab switch or a conditional: the component the selector
            // named is gone, and the same-typed elements left (often template parts) are not candidates for it.
            if (tap.RequestReactorDescribe(cancellationToken) is { Ok: true } described &&
                ReactorDescription.Parse(described.ResultJson) is { IsReactor: true })
            {
                return Resolution.Fail(
                    $"'{selector}' is stale: the element it named is no longer rendered, for example because the app " +
                    "navigated, switched tabs or a condition removed it, or a different component is rendered in its place. " +
                    "Run `winapp devtools search` with the component's name (for example `winapp devtools search \"<Counter>\"`) " +
                    "or `winapp devtools inspect` to get its current selector.");
            }
            return Resolution.Fail(
                $"'{selector}' is stale: an element of that type and name is live, but at a different place in " +
                "the tree, so it is not the one this selector named. Re-run `winapp devtools inspect` or " +
                "`winapp devtools search` for current selectors:",
                nearMisses);
        }

        return Resolution.Fail(
            $"No element matches the selector '{selector}'. Selectors are minted from the tree's shape, so " +
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

        // find() is a substring search over type/#name/file, so narrow to an EXACT x:Name (or a Reactor element's C#
        // name, which reads the same way) before deciding.
        var exact = found.Result!.Matches
            .Where(m => m.IsNamed(selector, StringComparison.Ordinal))
            .ToArray();

        // The search itself is node-capped. On a tree above that cap, "exactly one match" can mean "exactly
        // one in the part we looked at", and this is the path that decides which single element a later
        // set-property MUTATES. Resolve, but never silently: say the search was incomplete.
        var incomplete = found.Result.Truncated
            ? "The agent's node limit truncated the search, so another element could share this name. " +
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

        // No x:Name: an AutomationId, which `winapp ui` selects by, names the same element here.
        if (found.Result.Matches.Count > 0)
        {
            var facts = DevToolsPreviews.Fetch(tap, found.Result.Matches.Select(m => m.Handle), cancellationToken).Facts;
            var byAutomationId = found.Result.Matches
                .Where(m => string.Equals(facts.GetValueOrDefault(m.Handle)?.AutomationId, selector, StringComparison.Ordinal))
                .Select(m => m with { Facts = facts[m.Handle] })
                .ToArray();
            if (byAutomationId.Length == 1)
            {
                return Resolution.Found(byAutomationId[0].Handle, incomplete);
            }
            if (byAutomationId.Length > 1)
            {
                return Resolution.Fail(
                    $"{byAutomationId.Length} elements have AutomationId '{selector}'. Use one of their selectors instead:",
                    byAutomationId);
            }
        }

        var caseInsensitive = found.Result.Matches
            .Where(m => m.IsNamed(selector, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (caseInsensitive.Length == 1)
        {
            return Resolution.Found(caseInsensitive[0].Handle, incomplete);
        }

        var notFound = $"No element is named '{selector}' (an x:Name, an AutomationId, or the C# variable a Reactor element was assigned to). " +
            $"Run `winapp devtools search {selector}` to find it by type or text.";
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
                string? label = null;
                string? detail = null;
                string? reactorName = null;
                var isReactor = m.TryGetProperty("reactor", out var reactor) && reactor.ValueKind == JsonValueKind.Object;
                if (isReactor)
                {
                    var element = ReadString(reactor, "element");
                    var component = ReadString(reactor, "component");
                    label = component.Length > 0 ? $"<{component}>" : element.Length > 0 && !string.Equals(element,
                        DevToolsFormat.ShortTypeName(ReadString(m, "type")), StringComparison.Ordinal) ? element : null;
                    var parts = new List<string>();
                    if (ReadString(reactor, "name") is { Length: > 0 } name)
                    {
                        reactorName = name;
                        parts.Add("#" + name);
                    }
                    if (ReadString(reactor, "key") is { Length: > 0 } key) { parts.Add($"key={key}"); }
                    if (ReadString(reactor, "file") is { Length: > 0 } site)
                    {
                        parts.Add(ReactorNode.Short(site, reactor.TryGetProperty("line", out var line) && line.TryGetInt32(out var l) ? l : 0));
                    }
                    if (ReadString(reactor, "owner") is { Length: > 0 } owner) { parts.Add($"in <{owner}>"); }
                    detail = parts.Count == 0 ? null : string.Join(" ", parts);
                }
                matches.Add(new VisualTreeMatch(
                    handle,
                    ReadString(m, "name"),
                    ReadString(m, "type"),
                    file.Length == 0 ? null : file,
                    id.Length == 0 ? null : id,
                    m.TryGetProperty("uniqueName", out var unique) && unique.ValueKind == JsonValueKind.True,
                    m.TryGetProperty("depth", out var d) && d.TryGetInt32(out var depth) ? depth : 0)
                { Label = label, Detail = detail, ReactorName = reactorName, IsReactor = isReactor });
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

    private static bool IsIdentifier(string text) =>
        text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_') && text.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
}

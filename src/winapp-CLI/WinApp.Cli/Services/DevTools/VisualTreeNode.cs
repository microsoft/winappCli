// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace WinApp.Cli.Services.DevTools;

/// <summary>
/// One <c>VisualTree.enumerate</c> node. Parse reflection-free, preserve the tap's identity proof,
/// and keep hidden children separate from children filtered out by the CLI.
/// </summary>
internal sealed record VisualTreeNode(
    string Handle,
    string Name,
    string Type,
    string? File,
    string? Id,
    bool UniqueName,
    int ChildCount,
    IReadOnlyList<VisualTreeNode> Children,
    int HiddenChildren)
{
    public IReadOnlyList<DevToolsPreviewValue> Preview { get; init; } = [];

    public string ShortType => DevToolsFormat.ShortTypeName(Type);

    public string? ShortFile => DevToolsFormat.ShortFileName(File);

    public string Selector => DevToolsSelector.Display(Handle, Type, Name, Id, UniqueName);

    public static IReadOnlyList<VisualTreeNode>? ParseForest(string? resultJson)
    {
        if (string.IsNullOrEmpty(resultJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(resultJson, TapWireJson.DocumentOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var roots = new List<VisualTreeNode>(doc.RootElement.GetArrayLength());
            foreach (var node in doc.RootElement.EnumerateArray())
            {
                if (ParseNode(node) is VisualTreeNode parsed)
                {
                    roots.Add(parsed);
                }
                else
                {
                    return null;
                }
            }

            return roots;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static VisualTreeNode? ParseNode(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var handle = ReadString(element, "handle");
        if (handle.Length == 0)
        {
            return null;
        }

        var children = new List<VisualTreeNode>();
        if (element.TryGetProperty("children", out var kids))
        {
            if (kids.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            foreach (var kid in kids.EnumerateArray())
            {
                if (ParseNode(kid) is VisualTreeNode parsed)
                {
                    children.Add(parsed);
                }
                else
                {
                    return null;
                }
            }
        }

        var file = ReadString(element, "file");
        var id = ReadString(element, "id");
        var childCount = element.TryGetProperty("childCount", out var cc) && cc.ValueKind == JsonValueKind.Number && cc.TryGetInt32(out var count)
            ? count
            : children.Count;
        return new VisualTreeNode(
            handle,
            ReadString(element, "name"),
            ReadString(element, "type"),
            file.Length == 0 ? null : file,
            id.Length == 0 ? null : id,
            element.TryGetProperty("uniqueName", out var unique) && unique.ValueKind == JsonValueKind.True,
            childCount,
            children,
            Math.Max(0, childCount - children.Count));
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

internal sealed record AuthoredForest(
    IReadOnlyList<VisualTreeNode> Roots,
    bool SourceInstrumented,
    bool ClassificationTruncated,
    int ClassifiedNodes,
    int CensusNodes,
    int AuthoredNodes)
{
    public static AuthoredForest? Parse(string? resultJson)
    {
        if (string.IsNullOrEmpty(resultJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(resultJson, TapWireJson.DocumentOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("nodes", out var nodes)
                || nodes.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var roots = VisualTreeNode.ParseForest(nodes.GetRawText());
            return roots is null
                ? null
                : new AuthoredForest(
                    roots,
                    ReadBool(root, "sourceInstrumented"),
                    ReadBool(root, "classificationTruncated"),
                    ReadInt(root, "classifiedNodes"),
                    ReadInt(root, "censusNodes"),
                    ReadInt(root, "authoredNodes"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Why this view has nothing to show, or <c>null</c> when it does. The three empty cases are different
    /// facts and only one of them is about the app's markup, so they get different sentences — and the order
    /// matters, because each one names a different next step.
    /// <para>
    /// "No source information at all" is decided FIRST, even mid-classification. The runtime banks a source
    /// file as each element is added, not only when the backfill reaches it, so an instrumented app reports
    /// <c>sourceInstrumented</c> on its very first pass. Absence is therefore strong evidence about the
    /// LAUNCH rather than about how far the classifier has got — and leading with "still classifying" there
    /// would tell a developer to wait for something that will never arrive, when the fix is to relaunch.
    /// The in-progress caveat is still stated, so the answer does not overclaim.
    /// </para>
    /// </summary>
    public string? ExplainIfEmpty()
    {
        if (Roots.Count > 0)
        {
            return null;
        }

        if (!SourceInstrumented)
        {
            var why = "This app reports no XAML source information, so its own elements cannot be told apart " +
                      "from the framework's. Showing the raw tree instead. Launch it with " +
                      "`winapp run --devtools`, which turns source information on.";
            return ClassificationTruncated
                ? why + " (The agent is still classifying, so run the command again if you did launch it that way.)"
                : why;
        }

        return ClassificationTruncated
            ? "The agent has not finished classifying this app's XAML yet, so it cannot yet say which " +
              "elements are yours. Showing the raw tree instead; run the command again in a moment."
            : "The agent classified no live element as app-authored: everything in the tree came " +
              "from framework or template XAML. Showing the raw tree instead.";
    }

    private static bool ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
}

internal sealed record VisualTreeSnapshot(IReadOnlyList<VisualTreeNode> Roots, int RequestedDepth)
{
    public int Count => Roots.Sum(CountNodes);

    /// <summary>
    /// Whether the tap's node-walk limit truncated the reply — as opposed to the depth the caller asked for.
    /// <para>
    /// The tap returns each node's TRUE <c>childCount</c> even where it did not return those children, and its
    /// selection is breadth-first, so a node with hidden children ABOVE the level the request could reach can
    /// only mean the walk ran out of budget: asking for more depth would not reveal them. A node with hidden
    /// children AT that level is the ordinary bounded view, and more depth does expand it. That distinction is
    /// derived here rather than guessed, so hard truncation is never reported as "rerun with a bigger --depth"
    /// and never passes silently.
    /// </para>
    /// </summary>
    public bool HardTruncated => Roots.Any(root => HasHiddenChildrenAbove(root, 1));

    /// <summary>Elements that hid children because the requested depth stopped there (expandable with more depth).</summary>
    public int DepthLimitedElements => Roots.Sum(root => CountDepthLimited(root, 1));

    /// <summary>
    /// The 1-based level at which hidden children are EXPECTED, given the requested depth.
    /// <para>
    /// The tap seeds its walk with the roots and then performs <c>depth</c> expansions
    /// (<c>SelectBoundedBreadthFirst</c>: <c>for (depth = 0; depth &lt; maxDepth; ++depth)</c>), so
    /// <c>--depth D</c> returns levels 1..D+1 and every node at level ≤ D had its children ATTEMPTED. The
    /// edge is therefore D+1, not D. Getting this off by one would excuse real budget truncation at level D
    /// as an expected cut-off AND tell the caller to rerun with a bigger depth that cannot reveal it — the
    /// exact retry loop this derivation exists to prevent. Verified against a live tree: <c>--depth 4</c>
    /// returns five levels.
    /// </para>
    /// <para>0 when the request was unlimited, where no level is an expected cut-off.</para>
    /// </summary>
    private int ExpectedEdgeLevel => RequestedDepth > 0 ? RequestedDepth + 1 : 0;

    private static int CountNodes(VisualTreeNode node) => 1 + node.Children.Sum(CountNodes);

    // `level` is the node's 1-based level within the returned forest.
    private bool HasHiddenChildrenAbove(VisualTreeNode node, int level)
    {
        // An unlimited-depth request (ExpectedEdgeLevel == 0) has no expected cut-off, so ANY hidden child is
        // the budget speaking.
        var atExpectedEdge = ExpectedEdgeLevel > 0 && level >= ExpectedEdgeLevel;
        if (node.HiddenChildren > 0 && !atExpectedEdge)
        {
            return true;
        }

        return node.Children.Any(child => HasHiddenChildrenAbove(child, level + 1));
    }

    private int CountDepthLimited(VisualTreeNode node, int level)
    {
        var atExpectedEdge = ExpectedEdgeLevel > 0 && level >= ExpectedEdgeLevel;
        var self = node.HiddenChildren > 0 && atExpectedEdge ? 1 : 0;
        return self + node.Children.Sum(child => CountDepthLimited(child, level + 1));
    }
}

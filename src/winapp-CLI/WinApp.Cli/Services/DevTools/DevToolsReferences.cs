// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Spectre.Console;

namespace WinApp.Cli.Services.DevTools;

/// <summary>
/// One element reference from <c>Property.references</c>: an accessibility relation (LabeledBy, DescribedBy, FlowsTo,
/// FlowsFrom) or an XY keyboard focus target (XYFocusUp/Down/Left/Right), from this element (<c>out</c>) or to it (<c>in</c>).
/// <see cref="Pending"/>: the target isn't shown: it exists outside the live tree (an unselected tab, a closed popup), or
/// Reactor hasn't created it yet.
/// </summary>
internal sealed record DevToolsReference(string Kind, bool Incoming, string? Handle, string Element, bool Cycle, bool Pending = false)
{
    public string Line => (Incoming ? $"{Element} → this ({Kind})" : $"{Kind} → {Element}") + (Pending ? " (not shown)" : "") + (Cycle ? "  ↺ cycle" : "");
}

internal static class DevToolsReferences
{
    /// <summary>The element's references, or null when the engine can't say (an older engine, or a stale handle).</summary>
    public static (IReadOnlyList<DevToolsReference> Edges, string Raw, bool Truncated)? Read(VisualTreeTap tap, string handle, CancellationToken cancellationToken)
    {
        var response = tap.RequestReferences(handle, cancellationToken);
        if (!response.Ok || response.ResultJson is not { } raw)
        {
            return null;
        }
        return Parse(raw) is { } edges ? (edges.Edges, raw, edges.Truncated) : null;
    }

    internal static (IReadOnlyList<DevToolsReference> Edges, bool Truncated)? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("edges", out var edges) || edges.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            var list = new List<DevToolsReference>();
            foreach (var edge in edges.EnumerateArray())
            {
                if (edge.ValueKind != JsonValueKind.Object ||
                    !edge.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String ||
                    !edge.TryGetProperty("element", out var element) || element.ValueKind != JsonValueKind.String)
                {
                    return null;
                }
                list.Add(new DevToolsReference(kind.GetString()!,
                    edge.TryGetProperty("direction", out var direction) && direction.ValueKind == JsonValueKind.String && direction.GetString() == "in",
                    edge.TryGetProperty("handle", out var target) && target.ValueKind == JsonValueKind.String ? target.GetString() : null,
                    element.GetString()!,
                    edge.TryGetProperty("cycle", out var cycle) && cycle.ValueKind == JsonValueKind.True,
                    edge.TryGetProperty("pending", out var pending) && pending.ValueKind == JsonValueKind.True));
            }
            var truncated = doc.RootElement.TryGetProperty("incomingTruncated", out var partial) && partial.ValueKind == JsonValueKind.True;
            return (list, truncated);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The References block of <c>get-property</c>; nothing when the element has none.</summary>
    public static IEnumerable<string> Lines(IReadOnlyList<DevToolsReference> edges, bool truncated)
    {
        if (edges.Count == 0)
        {
            yield break;
        }
        yield return "  References:";
        foreach (var edge in edges)
        {
            var text = Markup.Escape(edge.Line);
            yield return edge.Cycle ? $"    [yellow]{text}[/]" : edge.Pending ? $"    [grey]{text}[/]" : $"    {text}";
        }
        if (truncated)
        {
            yield return "    [grey]Only part of the tree was searched for elements pointing here.[/]";
        }
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace WinApp.Cli.Services.DevTools;

internal sealed record DevToolsPreviewValue(string Name, string Value, string ValueState, string ValueType, bool Truncated);

/// <summary>
/// An element's AutomationId, which `winapp ui` selects by, and its declaration when the build map confirms one
/// (File is then the project-relative path and Line the declaration's first line).
/// </summary>
internal sealed record DevToolsElementFacts(string? AutomationId, string? File, int Line, int EndLine, int Column);

internal static class DevToolsPreviews
{
    public static (IReadOnlyList<VisualTreeNode> Roots, string? Error) Read(
        VisualTreeTap tap, IReadOnlyList<VisualTreeNode> roots, CancellationToken cancellationToken)
    {
        var (values, facts, error) = Fetch(tap, Flatten(roots).Select(node => node.Handle), cancellationToken);
        VisualTreeNode Enrich(VisualTreeNode node) => node with
        {
            Preview = values.GetValueOrDefault(node.Handle) ?? [],
            Facts = facts.GetValueOrDefault(node.Handle),
            Children = node.Children.Select(Enrich).ToArray(),
        };
        return (roots.Select(Enrich).ToArray(), error);
    }

    /// <summary>Compact values and identity facts for these elements, read in bounded batches on the app's UI thread.</summary>
    public static (Dictionary<string, IReadOnlyList<DevToolsPreviewValue>> Values, Dictionary<string, DevToolsElementFacts> Facts, string? Error)
        Fetch(VisualTreeTap tap, IEnumerable<string> handles, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, IReadOnlyList<DevToolsPreviewValue>>(StringComparer.Ordinal);
        var facts = new Dictionary<string, DevToolsElementFacts>(StringComparer.Ordinal);
        string? error = null;
        foreach (var batch in handles.Chunk(256))
        {
            var response = tap.Request("VisualTree.getPreviews", writer =>
            {
                writer.WriteStartArray("handles");
                foreach (var handle in batch)
                {
                    writer.WriteStringValue(handle);
                }
                writer.WriteEndArray();
            }, cancellationToken: cancellationToken);
            if (!response.Ok)
            {
                error = $"Compact values are incomplete: {response.Error!.Message}";
                break;
            }
            using var document = response.TryParseResult();
            if (document is null || !Parse(document.RootElement, values, facts))
            {
                error = "Compact values are incomplete: the agent returned an invalid preview batch.";
                break;
            }
            if (document.RootElement.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
            {
                error = "Compact values are incomplete: the agent truncated a preview batch.";
                break;
            }
        }
        return (values, facts, error);
    }

    internal static IEnumerable<VisualTreeNode> Flatten(IReadOnlyList<VisualTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children))
            {
                yield return child;
            }
        }
    }

    internal static bool Parse(JsonElement root, Dictionary<string, IReadOnlyList<DevToolsPreviewValue>> values,
        Dictionary<string, DevToolsElementFacts>? facts = null)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("previews", out var previews) || previews.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        foreach (var preview in previews.EnumerateArray())
        {
            if (preview.ValueKind != JsonValueKind.Object ||
                !preview.TryGetProperty("handle", out var handle) || handle.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            var automationId = TryString(preview, "automationId", out var aid) && aid.Length > 0 ? aid : null;
            var line = ReadInt(preview, "line");
            if (facts is not null && (automationId is not null || line > 0))
            {
                facts[handle.GetString()!] = new(automationId, line > 0 && TryString(preview, "file", out var file) ? file : null,
                    line, Math.Max(line, ReadInt(preview, "endLine")), ReadInt(preview, "column"));
            }
            if (!preview.TryGetProperty("values", out var fields))
            {
                continue; // Older agents supply an unlabelled caption, not a property value.
            }
            if (fields.ValueKind != JsonValueKind.Array)
            {
                return false;
            }
            var parsed = new List<DevToolsPreviewValue>();
            foreach (var field in fields.EnumerateArray())
            {
                if (field.ValueKind != JsonValueKind.Object ||
                    !TryString(field, "name", out var name) || !TryString(field, "value", out var value) ||
                    !TryString(field, "valueState", out var state) || !TryString(field, "valueType", out var type) ||
                    !field.TryGetProperty("truncated", out var truncated) ||
                    truncated.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return false;
                }
                parsed.Add(new(name, value, state, type, truncated.GetBoolean()));
            }
            values[handle.GetString()!] = parsed;
        }
        return true;
    }

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = "";
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = property.GetString()!;
        return true;
    }

    public static string Format(IReadOnlyList<DevToolsPreviewValue> values)
    {
        var parts = new List<string>();
        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value.ValueState == "value" && value.Value.Length == 0)
            {
                continue;
            }
            if (value.ValueState == "value" && DevToolsFormat.ShortTypeName(value.ValueType) == "String" &&
                !labels.Add(value.Value) && value.Name != "AutomationProperties.Name")
            {
                continue;
            }
            var text = value.ValueState == "value"
                ? "\"" + JsonEncodedText.Encode(value.Value, System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping) +
                    "\"" + (value.Truncated ? "..." : "")
                : value.ValueState == "object" ? $"<{value.ValueType}>" : value.ValueState == "null" ? "null" : $"<{value.ValueState}>";
            if (value.ValueState == "value" && DevToolsFormat.ShortTypeName(value.ValueType) == "Boolean" &&
                bool.TryParse(value.Value, out var boolean))
            {
                text = boolean ? "true" : "false";
            }
            parts.Add($"{value.Name}={text}");
        }
        return string.Join(" ", parts);
    }
}

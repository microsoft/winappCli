// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace WinApp.Cli.Services.DevTools;

internal sealed record DevToolsPreviewValue(string Name, string Value, string ValueState, string ValueType, bool Truncated);

internal static class DevToolsPreviews
{
    public static (IReadOnlyList<VisualTreeNode> Roots, string? Error) Read(
        VisualTreeTap tap, IReadOnlyList<VisualTreeNode> roots, CancellationToken cancellationToken)
    {
        var nodes = Flatten(roots).ToArray();
        var values = new Dictionary<string, IReadOnlyList<DevToolsPreviewValue>>(StringComparer.Ordinal);
        string? error = null;
        foreach (var batch in nodes.Chunk(256))
        {
            var response = tap.Request("VisualTree.getPreviews", writer =>
            {
                writer.WriteStartArray("handles");
                foreach (var node in batch)
                {
                    writer.WriteStringValue(node.Handle);
                }
                writer.WriteEndArray();
            }, cancellationToken: cancellationToken);
            if (!response.Ok)
            {
                error = $"Compact values are incomplete: {response.Error!.Message}";
                break;
            }
            using var document = response.TryParseResult();
            if (document is null || !Parse(document.RootElement, values))
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
        VisualTreeNode Enrich(VisualTreeNode node) => node with
        {
            Preview = values.GetValueOrDefault(node.Handle) ?? [],
            Children = node.Children.Select(Enrich).ToArray(),
        };
        return (roots.Select(Enrich).ToArray(), error);
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

    internal static bool Parse(JsonElement root, Dictionary<string, IReadOnlyList<DevToolsPreviewValue>> values)
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

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace WinApp.Cli.Services.DevTools;

// name=value is a string; name:=value is raw JSON, so opaque DevTools handles stay strings.
internal static class DevToolsCallParams
{
    internal sealed record Parsed(IReadOnlyList<(string Name, JsonValue Value)> Values, string? Error)
    {
        public bool Ok => Error is null;

        public bool HasStringValues => Values.Any(v => v.Value.Kind == JsonValueKind.String);
    }

    internal readonly record struct JsonValue(JsonValueKind Kind, string Text, JsonElement? Raw = null)
    {
        public void Write(Utf8JsonWriter writer, string name)
        {
            if (Raw is null)
            {
                writer.WriteString(name, Text);
                return;
            }

            writer.WritePropertyName(name);
            Raw.Value.WriteTo(writer);
        }
    }

    public static Parsed Parse(IReadOnlyList<string> arguments)
    {
        var values = new List<(string, JsonValue)>(arguments.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var argument in arguments)
        {
            var equals = argument.IndexOf('=');
            if (equals < 0)
            {
                return new Parsed(
                    [],
                    $"'{argument}' is not a parameter. Use name=value for a string, or name:=value for raw JSON " +
                    "(e.g. appAuthoredOnly:=true, depth:=4).");
            }

            var isRawJson = equals >= 1 && argument[equals - 1] == ':';
            var name = argument[..(isRawJson ? equals - 1 : equals)];
            var raw = argument[(equals + 1)..];
            if (name.Length == 0)
            {
                return new Parsed(
                    [],
                    $"'{argument}' has no parameter name before the '{(isRawJson ? ":=" : "=")}'.");
            }

            if (!seen.Add(name))
            {
                return new Parsed([], $"Parameter '{name}' was given more than once.");
            }

            if (!isRawJson)
            {
                values.Add((name, new JsonValue(JsonValueKind.String, raw)));
                continue;
            }

            if (TryReadJson(raw) is not JsonElement value)
            {
                return new Parsed(
                    [],
                    $"'{name}:={raw}' is not valid JSON. Use {name}={raw} to send it as a string, or correct the " +
                    "JSON (true, false, null, a number, a quoted string, an object, or an array).");
            }

            values.Add((name, new JsonValue(value.ValueKind, raw, value)));
        }

        return new Parsed(values, null);
    }

    private static JsonElement? TryReadJson(string raw)
    {
        if (raw.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

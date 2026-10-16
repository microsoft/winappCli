// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace WinApp.Cli.Services.DevTools;

/// <summary>
/// One row of <c>Property.get</c>: the property's live value, and the part a UI-Automation read cannot answer:
/// WHERE that effective value came from, and whether a binding or a resource produced it.
/// </summary>
/// <param name="Name">The dependency property name.</param>
/// <param name="Value">The effective value, already rendered by the tap (brushes as <c>#AARRGGBB</c>).</param>
/// <param name="ValueType">The runtime type of the value.</param>
/// <param name="ValueSource">The runtime precedence slot: <c>Local</c>, <c>Style</c>, <c>Default</c>…</param>
/// <param name="Binding">The binding expression driving the value, when the live element still reports one.</param>
/// <param name="Authored">What the developer literally wrote, e.g. <c>{ThemeResource TextFillColorSecondaryBrush}</c>.</param>
/// <param name="AuthoredKind">
/// How it was authored: <c>themeResource</c>, <c>staticResource</c>, <c>xBind</c>, <c>binding</c>,
/// <c>templateBinding</c>, <c>customMarkup</c>, or <c>literal</c>.
/// </param>
/// <param name="AuthoredKey">The resource key, for the resource kinds.</param>
/// <param name="WriteType">The XAML type a write must create. Absent means the tap reports it as not settable.</param>
/// <param name="Redacted">The property holds a secret (a password): DevTools neither shows nor writes it.</param>
/// <param name="Origin">Where the value came from, in one label, as the engine reports it.</param>
internal sealed record DevToolsPropertyRow(
    string Name,
    string Value,
    string ValueType,
    string? ValueSource,
    string? Binding,
    string? Authored,
    string? AuthoredKind,
    string? AuthoredKey,
    string? WriteType,
    bool Redacted = false,
    string? Origin = null)
{
    /// <summary>
    /// How this row's provenance reads in one bracketed token: the engine's <c>origin</c>, which names what the developer
    /// authored on this element (a resource and its key, a binding) before the runtime's precedence slot. The DevTools
    /// window's pill is the same label in its short form.
    /// </summary>
    public string? SourceLabel => Origin;

    public static IReadOnlyList<DevToolsPropertyRow>? Parse(string? resultJson)
    {
        if (string.IsNullOrEmpty(resultJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(resultJson, TapWireJson.DocumentOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("props", out var props)
                || props.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var rows = new List<DevToolsPropertyRow>(props.GetArrayLength());
            foreach (var row in props.EnumerateArray())
            {
                if (FromElement(row) is DevToolsPropertyRow parsed)
                {
                    rows.Add(parsed);
                }
                else
                {
                    return null;
                }
            }

            return rows;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static DevToolsPropertyRow? Find(IReadOnlyList<DevToolsPropertyRow>? rows, string property) =>
        rows?.FirstOrDefault(r => string.Equals(r.Name, property, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What to say about a property name the agent doesn't read: the few closest names and how to list every one.
    /// An element has hundreds of properties, so listing all of them buries the likely typo.
    /// </summary>
    public static string UnknownPropertyHint(IEnumerable<string> names, string requested, string? selector)
    {
        var closest = ClosestNames(names, requested);
        var all = $"`winapp devtools get-property {selector ?? "<selector>"} --all` lists every property.";
        return closest.Count == 0 ? all : $"Closest: {string.Join(", ", closest)}. {all}";
    }

    internal static IReadOnlyList<string> ClosestNames(IEnumerable<string> names, string requested, int max = 5)
    {
        var wanted = requested.Trim();
        return names.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => (Name: name, Score: Score(name)))
            .Where(candidate => candidate.Score < int.MaxValue)
            .OrderBy(candidate => candidate.Score).ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max).Select(candidate => candidate.Name).ToList();

        int Score(string name)
        {
            var member = name[(name.LastIndexOf('.') + 1)..];
            if (member.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }
            if (wanted.Length >= 3 && (member.StartsWith(wanted, StringComparison.OrdinalIgnoreCase) || name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)))
            {
                return 1;
            }
            if (wanted.Length >= 3 && name.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }
            var distance = EditDistance(member.ToUpperInvariant(), wanted.ToUpperInvariant());
            return distance <= Math.Max(2, wanted.Length / 3) ? 10 + distance : int.MaxValue;
        }
    }

    private static int EditDistance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1], previous[j]) + 1, previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }
            previous = current;
        }
        return previous[b.Length];
    }

    public static DevToolsPropertyRow? FromElement(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!row.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String ||
            !row.TryGetProperty("valueType", out var type) || type.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        foreach (var field in new[] { "valueSource", "binding", "authored", "authoredKind", "authoredKey", "writeType", "origin" })
        {
            if (row.TryGetProperty(field, out var optional) &&
                optional.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                return null;
            }
        }
        var name = ReadString(row, "name");
        return name.Length == 0
            ? null
            : new DevToolsPropertyRow(
                name,
                ReadString(row, "value"),
                ReadString(row, "valueType"),
                NullIfEmpty(ReadString(row, "valueSource")),
                NullIfEmpty(ReadString(row, "binding")),
                NullIfEmpty(ReadString(row, "authored")),
                NullIfEmpty(ReadString(row, "authoredKind")),
                NullIfEmpty(ReadString(row, "authoredKey")),
                NullIfEmpty(ReadString(row, "writeType")),
                row.TryGetProperty("redacted", out var redacted) && redacted.ValueKind == JsonValueKind.True,
                NullIfEmpty(ReadString(row, "origin")));
    }

    public bool IsSet =>
        Binding is not null
        || (AuthoredKind is not null && AuthoredKind != "literal")
        || (ValueSource is not null && !ValueSource.Equals("default", StringComparison.OrdinalIgnoreCase));

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace WinApp.Cli.Services.DevTools;

/// <summary>
/// One row of <c>Property.get</c>: the property's live value, and the part a UI-Automation read cannot answer —
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
    bool Redacted = false)
{
    /// <summary>
    /// How this row's provenance reads in one bracketed token.
    /// <para>
    /// Derived from <see cref="AuthoredKind"/> FIRST, and from <see cref="ValueSource"/> only when nothing was
    /// authored on this element. The two fields answer different questions and both answer truthfully: the
    /// runtime resolves a <c>{ThemeResource}</c> to a plain local value, so <c>valueSource</c> genuinely says
    /// <c>Local</c> — and a label built from it alone prints "Local" for a brush the developer wrote as
    /// <c>{ThemeResource TextFillColorSecondaryBrush}</c>, which is the one thing this command exists to tell
    /// them. This mirrors <c>OriginLabel</c> in <c>DevToolsWindow.cpp</c>, which resolved the same bug for the
    /// in-app pane; the two surfaces must not disagree about the same wire fields.
    /// </para>
    /// </summary>
    public string? SourceLabel => AuthoredKind switch
    {
        "themeResource" => Describe("ThemeResource"),
        "staticResource" => Describe("StaticResource"),
        "xBind" => Authored ?? "x:Bind",
        "binding" => Authored ?? Binding ?? "Binding",
        "templateBinding" => Authored ?? "TemplateBinding",
        "customMarkup" => Authored ?? "Markup",
        // A literal authored on this element IS a local value, and valueSource says so in the runtime's own
        // vocabulary. Nothing to correct.
        _ => Binding ?? ValueSource,
    };

    private string Describe(string kind) =>
        string.IsNullOrEmpty(AuthoredKey) ? kind : $"{kind} {AuthoredKey}";

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
        foreach (var field in new[] { "valueSource", "binding", "authored", "authoredKind", "authoredKey", "writeType" })
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
                row.TryGetProperty("redacted", out var redacted) && redacted.ValueKind == JsonValueKind.True);
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

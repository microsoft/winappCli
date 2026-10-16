// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace WinApp.Cli.Services.DevTools;

/// <summary>A WinUI property the element's C# sets: from the element record (<c>TextBlock("hi")</c>) or a modifier (<c>.Width(200)</c>).</summary>
internal sealed record ReactorOwnedProperty(string Property, string Name, string Value, string From, bool Redacted);

/// <summary>An event handler the element's C# sets (<c>OnClick</c>), what it runs, and whether invoke-handler can call it.</summary>
internal sealed record ReactorHandlerInfo(string Event, string Method, string Signature, bool Invokable, bool NeedsValue, string? File, int Line)
{
    public string? ShortSource => File is null ? null : ReactorNode.Short(File, Line);
}

/// <summary>
/// What Microsoft.UI.Reactor set on one element (<c>Reactor.element</c>): its kind, key and C# call site, the WinUI
/// properties its record and modifiers set, the component that rendered it, and the props of the component it mounts.
/// </summary>
internal sealed record ReactorElementInfo(
    string? Element,
    string? Key,
    string? File,
    int Line,
    int Column,
    string? Owner,
    ReactorComponent? Component,
    IReadOnlyList<ReactorOwnedProperty> Owned,
    string? ComponentPropsType,
    IReadOnlyList<(string Name, string Value, bool Redacted)>? ComponentProps)
{
    /// <summary>The element's own description, or null for a control no Reactor element produced (or a non-Reactor app).</summary>
    public static ReactorElementInfo? Read(VisualTreeTap tap, string handle, CancellationToken cancellationToken)
    {
        var response = tap.RequestReactorElement(handle, cancellationToken);
        return response.Ok ? Parse(response.ResultJson) : null;
    }

    public static ReactorElementInfo? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(json, TapWireJson.DocumentOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || ReactorDescription.ReadString(root, "state") != "ok")
            {
                return null;
            }
            var tagged = root.TryGetProperty("tagged", out var t) && t.ValueKind == JsonValueKind.True;
            var hasProps = root.TryGetProperty("componentProps", out var cp) && cp.ValueKind == JsonValueKind.Object;
            if (!tagged && !hasProps)
            {
                return null;
            }

            var owned = new List<ReactorOwnedProperty>();
            void Read(string member, string from, string nameField)
            {
                if (!root.TryGetProperty(member, out var array) || array.ValueKind != JsonValueKind.Array)
                {
                    return;
                }
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || ReactorDescription.ReadString(item, "property") is not { } property)
                    {
                        continue;
                    }
                    owned.Add(new ReactorOwnedProperty(property, ReactorDescription.ReadString(item, nameField) ?? property,
                        item.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "",
                        from, item.TryGetProperty("redacted", out var r) && r.ValueKind == JsonValueKind.True));
                }
            }
            Read("props", "element", "name");
            Read("modifiers", "modifier", "modifier");

            List<(string, string, bool)>? props = null;
            if (hasProps)
            {
                props = [];
                if (cp.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
                {
                    foreach (var value in values.EnumerateArray())
                    {
                        if (value.ValueKind == JsonValueKind.Object && ReactorDescription.ReadString(value, "name") is { } name)
                        {
                            props.Add((name, value.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "",
                                value.TryGetProperty("redacted", out var r) && r.ValueKind == JsonValueKind.True));
                        }
                    }
                }
            }

            ReactorComponent? component = null;
            if (root.TryGetProperty("component", out var c) && c.ValueKind == JsonValueKind.Object)
            {
                component = new(ReactorDescription.ReadString(c, "kind") ?? "class", ReactorDescription.ReadString(c, "name") ?? "Component",
                    ReactorDescription.ReadString(c, "type"));
            }

            return new ReactorElementInfo(
                ReactorDescription.ReadString(root, "element"),
                ReactorDescription.ReadString(root, "key"),
                ReactorDescription.ReadString(root, "fileOnDisk") ?? ReactorDescription.ReadString(root, "file"),
                root.TryGetProperty("line", out var line) && line.TryGetInt32(out var l) ? l : 0,
                root.TryGetProperty("column", out var column) && column.TryGetInt32(out var col) ? col : 0,
                ReactorDescription.ReadString(root, "owner"),
                component,
                owned,
                hasProps ? ReactorDescription.ReadString(cp, "type") : null,
                props)
            {
                ThemeBindings = ReadThemeBindings(root),
                Handlers = ReadHandlers(root),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Properties that follow a theme resource through Reactor's ThemeRef, by WinUI property name.</summary>
    public IReadOnlyDictionary<string, string> ThemeBindings { get; init; } = new Dictionary<string, string>();

    /// <summary>The event handlers the element's C# sets.</summary>
    public IReadOnlyList<ReactorHandlerInfo> Handlers { get; init; } = [];

    /// <summary>The theme resource a property follows (<c>AccentFillColorDefaultBrush</c>), when its C# uses a ThemeRef.</summary>
    public string? ThemeResourceOf(string property) =>
        ThemeBindings.FirstOrDefault(pair => string.Equals(pair.Key, property, StringComparison.OrdinalIgnoreCase)).Value;

    private static Dictionary<string, string> ReadThemeBindings(JsonElement root)
    {
        var bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("themeBindings", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (ReactorDescription.ReadString(item, "property") is { } property && ReactorDescription.ReadString(item, "resourceKey") is { } key)
                {
                    bindings[property] = key;
                }
            }
        }
        return bindings;
    }

    private static List<ReactorHandlerInfo> ReadHandlers(JsonElement root)
    {
        var handlers = new List<ReactorHandlerInfo>();
        if (root.TryGetProperty("handlers", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (ReactorDescription.ReadString(item, "event") is not { } name)
                {
                    continue;
                }
                handlers.Add(new ReactorHandlerInfo(name, ReactorDescription.ReadString(item, "method") ?? "",
                    ReactorDescription.ReadString(item, "signature") ?? "()",
                    item.TryGetProperty("invokable", out var invokable) && invokable.ValueKind == JsonValueKind.True,
                    item.TryGetProperty("needsValue", out var needsValue) && needsValue.ValueKind == JsonValueKind.True,
                    ReactorDescription.ReadString(item, "file"),
                    item.TryGetProperty("line", out var line) && line.TryGetInt32(out var l) ? l : 0));
            }
        }
        return handlers;
    }

    /// <summary>The C# call site as <c>App.cs:35</c>.</summary>
    public string? ShortSource => File is null ? null : ReactorNode.Short(File, Line);

    /// <summary>The component a re-render of which reapplies the element's values: the one that rendered it.</summary>
    public string? RenderedBy => Owner ?? Component?.Name;

    /// <summary>Whether the element's C# sets this WinUI property (record property or modifier).</summary>
    public ReactorOwnedProperty? OwnerOf(string property) =>
        Owned.FirstOrDefault(owned => string.Equals(owned.Property, property, StringComparison.OrdinalIgnoreCase));

    /// <summary>The one line a write to a Reactor-owned property is followed by.</summary>
    public string OwnedWarning(string property)
    {
        var site = ShortSource is { } s ? $" ({s})" : "";
        var component = RenderedBy is { } name ? $"<{name}>" : "its component";
        return $"{property} is set in C#{site}; Reactor reapplies its C# value when {component} re-renders with a changed value.";
    }
}

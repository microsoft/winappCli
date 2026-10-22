// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Spectre.Console;

namespace WinApp.Cli.Services.DevTools;

/// <summary>One hook cell of a Reactor component, as <c>Reactor.component</c> reports it.</summary>
internal sealed record ReactorHookInfo(int Index, string Kind, string? Type, string? Value, bool Redacted, string? Context,
    bool CanSet, bool Editable, bool Migrated)
{
    /// <summary>The C# variable the hook was assigned to, when Reactor recorded it (<c>var (count, setCount) = UseState(0)</c>).</summary>
    public string? Name { get; init; }

    /// <summary>The line of the hook's Use* call, when Reactor recorded it; 0 otherwise.</summary>
    public int Line { get; init; }
}

/// <summary>A context a component reads, with the value it read at its last render.</summary>
internal sealed record ReactorContextInfo(int Hook, string Name, string? Type, string Value, bool Redacted, bool IsDefault);

/// <summary>Where C# declares or defines a component; <c>Stale</c> when the file changed since the build.</summary>
internal sealed record ReactorCodeLocation(string File, int Line, int Column, string? Method, bool? Stale)
{
    public string Short => ReactorNode.Short(File, Line);
}

/// <summary>
/// The component that renders an element (<c>Reactor.component</c>): identity, where it is declared and defined, its
/// props, hooks in call order, and the contexts it reads.
/// </summary>
internal sealed record ReactorComponentState(
    string Name,
    string Kind,
    bool IsRoot,
    bool Exact,
    string Api,
    bool? Idle,
    ReactorCodeLocation? Declared,
    ReactorCodeLocation? Definition,
    string? PropsType,
    IReadOnlyList<(string Name, string Value, bool Redacted)>? Props,
    IReadOnlyList<ReactorHookInfo> Hooks,
    IReadOnlyList<ReactorContextInfo> Contexts)
{
    /// <summary>
    /// Hooks Reactor named whose index is not known statically (after a hook in a branch, a loop or a composite
    /// hook): listed by name, but not matched to a live hook, so set-state cannot address them by name.
    /// </summary>
    public IReadOnlyList<(string Name, int Line)> UnindexedHooks { get; init; } = [];

    /// <summary>
    /// The hook a set-state argument names: its index (<c>0</c>), or the C# variable Reactor recorded for it
    /// (<c>count</c>). Null with the reason when it names none, or more than one.
    /// </summary>
    public (ReactorHookInfo? Hook, string? Problem) FindHook(string text)
    {
        if (int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index))
        {
            return Hooks.FirstOrDefault(h => h.Index == index) is { } byIndex
                ? (byIndex, null)
                : (null, $"<{Name}> has no hook {index}.");
        }
        var named = Hooks.Where(h => string.Equals(h.Name, text, StringComparison.Ordinal)).ToList();
        if (named.Count == 0)
        {
            named = [.. Hooks.Where(h => string.Equals(h.Name, text, StringComparison.OrdinalIgnoreCase))];
        }
        if (named.Count == 1)
        {
            return (named[0], null);
        }
        if (named.Count > 1)
        {
            return (null, $"<{Name}> has {named.Count} hooks named '{text}' ({string.Join(", ", named.Select(h => $"[{h.Index}]"))}). Use the index.");
        }
        if (UnindexedHooks.Any(h => string.Equals(h.Name, text, StringComparison.OrdinalIgnoreCase)))
        {
            return (null, $"'{text}' is declared after a hook in a branch, a loop or a composite hook, so its index is not known " +
                "statically. Use the index `get-state` lists for it.");
        }
        return (null, Hooks.Any(h => h.Name is not null)
            ? $"<{Name}> has no hook named '{text}'."
            : $"'{text}' is not a hook index, and this Reactor version does not record hook names.");
    }

    /// <summary>The answer and, when there is no component to show, why (Reactor absent, unavailable, or none here).</summary>
    public static (ReactorComponentState? State, string? Json, string? Problem, string? Token) Read(
        VisualTreeTap tap, string handle, CancellationToken cancellationToken)
    {
        var response = tap.RequestReactorComponent(handle, cancellationToken);
        if (!response.Ok)
        {
            return (null, null, DevToolsErrors.Describe(response.Error!), response.Error!.Token);
        }
        var (state, problem, token) = Parse(response.ResultJson);
        return (state, response.ResultJson, problem, token);
    }

    public static (ReactorComponentState? State, string? Problem, string? Token) Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return (null, "The DevTools agent returned no component.", "invalid-response");
        }
        try
        {
            using var doc = JsonDocument.Parse(json, TapWireJson.DocumentOptions);
            var root = doc.RootElement;
            var state = ReactorDescription.ReadString(root, "state");
            switch (state)
            {
                case "ok":
                    break;
                case "absent":
                    return (null, "This app does not use Microsoft.UI.Reactor.", "reactor-absent");
                case "none":
                    return (null, "No Reactor component renders this element.", "no-component");
                default:
                    var reason = ReactorDescription.ReadString(root, "reason");
                    return (null, ReactorDescription.IsBusy(reason) ? ReactorDescription.BusyMessage : "Reactor component state is unavailable: " +
                        (reason ?? "the managed DevTools agent did not answer") + ".", "reactor-unavailable");
            }
            if (!root.TryGetProperty("component", out var component) || component.ValueKind != JsonValueKind.Object)
            {
                return (null, "The DevTools agent returned an unreadable component.", "invalid-response");
            }

            List<(string, string, bool)>? props = null;
            string? propsType = null;
            if (root.TryGetProperty("props", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                propsType = ReactorDescription.ReadString(p, "type");
                props = [];
                if (p.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
                {
                    foreach (var value in values.EnumerateArray())
                    {
                        if (ReactorDescription.ReadString(value, "name") is { } name)
                        {
                            props.Add((name, ReactorDescription.ReadString(value, "value") ?? "", Bool(value, "redacted")));
                        }
                    }
                }
            }

            var hooks = new List<ReactorHookInfo>();
            if (root.TryGetProperty("hooks", out var h) && h.ValueKind == JsonValueKind.Array)
            {
                foreach (var hook in h.EnumerateArray())
                {
                    if (hook.ValueKind != JsonValueKind.Object || !hook.TryGetProperty("index", out var index) || !index.TryGetInt32(out var i))
                    {
                        continue;
                    }
                    hooks.Add(new ReactorHookInfo(i, ReactorDescription.ReadString(hook, "kind") ?? "unknown",
                        ReactorDescription.ReadString(hook, "type"), ReactorDescription.ReadString(hook, "value"), Bool(hook, "redacted"),
                        ReactorDescription.ReadString(hook, "context"), Bool(hook, "canSet"), Bool(hook, "editable"), Bool(hook, "migrated"))
                    {
                        Name = ReactorDescription.ReadString(hook, "name"),
                        Line = hook.TryGetProperty("line", out var hookLine) && hookLine.TryGetInt32(out var l) ? l : 0,
                    });
                }
            }

            var contexts = new List<ReactorContextInfo>();
            if (root.TryGetProperty("contexts", out var c) && c.ValueKind == JsonValueKind.Array)
            {
                foreach (var context in c.EnumerateArray())
                {
                    if (ReactorDescription.ReadString(context, "name") is { } name)
                    {
                        contexts.Add(new ReactorContextInfo(context.TryGetProperty("hook", out var at) && at.TryGetInt32(out var hookIndex) ? hookIndex : -1,
                            name, ReactorDescription.ReadString(context, "type"), ReactorDescription.ReadString(context, "value") ?? "",
                            Bool(context, "redacted"), Bool(context, "isDefault")));
                    }
                }
            }

            return (new ReactorComponentState(
                ReactorDescription.ReadString(component, "name") ?? "Component",
                ReactorDescription.ReadString(component, "kind") ?? "class",
                Bool(component, "isRoot"),
                Bool(root, "exact"),
                ReactorDescription.ReadString(root, "api") ?? "reactor",
                root.TryGetProperty("idle", out var idle) && idle.ValueKind is JsonValueKind.True or JsonValueKind.False ? idle.GetBoolean() : null,
                Location(root, "declared"),
                Location(root, "definition"),
                propsType,
                props,
                hooks,
                contexts)
            { UnindexedHooks = Unindexed(root) }, null, null);
        }
        catch (JsonException)
        {
            return (null, "The DevTools agent returned an unreadable component.", "invalid-response");
        }
    }

    private static IReadOnlyList<(string Name, int Line)> Unindexed(JsonElement root)
    {
        if (!root.TryGetProperty("unindexedHooks", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return [.. list.EnumerateArray()
            .Where(h => h.ValueKind == JsonValueKind.Object && ReactorDescription.ReadString(h, "name") is not null)
            .Select(h => (ReactorDescription.ReadString(h, "name")!,
                h.TryGetProperty("line", out var line) && line.TryGetInt32(out var l) ? l : 0))];
    }

    private static ReactorCodeLocation? Location(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var at) || at.ValueKind != JsonValueKind.Object ||
            (ReactorDescription.ReadString(at, "fileOnDisk") ?? ReactorDescription.ReadString(at, "file")) is not { } file)
        {
            return null;
        }
        return new ReactorCodeLocation(file,
            at.TryGetProperty("line", out var line) && line.TryGetInt32(out var l) ? l : 0,
            at.TryGetProperty("column", out var column) && column.TryGetInt32(out var col) ? col : 0,
            ReactorDescription.ReadString(at, "method"),
            at.TryGetProperty("stale", out var stale) && stale.ValueKind is JsonValueKind.True or JsonValueKind.False ? stale.GetBoolean() : null);
    }

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>The kind as a reader says it: class component, function component, memo.</summary>
    public string KindLabel => (IsRoot ? "root " : "") + Kind switch
    {
        "function" => "function component",
        "memo" => "memo component",
        _ => "class component",
    };

    /// <summary>The hooks that hold a value (not effects or navigation callbacks).</summary>
    public IEnumerable<ReactorHookInfo> ValueHooks => Hooks.Where(h => h.Value is not null);

    /// <summary>
    /// The lines the human output prints for this component, in Spectre markup: identity, where it is defined and
    /// declared, its props, hooks and contexts. <paramref name="highlight"/> marks values the shown value contains.
    /// </summary>
    public IEnumerable<string> Lines(string indent = "  ", string? highlight = null)
    {
        var head = $"{indent}[bold]<{Markup.Escape(Name)}>[/] [grey]{Markup.Escape(KindLabel)}";
        if (Definition is { } definition)
        {
            head += $" · defined {Markup.Escape(definition.Short)}" + (definition.Method is { } method ? $" ({Markup.Escape(method)})" : "");
        }
        if (Declared is { } declared)
        {
            head += $" · declared {Markup.Escape(declared.Short)}";
        }
        yield return head + "[/]";
        if (Definition is { Stale: true } changed)
        {
            yield return $"{indent}  [yellow]{Markup.Escape(Path.GetFileName(changed.File))} changed since the app was built; lines may have moved.[/]";
        }
        if (Props is { } props)
        {
            yield return props.Count == 0
                ? $"{indent}  [grey]No props.[/]"
                : $"{indent}  Props{(PropsType is { } type ? $" [grey]({Markup.Escape(type)})[/]" : "")}:";
            foreach (var (name, value, _) in props)
            {
                yield return $"{indent}    {Markup.Escape(name)} = {Value(value, highlight)}";
            }
        }
        if (Hooks.Count == 0)
        {
            yield return $"{indent}  [grey]No hooks.[/]";
        }
        else
        {
            yield return $"{indent}  Hooks:";
            foreach (var hook in Hooks)
            {
                var line = $"{indent}    [[{hook.Index}]] " + (hook.Name is { } hookName ? $"[bold]{Markup.Escape(hookName)}[/] " : "") +
                    Markup.Escape(hook.Kind);
                if (hook.Type is { } type) { line += $" {Markup.Escape(type)}"; }
                if (hook.Value is { } value) { line += " = " + Value(value, highlight); }
                if (hook.Context is { } context) { line += $" [grey]({Markup.Escape(context)})[/]"; }
                if (hook.Editable) { line += " [grey]settable[/]"; }
                if (hook.Migrated) { line += " [grey](migrated by hot reload)[/]"; }
                yield return line;
            }
            foreach (var (name, at) in UnindexedHooks)
            {
                yield return $"{indent}    [[?]] [bold]{Markup.Escape(name)}[/]" + (at > 0 ? $" [grey]line {at}[/]" : "") +
                    " [grey](its index is not known statically)[/]";
            }
        }
        foreach (var context in Contexts)
        {
            yield return $"{indent}  Context {Markup.Escape(context.Name)} = {Value(context.Value, highlight)}" +
                (context.IsDefault ? " [grey](default: no provider above)[/]" : "");
        }
    }

    /// <summary>Whether a prop, hook or context value appears in the value an element shows (a likely source of it).</summary>
    internal static bool Feeds(string value, string? shown)
    {
        // Text values are shown quoted ("Ada"); what the element shows is the text itself.
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
        }
        // A value with no letter or digit (a dash, a comma) appears everywhere and says nothing.
        if (shown is not { Length: > 0 } || !value.Any(char.IsLetterOrDigit) || value is "null" or "True" or "False" or "<redacted>")
        {
            return false;
        }
        // Whole values only: 1 and 3 are not shown by "Done today: 13", and 3 is not shown by "1.3".
        for (var at = shown.IndexOf(value, StringComparison.Ordinal); at >= 0;
             at = shown.IndexOf(value, at + 1, StringComparison.Ordinal))
        {
            var end = at + value.Length;
            if (!ContinuesToken(shown, at - 1, at - 2, value[0]) && !ContinuesToken(shown, end, end + 1, value[^1]))
            {
                return true;
            }
        }
        return false;
    }

    // Whether the character next to a match continues the token the match is part of.
    private static bool ContinuesToken(string text, int next, int beyond, char edge)
    {
        if (next < 0 || next >= text.Length || !char.IsLetterOrDigit(edge))
        {
            return false;
        }
        var neighbor = text[next];
        // A digit next to a decimal or group separator that has another digit beyond it: 3 in 1.3 or 3,5.
        return char.IsLetterOrDigit(neighbor) || neighbor == '_' ||
            char.IsDigit(edge) && neighbor is '.' or ',' && beyond >= 0 && beyond < text.Length && char.IsDigit(text[beyond]);
    }

    private static string Value(string value, string? highlight) =>
        Feeds(value, highlight) ? $"[green]{Markup.Escape(value)}[/]" : Markup.Escape(value);
}

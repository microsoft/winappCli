// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections;
using System.Reflection;
using Microsoft.UI.Xaml;

namespace WinApp.DevTools.Managed;

/// <summary>
/// One value of Reactor's <c>ComponentSnapshot</c> (a <c>DiagnosticValue</c>): a prop, a hook or a context, formatted
/// by Reactor. Reactor does not redact, so a value whose name, type or text says it is a secret
/// (<see cref="ReactorSecrets"/>) is replaced here and is never editable.
/// </summary>
internal sealed record ReactorShownValue(int Index, string Name, string Kind, string Type, string Value, bool Editable,
    bool Redacted, bool Migrated)
{
    internal static ReactorShownValue? From(object? value, string? contextName = null)
    {
        if (value is null)
        {
            return null;
        }
        var name = ReactorReflect.Member(value, "Name") as string ?? "";
        var type = ReactorReflect.Member(value, "Type") as string ?? "";
        var text = ReactorReflect.Member(value, "Value") as string ?? "";
        var secret = ReactorSecrets.IsSecretName(name) || ReactorSecrets.IsSecretName(contextName) ||
            ReactorSecrets.IsSecretTypeName(type) || ReactorSecrets.HasLabelledSecret(text);
        return new ReactorShownValue(
            ReactorReflect.Member(value, "Index") is int index ? index : -1,
            name,
            ReactorReflect.Member(value, "Kind") as string ?? "unknown",
            type,
            secret ? ReactorElementProps.Redacted : text,
            !secret && ReactorReflect.Member(value, "Editable") is true,
            secret,
            ReactorReflect.Member(value, "MigratedByHotReload") is true);
    }
}

/// <summary>
/// Reactor's <c>ComponentSnapshot</c> from <c>ReactorDiagnostics.DescribeComponent</c>: the component's name and kind,
/// and its props, hooks and contexts as text. Reactor formats every value; DevTools redacts secrets (see
/// <see cref="ReactorShownValue"/>), including a context hook whose context is named as one.
/// </summary>
internal sealed record ReactorSnapshot(string Name, string Kind, bool IsRoot, IReadOnlyList<ReactorShownValue> Props,
    IReadOnlyList<ReactorShownValue> State, IReadOnlyList<ReactorShownValue> Contexts)
{
    internal static ReactorSnapshot? From(object? snapshot)
    {
        if (snapshot is null || ReactorReflect.Member(snapshot, "Name") is not string { Length: > 0 } name)
        {
            return null;
        }
        var kind = (ReactorReflect.Member(snapshot, "Kind") as string)?.ToLowerInvariant() switch
        {
            "function" => "function",
            "memo" => "memo",
            _ => "class",
        };
        var contexts = Values(snapshot, "Contexts");
        var contextNames = contexts.GroupBy(context => context.Index).ToDictionary(group => group.Key, group => group.First().Name);
        return new ReactorSnapshot(name, kind, ReactorReflect.Member(snapshot, "IsRoot") is true,
            Values(snapshot, "Props"), Values(snapshot, "State", contextNames), contexts);
    }

    // A context hook reads the context of the same index, whose name says whether its value is a secret.
    private static IReadOnlyList<ReactorShownValue> Values(object snapshot, string member, IReadOnlyDictionary<int, string>? contextNames = null)
    {
        var list = new List<ReactorShownValue>();
        if (ReactorReflect.Member(snapshot, member) is IEnumerable values)
        {
            foreach (var value in values)
            {
                var contextName = contextNames is not null && ReactorReflect.Member(value, "Kind") as string == "context" &&
                    ReactorReflect.Member(value, "Index") is int index ? contextNames.GetValueOrDefault(index) : null;
                if (ReactorShownValue.From(value, contextName) is { } shown)
                {
                    list.Add(shown);
                }
            }
        }
        return list;
    }
}

/// <summary>
/// A component described by Reactor's text diagnostics (<c>DescribeComponent</c>, <c>TrySetState</c>,
/// <c>Rerender</c>). Its values are Reactor's own text; where it is declared and defined still comes from the element
/// record that mounted it (or, for a root, the one component type of that name).
/// </summary>
internal sealed class SnapshotReactorComponent(
    ReactorSnapshot snapshot,
    Type? componentType,
    Delegate? renderFunc,
    object? declaringElement,
    string? propsType,
    Func<int, string, (bool Ok, string? Error)> setState,
    Func<bool> rerender) : ReactorComponentHandle
{
    public ReactorSnapshot Snapshot { get; } = snapshot;

    /// <summary>The props record's type as C# reads it, when the element record that mounted the component says.</summary>
    public string? PropsType { get; } = propsType;

    public override string Api => "reactor";
    public override string Kind => Snapshot.Kind;
    public override bool IsRoot => Snapshot.IsRoot;
    public override string Name => Snapshot.Name;
    public override Type? ComponentType => componentType;
    public override Delegate? RenderFunc => renderFunc;
    public override object? Props => null;
    public override object? DeclaringElement => declaringElement;
    public override bool IsMounted => true;

    // Values are text on this path: see Snapshot.
    public override IReadOnlyList<ReactorHook> Hooks() => [];
    public override IReadOnlyList<ReactorContextRead> Contexts() => [];
    public override bool TrySet(int index, object? value) => false;

    /// <summary>Reactor's <c>TrySetState</c>: parses the text to the hook's type; the error is a sentence to show as is.</summary>
    public (bool Ok, string? Error) TrySetText(int index, string text) => setState(index, text);

    public override bool Rerender() => rerender();

    /// <summary>The hook of this index, if Reactor reported one.</summary>
    public ReactorShownValue? Hook(int index) => Snapshot.State.FirstOrDefault(h => h.Index == index);
}

internal sealed partial class ReactorApi
{
    private static readonly Dictionary<string, Type?> ComponentTypes = new(StringComparer.Ordinal);

    /// <summary>Whether Reactor reports components as text (<c>ReactorDiagnostics.DescribeComponent</c>).</summary>
    public bool TextDiagnostics => describeComponent is not null;

    /// <summary>Reactor's <c>DescribeComponent</c> for the component mounted at a wrapper or host root; null elsewhere.</summary>
    internal ReactorSnapshot? DescribeComponent(UIElement control)
    {
        if (describeComponent is null)
        {
            return null;
        }
        try { return ReactorSnapshot.From(describeComponent.Invoke(null, [control])); }
        catch (Exception ex) when (ex is TargetInvocationException or InvalidCastException or MemberAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The component Reactor describes at <paramref name="control"/>, with what its element record adds.</summary>
    private SnapshotReactorComponent? SnapshotComponentAt(UIElement control)
    {
        if (DescribeComponent(control) is not { } snapshot)
        {
            return null;
        }
        var tag = GetTag(control);
        var mounted = tag is not null && (componentElement?.IsInstanceOfType(tag) == true || funcElement?.IsInstanceOfType(tag) == true ||
            memoElement?.IsInstanceOfType(tag) == true);
        var type = mounted && componentType?.GetValue(tag) is Type declared ? declared
            : snapshot.Kind == "class" ? ComponentTypeNamed(snapshot.Name) : null;
        var render = mounted ? tag!.GetType().GetProperty("RenderFunc")?.GetValue(tag) as Delegate : null;
        var props = mounted && componentElement?.IsInstanceOfType(tag) == true ? componentProps?.GetValue(tag) : null;
        return new SnapshotReactorComponent(snapshot, type, render, mounted ? tag : null,
            props is null ? null : ReactorComponents.FriendlyTypeName(props.GetType()),
            (index, text) => SetState(control, index, text),
            () => Rerender(control));
    }

    /// <summary>Reactor's <c>TrySetState</c>; the error is Reactor's own sentence.</summary>
    internal (bool Ok, string? Error) SetState(UIElement control, int index, string text)
    {
        if (trySetState is null)
        {
            return (false, "this Reactor version cannot set state from text");
        }
        try
        {
            var args = new object?[] { control, index, text, null };
            return trySetState.Invoke(null, args) is true ? (true, null) : (false, args[3] as string);
        }
        catch (Exception ex) when (ex is TargetInvocationException or InvalidCastException or MemberAccessException or ArgumentException)
        {
            return (false, (ex.InnerException ?? ex).Message);
        }
    }

    internal bool Rerender(UIElement control)
    {
        try { return rerenderComponent?.Invoke(null, [control]) is true; }
        catch (Exception ex) when (ex is TargetInvocationException or InvalidCastException or MemberAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The one class component type with this display name in the app, for a component Reactor names but no element
    /// record mounts (a host's root): it gives the full type name and where <c>Render</c> is defined. Null when no type
    /// or several types have the name.
    /// </summary>
    internal Type? ComponentTypeNamed(string name)
    {
        lock (ComponentTypes)
        {
            if (ComponentTypes.TryGetValue(name, out var known))
            {
                return known;
            }
            Type? found = null;
            var ambiguous = false;
            var reactor = reactorAssembly.GetName().Name;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic || assembly == reactorAssembly ||
                    !assembly.GetReferencedAssemblies().Any(reference => reference.Name == reactor))
                {
                    continue;
                }
                Type?[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                foreach (var type in types)
                {
                    if (type is null || type.IsAbstract || !type.IsClass || TypeName(type) != name ||
                        type.GetMethod("Render", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes) is null)
                    {
                        continue;
                    }
                    ambiguous |= found is not null;
                    found = type;
                }
            }
            return ComponentTypes[name] = ambiguous ? null : found;
        }
    }
}

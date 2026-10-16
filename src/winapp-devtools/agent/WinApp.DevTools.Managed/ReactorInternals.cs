// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections;
using System.Reflection;
using Microsoft.UI.Xaml;

namespace WinApp.DevTools.Managed;

/// <summary>
/// The one place DevTools reads Microsoft.UI.Reactor internals: the adapter for Reactor versions without the public
/// diagnostics API (published 0.1.0-preview.16 and earlier). <see cref="ReactorApi"/> creates it only when
/// <c>ReactorDiagnostics.GetHosts</c>, <c>TryGetComponent</c> and <c>TryGetRootComponent</c> are missing, so a
/// Reactor with them is never read through internals. Every read is defensive: an internal that moved reads as
/// absent rather than failing the request. Delete this adapter once DevTools no longer supports those versions.
/// </summary>
internal sealed class ReactorInternals
{
    private readonly PropertyInfo? appWindows;
    private readonly Type? hostControl;
    private readonly Type? componentElement;
    private readonly PropertyInfo? componentProps;
    private readonly Type? memoElement;

    internal ReactorInternals(Assembly assembly)
    {
        appWindows = assembly.GetType("Microsoft.UI.Reactor.ReactorApp")?.GetProperty("Windows", BindingFlags.Public | BindingFlags.Static);
        hostControl = assembly.GetType("Microsoft.UI.Reactor.Hosting.ReactorHostControl");
        componentElement = assembly.GetType("Microsoft.UI.Reactor.Core.ComponentElement");
        componentProps = componentElement?.GetProperty("Props", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        memoElement = assembly.GetType("Microsoft.UI.Reactor.Core.MemoElement");
    }

    private static object? Member(object? target, params string[] names) => ReactorApi.Member(target, names);

    /// <summary>The root of every <c>ReactorWindow</c> (<c>ReactorApp.Windows</c>), with its host's root component.</summary>
    public IEnumerable<ReactorHostInfo> WindowHosts()
    {
        var list = new List<ReactorHostInfo>();
        object? windows;
        try { windows = appWindows?.GetValue(null); }
        catch (TargetInvocationException) { return list; }
        if (windows is not IEnumerable items)
        {
            return list;
        }
        foreach (var window in items)
        {
            try
            {
                if (window is null || Member(window, "Host") is not { } host || HostRootControl(host) is not { } control)
                {
                    continue;
                }
                list.Add(new ReactorHostInfo(control, "window",
                    Member(window, "Id") as string,
                    Member(window, "Key")?.ToString(),
                    Member(Member(window, "NativeWindow"), "Title") as string,
                    RootComponent(host)));
            }
            catch (Exception ex) when (ex is TargetInvocationException or InvalidCastException or MemberAccessException)
            {
                // A window closing while it is described has no root to report.
            }
        }
        return list;
    }

    /// <summary>A <c>ReactorHostControl</c> island inside XAML, described by the root it renders.</summary>
    public ReactorHostInfo? IslandHost(UIElement element)
    {
        if (hostControl?.IsInstanceOfType(element) != true)
        {
            return null;
        }
        try
        {
            var control = Member(element, "_currentControl", "CurrentControl") as UIElement
                ?? (element as Microsoft.UI.Xaml.Controls.ContentControl)?.Content as UIElement;
            return control is null ? null : new ReactorHostInfo(control, "island", null, null, null, RootComponent(element));
        }
        catch (Exception ex) when (ex is TargetInvocationException or InvalidCastException or MemberAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Every Reactor host the element chain could belong to: islands on it, then every <c>ReactorWindow</c>'s host.
    /// </summary>
    public List<object> HostObjects(IReadOnlyList<UIElement> chain)
    {
        var hosts = new List<object>();
        foreach (var ancestor in chain)
        {
            if (hostControl?.IsInstanceOfType(ancestor) == true)
            {
                hosts.Add(ancestor);
            }
        }
        object? windows;
        try { windows = appWindows?.GetValue(null); }
        catch (TargetInvocationException) { return hosts; }
        if (windows is IEnumerable items)
        {
            foreach (var window in items)
            {
                if (Member(window, "Host") is { } host && !hosts.Contains(host))
                {
                    hosts.Add(host);
                }
            }
        }
        return hosts;
    }

    /// <summary>The nearest island on the chain, or the window host whose root control is on it.</summary>
    public object? HostFor(IReadOnlyList<UIElement> chain)
    {
        foreach (var ancestor in chain)
        {
            if (hostControl?.IsInstanceOfType(ancestor) == true)
            {
                return ancestor;
            }
        }
        foreach (var host in HostObjects(chain))
        {
            if (HostRootControl(host) is { } root && chain.Contains(root))
            {
                return host;
            }
        }
        return null;
    }

    /// <summary>The component mounted at <paramref name="element"/>, read from a host's reconciler.</summary>
    public ReactorComponentHandle? ComponentAt(UIElement element, IReadOnlyList<object> hosts)
    {
        foreach (var host in hosts)
        {
            if (Member(host, "Reconciler", "_reconciler") is { } reconciler &&
                Member(reconciler, "_componentNodes") is IDictionary nodes && nodes.Contains(element) &&
                nodes[element] is { } node)
            {
                var component = Member(node, "Component");
                var context = Member(component, "Context") ?? Member(node, "Context");
                if (context is null)
                {
                    return null;
                }
                var declaring = Member(node, "Element");
                var kind = component is not null ? "class" : memoElement?.IsInstanceOfType(declaring) == true ? "memo" : "function";
                var props = Member(component, "PropsBoxed", "Props") ?? (componentElement?.IsInstanceOfType(declaring) == true ? componentProps?.GetValue(declaring) : null);
                return new InternalReactorComponent(context, kind, false, component?.GetType(),
                    Member(declaring, "RenderFunc") as Delegate, props, declaring,
                    () => nodes.Contains(element) && ReferenceEquals(nodes[element], node));
            }
        }
        foreach (var host in hosts)
        {
            if (!ReferenceEquals(HostRootControl(host), element))
            {
                continue;
            }
            var component = Member(host, "_rootComponent", "RootComponent");
            var context = Member(component, "Context") ?? Member(host, "_funcContext", "FuncContext");
            if (context is null)
            {
                return null;
            }
            return new InternalReactorComponent(context, component is null ? "function" : "class", true, component?.GetType(),
                component is null ? Member(host, "_rootRenderFunc", "RootRenderFunc") as Delegate : null,
                Member(component, "PropsBoxed", "Props"), null, () => ReferenceEquals(HostRootControl(host), element));
        }
        return null;
    }

    /// <summary>
    /// Drops what each <c>ThemeRef</c> resolved to, so the next render reads an edited resource. Reactor versions
    /// with the public <c>Theme.NotifyResourcesChanged</c> do this themselves.
    /// </summary>
    public static void InvalidateThemeCache(Assembly assembly)
    {
        try
        {
            assembly.GetType("Microsoft.UI.Reactor.Core.ThemeRef")?
                .GetMethod("InvalidateResolutionCache", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.Invoke(null, null);
        }
        catch (TargetInvocationException)
        {
            // The next theme change clears it.
        }
    }

    private static UIElement? HostRootControl(object host) =>
        Member(host, "CurrentControl", "_currentControl") as UIElement
            ?? (Member(host, "ContentTarget") as Microsoft.UI.Xaml.Controls.Border)?.Child
            ?? (Member(host, "Window") as Window)?.Content;

    // A class component instance, or the render function a host was mounted with.
    private static (string Kind, string? Type, string Name)? RootComponent(object host)
    {
        if (Member(host, "RootComponent", "_rootComponent") is { } component)
        {
            var type = component.GetType();
            return ("class", type.FullName ?? type.Name, ReactorApi.TypeName(type));
        }
        if (Member(host, "_rootRenderFunc", "RootRenderFunc") is Delegate render)
        {
            return ("function", null, ReactorApi.DelegateName(render, "render"));
        }
        return null;
    }
}

/// <summary>
/// A component read straight from Reactor's internals (<c>RenderContext._hooks</c> and its hook cells), for Reactor
/// versions without <c>ComponentDiagnostics</c>. Every read is defensive: a cell this adapter does not recognise
/// reads as <c>unknown</c> and refuses writes.
/// </summary>
internal sealed class InternalReactorComponent(
    object context, string kind, bool isRoot, Type? componentType, Delegate? renderFunc, object? props,
    object? declaringElement, Func<bool>? isMounted) : ReactorComponentHandle
{
    public override string Api => "internal";
    public override string Kind => kind;
    public override bool IsRoot => isRoot;
    public override Type? ComponentType => componentType;
    public override Delegate? RenderFunc => renderFunc;
    public override object? Props => props;
    public override object? DeclaringElement => declaringElement;
    public override bool IsMounted => isMounted?.Invoke() ?? true;

    private IList? Cells => ReactorReflect.Member(context, "_hooks") as IList;

    public override IReadOnlyList<ReactorHook> Hooks()
    {
        var list = new List<ReactorHook>();
        if (Cells is not { } cells)
        {
            return list;
        }
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            var (cellKind, type, value) = Classify(cell);
            list.Add(new ReactorHook(i, cellKind, type, value, cellKind is "state" or "reducer" or "persisted",
                cell is not null && ReactorReflect.Member(cell, "Migrated") is true));
        }
        return list;
    }

    public override IReadOnlyList<ReactorContextRead> Contexts()
    {
        var list = new List<ReactorContextRead>();
        if (Cells is not { } cells)
        {
            return list;
        }
        for (var i = 0; i < cells.Count; i++)
        {
            if (cells[i] is not { } cell || cell.GetType().Name != "ContextHookState")
            {
                continue;
            }
            var ctx = ReactorReflect.Member(cell, "Context");
            var type = ReactorReflect.Member(ctx, "ValueType") as Type
                ?? (ctx?.GetType() is { IsGenericType: true } generic ? generic.GetGenericArguments()[0] : null);
            var value = ReactorReflect.Member(cell, "LastValue");
            var fallback = ReactorReflect.Member(ctx, "DefaultValue", "DefaultValueBoxed");
            var name = ReactorReflect.Member(ctx, "DiagnosticName", "DebugName", "Name") as string;
            list.Add(new ReactorContextRead(i, name is { Length: > 0 } ? name : ReactorComponents.ContextName(type), type, value,
                Equals(value, fallback)));
        }
        return list;
    }

    public override bool TrySet(int index, object? value)
    {
        if (Cells is not { } cells || index < 0 || index >= cells.Count || cells[index] is not { } cell)
        {
            return false;
        }
        var (cellKind, type, _) = Classify(cell);
        if (cellKind is not ("state" or "reducer" or "persisted") || type is null ||
            !(value is null ? !type.IsValueType || Nullable.GetUnderlyingType(type) is not null : type.IsInstanceOfType(value)))
        {
            return false;
        }
        var field = cell.GetType().GetField("Value", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field is null)
        {
            return false;
        }
        bool changed;
        // A thread-safe UseState guards its value with a per-cell lock; take it the same way the setter does.
        if (ReactorReflect.Member(cell, "Lock") is { } gate)
        {
            lock (gate)
            {
                changed = !Equals(field.GetValue(cell), value);
                if (changed) { field.SetValue(cell, value); }
            }
        }
        else
        {
            changed = !Equals(field.GetValue(cell), value);
            if (changed) { field.SetValue(cell, value); }
        }
        if (changed)
        {
            (ReactorReflect.Member(context, "_requestRerender") as Action)?.Invoke();
        }
        return true;
    }

    public override bool Rerender()
    {
        if (ReactorReflect.Member(context, "_requestRerender") is not Action rerender)
        {
            return false;
        }
        rerender();
        return true;
    }

    /// <summary>The kind, value type and value of one internal hook cell, named after Reactor's cell classes.</summary>
    internal static (string Kind, Type? ValueType, object? Value) Classify(object? cell)
    {
        if (cell is null)
        {
            return ("unknown", null, null);
        }
        var type = cell.GetType();
        var name = type.IsGenericType ? type.Name[..type.Name.IndexOf('`')] : type.Name;
        var argument = type.IsGenericType ? type.GetGenericArguments()[0] : null;
        object? Value() => ReactorReflect.Member(cell, "Value");
        switch (name)
        {
            case "ValueHookState":
                if (argument is { IsGenericType: true } refType && refType.Name.StartsWith("Ref`", StringComparison.Ordinal))
                {
                    return ("ref", argument, Value());
                }
                // Reactor records which hook built the cached setter: 2 and 3 are UseReducer's updater and dispatch.
                return (ReactorReflect.Member(cell, "SetterKind") is byte setter && setter is 2 or 3 ? "reducer" : "state", argument, Value());
            case "MemoHookState":
                return ("memo", argument, Value());
            case "PersistedHookState":
                return ("persisted", argument, Value());
            case "EffectHookState":
                return ("effect", null, null);
            case "ContextHookState":
                var last = ReactorReflect.Member(cell, "LastValue");
                var ctx = ReactorReflect.Member(cell, "Context");
                return ("context", ReactorReflect.Member(ctx, "ValueType") as Type ?? last?.GetType(), last);
            case "NavigationLifecycleHookState":
                return ("navigationLifecycle", null, null);
            default:
                return ("unknown", null, null);
        }
    }
}

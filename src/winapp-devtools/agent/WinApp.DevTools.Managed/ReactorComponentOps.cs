// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WinApp.DevTools.Managed;

/// <summary>
/// The component operations DevTools runs in a Reactor app: describe the component that renders an element (props,
/// hooks, contexts, where it is defined), write one state hook, re-render, and invoke an element's event handler.
/// Writes wait for the owning host to finish rendering first, so a value is never read or written mid-render.
/// </summary>
internal static class ReactorComponentOps
{
    private const int TimeoutMs = 10000;
    private const int IdleBudgetMs = 3000;

    public static string Run(BindingTarget target, string op, string arg, string value)
    {
        var mutation = op is "reactorsetstate" or "reactorrerender" or "reactorinvoke";
        return RunOnUi(target, mutation, (element, done) =>
        {
            var api = ReactorApi.TryLoad(AppDomain.CurrentDomain.GetAssemblies());
            if (api is null)
            {
                done("{\"state\":\"absent\"}");
                return;
            }
            if (api.Error is not null)
            {
                done(Unavailable(api.Error));
                return;
            }
            switch (op)
            {
                case "reactorcomponent":
                    done(Describe(element, api));
                    break;
                case "reactorsetstate":
                    SetState(element, api, arg, value, done);
                    break;
                case "reactorrerender":
                    Rerender(element, api, done);
                    break;
                case "reactorinvoke":
                    Invoke(element, api, arg, value, done);
                    break;
                default:
                    done(Unavailable("unknown Reactor operation: " + op));
                    break;
            }
        });
    }

    /// <summary>
    /// Runs <paramref name="start"/> on the element's UI thread and waits for the answer it hands to its callback,
    /// which may come several dispatcher turns later (after the host goes idle).
    /// </summary>
    internal static string RunOnUi(BindingTarget target, bool mutation, Action<UIElement, Action<string>> start) =>
        RunOnUi(target, target.Project, (element, work) => element.DispatcherQueue.TryEnqueue(() => work()), mutation, TimeoutMs, start);

    /// <summary>
    /// <see cref="RunOnUi(BindingTarget, bool, Action{UIElement, Action{string}})"/> over its parts: the target
    /// (<paramref name="owner"/>, released exactly once), how it projects and how work reaches its UI thread. A
    /// callback that starts claims the target before projecting it, so a timeout can only release a target no callback
    /// is using; a callback that runs after the timeout does nothing.
    /// </summary>
    internal static string RunOnUi<T>(IDisposable owner, Func<object?> project, Func<T, Action, bool> enqueue, bool mutation,
        int timeoutMs, Action<T, Action<string>> start) where T : class
    {
        var operation = new UiOperation(owner);
        try
        {
            if (project() is not T first)
            {
                return Unavailable("the selected object is not a XAML element");
            }
            var done = operation.Done;
            var enqueued = enqueue(first, () => operation.Run(() =>
            {
                if (project() is not T element)
                {
                    done.TrySetResult(Unavailable("the selected object is not a XAML element"));
                    return;
                }
                start(element, result => done.TrySetResult(result));
            }));
            if (!enqueued)
            {
                return Unavailable("the app UI thread is not accepting work");
            }
            if (!done.Task.Wait(timeoutMs))
            {
                operation.Cancel();
                return mutation
                    ? Unavailable("the app UI thread did not respond in time; the change may or may not have been applied", indeterminate: true)
                    : Unavailable("the app UI thread did not respond");
            }
            return done.Task.Result;
        }
        catch (Exception ex)
        {
            return Unavailable(ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            operation.Cancel();
        }
    }

    /// <summary>
    /// Who owns the target of one request: the pipe thread until a UI callback claims it, then that callback, which
    /// releases it when its synchronous part ends. Claiming and cancelling are one atomic step, as in
    /// <c>BindingDiagnosis</c>'s operation.
    /// </summary>
    private sealed class UiOperation(IDisposable owner)
    {
        private const int Pending = 0, Running = 1, Cancelled = 2, Finished = 3;
        private IDisposable? owner = owner;
        private int state;

        internal TaskCompletionSource<string> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Cancel()
        {
            if (Interlocked.CompareExchange(ref state, Cancelled, Pending) == Pending)
            {
                Interlocked.Exchange(ref owner, null)?.Dispose();
            }
        }

        internal void Run(Action body)
        {
            if (Interlocked.CompareExchange(ref state, Running, Pending) != Pending)
            {
                return;
            }
            try { body(); }
            catch (Exception ex) { Done.TrySetResult(Fault(ex)); }
            finally
            {
                try { Interlocked.Exchange(ref owner, null)?.Dispose(); }
                catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Releasing a Reactor request's element failed: {0}", ex.GetType().Name); }
                Volatile.Write(ref state, Finished);
            }
        }
    }

    /// <summary>
    /// Calls <paramref name="then"/> on the UI thread once the host is idle, or with false when it stays busy. Every
    /// check after the first runs in a later dispatcher turn, outside any caller's catch, so a fault in
    /// <paramref name="isIdle"/> or <paramref name="then"/> completes the request as unavailable through
    /// <paramref name="done"/> instead of reaching the app's dispatcher.
    /// </summary>
    internal static void WhenIdle(Func<Action, bool> enqueueLow, Func<bool?> isIdle, int budgetMs, Action<string> done, Action<bool> then)
    {
        var until = Environment.TickCount64 + budgetMs;
        void Check()
        {
            try
            {
                if (isIdle() is not false)
                {
                    then(true);
                    return;
                }
                if (Environment.TickCount64 > until || !enqueueLow(Check))
                {
                    then(false);
                }
            }
            catch (Exception ex)
            {
                done(Fault(ex));
            }
        }
        Check();
    }

    /// <summary>Queues work behind the app's own at low priority on the element's UI thread.</summary>
    private static Func<Action, bool> Low(UIElement element) =>
        work => element.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => work());

    /// <summary>A request that failed inside the app, as an unavailable answer; the failure goes to the agent's trace.</summary>
    internal static string Fault(Exception ex)
    {
        var inner = ex is TargetInvocationException { InnerException: { } wrapped } ? wrapped : ex;
        System.Diagnostics.Trace.TraceWarning("Reactor component operation failed: {0}", inner);
        return Unavailable(inner.GetType().Name + ": " + inner.Message);
    }

    internal static string Describe(UIElement element, ReactorApi api)
    {
        var component = api.FindComponent(element, out var exact, out var row);
        if (row == ReactorRowState.Recycled)
        {
            return Refused("unmounted", RecycledReason);
        }
        var host = api.HostFor(element);
        return Json(w =>
        {
            w.WriteString("state", component is null ? "none" : "ok");
            w.WriteString("version", api.Version);
            if (component is null)
            {
                if (api.ErrorScreenReason(element) is { } errorScreen)
                {
                    w.WriteString("reason", errorScreen);
                    w.WriteBoolean("errorScreen", true);
                    return;
                }
                w.WriteString("reason", "no Reactor component renders this element");
                return;
            }
            w.WriteBoolean("exact", exact);
            if (row == ReactorRowState.Parked) { w.WriteBoolean("parked", true); }
            if (ReactorApi.IsIdle(host) is bool idle) { w.WriteBoolean("idle", idle); }
            WriteComponent(w, component, api);
        });
    }

    /// <summary>The component's identity, where it is declared and defined, its props, hooks and contexts.</summary>
    internal static void WriteComponent(Utf8JsonWriter w, ReactorComponentHandle component, ReactorApi? api)
    {
        w.WriteString("api", component.Api);
        w.WriteStartObject("component");
        w.WriteString("kind", component.Kind);
        w.WriteString("name", component.Name);
        if (component.ComponentType is { } type) { w.WriteString("type", type.FullName ?? type.Name); }
        w.WriteBoolean("isRoot", component.IsRoot);
        w.WriteBoolean("mounted", component.IsMounted);
        w.WriteEndObject();

        if (component.DeclaringElement is { } declaring && ReactorApi.Location(ReactorReflect.Member(declaring, "CallSite")) is { } site)
        {
            w.WriteStartObject("declared");
            w.WriteString("file", site.File);
            w.WriteNumber("line", site.Line);
            if (site.Column > 0) { w.WriteNumber("column", site.Column); }
            w.WriteEndObject();
        }
        var render = component.RenderMethod;
        if (render is not null && ReactorPdb.Locate(render) is { } definition)
        {
            w.WriteStartObject("definition");
            w.WriteString("file", definition.File);
            w.WriteNumber("line", definition.Line);
            if (definition.Column > 0) { w.WriteNumber("column", definition.Column); }
            w.WriteString("method", MethodLabel(render));
            if (definition.ChecksumAlgorithm is not null && definition.Checksum is not null)
            {
                w.WriteString("checksumAlgorithm", definition.ChecksumAlgorithm);
                w.WriteString("checksum", definition.Checksum);
            }
            w.WriteEndObject();
        }

        if (component is SnapshotReactorComponent shown)
        {
            WriteSnapshotValues(w, shown);
            return;
        }

        var props = component.Props;
        if (props is not null || component.Kind == "class")
        {
            w.WriteStartObject("props");
            if (props is not null) { w.WriteString("type", ReactorComponents.FriendlyTypeName(props.GetType())); }
            w.WriteStartArray("values");
            foreach (var (name, value, redacted) in ReactorElementProps.ComponentProps(props))
            {
                w.WriteStartObject();
                w.WriteString("name", name);
                w.WriteString("value", value);
                if (redacted) { w.WriteBoolean("redacted", true); }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        var contexts = component.Contexts();
        w.WriteStartArray("hooks");
        foreach (var hook in component.Hooks())
        {
            w.WriteStartObject();
            w.WriteNumber("index", hook.Index);
            w.WriteString("kind", hook.Kind);
            if (hook.ValueType is { } valueType) { w.WriteString("type", ReactorComponents.FriendlyTypeName(valueType)); }
            if (hook.Kind is not ("effect" or "navigationLifecycle" or "unknown"))
            {
                var context = hook.Kind == "context" ? contexts.FirstOrDefault(c => c.HookIndex == hook.Index) : null;
                var (text, redacted) = ReactorComponents.Format(context?.Name ?? "", hook.ValueType, hook.Value);
                w.WriteString("value", text);
                if (redacted) { w.WriteBoolean("redacted", true); }
                if (context is not null) { w.WriteString("context", context.Name); }
                w.WriteBoolean("canSet", hook.CanSet);
                w.WriteBoolean("editable", hook.CanSet && ReactorComponents.IsEditableValue(context?.Name ?? "", hook.ValueType, redacted));
            }
            if (hook.Migrated) { w.WriteBoolean("migrated", true); }
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("contexts");
        foreach (var context in contexts)
        {
            w.WriteStartObject();
            w.WriteNumber("hook", context.HookIndex);
            w.WriteString("name", context.Name);
            if (context.ValueType is { } valueType) { w.WriteString("type", ReactorComponents.FriendlyTypeName(valueType)); }
            var (text, redacted) = ReactorComponents.Format(context.Name, context.ValueType, context.Value);
            w.WriteString("value", text);
            if (redacted) { w.WriteBoolean("redacted", true); }
            if (context.IsDefault) { w.WriteBoolean("isDefault", true); }
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    // Props, hooks and contexts as Reactor formatted them, secrets redacted, in the shape the typed path writes.
    private static void WriteSnapshotValues(Utf8JsonWriter w, SnapshotReactorComponent component)
    {
        var snapshot = component.Snapshot;
        if (snapshot.Props.Count > 0 || component.Kind == "class")
        {
            w.WriteStartObject("props");
            if (component.PropsType is { } type) { w.WriteString("type", type); }
            w.WriteStartArray("values");
            foreach (var prop in snapshot.Props)
            {
                w.WriteStartObject();
                w.WriteString("name", prop.Name);
                w.WriteString("value", prop.Value);
                if (prop.Redacted) { w.WriteBoolean("redacted", true); }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteStartArray("hooks");
        foreach (var hook in snapshot.State)
        {
            w.WriteStartObject();
            w.WriteNumber("index", hook.Index);
            w.WriteString("kind", hook.Kind);
            if (hook.Name.Length > 0) { w.WriteString("name", hook.Name); }
            if (hook.Type.Length > 0) { w.WriteString("type", hook.Type); }
            if (hook.Kind is not ("effect" or "navigationLifecycle" or "unknown"))
            {
                w.WriteString("value", hook.Value);
                if (hook.Redacted) { w.WriteBoolean("redacted", true); }
                var context = hook.Kind == "context" ? snapshot.Contexts.FirstOrDefault(c => c.Index == hook.Index) : null;
                if (context is not null) { w.WriteString("context", context.Name); }
                w.WriteBoolean("canSet", hook.Kind is "state" or "reducer" or "persisted");
                w.WriteBoolean("editable", hook.Editable);
            }
            if (hook.Migrated) { w.WriteBoolean("migrated", true); }
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("contexts");
        foreach (var context in snapshot.Contexts)
        {
            w.WriteStartObject();
            w.WriteNumber("hook", context.Index);
            w.WriteString("name", context.Name);
            if (context.Type.Length > 0) { w.WriteString("type", context.Type); }
            w.WriteString("value", context.Value);
            if (context.Redacted) { w.WriteBoolean("redacted", true); }
            if (context.Kind == "default") { w.WriteBoolean("isDefault", true); }
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static string MethodLabel(MethodBase method)
    {
        var owner = method.DeclaringType;
        while (owner is not null && owner.Name.StartsWith('<'))
        {
            owner = owner.DeclaringType;
        }
        var name = method.Name;
        if (name.StartsWith('<') && name.IndexOf('>') is var close and > 1)
        {
            name = name[1..close];
        }
        return owner is null ? name : ReactorApi.TypeName(owner) + "." + name;
    }

    private static void SetState(UIElement element, ReactorApi api, string indexText, string text, Action<string> done)
    {
        if (!int.TryParse(indexText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index))
        {
            done(Refused("bad-hook", $"'{indexText}' is not a hook index; run `winapp devtools get-state` to list them"));
            return;
        }
        var host = api.HostFor(element);
        WhenIdle(Low(element), () => ReactorApi.IsIdle(host), IdleBudgetMs, done, idleBefore =>
        {
            var component = api.FindComponent(element, out _, out var row);
            if (row == ReactorRowState.Recycled)
            {
                done(Refused("unmounted", RecycledReason));
                return;
            }
            if (component is null)
            {
                done(NoComponent(element, api));
                return;
            }
            var parked = row == ReactorRowState.Parked;
            if (component is SnapshotReactorComponent shown)
            {
                SetStateText(element, api, host, shown, index, text, idleBefore, done, parked);
                return;
            }
            var hook = component.Hooks().FirstOrDefault(h => h.Index == index);
            if (hook is null)
            {
                done(Refused("bad-hook", $"<{component.Name}> has no hook {index}; run `winapp devtools get-state` to list them"));
                return;
            }
            if (!hook.CanSet)
            {
                done(Refused("not-settable", $"hook {index} of <{component.Name}> is a {hook.Kind} hook; only state, reducer and persisted hooks can be set"));
                return;
            }
            var beforeText = ReactorComponents.Format("", hook.ValueType, hook.Value).Text;
            if (hook.ValueType is null)
            {
                done(Refused("bad-value", "the hook has no value type"));
                return;
            }
            if (!ReactorComponents.TryParse(text, hook.ValueType, out var parsed, out var parseError))
            {
                done(Refused("bad-value", parseError!));
                return;
            }
            if (!component.TrySet(index, parsed))
            {
                done(Refused("refused", $"<{component.Name}> refused the value (it may have unmounted)"));
                return;
            }
            // Let the re-render the write scheduled finish before reading back, so `after` is what the UI shows.
            WhenIdle(Low(element), () => ReactorApi.IsIdle(host), IdleBudgetMs, done, idleAfter =>
            {
                var current = api.FindComponent(element, out _) ?? component;
                var after = current.Hooks().FirstOrDefault(h => h.Index == index);
                done(Json(w =>
                {
                    w.WriteString("state", "ok");
                    w.WriteString("component", current.Name);
                    w.WriteNumber("index", index);
                    w.WriteString("kind", hook.Kind);
                    w.WriteString("type", ReactorComponents.FriendlyTypeName(hook.ValueType));
                    w.WriteString("requested", text);
                    w.WriteString("before", beforeText);
                    if (after is null) { w.WriteNull("after"); }
                    else { w.WriteString("after", ReactorComponents.Format("", after.ValueType, after.Value).Text); }
                    w.WriteBoolean("changed", after is not null && !Equals(hook.Value, after.Value));
                    w.WriteBoolean("waitedForIdle", idleBefore);
                    WriteRendered(w, idleAfter, parked);
                    w.WriteString("api", current.Api);
                }));
            });
        });
    }

    // Reactor parses, checks and writes the value (TrySetState); its refusal is shown as Reactor words it.
    internal static void SetStateText(UIElement element, ReactorApi api, object? host, SnapshotReactorComponent component, int index,
        string text, bool idleBefore, Action<string> done, bool parked = false)
    {
        var before = component.Hook(index);
        // Reactor writes any text-typed hook; DevTools never writes one whose name says it holds a secret.
        if (before is { Redacted: true })
        {
            done(Refused("secret", $"hook {index} of <{component.Name}> holds a secret; DevTools does not write it"));
            return;
        }
        var (ok, error) = component.TrySetText(index, text);
        if (!ok)
        {
            var code = before is null ? "bad-hook"
                : before.Kind is not ("state" or "reducer" or "persisted") ? "not-settable"
                : "bad-value";
            done(Refused(code, error ?? $"<{component.Name}> refused the value"));
            return;
        }
        WhenIdle(Low(element), () => ReactorApi.IsIdle(host), IdleBudgetMs, done, idleAfter =>
        {
            var current = api.FindComponent(element, out _) as SnapshotReactorComponent ?? component;
            var after = current.Hook(index);
            done(Json(w =>
            {
                w.WriteString("state", "ok");
                w.WriteString("component", current.Name);
                w.WriteNumber("index", index);
                w.WriteString("kind", before?.Kind ?? after?.Kind ?? "state");
                if ((before ?? after)?.Type is { Length: > 0 } type) { w.WriteString("type", type); }
                w.WriteString("requested", text);
                w.WriteString("before", before?.Value ?? "");
                if (after is null) { w.WriteNull("after"); }
                else { w.WriteString("after", after.Value); }
                w.WriteBoolean("changed", after is not null && before is not null && after.Value != before.Value);
                w.WriteBoolean("waitedForIdle", idleBefore);
                WriteRendered(w, idleAfter, parked);
                w.WriteString("api", current.Api);
            }));
        });
    }

    private static void Rerender(UIElement element, ReactorApi api, Action<string> done)
    {
        var host = api.HostFor(element);
        WhenIdle(Low(element), () => ReactorApi.IsIdle(host), IdleBudgetMs, done, _ =>
        {
            var component = api.FindComponent(element, out _, out var row);
            if (row == ReactorRowState.Recycled)
            {
                done(Refused("unmounted", RecycledReason));
                return;
            }
            if (component is null)
            {
                done(NoComponent(element, api));
                return;
            }
            if (!component.Rerender())
            {
                done(Refused("refused", $"<{component.Name}> could not re-render (it has unmounted or never rendered)"));
                return;
            }
            WhenIdle(Low(element), () => ReactorApi.IsIdle(host), IdleBudgetMs, done, idle => done(Json(w =>
            {
                w.WriteString("state", "ok");
                w.WriteString("component", component.Name);
                WriteRendered(w, idle, row == ReactorRowState.Parked);
                w.WriteString("api", component.Api);
            })));
        });
    }

    internal const string RecycledReason =
        "this element was in a list row that its list has since recycled, so the component that rendered it is gone; " +
        "find the row again with `winapp devtools search` and use its new handle";

    /// <summary>
    /// <c>rendered</c>: true only when the host finished the render and the component is on screen. A component in a
    /// row its list parked for reuse renders into a collapsed row, so nothing visible changed (<c>parked</c>).
    /// </summary>
    internal static void WriteRendered(Utf8JsonWriter w, bool idle, bool parked)
    {
        w.WriteBoolean("rendered", idle && !parked);
        if (parked) { w.WriteBoolean("parked", true); }
    }

    private static void Invoke(UIElement element, ReactorApi api, string name, string valueText, Action<string> done)
    {
        var host = api.HostFor(element);
        WhenIdle(Low(element), () => ReactorApi.IsIdle(host), IdleBudgetMs, done, _ =>
        {
            var tag = api.GetTag(element);
            var handlers = tag is null ? [] : ReactorHandlers.Read(tag);
            var handler = ReactorHandlers.Find(handlers, name);
            if (handler is null)
            {
                done(Refused("no-handler", handlers.Count == 0
                    ? "this element has no Reactor event handlers"
                    : $"this element has no '{name}' handler; it has {string.Join(", ", handlers.Select(h => h.Event))}"));
                return;
            }
            if (!ReactorHandlers.TryArguments(handler.Handler, element, valueText, out var args, out var why))
            {
                done(Refused("unsupported-signature", why!));
                return;
            }
            try
            {
                handler.Handler.DynamicInvoke(args);
            }
            catch (TargetInvocationException ex)
            {
                var inner = ex.InnerException ?? ex;
                done(Json(w =>
                {
                    w.WriteString("state", "threw");
                    w.WriteString("event", handler.Event);
                    w.WriteString("method", ReactorComponents.HandlerName(handler.Handler));
                    w.WriteString("exception", inner.GetType().FullName);
                    w.WriteString("message", inner.Message);
                }));
                return;
            }
            WhenIdle(Low(element), () => ReactorApi.IsIdle(host), IdleBudgetMs, done, rendered => done(Json(w =>
            {
                w.WriteString("state", "ok");
                w.WriteString("event", handler.Event);
                w.WriteString("method", ReactorComponents.HandlerName(handler.Handler));
                w.WriteBoolean("rendered", rendered);
            })));
        });
    }

    internal static string Refused(string code, string reason) => Json(w =>
    {
        w.WriteString("state", "refused");
        w.WriteString("code", code);
        w.WriteString("reason", reason);
    });

    // No component renders the element; say so, or that its window shows Reactor's error screen instead of its root.
    private static string NoComponent(UIElement element, ReactorApi api) =>
        api.ErrorScreenReason(element) is { } errorScreen
            ? Refused("reactor-error-screen", errorScreen)
            : Refused("no-component", "no Reactor component renders this element");

    internal static string Unavailable(string reason, bool indeterminate = false) => Json(w =>
    {
        w.WriteString("state", "unavailable");
        w.WriteString("reason", reason);
        if (indeterminate) { w.WriteBoolean("indeterminate", true); }
    });

    internal static string Json(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            write(w);
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}

/// <summary>One event handler an element record carries: the record (or modifier) property and its delegate.</summary>
internal sealed record ReactorHandler(string Event, Delegate Handler, bool Modifier);

/// <summary>The event handlers a Reactor element record sets, and how DevTools invokes one.</summary>
internal static class ReactorHandlers
{
    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

    public static IReadOnlyList<ReactorHandler> Read(object tag)
    {
        var list = new List<ReactorHandler>();
        Collect(tag, false, list);
        if (tag.GetType().GetProperty("Modifiers", PublicInstance) is { } modifiers &&
            !typeof(Delegate).IsAssignableFrom(modifiers.PropertyType))
        {
            try
            {
                if (modifiers.GetValue(tag) is { } value)
                {
                    Collect(value, true, list);
                }
            }
            catch (TargetInvocationException)
            {
                // No modifiers to read.
            }
        }
        return list;
    }

    private static void Collect(object source, bool modifier, List<ReactorHandler> list)
    {
        foreach (var property in source.GetType().GetProperties(PublicInstance))
        {
            if (!typeof(Delegate).IsAssignableFrom(property.PropertyType) || property.GetIndexParameters().Length > 0 ||
                property.Name is "RenderFunc" or "EqualityContract")
            {
                continue;
            }
            try
            {
                if (property.GetValue(source) is Delegate handler && list.All(h => h.Event != property.Name))
                {
                    list.Add(new ReactorHandler(property.Name, handler, modifier));
                }
            }
            catch (TargetInvocationException)
            {
                // A property that throws has no handler to show.
            }
        }
    }

    /// <summary>The handler named <paramref name="name"/>: <c>OnClick</c>, or <c>Click</c> for it.</summary>
    public static ReactorHandler? Find(IReadOnlyList<ReactorHandler> handlers, string name) =>
        handlers.FirstOrDefault(h => string.Equals(h.Event, name, StringComparison.OrdinalIgnoreCase)) ??
        handlers.FirstOrDefault(h => string.Equals(h.Event, "On" + name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The parameters as C# reads them: <c>(string text)</c>.</summary>
    public static string Signature(Delegate handler)
    {
        var invoke = handler.GetType().GetMethod("Invoke");
        var parameters = invoke?.GetParameters() ?? [];
        return "(" + string.Join(", ", parameters.Select(p => ReactorComponents.FriendlyTypeName(p.ParameterType) +
            (string.IsNullOrEmpty(p.Name) ? "" : " " + p.Name))) + ")";
    }

    /// <summary>
    /// Arguments DevTools can supply honestly: none; the element as a sender; a fresh <c>RoutedEventArgs</c> or
    /// <c>EventArgs.Empty</c>; or one text, number, bool or enum value from <paramref name="valueText"/>. Anything
    /// else is refused rather than faked.
    /// </summary>
    public static bool TryArguments(Delegate handler, UIElement? sender, string valueText, out object?[] args, out string? why)
    {
        var parameters = handler.GetType().GetMethod("Invoke")?.GetParameters() ?? [];
        args = new object?[parameters.Length];
        why = null;
        var valueUsed = false;
        for (var i = 0; i < parameters.Length; i++)
        {
            var type = parameters[i].ParameterType;
            if (sender is not null && i == 0 && parameters.Length > 1 && (type == typeof(object) || type.IsInstanceOfType(sender)))
            {
                args[i] = sender;
                continue;
            }
            if (type == typeof(RoutedEventArgs))
            {
                args[i] = new RoutedEventArgs();
                continue;
            }
            if (type == typeof(EventArgs))
            {
                args[i] = EventArgs.Empty;
                continue;
            }
            if (!valueUsed && ReactorComponents.IsEditable(type))
            {
                if (valueText.Length == 0)
                {
                    why = $"the handler takes {Signature(handler)}; pass the {ReactorComponents.FriendlyTypeName(type)} argument with --value";
                    return false;
                }
                if (!ReactorComponents.TryParse(valueText, type, out var parsed, out var error))
                {
                    why = error;
                    return false;
                }
                args[i] = parsed;
                valueUsed = true;
                continue;
            }
            why = $"the handler takes {Signature(handler)}; DevTools cannot create a {ReactorComponents.FriendlyTypeName(type)} " +
                "argument. Trigger it through the UI instead, e.g. `winapp ui invoke`.";
            return false;
        }
        if (valueText.Length > 0 && !valueUsed)
        {
            why = $"the handler takes {Signature(handler)} and has no value to pass --value to";
            return false;
        }
        return true;
    }
}

/// <summary>Where the list row an element sits in stands: on screen, parked off screen for reuse, or recycled.</summary>
internal enum ReactorRowState { Live, Parked, Recycled }

internal sealed partial class ReactorApi
{
    /// <summary>
    /// The component that renders <paramref name="element"/>: the component mounted at it (a component's wrapper or
    /// a host's root control) or at its nearest ancestor that is one. <paramref name="exact"/> is true for the former.
    /// </summary>
    public ReactorComponentHandle? FindComponent(UIElement element, out bool exact) => FindComponent(element, out exact, out _);

    /// <summary>
    /// <see cref="FindComponent(UIElement, out bool)"/>, with where the element's list row stands
    /// (<see cref="RowState"/>). A recycled row has no component: null.
    /// </summary>
    public ReactorComponentHandle? FindComponent(UIElement element, out bool exact, out ReactorRowState row)
    {
        exact = false;
        row = ReactorRowState.Live;
        var chain = Ancestors(element);
        var hosts = Internals?.HostObjects(chain);
        for (var i = 0; i < chain.Count; i++)
        {
            if ((Internals is { } internals ? internals.ComponentAt(chain[i], hosts!) : SnapshotComponentAt(chain[i])) is { } component)
            {
                row = RowState(chain.Select(IsParkedRow).ToList(), i);
                if (row == ReactorRowState.Recycled)
                {
                    return null;
                }
                exact = i == 0;
                return component;
            }
        }
        return null;
    }

    /// <summary>
    /// Where an element's list row stands, from which elements on its way up (nearest first) are rows their list parked
    /// for reuse. One parked below the component's anchor (<paramref name="anchor"/>) lost its component: the walk up
    /// reached the component rendering the whole list instead (<see cref="ReactorRowState.Recycled"/>). One parked at
    /// or above the anchor keeps the component, off screen (<see cref="ReactorRowState.Parked"/>).
    /// </summary>
    internal static ReactorRowState RowState(IReadOnlyList<bool> parked, int anchor)
    {
        var first = parked.Select((value, index) => value ? index : -1).FirstOrDefault(index => index >= 0, -1);
        return first < 0 ? ReactorRowState.Live : first < anchor ? ReactorRowState.Recycled : ReactorRowState.Parked;
    }

    // Reactor parks a row it keeps for reuse by collapsing it in place: a repeater's child (VirtualList, LazyVStack) or
    // an ItemContainer's content (ItemsView).
    private static bool IsParkedRow(UIElement element) =>
        element.Visibility == Visibility.Collapsed &&
        VisualTreeHelper.GetParent(element) is Microsoft.UI.Xaml.Controls.ItemsRepeater or Microsoft.UI.Xaml.Controls.ItemContainer;

    /// <summary>The host whose render loop owns <paramref name="element"/>, for its <c>IsIdle</c>; null when unknown.</summary>
    public object? HostFor(UIElement element)
    {
        var chain = Ancestors(element);
        if (Internals is { } internals)
        {
            return internals.HostFor(chain);
        }
        var hosts = PublicHostObjects();
        foreach (var ancestor in chain.Where(hosts.ContainsKey))
        {
            return hosts[ancestor];
        }
        return null;
    }

    /// <summary>
    /// The headline of Reactor's built-in error screen (<c>Render error: ...</c>, or the neutral <c>Something went
    /// wrong.</c>) when <paramref name="control"/>, a host's untagged root control, is that screen; null otherwise.
    /// Reactor shows it in place of the root's content after a render or commit fails, and it is a plain Border.
    /// </summary>
    internal static string? ErrorScreenHeadline(UIElement control)
    {
        if (control is not Microsoft.UI.Xaml.Controls.Border border)
        {
            return null;
        }
        var text = border.Child switch
        {
            Microsoft.UI.Xaml.Controls.TextBlock neutral => neutral.Text,
            Microsoft.UI.Xaml.Controls.ScrollViewer { Content: Microsoft.UI.Xaml.Controls.StackPanel stack } when stack.Children.Count > 0 &&
                stack.Children[0] is Microsoft.UI.Xaml.Controls.TextBlock header => header.Text,
            _ => null,
        };
        // One line: the exception message can span several.
        return IsErrorScreenHeadline(text) ? string.Join(' ', text!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) : null;
    }

    internal static bool IsErrorScreenHeadline(string? text) =>
        text is not null && (text.StartsWith("Render error: ", StringComparison.Ordinal) || text == "Something went wrong.");

    /// <summary>
    /// Why no component renders <paramref name="element"/> when its window or island shows Reactor's error screen
    /// instead of its root; null when it does not.
    /// </summary>
    public string? ErrorScreenReason(UIElement element)
    {
        if (!PublicDiagnostics)
        {
            return null;
        }
        var hosts = WindowHosts();
        foreach (var host in Ancestors(element).Where(hosts.ContainsKey).Select(ancestor => hosts[ancestor]))
        {
            return host.ErrorScreen is { } headline ? ErrorScreenReason(host.Root?.Name, headline) : null;
        }
        return null;
    }

    internal static string ErrorScreenReason(string? root, string headline) =>
        $"this window shows Reactor's error screen instead of {(root is null ? "its root component" : $"<{root}>")}, so no component " +
        $"is rendered here. {headline.TrimEnd('.')}. Fix the error in the app's C#, then use Hot Reload or restart the app";

    /// <summary>The element and its ancestors, nearest first.</summary>
    internal static List<UIElement> Ancestors(UIElement element)
    {
        var chain = new List<UIElement>();
        for (DependencyObject? current = element; current is not null && chain.Count < 4096; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement ui)
            {
                chain.Add(ui);
            }
        }
        return chain;
    }

    /// <summary>Whether a host has finished rendering (Reactor's public <c>IsIdle</c>); null when it cannot say.</summary>
    public static bool? IsIdle(object? host)
    {
        try { return Member(host, "IsIdle") as bool?; }
        catch (TargetInvocationException) { return null; }
    }
}

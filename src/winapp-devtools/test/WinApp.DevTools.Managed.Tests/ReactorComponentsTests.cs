// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinApp.DevTools.Managed.Tests;

[TestClass]
public sealed class ReactorComponentsTests
{
    public enum Filter { All, Active, Done }

    public sealed record TaskItem(int Id, string Title);

    public sealed class Ref<T>(T current)
    {
        public T Current { get; set; } = current;
    }

    // The shapes of Reactor's internal hook cells, named as Reactor names them.
    private sealed class ValueHookState<T>(T value, byte setterKind = 1)
    {
        public T Value = value;
        public byte SetterKind = setterKind;
        public bool Migrated = false;
    }

    private sealed class MemoHookState<T>(T value)
    {
        public T Value = value;
    }

    private sealed class EffectHookState;

    private sealed class Context<T>(T fallback)
    {
        public Type ValueType => typeof(T);
        public T DefaultValue { get; } = fallback;
    }

    private sealed class ContextHookState(object context, object? last)
    {
        public object Context = context;
        public object? LastValue = last;
    }

    private sealed class RenderContext(params object[] hooks)
    {
        public int Renders;
#pragma warning disable IDE0052, CS0414 // Read by reflection, as Reactor's own fields are.
        private readonly List<object> _hooks = [.. hooks];
        private readonly Action _requestRerender = null!;
#pragma warning restore IDE0052, CS0414
        public RenderContext Init()
        {
            typeof(RenderContext).GetField("_requestRerender", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(this, (Action)(() => Renders++));
            return this;
        }
    }

    private sealed class Counter
    {
        public object Render() => (Action)Increment;
        public static void Increment() { }
        public Action Lambda() => () => Increment();
        public Action Local()
        {
            return Step;
            static void Step() { }
        }
    }

    private static InternalReactorComponent Internal(RenderContext context) =>
        new(context, "class", false, typeof(Counter), null, new TaskItem(1, "Ship"), null, null);

    [TestMethod]
    public void Internal_ReadsEveryHookKindInCallOrder()
    {
        var theme = new Context<string>("Light");
        var component = Internal(new RenderContext(
            new ValueHookState<int>(3),
            new ValueHookState<Filter>(Filter.All, setterKind: 2),
            new ValueHookState<Ref<int>>(new Ref<int>(0)),
            new MemoHookState<string>("memo"),
            new EffectHookState(),
            new ContextHookState(theme, "Dark")).Init());

        var hooks = component.Hooks();

        CollectionAssert.AreEqual(new[] { "state", "reducer", "ref", "memo", "effect", "context" }, hooks.Select(h => h.Kind).ToArray());
        Assert.AreEqual(3, hooks[0].Value);
        Assert.IsTrue(hooks[0].CanSet);
        Assert.IsTrue(hooks[1].CanSet);
        Assert.IsFalse(hooks[2].CanSet);
        Assert.AreEqual(typeof(string), hooks[5].ValueType);
        var context = component.Contexts().Single();
        Assert.AreEqual(5, context.HookIndex);
        Assert.AreEqual("Dark", context.Value);
        Assert.IsFalse(context.IsDefault);
        Assert.AreEqual("Context<string>", context.Name);
    }

    public enum SessionToken { None, Issued }

    [TestMethod]
    public void ASecretTypedHookHoldingNull_IsNotEditable()
    {
        var component = Internal(new RenderContext(new ValueHookState<SessionToken?>(null), new ValueHookState<Filter?>(null)).Init());
        var json = ReactorComponentOps.Json(w => ReactorComponentOps.WriteComponent(w, component, null));
        using var doc = JsonDocument.Parse(json);
        var hooks = doc.RootElement.GetProperty("hooks");

        Assert.AreEqual("null", hooks[0].GetProperty("value").GetString(), "null has nothing to redact");
        Assert.IsFalse(hooks[0].GetProperty("editable").GetBoolean(), "but a secret type is never offered for editing");
        Assert.IsTrue(hooks[1].GetProperty("editable").GetBoolean(), "an ordinary nullable enum is");
        Assert.IsFalse(ReactorComponents.IsEditableValue("ApiPassword", typeof(string), redacted: false), "nor a secret name");
        Assert.IsTrue(ReactorComponents.IsEditableValue("Title", typeof(string), redacted: false));
    }

    [TestMethod]
    public void Internal_SetRerendersOnlyWhenTheValueChanges()
    {
        var context = new RenderContext(new ValueHookState<int>(3), new ValueHookState<Ref<int>>(new Ref<int>(0))).Init();
        var component = Internal(context);

        Assert.IsTrue(component.TrySet(0, 42));
        Assert.AreEqual(42, component.Hooks()[0].Value);
        Assert.AreEqual(1, context.Renders);

        Assert.IsTrue(component.TrySet(0, 42));
        Assert.AreEqual(1, context.Renders, "an equal value must not schedule a render");

        Assert.IsFalse(component.TrySet(0, "42"), "no conversion: a string is not an int");
        Assert.IsFalse(component.TrySet(1, new Ref<int>(5)), "a ref is not state");
        Assert.IsFalse(component.TrySet(9, 1));
        Assert.IsTrue(component.Rerender());
        Assert.AreEqual(2, context.Renders);
    }

    // Reactor's public HookDiagnostics/ContextDiagnostics and ComponentDiagnostics shapes.
    public enum HookKind { Unknown, State, Reducer, Ref, Memo, Effect, Context }

    public sealed record HookDiagnostics(int Index, HookKind Kind, Type? ValueType, object? Value, bool CanSetValue, bool MigratedByHotReload);

    public sealed record ContextDiagnostics(int HookIndex, object Context, string? Name, Type ValueType, object? Value, object? DefaultValue);

    public enum ComponentKind { Class, Function, Memo }

    public sealed class ComponentDiagnostics
    {
        public ComponentKind Kind => ComponentKind.Class;
        public bool IsRoot => false;
        public Type ComponentType => typeof(Counter);
        public object Props => new TaskItem(7, "Write docs");
        public bool IsMounted => true;
        public object? Written;
        public IReadOnlyList<HookDiagnostics> GetHooks() =>
            [new(0, HookKind.State, typeof(int), 3, true, false), new(1, HookKind.Context, typeof(string), "Dark", false, false)];
        public IReadOnlyList<ContextDiagnostics> GetContexts() => [new(1, new object(), "ThemeContext", typeof(string), "Dark", "Light")];
        public bool TrySetHookValue(int index, object? value) { Written = value; return index == 0; }
        public bool RequestRerender() => true;
    }

    [TestMethod]
    public void Public_BindsReactorsComponentDiagnosticsByName()
    {
        var diagnostics = new ComponentDiagnostics();
        var component = new PublicReactorComponent(diagnostics);

        Assert.AreEqual("reactor", component.Api);
        Assert.AreEqual("class", component.Kind);
        Assert.AreEqual("Counter", component.Name);
        CollectionAssert.AreEqual(new[] { "state", "context" }, component.Hooks().Select(h => h.Kind).ToArray());
        Assert.AreEqual("ThemeContext", component.Contexts().Single().Name);
        Assert.IsTrue(component.TrySet(0, 5));
        Assert.AreEqual(5, diagnostics.Written);
        Assert.IsTrue(component.Rerender());
        Assert.AreEqual("Render", component.RenderMethod?.Name);
    }

    [TestMethod]
    public void WriteComponent_ReportsHooksPropsContextsAndTheRenderDefinition()
    {
        var json = ReactorComponentOps.Json(w => ReactorComponentOps.WriteComponent(w, new PublicReactorComponent(new ComponentDiagnostics()), null));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.AreEqual("Counter", root.GetProperty("component").GetProperty("name").GetString());
        var hook = root.GetProperty("hooks")[0];
        Assert.AreEqual("state", hook.GetProperty("kind").GetString());
        Assert.AreEqual("int", hook.GetProperty("type").GetString());
        Assert.AreEqual("3", hook.GetProperty("value").GetString());
        Assert.IsTrue(hook.GetProperty("editable").GetBoolean());
        Assert.AreEqual("ThemeContext", root.GetProperty("hooks")[1].GetProperty("context").GetString());
        Assert.AreEqual("Write docs", root.GetProperty("props").GetProperty("values").EnumerateArray()
            .Single(v => v.GetProperty("name").GetString() == "Title").GetProperty("value").GetString());
        var definition = root.GetProperty("definition");
        StringAssert.EndsWith(definition.GetProperty("file").GetString(), "ReactorComponentsTests.cs");
        Assert.AreEqual("Counter.Render", definition.GetProperty("method").GetString());
        Assert.AreEqual("SHA256", definition.GetProperty("checksumAlgorithm").GetString());
        Assert.AreEqual(64, definition.GetProperty("checksum").GetString()!.Length);
    }

    [TestMethod]
    public void Pdb_LocatesALambdaAtItsLine()
    {
        var span = ReactorPdb.Locate(new Counter().Lambda().Method);
        Assert.IsNotNull(span);
        var line = File.ReadAllLines(span.File)[span.Line - 1];
        StringAssert.Contains(line, "public Action Lambda()");
    }

    [TestMethod]
    public void TryParse_ConvertsToTheHookTypeOrExplainsWhyNot()
    {
        Assert.IsTrue(ReactorComponents.TryParse("42", typeof(int), out var number, out _));
        Assert.AreEqual(42, number);
        Assert.IsTrue(ReactorComponents.TryParse("done", typeof(Filter), out var filter, out _));
        Assert.AreEqual(Filter.Done, filter);
        Assert.IsTrue(ReactorComponents.TryParse("null", typeof(int?), out var none, out _));
        Assert.IsNull(none);
        Assert.IsTrue(ReactorComponents.TryParse("1.5", typeof(double), out var real, out _));
        Assert.AreEqual(1.5, real);

        Assert.IsFalse(ReactorComponents.TryParse("maybe", typeof(bool), out _, out var error));
        StringAssert.Contains(error, "true or false");
        Assert.IsFalse(ReactorComponents.TryParse("Later", typeof(Filter), out _, out error));
        StringAssert.Contains(error, "All, Active, Done");
        Assert.IsFalse(ReactorComponents.TryParse("x", typeof(int), out _, out error));
        StringAssert.Contains(error, "not a valid int");
        Assert.IsFalse(ReactorComponents.TryParse("{}", typeof(TaskItem), out _, out error));
        StringAssert.Contains(error, "cannot be typed as text");
        Assert.IsFalse(ReactorComponents.TryParse("null", typeof(int), out _, out _));
    }

    [TestMethod]
    public void Format_SummarisesCollectionsAndRedactsSecrets()
    {
        var list = new List<TaskItem> { new(1, "a"), new(2, "b") };
        Assert.AreEqual("List<TaskItem> (2 items)", ReactorComponents.Format("", list.GetType(), list).Text);
        Assert.AreEqual("TaskItem { Id = 1, Title = a }", ReactorComponents.Format("", typeof(TaskItem), list[0]).Text);
        Assert.AreEqual(("<redacted>", true), ReactorComponents.Format("", typeof(System.Security.SecureString), new System.Security.SecureString()));
        Assert.AreEqual(("<redacted>", true), ReactorComponents.Format("ApiPassword", typeof(string), "hunter2"));
        Assert.AreEqual("string?", ReactorComponents.FriendlyTypeName(typeof(string)) + "?");
        Assert.AreEqual("int?", ReactorComponents.FriendlyTypeName(typeof(int?)));
        Assert.AreEqual("Dictionary<string, int>", ReactorComponents.FriendlyTypeName(typeof(Dictionary<string, int>)));
    }

    [TestMethod]
    public void HandlerName_DemanglesLambdasLocalFunctionsAndMethodGroups()
    {
        Assert.AreEqual("Counter.Increment", ReactorComponents.HandlerName((Action)Counter.Increment));
        Assert.AreEqual("lambda in Counter.Lambda", ReactorComponents.HandlerName(new Counter().Lambda()));
        Assert.AreEqual("Step in Counter.Local", ReactorComponents.HandlerName(new Counter().Local()));
    }

    private sealed record ButtonElement(string Label)
    {
        public Action? OnClick { get; init; }
        public Action<string>? OnRename { get; init; }
        public Action<TaskItem>? OnDrop { get; init; }
        public ButtonModifiers? Modifiers { get; init; }
    }

    private sealed record ButtonModifiers
    {
        public Action<int>? OnPointerWheel { get; init; }
    }

    [TestMethod]
    public void Handlers_ListsRecordAndModifierHandlersAndSynthesizesOnlyHonestArguments()
    {
        var clicks = 0;
        string? renamed = null;
        var tag = new ButtonElement("Save")
        {
            OnClick = () => clicks++,
            OnRename = text => renamed = text,
            OnDrop = _ => { },
            Modifiers = new ButtonModifiers { OnPointerWheel = _ => { } },
        };

        var handlers = ReactorHandlers.Read(tag);
        CollectionAssert.AreEquivalent(new[] { "OnClick", "OnRename", "OnDrop", "OnPointerWheel" }, handlers.Select(h => h.Event).ToArray());
        Assert.IsTrue(handlers.Single(h => h.Event == "OnPointerWheel").Modifier);
        Assert.AreSame(handlers[0], ReactorHandlers.Find(handlers, "click"));

        Assert.IsTrue(ReactorHandlers.TryArguments(tag.OnClick, null, "", out var args, out _));
        tag.OnClick.DynamicInvoke(args);
        Assert.AreEqual(1, clicks);

        Assert.IsFalse(ReactorHandlers.TryArguments(tag.OnRename, null, "", out _, out var why));
        StringAssert.Contains(why, "--value");
        Assert.IsTrue(ReactorHandlers.TryArguments(tag.OnRename, null, "Draft", out args, out _));
        tag.OnRename.DynamicInvoke(args);
        Assert.AreEqual("Draft", renamed);

        Assert.IsFalse(ReactorHandlers.TryArguments(tag.OnDrop, null, "", out _, out why));
        StringAssert.Contains(why, "cannot create a TaskItem");
        Assert.IsFalse(ReactorHandlers.TryArguments(tag.OnClick, null, "extra", out _, out why));
        Assert.AreEqual("(string obj)", ReactorHandlers.Signature(tag.OnRename));
    }

    [TestMethod]
    public void Resources_ParseColorsByHex()
    {
        Assert.IsTrue(ResourceOps.TryColor("#FF0067C0", out var color));
        Assert.AreEqual((byte)0x67, color.G);
        Assert.IsTrue(ResourceOps.TryColor("#C42B1C", out color));
        Assert.AreEqual((byte)0xFF, color.A);
        Assert.IsFalse(ResourceOps.TryColor("#12", out _));
    }
}

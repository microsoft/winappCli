// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinApp.DevTools.Managed.Tests;

/// <summary>
/// The agent runs inside the app: a component lookup or a Reactor call that fails must answer the request, never
/// throw on the app's dispatcher. Built against an emitted stand-in of Reactor's element records.
/// </summary>
[TestClass]
public sealed class ReactorComponentOpsSafetyTests
{
    private sealed class Counter
    {
        public object Render() => this;
    }

    private static ReactorSnapshot Snapshot(string name, string kind) => new(name, kind, false, [], [], []);

    [TestMethod]
    public void MountedBy_AMemoElement_HasARenderFunctionAndNoComponentType()
    {
        var reactor = FakeElements();
        var api = ReactorApi.ForAssembly(reactor);
        var memo = Activator.CreateInstance(reactor.GetType("Microsoft.UI.Reactor.Core.MemoElement")!)!;
        Func<object, object> render = ctx => ctx;
        memo.GetType().GetProperty("RenderFunc")!.SetValue(memo, render);

        var (type, found, props, declaring) = api.MountedBy(memo, Snapshot("Memo in App.Render", "memo"));

        Assert.IsNull(type, "a memo component has no component type (reading ComponentType off it threw TargetException)");
        Assert.AreSame(render, found);
        Assert.IsNull(props);
        Assert.AreSame(memo, declaring);
    }

    [TestMethod]
    public void MountedBy_AFunctionElement_HasARenderFunction()
    {
        var reactor = FakeElements();
        var api = ReactorApi.ForAssembly(reactor);
        var func = Activator.CreateInstance(reactor.GetType("Microsoft.UI.Reactor.Core.FuncElement")!)!;

        var (type, _, props, declaring) = api.MountedBy(func, Snapshot("RenderEachTime in App.Render", "function"));

        Assert.IsNull(type);
        Assert.IsNull(props);
        Assert.AreSame(func, declaring);
    }

    [TestMethod]
    public void MountedBy_AComponentElement_HasItsTypeAndProps()
    {
        var reactor = FakeElements();
        var api = ReactorApi.ForAssembly(reactor);
        var element = Activator.CreateInstance(reactor.GetType("Microsoft.UI.Reactor.Core.ComponentElement")!)!;
        element.GetType().GetProperty("ComponentType")!.SetValue(element, typeof(Counter));
        element.GetType().GetProperty("Props")!.SetValue(element, "label");

        var (type, _, props, declaring) = api.MountedBy(element, Snapshot("Counter", "class"));

        Assert.AreEqual(typeof(Counter), type);
        Assert.AreEqual("label", props);
        Assert.AreSame(element, declaring);
    }

    [TestMethod]
    public void MountedBy_NoTag_IsNotMounted()
    {
        var api = ReactorApi.ForAssembly(FakeElements());
        var (_, render, props, declaring) = api.MountedBy(null, Snapshot("Memo in App.Render", "memo"));
        Assert.IsNull(render);
        Assert.IsNull(props);
        Assert.IsNull(declaring);
    }

    [TestMethod]
    public void WhenIdle_ALookupThatThrowsInALaterTurn_AnswersUnavailable()
    {
        var queued = new Queue<Action>();
        var idle = false;
        string? answer = null;

        ReactorComponentOps.WhenIdle(work => { queued.Enqueue(work); return true; }, () => idle, 10000,
            json => answer ??= json,
            _ => throw new TargetException("Object type ComponentElement does not match target type MemoElement."));

        Assert.IsNull(answer, "the host was busy, so the lookup waits for a later dispatcher turn");
        Assert.AreEqual(1, queued.Count);
        idle = true;
        queued.Dequeue()();

        Assert.IsNotNull(answer, "the fault completes the request");
        using var doc = JsonDocument.Parse(answer);
        Assert.AreEqual("unavailable", doc.RootElement.GetProperty("state").GetString());
        StringAssert.Contains(doc.RootElement.GetProperty("reason").GetString(), "TargetException");
    }

    [TestMethod]
    public void WhenIdle_AnIdleCheckThatThrows_AnswersUnavailable()
    {
        string? answer = null;
        ReactorComponentOps.WhenIdle(_ => true, () => throw new InvalidOperationException("host closed"), 10000,
            json => answer = json, _ => Assert.Fail("not reached"));

        Assert.IsNotNull(answer);
        StringAssert.Contains(answer, "\"state\":\"unavailable\"");
        StringAssert.Contains(answer, "host closed");
    }

    [TestMethod]
    public void WhenIdle_ABusyHostPastItsBudget_ContinuesWithoutIdle()
    {
        bool? result = null;
        ReactorComponentOps.WhenIdle(_ => true, () => false, -1, _ => Assert.Fail("no fault"), idle => result = idle);
        Assert.IsFalse(result);
    }

    // Reactor's element records as DevTools reads them: an Element base, ComponentElement (ComponentType, Props),
    // FuncElement and MemoElement (RenderFunc), and Reconciler.GetElementTag.
    internal static Assembly FakeElements()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("FakeReactorElements" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("Reactor");
        var element = module.DefineType("Microsoft.UI.Reactor.Core.Element", TypeAttributes.Public | TypeAttributes.Class);
        element.DefineDefaultConstructor(MethodAttributes.Public);
        var elementType = element.CreateType();

        var component = module.DefineType("Microsoft.UI.Reactor.Core.ComponentElement", TypeAttributes.Public | TypeAttributes.Class, elementType);
        component.DefineDefaultConstructor(MethodAttributes.Public);
        AutoProperty(component, "ComponentType", typeof(Type));
        AutoProperty(component, "Props", typeof(object));
        component.CreateType();
        foreach (var type in new[] { "FuncElement", "MemoElement" }
            .Select(name => module.DefineType("Microsoft.UI.Reactor.Core." + name, TypeAttributes.Public | TypeAttributes.Class, elementType)))
        {
            type.DefineDefaultConstructor(MethodAttributes.Public);
            AutoProperty(type, "RenderFunc", typeof(Delegate));
            type.CreateType();
        }

        var reconciler = module.DefineType("Microsoft.UI.Reactor.Core.Reconciler", TypeAttributes.Public | TypeAttributes.Class);
        var tag = reconciler.DefineMethod("GetElementTag", MethodAttributes.Public | MethodAttributes.Static, typeof(object), [typeof(UIElement)]);
        var il = tag.GetILGenerator();
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        reconciler.CreateType();
        return assembly;
    }

    private static void AutoProperty(TypeBuilder type, string name, Type valueType)
    {
        var field = type.DefineField("_" + name, valueType, FieldAttributes.Private);
        const MethodAttributes accessor = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig;
        var getter = type.DefineMethod("get_" + name, accessor, valueType, Type.EmptyTypes);
        var il = getter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Ret);
        var setter = type.DefineMethod("set_" + name, accessor, null, [valueType]);
        il = setter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, field);
        il.Emit(OpCodes.Ret);
        var property = type.DefineProperty(name, PropertyAttributes.None, valueType, null);
        property.SetGetMethod(getter);
        property.SetSetMethod(setter);
    }
}

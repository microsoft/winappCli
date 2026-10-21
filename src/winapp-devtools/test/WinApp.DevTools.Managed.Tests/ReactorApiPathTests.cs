// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Reflection.Emit;
using Microsoft.UI.Xaml;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinApp.DevTools.Managed.Tests;

/// <summary>
/// DevTools reads a Reactor with the public diagnostics API (GetHosts and DescribeComponent) only through it, and an
/// older Reactor (published 0.1.0-preview.16) only through the internals adapter. Built against emitted stand-ins of both shapes.
/// </summary>
[TestClass]
public sealed class ReactorApiPathTests
{
    [TestMethod]
    public void PublicDiagnostics_AreUsedAlone_WhenReactorHasThem()
    {
        var reactor = FakeReactor(getHosts: true, describeComponent: true);
        var api = ReactorApi.ForAssembly(reactor);

        Assert.IsNull(api.Error);
        Assert.IsTrue(api.PublicDiagnostics);
        Assert.AreEqual("reactor", api.Api);
        Assert.IsNull(api.Internals, "no internals adapter for a Reactor with the public API");

        Assert.AreEqual(0, api.WindowHosts().Count);
        Assert.IsTrue(Flag(reactor, DiagnosticsType, "HostsRead"), "the host registry answered");
        Assert.IsFalse(Flag(reactor, AppType, "WindowsRead"), "ReactorApp.Windows is not consulted");
    }

    [TestMethod]
    public void Internals_AreUsed_ForAReactorWithoutThePublicApi()
    {
        var reactor = FakeReactor(getHosts: false, describeComponent: false);
        var api = ReactorApi.ForAssembly(reactor);

        Assert.IsNull(api.Error);
        Assert.IsFalse(api.PublicDiagnostics);
        Assert.AreEqual("internal", api.Api);
        Assert.IsNotNull(api.Internals);

        Assert.AreEqual(0, api.WindowHosts().Count);
        Assert.IsTrue(Flag(reactor, AppType, "WindowsRead"), "the adapter reads ReactorApp.Windows");
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public void APartialPublicApi_FallsBackToInternalsAsAWhole(bool getHosts, bool describeComponent)
    {
        var reactor = FakeReactor(getHosts, describeComponent);
        var api = ReactorApi.ForAssembly(reactor);

        Assert.IsFalse(api.PublicDiagnostics);
        Assert.IsNotNull(api.Internals);
        api.WindowHosts();
        Assert.IsFalse(Flag(reactor, DiagnosticsType, "HostsRead"), "one path per Reactor version, never both");
        Assert.IsTrue(Flag(reactor, AppType, "WindowsRead"));
    }

    [TestMethod]
    public void TextDiagnostics_AreThePublicApi()
    {
        // Reactor's final API: GetHosts and DescribeComponent/TrySetState/Rerender.
        var api = ReactorApi.ForAssembly(FakeReactor(getHosts: true, describeComponent: true));

        Assert.IsTrue(api.PublicDiagnostics);
        Assert.IsTrue(api.TextDiagnostics);
        Assert.IsNull(api.Internals);
        Assert.IsNull(api.DescribeComponent(null!), "no component described here");
        Assert.AreEqual((false, "Counter has no hook 9."), api.SetState(null!, 9, "1"), "Reactor's refusal is passed on word for word");
        Assert.IsTrue(api.Rerender(null!));
    }

    [TestMethod]
    public void IslandHost_IsOnlyAskedOfTheInternalsAdapter()
    {
        var api = ReactorApi.ForAssembly(FakeReactor(getHosts: true, describeComponent: true));
        Assert.IsNull(api.IslandHost(null!), "the public host registry already lists islands");
    }

    [TestMethod]
    public void PendingReferences_ComeFromGetReferenceEdges_WhenReactorHasIt()
    {
        var api = ReactorApi.ForAssembly(FakeReactor(getHosts: true, describeComponent: true, referenceEdges: nameof(ReactorReferenceTests.Snapshots)));

        var pending = api.PendingReferences(null!);

        Assert.IsNotNull(pending);
        CollectionAssert.AreEqual(new[] { "XYFocusRight", "LabeledBy", "FlowsTo" }, pending.Select(edge => edge.Property).ToArray());
    }

    [TestMethod]
    public void PendingReferences_AreUnknown_ForAReactorWithoutGetReferenceEdges()
    {
        var api = ReactorApi.ForAssembly(FakeReactor(getHosts: true, describeComponent: true));

        Assert.IsNull(api.PendingReferences(null!));
        Assert.AreEqual("{\"state\":\"ok\",\"supported\":false,\"edges\":[]}", ReactorProvider.DescribeReferences(null));
    }

    private const string DiagnosticsType = "Microsoft.UI.Reactor.Core.Diagnostics.ReactorDiagnostics";
    private const string AppType = "Microsoft.UI.Reactor.ReactorApp";

    private static bool Flag(Assembly assembly, string type, string field) =>
        assembly.GetType(type)?.GetField(field)?.GetValue(null) is true;

    private static Assembly FakeReactor(bool getHosts, bool describeComponent, string? referenceEdges = null)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("FakeReactor" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("Reactor");

        module.DefineType("Microsoft.UI.Reactor.Core.Element", TypeAttributes.Public | TypeAttributes.Class).CreateType();

        var reconciler = module.DefineType("Microsoft.UI.Reactor.Core.Reconciler", TypeAttributes.Public | TypeAttributes.Class);
        var tag = reconciler.DefineMethod("GetElementTag", MethodAttributes.Public | MethodAttributes.Static, typeof(object), [typeof(UIElement)]);
        var il = tag.GetILGenerator();
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        reconciler.CreateType();

        var app = module.DefineType(AppType, TypeAttributes.Public | TypeAttributes.Class);
        var windowsRead = app.DefineField("WindowsRead", typeof(bool), FieldAttributes.Public | FieldAttributes.Static);
        EmptyListGetter(app, "Windows", windowsRead);
        app.CreateType();

        if (getHosts || describeComponent || referenceEdges is not null)
        {
            var diagnostics = module.DefineType(DiagnosticsType, TypeAttributes.Public | TypeAttributes.Class);
            var hostsRead = diagnostics.DefineField("HostsRead", typeof(bool), FieldAttributes.Public | FieldAttributes.Static);
            if (referenceEdges is not null)
            {
                // Hands back the test's stand-ins for Reactor's reference edges, in the requested shape.
                var edges = typeof(ReactorReferenceTests).GetMethod(referenceEdges)!;
                var method = diagnostics.DefineMethod("GetReferenceEdges", MethodAttributes.Public | MethodAttributes.Static,
                    edges.ReturnType, [typeof(UIElement)]);
                var body = method.GetILGenerator();
                body.Emit(OpCodes.Call, edges);
                body.Emit(OpCodes.Ret);
            }
            if (getHosts)
            {
                var method = diagnostics.DefineMethod("GetHosts", MethodAttributes.Public | MethodAttributes.Static, typeof(object[]), Type.EmptyTypes);
                EmitFlagAndEmptyArray(method.GetILGenerator(), hostsRead);
            }
            if (describeComponent)
            {
                var describe = diagnostics.DefineMethod("DescribeComponent", MethodAttributes.Public | MethodAttributes.Static, typeof(object), [typeof(UIElement)]);
                var body = describe.GetILGenerator();
                body.Emit(OpCodes.Ldnull);
                body.Emit(OpCodes.Ret);
                var set = diagnostics.DefineMethod("TrySetState", MethodAttributes.Public | MethodAttributes.Static, typeof(bool),
                    [typeof(UIElement), typeof(int), typeof(string), typeof(string).MakeByRefType()]);
                set.DefineParameter(4, ParameterAttributes.Out, "error");
                body = set.GetILGenerator();
                body.Emit(OpCodes.Ldarg_3);
                body.Emit(OpCodes.Ldstr, "Counter has no hook 9.");
                body.Emit(OpCodes.Stind_Ref);
                body.Emit(OpCodes.Ldc_I4_0);
                body.Emit(OpCodes.Ret);
                var rerender = diagnostics.DefineMethod("Rerender", MethodAttributes.Public | MethodAttributes.Static, typeof(bool), [typeof(UIElement)]);
                body = rerender.GetILGenerator();
                body.Emit(OpCodes.Ldc_I4_1);
                body.Emit(OpCodes.Ret);
            }
            diagnostics.CreateType();
        }
        return assembly;
    }

    private static void EmptyListGetter(TypeBuilder type, string name, FieldBuilder flag)
    {
        var getter = type.DefineMethod("get_" + name, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            typeof(object[]), Type.EmptyTypes);
        EmitFlagAndEmptyArray(getter.GetILGenerator(), flag);
        type.DefineProperty(name, PropertyAttributes.None, typeof(object[]), null).SetGetMethod(getter);
    }

    private static void EmitFlagAndEmptyArray(ILGenerator il, FieldInfo flag)
    {
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stsfld, flag);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, typeof(object));
        il.Emit(OpCodes.Ret);
    }
}

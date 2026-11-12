// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Reflection.Emit;
using Microsoft.UI.Xaml;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinApp.DevTools.Managed.Tests;

/// <summary>
/// DevTools reads Reactor only through its public diagnostics API (GetHosts and DescribeComponent); a Reactor without
/// it gets one message asking for an update. Built against emitted stand-ins of both shapes.
/// </summary>
[TestClass]
public sealed class ReactorApiPathTests
{
    [TestMethod]
    public void PublicDiagnostics_AreRead_WhenReactorHasThem()
    {
        var reactor = FakeReactor(getHosts: true, describeComponent: true);
        var api = ReactorApi.ForAssembly(reactor);

        Assert.IsNull(api.Error);
        Assert.AreEqual("reactor", api.Api);
        Assert.AreEqual(0, api.WindowHosts().Count);
        Assert.IsTrue(Flag(reactor, DiagnosticsType, "HostsRead"), "the host registry answered");
        Assert.IsNull(api.DescribeComponent(null!), "no component described here");
        Assert.AreEqual((false, "Counter has no hook 9."), api.SetState(null!, 9, "1"), "Reactor's refusal is passed on word for word");
        Assert.IsTrue(api.Rerender(null!));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public void AReactorWithoutThePublicApi_AsksForAnUpdate(bool getHosts, bool describeComponent)
    {
        var api = ReactorApi.ForAssembly(FakeReactor(getHosts, describeComponent));

        Assert.AreEqual($"this app uses Microsoft.UI.Reactor {api.Version}; DevTools needs Microsoft.UI.Reactor 0.1.0-preview.17 or later. Update the package",
            api.Error);
        using var answer = System.Text.Json.JsonDocument.Parse(ReactorProvider.Describe(null!, api));
        Assert.AreEqual("unavailable", answer.RootElement.GetProperty("state").GetString(), "the tree shows raw controls");
        Assert.AreEqual(api.Error, answer.RootElement.GetProperty("reason").GetString());
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

    private static void EmitFlagAndEmptyArray(ILGenerator il, FieldInfo flag)
    {
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stsfld, flag);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, typeof(object));
        il.Emit(OpCodes.Ret);
    }
}

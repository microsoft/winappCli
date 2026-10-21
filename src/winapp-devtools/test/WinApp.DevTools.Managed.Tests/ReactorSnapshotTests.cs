// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinApp.DevTools.Managed.Tests;

/// <summary>
/// Reactor's text component diagnostics (<c>ReactorDiagnostics.DescribeComponent</c>): values arrive formatted but not
/// redacted, and DevTools passes them on in the shape the CLI already reads, redacting secrets itself.
/// </summary>
[TestClass]
public sealed class ReactorSnapshotTests
{
    // Reactor's public shapes, named as Reactor names them.
    public sealed record DiagnosticValue(int Index, string Name, string Kind, string Type, string Value, bool Editable,
        bool MigratedByHotReload);

    public sealed record ComponentSnapshot(string Name, string Kind, bool IsRoot, IReadOnlyList<DiagnosticValue> Props,
        IReadOnlyList<DiagnosticValue> State, IReadOnlyList<DiagnosticValue> Contexts);

    public sealed record CounterProps(string Label);

    private sealed class Counter
    {
        public object Render() => this;
    }

    private static ComponentSnapshot CounterSnapshot() => new("Counter", "class", false,
        [new(0, "Label", "prop", "string", "\"Done today\"", false, false),
         new(1, "ApiPassword", "prop", "string", "\"hunter2\"", false, false)],
        [new(0, "", "state", "int", "3", true, false),
         new(1, "", "context", "string", "\"Compact\"", false, false),
         new(2, "", "ref", "List<int>", "List<int> (3 items)", false, true),
         new(3, "", "effect", "", "", false, false),
         new(4, "", "state", "SecureString", "System.Security.SecureString", true, false),
         new(5, "", "context", "string", "\"abc123\"", false, false),
         new(6, "status", "state", "string", "\"AccessToken=abc123\"", true, false)],
        [new(1, "Density", "provided", "string", "\"Compact\"", false, false),
         new(5, "AuthToken", "provided", "string", "\"abc123\"", false, false)]);

    private static SnapshotReactorComponent Component(ComponentSnapshot snapshot, Func<int, string, (bool, string?)>? set = null) =>
        new(ReactorSnapshot.From(snapshot)!, typeof(Counter), null, null, "CounterProps",
            set ?? ((_, _) => (true, null)), () => true);

    [TestMethod]
    public void From_ReadsReactorsSnapshotByName()
    {
        var snapshot = ReactorSnapshot.From(CounterSnapshot())!;

        Assert.AreEqual("Counter", snapshot.Name);
        Assert.AreEqual("class", snapshot.Kind);
        Assert.AreEqual(2, snapshot.Props.Count);
        CollectionAssert.AreEqual(new[] { "state", "context", "ref", "effect", "state", "context", "state" }, snapshot.State.Select(s => s.Kind).ToArray());
        Assert.IsTrue(snapshot.State[2].Migrated);
        Assert.AreEqual("provided", snapshot.Contexts[1].Kind);
        Assert.AreEqual("function", ReactorSnapshot.From(new ComponentSnapshot("render in Program.Main", "Function", true, [], [], []))!.Kind);
        Assert.IsNull(ReactorSnapshot.From(null));
        Assert.IsNull(ReactorSnapshot.From(new ComponentSnapshot("", "class", false, [], [], [])), "a snapshot without a name is not a component");
    }

    [TestMethod]
    public void WriteComponent_PassesReactorsTextOn_AndRedactsSecrets()
    {
        var json = ReactorComponentOps.Json(w => ReactorComponentOps.WriteComponent(w, Component(CounterSnapshot()), null));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.AreEqual("reactor", root.GetProperty("api").GetString());
        var component = root.GetProperty("component");
        Assert.AreEqual("Counter", component.GetProperty("name").GetString());
        StringAssert.EndsWith(component.GetProperty("type").GetString(), "+Counter", "the element record's type is still reported");
        Assert.AreEqual("Counter.Render", root.GetProperty("definition").GetProperty("method").GetString(), "and where it is defined");

        var props = root.GetProperty("props");
        Assert.AreEqual("CounterProps", props.GetProperty("type").GetString());
        var label = props.GetProperty("values")[0];
        Assert.AreEqual("\"Done today\"", label.GetProperty("value").GetString(), "Reactor's quoting is kept");
        Assert.IsTrue(props.GetProperty("values")[1].GetProperty("redacted").GetBoolean(), "a secret-named prop is redacted by DevTools");
        Assert.AreEqual("<redacted>", props.GetProperty("values")[1].GetProperty("value").GetString());

        var hooks = root.GetProperty("hooks");
        Assert.AreEqual(7, hooks.GetArrayLength());
        Assert.AreEqual("3", hooks[0].GetProperty("value").GetString());
        Assert.IsTrue(hooks[0].GetProperty("editable").GetBoolean());
        Assert.IsTrue(hooks[0].GetProperty("canSet").GetBoolean());
        Assert.IsFalse(hooks[0].TryGetProperty("name", out _), "Reactor names no hooks; DevTools takes names from hooks= instead");
        Assert.AreEqual("Density", hooks[1].GetProperty("context").GetString());
        Assert.IsFalse(hooks[1].GetProperty("canSet").GetBoolean());
        Assert.AreEqual("List<int> (3 items)", hooks[2].GetProperty("value").GetString());
        Assert.IsTrue(hooks[2].GetProperty("migrated").GetBoolean());
        Assert.IsFalse(hooks[3].TryGetProperty("value", out _), "an effect holds no value");
        Assert.IsTrue(hooks[4].GetProperty("redacted").GetBoolean(), "a secret type is redacted");
        Assert.AreEqual("<redacted>", hooks[4].GetProperty("value").GetString());
        Assert.IsFalse(hooks[4].GetProperty("editable").GetBoolean(), "and never editable, whatever Reactor says");
        Assert.IsTrue(hooks[5].GetProperty("redacted").GetBoolean(), "a context hook whose context is named as a secret");
        Assert.IsTrue(hooks[6].GetProperty("redacted").GetBoolean(), "text that carries a labelled secret");
        Assert.IsFalse(hooks[6].GetProperty("editable").GetBoolean());

        var contexts = root.GetProperty("contexts");
        Assert.IsFalse(contexts[0].TryGetProperty("isDefault", out _));
        Assert.IsTrue(contexts[1].GetProperty("redacted").GetBoolean());
        Assert.AreEqual(5, contexts[1].GetProperty("hook").GetInt32());
    }

    [TestMethod]
    public void WriteComponent_MarksAContextNoProviderSuppliesAsDefault()
    {
        var snapshot = new ComponentSnapshot("Counter", "class", false, [], [new(0, "", "context", "string", "\"Light\"", false, false)],
            [new(0, "Theme", "default", "string", "\"Light\"", false, false)]);
        var json = ReactorComponentOps.Json(w => ReactorComponentOps.WriteComponent(w, Component(snapshot), null));
        using var doc = JsonDocument.Parse(json);

        var context = doc.RootElement.GetProperty("contexts")[0];
        Assert.IsTrue(context.GetProperty("isDefault").GetBoolean(), "Kind default means no provider above");
        Assert.AreEqual("\"Light\"", context.GetProperty("value").GetString());
        Assert.IsFalse(context.TryGetProperty("redacted", out _));
    }

    [TestMethod]
    public void WriteComponent_NamesARootFunctionAndOmitsAnUnknownPropsType()
    {
        var root = new ComponentSnapshot("render in Program.Main", "function", true, [], [new(0, "", "state", "string", "\"Ada\"", true, false)], []);
        var json = ReactorComponentOps.Json(w => ReactorComponentOps.WriteComponent(w,
            new SnapshotReactorComponent(ReactorSnapshot.From(root)!, null, null, null, null, (_, _) => (true, null), () => true), null));
        using var doc = JsonDocument.Parse(json);

        Assert.AreEqual("render in Program.Main", doc.RootElement.GetProperty("component").GetProperty("name").GetString());
        Assert.IsTrue(doc.RootElement.GetProperty("component").GetProperty("isRoot").GetBoolean());
        Assert.IsFalse(doc.RootElement.TryGetProperty("props", out _), "a function component without props lists none");
        Assert.AreEqual("\"Ada\"", doc.RootElement.GetProperty("hooks")[0].GetProperty("value").GetString());
    }

    [TestMethod]
    public void TrySetText_HandsReactorTheTextAndItsRefusalBack()
    {
        (int Index, string Text)? asked = null;
        var component = Component(CounterSnapshot(), (index, text) =>
        {
            asked = (index, text);
            return index == 0 ? (true, null) : (false, "'x' is not a valid int.");
        });

        Assert.AreEqual((true, (string?)null), component.TrySetText(0, "42"));
        Assert.AreEqual((0, "42"), asked);
        Assert.AreEqual((false, "'x' is not a valid int."), component.TrySetText(4, "x"));
        Assert.AreEqual("3", component.Hook(0)!.Value);
        Assert.IsNull(component.Hook(9));
        Assert.AreEqual(0, component.Hooks().Count, "typed hooks are not read on this path");
        Assert.IsFalse(component.TrySet(0, 42), "typed writes are not used on this path");
        Assert.IsTrue(component.Rerender());
    }
}

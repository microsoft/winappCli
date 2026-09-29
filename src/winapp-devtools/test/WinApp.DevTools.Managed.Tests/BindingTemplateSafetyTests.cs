// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinApp.DevTools.Managed.Tests;

[TestClass]
public sealed class BindingTemplateSafetyTests
{
    public sealed class Person
    {
        public int SetterCalls;
        public string Name
        {
            get => "Ada";
            set { SetterCalls++; throw new InvalidOperationException("setter rejected"); }
        }
    }

    private sealed class ReverseFailure : IValueConverter
    {
        public int ForwardCalls;
        public int ReverseCalls;
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            ForwardCalls++;
            return value;
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            ReverseCalls++;
            throw new InvalidOperationException("ConvertBack rejected");
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PriorRejectedWriteDoesNotTurnForwardEvaluationIntoReverseValidation(bool converterFailure)
    {
        var source = new Person();
        var converter = new ReverseFailure();
        if (converterFailure)
            Assert.ThrowsExactly<InvalidOperationException>(() => converter.ConvertBack("OVERRIDE", typeof(string), null!, ""));
        else
            Assert.ThrowsExactly<InvalidOperationException>(() => source.Name = "OVERRIDE");
        int setters = source.SetterCalls, reverse = converter.ReverseCalls;
        for (int i = 0; i < 3; i++)
        {
            using var result = JsonDocument.Parse(BindingDiagnosis.Evaluate("Name", source, "Person",
                "{Binding}", "TwoWay", typeof(string), "Text", () => "OVERRIDE", converter));
            var answer = result.RootElement;
            Assert.AreEqual("evaluated", answer.GetProperty("state").GetString());
            Assert.AreEqual("forward-path-and-type", answer.GetProperty("evaluationScope").GetString());
            Assert.AreEqual("unchecked", answer.GetProperty("reversePropagation").GetString());
            StringAssert.Contains(answer.GetProperty("reason").GetString()!, "ConvertBack");
            Assert.AreEqual("Ada", answer.GetProperty("sourceValue").GetString());
            Assert.AreEqual("OVERRIDE", answer.GetProperty("targetValue").GetString());
        }
        Assert.AreEqual(setters, source.SetterCalls);
        Assert.AreEqual(reverse, converter.ReverseCalls);
        Assert.AreEqual(3, converter.ForwardCalls);
    }

    [TestMethod]
    public void UnprovenPageAncestorCannotStandInForAnInstantiatedTemplate()
    {
        var page = (BindingAcceptanceTests.PageOwner)RuntimeHelpers.GetUninitializedObject(typeof(BindingAcceptanceTests.PageOwner));
        var row = (FrameworkElement)RuntimeHelpers.GetUninitializedObject(typeof(FrameworkElement));
        Assert.IsNull(BindingDiagnosis.XBindSource(row, node => ReferenceEquals(node, row) ? page : null, _ => null));
    }

    [TestMethod]
    public void ElementLocalMarkupCannotRuleOutInheritedDefaultBindMode()
    {
        var sb = new StringBuilder("{\"state\":\"captured\"");
        BindingDiagnosis.CompiledEditMetadata(sb, "{x:Bind Vm.Title}", typeof(TextBox), "Text");
        using var result = JsonDocument.Parse(sb.Append('}').ToString());
        Assert.AreEqual("Unknown", result.RootElement.GetProperty("mode").GetString());
        Assert.AreEqual("Unknown", result.RootElement.GetProperty("writesThrough").GetString());
    }

    public sealed class Row(string name)
    {
        public string Text = name;
        public object? DataContext;
    }

    public sealed class Item(string name) { public string Name = name; }

    [System.CodeDom.Compiler.GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", "3.0")]
    public sealed class TemplateBindings(object source, Row target)
    {
        public object? dataRoot = source;
        public Row obj4 = target;
        public int Calls;
        public bool Disabled;
        public void Update()
        {
            Calls++;
            if (!Disabled) obj4.Text = ((Item)dataRoot!).Name;
        }
    }

    private static BindingDiagnosis.CompiledOwner? Resolve(Row target, object component)
        => BindingDiagnosis.ResolveCompiledOwner(target, _ => null, _ => component);

    [TestMethod]
    public void TwoTemplateInstancesResolveTheirOwnGeneratedDataRootsNotPageNameOrDataContext()
    {
        var ada = new Item("Ada");
        var grace = new Item("Grace");
        var first = new Row("Ada") { DataContext = new Item("WRONG") };
        var second = new Row("Grace") { DataContext = new Item("WRONG") };
        foreach (var (row, source) in new[] { (first, ada), (second, grace) })
        {
            var scope = Resolve(row, new TemplateBindings(source, row));
            Assert.IsNotNull(scope);
            Assert.AreSame(source, scope.Source);
            using var result = JsonDocument.Parse(BindingDiagnosis.Evaluate("Name", scope.Source, "Item", "{x:Bind}",
                "Unknown", typeof(string), "Text", () => row.Text));
            Assert.AreEqual(source.Name, result.RootElement.GetProperty("sourceValue").GetString());
            Assert.AreEqual(source.Name, result.RootElement.GetProperty("targetValue").GetString());
        }
        Assert.IsNull(Resolve(first, new TemplateBindings(grace, second)));
    }

    [TestMethod]
    public void AttachedTemplateScopeWinsBeforeEnclosingPageAndUnknownShapeStopsTheSearch()
    {
        var row = new Row("Ada");
        var templateRoot = new object();
        var page = new BindingAcceptanceTests.CompiledOwner();
        var bindings = new TemplateBindings(new Item("Ada"), row);
        var scope = BindingDiagnosis.ResolveCompiledOwner(row,
            node => ReferenceEquals(node, row) ? templateRoot : page,
            node => ReferenceEquals(node, templateRoot) ? bindings : null);
        Assert.AreSame(bindings, scope!.Bindings);
        Assert.AreSame(templateRoot, scope.Owner);
        Assert.IsNull(BindingDiagnosis.ResolveCompiledOwner(row,
            _ => throw new AssertFailedException("Do not escape an unknown template scope"),
            _ => new object()));
        bindings.dataRoot = null;
        Assert.IsNull(Resolve(row, bindings));
    }

    [TestMethod]
    public void MissingTemplateComponentCannotFallBackToAPageThatDoesNotReferenceTheRow()
    {
        var row = new Row("Ada");
        var page = new BindingAcceptanceTests.CompiledOwner();
        page.Bindings.dataRoot = page;
        page.Bindings.obj1 = new Row("Other page target");
        Assert.IsNull(BindingDiagnosis.ResolveCompiledOwner(row,
            node => ReferenceEquals(node, row) ? page : null, _ => null));
        using var restore = JsonDocument.Parse(BindingDiagnosis.RestoreCompiled(null, true,
            () => throw new AssertFailedException("No owner means no target work")));
        Assert.AreEqual("unavailable", restore.RootElement.GetProperty("state").GetString());
        Assert.AreEqual("False", restore.RootElement.GetProperty("nativeFallback").GetString());
        var capture = new StringBuilder("{\"state\":\"captured\"");
        BindingDiagnosis.CompiledCaptureMetadata(capture, null);
        using var result = JsonDocument.Parse(capture.Append('}').ToString());
        Assert.AreEqual("none", result.RootElement.GetProperty("restoreKind").GetString());
        Assert.AreEqual(0, page.Bindings.Calls);
    }

    [TestMethod]
    [DataRow(true, "OVERRIDE")]
    [DataRow(false, "OVERRIDE")]
    [DataRow(false, "Ada")]
    public void UpdateReturnAndEqualOrDifferentValuesNeverProveSelectedRestoration(bool disabled, string initial)
    {
        var first = new Row(initial);
        var second = new Row("Grace");
        var bindings = new TemplateBindings(new Item("Ada"), first) { Disabled = disabled };
        var other = new TemplateBindings(new Item("Grace"), second);
        var owner = Resolve(first, bindings);
        using var preview = JsonDocument.Parse(BindingDiagnosis.RestoreCompiled(owner, false, () => first.Text));
        Assert.AreEqual("confirmation-required", preview.RootElement.GetProperty("state").GetString());
        Assert.AreEqual(0, bindings.Calls);
        for (int i = 0; i < 3; i++)
        {
            using var result = JsonDocument.Parse(BindingDiagnosis.RestoreCompiled(owner, true, () => first.Text));
            var answer = result.RootElement;
            Assert.AreEqual("unavailable", answer.GetProperty("state").GetString());
            Assert.AreEqual("True", answer.GetProperty("ownerUpdated").GetString());
            Assert.AreEqual("unchecked", answer.GetProperty("restoreVerification").GetString());
            Assert.AreEqual("owner", answer.GetProperty("restoreScope").GetString());
            Assert.AreEqual(disabled ? initial : "Ada", answer.GetProperty("after").GetString());
            StringAssert.Contains(answer.GetProperty("reason").GetString()!, "neither restoration nor failure");
        }
        Assert.AreEqual(3, bindings.Calls);
        Assert.AreEqual("Grace", second.Text);
        Assert.AreEqual(0, other.Calls);
    }

    [TestMethod]
    public void SourceReplacementIsReadFromGeneratedComponentWithoutCachingDataContext()
    {
        var row = new Row("Ada");
        var bindings = new TemplateBindings(new Item("Ada"), row);
        var previous = Resolve(row, bindings)!.Source;
        row.DataContext = new Item("DataContext changed");
        Assert.AreSame(previous, Resolve(row, bindings)!.Source);
        bindings.dataRoot = new Item("Recycled template item");
        Assert.AreSame(bindings.dataRoot, Resolve(row, bindings)!.Source);
        Assert.AreNotSame(previous, Resolve(row, bindings)!.Source);
    }

    [TestMethod]
    [DataRow("Default", "LostFocus")]
    [DataRow("PropertyChanged", "PropertyChanged")]
    [DataRow("Explicit", "Explicit")]
    [DataRow("LostFocus", "LostFocus")]
    public void TextBoxTriggerMetadataKeepsCommitContractsDistinct(string trigger, string expected)
    {
        Assert.AreEqual(expected, BindingDiagnosis.EffectiveTrigger(trigger, typeof(TextBox), "Text"));
        var sb = new StringBuilder("{\"state\":\"captured\"");
        BindingDiagnosis.CompiledEditMetadata(sb, "{x:Bind Vm.Title, Mode=TwoWay, UpdateSourceTrigger=" + trigger + "}",
            typeof(TextBox), "Text");
        using var result = JsonDocument.Parse(sb.Append('}').ToString());
        Assert.AreEqual(expected, result.RootElement.GetProperty("updateSourceTrigger").GetString());
    }

    [TestMethod]
    [DataRow("{Binding}", "OneWay", "NEW", "NEW")]
    [DataRow("{Binding}", "OneTime", "NEW", "INITIAL")]
    [DataRow("{Binding}", "OneTime", "REPLACEMENT", "REPLACEMENT")]
    [DataRow("{x:Bind}", "OneTime", "NEW", "INITIAL")]
    [DataRow("{x:Bind}", "Unknown", "NEW", "NEW")]
    [DataRow("{x:Bind}", "OneWay", "NEW", "NEW")]
    public void NormalModeAndContextObservationsAreNotMisdiagnosed(string kind, string mode, string source, string target)
    {
        using var result = JsonDocument.Parse(BindingDiagnosis.Evaluate("Name", new Item(source), "Item", kind,
            mode, typeof(string), "Text", () => target));
        Assert.AreEqual("evaluated", result.RootElement.GetProperty("state").GetString());
        Assert.AreEqual(source, result.RootElement.GetProperty("sourceValue").GetString());
        Assert.AreEqual(target, result.RootElement.GetProperty("targetValue").GetString());
        Assert.AreEqual("unchecked", result.RootElement.GetProperty("reversePropagation").GetString());
    }

    [TestMethod]
    [DataRow("{x:Bind Vm.Title, Mode=OneTime}", "OneTime", "False")]
    [DataRow("{x:Bind Vm.Title, Mode=OneWay}", "OneWay", "False")]
    [DataRow("{x:Bind Vm.Title}", "Unknown", "Unknown")]
    [DataRow("", "Unknown", "Unknown")]
    public void DeclaredModeAndMissingInheritedContextRemainDistinct(string authored, string mode, string writes)
    {
        var sb = new StringBuilder("{\"state\":\"captured\"");
        BindingDiagnosis.CompiledEditMetadata(sb, authored, typeof(TextBox), "Text");
        using var result = JsonDocument.Parse(sb.Append('}').ToString());
        Assert.AreEqual(mode, result.RootElement.GetProperty("mode").GetString());
        Assert.AreEqual(writes, result.RootElement.GetProperty("writesThrough").GetString());
    }

    private enum VisibilityValue { Visible, Collapsed }

    [TestMethod]
    [DataRow(7, "7", typeof(string))]
    [DataRow("42.5", 42.5, typeof(double))]
    [DataRow(false, VisibilityValue.Collapsed, typeof(VisibilityValue))]
    [DataRow("not-a-number", 0.0, typeof(double))]
    public void FrameworkConversionObservationsStayUnverifiedRatherThanBroken(object source, object target, Type targetType)
    {
        using var result = JsonDocument.Parse(BindingDiagnosis.Evaluate("", source, "Source", "{Binding}",
            "OneTime", targetType, "Value", () => target));
        Assert.AreEqual("silent", result.RootElement.GetProperty("state").GetString());
        Assert.AreEqual("unchecked", result.RootElement.GetProperty("reversePropagation").GetString());
        StringAssert.Contains(result.RootElement.GetProperty("reason").GetString()!, "Forward");
    }

    public sealed class ThrowingBindings
    {
        public void Update() => throw new InvalidOperationException("update rejected");
    }

    [TestMethod]
    public void FailedUpdateIsNotACompletedOwnerRefresh()
    {
        var owner = new BindingDiagnosis.CompiledOwner(new object(), new object(), new ThrowingBindings());
        using var result = JsonDocument.Parse(BindingDiagnosis.RestoreCompiled(owner, true, () => "INITIAL"));
        Assert.AreEqual("unavailable", result.RootElement.GetProperty("state").GetString());
        Assert.IsFalse(result.RootElement.TryGetProperty("ownerUpdated", out _));
        Assert.AreEqual("False", result.RootElement.GetProperty("nativeFallback").GetString());
        StringAssert.Contains(result.RootElement.GetProperty("reason").GetString()!, "update rejected");
    }

    [TestMethod]
    public void MissingUpdateCannotBeCapturedAsRestorable()
    {
        var owner = new BindingDiagnosis.CompiledOwner(new object(), new object(), new object());
        var sb = new StringBuilder("{\"state\":\"captured\"");
        BindingDiagnosis.CompiledCaptureMetadata(sb, owner);
        using var capture = JsonDocument.Parse(sb.Append('}').ToString());
        Assert.AreEqual("none", capture.RootElement.GetProperty("restoreKind").GetString());
        using var restore = JsonDocument.Parse(BindingDiagnosis.RestoreCompiled(owner, true, () => "OVERRIDE"));
        Assert.AreEqual("unavailable", restore.RootElement.GetProperty("state").GetString());
    }

    [TestMethod]
    public void ExplicitOneTimeRefreshUsesCurrentSourceButDoesNotUndoTheModel()
    {
        var source = new Item("INITIAL");
        var row = new Row(source.Name);
        var bindings = new TemplateBindings(source, row);
        source.Name = "CURRENT";
        Assert.AreEqual("INITIAL", row.Text);
        using var result = JsonDocument.Parse(BindingDiagnosis.RestoreCompiled(Resolve(row, bindings), true, () => row.Text));
        Assert.AreEqual("CURRENT", row.Text);
        Assert.AreEqual("CURRENT", source.Name);
        Assert.AreEqual("unchecked", result.RootElement.GetProperty("restoreVerification").GetString());
    }
}

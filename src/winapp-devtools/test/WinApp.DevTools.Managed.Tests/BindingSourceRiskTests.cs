// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinApp.DevTools.Managed.Tests;

[TestClass]
public sealed class BindingSourceRiskTests
{
    private sealed class Element
    {
        public string Tag => "SELF-VALUE";
        public object DataContext => new object();
    }
    private sealed class TemplateOwner { public string Content => "TEMPLATE-VALUE"; }

    [TestMethod]
    public void SelfEvaluatesTargetInsteadOfItsUnrelatedDataContext()
    {
        var element = new Element();
        object? source = BindingDiagnosis.ResolveRelativeSource(RelativeSourceMode.Self, element,
            () => throw new AssertFailedException("Self must not consult another source"));
        Assert.AreSame(element, source);
        using var result = JsonDocument.Parse(BindingDiagnosis.Evaluate("Tag", source, "RelativeSource=Self",
            "{Binding}", "OneWay", typeof(string), "Text", () => "SELF-VALUE"));
        Assert.AreEqual("evaluated", result.RootElement.GetProperty("state").GetString());
        Assert.AreEqual("SELF-VALUE", result.RootElement.GetProperty("sourceValue").GetString());
    }

    [TestMethod]
    public void TemplatedParentEvaluatesActualOwnerInsteadOfDataContext()
    {
        var owner = new TemplateOwner();
        object? source = BindingDiagnosis.ResolveRelativeSource(RelativeSourceMode.TemplatedParent, new Element(), () => owner);
        Assert.AreSame(owner, source);
        using var result = JsonDocument.Parse(BindingDiagnosis.Evaluate("Content", source, "RelativeSource=TemplatedParent",
            "{Binding}", "OneWay", typeof(string), "Text", () => "TEMPLATE-VALUE"));
        Assert.AreEqual("evaluated", result.RootElement.GetProperty("state").GetString());
        Assert.AreEqual("TEMPLATE-VALUE", result.RootElement.GetProperty("sourceValue").GetString());
    }

    [TestMethod]
    public void MissingOrInaccessibleTemplateOwnerAndUnknownModeNeverUseDataContext()
    {
        var element = new Element();
        Assert.IsNull(BindingDiagnosis.ResolveRelativeSource(RelativeSourceMode.TemplatedParent, element, () => null));
        Assert.IsNull(BindingDiagnosis.ResolveRelativeSource(RelativeSourceMode.TemplatedParent, element,
            () => throw new InvalidOperationException("not exposed")));
        Assert.IsNull(BindingDiagnosis.ResolveRelativeSource((RelativeSourceMode)999, element, () => element.DataContext));
    }

    [TestMethod]
    [DataRow("{x:Bind Vm.XChangedText, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}", "TwoWay", "PropertyChanged", "True")]
    [DataRow("{x:Bind Vm.XChangedText, Mode = TwoWay, UpdateSourceTrigger = LostFocus}", "TwoWay", "LostFocus", "True")]
    [DataRow("{x:Bind Vm.Title, Mode=TwoWay}", "TwoWay", "LostFocus", "True")]
    [DataRow("{x:Bind Vm.Title, Mode=OneWay}", "OneWay", "LostFocus", "False")]
    [DataRow("{x:Bind Vm.Title}", "Unknown", "LostFocus", "Unknown")]
    [DataRow("", "Unknown", "Unknown", "Unknown")]
    [DataRow("{x:Bind Vm.Title, Mode=Invalid}", "Unknown", "LostFocus", "Unknown")]
    [DataRow("{x:Bind Vm.Title, Mode=TwoWay, Mode=OneWay}", "Unknown", "LostFocus", "Unknown")]
    [DataRow("{x:Bind Vm.Title, Mode=TwoWay", "Unknown", "Unknown", "Unknown")]
    [DataRow("{x:Bind Vm.Title, Mode=TwoWay, UpdateSourceTrigger=Invalid}", "TwoWay", "Unknown", "True")]
    [DataRow("{x:Bind Vm.Title, Converter={StaticResource C}, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}", "TwoWay", "PropertyChanged", "True")]
    public void CompiledCaptureDoesNotConfuseMissingExpressionWithNoSourceWrites(string authored, string mode, string trigger, string writes)
    {
        var sb = new StringBuilder("{\"state\":\"captured\"");
        BindingDiagnosis.CompiledEditMetadata(sb, authored, typeof(TextBox), "Text");
        using var result = JsonDocument.Parse(sb.Append('}').ToString());
        Assert.AreEqual(mode, result.RootElement.GetProperty("mode").GetString());
        Assert.AreEqual(trigger, result.RootElement.GetProperty("updateSourceTrigger").GetString());
        Assert.AreEqual(writes, result.RootElement.GetProperty("writesThrough").GetString());
        if (writes != "False")
        {
            string warning = result.RootElement.GetProperty("editWarning").GetString()!;
            StringAssert.Contains(warning, "source model");
            StringAssert.Contains(warning, "does not undo");
            if (trigger == "PropertyChanged") StringAssert.Contains(warning, "immediately");
        }
    }

    [TestMethod]
    public void UnprovenTargetDefaultTriggerRemainsUnknown()
    {
        var sb = new StringBuilder("{\"state\":\"captured\"");
        BindingDiagnosis.CompiledEditMetadata(sb, "{x:Bind Vm.Value, Mode=TwoWay}", typeof(object), "Value");
        using var result = JsonDocument.Parse(sb.Append('}').ToString());
        Assert.AreEqual("Unknown", result.RootElement.GetProperty("updateSourceTrigger").GetString());
        Assert.AreEqual("True", result.RootElement.GetProperty("writesThrough").GetString());
    }

    public sealed class Model { public string Value = "ORIGINAL"; }
    public sealed class GeneratedBindings(Model model)
    {
        public int Calls;
        public string Target = "ORIGINAL";
        public void Update() { Calls++; Target = model.Value; }
    }
    public sealed class CompiledOwner(Model model) { public GeneratedBindings Bindings = new(model); }

    [TestMethod]
    public void RestoreDisclosesAndRetainsSourceMutationRatherThanClaimingModelUndo()
    {
        var model = new Model { Value = "DEVTOOLS-LITERAL" };
        var owner = new CompiledOwner(model);
        using var preview = JsonDocument.Parse(BindingDiagnosis.RebindOwner(owner, false)!);
        StringAssert.Contains(preview.RootElement.GetProperty("restoreDisclosure").GetString()!, "not undone");
        Assert.AreEqual(0, owner.Bindings.Calls);
        Assert.IsNull(BindingDiagnosis.RebindOwner(owner, true));
        Assert.AreEqual(1, owner.Bindings.Calls);
        Assert.AreEqual("DEVTOOLS-LITERAL", model.Value);
        Assert.AreEqual(model.Value, owner.Bindings.Target);
    }
}

// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinApp.DevTools.Managed.Tests;

[TestClass]
public sealed class BindingAcceptanceTests
{
    public sealed class PageOwner : Page { public Updates Bindings = new(); }
    public sealed class ControlOwner : UserControl { public Updates Bindings = new(); }
    public sealed class DialogOwner : ContentDialog { public Updates Bindings = new(); }
    [System.CodeDom.Compiler.GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", "test")]
    public sealed class Updates
    {
        public object? dataRoot;
        public object? obj1;
        public object? obj2;
        public int Calls;
        public string First = "first-edit";
        public string Other = "other-edit";
        public void Update() { Calls++; First = "first-source"; Other = "other-source"; }
    }
    public sealed class CompiledOwner { public Updates Bindings = new(); }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("{Binding Value}")]
    [DataRow("{ThemeResource Label}")]
    public void MissingAuthoredLiteralCannotRuleOutCompiledBindings(string authored)
    {
        using var result = JsonDocument.Parse(BindingDiagnosis.DiagnoseWithoutRuntimeBinding(authored));
        Assert.AreEqual("unavailable", result.RootElement.GetProperty("state").GetString());
        StringAssert.Contains(result.RootElement.GetProperty("reason").GetString()!, "cannot be ruled out");
    }

    [TestMethod]
    [DataRow("Literal")]
    [DataRow("{}{not a binding}")]
    public void KnownAuthoredLiteralMayReportNone(string authored)
    {
        using var result = JsonDocument.Parse(BindingDiagnosis.DiagnoseWithoutRuntimeBinding(authored));
        Assert.AreEqual("none", result.RootElement.GetProperty("state").GetString());
    }

    [TestMethod]
    public void NamescopeOwnersArePreservedWithoutConstructingControlsOrWindows()
    {
        foreach (Type type in new[] { typeof(PageOwner), typeof(ControlOwner), typeof(DialogOwner) })
        {
            var owner = (FrameworkElement)RuntimeHelpers.GetUninitializedObject(type);
            var child = (FrameworkElement)RuntimeHelpers.GetUninitializedObject(typeof(FrameworkElement));
            type.GetField("Bindings")!.SetValue(owner, new Updates { dataRoot = owner, obj1 = child, obj2 = owner });
            Assert.AreSame(owner, BindingDiagnosis.XBindSource(child, node => ReferenceEquals(node, child) ? owner : null, _ => null));
            Assert.AreSame(owner, BindingDiagnosis.XBindSource(owner, _ => throw new AssertFailedException("Nearest owner must win"), _ => null));
        }
    }

    [TestMethod]
    public void WindowRootWithoutAProvenOwnerIsUnavailableAcrossSelections()
    {
        var child = (FrameworkElement)RuntimeHelpers.GetUninitializedObject(typeof(FrameworkElement));
        var owner = (PageOwner)RuntimeHelpers.GetUninitializedObject(typeof(PageOwner));
        owner.Bindings = new Updates { dataRoot = owner, obj1 = child };
        Assert.AreSame(owner, BindingDiagnosis.XBindSource(child, _ => owner, _ => null));
        Assert.IsNull(BindingDiagnosis.XBindSource(child, _ => null, _ => null), "A previous surface must never supply a cached owner.");
        using var result = JsonDocument.Parse(BindingDiagnosis.RebindOwner(null, true)!);
        Assert.AreEqual("unavailable", result.RootElement.GetProperty("state").GetString());
    }

    [TestMethod]
    public void CompiledRestoreDisclosesOwnerWideEffectBeforeAnyWrite()
    {
        var owner = new CompiledOwner();
        using var preview = JsonDocument.Parse(BindingDiagnosis.RebindOwner(owner, false)!);
        Assert.AreEqual("confirmation-required", preview.RootElement.GetProperty("state").GetString());
        Assert.AreEqual("owner", preview.RootElement.GetProperty("restoreScope").GetString());
        Assert.AreEqual(nameof(CompiledOwner), preview.RootElement.GetProperty("restoreOwner").GetString());
        StringAssert.Contains(preview.RootElement.GetProperty("warning").GetString()!, "other live edits");
        Assert.AreEqual(0, owner.Bindings.Calls);
        Assert.AreEqual("other-edit", owner.Bindings.Other);
        Assert.IsNull(BindingDiagnosis.RebindOwner(owner, true));
        Assert.AreEqual(1, owner.Bindings.Calls);
        Assert.AreEqual("first-source", owner.Bindings.First);
        Assert.AreEqual("other-source", owner.Bindings.Other);
    }

    [TestMethod]
    public void OwnerWithoutUpdateCannotClaimRestore()
    {
        using var result = JsonDocument.Parse(BindingDiagnosis.RebindOwner(new object(), true)!);
        Assert.AreEqual("unavailable", result.RootElement.GetProperty("state").GetString());
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Tests;

/// <summary>
/// Direct unit tests for the small UI helper types (<see cref="UiSymbols"/>,
/// <see cref="UiElementScrubber"/>) that are otherwise only exercised incidentally through the
/// command JSON paths.
/// </summary>
[TestClass]
public class UiHelpersTests
{
    [TestMethod]
    public void UiSymbols_EveryGlyph_ResolvesToNonEmptyString()
    {
        var props = typeof(UiSymbols).GetProperties(BindingFlags.Public | BindingFlags.Static);

        Assert.IsTrue(props.Length >= 20, "UiSymbols is expected to expose the full glyph set.");
        foreach (var p in props)
        {
            var value = (string?)p.GetValue(null);
            Assert.IsFalse(string.IsNullOrEmpty(value), $"UiSymbols.{p.Name} must resolve to a non-empty glyph.");
        }
    }

    [TestMethod]
    public void Scrub_Null_IsNoOp()
    {
        // Should simply return without throwing.
        UiElementScrubber.Scrub(null);
    }

    [TestMethod]
    public void ScrubAll_Null_IsNoOp()
    {
        UiElementScrubber.ScrubAll(null);
    }

    [TestMethod]
    public void Scrub_NestedTree_ClearsInternalFieldsRecursively()
    {
        var child = new UiElement
        {
            Type = "Button",
            Id = "e1",
            ParentSelector = "root-sel",
            WindowHandle = 0x100,
            Selector = "btn-ok",
        };
        var root = new UiElement
        {
            Type = "Window",
            Id = "e0",
            ParentSelector = "none",
            WindowHandle = 0x100,
            Selector = "win-main",
            Children = [child],
            InvokableAncestor = new UiElement
            {
                Type = "Pane",
                Id = "e-anc",
                Selector = "pane-1",
                IsInvokable = true,
                IsEnabled = true,
                IsOffscreen = true,
                X = 10, Y = 20, Width = 300, Height = 40,
                Children = [new UiElement { Type = "Leaf" }],
            },
        };

        UiElementScrubber.Scrub(root);

        // Root internal fields stripped.
        Assert.IsNull(root.Id);
        Assert.IsNull(root.ParentSelector);
        Assert.IsNull(root.WindowHandle);
        Assert.AreEqual("win-main", root.Selector);

        // Child scrubbed via recursion (the Children branch).
        Assert.IsNull(child.Id);
        Assert.IsNull(child.ParentSelector);
        Assert.IsNull(child.WindowHandle);
        Assert.AreEqual("btn-ok", child.Selector);

        // InvokableAncestor flattened to a cycle-free hint: label fields kept, nested tree dropped.
        Assert.IsNotNull(root.InvokableAncestor);
        Assert.AreEqual("Pane", root.InvokableAncestor!.Type);
        Assert.AreEqual("pane-1", root.InvokableAncestor.Selector);
        Assert.IsTrue(root.InvokableAncestor.IsInvokable);
        // Real state and bounds survive the flattening (not false / 0,0).
        Assert.IsTrue(root.InvokableAncestor.IsEnabled);
        Assert.IsTrue(root.InvokableAncestor.IsOffscreen);
        Assert.AreEqual(10, root.InvokableAncestor.X);
        Assert.AreEqual(20, root.InvokableAncestor.Y);
        Assert.AreEqual(300, root.InvokableAncestor.Width);
        Assert.AreEqual(40, root.InvokableAncestor.Height);
        Assert.IsNull(root.InvokableAncestor.Id);
        Assert.IsNull(root.InvokableAncestor.Children);
    }

    [TestMethod]
    public void ScrubAll_FlatList_ScrubsEachElement()
    {
        var a = new UiElement { Type = "A", Id = "e0", WindowHandle = 1 };
        var b = new UiElement { Type = "B", Id = "e1", WindowHandle = 2 };

        UiElementScrubber.ScrubAll([a, b]);

        Assert.IsNull(a.Id);
        Assert.IsNull(a.WindowHandle);
        Assert.IsNull(b.Id);
        Assert.IsNull(b.WindowHandle);
    }
}

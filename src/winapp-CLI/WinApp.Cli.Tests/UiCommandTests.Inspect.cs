// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Models;

namespace WinApp.Cli.Tests;

/// <summary>
/// Covers <c>winapp ui inspect</c>'s non-JSON render path (tree rows with every decoration,
/// +more truncation hints, interactive breadcrumbs, and the footer), the hide-disabled/-offscreen
/// filters, the two-level invokable-ancestor walk, and the COMException / generic error branches.
/// All exercised through the command layer with fakes.
/// </summary>
public partial class UiCommandTests
{
    [TestMethod]
    public async Task Inspect_MissingApp_ReturnsError()
    {
        var command = GetRequiredService<UiInspectCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Inspect_NonJson_NonInteractive_RendersTreeAndFooter()
    {
        _fakeUia.InspectResult =
        [
            new UiElement { Type = "---", Name = "HWND 100: \"Win A\" (window, ClassA)" },
            new UiElement { Type = "Pane", Depth = 0, Selector = "pane-1", Width = 800, Height = 600, IsEnabled = true },
            new UiElement
            {
                Type = "Button",
                Depth = 1,
                Selector = "btn-ok-1",
                Name = "OK",
                Value = "clicked",
                ToggleState = "on",
                ExpandState = "expanded",
                ScrollDir = "vh",
                X = 1,
                Y = 2,
                Width = 100,
                Height = 30,
                IsEnabled = false,   // → [disabled]
                IsOffscreen = true,  // → [offscreen]
                HasMoreChildren = true, // → +more line + footer truncation hint
            },
            new UiElement { Type = "---", Name = "HWND 200: \"Win B\" (window, ClassB)" }, // 2nd separator → "Use -w"
        ];

        var command = GetRequiredService<UiInspectCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp"]);

        Assert.AreEqual(0, exitCode);
        var output = TestAnsiConsole.Output;
        StringAssert.Contains(output, "pane-1");
        StringAssert.Contains(output, "btn-ok-1");
        StringAssert.Contains(output, "clicked");
        StringAssert.Contains(output, "disabled");
        StringAssert.Contains(output, "offscreen");
        StringAssert.Contains(output, "+more");
        StringAssert.Contains(output, "hidden children");
        StringAssert.Contains(output, "Use -w");
        StringAssert.Contains(output, "Use -i");
    }

    [TestMethod]
    public async Task Inspect_NonJson_Interactive_RendersBreadcrumbs()
    {
        _fakeUia.InspectResult =
        [
            // Non-interactive ancestor whose subtree was truncated → breadcrumb + +more hint.
            new UiElement { Type = "Pane", Depth = 0, Selector = "pane-1", IsInvokable = false, HasMoreChildren = true, IsEnabled = true },
            // Non-interactive intermediate ancestor.
            new UiElement { Type = "Group", Depth = 1, Selector = "grp-1", IsInvokable = false, IsEnabled = true },
            // Interactive leaf at depth 2 → breadcrumb "Pane > Group" then the row.
            new UiElement { Type = "Button", Depth = 2, Selector = "btn-1", Name = "Go", IsEnabled = true },
        ];

        var command = GetRequiredService<UiInspectCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "-i"]);

        Assert.AreEqual(0, exitCode);
        var output = TestAnsiConsole.Output;
        StringAssert.Contains(output, "Pane");
        StringAssert.Contains(output, "Pane > Group");
        StringAssert.Contains(output, "btn-1");
        StringAssert.Contains(output, "+more");
    }

    [TestMethod]
    public async Task Inspect_NonJson_Empty_RendersFooterWithoutExample()
    {
        _fakeUia.InspectResult = [];
        var command = GetRequiredService<UiInspectCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp"]);
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "Found 0 elements");
    }

    [TestMethod]
    public async Task Inspect_HideDisabledAndOffscreen_FiltersElements()
    {
        _fakeUia.InspectResult =
        [
            new UiElement { Type = "Button", Depth = 0, Selector = "on-1", Name = "Enabled", IsEnabled = true, IsOffscreen = false, Width = 10, Height = 10 },
            new UiElement { Type = "Button", Depth = 0, Selector = "off-1", Name = "Disabled", IsEnabled = false, IsOffscreen = false },
            new UiElement { Type = "Button", Depth = 0, Selector = "hidden-1", Name = "Offscreen", IsEnabled = true, IsOffscreen = true },
        ];

        var command = GetRequiredService<UiInspectCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--hide-disabled", "--hide-offscreen"]);

        Assert.AreEqual(0, exitCode);
        var output = TestAnsiConsole.Output;
        StringAssert.Contains(output, "on-1");
        Assert.IsFalse(output.Contains("off-1"));
        Assert.IsFalse(output.Contains("hidden-1"));
    }

    [TestMethod]
    public async Task Inspect_Interactive_Json_AttachesGrandparentInvokableAncestor()
    {
        // child (interactive, not invokable) → parent (not invokable) → grandparent (invokable).
        // The ancestor walk must hop past the parent to the grandparent (the 2-level branch).
        _fakeUia.InspectResult =
        [
            new UiElement { Type = "Custom", Depth = 0, Selector = "gp", IsInvokable = true, IsEnabled = true },
            new UiElement { Type = "Group", Depth = 1, Selector = "p", ParentSelector = "gp", IsInvokable = false, IsEnabled = true },
            new UiElement { Type = "MenuItem", Depth = 2, Selector = "c", ParentSelector = "p", Name = "Item", IsInvokable = false, IsEnabled = true },
        ];

        var command = GetRequiredService<UiInspectCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "-i", "--json"]);

        Assert.AreEqual(0, exitCode);
        // The grandparent selector surfaces as the invokable-ancestor hint on the menu item.
        StringAssert.Contains(TestAnsiConsole.Output, "\"gp\"");
    }

    [TestMethod]
    public async Task Inspect_Com_ReturnsError()
    {
        _fakeUia.InspectThrow = FakeComException;
        var command = GetRequiredService<UiInspectCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Inspect_Generic_ReturnsError()
    {
        _fakeTargetResolver.ResolveThrow = FakeGenericException;
        var command = GetRequiredService<UiInspectCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }
}

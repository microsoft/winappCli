// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using WinApp.Cli.Commands;
using WinApp.Cli.Models;

namespace WinApp.Cli.Tests;

/// <summary>
/// Covers the "simple verb" UI commands (status, focus, get-focused, get-property, get-value,
/// set-value, scroll-into-view, invoke): short descriptions, non-JSON output, and the shared
/// missing-app / missing-selector / element-not-found / COMException / generic error branches.
/// All exercised through the command layer with fakes — no real UIA.
/// </summary>
public partial class UiCommandTests
{
    private static readonly COMException FakeComException = new("stale (test)");
    private static readonly InvalidOperationException FakeGenericException = new("boom (test)");

    [TestMethod]
    public void SimpleVerbs_ShortDescriptions_AreNonEmpty()
    {
        // Reading each command's ShortDescription (the IShortDescription surface) — a real palette
        // check that the help metadata is present for every verb. Covers the getter for every
        // command file in one place.
        foreach (var description in new[]
        {
            GetRequiredService<UiStatusCommand>().ShortDescription,
            GetRequiredService<UiFocusCommand>().ShortDescription,
            GetRequiredService<UiGetFocusedCommand>().ShortDescription,
            GetRequiredService<UiGetPropertyCommand>().ShortDescription,
            GetRequiredService<UiGetValueCommand>().ShortDescription,
            GetRequiredService<UiSetValueCommand>().ShortDescription,
            GetRequiredService<UiScrollIntoViewCommand>().ShortDescription,
            GetRequiredService<UiInvokeCommand>().ShortDescription,
            GetRequiredService<UiInspectCommand>().ShortDescription,
            GetRequiredService<UiSearchCommand>().ShortDescription,
            GetRequiredService<UiScreenshotCommand>().ShortDescription,
            GetRequiredService<UiWaitForCommand>().ShortDescription,
            GetRequiredService<UiListWindowsCommand>().ShortDescription,
            GetRequiredService<UiClickCommand>().ShortDescription,
            GetRequiredService<UiScrollCommand>().ShortDescription,
            GetRequiredService<UiHoverCommand>().ShortDescription,
            GetRequiredService<UiDragCommand>().ShortDescription,
            GetRequiredService<UiSendKeysCommand>().ShortDescription,
        })
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(description));
        }
    }

    // ---------- status ----------

    [TestMethod]
    public async Task Status_NonJson_PrintsProcessAndHwnd()
    {
        _fakeTargetResolver.TargetResult = new UiTarget
        {
            ProcessId = 4321,
            ProcessName = "Notepad",
            WindowTitle = "Untitled - Notepad",
            WindowHandle = 0x1234,
        };

        var command = GetRequiredService<UiStatusCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "Notepad"]);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "Process: Notepad");
        StringAssert.Contains(TestAnsiConsole.Output, "PID: 4321");
        StringAssert.Contains(TestAnsiConsole.Output, "HWND: 4660");
    }

    [TestMethod]
    public async Task Status_Generic_ReturnsError()
    {
        _fakeTargetResolver.ResolveThrow = FakeGenericException;

        var command = GetRequiredService<UiStatusCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
    }

    // ---------- focus ----------

    [TestMethod]
    public async Task Focus_MissingApp_ReturnsError()
    {
        var command = GetRequiredService<UiFocusCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e0"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Focus_MissingSelector_ReturnsError()
    {
        var command = GetRequiredService<UiFocusCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Focus_ElementNotFound_ReturnsError()
    {
        _fakeUia.FindSingleResult = null;
        var command = GetRequiredService<UiFocusCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["#Nope", "-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Focus_Com_ReturnsError()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Name = "OK" };
        _fakeUia.FocusThrow = FakeComException;
        var command = GetRequiredService<UiFocusCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e0", "-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Focus_Generic_ReturnsError()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Name = "OK" };
        _fakeUia.FocusThrow = FakeGenericException;
        var command = GetRequiredService<UiFocusCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e0", "-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    // ---------- get-focused ----------

    [TestMethod]
    public async Task GetFocused_MissingApp_ReturnsError()
    {
        var command = GetRequiredService<UiGetFocusedCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task GetFocused_NonJson_NoFocus_ReturnsSuccess()
    {
        _fakeUia.FocusedResult = null;
        var command = GetRequiredService<UiGetFocusedCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp"]);
        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task GetFocused_NonJson_WithFocus_RendersElement()
    {
        _fakeUia.FocusedResult = new UiElement
        {
            Id = "e0",
            Type = "Edit",
            Name = "Address bar",
            Selector = "edit-address-9f",
            Value = "https://example.com",
            X = 10,
            Y = 20,
            Width = 300,
            Height = 24,
        };

        var command = GetRequiredService<UiGetFocusedCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp"]);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "edit-address-9f");
        StringAssert.Contains(TestAnsiConsole.Output, "Address bar");
        StringAssert.Contains(TestAnsiConsole.Output, "https://example.com");
    }

    [TestMethod]
    public async Task GetFocused_Com_ReturnsError()
    {
        _fakeUia.GetFocusedThrow = FakeComException;
        var command = GetRequiredService<UiGetFocusedCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task GetFocused_Generic_ReturnsError()
    {
        _fakeTargetResolver.ResolveThrow = FakeGenericException;
        var command = GetRequiredService<UiGetFocusedCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    // ---------- get-property ----------

    [TestMethod]
    public async Task GetProperty_MissingApp_ReturnsError()
    {
        var command = GetRequiredService<UiGetPropertyCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e0"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task GetProperty_MissingSelector_ReturnsError()
    {
        var command = GetRequiredService<UiGetPropertyCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task GetProperty_ElementNotFound_ReturnsError()
    {
        _fakeUia.FindSingleResult = null;
        var command = GetRequiredService<UiGetPropertyCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["#Nope", "-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task GetProperty_NonJson_PrintsSanitizedValues()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Edit", Name = "Body" };
        _fakeUia.PropertiesResult = new Dictionary<string, object?>
        {
            ["Name"] = "Body",
            ["Value"] = "line1\r\nline2\ttabbed",
            ["Empty"] = null,
        };

        var command = GetRequiredService<UiGetPropertyCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e0", "-a", "TestApp"]);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "Name: Body");
        StringAssert.Contains(TestAnsiConsole.Output, "line1↵line2→tabbed");
        StringAssert.Contains(TestAnsiConsole.Output, "Empty: (null)");
    }

    [TestMethod]
    public async Task GetProperty_SingleProperty_PassesPropertyName()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Name = "OK" };
        _fakeUia.PropertiesResult = new Dictionary<string, object?> { ["IsEnabled"] = true };

        var command = GetRequiredService<UiGetPropertyCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e0", "-a", "TestApp", "--property", "IsEnabled", "--json"]);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "IsEnabled");
    }

    [TestMethod]
    public async Task GetProperty_Com_ReturnsError()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Name = "OK" };
        _fakeUia.PropertiesThrow = FakeComException;
        var command = GetRequiredService<UiGetPropertyCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e0", "-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task GetProperty_Generic_ReturnsError()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Name = "OK" };
        _fakeUia.PropertiesThrow = FakeGenericException;
        var command = GetRequiredService<UiGetPropertyCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e0", "-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    // ---------- get-value ----------

    [TestMethod]
    public async Task GetValue_MissingApp_ReturnsError()
    {
        var command = GetRequiredService<UiGetValueCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e0"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task GetValue_ElementNotFound_ReturnsError()
    {
        _fakeUia.FindSingleResult = null;
        var command = GetRequiredService<UiGetValueCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["#Nope", "-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task GetValue_NonJson_PrintsNormalizedText()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e1", Type = "Document", Name = "Editor" };
        _fakeUia.GetTextResult = "hello\r\nworld\n";

        var command = GetRequiredService<UiGetValueCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e1", "-a", "TestApp"]);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "hello");
        StringAssert.Contains(TestAnsiConsole.Output, "world");
    }

    [TestMethod]
    public async Task GetValue_NonJson_NoValue_ReturnsSuccess()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e1", Type = "Document", Name = "Editor" };
        _fakeUia.GetTextResult = null;

        var command = GetRequiredService<UiGetValueCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e1", "-a", "TestApp"]);

        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task GetValue_Com_ReturnsError()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e1", Type = "Document", Name = "Editor" };
        _fakeUia.GetTextThrow = FakeComException;
        var command = GetRequiredService<UiGetValueCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e1", "-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task GetValue_Generic_ReturnsError()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e1", Type = "Document", Name = "Editor" };
        _fakeUia.GetTextThrow = FakeGenericException;
        var command = GetRequiredService<UiGetValueCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e1", "-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    // ---------- set-value ----------

    [TestMethod]
    public async Task SetValue_MissingApp_ReturnsError()
    {
        var command = GetRequiredService<UiSetValueCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e1", "Hello"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task SetValue_MissingSelector_ReturnsError()
    {
        // No positional selector: the sole value token is treated as the selector, so the value
        // argument is absent — but with app present this exercises the missing-selector branch
        // only when the selector itself is blank. Pass an explicit blank selector.
        var command = GetRequiredService<UiSetValueCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [" ", "Hello", "-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task SetValue_ElementNotFound_ReturnsError()
    {
        _fakeUia.FindSingleResult = null;
        var command = GetRequiredService<UiSetValueCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["#Nope", "Hello", "-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task SetValue_Com_ReturnsError()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e1", Type = "Edit", Name = "Edit" };
        _fakeUia.SetValueThrow = FakeComException;
        var command = GetRequiredService<UiSetValueCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e1", "Hello", "-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task SetValue_Generic_ReturnsError()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e1", Type = "Edit", Name = "Edit" };
        _fakeUia.SetValueThrow = FakeGenericException;
        var command = GetRequiredService<UiSetValueCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e1", "Hello", "-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    // ---------- scroll-into-view ----------

    [TestMethod]
    public async Task ScrollIntoView_MissingApp_ReturnsError()
    {
        var command = GetRequiredService<UiScrollIntoViewCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e0"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task ScrollIntoView_MissingSelector_ReturnsError()
    {
        var command = GetRequiredService<UiScrollIntoViewCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task ScrollIntoView_ElementNotFound_ReturnsError()
    {
        _fakeUia.FindSingleResult = null;
        var command = GetRequiredService<UiScrollIntoViewCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["#Nope", "-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task ScrollIntoView_NonJson_ReturnsSuccess()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "ListItem", Name = "Row 42", Selector = "item-42" };
        var command = GetRequiredService<UiScrollIntoViewCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["item-42", "-a", "TestApp"]);
        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task ScrollIntoView_Com_ReturnsError()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "ListItem", Name = "Row" };
        _fakeUia.ScrollIntoViewThrow = FakeComException;
        var command = GetRequiredService<UiScrollIntoViewCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e0", "-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task ScrollIntoView_Generic_ReturnsError()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "ListItem", Name = "Row" };
        _fakeUia.ScrollIntoViewThrow = FakeGenericException;
        var command = GetRequiredService<UiScrollIntoViewCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e0", "-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    // ---------- invoke ----------

    [TestMethod]
    public async Task Invoke_MissingApp_ReturnsError()
    {
        var command = GetRequiredService<UiInvokeCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["#Submit"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Invoke_MissingSelector_ReturnsError()
    {
        var command = GetRequiredService<UiInvokeCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Invoke_NonJson_ReturnsSuccess()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Name = "Submit", Selector = "btn-submit-1" };
        _fakeUia.InvokeResult = "InvokePattern";
        var command = GetRequiredService<UiInvokeCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-submit-1", "-a", "TestApp"]);
        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task Invoke_AncestorFallback_Json_InvokesAncestor()
    {
        // Matched element isn't invokable but has an invokable ancestor. The fake throws
        // InvalidOperationException for the primary element (which carries InvokableAncestor)
        // and succeeds for the ancestor.
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "e0",
            Type = "Text",
            Name = "Label",
            Selector = "txt-label-1",
            InvokableAncestor = new UiElement { Id = "e1", Type = "Button", Name = "Submit", Selector = "btn-submit-9" },
        };
        _fakeUia.InvokeThrowsForAncestorFallback = true;
        _fakeUia.InvokeResult = "InvokePattern";

        var command = GetRequiredService<UiInvokeCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["txt-label-1", "-a", "TestApp", "--json"]);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"elementId\": \"btn-submit-9\"");
    }

    [TestMethod]
    public async Task Invoke_AncestorFallback_NonJson_InvokesAncestor()
    {
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "e0",
            Type = "Text",
            Name = "Label",
            Selector = "txt-label-1",
            InvokableAncestor = new UiElement { Id = "e1", Type = "Button", Name = "Submit", Selector = "btn-submit-9" },
        };
        _fakeUia.InvokeThrowsForAncestorFallback = true;
        _fakeUia.InvokeResult = "InvokePattern";

        var command = GetRequiredService<UiInvokeCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["txt-label-1", "-a", "TestApp"]);

        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task Invoke_Com_ReturnsError()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Name = "Submit" };
        _fakeUia.InvokeThrow = FakeComException;
        var command = GetRequiredService<UiInvokeCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e0", "-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Invoke_Generic_ReturnsError()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Name = "Submit" };
        _fakeUia.InvokeThrow = FakeGenericException;
        var command = GetRequiredService<UiInvokeCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e0", "-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }
}

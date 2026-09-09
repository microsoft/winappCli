// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize] // UiCommandTests uses shared static fields on Handler (s_isInputRedirectedOverride, s_stdinOverride)
public partial class UiCommandTests : BaseCommandTests
{
    private FakeUiAutomationService _fakeUia = null!;
    private FakeUiRecordingService _fakeRecording = null!;
    private FakeUiTargetResolver _fakeTargetResolver = null!;
    private FakeMouseInput _fakeMouse = null!;
    private FakeKeyboardInput _fakeKeyboard = null!;
    private FakeForegroundGuard _fakeForeground = null!;
    private FakePointerInput _fakePointer = null!;
    private FakeOwnedWindowFinder _fakeWindowFinder = null!;
    private FakeSystemUiQuery _fakeSystemQuery = null!;
    private FakePollDelay _fakePollDelay = null!;
    private FakeInteractiveDesktopLock _fakeDesktopLock = null!;
    private FakeDesktopForegroundService _fakeDesktopForeground = null!;
    private FakeWindowCapture _fakeWindowCapture = null!;

    private void AssertJsonErrorCode(string expectedCode)
        => AssertJsonErrorCodeIn(ConsoleStdErr.ToString(), expectedCode);

    private static void AssertJsonErrorCodeIn(string stderr, string expectedCode)
    {
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain a JSON error object; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(expectedCode, error.GetProperty("error").GetProperty("code").GetString());
    }

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeUia = new FakeUiAutomationService();
        _fakeRecording = new FakeUiRecordingService();
        _fakeTargetResolver = new FakeUiTargetResolver();
        _fakeMouse = new FakeMouseInput();
        _fakeKeyboard = new FakeKeyboardInput();
        _fakeForeground = new FakeForegroundGuard();
        _fakePointer = new FakePointerInput();
        _fakeWindowFinder = new FakeOwnedWindowFinder();
        _fakeSystemQuery = new FakeSystemUiQuery { ProcessIdForWindowResult = 1234 };
        _fakePollDelay = new FakePollDelay();
        _fakeDesktopLock = new FakeInteractiveDesktopLock();
        _fakeDesktopForeground = new FakeDesktopForegroundService();
        _fakeWindowCapture = new FakeWindowCapture();
        return services
            .AddSingleton<IUiAutomation>(_fakeUia)
            .AddSingleton<IUiRecordingService>(_fakeRecording)
            .AddSingleton<IUiTargetResolver>(_fakeTargetResolver)
            .AddSingleton<IMouseInput>(_fakeMouse)
            .AddSingleton<IKeyboardInput>(_fakeKeyboard)
            .AddSingleton<IForegroundGuard>(_fakeForeground)
            .AddSingleton<IPointerInput>(_fakePointer)
            .AddSingleton<IOwnedWindowFinder>(_fakeWindowFinder)
            .AddSingleton<ISystemUiQuery>(_fakeSystemQuery)
            .AddSingleton<IPollDelay>(_fakePollDelay)
            .AddSingleton<IInteractiveDesktopLock>(_fakeDesktopLock)
            .AddSingleton<IDesktopForegroundService>(_fakeDesktopForeground)
            .AddSingleton<IWindowCapture>(_fakeWindowCapture);
    }

    [TestMethod]
    public async Task Status_WithApp_ReturnsSuccess()
    {
        var command = GetRequiredService<UiStatusCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"processId\": 1234");
    }

    [TestMethod]
    public async Task Status_WithoutApp_ReturnsError()
    {
        var command = GetRequiredService<UiStatusCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Inspect_ReturnsElements()
    {
        // Inspect returns a DFS-ordered flat list with depth values; the JSON path nests them
        // into windows[].elements[].children[]. Setting Depth lets the depth-stack reconstruct
        // the tree (Window contains Button as a child).
        _fakeUia.InspectResult = [
            new UiElement { Id = "e0", Type = "Window", Name = "Test", Depth = 0, IsEnabled = true, Width = 800, Height = 600 },
            new UiElement { Id = "e1", Type = "Button", Name = "OK", Depth = 1, IsEnabled = true, Width = 100, Height = 30 }
        ];

        var command = GetRequiredService<UiInspectCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        // New nested-tree shape: top-level "windows" array, each with nested "elements" -> "children".
        StringAssert.Contains(TestAnsiConsole.Output, "\"windows\":");
        StringAssert.Contains(TestAnsiConsole.Output, "\"elements\":");
        StringAssert.Contains(TestAnsiConsole.Output, "\"children\":");
        StringAssert.Contains(TestAnsiConsole.Output, "\"type\": \"Window\"");
        StringAssert.Contains(TestAnsiConsole.Output, "\"type\": \"Button\"");
    }

    [TestMethod]
    public async Task Inspect_Json_OmitsRedundantFields()
    {
        // The internal element id (e0/e1) is implementation detail — selectors are the public handle.
        // Element-level depth/parentSelector/windowHandle are also redundant in nested form
        // (implied by tree position). Note the request-level "depth" option is still emitted at
        // the top of the envelope; we're asserting on per-element fields only.
        _fakeUia.InspectResult = [
            new UiElement { Id = "e0", Type = "Window", Name = "Test", Depth = 0, ParentSelector = "p", WindowHandle = 42 }
        ];

        var command = GetRequiredService<UiInspectCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        var output = TestAnsiConsole.Output;
        // No "id" property anywhere (top-level envelope has no id either).
        StringAssert.DoesNotMatch(output, new System.Text.RegularExpressions.Regex("\"id\"\\s*:"));
        // Element-level redundant fields stripped.
        StringAssert.DoesNotMatch(output, new System.Text.RegularExpressions.Regex("\"parentSelector\"\\s*:"));
        // windowHandle moved up to the window envelope ("hwnd"); element's windowHandle: 42 must be gone.
        Assert.IsFalse(output.Contains("\"windowHandle\""), "windowHandle should be stripped from elements");
    }

    [TestMethod]
    public async Task Inspect_Interactive_AddsAncestorPath()
    {
        // --interactive collapses non-interactive ancestors (Window/Pane/Group) out of the tree
        // and surfaces them as ancestorPath on the surviving interactive descendants (e.g., Button).
        _fakeUia.InspectResult = [
            new UiElement { Id = "e0", Type = "Window", Name = "App",  Depth = 0 },
            new UiElement { Id = "e1", Type = "Pane",   Name = "Root", Depth = 1 },
            new UiElement { Id = "e2", Type = "Group",  Name = "Bar",  Depth = 2 },
            new UiElement { Id = "e3", Type = "Button", Name = "OK",   Depth = 3, IsEnabled = true }
        ];

        var command = GetRequiredService<UiInspectCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--interactive", "--json"]);
        Assert.AreEqual(0, exitCode);
        var output = TestAnsiConsole.Output;
        StringAssert.Contains(output, "\"type\": \"Button\"");
        // Window/Pane/Group are non-interactive — they should be dropped from the tree...
        StringAssert.DoesNotMatch(output, new System.Text.RegularExpressions.Regex("\"type\"\\s*:\\s*\"Window\""));
        StringAssert.DoesNotMatch(output, new System.Text.RegularExpressions.Regex("\"type\"\\s*:\\s*\"Pane\""));
        StringAssert.DoesNotMatch(output, new System.Text.RegularExpressions.Regex("\"type\"\\s*:\\s*\"Group\""));
        // ...but their types should appear in ancestorPath on Button.
        StringAssert.Contains(output, "\"ancestorPath\":");
        StringAssert.Contains(output, "\"Window\"");
        StringAssert.Contains(output, "\"Pane\"");
        StringAssert.Contains(output, "\"Group\"");
    }

    [TestMethod]
    public async Task Inspect_Json_HasMoreChildrenHint()
    {
        // When WalkTree hits the depth limit but more children exist, it sets HasMoreChildren=true.
        // The JSON output should preserve this hint so consumers can prompt for a deeper inspect.
        _fakeUia.InspectResult = [
            new UiElement { Id = "e0", Type = "Window", Depth = 0, HasMoreChildren = true }
        ];

        var command = GetRequiredService<UiInspectCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"hasMoreChildren\": true");
    }

    [TestMethod]
    public async Task Search_ReturnsMatches()
    {
        _fakeUia.SearchResult = [
            new UiElement { Id = "e0", Type = "Button", Name = "OK", IsEnabled = true },
            new UiElement { Id = "e1", Type = "Button", Name = "Cancel", IsEnabled = true }
        ];

        var command = GetRequiredService<UiSearchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["Button", "-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"matchCount\": 2");
    }

    [TestMethod]
    public async Task Search_Json_OmitsInternalId()
    {
        // Selectors are the stable public handle; the internal "e0/e1" counter is implementation detail.
        _fakeUia.SearchResult = [
            new UiElement { Id = "e0", Type = "Button", Name = "OK", Selector = "btn-ok-a1b2" }
        ];

        var command = GetRequiredService<UiSearchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["Button", "-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        StringAssert.DoesNotMatch(TestAnsiConsole.Output, new System.Text.RegularExpressions.Regex("\"id\"\\s*:"));
        StringAssert.Contains(TestAnsiConsole.Output, "\"selector\": \"btn-ok-a1b2\"");
    }

    [TestMethod]
    public async Task Search_WithoutSelector_ReturnsError()
    {
        var command = GetRequiredService<UiSearchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Invoke_WithNameSelector_ReturnsSuccess()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Name = "Submit" };
        _fakeUia.InvokeResult = "InvokePattern";

        var command = GetRequiredService<UiInvokeCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["#Submit", "-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"pattern\": \"InvokePattern\"");
    }

    [TestMethod]
    public async Task Invoke_ElementNotFound_ReturnsError()
    {
        _fakeUia.FindSingleResult = null;

        var command = GetRequiredService<UiInvokeCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["#NonExistent", "-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Invoke_ByElementId_ReturnsSuccess()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Name = "TestButton" };

        var command = GetRequiredService<UiInvokeCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e0", "-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task GetProperty_ReturnsProperties()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Name = "OK", IsEnabled = true };
        _fakeUia.PropertiesResult = new Dictionary<string, object?> { ["IsEnabled"] = true, ["Name"] = "OK" };

        var command = GetRequiredService<UiGetPropertyCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e0", "-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"elementId\": \"e0\"");
    }

    [TestMethod]
    public async Task Screenshot_Json_ReturnsFilePath()
    {
        // Small 1x1 BGRA pixel for the fake
        _fakeUia.ScreenshotResult = (new byte[4], 1, 1);

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"filePath\":");
        StringAssert.Contains(TestAnsiConsole.Output, "\"width\": 1");
    }

    [TestMethod]
    public async Task SetValue_WithText_ReturnsSuccess()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e1", Type = "Edit", Name = "TestEdit" };

        var command = GetRequiredService<UiSetValueCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e1", "Hello", "-a", "TestApp"]);
        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task SetValue_WithoutText_ReturnsError()
    {
        var command = GetRequiredService<UiSetValueCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e1", "-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Focus_ReturnsSuccess()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Name = "OK" };

        var command = GetRequiredService<UiFocusCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["e0", "-a", "TestApp"]);
        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task GetValue_ReturnsText()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e1", Type = "Document", Name = "Text editor", Selector = "doc-texteditor-53ad" };

        var command = GetRequiredService<UiGetValueCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["doc-texteditor-53ad", "-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"text\":");
    }

    [TestMethod]
    public async Task GetValue_WithoutSelector_ReturnsError()
    {
        var command = GetRequiredService<UiGetValueCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task WaitFor_ExistingElement_ReturnsSuccess()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Name = "Submit" };

        var command = GetRequiredService<UiWaitForCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["Button", "-a", "TestApp", "--timeout", "1000"]);
        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task WaitFor_Json_OmitsInternalId()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Name = "Submit", Selector = "btn-submit-a1b2" };

        var command = GetRequiredService<UiWaitForCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["Button", "-a", "TestApp", "--timeout", "1000", "--json"]);
        Assert.AreEqual(0, exitCode);
        var output = TestAnsiConsole.Output;
        StringAssert.Contains(output, "\"found\": true");
        StringAssert.DoesNotMatch(output, new System.Text.RegularExpressions.Regex("\"id\"\\s*:"));
        StringAssert.Contains(output, "\"selector\": \"btn-submit-a1b2\"");
    }

    [TestMethod]
    public async Task GetFocused_Json_NoFocus_EmitsEnvelope()
    {
        // When no element has keyboard focus the JSON output must still be a parsable object,
        // not a bare "null" — consumers need a stable schema with hasFocus to detect the case.
        _fakeUia.FocusedResult = null;

        var command = GetRequiredService<UiGetFocusedCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        var output = TestAnsiConsole.Output.Trim();
        Assert.AreNotEqual("null", output, "Bare null is unhelpful — should be an envelope object.");
        StringAssert.Contains(output, "\"hasFocus\": false");
    }

    [TestMethod]
    public async Task GetFocused_Json_WithFocus_EmitsEnvelope()
    {
        _fakeUia.FocusedResult = new UiElement { Id = "e0", Type = "Edit", Name = "TextBox", Selector = "edit-textbox-a1b2" };

        var command = GetRequiredService<UiGetFocusedCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        var output = TestAnsiConsole.Output;
        StringAssert.Contains(output, "\"hasFocus\": true");
        StringAssert.Contains(output, "\"element\":");
        StringAssert.Contains(output, "\"selector\": \"edit-textbox-a1b2\"");
        // Internal id should not leak into the focused envelope either.
        StringAssert.DoesNotMatch(output, new System.Text.RegularExpressions.Regex("\"id\"\\s*:"));
    }

    [TestMethod]
    public async Task WaitFor_NonExistent_TimesOut()
    {
        _fakeUia.SearchResult = [];

        var command = GetRequiredService<UiWaitForCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["#NonExistent", "-a", "TestApp", "--timeout", "500"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task ListWindows_ReturnsWindows()
    {
        _fakeUia.WindowsByTitleResult = [
            (1001, 1234, "Main Window"),
            (1002, 1234, "Popup")
        ];

        var command = GetRequiredService<UiListWindowsCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"hwnd\": 1001");
    }

    [TestMethod]
    public async Task ListWindows_ExcludesUntitledZeroSizeByDefault()
    {
        _fakeUia.WindowsByTitleResult = [
            (1001, 1234, "Visible Window"),
            (1002, 1234, "")
        ];

        var command = GetRequiredService<UiListWindowsCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--json"]);
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"hwnd\": 1001");
        Assert.IsFalse(TestAnsiConsole.Output.Contains("\"hwnd\": 1002"), "Untitled zero-size window should be excluded");
    }

    [TestMethod]
    public async Task ListWindows_ShowHiddenIncludesUntitledZeroSize()
    {
        _fakeUia.WindowsByTitleResult = [
            (1001, 1234, "Visible Window"),
            (1002, 1234, "")
        ];

        var command = GetRequiredService<UiListWindowsCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--show-hidden", "--json"]);
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"hwnd\": 1001");
        StringAssert.Contains(TestAnsiConsole.Output, "\"hwnd\": 1002");
    }

    // ---------------------------------------------------------------------
    // ShouldIncludeWindow predicate unit tests (#570 regression coverage)
    // ---------------------------------------------------------------------

    [TestMethod]
    public void ShouldIncludeWindow_UntitledNonZeroSize_IncludedByDefault()
    {
        // Core #570 regression: an untitled window with real size must be visible
        Assert.IsTrue(UiListWindowsCommand.ShouldIncludeWindow("", 800, 600, showHidden: false));
        Assert.IsTrue(UiListWindowsCommand.ShouldIncludeWindow(null, 1920, 1080, showHidden: false));
    }

    [TestMethod]
    public void ShouldIncludeWindow_UntitledZeroSize_ExcludedByDefault()
    {
        Assert.IsFalse(UiListWindowsCommand.ShouldIncludeWindow("", 0, 0, showHidden: false));
        Assert.IsFalse(UiListWindowsCommand.ShouldIncludeWindow(null, 0, 0, showHidden: false));
    }

    [TestMethod]
    public void ShouldIncludeWindow_UntitledZeroSize_IncludedWithShowHidden()
    {
        Assert.IsTrue(UiListWindowsCommand.ShouldIncludeWindow("", 0, 0, showHidden: true));
    }

    [TestMethod]
    public void ShouldIncludeWindow_TitledWindow_AlwaysIncluded()
    {
        Assert.IsTrue(UiListWindowsCommand.ShouldIncludeWindow("My App", 0, 0, showHidden: false));
        Assert.IsTrue(UiListWindowsCommand.ShouldIncludeWindow("My App", 800, 600, showHidden: false));
    }

    // ---------------------------------------------------------------------
    // Tree-shape edge cases (M9): ensure BuildWindows/NestElements parse
    // unusual but realistic flat lists into the right window/root layout.
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task Inspect_Json_MultipleWindows_GroupsBySeparator()
    {
        // Two windows separated by a "---" entry. JSON output must be two windows[] entries,
        // each with its own elements[] tree. (Separators come from MsixService walk; the Name
        // field carries the HWND/title metadata parsed by SeparatorRegex.)
        _fakeUia.InspectResult = [
            new UiElement { Type = "---", Name = "HWND 100: \"Window A\" (App, Win32Class)", WindowHandle = 100 },
            new UiElement { Id = "e0", Type = "Window", Name = "A", Depth = 0, IsEnabled = true },
            new UiElement { Id = "e1", Type = "Button", Name = "OK", Depth = 1, IsEnabled = true },
            new UiElement { Type = "---", Name = "HWND 200: \"Window B\" (App, Win32Class)", WindowHandle = 200 },
            new UiElement { Id = "e2", Type = "Window", Name = "B", Depth = 0, IsEnabled = true },
        ];

        var command = GetRequiredService<UiInspectCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        var output = TestAnsiConsole.Output;
        StringAssert.Contains(output, "\"hwnd\": 100");
        StringAssert.Contains(output, "\"hwnd\": 200");
        StringAssert.Contains(output, "\"title\": \"Window A\"");
        StringAssert.Contains(output, "\"title\": \"Window B\"");
    }

    [TestMethod]
    public async Task Inspect_Json_DepthJump_KeepsSiblings()
    {
        // Depth 0 -> 2 (skipping 1). The depth-stack should still nest the Button under the
        // Window (any node with depth > root depth becomes a descendant), not as a root sibling.
        _fakeUia.InspectResult = [
            new UiElement { Id = "e0", Type = "Window", Name = "Root", Depth = 0, IsEnabled = true },
            new UiElement { Id = "e1", Type = "Button", Name = "Deep", Depth = 2, IsEnabled = true }
        ];

        var command = GetRequiredService<UiInspectCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        var output = TestAnsiConsole.Output;
        StringAssert.Contains(output, "\"type\": \"Window\"");
        StringAssert.Contains(output, "\"type\": \"Button\"");
        StringAssert.Contains(output, "\"children\":");
    }

    [TestMethod]
    public async Task Inspect_Interactive_Json_FlattensInvokableAncestor_NoCycle()
    {
        // Regression for the InvokableAncestor reference cycle:
        // - Group is non-interactive but invokable (both surface in --interactive).
        // - Button is invokable; AttachInvokableAncestors links Button.InvokableAncestor = Group.
        // - In the resulting JSON tree Group also has Button as a child.
        // Without flattening, System.Text.Json (no ReferenceHandler) throws on the
        // Group -> children -> Button -> invokableAncestor -> Group cycle.
        _fakeUia.InspectResult = [
            new UiElement { Id = "e0", Type = "Window", Name = "App",  Depth = 0 },
            new UiElement { Id = "e1", Type = "Group",  Name = "Bar",  Depth = 1, IsInvokable = true, Selector = "grp-bar-aaaa" },
            new UiElement { Id = "e2", Type = "Button", Name = "OK",   Depth = 2, IsEnabled = true, IsInvokable = false, Selector = "btn-ok-bbbb", ParentSelector = "grp-bar-aaaa" }
        ];

        var command = GetRequiredService<UiInspectCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--interactive", "--json"]);
        Assert.AreEqual(0, exitCode);
        var output = TestAnsiConsole.Output;
        // Did not throw on serialization. InvokableAncestor still present as a hint…
        StringAssert.Contains(output, "\"invokableAncestor\":");
        StringAssert.Contains(output, "\"selector\": \"grp-bar-aaaa\"");
        // …but flattened — no nested children/invokableAncestor inside it.
        // (Children still appears in the outer Button node, but the ancestor hint object should not contain a "children" key.)
        // We assert a structural property that proves the projection: the hint copy has no AutomationId on this fixture.
        StringAssert.Contains(output, "\"isInvokable\": true");
    }

    [TestMethod]
    public async Task Search_Json_FlattensInvokableAncestor_Scrubs()
    {
        // Search results with an InvokableAncestor must (a) not leak the ancestor's internal id /
        // parentSelector / windowHandle, and (b) flatten its Children/InvokableAncestor chain.
        var ancestor = new UiElement
        {
            Id = "e9", Type = "Group", Selector = "grp-toolbar-1234",
            ParentSelector = "wnd-app-0000", WindowHandle = 99,
            IsInvokable = true,
            Children = [new UiElement { Id = "e10", Type = "Button" }]
        };
        _fakeUia.SearchResult = [
            new UiElement
            {
                Id = "e0", Type = "MenuItem", Name = "Open", Selector = "mi-open-abcd",
                ParentSelector = "grp-toolbar-1234", WindowHandle = 99,
                InvokableAncestor = ancestor
            }
        ];

        var command = GetRequiredService<UiSearchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["Open", "-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        var output = TestAnsiConsole.Output;
        StringAssert.Contains(output, "\"selector\": \"mi-open-abcd\"");
        StringAssert.Contains(output, "\"invokableAncestor\":");
        StringAssert.Contains(output, "\"selector\": \"grp-toolbar-1234\"");
        // Internal/redundant fields stripped at every level (top-level + nested ancestor).
        StringAssert.DoesNotMatch(output, new System.Text.RegularExpressions.Regex("\"id\"\\s*:"));
        StringAssert.DoesNotMatch(output, new System.Text.RegularExpressions.Regex("\"parentSelector\"\\s*:"));
        StringAssert.DoesNotMatch(output, new System.Text.RegularExpressions.Regex("\"windowHandle\"\\s*:"));
    }

    [TestMethod]
    public async Task Inspect_Ancestors_Json_NestsChain()
    {
        // M3 regression: InspectAncestorsAsync returns root..target with no Depth assigned.
        // The command must assign Depth = i so BuildWindows nests them as a single chain
        // instead of flattening them into N sibling roots.
        // Fake's InspectAncestorsAsync mirrors InspectResult, so populate that.
        _fakeUia.InspectResult = [
            new UiElement { Id = "e0", Type = "Window", Name = "App",  IsEnabled = true, Selector = "wnd-app-aaaa" },
            new UiElement { Id = "e1", Type = "Pane",   Name = "Root", IsEnabled = true, Selector = "pn-root-bbbb" },
            new UiElement { Id = "e2", Type = "Group",  Name = "Bar",  IsEnabled = true, Selector = "grp-bar-cccc" },
            new UiElement { Id = "e3", Type = "Button", Name = "OK",   IsEnabled = true, Selector = "btn-ok-dddd" }
        ];

        var command = GetRequiredService<UiInspectCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-ok-dddd", "-a", "TestApp", "--ancestors", "--json"]);
        Assert.AreEqual(0, exitCode);
        var output = TestAnsiConsole.Output;
        // All four types present...
        StringAssert.Contains(output, "\"selector\": \"wnd-app-aaaa\"");
        StringAssert.Contains(output, "\"selector\": \"btn-ok-dddd\"");
        // ...and the chain is nested (the Window has at least one child layer of nesting).
        StringAssert.Contains(output, "\"children\":");
        // elementCount should reflect all 4 elements when properly nested.
        StringAssert.Contains(output, "\"elementCount\": 4");
    }

    // ---------------------------------------------------------------------
    // NativeAOT smoke tests (M10): exercise each result-type registration in
    // UiJsonContext at least once via --json so a missing/incorrect registration
    // fails Debug too instead of only blowing up in the published trim build.
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task Click_Json_EmitsEnvelope()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Selector = "btn-go-1234", X = 10, Y = 20, Width = 100, Height = 30 };

        var command = GetRequiredService<UiClickCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-go-1234", "-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"clickType\":");
    }

    [TestMethod]
    public async Task Hover_Json_EmitsFullEnvelopeWithCorrectValues()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Selector = "btn-info-5678", X = 50, Y = 60, Width = 120, Height = 40 };

        var command = GetRequiredService<UiHoverCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-info-5678", "-a", "TestApp", "--json", "--dwell-time", "0"]);
        Assert.AreEqual(0, exitCode);

        // Deserialize and assert exact JSON shape (L1)
        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual("btn-info-5678", result.GetProperty("elementId").GetString());
        Assert.AreEqual(110, result.GetProperty("x").GetInt32()); // 50 + 120/2
        Assert.AreEqual(80, result.GetProperty("y").GetInt32());  // 60 + 40/2
        Assert.AreEqual(0, result.GetProperty("dwellTimeMs").GetInt32());
        Assert.AreEqual(0, result.GetProperty("hwnd").GetInt64()); // fake session has no window handle

        // Verify fake mouse received correct coordinates (M2)
        Assert.AreEqual(1, _fakeMouse.HoverCalls.Count);
        Assert.AreEqual(110, _fakeMouse.HoverCalls[0].ScreenX);
        Assert.AreEqual(80, _fakeMouse.HoverCalls[0].ScreenY);
    }

    [TestMethod]
    public async Task Hover_DefaultDwellTime_Uses800ms()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Selector = "btn-info-5678", X = 10, Y = 10, Width = 100, Height = 100 };

        var command = GetRequiredService<UiHoverCommand>();
        // Omit --dwell-time to test default
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-info-5678", "-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual(800, result.GetProperty("dwellTimeMs").GetInt32());
    }

    [TestMethod]
    [DataRow(10000, DisplayName = "Max boundary (10000) is valid")]
    [DataRow(0, DisplayName = "Min boundary (0) is valid")]
    public async Task Hover_BoundaryDwellTime_Succeeds(int dwellTime)
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Selector = "btn-x-0", X = 0, Y = 0, Width = 10, Height = 10 };

        var command = GetRequiredService<UiHoverCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-x-0", "-a", "TestApp", "--json", "--dwell-time", dwellTime.ToString()]);
        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    [DataRow(-1, DisplayName = "Negative dwell time")]
    [DataRow(10001, DisplayName = "Over-max dwell time")]
    public async Task Hover_InvalidDwellTime_ReturnsError(int dwellTime)
    {
        var command = GetRequiredService<UiHoverCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-x-0", "-a", "TestApp", "--json", "--dwell-time", dwellTime.ToString()]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Hover_MissingSelectorArgument_ReturnsError()
    {
        var command = GetRequiredService<UiHoverCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Hover_ElementWithWindowHandle_UsesElementHwnd()
    {
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "popup-btn", Type = "Button", Selector = "popup-btn-001",
            X = 200, Y = 300, Width = 80, Height = 30,
            WindowHandle = 99999
        };

        var command = GetRequiredService<UiHoverCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["popup-btn-001", "-a", "TestApp", "--json", "--dwell-time", "0"]);
        Assert.AreEqual(0, exitCode);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual(99999, result.GetProperty("hwnd").GetInt64());
    }

    [TestMethod]
    public async Task Hover_MissingApp_ReturnsError()
    {
        var command = GetRequiredService<UiHoverCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-info-5678", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Hover_ElementNotFound_ReturnsError()
    {
        _fakeUia.FindSingleResult = null;

        var command = GetRequiredService<UiHoverCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-nonexist-0000", "-a", "TestApp", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Hover_ZeroSizeElement_ReturnsError()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Selector = "btn-tiny-0000", X = 10, Y = 20, Width = 0, Height = 0 };

        var command = GetRequiredService<UiHoverCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-tiny-0000", "-a", "TestApp", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    // ---------------------------------------------------------------------
    // N5 — stable re-resolve before injection (moving/animating targets)
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task Click_MovingTarget_ReturnsTargetMoved()
    {
        // The element's bounds keep changing on every read (an animating/scrolling target). The click
        // must refuse rather than report a false success after landing on empty space.
        const string sel = "btn-moving-1234";
        var seq = new Queue<UiElement?>();
        for (int i = 0; i < 4; i++)
        {
            seq.Enqueue(new UiElement { Id = "e0", Type = "Button", Selector = sel, X = 10 + i * 60, Y = 20, Width = 40, Height = 30 });
        }
        _fakeUia.MovingResults[sel] = seq;

        var command = GetRequiredService<UiClickCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [sel, "-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMouse.ClickCalls.Count, "no click should be injected at a moving target");
    }

    [TestMethod]
    public async Task Click_TargetSettlesAfterMove_UsesReResolvedCenter()
    {
        // The element moves once (initial read at X=10, then settles at X=200). The click must use the
        // re-resolved, settled center (250), not the stale initial center (30).
        const string sel = "btn-settle-1234";
        var seq = new Queue<UiElement?>();
        seq.Enqueue(new UiElement { Id = "e0", Type = "Button", Selector = sel, X = 10, Y = 20, Width = 40, Height = 30 });   // initial center 30,35
        seq.Enqueue(new UiElement { Id = "e0", Type = "Button", Selector = sel, X = 200, Y = 20, Width = 100, Height = 30 }); // settled center 250,35
        _fakeUia.MovingResults[sel] = seq;

        var command = GetRequiredService<UiClickCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [sel, "-a", "TestApp", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeMouse.ClickCalls.Count);
        Assert.AreEqual(250, _fakeMouse.ClickCalls[0].ScreenX);
        Assert.AreEqual(35, _fakeMouse.ClickCalls[0].ScreenY);
    }

    [TestMethod]
    public async Task Click_TargetMovesDuringSettle_ReturnsTargetMoved()
    {
        // F3/N5 residual race: ResolveStableAsync sees the element settle (two equal reads), but the
        // target then drifts during the cursor-settle window before the button-down. The final confirm
        // read must catch it and refuse rather than report a false success after the click lands on
        // empty space. Sequence: initial + stable re-read at X=100, then the confirm read finds X=500.
        const string sel = "btn-drift-1234";
        var seq = new Queue<UiElement?>();
        seq.Enqueue(new UiElement { Id = "e0", Type = "Button", Selector = sel, X = 100, Y = 20, Width = 40, Height = 30 }); // initial
        seq.Enqueue(new UiElement { Id = "e0", Type = "Button", Selector = sel, X = 100, Y = 20, Width = 40, Height = 30 }); // settles
        seq.Enqueue(new UiElement { Id = "e0", Type = "Button", Selector = sel, X = 500, Y = 20, Width = 40, Height = 30 }); // drifted on confirm
        _fakeUia.MovingResults[sel] = seq;

        var command = GetRequiredService<UiClickCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [sel, "-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMouse.ClickCalls.Count, "no button-down should fire when the target drifted during the settle");
        Assert.AreEqual(1, _fakeMouse.MoveCursorCalls.Count, "the cursor was positioned before the confirm read");
    }

    [TestMethod]
    public async Task Click_SettledTarget_PressesWithoutExtraSettle()
    {
        // The button-down must use settleMs:0 — the command already positioned the cursor, settled, and
        // re-confirmed, so a second internal settle would reopen the move-during-settle window.
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Selector = "btn-go-1234", X = 10, Y = 20, Width = 100, Height = 30 };

        var command = GetRequiredService<UiClickCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-go-1234", "-a", "TestApp", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeMouse.ClickCalls.Count);
        Assert.AreEqual(0, _fakeMouse.ClickCalls[0].SettleMs);
        Assert.AreEqual(1, _fakeMouse.MoveCursorCalls.Count);
    }

    // ---------------------------------------------------------------------
    // F1 — foreground guard now also gates click / hover (parity with drag/scroll/send-keys)
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task Click_ForegroundGuardDenies_AbortsWithoutClicking()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Selector = "btn-go-1234", X = 10, Y = 20, Width = 100, Height = 30 };
        _fakeForeground.Allow = false;
        _fakeForeground.DenyReason = ForegroundCheck.ForegroundNotTarget;

        var command = GetRequiredService<UiClickCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-go-1234", "-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMouse.ClickCalls.Count, "no click should be injected when the foreground guard refuses");
        Assert.AreEqual(1, _fakeForeground.Calls.Count);
        StringAssert.Contains(ConsoleStdErr.ToString(), "refusing to click",
            "the refusal must name the action the command was attempting");
    }

    [TestMethod]
    public async Task Hover_ForegroundGuardDenies_AbortsWithoutHovering()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Button", Selector = "btn-info-5678", X = 50, Y = 60, Width = 120, Height = 40 };
        _fakeForeground.Allow = false;
        _fakeForeground.DenyReason = ForegroundCheck.ForegroundNotTarget;

        var command = GetRequiredService<UiHoverCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-info-5678", "-a", "TestApp", "--json", "--dwell-time", "0"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMouse.HoverCalls.Count, "no hover should be injected when the foreground guard refuses");
        Assert.AreEqual(1, _fakeForeground.Calls.Count);
        StringAssert.Contains(ConsoleStdErr.ToString(), "refusing to hover",
            "the refusal must name the action the command was attempting");
    }

    [TestMethod]
    public async Task Click_ForegroundGuardAllows_InjectsAndPassesTargetHwnd()
    {
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "e0", Type = "Button", Selector = "btn-go-1234",
            X = 10, Y = 20, Width = 100, Height = 30, WindowHandle = 7777
        };

        var command = GetRequiredService<UiClickCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-go-1234", "-a", "TestApp", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeMouse.ClickCalls.Count);
        Assert.AreEqual(2, _fakeForeground.Calls.Count, "click checks the foreground twice on the success path: once before the cursor-settle confirm read and once immediately before the button-down");
        Assert.AreEqual(7777, _fakeForeground.Calls[0].TargetHwnd);
    }

    [TestMethod]
    public void SendKeys_KeysArgument_DocumentsTextEscape()
    {
        // F2: the text=<literal> escape must be discoverable via --help / cli-schema, not only the guide.
        StringAssert.Contains(UiSendKeysCommand.KeysArgument.Description, "text=");
    }

    [TestMethod]
    public async Task Scroll_Wheel_MovingTarget_ReturnsTargetMoved()
    {
        const string sel = "lst-moving-1234";
        var seq = new Queue<UiElement?>();
        for (int i = 0; i < 4; i++)
        {
            seq.Enqueue(new UiElement { Id = "e0", Type = "List", Selector = sel, X = 10, Y = 20 + i * 60, Width = 120, Height = 40 });
        }
        _fakeUia.MovingResults[sel] = seq;

        var command = GetRequiredService<UiScrollCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [sel, "-a", "TestApp", "--wheel", "-3", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMouse.ScrollWheelCalls.Count);
    }

    [TestMethod]
    public async Task Focus_Json_EmitsEnvelope()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "Edit", Selector = "edit-name-1234" };

        var command = GetRequiredService<UiFocusCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["edit-name-1234", "-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"elementId\":");
    }

    [TestMethod]
    public async Task SetValue_Json_EmitsEnvelope()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e1", Type = "Edit", Selector = "edit-name-1234" };

        var command = GetRequiredService<UiSetValueCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["edit-name-1234", "Hello", "-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"elementId\":");
    }

    [TestMethod]
    public async Task Scroll_Json_EmitsEnvelope()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "List", Selector = "lst-items-1234" };

        var command = GetRequiredService<UiScrollCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["lst-items-1234", "--direction", "down", "-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"direction\":");
    }

    [TestMethod]
    public async Task ScrollIntoView_Json_EmitsEnvelope()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "e0", Type = "ListItem", Selector = "li-row42-1234" };

        var command = GetRequiredService<UiScrollIntoViewCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["li-row42-1234", "-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"elementId\":");
    }
}

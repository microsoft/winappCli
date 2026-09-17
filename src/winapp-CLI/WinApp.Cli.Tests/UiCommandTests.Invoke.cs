// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Invoke_Json_ReportsInvokedElementsSourceWindow(bool explicitAction)
    {
        _fakeTargetResolver.TargetResult.WindowHandle = 1234;
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "secondary", Selector = "secondary", WindowHandle = 5678
        };
        _fakeUia.ExplicitInvokeResult = new UiInvokeActionResult("InvokePattern", "invoke");
        var args = new List<string> { "secondary", "-a", "TestApp", "--json" };
        if (explicitAction) { args.AddRange(["--action", "invoke"]); }

        var exitCode = await ParseAndInvokeWithCaptureAsync(GetRequiredService<UiInvokeCommand>(), args.ToArray());

        Assert.AreEqual(0, exitCode);
        using var json = JsonDocument.Parse(TestAnsiConsole.Output);
        Assert.AreEqual(5678, json.RootElement.GetProperty("hwnd").GetInt64());
    }

    [TestMethod]
    [DataRow("invoke", UiInvokeAction.Invoke, "InvokePattern", "invoke")]
    [DataRow("select", UiInvokeAction.Select, "SelectionItemPattern", "select")]
    [DataRow("toggle", UiInvokeAction.Toggle, "TogglePattern", "toggle")]
    [DataRow("toggle-on", UiInvokeAction.ToggleOn, "TogglePattern", "toggle")]
    [DataRow("toggle-off", UiInvokeAction.ToggleOff, "TogglePattern", "toggle")]
    [DataRow("expand", UiInvokeAction.Expand, "ExpandCollapsePattern", "expand")]
    [DataRow("collapse", UiInvokeAction.Collapse, "ExpandCollapsePattern", "collapse")]
    public async Task Invoke_ExplicitAction_RoutesExactlyAndReportsJson(
        string action, UiInvokeAction expectedAction, string pattern, string performed)
    {
        var element = new UiElement
        {
            Id = "internal-id", Selector = "item", Type = "ListItem", Name = "Settings",
            InvokableAncestor = new UiElement { Id = "parent", Selector = "parent" }
        };
        _fakeUia.FindSingleResult = element;
        _fakeUia.ExplicitInvokeResult = new UiInvokeActionResult(pattern, performed);

        var exitCode = await ParseAndInvokeWithCaptureAsync(GetRequiredService<UiInvokeCommand>(),
            ["item", "-a", "TestApp", "--action", action, "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(expectedAction, _fakeUia.LastInvokeAction);
        Assert.AreSame(element, _fakeUia.LastInvokedElement);
        using var document = JsonDocument.Parse(TestAnsiConsole.Output);
        var result = document.RootElement;
        Assert.AreEqual("item", result.GetProperty("elementId").GetString());
        Assert.AreEqual(action, result.GetProperty("requestedAction").GetString());
        Assert.AreEqual(performed, result.GetProperty("performedAction").GetString());
        Assert.AreEqual(pattern, result.GetProperty("pattern").GetString());
        Assert.AreEqual(_fakeTargetResolver.TargetResult.WindowHandle, result.GetProperty("hwnd").GetInt64());
        Assert.IsFalse(result.TryGetProperty("element", out _), "Do not expose the internal element graph.");
        Assert.DoesNotContain("internal-id", TestAnsiConsole.Output);
        Assert.DoesNotContain("parent", TestAnsiConsole.Output);
    }

    [TestMethod]
    [DataRow("toggle-on")]
    [DataRow("toggle-off")]
    public async Task Invoke_ExplicitAlreadyCorrect_ReportsNoAction(string action)
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "check", Selector = "check" };
        _fakeUia.ExplicitInvokeResult = new UiInvokeActionResult("TogglePattern", "none");

        var exitCode = await ParseAndInvokeWithCaptureAsync(GetRequiredService<UiInvokeCommand>(),
            ["check", "-a", "TestApp", "--action", action, "--json"]);

        Assert.AreEqual(0, exitCode);
        using var document = JsonDocument.Parse(TestAnsiConsole.Output);
        Assert.AreEqual(action, document.RootElement.GetProperty("requestedAction").GetString());
        Assert.AreEqual("none", document.RootElement.GetProperty("performedAction").GetString());
    }

    [TestMethod]
    [DataRow("invoke")]
    [DataRow("select")]
    [DataRow("toggle")]
    [DataRow("toggle-on")]
    [DataRow("toggle-off")]
    [DataRow("expand")]
    [DataRow("collapse")]
    public async Task Invoke_ExplicitFailure_NeverRetriesAncestor(string action)
    {
        var element = new UiElement
        {
            Id = "label", Selector = "label", Type = "Text",
            InvokableAncestor = new UiElement { Id = "button", Selector = "button" }
        };
        _fakeUia.FindSingleResult = element;
        _fakeUia.ExplicitInvokeThrow = new InvalidOperationException("Selected element does not support the requested action.");

        var exitCode = await ParseAndInvokeWithCaptureAsync(GetRequiredService<UiInvokeCommand>(),
            ["label", "-a", "TestApp", "--action", action, "--json"]);

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCode(UiJsonError.CodeInternalError);
        Assert.AreSame(element, _fakeUia.LastInvokedElement);
        Assert.AreEqual(1, _fakeUia.ExplicitInvokeCalls);
        Assert.AreEqual(0, _fakeUia.AutomaticInvokeCalls);
        Assert.IsTrue(string.IsNullOrWhiteSpace(TestAnsiConsole.Output));
    }

    [TestMethod]
    public void Invoke_InvalidAction_IsRejectedByParserBeforeTargetRouting()
    {
        var parsed = GetRequiredService<WinAppRootCommand>().Parse(
            ["ui", "invoke", "Save", "-a", "TestApp", "--on", "sandbox", "--action", "selekt", "--json"]);

        Assert.IsNotEmpty(parsed.Errors, "Remote UI routing runs before handler preflight; invalid actions must fail parsing.");
        StringAssert.Contains(string.Join(" ", parsed.Errors.Select(error => error.Message)), "--action");
    }

    [TestMethod]
    [DataRow("click")]
    [DataRow("auto")]
    [DataRow("ToggleOn")]
    [DataRow("SELECT")]
    [DataRow("1")]
    [DataRow("")]
    public async Task Invoke_InvalidAction_RejectsBeforeDesktopAcquisition(string action)
    {
        string[] args = ["ui", "invoke", "item", "-a", "TestApp", "--action", action, "--json"];
        Assert.IsNotEmpty(GetRequiredService<WinAppRootCommand>().Parse(args).Errors);
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(args);

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCodeIn(stderr, UiJsonError.CodeInvalidArguments);
        Assert.IsTrue(string.IsNullOrWhiteSpace(stdout));
    }

    [TestMethod]
    public async Task Invoke_InvalidRemoteAction_ReportsJsonBeforePreparingTarget()
    {
        string[] args = ["ui", "invoke", "Save", "-a", "TestApp", "--on", "sandbox", "--action", "", "--json"];
        Assert.IsNotEmpty(GetRequiredService<WinAppRootCommand>().Parse(args).Errors,
            "Do not run a remote command unless parsing rejects it before target preparation.");
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(args);

        Assert.AreEqual(1, exitCode);
        Assert.IsTrue(string.IsNullOrWhiteSpace(stdout));
        AssertJsonErrorCodeIn(stderr, UiJsonError.CodeInvalidArguments);
    }

    [TestMethod]
    public void Invoke_MissingActionValue_IsParseError()
    {
        var result = GetRequiredService<UiInvokeCommand>().Parse(["item", "-a", "TestApp", "--action"]);
        Assert.IsNotEmpty(result.Errors);
    }

    [TestMethod]
    public void Invoke_RepeatedAction_IsParseError()
    {
        var result = GetRequiredService<UiInvokeCommand>().Parse(
            ["item", "-a", "TestApp", "--action", "invoke", "--action", "select"]);
        Assert.IsNotEmpty(result.Errors);
    }

    [TestMethod]
    public async Task Invoke_ExplicitStaleElement_ReportsErrorWithoutFallback()
    {
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "item", InvokableAncestor = new UiElement { Id = "parent" }
        };
        _fakeUia.ExplicitInvokeThrow = FakeComException;

        var exitCode = await ParseAndInvokeWithCaptureAsync(GetRequiredService<UiInvokeCommand>(),
            ["item", "-a", "TestApp", "--action", "select", "--json"]);

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCode(UiJsonError.CodeStaleElement);
        Assert.AreEqual(1, _fakeUia.ExplicitInvokeCalls);
        Assert.AreEqual(0, _fakeUia.AutomaticInvokeCalls);
    }

    [TestMethod]
    [DataRow("InvokePattern", "invoke")]
    [DataRow("TogglePattern", "toggle")]
    [DataRow("SelectionItemPattern", "select")]
    [DataRow("ExpandCollapsePattern", "expand")]
    public async Task Invoke_OmittedAction_PreservesAutomaticPatternAndAncestor(string pattern, string performed)
    {
        var ancestor = new UiElement { Id = "parent-id", Selector = "parent", Type = "Button" };
        _fakeUia.FindSingleResult = new UiElement { Id = "label", InvokableAncestor = ancestor };
        _fakeUia.InvokeThrowsForAncestorFallback = true;
        _fakeUia.InvokeResult = pattern;

        var exitCode = await ParseAndInvokeWithCaptureAsync(GetRequiredService<UiInvokeCommand>(),
            ["label", "-a", "TestApp", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.IsNull(_fakeUia.LastInvokeAction);
        Assert.IsFalse(_fakeUia.FindSingleRequireUniqueCalls.Single());
        Assert.AreSame(ancestor, _fakeUia.LastInvokedElement);
        using var document = JsonDocument.Parse(TestAnsiConsole.Output);
        Assert.AreEqual("auto", document.RootElement.GetProperty("requestedAction").GetString());
        Assert.AreEqual(performed, document.RootElement.GetProperty("performedAction").GetString());
        Assert.AreEqual(pattern, document.RootElement.GetProperty("pattern").GetString());
        Assert.AreEqual("parent", document.RootElement.GetProperty("elementId").GetString());
    }

    [TestMethod]
    public async Task Invoke_ExplicitAction_PreservesSelectedIdentityAcrossDesktopWait()
    {
        var selected = new UiElement { Id = "primary", Selector = "primary", Name = "Save", WindowHandle = 4242 };
        var replacement = new UiElement { Id = "secondary", Selector = "secondary", Name = "Save", WindowHandle = 4242 };
        _fakeUia.MovingResults["Save"] = new Queue<UiElement?>([selected, replacement]);
        _fakeUia.ExplicitInvokeResult = new UiInvokeActionResult("SelectionItemPattern", "select");

        var exitCode = await ParseAndInvokeWithCaptureAsync(GetRequiredService<UiInvokeCommand>(),
            ["Save", "-a", "TestApp", "--action", "select", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreSame(selected, _fakeUia.LastInvokedElement,
            "The strict service resolver must receive the selected identity, not a new text-query match.");
        Assert.HasCount(1, _fakeUia.MovingResults["Save"], "Do not rerun the text query after waiting.");
        Assert.AreEqual(1, _fakeDesktopLock.DesktopSectionEnters);
    }

    [TestMethod]
    public async Task Invoke_ExplicitAction_RemovedWhileQueued_DoesNotReselectSibling()
    {
        var selected = new UiElement { Id = "primary", Selector = "primary", Name = "Save", WindowHandle = 4242 };
        var replacement = new UiElement { Id = "secondary", Selector = "secondary", Name = "Save", WindowHandle = 4242 };
        _fakeUia.MovingResults["Save"] = new Queue<UiElement?>([selected, replacement]);
        _fakeUia.ExplicitInvokeThrow = new InvalidOperationException("Element primary is stale.");

        var exitCode = await ParseAndInvokeWithCaptureAsync(GetRequiredService<UiInvokeCommand>(),
            ["Save", "-a", "TestApp", "--action", "invoke", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreSame(selected, _fakeUia.LastInvokedElement);
        Assert.HasCount(1, _fakeUia.MovingResults["Save"]);
        Assert.AreEqual(1, _fakeUia.ExplicitInvokeCalls);
        Assert.AreEqual(0, _fakeUia.AutomaticInvokeCalls);
        AssertJsonErrorCode(UiJsonError.CodeInternalError);
    }

    [TestMethod]
    public async Task Invoke_ExplicitAction_RefusesRecycledWindow()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "item", Selector = "item", WindowHandle = 4242 };
        _fakeSystemQuery.ProcessIdForWindowResult = 9999;

        var exitCode = await ParseAndInvokeWithCaptureAsync(GetRequiredService<UiInvokeCommand>(),
            ["item", "-a", "TestApp", "--action", "select", "--json"]);

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCode(UiJsonError.CodeStaleElement);
        Assert.IsNull(_fakeUia.LastInvokeAction);
    }

    [TestMethod]
    public async Task Invoke_ExplicitAction_TextReportsRequestedAndPerformed()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "check", Selector = "check" };
        _fakeUia.ExplicitInvokeResult = new UiInvokeActionResult("TogglePattern", "none");

        var (exitCode, output) = await InvokeWithAmbientConsoleCaptureAsync(GetRequiredService<UiInvokeCommand>(),
            ["check", "-a", "TestApp", "--action", "toggle-on"]);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(output, "toggle-on");
        StringAssert.Contains(output, "none");
    }

    [TestMethod]
    public async Task Invoke_ExplicitAction_AmbiguousTextSelector_FailsClosedWithoutInvoking()
    {
        _fakeUia.FindUniqueThrow = new UiAmbiguousSelectorException("Selector matched 2 elements. Use a slug from 'inspect'.");
        _fakeUia.SearchThrow = new AssertFailedException("Search results cannot establish uniqueness.");

        var exitCode = await ParseAndInvokeWithCaptureAsync(GetRequiredService<UiInvokeCommand>(),
            ["save", "-a", "TestApp", "--action", "invoke", "--json"]);

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCode(UiJsonError.CodeAmbiguousSelector);
        Assert.AreEqual(0, _fakeUia.ExplicitInvokeCalls, "An ambiguous --action selector must not invoke anything.");
        Assert.IsTrue(_fakeUia.FindSingleRequireUniqueCalls.Single());
        Assert.AreEqual(0, _fakeDesktopLock.DesktopSectionEnters);
    }

    [TestMethod]
    public async Task Invoke_ExplicitAction_UniqueTextSelector_Invokes()
    {
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "internal", Selector = "elm-save-9a9a", AutomationId = "save", Name = "Save", Type = "Button"
        };
        _fakeUia.SearchThrow = new AssertFailedException("Strict selection must not be checked with Search.");
        _fakeUia.ExplicitInvokeResult = new UiInvokeActionResult("InvokePattern", "invoke");

        var exitCode = await ParseAndInvokeWithCaptureAsync(GetRequiredService<UiInvokeCommand>(),
            ["Save", "-a", "TestApp", "--action", "invoke", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeUia.ExplicitInvokeCalls, "A unique --action selector must still invoke.");
        Assert.AreEqual("elm-save-9a9a", _fakeUia.LastInvokedElement!.Selector,
            "A unique name match must retain its identity, not become a possibly duplicated AutomationId.");
        Assert.AreSame(_fakeUia.FindSingleResult, _fakeUia.LastInvokedElement);
        Assert.IsTrue(_fakeUia.FindSingleRequireUniqueCalls.Single());
    }

    [TestMethod]
    public async Task Invoke_ExplicitAction_SlugSelector_SkipsAmbiguityCheck()
    {
        // A slug names exactly one element by RuntimeId, so the ambiguity guard does not apply even when
        // a text query for the same name would be ambiguous.
        _fakeUia.FindSingleResult = new UiElement { Id = "internal", Selector = "elm-save-9a9a", AutomationId = "save" };
        _fakeUia.SearchThrow = new AssertFailedException("Slugs must not use bulk search.");
        _fakeUia.ExplicitInvokeResult = new UiInvokeActionResult("InvokePattern", "invoke");

        var exitCode = await ParseAndInvokeWithCaptureAsync(GetRequiredService<UiInvokeCommand>(),
            ["elm-save-9a9a", "-a", "TestApp", "--action", "invoke", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeUia.ExplicitInvokeCalls, "A slug selector must invoke without an ambiguity check.");
        Assert.AreEqual("elm-save-9a9a", _fakeUia.LastInvokedElement!.Selector);
    }
}

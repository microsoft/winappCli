// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    [TestMethod]
    [DataRow("search", "--root")]
    [DataRow("get-property", "--root")]
    [DataRow("get-value", "--root")]
    [DataRow("wait-for", "--root")]
    [DataRow("search", "--type")]
    [DataRow("get-property", "--type")]
    [DataRow("get-value", "--type")]
    [DataRow("wait-for", "--type")]
    [DataRow("search", "--class-name")]
    [DataRow("get-property", "--class-name")]
    [DataRow("get-value", "--class-name")]
    [DataRow("wait-for", "--class-name")]
    public async Task QueryOptions_ExplicitEmptyStrings_KeepNativeSemantics(string command, string option)
    {
        _fakeUia.FindSingleResult = new UiElement { Type = "Edit", Selector = "txt-value-a123" };
        _fakeUia.SearchResult = [_fakeUia.FindSingleResult];
        var exit = await ParseAndInvokeWithCaptureAsync(QueryCommand(command),
            ["Welcome", "-a", "TestApp", option, "", "--json"]);
        if (option == "--class-name")
        {
            Assert.AreEqual(0, exit);
            Assert.AreEqual("", _fakeUia.Queries.Single().ClassName);
        }
        else
        {
            Assert.AreEqual(1, exit);
            AssertJsonErrorCode("invalid_arguments");
            Assert.IsEmpty(_fakeUia.Queries);
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task QueryOptions_Wait_ReadReplacementRetriesFullQuery(bool property, bool comFailure)
    {
        _fakeUia.ReadFailures.Enqueue(comFailure
            ? new System.Runtime.InteropServices.COMException("Replaced.", unchecked((int)0x80040201))
            : new UiElementNotFoundException("txt-old-a123"));
        _fakeUia.MovingResults["Welcome"] = new Queue<UiElement?>(
            [new UiElement { Type = "Edit", Selector = "txt-old-a123" },
             new UiElement { Type = "Edit", Selector = "txt-new-b234" }]);
        _fakeUia.GetTextResult = "ready";
        _fakeUia.PropertiesResult = new() { ["Value"] = "ready" };
        var args = new List<string>
        {
            "Welcome", "-a", "TestApp", "--root", "MailRow", "--type", "Edit",
            "--value", "ready", "--timeout", "2000", "--json",
        };
        if (property) { args.AddRange(["--property", "Value"]); }

        var exit = await ParseAndInvokeWithCaptureAsync(QueryCommand("wait-for"), args.ToArray());

        Assert.AreEqual(0, exit);
        Assert.HasCount(2, _fakeUia.Queries);
        Assert.IsTrue(_fakeUia.Queries.All(q => q.Root?.Query == "MailRow" && q.ControlType == "Edit"));
        Assert.AreEqual(1, _fakePollDelay.CallCount);
        StringAssert.Contains(TestAnsiConsole.Output, "txt-new-b234");
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task QueryOptions_Wait_ReadOtherFailureDoesNotRetry(bool property, bool comFailure)
    {
        _fakeUia.FindSingleResult = new UiElement { Type = "Edit", Selector = "txt-old-a123" };
        _fakeUia.ReadFailures.Enqueue(comFailure
            ? new System.Runtime.InteropServices.COMException("Provider failed.", unchecked((int)0x80004005))
            : new InvalidOperationException("Read failed."));
        var args = new List<string>
        {
            "Welcome", "-a", "TestApp", "--root", "MailRow", "--value", "ready", "--json",
        };
        if (property) { args.AddRange(["--property", "Value"]); }

        var exit = await ParseAndInvokeWithCaptureAsync(QueryCommand("wait-for"), args.ToArray());

        Assert.AreEqual(1, exit);
        Assert.AreEqual(0, _fakePollDelay.CallCount);
        AssertJsonErrorCode(comFailure ? "stale_element" : "internal_error");
    }

    private Command QueryCommand(string name) => name switch
    {
        "search" => GetRequiredService<UiSearchCommand>(),
        "get-property" => GetRequiredService<UiGetPropertyCommand>(),
        "get-value" => GetRequiredService<UiGetValueCommand>(),
        _ => GetRequiredService<UiWaitForCommand>(),
    };

    [TestMethod]
    [DataRow("search")]
    [DataRow("get-property")]
    [DataRow("get-value")]
    [DataRow("wait-for")]
    public async Task QueryOptions_AllCommands_ComposePredicates(string name)
    {
        _fakeUia.FindSingleResult = new UiElement { Type = "Edit", Selector = "txt-value-a123" };
        _fakeUia.SearchResult = [_fakeUia.FindSingleResult];
        var exit = await ParseAndInvokeWithCaptureAsync(QueryCommand(name),
            ["Welcome", "-w", "1234", "--root", "MailRow", "--type", "tExTbOx", "--class-name", "Literal.*", "--json"]);
        Assert.AreEqual(0, exit);
        var query = _fakeUia.Queries.Single();
        Assert.AreEqual("Welcome", query.Query);
        Assert.AreEqual("MailRow", query.Root?.Query);
        Assert.AreEqual("tExTbOx", query.ControlType);
        Assert.AreEqual("Literal.*", query.ClassName);
    }

    [TestMethod]
    [DataRow("search")]
    [DataRow("get-property")]
    [DataRow("get-value")]
    [DataRow("wait-for")]
    public async Task QueryOptions_InvalidType_FailsBeforeQuery(string name)
    {
        var exit = await ParseAndInvokeWithCaptureAsync(QueryCommand(name),
            ["Welcome", "-a", "TestApp", "--type", "50000", "--json"]);
        Assert.AreEqual(1, exit);
        AssertJsonErrorCode("invalid_arguments");
        Assert.IsEmpty(_fakeUia.Queries);
    }

    [TestMethod]
    [DataRow("search")]
    [DataRow("get-property")]
    [DataRow("get-value")]
    [DataRow("wait-for")]
    public async Task QueryOptions_EmptyRoot_FailsBeforeQuery(string name)
    {
        var exit = await ParseAndInvokeWithCaptureAsync(QueryCommand(name),
            ["Welcome", "-a", "TestApp", "--root", " ", "--json"]);
        Assert.AreEqual(1, exit);
        AssertJsonErrorCode("invalid_arguments");
        Assert.IsEmpty(_fakeUia.Queries);
    }

    [TestMethod]
    public async Task QueryOptions_Wait_PreservesRootOnEveryPoll()
    {
        _fakeUia.MovingResults["Welcome"] = new Queue<UiElement?>(
            [null, null, new UiElement { Type = "Text", Selector = "lbl-welcome-a123" }]);
        var exit = await ParseAndInvokeWithCaptureAsync(QueryCommand("wait-for"),
            ["Welcome", "-a", "TestApp", "--root", "MailRow", "--type", "Text", "--json"]);
        Assert.AreEqual(0, exit);
        Assert.HasCount(3, _fakeUia.Queries);
        Assert.IsTrue(_fakeUia.Queries.All(q => q.Root?.Query == "MailRow" && q.ControlType == "Text"));
    }

    [TestMethod]
    public async Task QueryOptions_Wait_AmbiguousRootIsNotGone()
    {
        _fakeUia.FindSingleThrow = new UiAmbiguousSelectorException("Root matched multiple elements.");
        var exit = await ParseAndInvokeWithCaptureAsync(QueryCommand("wait-for"),
            ["Welcome", "-a", "TestApp", "--root", "MailRow", "--gone", "--json"]);
        Assert.AreEqual(1, exit);
        AssertJsonErrorCode("ambiguous_selector");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task QueryOptions_Wait_LookupFailureIsNotAbsence(bool comFailure)
    {
        _fakeUia.FindSingleThrow = comFailure
            ? new System.Runtime.InteropServices.COMException("Provider unavailable.")
            : new InvalidOperationException("Lookup failed.");
        var exit = await ParseAndInvokeWithCaptureAsync(QueryCommand("wait-for"),
            ["Welcome", "-a", "TestApp", "--root", "MailRow", "--gone", "--json"]);
        Assert.AreEqual(1, exit);
        AssertJsonErrorCode(comFailure ? "stale_element" : "internal_error");
    }

    [TestMethod]
    public async Task QueryOptions_Wait_UnavailableElementRetriesButIsNotGone()
    {
        _fakeUia.FindSingleThrow = new System.Runtime.InteropServices.COMException(
            "Element was removed during traversal.", unchecked((int)0x80040201));
        var exit = await ParseAndInvokeWithCaptureAsync(QueryCommand("wait-for"),
            ["Welcome", "-a", "TestApp", "--root", "MailRow", "--gone", "--timeout", "150", "--json"]);
        Assert.AreEqual(1, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "\"timedOut\": true");
        Assert.IsTrue(_fakeUia.Queries.Count > 1);
    }

    [TestMethod]
    public async Task QueryOptions_FilteredInspect_StaysInTheSelectedWindow()
    {
        // Inspect walks only the selected window, so a filtered match in another app window must not be used.
        _fakeTargetResolver.TargetResult.WindowHandle = 4242;
        _fakeUia.FindSingleResult = new UiElement { Type = "Button", Selector = "btn-save-a123" };
        _fakeUia.InspectResult = [new UiElement { Type = "Button", Depth = 0, Selector = "btn-save-a123" }];

        var exit = await ParseAndInvokeWithCaptureAsync(GetRequiredService<UiInspectCommand>(),
            ["Save", "-a", "TestApp", "--type", "Button", "--json"]);

        Assert.AreEqual(0, exit);
        var scope = _fakeUia.QueryTargets.Single();
        Assert.AreEqual(4242, scope.WindowHandle);
        Assert.IsTrue(scope.IsExplicitWindow);
        Assert.IsFalse(_fakeTargetResolver.TargetResult.IsExplicitWindow);
        Assert.IsTrue(_fakeUia.FindSingleRequireUniqueCalls.Single());
    }

    [TestMethod]
    public async Task QueryOptions_FilteredRecord_KeepsSearchingPopupWindows()
    {
        // Recording finds a slug in the app's popups and owned dialogs, so its filtered lookup must too.
        _fakeTargetResolver.TargetResult.WindowHandle = 4242;
        _fakeUia.FindSingleResult = new UiElement { Type = "Button", Selector = "btn-save-a123" };
        _fakeRecording.RecordResult = new RecordCaptureResult { Frames = 5, Width = 100, Height = 30, Mode = "wgc" };

        var exit = await ParseAndInvokeWithCaptureAsync(GetRequiredService<UiRecordCommand>(),
            ["Save", "-a", "TestApp", "--type", "Button", "--duration-sec", "1",
             "-o", Path.Combine(_tempDirectory.FullName, "filtered.mp4"), "--json"]);

        Assert.AreEqual(0, exit);
        Assert.IsFalse(_fakeUia.QueryTargets.First().IsExplicitWindow);
        Assert.IsTrue(_fakeUia.FindSingleRequireUniqueCalls.First());
    }

    [TestMethod]
    [DataRow(4242L)]
    [DataRow(5555L)]
    public async Task QueryOptions_FilteredRecord_RecordsFromTheMatchedWindow(long matchHwnd)
    {
        // The recorder resolves the slug again, main window first, so a unique match in an owned dialog
        // must be recorded from that dialog rather than looked up where a same-named element may exist.
        _fakeTargetResolver.TargetResult.WindowHandle = 4242;
        _fakeUia.FindSingleResult = new UiElement { Type = "Edit", Selector = "edt-subject-a123", WindowHandle = matchHwnd };
        _fakeRecording.RecordResult = new RecordCaptureResult { Frames = 5, Width = 100, Height = 30, Mode = "wgc" };

        var exit = await ParseAndInvokeWithCaptureAsync(GetRequiredService<UiRecordCommand>(),
            ["Subject", "-a", "TestApp", "--type", "Edit", "--duration-sec", "1",
             "-o", Path.Combine(_tempDirectory.FullName, "matched.mp4"), "--json"]);

        Assert.AreEqual(0, exit, TestAnsiConsole.Output);
        Assert.AreEqual("edt-subject-a123", _fakeRecording.LastElementId);
        Assert.AreEqual(matchHwnd, _fakeRecording.LastTarget!.WindowHandle);
        Assert.AreEqual(matchHwnd != 4242, _fakeRecording.LastTarget.IsExplicitWindow);
        Assert.AreEqual(1234, _fakeRecording.LastTarget.ProcessId);
    }

    [TestMethod]
    [DataRow("touch", "--at", "5,5")]
    [DataRow("pen", "--at", "5,5")]
    [DataRow("pen", "--path", "5,5 10,10")]
    public async Task QueryOptions_PointerCoordinates_RejectFilters(string name, string option, string value)
    {
        Command command = name == "touch" ? GetRequiredService<UiTouchCommand>() : GetRequiredService<UiPenCommand>();
        var exit = await ParseAndInvokeWithCaptureAsync(command,
            [option, value, "--type", "Button", "-w", "4242", "--json"]);
        Assert.AreEqual(1, exit);
        AssertJsonErrorCode("invalid_arguments");
        Assert.IsEmpty(_fakeUia.Queries);
    }

    private Task<int> RunElementActionAsync(string name, bool filtered)
    {
        Command command = name switch
        {
            "set-value" => GetRequiredService<UiSetValueCommand>(),
            "click" => GetRequiredService<UiClickCommand>(),
            "focus" => GetRequiredService<UiFocusCommand>(),
            "hover" => GetRequiredService<UiHoverCommand>(),
            "scroll" => GetRequiredService<UiScrollCommand>(),
            "scroll-into-view" => GetRequiredService<UiScrollIntoViewCommand>(),
            "touch" => GetRequiredService<UiTouchCommand>(),
            _ => GetRequiredService<UiPenCommand>(),
        };
        List<string> args = name switch
        {
            "set-value" => ["Subject", "draft"],
            "scroll" => ["Subject", "--wheel", "1"],
            _ => ["Subject"],
        };
        args.AddRange(["-a", "TestApp", "--json"]);
        if (filtered) { args.AddRange(["--type", "Edit"]); }
        _fakeUia.PropertiesResult["HasKeyboardFocus"] = true;
        return ParseAndInvokeWithCaptureAsync(command, [.. args]);
    }

    [TestMethod]
    [DataRow("set-value")]
    [DataRow("click")]
    [DataRow("focus")]
    [DataRow("hover")]
    [DataRow("scroll")]
    [DataRow("scroll-into-view")]
    [DataRow("touch")]
    [DataRow("pen")]
    public async Task QueryOptions_FilteredAction_EveryLookupRequiresAUniqueMatch(string name)
    {
        // A filtered action must check every app window for a second match, including when a gesture
        // re-reads the element just before injecting input.
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "txt", Selector = "txt-subject-a1b2", Name = "Subject", Type = "Edit",
            X = 10, Y = 20, Width = 40, Height = 30, WindowHandle = 4242,
        };

        var exit = await RunElementActionAsync(name, filtered: true);

        Assert.AreEqual(0, exit, TestAnsiConsole.Output);
        Assert.IsNotEmpty(_fakeUia.FindSingleRequireUniqueCalls);
        Assert.IsTrue(_fakeUia.FindSingleRequireUniqueCalls.All(unique => unique));
        Assert.HasCount(_fakeUia.Queries.Count, _fakeUia.FindSingleRequireUniqueCalls,
            "every lookup of a filtered selector must require a unique match");
    }

    [TestMethod]
    [DataRow("set-value")]
    [DataRow("click")]
    [DataRow("focus")]
    [DataRow("hover")]
    [DataRow("scroll")]
    [DataRow("scroll-into-view")]
    [DataRow("touch")]
    [DataRow("pen")]
    public async Task QueryOptions_FilteredAction_AmbiguousAcrossWindowsFailsWithoutActing(string name)
    {
        // Same-named fields in the main window and an owned dialog: fail instead of using the main window's.
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "txt", Selector = "txt-subject-a1b2", Name = "Subject", Type = "Edit",
            X = 10, Y = 20, Width = 40, Height = 30, WindowHandle = 4242,
        };
        _fakeUia.FindUniqueThrow = new UiAmbiguousSelectorException("Selector matched 2 elements.");

        var exit = await RunElementActionAsync(name, filtered: true);

        Assert.AreEqual(1, exit);
        AssertJsonErrorCode("ambiguous_selector");
        Assert.IsTrue(_fakeUia.FindSingleRequireUniqueCalls.Single(), "the command must stop at the ambiguous lookup");
        Assert.IsEmpty(_fakeUia.Queries);
        Assert.IsEmpty(_fakeDesktopForeground.ForegroundRequests);
        Assert.IsEmpty(_fakeMouse.ClickCalls);
    }

    [TestMethod]
    [DataRow("set-value")]
    [DataRow("click")]
    [DataRow("touch")]
    public async Task QueryOptions_UnfilteredAction_KeepsMainWindowFirstLookup(string name)
    {
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "txt", Selector = "txt-subject-a1b2", Name = "Subject", Type = "Edit",
            X = 10, Y = 20, Width = 40, Height = 30, WindowHandle = 4242,
        };

        var exit = await RunElementActionAsync(name, filtered: false);

        Assert.AreEqual(0, exit, TestAnsiConsole.Output);
        Assert.IsEmpty(_fakeUia.FindSingleRequireUniqueCalls);
    }

    [TestMethod]
    public void QueryOptions_EverySelectorCommand_AcceptsElementFilters()
    {
        static IEnumerable<Command> All(Command command) => command.Subcommands.SelectMany(c => All(c).Prepend(c));
        var offenders = All(GetRequiredService<WinAppRootCommand>())
            .Where(c => c.Arguments.Any(a => a.Name == "selector")
                && !(c.Options.Contains(UiQueryOptions.Type)
                     && c.Options.Contains(UiQueryOptions.Root)
                     && c.Options.Contains(UiQueryOptions.ClassName)))
            .Select(c => c.Name)
            .ToList();

        Assert.IsEmpty(offenders,
            $"Commands that take a selector must accept --type, --root, and --class-name (UiQueryOptions.AddTo): {string.Join(", ", offenders)}");
    }
}
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
}

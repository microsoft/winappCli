// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
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
}

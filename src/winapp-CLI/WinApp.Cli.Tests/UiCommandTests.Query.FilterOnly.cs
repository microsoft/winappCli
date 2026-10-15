// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

/// <summary><c>--type</c> and <c>--class-name</c> used as the whole query, with no text selector.</summary>
public partial class UiCommandTests
{
    [TestMethod]
    [DataRow("search", "--class-name")]
    [DataRow("get-property", "--class-name")]
    [DataRow("get-value", "--class-name")]
    [DataRow("wait-for", "--class-name")]
    [DataRow("search", "--type")]
    [DataRow("get-property", "--type")]
    [DataRow("get-value", "--type")]
    [DataRow("wait-for", "--type")]
    public async Task FilterOnly_QueryCommands_AcceptAFilterWithoutASelector(string command, string option)
    {
        _fakeUia.FindSingleResult = new UiElement { Type = "Pane", Selector = "pane-calendar-a123" };
        _fakeUia.SearchResult = [_fakeUia.FindSingleResult];
        var value = option == "--type" ? "Pane" : "CalendarSurface";

        var exit = await ParseAndInvokeWithCaptureAsync(QueryCommand(command), ["-a", "TestApp", option, value, "--json"]);

        Assert.AreEqual(0, exit, TestAnsiConsole.Output);
        var query = _fakeUia.Queries.Single();
        Assert.IsNull(query.Query);
        Assert.IsNull(query.Slug);
        Assert.AreEqual(value, option == "--type" ? query.ControlType : query.ClassName);
    }

    [TestMethod]
    [DataRow("search")]
    [DataRow("get-property")]
    [DataRow("get-value")]
    [DataRow("wait-for")]
    public async Task FilterOnly_RootAlone_IsNotAQuery(string command)
    {
        var exit = await ParseAndInvokeWithCaptureAsync(QueryCommand(command), ["-a", "TestApp", "--root", "MailRow", "--json"]);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(ConsoleStdErr.ToString(), "A selector, --type, or --class-name is required");
        Assert.IsEmpty(_fakeUia.Queries);
    }

    [TestMethod]
    public async Task FilterOnly_NotFound_NamesTheFilters()
    {
        _fakeUia.FindSingleResult = null;

        var exit = await ParseAndInvokeWithCaptureAsync(QueryCommand("get-property"),
            ["-a", "TestApp", "--type", "Pane", "--class-name", "CalendarSurface", "--json"]);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(ConsoleStdErr.ToString(), "--type Pane --class-name CalendarSurface");
    }

    private Task<int> RunFilterOnlyActionAsync(string name)
    {
        Command command = name switch
        {
            "set-value" => GetRequiredService<UiSetValueCommand>(),
            "click" => GetRequiredService<UiClickCommand>(),
            "focus" => GetRequiredService<UiFocusCommand>(),
            "hover" => GetRequiredService<UiHoverCommand>(),
            "invoke" => GetRequiredService<UiInvokeCommand>(),
            "scroll" => GetRequiredService<UiScrollCommand>(),
            "scroll-into-view" => GetRequiredService<UiScrollIntoViewCommand>(),
            "touch" => GetRequiredService<UiTouchCommand>(),
            _ => GetRequiredService<UiPenCommand>(),
        };
        List<string> args = name switch
        {
            // The value is the second positional, so an empty selector stands in for the missing one.
            "set-value" => ["", "draft", "--class-name", "CalendarSurface", "-a", "TestApp", "--json"],
            "scroll" => ["--class-name", "CalendarSurface", "-a", "TestApp", "--json", "--wheel", "1"],
            _ => ["--class-name", "CalendarSurface", "-a", "TestApp", "--json"],
        };
        _fakeUia.PropertiesResult["HasKeyboardFocus"] = true;
        return ParseAndInvokeWithCaptureAsync(command, [.. args]);
    }

    [TestMethod]
    [DataRow("set-value")]
    [DataRow("click")]
    [DataRow("focus")]
    [DataRow("hover")]
    [DataRow("invoke")]
    [DataRow("scroll")]
    [DataRow("scroll-into-view")]
    [DataRow("touch")]
    [DataRow("pen")]
    public async Task FilterOnly_Action_RequiresExactlyOneMatch(string name)
    {
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "cal", Selector = "pane-calendar-a1b2", Type = "Pane", ClassName = "CalendarSurface",
            X = 10, Y = 20, Width = 40, Height = 30, WindowHandle = 4242,
        };

        var exit = await RunFilterOnlyActionAsync(name);

        Assert.AreEqual(0, exit, TestAnsiConsole.Output + ConsoleStdErr);
        Assert.IsNotEmpty(_fakeUia.FindSingleRequireUniqueCalls);
        Assert.IsTrue(_fakeUia.FindSingleRequireUniqueCalls.All(unique => unique));
        Assert.IsTrue(_fakeUia.Queries.All(q => q.Query is null && q.Slug is null && q.ClassName == "CalendarSurface"));
    }

    [TestMethod]
    [DataRow("click")]
    [DataRow("focus")]
    [DataRow("invoke")]
    [DataRow("touch")]
    public async Task FilterOnly_Action_SeveralMatchesFailWithoutActing(string name)
    {
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "cal", Selector = "pane-calendar-a1b2", Type = "Pane",
            X = 10, Y = 20, Width = 40, Height = 30, WindowHandle = 4242,
        };
        _fakeUia.FindUniqueThrow = new UiAmbiguousSelectorException("Selector matched 2 elements.");

        var exit = await RunFilterOnlyActionAsync(name);

        Assert.AreEqual(1, exit);
        AssertJsonErrorCode("ambiguous_selector");
        Assert.IsEmpty(_fakeDesktopForeground.ForegroundRequests);
        Assert.IsEmpty(_fakeMouse.ClickCalls);
        Assert.AreEqual(0, _fakeUia.AutomaticInvokeCalls);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task FilterOnly_SetValue_MissingValuePointsAtTheEmptySelector(bool onePositional)
    {
        // A lone "draft" binds to the selector slot, so the value is missing either way.
        List<string> args = ["--class-name", "CalendarSurface", "-a", "TestApp"];
        if (onePositional) { args.Insert(0, "draft"); }

        var exit = await ParseAndInvokeWithCaptureAsync(GetRequiredService<UiSetValueCommand>(), [.. args]);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(ConsoleStdErr.ToString(), "set-value \"\" <value>");
        Assert.IsEmpty(_fakeUia.Queries);
    }

    [TestMethod]
    public async Task FilterOnly_Inspect_ShowsTheOneMatchingElement()
    {
        _fakeUia.FindSingleResult = new UiElement { Type = "Pane", Selector = "pane-calendar-a123" };
        _fakeUia.InspectResult = [new UiElement { Type = "Pane", Depth = 0, Selector = "pane-calendar-a123" }];

        var exit = await ParseAndInvokeWithCaptureAsync(GetRequiredService<UiInspectCommand>(),
            ["-a", "TestApp", "--class-name", "CalendarSurface", "--json"]);

        Assert.AreEqual(0, exit, TestAnsiConsole.Output);
        Assert.IsTrue(_fakeUia.FindSingleRequireUniqueCalls.Single());
        Assert.AreEqual("CalendarSurface", _fakeUia.Queries.Single().ClassName);
    }

    [TestMethod]
    public async Task FilterOnly_Screenshot_CapturesTheElementInsteadOfEveryWindow()
    {
        ArrangeTwoTopLevelWindows();
        _fakeUia.FindSingleResult = new UiElement { Type = "Pane", Selector = "pane-calendar-a123" };

        var exit = await ParseAndInvokeWithCaptureAsync(GetRequiredService<UiScreenshotCommand>(),
            ["-a", App, "--class-name", "CalendarSurface", "--json", "-o", ShotPath()]);

        Assert.AreEqual(0, exit, TestAnsiConsole.Output + ConsoleStdErr);
        Assert.IsTrue(_fakeUia.FindSingleRequireUniqueCalls.Single());
        Assert.HasCount(1, _fakeUia.ScreenshotCalls, "a filter picks one element, not a multi-window composite");
    }

    [TestMethod]
    [DataRow("inspect")]
    [DataRow("screenshot")]
    [DataRow("focus")]
    public async Task FilterOnly_OptionalSelectorCommands_RejectRootAlone(string name)
    {
        Command command = name switch
        {
            "inspect" => GetRequiredService<UiInspectCommand>(),
            "screenshot" => GetRequiredService<UiScreenshotCommand>(),
            _ => GetRequiredService<UiFocusCommand>(),
        };
        List<string> args = ["-a", "TestApp", "--root", "MailRow", "--json"];
        if (name == "screenshot") { args.AddRange(["-o", ShotPath()]); }

        var exit = await ParseAndInvokeWithCaptureAsync(command, [.. args]);

        Assert.AreEqual(1, exit);
        AssertJsonErrorCode("invalid_arguments");
        Assert.IsEmpty(_fakeUia.Queries);
    }
}

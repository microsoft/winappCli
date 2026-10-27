// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Spectre.Console.Testing;
using WinApp.Cli.Commands;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public class DevToolsLogsCommandTests
{
    private const string Logs = """
        {"state":"ok","next":3,"dropped":0,"errors":1,"entries":[
          {"seq":1,"kind":"renderError","level":"error","event":"RenderError","time":"2026-10-02T09:00:00.000Z","log":true,"component":"Counter","exception":"System.InvalidOperationException"},
          {"seq":2,"kind":"warning","level":"warning","event":"Warning","time":"2026-10-02T09:00:01.000Z","log":true,"category":"Theme","operation":"Resolve","message":"Missing brush key"}]}
        """;

    private static async Task<(int Exit, string Output)> Run(FakeDevToolsProtocolAgent agent, params string[] args)
    {
        var console = new TestConsole();
        console.Profile.Width = 300;
        var command = new DevToolsLogsCommand();
        command.Action = new DevToolsLogsCommand.Handler(agent.Resolver(), console) { Interval = TimeSpan.FromMilliseconds(10) };
        var exit = await command.Parse(args).InvokeAsync();
        return (exit, console.Output);
    }

    [TestMethod]
    public async Task Logs_ShowsErrorsAndWarnings()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("Reactor.logs", Logs);
        var (exit, output) = await Run(agent);
        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(output, "Render error in <Counter>: System.InvalidOperationException");
        StringAssert.Contains(output, "Theme/Resolve: Missing brush key");
        var request = JsonDocument.Parse(agent.ReceivedRequests.Single(r => r.Contains("Reactor.logs", StringComparison.Ordinal)));
        Assert.AreEqual("info", request.RootElement.GetProperty("params").GetProperty("level").GetString());
    }

    [TestMethod]
    public async Task Logs_AreProcessWide_SoTheyOfferNoWindowOrRootScope()
    {
        var command = new DevToolsLogsCommand();
        Assert.IsFalse(command.Options.Any(option => option.Name is "--window" or "--root" || option.Aliases.Contains("-w")),
            "the app's logs are not per window; -w would always fail");
        Assert.IsNotEmpty(command.Parse(["-w", "100"]).Errors);

        // Without the options, the handler still runs (it reads no window from the parse).
        using var agent = new FakeDevToolsProtocolAgent().Answer("Reactor.logs", Logs);
        var (exit, output) = await Run(agent);
        Assert.AreEqual(0, exit, output);
        Assert.IsFalse(agent.ReceivedRequests.Any(r => r.Contains("\"window\"", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Logs_JsonCarriesTheEntries()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("Reactor.logs", Logs);
        var (exit, output) = await Run(agent, "--json", "--level", "warning");
        Assert.AreEqual(0, exit, output);
        using var doc = JsonDocument.Parse(output);
        Assert.IsTrue(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.AreEqual(2, doc.RootElement.GetProperty("entries").GetArrayLength());
        Assert.AreEqual(1, doc.RootElement.GetProperty("errors").GetInt32());
        Assert.IsTrue(agent.ReceivedRequests.Any(r => r.Contains("\"level\":\"warning\"", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Logs_FollowAsksOnlyForNewEntries_AndStopsWhenTheAppExits()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("Reactor.logs", Logs, request => request.Contains("\"since\":0", StringComparison.Ordinal))
            .Answer("Reactor.logs", """{"state":"ok","next":4,"dropped":0,"errors":1,"entries":[{"seq":3,"kind":"windowOpened","level":"info","event":"WindowOpened","log":true,"windowType":"ReactorWindow"}]}""",
                request => request.Contains("\"since\":3", StringComparison.Ordinal))
            .Error("Reactor.logs", -32000, "app-exited", "The app exited.");
        var (exit, output) = await Run(agent, "--follow");
        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(output, "Render error in <Counter>");
        StringAssert.Contains(output, "Window opened: ReactorWindow");
        Assert.AreEqual(1, output.Split("Render error").Length - 1, "an entry is printed once");
    }

    [TestMethod]
    public async Task Logs_InAnAppWithoutReactor_Explains()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("Reactor.logs", """{"state":"absent","entries":[],"next":0,"dropped":0,"errors":0}""");
        var (exit, output) = await Run(agent, "--json");
        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "reactor-absent");
    }

    [TestMethod]
    public async Task Logs_RefusesAnUnknownLevel()
    {
        using var agent = new FakeDevToolsProtocolAgent();
        var (exit, _) = await Run(agent, "--level", "loud");
        Assert.AreNotEqual(0, exit);
        Assert.IsFalse(agent.ReceivedRequests.Any(r => r.Contains("Reactor.logs", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void UnavailableMessage_TellsANativeAotAppToRunWithoutAot_AndOthersToRelaunch()
    {
        Assert.AreEqual("Reactor logs are unavailable: the app has no .NET runtime to load the managed DevTools agent into (Native AOT). " +
            "Logs need the managed DevTools agent: run a build without --aot to read them.",
            DevToolsLogsCommand.UnavailableMessage("the app has no .NET runtime to load the managed DevTools agent into (Native AOT)"));
        Assert.AreEqual("Reactor logs are unavailable: no managed DevTools agent is loaded in this app. " +
            "Launch the app with 'winapp run <project> --devtools on'.",
            DevToolsLogsCommand.UnavailableMessage("no managed DevTools agent is loaded in this app."));
        StringAssert.StartsWith(DevToolsLogsCommand.UnavailableMessage(null), "Reactor logs are unavailable. Launch");
    }

    [TestMethod]
    public void Format_DescribesEachKind()
    {
        string Line(string json) => DevToolsLogsCommand.Format(JsonDocument.Parse(json).RootElement);
        StringAssert.Contains(Line("""{"kind":"hresult","level":"warning","category":"Hosting","operation":"Dpi","hresult":"0x80004005"}"""),
            "Hosting/Dpi failed with 0x80004005");
        StringAssert.Contains(Line("""{"kind":"hotReload","level":"info","source":"metadataUpdate"}"""), "Hot reload applied an edit");
        StringAssert.Contains(Line("""{"kind":"swallowedError","level":"warning","category":"Theme","operation":"Apply","exception":"COMException"}"""),
            "Swallowed error in Theme/Apply: COMException");
        StringAssert.Contains(Line("""{"kind":"other","level":"warning","event":"IntlMissingKey","detail":"key=Title"}"""), "IntlMissingKey: key=Title");
        StringAssert.Contains(Line("""{"kind":"keyedList","level":"warning","control":"ListView","problem":"duplicateKey","count":3,"keys":2}"""),
            "Keyed list in ListView has duplicate keys (2 keys affected); it re-creates its rows");
        StringAssert.Contains(Line("""{"kind":"keyedList","level":"warning","control":"ItemsRepeater","problem":"nullKey","count":1,"keys":1,"component":"TaskList","file":"C:\\src\\App\\TaskList.cs","line":42,"column":13,"first":true}"""),
            "Keyed list in ItemsRepeater of <TaskList> at TaskList.cs:42 has an item with a null key (1 key affected)");
    }
}

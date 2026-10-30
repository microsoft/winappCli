// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Spectre.Console.Testing;
using WinApp.Cli.Commands;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public class ReactorStateCommandTests
{
    private const string Counter = """
        {"state":"ok","version":"0.1.0","api":"internal","exact":true,"idle":true,
         "component":{"kind":"class","name":"Counter","type":"Counter","isRoot":false,"mounted":true},
         "declared":{"file":"C:\\src\\App.cs","line":44,"column":13},
         "definition":{"file":"/_/App.cs","fileOnDisk":"C:\\src\\App.cs","line":71,"column":5,"method":"Counter.Render","checksumAlgorithm":"SHA256","checksum":"ab","stale":true},
         "props":{"type":"CounterProps","values":[{"name":"Label","value":"Done today"}]},
         "hooks":[
           {"index":0,"kind":"state","type":"int","value":"3","canSet":true,"editable":true},
           {"index":1,"kind":"context","type":"string","value":"\"Compact\"","context":"Density","canSet":false,"editable":false},
           {"index":2,"kind":"effect"}],
         "contexts":[{"hook":1,"name":"Density","type":"string","value":"\"Compact\""}]}
        """;

    private static async Task<(int Exit, string Output)> Run(DevToolsLiveCommand command, FakeDevToolsProtocolAgent agent, string[] args)
    {
        using var console = new TestConsole();
        console.Profile.Width = 300;
        command.Action = command switch
        {
            DevToolsGetStateCommand => new DevToolsGetStateCommand.Handler(agent.Resolver(), console),
            DevToolsSetStateCommand => new DevToolsSetStateCommand.Handler(agent.Resolver(), console),
            DevToolsRerenderCommand => new DevToolsRerenderCommand.Handler(agent.Resolver(), console),
            DevToolsInvokeHandlerCommand => new DevToolsInvokeHandlerCommand.Handler(agent.Resolver(), console),
            DevToolsGetResourceCommand => new DevToolsGetResourceCommand.Handler(agent.Resolver(), console),
            DevToolsSetResourceCommand => new DevToolsSetResourceCommand.Handler(agent.Resolver(), console),
            DevToolsDiagnoseBindingCommand => new DevToolsDiagnoseBindingCommand.Handler(agent.Resolver(), console),
            DevToolsGetPropertyCommand => new DevToolsGetPropertyCommand.Handler(agent.Resolver(), console),
            _ => throw new NotSupportedException(),
        };
        var exit = await command.Parse(args).InvokeAsync();
        return (exit, console.Output);
    }

    private static JsonElement Params(FakeDevToolsProtocolAgent agent, string method) =>
        JsonDocument.Parse(agent.ReceivedRequests.Single(r => r.Contains($"\"method\":\"{method}\"", StringComparison.Ordinal)))
            .RootElement.GetProperty("params").Clone();

    [TestMethod]
    public async Task GetState_ListsPropsHooksContextsAndWhereTheComponentIsDefined()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("Reactor.component", Counter);
        var (exit, output) = await Run(new DevToolsGetStateCommand(), agent, ["42"]);

        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(output, "<Counter> class component · defined App.cs:71 (Counter.Render) · declared App.cs:44");
        StringAssert.Contains(output, "App.cs changed since the app was built");
        StringAssert.Contains(output, "Label = Done today");
        StringAssert.Contains(output, "[0] state int = 3 settable");
        StringAssert.Contains(output, "[1] context string = \"Compact\" (Density)");
        StringAssert.Contains(output, "[2] effect");
        StringAssert.Contains(output, "set-state");
        Assert.AreEqual("42", Params(agent, "Reactor.component").GetProperty("handle").GetString());
    }

    [TestMethod]
    public async Task GetState_JsonCarriesTheAgentAnswerWithIdentity()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("Reactor.component", Counter);
        var (exit, output) = await Run(new DevToolsGetStateCommand(), agent, ["42", "--json"]);

        Assert.AreEqual(0, exit, output);
        using var doc = JsonDocument.Parse(output);
        Assert.IsTrue(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.AreEqual("42", doc.RootElement.GetProperty("handle").GetString());
        Assert.AreEqual(3, doc.RootElement.GetProperty("hooks").GetArrayLength());
        Assert.IsTrue(doc.RootElement.GetProperty("definition").GetProperty("stale").GetBoolean());
        Assert.IsFalse(doc.RootElement.TryGetProperty("state", out _));
    }

    [TestMethod]
    public async Task GetState_FailsWhenNoComponentRendersTheElementOrReactorIsAbsent()
    {
        using (var agent = new FakeDevToolsProtocolAgent().Answer("Reactor.component", """{"state":"none","reason":"no Reactor component renders this element"}"""))
        {
            var (exit, output) = await Run(new DevToolsGetStateCommand(), agent, ["42", "--json"]);
            Assert.AreEqual(1, exit);
            Assert.AreEqual("no-component", JsonDocument.Parse(output).RootElement.GetProperty("error").GetProperty("token").GetString());
        }
        using (var agent = new FakeDevToolsProtocolAgent().Answer("Reactor.component", """{"state":"absent"}"""))
        {
            var (exit, output) = await Run(new DevToolsGetStateCommand(), agent, ["42"]);
            Assert.AreEqual(1, exit);
            StringAssert.Contains(output, "does not use Microsoft.UI.Reactor");
        }
    }

    [TestMethod]
    public async Task GetStateAndRerender_SayWhenTheWindowShowsReactorsErrorScreen()
    {
        const string Reason = "this window shows Reactor's error screen instead of <MainShell>, so no component is rendered here. " +
            "Render error: ArgumentException: ShowAttached failed. Fix the error in the app's C#, then use Hot Reload or restart the app";
        using (var agent = new FakeDevToolsProtocolAgent().Answer("Reactor.component", $$"""{"state":"none","errorScreen":true,"reason":"{{Reason}}"}"""))
        {
            var (exit, output) = await Run(new DevToolsGetStateCommand(), agent, ["42", "--json"]);
            Assert.AreEqual(1, exit);
            var error = JsonDocument.Parse(output).RootElement.GetProperty("error");
            Assert.AreEqual("reactor-error-screen", error.GetProperty("token").GetString());
            StringAssert.StartsWith(error.GetProperty("message").GetString(), "This window shows Reactor's error screen instead of <MainShell>");
        }
        using (var agent = new FakeDevToolsProtocolAgent().Answer("Reactor.rerender", $$"""{"state":"refused","code":"reactor-error-screen","reason":"{{Reason}}"}"""))
        {
            var (exit, output) = await Run(new DevToolsRerenderCommand(), agent, ["42"]);
            Assert.AreEqual(1, exit);
            StringAssert.Contains(output, "error screen");
            Assert.IsFalse(output.Contains("No Reactor component renders", StringComparison.Ordinal), output);
        }
    }

    [TestMethod]
    public async Task SetState_SendsTheIndexAndValueAndReportsTheReadBack()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("Reactor.setState", """
            {"state":"ok","component":"Counter","index":0,"kind":"state","type":"int","requested":"42","before":"3","after":"42","changed":true,"waitedForIdle":true,"rendered":true,"api":"internal"}
            """);
        var (exit, output) = await Run(new DevToolsSetStateCommand(), agent, ["42", "0", "42"]);

        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(output, "<Counter> hook [0] (state int): 3 -> 42");
        StringAssert.Contains(output, "Live only");
        var request = Params(agent, "Reactor.setState");
        Assert.AreEqual(0, request.GetProperty("hook").GetInt32());
        Assert.AreEqual("42", request.GetProperty("value").GetString());
    }

    [TestMethod]
    public async Task BusyUiThread_SaysToRetry_NotToRelaunch()
    {
        const string busy = """{"state":"unavailable","reason":"the app's UI thread did not answer (0x80070102)"}""";
        using (var agent = new FakeDevToolsProtocolAgent().Answer("Reactor.component", busy))
        {
            var (exit, output) = await Run(new DevToolsGetStateCommand(), agent, ["42"]);
            Assert.AreEqual(1, exit);
            StringAssert.Contains(output, "busy");
            StringAssert.Contains(output, "run the command again");
            Assert.DoesNotContain("--devtools on", output, "it was launched that way; relaunching would not help");
        }
        Assert.AreEqual(ReactorDescription.BusyMessage, ReactorDescription.Parse(busy)!.ExplainUnavailable());
        Assert.AreEqual(ReactorDescription.BusyMessage, DevToolsLogsCommand.UnavailableMessage("the app's UI thread did not answer (0x80070102)"));
        StringAssert.Contains(DevToolsLogsCommand.UnavailableMessage("no managed DevTools agent is loaded"), "--devtools on");
    }

    [TestMethod]
    public async Task SetState_RefusalsAndTimeoutsAreFailuresWithStableTokens()
    {
        using (var agent = new FakeDevToolsProtocolAgent().Answer("Reactor.setState",
            """{"state":"refused","code":"bad-value","reason":"'x' is not a valid int"}"""))
        {
            var (exit, output) = await Run(new DevToolsSetStateCommand(), agent, ["42", "0", "x", "--json"]);
            Assert.AreEqual(1, exit);
            var error = JsonDocument.Parse(output).RootElement.GetProperty("error");
            Assert.AreEqual("bad-value", error.GetProperty("token").GetString());
            StringAssert.Contains(error.GetProperty("message").GetString(), "not a valid int");
        }
        using (var agent = new FakeDevToolsProtocolAgent().Answer("Reactor.setState",
            """{"state":"unavailable","reason":"the app UI thread did not respond in time","indeterminate":true}"""))
        {
            var (exit, output) = await Run(new DevToolsSetStateCommand(), agent, ["42", "0", "1", "--json"]);
            Assert.AreEqual(1, exit);
            using var doc = JsonDocument.Parse(output);
            Assert.IsTrue(doc.RootElement.GetProperty("indeterminate").GetBoolean());
            Assert.AreEqual("indeterminate", doc.RootElement.GetProperty("error").GetProperty("token").GetString());
        }
    }

    private const string NamedCounter = """
        {"state":"ok","version":"0.0.0-devtools.8","api":"reactor","exact":true,
         "component":{"kind":"class","name":"Counter","isRoot":false,"mounted":true},
         "hooks":[
           {"index":0,"kind":"state","type":"int","value":"3","canSet":true,"editable":true,"name":"count","line":68},
           {"index":1,"kind":"state","type":"string","value":"\"\"","canSet":true,"editable":true,"name":"draft","line":69},
           {"index":2,"kind":"effect","name":"UseEffect","line":70}],
         "unindexedHooks":[{"name":"filter","line":74}],
         "contexts":[]}
        """;

    [TestMethod]
    public async Task GetState_ShowsHookNamesNextToTheirIndexes()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("Reactor.component", NamedCounter);
        var (exit, output) = await Run(new DevToolsGetStateCommand(), agent, ["42"]);

        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(output, "[0] count state int = 3 settable");
        StringAssert.Contains(output, "[2] UseEffect effect");
        StringAssert.Contains(output, "[?] filter line 74 (its index is not known statically)");
        StringAssert.Contains(output, "set-state <selector> <name|index> <value>");
    }

    [TestMethod]
    public async Task SetState_AcceptsTheHooksCSharpName()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("Reactor.component", NamedCounter)
            .Answer("Reactor.setState", """
                {"state":"ok","component":"Counter","index":0,"kind":"state","type":"int","requested":"42","before":"3","after":"42","changed":true,"waitedForIdle":true,"rendered":true,"api":"reactor"}
                """);
        var (exit, output) = await Run(new DevToolsSetStateCommand(), agent, ["42", "count", "42"]);

        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(output, "<Counter> hook [0] count (state int): 3 -> 42");
        Assert.AreEqual(0, Params(agent, "Reactor.setState").GetProperty("hook").GetInt32(), "the name is sent as its index");
    }

    [TestMethod]
    public async Task SetState_RefusesANameThatNamesNoIndexedHook()
    {
        using (var agent = new FakeDevToolsProtocolAgent().Answer("Reactor.component", NamedCounter))
        {
            var (exit, output) = await Run(new DevToolsSetStateCommand(), agent, ["42", "missing", "1", "--json"]);
            Assert.AreEqual(1, exit);
            var error = JsonDocument.Parse(output).RootElement.GetProperty("error");
            Assert.AreEqual("bad-hook", error.GetProperty("token").GetString());
            StringAssert.Contains(error.GetProperty("message").GetString(), "has no hook named 'missing'");
            Assert.IsFalse(agent.ReceivedRequests.Any(r => r.Contains("Reactor.setState", StringComparison.Ordinal)));
        }
        using (var agent = new FakeDevToolsProtocolAgent().Answer("Reactor.component", NamedCounter))
        {
            var (exit, output) = await Run(new DevToolsSetStateCommand(), agent, ["42", "filter", "x"]);
            Assert.AreEqual(1, exit);
            StringAssert.Contains(output, "not known");
        }
        using (var agent = new FakeDevToolsProtocolAgent().Answer("Reactor.component", Counter))
        {
            var (exit, output) = await Run(new DevToolsSetStateCommand(), agent, ["42", "count", "1"]);
            Assert.AreEqual(1, exit);
            StringAssert.Contains(output, "does not record hook names", "an older Reactor takes the index only");
        }
    }

    // A late attach: Reactor recorded no hook names, and the definition's C# is on this machine.
    private static string LateAttachLogin(string sourcePath) => $$"""
        {"state":"ok","version":"0.1.0","api":"internal","exact":true,"idle":true,
         "component":{"kind":"class","name":"Login2","type":"Login2","isRoot":false,"mounted":true},
         "definition":{"file":{{JsonSerializer.Serialize(sourcePath)}},"line":4,"column":5,"method":"Login2.Render"},
         "hooks":[
           {"index":0,"kind":"state","type":"string","value":"\"hook-pass-1\"","canSet":true,"editable":true},
           {"index":1,"kind":"state","type":"int","value":"3","canSet":true,"editable":true},
           {"index":2,"kind":"context","type":"string","value":"\"ctx-pass-1\"","context":"Context<string>","canSet":false,"editable":false}],
         "contexts":[{"hook":2,"name":"Context<string>","type":"string","value":"\"ctx-pass-1\""}]}
        """;

    private const string LoginSource = """
        class Login2 : Component<Login2Props>
        {
            public override Element Render()
            {
                var (password, setPassword) = UseState("hook-pass-1");
                var (count, setCount) = UseState(3);
                var dbPassword = UseContext(Ctx.Db);
                return TextBlock($"{password.Length} {count} {dbPassword.Length}");
            }
        }
        """;

    private static string WriteSource(string text)
    {
        var folder = Path.Combine(Path.GetTempPath(), "winapp-hooksource-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "Panels.cs");
        File.WriteAllText(path, text);
        return path;
    }

    [TestMethod]
    public async Task GetState_AfterALateAttachNamesHooksFromTheSourceAndRedactsPasswords()
    {
        var path = WriteSource(LoginSource);
        try
        {
            string output;
            using (var agent = new FakeDevToolsProtocolAgent().Answer("Reactor.component", LateAttachLogin(path)))
            {
                int exit;
                (exit, output) = await Run(new DevToolsGetStateCommand(), agent, ["42"]);
                Assert.AreEqual(0, exit, output);
            }
            StringAssert.Contains(output, "[0] password state string = <redacted>");
            StringAssert.Contains(output, "[1] count state int = 3 settable");
            StringAssert.Contains(output, "[2] dbPassword context string = <redacted>");
            StringAssert.Contains(output, "Context Context<string> = <redacted>");
            StringAssert.Contains(output, "Hook names read from Panels.cs");
            Assert.IsFalse(output.Contains("hook-pass-1", StringComparison.Ordinal) || output.Contains("ctx-pass-1", StringComparison.Ordinal), output);

            using var jsonAgent = new FakeDevToolsProtocolAgent().Answer("Reactor.component", LateAttachLogin(path));
            var (jsonExit, json) = await Run(new DevToolsGetStateCommand(), jsonAgent, ["42", "--json"]);
            Assert.AreEqual(0, jsonExit, json);
            Assert.IsFalse(json.Contains("hook-pass-1", StringComparison.Ordinal) || json.Contains("ctx-pass-1", StringComparison.Ordinal), json);
            var hooks = JsonDocument.Parse(json).RootElement.GetProperty("hooks");
            Assert.AreEqual("password", hooks[0].GetProperty("name").GetString());
            Assert.AreEqual("csharp-source-text", hooks[0].GetProperty("nameSource").GetString());
            Assert.IsTrue(hooks[0].GetProperty("redacted").GetBoolean());
            Assert.IsFalse(hooks[0].GetProperty("editable").GetBoolean());
            Assert.AreEqual("count", hooks[1].GetProperty("name").GetString());
            Assert.IsTrue(hooks[1].GetProperty("editable").GetBoolean());
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [TestMethod]
    public async Task SetState_AfterALateAttachRefusesAPasswordHookWithoutCallingTheApp()
    {
        var path = WriteSource(LoginSource);
        try
        {
            foreach (var hook in new[] { "0", "password" })
            {
                using var agent = new FakeDevToolsProtocolAgent().Answer("Reactor.component", LateAttachLogin(path));
                var (exit, output) = await Run(new DevToolsSetStateCommand(), agent, ["42", hook, "x", "--json"]);
                Assert.AreEqual(1, exit, output);
                Assert.AreEqual("secret", JsonDocument.Parse(output).RootElement.GetProperty("error").GetProperty("token").GetString());
                Assert.IsFalse(agent.ReceivedRequests.Any(r => r.Contains("Reactor.setState", StringComparison.Ordinal)), hook);
            }

            using var allowed = new FakeDevToolsProtocolAgent()
                .Answer("Reactor.component", LateAttachLogin(path))
                .Answer("Reactor.setState", """
                    {"state":"ok","component":"Login2","index":1,"kind":"state","type":"int","requested":"5","before":"3","after":"5","changed":true,"waitedForIdle":true,"rendered":true,"api":"internal"}
                    """);
            var (allowedExit, allowedOutput) = await Run(new DevToolsSetStateCommand(), allowed, ["42", "count", "5"]);
            Assert.AreEqual(0, allowedExit, allowedOutput);
            Assert.AreEqual(1, Params(allowed, "Reactor.setState").GetProperty("hook").GetInt32(), "a hook named from source is addressable by name");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [TestMethod]
    public async Task GetState_AfterALateAttachLeavesHooksUnnamedWhenTheSourceIsAmbiguous()
    {
        var path = WriteSource(LoginSource.Replace("var (count, setCount) = UseState(3);", "if (Props.Ready) { var (count, setCount) = UseState(3); }",
            StringComparison.Ordinal));
        try
        {
            using var agent = new FakeDevToolsProtocolAgent().Answer("Reactor.component", LateAttachLogin(path));
            var (exit, output) = await Run(new DevToolsGetStateCommand(), agent, ["42"]);
            Assert.AreEqual(0, exit, output);
            StringAssert.Contains(output, "[0] state string = \"hook-pass-1\" settable", "an unproven mapping names nothing");
            Assert.IsFalse(output.Contains("read from", StringComparison.Ordinal), output);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [TestMethod]
    public async Task Rerender_SaysWhatARenderDoesNotUndo()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("Reactor.rerender", """{"state":"ok","component":"Counter","rendered":true,"api":"reactor"}""");
        var (exit, output) = await Run(new DevToolsRerenderCommand(), agent, ["42"]);
        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(output, "<Counter> rendered again.");
        StringAssert.Contains(output, "keeps its edit until its C# value changes");
    }

    [TestMethod]
    public async Task ARowItsListRecycled_IsRefused_AndAParkedRowIsNotReportedAsShown()
    {
        const string recycled = """{"state":"refused","code":"unmounted","reason":"this element was in a list row that its list has since recycled, so the component that rendered it is gone; find the row again with `winapp devtools search` and use its new handle"}""";
        using (var agent = new FakeDevToolsProtocolAgent().Answer("Reactor.component", recycled))
        {
            var (exit, output) = await Run(new DevToolsGetStateCommand(), agent, ["42", "--json"]);
            Assert.AreEqual(1, exit);
            var error = JsonDocument.Parse(output).RootElement.GetProperty("error");
            Assert.AreEqual("unmounted", error.GetProperty("token").GetString());
            StringAssert.StartsWith(error.GetProperty("message").GetString(), "This element was in a list row");
        }
        using (var agent = new FakeDevToolsProtocolAgent().Answer("Reactor.rerender", recycled))
        {
            var (exit, output) = await Run(new DevToolsRerenderCommand(), agent, ["42", "--json"]);
            Assert.AreEqual(1, exit);
            Assert.AreEqual("unmounted", JsonDocument.Parse(output).RootElement.GetProperty("error").GetProperty("token").GetString());
        }
        using (var agent = new FakeDevToolsProtocolAgent().Answer("Reactor.component", Counter.Replace("\"exact\":true", "\"exact\":true,\"parked\":true")))
        {
            var (exit, output) = await Run(new DevToolsGetStateCommand(), agent, ["42"]);
            Assert.AreEqual(0, exit, output);
            StringAssert.Contains(output, "Not on screen: its list parked this row for reuse");
        }
        using (var agent = new FakeDevToolsProtocolAgent().Answer("Reactor.rerender", """{"state":"ok","component":"VRow","rendered":false,"parked":true,"api":"reactor"}"""))
        {
            var (exit, output) = await Run(new DevToolsRerenderCommand(), agent, ["42"]);
            Assert.AreEqual(0, exit, output);
            StringAssert.Contains(output, "<VRow> rendered again, off screen.");
            Assert.IsFalse(output.Contains("still rendering", StringComparison.Ordinal), "parked is not a render in progress");
        }
        using (var agent = new FakeDevToolsProtocolAgent().Answer("Reactor.setState", """
            {"state":"ok","component":"VRow","index":0,"kind":"state","type":"int","requested":"5","before":"0","after":"5","changed":true,"waitedForIdle":true,"rendered":false,"parked":true,"api":"reactor"}
            """))
        {
            var (exit, output) = await Run(new DevToolsSetStateCommand(), agent, ["42", "0", "5"]);
            Assert.AreEqual(0, exit, output);
            StringAssert.Contains(output, "Not on screen");
            Assert.IsFalse(output.Contains("still rendering", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task InvokeHandler_SendsTheEventAndValueAndReportsAThrow()
    {
        using (var agent = new FakeDevToolsProtocolAgent().Answer("Reactor.invokeHandler",
            """{"state":"ok","event":"OnClick","method":"lambda in Counter.Render","rendered":true}"""))
        {
            var (exit, output) = await Run(new DevToolsInvokeHandlerCommand(), agent, ["42", "Click"]);
            Assert.AreEqual(0, exit, output);
            StringAssert.Contains(output, "ran OnClick → lambda in Counter.Render");
            Assert.AreEqual("Click", Params(agent, "Reactor.invokeHandler").GetProperty("event").GetString());
            Assert.IsFalse(Params(agent, "Reactor.invokeHandler").TryGetProperty("value", out _));
        }
        using (var agent = new FakeDevToolsProtocolAgent().Answer("Reactor.invokeHandler",
            """{"state":"threw","event":"OnChanged","method":"lambda in App.Render","exception":"System.InvalidOperationException","message":"boom"}"""))
        {
            var (exit, output) = await Run(new DevToolsInvokeHandlerCommand(), agent, ["42", "OnChanged", "--value", "hi", "--json"]);
            Assert.AreEqual(1, exit);
            using var doc = JsonDocument.Parse(output);
            Assert.AreEqual("handler-threw", doc.RootElement.GetProperty("error").GetProperty("token").GetString());
            Assert.AreEqual("System.InvalidOperationException", doc.RootElement.GetProperty("exception").GetString());
            Assert.AreEqual("hi", Params(agent, "Reactor.invokeHandler").GetProperty("value").GetString());
        }
    }

    private const string WindowTree = """
        [{"handle":"329","name":"","type":"Microsoft.UI.Xaml.Hosting.DesktopWindowXamlSource","childCount":2,"children":[
          {"handle":"401","name":"","type":"Microsoft.UI.Xaml.PopupRoot","childCount":0,"children":[]},
          {"handle":"402","name":"","type":"Microsoft.UI.Xaml.Internal.RootScrollViewer","childCount":0,"children":[]}]}]
        """;

    [TestMethod]
    public async Task GetResource_StartsFromTheWindowContentAndFailsWhenNotFound()
    {
        using (var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", WindowTree)
            .Answer("Resource.get", """{"state":"ok","key":"AccentFillColorDefaultBrush","theme":"Light","found":true,"scope":"application","dictionary":"Application.Resources.MergedDictionaries[0].ThemeDictionaries[Light]","value":"SolidColorBrush #FF0067C0","valueType":"Microsoft.UI.Xaml.Media.SolidColorBrush"}"""))
        {
            var (exit, output) = await Run(new DevToolsGetResourceCommand(), agent, ["AccentFillColorDefaultBrush"]);
            Assert.AreEqual(0, exit, output);
            StringAssert.Contains(output, "AccentFillColorDefaultBrush = SolidColorBrush #FF0067C0");
            StringAssert.Contains(output, "ThemeDictionaries[Light]");
            var request = Params(agent, "Resource.get");
            Assert.AreEqual("402", request.GetProperty("handle").GetString(), "the window's content, not its XAML source or popup root");
            Assert.IsFalse(request.TryGetProperty("fromElement", out _));
        }
        using (var agent = new FakeDevToolsProtocolAgent()
            .Answer("Resource.get", """{"state":"ok","key":"Missing","theme":"Dark","found":false}"""))
        {
            var (exit, output) = await Run(new DevToolsGetResourceCommand(), agent, ["Missing", "42", "--json"]);
            Assert.AreEqual(1, exit);
            Assert.AreEqual("no-resource", JsonDocument.Parse(output).RootElement.GetProperty("error").GetProperty("token").GetString());
            Assert.IsTrue(Params(agent, "Resource.get").GetProperty("fromElement").GetBoolean());
        }
    }

    [TestMethod]
    public async Task SetResource_DefaultsToAppScopeWithoutASelectorAndElementScopeWithOne()
    {
        const string Answer = """{"state":"ok","key":"K","scope":"app","theme":"Light","requested":"#FFC42B1C","written":"Application.Resources","before":"SolidColorBrush #FF0067C0","after":"SolidColorBrush #FFC42B1C","valueType":"x","added":false,"refreshedRoots":1}""";
        using (var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.enumerate", WindowTree).Answer("Resource.set", Answer))
        {
            var (exit, output) = await Run(new DevToolsSetResourceCommand(), agent, ["K", "#FFC42B1C"]);
            Assert.AreEqual(0, exit, output);
            StringAssert.Contains(output, "K: SolidColorBrush #FF0067C0 -> SolidColorBrush #FFC42B1C");
            Assert.AreEqual("app", Params(agent, "Resource.set").GetProperty("scope").GetString());
        }
        using (var agent = new FakeDevToolsProtocolAgent().Answer("Resource.set", Answer))
        {
            var (exit, _) = await Run(new DevToolsSetResourceCommand(), agent, ["K", "#FFC42B1C", "42"]);
            Assert.AreEqual(0, exit);
            Assert.AreEqual("element", Params(agent, "Resource.set").GetProperty("scope").GetString());
        }
        using (var agent = new FakeDevToolsProtocolAgent())
        {
            var (exit, output) = await Run(new DevToolsSetResourceCommand(), agent, ["K", "#FFC42B1C", "--scope", "element"]);
            Assert.AreEqual(1, exit);
            StringAssert.Contains(output, "--scope element needs the element");
            Assert.AreEqual(0, agent.Received.Count);
        }
    }

    [TestMethod]
    public async Task SetResource_AValueTheAppSetsBackIsAFailureWithTheLiveValue()
    {
        // The Border's Resources are filled by a Reactor .Set(...) that runs again on the refresh's re-render.
        const string Answer = """{"state":"ok","key":"Rx4Plain","scope":"element","theme":"Light","requested":"elem value 2","written":"Resources","before":"scoped plain","after":"scoped plain","valueType":"System.String","added":false,"refreshedRoots":1,"settled":true,"reverted":true,"setBy":{"file":"C:\\src\\Panels.cs","line":42}}""";
        using (var agent = new FakeDevToolsProtocolAgent().Answer("Resource.set", Answer))
        {
            var (exit, output) = await Run(new DevToolsSetResourceCommand(), agent, ["Rx4Plain", "elem value 2", "42", "--json"]);
            Assert.AreEqual(1, exit, output);
            using var doc = JsonDocument.Parse(output);
            Assert.IsFalse(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.AreEqual("scoped plain", doc.RootElement.GetProperty("after").GetString(), "JSON reports the live value, not the one written");
            Assert.IsTrue(doc.RootElement.GetProperty("reverted").GetBoolean());
            var error = doc.RootElement.GetProperty("error");
            Assert.AreEqual("resource-reverted", error.GetProperty("token").GetString());
            StringAssert.Contains(error.GetProperty("message").GetString(), "Panels.cs:42");
        }
        using (var agent = new FakeDevToolsProtocolAgent().Answer("Resource.set", Answer))
        {
            var (exit, output) = await Run(new DevToolsSetResourceCommand(), agent, ["Rx4Plain", "elem value 2", "42"]);
            Assert.AreEqual(1, exit, output);
            StringAssert.Contains(output, "set it back to scoped plain");
            StringAssert.Contains(output, "won't stick");
            Assert.IsFalse(output.Contains("-> elem value 2", StringComparison.Ordinal), output);
        }
    }

    [TestMethod]
    public async Task SetResource_AnAppResourcesDictionaryThatTakesNoKeysIsASpecificRefusal()
    {
        // A Reactor app's Application.Resources is a XamlControlsResources; when even a merged dictionary cannot be added,
        // the agent refuses with its own token and the next step, not a raw COM error reported as "reactor-unavailable".
        var refusal = """{"state":"refused","code":"resource-read-only","reason":"Application.Resources is loaded from a Source (such as XamlControlsResources), so it cannot take this key. Set it on an element with --scope element <selector>, or add a ResourceDictionary to Application.Resources.MergedDictionaries in the app"}""";
        using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.enumerate", WindowTree).Answer("Resource.set", refusal);
        var (exit, output) = await Run(new DevToolsSetResourceCommand(), agent, ["NewKey", "x", "--scope", "app", "--json"]);

        Assert.AreEqual(1, exit, output);
        var error = JsonDocument.Parse(output).RootElement.GetProperty("error");
        Assert.AreEqual("resource-read-only", error.GetProperty("token").GetString());
        StringAssert.Contains(error.GetProperty("message").GetString(), "--scope element");
        Assert.IsFalse(output.Contains("COMException", StringComparison.Ordinal), output);
    }

    private const string ElementWithText = """
        {"state":"ok","tagged":true,"element":"TextBlock","file":"C:\\src\\App.cs","line":75,"owner":"Counter",
         "props":[{"name":"Content","property":"Text","value":"Done today: 3"}],"modifiers":[],
         "themeBindings":[{"property":"Foreground","resourceKey":"AccentTextFillColorPrimaryBrush"}],
         "handlers":[{"event":"OnClick","method":"lambda in Counter.Render","signature":"()","invokable":true,"file":"C:\\src\\App.cs","line":76}]}
        """;

    [TestMethod]
    public async Task DiagnoseBinding_AnswersWhyAReactorElementShowsItsValue()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("Binding.diagnose", """{"state":"none"}""")
            .Answer("Reactor.element", ElementWithText)
            .Answer("Property.get", """{"handle":"42","authoredState":"noSourceInfo","props":[{"name":"Text","value":"Done today: 3","valueType":"String","valueSource":"Local","editKind":"text"}]}""")
            .Answer("Reactor.component", Counter);
        var (exit, output) = await Run(new DevToolsDiagnoseBindingCommand(), agent, ["42", "Text", "--json"]);

        Assert.AreEqual(0, exit, output);
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.AreEqual("reactor", root.GetProperty("kind").GetString());
        Assert.AreEqual("Done today: 3", root.GetProperty("value").GetString());
        Assert.AreEqual("element", root.GetProperty("reactor").GetProperty("from").GetString());
        Assert.AreEqual("Counter", root.GetProperty("reactor").GetProperty("component").GetString());
        var sources = root.GetProperty("likelySources").EnumerateArray().Select(s => s.GetProperty("name").GetString()).ToArray();
        CollectionAssert.Contains(sources, "Label");
        CollectionAssert.Contains(sources, "[0] state");
        CollectionAssert.DoesNotContain(sources, "Density");
    }

    [TestMethod]
    public async Task DiagnoseBinding_HumanChainShowsRecordCallSiteAndComponent()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("Binding.diagnose", """{"state":"none"}""")
            .Answer("Reactor.element", ElementWithText)
            .Answer("Property.get", """{"handle":"42","authoredState":"noSourceInfo","props":[{"name":"Foreground","value":"#FF003E92","valueType":"SolidColorBrush","valueSource":"Style","editKind":"color"}]}""")
            .Answer("Reactor.component", Counter);
        var (exit, output) = await Run(new DevToolsDiagnoseBindingCommand(), agent, ["42", "Foreground"]);

        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(output, "kind: reactor");
        StringAssert.Contains(output, "← ThemeRef AccentTextFillColorPrimaryBrush");
        StringAssert.Contains(output, "← created at App.cs:75 in <Counter>");
        StringAssert.Contains(output, "<Counter> as it last rendered");
    }

    [TestMethod]
    public async Task DiagnoseBinding_KeepsTheXamlAnswerWhenReactorDoesNotSetTheProperty()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("Binding.diagnose", """{"state":"none"}""")
            .Answer("Reactor.element", ElementWithText);
        var (exit, output) = await Run(new DevToolsDiagnoseBindingCommand(), agent, ["42", "Width"]);
        Assert.AreEqual(0, exit, output);
        Assert.DoesNotContain("kind: reactor", output);
        StringAssert.Contains(output, "No binding");
    }

    [TestMethod]
    public async Task GetProperty_MarksThemeRefsAndListsHandlers()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("Property.get", """{"handle":"42","authoredState":"noSourceInfo","props":[{"name":"Foreground","value":"#FF003E92","valueType":"SolidColorBrush","valueSource":"Style","editKind":"color","origin":"Style"}]}""")
            .Answer("Reactor.element", ElementWithText);
        var (exit, output) = await Run(new DevToolsGetPropertyCommand(), agent, ["42"]);
        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(output, "Foreground: #FF003E92 [Style] [← ThemeRef AccentTextFillColorPrimaryBrush]");
        StringAssert.Contains(output, "OnClick → lambda in Counter.Render App.cs:76");

        var (_, json) = await Run(new DevToolsGetPropertyCommand(), agent, ["42", "--json"]);
        using var doc = JsonDocument.Parse(json);
        Assert.AreEqual("AccentTextFillColorPrimaryBrush", doc.RootElement.GetProperty("properties")[0].GetProperty("themeResource").GetString());
        Assert.AreEqual("OnClick", doc.RootElement.GetProperty("reactor").GetProperty("handlers")[0].GetProperty("event").GetString());
    }

    private const string DoneToday = """
        {"state":"ok","version":"0.1.0","api":"internal","exact":true,"idle":true,
         "component":{"kind":"class","name":"DoneToday","type":"DoneToday","isRoot":false,"mounted":true},
         "props":{"type":"DoneTodayProps","values":[{"name":"Id","value":"13"}]},
         "hooks":[
           {"index":0,"kind":"state","type":"int","value":"1","name":"count","canSet":true,"editable":true},
           {"index":1,"kind":"state","type":"int","value":"3","name":"total","canSet":true,"editable":true}],
         "contexts":[]}
        """;

    [TestMethod]
    [DataRow("Done today: 13", "Id")]
    [DataRow("1 of 3", "count,total")]
    public async Task DiagnoseBinding_LikelySources_AreWholeValuesOnly_AndNamedHooksByName(string shown, string expected)
    {
        // <DoneToday Id=13> with hooks count=1 and total=3: "Done today: 13" shows the Id, not the 1 or the 3 inside it.
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("Binding.diagnose", """{"state":"none"}""")
            .Answer("Reactor.element", ElementWithText.Replace("Done today: 3", shown, StringComparison.Ordinal))
            .Answer("Property.get", """{"handle":"42","authoredState":"noSourceInfo","props":[{"name":"Text","value":"SHOWN","valueType":"String","valueSource":"Local","editKind":"text"}]}""".Replace("SHOWN", shown, StringComparison.Ordinal))
            .Answer("Reactor.component", DoneToday);
        var (exit, output) = await Run(new DevToolsDiagnoseBindingCommand(), agent, ["42", "Text", "--json"]);

        Assert.AreEqual(0, exit, output);
        using var doc = JsonDocument.Parse(output);
        var sources = doc.RootElement.GetProperty("likelySources").EnumerateArray().Select(s => s.GetProperty("name").GetString()).ToArray();
        CollectionAssert.AreEqual(expected.Split(','), sources);
    }

    [TestMethod]
    public async Task GetProperty_Json_SaysWhichHandlersNeedAValue()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("Property.get", """{"handle":"42","authoredState":"noSourceInfo","props":[]}""")
            .Answer("Reactor.element", ElementWithText.Replace(
                "\"line\":76}]",
                "\"line\":76},{\"event\":\"OnChanged\",\"method\":\"lambda in Counter.Render\",\"signature\":\"(string)\",\"invokable\":false,\"needsValue\":true}]",
                StringComparison.Ordinal));
        var (exit, json) = await Run(new DevToolsGetPropertyCommand(), agent, ["42", "--json"]);

        Assert.AreEqual(0, exit, json);
        using var doc = JsonDocument.Parse(json);
        var handlers = doc.RootElement.GetProperty("reactor").GetProperty("handlers");
        Assert.AreEqual(2, handlers.GetArrayLength());
        Assert.IsFalse(handlers[0].TryGetProperty("needsValue", out _));
        Assert.IsFalse(handlers[1].GetProperty("invokable").GetBoolean());
        Assert.IsTrue(handlers[1].GetProperty("needsValue").GetBoolean(), "invoke-handler --value runs it");
    }

    [TestMethod]
    public void Feeds_MatchesTextInsideTheShownValueOnly()
    {
        Assert.IsTrue(ReactorComponentState.Feeds("\"Ada\"", "Hello Ada"));
        Assert.IsTrue(ReactorComponentState.Feeds("3", "Done today: 3"));
        Assert.IsTrue(ReactorComponentState.Feeds("13", "Done today: 13"));
        Assert.IsFalse(ReactorComponentState.Feeds("1", "Done today: 13"), "part of a number is not the number");
        Assert.IsFalse(ReactorComponentState.Feeds("3", "Done today: 13"));
        Assert.IsFalse(ReactorComponentState.Feeds("3", "Version 1.3"));
        Assert.IsFalse(ReactorComponentState.Feeds("\"Ad\"", "Hello Ada"), "part of a word is not the word");
        Assert.IsFalse(ReactorComponentState.Feeds("\"-\"", "Mon - Fri"), "punctuation alone says nothing");
        Assert.IsFalse(ReactorComponentState.Feeds("True", "True"), "booleans match too much to mean anything");
        Assert.IsFalse(ReactorComponentState.Feeds("\"\"", "anything"));
        Assert.IsFalse(ReactorComponentState.Feeds("4", null));
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Testing;
using WinApp.Cli.Commands;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

/// <summary>
/// Covers the pieces of the agent-facing <c>winapp devtools</c> surface that are decidable without a running
/// app: the option contract every live command shares, how <c>devtools call</c> types its shell-friendly
/// parameters, and how a DevTools failure is turned into something actionable. The live behaviour is proven
/// against a real WinUI fixture by the DevTools E2E scripts, not here.
/// </summary>
[TestClass]
[DoNotParallelize]
public class DevToolsAgentCommandTests : BaseCommandTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void HumanLines_PreserveTerminalRenderingAndDoNotWrapPipes(bool terminal)
    {
        using var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new TestOutput(output, terminal),
            Ansi = AnsiSupport.No,
        });
        var text = new string('x', 180) + "[literal]";
        var markup = $"[green]{Markup.Escape(text)}[/]";
        DevToolsRender.WriteMarkupLine(console, markup);
        if (terminal)
        {
            using var baseline = new StringWriter();
            var unchanged = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Out = new TestOutput(baseline, true),
                Ansi = AnsiSupport.No,
            });
            unchanged.MarkupLine(markup);
            Assert.AreEqual(baseline.ToString(), output.ToString());
        }
        else { Assert.AreEqual(text + Environment.NewLine, output.ToString()); }
        Assert.AreEqual(80, console.Profile.Width);
    }

    [TestMethod]
    public void PipedHumanMarkup_RemovesControlsWithoutInterpretingLiteralMarkup()
    {
        var console = new TestConsole();
        DevToolsRender.WriteMarkupLine(console,
            $"[green]{Markup.Escape("safe\u001b[31mred\u001b[0m[grey]\r\nnext\tend")}[/]");
        Assert.AreEqual("safered[grey]\u21b5next\u2192end" + Environment.NewLine, console.Output);
    }

    private sealed class TestOutput(TextWriter writer, bool terminal) : IAnsiConsoleOutput
    {
        public TextWriter Writer => writer;
        public bool IsTerminal => terminal;
        public int Width => 80;
        public int Height => 25;
        public void SetEncoding(System.Text.Encoding encoding) { }
    }

    [TestMethod]
    [DataRow("query:=\"Save\"", """{"query":"Save"}""")]
    [DataRow("handles:=[\n\"1\",\n\"2\"\n]", """{"handles":["1","2"]}""")]
    [DataRow("n:=123456789012345678901234567890.123456789", """{"n":123456789012345678901234567890.123456789}""")]
    public void Correction_RawJson_WritesSemanticSingleLineValue(string argument, string expected)
    {
        var parsed = DevToolsCallParams.Parse([argument]);
        Assert.IsTrue(parsed.Ok, parsed.Error);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in parsed.Values)
            {
                value.Write(writer, name);
            }
            writer.WriteEndObject();
        }
        Assert.AreEqual(expected, System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    [TestMethod]
    [DataRow("query:=\"Save\"", "\"Save\"")]
    [DataRow("query=01", "\"01\"")]
    [DataRow("query:=\"line\\nnext\"", "\"line\\nnext\"")]
    [DataRow("query:={\r\n\"rows\":[1,\n{\"key\":true}]}", "{\"rows\":[1,{\"key\":true}]}")]
    [DataRow("query:=123456789012345678901234567890.123456789", "123456789012345678901234567890.123456789")]
    [DataRow("query:=1.234567890123456789e+123", "1.234567890123456789e+123")]
    public async Task Correction_RawJson_ActualCallUsesOneSemanticFrame(string argument, string expected)
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.find", "{}");
        var handler = new DevToolsCallCommand.Handler(agent.Resolver(), new TestConsole());
        Assert.AreEqual(0, await handler.InvokeAsync(new DevToolsCallCommand().Parse(["VisualTree.find", argument, "--json"])));
        CollectionAssert.AreEqual(new List<string> { "DevTools.negotiate", "VisualTree.find" }, agent.Received);
        var request = agent.ReceivedRequests.Last();
        Assert.IsFalse(request.Contains('\n') || request.Contains('\r'));
        using var doc = JsonDocument.Parse(request);
        Assert.AreEqual(expected, doc.RootElement.GetProperty("params").GetProperty("query").GetRawText());
    }

    /// <summary>
    /// every live command targets with the SAME <c>-a</c>/<c>-w</c> as <c>winapp ui</c>. If one drifts, an
    /// agent that learned the pattern on one command silently mis-targets on another.
    /// </summary>
    [TestMethod]
    public void EveryLiveCommand_SharesUiTargetingAndJson()
    {
        foreach (var command in LiveCommands())
        {
            var names = command.Options.SelectMany(o => o.Aliases.Append(o.Name)).ToArray();
            CollectionAssert.Contains(names, "--app", $"{command.Name} must accept --app.");
            CollectionAssert.Contains(names, "-a", $"{command.Name} must accept -a.");
            CollectionAssert.Contains(names, "--window", $"{command.Name} must accept --window.");
            CollectionAssert.Contains(names, "-w", $"{command.Name} must accept -w.");
            CollectionAssert.Contains(names, "--json", $"{command.Name} must accept --json.");
            CollectionAssert.Contains(names, "--attach", $"{command.Name} must require explicit injection consent.");
            CollectionAssert.Contains(names, "--root", $"{command.Name} must accept an exact visual subtree.");
            Assert.IsFalse(command.Parse([]).GetValue(SharedDevToolsOptions.AttachOption));
            Assert.IsTrue(command.Parse(["--attach"]).GetValue(SharedDevToolsOptions.AttachOption));
        }
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-1")]
    [DataRow("SharedLabel")]
    [DataRow("18446744073709551616")]
    public async Task InvalidRoot_RefusesBeforeTargetResolutionOrAttachment(string root)
    {
        using var agent = new FakeDevToolsProtocolAgent();
        var resolver = agent.Resolver();
        var console = new TestConsole();
        var handler = new DevToolsCallCommand.Handler(resolver, console);
        Assert.AreEqual(1, await handler.InvokeAsync(
            new DevToolsCallCommand().Parse(["DevTools.ping", "--root", root, "--attach", "--json"])));
        Assert.IsNull(resolver.LastAttachIfNeeded);
        Assert.IsEmpty(agent.Received);
        StringAssert.Contains(console.Output, "bad-root");
    }

    [TestMethod]
    public async Task Call_RootScopeReachesNegotiationAndExecutionAndCannotBeOverridden()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("DevTools.ping", "{}");
        var console = new TestConsole();
        var handler = new DevToolsCallCommand.Handler(agent.Resolver(), console);
        var command = new DevToolsCallCommand();
        Assert.AreEqual(0, await handler.InvokeAsync(command.Parse(["DevTools.ping", "--root", "9001"])));
        foreach (var request in agent.ReceivedRequests)
        {
            using var json = JsonDocument.Parse(request);
            Assert.AreEqual("9001", json.RootElement.GetProperty("params").GetProperty("root").GetString());
        }
        var executions = agent.Received.Count(method => method == "DevTools.ping");
        Assert.AreEqual(1, await handler.InvokeAsync(command.Parse(["DevTools.ping", "root=9002", "--root", "9001"])));
        Assert.AreEqual(executions, agent.Received.Count(method => method == "DevTools.ping"));
    }

    /// <summary>No target means "the only attached app".</summary>
    [TestMethod]
    public void EveryLiveCommand_ParsesWithNoTarget()
    {
        foreach (var command in LiveCommands())
        {
            var parse = command.Parse([]);
            Assert.AreEqual(0, parse.Errors.Count,
                $"{command.Name} must parse with no target: {string.Join("; ", parse.Errors.Select(e => e.Message))}");
        }
    }

    [TestMethod]
    public void Inspect_DefaultsToDepthFour()
    {
        var command = GetRequiredService<DevToolsInspectCommand>();

        Assert.AreEqual(4, command.Parse([]).GetRequiredValue(DevToolsInspectCommand.DepthOption));
        Assert.AreEqual(8, command.Parse(["--depth", "8"]).GetRequiredValue(DevToolsInspectCommand.DepthOption));
        Assert.AreEqual(8, command.Parse(["-d", "8"]).GetRequiredValue(DevToolsInspectCommand.DepthOption));
    }

    [TestMethod]
    public void Inspect_TakesSelectorRootAndFilters()
    {
        var command = GetRequiredService<DevToolsInspectCommand>();

        var parse = command.Parse(["12345", "--all", "--filter", "Button", "--ancestors"]);

        Assert.AreEqual(0, parse.Errors.Count);
        Assert.AreEqual("12345", parse.GetValue(DevToolsInspectCommand.SelectorArgument));
        Assert.IsTrue(parse.GetValue(SharedDevToolsOptions.AllOption));
        Assert.AreEqual("Button", parse.GetValue(SharedDevToolsOptions.FilterOption));
        Assert.IsTrue(parse.GetValue(DevToolsInspectCommand.AncestorsOption));
    }

    /// <summary>
    /// The app's own XAML is the DEFAULT view, not an opt-in. A WinUI control template inserts 12-14 levels
    /// between the window and an ordinary Button, so a raw default listing showed framework plumbing at every
    /// depth a person would actually type. <c>--all</c> is the single opt-out — there is no longer a positive
    /// <c>--app-authored</c> flag, and it is asserted GONE rather than merely unused, because an option left
    /// registered as a no-op is the worst of both: it reads as supported and changes nothing.
    /// </summary>
    [TestMethod]
    public void Inspect_ShowsTheAppsOwnXamlByDefault()
    {
        var command = GetRequiredService<DevToolsInspectCommand>();

        Assert.IsFalse(command.Parse([]).GetValue(SharedDevToolsOptions.AllOption),
            "The default view is the app's own XAML, so --all is off.");
        Assert.IsTrue(command.Parse(["--all"]).GetValue(SharedDevToolsOptions.AllOption));
        Assert.IsFalse(
            command.Options.Any(o => o.Name == "--app-authored" || o.Aliases.Contains("--app-authored")),
            "--app-authored is gone; --all is the one canonical scope flag.");
    }

    /// <summary>Search speaks the same scope vocabulary, so one flag means one thing across the surface.</summary>
    [TestMethod]
    public void Search_SearchesTheAppsOwnXamlByDefault()
    {
        var command = GetRequiredService<DevToolsSearchCommand>();

        Assert.IsFalse(command.Parse(["Button"]).GetValue(SharedDevToolsOptions.AllOption));
        Assert.IsTrue(command.Parse(["Button", "--all"]).GetValue(SharedDevToolsOptions.AllOption));
        Assert.IsFalse(
            command.Options.Any(o => o.Name == "--app-authored" || o.Aliases.Contains("--app-authored")),
            "--app-authored is gone; --all is the one canonical scope flag.");
    }

    [TestMethod]
    public void Call_TakesAMethodAndVariadicNameValuePairs()
    {
        var command = GetRequiredService<DevToolsCallCommand>();

        var parse = command.Parse(["Layout.get", "handle=12345", "prop=Width"]);

        Assert.AreEqual(0, parse.Errors.Count);
        Assert.AreEqual("Layout.get", parse.GetValue(DevToolsCallCommand.MethodArgument));
        CollectionAssert.AreEqual(
            ExpectedCallParams,
            parse.GetValue(DevToolsCallCommand.ParamsArgument));
    }

    [TestMethod]
    public void Call_DoesNotInjectUnlessAttachIsAskedFor()
    {
        // `call` is the arbitrary-verb surface, so "connect to what I named" must not also mean "load a DLL
        // into it". The handler's opt-in has to track the flag, or the resolver's refusal is unreachable.
        var command = GetRequiredService<DevToolsCallCommand>();

        Assert.IsFalse(command.Parse(["DevTools.ping"]).GetValue(SharedDevToolsOptions.AttachOption),
            "Injection must be off by default.");
        Assert.IsTrue(command.Parse(["DevTools.ping", "--attach"]).GetValue(SharedDevToolsOptions.AttachOption));
        Assert.AreEqual(0, command.Parse(["DevTools.ping", "--attach"]).Errors.Count);
    }

    [TestMethod]
    public void Call_PassesTheAttachDecisionToTheResolver()
    {
        using var agent = new FakeDevToolsProtocolAgent();
        agent.Answer("DevTools.ping", """{"ok":true}""");
        var resolver = agent.Resolver();
        var handler = new DevToolsCallCommand.Handler(resolver, new TestConsole());
        var command = GetRequiredService<DevToolsCallCommand>();

        Assert.AreEqual(0, handler.InvokeAsync(command.Parse(["DevTools.ping"])).GetAwaiter().GetResult());
        Assert.AreEqual(false, resolver.LastAttachIfNeeded, "Without --attach the resolver must be told not to inject.");

        Assert.AreEqual(0, handler.InvokeAsync(command.Parse(["DevTools.ping", "--attach"])).GetAwaiter().GetResult());
        Assert.AreEqual(true, resolver.LastAttachIfNeeded, "--attach must reach the resolver.");
    }

    private static readonly string[] ExpectedCallParams = ["handle=12345", "prop=Width"];

    [TestMethod]
    public async Task Call_RefusesAdvertisedInternalMethodBeforeDispatch()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("Internal.sourceRoot", """{"sourceRoot":"owned"}""");
        var console = new TestConsole();
        var command = new DevToolsCallCommand();
        var handler = new DevToolsCallCommand.Handler(agent.Resolver(), console);
        Assert.AreEqual(1, await handler.InvokeAsync(command.Parse(["Internal.sourceRoot", "--json"])));
        CollectionAssert.AreEqual(new List<string> { "DevTools.negotiate" }, agent.Received);
        using var json = JsonDocument.Parse(console.Output);
        Assert.AreEqual("unknown-method", json.RootElement.GetProperty("error").GetProperty("token").GetString());
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Call_PreservesCodeAndTokenThroughNegotiationOrParameterHint(bool negotiation)
    {
        using var agent = new FakeDevToolsProtocolAgent().Error(
            negotiation ? "DevTools.negotiate" : "Example.bad", -32004, negotiation ? "unauthorized" : "bad-args", "controlled refusal");
        var console = new TestConsole();
        var command = new DevToolsCallCommand();
        var handler = new DevToolsCallCommand.Handler(agent.Resolver(), console);
        Assert.AreEqual(1, await handler.InvokeAsync(command.Parse(["Example.bad", "value=text", "--json"])));
        using var json = JsonDocument.Parse(console.Output);
        var error = json.RootElement.GetProperty("error");
        Assert.AreEqual(-32004, error.GetProperty("code").GetInt32());
        Assert.AreEqual(negotiation ? "unauthorized" : "bad-args", error.GetProperty("token").GetString());
        StringAssert.Contains(error.GetProperty("message").GetString()!, "controlled refusal");
    }

    [TestMethod]
    [DataRow("handle=SaveButton", "\"SaveButton\"")]
    [DataRow("handle:=\"SaveButton\"", "\"SaveButton\"")]
    [DataRow("handle:=123", "123")]
    public async Task Call_ExplainsRawHandleWithoutRewritingSelector(string argument, string expectedWireValue)
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Error("Binding.walk", -32602, "bad-args", "handle must be a canonical non-zero decimal string");
        var console = new TestConsole();
        var handler = new DevToolsCallCommand.Handler(agent.Resolver(), console);
        Assert.AreEqual(1, await handler.InvokeAsync(
            new DevToolsCallCommand().Parse(["Binding.walk", argument, "prop=Text", "--json"])));
        using var result = JsonDocument.Parse(console.Output);
        var error = result.RootElement.GetProperty("error");
        Assert.AreEqual(-32602, error.GetProperty("code").GetInt32());
        Assert.AreEqual("bad-args", error.GetProperty("token").GetString());
        StringAssert.Contains(error.GetProperty("message").GetString()!, "numeric handle string");
        using var request = JsonDocument.Parse(agent.ReceivedRequests.Last());
        Assert.AreEqual(expectedWireValue, request.RootElement.GetProperty("params").GetProperty("handle").GetRawText());
        CollectionAssert.AreEqual(new List<string> { "DevTools.negotiate", "Binding.walk" }, agent.Received);
    }

    [TestMethod]
    public async Task Call_PreservesWideNumericHandleAsAString()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("Binding.walk", "{}");
        var handler = new DevToolsCallCommand.Handler(agent.Resolver(), new TestConsole());
        Assert.AreEqual(0, await handler.InvokeAsync(
            new DevToolsCallCommand().Parse(["Binding.walk", "handle=9007199254740993", "prop=Text", "--json"])));
        using var request = JsonDocument.Parse(agent.ReceivedRequests.Last());
        Assert.AreEqual("9007199254740993", request.RootElement.GetProperty("params").GetProperty("handle").GetString());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Call_LongPipedValuesDoNotWrap_AndJsonKeepsOriginalControls(bool json)
    {
        var value = new string('x', 180) + "[literal]\u001b[31mred\u001b[0m";
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("Example.read", JsonSerializer.Serialize(new { value }));
        var console = new TestConsole();
        console.Profile.Width = 80;
        var handler = new DevToolsCallCommand.Handler(agent.Resolver(), console);
        Assert.AreEqual(0, await handler.InvokeAsync(
            new DevToolsCallCommand().Parse(json ? ["Example.read", "--json"] : ["Example.read"])));
        if (json)
        {
            using var result = JsonDocument.Parse(console.Output);
            Assert.AreEqual(value, result.RootElement.GetProperty("result").GetProperty("value").GetString());
        }
        else
        {
            Assert.AreEqual("value: " + Helpers.TerminalText.Sanitize(value) + Environment.NewLine, console.Output);
        }
    }

    /// <summary>
    /// <c>name=value</c> is always a string, so a value that LOOKS like a boolean or a number is still
    /// sendable as text. Value-based typing made <c>call VisualTree.find query=true</c> — a legitimate search
    /// for the literal text "true" — impossible to express, because the native dispatch requires a JSON string
    /// and no shell quoting survives argument parsing.
    /// </summary>
    [TestMethod]
    public void CallParams_EqualsAlwaysSendsAString()
    {
        var parsed = DevToolsCallParams.Parse(
            ["query=true", "b=false", "c=null", "d=200", "e=1.5", "handle=281474976710658", "f=Save changes", "g="]);

        Assert.IsTrue(parsed.Ok);
        foreach (var (name, value) in parsed.Values)
        {
            Assert.AreEqual(JsonValueKind.String, value.Kind, $"'{name}' used '=', so it must be a JSON string.");
        }

        Assert.AreEqual("true", parsed.Values[0].Value.Text);
        Assert.AreEqual("281474976710658", parsed.Values[5].Value.Text,
            "A handle is an opaque decimal string; '=' makes that true by construction rather than by special case.");
    }

    /// <summary><c>:=</c> sends a boolean, a number, or null.</summary>
    [TestMethod]
    public void CallParams_ColonEqualsSendsRawJson()
    {
        var parsed = DevToolsCallParams.Parse(
            ["a:=true", "b:=false", "c:=null", "d:=200", "e:=1.5", "f:=\"text\"", "g:=[1,2]", "h:={\"k\":1}"]);

        Assert.IsTrue(parsed.Ok, parsed.Error);
        CollectionAssert.AreEqual(
            new[]
            {
                JsonValueKind.True, JsonValueKind.False, JsonValueKind.Null, JsonValueKind.Number,
                JsonValueKind.Number, JsonValueKind.String, JsonValueKind.Array, JsonValueKind.Object,
            },
            parsed.Values.Select(v => v.Value.Kind).ToArray());
    }

    /// <summary>
    /// A malformed <c>:=</c> value is rejected at parse time with the token named. Letting
    /// it through made <c>Utf8JsonWriter</c> throw while the request was still being built, which escaped the
    /// command — so a <c>--json</c> caller got a bare stack-trace message and no payload at all.
    /// </summary>
    [TestMethod]
    public void CallParams_RejectsMalformedRawJsonBeforeItReachesTheWriter()
    {
        foreach (var raw in new[] { "+5", ".5", "5.", "-", "1e", "0x10", "{", "[1,", "1 2", "true false", "" })
        {
            var parsed = DevToolsCallParams.Parse([$"p:={raw}"]);
            Assert.IsFalse(parsed.Ok, $"'p:={raw}' is not one valid JSON value and must be rejected.");
            StringAssert.Contains(parsed.Error!, "p=", "The error must point at the string form that would work.");
        }
    }

    [TestMethod]
    public void CallParams_RejectsMalformedAndDuplicateArguments()
    {
        Assert.IsFalse(DevToolsCallParams.Parse(["handle"]).Ok, "A bare token is not a parameter.");
        Assert.IsFalse(DevToolsCallParams.Parse(["=12345"]).Ok, "A nameless value is not a parameter.");
        Assert.IsFalse(DevToolsCallParams.Parse([":=12345"]).Ok, "A nameless raw value is not a parameter.");
        Assert.IsFalse(DevToolsCallParams.Parse(["h=1", "h=2"]).Ok, "A repeated name is ambiguous, not last-wins.");
        Assert.IsFalse(DevToolsCallParams.Parse(["h=1", "h:=2"]).Ok, "The two forms share one name space.");
    }

    /// <summary>
    /// Every classified value must actually be writable as the kind it claims — asserted against the writer
    /// rather than against the classifier's own opinion, because the failure mode was the writer throwing.
    /// </summary>
    [TestMethod]
    public void CallParams_EveryClassifiedValueIsWritableAsThatKind()
    {
        var parsed = DevToolsCallParams.Parse(
            ["a:=true", "b:=null", "c:=200", "d:=1.5", "e=+5", "f=.5", "g=5.", "handle=281474976710658", "h=Save changes"]);

        Assert.IsTrue(parsed.Ok, parsed.Error);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in parsed.Values)
            {
                value.Write(writer, name);
            }

            writer.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(stream.ToArray());
        Assert.AreEqual("+5", doc.RootElement.GetProperty("e").GetString());
        Assert.AreEqual(200, doc.RootElement.GetProperty("c").GetInt32());
        Assert.IsTrue(doc.RootElement.GetProperty("a").GetBoolean());
        Assert.AreEqual("281474976710658", doc.RootElement.GetProperty("handle").GetString());
    }

    /// <summary>
    /// the runtime resolves a <c>{ThemeResource}</c> to a plain local value, so <c>valueSource</c> really
    /// does say <c>Local</c>. A label built from it alone printed "Local" for a brush the developer wrote as
    /// <c>{ThemeResource TextFillColorSecondaryBrush}</c> — the one thing this command exists to tell them.
    /// Provenance comes from <c>authoredKind</c> first, matching <c>OriginLabel</c> in <c>DevToolsWindow.cpp</c>.
    /// </summary>
    [TestMethod]
    public void PropertyRows_AuthoredKindWinsOverAResolvedLocalValueSource()
    {
        var rows = DevToolsPropertyRow.Parse(
            """
            {"handle":"1","props":[
              {"name":"Foreground","value":"#C5FFFFFF","valueType":"SolidColorBrush","valueSource":"Local",
               "authored":"{ThemeResource TextFillColorSecondaryBrush}","authoredKind":"themeResource",
               "authoredKey":"TextFillColorSecondaryBrush","editKind":"none"},
              {"name":"Background","value":"#FF102030","valueType":"SolidColorBrush","valueSource":"Local",
               "authored":"{StaticResource CardBrush}","authoredKind":"staticResource","authoredKey":"CardBrush","editKind":"none"},
              {"name":"Padding","value":"4","valueType":"Thickness","valueSource":"Local",
               "authored":"4","authoredKind":"literal","editKind":"text"},
              {"name":"Width","value":"120","valueType":"Double","valueSource":"Local","editKind":"text"}]}
            """)!;

        Assert.AreEqual("ThemeResource TextFillColorSecondaryBrush", DevToolsPropertyRow.Find(rows, "Foreground")!.SourceLabel);
        Assert.AreEqual("StaticResource CardBrush", DevToolsPropertyRow.Find(rows, "Background")!.SourceLabel);
        Assert.AreEqual("Local", DevToolsPropertyRow.Find(rows, "Padding")!.SourceLabel,
            "A literal authored here IS a local value; the runtime's own vocabulary is right.");
        Assert.AreEqual("Local", DevToolsPropertyRow.Find(rows, "Width")!.SourceLabel);
    }

    [TestMethod]
    public void PropertyRows_BindingKindsReadAsWhatWasAuthored()
    {
        var rows = DevToolsPropertyRow.Parse(
            """
            {"handle":"1","props":[
              {"name":"IsEnabled","value":"True","valueType":"Boolean","valueSource":"Local",
               "authored":"{x:Bind ViewModel.CanSave}","authoredKind":"xBind","editKind":"none"},
              {"name":"Text","value":"hi","valueType":"String","valueSource":"Local","authoredKind":"binding",
               "binding":"{Binding Title}","editKind":"none"},
              {"name":"Foreground","value":"#FF000000","valueType":"SolidColorBrush","valueSource":"Local",
               "authoredKind":"templateBinding","editKind":"none"}]}
            """)!;

        Assert.AreEqual("{x:Bind ViewModel.CanSave}", DevToolsPropertyRow.Find(rows, "IsEnabled")!.SourceLabel);
        Assert.AreEqual("{Binding Title}", DevToolsPropertyRow.Find(rows, "Text")!.SourceLabel);
        Assert.AreEqual("TemplateBinding", DevToolsPropertyRow.Find(rows, "Foreground")!.SourceLabel);
    }

    /// <summary>An authored value is "set", even where the runtime's precedence slot alone would not say so.</summary>
    [TestMethod]
    public void PropertyRows_AnAuthoredResourceCountsAsSet()
    {
        var rows = DevToolsPropertyRow.Parse(
            """
            {"handle":"1","props":[
              {"name":"Foreground","value":"#C5FFFFFF","valueType":"SolidColorBrush","valueSource":"Default",
               "authoredKind":"themeResource","authoredKey":"K","editKind":"none"},
              {"name":"Opacity","value":"1","valueType":"Double","valueSource":"Default","editKind":"text"}]}
            """)!;

        Assert.IsTrue(DevToolsPropertyRow.Find(rows, "Foreground")!.IsSet);
        Assert.IsFalse(DevToolsPropertyRow.Find(rows, "Opacity")!.IsSet);
    }

    [TestMethod]
    public void Selector_TellsAHandleFromAName()
    {
        Assert.IsTrue(DevToolsSelector.IsHandle("281474976710658"));
        Assert.IsFalse(DevToolsSelector.IsHandle("0"), "0 is the tap's 'nothing' handle, not an element.");
        Assert.IsFalse(DevToolsSelector.IsHandle("SaveButton"));
        Assert.IsFalse(DevToolsSelector.IsHandle("N42"));
        Assert.IsFalse(DevToolsSelector.IsHandle(""));
    }

    /// <summary>
    /// A stale handle is the single most common failure an agent hits (the tree rebuilds between commands), so
    /// its message must say what to do next rather than repeat the token.
    /// </summary>
    [TestMethod]
    public void Errors_ExplainTheCommonFailuresInActionableTerms()
    {
        StringAssert.Contains(
            DevToolsErrors.Describe(new DevToolsProtocolError(-32001, "stale-handle", "gone")),
            "devtools inspect");
        StringAssert.Contains(
            DevToolsErrors.Describe(new DevToolsProtocolError(-32005, "refused-unsafe", "refused: posture")),
            "restart",
            "A refused write must explain that posture is fixed at injection, not just that it was refused.");
        StringAssert.Contains(
            DevToolsErrors.Describe(DevToolsProtocolError.NoResponse()),
            "did not answer");
    }

    [TestMethod]
    public void Json_ErrorPayloadPreservesTheProtocolCodeAndToken()
    {
        var payload = DevToolsJson.Error(1234, new DevToolsProtocolError(-32001, "stale-handle", "gone"));

        using var doc = JsonDocument.Parse(payload);
        Assert.IsFalse(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.AreEqual(1234, doc.RootElement.GetProperty("pid").GetInt32());
        var error = doc.RootElement.GetProperty("error");
        Assert.AreEqual(-32001, error.GetProperty("code").GetInt32());
        Assert.AreEqual("stale-handle", error.GetProperty("token").GetString());
        Assert.AreEqual("gone", error.GetProperty("message").GetString());
    }

    [TestMethod]
    public void Json_ResultPayloadCopiesTheProtocolResultThrough()
    {
        var payload = DevToolsJson.Result(7, "{\"handle\":\"1\",\"unmodelled\":true}", w => w.WriteString("method", "Layout.get"));

        using var doc = JsonDocument.Parse(payload);
        Assert.IsTrue(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.AreEqual("Layout.get", doc.RootElement.GetProperty("method").GetString());
        Assert.IsTrue(
            doc.RootElement.GetProperty("result").GetProperty("unmodelled").GetBoolean(),
            "A field the CLI does not model must still reach a --json consumer.");
    }

    [TestMethod]
    public void PropertyRows_CarryValueSourceAndWriteType()
    {
        var rows = DevToolsPropertyRow.Parse(
            """
            {"handle":"1","props":[
              {"name":"Width","value":"120","valueType":"Double","valueSource":"local","writeType":"Double"},
              {"name":"IsEnabled","value":"True","valueType":"Boolean","binding":"{Binding CanSave}","editKind":"none"}]}
            """);

        Assert.IsNotNull(rows);
        Assert.AreEqual("local", rows[0].ValueSource);
        Assert.AreEqual("Double", rows[0].WriteType);
        Assert.AreEqual("{Binding CanSave}", DevToolsPropertyRow.Find(rows, "isenabled")!.SourceLabel,
            "A bound value's provenance is the binding, not its precedence slot.");
        Assert.IsNull(DevToolsPropertyRow.Find(rows, "IsEnabled")!.WriteType,
            "No writeType is the tap's read-only signal; inventing one would produce a refused write.");
    }

    private IEnumerable<System.CommandLine.Command> LiveCommands() =>
    [
        GetRequiredService<DevToolsInspectCommand>(),
        GetRequiredService<DevToolsSearchCommand>(),
        GetRequiredService<DevToolsGetPropertyCommand>(),
        GetRequiredService<DevToolsGetLayoutCommand>(),
        GetRequiredService<DevToolsGetSourceCommand>(),
        GetRequiredService<DevToolsDiagnoseBindingCommand>(),
        GetRequiredService<DevToolsSetPropertyCommand>(),
        GetRequiredService<DevToolsCallCommand>(),
    ];
}

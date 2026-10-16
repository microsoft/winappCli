// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Spectre.Console.Testing;
using WinApp.Cli.Commands;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public class DevToolsQueryCommandTests
{
    private const string Row = """
        {"handle":"42","name":"SearchBox","type":"Microsoft.UI.Xaml.Controls.TextBox","depth":0,"context":false,"uniqueName":true,"fields":[
        {"name":"Text","value":"","valueState":"value","valueType":"String","bindingState":"unknown"},
        {"name":"PlaceholderText","value":"Search tasks","valueState":"value","valueType":"String","bindingState":"unknown"},
        {"name":"AutomationProperties.Name","value":"Search tasks","valueState":"value","valueType":"String","bindingState":"unknown"},
        {"name":"Missing","value":null,"valueState":"unavailable","valueType":"","bindingState":"unknown"}]}
        """;

    private static string Result(string rows = Row, bool complete = true, bool ok = true, string? error = null) =>
        $$"""
        {"ok":{{ok.ToString().ToLowerInvariant()}},"status":"read","complete":{{complete.ToString().ToLowerInvariant()}},"truncated":false,
        "scopeNodes":2,"censusNodes":2,"evaluated":1,"unevaluated":{{(complete ? 0 : 1)}},"propertyReads":1,"propertyRows":5,
        "scope":{"rootHandle":"0","depth":-1,"ofType":"Microsoft.UI.Xaml.Controls.TextBox","authored":false},
        "uiThreadMicroseconds":100,"reasons":{},"matches":[{{rows}}],"context":[]{{(error is null ? "" : ",\"error\":\"" + error + "\"")}}}
        """;

    [TestMethod]
    public async Task Search_StructuredCriteriaAreOneRequestWithAndPredicates()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.query", Result());
        var (exit, output) = await Run(new DevToolsSearchCommand(), agent,
            ["--of-type", "TextBox", "--with", "FontSize>=20", "--with", "IsEnabled==false", "--fields", "Text,PlaceholderText,Missing", "--json"]);
        Assert.AreEqual(0, exit);
        Assert.AreEqual("VisualTree.query", agent.Received.Single());
        using var request = JsonDocument.Parse(agent.ReceivedRequests.Single());
        Assert.AreEqual("FontSize>=20|IsEnabled==false",
            string.Join('|', request.RootElement.GetProperty("params").GetProperty("with").EnumerateArray().Select(value => value.GetString())));
        using var result = JsonDocument.Parse(output);
        Assert.AreEqual("SearchBox", result.RootElement.GetProperty("matches")[0].GetProperty("selector").GetString());
        Assert.AreEqual("", result.RootElement.GetProperty("matches")[0].GetProperty("fields")[0].GetProperty("value").GetString());
        StringAssert.Contains(result.RootElement.GetProperty("warnings")[0].GetString(), "Missing");
    }

    [TestMethod]
    public async Task Search_HumanOutputDistinguishesPlaceholderAccessibilityAndMissing()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.query", Result());
        var (exit, output) = await Run(new DevToolsSearchCommand(), agent,
            ["--of-type", "TextBox", "--fields", "Text,PlaceholderText,AutomationProperties.Name,Missing"]);
        Assert.AreEqual(0, exit);
        StringAssert.Contains(output, "Text=\"\"");
        StringAssert.Contains(output, "PlaceholderText=\"Search tasks\"");
        StringAssert.Contains(output, "AutomationProperties.Name=\"Search tasks\"");
        StringAssert.Contains(output, "Missing=<n/a>");
        StringAssert.Contains(output, "complete within requested scope");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PipedHumanValues_AreNotWrappedAtConsoleWidth(bool inspect)
    {
        var value = new string('x', 180);
        using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.query",
            Result(Row.Replace("Search tasks", value, StringComparison.Ordinal)));
        var (exit, output) = await Run(inspect ? new DevToolsInspectCommand() : new DevToolsSearchCommand(), agent,
            ["--of-type", "TextBox", "--fields", "PlaceholderText"], width: 80);
        Assert.AreEqual(0, exit);
        StringAssert.Contains(output, $"PlaceholderText=\"{value}\"");
    }

    [TestMethod]
    public async Task Inspect_PreservesLabelFilterSeparateFromSearchText()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.query", Result());
        var (exit, _) = await Run(new DevToolsInspectCommand(), agent,
            ["--of-type", "TextBox", "--filter", " Views/MainPage.xaml ", "--json"]);
        Assert.AreEqual(0, exit);
        using var request = JsonDocument.Parse(agent.ReceivedRequests.Single());
        Assert.AreEqual("views/mainpage.xaml", request.RootElement.GetProperty("params").GetProperty("filter").GetString());
        Assert.IsFalse(request.RootElement.GetProperty("params").TryGetProperty("text", out _));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ZeroMatches_PreservesNonzeroAndIncompleteDistinction(bool complete)
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.query",
            Result("", complete, false, complete ? "no-match" : "incomplete"));
        var (exit, output) = await Run(new DevToolsSearchCommand(), agent, ["--of-type", "Button", "--json"]);
        Assert.AreEqual(1, exit);
        using var result = JsonDocument.Parse(output);
        Assert.AreEqual(complete, result.RootElement.GetProperty("complete").GetBoolean());
        Assert.AreEqual(complete ? "no-match" : "incomplete", result.RootElement.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task Set_UsesOnlyGuardedBackendNotClientReadThenWrite()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("Property.setQuery",
            Result().Replace("\"status\":\"read\"", """
                "status":"applied",
                "before":{"name":"Text","value":"Today","valueType":"String","valueState":"value","bindingState":"unknown"},
                "after":{"name":"Text","value":"Tomorrow","valueType":"String","valueState":"value","bindingState":"unknown"}
                """, StringComparison.Ordinal));
        var (exit, _) = await Run(new DevToolsSetPropertyCommand(), agent,
            ["--of-type", "TextBlock", "--with", "Text==Today", "--value", "Tomorrow", "--property", "Text", "--type", "String", "--json"]);
        Assert.AreEqual(0, exit);
        Assert.AreEqual("Property.setQuery", agent.Received.Single());
        using var request = JsonDocument.Parse(agent.ReceivedRequests.Single());
        Assert.AreEqual("Tomorrow", request.RootElement.GetProperty("params").GetProperty("value").GetString());
        Assert.AreEqual("String", request.RootElement.GetProperty("params").GetProperty("writeType").GetString());
    }

    [TestMethod]
    [DataRow("search", false)]
    [DataRow("inspect", false)]
    [DataRow("set", false)]
    [DataRow("search", true)]
    [DataRow("inspect", true)]
    [DataRow("set", true)]
    public async Task QueryJson_NarrowConsolePreservesExactStrings(string mode, bool unicode)
    {
        var value = string.Concat(Enumerable.Repeat(unicode
            ? "\u041f\u0440\u043e\u043d\u0430\u0458\u0434\u0438 \u0437\u0430\u0434\u0430\u0447\u0430\n"
            : "A long ASCII value with spaces and a deliberate newline.\n", 4));
        var field = JsonSerializer.Serialize(new
        {
            name = "Text", value, valueType = "String", valueState = "value", bindingState = "unknown",
        });
        var reply = Result(Row.Replace("\"value\":\"\"", "\"value\":" + JsonSerializer.Serialize(value), StringComparison.Ordinal));
        if (mode == "set")
        {
            reply = reply.Replace("\"status\":\"read\"", "\"status\":\"applied\",\"before\":" + field + ",\"after\":" + field, StringComparison.Ordinal);
        }
        using var agent = new FakeDevToolsProtocolAgent().Answer(mode == "set" ? "Property.setQuery" : "VisualTree.query", reply);
        DevToolsLiveCommand command = mode switch
        {
            "set" => new DevToolsSetPropertyCommand(),
            "inspect" => new DevToolsInspectCommand(),
            _ => new DevToolsSearchCommand(),
        };
        string[] args = mode == "set"
            ? ["--of-type", "TextBlock", "--property", "Text", "--value", value, "--json"]
            : ["--of-type", "TextBlock", "--fields", "Text", "--json"];
        var (exit, output) = await Run(command, agent, args, width: 40);
        Assert.AreEqual(0, exit);
        using var result = JsonDocument.Parse(output);
        Assert.AreEqual(value, result.RootElement.GetProperty("matches")[0].GetProperty("fields")[0].GetProperty("value").GetString());
        if (mode == "set")
        {
            Assert.AreEqual(value, result.RootElement.GetProperty("after").GetProperty("value").GetString());
            Assert.AreEqual(value, result.RootElement.GetProperty("before").GetProperty("value").GetString());
        }
    }

    [TestMethod]
    public async Task QueryJson_NarrowConsolePreservesError()
    {
        var message = string.Concat(Enumerable.Repeat("Long diagnostic with whitespace. ", 8));
        using var agent = new FakeDevToolsProtocolAgent().Error("VisualTree.query", -32000, "bad-args", message);
        var (exit, output) = await Run(new DevToolsSearchCommand(), agent,
            ["--of-type", "TextBlock", "--json"], width: 40);
        Assert.AreEqual(1, exit);
        using var result = JsonDocument.Parse(output);
        Assert.AreEqual(message, result.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [TestMethod]
    public async Task Set_DispatchedTimeoutIsIndeterminateAndNeverRetried()
    {
        using var agent = new FakeDevToolsProtocolAgent().Error("Property.setQuery", -32010, "indeterminate", "Read to reconcile; do not retry.");
        var (exit, output) = await Run(new DevToolsSetPropertyCommand(), agent,
            ["--of-type", "TextBlock", "--value", "Tomorrow", "--property", "Text", "--json"]);
        Assert.AreEqual(1, exit);
        using var result = JsonDocument.Parse(output);
        Assert.AreEqual("indeterminate", result.RootElement.GetProperty("error").GetProperty("token").GetString());
        Assert.AreEqual("Property.setQuery", agent.Received.Single());
    }

    [TestMethod]
    [DataRow(false, 1)]
    [DataRow(true, 2)]
    public async Task Get_QueryRetainsExistingCompactAndAllPropertyBehavior(bool all, int count)
    {
        const string properties = """
            {"handle":"42","authoredState":"noSourceInfo","props":[
            {"name":"Text","value":"Today","valueType":"String","valueSource":"Local","editKind":"text","binding":"{Binding Title}",
             "chain":[{"source":"Local","value":"Today","winner":true},{"source":"Default","value":"","winner":false}]},
            {"name":"Width","value":"10","valueType":"Double","valueSource":"Default","editKind":"number"}]}
            """;
        var reply = Result()[..^1] + ",\"properties\":" + properties + "}";
        using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.query", reply);
        var arguments = new List<string> { "--of-type", "TextBox", "--json" };
        if (all)
        {
            arguments.Add("--all");
        }
        var (exit, output) = await Run(new DevToolsGetPropertyCommand(), agent, arguments.ToArray());
        Assert.AreEqual(0, exit);
        using var result = JsonDocument.Parse(output);
        Assert.AreEqual(count, result.RootElement.GetProperty("properties").GetArrayLength());
        Assert.AreEqual(2, result.RootElement.GetProperty("properties")[0].GetProperty("chain").GetArrayLength());
        Assert.AreEqual("{Binding Title}", result.RootElement.GetProperty("properties")[0].GetProperty("binding").GetString());
        Assert.HasCount(1, agent.Received);
        Assert.AreEqual("SearchBox", result.RootElement.GetProperty("selector").GetString());
        Assert.IsTrue(result.RootElement.GetProperty("query").GetProperty("complete").GetBoolean());
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("""{"ok":true,"matches":[null]}""")]
    public async Task InvalidQueryReplyFailsExplicitly(string reply)
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.query", reply);
        var (exit, output) = await Run(new DevToolsSearchCommand(), agent, ["--of-type", "TextBox", "--json"]);
        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "invalid-response");
    }

    [TestMethod]
    public async Task Projection_OmittedRequestedFieldIsNotSilentlyAccepted()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.query", Result());
        var (exit, output) = await Run(new DevToolsSearchCommand(), agent, ["--of-type", "TextBox", "--fields", "Unreported", "--json"]);
        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "invalid-response");
    }

    [TestMethod]
    public async Task Get_IncompleteMatchRefusesReadWithoutRetry()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.query", Result(complete: false, ok: false, error: "incomplete"));
        var (exit, output) = await Run(new DevToolsGetPropertyCommand(), agent, ["--with", "Text!=Save", "--json"]);
        Assert.AreEqual(1, exit);
        Assert.AreEqual("VisualTree.query", agent.Received.Single());
        using var result = JsonDocument.Parse(output);
        Assert.IsFalse(result.RootElement.GetProperty("complete").GetBoolean());
        Assert.AreEqual(1, result.RootElement.GetProperty("unevaluated").GetInt32());
    }

    private static string DiagnosticResult(string phase, int omitted = 0)
    {
        var diagnostics = JsonSerializer.Serialize(new
        {
            candidates = new[]
            {
                new
                {
                    selector = "42",
                    census = new
                    {
                        handle = "42", name = "Quoted \"name\"\n\\\u0416", type = "Microsoft.UI.Xaml.Controls.Button",
                        id = "census-only-id", uniqueName = false, context = false, fields = Array.Empty<object>(),
                    },
                    phase, reason = "null",
                    predicates = new[]
                    {
                        new { property = "AutomationProperties.AutomationId", @operator = "==", truth = "true", reason = "", valueType = "String", valueState = "value" },
                        new { property = "Content", @operator = "==", truth = "unknown", reason = "null", valueType = "Object", valueState = "null" },
                    },
                },
            },
            omitted, truncated = omitted > 0,
        });
        return Result("", complete: false, ok: false, error: phase == "final" ? "target-changed" : "incomplete")
            .Replace("\"status\":\"read\"", "\"status\":\"not-applied\"", StringComparison.Ordinal)[..^1] +
            ",\"diagnostics\":" + diagnostics + "}";
    }

    [TestMethod]
    [DataRow(false, false, "initial")]
    [DataRow(false, true, "initial")]
    [DataRow(true, false, "initial")]
    [DataRow(true, true, "initial")]
    [DataRow(true, false, "final")]
    [DataRow(true, true, "final")]
    public async Task ExactFailure_ShowsBoundedCandidateDiagnosticsWithoutRetry(bool write, bool json, string phase)
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer(write ? "Property.setQuery" : "VisualTree.query",
            DiagnosticResult(phase, 3));
        var args = new List<string> { "--of-type", "Button", "--with", "Content==New intention" };
        if (write)
        {
            args.AddRange(["--property", "Content", "--value", "New"]);
        }
        if (json)
        {
            args.Add("--json");
        }
        var (exit, output) = await Run(write ? new DevToolsSetPropertyCommand() : new DevToolsGetPropertyCommand(),
            agent, args.ToArray(), width: json ? 40 : 300);
        Assert.AreEqual(1, exit);
        Assert.HasCount(1, agent.Received);
        if (json)
        {
            using var document = JsonDocument.Parse(output);
            var details = document.RootElement.GetProperty("diagnostics");
            var candidate = details.GetProperty("candidates")[0];
            Assert.AreEqual("42", candidate.GetProperty("selector").GetString());
            Assert.AreEqual("Quoted \"name\"\n\\\u0416", candidate.GetProperty("census").GetProperty("name").GetString());
            Assert.AreEqual(phase, candidate.GetProperty("phase").GetString());
            Assert.AreEqual("true", candidate.GetProperty("predicates")[0].GetProperty("truth").GetString());
            Assert.AreEqual("unknown", candidate.GetProperty("predicates")[1].GetProperty("truth").GetString());
            Assert.AreEqual(3, details.GetProperty("omitted").GetInt32());
            Assert.IsTrue(details.GetProperty("truncated").GetBoolean());
            Assert.IsFalse(document.RootElement.GetProperty("complete").GetBoolean());
            Assert.IsFalse(candidate.GetProperty("predicates")[0].TryGetProperty("value", out _));
        }
        else
        {
            StringAssert.Contains(output, "[42] census name=");
            StringAssert.Contains(output, $"{phase}: null");
            StringAssert.Contains(output, "AutomationProperties.AutomationId ==: true");
            StringAssert.Contains(output, "Content ==: unknown; state=null");
            StringAssert.Contains(output, "3 omitted. Query completeness is unchanged.");
        }
    }

    [TestMethod]
    [DataRow("truth")]
    [DataRow("selector")]
    [DataRow("count")]
    [DataRow("bytes")]
    [DataRow("phase")]
    [DataRow("truncated")]
    [DataRow("candidate-cap")]
    [DataRow("predicate-cap")]
    public async Task InvalidDiagnostics_FailWithoutFollowupRequest(string fault)
    {
        var reply = DiagnosticResult("initial");
        reply = fault switch
        {
            "truth" => reply.Replace("\"truth\":\"unknown\"", "\"truth\":\"maybe\"", StringComparison.Ordinal),
            "selector" => reply.Replace("\"selector\":\"42\"", "\"selector\":\"43\"", StringComparison.Ordinal),
            "count" => reply.Replace("\"omitted\":0", "\"omitted\":-1", StringComparison.Ordinal),
            "bytes" => reply.Replace("census-only-id", new string('x', 65536), StringComparison.Ordinal),
            "phase" => reply.Replace("\"phase\":\"initial\"", "\"phase\":\"later\"", StringComparison.Ordinal),
            "truncated" => reply.Replace("\"omitted\":0", "\"omitted\":1", StringComparison.Ordinal),
            _ => reply,
        };
        using var original = JsonDocument.Parse(reply);
        var candidates = original.RootElement.GetProperty("diagnostics").GetProperty("candidates");
        if (fault == "candidate-cap")
        {
            reply = reply.Replace(candidates.GetRawText(),
                "[" + string.Join(',', Enumerable.Repeat(candidates[0].GetRawText(), 33)) + "]", StringComparison.Ordinal);
        }
        if (fault == "predicate-cap")
        {
            var predicates = candidates[0].GetProperty("predicates");
            reply = reply.Replace(predicates.GetRawText(),
                "[" + string.Join(',', Enumerable.Repeat(predicates[0].GetRawText(), 17)) + "]", StringComparison.Ordinal);
        }
        using var agent = new FakeDevToolsProtocolAgent().Answer("Property.setQuery", reply);
        var (exit, output) = await Run(new DevToolsSetPropertyCommand(), agent,
            ["--of-type", "Button", "--property", "Content", "--value", "New", "--json"]);
        Assert.AreEqual(1, exit);
        Assert.HasCount(1, agent.Received);
        StringAssert.Contains(output, "indeterminate");
    }

    [TestMethod]
    public async Task DiagnosticJson_PreservesUtf8ByteBudgetAtNarrowWidth()
    {
        using var original = JsonDocument.Parse(DiagnosticResult("initial"));
        var reply = JsonSerializer.Serialize(original.RootElement)
            .Replace("census-only-id", new string('\u0416', 20000), StringComparison.Ordinal);
        using var agent = new FakeDevToolsProtocolAgent
        {
            RawReply = System.Text.Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":" + reply + "}\n"),
        };
        var (exit, output) = await Run(new DevToolsGetPropertyCommand(), agent,
            ["--of-type", "Button", "--json"], width: 40);
        Assert.AreEqual(1, exit);
        Assert.HasCount(1, agent.Received);
        using var parsed = JsonDocument.Parse(output);
        Assert.IsTrue(parsed.RootElement.TryGetProperty("diagnostics", out var details), output);
        var diagnostic = details.GetRawText();
        Assert.IsLessThanOrEqualTo(64 * 1024, System.Text.Encoding.UTF8.GetByteCount(diagnostic));
        Assert.AreEqual(new string('\u0416', 20000), parsed.RootElement.GetProperty("diagnostics")
            .GetProperty("candidates")[0].GetProperty("census").GetProperty("id").GetString());
    }

    [TestMethod]
    public async Task Set_InconsistentAcknowledgementIsIndeterminate()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("Property.setQuery",
            Result(complete: false).Replace("\"status\":\"read\"", "\"status\":\"applied\"", StringComparison.Ordinal));
        var (exit, output) = await Run(new DevToolsSetPropertyCommand(), agent,
            ["--of-type", "TextBlock", "--property", "Text", "--value", "New", "--json"]);
        Assert.AreEqual(1, exit);
        Assert.AreEqual("Property.setQuery", agent.Received.Single());
        StringAssert.Contains(output, "indeterminate");
    }

    [TestMethod]
    [DataRow("width", 0)]
    [DataRow("NotAProperty", 1)]
    public async Task Get_QueryUsesExistingCaseInsensitivePropertySelection(string property, int expected)
    {
        var reply = Result()[..^1] + """
            ,"properties":{"handle":"42","authoredState":"noSourceInfo","props":[
            {"name":"Width","value":"10","valueType":"Double","valueSource":"Default","editKind":"number"}]}}
            """;
        using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.query", reply);
        var (exit, output) = await Run(new DevToolsGetPropertyCommand(), agent,
            ["--of-type", "TextBox", "--property", property, "--json"]);
        Assert.AreEqual(expected, exit);
        Assert.HasCount(1, agent.Received);
        if (expected == 0)
        {
            using var result = JsonDocument.Parse(output);
            Assert.AreEqual("Width", result.RootElement.GetProperty("properties")[0].GetProperty("name").GetString());
        }
    }

    [TestMethod]
    public async Task Search_MaxOneDoesNotHideMultipleMatches()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.query", Result(Row + "," + Row));
        var (exit, output) = await Run(new DevToolsSearchCommand(), agent, ["--of-type", "TextBox", "--max", "1", "--json"]);
        Assert.AreEqual(0, exit);
        using var result = JsonDocument.Parse(output);
        Assert.AreEqual(2, result.RootElement.GetProperty("matchCount").GetInt32());
        Assert.AreEqual(1, result.RootElement.GetProperty("matches").GetArrayLength());
        Assert.IsTrue(result.RootElement.GetProperty("hasMore").GetBoolean());
        using var request = JsonDocument.Parse(agent.ReceivedRequests.Single());
        Assert.IsFalse(request.RootElement.GetProperty("params").TryGetProperty("max", out _));
    }

    [TestMethod]
    [DataRow("--with", "Text=x")]
    [DataRow("--with", "==x")]
    [DataRow("--with", "FontSize>>20")]
    [DataRow("--with", "FontSize<>20")]
    [DataRow("--with", "FontSize><20")]
    [DataRow("--with", "FontSize>==20")]
    [DataRow("--with", "FontSize<<20")]
    [DataRow("--with", "FontSize<*20")]
    [DataRow("--with", "FontSize<=!20")]
    [DataRow("--fields", "Text,,FontSize")]
    [DataRow("--fields", "Text.Value[0]")]
    public async Task InvalidGrammar_NeverSendsQuery(string option, string value)
    {
        using var agent = new FakeDevToolsProtocolAgent();
        var (exit, output) = await Run(new DevToolsSearchCommand(), agent, ["--of-type", "TextBox", option, value, "--json"]);
        Assert.AreEqual(1, exit);
        Assert.AreEqual(0, agent.Received.Count);
        StringAssert.Contains(output, "bad-args");
    }

    [TestMethod]
    public void InvalidOrderedOperator_WholeArgumentQuotesDoNotMakeItValid()
    {
        var command = new DevToolsSearchCommand();
        var parse = command.Parse("--with \"FontSize>>20\"");
        Assert.IsEmpty(parse.Errors);
        Assert.IsNotNull(DevToolsQueryOptions.Validate(parse, true, out _));
    }

    [TestMethod]
    [DataRow("Text==>20")]
    [DataRow("Text!=<20")]
    [DataRow("Text*==")]
    [DataRow("Text==\"quoted\"")]
    public async Task StringLiteral_LeadingPunctuationIsPreserved(string predicate)
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.query", Result(Row));
        var (exit, _) = await Run(new DevToolsSearchCommand(), agent, ["--with", predicate, "--json"]);
        Assert.AreEqual(0, exit);
        using var request = JsonDocument.Parse(agent.ReceivedRequests.Single());
        Assert.AreEqual(predicate, request.RootElement.GetProperty("params").GetProperty("with")[0].GetString());
    }

    [TestMethod]
    public async Task ExactQueries_RejectPositionalSelectorAndMissingValue()
    {
        using var agent = new FakeDevToolsProtocolAgent();
        Assert.AreEqual(1, (await Run(new DevToolsGetPropertyCommand(), agent, ["Heading", "--of-type", "TextBlock"])).Exit);
        Assert.AreEqual(1, (await Run(new DevToolsSetPropertyCommand(), agent, ["Heading", "--of-type", "TextBlock", "--value", "x", "-p", "Text"])).Exit);
        Assert.AreEqual(1, (await Run(new DevToolsSetPropertyCommand(), agent, ["--of-type", "TextBlock", "-p", "Text"])).Exit);
        Assert.AreEqual(0, agent.Received.Count);
    }

    [TestMethod]
    public void Preview_FormatKeepsAccessibleNameSeparateAndReportsTruncation()
    {
        var text = DevToolsPreviews.Format([
            new("PlaceholderText", "Search tasks", "value", "String", false),
            new("AutomationProperties.Name", "Search tasks", "value", "String", false),
            new("Header", "Very long", "value", "String", true),
            new("Content", "", "object", "StackPanel", false),
        ]);
        StringAssert.Contains(text, "PlaceholderText=");
        StringAssert.Contains(text, "AutomationProperties.Name=");
        StringAssert.Contains(text, "Header=\"Very long\"...");
        StringAssert.Contains(text, "Content=<StackPanel>");
    }

    [TestMethod]
    public void Preview_TruncatedBatchDisclosesIncompleteValues()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.getPreviews", """{"previews":[],"truncated":true}""");
        var (roots, error) = DevToolsPreviews.Read(new VisualTreeTap((uint)agent.Pid),
            [new("42", "SearchBox", "TextBox", null, null, true, 0, [], 0)], CancellationToken.None);
        Assert.HasCount(1, roots);
        StringAssert.Contains(error, "truncated");
        Assert.AreEqual("VisualTree.getPreviews", agent.Received.Single());
    }

    private static async Task<(int Exit, string Output)> Run(DevToolsLiveCommand command, FakeDevToolsProtocolAgent agent, string[] args, int width = 300)
    {
        var console = new TestConsole();
        console.Profile.Width = width;
        command.Action = command switch
        {
            DevToolsInspectCommand => new DevToolsInspectCommand.Handler(agent.Resolver(), console),
            DevToolsSearchCommand => new DevToolsSearchCommand.Handler(agent.Resolver(), console),
            DevToolsGetPropertyCommand => new DevToolsGetPropertyCommand.Handler(agent.Resolver(), console),
            DevToolsSetPropertyCommand => new DevToolsSetPropertyCommand.Handler(agent.Resolver(), console),
            _ => throw new NotSupportedException(),
        };
        var exit = await command.Parse(args).InvokeAsync();
        return (exit, console.Output);
    }
}

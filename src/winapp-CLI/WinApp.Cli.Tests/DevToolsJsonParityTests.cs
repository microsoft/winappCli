// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Spectre.Console.Testing;
using WinApp.Cli.Commands;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

/// <summary>
/// Drives actual commands against <see cref="FakeDevToolsProtocolAgent"/>. Human and JSON output must agree on
/// success, availability and filtering, including malformed replies and unconfirmed writes.
/// <para>
/// These run the REAL <see cref="VisualTreeTap"/> over a real named pipe, because a shape disagreement only
/// exists once a command has run against a reply.
/// </para>
/// </summary>
[TestClass]
[DoNotParallelize]
public class DevToolsJsonParityTests
{
    [TestMethod]
    public async Task Correction_Search_PreservesRemoteError()
    {
        using var agent = new FakeDevToolsProtocolAgent().Error("VisualTree.find", -32011, "no-tree", "controlled");
        var (exit, output) = await RunAsync(new DevToolsSearchCommand(), agent, ["Button", "--all", "--json"]);
        Assert.AreEqual(1, exit);
        using var doc = JsonDocument.Parse(output);
        Assert.AreEqual(-32011, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.AreEqual("no-tree", doc.RootElement.GetProperty("error").GetProperty("token").GetString());
    }

    [TestMethod]
    public async Task Correction_SlugFallback_PreservesRemoteErrorWithoutPropertyRead()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.find", Find("[]"))
            .Error("VisualTree.enumerate", -32011, "no-tree", "controlled fallback");
        var (exit, output) = await RunAsync(new DevToolsGetPropertyCommand(), agent, ["button-cafebabe01", "--json"]);
        Assert.AreEqual(1, exit);
        using var doc = JsonDocument.Parse(output);
        Assert.AreEqual(-32011, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.AreEqual("no-tree", doc.RootElement.GetProperty("error").GetProperty("token").GetString());
        CollectionAssert.AreEqual(new List<string> { "VisualTree.find", "VisualTree.enumerate" }, agent.Received);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Correction_CancelledSelector_PreventsFollowingRequests(bool preCancelled)
    {
        using var cancel = new CancellationTokenSource();
        using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.find", Find("[]"),
            _ => { cancel.Cancel(); return true; });
        if (preCancelled) { cancel.Cancel(); }
        var handler = new DevToolsSetPropertyCommand.Handler(agent.Resolver(), new TestConsole());
        try
        {
            await handler.InvokeAsync(new DevToolsSetPropertyCommand().Parse(["button-cafebabe01", "after", "-p", "Text"]), cancel.Token);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        CollectionAssert.AreEqual(preCancelled ? new List<string>() : new List<string> { "VisualTree.find" }, agent.Received);
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("""{"name":"Text","value":123,"valueType":"String"}""")]
    [DataRow("""{"name":"Text","value":"hello"}""")]
    [DataRow("""{"name":"Text","value":"hello","valueType":"String","writeType":42}""")]
    [DataRow("""{"name":"","value":"hello","valueType":"String"}""")]
    public async Task Correction_InvalidPropertyRow_IsNotEmptySuccess(string row)
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("Property.get",
            $$"""{"handle":"42","authoredState":"available","props":[{{row}}]}""");
        var (exit, _) = await RunAsync(new DevToolsGetPropertyCommand(), agent, ["42", "--all", "--json"]);
        Assert.AreEqual(1, exit);
    }

    [TestMethod]
    public async Task Correction_CancellationAfterRead_PreventsWrite()
    {
        using var cancel = new CancellationTokenSource();
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("Property.get", """{"props":[{"name":"Text","value":"before","valueType":"String","writeType":"String"}]}""",
                _ => { cancel.Cancel(); return true; })
            .Answer("HotReload.setProperty", "null")
            .Answer("VisualTree.enumerate", Tree);
        var handler = new DevToolsSetPropertyCommand.Handler(agent.Resolver(), new TestConsole());
        try
        {
            await handler.InvokeAsync(new DevToolsSetPropertyCommand().Parse(["42", "after", "-p", "Text", "--json"]), cancel.Token);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        CollectionAssert.DoesNotContain(agent.Received, "HotReload.setProperty");
        CollectionAssert.AreEqual(new List<string> { "Property.get" }, agent.Received);
    }

    [TestMethod]
    [DataRow("SolidColorBrush", "#0067C0", "#FF0067C0", true)]
    [DataRow("SolidColorBrush", "#abc", "#FFAABBCC", true)]
    [DataRow("SolidColorBrush", "#1abc", "#11AABBCC", true)]
    [DataRow("String", "foo", "Foo", false)]
    [DataRow("String", "01", "1", false)]
    [DataRow("Int64", "9007199254740993", "9007199254740992", false)]
    [DataRow("Double", "0.00001", "0.00002", false)]
    [DataRow("Boolean", "true", "True", true)]
    [DataRow("Thickness", "10", "10,10,10,10", false)]
    public async Task Correction_ReadbackMatch_IsTypeAware(string type, string requested, string observed, bool matches)
    {
        var row = JsonSerializer.Serialize(new { name = "Value", value = observed, valueType = type, writeType = type });
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("Property.get", $$"""{"props":[{{row}}]}""")
            .Answer("HotReload.setProperty", "null")
            .Answer("VisualTree.enumerate", Tree);
        var (exit, output) = await RunAsync(new DevToolsSetPropertyCommand(), agent, ["42", requested, "-p", "Value", "--json"]);
        using var doc = JsonDocument.Parse(output);
        Assert.AreEqual(matches, doc.RootElement.GetProperty("matchesRequested").GetBoolean());
        Assert.AreEqual(matches ? 0 : 1, exit);
    }

    /// <summary>The tree the fake serves: a Grid with one named Button child.</summary>
    private const string Tree =
        """
        [{"handle":"10","name":"Root","type":"Microsoft.UI.Xaml.Controls.Grid","file":"ms-appx:///MainWindow.xaml",
          "childCount":1,"children":[
            {"handle":"42","name":"SaveButton","type":"Microsoft.UI.Xaml.Controls.Button",
             "file":"ms-appx:///MainWindow.xaml","childCount":0,"children":[]}]}]
        """;

    /// <summary>
    /// The authored reply for the same tree: <c>inspect</c> defaults to the authored view, and that view is
    /// an object envelope carrying the classifier state alongside its nodes. These tests are about verdict
    /// parity rather than classification, so the state says "instrumented and finished".
    /// </summary>
    private static string AuthoredTree(string nodes = Tree) =>
        $$"""
        {"nodes":{{nodes}},"authored":true,"sourceInstrumented":true,"classificationTruncated":false,
         "classifiedNodes":2,"censusNodes":2,"authoredNodes":2,"classifier":"allowlist"}
        """;

    private static bool IsAuthored(string request) => request.Contains("\"authored\":true", StringComparison.Ordinal);

    private static string Find(string matches, bool truncated = false) => $$"""
        {"matches":{{matches}},"query":"q","appAuthoredOnly":false,"searchedNodes":2,"censusNodes":2,"truncated":{{(truncated ? "true" : "false")}}}
        """;

    private const string SaveButtonMatch =
        """[{"handle":"42","name":"SaveButton","type":"Microsoft.UI.Xaml.Controls.Button","depth":2}]""";

    [TestMethod]
    public async Task Search_NoMatches_IsAFailureInBothShapes()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.find", Find("[]"));

        var (humanExit, _) = await RunAsync(new DevToolsSearchCommand(), agent, ["__missing__"]);
        var (jsonExit, jsonOut) = await RunAsync(new DevToolsSearchCommand(), agent, ["__missing__", "--json"]);

        Assert.AreEqual(1, humanExit);
        Assert.AreEqual(humanExit, jsonExit, "--json must not disagree with the human path about the exit code.");
        using var doc = JsonDocument.Parse(jsonOut);
        Assert.IsFalse(doc.RootElement.GetProperty("ok").GetBoolean(), "ok:true with matchCount:0 is the bug.");
        Assert.AreEqual(0, doc.RootElement.GetProperty("matchCount").GetInt32());
        StringAssert.Contains(doc.RootElement.GetProperty("error").GetProperty("message").GetString()!, "__missing__",
            "The JSON payload must say WHY, not leave the caller to infer it from an empty array.");
    }

    [TestMethod]
    public async Task Search_WithMatches_IsASuccessInBothShapes()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.find", Find(SaveButtonMatch));

        var (humanExit, _) = await RunAsync(new DevToolsSearchCommand(), agent, ["Save"]);
        var (jsonExit, jsonOut) = await RunAsync(new DevToolsSearchCommand(), agent, ["Save", "--json"]);

        Assert.AreEqual(0, humanExit);
        Assert.AreEqual(0, jsonExit);
        using var doc = JsonDocument.Parse(jsonOut);
        Assert.IsTrue(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.IsFalse(doc.RootElement.TryGetProperty("error", out _), "A success carries no error.");
    }

    [TestMethod]
    public async Task Inspect_FilterWithNoMatchAboveTheDepthCutoff_SaysDeeperElementsWereNotSearched()
    {
        const string cut = """
            [{"handle":"10","name":"Root","type":"Microsoft.UI.Xaml.Controls.Grid","file":"ms-appx:///MainWindow.xaml",
              "childCount":1,"children":[
                {"handle":"42","name":"SaveButton","type":"Microsoft.UI.Xaml.Controls.Button",
                 "file":"ms-appx:///MainWindow.xaml","childCount":2,"children":[]}]}]
            """;
        using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.enumerate", AuthoredTree(cut), IsAuthored).Answer("VisualTree.enumerate", cut);

        var (exit, output) = await RunAsync(new DevToolsInspectCommand(), agent, ["--filter", "DeepLabel", "--depth", "1"]);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "within --depth 1; deeper elements were not searched");
        Assert.IsFalse(output.Contains("No elements matched.", StringComparison.Ordinal), output);
    }

    [TestMethod]
    public async Task Inspect_FilterMatchingNothing_IsAFailureInBothShapes()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.enumerate", AuthoredTree(), IsAuthored).Answer("VisualTree.enumerate", Tree);

        var (humanExit, _) = await RunAsync(new DevToolsInspectCommand(), agent, ["--filter", "__missing__"]);
        var (jsonExit, jsonOut) = await RunAsync(new DevToolsInspectCommand(), agent, ["--filter", "__missing__", "--json"]);

        Assert.AreEqual(1, humanExit);
        Assert.AreEqual(humanExit, jsonExit, "--json returned 0 while the human path returned 1.");
        using var doc = JsonDocument.Parse(jsonOut);
        Assert.IsTrue(doc.RootElement.TryGetProperty("ok", out var okElement), $"No 'ok' in: {jsonOut}");
        Assert.IsFalse(okElement.GetBoolean());
        Assert.AreEqual(0, doc.RootElement.GetProperty("count").GetInt32());
        Assert.IsTrue(doc.RootElement.TryGetProperty("error", out _));
    }

    [TestMethod]
    public async Task GetSource_WithNoSourceRecorded_IsAFailureInBothShapes()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.find", Find(SaveButtonMatch))
            .Answer("VisualTree.enumerate", AuthoredTree(), IsAuthored).Answer("VisualTree.enumerate", Tree)
            .Answer("Source.get", """{"handle":"42","fileName":"","lineNumber":0,"columnNumber":0,"authoredState":"noSourceInfo"}""");

        var (humanExit, humanOut) = await RunAsync(new DevToolsGetSourceCommand(), agent, ["42"]);
        var (jsonExit, jsonOut) = await RunAsync(new DevToolsGetSourceCommand(), agent, ["42", "--json"]);

        Assert.AreEqual(1, humanExit);
        Assert.AreEqual(humanExit, jsonExit, "ok:true with fileName:\"\" and exit 0 is the bug.");
        using var doc = JsonDocument.Parse(jsonOut);
        Assert.IsFalse(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.IsFalse(doc.RootElement.TryGetProperty("fileName", out _),
            "An empty fileName must not be emitted as though it were a location.");
        var error = doc.RootElement.GetProperty("error");
        Assert.AreEqual("source-unavailable", error.GetProperty("token").GetString());
        const string message = "No XAML source is recorded for this element. Framework, template or generated elements may have no app-authored declaration.";
        Assert.AreEqual(message, error.GetProperty("message").GetString());
        StringAssert.Contains(humanOut.Replace("\r", "").Replace("\n", " "), message);
    }

    [TestMethod]
    public async Task GetSource_WithASourceRecord_IsASuccessInBothShapes()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", AuthoredTree(), IsAuthored).Answer("VisualTree.enumerate", Tree)
            .Answer("Source.get", """{"handle":"42","fileName":"ms-appx:///MainWindow.xaml","lineNumber":90,"columnNumber":21,"authoredState":"available"}""");

        var (humanExit, _) = await RunAsync(new DevToolsGetSourceCommand(), agent, ["42"]);
        var (jsonExit, jsonOut) = await RunAsync(new DevToolsGetSourceCommand(), agent, ["42", "--json"]);

        Assert.AreEqual(0, humanExit);
        Assert.AreEqual(0, jsonExit);
        using var doc = JsonDocument.Parse(jsonOut);
        Assert.IsTrue(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.AreEqual(90, doc.RootElement.GetProperty("lineNumber").GetInt32());
    }

    [TestMethod]
    public async Task GetSource_MappedDeclaration_DoesNotRewriteRawCapturedCoordinates()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", AuthoredTree(), IsAuthored).Answer("VisualTree.enumerate", Tree)
            .Answer("Source.get", """
                {"handle":"42","fileName":"ms-appx:///Views/Header.xaml","lineNumber":246,"columnNumber":128,
                 "authoredState":"available","authoredFileName":"MainPage.xaml","authoredLineNumber":246,
                 "authoredColumnNumber":25,"coordinateProvenance":"disk-matched-unique-declaration","xaml":"<TextBlock />"}
                """);
        var (humanExit, humanOut) = await RunAsync(new DevToolsGetSourceCommand(), agent, ["42"]);
        var (jsonExit, jsonOut) = await RunAsync(new DevToolsGetSourceCommand(), agent, ["42", "--json"]);
        Assert.AreEqual(0, humanExit);
        Assert.AreEqual(0, jsonExit);
        StringAssert.Contains(humanOut, "MainPage.xaml:246:25");
        StringAssert.Contains(humanOut, "disk-matched");
        StringAssert.Contains(humanOut, "running app may retain different XAML");
        using var document = JsonDocument.Parse(jsonOut);
        Assert.AreEqual(128, document.RootElement.GetProperty("columnNumber").GetInt32());
        Assert.AreEqual(25, document.RootElement.GetProperty("authoredColumnNumber").GetInt32());
        Assert.AreEqual("MainPage.xaml", document.RootElement.GetProperty("authoredFileName").GetString());
        Assert.AreEqual("disk-matched-unique-declaration", document.RootElement.GetProperty("coordinateProvenance").GetString());
    }

    [TestMethod]
    public async Task GetSource_LikelyDeclaration_LabelsMissingEvidenceAndPreservesRawTuple()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", AuthoredTree(), IsAuthored).Answer("VisualTree.enumerate", Tree)
            .Answer("Source.get", """
                {"handle":"42","fileName":"ms-appx:///MainPage.xaml","lineNumber":48,"columnNumber":128,
                 "authoredState":"likely","authoredFileName":"MainPage.xaml","authoredLineNumber":48,
                 "authoredColumnNumber":25,"coordinateProvenance":"likely-source-line",
                 "sourceEvidence":"Compiler line preservation is unverified.","xaml":"<TextBlock />"}
                """);
        var (humanExit, humanOut) = await RunAsync(new DevToolsGetSourceCommand(), agent, ["42"]);
        var (jsonExit, jsonOut) = await RunAsync(new DevToolsGetSourceCommand(), agent, ["42", "--json"]);
        Assert.AreEqual(0, humanExit);
        Assert.AreEqual(0, jsonExit);
        StringAssert.Contains(humanOut, "likely");
        StringAssert.Contains(humanOut, "Compiler line preservation is unverified.");
        StringAssert.Contains(humanOut, "MainPage.xaml:48:25");
        using var document = JsonDocument.Parse(jsonOut);
        Assert.AreEqual("likely", document.RootElement.GetProperty("authoredState").GetString());
        Assert.AreEqual("likely-source-line", document.RootElement.GetProperty("coordinateProvenance").GetString());
        Assert.AreEqual(128, document.RootElement.GetProperty("columnNumber").GetInt32());
        Assert.AreEqual(25, document.RootElement.GetProperty("authoredColumnNumber").GetInt32());
    }

    [TestMethod]
    public async Task GetSource_UnverifiedBuild_IsNotAnAvailableAuthoredDeclaration()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", AuthoredTree(), IsAuthored).Answer("VisualTree.enumerate", Tree)
            .Answer("Source.get", """
                {"handle":"42","fileName":"ms-appx:///MainPage.xaml","lineNumber":246,"columnNumber":128,"authoredState":"unverifiedBuild"}
                """);
        var (exit, output) = await RunAsync(new DevToolsGetSourceCommand(), agent, ["42", "--json"]);
        Assert.AreEqual(0, exit, "Raw source location remains readable; no new source-command exit semantics.");
        using var document = JsonDocument.Parse(output);
        Assert.AreEqual("unverifiedBuild", document.RootElement.GetProperty("authoredState").GetString());
        Assert.IsFalse(document.RootElement.TryGetProperty("xaml", out _));
        StringAssert.Contains(document.RootElement.GetProperty("xamlUnavailable").GetString()!, "could not be verified together");
        StringAssert.Contains(document.RootElement.GetProperty("xamlUnavailable").GetString()!, "rebuilding alone does not verify");
    }

    [TestMethod]
    public async Task DiagnoseBinding_Unavailable_IsAFailureInBothShapes()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", AuthoredTree(), IsAuthored).Answer("VisualTree.enumerate", Tree)
            .Answer("Binding.diagnose", """{"state":"unavailable","reason":"this app has no managed DevTools agent"}""");

        var (humanExit, _) = await RunAsync(new DevToolsDiagnoseBindingCommand(), agent, ["42", "IsEnabled"]);
        var (jsonExit, jsonOut) = await RunAsync(new DevToolsDiagnoseBindingCommand(), agent, ["42", "IsEnabled", "--json"]);

        Assert.AreEqual(1, humanExit);
        Assert.AreEqual(humanExit, jsonExit,
            "ok:true for state:unavailable is the reading most likely to make an agent call a broken binding fine.");
        using var doc = JsonDocument.Parse(jsonOut);
        Assert.IsFalse(doc.RootElement.GetProperty("ok").GetBoolean());
        StringAssert.Contains(doc.RootElement.GetProperty("error").GetProperty("message").GetString()!, "managed DevTools agent",
            "The agent's own reason must reach the caller.");
    }

    [TestMethod]
    public async Task DiagnoseBinding_Broken_IsADiagnosisAndSucceeds()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", AuthoredTree(), IsAuthored).Answer("VisualTree.enumerate", Tree)
            .Answer("Binding.diagnose", """{"state":"broken","path":"ViewModel.CanSave","segment":"CanSave","reason":"property not found"}""");

        var (humanExit, _) = await RunAsync(new DevToolsDiagnoseBindingCommand(), agent, ["42", "IsEnabled"]);
        var (jsonExit, jsonOut) = await RunAsync(new DevToolsDiagnoseBindingCommand(), agent, ["42", "IsEnabled", "--json"]);

        Assert.AreEqual(0, humanExit, "A broken binding is a successful DIAGNOSIS, not a failed command.");
        Assert.AreEqual(0, jsonExit);
        using var doc = JsonDocument.Parse(jsonOut);
        Assert.IsTrue(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.AreEqual("broken", doc.RootElement.GetProperty("result").GetProperty("state").GetString());
    }

    [TestMethod]
    public async Task DiagnoseBinding_HealthyReasonStaysInJsonOnly_AndBrokenReasonIsShown()
    {
        using (var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", AuthoredTree(), IsAuthored).Answer("VisualTree.enumerate", Tree)
            .Answer("Binding.diagnose", """{"state":"evaluated","path":"Title","reason":"Forward path and CLR type evaluated only"}"""))
        {
            var (_, human) = await RunAsync(new DevToolsDiagnoseBindingCommand(), agent, ["42", "IsEnabled"]);
            var (_, json) = await RunAsync(new DevToolsDiagnoseBindingCommand(), agent, ["42", "IsEnabled", "--json"]);
            Assert.IsFalse(human.Contains("Forward path", StringComparison.Ordinal), human);
            StringAssert.Contains(json, "Forward path and CLR type evaluated only");
        }

        using var broken = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", AuthoredTree(), IsAuthored).Answer("VisualTree.enumerate", Tree)
            .Answer("Binding.diagnose", """{"state":"bad-segment","path":"Missing","reason":"no property or field 'Missing'"}""");
        StringAssert.Contains((await RunAsync(new DevToolsDiagnoseBindingCommand(), broken, ["42", "IsEnabled"])).Output,
            "reason: no property or field 'Missing'");
    }

    [TestMethod]
    public async Task GetProperty_UnknownPropertySaysProperties()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", AuthoredTree(), IsAuthored).Answer("VisualTree.enumerate", Tree)
            .Answer("Property.get", """{"props":[{"name":"Text","value":"a","valueType":"String"},{"name":"Width","value":"1","valueType":"Double"}]}""");
        var (exit, output) = await RunAsync(new DevToolsGetPropertyCommand(), agent, ["42", "Missing"]);
        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "among the 2 properties");
        Assert.IsFalse(output.Contains("(ies)", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>-p Foreground --json</c> used to return all 249 rows: the raw payload was emitted before the
    /// command had applied the selection the caller asked for.
    /// </summary>
    [TestMethod]
    public async Task GetProperty_SingleProperty_ReturnsOnlyThatPropertyInJson()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", AuthoredTree(), IsAuthored).Answer("VisualTree.enumerate", Tree)
            .Answer("Property.get",
                """
                {"handle":"42","authoredState":"available","props":[
                  {"name":"Width","value":"120","valueType":"Double","valueSource":"Local","editKind":"text"},
                  {"name":"Foreground","value":"#C5FFFFFF","valueType":"SolidColorBrush","valueSource":"Local",
                   "authored":"{ThemeResource K}","authoredKind":"themeResource","authoredKey":"K","editKind":"none"},
                  {"name":"Opacity","value":"1","valueType":"Double","valueSource":"Default","editKind":"text"}]}
                """);

        var (exit, output) = await RunAsync(new DevToolsGetPropertyCommand(), agent, ["42", "-p", "Foreground", "--json"]);

        Assert.AreEqual(0, exit, $"Output was: {output}");
        using var doc = JsonDocument.Parse(output);
        var properties = doc.RootElement.GetProperty("properties");
        Assert.AreEqual(1, properties.GetArrayLength(), "--property must select in JSON exactly as it does for humans.");
        Assert.AreEqual("Foreground", properties[0].GetProperty("name").GetString());
        Assert.AreEqual(3, doc.RootElement.GetProperty("totalProperties").GetInt32(),
            "The full count is still reported, so the caller knows what was filtered.");
        // A resolved ThemeResource must not read as a plain Local value in JSON either.
        Assert.AreEqual("ThemeResource K", properties[0].GetProperty("source").GetString());
    }

    [TestMethod]
    public async Task GetProperty_CompactDefault_OmitsDefaultsInJsonToo()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", AuthoredTree(), IsAuthored).Answer("VisualTree.enumerate", Tree)
            .Answer("Property.get",
                """
                {"handle":"42","authoredState":"available","props":[
                  {"name":"Width","value":"120","valueType":"Double","valueSource":"Local","editKind":"text"},
                  {"name":"Opacity","value":"1","valueType":"Double","valueSource":"Default","editKind":"text"}]}
                """);

        var (_, compact) = await RunAsync(new DevToolsGetPropertyCommand(), agent, ["42", "--json"]);
        var (_, all) = await RunAsync(new DevToolsGetPropertyCommand(), agent, ["42", "--all", "--json"]);

        using var compactDoc = JsonDocument.Parse(compact);
        using var allDoc = JsonDocument.Parse(all);
        Assert.AreEqual(1, compactDoc.RootElement.GetProperty("properties").GetArrayLength());
        Assert.AreEqual(2, allDoc.RootElement.GetProperty("properties").GetArrayLength());
    }

    /// <summary>
    /// a node-capped search that found one <c>x:Name</c> may simply not have looked at a duplicate, so a
    /// WRITE through that name is refused outright. Warning-and-proceeding was not enough — <c>--json</c>
    /// suppressed even the warning, and the write still landed.
    /// </summary>
    [TestMethod]
    public async Task SetProperty_ByName_IsRefusedWhenTheSearchWasTruncated()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.find", Find(SaveButtonMatch, truncated: true))
            .Answer("VisualTree.enumerate", AuthoredTree(), IsAuthored).Answer("VisualTree.enumerate", Tree)
            .Answer("Property.get", """{"handle":"42","authoredState":"available","props":[{"name":"Width","value":"120","valueType":"Double","valueSource":"Local","editKind":"text","writeType":"Double"}]}""")
            .Answer("HotReload.setProperty", "null");

        var (exit, output) = await RunAsync(
            new DevToolsSetPropertyCommand(), agent, ["SaveButton", "200", "--property", "Width", "--json"]);

        Assert.AreEqual(1, exit);
        using var doc = JsonDocument.Parse(output);
        Assert.IsFalse(doc.RootElement.GetProperty("ok").GetBoolean());
        StringAssert.Contains(doc.RootElement.GetProperty("error").GetProperty("message").GetString()!, "Refusing to write");
        CollectionAssert.DoesNotContain(agent.Received, "HotReload.setProperty",
            "The write must not reach the app at all when the name could not be shown to be unique.");
    }

    /// <summary>A handle is unambiguous by construction, so a truncated census does not block a write through one.</summary>
    [TestMethod]
    public async Task SetProperty_ByHandle_IsUnaffectedByATruncatedCensus()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.find", Find(SaveButtonMatch, truncated: true))
            .Answer("VisualTree.enumerate", AuthoredTree(), IsAuthored).Answer("VisualTree.enumerate", Tree)
            .Answer("Property.get", """{"handle":"42","authoredState":"available","props":[{"name":"Width","value":"200","valueType":"Double","valueSource":"Local","editKind":"text","writeType":"Double"}]}""")
            .Answer("HotReload.setProperty", "null");

        var (exit, output) = await RunAsync(
            new DevToolsSetPropertyCommand(), agent, ["42", "200", "--property", "Width", "--json"]);

        Assert.AreEqual(0, exit);
        using var doc = JsonDocument.Parse(output);
        Assert.IsTrue(doc.RootElement.GetProperty("ok").GetBoolean());
        CollectionAssert.Contains(agent.Received, "HotReload.setProperty");
        CollectionAssert.DoesNotContain(agent.Received, "VisualTree.find",
            "A handle needs no name lookup at all.");
    }

    /// <summary>A READ may proceed on a truncated lookup, but the caveat must reach a <c>--json</c> consumer.</summary>
    [TestMethod]
    public async Task GetProperty_ByName_CarriesTheTruncationWarningIntoJson()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.find", Find(SaveButtonMatch, truncated: true))
            .Answer("VisualTree.enumerate", AuthoredTree(), IsAuthored).Answer("VisualTree.enumerate", Tree)
            .Answer("Property.get", """{"handle":"42","authoredState":"available","props":[{"name":"Width","value":"120","valueType":"Double","valueSource":"Local","editKind":"text"}]}""");

        var (exit, output) = await RunAsync(new DevToolsGetPropertyCommand(), agent, ["SaveButton", "--json"]);

        Assert.AreEqual(0, exit, "A read is still useful; it just cannot promise the name was unique.");
        using var doc = JsonDocument.Parse(output);
        StringAssert.Contains(doc.RootElement.GetProperty("warning").GetString()!, "node limit");
    }

    /// <summary>
    /// a truncated <c>x:Name</c> lookup resolved this root. Without the caveat, JSON hands back an
    /// apparently unambiguous handle that a caller would then pass to <c>set-property</c> — routing straight
    /// around the name-based mutation refusal, which only fires when the NAME is what gets written through.
    /// </summary>
    [TestMethod]
    public async Task Inspect_ByName_CarriesTheTruncationWarningIntoJson()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.find", Find(SaveButtonMatch, truncated: true))
            .Answer("VisualTree.enumerate", AuthoredTree(), IsAuthored).Answer("VisualTree.enumerate", Tree);

        var (exit, output) = await RunAsync(new DevToolsInspectCommand(), agent, ["SaveButton", "--json"]);

        Assert.AreEqual(0, exit);
        using var doc = JsonDocument.Parse(output);
        Assert.IsTrue(doc.RootElement.TryGetProperty("warning", out var warning), $"No warning in: {output}");
        StringAssert.Contains(warning.GetString()!, "node limit");
    }

    [TestMethod]
    public async Task InspectAncestors_ByName_CarriesTheTruncationWarningIntoJson()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.find", Find(SaveButtonMatch, truncated: true))
            .Answer("VisualTree.enumerate", AuthoredTree(), IsAuthored).Answer("VisualTree.enumerate", Tree);

        var (exit, output) = await RunAsync(new DevToolsInspectCommand(), agent, ["SaveButton", "--ancestors", "--json"]);

        Assert.AreEqual(0, exit);
        using var doc = JsonDocument.Parse(output);
        Assert.IsTrue(doc.RootElement.TryGetProperty("warning", out var warning), $"No warning in: {output}");
        StringAssert.Contains(warning.GetString()!, "node limit");
    }

    [TestMethod]
    public async Task Inspect_ByHandle_HasNoWarning()
    {
        using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.enumerate", AuthoredTree(), IsAuthored).Answer("VisualTree.enumerate", Tree);

        var (_, output) = await RunAsync(new DevToolsInspectCommand(), agent, ["42", "--json"]);

        using var doc = JsonDocument.Parse(output);
        Assert.IsFalse(doc.RootElement.TryGetProperty("warning", out _),
            "A handle is unambiguous by construction; a warning there would be noise.");
    }

    /// <summary>
    /// filtering must not narrow the protocol payload. Rebuilding each row from the CLI's own record
    /// dropped every wire field it does not model — <c>editKind</c>, <c>chain</c>, <c>children</c>,
    /// <c>enumValues</c>, <c>valueState</c> — and <c>chain</c> is the strongest "why did this value win?"
    /// evidence the protocol carries.
    /// </summary>
    [TestMethod]
    public async Task GetProperty_Json_CopiesEveryWireFieldOfARetainedRow()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", AuthoredTree(), IsAuthored).Answer("VisualTree.enumerate", Tree)
            .Answer("Property.get",
                """
                {"handle":"42","authoredState":"available","props":[
                  {"name":"Foreground","value":"#C5FFFFFF","valueType":"SolidColorBrush","valueSource":"Local",
                   "editKind":"brush","valueState":"set","enumValues":["a","b"],"fields":["A","R","G","B"],
                   "writeType":"SolidColorBrush","authored":"{ThemeResource K}","authoredKind":"themeResource","authoredKey":"K",
                   "chain":[{"source":"Local","value":"#C5FFFFFF","winner":true},{"source":"Default","value":"#FF000000","winner":false}],
                   "children":[{"name":"Color","value":"#C5FFFFFF","valueType":"Color","editKind":"text"}]}]}
                """);

        var (exit, output) = await RunAsync(new DevToolsGetPropertyCommand(), agent, ["42", "-p", "Foreground", "--json"]);

        Assert.AreEqual(0, exit);
        using var doc = JsonDocument.Parse(output);
        var row = doc.RootElement.GetProperty("properties")[0];
        foreach (var field in new[]
                 {
                     "name", "value", "valueType", "valueSource", "editKind", "valueState", "enumValues",
                     "fields", "writeType", "authored", "authoredKind", "authoredKey", "chain", "children",
                 })
        {
            Assert.IsTrue(row.TryGetProperty(field, out _), $"'{field}' was dropped from the retained row.");
        }

        Assert.AreEqual(2, row.GetProperty("chain").GetArrayLength(), "The full precedence chain must survive.");
        Assert.IsTrue(row.GetProperty("chain")[0].GetProperty("winner").GetBoolean());
        Assert.AreEqual("Color", row.GetProperty("children")[0].GetProperty("name").GetString());

        // Derived fields are APPENDED, never a replacement for the wire view.
        Assert.AreEqual("ThemeResource K", row.GetProperty("source").GetString());
        Assert.IsTrue(row.GetProperty("isSet").GetBoolean());
    }

    /// <summary>
    /// <c>authoredState</c> is a per-element fact the result contract requires. Dropping it turns "we
    /// could not look" into "there is nothing authored here" — the confidently-wrong answer it exists to stop.
    /// </summary>
    [TestMethod]
    public async Task GetProperty_Json_CarriesAuthoredState()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", AuthoredTree(), IsAuthored).Answer("VisualTree.enumerate", Tree)
            .Answer("Property.get",
                """
                {"handle":"42","authoredState":"noSourceInfo","props":[
                  {"name":"Width","value":"120","valueType":"Double","valueSource":"Local","editKind":"text"}]}
                """);

        var (_, output) = await RunAsync(new DevToolsGetPropertyCommand(), agent, ["42", "--json"]);

        using var doc = JsonDocument.Parse(output);
        Assert.AreEqual("noSourceInfo", doc.RootElement.GetProperty("authoredState").GetString());
    }

    /// <summary>
    /// <c>error</c> is an object everywhere. A semantic no-result emitting a bare string while a protocol
    /// refusal emits an object makes an agent that branches on <c>error.token</c> throw on the ordinary case.
    /// </summary>
    [TestMethod]
    public async Task Error_IsAlwaysAnObjectWithCodeTokenAndMessage()
    {
        var cases = new List<(string Label, string Output, string ExpectedToken)>();

        using (var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.find", Find("[]")))
        {
            var (_, o) = await RunAsync(new DevToolsSearchCommand(), agent, ["__missing__", "--json"]);
            cases.Add(("search no-match", o, "no-match"));
        }

        using (var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.enumerate", AuthoredTree(), IsAuthored).Answer("VisualTree.enumerate", Tree))
        {
            var (_, o) = await RunAsync(new DevToolsInspectCommand(), agent, ["--filter", "__missing__", "--json"]);
            cases.Add(("inspect no-match", o, "no-match"));
        }

        using (var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", AuthoredTree(), IsAuthored).Answer("VisualTree.enumerate", Tree)
            .Answer("Source.get", """{"handle":"42","fileName":"","lineNumber":0,"columnNumber":0,"authoredState":"noSourceInfo"}"""))
        {
            var (_, o) = await RunAsync(new DevToolsGetSourceCommand(), agent, ["42", "--json"]);
            cases.Add(("get-source unavailable", o, "source-unavailable"));
        }

        using (var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", AuthoredTree(), IsAuthored).Answer("VisualTree.enumerate", Tree)
            .Answer("Binding.diagnose", """{"state":"unavailable","reason":"no managed agent"}"""))
        {
            var (_, o) = await RunAsync(new DevToolsDiagnoseBindingCommand(), agent, ["42", "IsEnabled", "--json"]);
            cases.Add(("diagnose-binding unavailable", o, "binding-unavailable"));
        }

        // The protocol-failure shape, for comparison: it was already an object and must stay one.
        using (var agent = new FakeDevToolsProtocolAgent().Error("Property.get", -32601, "unknown", "Protocol refusal"))
        {
            var (_, o) = await RunAsync(new DevToolsGetPropertyCommand(), agent, ["42", "--json"]);
            cases.Add(("protocol refusal", o, "unknown"));
        }

        foreach (var (label, output, expectedToken) in cases)
        {
            using var doc = JsonDocument.Parse(output);
            Assert.IsFalse(doc.RootElement.GetProperty("ok").GetBoolean(), $"{label}: expected ok:false.");
            Assert.IsTrue(doc.RootElement.TryGetProperty("error", out var error), $"{label}: no error member.");
            Assert.AreEqual(JsonValueKind.Object, error.ValueKind,
                $"{label}: error must be an object, not a bare string — an agent branches on error.token.");
            Assert.AreEqual(JsonValueKind.Number, error.GetProperty("code").ValueKind, $"{label}: no code.");
            Assert.AreEqual(expectedToken, error.GetProperty("token").GetString(), $"{label}: wrong token.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()), $"{label}: empty message.");
        }
    }

    [TestMethod]
    [DataRow("binding", "{Binding Title, Mode=OneWay}", "Local", null)]
    [DataRow("xBind", "{x:Bind ViewModel.Title}", "Local", null)]
    [DataRow("themeResource", "{ThemeResource TitleBrush}", "Local", null)]
    [DataRow("binding", "{Binding Title}", "Local", "{Binding Title}")]
    [DataRow("binding", "{Binding Title}", "Style", null)]
    [DataRow("binding", "{Binding Title}", null, null)]
    public async Task SetProperty_SeparatesRuntimeProvenanceFromAuthoredExpression(
        string authoredKind, string authored, string? valueSource, string? binding)
    {
        var afterRow = JsonSerializer.Serialize(new
        {
            name = "Text", value = "override", valueType = "String", writeType = "String",
            valueSource, binding, authored, authoredKind,
        });
        var reads = 0;
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("Property.get",
                """{"props":[{"name":"Text","value":"seed","valueType":"String","writeType":"String","binding":"{Binding Title}"}]}""",
                _ => Interlocked.Increment(ref reads) == 1)
            .Answer("Property.get", $$"""{"authoredState":"available","props":[{{afterRow}}]}""")
            .Answer("HotReload.setProperty", "null")
            .Answer("VisualTree.enumerate", Tree);

        var (exit, output) = await RunAsync(
            new DevToolsSetPropertyCommand(), agent, ["42", "override", "-p", "Text", "--json"]);

        Assert.AreEqual(0, exit);
        using var doc = JsonDocument.Parse(output);
        var result = doc.RootElement;
        Assert.AreEqual("seed", result.GetProperty("before").GetString());
        Assert.AreEqual("override", result.GetProperty("after").GetString());
        Assert.AreEqual(valueSource is not null, result.TryGetProperty("valueSource", out var reportedSource));
        if (valueSource is not null)
        {
            Assert.AreEqual(valueSource, reportedSource.GetString());
        }
        Assert.AreEqual(authored, result.GetProperty("authored").GetString());
        Assert.AreEqual(binding is not null, result.TryGetProperty("binding", out var reportedBinding));
        if (binding is not null)
        {
            Assert.AreEqual(binding, reportedBinding.GetString());
        }

        var (readExit, readOutput) = await RunAsync(
            new DevToolsGetPropertyCommand(), agent, ["42", "-p", "Text", "--json"]);
        Assert.AreEqual(0, readExit);
        using var read = JsonDocument.Parse(readOutput);
        var property = read.RootElement.GetProperty("properties")[0];
        Assert.AreEqual(valueSource, property.GetProperty("valueSource").GetString());
        Assert.AreEqual(authored, property.GetProperty("authored").GetString());
    }

    [TestMethod]
    [DataRow("""{"binding":"{Binding Title}"}""", """{}""", "{Binding Title}", "This replaced the binding {Binding Title}")]
    [DataRow("""{"binding":"{Binding Title}"}""", """{"binding":"{Binding Title}"}""", null, null)]
    [DataRow("""{"authored":"{x:Bind Label}","authoredKind":"xBind"}""", """{"authored":"{x:Bind Label}","authoredKind":"xBind"}""", "{x:Bind Label}", "This overrode {x:Bind Label}")]
    [DataRow("""{}""", """{}""", null, null)]
    public async Task SetProperty_ReportsAReplacedBinding(string beforeExtra, string afterExtra, string? replaced, string? warning)
    {
        static string Row(string value, string extra)
        {
            var row = System.Text.Json.Nodes.JsonNode.Parse(extra)!.AsObject();
            row["name"] = "Text"; row["value"] = value; row["valueType"] = "String"; row["writeType"] = "String";
            return $$"""{"props":[{{row.ToJsonString()}}]}""";
        }
        FakeDevToolsProtocolAgent Agent()
        {
            var reads = 0;
            return new FakeDevToolsProtocolAgent()
                .Answer("Property.get", Row("seed", beforeExtra), _ => Interlocked.Increment(ref reads) == 1)
                .Answer("Property.get", Row("override", afterExtra))
                .Answer("HotReload.setProperty", "null")
                .Answer("VisualTree.enumerate", Tree);
        }

        using (var agent = Agent())
        {
            var (exit, output) = await RunAsync(new DevToolsSetPropertyCommand(), agent, ["42", "override", "-p", "Text", "--json"]);
            Assert.AreEqual(0, exit, output);
            using var doc = JsonDocument.Parse(output);
            Assert.AreEqual(replaced, doc.RootElement.TryGetProperty("replacedBinding", out var value) ? value.GetString() : null);
        }
        using (var agent = Agent())
        {
            var (exit, output) = await RunAsync(new DevToolsSetPropertyCommand(), agent, ["42", "override", "-p", "Text"]);
            Assert.AreEqual(0, exit, output);
            if (warning is null)
            {
                Assert.IsFalse(output.Contains("binding", StringComparison.OrdinalIgnoreCase) || output.Contains("overrode", StringComparison.Ordinal), output);
            }
            else
            {
                StringAssert.Contains(output, warning);
            }
        }
    }

    [TestMethod]
    public async Task SetProperty_UnconfirmedHumanWrite_DoesNotCallAuthoredBindingEffective()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("Property.get",
                """
                {"props":[{"name":"Text","value":"unchanged","valueType":"String","writeType":"String",
                  "valueSource":"Local","authored":"{Binding OldTitle}","authoredKind":"binding"}]}
                """)
            .Answer("HotReload.setProperty", "null")
            .Answer("VisualTree.enumerate", Tree);

        var (exit, output) = await RunAsync(
            new DevToolsSetPropertyCommand(), agent, ["42", "requested", "-p", "Text"]);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "Its effective value comes from Local.");
        Assert.IsFalse(output.Contains("OldTitle", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SetProperty_UnconfirmedWrite_ReportsAnErrorObject()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", AuthoredTree(), IsAuthored).Answer("VisualTree.enumerate", Tree)
            // The value reads back unchanged and does not match the request: a style or binding won.
            .Answer("Property.get", """{"handle":"42","authoredState":"available","props":[{"name":"Width","value":"120","valueType":"Double","valueSource":"Style","editKind":"text","writeType":"Double"}]}""")
            .Answer("HotReload.setProperty", "null");

        var (exit, output) = await RunAsync(
            new DevToolsSetPropertyCommand(), agent, ["42", "200", "--property", "Width", "--json"]);

        Assert.AreEqual(1, exit);
        using var doc = JsonDocument.Parse(output);
        var error = doc.RootElement.GetProperty("error");
        Assert.AreEqual(JsonValueKind.Object, error.ValueKind);
        Assert.AreEqual("write-unconfirmed", error.GetProperty("token").GetString());
    }

    // Builds the command's handler by hand rather than through DI: these tests supply their own resolver, and
    // the console has to be the one whose output is asserted.
    private static async Task<(int ExitCode, string Output)> RunAsync(
        DevToolsLiveCommand command, FakeDevToolsProtocolAgent agent, string[] args)
    {
        var console = new TestConsole();
        console.Profile.Width = 200; // Don't let console wrapping split a phrase an assertion matches on.
        System.CommandLine.Invocation.AsynchronousCommandLineAction handler = command switch
        {
            DevToolsInspectCommand => new DevToolsInspectCommand.Handler(agent.Resolver(), console),
            DevToolsSearchCommand => new DevToolsSearchCommand.Handler(agent.Resolver(), console),
            DevToolsGetPropertyCommand => new DevToolsGetPropertyCommand.Handler(agent.Resolver(), console),
            DevToolsGetSourceCommand => new DevToolsGetSourceCommand.Handler(agent.Resolver(), console),
            DevToolsDiagnoseBindingCommand => new DevToolsDiagnoseBindingCommand.Handler(agent.Resolver(), console),
            DevToolsSetPropertyCommand => new DevToolsSetPropertyCommand.Handler(agent.Resolver(), console),
            _ => throw new NotSupportedException(command.Name),
        };

        command.Action = handler;
        var exitCode = await command.Parse(args).InvokeAsync();
        return (exitCode, console.Output);
    }
}

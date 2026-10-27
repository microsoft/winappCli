// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

[TestClass]
public class ReactorTreeTests
{
    // CA1861: expectations read best inline; the params array is built per call on purpose.
    private static string[] Expected(params string[] values) => values;

    // App (class component) -> FlexColumn -> [TextBlock, <Counter> -> FlexRow -> Button]
    private const string Described = """
        {"state":"ok","version":"0.1.0-preview.16","sourceMap":true,
         "nodes":[
           {"handle":"10","element":"Component","file":0,"line":8,"component":{"kind":"class","name":"App","type":"App"}},
           {"handle":"11","parent":"10","element":"FlexColumn","file":0,"line":24},
           {"handle":"12","parent":"11","element":"TextBlock","file":0,"line":18},
           {"handle":"13","parent":"11","element":"Component","file":0,"line":19,"component":{"kind":"class","name":"Counter","type":"Counter"}},
           {"handle":"14","parent":"13","element":"FlexRow","file":1,"line":35},
           {"handle":"15","parent":"14","element":"Button","key":"inc"},
           {"handle":"16","parent":"999","element":"TextBlock","file":1,"line":40}
         ],
         "files":["C:\\src\\App.cs","C:\\src\\Counter.cs"],"unmapped":2,"truncated":false}
        """;

    [TestMethod]
    public void Parse_NestsByReactorParent_AndResolvesFiles()
    {
        var reactor = ReactorDescription.Parse(Described)!;

        Assert.IsTrue(reactor.IsReactor);
        Assert.AreEqual("0.1.0-preview.16", reactor.Version);
        Assert.AreEqual(true, reactor.SourceMap);
        Assert.AreEqual(2, reactor.Unmapped);
        CollectionAssert.AreEqual(Expected("10", "16"), reactor.Roots.Select(root => root.Handle).ToArray());

        var counter = reactor.ByHandle["13"];
        Assert.AreEqual("Counter", counter.Component!.Name);
        Assert.AreEqual(@"C:\src\App.cs:19", counter.Source);
        var row = counter.Children.Single();
        Assert.AreEqual(@"C:\src\Counter.cs", row.File);
        Assert.AreEqual("inc", row.Children.Single().Key);
        Assert.IsNull(row.Children.Single().Source);
    }

    [TestMethod]
    public void Parse_RejectsMissingState()
    {
        Assert.IsNull(ReactorDescription.Parse("""{"nodes":[]}"""));
        Assert.IsNull(ReactorDescription.Parse("not json"));
    }

    [TestMethod]
    public void ExplainIfEmpty_TellsNonReactorAppsWhatToUse()
    {
        var absent = ReactorDescription.Parse("""{"state":"absent","nodes":[],"files":[],"truncated":false}""")!;
        StringAssert.Contains(absent.ExplainIfEmpty(), "does not use Microsoft.UI.Reactor");

        var noAgent = ReactorDescription.Parse("""{"state":"unavailable","reason":"no managed host","nodes":[],"files":[],"truncated":false}""")!;
        StringAssert.Contains(noAgent.ExplainIfEmpty(), "winapp run --devtools on");

        Assert.IsNull(ReactorDescription.Parse(Described)!.ExplainIfEmpty());
    }

    [TestMethod]
    public void ExplainMissingSource_PointsAtTheSourceMapSwitch()
    {
        var off = ReactorDescription.Parse("""{"state":"ok","sourceMap":false,"nodes":[{"handle":"1","element":"Button"}],"files":[],"truncated":false}""")!;
        StringAssert.Contains(off.ExplainMissingSource(), "REACTOR_SOURCEMAP=1");
        Assert.IsNull(ReactorDescription.Parse(Described)!.ExplainMissingSource());
    }

    // Two windows: Main (root <App>, class) and Settings (root rendered by a function), and a <Counter> in each.
    private const string Annotated = """
        {"nodes":[
           {"handle":"1","type":"Microsoft.UI.Xaml.Controls.StackPanel","childCount":2,"children":[
             {"handle":"2","type":"Microsoft.UI.Xaml.Controls.Border","childCount":1,"children":[
               {"handle":"3","type":"Microsoft.UI.Xaml.Controls.Button","childCount":0,"children":[]}]},
             {"handle":"4","type":"Microsoft.UI.Xaml.Controls.TextBlock","name":"Title","file":"ms-appx:///MainPage.xaml","childCount":0,"children":[]}]},
           {"handle":"5","type":"Microsoft.UI.Xaml.Controls.Grid","childCount":1,"children":[
             {"handle":"6","type":"Microsoft.UI.Xaml.Controls.Border","childCount":0,"children":[]}]}],
         "authored":true,"sourceInstrumented":true,"classificationTruncated":false,"classifiedNodes":6,"censusNodes":40,"authoredNodes":6,"classifier":"allowlist",
         "windows":[{"window":"100","title":"TaskBoard","surface":"TaskBoard","roots":["1"]},{"window":"200","title":"Settings","surface":"Settings","roots":["5"]}],
         "reactor":{"present":true,"agent":"ok","version":"0.1.0","sourceMap":true,
           "nodes":[
             {"handle":"1","element":"FlexColumn","file":0,"line":10,"host":{"kind":"window","window":"100","windowId":"win-1","title":"TaskBoard","root":{"kind":"class","name":"App","type":"TaskBoard.App"}}},
             {"handle":"2","parent":"1","element":"Component","file":0,"line":12,"component":{"kind":"class","name":"Counter","type":"TaskBoard.Counter"}},
             {"handle":"3","parent":"2","element":"Button","key":"inc","file":1,"line":7},
             {"handle":"5","element":"Grid","host":{"kind":"window","window":"200","windowId":"win-2","windowKey":"Settings","title":"Settings","root":{"kind":"function","name":"render in App.Settings"}}},
             {"handle":"6","parent":"5","element":"Component","file":0,"line":30,"component":{"kind":"class","name":"Counter","type":"TaskBoard.Counter"}}],
           "files":["C:\\src\\App.cs","C:\\src\\Counter.cs"],"unmapped":0,"truncated":false}}
        """;

    private static DevToolsTreeAnnotations Annotations(bool raw = false)
    {
        var forest = AuthoredForest.Parse(Annotated)!;
        return new DevToolsTreeAnnotations(forest.Reactor, forest.Windows, raw);
    }

    private static VisualTreeNode Node(string handle) =>
        DevToolsPreviews.Flatten(AuthoredForest.Parse(Annotated)!.Roots).Single(node => node.Handle == handle);

    [TestMethod]
    public void AuthoredForest_ParsesWindowsAndReactorAnnotations()
    {
        var forest = AuthoredForest.Parse(Annotated)!;

        Assert.AreEqual(2, forest.Windows.Count);
        Assert.AreEqual("Settings", forest.Windows[1].Title);
        Assert.IsTrue(forest.Reactor!.IsReactor);
        var settings = forest.Reactor.ByHandle["5"].Host!;
        Assert.AreEqual("200", settings.Window);
        Assert.AreEqual("Settings", settings.WindowKey);
        Assert.AreEqual("function", settings.Root!.Kind);
    }

    [TestMethod]
    public void ParseAnnotations_DistinguishesAbsentAndUnreadable()
    {
        using var absent = System.Text.Json.JsonDocument.Parse("""{"present":false,"agent":"ok"}""");
        Assert.IsFalse(ReactorDescription.ParseAnnotations(absent.RootElement)!.Present);

        using var noAgent = System.Text.Json.JsonDocument.Parse("""{"present":true,"agent":"absent"}""");
        var unreadable = ReactorDescription.ParseAnnotations(noAgent.RootElement)!;
        Assert.IsTrue(unreadable.Present);
        Assert.IsFalse(unreadable.IsReactor);
        Assert.AreEqual(ReactorDescription.UnavailableToken, unreadable.Token);
        StringAssert.Contains(unreadable.ExplainUnavailable(), "winapp run --devtools on");
    }

    [TestMethod]
    public void ParseAnnotations_PublishedDiagnosticsDescribeWithoutTheAgent()
    {
        using var published = System.Text.Json.JsonDocument.Parse("""
            {"present":true,"agent":"absent","annotations":"published","reason":"the app has no .NET runtime (Native AOT)",
             "nodes":[{"handle":"7","element":"Button","file":0,"line":80}],"files":["C:\\src\\App.cs"],"unmapped":0,"truncated":false}
            """);
        var tree = ReactorDescription.ParseAnnotations(published.RootElement)!;
        Assert.IsTrue(tree.IsReactor);
        Assert.AreEqual("published", tree.Annotations);
        Assert.AreEqual(80, tree.ByHandle["7"].Line);

        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            new DevToolsTreeAnnotations(tree, [], raw: false).WriteEnvelope(writer);
            writer.WriteEndObject();
        }
        using var envelope = System.Text.Json.JsonDocument.Parse(stream.ToArray());
        var reactor = envelope.RootElement.GetProperty("reactor");
        Assert.AreEqual("absent", reactor.GetProperty("agent").GetString(), "the summary must not claim an agent");
        Assert.AreEqual("published", reactor.GetProperty("annotations").GetString());

        using var unpublished = System.Text.Json.JsonDocument.Parse("""{"present":true,"agent":"absent","annotations":"published"}""");
        Assert.IsFalse(ReactorDescription.ParseAnnotations(unpublished.RootElement)!.IsReactor,
            "published diagnostics that could not be read leave the tree unavailable");
    }

    [TestMethod]
    public void Annotations_NoteOnlyWhenReactorIsPresentButUnreadable()
    {
        Assert.IsNull(Annotations().Note);
        Assert.IsNull(DevToolsTreeAnnotations.Empty.Note);

        using var noAgent = System.Text.Json.JsonDocument.Parse("""{"present":true,"agent":"absent"}""");
        var annotations = new DevToolsTreeAnnotations(ReactorDescription.ParseAnnotations(noAgent.RootElement), [], raw: false);
        Assert.AreEqual(ReactorDescription.UnavailableToken, annotations.Note!.Value.Token);
    }

    [TestMethod]
    public void Annotations_AWindowShowingReactorsErrorScreenSaysSo()
    {
        // A commit-phase error replaced the TaskBoard window's root with Reactor's error screen: its elements are gone, so
        // the note must name the error screen, not missing C# locations, and the root line must say what it shows.
        var forest = AuthoredForest.Parse(Annotated.Replace("""
            "type":"TaskBoard.App"}}}
            """, """
            "type":"TaskBoard.App"},"errorScreen":"Render error: ArgumentException: ShowAttached failed."}}
            """, StringComparison.Ordinal))!;
        var annotations = new DevToolsTreeAnnotations(forest.Reactor, forest.Windows, raw: false);

        var note = annotations.Note!.Value;
        Assert.AreEqual(ReactorDescription.ErrorScreenToken, note.Token);
        StringAssert.Contains(note.Message, "Window \"TaskBoard\" shows Reactor's error screen instead of <App>: Render error: ArgumentException: ShowAttached failed.");
        StringAssert.Contains(note.Message, "Hot Reload");
        StringAssert.Contains(Spectre.Console.Markup.Remove(annotations.RootLine(Node("1"))!), "<App> root showing Reactor's error screen");
        Assert.IsFalse(Spectre.Console.Markup.Remove(Annotations().RootLine(Node("1"))!).Contains("error screen", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Line_ShowsComponentKeyAndCallSite_AndKeepsXamlLines()
    {
        var annotations = Annotations();

        StringAssert.Contains(annotations.Line(Node("2")), "<Counter>");
        StringAssert.Contains(annotations.Line(Node("2")), "App.cs:12");
        Assert.IsFalse(annotations.Line(Node("2")).Contains("Border"), "a component line hides its wrapper Border");
        StringAssert.Contains(annotations.Line(Node("3")), "key=inc");
        StringAssert.Contains(annotations.Line(Node("3")), "Counter.cs:7");
        StringAssert.Contains(annotations.Line(Node("4")), "MainPage.xaml");
    }

    [TestMethod]
    public void Line_InTheRawTree_KeepsTheWinUITypeAndAddsTheReactorElement()
    {
        var line = Annotations(raw: true).Line(Node("2"));

        StringAssert.Contains(line, "Border");
        StringAssert.Contains(line, "<Counter>");
        Assert.IsNull(Annotations(raw: true).RootLine(Node("1")), "--all shows the root inline, not as its own row");
        StringAssert.Contains(Annotations(raw: true).Line(Node("1")), "<App>");
    }

    [TestMethod]
    public void RootLine_SynthesizesEachHostsRootComponent()
    {
        var annotations = Annotations();

        StringAssert.Contains(annotations.RootLine(Node("1")), "<App>");
        StringAssert.Contains(annotations.RootLine(Node("5")), "<render in App.Settings>");
        Assert.IsNull(annotations.RootLine(Node("2")));
    }

    [TestMethod]
    public void Group_SplitsTopLevelElementsByWindow_WithReactorIdentity()
    {
        var annotations = Annotations();
        var groups = annotations.Group(AuthoredForest.Parse(Annotated)!.Roots);

        Assert.IsTrue(annotations.Grouped);
        CollectionAssert.AreEqual(Expected("100", "200"), groups.Select(group => group.Window!.Window).ToArray());
        StringAssert.Contains(annotations.WindowHeader(groups[1].Window), "key=Settings");
        StringAssert.Contains(annotations.WindowHeader(groups[1].Window), "-w 200");
    }

    [TestMethod]
    public void Group_IsOneUnlabelledGroupForASingleWindow()
    {
        var forest = AuthoredForest.Parse(Annotated)!;
        var annotations = new DevToolsTreeAnnotations(forest.Reactor, [forest.Windows[0]], raw: false);

        Assert.IsFalse(annotations.Grouped);
        Assert.IsNull(annotations.Group(forest.Roots).Single().Window);
    }

    [TestMethod]
    public void Matches_FindsComponentsKeysAndCallSites()
    {
        var annotations = Annotations();

        Assert.IsTrue(annotations.Matches(Node("2"), "counter"));
        Assert.IsTrue(annotations.Matches(Node("3"), "inc"));
        Assert.IsTrue(annotations.Matches(Node("3"), "counter.cs"));
        Assert.IsFalse(annotations.Matches(Node("4"), "counter"));
    }

    [TestMethod]
    public void ComponentMatches_FindsEveryInstanceAcrossWindows_AndRootComponents()
    {
        var reactor = AuthoredForest.Parse(Annotated)!.Reactor!;

        CollectionAssert.AreEquivalent(Expected("2", "6"),
            DevToolsSelector.ComponentMatches(reactor, "Counter", StringComparison.Ordinal).Select(node => node.Handle).ToArray());
        Assert.AreEqual("1", DevToolsSelector.ComponentMatches(reactor, "App", StringComparison.Ordinal).Single().Handle);
        Assert.AreEqual(2, DevToolsSelector.ComponentMatches(reactor, "TaskBoard.Counter", StringComparison.Ordinal).Count);
        Assert.AreEqual(0, DevToolsSelector.ComponentMatches(reactor, "counter", StringComparison.Ordinal).Count);
    }

    [TestMethod]
    public void ExplainMissingSource_IsSilentWhenNoReactorElementIsInScope()
    {
        var empty = ReactorDescription.Parse("""{"state":"ok","sourceMap":true,"nodes":[],"files":[],"truncated":false}""")!;
        Assert.IsNull(empty.ExplainMissingSource());
    }

    [TestMethod]
    public void ComponentName_RecognisesAngleBracketSelectorsOnly()
    {
        Assert.AreEqual("Counter", DevToolsSelector.ComponentName("<Counter>"));
        Assert.IsNull(DevToolsSelector.ComponentName("Counter"));
        Assert.IsNull(DevToolsSelector.ComponentName("<>"));
    }

    // <Counter> -> Button assigned to `incrementButton` (Reactor recorded it), and a TextBlock whose name was read from
    // the call site's line.
    private const string Named = """
        {"nodes":[
           {"handle":"1","type":"Microsoft.UI.Xaml.Controls.Border","childCount":2,"children":[
             {"handle":"2","type":"Microsoft.UI.Xaml.Controls.Button","childCount":0,"children":[]},
             {"handle":"3","type":"Microsoft.UI.Xaml.Controls.TextBlock","name":"Label","childCount":0,"children":[]}]}],
         "authored":true,"sourceInstrumented":true,"classificationTruncated":false,"classifiedNodes":3,"censusNodes":3,"authoredNodes":3,"classifier":"allowlist",
         "reactor":{"present":true,"agent":"ok","sourceMap":true,
           "nodes":[
             {"handle":"1","element":"Component","file":0,"line":66,"component":{"kind":"class","name":"Counter"}},
             {"handle":"2","parent":"1","element":"Button","file":0,"line":67,"column":13,"name":"incrementButton","nameSource":"csharp-variable","defaultAutomationName":true},
             {"handle":"3","parent":"1","element":"TextBlock","file":0,"line":68,"name":"label","nameSource":"csharp-source-text"}],
           "files":["C:\\src\\App.cs"],"unmapped":0,"truncated":false}}
        """;

    [TestMethod]
    public void Names_ReadLikeXNamesInTheTree()
    {
        var forest = AuthoredForest.Parse(Named)!;
        var annotations = new DevToolsTreeAnnotations(forest.Reactor, [], raw: false);
        var nodes = DevToolsPreviews.Flatten(forest.Roots).ToDictionary(node => node.Handle);

        var button = forest.Reactor!.ByHandle["2"];
        Assert.AreEqual("incrementButton", button.Name);
        Assert.AreEqual("csharp-variable", button.NameSource);
        Assert.IsTrue(button.DefaultAutomationName);
        Assert.AreEqual("csharp-source-text", forest.Reactor.ByHandle["3"].NameSource);
        Assert.IsNull(forest.Reactor.ByHandle["1"].Name, "an element built inline has no name");

        var line = Spectre.Console.Markup.Remove(annotations.Line(nodes["2"]));
        StringAssert.Contains(line, "Button #incrementButton App.cs:67");
        StringAssert.Contains(Spectre.Console.Markup.Remove(new DevToolsTreeAnnotations(forest.Reactor, [], raw: true).Line(nodes["2"])),
            "#incrementButton", "--all shows the name with the Reactor element too");
        Assert.IsTrue(annotations.Matches(nodes["2"], "#incrementbutton"), "a tree filter finds it by #name");

        // A Name the control itself has is its name; the C# variable does not repeat it.
        var own = Spectre.Console.Markup.Remove(annotations.Line(nodes["3"]));
        StringAssert.Contains(own, "#Label");
        Assert.IsFalse(own.Contains("#label", StringComparison.Ordinal), own);
    }

    [TestMethod]
    public void Names_AreWrittenToJsonWithWhereTheyCameFrom()
    {
        var forest = AuthoredForest.Parse(Named)!;
        var annotations = new DevToolsTreeAnnotations(forest.Reactor, [], raw: false);
        var nodes = DevToolsPreviews.Flatten(forest.Roots).ToDictionary(node => node.Handle);

        static System.Text.Json.JsonElement Extras(DevToolsTreeAnnotations annotations, VisualTreeNode node)
        {
            using var stream = new MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                annotations.WriteNodeExtras(writer, node);
                writer.WriteEndObject();
            }
            return System.Text.Json.JsonDocument.Parse(stream.ToArray()).RootElement.GetProperty("reactor").Clone();
        }

        var exact = Extras(annotations, nodes["2"]);
        Assert.AreEqual("incrementButton", exact.GetProperty("name").GetString());
        Assert.AreEqual("csharp-variable", exact.GetProperty("nameSource").GetString());
        Assert.AreEqual("csharp-source-text", Extras(annotations, nodes["3"]).GetProperty("nameSource").GetString());
        Assert.IsFalse(Extras(annotations, nodes["1"]).TryGetProperty("name", out _));
    }

    // A XAML app without source information hosting two Reactor islands: the authored view finds only the islands, in
    // the classifier's order; the raw tree has the XAML around them in visual order.
    private const string IslandReactor = """
        "reactor":{"present":true,"agent":"ok","sourceMap":false,
          "nodes":[
            {"handle":"20","element":"Border","host":{"kind":"island","window":"100","root":{"kind":"class","name":"InteropPanel"}}},
            {"handle":"10","element":"Border","host":{"kind":"island","window":"100","root":{"kind":"class","name":"ChartPanel"}}}],
          "files":[],"unmapped":0,"truncated":false}
        """;

    private const string IslandsOnly = """
        {"nodes":[
           {"handle":"20","type":"Microsoft.UI.Xaml.Controls.Border","childCount":0,"children":[]},
           {"handle":"10","type":"Microsoft.UI.Xaml.Controls.Border","childCount":0,"children":[]}],
         "authored":true,"sourceInstrumented":false,"classificationTruncated":false,"classifiedNodes":6,"censusNodes":6,"authoredNodes":2,
        """ + IslandReactor + "}";

    private const string IslandsInXaml = """
        {"nodes":[
           {"handle":"1","type":"Microsoft.UI.Xaml.Controls.Grid","childCount":3,"children":[
             {"handle":"2","type":"Microsoft.UI.Xaml.Controls.StackPanel","childCount":1,"children":[
               {"handle":"3","type":"Microsoft.UI.Xaml.Controls.Button","name":"SaveButton","uniqueName":true,"childCount":0,"children":[]}]},
             {"handle":"10","type":"Microsoft.UI.Xaml.Controls.Border","childCount":0,"children":[]},
             {"handle":"20","type":"Microsoft.UI.Xaml.Controls.Border","childCount":0,"children":[]}]}],
         "authored":false,"sourceInstrumented":false,
        """ + IslandReactor + "}";

    [TestMethod]
    [DoNotParallelize]
    public async Task Inspect_WithoutXamlSourceInfo_ShowsTheXamlAroundReactorIslandsInVisualOrder()
    {
        static bool Authored(string request) => request.Contains("\"authored\":true", StringComparison.Ordinal);
        foreach (var json in new[] { false, true })
        {
            using var agent = new FakeDevToolsProtocolAgent()
                .Answer("VisualTree.enumerate", IslandsOnly, Authored)
                .Answer("VisualTree.enumerate", IslandsInXaml);
            var console = new Spectre.Console.Testing.TestConsole();
            console.Profile.Width = 300;
            var command = new DevToolsInspectCommand();
            command.Action = new DevToolsInspectCommand.Handler(agent.Resolver(), console);
            var exit = await command.Parse(json ? ["--json"] : []).InvokeAsync();
            var output = console.Output;

            Assert.AreEqual(0, exit, output);
            Assert.AreEqual(2, agent.Received.Count(method => method == "VisualTree.enumerate"), "falls back to the whole tree");
            if (json)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(output);
                Assert.IsTrue(doc.RootElement.GetProperty("fallback").GetBoolean());
                Assert.IsFalse(doc.RootElement.GetProperty("appAuthored").GetBoolean());
                StringAssert.Contains(doc.RootElement.GetProperty("fallbackReason").GetString(), "no XAML source information");
                continue;
            }
            StringAssert.Contains(output, "reports no XAML source information");
            StringAssert.Contains(output, "winapp run --devtools on");
            StringAssert.Contains(output, "SaveButton", "the XAML around the islands is kept");
            Assert.IsTrue(output.IndexOf("<ChartPanel>", StringComparison.Ordinal) < output.IndexOf("<InteropPanel>", StringComparison.Ordinal),
                "islands keep their visual order: " + output);
        }
    }

    [TestMethod]
    public void ExplainHiddenXaml_OnlyWhenIslandsSitInUnclassifiedXaml()
    {
        var islands = AuthoredForest.Parse(IslandsOnly)!;
        StringAssert.Contains(islands.ExplainHiddenXaml(rootHandle: null), "no XAML source information");
        Assert.IsNull(AuthoredForest.Parse(IslandsOnly.Replace("""{"handle":"20","type":"Microsoft.UI.Xaml.Controls.Border","childCount":0,"children":[]},""", "", StringComparison.Ordinal))!
            .ExplainHiddenXaml(rootHandle: "10"), "an island inspected on its own has no XAML around it");
        Assert.IsNull(AuthoredForest.Parse(Annotated)!.ExplainHiddenXaml(null), "an instrumented app classifies its XAML");
        Assert.IsNull(AuthoredForest.Parse(Annotated.Replace("\"sourceInstrumented\":true", "\"sourceInstrumented\":false", StringComparison.Ordinal))!
            .ExplainHiddenXaml(null), "a Reactor window has no XAML around it to lose");
    }

    [TestMethod]
    public void StaticOnly_SaysWhatWorksWithoutTheAgent()
    {
        using var published = System.Text.Json.JsonDocument.Parse("""
            {"present":true,"agent":"absent","annotations":"published","nodes":[{"handle":"7","element":"Button"}],"files":[]}
            """);
        Assert.IsTrue(ReactorDescription.ParseAnnotations(published.RootElement)!.StaticOnly);
        Assert.IsFalse(AuthoredForest.Parse(Named)!.Reactor!.StaticOnly, "the agent's tree has state too");
        StringAssert.Contains(ReactorDescription.StaticOnlyNote, "state, handlers, re-renders and logs are unavailable");
    }
}
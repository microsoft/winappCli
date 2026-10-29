// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;
using WinApp.Cli.Services.DevTools;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Tests;

/// <summary>
/// Comments on Microsoft.UI.Reactor elements: the C# call-site anchor (live capture, offline <c>--file App.cs --line</c>,
/// re-anchoring), and the resolver that finds the call site again after lines move or change, run against real files.
/// </summary>
[TestClass]
[DoNotParallelize]
public class DevToolsCSharpCommentsTests
{
    private static readonly string AppSource = AppSourceText.ReplaceLineEndings("\n");
    private static readonly string[] DuplicatedLineCandidates = ["App.cs:11:13", "App.cs:12:13"];

    private const string AppSourceText = """
        using Microsoft.UI.Reactor;

        class App : Component
        {
            public override Element Render()
            {
                var (count, setCount) = UseState(0);
                return VStack(
                    TextBlock($"Count: {count}"),
                    Button("+1", () => setCount(count + 1)).Width(120),
                    Counter());
            }
        }

        class Counter : Component
        {
            public override Element Render() => Button("Reset", () => { });
        }
        """;

    private string _root = string.Empty;

    private string AppFile => Path.Combine(_root, "App.cs");

    private string StorePath => Path.Combine(_root, ".winapp", "ui-comments.json");

    [TestInitialize]
    public void Init()
    {
        _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "uic-cs-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(AppFile, AppSource);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    // ---- CSharpSourceText ----

    [TestMethod]
    [DataRow("return VStack(", "VStack@8")]
    [DataRow("    Button(\"+1\", () => setCount(count + 1)).Width(120),", "Button@5")]
    [DataRow("TextBlock(\"Button(\" + Label()),", "TextBlock@1,Label@23")]
    [DataRow("var x = new Thickness(4); // Button(", "")]
    [DataRow("public override Element Render()", "")]
    [DataRow("var (count, setCount) = UseState(0);", "")]
    [DataRow("Component<Counter>(new CounterProps(1)),", "Component@1")]
    [DataRow("public override Element Render() => Button(\"Reset\");", "Button@37")]
    [DataRow("Console.WriteLine(Math.Max(1, 2));", "")]
    [DataRow("List<Element> Items(", "")]
    public void CallSites_FindFactoryCallsOnly(string line, string expected)
    {
        var masked = CSharpSourceText.Mask([line])[0];
        var sites = CSharpSourceText.CallSites(masked).Select(site => $"{site.Name}@{site.Column}");
        Assert.AreEqual(expected, string.Join(",", sites));
    }

    [TestMethod]
    public void Mask_KeepsColumnsAndSpansLines()
    {
        string[] lines = ["Button(@\"multi", "Button( still text\")", "/* Button( */ TextBlock(\"x\")"];
        var masked = CSharpSourceText.Mask(lines);
        Assert.AreEqual(lines[0].Length, masked[0].Length);
        Assert.AreEqual("Button@1", string.Join(",", CSharpSourceText.CallSites(masked[0]).Select(site => $"{site.Name}@{site.Column}")));
        Assert.AreEqual(0, CSharpSourceText.CallSites(masked[1]).Count, "the verbatim string continues onto line 2");
        Assert.AreEqual("TextBlock@15", string.Join(",", CSharpSourceText.CallSites(masked[2]).Select(site => $"{site.Name}@{site.Column}")));
    }

    [TestMethod]
    public void LineHash_IgnoresIndentation_AndRedactsPasswords()
    {
        Assert.AreEqual(CSharpSourceText.LineHash("    Button(\"+1\",  x)"), CSharpSourceText.LineHash("Button(\"+1\", x)  "));
        var stored = CSharpSourceText.StoredText("PasswordBox(\"hunter2\")");
        Assert.AreEqual("PasswordBox(\"<redacted>\")", stored);
        Assert.AreEqual(CSharpSourceText.LineHash("PasswordBox(\"hunter2\")"), CSharpSourceText.LineHash("PasswordBox(\"other\")"),
            "the hash is over redacted text, so it cannot be used to guess the password");
    }

    [TestMethod]
    public void DeclarationSpan_FindsTheComponentBody()
    {
        var masked = CSharpSourceText.Mask(CSharpSourceText.SplitLines(AppSource));
        Assert.AreEqual((3, 13), CSharpSourceText.DeclarationSpan(masked, "App"));
        Assert.AreEqual((15, 18), CSharpSourceText.DeclarationSpan(masked, "Counter"));
        Assert.IsNull(CSharpSourceText.DeclarationSpan(masked, "Missing"));
        Assert.AreEqual("Counter", CSharpSourceText.EnclosingType(masked, 17));
        Assert.AreEqual("App", CSharpSourceText.EnclosingType(masked, 10));
    }

    // ---- offline add ----

    [TestMethod]
    public void OfflineAdd_OneCallSite_AnchorsAndListConfirms()
    {
        var (exit, output) = Add("--text", "make it bigger", "--file", "App.cs", "--line", "10", "--json");
        Assert.AreEqual(0, exit, output);
        var comment = new CommentStore().Load(StorePath).Comments.Single();
        var cs = comment.Anchor.CSharp!;
        Assert.AreEqual("App.cs", cs.SourceFile);
        Assert.AreEqual(10, cs.Line);
        Assert.AreEqual(13, cs.Column);
        Assert.AreEqual("Button", cs.Factory);
        Assert.AreEqual("App", cs.Component);
        Assert.IsFalse(comment.Anchor.Weak);

        var view = View(comment);
        Assert.IsTrue(view.AnchorConfirmed);
        Assert.AreEqual("call-site", view.Hits.Single().Via);
        Assert.AreEqual(10, view.Hits.Single().Line);
    }

    [TestMethod]
    public void OfflineAdd_SeveralCallSites_RefusesWithCandidates_ColumnChooses()
    {
        File.WriteAllText(AppFile, AppSource.Replace("Counter());", "Counter(), TextBlock(\"tail\"));"));
        var (exit, output) = Add("--text", "x", "--file", "App.cs", "--line", "11");
        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "App.cs:11:13 Counter");
        StringAssert.Contains(output, "App.cs:11:24 TextBlock");
        StringAssert.Contains(output, "--column");
        Assert.IsFalse(File.Exists(StorePath));

        (exit, output) = Add("--text", "x", "--file", "App.cs", "--line", "11", "--column", "24");
        Assert.AreEqual(0, exit, output);
        Assert.AreEqual("TextBlock", new CommentStore().Load(StorePath).Comments.Single().Anchor.CSharp!.Factory);
    }

    [TestMethod]
    public void OfflineAdd_NoCallSite_Refuses()
    {
        var (exit, output) = Add("--text", "x", "--file", "App.cs", "--line", "7");
        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "no Reactor call site");
        (exit, output) = Add("--text", "x", "--file", "App.cs");
        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "--line");
    }

    // ---- resolver ----

    [TestMethod]
    public void OfflineAdd_AStringPassedToAPasswordParameter_IsNotStored_AndTheAnchorStillResolves()
    {
        File.WriteAllText(Path.Combine(_root, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(_root, "Models.cs"),
            "record Login2Props(string User, string UserPassword, Creds Account);\nrecord Creds(string Name, string Password);\n");
        File.WriteAllText(AppFile, AppSource.Replace("Counter());",
            "Component<Login2, Login2Props>(new Login2Props(\"bob\", \"pos-secret-1\", new Creds(\"alice\", \"nested-pw-1\"))));",
            StringComparison.Ordinal));

        var (exit, output) = Add("--text", "x", "--file", "App.cs", "--line", "11");

        Assert.AreEqual(0, exit, output);
        var stored = File.ReadAllText(StorePath);
        Assert.IsFalse(stored.Contains("pos-secret-1", StringComparison.Ordinal) || stored.Contains("nested-pw-1", StringComparison.Ordinal), stored);
        var comment = new CommentStore().Load(StorePath).Comments.Single();
        StringAssert.Contains(comment.Anchor.CSharp!.LineText, "new Login2Props(\"bob\", \"<redacted>\", new Creds(\"alice\", \"<redacted>\"))");
        var view = View(comment);
        Assert.IsTrue(view.AnchorConfirmed, "the line is compared by its redacted text");
        Assert.AreEqual("call-site", view.Hits.Single().Via);
        Assert.IsFalse(view.Hits.Single().Text!.Contains("pos-secret-1", StringComparison.Ordinal));

        File.WriteAllText(AppFile, File.ReadAllText(AppFile).Replace("    public override Element Render()\n    {",
            "    // moved\n    public override Element Render()\n    {", StringComparison.Ordinal));
        view = View(new CommentStore().Load(StorePath).Comments.Single());
        Assert.IsTrue(view.AnchorConfirmed, "a moved line is found by the same redacted text");
        Assert.AreEqual(12, view.Hits.Single().Line);
    }

    [TestMethod]
    public void Resolver_MovedLine_StaysConfirmed()
    {
        Add("--text", "x", "--file", "App.cs", "--line", "10");
        File.WriteAllText(AppFile, AppSource.Replace("    public override Element Render()\n    {", "    // a new comment\n    // and another\n    public override Element Render()\n    {"));
        var view = View(new CommentStore().Load(StorePath).Comments.Single());
        Assert.IsTrue(view.AnchorConfirmed, string.Join(";", view.Hits.Select(hit => $"{hit.Line} {hit.Via} {hit.Confidence}")));
        Assert.AreEqual(12, view.Hits.Single().Line);
        Assert.AreEqual("line-text", view.Hits.Single().Via);
    }

    [TestMethod]
    public void Resolver_DuplicatedLine_IsAmbiguous()
    {
        Add("--text", "x", "--file", "App.cs", "--line", "10");
        var line = "            Button(\"+1\", () => setCount(count + 1)).Width(120),";
        File.WriteAllText(AppFile, AppSource.Replace(line, "            TextBlock(\"top\"),\n" + line + "\n" + line));
        var view = View(new CommentStore().Load(StorePath).Comments.Single());
        Assert.IsFalse(view.AnchorConfirmed);
        Assert.IsTrue(view.RequiresConfirmation);
        Assert.IsTrue(view.Ambiguous);
        CollectionAssert.AreEquivalent(DuplicatedLineCandidates, view.Candidates);
    }

    [TestMethod]
    public void Resolver_EditedLine_IsARankedCandidate()
    {
        Add("--text", "x", "--file", "App.cs", "--line", "10");
        File.WriteAllText(AppFile, AppSource.Replace("Button(\"+1\", () => setCount(count + 1)).Width(120)",
            "Button(\"+2\", () => setCount(count + 2)).Width(120)"));
        var view = View(new CommentStore().Load(StorePath).Comments.Single());
        Assert.IsFalse(view.AnchorConfirmed);
        Assert.IsTrue(view.RequiresConfirmation);
        Assert.AreEqual(10, view.Hits[0].Line, "the edited call site ranks first");
        Assert.AreEqual("factory", view.Hits[0].Via);
        Assert.AreEqual("weak", view.Hits[0].Confidence);
        Assert.IsFalse(view.Hits.Any(hit => hit.Line == 17), "Counter's Button is outside <App>'s render");
    }

    [TestMethod]
    public void Resolver_RenamedFile_FindsTheLineTextButNeedsConfirmation()
    {
        Add("--text", "x", "--file", "App.cs", "--line", "10");
        Directory.CreateDirectory(Path.Combine(_root, "Views"));
        File.Move(AppFile, Path.Combine(_root, "Views", "Main.cs"));
        var view = View(new CommentStore().Load(StorePath).Comments.Single());
        Assert.IsFalse(view.AnchorConfirmed);
        Assert.AreEqual(Path.Combine("Views", "Main.cs"), view.Hits.Single().File);
        Assert.AreEqual("line-text", view.Hits.Single().Via);
    }

    [TestMethod]
    public void Resolver_RemovedCallSite_IsStale()
    {
        Add("--text", "x", "--file", "App.cs", "--line", "10");
        File.WriteAllText(AppFile, AppSource.Replace("            Button(\"+1\", () => setCount(count + 1)).Width(120),\n", ""));
        var view = View(new CommentStore().Load(StorePath).Comments.Single());
        Assert.AreEqual(0, view.Hits.Count);
        Assert.IsTrue(CommentViewBuilder.IsMaybeStale(view));
    }

    // ---- live capture ----

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(false, true)]
    public async Task LiveCapture_ReactorElement_AnchorsToItsCallSite(bool exact, bool confirm)
    {
        using var agent = ReactorAgent(exact);
        using var console = new TestConsole();
        var store = new CommentStore();
        var handler = new DevToolsCommentsAddCommand.Handler(store, new CommentAnchorResolver(), new CommentPusher(store),
            new CommentTestTargetResolver(agent.Pid), new FixedCwd(_root), console, NullLogger<DevToolsCommentsAddCommand>.Instance);
        List<string> args = ["--app", agent.Pid.ToString(), "--from-element", "7", "--text", "bigger", "--json"];
        if (confirm) { args.Add("--confirm-likely-source"); }
        var exit = await handler.InvokeAsync(new DevToolsCommentsAddCommand().Parse(args.ToArray()));
        if (!exact && !confirm)
        {
            Assert.AreEqual(1, exit);
            StringAssert.Contains(console.Output, "--confirm-likely-source");
            Assert.IsFalse(File.Exists(StorePath));
            return;
        }
        Assert.AreEqual(0, exit, console.Output);
        var anchor = store.Load(StorePath).Comments.Single().Anchor;
        Assert.AreEqual("App.cs", anchor.SourceFile);
        Assert.AreEqual(10, anchor.Line);
        Assert.AreEqual("Button", anchor.CSharp!.Factory);
        Assert.AreEqual("App", anchor.CSharp.Component);
        Assert.AreEqual("k1", anchor.CSharp.Key);
        Assert.AreEqual(2, anchor.CSharp.Ordinal);
        Assert.AreEqual(exact, anchor.CSharp.Exact);
        Assert.AreEqual(exact ? "reactor-call-site" : "user-confirmed-likely-source", anchor.SourceProvenance);
        Assert.AreEqual("reactor:opaque", anchor.ElementPath);
        Assert.IsFalse(anchor.Weak);
        using var output = JsonDocument.Parse(console.Output);
        Assert.IsTrue(output.RootElement.GetProperty("comment").GetProperty("anchorConfirmed").GetBoolean());
    }

    [TestMethod]
    public async Task LiveCapture_ReactorElement_StoresTheLiveAutomationIdAndWindowTitle()
    {
        using var agent = ReactorAgent(exact: true)
            .Answer("VisualTree.getPreviews", """
                {"previews":[{"handle":"7","preview":"","automationId":"IncrementButton"}],"requested":1,"returned":1,"truncated":false}
                """)
            .Answer("Surface.list", """
                {"activeSurface":"Counter","surfaces":[
                  {"name":"Counter","rootHandle":"7","window":"5","active":true,"contentWidth":1,"contentHeight":1,"screenRect":{"x":0,"y":0,"w":1,"h":1},"isIsland":false,"hostVisible":true,"derived":false}]}
                """);
        using var console = new TestConsole();
        var store = new CommentStore();
        var handler = new DevToolsCommentsAddCommand.Handler(store, new CommentAnchorResolver(), new CommentPusher(store),
            new CommentTestTargetResolver(agent.Pid), new FixedCwd(_root), console, NullLogger<DevToolsCommentsAddCommand>.Instance);
        var exit = await handler.InvokeAsync(new DevToolsCommentsAddCommand().Parse(
            ["--app", agent.Pid.ToString(), "--from-element", "7", "--text", "bigger"]));

        Assert.AreEqual(0, exit, console.Output);
        var comment = store.Load(StorePath).Comments.Single();
        Assert.AreEqual("reactor-call-site", comment.Anchor.SourceProvenance);
        Assert.AreEqual("IncrementButton", comment.Anchor.Identity.AutomationId);
        Assert.AreEqual("Counter", comment.Context?.WindowTitle);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Recapture_KeepsTextAndMovesTheAnchor(bool positionalSelector)
    {
        var (exit, _) = Add("--id", "cmt_keep", "--text", "keep me", "--kind", "visual", "--file", "App.cs", "--line", "9");
        Assert.AreEqual(0, exit);
        using var agent = ReactorAgent(exact: true);
        using var console = new TestConsole();
        var store = new CommentStore();
        var handler = new DevToolsCommentsAddCommand.Handler(store, new CommentAnchorResolver(), new CommentPusher(store),
            new CommentTestTargetResolver(agent.Pid), new FixedCwd(_root), console, NullLogger<DevToolsCommentsAddCommand>.Instance);
        string[] element = positionalSelector ? ["7"] : ["--from-element", "7"];
        exit = await handler.InvokeAsync(new DevToolsCommentsAddCommand().Parse(
            ["--id", "cmt_keep", "--app", agent.Pid.ToString(), .. element]));
        Assert.AreEqual(0, exit, console.Output);
        var comment = store.Load(StorePath).Comments.Single();
        Assert.AreEqual("keep me", comment.Text);
        Assert.AreEqual("visual", comment.Kind);
        Assert.AreEqual(10, comment.Anchor.CSharp!.Line);
    }

    [TestMethod]
    public void Recapture_UnknownId_Refuses()
    {
        using var console = new TestConsole();
        var store = new CommentStore();
        var handler = new DevToolsCommentsAddCommand.Handler(store, new CommentAnchorResolver(), new CommentPusher(store),
            new CommentTestTargetResolver(), new FixedCwd(_root), console, NullLogger<DevToolsCommentsAddCommand>.Instance);
        var exit = handler.InvokeAsync(new DevToolsCommentsAddCommand().Parse(["--id", "cmt_none", "--from-selection", "--app", "1"]))
            .GetAwaiter().GetResult();
        Assert.AreEqual(1, exit);
    }

    [TestMethod]
    public async Task AmbiguousSelector_ListsTheCandidates()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("Reactor.describe", """
                {"state":"ok","nodes":[
                  {"handle":"20","element":"Stack","file":0,"line":7,"host":{"kind":"window","window":"100","title":"Main","root":{"kind":"class","name":"App"}}},
                  {"handle":"21","parent":"20","element":"Component","file":0,"line":36,"key":"first","component":{"kind":"class","name":"Counter"}},
                  {"handle":"31","parent":"20","element":"Component","file":0,"line":79,"key":"second","component":{"kind":"class","name":"Counter"}}],
                 "files":["C:\\src\\App.cs"],"unmapped":0,"truncated":false,"complete":true}
                """)
            .Answer("VisualTree.enumerate", """{"nodes":[]}""");
        using var console = new TestConsole();
        console.Profile.Width = 200;
        var store = new CommentStore();
        var handler = new DevToolsCommentsAddCommand.Handler(store, new CommentAnchorResolver(), new CommentPusher(store),
            new CommentTestTargetResolver(agent.Pid), new FixedCwd(_root), console, NullLogger<DevToolsCommentsAddCommand>.Instance);

        var exit = await handler.InvokeAsync(new DevToolsCommentsAddCommand().Parse(["<Counter>", "--app", agent.Pid.ToString(), "--text", "x"]));

        Assert.AreEqual(1, exit);
        StringAssert.Contains(console.Output, "one of their selectors:");
        StringAssert.Contains(console.Output, "key=first App.cs:36");
        StringAssert.Contains(console.Output, "key=second App.cs:79");
    }

    [TestMethod]
    public void ReactorMarkerAnchor_MatchesTheAppsFormat_AndIsOnlyForOwnCallSites()
    {
        var cs = new CommentCSharpAnchor { SourceFile = "App.cs", Line = 78, Column = 0, Element = "Button", Component = "Counter", LineHash = "" };
        Assert.AreEqual("reactor:6:App.cs2:781:06:Button0:7:Counter1:01:0", CommentPusher.ReactorMarkerAnchor(cs),
            "the same string the app captured live for <Counter>/Button at App.cs:78");
        cs.Exact = false;
        Assert.IsNull(CommentPusher.ReactorMarkerAnchor(cs), "an ancestor's call site does not name the element itself");
        Assert.IsNull(CommentPusher.ReactorMarkerAnchor(null));
    }

    [TestMethod]
    public void Store_WritesTheCSharpAnchorAsCsharp()
    {
        Add("--text", "x", "--file", "App.cs", "--line", "10");
        StringAssert.Contains(File.ReadAllText(StorePath), "\"csharp\": {");
    }

    [TestMethod]
    public void ReactorSourceReply_ReadsOnlyCSharpAnswers()
    {
        Assert.IsNull(ReactorSourceReply.Parse("""{"fileName":"ms-appx:///Main.xaml","lineNumber":2}"""));
        var reply = ReactorSourceReply.Parse("""
            {"kind":"csharp","fileName":"C:\\a\\App.cs","lineNumber":10,"columnNumber":0,"onDisk":true,
             "reactor":{"element":"Button","exact":false,"ordinal":3}}
            """)!;
        Assert.AreEqual(10, reply.Line);
        Assert.AreEqual(0, reply.Column);
        Assert.IsFalse(reply.Exact);
        Assert.AreEqual(3, reply.Ordinal);
    }

    [TestMethod]
    public void BuildCSharpAnchor_WithoutColumn_UsesTheElementKind()
    {
        var anchor = CommentSelectionCapture.BuildCSharpAnchor(_root,
            new ReactorSourceReply(AppFile, 10, 0, true, "Button", null, "App", 0, true))!;
        Assert.AreEqual("Button", anchor.Factory);
        Assert.AreEqual(0, anchor.Column);
        Assert.IsNull(CommentSelectionCapture.BuildCSharpAnchor(_root,
            new ReactorSourceReply(Path.Combine(Path.GetTempPath(), "Elsewhere.cs"), 1, 0, true, null, null, null, 0, true)),
            "a call site outside the project is not anchored");
    }

    private FakeDevToolsProtocolAgent ReactorAgent(bool exact, string? file = null, bool onDisk = true, string? sourceRoot = null) => new FakeDevToolsProtocolAgent()
        .Answer("VisualTree.find", """{"matches":[{"handle":"7","name":"","type":"Microsoft.UI.Xaml.Controls.Button"}]}""")
        .Answer("VisualTree.enumerate", """[{"handle":"7","name":"","type":"Microsoft.UI.Xaml.Controls.Button","children":[]}]""")
        .Answer("Property.get", """{"props":[]}""")
        .Answer("Source.get", JsonSerializer.Serialize(new
        {
            handle = "7", kind = "csharp", fileName = file ?? AppFile, lineNumber = 10, columnNumber = 13, onDisk,
            authoredState = "available",
            reactor = new { element = "Button", handle = "7", exact, key = "k1", component = "App", ordinal = 2 },
        }))
        .Answer("Internal.elementAnchor", """{"anchor":"reactor:opaque","unique":true}""")
        .Answer("Internal.sourceRoot", JsonSerializer.Serialize(new { sourceRoot = sourceRoot ?? _root }))
        .Answer("Internal.setComments", """{"total":1,"placed":1}""");

    // ---- call sites outside the project ----

    // repo\App is the app's project; repo\Lib\Panel.cs is in a library it references (or a linked file).
    private (string App, string LibFile) LibraryLayout()
    {
        var app = Directory.CreateDirectory(Path.Combine(_root, "App")).FullName;
        var lib = Directory.CreateDirectory(Path.Combine(_root, "Lib")).FullName;
        var file = Path.Combine(lib, "Panel.cs");
        File.WriteAllText(file, AppSource);
        return (app, file);
    }

    [TestMethod]
    public async Task LiveCapture_ACallSiteInAnotherProjectOfTheRepository_IsAnchored()
    {
        var (app, libFile) = LibraryLayout();
        using var agent = ReactorAgent(exact: true, file: libFile, sourceRoot: app);
        using var console = new TestConsole();
        var store = new CommentStore();
        var handler = new DevToolsCommentsAddCommand.Handler(store, new CommentAnchorResolver(), new CommentPusher(store),
            new CommentTestTargetResolver(agent.Pid), new FixedCwd(app), console, NullLogger<DevToolsCommentsAddCommand>.Instance);
        var exit = await handler.InvokeAsync(new DevToolsCommentsAddCommand().Parse(
            ["--app", agent.Pid.ToString(), "--from-element", "7", "--text", "bigger", "--json"]));

        Assert.AreEqual(0, exit, console.Output);
        var anchor = store.Load(StorePath).Comments.Single().Anchor;
        Assert.AreEqual(@"..\Lib\Panel.cs", anchor.SourceFile, "stored relative to the app's project, inside the repository");
        Assert.AreEqual(@"..\Lib\Panel.cs", anchor.CSharp!.SourceFile);
        Assert.IsFalse(anchor.Weak);
        using var output = JsonDocument.Parse(console.Output);
        Assert.IsTrue(output.RootElement.GetProperty("comment").GetProperty("anchorConfirmed").GetBoolean());
        Assert.IsFalse(output.RootElement.TryGetProperty("unanchored", out _));
    }

    [TestMethod]
    public void OfflineFile_InAnotherProjectOfTheRepository_IsAnchored()
    {
        var (app, libFile) = LibraryLayout();
        var (exit, output) = Add("--text", "x", "--file", @"..\Lib\Panel.cs", "--line", "10", "--source-root", app);
        Assert.AreEqual(0, exit, output);
        (exit, output) = Add("--text", "y", "--file", libFile, "--line", "10", "--source-root", app);
        Assert.AreEqual(0, exit, output);
        foreach (var comment in new CommentStore().Load(StorePath).Comments)
        {
            Assert.AreEqual(@"..\Lib\Panel.cs", comment.Anchor.CSharp!.SourceFile);
            var view = View(comment);
            Assert.IsTrue(view.AnchorConfirmed, "listing finds the call site in the other project");
            Assert.AreEqual(@"..\Lib\Panel.cs", view.Hits.Single().File);
        }
    }

    [TestMethod]
    public void BuildCSharpAnchor_OutsideTheRepository_IsNotAnchored()
    {
        var (app, _) = LibraryLayout();
        var outside = Path.Combine(Path.GetTempPath(), $"winapp-outside-{Guid.NewGuid():N}.cs");
        File.WriteAllText(outside, AppSource);
        try
        {
            Assert.IsNull(CommentSelectionCapture.BuildCSharpAnchor(app,
                new ReactorSourceReply(outside, 10, 13, true, "Button", null, "App", 0, true)));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LiveCapture_AnElementWithNoSourceOnDisk_SaysItIsNotLinked(bool json)
    {
        using var agent = ReactorAgent(exact: true, file: "Generated/Wrapper.g.cs", onDisk: false);
        using var console = new TestConsole();
        var store = new CommentStore();
        var handler = new DevToolsCommentsAddCommand.Handler(store, new CommentAnchorResolver(), new CommentPusher(store),
            new CommentTestTargetResolver(agent.Pid), new FixedCwd(_root), console, NullLogger<DevToolsCommentsAddCommand>.Instance);
        List<string> args = ["--app", agent.Pid.ToString(), "--from-element", "7", "--text", "bigger"];
        if (json) { args.Add("--json"); }
        var exit = await handler.InvokeAsync(new DevToolsCommentsAddCommand().Parse(args.ToArray()));

        Assert.AreEqual(0, exit, console.Output);
        Assert.IsTrue(store.Load(StorePath).Comments.Single().Anchor.Weak);
        var notice = console.Output;
        if (json)
        {
            using var output = JsonDocument.Parse(console.Output);
            notice = output.RootElement.GetProperty("unanchored").GetString()!;
        }
        StringAssert.Contains(notice, "Saved without a source location");
        StringAssert.Contains(notice, "Generated/Wrapper.g.cs");
        StringAssert.Contains(notice, "--file");
    }

    private (int Exit, string Output) Add(params string[] args)
    {
        using var console = new TestConsole();
        var store = new CommentStore();
        var handler = new DevToolsCommentsAddCommand.Handler(store, new CommentAnchorResolver(), new NoPush(),
            new CommentTestTargetResolver(), new FixedCwd(_root), console, NullLogger<DevToolsCommentsAddCommand>.Instance);
        var exit = handler.InvokeAsync(new DevToolsCommentsAddCommand().Parse(args)).GetAwaiter().GetResult();
        return (exit, console.Output);
    }

    private CommentView View(Comment comment) => CommentViewBuilder.ToView(comment, new CommentAnchorResolver(), _root);

    private sealed class NoPush : ICommentPusher
    {
        public (int Total, int Placed)? Push(uint pid, string fallbackRoot, string? sourceRootOverride = null,
            CancellationToken cancellationToken = default) => null;

        public (int Refreshed, int Failed) PushStore(string storePath, uint? skipPid, CancellationToken cancellationToken = default) => (0, 0);
    }

    private sealed class FixedCwd(string dir) : ICurrentDirectoryProvider
    {
        public string GetCurrentDirectory() => dir;

        public DirectoryInfo GetCurrentDirectoryInfo() => new(dir);
    }
}
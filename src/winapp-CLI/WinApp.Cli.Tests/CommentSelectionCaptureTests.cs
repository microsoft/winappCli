// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Services.DevTools;
using WinApp.Cli.Services.DevTools.Comments;
using WinApp.Cli.Services;
using Spectre.Console.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace WinApp.Cli.Tests;

/// <summary>
/// Pins the pure wire-JSON parsing that <c>comments add --from-selection</c> uses to auto-fill an anchor:
/// DFS handle lookup in the enumerate tree, Text→Content snippet extraction (placeholders rejected),
/// element.source URI/leaf/line parsing, and <c>--app</c> pid resolution. No live app involved.
/// </summary>
[TestClass]
[DoNotParallelize]
public class CommentSelectionCaptureTests
{
    private static readonly string[] CaptureCalls = ["Selection.poll", "VisualTree.enumerate", "Property.get", "Source.get",
        "Internal.elementAnchor", "Internal.sourceRoot", "VisualTree.getPreviews", "Surface.list"];

    [TestMethod]
    [DataRow(false, null)]
    [DataRow(true, null)]
    [DataRow(true, "--line")]
    [DataRow(true, "--column")]
    [DataRow(true, "--file")]
    [DataRow(true, "--source-uri")]
    [DataRow(true, "--source-root")]
    [DataRow(true, "--name")]
    [DataRow(true, "--type")]
    [DataRow(true, "--automation-id")]
    [DataRow(true, "--tree-path")]
    [DataRow(true, "--content")]
    public async Task LikelySource_RequiresExplicitConfirmation_AndPersistsItsEvidence(bool confirm, string? overrideOption)
    {
        var directory = Directory.CreateTempSubdirectory("winapp-likely-source-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "MainPage.xaml"), "<Page>\n<TextBlock/>\n</Page>");
            using var agent = new FakeDevToolsProtocolAgent()
                .Answer("VisualTree.enumerate", """[{"handle":"2","name":"","type":"TextBlock","children":[]}]""")
                .Answer("Property.get", """{"props":[]}""")
                .Answer("Source.get", """
                    {"fileName":"ms-appx:///MainPage.xaml","lineNumber":2,"columnNumber":128,
                    "authoredState":"likely","authoredFileName":"MainPage.xaml","authoredLineNumber":2,
                    "authoredColumnNumber":1,"coordinateProvenance":"likely-source-line",
                    "sourceEvidence":"Disk XBF and compiler line preservation are unverified.","xaml":"<TextBlock/>"}
                    """)
                .Answer("Internal.elementAnchor", """{"anchor":"opaque-unchanged","unique":true}""")
                .Answer("Internal.sourceRoot", System.Text.Json.JsonSerializer.Serialize(new { sourceRoot = directory.FullName }))
                .Answer("Internal.setComments", """{"total":1,"placed":0}""");
            var store = new CommentStore();
            var console = new TestConsole();
            var handler = new DevToolsCommentsAddCommand.Handler(store, new CommentAnchorResolver(),
                new CommentPusher(store), new CommentTestTargetResolver(agent.Pid),
                new FixedDirectory(directory.FullName), console, NullLogger<DevToolsCommentsAddCommand>.Instance);
            var arguments = new List<string> { "--app", agent.Pid.ToString(), "--from-element", "2", "--text", "note", "--json" };
            if (confirm) { arguments.Add("--confirm-likely-source"); }
            var selectedRoot = directory;
            if (overrideOption is not null)
            {
                var value = overrideOption switch
                {
                    "--line" => "3",
                    "--column" => "2",
                    "--file" => "Different.xaml",
                    "--source-uri" => "ms-appx:///Different.xaml",
                    "--source-root" => (selectedRoot = directory.CreateSubdirectory("other-project")).FullName,
                    "--type" => "Button",
                    _ => "Other",
                };
                arguments.AddRange([overrideOption, value]);
            }
            var result = await handler.InvokeAsync(new DevToolsCommentsAddCommand().Parse(arguments.ToArray()));
            Assert.AreEqual(confirm ? 0 : 1, result, console.Output);
            var storePath = store.GetStorePath(selectedRoot);
            Assert.AreEqual(confirm, File.Exists(storePath));
            if (confirm)
            {
                var anchor = store.Load(storePath).Comments.Single().Anchor;
                var overridesSource = overrideOption is "--file" or "--source-uri" or "--source-root" or "--line" or "--column";
                Assert.AreEqual(overridesSource ? "user-specified" : "user-confirmed-likely-source", anchor.SourceProvenance);
                Assert.AreEqual("Disk XBF and compiler line preservation are unverified.", anchor.SourceEvidence);
                Assert.AreEqual(128, anchor.RawColumn);
                Assert.AreEqual(overrideOption == "--column" ? 2 : 1, anchor.Column);
                Assert.AreEqual(overrideOption == "--line" ? 3 : 2, anchor.Line);
                Assert.AreEqual("opaque-unchanged", anchor.ElementPath);
                Assert.AreEqual(overrideOption is null or "--content", anchor.Authored is not null);
            }
            else
            {
                StringAssert.Contains(console.Output, "--confirm-likely-source");
                Assert.IsFalse(agent.Received.Contains("Internal.setComments"));
            }
        }
        finally { directory.Delete(recursive: true); }
    }

    [TestMethod]
    [DataRow("likely", "disk-matched-unique-declaration", "Missing compiler proof.")]
    [DataRow("available", "likely-source-line", "Missing compiler proof.")]
    [DataRow("likely", "likely-source-line", "")]
    public void LikelySource_MalformedEvidenceCannotFallBackToRawCapture(string state, string provenance, string evidence)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            fileName = "ms-appx:///MainPage.xaml", lineNumber = 2, columnNumber = 128,
            authoredState = state, coordinateProvenance = provenance, sourceEvidence = evidence,
        });
        Assert.ThrowsExactly<System.Text.Json.JsonException>(() => CommentSelectionCapture.ParseSource(json));
    }

    [TestMethod]
    public void Correction_CappedName_CannotCaptureAnAnchor()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.find", """{"matches":[{"handle":"2","name":"SameName","type":"Button"}],"truncated":true,"censusNodes":20001}""")
            .Answer("VisualTree.enumerate", Tree)
            .Answer("Property.get", """{"props":[]}""")
            .Answer("Source.get", """{"fileName":""}""")
            .Answer("Internal.elementAnchor", """{"anchor":"Root/0"}""")
            .Answer("Internal.sourceRoot", """{"sourceRoot":""}""");
        var result = CommentSelectionCapture.CaptureElement((uint)agent.Pid, "SameName");
        Assert.AreEqual(CaptureStatus.Failed, result.Status);
        CollectionAssert.AreEqual(new List<string> { "VisualTree.find" }, agent.Received);
    }

    [TestMethod]
    public async Task Correction_CappedName_ActualAddDoesNotCreateStore()
    {
        var directory = Directory.CreateTempSubdirectory("winapp-correction-capture-");
        try
        {
            using var agent = new FakeDevToolsProtocolAgent().Answer("VisualTree.find",
                """{"matches":[{"handle":"2","name":"SameName","type":"Button"}],"truncated":true,"censusNodes":20001}""");
            var store = new CommentStore();
            var handler = new DevToolsCommentsAddCommand.Handler(store, new CommentAnchorResolver(),
                new CommentPusher(store), new CommentTestTargetResolver(agent.Pid),
                new FixedDirectory(directory.FullName), new TestConsole(), NullLogger<DevToolsCommentsAddCommand>.Instance);
            var exit = await handler.InvokeAsync(new DevToolsCommentsAddCommand().Parse(
                ["--app", agent.Pid.ToString(), "--from-element", "SameName", "--text", "note", "--source-root", directory.FullName, "--json"]));
            Assert.AreEqual(1, exit);
            Assert.IsFalse(File.Exists(store.GetStorePath(directory)));
            CollectionAssert.AreEqual(new List<string> { "VisualTree.find" }, agent.Received);
        }
        finally { directory.Delete(recursive: true); }
    }

    private sealed class FixedDirectory(string path) : ICurrentDirectoryProvider
    {
        public string GetCurrentDirectory() => path;
        public DirectoryInfo GetCurrentDirectoryInfo() => new(path);
    }

    [TestMethod]
    public void Correction_CaptureCancelledAfterTree_DoesNotReadPropertiesOrAnchor()
    {
        using var cancel = new CancellationTokenSource();
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", Tree, _ => { cancel.Cancel(); return true; });
        try
        {
            CommentSelectionCapture.CaptureElement((uint)agent.Pid, "2", cancel.Token);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        CollectionAssert.AreEqual(new List<string> { "VisualTree.enumerate" }, agent.Received);
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("""{"name":"Text","value":123,"valueType":"String"}""")]
    public void Correction_CaptureMalformedProperties_CannotClaimSuccessfulAnchor(string row)
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", Tree)
            .Answer("Property.get", $$"""{"props":[{{row}}]}""");
        var result = CommentSelectionCapture.CaptureElement((uint)agent.Pid, "2");
        Assert.AreEqual(CaptureStatus.Failed, result.Status);
        CollectionAssert.AreEqual(new List<string> { "VisualTree.enumerate", "Property.get" }, agent.Received);
    }

    private const string Tree = """
        [{"handle":"1","name":"Root","type":"Grid","childCount":2,"children":[
          {"handle":"2","name":"HeaderText","type":"TextBlock","childCount":0,"children":[]},
          {"handle":"3","name":"","type":"Button","childCount":1,"children":[
            {"handle":"4","name":"Inner","type":"Border","childCount":0,"children":[]}
          ]}
        ]}]
        """;

    [TestMethod]
    [DataRow("")]
    [DataRow("A live heading longer than forty-two characters that is not a source literal")]
    public void DiskMatchedCapture_RetainsAuthoredIdentityIndependentOfRuntimeText(string liveText)
    {
        var directory = Directory.CreateTempSubdirectory("winapp-disk-anchor-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "MainPage.xaml"),
                "<Page>\n                        <TextBlock Text=\"{x:Bind Vm.Title}\" AutomationProperties.AutomationId=\"Heading\" />\n</Page>");
            var properties = System.Text.Json.JsonSerializer.Serialize(new
            {
                props = new[] { new { name = "Text", value = liveText, valueType = "String" } },
            });
            using var agent = new FakeDevToolsProtocolAgent()
                .Answer("VisualTree.enumerate", """[{"handle":"2","name":"","type":"TextBlock","children":[]}]""")
                .Answer("Property.get", properties)
                .Answer("Source.get", """
                    {"fileName":"ms-appx:///MainPage.xaml","lineNumber":2,"columnNumber":128,
                     "authoredState":"available","authoredFileName":"MainPage.xaml","authoredLineNumber":2,
                     "authoredColumnNumber":25,"coordinateProvenance":"disk-matched-unique-declaration",
                     "xaml":"<TextBlock Text=\"{x:Bind Vm.Title}\" AutomationProperties.AutomationId=\"Heading\" />"}
                    """)
                .Answer("Internal.elementAnchor", """{"anchor":"existing-opaque-source-and-parent-anchor","unique":true}""")
                .Answer("Internal.sourceRoot", System.Text.Json.JsonSerializer.Serialize(new { sourceRoot = directory.FullName }));
            var result = CommentSelectionCapture.CaptureElement((uint)agent.Pid, "2");
            Assert.AreEqual(CaptureStatus.Ok, result.Status);
            var captured = result.Element!;
            Assert.AreEqual("existing-opaque-source-and-parent-anchor", captured.ElementPath);
            Assert.AreEqual(25, captured.Column);
            var comment = new Comment
            {
                Id = "disk-matched-heading",
                Text = "Check the heading",
                ProjectRoot = directory.FullName,
                Anchor = new()
                {
                    SourceFile = captured.SourceFile, SourceUri = captured.SourceUri,
                    Line = captured.Line, Column = captured.Column, ElementPath = captured.ElementPath,
                    Authored = captured.Authored,
                    Identity = new() { Type = captured.Type, Name = captured.Name, Content = captured.Content, AutomationId = captured.AutomationId },
                },
            };
            var store = new CommentStore();
            var storePath = store.GetStorePath(directory);
            store.Add(storePath, comment);
            var saved = store.Load(storePath);
            var view = CommentViewBuilder.ToView(saved.Comments.Single(), new CommentAnchorResolver(), directory.FullName);
            Assert.AreEqual(2, view.Anchor.Line);
            Assert.AreEqual(25, view.Anchor.Column);
            Assert.IsTrue(view.AnchorConfirmed, "Authored identity does not require the runtime value to appear in XAML.");
            Assert.AreEqual(2, view.Hits.Single().Line);
            Assert.AreEqual("declaration", view.Hits[0].Via);
            Assert.AreEqual("strong", view.Hits[0].Confidence);
            Assert.AreEqual("Heading", captured.AutomationId);
            if (liveText.Length > 0) { Assert.AreEqual(liveText, view.Anchor.Identity.Content); }
            var path = Path.Combine(directory.FullName, "MainPage.xaml");
            File.WriteAllText(path, File.ReadAllText(path).Replace("Vm.Title", "Vm.Other", StringComparison.Ordinal));
            var afterEdit = CommentViewBuilder.ToView(saved.Comments.Single(), new CommentAnchorResolver(), directory.FullName);
            Assert.IsTrue(afterEdit.AnchorConfirmed);
            Assert.AreEqual("AutomationId", afterEdit.Hits.Single().Via);
            StringAssert.Contains(afterEdit.Anchor.Authored!.Declaration, "Vm.Title");
            var marker = CommentPusher.ToTapComments(saved).Single();
            Assert.AreEqual(captured.ElementPath, marker.Anchor, "Host search confirmation does not gate marker transport.");
            Assert.AreEqual("MainPage.xaml", marker.File);
            Assert.AreEqual(2, marker.Line);
        }
        finally { directory.Delete(recursive: true); }
    }

    [TestMethod]
    [DataRow("", true, false)]
    [DataRow("<TextBlock/>", false, false)]
    [DataRow("<Button/>", true, true)]
    [DataRow("<TextBlock/><TextBlock/>", true, true)]
    [DataRow("<TextBlock", true, true)]
    public void MissingOrAmbiguousProofCannotConfirm_MalformedOrChangedDeclarationsRefuseCapture(
        string markup, bool unique, bool fails)
    {
        var root = Directory.CreateTempSubdirectory("winapp-capture-proof-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "Page.xaml"), "<Page>\n<TextBlock/>\n</Page>");
            using var agent = new FakeDevToolsProtocolAgent()
                .Answer("VisualTree.enumerate", """[{"handle":"2","type":"TextBlock","name":"","children":[]}]""")
                .Answer("Property.get", """{"props":[]}""")
                .Answer("Source.get", System.Text.Json.JsonSerializer.Serialize(new
                {
                    fileName = "ms-appx:///Page.xaml", lineNumber = 2, columnNumber = 1,
                    authoredState = "available", xaml = markup,
                }))
                .Answer("Internal.elementAnchor", System.Text.Json.JsonSerializer.Serialize(new
                {
                    anchor = "opaque", unique, uniquenessReason = unique ? "unique" : "unclassified-peer",
                }))
                .Answer("Internal.sourceRoot", System.Text.Json.JsonSerializer.Serialize(new { sourceRoot = root.FullName }));
            var result = CommentSelectionCapture.CaptureElement((uint)agent.Pid, "2");
            Assert.AreEqual(fails ? CaptureStatus.Failed : CaptureStatus.Ok, result.Status);
            if (fails)
            {
                Assert.AreEqual("source-unavailable", result.Error!.Token);
                Assert.IsNull(result.Element);
                return;
            }
            var captured = result.Element!;
            Assert.AreEqual(markup.Length > 0, captured.Authored is not null);
            var comment = new Comment
            {
                ProjectRoot = root.FullName,
                Anchor = new()
                {
                    SourceFile = captured.SourceFile, SourceUri = captured.SourceUri, Line = captured.Line,
                    Identity = new() { Type = captured.Type }, Authored = captured.Authored,
                },
            };
            var view = CommentViewBuilder.ToView(comment, new CommentAnchorResolver(), root.FullName);
            if (captured.Authored is not null)
            {
                // The declaration matches exactly one source declaration; an unclassified runtime peer is irrelevant.
                Assert.AreEqual("unclassified-peer", captured.Authored.UniquenessReason);
                Assert.IsTrue(view.AnchorConfirmed);
                Assert.IsFalse(view.RequiresConfirmation);
                Assert.IsNull(CommentViewBuilder.AnchorHealthWarning(view));
            }
            else
            {
                Assert.IsFalse(view.AnchorConfirmed);
                Assert.IsTrue(view.RequiresConfirmation);
            }
        }
        finally { root.Delete(recursive: true); }
    }

    // WinUI Gallery's ControlExample sets x:Name="RootPanel" on its own x:Class root, so every instance reports that
    // name while its usage on a page declares none. The app has already verified the declaration ('available').
    [TestMethod]
    public void UnnamedUsageOfASelfNamedControl_CapturesAStrongAnchor()
    {
        var root = Directory.CreateTempSubdirectory("winapp-capture-self-named-");
        try
        {
            const string usage = "<local:ControlExample xmlns:local=\"using:App\" SampleDefinition=\"a.txt\">";
            File.WriteAllText(Path.Combine(root.FullName, "Page.xaml"), "<Page>\n" + usage + "</local:ControlExample>\n</Page>");
            using var agent = new FakeDevToolsProtocolAgent()
                .Answer("VisualTree.enumerate", """[{"handle":"2","type":"App.ControlExample","name":"RootPanel","children":[]}]""")
                .Answer("Property.get", """{"props":[]}""")
                .Answer("Source.get", System.Text.Json.JsonSerializer.Serialize(new
                {
                    fileName = "ms-appx:///Page.xaml", lineNumber = 2, columnNumber = 40,
                    authoredState = "available", authoredFileName = "Page.xaml", authoredLineNumber = 2, authoredColumnNumber = 1,
                    coordinateProvenance = "disk-matched-unique-declaration", xaml = usage,
                }))
                .Answer("Internal.elementAnchor", """{"anchor":"opaque","unique":true}""")
                .Answer("Internal.sourceRoot", System.Text.Json.JsonSerializer.Serialize(new { sourceRoot = root.FullName }));
            var result = CommentSelectionCapture.CaptureElement((uint)agent.Pid, "2");
            Assert.AreEqual(CaptureStatus.Ok, result.Status, result.Error?.Message);
            var captured = result.Element!;
            Assert.IsNull(captured.Name, "a name the type gives itself is not part of where to edit");
            var comment = new Comment
            {
                ProjectRoot = root.FullName,
                Anchor = new()
                {
                    SourceFile = captured.SourceFile, SourceUri = captured.SourceUri, Line = captured.Line, Column = captured.Column,
                    Identity = new() { Type = captured.Type, Name = captured.Name }, Authored = captured.Authored,
                },
            };
            var view = CommentViewBuilder.ToView(comment, new CommentAnchorResolver(), root.FullName);
            Assert.IsTrue(view.AnchorConfirmed);
            Assert.AreEqual("strong", view.Hits.Single().Confidence);
        }
        finally { root.Delete(recursive: true); }
    }

    [TestMethod]
    public void UnprefixedNameCanCaptureAnAuthoredDeclaration()
    {
        var root = Directory.CreateTempSubdirectory("winapp-capture-name-");
        try
        {
            const string declaration = "<TextBlock Name=\"Label\" Text=\"Same\"/>";
            File.WriteAllText(Path.Combine(root.FullName, "Page.xaml"), "<Page Name=\"Root\">\n" + declaration + "\n</Page>");
            using var agent = new FakeDevToolsProtocolAgent()
                .Answer("VisualTree.enumerate", """[{"handle":"2","type":"TextBlock","name":"Label","children":[]}]""")
                .Answer("Property.get", """{"props":[]}""")
                .Answer("Source.get", System.Text.Json.JsonSerializer.Serialize(new
                {
                    fileName = "ms-appx:///Page.xaml", lineNumber = 2, columnNumber = 1,
                    authoredState = "available", xaml = declaration,
                }))
                .Answer("Internal.elementAnchor", """{"anchor":"opaque","unique":true}""")
                .Answer("Internal.sourceRoot", System.Text.Json.JsonSerializer.Serialize(new { sourceRoot = root.FullName }));
            var result = CommentSelectionCapture.CaptureElement((uint)agent.Pid, "2");
            Assert.AreEqual(CaptureStatus.Ok, result.Status);
            Assert.AreEqual("Label", result.Element!.Name);
            Assert.AreEqual(declaration, result.Element.Authored!.Declaration);
            StringAssert.Contains(result.Element.Authored.Scope, "Root");
        }
        finally { root.Delete(recursive: true); }
    }

    [TestMethod]
    public void StaleHandleAfterSourceReadCannotProduceAnAuthoredCapture()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", Tree)
            .Answer("Property.get", """{"props":[]}""")
            .Answer("Source.get", """{"fileName":"ms-appx:///Page.xaml","lineNumber":2,"columnNumber":1}""")
            .Error("Internal.elementAnchor", -32000, "stale-handle", "The selected element was removed.");
        var result = CommentSelectionCapture.CaptureElement((uint)agent.Pid, "2");
        Assert.AreEqual(CaptureStatus.Failed, result.Status);
        Assert.AreEqual("stale-handle", result.Error!.Token);
        Assert.IsNull(result.Element);
    }

    [TestMethod]
    public void FindNode_DeepChild_ReturnsTypeAndName()
    {
        var node = CommentSelectionCapture.FindNode(Tree, "4");
        Assert.IsNotNull(node);
        Assert.AreEqual("Border", node.Type);
        Assert.AreEqual("Inner", node.Name);
    }

    [TestMethod]
    public void FindNode_MissingHandle_ReturnsNull()
        => Assert.IsNull(CommentSelectionCapture.FindNode(Tree, "999"));

    [TestMethod]
    public void FindNode_MalformedTree_IsNotAnEmptyCensus()
        => Assert.Throws<System.Text.Json.JsonException>(() => CommentSelectionCapture.FindNode("ERR no-agent", "1"));

    // the identity bundle is read out of the census, and the census of a real app nests far past
    // System.Text.Json's default 64-level ceiling (AI Dev Gallery: 97). A deserializer left at the default
    // silently yields NO nodes, so every captured anchor gets an empty type/name — which then reads downstream
    // as "the element was renamed or removed" for elements that are on screen.
    [TestMethod]
    public void FindNode_DeeplyNestedCensus_PastDefaultJsonDepth_StillResolves()
    {
        var deep = """[{"handle":"119","name":"Leaf","type":"Grid","children":[]}]""";
        for (var index = 118; index >= 0; index--)
        {
            deep = $"[{{\"handle\":\"{index}\",\"name\":\"Parent\",\"type\":\"Grid\",\"children\":{deep}}}]";
        }
        var node = CommentSelectionCapture.FindNode(deep, "119");
        Assert.IsNotNull(node, "A census deeper than 64 JSON levels must still be walkable — otherwise identity capture is silently empty.");
        Assert.AreEqual("Leaf", node.Name);
        Assert.AreEqual("Grid", node.Type);
    }

    [TestMethod]
    public void ExtractContent_PrefersText()
    {
        var props = """{"handle":"2","props":[{"name":"Text","value":"Hello","valueType":"String"},{"name":"Content","value":"Ignored","valueType":"String"}]}""";
        Assert.AreEqual("Hello", CommentSelectionCapture.ExtractContent(props));
    }

    [TestMethod]
    public void ExtractContent_FallsBackToContent()
    {
        var props = """{"handle":"3","props":[{"name":"Content","value":"Click me","valueType":"String"}]}""";
        Assert.AreEqual("Click me", CommentSelectionCapture.ExtractContent(props));
    }

    [TestMethod]
    public void ExtractContent_RejectsObjectPlaceholder()
    {
        var props = """{"handle":"1","props":[{"name":"Content","value":"{Grid}","valueType":"Object"}]}""";
        Assert.IsNull(CommentSelectionCapture.ExtractContent(props));
    }

    [TestMethod]
    public void ExtractContent_RejectsEmpty()
    {
        var props = """{"handle":"1","props":[{"name":"Text","value":"","valueType":"String"}]}""";
        Assert.IsNull(CommentSelectionCapture.ExtractContent(props));
    }

    [TestMethod]
    public void ParseSource_InstrumentedApp_ReturnsUriLeafAndLine()
    {
        var (uri, file, line, column) = CommentSelectionCapture.ParseSource(
            """{"handle":5,"fileName":"ms-appx:///Views/MainWindow.xaml","lineNumber":35,"columnNumber":21}""");
        Assert.AreEqual("ms-appx:///Views/MainWindow.xaml", uri);
        Assert.AreEqual(@"Views\MainWindow.xaml", file);
        Assert.AreEqual(35, line);
        Assert.AreEqual(21, column);
    }

    [TestMethod]
    [DataRow("MainPage.xaml", "ms-appx:///MainPage.xaml")]
    [DataRow("Views/Header.xaml", null)]
    public void ParseSource_DiskMatchedNewNote_UsesAuthoredCoordinatesWithoutChangingTheWire(string resource, string? expectedUri)
    {
        var json = $$"""
            {"fileName":"ms-appx:///{{resource}}","lineNumber":246,"columnNumber":128,
             "authoredState":"available","authoredFileName":"MainPage.xaml","authoredLineNumber":246,
             "authoredColumnNumber":25,"coordinateProvenance":"disk-matched-unique-declaration"}
            """;
        var (uri, file, line, column) = CommentSelectionCapture.ParseSource(json);
        Assert.AreEqual(expectedUri, uri, "A compiled Link must not override the authored file during comment resolution.");
        Assert.AreEqual("MainPage.xaml", file);
        Assert.AreEqual(246, line);
        Assert.AreEqual(25, column);
        using var original = System.Text.Json.JsonDocument.Parse(json);
        Assert.AreEqual(128, original.RootElement.GetProperty("columnNumber").GetInt32());
    }

    [TestMethod]
    [DataRow("available", "unique-source-line")]
    [DataRow("unverifiedBuild", "disk-matched-unique-declaration")]
    public void ParseSource_AdvisoryOrUnavailableMap_DoesNotPromoteItsAuthoredCoordinates(string state, string provenance)
    {
        var (_, _, _, column) = CommentSelectionCapture.ParseSource($$"""
            {"fileName":"ms-appx:///MainPage.xaml","lineNumber":246,"columnNumber":128,
             "authoredState":"{{state}}","authoredFileName":"MainPage.xaml","authoredLineNumber":246,
             "authoredColumnNumber":25,"coordinateProvenance":"{{provenance}}"}
            """);
        Assert.AreEqual(128, column);
    }

    [TestMethod]
    public void ParseSource_DiskMatchedResponseWithoutAuthoredCoordinates_IsRejected()
    {
        Assert.ThrowsExactly<System.Text.Json.JsonException>(() => CommentSelectionCapture.ParseSource("""
            {"fileName":"ms-appx:///MainPage.xaml","lineNumber":246,"columnNumber":128,
             "authoredState":"available","coordinateProvenance":"disk-matched-unique-declaration"}
            """));
    }

    [TestMethod]
    public void ParseSource_Uninstrumented_ReturnsAllNull()
    {
        var (uri, file, line, column) = CommentSelectionCapture.ParseSource(
            """{"handle":5,"fileName":"","lineNumber":0,"columnNumber":0}""");
        Assert.IsNull(uri);
        Assert.IsNull(file);
        Assert.IsNull(line);
        Assert.IsNull(column);
    }

    [TestMethod]
    public void ParseSource_MalformedInput_IsNotUninstrumentedSuccess()
    {
        Assert.Throws<System.Text.Json.JsonException>(() => CommentSelectionCapture.ParseSource("ERR bad-handle"));
    }

    [TestMethod]
    public void ShortType_FullyQualified_ReturnsLeaf()
        => Assert.AreEqual("Button", CommentSelectionCapture.ShortType("Microsoft.UI.Xaml.Controls.Button"));

    [TestMethod]
    public void ShortType_AlreadyShort_Unchanged()
        => Assert.AreEqual("Grid", CommentSelectionCapture.ShortType("Grid"));

    [TestMethod]
    public void ShortType_Null_ReturnsNull()
        => Assert.IsNull(CommentSelectionCapture.ShortType(null));

    [TestMethod]
    public void TryResolvePid_NumericPid_Succeeds()
    {
        var resolver = new CommentTestTargetResolver(15412);
        Assert.IsTrue(DevToolsCommentsAddCommand.TryResolvePid(resolver, "15412", CancellationToken.None, out var pid, out var error));
        Assert.AreEqual(15412u, pid);
        Assert.IsNull(error);
        Assert.AreEqual("15412", resolver.LastApp);
        Assert.IsTrue(resolver.ProcessOnlyRequested);
    }

    [TestMethod]
    public void TryResolvePid_UnknownName_Fails()
    {
        Assert.IsFalse(DevToolsCommentsAddCommand.TryResolvePid(
            new CommentTestTargetResolver(error: "No matching app"), "winapp-no-such-process-xyz", CancellationToken.None, out _, out var error));
        Assert.IsNotNull(error);
    }

    [TestMethod]
    public void Capture_CommonClient_PreservesIdentitySourceAndAnchor()
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("Selection.poll", """{"handle":"2"}""")
            .Answer("VisualTree.enumerate", Tree)
            .Answer("Property.get", """{"props":[{"name":"Text","value":"Header","valueType":"String"}]}""")
            .Answer("Source.get", """{"fileName":"ms-appx:///Views/MainWindow.xaml","lineNumber":35,"columnNumber":21}""")
            .Answer("Internal.elementAnchor", """{"anchor":"Root/0"}""")
            .Answer("Internal.sourceRoot", """{"sourceRoot":"C:\\owned\\project"}""");

        var result = CommentSelectionCapture.Capture((uint)agent.Pid);

        Assert.AreEqual(CaptureStatus.Ok, result.Status, result.Error?.Message);
        Assert.AreEqual("2", result.Element!.Handle);
        Assert.AreEqual("HeaderText", result.Element.Name);
        Assert.AreEqual("TextBlock", result.Element.Type);
        Assert.AreEqual("Header", result.Element.Content);
        Assert.AreEqual(@"Views\MainWindow.xaml", result.Element.SourceFile);
        Assert.AreEqual(35, result.Element.Line);
        Assert.AreEqual("Root/0", result.Element.ElementPath);
        Assert.AreEqual(@"C:\owned\project", result.Element.SourceRoot);
        CollectionAssert.AreEqual(CaptureCalls, agent.Received);
    }

    [TestMethod]
    public void Capture_CommonClient_PropagatesActualRefusal()
    {
        using var agent = new FakeDevToolsProtocolAgent().Error("Selection.poll", -32004, "unauthorized", "controlled refusal");
        var result = CommentSelectionCapture.Capture((uint)agent.Pid);
        Assert.AreEqual(CaptureStatus.Failed, result.Status);
        Assert.AreEqual(new DevToolsProtocolError(-32004, "unauthorized", "controlled refusal"), result.Error);
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("""[{"handle":"1","type":"Grid","children":[42]}]""")]
    [DataRow("""[{"handle":"1","type":"Grid","children":{}}]""")]
    public void Capture_CommonClient_MalformedTreeIsNotMissingSelection(string tree)
    {
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("Selection.poll", """{"handle":"2"}""").Answer("VisualTree.enumerate", tree);
        var result = CommentSelectionCapture.Capture((uint)agent.Pid);
        Assert.AreEqual(CaptureStatus.Failed, result.Status);
        Assert.AreEqual("parse-error", result.Error!.Token);
        Assert.AreEqual(2, agent.Received.Count);
    }
}

internal sealed class CommentTestTargetResolver(int pid = 4321, string? error = null) : IUiTargetResolver
{
    public bool ProcessOnlyRequested { get; private set; }

    public Task<UiTarget> ResolveProcessAsync(string app, CancellationToken ct)
    {
        ProcessOnlyRequested = true;
        return ResolveAsync(app, null, ct);
    }

    public string? LastApp { get; private set; }

    public Task<UiTarget> ResolveAsync(string? app, long? hwnd, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        LastApp = app;
        return error is not null
            ? throw new InvalidOperationException(error)
            : Task.FromResult(new UiTarget { ProcessId = pid });
    }
}

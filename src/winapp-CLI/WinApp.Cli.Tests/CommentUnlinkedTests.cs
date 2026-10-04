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
/// An app launched without a project (for example a C++ app run from its build output) still keeps its comments:
/// they are saved where winapp was run, shown back to the app, and carry enough live identity to search for.
/// </summary>
[TestClass]
[DoNotParallelize]
public class CommentUnlinkedTests
{
    private const string Tree = """
        [{"handle":"1","name":"","type":"Microsoft.UI.Xaml.Controls.Grid","children":[
          {"handle":"2","name":"ShippedPill","type":"Microsoft.UI.Xaml.Controls.Border","children":[]}]}]
        """;

    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void Setup() => _root = Directory.CreateTempSubdirectory("winapp-unlinked-comments-");

    [TestCleanup]
    public void Cleanup() => _root.Delete(recursive: true);

    private static FakeDevToolsProtocolAgent Agent(string sourceRootJson, bool identityExtras = true)
    {
        var agent = new FakeDevToolsProtocolAgent()
            .Answer("VisualTree.enumerate", Tree)
            .Answer("Property.get", """{"props":[]}""")
            .Answer("Source.get", """{"fileName":""}""")
            .Answer("Internal.elementAnchor", """{"anchor":"","unique":false}""")
            .Answer("Internal.sourceRoot", sourceRootJson)
            .Answer("Internal.setComments", """{"total":1,"placed":0}""");
        if (identityExtras)
        {
            agent.Answer("VisualTree.getPreviews", """
                    {"previews":[{"handle":"2","preview":"","automationId":"ShippedStatus"}],"requested":1,"returned":1,"truncated":false}
                    """)
                .Answer("Surface.list", """
                    {"activeSurface":"Orders","surfaces":[
                      {"name":"Settings","rootHandle":"9","window":"7","active":false,"contentWidth":1,"contentHeight":1,"screenRect":{"x":0,"y":0,"w":1,"h":1},"isIsland":false,"hostVisible":true,"derived":false},
                      {"name":"Orders","rootHandle":"1","window":"5","active":true,"contentWidth":1,"contentHeight":1,"screenRect":{"x":0,"y":0,"w":1,"h":1},"isIsland":false,"hostVisible":true,"derived":false}]}
                    """);
        }
        return agent;
    }

    private async Task<(int Exit, string Output)> AddAsync(FakeDevToolsProtocolAgent agent, CommentStore store, string cwd)
    {
        var console = new TestConsole();
        // The in-app writer runs in the app's working directory, which is its build output.
        var handler = new DevToolsCommentsAddCommand.Handler(store, new CommentAnchorResolver(), new CommentPusher(store),
            new CommentTestTargetResolver(agent.Pid), new FixedDirectory(cwd), console,
            NullLogger<DevToolsCommentsAddCommand>.Instance);
        var exit = await handler.InvokeAsync(new DevToolsCommentsAddCommand().Parse(
            ["--app", agent.Pid.ToString(), "--from-element", "2", "--text", "this looks off"]));
        return (exit, console.Output);
    }

    [TestMethod]
    public async Task AnAppWithoutAProject_KeepsItsCommentsInTheFolderItWasLaunchedFrom()
    {
        var launch = _root.CreateSubdirectory("launch");
        var output = _root.CreateSubdirectory("bin");
        using var agent = Agent(JsonSerializer.Serialize(new { sourceRoot = "", commentRoot = launch.FullName }));
        var store = new CommentStore();

        var (exit, console) = await AddAsync(agent, store, output.FullName);

        Assert.AreEqual(0, exit, console);
        var comment = store.Load(store.GetStorePath(launch)).Comments.Single();
        Assert.AreEqual(launch.FullName, comment.ProjectRoot);
        Assert.IsTrue(comment.Anchor.Weak, "it is still not linked to source");
        Assert.IsFalse(File.Exists(store.GetStorePath(output)), "nothing is written into the build output");
        Assert.IsTrue(agent.ReceivedRequests.Any(r => r.Contains("Internal.setComments") && r.Contains("this looks off")),
            "the running app is shown the comment it just saved");
    }

    [TestMethod]
    public async Task AnUnlinkedComment_StoresTheLiveAutomationIdAndWindowTitle()
    {
        using var agent = Agent(JsonSerializer.Serialize(new { sourceRoot = "", commentRoot = _root.FullName }));
        var store = new CommentStore();

        Assert.AreEqual(0, (await AddAsync(agent, store, _root.FullName)).Exit);

        var comment = store.Load(store.GetStorePath(_root)).Comments.Single();
        Assert.AreEqual("ShippedPill", comment.Anchor.Identity.Name);
        Assert.AreEqual("ShippedStatus", comment.Anchor.Identity.AutomationId);
        Assert.AreEqual("Orders", comment.Context?.WindowTitle, "the window whose content holds the element, not another one");
    }

    [TestMethod]
    public async Task IdentityExtrasAreBestEffort()
    {
        using var agent = Agent(JsonSerializer.Serialize(new { sourceRoot = "", commentRoot = _root.FullName }), identityExtras: false);
        var store = new CommentStore();

        Assert.AreEqual(0, (await AddAsync(agent, store, _root.FullName)).Exit);

        var comment = store.Load(store.GetStorePath(_root)).Comments.Single();
        Assert.IsNull(comment.Anchor.Identity.AutomationId);
        Assert.IsNull(comment.Context);
    }

    [TestMethod]
    public void AnAppFromAnOlderEngine_StillReportsOnlyItsSourceRoot()
    {
        Assert.AreEqual("", CommentSelectionCapture.ReadCommentRoot(DevToolsProtocolResponse.Success("""{"sourceRoot":""}""")));
        Assert.AreEqual(@"C:\p", CommentSelectionCapture.ReadCommentRoot(
            DevToolsProtocolResponse.Success("""{"sourceRoot":"C:\\p","commentRoot":"C:\\other"}""")), "a project always wins");
    }

    [TestMethod]
    public void Ancestors_AreNearestFirst()
    {
        CollectionAssert.AreEqual(new[] { "2", "1" }, CommentSelectionCapture.AncestorsOf(Tree, "2").ToArray());
        Assert.AreEqual(0, CommentSelectionCapture.AncestorsOf(Tree, "404").Count);
    }

    private sealed class FixedDirectory(string path) : ICurrentDirectoryProvider
    {
        public string GetCurrentDirectory() => path;
        public DirectoryInfo GetCurrentDirectoryInfo() => new(path);
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Tests;

/// <summary>
/// A comment authored from a live element carries a picture of it. The image is best effort, stored beside the
/// comments store, referenced (never inlined) from command output, and removed with its comment.
/// </summary>
[TestClass]
[DoNotParallelize]
public class CommentScreenshotTests
{
    // A 1x1 PNG.
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=";

    private static readonly string[] NotCaptured = ["WebView2"];

    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void Setup() => _root = Directory.CreateTempSubdirectory("winapp-comment-shots-");

    [TestCleanup]
    public void Cleanup() => _root.Delete(recursive: true);

    private FakeDevToolsProtocolAgent Agent(bool snapshot) => new FakeDevToolsProtocolAgent()
        .Answer("VisualTree.enumerate", """[{"handle":"2","name":"StatusPill","type":"Border","children":[]}]""")
        .Answer("Property.get", """{"props":[]}""")
        .Answer("Source.get", """{"fileName":""}""")
        .Answer("Internal.elementAnchor", """{"anchor":"","unique":false}""")
        .Answer("Internal.sourceRoot", JsonSerializer.Serialize(new { sourceRoot = _root.FullName }))
        .Answer("Internal.setComments", """{"total":1,"placed":0}""")
        .Answer(snapshot ? "Internal.elementSnapshot" : "Unused.method", JsonSerializer.Serialize(new
        {
            png = Png, width = 1, height = 1, visibility = "partial", theme = "dark", masked = 0,
            notCaptured = NotCaptured, timing = new { totalMs = 60.0 },
        }));

    private async Task<(int Exit, string Output)> AddAsync(FakeDevToolsProtocolAgent agent, CommentStore store, params string[] extra)
    {
        var console = new TestConsole();
        var handler = new DevToolsCommentsAddCommand.Handler(store, new CommentAnchorResolver(), new CommentPusher(store),
            new CommentTestTargetResolver(agent.Pid), new FixedDirectory(_root.FullName), console,
            NullLogger<DevToolsCommentsAddCommand>.Instance);
        string[] arguments = ["--app", agent.Pid.ToString(), "--from-element", "2", "--text", "this looks off", "--json", .. extra];
        var exit = await handler.InvokeAsync(new DevToolsCommentsAddCommand().Parse(arguments));
        return (exit, console.Output);
    }

    [TestMethod]
    public async Task AddFromElement_StoresTheImageBesideTheStore_AndReferencesItByPath()
    {
        using var agent = Agent(snapshot: true);
        var store = new CommentStore();
        var (exit, output) = await AddAsync(agent, store, "--id", "cmt_shot");

        Assert.AreEqual(0, exit, output);
        var storePath = store.GetStorePath(_root);
        var file = Path.Combine(Path.GetDirectoryName(storePath)!, "ui-comment-shots", "cmt_shot.png");
        CollectionAssert.AreEqual(Convert.FromBase64String(Png), File.ReadAllBytes(file));
        var stored = store.Get(storePath, "cmt_shot")!.Screenshot!;
        Assert.AreEqual("ui-comment-shots/cmt_shot.png", stored.Path, "the store keeps a path relative to .winapp");
        Assert.AreEqual("partial", stored.Visibility);
        CollectionAssert.AreEqual(NotCaptured, stored.NotCaptured);
        using var json = JsonDocument.Parse(output);
        var shot = json.RootElement.GetProperty("comment").GetProperty("screenshot");
        Assert.AreEqual(file, shot.GetProperty("path").GetString(), "output names the file absolutely");
        Assert.IsFalse(output.Contains(Png, StringComparison.Ordinal), "the image is never inlined");
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp-*").Length, "no staging file is left behind");
    }

    [TestMethod]
    public async Task AddFromElement_WhenTheAppCannotRenderIt_SavesTheCommentWithoutAnImage()
    {
        using var agent = Agent(snapshot: false);
        var store = new CommentStore();
        var (exit, output) = await AddAsync(agent, store);

        Assert.AreEqual(0, exit, output);
        Assert.IsTrue(agent.Received.Contains("Internal.elementSnapshot"), "a capture was attempted");
        var comment = store.Load(store.GetStorePath(_root)).Comments.Single();
        Assert.IsNull(comment.Screenshot);
        Assert.IsFalse(JsonDocument.Parse(output).RootElement.GetProperty("comment").TryGetProperty("screenshot", out _));
    }

    [TestMethod]
    public async Task OfflineAdd_NeverAsksForAnImage()
    {
        using var agent = Agent(snapshot: true);
        var store = new CommentStore();
        var console = new TestConsole();
        var handler = new DevToolsCommentsAddCommand.Handler(store, new CommentAnchorResolver(), new CommentPusher(store),
            new CommentTestTargetResolver(agent.Pid), new FixedDirectory(_root.FullName), console,
            NullLogger<DevToolsCommentsAddCommand>.Instance);
        Assert.AreEqual(0, await handler.InvokeAsync(new DevToolsCommentsAddCommand().Parse(["--name", "StatusPill", "--text", "x"])));
        Assert.IsFalse(agent.Received.Contains("Internal.elementSnapshot"));
        Assert.IsNull(store.Load(store.GetStorePath(_root)).Comments.Single().Screenshot);
    }

    [TestMethod]
    public void Delete_RemovesTheImage_AndAReplacementWithoutOneReleasesIt()
    {
        var store = new CommentStore();
        var storePath = store.GetStorePath(_root);
        var first = WithShot(store, storePath, "cmt_a");
        var second = WithShot(store, storePath, "cmt_b");

        store.Delete(storePath, "cmt_a");
        Assert.IsFalse(File.Exists(first), "a deleted comment takes its image with it");
        Assert.IsTrue(File.Exists(second), "other comments keep theirs");

        store.Update(storePath, "cmt_b", c => c.Status = CommentStatus.Resolved);
        Assert.IsTrue(File.Exists(second), "resolving keeps the image");

        store.AddOrReplace(storePath, NewComment("cmt_b"), out _);
        Assert.IsFalse(File.Exists(second), "a re-save without an image releases the old one");
    }

    [TestMethod]
    public void AStoredReference_OutsideItsOwnImage_IsNeverReadOrDeleted()
    {
        var store = new CommentStore();
        var storePath = store.GetStorePath(_root);
        var victim = Path.Combine(_root.FullName, "victim.png");
        File.WriteAllText(victim, "keep");
        var comment = NewComment("cmt_c");
        comment.Screenshot = new CommentScreenshot { Path = "../victim.png" };
        store.Add(storePath, comment);

        Assert.IsNull(CommentScreenshots.ForOutput(storePath, comment));
        store.Delete(storePath, "cmt_c");
        Assert.IsTrue(File.Exists(victim));
    }

    private static string WithShot(CommentStore store, string storePath, string id)
    {
        var comment = NewComment(id);
        comment.Screenshot = new CommentScreenshot { Path = CommentScreenshots.RelativePath(id), Width = 1, Height = 1 };
        store.Add(storePath, comment);
        var file = Path.Combine(Path.GetDirectoryName(storePath)!, CommentScreenshots.DirectoryName, id + ".png");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, Convert.FromBase64String(Png));
        Assert.AreEqual(file, CommentScreenshots.ForOutput(storePath, comment)!.Path);
        return file;
    }

    private static Comment NewComment(string id) => new()
    {
        Id = id, Text = "note", Anchor = new CommentAnchor { Identity = new CommentIdentity { Name = "StatusPill" } },
    };

    private sealed class FixedDirectory(string path) : ICurrentDirectoryProvider
    {
        public string GetCurrentDirectory() => path;
        public DirectoryInfo GetCurrentDirectoryInfo() => new(path);
    }
}

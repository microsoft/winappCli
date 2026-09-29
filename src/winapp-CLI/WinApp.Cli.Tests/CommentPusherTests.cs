// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Tests;

/// <summary>
/// Pins what the running app is told about: the open comments only, each carrying the live anchor the
/// overlay needs to put its marker back. A resolved comment is backlog, not chrome.
/// </summary>
[TestClass]
[DoNotParallelize]
public class CommentPusherTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Correction_PushUnknownRootNeedsExplicitOverride(bool explicitRoot)
    {
        var root = Directory.CreateTempSubdirectory("winapp-correction-scope-");
        try
        {
            var store = new CommentStore();
            var comment = NewComment("scoped", CommentStatus.Open, "Root/0");
            comment.ProjectRoot = root.FullName;
            store.Add(store.GetStorePath(root), comment);
            store.Add(store.GetStorePath(root), NewComment("unscoped", CommentStatus.Open, "Root/1"));
            using var agent = new FakeDevToolsProtocolAgent()
                .Answer("Internal.sourceRoot", """{"sourceRoot":""}""")
                .Answer("Internal.setComments", explicitRoot ? """{"total":1,"placed":0}""" : """{"total":0,"placed":0}""");
            Assert.AreEqual((explicitRoot ? 1 : 0, 0),
                new CommentPusher(store).Push((uint)agent.Pid, root.FullName, explicitRoot ? root.FullName : null));
            using var request = JsonDocument.Parse(agent.ReceivedRequests.Last());
            var rows = request.RootElement.GetProperty("params").GetProperty("comments");
            Assert.AreEqual(explicitRoot ? 1 : 0, rows.GetArrayLength());
            if (explicitRoot)
            {
                Assert.AreEqual("scoped", rows[0].GetProperty("id").GetString());
                CollectionAssert.DoesNotContain(agent.Received, "Internal.sourceRoot");
            }
        }
        finally { root.Delete(recursive: true); }
    }

    [TestMethod]
    public void Correction_PushCancellation_PreservesSavedBytesAndLogsWarning()
    {
        var root = Directory.CreateTempSubdirectory("winapp-correction-push-cancel-");
        try
        {
            var store = new CommentStore();
            var path = store.GetStorePath(root);
            store.Add(path, NewComment("saved", CommentStatus.Open, null));
            var bytes = File.ReadAllBytes(path);
            using var cancel = new CancellationTokenSource();
            using var agent = new FakeDevToolsProtocolAgent().Answer("Internal.sourceRoot", """{"sourceRoot":""}""",
                _ => { cancel.Cancel(); return true; });
            var logger = new RecordingLogger();
            Assert.IsNull(new CommentPusher(store, logger).Push((uint)agent.Pid, root.FullName, cancellationToken: cancel.Token));
            Assert.AreEqual(1, logger.Warnings);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
            CollectionAssert.AreEqual(new List<string> { "Internal.sourceRoot" }, agent.Received);
        }
        finally { root.Delete(recursive: true); }
    }

    [TestMethod]
    public void Correction_PushDoesNotLeakOtherProjects()
    {
        var root = Directory.CreateTempSubdirectory("winapp-correction-push-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, ".git"));
            var a = root.CreateSubdirectory("AppA");
            var b = root.CreateSubdirectory("AppB");
            var store = new CommentStore();
            var first = NewComment("only-a", CommentStatus.Open, "Root/0");
            first.ProjectRoot = a.FullName;
            var second = NewComment("only-b", CommentStatus.Open, "Root/0");
            second.ProjectRoot = b.FullName;
            store.Add(store.GetStorePath(a), first);
            store.Add(store.GetStorePath(b), second);
            using var agent = new FakeDevToolsProtocolAgent()
                .Answer("Internal.sourceRoot", JsonSerializer.Serialize(new { sourceRoot = b.FullName }))
                .Answer("Internal.setComments", """{"total":1,"placed":1}""");
            new CommentPusher(store).Push((uint)agent.Pid, root.FullName);
            using var request = JsonDocument.Parse(agent.ReceivedRequests.Last());
            var rows = request.RootElement.GetProperty("params").GetProperty("comments");
            Assert.AreEqual(1, rows.GetArrayLength());
            Assert.AreEqual("only-b", rows[0].GetProperty("id").GetString());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void Push_CommonClient_UsesAppRootAndActualPlacementCounts()
    {
        var root = Directory.CreateTempSubdirectory("winapp-host-pusher-");
        try
        {
            var store = new CommentStore();
            var comment = NewComment("cmt_owned", CommentStatus.Open, "Root/0");
            comment.ProjectRoot = root.FullName;
            store.Add(store.GetStorePath(root), comment);
            using var agent = new FakeDevToolsProtocolAgent()
                .Answer("Internal.sourceRoot", $"{{\"sourceRoot\":{JsonSerializer.Serialize(root.FullName)}}}")
                .Answer("Internal.setComments", """{"total":1,"placed":0}""");
            Assert.AreEqual((1, 0), new CommentPusher(store).Push((uint)agent.Pid, @"C:\not-the-app"));
            using var request = JsonDocument.Parse(agent.ReceivedRequests.Last());
            Assert.AreEqual("cmt_owned", request.RootElement.GetProperty("params").GetProperty("comments")[0].GetProperty("id").GetString());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    [DataRow("""{"total":-1,"placed":0}""")]
    [DataRow("""{"total":0,"placed":1}""")]
    [DataRow("""{"total":1,"placed":-1}""")]
    [DataRow("""{"total":"1","placed":0}""")]
    [DataRow("""{"total":1,"placed":0}""")]
    public void Push_InvalidPlacement_IsLoggedAndNeverReportedAsPlaced(string reply)
    {
        var root = Directory.CreateTempSubdirectory("winapp-host-pusher-");
        try
        {
            var store = new CommentStore();
            var path = store.GetStorePath(root);
            store.Add(path, NewComment("cmt_owned", CommentStatus.Open, "Root/0"));
            var original = File.ReadAllBytes(path);
            var logger = new RecordingLogger();
            using var agent = new FakeDevToolsProtocolAgent().Answer("Internal.sourceRoot", """{"sourceRoot":""}""")
                .Answer("Internal.setComments", reply);
            Assert.IsNull(new CommentPusher(store, logger).Push((uint)agent.Pid, root.FullName));
            Assert.AreEqual(1, logger.Warnings);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private sealed class RecordingLogger : ILogger<CommentPusher>
    {
        public int Warnings { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings++;
            }
        }
    }

    private static Comment NewComment(string id, string status, string? elementPath) => new()
    {
        Id = id,
        Text = id + " note",
        Status = status,
        Anchor = new CommentAnchor
        {
            SourceFile = "MainWindow.xaml",
            ElementPath = elementPath,
            Identity = new CommentIdentity { Type = "Button" },
        },
    };

    [TestMethod]
    public void ToTapComments_PushesOnlyOpenComments()
    {
        var doc = new CommentStoreDocument
        {
            Comments =
            [
                NewComment("cmt_open", CommentStatus.Open, "RtList/0/2"),
                NewComment("cmt_done", CommentStatus.Resolved, "RtList/0/3"),
                NewComment("cmt_stale", CommentStatus.Stale, "RtList/0/4"),
            ],
        };

        var pushed = CommentPusher.ToTapComments(doc);

        Assert.AreEqual(1, pushed.Count);
        Assert.AreEqual("cmt_open", pushed[0].Id);
        Assert.AreEqual("RtList/0/2", pushed[0].Anchor);
    }

    [TestMethod]
    public void ToTapComments_KeepsUnanchoredComments()
    {
        // A comment with no live anchor still counts — it simply has no marker until its element exists. The
        // count and the markers are allowed to differ; what is not allowed is dropping the comment silently.
        var doc = new CommentStoreDocument
        {
            Comments = [NewComment("cmt_noanchor", CommentStatus.Open, null)],
        };

        var pushed = CommentPusher.ToTapComments(doc);

        Assert.AreEqual(1, pushed.Count);
        Assert.IsNull(pushed[0].Anchor);
    }

    [TestMethod]
    public void ToTapComments_EmptyStore_PushesNothing()
        => Assert.AreEqual(0, CommentPusher.ToTapComments(new CommentStoreDocument()).Count);
}

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
    public void Push_LargeCommentSet_ArrivesInPartsThatEachFitOnePipeLine()
    {
        var root = Directory.CreateTempSubdirectory("winapp-push-large-");
        try
        {
            var doc = new CommentStoreDocument();
            for (var i = 0; i < 1000; i++)
            {
                var comment = NewComment($"cmt_{i:D4}", CommentStatus.Open, $"Root/0/{i}");
                comment.Text = $"Note {i}: \"quotes\", <tags> & ünïcödé 💬 " + new string('x', 1000);
                comment.ProjectRoot = root.FullName;
                doc.Comments.Add(comment);
            }
            var huge = NewComment("cmt_huge", CommentStatus.Open, "Root/1");
            huge.Text = string.Concat(Enumerable.Repeat("\"<+>\" ", 30_000));
            huge.ProjectRoot = root.FullName;
            doc.Comments.Add(huge);
            var store = new SnapshotStore(doc);
            using var agent = new FakeDevToolsProtocolAgent()
                .Answer("Internal.sourceRoot", JsonSerializer.Serialize(new { sourceRoot = root.FullName }))
                .Answer("Internal.setComments", """{"total":1001,"placed":0}""", request =>
                {
                    using var parsed = JsonDocument.Parse(request);
                    var p = parsed.RootElement.GetProperty("params");
                    return p.GetProperty("part").GetInt32() + 1 == p.GetProperty("parts").GetInt32();
                })
                .Answer("Internal.setComments", """{"staged":1}""");

            var log = new RecordingLogger();
            Assert.AreEqual((1001, 0), new CommentPusher(store, log).Push((uint)agent.Pid, root.FullName), log.Last);

            var parts = agent.ReceivedRequests.Where(r => r.Contains("Internal.setComments", StringComparison.Ordinal)).ToList();
            Assert.IsGreaterThan(1, parts.Count);
            var data = new System.Text.StringBuilder();
            for (var i = 0; i < parts.Count; i++)
            {
                Assert.IsLessThanOrEqualTo(WinApp.Cli.Services.DevTools.VisualTreeTap.MaxRequestBytes, System.Text.Encoding.UTF8.GetByteCount(parts[i]) + 1);
                using var parsed = JsonDocument.Parse(parts[i]);
                var p = parsed.RootElement.GetProperty("params");
                Assert.AreEqual(i, p.GetProperty("part").GetInt32());
                Assert.AreEqual(parts.Count, p.GetProperty("parts").GetInt32());
                data.Append(p.GetProperty("data").GetString());
            }
            using var set = JsonDocument.Parse(data.ToString());
            var comments = set.RootElement.GetProperty("comments");
            Assert.AreEqual(1001, comments.GetArrayLength());
            Assert.AreEqual(huge.Text, comments[1000].GetProperty("text").GetString());
            Assert.AreEqual(doc.Comments[999].Text, comments[999].GetProperty("text").GetString());
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

    // The app is shown open comments only, and told how many are resolved so its empty Comments pane can say so.
    [TestMethod]
    public void Push_TellsTheAppHowManyCommentsAreResolved()
    {
        var root = Directory.CreateTempSubdirectory("winapp-resolved-pusher-");
        try
        {
            var store = new CommentStore();
            foreach (var (id, status) in new[] { ("a", CommentStatus.Resolved), ("b", CommentStatus.Resolved), ("c", CommentStatus.Open) })
            {
                var comment = NewComment(id, status, "Root/0");
                comment.ProjectRoot = root.FullName;
                store.Add(store.GetStorePath(root), comment);
            }
            using var agent = new FakeDevToolsProtocolAgent()
                .Answer("Internal.sourceRoot", $"{{\"sourceRoot\":{JsonSerializer.Serialize(root.FullName)}}}")
                .Answer("Internal.setComments", """{"total":1,"placed":0}""");
            new CommentPusher(store).Push((uint)agent.Pid, root.FullName);
            using var request = JsonDocument.Parse(agent.ReceivedRequests.Last());
            var set = request.RootElement.GetProperty("params");
            Assert.AreEqual(1, set.GetProperty("comments").GetArrayLength(), "only the open comment is drawn");
            Assert.AreEqual(2, set.GetProperty("resolved").GetInt32(), "the resolved ones are counted");
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

    // The pusher only reads the store; a snapshot keeps a 1,000-comment set fast to build.
    private sealed class SnapshotStore(CommentStoreDocument doc) : ICommentStore
    {
        public CommentStoreLocation Locate(string? startDirectory = null) => throw new NotSupportedException();
        public string GetStorePath(DirectoryInfo? baseDirectory = null) => Path.Combine(baseDirectory?.FullName ?? "", "ui-comments.json");
        public CommentStoreDocument Load(string storePath) => doc;
        public Comment Add(string storePath, Comment comment) => throw new NotSupportedException();
        public Comment AddOrReplace(string storePath, Comment comment, out bool replaced) => throw new NotSupportedException();
        public Comment? Update(string storePath, string id, Action<Comment> mutate) => throw new NotSupportedException();
        public Comment? Get(string storePath, string id) => throw new NotSupportedException();
        public Comment? Delete(string storePath, string id) => throw new NotSupportedException();
    }

    private sealed class RecordingLogger : ILogger<CommentPusher>
    {
        public int Warnings { get; private set; }
        public string? Last { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Last = exception?.Message ?? formatter(state, exception);
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

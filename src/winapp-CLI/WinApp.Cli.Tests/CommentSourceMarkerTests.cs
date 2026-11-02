// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public class CommentSourceMarkerTests
{
    private const string Markup = "<Button x:Name=\"SaveButton\" Content=\"Save\"/>";
    private DirectoryInfo root = null!;

    [TestInitialize]
    public void Initialize()
    {
        root = Directory.CreateTempSubdirectory("winapp-source-markers-");
        WriteSource(Markup);
    }

    [TestCleanup]
    public void Cleanup() => root.Delete(recursive: true);

    [TestMethod]
    public void Push_OfflineDeclarationGetsCurrentAnchorOnEveryRefresh_WithoutSavingIt()
    {
        var store = new CommentStore();
        var comment = OfflineComment();
        var path = store.GetStorePath(root);
        store.Add(path, comment);
        var before = File.ReadAllBytes(path);
        var revision = ((ICommentStore)store).GetRevision(comment);
        var warnings = new RecordingLogger();
        var pusher = new CommentPusher(store, warnings);
        for (var refresh = 0; refresh < 2; refresh++)
        {
            var liveAnchor = $"source-qualified-live-anchor-{refresh}";
            using var agent = Agent(markerAnchor: liveAnchor);
            Assert.AreEqual((1, 1), pusher.Push((uint)agent.Pid, root.FullName));
            using var request = JsonDocument.Parse(agent.ReceivedRequests.Last());
            var pushed = request.RootElement.GetProperty("params").GetProperty("comments")[0];
            Assert.AreEqual(liveAnchor, pushed.GetProperty("anchor").GetString());
            Assert.AreEqual(revision ?? "", pushed.GetProperty("revision").GetString());
            Assert.AreEqual("offline", pushed.GetProperty("id").GetString());
        }
        Assert.IsEmpty(warnings.Messages);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
        Assert.IsNull(store.Load(path).Comments.Single().Anchor.ElementPath);
    }

    [TestMethod]
    [DataRow("changed declaration", "captured source declaration")]
    [DataRow("changed scope", "captured source declaration")]
    [DataRow("missing source", "captured source declaration")]
    [DataRow("invalid source", "remains unplaced")]
    [DataRow("duplicate declaration", "captured source declaration")]
    [DataRow("template", "template declaration")]
    [DataRow("weak", "No verified named source declaration")]
    [DataRow("no evidence", "No verified named source declaration")]
    [DataRow("wrong project", "No verified named source declaration")]
    [DataRow("wrong file", "unique, current source match")]
    [DataRow("duplicate live name", "does not identify one live element")]
    [DataRow("no live name", "does not identify one live element")]
    [DataRow("capped search", "live search is incomplete")]
    [DataRow("incomplete classification", "live search is incomplete")]
    [DataRow("stale build", "unique, current source match")]
    [DataRow("no live source", "unique, current source match")]
    [DataRow("likely source", "unique, current source match")]
    [DataRow("nonunique anchor", "unique, current source match")]
    [DataRow("no anchor", "unique, current source match")]
    [DataRow("wrong live project", "unique, current source match")]
    public void Push_UnsafeSourceMappingLeavesMarkerAbsentAndExplainsWhy(string failure, string reason)
    {
        var comment = OfflineComment();
        switch (failure)
        {
            case "changed declaration": WriteSource(Markup.Replace("Save\"", "Changed\"", StringComparison.Ordinal)); break;
            case "changed scope": WriteSource("<Grid>" + Markup + "</Grid>"); break;
            case "missing source": File.Delete(Path.Combine(root.FullName, "MainWindow.xaml")); break;
            case "invalid source": WriteSource("<Invalid"); break;
            case "duplicate declaration": WriteSource(Markup + Markup); break;
            case "template": comment.Anchor.Authored!.Templated = true; break;
            case "weak": comment.Anchor.Weak = true; break;
            case "no evidence": comment.Anchor.Authored = null; break;
            case "wrong project": comment.Anchor.Authored!.ProjectRoot = root.CreateSubdirectory("other").FullName; break;
            case "wrong file": File.Copy(Path.Combine(root.FullName, "MainWindow.xaml"), Path.Combine(root.FullName, "Other.xaml")); break;
            case "wrong live project":
                var other = root.CreateSubdirectory("other");
                File.Copy(Path.Combine(root.FullName, "MainWindow.xaml"), Path.Combine(other.FullName, "MainWindow.xaml"));
                break;
        }
        var store = new CommentStore();
        var path = store.GetStorePath(root);
        store.Add(path, comment);
        var before = File.ReadAllBytes(path);
        var warnings = new RecordingLogger();
        using var agent = Agent(failure);
        Assert.AreEqual((1, 0), new CommentPusher(store, warnings).Push((uint)agent.Pid, root.FullName, root.FullName));
        Assert.HasCount(1, warnings.Messages);
        StringAssert.Contains(warnings.Messages[0], reason);
        using var request = JsonDocument.Parse(agent.ReceivedRequests.Last());
        Assert.AreEqual("", request.RootElement.GetProperty("params").GetProperty("comments")[0].GetProperty("anchor").GetString());
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void Push_ExistingLiveAnchorIsNeverReplacedBySourceResolution()
    {
        var comment = OfflineComment();
        comment.Anchor.ElementPath = "original-live-anchor";
        WriteSource("<Grid/>");
        var store = new CommentStore();
        store.Add(store.GetStorePath(root), comment);
        using var agent = new FakeDevToolsProtocolAgent()
            .Answer("Internal.setComments", """{"total":1,"placed":0}""");
        Assert.AreEqual((1, 0), new CommentPusher(store).Push((uint)agent.Pid, root.FullName, root.FullName));
        CollectionAssert.AreEqual(new List<string> { "Internal.setComments" }, agent.Received);
        using var request = JsonDocument.Parse(agent.ReceivedRequests.Single());
        Assert.AreEqual("original-live-anchor", request.RootElement.GetProperty("params").GetProperty("comments")[0].GetProperty("anchor").GetString());
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("stale build")]
    [DataRow("duplicate live name")]
    public async Task GuestHostSnapshot_ResolvesAgainstGuestSources_WithoutChangingHostIdentity(string? failure)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pair = DuplexStreamPair.Create();
        using var host = pair.Client;
        using var guest = pair.Server;
        var comment = OfflineComment();
        comment.ProjectRoot = @"C:\host\project";
        comment.Anchor.Authored!.ProjectRoot = comment.ProjectRoot;
        var before = JsonSerializer.Serialize(comment, CommentsJsonContext.Default.Comment);
        var pushed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var agent = Agent(failure, onPush: request => pushed.TrySetResult(request));
        var session = new GuestCommentSession("binding", "sandbox", "epoch",
            new(agent.Pid, DateTime.UtcNow.Ticks));
        var endpoint = new GuestCommentEndpoint(session);
        var warnings = new List<string>();
        var serving = endpoint.RunAsync(guest, guest, warnings.Add, timeout.Token, isAppAlive: () => true);
        _ = await GuestCommentFrames.ReadAsync(host, timeout.Token);
        await GuestCommentFrames.WriteAsync(host,
            new GuestCommentReply(GuestCommentReply.RefreshOperationId, Comments: [comment],
                GuestSourceRoot: root.FullName, Generation: 7),
            GuestCommentsJsonContext.Default.GuestCommentReply, timeout.Token);
        var wire = await pushed.Task.WaitAsync(timeout.Token);
        using var request = JsonDocument.Parse(wire);
        var parameters = request.RootElement.GetProperty("params");
        Assert.AreEqual(failure is null ? "source-qualified-live-anchor" : "",
            parameters.GetProperty("comments")[0].GetProperty("anchor").GetString());
        Assert.AreEqual(CommentStore.Revision(comment), parameters.GetProperty("comments")[0].GetProperty("revision").GetString());
        Assert.AreEqual(7, parameters.GetProperty("generation").GetInt64());
        Assert.AreEqual(before, JsonSerializer.Serialize(comment, CommentsJsonContext.Default.Comment));
        await timeout.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => serving);
        Assert.HasCount(failure is null ? 0 : 1, warnings);
    }

    private Comment OfflineComment()
    {
        var comment = new Comment
        {
            Id = "offline", Text = "Review this", ProjectRoot = root.FullName,
            Anchor = new() { SourceFile = "MainWindow.xaml", Identity = new() { Name = "SaveButton" } },
        };
        DevToolsCommentsAddCommand.CaptureNamedDeclaration(comment, root.FullName);
        Assert.IsNotNull(comment.Anchor.Authored);
        return comment;
    }

    private void WriteSource(string markup) => File.WriteAllText(Path.Combine(root.FullName, "MainWindow.xaml"),
        "<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
        "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" + markup + "\n</Page>");

    private FakeDevToolsProtocolAgent Agent(string? failure = null, Func<string, bool>? onPush = null,
        string markerAnchor = "source-qualified-live-anchor")
    {
        var matches = failure == "no live name" ? "[]" : failure == "duplicate live name"
            ? """[{"handle":"2","name":"SaveButton","type":"Button"},{"handle":"3","name":"SaveButton","type":"Button"}]"""
            : """[{"handle":"2","name":"SaveButton","type":"Button"}]""";
        return new FakeDevToolsProtocolAgent()
            .Answer("Internal.sourceRoot", JsonSerializer.Serialize(new
            {
                sourceRoot = failure == "wrong live project" ? Path.Combine(root.FullName, "other") : root.FullName,
            }))
            .Answer("VisualTree.find", $$"""{"matches":{{matches}},"truncated":{{(failure == "capped search" ? "true" : "false")}},"classificationTruncated":{{(failure == "incomplete classification" ? "true" : "false")}}}""")
            .Answer("VisualTree.enumerate", """[{"handle":"2","name":"SaveButton","type":"Button","children":[]}]""")
            .Answer("Property.get", """{"props":[]}""")
            .Answer("Source.get", JsonSerializer.Serialize(new
            {
                fileName = failure == "wrong file" ? "ms-appx:///Other.xaml" : "ms-appx:///MainWindow.xaml",
                lineNumber = 2, columnNumber = 1,
                authoredFileName = failure == "wrong file" ? "Other.xaml" : "MainWindow.xaml",
                authoredLineNumber = 2, authoredColumnNumber = 1,
                authoredState = failure == "stale build" ? "stale" : failure == "no live source" ? "unavailable" :
                    failure == "likely source" ? "likely" : "available",
                coordinateProvenance = failure == "likely source" ? "likely-source-line" : "disk-matched-unique-declaration",
                sourceEvidence = "Compiler proof", xaml = Markup,
            }))
            .Answer("Internal.elementAnchor", JsonSerializer.Serialize(new
            {
                anchor = failure == "no anchor" ? "" : markerAnchor,
                unique = failure != "nonunique anchor",
            }))
            .Answer("Internal.setComments", """{"total":1,"placed":1}""", request =>
            {
                using var json = JsonDocument.Parse(request);
                var marker = json.RootElement.GetProperty("params").GetProperty("comments")[0].GetProperty("anchor");
                var placed = marker.ValueKind == JsonValueKind.String && marker.GetString() == markerAnchor;
                if (placed) { onPush?.Invoke(request); }
                return placed;
            })
            .Answer("Internal.setComments", """{"total":1,"placed":0}""", request =>
            {
                onPush?.Invoke(request);
                return true;
            });
    }

    private sealed class RecordingLogger : ILogger<CommentPusher>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}

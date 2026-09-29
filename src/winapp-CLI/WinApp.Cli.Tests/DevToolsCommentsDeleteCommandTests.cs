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
/// Pins <c>comments delete</c>: a delete removes the row outright (it is NOT a quiet resolve), a delete
/// that removed nothing exits non-zero instead of reporting success, and the running app is refreshed
/// only when <c>--app</c> names one, so the marker leaves the screen without a relaunch.
/// </summary>
[TestClass]
public class DevToolsCommentsDeleteCommandTests
{
    private string _dir = string.Empty;

    [TestInitialize]
    public void Init()
    {
        _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "uic-delete-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string StorePath => Path.Combine(_dir, ".winapp", "ui-comments.json");

    private static CommentStore NewStore() => new();

    private (int Exit, string Out, RecordingPusher Pusher) Run(params string[] args)
    {
        var console = new TestConsole();
        var command = new DevToolsCommentsDeleteCommand();
        var pusher = new RecordingPusher();
        var handler = new DevToolsCommentsDeleteCommand.Handler(
            NewStore(), new NoHits(), pusher, new CommentTestTargetResolver(), new FixedCwd(_dir), console, NullLogger<DevToolsCommentsDeleteCommand>.Instance);
        var exit = handler.InvokeAsync(command.Parse(args)).GetAwaiter().GetResult();
        return (exit, console.Output, pusher);
    }

    private Comment Seed(string id = "cmt_a91f0c31", string text = "too small")
    {
        var comment = new Comment
        {
            Id = id,
            Text = text,
            Status = CommentStatus.Open,
            CreatedAt = CommentTimestamps.Now(),
            UpdatedAt = CommentTimestamps.Now(),
            Anchor = new CommentAnchor { SourceFile = "MainWindow.xaml", Identity = new CommentIdentity { Type = "Button", Name = "SaveButton" } },
        };
        NewStore().Add(StorePath, comment);
        return comment;
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Correction_CancelledMutation_PreservesStore(bool resolve)
    {
        var comment = Seed();
        var bytes = File.ReadAllBytes(StorePath);
        var pusher = new RecordingPusher();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        try
        {
            if (resolve)
            {
                var handler = new DevToolsCommentsUpdateCommand.Handler(
                    NewStore(), new NoHits(), pusher, new CommentTestTargetResolver(), new FixedCwd(_dir),
                    new TestConsole(), NullLogger<DevToolsCommentsUpdateCommand>.Instance);
                handler.InvokeAsync(new DevToolsCommentsUpdateCommand().Parse([comment.Id, "--status", "resolved"]), cancel.Token).GetAwaiter().GetResult();
            }
            else
            {
                var handler = new DevToolsCommentsDeleteCommand.Handler(
                    NewStore(), new NoHits(), pusher, new CommentTestTargetResolver(), new FixedCwd(_dir),
                    new TestConsole(), NullLogger<DevToolsCommentsDeleteCommand>.Instance);
                handler.InvokeAsync(new DevToolsCommentsDeleteCommand().Parse([comment.Id]), cancel.Token).GetAwaiter().GetResult();
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(StorePath));
        Assert.AreEqual(0, pusher.Pushes.Count);
    }

    [TestMethod]
    public void Delete_ExistingId_RemovesFromStoreAndExitsZero()
    {
        var seeded = Seed();

        var (exit, output, _) = Run(seeded.Id);

        Assert.AreEqual(0, exit);
        StringAssert.Contains(output, "Deleted");
        Assert.AreEqual(0, NewStore().Load(StorePath).Comments.Count);
    }

    [TestMethod]
    public void Delete_ExistingId_Json_EchoesTheRemovedComment()
    {
        var seeded = Seed(text: "this should never have been here");

        var (exit, output, _) = Run(seeded.Id, "--json");

        Assert.AreEqual(0, exit);
        var payload = JsonSerializer.Deserialize(output, CommentsJsonContext.Default.CommentResultPayload);
        Assert.IsNotNull(payload);
        Assert.IsTrue(payload.Ok);
        Assert.AreEqual(seeded.Id, payload.Comment?.Id);
        Assert.AreEqual("this should never have been here", payload.Comment?.Text);
    }

    [TestMethod]
    public void Delete_UnknownId_ExitsNonZeroAndLeavesTheStoreAlone()
    {
        Seed();

        var (exit, output, _) = Run("cmt_definitelynotreal", "--json");

        Assert.AreEqual(1, exit, "a delete that removed nothing must not report success");
        var payload = JsonSerializer.Deserialize(output, CommentsJsonContext.Default.CommentResultPayload);
        Assert.IsNotNull(payload);
        Assert.IsFalse(payload.Ok);
        StringAssert.Contains(payload.Error ?? string.Empty, "not found");
        Assert.AreEqual(1, NewStore().Load(StorePath).Comments.Count);
    }

    [TestMethod]
    public void Delete_IsNotResolve_NothingSurvivesInTheBacklog()
    {
        var seeded = Seed();

        Run(seeded.Id);

        var doc = NewStore().Load(StorePath);
        Assert.AreEqual(0, doc.Comments.Count, "delete removes the row; resolve is the verb that keeps it");
    }

    [TestMethod]
    public void Delete_WithoutApp_DoesNotPush()
    {
        var seeded = Seed();

        var (_, _, pusher) = Run(seeded.Id);

        Assert.AreEqual(0, pusher.Pushes.Count, "deleting with the app closed is normal");
    }

    [TestMethod]
    public void Delete_WithAppPid_RefreshesTheRunningApp()
    {
        var seeded = Seed();

        var (exit, _, pusher) = Run(seeded.Id, "--app", "4321");

        Assert.AreEqual(0, exit);
        CollectionAssert.AreEqual(new List<uint> { 4321 }, pusher.Pushes, "the marker must leave the screen without a relaunch");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Delete_MarkerFailure_PreservesMutationAndReportsWarning(bool json)
    {
        var seeded = Seed();
        var console = new TestConsole();
        var handler = new DevToolsCommentsDeleteCommand.Handler(
            NewStore(), new NoHits(), new RecordingPusher { Fail = true }, new CommentTestTargetResolver(),
            new FixedCwd(_dir), console, NullLogger<DevToolsCommentsDeleteCommand>.Instance);
        var args = new List<string> { seeded.Id, "--app", "4321" };
        if (json) { args.Add("--json"); }
        Assert.AreEqual(0, handler.InvokeAsync(new DevToolsCommentsDeleteCommand().Parse(args.ToArray())).GetAwaiter().GetResult());
        Assert.AreEqual(0, NewStore().Load(StorePath).Comments.Count);
        if (json)
        {
            var payload = JsonSerializer.Deserialize(console.Output, CommentsJsonContext.Default.CommentResultPayload)!;
            Assert.IsTrue(payload.Ok);
            Assert.AreEqual(CommentsSharedOptions.MarkerRefreshWarning, payload.Warning);
        }
        else
        {
            StringAssert.Contains(console.Output, "markers could not be");
            StringAssert.Contains(console.Output, "Reattach DevTools");
        }
    }

    private sealed class RecordingPusher : ICommentPusher
    {
        public bool Fail { get; init; }
        public List<uint> Pushes { get; } = [];

        public (int Total, int Placed)? Push(uint pid, string fallbackRoot, string? sourceRootOverride = null,
            CancellationToken cancellationToken = default)
        {
            Pushes.Add(pid);
            return Fail ? null : (0, 0);
        }

        public (int Refreshed, int Failed) PushStore(string storePath, uint? skipPid, CancellationToken cancellationToken = default) => (0, 0);
    }


    private sealed class FixedCwd(string dir) : ICurrentDirectoryProvider
    {
        public string GetCurrentDirectory() => dir;

        public DirectoryInfo GetCurrentDirectoryInfo() => new(dir);
    }

    private sealed class NoHits : ICommentAnchorResolver
    {
        public IReadOnlyList<SourceHit> ReAnchor(CommentAnchor anchor, string? sourceRoot) => [];
    }
}

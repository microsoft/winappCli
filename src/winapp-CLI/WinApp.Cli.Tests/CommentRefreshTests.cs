// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Tests;

[TestClass]
public class CommentRefreshTests
{
    private string _dir = string.Empty;

    [TestInitialize]
    public void Init() => _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "uic-refresh-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static Comment NewComment(string id) => new()
    {
        Id = id,
        Text = "note",
        Status = CommentStatus.Open,
        Anchor = new CommentAnchor { SourceFile = "Main.xaml", Identity = new CommentIdentity { Type = "Button" } },
    };

    [TestMethod]
    public void EveryWrite_AdvancesTheStoreGeneration()
    {
        var store = new CommentStore();
        var path = Path.Combine(_dir, "ui-comments.json");
        store.Add(path, NewComment("a"));
        var afterAdd = store.Load(path).Generation;
        store.Update(path, "a", c => c.Text = "changed");
        var afterUpdate = store.Load(path).Generation;
        store.Delete(path, "a");
        Assert.IsTrue(afterAdd > 0 && afterUpdate > afterAdd && store.Load(path).Generation > afterUpdate);
    }

    [TestMethod]
    public void PushStore_OnlyTargetsOtherAppsUsingTheSameStore()
    {
        var store = new CommentStore();
        var project = Directory.CreateDirectory(Path.Combine(_dir, "app")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(_dir, "other", ".winapp")).Parent!.FullName;
        var storePath = store.GetStorePath(new DirectoryInfo(project));
        Assert.AreNotEqual(storePath, store.GetStorePath(new DirectoryInfo(other)));
        var asked = new List<uint>();
        var pusher = new CommentPusher(store)
        {
            ListTaps = () => [11, 12, 13, 14],
            ReadSourceRoot = (pid, _) =>
            {
                asked.Add(pid);
                return pid switch
                {
                    11 => project,
                    12 => other,
                    13 => throw new IOException("the app exited"),
                    _ => project,
                };
            },
        };

        var (refreshed, failed) = pusher.PushStore(storePath, skipPid: 14);

        CollectionAssert.AreEqual(new uint[] { 11, 12, 13 }, asked, "The app already refreshed by --app is not asked again.");
        // Only 11 shares the store. No tap listens on that fake PID, so it counts as a view that could not refresh.
        Assert.AreEqual((0, 1), (refreshed, failed));
    }

    [TestMethod]
    public async Task GuestEndpoint_PushesHostSnapshotsToTheAppWithoutTreatingThemAsAcknowledgements()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pair = DuplexStreamPair.Create();
        using var host = pair.Client;
        using var guest = pair.Server;
        var process = new GuestProcessStart(Environment.ProcessId, DateTime.UtcNow.Ticks);
        var session = new GuestCommentSession(Guid.NewGuid().ToString("N"), "sandbox-bound", "epoch", process);
        var pushed = new TaskCompletionSource<GuestCommentReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serve = new GuestCommentEndpoint(session).RunAsync(guest, guest, _ => { }, timeout.Token,
            isAppAlive: () => true, pushToApp: snapshot => pushed.TrySetResult(snapshot));
        _ = await GuestCommentFrames.ReadAsync(host, timeout.Token);

        await GuestCommentFrames.WriteAsync(host, new GuestCommentReply(GuestCommentReply.RefreshOperationId,
            Comments: [NewComment("host-edit")], Generation: 7), GuestCommentsJsonContext.Default.GuestCommentReply, timeout.Token);
        var snapshot = await pushed.Task.WaitAsync(timeout.Token);
        Assert.AreEqual(7, snapshot.Generation);
        Assert.AreEqual("host-edit", snapshot.Comments!.Single().Id);

        // A later guest write still receives its own acknowledgement.
        var requestId = Guid.NewGuid().ToString("N");
        var reading = GuestCommentClient.ExchangeAsync(process, hello => new("read",
            new(hello.BindingId, hello.TargetId, hello.Epoch, hello.Process, requestId, "", "", null)), timeout.Token);
        _ = await GuestCommentFrames.ReadAsync(host, timeout.Token);
        await GuestCommentFrames.WriteAsync(host, new GuestCommentReply(requestId, Comments: []),
            GuestCommentsJsonContext.Default.GuestCommentReply, timeout.Token);
        Assert.AreEqual(requestId, (await reading).OperationId);

        await timeout.CancelAsync();
        try { await serve; } catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
    }
}

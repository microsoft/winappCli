// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Tests;

[TestClass]
public class GuestCommentOwnerTests
{
    private string _root = null!;
    private readonly CommentStore _store = new();
    private GuestCommentBinding _binding = null!;
    private GuestCommentOwner _owner = null!;
    private const string GuestRoot = @"C:\guest\source";

    [TestInitialize]
    public void Setup()
    {
        _root = TestPaths.TempRoot(nameof(GuestCommentOwnerTests));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "App.csproj"), "<Project/>");
        File.WriteAllText(Path.Combine(_root, "Main.xaml"), "<Page/>");
        _binding = Bind("epoch", 123, 1000);
        _owner = new(_binding, _store);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    private GuestCommentBinding Bind(string epoch, int pid, long start) => new(
        new ExecutionTargetRef("sandbox", "default"), new ExecutionTargetEpoch(epoch),
        new(pid, start), new FileInfo(Path.Combine(_root, "App.csproj")), ["Main.xaml"], GuestRoot);

    private GuestCommentRequest Request(string text = "  first\nsecond\n", string revision = "") => new(
        _binding.Id, _binding.TargetId, _binding.Epoch, _binding.Process, Guid.NewGuid().ToString("N"),
        "cmt_one", revision, new Comment
        {
            Id = "cmt_one", Text = text, ProjectRoot = @"C:\guest-chosen-host-destination",
            CreatedAt = "2026-09-17T00:00:00Z", UpdatedAt = "2026-09-17T00:00:00Z",
            Anchor = new() { SourceFile = "Main.xaml", SourceUri = @"C:\wrong\secret.xaml" },
        });

    [TestMethod]
    public void Save_PersistsExactTextOnlyInHostBoundStore()
    {
        var request = Request();
        var saved = _owner.Apply(request).Current!;
        Assert.AreEqual(request.Replacement!.Text, saved.Text);
        Assert.AreEqual(_root, saved.ProjectRoot);
        Assert.AreEqual("ms-appx:///Main.xaml", saved.Anchor.SourceUri);
        Assert.AreEqual(saved.Text, _store.Get(_binding.StorePath, saved.Id)!.Text);
        Assert.AreEqual(@"C:\guest-chosen-host-destination", request.Replacement.ProjectRoot);
    }

    [TestMethod]
    [DataRow("valid")]
    [DataRow("foreign-project")]
    [DataRow("foreign-file")]
    public void AuthoredIdentityUsesOnlyTheBoundGuestProjectAndCanonicalHostSource(string kind)
    {
        var request = Request();
        var declaration = CommentAuthoredIdentity.Read(Path.Combine(_root, "Main.xaml"), _root).Single();
        var authored = CommentAuthoredIdentity.Capture(GuestRoot, "Main.xaml", declaration, declaration.Text, true);
        request.Replacement!.Anchor.Authored = authored;
        request.Replacement.Anchor.Identity.Type = "Page";
        if (kind == "foreign-project") { authored.ProjectRoot = _root; }
        if (kind == "foreign-file") { authored.SourceFile = "Foreign.xaml"; }
        if (kind != "valid")
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => _owner.Apply(request));
            Assert.IsFalse(File.Exists(_binding.StorePath));
            return;
        }
        var saved = _owner.Apply(request).Current!;
        Assert.AreEqual(_root, saved.Anchor.Authored!.ProjectRoot);
        Assert.AreEqual("Main.xaml", saved.Anchor.Authored.SourceFile);
        Assert.AreEqual(GuestRoot, authored.ProjectRoot, "Host ownership must not mutate the guest request.");
        Assert.IsTrue(CommentViewBuilder.ToView(saved, new CommentAnchorResolver(), _root).AnchorConfirmed);
    }

    [TestMethod]
    public void LostAcknowledgement_AfterHostEdit_RecognizesCommittedOperationWithoutOverwriting()
    {
        var request = Request();
        var committed = _owner.Apply(request); // Commit succeeded, caller never received acknowledgement.
        var saved = committed.Current!;
        _store.Update(_binding.StorePath, saved.Id, c =>
        {
            c.Text = "host changed it";
            c.Status = CommentStatus.Resolved;
            c.Resolution = new() { Note = "done" };
        });
        var restartedOwner = new GuestCommentOwner(_binding, new CommentStore());
        var replay = restartedOwner.Apply(request);
        Assert.AreEqual(committed.PersistedRevision, replay.PersistedRevision);
        Assert.AreEqual("host changed it", replay.Current!.Text);
        var current = _store.Get(_binding.StorePath, saved.Id)!;
        Assert.AreEqual("host changed it", current.Text);
        Assert.AreEqual(CommentStatus.Resolved, current.Status);
        Assert.AreEqual("done", current.Resolution!.Note);
    }

    [TestMethod]
    public void ConcurrentHostEdit_RefusesStaleGuestRevision()
    {
        var saved = _owner.Apply(Request()).Current!;
        var revision = CommentStore.Revision(saved);
        _store.Update(_binding.StorePath, saved.Id, c => c.Text = "host edit");
        Assert.ThrowsExactly<InvalidOperationException>(() => _owner.Apply(Request("guest edit", revision)));
        Assert.AreEqual("host edit", _store.Get(_binding.StorePath, saved.Id)!.Text);
    }

    [TestMethod]
    public void CompetingOwners_SerializeAndRejectLostUpdate()
    {
        var saved = _owner.Apply(Request()).Current!;
        var revision = CommentStore.Revision(saved);
        new GuestCommentOwner(_binding, new CommentStore()).Apply(Request("first", revision));
        Assert.ThrowsExactly<InvalidOperationException>(() => _owner.Apply(Request("second", revision)));
        Assert.AreEqual("first", _store.Get(_binding.StorePath, saved.Id)!.Text);
    }

    [TestMethod]
    public void ExactNoOp_DoesNotChangeUpdatedAt()
    {
        var saved = _owner.Apply(Request()).Current!;
        _store.Update(_binding.StorePath, saved.Id, c => c.CreatedAt = "2020-01-01T00:00:00Z");
        var current = _store.Get(_binding.StorePath, saved.Id)!;
        var request = Request(current.Text, CommentStore.Revision(current));
        var unchanged = _owner.Apply(request).Current!;
        Assert.AreEqual(current.UpdatedAt, unchanged.UpdatedAt);
        Assert.AreEqual(current.CreatedAt, unchanged.CreatedAt);
    }

    [TestMethod]
    public void AppAndSandboxRestart_PreserveHostStoreButRejectPreviousLaunchRequests()
    {
        var request = Request();
        _owner.Apply(request);
        foreach (var next in new[] { Bind("epoch", 123, 2000), Bind("new-epoch", 123, 1000) })
        {
            var nextOwner = new GuestCommentOwner(next, _store);
            Assert.AreEqual(request.Replacement!.Text, nextOwner.Read().Single().Text);
            Assert.ThrowsExactly<InvalidOperationException>(() => nextOwner.Apply(request));
        }
    }

    [TestMethod]
    [DataRow("binding")]
    [DataRow("target")]
    [DataRow("epoch")]
    [DataRow("pid")]
    [DataRow("start")]
    public void ForeignLifetimeRequest_RefusedBeforeStoreCreation(string field)
    {
        var request = Request();
        request = field switch
        {
            "binding" => request with { BindingId = Guid.NewGuid().ToString("N") },
            "target" => request with { TargetId = "foreign" },
            "epoch" => request with { Epoch = "old" },
            "pid" => request with { Process = new(456, 1000) },
            _ => request with { Process = new(123, 2000) },
        };
        Assert.ThrowsExactly<InvalidOperationException>(() => _owner.Apply(request));
        Assert.IsFalse(File.Exists(_binding.StorePath));
    }

    [TestMethod]
    [DataRow(@"..\outside.xaml")]
    [DataRow(@"C:\outside.xaml")]
    [DataRow(@".winapp\comments.xaml")]
    [DataRow(@"bin\Main.xaml")]
    [DataRow(@"Unknown.xaml")]
    [DataRow(@"Main.xaml:stream")]
    public void UnmappedSource_RefusedBeforeAnyPersistence(string source)
    {
        var request = Request();
        request.Replacement!.Anchor.SourceFile = source;
        Assert.ThrowsExactly<InvalidOperationException>(() => _owner.Apply(request));
        Assert.IsFalse(File.Exists(_binding.StorePath));
    }

    [TestMethod]
    public void SourceRemovedAfterBinding_RefusedAtPersistTime()
    {
        File.Delete(Path.Combine(_root, "Main.xaml"));
        Assert.ThrowsExactly<InvalidOperationException>(() => _owner.Apply(Request()));
        Assert.IsFalse(File.Exists(_binding.StorePath));
    }

    [TestMethod]
    public void DeleteAfterProjectRemoved_RefusesWithoutTouchingPersistedRow()
    {
        var saved = _owner.Apply(Request()).Current!;
        var delete = Request(revision: CommentStore.Revision(saved)) with { Replacement = null };
        File.Delete(Path.Combine(_root, "App.csproj"));
        Assert.ThrowsExactly<InvalidOperationException>(() => _owner.Apply(delete));
        Assert.IsNotNull(_store.Get(_binding.StorePath, saved.Id));
    }

    [TestMethod]
    public void UncommittedRequestRetry_AppliesOnceAfterEarlierRefusal()
    {
        var request = Request();
        File.Delete(Path.Combine(_root, "Main.xaml"));
        Assert.ThrowsExactly<InvalidOperationException>(() => _owner.Apply(request));
        Assert.IsFalse(File.Exists(_binding.StorePath));
        File.WriteAllText(Path.Combine(_root, "Main.xaml"), "<Page/>");
        var first = _owner.Apply(request);
        var retry = _owner.Apply(request);
        Assert.AreEqual(first.PersistedRevision, retry.PersistedRevision);
        Assert.HasCount(1, _owner.Read());
    }

    [TestMethod]
    public void UnknownDelete_IsNotReportedAsSuccessfulPersistence()
    {
        var request = Request() with { Replacement = null };
        Assert.ThrowsExactly<KeyNotFoundException>(() => _owner.Apply(request));
        Assert.IsFalse(File.Exists(_binding.StorePath));
    }

    [TestMethod]
    public void OperationIdReuseWithDifferentPayload_Refused()
    {
        var request = Request();
        _owner.Apply(request);
        request.Replacement!.Text = "changed";
        Assert.ThrowsExactly<InvalidOperationException>(() => _owner.Apply(request));
        Assert.AreEqual("  first\nsecond\n", _owner.Read().Single().Text);
    }

    [TestMethod]
    public void CrossProjectIdCollision_RefusesOverwrite()
    {
        var request = Request();
        var other = request.Replacement!;
        other.ProjectRoot = Path.Combine(_root, "other-project");
        _store.Add(_binding.StorePath, other);
        Assert.ThrowsExactly<InvalidOperationException>(() => _owner.Apply(request));
        Assert.AreEqual(other.ProjectRoot, _store.Get(_binding.StorePath, other.Id)!.ProjectRoot);
    }

    [TestMethod]
    public void Replay_DoesNotExposeForeignProjectRowReusingDeletedId()
    {
        var request = Request();
        var saved = _owner.Apply(request);
        _store.Delete(_binding.StorePath, request.Id);
        var foreign = request.Replacement!;
        foreign.ProjectRoot = Path.Combine(_root, "other-project");
        _store.Add(_binding.StorePath, foreign);
        var replay = _owner.Apply(request);
        Assert.AreEqual(saved.PersistedRevision, replay.PersistedRevision);
        Assert.IsNull(replay.Current);
    }

    [TestMethod]
    public void ResolveAndDelete_PersistWithRevisionAndReplayProtection()
    {
        var saved = _owner.Apply(Request()).Current!;
        var resolve = Request(saved.Text, CommentStore.Revision(saved));
        resolve.Replacement!.Status = CommentStatus.Resolved;
        resolve.Replacement.Resolution = new() { Note = "fixed\nexact" };
        var resolved = _owner.Apply(resolve).Current!;
        Assert.AreEqual("fixed\nexact", _owner.Read().Single().Resolution!.Note);
        var delete = resolve with
        {
            OperationId = Guid.NewGuid().ToString("N"), ExpectedRevision = CommentStore.Revision(resolved), Replacement = null,
        };
        Assert.IsNull(_owner.Apply(delete).PersistedRevision);
        Assert.IsEmpty(_owner.Read());
        _owner.Apply(Request("new row with same id"));
        Assert.IsNull(_owner.Apply(delete).PersistedRevision);
        Assert.AreEqual("new row with same id", _owner.Read().Single().Text);
    }
}

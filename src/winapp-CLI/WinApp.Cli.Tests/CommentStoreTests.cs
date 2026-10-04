// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Tests;

/// <summary>
/// Pins the persisted UI-comments store: atomic (temp-then-rename) writes, fail-closed load (corrupt store
/// throws + is preserved, never silently emptied or overwritten), in-place update, and path resolution under
/// the nearest <c>.winapp</c>.
/// </summary>
[TestClass]
public class CommentStoreTests
{
    private string _dir = string.Empty;

    [TestInitialize]
    public void Init() => _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "uic-store-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static CommentStore NewStore() => new();

    private string StorePath => Path.Combine(_dir, "ui-comments.json");

    [TestMethod]
    [DataRow("""{"version":1,"Comments":[{"id":"cmt_keep","text":"recoverable"}]}""")]
    [DataRow("""{"comments":[]}""")]
    [DataRow("""{"version":1,"comments":[{"text":"note","status":"open","anchor":{"identity":{}}}]}""")]
    [DataRow("""{"version":1,"comments":[{"id":"","text":"note","status":"open","anchor":{"identity":{}}}]}""")]
    [DataRow("""{"version":1,"comments":[{"id":"x","text":"note","status":"bad","anchor":{"identity":{}}}]}""")]
    [DataRow("""{"version":1,"comments":[{"id":"x","text":"note","status":"open","anchor":{} }]}""")]
    [DataRow("""{"version":1,"comments":[{"id":"x","text":"note","status":"open","anchor":{"identity":null}}]}""")]
    [DataRow("""{"version":1,"comments":[{"id":"x","text":"note","status":"open","anchor":{"identity":{}}},{"id":"x","text":"note","status":"open","anchor":{"identity":{}}}]}""")]
    public void Correction_InvalidConsumedStoreShape_PreservesBytes(string json)
    {
        File.WriteAllText(StorePath, json);
        var original = File.ReadAllBytes(StorePath);
        Assert.Throws<CommentStoreCorruptException>(() => NewStore().Add(StorePath, NewComment()));
        Assert.Throws<CommentStoreCorruptException>(() => NewStore().AddOrReplace(StorePath, NewComment(), out _));
        Assert.Throws<CommentStoreCorruptException>(() => NewStore().Delete(StorePath, "x"));
        Assert.Throws<CommentStoreCorruptException>(() => NewStore().Update(StorePath, "x", c => c.Text = "changed"));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(StorePath));
    }

    [TestMethod]
    public void Correction_UpsertSameProject_WithNormalizedPath_Replaces()
    {
        var store = NewStore();
        var old = NewComment();
        old.ProjectRoot = _dir;
        store.Add(StorePath, old);
        var incoming = NewComment("updated");
        incoming.Id = old.Id;
        incoming.ProjectRoot = _dir.ToUpperInvariant() + Path.DirectorySeparatorChar;
        store.AddOrReplace(StorePath, incoming, out var replaced);
        Assert.IsTrue(replaced);
        Assert.AreEqual("updated", store.Load(StorePath).Comments.Single().Text);
    }

    [TestMethod]
    public void Correction_UpsertCannotReplaceAnotherProject()
    {
        var store = NewStore();
        var old = NewComment();
        old.ProjectRoot = Path.Combine(_dir, "AppA");
        store.Add(StorePath, old);
        var bytes = File.ReadAllBytes(StorePath);
        var incoming = NewComment();
        incoming.Id = old.Id;
        incoming.ProjectRoot = Path.Combine(_dir, "AppB");
        Assert.Throws<InvalidOperationException>(() => store.AddOrReplace(StorePath, incoming, out _));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(StorePath));
    }

    private static Comment NewComment(string text = "note") => new()
    {
        Id = "cmt_" + Guid.NewGuid().ToString("N")[..8],
        Text = text,
        Status = CommentStatus.Open,
        CreatedAt = CommentTimestamps.Now(),
        UpdatedAt = CommentTimestamps.Now(),
        Anchor = new CommentAnchor { SourceFile = "MainWindow.xaml", Identity = new CommentIdentity { Type = "Button", Name = "SaveButton" } },
    };

    [TestMethod]
    public void Load_MissingFile_ReturnsEmptyDocument()
    {
        var doc = NewStore().Load(StorePath);
        Assert.AreEqual(1, doc.Version);
        Assert.AreEqual(0, doc.Comments.Count);
    }

    [TestMethod]
    public void Add_ThenLoad_RoundTrips()
    {
        var store = NewStore();
        var c = NewComment("too small");
        store.Add(StorePath, c);

        var reloaded = store.Load(StorePath);
        Assert.AreEqual(1, reloaded.Comments.Count);
        Assert.AreEqual("too small", reloaded.Comments[0].Text);
        Assert.AreEqual("SaveButton", reloaded.Comments[0].Anchor.Identity.Name);
    }

    [TestMethod]
    public void Add_WritesAtomically_NoTempLeftBehind()
    {
        var store = NewStore();
        store.Add(StorePath, NewComment());
        Assert.IsTrue(File.Exists(StorePath));
        var temps = Directory.GetFiles(_dir, "ui-comments.json.tmp-*");
        Assert.AreEqual(0, temps.Length, "temp file should be renamed away");
    }

    [TestMethod]
    public void Update_ExistingId_MutatesAndBumpsUpdatedAt()
    {
        var store = NewStore();
        var c = NewComment();
        c.UpdatedAt = "2000-01-01T00:00:00Z";
        store.Add(StorePath, c);

        var updated = store.Update(StorePath, c.Id, x =>
        {
            x.Status = CommentStatus.Resolved;
            x.Resolution = new CommentResolution { Note = "fixed" };
        });

        Assert.IsNotNull(updated);
        Assert.AreEqual(CommentStatus.Resolved, updated!.Status);
        Assert.AreNotEqual("2000-01-01T00:00:00Z", updated.UpdatedAt);
        Assert.AreEqual("fixed", store.Load(StorePath).Comments[0].Resolution?.Note);
    }

    [TestMethod]
    public void Update_MissingId_ReturnsNull()
    {
        var store = NewStore();
        store.Add(StorePath, NewComment());
        Assert.IsNull(store.Update(StorePath, "cmt_nope", _ => { }));
    }

    [TestMethod]
    public void Load_CorruptFile_ThrowsRatherThanSilentlyEmpty()
    {
        File.WriteAllText(StorePath, "{ not json ][");
        Assert.ThrowsExactly<CommentStoreCorruptException>(() => NewStore().Load(StorePath));
    }

    [TestMethod]
    public void Add_OntoCorruptStore_FailsClosed_PreservesFileAndBacksUp()
    {
        // A truncated store containing recoverable content must NOT be overwritten by a mutation.
        const string corrupt = "{ \"version\":1, \"comments\":[ {\"id\":\"precious_1\"";
        File.WriteAllText(StorePath, corrupt);

        var store = NewStore();
        Assert.ThrowsExactly<CommentStoreCorruptException>(() => store.Add(StorePath, NewComment("new one")));

        // Original left byte-for-byte intact (recoverable), plus a .bak recovery copy exists.
        Assert.AreEqual(corrupt, File.ReadAllText(StorePath), "corrupt store must be left unchanged");
        Assert.IsTrue(File.Exists(StorePath + ".bak"), "a recovery copy should be written");
        Assert.AreEqual(corrupt, File.ReadAllText(StorePath + ".bak"));
    }

    [TestMethod]
    public void Delete_ExistingId_RemovesRowAndReturnsIt()
    {
        var store = NewStore();
        var keep = NewComment("keep me");
        var doomed = NewComment("delete me");
        store.Add(StorePath, keep);
        store.Add(StorePath, doomed);

        var removed = store.Delete(StorePath, doomed.Id);

        Assert.IsNotNull(removed);
        Assert.AreEqual("delete me", removed!.Text, "the removed row is echoed so a caller can see what it destroyed");
        var reloaded = store.Load(StorePath);
        Assert.AreEqual(1, reloaded.Comments.Count);
        Assert.AreEqual(keep.Id, reloaded.Comments[0].Id, "only the addressed comment goes");
    }

    [TestMethod]
    public void Delete_MissingId_ReturnsNullAndLeavesStoreIntact()
    {
        var store = NewStore();
        var c = NewComment();
        store.Add(StorePath, c);

        Assert.IsNull(store.Delete(StorePath, "cmt_nope"), "a delete that removed nothing must not report success");
        Assert.AreEqual(1, store.Load(StorePath).Comments.Count);
    }

    [TestMethod]
    public void Delete_IsNotResolve_RowDoesNotSurviveAsResolved()
    {
        // The two intents are distinct: resolve keeps the row (with its resolution history), delete leaves
        // nothing behind. A delete that quietly resolved would keep the comment in an agent's backlog.
        var store = NewStore();
        var c = NewComment();
        store.Add(StorePath, c);

        store.Delete(StorePath, c.Id);

        Assert.AreEqual(0, store.Load(StorePath).Comments.Count);
        Assert.IsNull(store.Get(StorePath, c.Id));
    }

    [TestMethod]
    public void Delete_OntoCorruptStore_FailsClosed_PreservesFile()
    {
        const string corrupt = "{ \"version\":1, \"comments\":[ {\"id\":\"precious_1\"";
        File.WriteAllText(StorePath, corrupt);

        var store = NewStore();
        Assert.ThrowsExactly<CommentStoreCorruptException>(() => store.Delete(StorePath, "precious_1"));
        Assert.AreEqual(corrupt, File.ReadAllText(StorePath), "a delete must not overwrite a corrupt store either");
    }

    /// <summary>
    /// with no enclosing git working tree the start directory IS the root, and the store sits under its
    /// <c>.winapp</c>. The repo-root case is covered in <see cref="CommentStoreLocatorTests"/>.
    /// </summary>
    [TestMethod]
    public void GetStorePath_NoGitWorkingTree_FallsBackToTheStartDirectory()
    {
        var path = NewStore().GetStorePath(new DirectoryInfo(_dir));
        Assert.AreEqual(Path.Combine(_dir, ".winapp", "ui-comments.json"), path);
    }

    /// <summary>
    /// writing the store is how <c>.winapp</c> first appears in a repo winapp never scaffolded, so the
    /// folder must ignore itself or it shows up untracked in the user's <c>git status</c>.
    /// </summary>
    [TestMethod]
    public void Add_MakesTheStoreFolderIgnoreItself()
    {
        var nested = Path.Combine(_dir, "sub", ".winapp");
        var storePath = Path.Combine(nested, "ui-comments.json");

        NewStore().Add(storePath, NewComment());

        var selfIgnore = Path.Combine(nested, ".gitignore");
        Assert.IsTrue(File.Exists(selfIgnore), ".winapp should ignore itself after the store is written.");
        Assert.Contains("*", File.ReadAllText(selfIgnore).Split('\n').Select(l => l.Trim()).ToList());
    }

    [TestMethod]
    public void Add_Twice_DoesNotDuplicateTheSelfIgnore()
    {
        var store = NewStore();
        var local = Path.Combine(_dir, ".winapp");
        var path = Path.Combine(local, "ui-comments.json");
        store.Add(path, NewComment());
        var afterFirst = File.ReadAllText(Path.Combine(local, ".gitignore"));

        store.Add(path, NewComment());

        Assert.AreEqual(afterFirst, File.ReadAllText(Path.Combine(local, ".gitignore")));
    }

    [TestMethod]
    public void Add_PreservesExistingLocalAndRootIgnoreFiles()
    {
        var local = Directory.CreateDirectory(Path.Combine(_dir, ".winapp")).FullName;
        var rootIgnore = Path.Combine(_dir, ".gitignore");
        var localIgnore = Path.Combine(local, ".gitignore");
        File.WriteAllText(rootIgnore, "root-owned\n");
        File.WriteAllText(localIgnore, "developer-owned\n");
        NewStore().Add(Path.Combine(local, "ui-comments.json"), NewComment());
        Assert.AreEqual("root-owned\n", File.ReadAllText(rootIgnore));
        Assert.AreEqual("developer-owned\n", File.ReadAllText(localIgnore));
    }

    [TestMethod]
    public void Add_OutsideWinappFolder_DoesNotWriteAnIgnore()
    {
        NewStore().Add(StorePath, NewComment());
        Assert.IsFalse(File.Exists(Path.Combine(_dir, ".gitignore")));
    }

    [TestMethod]
    public void Add_IgnoreCreationFailure_LeavesExistingStoreUnchanged()
    {
        var local = Path.Combine(_dir, ".winapp");
        var path = Path.Combine(local, "ui-comments.json");
        NewStore().Add(path, NewComment());
        var before = File.ReadAllBytes(path);
        var ignore = Path.Combine(local, ".gitignore");
        File.Delete(ignore);
        Directory.CreateDirectory(ignore);
        Assert.Throws<UnauthorizedAccessException>(() => NewStore().Add(path, NewComment("not written")));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void Add_ParallelIndependentStores_CreateOneLocalIgnore()
    {
        var local = Path.Combine(_dir, ".winapp");
        Parallel.For(0, 8, index => NewStore().Add(Path.Combine(local, $"{index}.json"), NewComment()));
        Assert.AreEqual(1, File.ReadAllLines(Path.Combine(local, ".gitignore")).Count(line => line == "*"));
        Assert.AreEqual(8, Directory.GetFiles(local, "*.json").Length);
        Assert.IsFalse(File.Exists(Path.Combine(_dir, ".gitignore")));
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("""{"version":2,"comments":[]}""")]
    [DataRow("""{"version":1,"comments":null}""")]
    [DataRow("""{"version":1,"comments":[null]}""")]
    public void Add_InvalidDocumentShape_CannotOverwriteExistingBytes(string content)
    {
        File.WriteAllText(StorePath, content);
        Assert.ThrowsExactly<CommentStoreCorruptException>(() => NewStore().Add(StorePath, NewComment()));
        Assert.AreEqual(content, File.ReadAllText(StorePath));
    }

    [TestMethod]
    public async Task Add_LockTimeout_DoesNotMutateWithoutOwningTheLock()
    {
        var initial = NewComment();
        NewStore().Add(StorePath, initial);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() => NewStore().Update(StorePath, initial.Id, _ =>
        {
            entered.Set();
            Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(20)));
        }));
        try
        {
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.ThrowsExactly<TimeoutException>(() => NewStore().Add(StorePath, NewComment("must not enter")));
            Assert.AreEqual(1, NewStore().Load(StorePath).Comments.Count);
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.AreEqual(initial.Id, NewStore().Load(StorePath).Comments.Single().Id);
    }
}

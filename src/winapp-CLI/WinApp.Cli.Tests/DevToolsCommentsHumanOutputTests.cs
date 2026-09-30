// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;
using WinApp.Cli.Services.DevTools;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Tests;

[TestClass]
public class DevToolsCommentsHumanOutputTests
{
    private string _root = string.Empty;
    private readonly CommentStore _store = new();
    private string StorePath => Path.Combine(_root, ".winapp", "ui-comments.json");
    private const string DeferralNote = "Blocked pending design approval; do not widen this button yet.";
    private const string Page = """
        <Window xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Grid>



                <Button x:Name="SaveButton" Content="Save" AutomationProperties.AutomationId="SaveAction" />
            </Grid>
        </Window>
        """;

    [TestInitialize]
    public void Init()
    {
        _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "comment-output-" + Guid.NewGuid().ToString("N"))).FullName;
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    private (int Exit, string Output) Run(string verb, params string[] args)
    {
        using var console = new TestConsole();
        console.Profile.Width = 4096;
        var resolver = new CommentAnchorResolver();
        var cwd = new FixedDirectory(_root);
        var pusher = new NoLivePusher();
        var target = new CommentTestTargetResolver();
        (Command Command, AsynchronousCommandLineAction Handler) operation = verb switch
        {
            "add" => (new DevToolsCommentsAddCommand(), new DevToolsCommentsAddCommand.Handler(
                _store, resolver, pusher, target, cwd, console,
                NullLogger<DevToolsCommentsAddCommand>.Instance)),
            "list" => (new DevToolsCommentsListCommand(), new DevToolsCommentsListCommand.Handler(
                _store, resolver, cwd, console, NullLogger<DevToolsCommentsListCommand>.Instance)),
            "get" => (new DevToolsCommentsGetCommand(), new DevToolsCommentsGetCommand.Handler(_store, resolver, cwd, console)),
            "update" => (new DevToolsCommentsUpdateCommand(), new DevToolsCommentsUpdateCommand.Handler(
                _store, resolver, pusher, target, cwd, console, NullLogger<DevToolsCommentsUpdateCommand>.Instance)),
            "delete" => (new DevToolsCommentsDeleteCommand(), new DevToolsCommentsDeleteCommand.Handler(
                _store, resolver, pusher, target, cwd, console, NullLogger<DevToolsCommentsDeleteCommand>.Instance)),
            _ => throw new ArgumentException("Unknown test verb.", nameof(verb)),
        };
        var parsed = operation.Command.Parse(args);
        Assert.AreEqual(0, parsed.Errors.Count, string.Join("; ", parsed.Errors));
        var exit = operation.Handler.InvokeAsync(parsed).GetAwaiter().GetResult();
        return (exit, console.Output);
    }

    private string Success(string verb, params string[] args)
    {
        var (exit, output) = Run(verb, args);
        Assert.AreEqual(0, exit, $"{verb}: {output}");
        return output;
    }

    [TestMethod]
    public void GetShowsHistoricalDeclarationAndRuntimeContextSeparatelyFromTheCurrentMatch()
    {
        var path = Path.Combine(_root, "MainWindow.xaml");
        File.WriteAllText(path, Page);
        var declaration = CommentAuthoredIdentity.Read(path, _root)
            .Single(item => CommentAuthoredIdentity.Name(item.Element) == "SaveButton");
        _store.Add(StorePath, new Comment
        {
            Id = "authored-note", Text = "Change the label", ProjectRoot = _root,
            Anchor = new()
            {
                SourceFile = "MainWindow.xaml", Line = declaration.Line,
                Identity = new() { Type = "Button", Name = "SaveButton", Content = "Saved 12 rows" },
                Authored = CommentAuthoredIdentity.Capture(_root, "MainWindow.xaml", declaration, declaration.Text, true),
            },
        });
        File.WriteAllText(path, "\n\n" + Page.Replace("Content=\"Save\"", "Content=\"Save changes\"", StringComparison.Ordinal));
        var human = Success("get", "authored-note");
        Assert.IsFalse(human.Contains("Created at (historical)", StringComparison.Ordinal),
            "A confirmed anchor has one location; the stale creation line would contradict it.");
        StringAssert.Contains(human, "Current match: MainWindow.xaml:8");
        StringAssert.Contains(human, "Captured declaration:");
        StringAssert.Contains(human, "Content=\"Save\"");
        StringAssert.Contains(human, "Content=\"Save changes\"");
        StringAssert.Contains(human, "Captured runtime text (context): Saved 12 rows");
        var getJson = Success("get", "authored-note", "--json");
        StringAssert.Contains(getJson, "<Button x:Name=\\\"SaveButton\\\"", "captured XAML is not \\u003C-escaped");
        var payload = JsonSerializer.Deserialize(getJson, CommentsJsonContext.Default.CommentResultPayload)!;
        Assert.IsTrue(payload.Comment!.AnchorConfirmed);
        Assert.IsFalse(payload.Comment.RequiresConfirmation);
        Assert.AreEqual(6, payload.Comment.Anchor.Line);
        Assert.AreEqual(8, payload.Comment.Hits.Single().Line);
    }

    [TestMethod]
    public void OfflineUniqueName_IsAStrongAnchorWithItsLine()
    {
        File.WriteAllText(Path.Combine(_root, "MainWindow.xaml"), Page);
        var added = JsonSerializer.Deserialize(Success("add", "--id", "cmt_named0000001", "--text", "Wider",
            "--file", "MainWindow.xaml", "--name", "SaveButton", "--json"), CommentsJsonContext.Default.CommentResultPayload)!;
        Assert.IsTrue(added.Comment!.AnchorConfirmed);
        Assert.IsFalse(added.Comment.RequiresConfirmation);
        Assert.AreEqual(6, added.Comment.Anchor.Line);
        Assert.AreEqual("strong", added.Comment.Hits.Single().Confidence);
        var human = Success("get", "cmt_named0000001");
        StringAssert.Contains(human, "MainWindow.xaml:6");
        Assert.IsFalse(human.Contains("historical", StringComparison.Ordinal), human);
    }

    [TestMethod]
    public void OfflineDuplicateName_StaysWeak()
    {
        File.WriteAllText(Path.Combine(_root, "MainWindow.xaml"), Page.Replace("</Grid>",
            "<Grid><Button x:Name=\"SaveButton\" /></Grid></Grid>", StringComparison.Ordinal));
        var added = JsonSerializer.Deserialize(Success("add", "--id", "cmt_named0000002", "--text", "Wider",
            "--file", "MainWindow.xaml", "--name", "SaveButton", "--json"), CommentsJsonContext.Default.CommentResultPayload)!;
        Assert.IsFalse(added.Comment!.AnchorConfirmed);
        Assert.IsTrue(added.Comment.RequiresConfirmation);
        Assert.IsNull(added.Comment.Anchor.Line);
    }

    [TestMethod]
    public void CommentWithoutSourceIsWeakAndSaysItIsNotLinked()
    {
        File.WriteAllText(Path.Combine(_root, "MainWindow.xaml"), Page);
        var added = JsonSerializer.Deserialize(Success("add", "--id", "cmt_unlinked00001", "--text", "Wider",
            "--type", "Button", "--content", "Save", "--json"), CommentsJsonContext.Default.CommentResultPayload)!;
        Assert.IsTrue(added.Comment!.Anchor.Weak);
        Assert.IsTrue(added.Comment.RequiresConfirmation);
        Assert.IsFalse(added.Comment.AnchorConfirmed);
        StringAssert.Contains(Success("list"), "not linked to source");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SiblingProjects_SameFileAndIdentity_RemainDistinctAndReanchorWithinCapturedRoot(bool drift)
    {
        var appA = Directory.CreateDirectory(Path.Combine(_root, "AppA")).FullName;
        var appB = Directory.CreateDirectory(Path.Combine(_root, "AppB")).FullName;
        foreach (var (project, id, text) in new[]
        {
            (appA, "cmt_111111111111", "Save should be wider."),
            (appB, "cmt_222222222222", "Save should be narrower."),
        })
        {
            File.WriteAllText(Path.Combine(project, "MainWindow.xaml"), Page);
            Success("add", "--id", id, "--text", text, "--file", "MainWindow.xaml", "--line", "6",
                "--name", "SaveButton", "--type", "Button", "--source-root", project);
        }
        if (drift)
        {
            File.WriteAllText(Path.Combine(appA, "MainWindow.xaml"), "\n\n\n" + Page);
        }

        var human = Success("list");
        StringAssert.Contains(human, $"Project root: {appA}");
        StringAssert.Contains(human, $"Project root: {appB}");
        StringAssert.Contains(human, $"MainWindow.xaml:{(drift ? 9 : 6)}");
        StringAssert.Contains(human, "MainWindow.xaml:6");

        var payload = JsonSerializer.Deserialize(Success("list", "--json"), CommentsJsonContext.Default.CommentsListPayload)!;
        Assert.AreEqual(2, payload.Comments.Count);
        for (var i = 0; i < 2; i++)
        {
            var view = payload.Comments[i];
            Assert.AreEqual(i == 0 ? appA : appB, view.ProjectRoot);
            Assert.AreEqual("MainWindow.xaml", view.Anchor.SourceFile);
            Assert.AreEqual(6, view.Anchor.Line);
            Assert.HasCount(1, view.Hits);
            Assert.AreEqual("MainWindow.xaml", view.Hits[0].File);
            Assert.AreEqual(i == 0 && drift ? 9 : 6, view.Hits[0].Line);
            Assert.IsFalse(view.AnchorConfirmed, "Offline identity flags do not provide captured authored scope.");
            Assert.IsTrue(view.RequiresConfirmation);
            Assert.IsFalse(view.Ambiguous);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void StaleList_RetainsDeferral(bool json)
    {
        Success("add", "--id", "one", "--text", "Check spacing again.");
        Success("update", "one", "--status", "stale", "--note", DeferralNote);

        var output = Success("list", json ? ["--status", "stale", "--json"] : ["--status", "stale"]);
        if (json)
        {
            var payload = JsonSerializer.Deserialize(output, CommentsJsonContext.Default.CommentsListPayload)!;
            Assert.AreEqual(CommentStatus.Stale, payload.Comments[0].Status);
            Assert.AreEqual(DeferralNote, payload.Comments[0].Resolution?.Note);
        }
        StringAssert.Contains(output, "Check spacing again.");
        StringAssert.Contains(output, "stale");
        StringAssert.Contains(output, DeferralNote);
        StringAssert.Contains(Success("get", "one"), DeferralNote);
    }

    [TestMethod]
    [DataRow("review[1]", false)]
    [DataRow("review[1]", true)]
    [DataRow("review[red]1[/]", false)]
    [DataRow("review]", false)]
    public void ExplicitId_AllOperationsRenderLiterallyAndReportCommittedSuccess(string id, bool json)
    {
        string Invoke(string verb, params string[] args)
        {
            var output = Success(verb, json ? [.. args, "--json"] : args);
            if (json)
            {
                using var payload = JsonDocument.Parse(output);
                var record = verb == "list"
                    ? payload.RootElement.GetProperty("comments")[0]
                    : payload.RootElement.GetProperty("comment");
                Assert.AreEqual(id, record.GetProperty("id").GetString());
            }
            else
            {
                StringAssert.Contains(output, id);
            }
            return output;
        }

        Invoke("add", "--id", id, "--text", "Keep literal punctuation.");
        Assert.AreEqual(id, _store.Load(StorePath).Comments.Single().Id);
        Invoke("add", "--id", id, "--text", "Updated literal punctuation.");
        Assert.AreEqual("Updated literal punctuation.", _store.Load(StorePath).Comments.Single().Text);
        Invoke("list");
        Invoke("get", id);
        Invoke("update", id, "--status", "resolved", "--note", "Verified");
        var resolved = _store.Load(StorePath).Comments.Single();
        Assert.AreEqual(CommentStatus.Resolved, resolved.Status);
        Assert.AreEqual("Verified", resolved.Resolution?.Note);
        Invoke("update", id, "--status", "stale", "--note", DeferralNote);
        Assert.AreEqual(CommentStatus.Stale, _store.Load(StorePath).Comments.Single().Status);
        Assert.AreEqual(DeferralNote, _store.Load(StorePath).Comments.Single().Resolution?.Note);
        Invoke("list", "--all");
        Invoke("get", id);
        Invoke("delete", id);
        Assert.IsEmpty(_store.Load(StorePath).Comments);
    }

    [TestMethod]
    [DataRow("get")]
    [DataRow("update")]
    [DataRow("delete")]
    public void MissingBracketedId_RendersLiteralErrorWithoutMutation(string verb)
    {
        Success("add", "--id", "keep", "--text", "unchanged");
        var bytes = File.ReadAllBytes(StorePath);
        var (exit, output) = Run(verb, verb == "update"
            ? ["review[1]", "--status", "stale"] : ["review[1]"]);
        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "review[1]");
        StringAssert.Contains(output, "not found");
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(StorePath));
    }

    [TestMethod]
    [DataRow(CommentStatus.Resolved)]
    [DataRow(CommentStatus.Stale)]
    [DataRow(CommentStatus.Dismissed)]
    public void List_DefaultsHumanOpenJsonAll_ExplicitStatusWins(string hiddenStatus)
    {
        Success("add", "--id", "open-note", "--text", "open request");
        Success("add", "--id", "hidden-note", "--text", "handled request");
        Success("update", "hidden-note", "--status", hiddenStatus, "--note", "Verified");
        var human = Success("list");
        StringAssert.Contains(human, "open-note");
        Assert.IsFalse(human.Contains("hidden-note", StringComparison.Ordinal));
        StringAssert.Contains(human, $"1 {hiddenStatus}");
        StringAssert.Contains(Success("list", "--all"), "hidden-note");
        var all = JsonSerializer.Deserialize(Success("list", "--json"), CommentsJsonContext.Default.CommentsListPayload)!;
        Assert.HasCount(2, all.Comments);
        Assert.IsTrue(all.Comments.Exists(c => c.Id == "hidden-note" && c.Status == hiddenStatus));
        var open = JsonSerializer.Deserialize(Success("list", "--status", "open", "--json"), CommentsJsonContext.Default.CommentsListPayload)!;
        Assert.AreEqual("open-note", open.Comments.Single().Id);
    }

    [TestMethod]
    public void ReopenedComment_DoesNotPresentItsOldNoteAsAResolution()
    {
        Success("add", "--id", "reopened", "--text", "tighten spacing", "--file", "MainWindow.xaml");
        Success("update", "reopened", "--status", CommentStatus.Resolved, "--note", "fixed padding");
        StringAssert.Contains(Success("list", "--all"), "Resolution note: fixed padding");
        Success("update", "reopened", "--status", CommentStatus.Open);
        var listed = Success("list");
        StringAssert.Contains(listed, "Previous status note: fixed padding");
        Assert.IsFalse(listed.Contains("Resolution note", StringComparison.Ordinal), listed);
        Assert.IsFalse(Success("get", "reopened").Contains("Resolution note", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(CommentStatus.Open)]
    [DataRow(CommentStatus.Resolved)]
    [DataRow(CommentStatus.Stale)]
    [DataRow(CommentStatus.Dismissed)]
    public void Update_ExplicitTransitionsPreserveAnchorTextAndMetadata(string initial)
    {
        Success("add", "--id", "transition", "--text", "original request", "--file", "MainWindow.xaml",
            "--line", "42", "--column", "7", "--type", "Button", "--name", "Save");
        foreach (var status in new[] { CommentStatus.Open, CommentStatus.Resolved, CommentStatus.Stale, CommentStatus.Dismissed })
        {
            Success("update", "transition", "--status", initial, "--note", "prior note", "--by", "reviewer");
            var before = _store.Get(StorePath, "transition")!;
            var anchor = JsonSerializer.Serialize(before.Anchor, CommentsJsonContext.Default.CommentAnchor);
            var output = Success("update", "transition", "--status", status, "--json");
            var after = _store.Get(StorePath, "transition")!;
            Assert.AreEqual(status, after.Status);
            Assert.AreEqual("original request", after.Text);
            Assert.AreEqual(anchor, JsonSerializer.Serialize(after.Anchor, CommentsJsonContext.Default.CommentAnchor));
            using var result = JsonDocument.Parse(output);
            Assert.AreEqual(status, result.RootElement.GetProperty("comment").GetProperty("status").GetString());
            if (status == CommentStatus.Resolved)
            {
                Assert.AreEqual(initial == CommentStatus.Resolved ? "prior note" : null, after.Resolution!.Note);
                Assert.AreEqual(Environment.UserName, after.Resolution.ResolvedBy);
            }
            else
            {
                Assert.AreEqual("prior note", after.Resolution!.Note);
                Assert.AreEqual("reviewer", after.Resolution.ResolvedBy);
                Assert.AreEqual(before.Resolution!.ResolvedAt, after.Resolution.ResolvedAt);
            }
            Success("update", "transition", "--status", status, "--note", "new note", "--by", "another");
            var metadata = _store.Get(StorePath, "transition")!.Resolution!;
            Assert.AreEqual("new note", metadata.Note);
            Assert.AreEqual("another", metadata.ResolvedBy);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Update_InvalidStatusDoesNotMutate(bool json)
    {
        Success("add", "--id", "unchanged", "--text", "not resolved");
        var before = File.ReadAllBytes(StorePath);
        var result = Run("update", json
            ? ["unchanged", "--status", "unknown", "--json"]
            : ["unchanged", "--status", "unknown"]);
        Assert.AreEqual(1, result.Exit);
        StringAssert.Contains(result.Output, "Invalid --status");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(StorePath));
    }

    [TestMethod]
    [DataRow("  first\nsecond\n", false)]
    [DataRow("  first\nsecond\n", true)]
    [DataRow("\tindented\r\nlast line\r\n", false)]
    [DataRow("\tindented\r\nlast line\r\n", true)]
    [DataRow("\n\n  leading and trailing  \n", false)]
    [DataRow("\n\n  leading and trailing  \n", true)]
    public void AddAndUpsert_PreserveExactComposerText(string text, bool existing)
    {
        const string id = "composer-exact";
        if (existing)
        {
            Success("add", "--id", id, "--text", "original");
        }
        foreach (var submitted in new[] { text, text + "  edited\n" })
        {
            var json = Success("add", "--id", id, "--text", submitted, "--json");
            using var reply = JsonDocument.Parse(json);
            Assert.AreEqual(submitted, reply.RootElement.GetProperty("comment").GetProperty("text").GetString());
            var stored = _store.Load(StorePath).Comments.Single();
            Assert.AreEqual(submitted, stored.Text,
                "The composer caches exactly its submitted text after save; persistence must agree.");
            var bytes = File.ReadAllBytes(StorePath);
            var updatedAt = stored.UpdatedAt;
            using var readBack = JsonDocument.Parse(Success("get", id, "--json"));
            Assert.AreEqual(submitted, readBack.RootElement.GetProperty("comment").GetProperty("text").GetString());
            Assert.AreEqual(updatedAt, _store.Load(StorePath).Comments.Single().UpdatedAt);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(StorePath), "Reading a saved draft must not normalize it.");
        }
    }

    [TestMethod]
    public void WhitespaceOnlyComposerText_IsRejectedBeforeAddOrUpsert()
    {
        Assert.AreEqual(1, Run("add", "--id", "blank", "--text", " \r\n\t").Exit);
        Assert.IsFalse(File.Exists(StorePath));
        Success("add", "--id", "blank", "--text", "  original\n");
        var before = File.ReadAllBytes(StorePath);
        Assert.AreEqual(1, Run("add", "--id", "blank", "--text", "\n  ").Exit);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(StorePath));
    }

    [TestMethod]
    public async Task ListHelp_AdvertisesHumanAndJsonDefaultsAndStatusFilter()
    {
        using var output = new StringWriter();
        var root = new RootCommand();
        root.Subcommands.Add(new DevToolsCommentsListCommand());
        var parse = root.Parse(["list", "--help"]);
        parse.InvocationConfiguration.Output = output;
        parse.InvocationConfiguration.Error = output;
        Assert.AreEqual(0, await parse.InvokeAsync(parse.InvocationConfiguration));
        var help = System.Text.RegularExpressions.Regex.Replace(output.ToString(), @"\s+", " ");
        StringAssert.Contains(help, "--status <status>");
        StringAssert.Contains(help, "open | resolved | stale | dismissed");
        StringAssert.Contains(help, "default: open for human output; all statuses with --json");
    }

    private sealed class FixedDirectory(string path) : ICurrentDirectoryProvider
    {
        public string GetCurrentDirectory() => path;
        public DirectoryInfo GetCurrentDirectoryInfo() => new(path);
    }

    private sealed class NoLivePusher : ICommentPusher
    {
        public (int Total, int Placed)? Push(uint pid, string fallbackRoot, string? sourceRootOverride = null,
            CancellationToken cancellationToken = default)
            => throw new AssertFailedException("Offline output tests must not contact an app.");

        public (int Refreshed, int Failed) PushStore(string storePath, uint? skipPid, CancellationToken cancellationToken = default) => (0, 0);
    }
}

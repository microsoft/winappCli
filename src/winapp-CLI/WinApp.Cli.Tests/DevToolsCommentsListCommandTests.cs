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
/// Tests for <c>comments list</c>, driven through the real command handler and the real
/// <see cref="CommentAnchorResolver"/> against real XAML on disk — a stub resolver could not reproduce either
/// defect, since both are about what the search returns.
/// </summary>
[TestClass]
public class DevToolsCommentsListCommandTests
{
    private string _root = string.Empty;

    /// <summary>The isolated project directory, bounded by its own <c>.git</c> marker.</summary>
    private string Project => _root;

    private string PageFile => Path.Combine(Project, "HomePage.xaml");

    /// <summary>Six nested <c>Grid</c>s — the shape that produced "6 candidates" for one picked element.</summary>
    private const string SixGridPage = """
<Page xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Class="Gallery.HomePage">
    <Grid>
        <Grid>
            <Grid>
                <Grid>
                    <Grid>
                        <Grid />
                    </Grid>
                </Grid>
            </Grid>
        </Grid>
    </Grid>
</Page>
""";

    [TestInitialize]
    public void Init()
    {
        _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "uic-list-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(PageFile, SixGridPage);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string StorePath => Path.Combine(Project, ".winapp", "ui-comments.json");

    /// <summary>Line 6 is the fourth <c>&lt;Grid&gt;</c> — one of six identical type-only matches.</summary>
    private const int CapturedGridLine = 6;

    private Comment Seed(string id, string status = CommentStatus.Open, int? line = CapturedGridLine, string? file = "HomePage.xaml", bool authored = true)
    {
        var comment = new Comment
        {
            Id = id,
            Text = "tighten this up",
            Status = status,
            CreatedAt = CommentTimestamps.Now(),
            UpdatedAt = CommentTimestamps.Now(),
            ProjectRoot = Project,
            Anchor = new CommentAnchor
            {
                SourceFile = file,
                Line = line,
                Identity = new CommentIdentity { Type = "Grid" },
            },
        };
        if (authored && line is > 0 && file is not null)
        {
            var declaration = CommentAuthoredIdentity.Read(PageFile, Project).Single(item => item.Line == line);
            comment.Anchor.Authored = CommentAuthoredIdentity.Capture(Project, file, declaration, declaration.Text, true);
        }
        new CommentStore().Add(StorePath, comment);
        return comment;
    }

    private (int Exit, string Out) Run(params string[] args)
    {
        var console = new TestConsole();
        var command = new DevToolsCommentsListCommand();
        var handler = new DevToolsCommentsListCommand.Handler(
            new CommentStore(),
            new CommentAnchorResolver(),
            new FixedCwd(Project),
            console,
            NullLogger<DevToolsCommentsListCommand>.Instance,
            new CommentTestTargetResolver());
        var exit = handler.InvokeAsync(command.Parse(args)).GetAwaiter().GetResult();
        return (exit, console.Output);
    }

    // Resolved comments are listed by default.

    [TestMethod]
    public void Default_HidesResolved_AndSaysSomethingWasHidden()
    {
        Seed("cmt_open01");
        Seed("cmt_done01", CommentStatus.Resolved);

        var (exit, output) = Run();

        Assert.AreEqual(0, exit);
        StringAssert.Contains(output, "cmt_open01");
        Assert.IsFalse(output.Contains("cmt_done01", StringComparison.Ordinal), "a resolved comment is finished work and must not be in the default list");

        // The total stays reachable even when some rows are hidden.
        StringAssert.Contains(output, "2 comments total");
        StringAssert.Contains(output, "1 resolved");
        StringAssert.Contains(output, "--all");
    }

    [TestMethod]
    public void All_ShowsResolved_AndDropsTheHiddenFootnote()
    {
        Seed("cmt_open01");
        Seed("cmt_done01", CommentStatus.Resolved);

        var (exit, output) = Run("--all");

        Assert.AreEqual(0, exit);
        StringAssert.Contains(output, "cmt_open01");
        StringAssert.Contains(output, "cmt_done01");
        Assert.IsFalse(output.Contains("hidden", StringComparison.Ordinal), "nothing is hidden, so nothing should claim to be");
    }

    [TestMethod]
    public void ExplicitStatus_StillWins()
    {
        Seed("cmt_open01");
        Seed("cmt_done01", CommentStatus.Resolved);

        var (exit, output) = Run("--status", "resolved");

        Assert.AreEqual(0, exit);
        StringAssert.Contains(output, "cmt_done01");
        Assert.IsFalse(output.Contains("cmt_open01", StringComparison.Ordinal));
        StringAssert.Contains(output, "(1 open hidden; use --all)", "the footer counts what the filter hid, not what it shows");
    }

    /// <summary>
    /// The machine contract is not filtered by a HUMAN default. An agent pulling the backlog wants every row
    /// with its status attached; silently dropping rows from <c>--json</c> is the same defect in a worse place.
    /// </summary>
    [TestMethod]
    public void Json_IsNotFilteredByTheOpenOnlyDefault()
    {
        Seed("cmt_open01");
        Seed("cmt_done01", CommentStatus.Resolved);

        var (exit, output) = Run("--json");

        Assert.AreEqual(0, exit);
        var payload = JsonSerializer.Deserialize(output, CommentsJsonContext.Default.CommentsListPayload);
        Assert.IsNotNull(payload);
        Assert.AreEqual(2, payload.Comments.Count, "--json must carry the resolved comment, with its status attached");
        Assert.IsTrue(payload.Comments.Exists(c => c.Id == "cmt_done01" && c.Status == CommentStatus.Resolved));
    }

    [TestMethod]
    public void Json_IsStillFilteredByAnExplicitStatus()
    {
        Seed("cmt_open01");
        Seed("cmt_done01", CommentStatus.Resolved);

        var (_, output) = Run("--json", "--status", "open");

        var payload = JsonSerializer.Deserialize(output, CommentsJsonContext.Default.CommentsListPayload);
        Assert.IsNotNull(payload);
        Assert.AreEqual(1, payload.Comments.Count);
        Assert.AreEqual("cmt_open01", payload.Comments[0].Id);
    }

    // Candidates for an element picked live.

    /// <summary>
    /// A unique declaration with captured ancestor scope confirms the selected Grid, not its historical line.
    /// </summary>
    [TestMethod]
    public void CapturedLocationStillMatches_ReportsNoCandidates()
    {
        Seed("cmt_grid01");

        var (exit, output) = Run();

        Assert.AreEqual(0, exit);
        StringAssert.Contains(output, $"HomePage.xaml:{CapturedGridLine}");
        Assert.IsFalse(output.Contains("candidates", StringComparison.Ordinal),
            "the picked element's captured location still holds, so the tool already knows the answer");
        Assert.IsFalse(output.Contains('⚠'));
    }

    [TestMethod]
    public void CapturedLocationStillMatches_CollapsesHitsAndMarksTheAnchorConfirmed()
    {
        Seed("cmt_grid01");

        var (_, output) = Run("--json");

        var payload = JsonSerializer.Deserialize(output, CommentsJsonContext.Default.CommentsListPayload);
        Assert.IsNotNull(payload);
        var view = payload.Comments[0];
        Assert.IsTrue(view.AnchorConfirmed);
        Assert.IsFalse(view.Ambiguous);
        Assert.AreEqual(0, view.Candidates.Count);
        Assert.AreEqual(1, view.Hits.Count, "a confirmed anchor is one location, not a shortlist");
        Assert.AreEqual(CapturedGridLine, view.Hits[0].Line);
    }

    /// <summary>
    /// A historical location without authored scope remains a search even when one candidate is nearby.
    /// </summary>
    [TestMethod]
    public void CapturedLocationNoLongerMatches_ReportsCandidatesAndSaysWhy()
    {
        Seed("cmt_grid01", line: 3, authored: false);

        // Push every Grid three lines down, so nothing sits on the captured line any more.
        File.WriteAllText(PageFile, "\n\n\n" + SixGridPage);

        var (exit, output) = Run();

        Assert.AreEqual(0, exit);
        StringAssert.Contains(output, "candidates");
        StringAssert.Contains(Flatten(output), Flatten("HomePage.xaml:3 is historical"));
    }

    [TestMethod]
    public void NoLocationWasEverCaptured_SaysThatRatherThanBlamingTheSource()
    {
        Seed("cmt_grid01", line: null);

        var (_, output) = Run();

        StringAssert.Contains(output, "candidates");
        StringAssert.Contains(output, "no location was captured");
    }

    // A miss is never silent.

    [TestMethod]
    public void NoStoreOnTheResolutionPath_SaysWhereItLooked()
    {
        var (exit, output) = Run();

        Assert.AreEqual(0, exit);
        StringAssert.Contains(output, "No comment store found");
        StringAssert.Contains(Flatten(output), Flatten(StorePath));
        Assert.IsFalse(output.Contains("No comments.", StringComparison.Ordinal),
            "\"No comments.\" is indistinguishable from \"your store is one directory up\" — that ambiguity is the bug");
    }

    [TestMethod]
    public void StoreExistsButIsEmpty_SaysWhichStoreItRead()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, "{\"version\":1,\"comments\":[]}");

        var (exit, output) = Run();

        Assert.AreEqual(0, exit);
        StringAssert.Contains(output, "No comments in");
        StringAssert.Contains(Flatten(output), Flatten(StorePath));
    }

    [TestMethod]
    public void Project_FiltersToOneProjectsComments()
    {
        Seed("cmt_mine01");
        var other = Seed("cmt_other1");
        new CommentStore().Update(StorePath, other.Id, c => c.ProjectRoot = Path.Combine(_root, "OtherApp"));

        var (_, output) = Run("--project", Project);

        StringAssert.Contains(output, "cmt_mine01");
        Assert.IsFalse(output.Contains("cmt_other1", StringComparison.Ordinal));
    }

    [TestMethod]
    public void StatusAndAll_Together_AreRejected()
    {
        var (exit, output) = Run("--all", "--status", "open");

        Assert.AreEqual(1, exit);
        StringAssert.Contains(output, "alternatives");
    }

    // Re-anchoring must not widen to the whole repo.

    /// <summary>
    /// Found by running it from the repo root: the store moved there, so re-anchoring under the CURRENT
    /// directory scanned every same-named .xaml in the repo and turned one element into ten candidates —
    /// and the answer differed depending on which directory you listed from. The comment's own project root
    /// is what keeps the search where the element lives.
    /// </summary>
    [TestMethod]
    public void ReAnchoring_UsesTheCommentsProjectRoot_NotTheCurrentDirectory()
    {
        // Two sibling projects with an identical page. Only one of them owns the comment.
        var mine = Directory.CreateDirectory(Path.Combine(_root, "MyApp")).FullName;
        var decoy = Directory.CreateDirectory(Path.Combine(_root, "OtherApp")).FullName;
        File.WriteAllText(Path.Combine(mine, "HomePage.xaml"), SixGridPage);
        File.WriteAllText(Path.Combine(decoy, "HomePage.xaml"), SixGridPage);

        var seeded = Seed("cmt_grid01");
        new CommentStore().Update(StorePath, seeded.Id, c =>
        {
            c.ProjectRoot = mine;
            c.Anchor.Authored!.ProjectRoot = mine;
        });

        var console = new TestConsole();
        var command = new DevToolsCommentsListCommand();
        var handler = new DevToolsCommentsListCommand.Handler(
            new CommentStore(),
            new CommentAnchorResolver(),
            new FixedCwd(_root),                 // list from ABOVE both projects, the way you list from a repo root
            console,
            NullLogger<DevToolsCommentsListCommand>.Instance,
            new CommentTestTargetResolver());
        handler.InvokeAsync(command.Parse(["--json"])).GetAwaiter().GetResult();

        var payload = JsonSerializer.Deserialize(console.Output, CommentsJsonContext.Default.CommentsListPayload);
        Assert.IsNotNull(payload);
        var view = payload.Comments[0];
        Assert.IsTrue(view.AnchorConfirmed, "the sibling project's identical file must not disturb the captured anchor");
        Assert.AreEqual(1, view.Hits.Count);
        Assert.AreEqual(CapturedGridLine, view.Hits[0].Line);
    }

    /// <summary>
    /// Console output WRAPS, and a wrap lands mid-token: asserting a path against the raw text fails on
    /// "ui-comments.\njson" while the command printed exactly the right thing. Whitespace-free is the only
    /// safe form for matching a path.
    /// </summary>
    private static string Flatten(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\s", string.Empty);

    private sealed class FixedCwd(string dir) : ICurrentDirectoryProvider
    {
        public string GetCurrentDirectory() => dir;

        public DirectoryInfo GetCurrentDirectoryInfo() => new(dir);
    }
}

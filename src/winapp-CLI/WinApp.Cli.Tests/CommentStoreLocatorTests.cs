// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Tests;

/// <summary>
/// The store lives at the REPOSITORY root, so <c>comments list</c> finds the same comments from the
/// repo root, a project directory, or a sibling — the working directory no longer decides whether your own
/// comments exist. With no enclosing git working tree the start directory is the root, which is the case where
/// running from ABOVE the project genuinely cannot see a store below it, and <c>--source-root</c> is the answer.
/// </summary>
[TestClass]
public class CommentStoreLocatorTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Init()
        => _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "uic-loc-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string Repo => Path.Combine(_root, "repo");

    private string Project => Path.Combine(Repo, "AIDevGallery");

    private string Sibling => Path.Combine(Repo, "tools");

    private void MakeRepo(bool git = true)
    {
        Directory.CreateDirectory(Project);
        Directory.CreateDirectory(Sibling);
        if (git)
        {
            Directory.CreateDirectory(Path.Combine(Repo, ".git"));
        }
    }

    /// <summary>
    /// The exact failure reported twice: <c>winapp run</c> from the repo root wrote under the project, so
    /// listing from the repo root said "No comments" until the user cd'd into the project. All three start
    /// directories must now name one file.
    /// </summary>
    [TestMethod]
    public void RepoRootProjectDirAndSibling_AllResolveToTheSameStore()
    {
        MakeRepo();

        var fromRepo = CommentStoreLocator.Resolve(Repo);
        var fromProject = CommentStoreLocator.Resolve(Project);
        var fromSibling = CommentStoreLocator.Resolve(Sibling);

        var expected = Path.Combine(Repo, ".winapp", "ui-comments.json");
        Assert.AreEqual(expected, fromRepo.StorePath);
        Assert.AreEqual(expected, fromProject.StorePath, "listing from the project directory must find the repo-root store");
        Assert.AreEqual(expected, fromSibling.StorePath, "listing from a sibling directory must find the repo-root store");
        Assert.AreEqual(CommentStoreRootKind.RepositoryRoot, fromProject.Kind);
    }

    /// <summary>A linked worktree / submodule marks <c>.git</c> as a FILE; treating that as "not a repo" would
    /// drop the store back at the project root for anyone reviewing in a worktree.</summary>
    [TestMethod]
    public void GitFile_CountsAsARepositoryRoot()
    {
        Directory.CreateDirectory(Project);
        File.WriteAllText(Path.Combine(Repo, ".git"), "gitdir: ../.git/worktrees/wt\n");

        var located = CommentStoreLocator.Resolve(Project);

        Assert.AreEqual(CommentStoreRootKind.RepositoryRoot, located.Kind);
        Assert.AreEqual(Path.Combine(Repo, ".winapp", "ui-comments.json"), located.StorePath);
    }

    [TestMethod]
    public void NoGitWorkingTree_FallsBackToTheStartDirectory()
    {
        MakeRepo(git: false);

        var located = CommentStoreLocator.Resolve(Project);

        Assert.AreEqual(CommentStoreRootKind.ProjectRootFallback, located.Kind);
        Assert.AreEqual(Path.Combine(Project, ".winapp", "ui-comments.json"), located.StorePath);
    }

    /// <summary>
    /// The trap named in the decision: with no git working tree, running from a directory ABOVE the project
    /// cannot find a store that sits below it. That is not a bug to paper over — it is why <c>--source-root</c>
    /// exists, and why the miss message has to name the path it looked at.
    /// </summary>
    [TestMethod]
    public void NoGit_FromAboveTheProject_LooksAboveAndSaysSo()
    {
        MakeRepo(git: false);

        var fromAbove = CommentStoreLocator.Resolve(Repo);
        var withSourceRoot = CommentStoreLocator.Resolve(Project);

        Assert.AreNotEqual(withSourceRoot.StorePath, fromAbove.StorePath);
        StringAssert.Contains(fromAbove.Explain(), Repo);
        StringAssert.Contains(fromAbove.Explain(), "not inside a git working tree");
        Assert.AreEqual(Path.Combine(Project, ".winapp", "ui-comments.json"), withSourceRoot.StorePath,
            "--source-root is the escape hatch for exactly this case");
    }

    [TestMethod]
    public void Explain_NamesTheRuleThatChoseThePath()
    {
        MakeRepo();

        var explanation = CommentStoreLocator.Resolve(Project).Explain();

        StringAssert.Contains(explanation, Path.Combine(Repo, ".winapp", "ui-comments.json"));
        StringAssert.Contains(explanation, "git repository root");
        StringAssert.Contains(explanation, Project);
    }
}

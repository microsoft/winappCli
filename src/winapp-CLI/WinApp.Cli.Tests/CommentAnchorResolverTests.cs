// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Tests;

/// <summary>
/// Pins the identity→source re-anchoring cascade: x:Name/AutomationId first, then content proximity, then
/// type; the stored line is advisory (drift-tolerant); build output is excluded; weak anchors scan the project.
/// </summary>
[TestClass]
public class CommentAnchorResolverTests
{
    private string _root = string.Empty;
    private readonly CommentAnchorResolver _resolver = new();

    [TestInitialize]
    public void Init() => _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "uic-anchor-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void WriteXaml(string name, string content)
    {
        var rootNameEnd = content.IndexOfAny([' ', '>', '/', '\r', '\n', '\t'], content.IndexOf('<') + 1);
        content = content.Insert(rootNameEnd, " xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"");
        File.WriteAllText(Path.Combine(_root, name), content);
    }

    [TestMethod]
    [DataRow("ordinary")]
    [DataRow("directory-link")]
    [DataRow("file-link")]
    public void Confirmation_ExactSource_RejectsRedirectedDirectoryAndLeaf(string mode)
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source"));
        File.WriteAllText(Path.Combine(source.FullName, "Page.xaml"), "<Button x:Name=\"Save\" />");
        var directoryLink = Path.Combine(_root, "directory-link");
        var fileLink = Path.Combine(_root, "file-link.xaml");
        try
        {
            var relative = @"source\Page.xaml";
            if (mode == "directory-link")
            {
                Directory.CreateSymbolicLink(directoryLink, source.FullName);
                relative = @"directory-link\Page.xaml";
            }
            else if (mode == "file-link")
            {
                File.CreateSymbolicLink(fileLink, Path.Combine(source.FullName, "Page.xaml"));
                relative = "file-link.xaml";
            }
            var anchor = new CommentAnchor { SourceUri = "ms-appx:///" + relative.Replace('\\', '/') };
            var resolved = CommentAnchorResolver.ResolveKnownSourcePath(_root, anchor);
            Assert.AreEqual(mode == "ordinary" ? Path.Combine(source.FullName, "Page.xaml") : null, resolved);
        }
        finally
        {
            if (Directory.Exists(directoryLink)) { Directory.Delete(directoryLink); }
            if (File.Exists(fileLink)) { File.Delete(fileLink); }
        }
    }

    [TestMethod]
    [DataRow("ms-appx:///Views/Page.xaml", @"Views\Page.xaml")]
    [DataRow("ms-appx:///Views/../Page.xaml", null)]
    [DataRow("ms-appx:///Views/%2e%2e/Page.xaml", null)]
    [DataRow("../Page.xaml", null)]
    [DataRow("https://example.com/Page.xaml", null)]
    [DataRow("ms-appx://other-package/Page.xaml", null)]
    public void Correction_SourceUri_IsProjectRelativeWithoutTraversal(string uri, string? expected)
        => Assert.AreEqual(expected, CommentAnchorResolver.RelativeSourcePath(uri, _root));

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LeafOnlyAnchorWithoutAuthoredScope_RequiresConfirmation(bool duplicate)
    {
        Directory.CreateDirectory(Path.Combine(_root, "A"));
        Directory.CreateDirectory(Path.Combine(_root, "B"));
        WriteXaml(@"A\Page.xaml", "<Button x:Name=\"SameName\" />");
        if (duplicate)
        {
            WriteXaml(@"B\Page.xaml", "<Button x:Name=\"SameName\" />");
        }
        var comment = new Comment
        {
            Anchor = new CommentAnchor
            {
                SourceFile = "Page.xaml",
                Line = 1,
                Identity = new CommentIdentity { Name = "SameName" },
            },
        };
        var view = CommentViewBuilder.ToView(comment, _resolver, _root);
        Assert.IsFalse(view.AnchorConfirmed);
        Assert.IsTrue(view.RequiresConfirmation);
        Assert.AreEqual(duplicate, view.Ambiguous);
    }

    [TestMethod]
    public void Correction_CappedHits_PreserveStrongFacetAmbiguity()
    {
        WriteXaml("Page.xaml", "<Grid>\n<Button x:Name=\"Same\" />\n" +
            string.Join("\n", Enumerable.Repeat("<Button AutomationId=\"Same\" />", 12)) +
            "\n<Button x:Name=\"Same\" />\n</Grid>");
        var comment = new Comment
        {
            Anchor = new CommentAnchor
            {
                SourceFile = "Page.xaml",
                Line = 1,
                Identity = new CommentIdentity { Name = "Same" },
            },
        };
        var view = CommentViewBuilder.ToView(comment, _resolver, _root);
        Assert.IsFalse(view.AnchorConfirmed);
        Assert.IsTrue(view.Ambiguous);
        Assert.AreEqual(2, view.Candidates.Count);
        Assert.AreEqual(10, view.Hits.Count);
    }

    [TestMethod]
    [DataRow("Missing/Page.xaml", null)]
    [DataRow("../Page.xaml", null)]
    [DataRow("Page.xaml", "ms-appx:///Views/../Page.xaml")]
    public void Correction_MissingOrUnsafeExactFile_CannotConfirmCoincidentLeaf(string file, string? uri)
    {
        WriteXaml("Page.xaml", "<Button x:Name=\"Same\" />");
        var comment = new Comment
        {
            Anchor = new CommentAnchor { SourceFile = file, SourceUri = uri, Line = 1, Identity = new CommentIdentity { Name = "Same" } },
        };
        Assert.IsFalse(CommentViewBuilder.ToView(comment, _resolver, _root).AnchorConfirmed);
    }

    [TestMethod]
    public void Correction_SameLeafOldLine_CannotConfirmWrongFile()
    {
        Directory.CreateDirectory(Path.Combine(_root, "ViewsA"));
        Directory.CreateDirectory(Path.Combine(_root, "ViewsB"));
        WriteXaml(@"ViewsA\Page.xaml", "<Page>\n<Grid>\n\n<Button x:Name=\"SameName\" />\n</Grid></Page>");
        WriteXaml(@"ViewsB\Page.xaml", "<Page>\n<Grid>\n<Button x:Name=\"SameName\" />\n</Grid></Page>");
        var comment = new Comment
        {
            ProjectRoot = _root,
            Anchor = new CommentAnchor
            {
                SourceFile = @"ViewsA\Page.xaml",
                SourceUri = "ms-appx:///ViewsA/Page.xaml",
                Line = 3,
                Identity = new CommentIdentity { Name = "SameName" },
            },
        };
        var view = CommentViewBuilder.ToView(comment, _resolver, _root);
        Assert.IsFalse(view.AnchorConfirmed);
        Assert.AreEqual(@"ViewsA\Page.xaml", view.Hits[0].File);
        Assert.AreEqual(4, view.Hits[0].Line);
    }

    [TestMethod]
    public void ReAnchor_MatchesXName_AndReturnsRealDriftedLine()
    {
        WriteXaml("MainWindow.xaml", "<Window>\n  <Grid>\n    <Button x:Name=\"SaveButton\" Content=\"Save\"/>\n  </Grid>\n</Window>");
        var anchor = new CommentAnchor
        {
            SourceFile = "MainWindow.xaml",
            Line = 42, // stale/drifted
            Identity = new CommentIdentity { Type = "Button", Name = "SaveButton", Content = "Save" },
        };

        var hits = _resolver.ReAnchor(anchor, _root);

        Assert.IsTrue(hits.Count >= 1);
        Assert.AreEqual("x:Name", hits[0].Via);
        Assert.AreEqual(3, hits[0].Line, "line is re-derived, not the stored 42");
        Assert.AreEqual("MainWindow.xaml", hits[0].File);
    }

    [TestMethod]
    public void ReAnchor_UnnamedElement_FallsBackToContent()
    {
        WriteXaml("Page.xaml", "<Page>\n  <TextBlock Text=\"Untitled\"/>\n</Page>");
        var anchor = new CommentAnchor
        {
            SourceFile = "Page.xaml",
            Line = 2,
            Identity = new CommentIdentity { Type = "TextBlock", Content = "Untitled" },
        };

        var hits = _resolver.ReAnchor(anchor, _root);

        Assert.IsTrue(hits.Count >= 1);
        Assert.AreEqual("content", hits[0].Via);
        Assert.AreEqual(2, hits[0].Line);
    }

    [TestMethod]
    public void ReAnchor_AutomationIdMatch_WhenDistinctFromName()
    {
        WriteXaml("A.xaml", "<Grid>\n  <Button AutomationProperties.AutomationId=\"SaveBtn\"/>\n</Grid>");
        var anchor = new CommentAnchor
        {
            SourceFile = "A.xaml",
            Identity = new CommentIdentity { Type = "Button", AutomationId = "SaveBtn" },
        };

        var hits = _resolver.ReAnchor(anchor, _root);

        Assert.IsTrue(hits.Count >= 1);
        Assert.AreEqual("AutomationId", hits[0].Via);
    }

    [TestMethod]
    public void ReAnchor_MissingName_PreservesWeakTypeSuggestion()
    {
        WriteXaml("A.xaml", "<Grid><Button x:Name=\"Other\"/></Grid>");
        var anchor = new CommentAnchor
        {
            SourceFile = "A.xaml",
            Identity = new CommentIdentity { Type = "Button", Name = "Missing" },
        };

        var hit = _resolver.ReAnchor(anchor, _root).Single();
        Assert.AreEqual("type", hit.Via);
        Assert.AreEqual("weak", hit.Confidence);
    }

    [TestMethod]
    public void ReAnchor_WeakAnchor_ScansWholeProject()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Views"));
        WriteXaml(@"Views\Deep.xaml", "<Grid>\n<Button x:Name=\"FindMe\"/>\n</Grid>");
        var anchor = new CommentAnchor
        {
            SourceFile = null, // uninstrumented / weak
            Weak = true,
            Identity = new CommentIdentity { Type = "Button", Name = "FindMe" },
        };

        var hits = _resolver.ReAnchor(anchor, _root);

        Assert.IsTrue(hits.Count >= 1);
        Assert.AreEqual(@"Views\Deep.xaml", hits[0].File);
    }

    [TestMethod]
    public void ReAnchor_ExcludesBinAndObj()
    {
        Directory.CreateDirectory(Path.Combine(_root, "obj"));
        File.WriteAllText(Path.Combine(_root, "obj", "MainWindow.g.xaml"), "<Button x:Name=\"SaveButton\"/>");
        WriteXaml("MainWindow.xaml", "<Grid>\n  <Button x:Name=\"SaveButton\"/>\n</Grid>");
        var anchor = new CommentAnchor
        {
            SourceFile = "MainWindow.xaml",
            Identity = new CommentIdentity { Type = "Button", Name = "SaveButton" },
        };

        var hits = _resolver.ReAnchor(anchor, _root);

        Assert.IsTrue(hits.Count >= 1);
        foreach (var h in hits)
        {
            Assert.AreEqual("MainWindow.xaml", h.File, "obj/ output must be excluded");
        }
    }

    [TestMethod]
    public void ReAnchor_NoSourceRoot_ReturnsEmpty()
    {
        var anchor = new CommentAnchor { Identity = new CommentIdentity { Name = "X" } };
        Assert.AreEqual(0, _resolver.ReAnchor(anchor, null).Count);
        Assert.AreEqual(0, _resolver.ReAnchor(anchor, Path.Combine(_root, "does-not-exist")).Count);
    }
}

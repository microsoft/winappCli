// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.DevTools.Comments;
using WinApp.Cli.Services.DevTools;
using WinApp.Cli.Services;
using WinApp.Cli.Commands;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using System.Text.Json;

namespace WinApp.Cli.Tests;

/// <summary>
/// Pins the generalized N-comment hand-off prompt: grouping by source file, continuous numbering across
/// groups, singular/plural phrasing, and the resolve/flag back-channel instructions.
/// </summary>
[TestClass]
public class CommentViewBuilderTests
{
    private static readonly ICommentAnchorResolver NullResolver = new NoHitsResolver();

    [TestMethod]
    public async Task Confirmation_AddThroughOwnedSourceLink_ReportsCommittedSuccess()
    {
        var root = Directory.CreateTempSubdirectory("winapp-confirmation-add-");
        var link = Path.Combine(root.FullName, "source-link");
        try
        {
            root.CreateSubdirectory(".git");
            var source = root.CreateSubdirectory("source");
            File.WriteAllText(Path.Combine(source.FullName, "Page.xaml"), "<Button xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" x:Name=\"Save\" />");
            Directory.CreateSymbolicLink(link, source.FullName);
            var store = new CommentStore();
            var console = new TestConsole();
            var handler = new DevToolsCommentsAddCommand.Handler(store, new CommentAnchorResolver(),
                new CommentPusher(store), new CommentTestTargetResolver(),
                new FixedDirectory(root.FullName), console, NullLogger<DevToolsCommentsAddCommand>.Instance);
            var exit = await handler.InvokeAsync(new DevToolsCommentsAddCommand().Parse(
                ["--text", "owned note", "--file", "Page.xaml", "--line", "1", "--name", "Save", "--source-root", link, "--json"]));
            Assert.AreEqual(1, store.Load(store.GetStorePath(new DirectoryInfo(link))).Comments.Count,
                "The add has already committed before optional source confirmation.");
            Assert.AreEqual(0, exit, console.Output);
            using var output = JsonDocument.Parse(console.Output);
            Assert.IsTrue(output.RootElement.GetProperty("ok").GetBoolean());
            Assert.IsFalse(output.RootElement.GetProperty("comment").GetProperty("anchorConfirmed").GetBoolean());
            Assert.AreEqual(0, output.RootElement.GetProperty("comment").GetProperty("hits").GetArrayLength());
        }
        finally
        {
            if (Directory.Exists(link)) { Directory.Delete(link); }
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void Confirmation_SourceRedirectedAfterHitsWereRead_IsUnconfirmedWithWarning()
    {
        var root = Directory.CreateTempSubdirectory("winapp-confirmation-race-");
        var source = root.CreateSubdirectory("source").FullName;
        var retired = Path.Combine(root.FullName, "retired");
        try
        {
            File.WriteAllText(Path.Combine(source, "Page.xaml"), "<Button xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" x:Name=\"Save\" />");
            var comment = new Comment
            {
                Anchor = new CommentAnchor { SourceFile = "Page.xaml", Line = 1, Identity = new CommentIdentity { Name = "Save" } },
            };
            var resolver = new AfterReadResolver(() =>
            {
                Directory.Move(source, retired);
                Directory.CreateSymbolicLink(source, retired);
            });
            var view = CommentViewBuilder.ToView(comment, resolver, source);
            Assert.AreEqual(1, resolver.ReadCount, "Real source hits must have existed before the path changed.");
            Assert.IsFalse(view.AnchorConfirmed);
            Assert.AreEqual(0, view.Hits.Count, "Unavailable source hits must not be presented as current confirmation.");
            Assert.IsNotNull(CommentViewBuilder.AnchorHealthWarning(view));
        }
        finally
        {
            if (Directory.Exists(retired) && Directory.Exists(source)) { Directory.Delete(source); }
            root.Delete(recursive: true);
        }
    }

    private sealed class AfterReadResolver(Action afterRead) : ICommentAnchorResolver
    {
        public int ReadCount { get; private set; }
        public IReadOnlyList<SourceHit> ReAnchor(CommentAnchor anchor, string? sourceRoot)
        {
            var hits = new CommentAnchorResolver().ReAnchor(anchor, sourceRoot);
            ReadCount = hits.Count;
            afterRead();
            return hits;
        }
    }

    private sealed class FixedDirectory(string path) : ICurrentDirectoryProvider
    {
        public string GetCurrentDirectory() => path;
        public DirectoryInfo GetCurrentDirectoryInfo() => new(path);
    }

    private static CommentView View(string id, string file, string type, string? name, string text, int? line = null)
        => new()
        {
            Id = id,
            Text = text,
            Status = CommentStatus.Open,
            Anchor = new CommentAnchor
            {
                SourceFile = file,
                Line = line,
                Identity = new CommentIdentity { Type = type, Name = name },
            },
        };

    // an anchor whose identity bundle is empty cannot be re-anchored NO MATTER WHAT THE SOURCE LOOKS
    // LIKE, so reporting it as "the element may be renamed or removed" asserts a cause that was never
    // established — while the element is on screen in front of the user. The two states must not share a
    // message.
    [TestMethod]
    public void AnchorHealth_EmptyIdentity_ReportsUnanchorable_NotRenamedOrRemoved()
    {
        var c = new CommentView
        {
            Id = "cmt_empty",
            Text = "this button does nothing",
            Anchor = new CommentAnchor { SourceFile = "HeaderTile.xaml", Line = 57, Identity = new CommentIdentity() },
        };

        Assert.IsTrue(CommentViewBuilder.IsUnanchorable(c));
        Assert.IsFalse(CommentViewBuilder.IsMaybeStale(c), "An identity-less anchor says nothing about whether the element still exists.");

        var warning = CommentViewBuilder.AnchorHealthWarning(c);
        StringAssert.Contains(warning, "cannot be re-anchored");
        Assert.IsFalse(warning!.Contains("renamed or removed", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AnchorHealth_NamedIdentityWithNoHits_IsStillMaybeStale()
    {
        var c = View("cmt_named", "MainWindow.xaml", "Button", "SaveButton", "too small", 42);

        Assert.IsTrue(CommentViewBuilder.IsMaybeStale(c), "A resolvable identity that matched nothing IS the genuine stale case.");
        Assert.IsFalse(CommentViewBuilder.IsUnanchorable(c));
        StringAssert.Contains(CommentViewBuilder.AnchorHealthWarning(c), "renamed or removed");
    }

    [TestMethod]
    public void AnchorHealth_TypeOnlyWithSourceFile_IsMaybeStale()
    {
        // Type + source file is the resolver's last-resort cascade step, so it CAN produce a hit — zero hits
        // here really is a claim about the source, not about the anchor.
        var c = View("cmt_type", "MainWindow.xaml", "Grid", null, "pad it");

        Assert.IsTrue(CommentViewBuilder.IsMaybeStale(c));
        Assert.IsFalse(CommentViewBuilder.IsUnanchorable(c));
    }

    [TestMethod]
    public void AnchorHealth_HealthyAnchorWithHits_WarnsNothing()
    {
        var c = View("cmt_ok", "MainWindow.xaml", "Button", "SaveButton", "too small", 42);
        c.Hits.Add(new SourceHit { File = "MainWindow.xaml", Line = 42, Via = "x:Name", Text = "<Button x:Name=\"SaveButton\" />" });

        Assert.IsNull(CommentViewBuilder.AnchorHealthWarning(c));
    }

    [TestMethod]
    public void BuildPayload_AttachesReanchoredHits()
    {
        var comment = new Comment
        {
            Id = "cmt_a",
            Text = "x",
            Anchor = new CommentAnchor { SourceFile = "MainWindow.xaml", Identity = new CommentIdentity { Name = "SaveButton" } },
        };

        var payload = CommentViewBuilder.BuildPayload([comment], new OneHitResolver(), "MyApp", null);

        Assert.AreEqual(1, payload.Comments.Count);
        Assert.AreEqual(1, payload.Comments[0].Hits.Count);
        Assert.AreEqual("x:Name", payload.Comments[0].Hits[0].Via);
    }

    [TestMethod]
    public void BuildPayload_MultipleHits_FlagsAmbiguity()
    {
        var comment = new Comment
        {
            Id = "cmt_amb",
            Text = "which one?",
            Anchor = new CommentAnchor { SourceFile = "MainWindow.xaml", Identity = new CommentIdentity { Type = "StackPanel" } },
        };

        var payload = CommentViewBuilder.BuildPayload([comment], new TwoHitResolver(), "MyApp", null);
        Assert.IsTrue(payload.Comments[0].Ambiguous);
        Assert.HasCount(2, payload.Comments[0].Candidates);
        Assert.AreEqual("MainWindow.xaml:15", payload.Comments[0].Candidates[0]);
        Assert.AreEqual("MainWindow.xaml:29", payload.Comments[0].Candidates[1]);
    }

    [TestMethod]
    public void BuildPayload_FreshHit_PreservesCapturedLineSeparately()
    {
        var comment = new Comment
        {
            Id = "cmt_a",
            Text = "x",
            Anchor = new CommentAnchor { SourceFile = "MainWindow.xaml", Line = 99, Identity = new CommentIdentity { Name = "SaveButton" } },
        };

        var payload = CommentViewBuilder.BuildPayload([comment], new OneHitResolver(), "MyApp", null);
        Assert.AreEqual(99, payload.Comments[0].Anchor.Line);
        Assert.AreEqual(3, payload.Comments[0].Hits[0].Line);
        Assert.IsFalse(payload.Comments[0].AnchorConfirmed);
    }

    [TestMethod]
    public void BuildPayload_CorroboratingFacets_NotFlaggedAmbiguous()
    {
        // x:Name + AutomationId on adjacent lines = the SAME element found two ways, not two candidates.
        var comment = new Comment
        {
            Id = "cmt_named",
            Text = "fine",
            Anchor = new CommentAnchor { SourceFile = "MainWindow.xaml", Identity = new CommentIdentity { Name = "CounterButton" } },
        };

        var payload = CommentViewBuilder.BuildPayload([comment], new CorroboratingResolver(), "MyApp", null);
        Assert.IsFalse(payload.Comments[0].Ambiguous);
    }

    private sealed class NoHitsResolver : ICommentAnchorResolver
    {
        public IReadOnlyList<SourceHit> ReAnchor(CommentAnchor anchor, string? sourceRoot) => [];
    }

    private sealed class OneHitResolver : ICommentAnchorResolver
    {
        public IReadOnlyList<SourceHit> ReAnchor(CommentAnchor anchor, string? sourceRoot)
            => [new SourceHit { File = "MainWindow.xaml", Line = 3, Via = "x:Name", Text = "<Button x:Name=\"SaveButton\"/>" }];
    }

    private sealed class TwoHitResolver : ICommentAnchorResolver
    {
        public IReadOnlyList<SourceHit> ReAnchor(CommentAnchor anchor, string? sourceRoot) =>
        [
            new SourceHit { File = "MainWindow.xaml", Line = 15, Via = "type", Text = "<StackPanel>" },
            new SourceHit { File = "MainWindow.xaml", Line = 29, Via = "type", Text = "<StackPanel>" },
        ];
    }

    private sealed class CorroboratingResolver : ICommentAnchorResolver
    {
        public IReadOnlyList<SourceHit> ReAnchor(CommentAnchor anchor, string? sourceRoot) =>
        [
            new SourceHit { File = "MainWindow.xaml", Line = 31, Via = "x:Name", Text = "x:Name=\"CounterButton\"" },
            new SourceHit { File = "MainWindow.xaml", Line = 32, Via = "AutomationId", Text = "AutomationProperties.AutomationId=\"CounterButton\"" },
        ];
    }
}

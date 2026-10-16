// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Tests;

/// <summary>
/// Sandbox launches of Microsoft.UI.Reactor projects copy their C# sources, so a comment left in the guest anchors to
/// a call site and is written to the host store under the host project.
/// </summary>
[TestClass]
public class GuestReactorCommentTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void Init() => _root = Directory.CreateTempSubdirectory("guest-reactor-");

    [TestCleanup]
    public void Cleanup()
    {
        try { _root.Delete(recursive: true); } catch { /* best effort */ }
    }

    [TestMethod]
    public void ReactorCSharpSources_SkipBuildOutputAndHiddenFolders()
    {
        var project = new FileInfo(Path.Combine(_root.FullName, "App.csproj"));
        File.WriteAllText(project.FullName, "<Project/>");
        File.WriteAllText(Path.Combine(_root.FullName, "App.cs"), "");
        Directory.CreateDirectory(Path.Combine(_root.FullName, "Views"));
        File.WriteAllText(Path.Combine(_root.FullName, "Views", "Counter.cs"), "");
        foreach (var excluded in new[] { "obj", "bin", ".vs" })
        {
            Directory.CreateDirectory(Path.Combine(_root.FullName, excluded));
            File.WriteAllText(Path.Combine(_root.FullName, excluded, "Generated.cs"), "");
        }
        var sources = GuestSourceSnapshot.ReactorCSharpSources(project)
            .Select(path => Path.GetRelativePath(_root.FullName, path)).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(new[] { "App.cs", Path.Combine("Views", "Counter.cs") }, sources);
        Assert.AreEqual(2, GuestSourceSnapshot.ValidateSources(project, GuestSourceSnapshot.ReactorCSharpSources(project)).Count,
            "C# files pass the same snapshot limits as XAML");
    }

    [TestMethod]
    public void Binding_MapsACSharpAnchorToTheHostProject()
    {
        var (binding, guest) = Bind();
        var mapped = binding.Map(GuestComment(guest.FullName));
        Assert.AreEqual(binding.ProjectRoot, mapped.ProjectRoot);
        Assert.AreEqual("App.cs", mapped.Anchor.SourceFile);
        Assert.IsNull(mapped.Anchor.SourceUri, "a C# file has no ms-appx resource");
        Assert.AreEqual(binding.ProjectRoot, mapped.Anchor.CSharp!.ProjectRoot);
        Assert.AreEqual(10, mapped.Anchor.CSharp.Line);
    }

    [TestMethod]
    public void Binding_RefusesACallSiteFromAnotherProject()
    {
        var (binding, _) = Bind();
        var comment = GuestComment(Path.Combine(_root.FullName, "elsewhere"));
        Assert.ThrowsExactly<InvalidOperationException>(() => binding.Map(comment));
    }

    [TestMethod]
    [DataRow("App.cs", true)]
    [DataRow(@"Views\Main.xaml", true)]
    [DataRow(@"obj\App.g.cs", false)]
    [DataRow("App.csproj", false)]
    [DataRow(@"..\App.cs", false)]
    public void ValidateRelativeSource_AcceptsXamlAndCSharpOnly(string path, bool accepted)
    {
        if (accepted)
        {
            Assert.AreEqual(path, GuestCommentBinding.ValidateRelativeSource(path));
        }
        else
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => GuestCommentBinding.ValidateRelativeSource(path));
        }
    }

    private (GuestCommentBinding Binding, DirectoryInfo Guest) Bind()
    {
        var host = _root.CreateSubdirectory("host");
        var guest = _root.CreateSubdirectory("guest");
        File.WriteAllText(Path.Combine(host.FullName, "App.csproj"), "<Project/>");
        File.WriteAllText(Path.Combine(host.FullName, "App.cs"), "class App {}");
        using var app = Process.GetCurrentProcess();
        var binding = new GuestCommentBinding(new("sandbox", "default"), new("opaque.epoch"),
            new GuestProcessStart(app.Id, app.StartTime.ToUniversalTime().Ticks),
            new(Path.Combine(host.FullName, "App.csproj")), ["App.cs"], guest.FullName);
        return (binding, guest);
    }

    private static Comment GuestComment(string guestRoot) => new()
    {
        Id = "cmt_guest",
        Text = "bigger",
        Status = CommentStatus.Open,
        ProjectRoot = guestRoot,
        Anchor = new CommentAnchor
        {
            SourceFile = "App.cs",
            Line = 10,
            Identity = new CommentIdentity { Type = "Button" },
            CSharp = new CommentCSharpAnchor
            {
                ProjectRoot = guestRoot,
                SourceFile = "App.cs",
                Line = 10,
                Column = 13,
                LineHash = new string('A', 64),
                Factory = "Button",
            },
        },
    };
}

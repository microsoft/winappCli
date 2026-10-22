// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

[TestClass]
public class ReactorRelativeSourceTests
{
    [TestMethod]
    public void RelativeCallSites_AreFoundFromTheProjectFolderOrBelowIt()
    {
        var root = Directory.CreateTempSubdirectory("winapp-relative-");
        try
        {
            var project = Directory.CreateDirectory(Path.Combine(root.FullName, "src", "App"));
            File.WriteAllText(Path.Combine(project.FullName, "App.csproj"), "<Project />");
            var pages = Directory.CreateDirectory(Path.Combine(project.FullName, "Pages"));
            File.WriteAllText(Path.Combine(pages.FullName, "Main.cs"), "// Main");

            Assert.AreEqual(Path.Combine(pages.FullName, "Main.cs"),
                DevToolsGetSourceCommand.Handler.FindRelativeSource("Pages/Main.cs", project.FullName),
                "Reactor's project-relative path resolves from the project folder");
            Assert.AreEqual(Path.Combine(pages.FullName, "Main.cs"),
                DevToolsGetSourceCommand.Handler.FindRelativeSource("Pages/Main.cs", pages.FullName),
                "and from a folder inside it, through its parents");
            Assert.AreEqual(Path.Combine(pages.FullName, "Main.cs"),
                DevToolsGetSourceCommand.Handler.FindRelativeSource("src/App/Pages/Main.cs", project.FullName),
                "a repository-relative path resolves from a folder above the project");
            Assert.IsNull(DevToolsGetSourceCommand.Handler.FindRelativeSource("Pages/Main.cs", root.FullName),
                "outside a project there is no project to resolve against");
            Assert.IsNull(DevToolsGetSourceCommand.Handler.FindRelativeSource("../../../../App/Pages/Main.cs", pages.FullName),
                "a path that climbs out of the repository is not followed");
            Assert.IsNull(DevToolsGetSourceCommand.Handler.FindRelativeSource(Path.Combine(pages.FullName, "Main.cs"), root.FullName),
                "an absolute path is the build's own and is not searched for");
            Assert.IsNull(DevToolsGetSourceCommand.Handler.FindRelativeSource("/_/App/Pages/Main.cs", root.FullName),
                "a deterministic path is mapped by the app, not searched for");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void RelativeCallSites_ResolveAgainstTheProjectThatCompiledThem()
    {
        // repo\App references repo\Lib; repo\Shared\Badge.cs is linked into App as ../Shared/Badge.cs.
        var root = Directory.CreateTempSubdirectory("winapp-relative-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, ".git"));
            var app = Directory.CreateDirectory(Path.Combine(root.FullName, "App"));
            var lib = Directory.CreateDirectory(Path.Combine(root.FullName, "Lib"));
            var shared = Directory.CreateDirectory(Path.Combine(root.FullName, "Shared"));
            File.WriteAllText(Path.Combine(app.FullName, "App.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(lib.FullName, "Lib.csproj"), "<Project />");
            Directory.CreateDirectory(Path.Combine(app.FullName, "obj"));
            File.WriteAllText(Path.Combine(app.FullName, "obj", "project.assets.json"),
                """{"libraries":{"Lib/1.0.0":{"type":"project","path":"../Lib/Lib.csproj"},"Some.Package/1.0.0":{"type":"package","path":"some.package/1.0.0"}}}""");
            Directory.CreateDirectory(Path.Combine(lib.FullName, "Panels"));
            var footer = Path.Combine(lib.FullName, "Panels", "Footer.cs");
            File.WriteAllText(footer, "// lib");
            var badge = Path.Combine(shared.FullName, "Badge.cs");
            File.WriteAllText(badge, "// shared");

            Assert.AreEqual(footer, DevToolsGetSourceCommand.Handler.FindRelativeSource("Panels/Footer.cs", app.FullName),
                "a library's project-relative path resolves under the referenced project");
            Assert.AreEqual(badge, DevToolsGetSourceCommand.Handler.FindRelativeSource("../Shared/Badge.cs", app.FullName),
                "a linked file's ../ path resolves when the file is in the repository");

            Directory.CreateDirectory(Path.Combine(app.FullName, "Panels"));
            File.WriteAllText(Path.Combine(app.FullName, "Panels", "Footer.cs"), "// a different file");
            Assert.IsNull(DevToolsGetSourceCommand.Handler.ResolveRelativeSource("Panels/Footer.cs", app.FullName, out var candidates),
                "a same-named file under the app makes the path ambiguous instead of resolving to the app's file");
            CollectionAssert.AreEquivalent(new[] { footer, Path.Combine(app.FullName, "Panels", "Footer.cs") }, candidates);
            StringAssert.Contains(DevToolsGetSourceCommand.Handler.DescribeMissingCSharp("Panels/Footer.cs", candidates, true, "LibPanel"),
                "cannot tell which one");

            var references = ReactorBuildFacts.ReadProjectReferences(Path.Combine(app.FullName, "obj", "project.assets.json"));
            CollectionAssert.AreEqual(new[] { "../Lib/Lib.csproj" }, references, "only project libraries are project references");
            var environment = ReactorBuildFacts.ApplyEnvironment(
                new Dictionary<string, string?> { [ReactorBuildFacts.SourceRootVariable] = app.FullName },
                new ReactorBuildFacts(true, null, true, false, null, references));
            Assert.AreEqual(lib.FullName, environment[ReactorBuildFacts.ProjectDirectoriesVariable],
                "the launch hands the referenced projects' folders to the app");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
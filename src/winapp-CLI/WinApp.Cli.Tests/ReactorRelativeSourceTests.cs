// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;

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
            var pages = Directory.CreateDirectory(Path.Combine(project.FullName, "Pages"));
            File.WriteAllText(Path.Combine(pages.FullName, "Main.cs"), "// Main");

            Assert.AreEqual(Path.Combine(pages.FullName, "Main.cs"),
                DevToolsGetSourceCommand.Handler.FindRelativeSource("Pages/Main.cs", project.FullName),
                "Reactor's project-relative path resolves from the project folder");
            Assert.AreEqual(Path.Combine(pages.FullName, "Main.cs"),
                DevToolsGetSourceCommand.Handler.FindRelativeSource("Pages/Main.cs", pages.FullName),
                "and from a folder inside it, through its parents");
            Assert.AreEqual(Path.Combine(pages.FullName, "Main.cs"),
                DevToolsGetSourceCommand.Handler.FindRelativeSource("src/App/Pages/Main.cs", root.FullName),
                "a repository-relative path resolves from the repository root");
            Assert.IsNull(DevToolsGetSourceCommand.Handler.FindRelativeSource("../App/Pages/Main.cs", pages.FullName),
                "a path that climbs out is not followed");
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
}

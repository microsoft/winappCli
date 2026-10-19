// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Text.Json;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class MsBuildProjectItemEvaluatorTests : BaseCommandTests
{
    [TestMethod]
    public async Task EvaluateAsync_QueriesAllKindsAndReturnsFullPaths()
    {
        var projectDirectory =
            _tempDirectory.CreateSubdirectory("Evaluator");
        var projectPath = Path.Combine(
            projectDirectory.FullName,
            "App.csproj");
        var sourcePath = Path.Combine(
            projectDirectory.FullName,
            "Nested",
            "Model.cs");
        var definingProject = Path.Combine(
            projectDirectory.FullName,
            "DefaultItems.props");
        var packageFolder = Path.Combine(
            projectDirectory.FullName,
            "packages");
        var fallbackPackageFolder = Path.Combine(
            projectDirectory.FullName,
            "fallback-packages");
        var packageDirectory = Path.Combine(
            fallbackPackageFolder,
            "contoso.package",
            "1.0.0");
        Directory.CreateDirectory(packageDirectory);
        var assetsFile = Path.Combine(
            projectDirectory.FullName,
            "project.assets.json");
        await File.WriteAllTextAsync(
            assetsFile,
            $$"""
            {
              "packageFolders": {
                "{{packageFolder.Replace("\\", "\\\\", StringComparison.Ordinal)}}\\": {},
                "{{fallbackPackageFolder.Replace("\\", "\\\\", StringComparison.Ordinal)}}\\": {}
              },
              "libraries": {
                "Contoso.Package/1.0.0": {
                  "type": "package",
                  "path": "contoso.package/1.0.0"
                }
              }
            }
            """,
            TestContext.CancellationToken);
        var dotNet = new FakeDotNetService
        {
            RunDotnetArgumentListHandler = arguments =>
            {
                CollectionAssert.IsSubsetOf(
                    MsBuildProjectItemEvaluator.ItemKinds
                        .Select(kind => $"--getItem:{kind}")
                        .ToList(),
                    arguments.ToList());
                CollectionAssert.Contains(
                    arguments.ToList(),
                    $"--getProperty:{MsBuildProjectItemEvaluator.ProjectAssetsFileProperty}");
                return (
                    0,
                    $$"""
                    {
                      "Properties": {
                        "ProjectAssetsFile": "{{assetsFile.Replace("\\", "\\\\", StringComparison.Ordinal)}}"
                      },
                      "Items": {
                        "Compile": [
                          {
                            "Identity": "Nested\\Model.cs",
                            "FullPath": "{{sourcePath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
                            "DefiningProjectFullPath": "{{definingProject.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
                            "Link": "Shared\\Model.cs",
                            "Generator": "MSBuild:Compile"
                          }
                        ],
                        "EmbeddedResource": [],
                        "Page": [],
                        "ApplicationDefinition": [],
                        "PRIResource": [],
                        "Content": [],
                        "None": []
                      }
                    }
                    """,
                    string.Empty);
            }
        };
        var evaluator = new MsBuildProjectItemEvaluator(dotNet);

        var result = await evaluator.EvaluateAsync(
            new FileInfo(projectPath),
            TestContext.CancellationToken);

        Assert.IsTrue(result.Succeeded, result.Error);
        var item = result.Items.Single();
        Assert.AreEqual("Compile", item.Kind);
        Assert.AreEqual(sourcePath, item.FullPath);
        Assert.AreEqual(definingProject, item.DefiningProjectFullPath);
        Assert.AreEqual("Shared\\Model.cs", item.Metadata["Link"]);
        Assert.AreEqual("MSBuild:Compile", item.Metadata["Generator"]);
        CollectionAssert.AreEquivalent(
            new[] { packageDirectory },
            result.RestoredPackageDirectories.ToList());
        Assert.AreEqual(
            projectDirectory.FullName,
            dotNet.ArgumentListWorkingDirectories.Single().FullName);
    }

    [TestMethod]
    public async Task EvaluateAsync_MalformedAssetsPackageFolderFailsClosed()
    {
        var projectDirectory =
            _tempDirectory.CreateSubdirectory("MalformedAssetsEvaluator");
        var assetsFile = Path.Combine(
            projectDirectory.FullName,
            "project.assets.json");
        await File.WriteAllTextAsync(
            assetsFile,
            """
            {
              "packageFolders": {
                "C:\\bad\u0000folder\\": {}
              },
              "libraries": {}
            }
            """,
            TestContext.CancellationToken);
        var dotNet = new FakeDotNetService
        {
            RunDotnetArgumentListHandler = _ =>
                (
                    0,
                    $$"""
                    {
                      "Properties": {
                        "ProjectAssetsFile": "{{assetsFile.Replace("\\", "\\\\", StringComparison.Ordinal)}}"
                      },
                      "Items": {
                        "Compile": [],
                        "EmbeddedResource": [],
                        "Page": [],
                        "ApplicationDefinition": [],
                        "PRIResource": [],
                        "Content": [],
                        "None": []
                      }
                    }
                    """,
                    string.Empty)
        };
        var evaluator = new MsBuildProjectItemEvaluator(dotNet);

        var result = await evaluator.EvaluateAsync(
            new FileInfo(Path.Combine(
                projectDirectory.FullName,
                "App.csproj")),
            TestContext.CancellationToken);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(
            result.Error,
            "assets file is invalid");
    }

    [TestMethod]
    [DataRow(".")]
    [DataRow("package/..")]
    [DataRow("../escape")]
    [DataRow("C:/absolute/package")]
    [DataRow(". ")]
    [DataRow(".. ")]
    [DataRow("package. /version")]
    public async Task EvaluateAsync_UnsafePackageLibraryPathFailsClosed(
        string libraryPath)
    {
        var projectDirectory =
            _tempDirectory.CreateSubdirectory(
                $"UnsafeLibraryPath-{Guid.NewGuid():N}");
        var packageFolder = Path.Combine(
            projectDirectory.FullName,
            "packages");
        Directory.CreateDirectory(packageFolder);
        var assetsFile = Path.Combine(
            projectDirectory.FullName,
            "project.assets.json");
        await File.WriteAllTextAsync(
            assetsFile,
            $$"""
            {
              "packageFolders": {
                "{{packageFolder.Replace("\\", "\\\\", StringComparison.Ordinal)}}\\": {}
              },
              "libraries": {
                "Contoso.Package/1.0.0": {
                  "type": "package",
                  "path": {{JsonSerializer.Serialize(libraryPath)}}
                }
              }
            }
            """,
            TestContext.CancellationToken);
        var dotNet = new FakeDotNetService
        {
            RunDotnetArgumentListHandler = _ =>
                (
                    0,
                    $$"""
                    {
                      "Properties": {
                        "ProjectAssetsFile": "{{assetsFile.Replace("\\", "\\\\", StringComparison.Ordinal)}}"
                      },
                      "Items": {
                        "Compile": [],
                        "EmbeddedResource": [],
                        "Page": [],
                        "ApplicationDefinition": [],
                        "PRIResource": [],
                        "Content": [],
                        "None": []
                      }
                    }
                    """,
                    string.Empty)
        };
        var evaluator = new MsBuildProjectItemEvaluator(dotNet);

        var result = await evaluator.EvaluateAsync(
            new FileInfo(Path.Combine(
                projectDirectory.FullName,
                "App.csproj")),
            TestContext.CancellationToken);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(
            result.Error,
            "unsafe path");
    }

    [TestMethod]
    public async Task EvaluateAsync_MalformedJsonFailsClosed()
    {
        var projectDirectory =
            _tempDirectory.CreateSubdirectory("MalformedEvaluator");
        var dotNet = new FakeDotNetService
        {
            RunDotnetArgumentListHandler = _ =>
                (0, """{ "Items": { "Compile": [""", string.Empty)
        };
        var evaluator = new MsBuildProjectItemEvaluator(dotNet);

        var result = await evaluator.EvaluateAsync(
            new FileInfo(Path.Combine(
                projectDirectory.FullName,
                "App.csproj")),
            TestContext.CancellationToken);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Error, "malformed");
    }

    [TestMethod]
    public async Task EvaluateAsync_MissingDotNetFailsClosed()
    {
        var projectDirectory =
            _tempDirectory.CreateSubdirectory("MissingDotNetEvaluator");
        var dotNet = new FakeDotNetService
        {
            RunDotnetArgumentListHandler = _ =>
                throw new Win32Exception("dotnet was not found")
        };
        var evaluator = new MsBuildProjectItemEvaluator(dotNet);

        var result = await evaluator.EvaluateAsync(
            new FileInfo(Path.Combine(
                projectDirectory.FullName,
                "App.csproj")),
            TestContext.CancellationToken);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Error, "dotnet was not found");
    }
}

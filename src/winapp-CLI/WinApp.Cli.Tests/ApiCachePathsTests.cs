// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Services.ApiSearch;

namespace WinApp.Cli.Tests;

/// <summary>
/// Covers the cache path helpers shared by the write side (<see cref="ApiCacheBuilder"/>)
/// and the read side (<see cref="ApiQueryEngine"/>). These names are the contract between
/// the two, so a collision here does not fail — it silently answers with the wrong data.
/// </summary>
[TestClass]
public sealed class ApiCachePathsTests
{
    [TestMethod]
    public void NamespaceFileName_DottedAndUnderscoredNamespaces_DoNotCollide()
    {
        // Sanitizing '.' to '_' is lossy: "A.B" and "A_B" are different namespaces that
        // reduce to the same readable stem. Sharing one file means the parallel export
        // overwrites one with the other and a whole namespace silently disappears.
        Assert.AreNotEqual(
            ApiCachePaths.NamespaceFileName("A.B"),
            ApiCachePaths.NamespaceFileName("A_B"));
    }

    [TestMethod]
    public void NamespaceFileName_NamespacesDifferingOnlyByCase_DoNotCollide()
    {
        // Windows file names are case-insensitive, so the stems alone would collide.
        Assert.IsFalse(
            string.Equals(
                ApiCachePaths.NamespaceFileName("My.Ns"),
                ApiCachePaths.NamespaceFileName("My.NS"),
                StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void NamespaceFileName_IsDeterministic()
    {
        Assert.AreEqual(
            ApiCachePaths.NamespaceFileName("Microsoft.UI.Xaml.Controls"),
            ApiCachePaths.NamespaceFileName("Microsoft.UI.Xaml.Controls"));
    }

    [TestMethod]
    public void NamespaceFileName_TraversalAttempt_StaysASingleSegment()
    {
        string name = ApiCachePaths.NamespaceFileName(@"..\..\evil");

        Assert.AreEqual(name, Path.GetFileName(name));
        Assert.IsFalse(name.Contains(Path.DirectorySeparatorChar));
        Assert.IsFalse(name.Contains(Path.AltDirectorySeparatorChar));
    }

    [TestMethod]
    public void NamespaceFileName_VeryLongNamespace_StaysWithinFileNameLimit()
    {
        string ns = string.Join('.', Enumerable.Repeat("VeryLongSegmentName", 40));

        string name = ApiCachePaths.NamespaceFileName(ns);

        Assert.IsTrue(name.Length <= 255, $"file name was {name.Length} characters");
    }

    [TestMethod]
    public void CollidingNamespaces_BothSurviveTheCacheRoundTrip()
    {
        // End-to-end companion to the unit test above: two namespaces whose sanitized
        // stems are identical must each keep their own types through a write/read cycle.
        string cacheDir = Path.Combine(Path.GetTempPath(), $"ApiCachePathsTests_{Guid.NewGuid():N}");
        try
        {
            string packageDir = Path.Combine(cacheDir, "packages", "Test.Pkg", "1.0.0", "0a1b2c3d");
            string typesDir = Path.Combine(packageDir, "types");
            Directory.CreateDirectory(typesDir);

            WriteNamespace(typesDir, "A.B", "Dotted");
            WriteNamespace(typesDir, "A_B", "Underscored");
            File.WriteAllText(
                Path.Combine(packageDir, "namespaces.json"),
                JsonSerializer.Serialize(new List<string> { "A.B", "A_B" }, ApiSearchJsonContext.Default.ListString));

            var manifest = new ProjectManifest
            {
                ProjectName = "TestApp",
                ProjectDir = cacheDir,
                ProjectFile = "TestApp.csproj",
                Packages = [new ProjectPackageRef { Id = "Test.Pkg", Version = "1.0.0", SourceStamp = "0a1b2c3d", AssetPathKey = "0a1b2c3d" }],
                GeneratedAt = DateTime.UtcNow.ToString("o"),
            };

            var dotted = ApiQueryEngine.Types("A.B", cacheDir, manifest);
            var underscored = ApiQueryEngine.Types("A_B", cacheDir, manifest);

            Assert.AreEqual("A.B.Dotted", dotted.Data!.Types.Single().FullName);
            Assert.AreEqual("A_B.Underscored", underscored.Data!.Types.Single().FullName);
        }
        finally
        {
            try
            {
                Directory.Delete(cacheDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    [TestMethod]
    public void TryCombineContained_MalformedSegment_ReturnsFalseInsteadOfThrowing()
    {
        // A package id read from project.assets.json is untrusted, and one that cannot be
        // a path at all — an embedded NUL, say — must be reported as unsafe like any other
        // rejected segment. Letting normalization throw aborts indexing, so a single bad
        // entry makes every later find-api query fail.
        string root = Path.Combine(Path.GetTempPath(), "ApiCachePathsTests_Malformed");

        Assert.IsFalse(ApiCachePaths.TryCombineContained(root, ["Bad\0Id"], out string combined));
        Assert.AreEqual(string.Empty, combined);
    }

    [TestMethod]
    public void TryPackageCacheDir_MalformedPackageId_SkipsThePackage()
    {
        string root = Path.Combine(Path.GetTempPath(), "ApiCachePathsTests_Malformed");

        Assert.IsFalse(ApiCachePaths.TryPackageCacheDir(root, "Bad\0Id", "1.0.0", "0a1b2c3d", out string dir));
        Assert.AreEqual(string.Empty, dir);
    }

    private static void WriteNamespace(string typesDir, string ns, string typeName)
    {
        var types = new List<WinMdTypeInfo>
        {
            new()
            {
                Namespace = ns,
                Name = typeName,
                FullName = ns + "." + typeName,
                Kind = TypeKind.Class,
                SourceFile = "test.winmd",
                Members = [],
            },
        };
        File.WriteAllText(
            Path.Combine(typesDir, ApiCachePaths.NamespaceFileName(ns)),
            JsonSerializer.Serialize(types, ApiSearchJsonContext.Default.ListWinMdTypeInfo));
    }
}

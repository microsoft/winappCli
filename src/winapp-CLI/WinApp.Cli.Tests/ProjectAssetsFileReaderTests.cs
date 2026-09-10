// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// Covers reading the package graph from the <c>project.assets.json</c> a build consumed.
/// </summary>
/// <remarks>
/// This exists because <c>dotnet package list</c> cannot be told what the build used — it takes no
/// <c>-c</c>, <c>-r</c> or <c>-p</c>, and the environment is not a substitute, since MSBuild ranks
/// environment properties below a value the project assigns while the build's own switches outrank it.
/// The fixtures mirror a real SDK-written assets file: one target per TFM plus one per TFM/RID, with
/// top-level references listed under <c>project.frameworks.&lt;tfm&gt;.dependencies</c>.
/// </remarks>
[TestClass]
public class ProjectAssetsFileReaderTests
{
    private DirectoryInfo _tempDirectory = null!;

    [TestInitialize]
    public void Initialize() =>
        _tempDirectory = Directory.CreateDirectory(Path.Join(Path.GetTempPath(), $"winapp-assets-{Guid.NewGuid():N}"));

    [TestCleanup]
    public void Cleanup()
    {
        if (_tempDirectory.Exists)
        {
            _tempDirectory.Delete(recursive: true);
        }
    }

    private FileInfo WriteAssets(string json)
    {
        var path = Path.Join(_tempDirectory.FullName, "project.assets.json");
        File.WriteAllText(path, json);
        return new FileInfo(path);
    }

    private const string RealisticAssets = """
    {
      "version": 3,
      "targets": {
        "net10.0": {
          "Newtonsoft.Json/13.0.3": { "type": "package" }
        },
        "net10.0/win-x64": {
          "Newtonsoft.Json/13.0.3": { "type": "package" },
          "Microsoft.WindowsAppSDK/1.7.250606001": { "type": "package" },
          "runtime.win-x64.Microsoft.DotNet.ILCompiler/10.0.12": { "type": "package" },
          "SomeReferencedProject/1.0.0": { "type": "project" }
        }
      },
      "project": {
        "frameworks": {
          "net10.0": {
            "dependencies": {
              "Newtonsoft.Json": { "target": "Package", "version": "[13.0.3, )" },
              "Microsoft.WindowsAppSDK": { "target": "Package", "version": "[1.7.250606001, )" }
            }
          }
        }
      }
    }
    """;

    [TestMethod]
    public void Read_FoldsTheRidQualifiedTargetIn_SoARidConditionalPackageIsSeen()
    {
        // A PackageReference added only under a RID lands in the TFM/RID target. Reading just the plain
        // TFM target would miss it, which is the whole failure this reader exists to prevent.
        var result = ProjectAssetsFileReader.TryRead(WriteAssets(RealisticAssets), "win-x64");

        Assert.IsNotNull(result);
        var framework = result.Projects.Single().Frameworks.Single();
        var all = framework.TopLevelPackages.Concat(framework.TransitivePackages).Select(p => p.Id).ToList();
        CollectionAssert.Contains(all, "Microsoft.WindowsAppSDK", "A RID-qualified package must be reported");
        CollectionAssert.Contains(all, "runtime.win-x64.Microsoft.DotNet.ILCompiler");
    }

    [TestMethod]
    public void Read_LabelsTheFrameworkWithThePlainTfm()
    {
        // FilterPackageListToFramework matches on the built TFM, so a 'net10.0/win-x64' label would make
        // it fail to match and fall back to every framework.
        var result = ProjectAssetsFileReader.TryRead(WriteAssets(RealisticAssets), "win-x64");

        Assert.AreEqual("net10.0", result!.Projects.Single().Frameworks.Single().Framework);
    }

    private static readonly string[] ExpectedTopLevel = ["Newtonsoft.Json", "Microsoft.WindowsAppSDK"];
    private static readonly string[] ExpectedTransitive = ["runtime.win-x64.Microsoft.DotNet.ILCompiler"];

    [TestMethod]
    public void Read_SplitsTopLevelFromTransitive_UsingTheProjectsOwnDependencies()
    {
        var result = ProjectAssetsFileReader.TryRead(WriteAssets(RealisticAssets), "win-x64");

        var framework = result!.Projects.Single().Frameworks.Single();
        CollectionAssert.AreEquivalent(
            ExpectedTopLevel,
            framework.TopLevelPackages.Select(p => p.Id).ToList(),
            "Only what the project references directly is top-level");
        CollectionAssert.AreEquivalent(
            ExpectedTransitive,
            framework.TransitivePackages.Select(p => p.Id).ToList());
    }

    [TestMethod]
    public void Read_ReportsTheResolvedVersionFromTheLibraryKey()
    {
        var result = ProjectAssetsFileReader.TryRead(WriteAssets(RealisticAssets), "win-x64");

        var sdk = result!.Projects.Single().Frameworks.Single().TopLevelPackages
            .Single(p => p.Id == "Microsoft.WindowsAppSDK");
        Assert.AreEqual("1.7.250606001", sdk.ResolvedVersion,
            "Runtime provisioning reads ResolvedVersion to pick the matching Windows App Runtime");
    }

    [TestMethod]
    public void Read_SkipsProjectReferences_SoOnlyNuGetPackagesAreListed()
    {
        var result = ProjectAssetsFileReader.TryRead(WriteAssets(RealisticAssets), "win-x64");

        var framework = result!.Projects.Single().Frameworks.Single();
        var all = framework.TopLevelPackages.Concat(framework.TransitivePackages).Select(p => p.Id);
        Assert.IsFalse(all.Contains("SomeReferencedProject"), "A project-to-project reference is not a package");
    }

    private const string MultiRidAssets = """
    {
      "version": 3,
      "targets": {
        "net10.0": {},
        "net10.0/win-x64": {
          "Microsoft.WindowsAppSDK/1.7.250606001": { "type": "package" }
        },
        "net10.0/win-arm64": {
          "Microsoft.WindowsAppSDK/1.8.999999999": { "type": "package" }
        }
      },
      "project": {
        "frameworks": {
          "net10.0": {
            "dependencies": {
              "Microsoft.WindowsAppSDK": { "target": "Package", "version": "[1.7.250606001, )" }
            }
          }
        }
      }
    }
    """;

    [TestMethod]
    public void Read_UsesOnlyTheRidTheBuildUsed_NotASiblingRidsVersion()
    {
        // Restore accumulates a target per RID it has ever resolved. Reading them all would let another
        // architecture's Windows App SDK version drive runtime provisioning for this build, so winapp
        // would check for and install the wrong Windows App Runtime family.
        var assets = WriteAssets(MultiRidAssets);

        var x64 = ProjectAssetsFileReader.TryRead(assets, "win-x64");
        var arm64 = ProjectAssetsFileReader.TryRead(assets, "win-arm64");

        Assert.AreEqual("1.7.250606001",
            x64!.Projects.Single().Frameworks.Single().TopLevelPackages.Single().ResolvedVersion);
        Assert.AreEqual("1.8.999999999",
            arm64!.Projects.Single().Frameworks.Single().TopLevelPackages.Single().ResolvedVersion);
    }

    [TestMethod]
    public void Read_WithoutARid_SkipsRidQualifiedTargetsRatherThanGuessing()
    {
        // With no RID to match, picking one of several RID targets would be arbitrary. The plain TFM
        // target is what a RID-less build resolved, so only that is read.
        var result = ProjectAssetsFileReader.TryRead(WriteAssets(MultiRidAssets), runtimeIdentifier: null);

        var framework = result!.Projects.Single().Frameworks.Single();
        Assert.AreEqual(0, framework.TopLevelPackages.Count + framework.TransitivePackages.Count,
            "A sibling RID's packages must not be attributed to a RID-less build");
    }

    [TestMethod]
    public void Read_MissingFile_ReturnsNull_SoDiscoveryFallsBackToTheCommand()
    {
        var missing = new FileInfo(Path.Join(_tempDirectory.FullName, "nope.json"));

        Assert.IsNull(ProjectAssetsFileReader.TryRead(missing));
    }

    [TestMethod]
    public void Read_MalformedJson_ReturnsNull_RatherThanFailingTheRun()
    {
        // A truncated or hand-edited assets file must not take the run down; discovery falls back.
        Assert.IsNull(ProjectAssetsFileReader.TryRead(WriteAssets("{ \"targets\": ")));
    }

    [TestMethod]
    public void Read_AssetsWithoutDeclaredFrameworks_ReturnsNull()
    {
        Assert.IsNull(ProjectAssetsFileReader.TryRead(WriteAssets("""{ "version": 3, "targets": {} }""")));
    }
}

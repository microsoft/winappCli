// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Services.ApiSearch;

namespace WinApp.Cli.Tests;

/// <summary>
/// Covers the metadata-selection rules that decide *which* .winmd files answer a
/// query. These are correctness rules rather than formatting: picking the wrong
/// SDK or runtime produces a confident answer about an API the project cannot
/// actually compile against.
/// </summary>
[TestClass]
public sealed class NuGetResolverTests
{
    private static readonly string[] SelectedWinmdOnly = ["Contoso.winmd"];
    private static readonly string[] ScannedRuntimeWinmd = ["Contoso.Runtime.winmd"];

    private string _dir = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"NuGetResolverTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [TestMethod]
    public void FindPackagesFromAssets_MalformedAssetsFile_WarnsInsteadOfSilentlyIndexingNothing()
    {
        // The caller only reaches this method when project.assets.json exists, so a parse
        // failure means restore output is present but unreadable. Staying silent leaves the
        // caller with a thin index and no way to tell an unavailable API from an unread one.
        string path = WriteAssets(@"{ ""libraries"": { ""Contoso/1.0.0"": { ""type"": ""pack");
        var warnings = new List<string>();

        List<PackageWithWinMd> packages = NuGetResolver.FindPackagesFromAssets(path, warnings.Add);

        Assert.AreEqual(0, packages.Count);
        Assert.AreEqual(1, warnings.Count, "a truncated assets file must be reported");
        StringAssert.Contains(warnings[0], "project.assets.json");
        StringAssert.Contains(warnings[0], "dotnet restore");
    }

    [TestMethod]
    public void FindPackagesFromAssets_WellFormedAssetsFile_DoesNotWarn()
    {
        // The warning must mark a real failure, not fire on every project.
        string path = WriteAssets(JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object>(),
            libraries = new Dictionary<string, object>(),
        }));
        var warnings = new List<string>();

        NuGetResolver.FindPackagesFromAssets(path, warnings.Add);

        Assert.AreEqual(0, warnings.Count);
    }
    private string WriteAssets(string json)
    {
        string path = Path.Combine(_dir, "project.assets.json");
        File.WriteAllText(path, json);
        return path;
    }

    [TestMethod]
    public void ReadTargetPlatformVersion_ReadsWindowsVersionFromTargetMoniker()
    {
        string path = WriteAssets("""
        {
          "targets": { "net8.0-windows10.0.26100.0": {} },
          "libraries": {}
        }
        """);

        Assert.AreEqual("10.0.26100.0", NuGetResolver.ReadTargetPlatformVersion(path));
    }

    [TestMethod]
    public void ReadTargetPlatformVersion_FallsBackToProjectFrameworks()
    {
        string path = WriteAssets("""
        {
          "targets": { "net8.0": {} },
          "project": { "frameworks": { "net8.0-windows10.0.22621.0": {} } }
        }
        """);

        Assert.AreEqual("10.0.22621.0", NuGetResolver.ReadTargetPlatformVersion(path));
    }

    [TestMethod]
    public void ReadTargetPlatformVersion_ReturnsNullWhenNotWindowsTargeted()
    {
        string path = WriteAssets("""
        {
          "targets": { "net8.0": {} },
          "project": { "frameworks": { "net8.0": {} } }
        }
        """);

        Assert.IsNull(NuGetResolver.ReadTargetPlatformVersion(path));
    }

    [TestMethod]
    public void ReadTargetPlatformVersion_ReturnsNullForUnreadableAssets()
    {
        string path = WriteAssets("{ this is not json");

        Assert.IsNull(NuGetResolver.ReadTargetPlatformVersion(path));
    }

    [TestMethod]
    public void FindPackagesFromAssets_PrefersSelectedCompileAssetsOverEveryWinmdOnDisk()
    {
        // The package ships metadata for two targets; restore selected only one.
        // Indexing both lets a query confirm an API the project cannot compile against.
        string packageRoot = Path.Combine(_dir, "packages");
        string packageDir = Path.Combine(packageRoot, "contoso.metadata", "1.0.0");
        string selected = Path.Combine(packageDir, "lib", "net8.0-windows10.0.26100.0");
        string notSelected = Path.Combine(packageDir, "lib", "uap10.0");
        Directory.CreateDirectory(selected);
        Directory.CreateDirectory(notSelected);
        File.WriteAllText(Path.Combine(selected, "Contoso.winmd"), "x");
        File.WriteAllText(Path.Combine(notSelected, "Legacy.winmd"), "x");

        string path = WriteAssets(JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [packageRoot] = new { } },
            targets = new Dictionary<string, object>
            {
                ["net8.0-windows10.0.26100.0"] = new Dictionary<string, object>
                {
                    ["Contoso.Metadata/1.0.0"] = new
                    {
                        compile = new Dictionary<string, object>
                        {
                            ["lib/net8.0-windows10.0.26100.0/Contoso.winmd"] = new { },
                        },
                    },
                },
            },
            libraries = new Dictionary<string, object>
            {
                ["Contoso.Metadata/1.0.0"] = new { type = "package", path = "contoso.metadata/1.0.0" },
            },
        }));

        List<PackageWithWinMd> packages = NuGetResolver.FindPackagesFromAssets(path);

        Assert.AreEqual(1, packages.Count);
        CollectionAssert.AreEquivalent(
            SelectedWinmdOnly,
            packages[0].WinMdFiles.Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public void FindPackagesFromAssets_FallsBackToScanWhenRestoreNamedNoCompileAssets()
    {
        // Some WinRT metadata packages carry .winmd outside any compile group. Those
        // must still index, or the fix for over-broad scanning would lose real APIs.
        string packageRoot = Path.Combine(_dir, "packages");
        string packageDir = Path.Combine(packageRoot, "contoso.runtime", "2.0.0");
        string metadata = Path.Combine(packageDir, "metadata");
        Directory.CreateDirectory(metadata);
        File.WriteAllText(Path.Combine(metadata, "Contoso.Runtime.winmd"), "x");

        string path = WriteAssets(JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [packageRoot] = new { } },
            targets = new Dictionary<string, object>
            {
                ["net8.0-windows10.0.26100.0"] = new Dictionary<string, object>
                {
                    ["Contoso.Runtime/2.0.0"] = new { },
                },
            },
            libraries = new Dictionary<string, object>
            {
                ["Contoso.Runtime/2.0.0"] = new { type = "package", path = "contoso.runtime/2.0.0" },
            },
        }));

        List<PackageWithWinMd> packages = NuGetResolver.FindPackagesFromAssets(path);

        Assert.AreEqual(1, packages.Count);
        CollectionAssert.AreEquivalent(
            ScannedRuntimeWinmd,
            packages[0].WinMdFiles.Select(Path.GetFileName).ToArray());
    }

    private static readonly (string Dir, Version? Version)[] InstalledSdks =
    [
        (@"C:\Kits\10.0.28000.0", new Version("10.0.28000.0")),
        (@"C:\Kits\10.0.26100.0", new Version("10.0.26100.0")),
        (@"C:\Kits\10.0.19041.0", new Version("10.0.19041.0")),
    ];

    [TestMethod]
    public void SelectWindowsSdkDir_PrefersTheExactTargetedVersion()
    {
        string? picked = NuGetResolver.SelectWindowsSdkDir(InstalledSdks, "10.0.26100.0", _ => true);

        Assert.AreEqual(@"C:\Kits\10.0.26100.0", picked);
    }

    [TestMethod]
    public void SelectWindowsSdkDir_NeverPicksAnSdkNewerThanTheTarget()
    {
        // The targeted 10.0.22621.0 is not installed. UnionMetadata is cumulative, so
        // answering from the newer 10.0.26100.0 would confirm APIs that do not exist at
        // the target and fail the build with CS0234. Cap at the highest install below it.
        var warnings = new List<string>();
        string? picked = NuGetResolver.SelectWindowsSdkDir(
            InstalledSdks, "10.0.22621.0", _ => true, warnings.Add);

        Assert.AreEqual(@"C:\Kits\10.0.19041.0", picked);
        Assert.AreEqual(1, warnings.Count);
        StringAssert.Contains(warnings[0], "10.0.22621.0 is not installed");
    }

    [TestMethod]
    public void SelectWindowsSdkDir_FallsBackToClosestNewerWhenNothingAtOrBelowTarget()
    {
        // Only newer SDKs are installed, so some overshoot is unavoidable. Take the
        // smallest one and say the results may include APIs the target does not have.
        var warnings = new List<string>();
        string? picked = NuGetResolver.SelectWindowsSdkDir(
            InstalledSdks, "10.0.17763.0", _ => true, warnings.Add);

        Assert.AreEqual(@"C:\Kits\10.0.19041.0", picked);
        Assert.AreEqual(1, warnings.Count);
        StringAssert.Contains(warnings[0], "at or below");
    }

    [TestMethod]
    public void SelectWindowsSdkDir_MatchesOnBuildNumberWhenRevisionsDisagree()
    {
        var warnings = new List<string>();
        string? picked = NuGetResolver.SelectWindowsSdkDir(
            InstalledSdks, "10.0.26100.1742", _ => true, warnings.Add);

        Assert.AreEqual(@"C:\Kits\10.0.26100.0", picked);
        Assert.AreEqual(0, warnings.Count, "a build-number match is the targeted SDK, so it must not warn");
    }

    [TestMethod]
    public void SelectWindowsSdkDir_UsesNewestWhenProjectTargetsNoPlatformVersion()
    {
        string? picked = NuGetResolver.SelectWindowsSdkDir(InstalledSdks, null, _ => true);

        Assert.AreEqual(@"C:\Kits\10.0.28000.0", picked);
    }

    [TestMethod]
    public void SelectWindowsSdkDir_SkipsCandidatesWithoutWindowsWinmd()
    {
        // An SDK folder can exist without UnionMetadata content; it must not be chosen
        // just because its version matches.
        string? picked = NuGetResolver.SelectWindowsSdkDir(
            InstalledSdks, "10.0.26100.0", dir => dir.EndsWith("19041.0", StringComparison.Ordinal));

        Assert.AreEqual(@"C:\Kits\10.0.19041.0", picked);
    }

    [TestMethod]
    public void FindPackagesFromAssets_SkipsLibraryAbsentFromSelectedTarget()
    {
        // "libraries" is global across every restored target framework. This package is
        // built only by the non-Windows target, so it is not on the Windows compile
        // surface. Without the target check it reaches the scan fallback and its .winmd
        // is indexed, which confirms an API the Windows build cannot compile against.
        string packageRoot = Path.Combine(_dir, "packages");
        string offTarget = Path.Combine(packageRoot, "contoso.nonwindows", "1.0.0", "metadata");
        Directory.CreateDirectory(offTarget);
        File.WriteAllText(Path.Combine(offTarget, "Contoso.NonWindows.winmd"), "x");

        string inTarget = Path.Combine(packageRoot, "contoso.runtime", "2.0.0", "metadata");
        Directory.CreateDirectory(inTarget);
        File.WriteAllText(Path.Combine(inTarget, "Contoso.Runtime.winmd"), "x");

        string path = WriteAssets(JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [packageRoot] = new { } },
            targets = new Dictionary<string, object>
            {
                ["net8.0-windows10.0.26100.0"] = new Dictionary<string, object>
                {
                    ["Contoso.Runtime/2.0.0"] = new { },
                },
                ["net8.0"] = new Dictionary<string, object>
                {
                    ["Contoso.NonWindows/1.0.0"] = new { },
                },
            },
            libraries = new Dictionary<string, object>
            {
                ["Contoso.Runtime/2.0.0"] = new { type = "package", path = "contoso.runtime/2.0.0" },
                ["Contoso.NonWindows/1.0.0"] = new { type = "package", path = "contoso.nonwindows/1.0.0" },
            },
        }));

        List<PackageWithWinMd> packages = NuGetResolver.FindPackagesFromAssets(path);

        Assert.AreEqual(1, packages.Count);
        Assert.AreEqual("Contoso.Runtime", packages[0].Id);
        CollectionAssert.AreEquivalent(
            ScannedRuntimeWinmd,
            packages[0].WinMdFiles.Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public void FindPackagesFromAssets_ScansEveryLibraryWhenNoTargetWasSelected()
    {
        // With no "targets" element there is no selected target to judge membership
        // against, so no library may be treated as out-of-target. Every one stays
        // eligible for the scan fallback, as it was before that check existed.
        string packageRoot = Path.Combine(_dir, "packages");
        string metadata = Path.Combine(packageRoot, "contoso.runtime", "2.0.0", "metadata");
        Directory.CreateDirectory(metadata);
        File.WriteAllText(Path.Combine(metadata, "Contoso.Runtime.winmd"), "x");

        string path = WriteAssets(JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [packageRoot] = new { } },
            libraries = new Dictionary<string, object>
            {
                ["Contoso.Runtime/2.0.0"] = new { type = "package", path = "contoso.runtime/2.0.0" },
            },
        }));

        List<PackageWithWinMd> packages = NuGetResolver.FindPackagesFromAssets(path);

        Assert.AreEqual(1, packages.Count);
        CollectionAssert.AreEquivalent(
            ScannedRuntimeWinmd,
            packages[0].WinMdFiles.Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public void FindPackagesFromAssets_SelectsWindowsTargetWhenSeveralWereRestored()
    {
        // A multi-targeted project lists several targets, and the non-Windows one can be
        // listed first. Reading its compile assets reports every Windows-only type in the
        // package as missing, even though the Windows build compiles against them.
        string packageRoot = Path.Combine(_dir, "packages");
        string packageDir = Path.Combine(packageRoot, "contoso.metadata", "1.0.0");
        string portable = Path.Combine(packageDir, "lib", "net8.0");
        string windows = Path.Combine(packageDir, "lib", "net8.0-windows10.0.19041.0");
        Directory.CreateDirectory(portable);
        Directory.CreateDirectory(windows);
        File.WriteAllText(Path.Combine(portable, "Portable.winmd"), "x");
        File.WriteAllText(Path.Combine(windows, "Contoso.winmd"), "x");

        string path = WriteAssets(JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [packageRoot] = new { } },
            targets = new Dictionary<string, object>
            {
                ["net8.0"] = new Dictionary<string, object>
                {
                    ["Contoso.Metadata/1.0.0"] = new
                    {
                        compile = new Dictionary<string, object> { ["lib/net8.0/Portable.winmd"] = new { } },
                    },
                },
                ["net8.0-windows10.0.19041.0"] = new Dictionary<string, object>
                {
                    ["Contoso.Metadata/1.0.0"] = new
                    {
                        compile = new Dictionary<string, object> { ["lib/net8.0-windows10.0.19041.0/Contoso.winmd"] = new { } },
                    },
                },
            },
            libraries = new Dictionary<string, object>
            {
                ["Contoso.Metadata/1.0.0"] = new { type = "package", path = "contoso.metadata/1.0.0" },
            },
        }));

        List<PackageWithWinMd> packages = NuGetResolver.FindPackagesFromAssets(path);

        Assert.AreEqual(1, packages.Count);
        CollectionAssert.AreEquivalent(
            SelectedWinmdOnly,
            packages[0].WinMdFiles.Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public void FindPackagesFromAssets_SelectsWindowsTargetWithoutAnSdkVersion()
    {
        // A desktop project commonly multi-targets net8.0 and net8.0-windows7.0, which
        // names no Windows SDK version at all. Requiring a three-part version here reads
        // the portable target's assets and reports every Windows-only type as missing.
        string packageRoot = Path.Combine(_dir, "packages");
        string packageDir = Path.Combine(packageRoot, "contoso.metadata", "1.0.0");
        string portable = Path.Combine(packageDir, "lib", "net8.0");
        string windows = Path.Combine(packageDir, "lib", "net8.0-windows7.0");
        Directory.CreateDirectory(portable);
        Directory.CreateDirectory(windows);
        File.WriteAllText(Path.Combine(portable, "Portable.winmd"), "x");
        File.WriteAllText(Path.Combine(windows, "Contoso.winmd"), "x");

        string path = WriteAssets(JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [packageRoot] = new { } },
            targets = new Dictionary<string, object>
            {
                ["net8.0"] = new Dictionary<string, object>
                {
                    ["Contoso.Metadata/1.0.0"] = new
                    {
                        compile = new Dictionary<string, object> { ["lib/net8.0/Portable.winmd"] = new { } },
                    },
                },
                ["net8.0-windows7.0"] = new Dictionary<string, object>
                {
                    ["Contoso.Metadata/1.0.0"] = new
                    {
                        compile = new Dictionary<string, object> { ["lib/net8.0-windows7.0/Contoso.winmd"] = new { } },
                    },
                },
            },
            libraries = new Dictionary<string, object>
            {
                ["Contoso.Metadata/1.0.0"] = new { type = "package", path = "contoso.metadata/1.0.0" },
            },
        }));

        List<PackageWithWinMd> packages = NuGetResolver.FindPackagesFromAssets(path);

        Assert.AreEqual(1, packages.Count);
        CollectionAssert.AreEquivalent(
            SelectedWinmdOnly,
            packages[0].WinMdFiles.Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public void ReadTargetPlatformVersion_PicksTheSameTargetTheCompileAssetsComeFrom()
    {
        // Compile assets are read from the highest Windows target. Reading the SDK
        // version from the first one instead pairs 26100 package assets with 19041 SDK
        // metadata, so an API introduced in 26100 is reported missing.
        string path = WriteAssets(JsonSerializer.Serialize(new
        {
            targets = new Dictionary<string, object>
            {
                ["net8.0-windows10.0.19041.0"] = new Dictionary<string, object>(),
                ["net8.0-windows10.0.26100.0"] = new Dictionary<string, object>(),
            },
            libraries = new Dictionary<string, object>(),
        }));

        Assert.AreEqual("10.0.26100.0", NuGetResolver.ReadTargetPlatformVersion(path));
    }

    [TestMethod]
    public void FindPackagesFromAssets_TreatsPlaceholderOnlyCompileGroupAsNoAssets()
    {
        // A compile group of nothing but NuGet's "_._" placeholder means the package
        // deliberately exposes no compile-time assets for this target. Reading that as
        // "restore named nothing" scans the package and confirms an API from a target
        // the project does not build.
        string packageRoot = Path.Combine(_dir, "packages");
        string packageDir = Path.Combine(packageRoot, "contoso.metadata", "1.0.0");
        string placeholder = Path.Combine(packageDir, "lib", "net8.0");
        string other = Path.Combine(packageDir, "lib", "uap10.0");
        Directory.CreateDirectory(placeholder);
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(placeholder, "_._"), string.Empty);
        File.WriteAllText(Path.Combine(other, "Legacy.winmd"), "x");

        string path = WriteAssets(JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [packageRoot] = new { } },
            targets = new Dictionary<string, object>
            {
                ["net8.0-windows10.0.19041.0"] = new Dictionary<string, object>
                {
                    ["Contoso.Metadata/1.0.0"] = new
                    {
                        compile = new Dictionary<string, object> { ["lib/net8.0/_._"] = new { } },
                    },
                },
            },
            libraries = new Dictionary<string, object>
            {
                ["Contoso.Metadata/1.0.0"] = new { type = "package", path = "contoso.metadata/1.0.0" },
            },
        }));

        Assert.AreEqual(0, NuGetResolver.FindPackagesFromAssets(path).Count);
    }

    [TestMethod]
    public void FindPackagesFromAssets_SkipsNetworkPackageFolders()
    {
        // packageFolders comes from a file inside the repository, so cloning a repository
        // is enough to choose it. A UNC value turns a local read-only query into an
        // outbound authentication attempt against a host the repository picked.
        string path = WriteAssets(JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [@"\\192.0.2.1\share"] = new { } },
            targets = new Dictionary<string, object>
            {
                ["net8.0-windows10.0.19041.0"] = new Dictionary<string, object>
                {
                    ["Contoso.Metadata/1.0.0"] = new { },
                },
            },
            libraries = new Dictionary<string, object>
            {
                ["Contoso.Metadata/1.0.0"] = new { type = "package", path = "contoso.metadata/1.0.0" },
            },
        }));

        Assert.AreEqual(0, NuGetResolver.FindPackagesFromAssets(path).Count);
    }

    [TestMethod]
    public void FindPackagesFromAssets_IgnoresCompileAssetsOutsideThePackage()
    {
        // A compile asset name is combined with the package directory, and a rooted or
        // climbing value silently wins over it — reading metadata from anywhere on disk.
        string packageRoot = Path.Combine(_dir, "packages");
        string packageDir = Path.Combine(packageRoot, "contoso.metadata", "1.0.0");
        string outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(packageDir);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "Escaped.winmd"), "x");

        string path = WriteAssets(JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [packageRoot] = new { } },
            targets = new Dictionary<string, object>
            {
                ["net8.0-windows10.0.19041.0"] = new Dictionary<string, object>
                {
                    ["Contoso.Metadata/1.0.0"] = new
                    {
                        compile = new Dictionary<string, object>
                        {
                            ["../../../outside/Escaped.winmd"] = new { },
                        },
                    },
                },
            },
            libraries = new Dictionary<string, object>
            {
                ["Contoso.Metadata/1.0.0"] = new { type = "package", path = "contoso.metadata/1.0.0" },
            },
        }));

        Assert.AreEqual(0, NuGetResolver.FindPackagesFromAssets(path).Count);
    }

    [TestMethod]
    public void RuntimeReleaseLabel_KeepsNameEncodedReleaseForOnePointX()
    {
        // 1.x encodes the release in the name and uses an unrelated package version.
        Assert.AreEqual(
            "1.8",
            NuGetResolver.RuntimeReleaseLabel("Microsoft.WindowsAppRuntime.1.8_8000.946.1701.0_arm64__8wekyb3d8bbwe"));
    }

    [TestMethod]
    public void RuntimeReleaseLabel_CombinesMajorOnlyNameWithPackageVersion()
    {
        // 2.x carries only the major in the name; the real release is in the version,
        // so a bare "2" would understate which runtime answered.
        Assert.AreEqual(
            "2.4",
            NuGetResolver.RuntimeReleaseLabel("Microsoft.WindowsAppRuntime.2_2.4.0.0_arm64__8wekyb3d8bbwe"));
    }

    [TestMethod]
    public void RuntimeReleaseLabel_PreservesExperimentalSuffix()
    {
        Assert.AreEqual(
            "1.7-experimental3",
            NuGetResolver.RuntimeReleaseLabel("Microsoft.WindowsAppRuntime.1.7-experimental3_7000.392.2319.0_arm64__8wekyb3d8bbwe"));
    }

    [TestMethod]
    public void RuntimeReleaseLabel_ReturnsFolderNameWhenNotARuntimePackage()
    {
        Assert.AreEqual("SomethingElse", NuGetResolver.RuntimeReleaseLabel("SomethingElse"));
    }

    #region winapp.yaml projects (Electron and other non-MSBuild apps)

    /// <summary>
    /// Lays out a project that has no MSBuild project file: <c>winapp.yaml</c> plus the
    /// <c>.winapp/winmds.lock.json</c> that <c>winapp restore</c> writes, with the named
    /// package's <c>.winmd</c> files staged under a NuGet-cache-shaped folder so the
    /// resolver's XML-doc lookup has a real package root to walk up to.
    /// </summary>
    private string WriteWinappProject(
        string packageId,
        string version,
        string[] winmdNames,
        int schema = 3,
        bool createWinmdFiles = true,
        string? xmlDocName = null)
    {
        string projectDir = Path.Combine(_dir, "app");
        Directory.CreateDirectory(Path.Combine(projectDir, ".winapp"));
        File.WriteAllText(Path.Combine(projectDir, "winapp.yaml"), $"packages:\n  - name: {packageId}\n    version: {version}\n");

        string packageRoot = Path.Combine(_dir, "nuget", packageId.ToLowerInvariant(), version);
        string libDir = Path.Combine(packageRoot, "lib", "uap10.0");
        Directory.CreateDirectory(libDir);

        var winmdPaths = new List<string>();
        foreach (string name in winmdNames)
        {
            string path = Path.Combine(libDir, name);
            if (createWinmdFiles)
            {
                File.WriteAllText(path, "not real metadata");
            }
            winmdPaths.Add(path);
        }
        if (xmlDocName is not null)
        {
            // FindXmlDocsInPackageFolder skips XML under 1 KB as carrying no real docs.
            File.WriteAllText(Path.Combine(libDir, xmlDocName), new string('x', 2048));
        }

        string lockfile = JsonSerializer.Serialize(new
        {
            schema,
            generated_at = DateTime.UtcNow.ToString("o"),
            packages = new[] { new { name = packageId, version, winmds = winmdPaths } },
        });
        File.WriteAllText(Path.Combine(projectDir, ".winapp", "winmds.lock.json"), lockfile);
        return projectDir;
    }

    [TestMethod]
    public void FindPackagesFromWinmdsLockfile_ReadsPackagesForAProjectWithNoProjectFile()
    {
        string projectDir = WriteWinappProject("Microsoft.WindowsAppSDK", "1.5.240607001", ["Microsoft.UI.Xaml.winmd", "Microsoft.UI.Text.winmd"]);

        List<PackageWithWinMd> packages = NuGetResolver.FindPackagesFromWinmdsLockfile(projectDir);

        Assert.HasCount(1, packages);
        Assert.AreEqual("Microsoft.WindowsAppSDK", packages[0].Id);
        Assert.AreEqual("1.5.240607001", packages[0].Version);
        Assert.HasCount(2, packages[0].WinMdFiles);
    }

    [TestMethod]
    public void FindPackagesFromWinmdsLockfile_FindsXmlDocsInThePackageFolder()
    {
        string projectDir = WriteWinappProject(
            "Microsoft.WindowsAppSDK", "1.5.240607001", ["Microsoft.UI.Xaml.winmd"], xmlDocName: "Microsoft.UI.Xaml.xml");

        List<PackageWithWinMd> packages = NuGetResolver.FindPackagesFromWinmdsLockfile(projectDir);

        Assert.HasCount(1, packages);
        Assert.HasCount(1, packages[0].XmlDocFiles);
        Assert.EndsWith("Microsoft.UI.Xaml.xml", packages[0].XmlDocFiles[0]);
    }

    [TestMethod]
    public void FindPackagesFromWinmdsLockfile_SkipsEntriesWhoseFilesAreGone()
    {
        // A lockfile written before the NuGet cache was cleared still names the files.
        // Indexing them would fail per-package; contributing nothing is the honest answer.
        string projectDir = WriteWinappProject(
            "Microsoft.WindowsAppSDK", "1.5.240607001", ["Microsoft.UI.Xaml.winmd"], createWinmdFiles: false);

        Assert.IsEmpty(NuGetResolver.FindPackagesFromWinmdsLockfile(projectDir));
    }

    [TestMethod]
    public void FindPackagesFromWinmdsLockfile_IgnoresAnUnknownSchema()
    {
        string projectDir = WriteWinappProject(
            "Microsoft.WindowsAppSDK", "1.5.240607001", ["Microsoft.UI.Xaml.winmd"], schema: 99);

        Assert.IsEmpty(NuGetResolver.FindPackagesFromWinmdsLockfile(projectDir));
    }

    [TestMethod]
    public void FindPackagesFromWinmdsLockfile_ReturnsNothingWhenThereIsNoLockfile()
    {
        string projectDir = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(projectDir);

        Assert.IsEmpty(NuGetResolver.FindPackagesFromWinmdsLockfile(projectDir));
    }

    [TestMethod]
    public void FindRestoreOutput_UsesTheLockfileWhenThereIsNoProjectAssetsJson()
    {
        string projectDir = WriteWinappProject("Microsoft.WindowsAppSDK", "1.5.240607001", ["Microsoft.UI.Xaml.winmd"]);

        string? restoreOutput = NuGetResolver.FindRestoreOutput(projectDir);

        Assert.IsNotNull(restoreOutput);
        Assert.EndsWith("winmds.lock.json", restoreOutput);
    }

    [TestMethod]
    public void FindRestoreOutput_PrefersProjectAssetsJsonWhenBothExist()
    {
        string projectDir = WriteWinappProject("Microsoft.WindowsAppSDK", "1.5.240607001", ["Microsoft.UI.Xaml.winmd"]);
        Directory.CreateDirectory(Path.Combine(projectDir, "obj"));
        File.WriteAllText(Path.Combine(projectDir, "obj", "project.assets.json"), "{}");

        string? restoreOutput = NuGetResolver.FindRestoreOutput(projectDir);

        Assert.IsNotNull(restoreOutput);
        Assert.EndsWith("project.assets.json", restoreOutput);
    }

    #endregion
}

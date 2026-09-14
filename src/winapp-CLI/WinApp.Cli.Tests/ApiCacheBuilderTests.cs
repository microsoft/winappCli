// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.ApiSearch;

namespace WinApp.Cli.Tests;

/// <summary>
/// Covers how the cache builder maps a resolved package to the directory its metadata is
/// exported to. A package id and version do not identify what was cached — two projects
/// can resolve the same id and version to different files — so the mapping has to keep
/// them apart or one project silently answers from the other's metadata.
/// </summary>
[TestClass]
public sealed class ApiCacheBuilderTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ApiCacheBuilderTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }

    [TestMethod]
    public void BuildCache_TwoProjectsRaiseTheSameCaveat_ReportsItOnce()
    {
        // A caveat is usually about the machine or a shared package, not one project, so
        // in a solution the identical sentence comes back from every project in it. A
        // refresh that repeats it once per project buries the rest of its output.
        string root = Path.Combine(_dir, "Solution");
        foreach (string name in new[] { "First", "Second" })
        {
            string dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            // Same file name in both directories, so both projects produce the very same
            // "no restore output" sentence, and each returns before any package is read.
            File.WriteAllText(Path.Combine(dir, "App.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup><PackageReference Include="Contoso" Version="1.0.0" /></ItemGroup>
                </Project>
                """);
        }
        var progress = new List<string>();

        ApiCacheBuilder.BuildCache(root, Path.Combine(_dir, "cache"), scan: true, null, progress.Add);

        Assert.AreEqual(
            1,
            progress.Count(m => m.Contains("has PackageReferences but no restore output", StringComparison.Ordinal)),
            "one refresh must state a caveat once, not once per project that hits it");
    }

    [TestMethod]
    public void ResolvePackageExports_SameIdAndVersionFromDifferentFiles_GetSeparateCaches()
    {
        // Two projects reference "Contoso.Sdk 1.0.0" but resolve it to different .winmd
        // files — a rebuilt project reference, or two target frameworks selecting
        // different compile assets. Keyed on id and version alone, the second project
        // finds the first project's directory already claimed, records it as reused, and
        // its manifest then points at metadata built from the other project's files.
        string cacheDir = Path.Combine(_dir, "cache");
        PackageWithWinMd fromProjectA = WritePackage("a", "Contoso.Sdk", "1.0.0", "alpha");
        PackageWithWinMd fromProjectB = WritePackage("b", "Contoso.Sdk", "1.0.0", "beta-different-bytes");

        var pendingExports = new Dictionary<string, PackageWithWinMd>(StringComparer.OrdinalIgnoreCase);
        var seenPackageDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int reused = 0;

        List<ProjectPackageRef> refsA = ApiCacheBuilder.ResolvePackageExports(
            [fromProjectA], cacheDir, force: false, pendingExports, seenPackageDirs, ref reused, report: null);
        List<ProjectPackageRef> refsB = ApiCacheBuilder.ResolvePackageExports(
            [fromProjectB], cacheDir, force: false, pendingExports, seenPackageDirs, ref reused, report: null);

        // Both projects must get their own export queued, and neither may be written off
        // as a reuse of the other.
        Assert.AreEqual(2, pendingExports.Count);
        Assert.AreEqual(0, reused);
        Assert.AreNotEqual(refsA.Single().SourceStamp, refsB.Single().SourceStamp);

        // And the manifest each project records has to resolve to its own directory.
        Assert.IsTrue(ApiCachePaths.TryPackageCacheDir(cacheDir, refsA.Single(), out string dirA));
        Assert.IsTrue(ApiCachePaths.TryPackageCacheDir(cacheDir, refsB.Single(), out string dirB));
        Assert.AreNotEqual(dirA, dirB);
    }

    [TestMethod]
    public void ResolvePackageExports_SamePackageFromSameFiles_IsExportedOnce()
    {
        // The reuse that does matter still has to happen: the Windows SDK and WinAppSDK
        // are shared by every project in a solution and must be parsed once per run.
        string cacheDir = Path.Combine(_dir, "cache");
        PackageWithWinMd shared = WritePackage("shared", "Contoso.Sdk", "1.0.0", "same");

        var pendingExports = new Dictionary<string, PackageWithWinMd>(StringComparer.OrdinalIgnoreCase);
        var seenPackageDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int reused = 0;

        List<ProjectPackageRef> first = ApiCacheBuilder.ResolvePackageExports(
            [shared], cacheDir, force: false, pendingExports, seenPackageDirs, ref reused, report: null);
        List<ProjectPackageRef> second = ApiCacheBuilder.ResolvePackageExports(
            [shared], cacheDir, force: false, pendingExports, seenPackageDirs, ref reused, report: null);

        Assert.AreEqual(1, pendingExports.Count);
        Assert.AreEqual(1, reused);
        Assert.AreEqual(first.Single().SourceStamp, second.Single().SourceStamp);
    }

    [TestMethod]
    public void ResolvePackageExports_SameFilesRebuilt_ReusesOneDirectory()
    {
        // Rebuilding a referenced project rewrites its .winmd at the same path. That has
        // to re-export into the same directory: keyed on the write time instead, every
        // build would mint a new directory and orphan the previous one forever.
        string cacheDir = Path.Combine(_dir, "cache");
        PackageWithWinMd before = WritePackage("proj", "Contoso.Sdk", "1.0.0", "v1");

        var pendingExports = new Dictionary<string, PackageWithWinMd>(StringComparer.OrdinalIgnoreCase);
        var seenPackageDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int reused = 0;
        List<ProjectPackageRef> first = ApiCacheBuilder.ResolvePackageExports(
            [before], cacheDir, force: false, pendingExports, seenPackageDirs, ref reused, report: null);
        string exportedFirstTo = pendingExports.Keys.Single();

        // Same package, same paths, different bytes and a later write time.
        PackageWithWinMd after = WritePackage("proj", "Contoso.Sdk", "1.0.0", "v2-different-bytes");

        pendingExports.Clear();
        seenPackageDirs.Clear();
        reused = 0;
        List<ProjectPackageRef> second = ApiCacheBuilder.ResolvePackageExports(
            [after], cacheDir, force: false, pendingExports, seenPackageDirs, ref reused, report: null);
        string exportedSecondTo = pendingExports.Keys.Single();

        // The rebuild must re-export — but into the directory it already used, not a new
        // one beside it.
        Assert.AreEqual(0, reused);
        Assert.AreEqual(exportedFirstTo, exportedSecondTo, "a rebuild must reuse the directory, not orphan it");
        Assert.AreNotEqual(first.Single().SourceStamp, second.Single().SourceStamp);

        // And each project's manifest entry has to resolve to that same directory.
        Assert.IsTrue(ApiCachePaths.TryPackageCacheDir(cacheDir, second.Single(), out string dirFromManifest));
        Assert.AreEqual(exportedSecondTo, dirFromManifest, "the export must be written where the manifest reads it");
    }

    [TestMethod]
    public void ResolvePackageExports_OlderLayoutLeftovers_AreDeleted()
    {
        // Caches written by an older layout can never be read again, so upgrading must
        // not strand them on disk.
        string cacheDir = Path.Combine(_dir, "cache");
        PackageWithWinMd package = WritePackage("proj", "Contoso.Sdk", "1.0.0", "v1");

        string versionDir = Path.Combine(cacheDir, "packages", "Contoso.Sdk", "1.0.0");
        string oldLayoutTypes = Path.Combine(versionDir, "types");
        string oldFormatDir = Path.Combine(versionDir, PackageKeyName('a'));
        string otherSelection = Path.Combine(versionDir, PackageKeyName('b'));
        string exportInFlight = Path.Combine(versionDir, PackageKeyName('c'));
        Directory.CreateDirectory(oldLayoutTypes);
        Directory.CreateDirectory(oldFormatDir);
        Directory.CreateDirectory(otherSelection);
        Directory.CreateDirectory(exportInFlight);
        File.WriteAllText(Path.Combine(oldFormatDir, "meta.json"), MetaJson(ApiCachePaths.CacheFormatVersion - 1));
        File.WriteAllText(Path.Combine(otherSelection, "meta.json"), MetaJson(ApiCachePaths.CacheFormatVersion));

        var pendingExports = new Dictionary<string, PackageWithWinMd>(StringComparer.OrdinalIgnoreCase);
        var seenPackageDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int reused = 0;
        ApiCacheBuilder.ResolvePackageExports(
            [package], cacheDir, force: false, pendingExports, seenPackageDirs, ref reused, report: null);

        Assert.IsFalse(Directory.Exists(oldLayoutTypes), "pre-hash layout leftover should be removed");
        Assert.IsFalse(Directory.Exists(oldFormatDir), "older-format cache should be removed");

        // Another project's genuinely different asset selection is still current, and
        // deleting it would make the two projects evict each other on every refresh.
        Assert.IsTrue(Directory.Exists(otherSelection), "current-format sibling must be kept");

        // meta.json is written last, so a cache directory without one may be an export
        // another process is running right now — `find-api refresh` does not hold the
        // cache lock. Deleting it recursively would break that run mid-write.
        Assert.IsTrue(Directory.Exists(exportInFlight), "an export in flight must not be deleted");
    }

    [TestMethod]
    public void DropUnrecordedPackages_OneFailedVariant_LeavesTheOtherProjectIndexed()
    {
        // Two projects reference "Contoso.Sdk 1.0.0" but resolve it to different files,
        // so each gets its own cache directory. If project B's directory cannot even
        // receive its "incomplete" marker, only B's reference may be dropped: dropping
        // project A's too unindexes a package A exported successfully, and A then answers
        // "no such type" with no warning that its index is partial.
        string cacheDir = Path.Combine(_dir, "cache");
        PackageWithWinMd fromProjectA = WritePackage("a", "Contoso.Sdk", "1.0.0", "alpha");
        PackageWithWinMd fromProjectB = WritePackage("b", "Contoso.Sdk", "1.0.0", "beta-different-bytes");

        var pendingExports = new Dictionary<string, PackageWithWinMd>(StringComparer.OrdinalIgnoreCase);
        var seenPackageDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int reused = 0;
        ProjectPackageRef refA = ApiCacheBuilder.ResolvePackageExports(
            [fromProjectA], cacheDir, force: false, pendingExports, seenPackageDirs, ref reused, report: null).Single();
        ProjectPackageRef refB = ApiCacheBuilder.ResolvePackageExports(
            [fromProjectB], cacheDir, force: false, pendingExports, seenPackageDirs, ref reused, report: null).Single();
        Assert.AreNotEqual(refA.AssetPathKey, refB.AssetPathKey, "the two projects must resolve to different exports");

        List<(string Name, ProjectManifest Manifest)> manifests =
        [
            ("A", ManifestWith("A", refA)),
            ("B", ManifestWith("B", refB)),
        ];

        ApiCacheBuilder.DropUnrecordedPackages(
            manifests,
            [new ApiCacheBuilder.UnrecordedPackage(refB.Id, refB.Version, refB.AssetPathKey)]);

        Assert.HasCount(1, manifests[0].Manifest.Packages, "project A's successful export must stay indexed");
        Assert.AreEqual(refA.AssetPathKey, manifests[0].Manifest.Packages.Single().AssetPathKey);
        Assert.IsEmpty(manifests[1].Manifest.Packages, "project B's unrecordable export must be dropped");
    }

    private static ProjectManifest ManifestWith(string projectName, params ProjectPackageRef[] packages) => new()
    {
        ProjectName = projectName,
        ProjectDir = projectName,
        ProjectFile = projectName + ".csproj",
        Packages = [.. packages],
        GeneratedAt = DateTime.UtcNow.ToString("o"),
    };

    /// <summary>
    /// A directory name this cache layout mints: exactly as many hex characters as an
    /// asset-path fingerprint. Derived from the constant rather than spelled out, so a
    /// change to the fingerprint width does not turn these fixtures into leftovers from
    /// an older layout and quietly invert what the test asserts.
    /// </summary>
    private static string PackageKeyName(char hexDigit) => new(hexDigit, ApiCachePaths.ShortHashLength);
    /// <summary>A complete <c>meta.json</c> body recording <paramref name="format"/>.</summary>
    private static string MetaJson(int format) =>
        $$"""
        {"format":{{format}},"packageId":"Contoso.Sdk","version":"1.0.0","winMdFiles":[],
         "totalTypes":0,"totalMembers":0,"totalNamespaces":0,"generatedAt":"2026-01-01T00:00:00Z"}
        """;

    /// <summary>
    /// Writes a .winmd whose bytes and path are unique to <paramref name="folder"/>, so
    /// the fingerprint the builder derives from it is distinguishable.
    /// </summary>
    private PackageWithWinMd WritePackage(string folder, string id, string version, string content)
    {
        string dir = Path.Combine(_dir, folder);
        Directory.CreateDirectory(dir);
        string winmd = Path.Combine(dir, id + ".winmd");
        File.WriteAllText(winmd, content);
        return new PackageWithWinMd(id, version, [winmd], []);
    }

    #region winapp.yaml project discovery

    private string NewProjectDir(string name)
    {
        string dir = Path.Combine(_dir, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [TestMethod]
    public void DiscoverProjectFiles_FindsWinappYamlWhenThereIsNoMsBuildProject()
    {
        // An Electron app has no .csproj. Without this it discovers no project at all,
        // indexes nothing, and every query typed in it reports the API surface as absent.
        string dir = NewProjectDir("my-electron-app");
        File.WriteAllText(Path.Combine(dir, "winapp.yaml"), "packages: []");

        List<string> found = ApiCacheBuilder.DiscoverProjectFiles(dir, scan: false);

        Assert.HasCount(1, found);
        Assert.EndsWith("winapp.yaml", found[0]);
    }

    [TestMethod]
    public void DiscoverProjectFiles_PrefersTheMsBuildProjectOverWinappYaml()
    {
        // A .NET project may use winapp.yaml for its SDK packages. Its .csproj is the
        // more precise description of what it compiles against, and indexing both would
        // index the same directory twice under two names.
        string dir = NewProjectDir("dotnet-app");
        File.WriteAllText(Path.Combine(dir, "winapp.yaml"), "packages: []");
        File.WriteAllText(Path.Combine(dir, "App.csproj"), "<Project />");

        List<string> found = ApiCacheBuilder.DiscoverProjectFiles(dir, scan: false);

        Assert.HasCount(1, found);
        Assert.EndsWith("App.csproj", found[0]);
    }

    [TestMethod]
    public void DiscoverProjectFiles_Scan_PrefersTheMsBuildProjectOverWinappYaml()
    {
        string dir = NewProjectDir("scanned");
        string app = Path.Combine(dir, "app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "winapp.yaml"), "packages: []");
        File.WriteAllText(Path.Combine(app, "App.csproj"), "<Project />");

        List<string> found = ApiCacheBuilder.DiscoverProjectFiles(dir, scan: true);

        Assert.HasCount(1, found);
        Assert.EndsWith("App.csproj", found[0]);
    }

    [TestMethod]
    public void DiscoverProjectFiles_Scan_SkipsWinappYamlUnderNodeModules()
    {
        string dir = NewProjectDir("with-deps");
        string nested = Path.Combine(dir, "node_modules", "some-package");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "winapp.yaml"), "packages: []");
        File.WriteAllText(Path.Combine(dir, "winapp.yaml"), "packages: []");

        List<string> found = ApiCacheBuilder.DiscoverProjectFiles(dir, scan: true);

        Assert.HasCount(1, found);
        Assert.AreEqual(Path.Combine(dir, "winapp.yaml"), found[0]);
    }

    [TestMethod]
    public void DiscoverProjectFiles_Scan_SkipsBuildOutputAndDependencies()
    {
        // These trees are pruned before they are descended into, so nothing under
        // them can be discovered — a copied project left in 'bin' is not a project
        // the caller wrote.
        string dir = NewProjectDir("built");
        File.WriteAllText(Path.Combine(dir, "App.csproj"), "<Project />");
        foreach (string excluded in new[] { "bin", "obj", "node_modules" })
        {
            string nested = Path.Combine(dir, excluded, "nested", "deep");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "Copy.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(nested, "Copy.vcxproj"), "<Project />");
            File.WriteAllText(Path.Combine(nested, "winapp.yaml"), "packages: []");
        }

        List<string> found = ApiCacheBuilder.DiscoverProjectFiles(dir, scan: true);

        Assert.HasCount(1, found);
        Assert.AreEqual(Path.Combine(dir, "App.csproj"), found[0]);
    }

    [TestMethod]
    public void DiscoverProjectFiles_Scan_DoesNotFollowSymlinkedDirectories()
    {
        // A directory symlink committed to a repository can point anywhere, including an
        // SMB share; reading a project file from there authenticates to whatever host
        // answers. The scan must not descend through one, even though the redirection is
        // invisible in the path string.
        string dir = NewProjectDir("linked");
        File.WriteAllText(Path.Combine(dir, "App.csproj"), "<Project />");
        string outside = Path.Combine(Path.GetTempPath(), "winapp-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(outside, "Lib"));
        File.WriteAllText(Path.Combine(outside, "Lib", "Lib.csproj"), "<Project />");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(dir, "vendor"), outside);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                Assert.Inconclusive("Creating a symbolic link requires Developer Mode or elevation.");
                return;
            }

            List<string> found = ApiCacheBuilder.DiscoverProjectFiles(dir, scan: true);

            Assert.HasCount(1, found);
            Assert.AreEqual(Path.Combine(dir, "App.csproj"), found[0]);
        }
        finally
        {
            try
            {
                Directory.Delete(Path.Combine(dir, "vendor"));
                Directory.Delete(outside, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                // Best-effort cleanup.
            }
        }
    }

    [TestMethod]
    public void DiscoverProjectFiles_Scan_RootsResultsAtThePathItWasGiven()
    {
        // Callers compare these against paths they built from their own input, so the
        // scan must root results at the string it was handed rather than canonicalize
        // it. Directory.EnumerateFiles behaves this way and this must match it.
        string dir = NewProjectDir("as-given");
        File.WriteAllText(Path.Combine(dir, "App.csproj"), "<Project />");
        string indirect = Path.Combine(dir, "..", "as-given");

        List<string> found = ApiCacheBuilder.DiscoverProjectFiles(indirect, scan: true);

        CollectionAssert.AreEqual(
            Directory.GetFiles(indirect, "*.csproj", SearchOption.AllDirectories),
            found);
    }

    [TestMethod]
    public void DiscoverProjectFiles_Scan_IndexesTheDirectoryItWasPointedAt()
    {
        // The exclusion applies to directories the scan would descend into, not to
        // the one the caller named. Asking to scan a directory and getting nothing
        // back because of what it happens to be called is not a useful answer.
        string root = NewProjectDir("node_modules");
        File.WriteAllText(Path.Combine(root, "App.csproj"), "<Project />");

        List<string> found = ApiCacheBuilder.DiscoverProjectFiles(root, scan: true);

        Assert.HasCount(1, found);
        Assert.AreEqual(Path.Combine(root, "App.csproj"), found[0]);
    }

    [TestMethod]
    public void ProjectNameFor_WinappYaml_UsesTheDirectoryName()
    {
        // "winapp" is the file's own stem, so it would name every such project
        // identically. The directory is the app's name.
        string dir = NewProjectDir("my-electron-app");
        string projectFile = Path.Combine(dir, "winapp.yaml");

        Assert.AreEqual("my-electron-app", ApiCacheBuilder.ProjectNameFor(projectFile));
    }

    [TestMethod]
    public void ProjectNameFor_MsBuildProject_StillUsesTheFileName()
    {
        Assert.AreEqual("App", ApiCacheBuilder.ProjectNameFor(Path.Combine(_dir, "App.csproj")));
    }

    [TestMethod]
    public void FindProjectNameInDir_FindsAWinappYamlProject()
    {
        string dir = NewProjectDir("my-electron-app");
        File.WriteAllText(Path.Combine(dir, "winapp.yaml"), "packages: []");

        Assert.AreEqual("my-electron-app", ApiCacheBuilder.FindProjectNameInDir(dir));
    }

    #endregion

    #region solution membership

    [TestMethod]
    public void DiscoverProjectFiles_SolutionDir_IndexesOnlyTheProjectsTheSolutionLists()
    {
        // A solution directory holds no project of its own. Recursing the tree there also
        // picks up sibling projects the solution deliberately excludes, and the caller is
        // then told to disambiguate against a project their solution never builds.
        string root = NewProjectDir("sln-listed");
        string listed = Path.Combine(root, "src", "App");
        string excluded = Path.Combine(root, "tools", "Unrelated");
        Directory.CreateDirectory(listed);
        Directory.CreateDirectory(excluded);
        File.WriteAllText(Path.Combine(listed, "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(excluded, "Unrelated.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(root, "App.slnx"),
            """<Solution><Project Path="src/App/App.csproj" /></Solution>""");

        List<string> found = ApiCacheBuilder.DiscoverProjectFiles(root, scan: false);

        Assert.HasCount(1, found);
        Assert.EndsWith(Path.Combine("App", "App.csproj"), found[0]);
    }

    [TestMethod]
    public void DiscoverProjectFiles_ClassicSln_IndexesOnlyTheProjectsTheSolutionLists()
    {
        string root = NewProjectDir("sln-classic");
        string listed = Path.Combine(root, "src", "App");
        string excluded = Path.Combine(root, "tools", "Unrelated");
        Directory.CreateDirectory(listed);
        Directory.CreateDirectory(excluded);
        File.WriteAllText(Path.Combine(listed, "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(excluded, "Unrelated.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(root, "App.sln"),
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "src\App\App.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            """);

        List<string> found = ApiCacheBuilder.DiscoverProjectFiles(root, scan: false);

        Assert.HasCount(1, found);
        Assert.EndsWith(Path.Combine("App", "App.csproj"), found[0]);
    }

    [TestMethod]
    public void DiscoverProjectFiles_SolutionThatListsNothingReadable_FallsBackToScanning()
    {
        // An unparseable solution means membership is unknown, not that the solution
        // builds nothing. Indexing nothing there would report every API as absent.
        string root = NewProjectDir("sln-opaque");
        string app = Path.Combine(root, "src", "App");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(root, "App.slnx"), "not xml at all <<<");

        List<string> found = ApiCacheBuilder.DiscoverProjectFiles(root, scan: false);

        Assert.HasCount(1, found);
        Assert.EndsWith(Path.Combine("App", "App.csproj"), found[0]);
    }

    [TestMethod]
    public void DiscoverProjectFiles_SolutionListingAMissingProject_FallsBackToScanning()
    {
        // Every listed path is gone (a stale solution). Treating that as "builds nothing"
        // would leave the projects that are actually present unindexed.
        string root = NewProjectDir("sln-stale");
        string app = Path.Combine(root, "src", "App");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(root, "App.slnx"),
            """<Solution><Project Path="src/Gone/Gone.csproj" /></Solution>""");

        List<string> found = ApiCacheBuilder.DiscoverProjectFiles(root, scan: false);

        Assert.HasCount(1, found);
        Assert.EndsWith(Path.Combine("App", "App.csproj"), found[0]);
    }

    [TestMethod]
    public void DiscoverProjectFiles_ExplicitScan_StillWalksTheWholeTree()
    {
        // 'refresh --scan' is the caller explicitly asking for everything below a
        // directory, so solution membership must not narrow it.
        string root = NewProjectDir("sln-scan");
        string listed = Path.Combine(root, "src", "App");
        string excluded = Path.Combine(root, "tools", "Unrelated");
        Directory.CreateDirectory(listed);
        Directory.CreateDirectory(excluded);
        File.WriteAllText(Path.Combine(listed, "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(excluded, "Unrelated.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(root, "App.slnx"),
            """<Solution><Project Path="src/App/App.csproj" /></Solution>""");

        List<string> found = ApiCacheBuilder.DiscoverProjectFiles(root, scan: true);

        Assert.HasCount(2, found);
    }

    #endregion
}

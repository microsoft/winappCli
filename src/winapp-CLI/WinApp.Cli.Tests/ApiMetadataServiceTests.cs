// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Services;
using WinApp.Cli.Services.ApiSearch;

namespace WinApp.Cli.Tests;

/// <summary>
/// Covers <see cref="ApiMetadataService"/> project-manifest resolution and the
/// no-project error paths, without requiring a real package cache. Resolution
/// success is asserted indirectly: once a manifest resolves, the query reaches
/// the engine and returns a non-<see cref="ApiQueryOutcome.NoProject"/> outcome.
/// </summary>
[TestClass]
public sealed class ApiMetadataServiceTests
{
    private string _globalDir = null!;
    private string _currentDir = null!;
    private string _projectsDir = null!;

    [TestInitialize]
    public void Setup()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ApiMetadataServiceTests_{Guid.NewGuid():N}");
        _globalDir = Path.Combine(root, "global");
        _currentDir = Path.Combine(root, "current");
        Directory.CreateDirectory(_globalDir);
        Directory.CreateDirectory(_currentDir);
        _projectsDir = Path.Combine(_globalDir, "cache", "find-api", "projects");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(Directory.GetParent(_globalDir)!.FullName, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private ApiMetadataService CreateService(ISdkPackageSource? sdkPackages = null) => new(
        new FakeWinappDirectoryService(new DirectoryInfo(_globalDir)),
        new CurrentDirectoryProvider(_currentDir),
        sdkPackages ?? new FakeSdkPackageSource(),
        NullLogger<ApiMetadataService>.Instance);

    /// <summary>
    /// An SDK source with no packages: the SDK scope is unavailable, so resolution
    /// fails loudly instead of silently answering from some other project. Tests
    /// that need a working SDK scope pre-write its manifest with
    /// <see cref="WriteSdkManifest"/> rather than indexing the real machine.
    /// </summary>
    private sealed class FakeSdkPackageSource : ISdkPackageSource
    {
        public List<PackageWithWinMd> GetSdkPackages() => [];
    }

    private void WriteSdkManifest()
    {
        string cacheDir = Path.Combine(_globalDir, "cache", "find-api");
        Directory.CreateDirectory(cacheDir);
        var manifest = new ProjectManifest
        {
            ProjectName = ApiCachePaths.SdkScopeName,
            ProjectDir = string.Empty,
            ProjectFile = string.Empty,
            Packages = [new ProjectPackageRef { Id = "WindowsSDK", Version = "10.0.0.0", SourceStamp = "0a1b2c3d", AssetPathKey = "0a1b2c3d" }],
            GeneratedAt = DateTime.UtcNow.ToString("o"),
        };
        File.WriteAllText(
            ApiCachePaths.SdkManifestPath(cacheDir),
            JsonSerializer.Serialize(manifest, ApiSearchJsonContext.Default.ProjectManifest));
    }

    /// <summary>Creates a project file so the directory looks like a real project to resolution.</summary>
    private static void WriteProjectFile(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
    }

    private void WriteManifest(string name, string? projectDir = null, string? fileName = null, List<string>? caveats = null)
    {
        Directory.CreateDirectory(_projectsDir);
        var manifest = new ProjectManifest
        {
            ProjectName = name,
            ProjectDir = projectDir ?? Path.Combine(_currentDir, name),
            ProjectFile = name + ".csproj",
            Packages = [new ProjectPackageRef { Id = "Some.Pkg", Version = "1.0.0", SourceStamp = "0a1b2c3d", AssetPathKey = "0a1b2c3d" }],
            GeneratedAt = DateTime.UtcNow.ToString("o"),
            Caveats = caveats,
        };
        File.WriteAllText(Path.Combine(_projectsDir, (fileName ?? name) + ".json"), JsonSerializer.Serialize(manifest, ApiSearchJsonContext.Default.ProjectManifest));
    }

    /// <summary>
    /// Writes a package cache entry the staleness check will accept, so a manifest
    /// referencing this package reads as fresh rather than as never-indexed.
    /// </summary>
    private static void WritePackageCache(string cacheDir, ProjectPackageRef package)
    {
        Assert.IsTrue(ApiCachePaths.TryPackageCacheDir(cacheDir, package, out string dir));
        Directory.CreateDirectory(dir);
        var meta = new PackageMeta
        {
            Format = ApiCachePaths.CacheFormatVersion,
            PackageId = package.Id,
            Version = package.Version,
            SourceStamp = package.SourceStamp,
            WinMdFiles = [],
            TotalTypes = 0,
            TotalMembers = 0,
            TotalNamespaces = 0,
            GeneratedAt = DateTime.UtcNow.ToString("o"),
        };
        File.WriteAllText(
            Path.Combine(dir, "meta.json"),
            JsonSerializer.Serialize(meta, ApiSearchJsonContext.Default.PackageMeta));
    }

    /// <summary>
    /// Records whether the indexing work actually ran. An index pass always ends up
    /// asking for the SDK packages, so this is a precise witness for "did the pass
    /// happen" that does not depend on real packages being on the machine.
    /// </summary>
    private sealed class RecordingSdkPackageSource : ISdkPackageSource
    {
        public int Calls { get; private set; }

        public List<PackageWithWinMd> GetSdkPackages()
        {
            Calls++;
            return [];
        }
    }

    [TestMethod]
    public void RunIndexWithLock_LockReleasedWhileWaiting_StillIndexes()
    {
        // The cache directory is shared by every project, so a process holding the
        // lock is very likely indexing a *different* project. Waiting for it and then
        // returning leaves this caller's project unindexed, and the query goes on to
        // answer from a cache that was never asked to include it. After the wait, the
        // lock has to be taken and the index pass actually run.
        string cacheDir = Path.Combine(_globalDir, "cache", "find-api");
        Directory.CreateDirectory(cacheDir);
        string lockPath = Path.Combine(cacheDir, ".lock");

        var sdkPackages = new RecordingSdkPackageSource();
        var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var releaser = Task.Run(async () =>
        {
            // Long enough that the first acquisition attempt fails, short enough that
            // the retry loop (which polls once a second) picks it up promptly.
            await Task.Delay(1500);
            held.Dispose();
        });

        try
        {
            CreateService(sdkPackages).RunIndexWithLock(_currentDir, cacheDir);
        }
        finally
        {
            releaser.Wait();
            held.Dispose();
        }

        Assert.AreEqual(1, sdkPackages.Calls);
    }

    [TestMethod]
    public void LockTimedOutResult_StillStale_RefusesToAnswer()
    {
        // The lock is only waited on because the index was already stale. Falling
        // through after the wait answers confidently from metadata that does not
        // describe the project — the caller gets a wrong answer, not a slow one.
        string cacheDir = Path.Combine(_globalDir, "cache", "find-api");
        Directory.CreateDirectory(cacheDir);
        WriteProjectFile(_currentDir, "App");

        string? error = CreateService().LockTimedOutResult(_currentDir, cacheDir);

        Assert.IsNotNull(error);
        StringAssert.Contains(error, "out of date");
        StringAssert.Contains(error, "find-api refresh");
    }

    [TestMethod]
    public void LockTimedOutResult_OtherProcessMadeItFresh_Answers()
    {
        // The process that held the lock may have been indexing this very project.
        // Refusing then would be a spurious failure, so the staleness is re-checked
        // rather than the timeout being treated as failure on its own.
        string cacheDir = Path.Combine(_globalDir, "cache", "find-api");
        Directory.CreateDirectory(cacheDir);
        WriteProjectFile(_currentDir, "App");
        WriteManifest("App", _currentDir, "App_11111111");
        WritePackageCache(cacheDir, new ProjectPackageRef { Id = "Some.Pkg", Version = "1.0.0", SourceStamp = "0a1b2c3d", AssetPathKey = "0a1b2c3d" });

        Assert.IsNull(CreateService().LockTimedOutResult(_currentDir, cacheDir));
    }

    [TestMethod]
    public void Query_NoIndexAndNoSdk_ReportsSdkUnavailable()
    {
        // Nothing indexed and no SDK on the machine: the only honest answer is a
        // loud failure naming both remedies.
        var result = CreateService().Members("Some.Ns.Type", new ApiRequestScope(null, null));

        Assert.AreEqual(ApiQueryOutcome.NoProject, result.Outcome);
        StringAssert.Contains(result.Message, "no Windows SDK metadata is available");
    }

    [TestMethod]
    public void Query_ProjectlessDir_DoesNotAnswerFromUnrelatedLoneProject()
    {
        // Regression: a query from a directory with no project must NEVER be answered
        // from whichever project happens to be the only one cached. Before this, a
        // lone cached project silently answered — so the result depended on unrelated
        // global state, and nothing in the output revealed the substitution.
        WriteManifest("Alpha");

        var result = CreateService().Members("Some.Ns.Type", new ApiRequestScope(null, null));

        Assert.AreEqual(ApiQueryOutcome.NoProject, result.Outcome);
        StringAssert.Contains(result.Message, "no Windows SDK metadata is available");
    }

    [TestMethod]
    public void Query_ProjectlessDir_FallsBackToSdkScope()
    {
        WriteManifest("Alpha");
        WriteSdkManifest();

        var result = CreateService().Namespaces(null, new ApiRequestScope(null, null));

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.AreEqual(ApiScopeNames.Sdk, result.Data!.Scope, "a projectless directory must be answered by the SDK scope, and say so");
    }

    [TestMethod]
    public void Query_ProjectlessDir_FallsBackToSdkScope_EvenWithManyProjectsIndexed()
    {
        // The outcome must not change just because more projects were indexed at some
        // point — that was the old fallback's core flaw (1 project answered, 2 errored).
        WriteManifest("Alpha");
        WriteManifest("Beta");
        WriteSdkManifest();

        var result = CreateService().Namespaces(null, new ApiRequestScope(null, null));

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.AreEqual(ApiScopeNames.Sdk, result.Data!.Scope);
    }

    [TestMethod]
    public void Query_ProjectInCurrentDir_ResolvesThatProjectAndIsProjectScoped()
    {
        WriteProjectFile(_currentDir, "Alpha");
        WriteManifest("Alpha", _currentDir);
        WriteSdkManifest();

        var result = CreateService().Namespaces(null, new ApiRequestScope(null, null));

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.AreEqual(ApiScopeNames.Project, result.Data!.Scope, "a real project in the current directory wins over the SDK scope");
    }

    [TestMethod]
    public void Query_ProjectScope_StampsProjectIdentityOnEveryPayload()
    {
        // Project names are not unique across directories, so 'scope: project' alone
        // does not identify which index answered. Tooling auditing find-api usage must
        // be able to read the answering project off the payload instead of inferring
        // it from cache file timestamps.
        WriteProjectFile(_currentDir, "Alpha");
        WriteManifest("Alpha", _currentDir);
        WriteSdkManifest();

        var result = CreateService().Namespaces(null, new ApiRequestScope(null, null));

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.AreEqual("Alpha", result.Data!.ProjectName);
        Assert.AreEqual(_currentDir, result.Data.ProjectDir);
    }

    [TestMethod]
    public void Query_IndexWithCaveats_CarriesThemOntoTheAnswer()
    {
        // The build that discovers a caveat — "runtime metadata was excluded", "this
        // project has 4 Windows targets and one was chosen" — usually happens once, long
        // before the queries it qualifies, and often in a different process. Reporting it
        // only at refresh time means every cached answer afterwards is presented as
        // complete, including under --json where nothing else is printed.
        WriteProjectFile(_currentDir, "Alpha");
        WriteManifest("Alpha", _currentDir, caveats: SampleCaveats);
        WriteSdkManifest();

        var result = CreateService().Namespaces(null, new ApiRequestScope(null, null));

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        CollectionAssert.AreEqual(SampleCaveats, result.Data!.Caveats, "the answer must carry what the index could not cover");
    }

    private static readonly List<string> SampleCaveats = ["Runtime metadata was excluded; some APIs may be missing."];

    [TestMethod]
    public void Query_IndexWithCaveats_QualifiesAFailedLookupToo()
    {
        // "Type not found" is the answer a caller acts on hardest — an agent takes it as
        // proof the API does not exist and writes different code. A failure carries no
        // payload, so the caveat that would have said "runtime metadata was excluded" is
        // dropped exactly when it changes the meaning of the answer.
        WriteProjectFile(_currentDir, "Alpha");
        WriteManifest("Alpha", _currentDir, caveats: SampleCaveats);
        WriteSdkManifest();

        var result = CreateService().Members("Contoso.Missing", new ApiRequestScope(null, null));

        Assert.AreNotEqual(ApiQueryOutcome.Ok, result.Outcome, "the type is absent from the index");
        StringAssert.Contains(
            result.Message,
            SampleCaveats[0],
            "a not-found answer must say the index was incomplete");
    }

    [TestMethod]
    public void QueryBatch_IndexWithCaveats_QualifiesEachFailedLookup()
    {
        // Batch mode is what an agent uses to check several types at once, so a dropped
        // caveat there is dropped across every answer in the run.
        WriteProjectFile(_currentDir, "Alpha");
        WriteManifest("Alpha", _currentDir, caveats: SampleCaveats);
        WriteSdkManifest();

        var results = CreateService().MembersBatch(["Contoso.Missing"], new ApiRequestScope(null, null));

        Assert.AreEqual(1, results.Count);
        Assert.AreNotEqual(ApiQueryOutcome.Ok, results[0].Result.Outcome);
        StringAssert.Contains(results[0].Result.Message, SampleCaveats[0]);
    }

    [TestMethod]
    public void Query_SdkScope_ReportsSdkNameAndNoProjectDir()
    {
        // The SDK scope has no project directory; it must be null rather than the
        // empty string the manifest stores, so consumers can test it directly.
        WriteSdkManifest();

        var result = CreateService().Namespaces(null, new ApiRequestScope(null, null));

        Assert.AreEqual(ApiScopeNames.Sdk, result.Data!.Scope);
        Assert.AreEqual(ApiCachePaths.SdkScopeName, result.Data.ProjectName);
        Assert.IsNull(result.Data.ProjectDir);
    }

    [TestMethod]
    public void Query_ProjectInCurrentDir_NotIndexed_DoesNotSilentlyNarrowToSdk()
    {
        // The user is standing in a real project. Quietly answering from the SDK
        // scope would hide its NuGet packages and look like genuine no-match results,
        // so this must fail with the refresh instruction instead.
        WriteProjectFile(_currentDir, "Gamma");
        WriteManifest("Alpha");
        WriteSdkManifest();

        var result = CreateService().Namespaces(null, new ApiRequestScope(null, null));

        Assert.AreEqual(ApiQueryOutcome.NoProject, result.Outcome);
        StringAssert.Contains(result.Message, "find-api refresh");
    }

    [TestMethod]
    public void Query_SolutionDir_ResolvesTheSingleProjectItBuilds()
    {
        // A solution directory holds no project of its own, so resolution used to widen
        // to the SDK scope and drop every NuGet package the solution's project references.
        File.WriteAllText(Path.Combine(_currentDir, "App.sln"), string.Empty);
        WriteProjectFile(Path.Combine(_currentDir, "src", "Alpha"), "Alpha");
        WriteManifest("Alpha", Path.Combine(_currentDir, "src", "Alpha"));
        WriteSdkManifest();

        var result = CreateService().Namespaces(null, new ApiRequestScope(null, null));

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.AreEqual(ApiScopeNames.Project, result.Data!.Scope);
        Assert.AreEqual("Alpha", result.Data.ProjectName);
    }

    [TestMethod]
    public void Query_SolutionDir_WithSeveralProjects_AsksWhichOne()
    {
        // Projects in one solution reference different packages, so picking whichever
        // was enumerated first would make the answer depend on directory ordering.
        File.WriteAllText(Path.Combine(_currentDir, "App.slnx"), "<Solution />");
        WriteManifest("Alpha", Path.Combine(_currentDir, "src", "Alpha"));
        WriteManifest("Beta", Path.Combine(_currentDir, "src", "Beta"));
        WriteSdkManifest();

        var result = CreateService().Namespaces(null, new ApiRequestScope(null, null));

        Assert.AreEqual(ApiQueryOutcome.NoProject, result.Outcome);
        StringAssert.Contains(result.Message, "'Alpha'");
        StringAssert.Contains(result.Message, "'Beta'");
        StringAssert.Contains(result.Message, "--project <name>");
    }

    [TestMethod]
    public void Query_SolutionDir_IgnoresProjectsOutsideTheSolutionTree()
    {
        // Only the projects the solution actually contains may answer for it; an
        // unrelated indexed project elsewhere must not be substituted.
        File.WriteAllText(Path.Combine(_currentDir, "App.sln"), string.Empty);
        WriteManifest("Alpha", Path.Combine(Path.GetTempPath(), "SomewhereElse", "Alpha"));
        WriteSdkManifest();

        var result = CreateService().Namespaces(null, new ApiRequestScope(null, null));

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.AreEqual(ApiScopeNames.Sdk, result.Data!.Scope);
    }

    [TestMethod]
    public void Query_SolutionDirNamedByProjectDir_ResolvesTheSameAsTheCurrentDirectory()
    {
        // A directory must not mean two different things depending on whether it is the
        // current directory or was named with --project-dir; naming it explicitly used to
        // widen to the SDK scope and drop the solution's packages.
        string solutionDir = Path.Combine(Directory.GetParent(_currentDir)!.FullName, "sln");
        Directory.CreateDirectory(solutionDir);
        File.WriteAllText(Path.Combine(solutionDir, "App.sln"), string.Empty);
        WriteProjectFile(Path.Combine(solutionDir, "src", "Alpha"), "Alpha");
        WriteManifest("Alpha", Path.Combine(solutionDir, "src", "Alpha"));
        WriteSdkManifest();

        var result = CreateService().Namespaces(null, new ApiRequestScope(solutionDir, null));

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.AreEqual(ApiScopeNames.Project, result.Data!.Scope);
        Assert.AreEqual("Alpha", result.Data.ProjectName);
    }

    [TestMethod]
    public void Query_SdkProjectOption_SelectsSdkScopeExplicitly()
    {
        WriteProjectFile(_currentDir, "Alpha");
        WriteManifest("Alpha", _currentDir);
        WriteSdkManifest();

        var result = CreateService().Namespaces(null, new ApiRequestScope(null, "sdk"));

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.AreEqual(ApiScopeNames.Sdk, result.Data!.Scope, "--project sdk must select the SDK scope from inside a project");
    }

    [TestMethod]
    public void Query_MultipleProjects_ProjectInCurrentDir_IsNotAmbiguous()
    {
        WriteProjectFile(_currentDir, "Beta");
        WriteManifest("Alpha");
        WriteManifest("Beta", _currentDir);

        var result = CreateService().Namespaces(null, new ApiRequestScope(null, null));

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.AreEqual(ApiScopeNames.Project, result.Data!.Scope);
    }

    [TestMethod]
    public void Query_ProjectOption_SelectsNamedProject()
    {
        WriteManifest("Alpha");
        WriteManifest("Beta");

        var result = CreateService().Members("Some.Ns.Type", new ApiRequestScope(null, "Beta"));

        // Named project resolved -> engine ran (type missing), not a NoProject failure.
        Assert.AreEqual(ApiQueryOutcome.NotFound, result.Outcome);
    }

    [TestMethod]
    public void Query_UnknownProjectOption_ReportsNotIndexed()
    {
        WriteManifest("Alpha");

        var result = CreateService().Members("Some.Ns.Type", new ApiRequestScope(null, "DoesNotExist"));

        Assert.AreEqual(ApiQueryOutcome.NoProject, result.Outcome);
        StringAssert.Contains(result.Message, "not indexed");
    }

    [TestMethod]
    public void Query_ProjectOption_DoesNotMatchLongerProjectNameByPrefix()
    {
        // Manifests are stored as 'ProjectName_hash', so a filename-prefix match let
        // '--project App' resolve to a project actually named 'App_Tests' and answer
        // confidently from the wrong index.
        WriteManifest("App_Tests", Path.Combine(_currentDir, "App_Tests"), "App_Tests_ab12cd34");

        var result = CreateService().Members("Some.Ns.Type", new ApiRequestScope(null, "App"));

        Assert.AreEqual(ApiQueryOutcome.NoProject, result.Outcome);
        StringAssert.Contains(result.Message, "not indexed");
    }

    [TestMethod]
    public void Refresh_ProjectOption_DoesNotMatchLongerProjectNameByPrefix()
    {
        WriteManifest("App_Tests", Path.Combine(_currentDir, "App_Tests"), "App_Tests_ab12cd34");

        var result = CreateService().Refresh(new ApiRequestScope(null, "App"), scan: false);

        Assert.AreEqual(ApiQueryOutcome.InvalidInput, result.Outcome);
    }

    [TestMethod]
    public void Refresh_UnknownProjectName_FailsInsteadOfIndexingCurrentDirectory()
    {
        // 'refresh --project Typo' used to fall through to the current directory and
        // report success, so a mistyped name looked like a completed refresh of a
        // project that was never touched.
        WriteManifest("Alpha");
        WriteProjectFile(_currentDir, "Unrelated");

        var result = CreateService().Refresh(new ApiRequestScope(null, "DoesNotExist"), scan: false);

        Assert.AreEqual(ApiQueryOutcome.InvalidInput, result.Outcome);
        StringAssert.Contains(result.Message, "DoesNotExist");
    }

    [TestMethod]
    public void Refresh_NonexistentProjectDir_FailsInsteadOfIndexingTheSdk()
    {
        // Symmetric with the unknown-name case above. A mistyped --project-dir used to
        // index the machine-wide SDK and exit 0, so the refresh looked successful and
        // every later query answered from the SDK — reporting the project's own NuGet
        // APIs as not found, with nothing pointing at the typo.
        string missing = Path.Combine(_currentDir, "no-such-project-dir");

        var result = CreateService().Refresh(new ApiRequestScope(missing, null), scan: false);

        Assert.AreEqual(ApiQueryOutcome.InvalidInput, result.Outcome);
        StringAssert.Contains(result.Message, "no-such-project-dir");
    }

    [TestMethod]
    public void Refresh_AmbiguousProjectName_FailsInsteadOfPickingOne()
    {
        string dirA = Path.Combine(_currentDir, "a", "Dup");
        string dirB = Path.Combine(_currentDir, "b", "Dup");
        WriteManifest("Dup", dirA, "Dup_1");
        WriteManifest("Dup", dirB, "Dup_2");

        var result = CreateService().Refresh(new ApiRequestScope(null, "Dup"), scan: false);

        Assert.AreEqual(ApiQueryOutcome.InvalidInput, result.Outcome);
    }

    [TestMethod]
    public void Refresh_KnownProjectName_IsNotRejected()
    {
        WriteManifest("Alpha", Path.Combine(_currentDir, "Alpha"));
        WriteProjectFile(Path.Combine(_currentDir, "Alpha"), "Alpha");

        var result = CreateService().Refresh(new ApiRequestScope(null, "Alpha"), scan: false);

        Assert.AreNotEqual(ApiQueryOutcome.InvalidInput, result.Outcome,
            "A name that resolves to exactly one indexed project must still refresh.");
    }

    [TestMethod]
    public void Query_ExplicitProjectDir_ProjectlessDir_DoesNotFallBackToLoneProject()
    {
        // Regression (C2): an explicit --project-dir that matches no indexed project
        // must never silently answer for the unrelated lone cached project. The
        // directory holds no project, so the SDK scope answers — and with no SDK
        // available here, that surfaces as a loud failure rather than Alpha's data.
        WriteManifest("Alpha");
        string unrelated = Path.Combine(Directory.GetParent(_globalDir)!.FullName, "unrelated");
        Directory.CreateDirectory(unrelated);

        var result = CreateService().Members("Some.Ns.Type", new ApiRequestScope(unrelated, null));

        Assert.AreEqual(ApiQueryOutcome.NoProject, result.Outcome);
        StringAssert.Contains(result.Message, "no Windows SDK metadata is available");
    }

    [TestMethod]
    public void Query_ExplicitProjectDir_UnindexedProject_ReportsNotIndexed()
    {
        // A real but unindexed project was named explicitly: report it, rather than
        // narrowing to the SDK scope and hiding that project's NuGet packages.
        WriteManifest("Alpha");
        string other = Path.Combine(Directory.GetParent(_globalDir)!.FullName, "other");
        WriteProjectFile(other, "Delta");

        var result = CreateService().Members("Some.Ns.Type", new ApiRequestScope(other, null));

        Assert.AreEqual(ApiQueryOutcome.NoProject, result.Outcome);
        StringAssert.Contains(result.Message, "No indexed API metadata was found for");
    }

    [TestMethod]
    public void Query_ProjectInCurrentDir_DoesNotResolveSameNamedProjectElsewhere()
    {
        // Regression (H1): identically-named projects in different directories are a
        // normal situation (monorepos, a template scaffolded repeatedly). Standing in
        // one of them must never be answered from another one's index just because the
        // project names collide — that silently returns the wrong package set.
        WriteProjectFile(_currentDir, "App");
        string elsewhere = Path.Combine(Directory.GetParent(_globalDir)!.FullName, "elsewhere", "App");
        WriteManifest("App", elsewhere);
        WriteSdkManifest();

        var result = CreateService().Namespaces(null, new ApiRequestScope(null, null));

        Assert.AreEqual(ApiQueryOutcome.NoProject, result.Outcome);
        StringAssert.Contains(result.Message, "find-api refresh");
    }

    [TestMethod]
    public void Query_ExplicitProjectDir_DoesNotResolveSameNamedProjectElsewhere()
    {
        // Same collision, reached through --project-dir.
        string target = Path.Combine(Directory.GetParent(_globalDir)!.FullName, "target", "App");
        WriteProjectFile(target, "App");
        string elsewhere = Path.Combine(Directory.GetParent(_globalDir)!.FullName, "elsewhere", "App");
        WriteManifest("App", elsewhere);

        var result = CreateService().Members("Some.Ns.Type", new ApiRequestScope(target, null));

        Assert.AreEqual(ApiQueryOutcome.NoProject, result.Outcome);
        StringAssert.Contains(result.Message, "No indexed API metadata was found for");
    }

    [TestMethod]
    public void Query_ProjectOption_AmbiguousName_ReportsAmbiguityInsteadOfPickingOne()
    {
        // Two indexed projects share a name. Resolving to whichever was enumerated
        // first would make the answer depend on directory ordering, so this must ask
        // the caller to disambiguate.
        string root = Directory.GetParent(_globalDir)!.FullName;
        WriteManifest("App", Path.Combine(root, "one", "App"), "App_11111111");
        WriteManifest("App", Path.Combine(root, "two", "App"), "App_22222222");

        var result = CreateService().Members("Some.Ns.Type", new ApiRequestScope(null, "App"));

        Assert.AreEqual(ApiQueryOutcome.NoProject, result.Outcome);
        StringAssert.Contains(result.Message, "ambiguous");
        StringAssert.Contains(result.Message, "--project-dir");
    }

    [TestMethod]
    public void Query_TwoProjectsInOneDirectory_AsksWhichOne()
    {
        // A directory may legally hold two projects, and they reference different
        // packages. Answering from whichever manifest was enumerated first makes the
        // API surface depend on file ordering, with nothing telling the caller the
        // other project even exists.
        WriteProjectFile(_currentDir, "App");
        WriteProjectFile(_currentDir, "Other");
        WriteManifest("App", _currentDir, "App_11111111");
        WriteManifest("Other", _currentDir, "Other_22222222");

        var result = CreateService().Namespaces(null, new ApiRequestScope(null, null));

        Assert.AreEqual(ApiQueryOutcome.NoProject, result.Outcome);
        StringAssert.Contains(result.Message, "'App'");
        StringAssert.Contains(result.Message, "'Other'");
        StringAssert.Contains(result.Message, "--project <name>");
    }

    [TestMethod]
    public void Query_TwoProjectsInOneDirectory_NamedByProjectDir_AsksWhichOne()
    {
        // A directory must not mean two different things depending on whether it is the
        // current directory or was named with --project-dir.
        string dir = Path.Combine(Directory.GetParent(_currentDir)!.FullName, "pair");
        WriteProjectFile(dir, "App");
        WriteProjectFile(dir, "Other");
        WriteManifest("App", dir, "App_11111111");
        WriteManifest("Other", dir, "Other_22222222");

        var result = CreateService().Namespaces(null, new ApiRequestScope(dir, null));

        Assert.AreEqual(ApiQueryOutcome.NoProject, result.Outcome);
        StringAssert.Contains(result.Message, "'App'");
        StringAssert.Contains(result.Message, "'Other'");
    }

    [TestMethod]
    public void Query_TwoProjectsInOneDirectory_NamingOneResolvesIt()
    {
        // The ambiguity message tells the caller to pass '--project <name>', so that
        // must actually work from the same directory.
        WriteProjectFile(_currentDir, "App");
        WriteProjectFile(_currentDir, "Other");
        WriteManifest("App", _currentDir, "App_11111111");
        WriteManifest("Other", _currentDir, "Other_22222222");

        var result = CreateService().Namespaces(null, new ApiRequestScope(null, "Other"));

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.AreEqual("Other", result.Data!.ProjectName);
    }

    [TestMethod]
    public void Query_SolutionDir_IgnoresIndexedProjectsTheSolutionDoesNotList()
    {
        // A sibling directory may hold a project the solution deliberately excludes.
        // Counting it forces the caller to disambiguate against something their
        // solution never builds.
        File.WriteAllText(Path.Combine(_currentDir, "App.slnx"),
            """<Solution><Project Path="src/Alpha/Alpha.csproj" /></Solution>""");
        WriteProjectFile(Path.Combine(_currentDir, "src", "Alpha"), "Alpha");
        WriteProjectFile(Path.Combine(_currentDir, "tools", "Beta"), "Beta");
        WriteManifest("Alpha", Path.Combine(_currentDir, "src", "Alpha"));
        WriteManifest("Beta", Path.Combine(_currentDir, "tools", "Beta"));
        WriteSdkManifest();

        var result = CreateService().Namespaces(null, new ApiRequestScope(null, null));

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.AreEqual(ApiScopeNames.Project, result.Data!.Scope);
        Assert.AreEqual("Alpha", result.Data.ProjectName);
    }

    [TestMethod]
    public void Query_SolutionDir_TwoProjectsInOneDirectory_AnswersFromTheOneTheSolutionLists()
    {
        // Two projects can share a directory. Reducing solution membership to the
        // directory lets the excluded project pass the filter and collapses both into
        // one group, where enumeration order decides the answer — so a query can be
        // answered from a project the solution does not build. Alpha is the excluded
        // one and sorts first, so directory-keyed resolution answers with it.
        File.WriteAllText(Path.Combine(_currentDir, "App.slnx"),
            """<Solution><Project Path="src/Beta.csproj" /></Solution>""");
        string shared = Path.Combine(_currentDir, "src");
        WriteProjectFile(shared, "Alpha");
        WriteProjectFile(shared, "Beta");
        WriteManifest("Alpha", shared);
        WriteManifest("Beta", shared);
        WriteSdkManifest();

        var result = CreateService().Namespaces(null, new ApiRequestScope(null, null));

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.AreEqual(ApiScopeNames.Project, result.Data!.Scope);
        Assert.AreEqual("Beta", result.Data.ProjectName, "only the project the solution lists may answer");
    }

    [TestMethod]
    public void Query_SolutionDir_ManifestNamingNoProjectFile_DoesNotCrash()
    {
        // A manifest can name no project file — the SDK scope writes both parts empty,
        // and a truncated or hand-edited manifest can too. Building a project identity
        // from an empty directory and file name throws, which would take down every
        // query made from a solution directory.
        File.WriteAllText(Path.Combine(_currentDir, "App.slnx"),
            """<Solution><Project Path="src/Alpha/Alpha.csproj" /></Solution>""");
        WriteProjectFile(Path.Combine(_currentDir, "src", "Alpha"), "Alpha");
        WriteManifest("Alpha", Path.Combine(_currentDir, "src", "Alpha"));
        WriteManifest("Broken", projectDir: string.Empty, fileName: "Broken");
        WriteSdkManifest();

        var result = CreateService().Namespaces(null, new ApiRequestScope(null, null));

        Assert.AreEqual(ApiQueryOutcome.Ok, result.Outcome);
        Assert.AreEqual(ApiScopeNames.Project, result.Data!.Scope);
        Assert.AreEqual("Alpha", result.Data.ProjectName);
    }

    [TestMethod]
    public void ManifestName_SameProjectNameInDifferentDirs_ProducesDistinctNames()
    {
        // The cache key must include the project's path, otherwise the second project
        // indexed simply overwrites the first one's manifest.
        string a = ApiCacheBuilder.ManifestName(Path.Combine("C:", "one", "App.csproj"));
        string b = ApiCacheBuilder.ManifestName(Path.Combine("C:", "two", "App.csproj"));

        Assert.AreNotEqual(a, b);
        StringAssert.StartsWith(a, "App_");
        StringAssert.StartsWith(b, "App_");
    }
}

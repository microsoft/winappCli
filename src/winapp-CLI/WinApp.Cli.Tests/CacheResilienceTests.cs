// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services;
using WinApp.Cli.Services.ApiSearch;
using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class CacheResilienceTests
{
    private string _root = null!;
    private string _cwd = null!;
    private string _profile = null!;
    private WinappDirectoryService _directories = null!;
    private string Global => Path.Combine(_profile, ".winapp");
    private string Local => Path.Combine(_cwd, ".winapp", "cache");

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Directory.GetCurrentDirectory(), $"cache-resilience-{Guid.NewGuid():N}");
        _cwd = Path.Combine(_root, "project");
        _profile = Path.Combine(_root, "profile");
        Directory.CreateDirectory(_cwd);
        Directory.CreateDirectory(_profile);
        _directories = new WinappDirectoryService(new CurrentDirectoryProvider(_cwd))
        {
            UserProfileProvider = () => _profile,
            CacheOverrideProvider = () => null,
        };
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public void LocalDirectory_DoesNotWalkToParentOrCreateDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".winapp", "cache"));
        Assert.AreEqual(Local, _directories.GetLocalCacheDirectory().FullName);
        Assert.IsFalse(Directory.Exists(Path.Combine(_cwd, ".winapp")));
    }

    [TestMethod]
    public void CacheConsumers_ConstructWithoutResolvingOrProbingCache()
    {
        _directories.CacheOverrideProvider = () => throw new InvalidOperationException("must remain lazy");
        using var controls = new ControlsSearchService(_directories);
        _ = new ApiMetadataService(_directories, new CurrentDirectoryProvider(_cwd),
            new NoSdkDownloads(), NullLogger<ApiMetadataService>.Instance);
        _ = new MSStoreCLIService(_directories, NullLogger<MSStoreCLIService>.Instance);
        _ = new XamlTriageService(NullLogger<XamlTriageService>.Instance, _directories, null!);
        _ = new VcLibsPayloadAcquirer(_directories);
        _ = new RuntimeFrameworkResolver(null!, null!, _directories);
        Assert.IsFalse(Directory.Exists(Global));
        Assert.IsFalse(Directory.Exists(Local));
    }

    [TestMethod]
    public void LocalCacheProbe_DoesNotRequireAccessToAncestorsAboveCwd()
    {
        var probes = new List<string>();
        CacheStorage.InspectAncestors(Path.Combine(Local, "find-api"), path =>
        {
            probes.Add(path);
            if (path.Equals(_cwd, StringComparison.OrdinalIgnoreCase))
            {
                return FileAttributes.Directory;
            }
            if (!path.StartsWith(_cwd + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("Outside allowed working directory");
            }
            throw new DirectoryNotFoundException();
        });

        Assert.AreEqual(_cwd, probes[^1]);
        Assert.IsTrue(probes.All(path => path.Equals(_cwd, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(_cwd + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void TestOverrideInspection_DoesNotResolveConfigurationOrTouchStorage()
    {
        _directories.CacheOverrideProvider = () => throw new InvalidOperationException("must remain lazy");
        Assert.IsNull(_directories.CacheDirectoryOverrideForTesting);
        var expected = new DirectoryInfo(Path.Combine(_root, "test-override"));
        _directories.SetCacheDirectoryForTesting(expected);
        Assert.AreSame(expected, _directories.CacheDirectoryOverrideForTesting);
        Assert.IsFalse(expected.Exists);
        _directories.SetCacheDirectoryForTesting(null);
        Assert.IsNull(_directories.CacheDirectoryOverrideForTesting);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("relative-cache")]
    public void InvalidExplicitOverride_IsNotRedirected(string value)
    {
        _directories.CacheOverrideProvider = () => value;
        var cache = Storage();
        var error = Assert.Throws<InvalidOperationException>(() => cache.Run(path => path));
        StringAssert.Contains(error.Message, "WINAPP_CLI_CACHE_DIRECTORY");
        Assert.IsFalse(Directory.Exists(Local));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow(@"C:\outside")]
    [DataRow(@"C:outside")]
    [DataRow(@"\outside")]
    [DataRow(@"..\outside")]
    [DataRow(@"cache\..\outside")]
    [DataRow(@"cache\.. \outside")]
    [DataRow(@"cache\name:stream")]
    public void CacheSubdirectories_RejectRootedOrTraversingPathsBeforeStorageAccess(string path)
    {
        _directories.CacheOverrideProvider = () => throw new AssertFailedException("Invalid subpaths must not resolve storage.");

        var global = Assert.Throws<ArgumentException>(() => new CacheStorage(_directories, path, "test"));
        var local = Assert.Throws<ArgumentException>(() => new CacheStorage(_directories, Path.Join("cache", "test"), path));

        Assert.AreEqual("globalRelativePath", global.ParamName);
        Assert.AreEqual("localRelativePath", local.ParamName);
        Assert.IsFalse(Directory.Exists(Global));
        Assert.IsFalse(Directory.Exists(Local));
    }

    [TestMethod]
    public void DeniedAncestor_RetriesOnlyInsideInvocationDirectory_AndWarns()
    {
        using var warnings = new StringWriter();
        var cache = Storage(new StorageDiagnostics(warnings, json: true));
        var defaultProbes = 0;
        cache.InspectDirectory = path =>
        {
            if (path.StartsWith(_profile, StringComparison.OrdinalIgnoreCase))
            {
                defaultProbes++;
                throw new UnauthorizedAccessException("Denied profile ancestor");
            }
            CacheStorage.InspectAncestors(path);
        };
        var result = cache.Run(path =>
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "entry"), "value");
            return path;
        });
        Assert.AreEqual(Path.Combine(Local, "test"), result);
        Assert.AreEqual(result, cache.Run(path => path));
        Assert.AreEqual(1, defaultProbes, "A later operation must not retry the already inaccessible default.");
        Assert.IsFalse(Directory.Exists(Global));
        using var warning = JsonDocument.Parse(warnings.ToString());
        Assert.AreEqual("cache-fallback", warning.RootElement.GetProperty("warning").GetProperty("code").GetString());
        StringAssert.Contains(warning.RootElement.GetProperty("warning").GetProperty("message").GetString()!, result);
        Assert.AreEqual(Global, _directories.GetGlobalWinappDirectory().FullName);
    }

    [TestMethod]
    public void BothLocationsDenied_ReportsActionableFailure()
    {
        var cache = Storage();
        cache.InspectDirectory = _ => throw new UnauthorizedAccessException("denied");
        var error = Assert.Throws<IOException>(() => cache.Run(path => path));
        StringAssert.Contains(error.Message, "Neither the default");
        StringAssert.Contains(error.Message, "WINAPP_CLI_CACHE_DIRECTORY");
    }

    [TestMethod]
    public void ExplicitOverrideWriteDenied_DoesNotRedirect()
    {
        _directories.CacheOverrideProvider = () => Global;
        var cache = Storage();
        var calls = 0;
        var error = Assert.Throws<IOException>(() => cache.Run<string>(_ =>
        {
            calls++;
            throw new UnauthorizedAccessException("write denied");
        }));
        Assert.AreEqual(1, calls);
        StringAssert.Contains(error.Message, "configured WINAPP_CLI_CACHE_DIRECTORY");
        Assert.IsFalse(Directory.Exists(Local));
    }

    [TestMethod]
    public async Task CorruptOrSignatureFailure_DoesNotRetryAnotherCache()
    {
        var cache = Storage();
        var calls = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.RunAsync<string>(_ =>
        {
            calls++;
            throw new InvalidDataException("corrupt archive");
        }));
        Assert.Throws<InvalidOperationException>(() => cache.Run<string>(_ =>
        {
            calls++;
            throw new InvalidOperationException("invalid signature");
        }));
        Assert.AreEqual(2, calls);
        Assert.IsFalse(Directory.Exists(Local));
    }

    [TestMethod]
    public void LocalCache_RejectsEscapingLink()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(_cwd, ".winapp");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive($"Symbolic links unavailable: {ex.Message}");
        }
        try
        {
            Assert.Throws<IOException>(() => _directories.GetLocalCacheDirectory());
            Assert.IsFalse(Directory.Exists(Path.Combine(outside, "cache")));
        }
        finally { Directory.Delete(link); }
    }

    [TestMethod]
    public void LocalCache_RejectsLinkInFeatureSubtree()
    {
        Directory.CreateDirectory(Path.Combine(Local, "test"));
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(Local, "test", "nested");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive($"Symbolic links unavailable: {ex.Message}");
        }
        try
        {
            File.WriteAllText(Global, "blocks default cache");
            Assert.Throws<IOException>(() => Storage().Run(path => path));
        }
        finally { Directory.Delete(link); }
    }

    [TestMethod]
    public async Task Controls_ReadableWriteLockedWarmCache_DoesNotFetchOrWrite()
    {
        var provider = new TestProvider { Storage = Storage() };
        await provider.LoadAsync();
        var directory = Path.Combine(Global, "cache", "test", "test-provider");
        var locks = new List<FileStream>();
        try
        {
            foreach (var path in Directory.GetFiles(directory))
            {
                locks.Add(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read));
            }
            var data = await provider.LoadAsync();
            Assert.AreEqual(CorpusOrigin.Cache, data.Origin);
            Assert.AreEqual(1, provider.Fetches);
            Assert.IsFalse(Directory.Exists(Local));
        }
        finally
        {
            foreach (var file in locks)
            {
                file.Dispose();
            }
        }
    }

    [TestMethod]
    public async Task Controls_RefreshWithBothCachesBlocked_FailsInsteadOfReportingCached()
    {
        File.WriteAllText(Global, "blocked");
        File.WriteAllText(Path.Combine(_cwd, ".winapp"), "blocked");
        using var warnings = new StringWriter();
        var provider = new TestProvider { Storage = Storage(new StorageDiagnostics(warnings)) };
        var data = await provider.LoadAsync();
        Assert.AreEqual(CorpusOrigin.Network, data.Origin);
        StringAssert.Contains(warnings.ToString(), "continuing without caching");
        Assert.IsFalse(warnings.ToString().Contains("Using cache", StringComparison.Ordinal));
        await Assert.ThrowsAsync<IOException>(() => provider.LoadAsync(forceRefresh: true));
    }

    [TestMethod]
    public async Task Controls_ClearCache_ClearsBothRoots()
    {
        var provider = new TestProvider { Storage = Storage() };
        await provider.LoadAsync();
        var localProvider = Path.Combine(Local, "test", "test-provider");
        Directory.CreateDirectory(localProvider);
        File.WriteAllText(Path.Combine(localProvider, "entry"), "old");
        provider.ClearCache();
        Assert.IsFalse(Directory.Exists(localProvider));
        Assert.IsFalse(Directory.Exists(Path.Combine(Global, "cache", "test", "test-provider")));
    }

    [TestMethod]
    public async Task Controls_ClearDeniedGlobal_DoesNotClaimItClearedLocalOnly()
    {
        File.WriteAllText(Global, "blocked");
        var provider = new TestProvider { Storage = Storage() };
        await provider.LoadAsync();
        Assert.Throws<IOException>(provider.ClearCache);
        Assert.IsTrue(Directory.Exists(Path.Combine(Local, "test", "test-provider")));
    }

    [TestMethod]
    public async Task Store_RejectsUnsignedLocalToolWhenDefaultAncestorIsBlocked()
    {
        File.WriteAllText(Global, "blocked");
        var toolDir = Path.Combine(Local, "tools", "msstore");
        Directory.CreateDirectory(toolDir);
        var exe = Path.Combine(toolDir, "msstore.exe");
        File.WriteAllText(exe, "tool");
        using var held = File.Open(exe, FileMode.Open, FileAccess.Read, FileShare.Read);
        var service = new MSStoreCLIService(_directories, NullLogger<MSStoreCLIService>.Instance);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureMSStoreCLIAvailableAsync());
        StringAssert.Contains(error.Message, "not validly signed by Microsoft");
        Assert.Throws<InvalidOperationException>(() => service.GetMSStoreCLIPath());
    }

    [TestMethod]
    public void Api_UsesLocalSdkManifestWhenDefaultAncestorIsBlocked()
    {
        File.WriteAllText(Global, "blocked");
        var cacheDir = Path.Combine(Local, "find-api");
        Directory.CreateDirectory(cacheDir);
        var path = ApiCachePaths.SdkManifestPath(cacheDir);
        File.WriteAllText(path, JsonSerializer.Serialize(new ProjectManifest
        {
            ProjectName = ApiCachePaths.SdkScopeName,
            ProjectDir = "",
            ProjectFile = "",
            Packages = [],
            GeneratedAt = DateTime.UtcNow.ToString("o"),
        }, ApiSearchJsonContext.Default.ProjectManifest));
        using var held = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var service = new ApiMetadataService(_directories, new CurrentDirectoryProvider(_cwd),
            new NoSdkDownloads(), NullLogger<ApiMetadataService>.Instance);
        Assert.AreEqual(ApiQueryOutcome.Ok, service.Stats(new ApiRequestScope(null, "sdk")).Outcome);
    }

    [TestMethod]
    public void Api_RequiredIndexWriteInBothBlockedLocations_FailsClearly()
    {
        File.WriteAllText(Global, "blocked");
        File.WriteAllText(Path.Combine(_cwd, ".winapp"), "blocked");
        var service = new ApiMetadataService(_directories, new CurrentDirectoryProvider(_cwd),
            new NoSdkDownloads(), NullLogger<ApiMetadataService>.Instance);
        var error = Assert.Throws<IOException>(() => service.Refresh(new ApiRequestScope(null, "sdk"), scan: false));
        StringAssert.Contains(error.Message, "Neither the default");
    }

    [TestMethod]
    public void Api_StaleIndexAndBlockedCaches_NeverReturnsOldMetadata()
    {
        var cacheDir = Path.Combine(Global, "cache", "find-api");
        Directory.CreateDirectory(Path.Combine(cacheDir, "projects"));
        Directory.CreateDirectory(Path.Combine(cacheDir, ".lock"));
        File.WriteAllText(Path.Combine(_cwd, ".winapp"), "blocked fallback");
        var projectPath = Path.Combine(_cwd, "App.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var manifestPath = Path.Combine(cacheDir, "projects", ApiCacheBuilder.ManifestName(projectPath) + ".json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new ProjectManifest
        {
            ProjectName = "App", ProjectDir = _cwd, ProjectFile = "App.csproj", Packages = [],
            GeneratedAt = DateTime.UtcNow.AddDays(-1).ToString("o"),
        }, ApiSearchJsonContext.Default.ProjectManifest));
        File.SetLastWriteTimeUtc(manifestPath, DateTime.UtcNow.AddDays(-1));
        Directory.CreateDirectory(Path.Combine(_cwd, "obj"));
        File.WriteAllText(Path.Combine(_cwd, "obj", "project.assets.json"),
            "{\"version\":3,\"targets\":{},\"libraries\":{},\"project\":{\"restore\":{\"projectPath\":"
            + JsonSerializer.Serialize(projectPath) + "},\"frameworks\":{}}}");

        var service = new ApiMetadataService(_directories, new CurrentDirectoryProvider(_cwd),
            new NoSdkDownloads(), NullLogger<ApiMetadataService>.Instance);
        var error = Assert.Throws<IOException>(() => service.Stats(new ApiRequestScope(null, "App")));
        StringAssert.Contains(error.Message, "Neither the default");
    }

    [TestMethod]
    public async Task VcLibs_LocalFallbackStillChecksSignatureBeforePublishing()
    {
        File.WriteAllText(Global, "blocked");
        string? verified = null;
        var service = new VcLibsPayloadAcquirer(_directories)
        {
            CacheDirectories = () => [],
            Downloader = (_, _) => Task.FromResult(new byte[] { 1, 2, 3 }),
            SignatureVerifier = path => { verified = path; return false; },
        };
        var payload = await service.TryAcquireAsync(
            new RuntimePackageRequirement
            {
                Name = "Microsoft.VCLibs.140.00.UWPDesktop", MinVersion = "14.0.0.0",
                Architecture = "x64", Publisher = VcLibsPayloadAcquirer.MicrosoftPublisher,
            },
            new DirectoryInfo(_cwd),
            new TaskContext(new GroupableTask("cache-test", null), null, new TestConsole(), NullLogger.Instance, new Lock()),
            CancellationToken.None);
        Assert.IsNull(payload);
        Assert.IsNotNull(verified);
        StringAssert.StartsWith(verified, Path.Combine(Local, "framework-packages") + Path.DirectorySeparatorChar);
        Assert.IsEmpty(Directory.GetFiles(Local, "*.appx", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task XamlTriage_BothCachesUnavailable_SkipsWithWarning()
    {
        File.WriteAllText(Global, "blocked");
        File.WriteAllText(Path.Combine(_cwd, ".winapp"), "blocked");
        using var warnings = new StringWriter();
        var service = new XamlTriageService(NullLogger<XamlTriageService>.Instance,
            _directories, null!, new StorageDiagnostics(warnings));
        var result = await service.TryAnalyzeAsync("unused.dmp", useSymbols: false);
        Assert.AreEqual(XamlTriageOutcome.Skipped, result.Outcome);
        StringAssert.Contains(result.LogText!, "cache is unavailable");
        StringAssert.Contains(warnings.ToString(), "continuing without caching");
    }

    private CacheStorage Storage(IStorageDiagnostics? diagnostics = null) =>
        new(_directories, Path.Combine("cache", "test"), "test", diagnostics);

    private sealed class NoSdkDownloads : ISdkPackageSource
    {
        public List<PackageWithWinMd> GetSdkPackages() => [];
    }

    private sealed class TestProvider() : CachedProviderBase("")
    {
        public int Fetches { get; private set; }
        public override string Id => "test-provider";
        public override string DisplayName => "Test";
        protected override Task<ProviderData> FetchAsync(CancellationToken cancellationToken)
        {
            Fetches++;
            return Task.FromResult(new ProviderData(
                [new Scenario { Id = "test-button", ControlId = "button", ControlName = "Button", HeaderText = "Button", Source = Id }],
                new() { ["button"] = ["button"] }, new()));
        }
    }
}

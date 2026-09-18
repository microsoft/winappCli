// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Coverage-focused tests for <see cref="UpdateNotificationService"/> exercising the
/// network fetch success/error paths, the background first-run refresh, the install-channel
/// path heuristics, and the cache read/write failure catches. Network and process-path
/// boundaries are driven through the service's test seams so every branch is deterministic.
/// </summary>
[TestClass]
[DoNotParallelize] // Modifies environment variables and the static background-refresh guard
public class UpdateNotificationServiceCoverageTests : BaseCommandTests
{
    private UpdateNotificationService _service = null!;
    private string? _savedCaller;
    private string? _savedUpdateCheck;

    private static readonly string[] CiVarNames =
    [
        "CI", "GITHUB_ACTIONS", "TF_BUILD", "APPVEYOR", "TRAVIS", "CIRCLECI",
        "TEAMCITY_VERSION", "JB_SPACE_API_URL",
        "CODEBUILD_BUILD_ID", "AWS_REGION", "BUILD_ID", "BUILD_URL", "PROJECT_ID"
    ];
    private Dictionary<string, string?> _savedCiVars = [];

    [TestInitialize]
    public void Setup()
    {
        _service = (UpdateNotificationService)GetRequiredService<IUpdateNotificationService>();
        _service.SkipBackgroundRefreshForTesting = true;
        _service.NotificationConsole = TestAnsiConsole;

        _savedCaller = Environment.GetEnvironmentVariable("WINAPP_CLI_CALLER");
        _savedUpdateCheck = Environment.GetEnvironmentVariable("WINAPP_CLI_UPDATE_CHECK");
        _savedCiVars = CiVarNames.ToDictionary(name => name, name => Environment.GetEnvironmentVariable(name));

        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", null);
        Environment.SetEnvironmentVariable("WINAPP_CLI_UPDATE_CHECK", null);
        foreach (var name in CiVarNames)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", _savedCaller);
        Environment.SetEnvironmentVariable("WINAPP_CLI_UPDATE_CHECK", _savedUpdateCheck);
        foreach (var (name, value) in _savedCiVars)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static HttpClient FakeGitHub(HttpStatusCode status, string content)
    {
        var handler = new FakeHttpMessageHandler().WhenUriContains("releases/latest", status, content);
        return new HttpClient(handler);
    }

    [TestMethod]
    public void CheckAndNotify_BlockedBookkeeping_DoesNotStartNetworkRefresh()
    {
        Directory.CreateDirectory(Path.Join(_testCacheDirectory.FullName, ".update-check"));
        using var error = new StringWriter();
        var handler = new FakeHttpMessageHandler().WhenUriContains(
            "releases/latest", HttpStatusCode.OK, """{"tag_name":"v0.0.1"}""");
        using var client = new HttpClient(handler);
        var service = new UpdateNotificationService(
            GetRequiredService<IWinappDirectoryService>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UpdateNotificationService>.Instance,
            new StorageDiagnostics(error))
        {
            Http = client,
        };

        service.CheckAndNotify();

        Assert.AreEqual(0, handler.Requests.Count);
        StringAssert.Contains(error.ToString(), "bookkeeping");
        Assert.IsFalse(Directory.EnumerateFiles(_testCacheDirectory.FullName, "*.tmp").Any());
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_OnSuccess_ReturnsParsedVersion()
    {
        _service.Http = FakeGitHub(HttpStatusCode.OK, """{"tag_name":"v42.7.0"}""");

        var version = await _service.GetLatestVersionAsync(TestContext.CancellationToken);

        Assert.AreEqual("42.7.0", version);
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_OnHttpError_ReturnsNull()
    {
        // A non-success status makes EnsureSuccessStatusCode throw, which the generic catch swallows.
        _service.Http = FakeGitHub(HttpStatusCode.InternalServerError, "");

        var version = await _service.GetLatestVersionAsync(TestContext.CancellationToken);

        Assert.IsNull(version);
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_WhenCancelled_Rethrows()
    {
        _service.Http = FakeGitHub(HttpStatusCode.OK, """{"tag_name":"1.0.0"}""");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await _service.GetLatestVersionAsync(cts.Token));
    }

    [TestMethod]
    public async Task RefreshCacheAsync_OnSuccess_PopulatesCacheWithVersion()
    {
        // Directly exercise the refresh + write path: fetch the version and persist it.
        _service.Http = FakeGitHub(HttpStatusCode.OK, """{"tag_name":"v0.0.1"}""");
        var cacheFile = new FileInfo(Path.Combine(_testCacheDirectory.FullName, ".update-check"));

        await _service.RefreshCacheAsync(cacheFile);

        var cache = UpdateNotificationService.ReadCache(cacheFile);
        Assert.AreEqual("0.0.1", cache.LatestVersion, "Refresh should persist the fetched version.");
        Assert.IsNotNull(cache.LastCheck, "Refresh should record a check timestamp.");
    }

    [TestMethod]
    public async Task CheckAndNotify_FirstRun_SchedulesBackgroundRefresh()
    {
        // No cache file exists yet, and background refresh is enabled: CheckAndNotify should write
        // a placeholder synchronously and kick off the fire-and-forget refresh (which hits the network).
        _service.SkipBackgroundRefreshForTesting = false;
        var handler = new FakeHttpMessageHandler().WhenUriContains("releases/latest", HttpStatusCode.OK, """{"tag_name":"v0.0.1"}""");
        _service.Http = new HttpClient(handler);

        var cacheFile = new FileInfo(Path.Combine(_testCacheDirectory.FullName, ".update-check"));
        Assert.IsFalse(cacheFile.Exists, "Precondition: no cache file for first run.");

        _service.CheckAndNotify();

        // A placeholder must be written synchronously before the background task is dispatched.
        cacheFile.Refresh();
        Assert.IsTrue(cacheFile.Exists, "First run should write a placeholder cache file synchronously.");
        var placeholder = UpdateNotificationService.ReadCache(cacheFile);
        Assert.IsNotNull(placeholder.LastCheck, "Placeholder should carry a LastCheck timestamp.");

        // The background refresh runs GetLatestVersionAsync; poll deterministically for that request.
        var sw = Stopwatch.StartNew();
        while (handler.Requests.Count == 0 && sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            await Task.Delay(25, TestContext.CancellationToken);
        }

        Assert.IsTrue(handler.Requests.Count >= 1,
            "The scheduled background refresh should have issued a GitHub release request.");
    }

    [TestMethod]
    public async Task CheckAndNotify_FirstRun_BackgroundRefreshFailure_IsSwallowed()
    {
        // Simulate an HTTP timeout (TaskCanceledException : OperationCanceledException) during the
        // background refresh. GetLatestVersionAsync rethrows it, RefreshCacheAsync propagates it, and
        // the fire-and-forget task's catch must swallow it so the process never crashes.
        _service.SkipBackgroundRefreshForTesting = false;
        var handler = new FakeHttpMessageHandler()
            .When(_ => true, _ => throw new TaskCanceledException("simulated timeout"));
        _service.Http = new HttpClient(handler);

        var cacheFile = new FileInfo(Path.Combine(_testCacheDirectory.FullName, ".update-check"));

        // Must not throw despite the background failure.
        _service.CheckAndNotify();

        var sw = Stopwatch.StartNew();
        while (handler.Requests.Count == 0 && sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            await Task.Delay(25, TestContext.CancellationToken);
        }

        Assert.IsTrue(handler.Requests.Count >= 1,
            "The background refresh should have attempted the request before failing.");

        // The refresh failed, so only the synchronous placeholder remains: a timestamp but no version.
        cacheFile.Refresh();
        Assert.IsTrue(cacheFile.Exists, "The synchronous placeholder should still exist after a failed refresh.");
        var cache = UpdateNotificationService.ReadCache(cacheFile);
        Assert.IsNotNull(cache.LastCheck, "Placeholder timestamp should survive a failed refresh.");
        Assert.AreEqual("", cache.LatestVersion, "A failed refresh must not populate a version.");
    }

    [TestMethod]
    public void DetectInstallChannel_WithNodeModulesPath_ReturnsNpm()
    {
        _service.ProcessPathProvider = () => @"C:\project\node_modules\.bin\winapp.exe";

        Assert.AreEqual(InstallChannel.Npm, _service.DetectInstallChannel());
    }

    [TestMethod]
    public void DetectInstallChannel_WithNugetPath_ReturnsNuGet()
    {
        _service.ProcessPathProvider = () => @"C:\Users\me\.nuget\packages\winappcli\1.0.0\winapp.exe";

        Assert.AreEqual(InstallChannel.NuGet, _service.DetectInstallChannel());
    }

    [TestMethod]
    public void DetectInstallChannel_WithStandalonePath_ReturnsStandaloneExe()
    {
        _service.ProcessPathProvider = () => @"C:\tools\winapp\winapp.exe";

        Assert.AreEqual(InstallChannel.StandaloneExe, _service.DetectInstallChannel());
    }

    [TestMethod]
    public void DetectInstallChannel_WithNullProcessPath_ReturnsStandaloneExe()
    {
        _service.ProcessPathProvider = () => null;

        Assert.AreEqual(InstallChannel.StandaloneExe, _service.DetectInstallChannel());
    }

    [TestMethod]
    public void ReadCache_WhenFileUnreadable_ReturnsEmpty()
    {
        var path = Path.Combine(_testCacheDirectory.FullName, ".update-check-locked");
        File.WriteAllText(path, $"{DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)}\n1.2.3\n");
        var cacheFile = new FileInfo(path);

        // Hold an exclusive lock so File.ReadAllLines throws while the file still Exists.
        using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var cache = UpdateNotificationService.ReadCache(cacheFile);

        Assert.AreEqual(UpdateNotificationService.UpdateCheckCache.Empty, cache);
    }

    [TestMethod]
    public async Task RefreshCacheAsync_WhenCacheWriteFails_DoesNotThrow()
    {
        _service.Http = FakeGitHub(HttpStatusCode.OK, """{"tag_name":"1.0.0"}""");

        // Point the cache at a path whose parent is a FILE, so Directory.Create() inside
        // WriteCacheFile throws and must be swallowed.
        var blocker = Path.Combine(_testCacheDirectory.FullName, "blocker-file");
        File.WriteAllText(blocker, "x");
        var cacheFile = new FileInfo(Path.Combine(blocker, ".update-check"));

        await _service.RefreshCacheAsync(cacheFile);

        cacheFile.Refresh();
        Assert.IsFalse(cacheFile.Exists, "The failed cache write should leave no file behind.");
    }
}

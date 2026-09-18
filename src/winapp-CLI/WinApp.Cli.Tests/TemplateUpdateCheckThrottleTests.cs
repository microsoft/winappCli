// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Coverage for <see cref="TemplateUpdateCheckThrottle"/> — the once-a-day gate that stops
/// <c>winapp new</c> from hitting the NuGet feed on every invocation to check the WinUI template pack.
/// </summary>
[TestClass]
public class TemplateUpdateCheckThrottleTests
{
    private DirectoryInfo _globalDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _globalDir = new DirectoryInfo(Path.Join(Path.GetTempPath(), $"TplThrottle_{Guid.NewGuid():N}"));
        _globalDir.Create();
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            _globalDir.Delete(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // best-effort cleanup
        }
    }

    private TemplateUpdateCheckThrottle CreateThrottle(Func<DateTimeOffset>? clock = null) =>
        new(new FakeWinappDirectoryService(_globalDir), NullLogger<TemplateUpdateCheckThrottle>.Instance)
        {
            UtcNowProvider = clock ?? (() => DateTimeOffset.UtcNow),
        };

    [TestMethod]
    public void CanCheckForUpdates_UnavailableBookkeeping_SkipsCheckWithWarning()
    {
        Directory.CreateDirectory(Path.Join(_globalDir.FullName, ".template-update-check"));
        using var error = new StringWriter();
        var throttle = new TemplateUpdateCheckThrottle(
            new FakeWinappDirectoryService(_globalDir),
            NullLogger<TemplateUpdateCheckThrottle>.Instance,
            new StorageDiagnostics(error));

        Assert.IsFalse(throttle.CanCheckForUpdates());
        StringAssert.Contains(error.ToString(), "Skipping the automatic template update check");
    }

    [TestMethod]
    public void CanCheckForUpdates_ReadableCache_IsNotTruncated()
    {
        var throttle = CreateThrottle();
        throttle.Record("1.0.0", "1.2.0");
        var path = Path.Join(_globalDir.FullName, ".template-update-check");
        var before = File.ReadAllText(path);

        Assert.IsTrue(throttle.CanCheckForUpdates());
        Assert.AreEqual(before, File.ReadAllText(path));
    }

    [TestMethod]
    public void CanCheckForUpdates_NoCache_DoesNotPublishAnEmptyResult()
    {
        Assert.IsTrue(CreateThrottle().CanCheckForUpdates());
        Assert.AreEqual(0, Directory.GetFiles(_globalDir.FullName).Length);
    }

    [TestMethod]
    public void TryGetRecentLatest_NoCache_ReturnsFalse()
    {
        var throttle = CreateThrottle();

        var recent = throttle.TryGetRecentLatest("1.0.0", out var latest);

        Assert.IsFalse(recent, "With no cache file a check must be due.");
        Assert.IsNull(latest);
    }

    [TestMethod]
    public void Record_ThenTryGetRecentLatest_SameVersion_ReturnsCachedLatest()
    {
        var throttle = CreateThrottle();
        throttle.Record("1.0.0", "1.2.0");

        var recent = throttle.TryGetRecentLatest("1.0.0", out var latest);

        Assert.IsTrue(recent, "A check recorded just now must be considered recent.");
        Assert.AreEqual("1.2.0", latest);
    }

    [TestMethod]
    public void Record_UpToDate_ReturnsRecentWithNullLatest()
    {
        var throttle = CreateThrottle();
        // A null/empty latest means "no update available as of the last check".
        throttle.Record("1.0.0", null);

        var recent = throttle.TryGetRecentLatest("1.0.0", out var latest);

        Assert.IsTrue(recent);
        Assert.IsNull(latest, "Up-to-date must be reused as a null latest, not an empty string.");
    }

    [TestMethod]
    public void TryGetRecentLatest_OlderThanADay_ReturnsFalse()
    {
        var recordedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var writer = CreateThrottle(() => recordedAt);
        writer.Record("1.0.0", "1.2.0");

        // Read 25 hours later: the cached check has expired, so a fresh check is due.
        var reader = CreateThrottle(() => recordedAt.AddHours(25));
        var recent = reader.TryGetRecentLatest("1.0.0", out var latest);

        Assert.IsFalse(recent, "A check older than the interval must expire.");
        Assert.IsNull(latest);
    }

    [TestMethod]
    public void TryGetRecentLatest_DifferentInstalledVersion_ReturnsFalse()
    {
        var throttle = CreateThrottle();
        throttle.Record("1.0.0", "1.2.0");

        // The pack was updated since the last check, so the cached "latest" no longer applies.
        var recent = throttle.TryGetRecentLatest("1.2.0", out var latest);

        Assert.IsFalse(recent, "A different installed version must invalidate the cached check.");
        Assert.IsNull(latest);
    }

    [TestMethod]
    public void Record_WritesHiddenCacheFile()
    {
        var throttle = CreateThrottle();
        throttle.Record("1.0.0", "1.2.0");

        var cacheFile = new FileInfo(Path.Join(_globalDir.FullName, ".template-update-check"));
        cacheFile.Refresh();

        Assert.IsTrue(cacheFile.Exists, "Recording a check must persist the cache file.");
        Assert.IsTrue(cacheFile.Attributes.HasFlag(FileAttributes.Hidden), "Cache file must be hidden.");
    }
}

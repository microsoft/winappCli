// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Coverage for <see cref="FirstRunService"/> — the one-time welcome/telemetry
/// notice gated by a hidden marker file in the global winapp directory.
/// </summary>
[TestClass]
public class FirstRunServiceTests
{
    private DirectoryInfo _tempDir = null!;
    private DirectoryInfo _globalDir = null!;

    private FirstRunService CreateService(CapturingLogger<FirstRunService> logger, TextWriter? error = null)
    {
        // WinappDirectoryService.SetCacheDirectoryForTesting overrides the value
        // returned by GetGlobalWinappDirectory, letting us point the marker file
        // at a throwaway directory instead of the real ~/.winapp.
        var dirService = new WinappDirectoryService(new CurrentDirectoryProvider(_tempDir.FullName));
        dirService.SetCacheDirectoryForTesting(_globalDir);
        return new FirstRunService(dirService, logger, new StorageDiagnostics(error ?? new StringWriter()));
    }

    [TestInitialize]
    public void Setup()
    {
        _tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"FirstRun_{Guid.NewGuid():N}"));
        _tempDir.Create();
        _globalDir = _tempDir.CreateSubdirectory("global");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            _tempDir.Delete(true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [TestMethod]
    public void CheckAndDisplayFirstRunNotice_FreshGlobalDir_ShowsNoticeAndWritesHiddenMarker()
    {
        var logger = new CapturingLogger<FirstRunService>();
        var service = CreateService(logger);

        var result = service.CheckAndDisplayFirstRunNotice();

        Assert.IsTrue(result, "First run must be reported the first time.");

        var marker = new FileInfo(Path.Combine(_globalDir.FullName, ".first-run-complete"));
        marker.Refresh();
        Assert.IsTrue(marker.Exists, "Marker file must be created so the notice is shown only once.");
        Assert.IsTrue(marker.Attributes.HasFlag(FileAttributes.Hidden), "Marker file must be hidden.");

        // The privacy/telemetry notice must actually be emitted, not silently skipped.
        Assert.IsTrue(
            logger.Has(LogLevel.Information, "anonymous usage data"),
            "Expected the telemetry disclosure to be logged on first run.");
    }

    [TestMethod]
    public void CheckAndDisplayFirstRunNotice_MarkerAlreadyExists_ReturnsFalseAndStaysSilent()
    {
        // Pre-create the marker: subsequent runs must be silent.
        File.WriteAllText(Path.Combine(_globalDir.FullName, ".first-run-complete"), string.Empty);

        var logger = new CapturingLogger<FirstRunService>();
        var service = CreateService(logger);

        var result = service.CheckAndDisplayFirstRunNotice();

        Assert.IsFalse(result, "Marker present => not a first run.");
        Assert.IsFalse(
            logger.Has(LogLevel.Information, "anonymous usage data"),
            "The notice must not be shown once the marker exists.");
    }

    [TestMethod]
    public void InvalidCacheConfiguration_DoesNotThrowBeforeTheCommandRuns()
    {
        var directories = new WinappDirectoryService(new CurrentDirectoryProvider(_tempDir.FullName))
        {
            CacheOverrideProvider = () => "relative-cache",
        };
        using var error = new StringWriter();
        var logger = new CapturingLogger<FirstRunService>();
        var service = new FirstRunService(directories, logger, new StorageDiagnostics(error));

        Assert.IsFalse(service.CheckAndDisplayFirstRunNotice());
        StringAssert.Contains(error.ToString(), "First-run bookkeeping is unavailable");
        Assert.IsFalse(logger.Has(LogLevel.Information, "anonymous usage data"));
    }

    [TestMethod]
    public void CheckAndDisplayFirstRunNotice_MarkerPathBlockedByDirectory_WarnsOnErrorStream()
    {
        // Create a *directory* where the marker *file* is expected. FileInfo.Exists is
        // false for a directory, so the first-run branch runs, but File.Create then
        // fails — exercising the catch/LogWarning path without any real corruption.
        Directory.CreateDirectory(Path.Combine(_globalDir.FullName, ".first-run-complete"));

        var logger = new CapturingLogger<FirstRunService>();
        using var error = new StringWriter();
        var service = CreateService(logger, error);

        var result = service.CheckAndDisplayFirstRunNotice();

        Assert.IsTrue(result, "Notice is still considered shown even if the marker can't be persisted.");
        StringAssert.Contains(error.ToString(), "Cannot save the first-run marker");
        Assert.IsFalse(logger.Has(LogLevel.Warning, "marker"),
            "Storage diagnostics must not use the logger's stdout warning channel.");
    }
}

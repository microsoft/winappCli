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

    private FirstRunService CreateService(CapturingLogger<FirstRunService> logger)
    {
        // WinappDirectoryService.SetCacheDirectoryForTesting overrides the value
        // returned by GetGlobalWinappDirectory, letting us point the marker file
        // at a throwaway directory instead of the real ~/.winapp.
        var dirService = new WinappDirectoryService(new CurrentDirectoryProvider(_tempDir.FullName));
        dirService.SetCacheDirectoryForTesting(_globalDir);
        return new FirstRunService(dirService, logger);
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
    public void CheckAndDisplayFirstRunNotice_MarkerCannotBeSaved_WritesShortNoticeToStderrOnly()
    {
        // Create a *directory* where the marker *file* is expected. FileInfo.Exists is
        // false for a directory, so the first-run branch runs, but File.Create then
        // fails — the same outcome as a global winapp directory that denies writes.
        Directory.CreateDirectory(Path.Combine(_globalDir.FullName, ".first-run-complete"));

        var logger = new CapturingLogger<FirstRunService>();
        var service = CreateService(logger);
        var stderr = new StringWriter();
        service.UnsavedNoticeWriter = stderr;

        var result = service.CheckAndDisplayFirstRunNotice();

        Assert.IsFalse(result, "The banner was not shown, so the no-args path still shows it.");
        StringAssert.Contains(stderr.ToString(), "anonymous usage data");
        StringAssert.Contains(stderr.ToString(), "WINAPP_CLI_TELEMETRY_OPTOUT=1");
        Assert.IsFalse(
            logger.Has(LogLevel.Information, "anonymous usage data"),
            "The full notice goes to stdout via the logger; it must not be shown when the marker can't be saved.");
        Assert.IsFalse(logger.Has(LogLevel.Warning, ""), "An unwritable global directory is not worth a warning on every run.");

        // Without the marker, the next run shows the short notice again.
        var secondStderr = new StringWriter();
        service.UnsavedNoticeWriter = secondStderr;
        Assert.IsFalse(service.CheckAndDisplayFirstRunNotice());
        StringAssert.Contains(secondStderr.ToString(), "anonymous usage data");
    }
}

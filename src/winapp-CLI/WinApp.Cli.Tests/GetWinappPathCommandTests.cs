// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class GetWinappPathCommandTests : BaseCommandTests
{
    [TestInitialize]
    public void SetupGetWinappPathTests()
    {
        // The default TestConsole width (80) wraps long Windows paths and breaks substring
        // assertions. Use a wide profile so the path lands on a single line.
        TestAnsiConsole.Profile.Width = 500;
    }

    /// <summary>
    /// Override the global cache to point at the real ~/.winapp. This ensures
    /// <c>GetLocalWinappDirectory</c>'s walk-up treats that path (if present on the dev
    /// machine) as the global cache and skips it, so tests behave the same on dev and CI.
    /// </summary>
    private DirectoryInfo PointGlobalAtUserProfileWinapp()
    {
        var userProfileWinapp = new DirectoryInfo(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".winapp"));
        GetRequiredService<IWinappDirectoryService>().SetCacheDirectoryForTesting(userProfileWinapp);
        return userProfileWinapp;
    }

    [TestMethod]
    public async Task GetWinappPath_LocalDirectoryExists_PrintsLocalPath()
    {
        // BaseCommandTests.SetupBase already creates _testWinappDirectory at _tempDirectory/.winapp
        PointGlobalAtUserProfileWinapp();
        var command = GetRequiredService<GetWinappPathCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, _testWinappDirectory.FullName,
            "Should print the resolved local .winapp path when it exists.");
    }

    [TestMethod]
    public async Task GetWinappPath_LocalDirectoryMissing_FallsBackToGlobal_WithWarning()
    {
        // Arrange — remove the local .winapp the base setup created so the resolver returns a
        // non-existent path (the #475 bug repro).
        _testWinappDirectory.Delete(recursive: true);
        var globalDir = PointGlobalAtUserProfileWinapp();

        var command = GetRequiredService<GetWinappPathCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        // Assert
        Assert.AreEqual(0, exitCode, "Missing local .winapp should not be a fatal error; the global cache is a sensible fallback.");

        var stdout = TestAnsiConsole.Output;
        StringAssert.Contains(stdout, globalDir.FullName,
            "Should fall back to the global .winapp path (#475) instead of returning a non-existent local path.");

        var nonExistentLocal = Path.Combine(_tempDirectory.FullName, ".winapp");
        Assert.IsFalse(File.Exists(nonExistentLocal) || Directory.Exists(nonExistentLocal),
            "Precondition check: the local .winapp should not exist for this test.");
        Assert.IsFalse(stdout.Contains(nonExistentLocal),
            "Must not print the non-existent <cwd>/.winapp path that scripts can't use (#475).");

        // The warning must go to stderr so `path = $(winapp get-winapp-path)` in scripts
        // captures only the path. Verifying both sides keeps stdout script-friendly.
        var stderr = ConsoleStdErr.ToString();
        StringAssert.Contains(stderr, "No local .winapp directory found",
            "Should warn (on stderr) that no local .winapp directory was found before falling back.");
        StringAssert.Contains(stderr, globalDir.FullName,
            "Warning should mention the global cache path being used as the fallback.");
        Assert.IsFalse(stdout.Contains("No local .winapp directory found"),
            "Warning text must not pollute stdout — scripts capturing stdout should get only the path.");
    }

    [TestMethod]
    public async Task GetWinappPath_GlobalPathIsFile_FailsWithoutPrintingAFallback()
    {
        var path = Path.Join(_tempDirectory.FullName, "not-a-directory");
        File.WriteAllText(path, "sentinel");
        GetRequiredService<IWinappDirectoryService>().SetCacheDirectoryForTesting(new DirectoryInfo(path));

        var exitCode = await ParseAndInvokeWithCaptureAsync(GetRequiredService<GetWinappPathCommand>(), ["--global"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(string.Empty, TestAnsiConsole.Output);
        StringAssert.Contains(ConsoleStdErr.ToString(), "not a directory");
        Assert.AreEqual("sentinel", File.ReadAllText(path));
    }

    [TestMethod]
    public async Task GetWinappPath_GlobalDirectoryMissing_ReturnsErrorExitCode()
    {
        // Arrange — point the global directory at a path that doesn't exist.
        var directoryService = GetRequiredService<IWinappDirectoryService>();
        var nonExistentGlobal = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "no-such-global"));
        directoryService.SetCacheDirectoryForTesting(nonExistentGlobal);

        var command = GetRequiredService<GetWinappPathCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--global"]);

        // Assert — explicit --global request for a missing directory should still fail loudly.
        Assert.AreNotEqual(0, exitCode,
            "An explicit --global request for a missing directory should return a non-zero exit code.");
    }
}

// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class WinappDirectoryServiceTests :  BaseCommandTests
{
    public WinappDirectoryServiceTests()
        : base(configPaths: false)
    {
    }

    [TestMethod]
    public void GetGlobalWinappDirectory_WithoutOverride_ReturnsDefaultDirectory()
    {
        // Act - Create a fresh instance without override to test default behavior
        var directoryService = GetRequiredService<IWinappDirectoryService>();
        var result = directoryService.GetGlobalWinappDirectory();

        // Assert
        var expectedDefaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".winapp");
        Assert.AreEqual(expectedDefaultPath, result.FullName);
    }

    [TestMethod]
    public void GetGlobalWinappDirectory_WithCustomDirectory_ReturnsCustomDirectory()
    {
        // Arrange - Create an alternate test directory
        var customDirectory = _tempDirectory.CreateSubdirectory("custom-winapp");

        // Act - Use SetCacheDirectoryForTesting to override the directory
        var directoryService = GetRequiredService<IWinappDirectoryService>();
        directoryService.SetCacheDirectoryForTesting(customDirectory);
        var result = directoryService.GetGlobalWinappDirectory();

        // Assert
        Assert.AreEqual(customDirectory.FullName, result.FullName);
    }

    [TestMethod]
    public void GetGlobalWinappDirectory_WithInstanceOverride_ReturnsOverrideDirectory()
    {
        // Act - Test instance override using SetCacheDirectoryForTesting
        var directoryService = GetRequiredService<IWinappDirectoryService>();
        directoryService.SetCacheDirectoryForTesting(_testWinappDirectory);
        var result = directoryService.GetGlobalWinappDirectory();

        // Assert
        Assert.AreEqual(_testWinappDirectory.FullName, result.FullName);
    }

    [TestMethod]
    public void SetCacheDirectoryForTesting_CanBeChangedMultipleTimes()
    {
        // Arrange - Create multiple test directories
        var firstPath = _tempDirectory.CreateSubdirectory("first-path");
        var secondPath = _tempDirectory.CreateSubdirectory("second-path");

        // Act & Assert - Test that override can be changed
        var directoryService = GetRequiredService<IWinappDirectoryService>();

        directoryService.SetCacheDirectoryForTesting(firstPath);
        var firstResult = directoryService.GetGlobalWinappDirectory();
        Assert.AreEqual(firstPath.FullName, firstResult.FullName, "First override should be returned");

        directoryService.SetCacheDirectoryForTesting(secondPath);
        var secondResult = directoryService.GetGlobalWinappDirectory();
        Assert.AreEqual(secondPath.FullName, secondResult.FullName, "Second override should replace the first");
    }

    [TestMethod]
    [DoNotParallelize] // Prevent parallel execution due to environment variable usage
    public void GetGlobalWinappDirectory_WithEnvironmentVariable_ReturnsEnvironmentVariablePath()
    {
        // Store original environment variable value to restore later
        var originalValue = Environment.GetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY");
        
        // Arrange - Create a test directory for environment variable
        var envTestDirectory = _tempDirectory.CreateSubdirectory("env-test-winapp");

        try
        {
            // Set environment variable
            Environment.SetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY", envTestDirectory.FullName);

            // Act - Create fresh instance to test environment variable behavior
            var directoryService = GetRequiredService<IWinappDirectoryService>();
            var result = directoryService.GetGlobalWinappDirectory();
            
            // Assert
            Assert.AreEqual(envTestDirectory.FullName, result?.FullName, "Should return environment variable path when set");
        }
        finally
        {
            // Cleanup - Restore original environment variable value
            Environment.SetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY", originalValue);
        }
    }

    [TestMethod]
    [DoNotParallelize] // Prevent parallel execution due to environment variable usage
    public void SetCacheDirectoryForTesting_TakesPrecedenceOverEnvironmentVariable()
    {
        // Store original environment variable value to restore later
        var originalValue = Environment.GetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY");
        
        // Arrange - Create test directories
        var envTestDirectory = _tempDirectory.CreateSubdirectory("env-winapp");
        var overrideTestDirectory = _tempDirectory.CreateSubdirectory("override-winapp");

        try
        {
            // Set environment variable
            Environment.SetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY", envTestDirectory.FullName);
            
            // Act - Create instance and set override (should take precedence)
            var directoryService = GetRequiredService<IWinappDirectoryService>();
            directoryService.SetCacheDirectoryForTesting(overrideTestDirectory);
            var result = directoryService.GetGlobalWinappDirectory();
            
            // Assert
            Assert.AreEqual(overrideTestDirectory.FullName, result?.FullName, "Instance override should take precedence over environment variable");
        }
        finally
        {
            // Cleanup - Restore original environment variable value
            Environment.SetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY", originalValue);
        }
    }

    [TestMethod]
    public void GetLocalWinappDirectory_WithExistingWinappDirectory_ReturnsExistingPath()
    {
        // Arrange - Create a .winapp directory in the temp directory
        var localWinappDir = _tempDirectory.CreateSubdirectory(".winapp");

        // Act
        var directoryService = GetRequiredService<IWinappDirectoryService>();
        var result = directoryService.GetLocalWinappDirectory(_tempDirectory);

        // Assert
        Assert.AreEqual(localWinappDir.FullName, result.FullName);
    }

    [TestMethod]
    public void GetLocalWinappDirectory_WithoutExistingDirectory_ReturnsPathInBaseDirectory()
    {
        // Act - No existing .winapp directory in temp directory
        var directoryService = GetRequiredService<IWinappDirectoryService>();
        var result = directoryService.GetLocalWinappDirectory(_tempDirectory);

        // Assert
        var expectedPath = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, ".winapp"));
        Assert.AreEqual(expectedPath.FullName, result.FullName);
    }

    [TestMethod]
    public void GetLocalWinappDirectory_WithNullBaseDirectory_UsesCurrentDirectory()
    {
        // Act
        var directoryService = GetRequiredService<IWinappDirectoryService>();
        var result = directoryService.GetLocalWinappDirectory(null);

        // Assert
        var expectedPath = new DirectoryInfo(Path.Combine(GetRequiredService<ICurrentDirectoryProvider>().GetCurrentDirectory(), ".winapp"));
        Assert.AreEqual(expectedPath.FullName, result.FullName);
    }
}

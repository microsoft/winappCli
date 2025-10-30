// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Winsdk.Cli.Services;

namespace Winsdk.Cli.Tests;

[TestClass]
public class WinsdkDirectoryServiceTests :  BaseCommandTests
{
    public WinsdkDirectoryServiceTests()
        : base(configPaths: false)
    {
    }

    [TestMethod]
    public void GetGlobalWinsdkDirectory_WithoutOverride_ReturnsDefaultDirectory()
    {
        // Act - Create a fresh instance without override to test default behavior
        var directoryService = GetRequiredService<IWinsdkDirectoryService>();
        var result = directoryService.GetGlobalWinsdkDirectory();

        // Assert
        var expectedDefaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".winsdk");
        Assert.AreEqual(expectedDefaultPath, result.FullName);
    }

    [TestMethod]
    public void GetGlobalWinsdkDirectory_WithCustomDirectory_ReturnsCustomDirectory()
    {
        // Arrange - Create an alternate test directory
        var customDirectory = _tempDirectory.CreateSubdirectory("custom-winsdk");

        // Act - Use SetCacheDirectoryForTesting to override the directory
        var directoryService = GetRequiredService<IWinsdkDirectoryService>();
        directoryService.SetCacheDirectoryForTesting(customDirectory);
        var result = directoryService.GetGlobalWinsdkDirectory();

        // Assert
        Assert.AreEqual(customDirectory.FullName, result.FullName);
    }

    [TestMethod]
    public void GetGlobalWinsdkDirectory_WithInstanceOverride_ReturnsOverrideDirectory()
    {
        // Act - Test instance override using SetCacheDirectoryForTesting
        var directoryService = GetRequiredService<IWinsdkDirectoryService>();
        directoryService.SetCacheDirectoryForTesting(_testWinsdkDirectory);
        var result = directoryService.GetGlobalWinsdkDirectory();

        // Assert
        Assert.AreEqual(_testWinsdkDirectory.FullName, result.FullName);
    }

    [TestMethod]
    public void SetCacheDirectoryForTesting_CanBeChangedMultipleTimes()
    {
        // Arrange - Create multiple test directories
        var firstPath = _tempDirectory.CreateSubdirectory("first-path");
        var secondPath = _tempDirectory.CreateSubdirectory("second-path");

        // Act & Assert - Test that override can be changed
        var directoryService = GetRequiredService<IWinsdkDirectoryService>();

        directoryService.SetCacheDirectoryForTesting(firstPath);
        var firstResult = directoryService.GetGlobalWinsdkDirectory();
        Assert.AreEqual(firstPath.FullName, firstResult.FullName, "First override should be returned");

        directoryService.SetCacheDirectoryForTesting(secondPath);
        var secondResult = directoryService.GetGlobalWinsdkDirectory();
        Assert.AreEqual(secondPath.FullName, secondResult.FullName, "Second override should replace the first");
    }

    [TestMethod]
    [DoNotParallelize] // Prevent parallel execution due to environment variable usage
    public void GetGlobalWinsdkDirectory_WithEnvironmentVariable_ReturnsEnvironmentVariablePath()
    {
        // Store original environment variable value to restore later
        var originalValue = Environment.GetEnvironmentVariable("WINSDK_CACHE_DIRECTORY");
        
        // Arrange - Create a test directory for environment variable
        var envTestDirectory = _tempDirectory.CreateSubdirectory("env-test-winsdk");

        try
        {
            // Set environment variable
            Environment.SetEnvironmentVariable("WINSDK_CACHE_DIRECTORY", envTestDirectory.FullName);
            
            // Act - Create fresh instance to test environment variable behavior
            var directoryService = GetRequiredService<IWinsdkDirectoryService>();
            var result = directoryService.GetGlobalWinsdkDirectory();
            
            // Assert
            Assert.AreEqual(envTestDirectory.FullName, result?.FullName, "Should return environment variable path when set");
        }
        finally
        {
            // Cleanup - Restore original environment variable value
            Environment.SetEnvironmentVariable("WINSDK_CACHE_DIRECTORY", originalValue);
        }
    }

    [TestMethod]
    [DoNotParallelize] // Prevent parallel execution due to environment variable usage
    public void SetCacheDirectoryForTesting_TakesPrecedenceOverEnvironmentVariable()
    {
        // Store original environment variable value to restore later
        var originalValue = Environment.GetEnvironmentVariable("WINSDK_CACHE_DIRECTORY");
        
        // Arrange - Create test directories
        var envTestDirectory = _tempDirectory.CreateSubdirectory("env-winsdk");
        var overrideTestDirectory = _tempDirectory.CreateSubdirectory("override-winsdk");

        try
        {
            // Set environment variable
            Environment.SetEnvironmentVariable("WINSDK_CACHE_DIRECTORY", envTestDirectory.FullName);
            
            // Act - Create instance and set override (should take precedence)
            var directoryService = GetRequiredService<IWinsdkDirectoryService>();
            directoryService.SetCacheDirectoryForTesting(overrideTestDirectory);
            var result = directoryService.GetGlobalWinsdkDirectory();
            
            // Assert
            Assert.AreEqual(overrideTestDirectory.FullName, result?.FullName, "Instance override should take precedence over environment variable");
        }
        finally
        {
            // Cleanup - Restore original environment variable value
            Environment.SetEnvironmentVariable("WINSDK_CACHE_DIRECTORY", originalValue);
        }
    }

    [TestMethod]
    public void GetLocalWinsdkDirectory_WithExistingWinsdkDirectory_ReturnsExistingPath()
    {
        // Arrange - Create a .winsdk directory in the temp directory
        var localWinsdkDir = _tempDirectory.CreateSubdirectory(".winsdk");

        // Act
        var directoryService = GetRequiredService<IWinsdkDirectoryService>();
        var result = directoryService.GetLocalWinsdkDirectory(_tempDirectory);

        // Assert
        Assert.AreEqual(localWinsdkDir.FullName, result.FullName);
    }

    [TestMethod]
    public void GetLocalWinsdkDirectory_WithoutExistingDirectory_ReturnsPathInBaseDirectory()
    {
        // Act - No existing .winsdk directory in temp directory
        var directoryService = GetRequiredService<IWinsdkDirectoryService>();
        var result = directoryService.GetLocalWinsdkDirectory(_tempDirectory);

        // Assert
        var expectedPath = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, ".winsdk"));
        Assert.AreEqual(expectedPath.FullName, result.FullName);
    }

    [TestMethod]
    public void GetLocalWinsdkDirectory_WithNullBaseDirectory_UsesCurrentDirectory()
    {
        // Act
        var directoryService = GetRequiredService<IWinsdkDirectoryService>();
        var result = directoryService.GetLocalWinsdkDirectory(null);

        // Assert
        var expectedPath = new DirectoryInfo(Path.Combine(GetRequiredService<ICurrentDirectoryProvider>().GetCurrentDirectory(), ".winsdk"));
        Assert.AreEqual(expectedPath.FullName, result.FullName);
    }
}

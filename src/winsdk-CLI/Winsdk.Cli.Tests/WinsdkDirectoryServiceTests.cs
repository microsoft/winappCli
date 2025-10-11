using Winsdk.Cli.Services;

namespace Winsdk.Cli.Tests;

[TestClass]
public class WinsdkDirectoryServiceTests :  BaseCommandTests
{
    private string _tempDirectory = null!;
    private string _testWinsdkDirectory = null!;

    [TestInitialize]
    public void Setup()
    {
        // Create a temp directory for each test to isolate them
        _tempDirectory = Path.Combine(Path.GetTempPath(), "WinsdkDirectoryServiceTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDirectory);

        // Set up a temporary winsdk directory for testing (isolates tests from real winsdk directory)
        _testWinsdkDirectory = Path.Combine(_tempDirectory, ".winsdk");
        Directory.CreateDirectory(_testWinsdkDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        // Clean up temporary files and directories
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [TestMethod]
    public void GetGlobalWinsdkDirectory_WithoutOverride_ReturnsDefaultDirectory()
    {
        // Act - Create a fresh instance without override to test default behavior
        var directoryService = GetRequiredService<IWinsdkDirectoryService>();
        var result = directoryService.GetGlobalWinsdkDirectory();

        // Assert
        var expectedDefaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".winsdk");
        Assert.AreEqual(expectedDefaultPath, result);
    }

    [TestMethod]
    public void GetGlobalWinsdkDirectory_WithCustomDirectory_ReturnsCustomDirectory()
    {
        // Arrange - Create an alternate test directory
        var customDirectory = Path.Combine(_tempDirectory, "custom-winsdk");
        Directory.CreateDirectory(customDirectory);

        // Act - Use SetCacheDirectoryForTesting to override the directory
        var directoryService = GetRequiredService<IWinsdkDirectoryService>();
        directoryService.SetCacheDirectoryForTesting(customDirectory);
        var result = directoryService.GetGlobalWinsdkDirectory();

        // Assert
        Assert.AreEqual(customDirectory, result);
    }

    [TestMethod]
    public void GetGlobalWinsdkDirectory_WithInstanceOverride_ReturnsOverrideDirectory()
    {
        // Act - Test instance override using SetCacheDirectoryForTesting
        var directoryService = GetRequiredService<IWinsdkDirectoryService>();
        directoryService.SetCacheDirectoryForTesting(_testWinsdkDirectory);
        var result = directoryService.GetGlobalWinsdkDirectory();

        // Assert
        Assert.AreEqual(_testWinsdkDirectory, result);
    }

    [TestMethod]
    public void SetCacheDirectoryForTesting_CanBeChangedMultipleTimes()
    {
        // Arrange - Create multiple test directories
        var firstPath = Path.Combine(_tempDirectory, "first-path");
        var secondPath = Path.Combine(_tempDirectory, "second-path");
        Directory.CreateDirectory(firstPath);
        Directory.CreateDirectory(secondPath);

        // Act & Assert - Test that override can be changed
        var directoryService = GetRequiredService<IWinsdkDirectoryService>();

        directoryService.SetCacheDirectoryForTesting(firstPath);
        var firstResult = directoryService.GetGlobalWinsdkDirectory();
        Assert.AreEqual(firstPath, firstResult, "First override should be returned");

        directoryService.SetCacheDirectoryForTesting(secondPath);
        var secondResult = directoryService.GetGlobalWinsdkDirectory();
        Assert.AreEqual(secondPath, secondResult, "Second override should replace the first");
    }

    [TestMethod]
    [DoNotParallelize] // Prevent parallel execution due to environment variable usage
    public void GetGlobalWinsdkDirectory_WithEnvironmentVariable_ReturnsEnvironmentVariablePath()
    {
        // Store original environment variable value to restore later
        var originalValue = Environment.GetEnvironmentVariable("WINSDK_CACHE_DIRECTORY");
        
        // Arrange - Create a test directory for environment variable
        var envTestDirectory = Path.Combine(_tempDirectory, "env-test-winsdk");
        Directory.CreateDirectory(envTestDirectory);
        
        try
        {
            // Set environment variable
            Environment.SetEnvironmentVariable("WINSDK_CACHE_DIRECTORY", envTestDirectory);
            
            // Act - Create fresh instance to test environment variable behavior
            var directoryService = GetRequiredService<IWinsdkDirectoryService>();
            var result = directoryService.GetGlobalWinsdkDirectory();
            
            // Assert
            Assert.AreEqual(envTestDirectory, result, "Should return environment variable path when set");
        }
        finally
        {
            // Cleanup - Restore original environment variable value
            Environment.SetEnvironmentVariable("WINSDK_CACHE_DIRECTORY", originalValue);
        }
    }

    [TestMethod]
    public void SetCacheDirectoryForTesting_TakesPrecedenceOverEnvironmentVariable()
    {
        // Store original environment variable value to restore later
        var originalValue = Environment.GetEnvironmentVariable("WINSDK_CACHE_DIRECTORY");
        
        // Arrange - Create test directories
        var envTestDirectory = Path.Combine(_tempDirectory, "env-winsdk");
        var overrideTestDirectory = Path.Combine(_tempDirectory, "override-winsdk");
        Directory.CreateDirectory(envTestDirectory);
        Directory.CreateDirectory(overrideTestDirectory);
        
        try
        {
            // Set environment variable
            Environment.SetEnvironmentVariable("WINSDK_CACHE_DIRECTORY", envTestDirectory);
            
            // Act - Create instance and set override (should take precedence)
            var directoryService = GetRequiredService<IWinsdkDirectoryService>();
            directoryService.SetCacheDirectoryForTesting(overrideTestDirectory);
            var result = directoryService.GetGlobalWinsdkDirectory();
            
            // Assert
            Assert.AreEqual(overrideTestDirectory, result, "Instance override should take precedence over environment variable");
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
        var localWinsdkDir = Path.Combine(_tempDirectory, ".winsdk");
        Directory.CreateDirectory(localWinsdkDir);

        // Act
        var directoryService = GetRequiredService<IWinsdkDirectoryService>();
        var result = directoryService.GetLocalWinsdkDirectory(_tempDirectory);

        // Assert
        Assert.AreEqual(localWinsdkDir, result);
    }

    [TestMethod]
    public void GetLocalWinsdkDirectory_WithoutExistingDirectory_ReturnsPathInBaseDirectory()
    {
        // Act - No existing .winsdk directory in temp directory
        var directoryService = GetRequiredService<IWinsdkDirectoryService>();
        var result = directoryService.GetLocalWinsdkDirectory(_tempDirectory);

        // Assert
        var expectedPath = Path.Combine(_tempDirectory, ".winsdk");
        Assert.AreEqual(expectedPath, result);
    }

    [TestMethod]
    public void GetLocalWinsdkDirectory_WithNullBaseDirectory_UsesCurrentDirectory()
    {
        // Act
        var directoryService = GetRequiredService<IWinsdkDirectoryService>();
        var result = directoryService.GetLocalWinsdkDirectory(null);

        // Assert
        var expectedPath = Path.Combine(Directory.GetCurrentDirectory(), ".winsdk");
        Assert.AreEqual(expectedPath, result);
    }
}

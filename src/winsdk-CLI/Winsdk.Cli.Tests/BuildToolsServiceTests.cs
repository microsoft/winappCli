using Winsdk.Cli.Services;

namespace Winsdk.Cli.Tests;

[TestClass]
[DoNotParallelize]
public class BuildToolsServiceTests : BaseCommandTests
{
    private string _tempDirectory = null!;
    private string _testWinsdkDirectory = null!;
    private IConfigService _configService = null!;
    private IBuildToolsService _buildToolsService = null!;

    [TestInitialize]
    public void Setup()
    {
        // Create a temporary directory for testing
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"WinsdkBuildToolsTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);

        // Set up a temporary winsdk directory for testing (isolates tests from real winsdk directory)
        _testWinsdkDirectory = Path.Combine(_tempDirectory, ".winsdk");
        Directory.CreateDirectory(_testWinsdkDirectory);

        // Set up services with test cache directory
        _configService = GetRequiredService<IConfigService>();
        _configService.ConfigPath = Path.Combine(_tempDirectory, "winsdk.yaml");

        var directoryService = GetRequiredService<IWinsdkDirectoryService>();
        directoryService.SetCacheDirectoryForTesting(_testWinsdkDirectory);
        _buildToolsService = GetRequiredService<IBuildToolsService>();
    }

    [TestCleanup]
    public void Cleanup()
    {
        // Clean up temporary files and directories
        if (Directory.Exists(_tempDirectory))
        {
            try
            {
                Directory.Delete(_tempDirectory, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [TestMethod]
    public void BuildToolsService_WithTestCacheDirectory_UsesOverriddenDirectory()
    {
        // The BuildToolsService instance should use our test directory for all operations
        // We can test this by verifying that GetBuildToolPath returns null when no packages are installed
        // in our isolated test directory (as opposed to potentially finding tools in the real user directory)

        // Act
        var result = _buildToolsService.GetBuildToolPath("mt.exe");

        // Assert - Should be null since we haven't installed any packages in our test directory
        Assert.IsNull(result);

        // Additional verification: Create a fake bin directory structure and verify it's found
        var packagesDir = Path.Combine(_testWinsdkDirectory, "packages");
        var buildToolsPackageDir = Path.Combine(packagesDir, "Microsoft.Windows.SDK.BuildTools.10.0.26100.1");
        var binDir = Path.Combine(buildToolsPackageDir, "bin", "10.0.26100.0", "x64");
        Directory.CreateDirectory(binDir);

        var fakeToolPath = Path.Combine(binDir, "mt.exe");
        File.WriteAllText(fakeToolPath, "fake tool");

        // Now it should find the tool in our test directory
        var result2 = _buildToolsService.GetBuildToolPath("mt.exe");
        Assert.AreEqual(fakeToolPath, result2);
    }
}

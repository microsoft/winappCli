// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class DirectoryPackagesServiceTests : BaseCommandTests
{
    private string _testTempDirectory = null!;
    private IDirectoryPackagesService _directoryPackagesService = null!;

    [TestInitialize]
    public void Setup()
    {
        // Create a temporary directory for testing
        _testTempDirectory = Path.Combine(Path.GetTempPath(), $"winappDirectoryPackagesTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testTempDirectory);

        _directoryPackagesService = GetRequiredService<IDirectoryPackagesService>();
    }

    [TestCleanup]
    public void Cleanup()
    {
        // Clean up temporary files and directories
        if (Directory.Exists(_testTempDirectory))
        {
            try
            {
                Directory.Delete(_testTempDirectory, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [TestMethod]
    public void UpdatePackageVersionsNoFileExistsReturnsFalse()
    {
        // Arrange
        var packageVersions = new Dictionary<string, string>
        {
            { "Microsoft.Windows.SDK.BuildTools", "10.0.22621.3233" }
        };

        // Act
        var result = _directoryPackagesService.UpdatePackageVersions(new DirectoryInfo(_testTempDirectory), packageVersions, TestTaskContext);

        // Assert
        Assert.IsFalse(result, "Should return false when Directory.Packages.props doesn't exist");
    }

    [TestMethod]
    public void UpdatePackageVersionsUpdatesSinglePackageReturnsTrue()
    {
        // Arrange
        var propsFilePath = Path.Combine(_testTempDirectory, "Directory.Packages.props");
        var originalContent = @"<Project>
  <ItemGroup>
    <PackageVersion Include=""Microsoft.Windows.SDK.BuildTools"" Version=""10.0.22621.1000"" />
  </ItemGroup>
</Project>";
        File.WriteAllText(propsFilePath, originalContent);

        var packageVersions = new Dictionary<string, string>
        {
            { "Microsoft.Windows.SDK.BuildTools", "10.0.22621.3233" }
        };

        // Act
        var result = _directoryPackagesService.UpdatePackageVersions(new DirectoryInfo(_testTempDirectory), packageVersions, TestTaskContext);

        // Assert
        Assert.IsTrue(result, "Should return true when update is successful");
        
        var updatedContent = File.ReadAllText(propsFilePath);
        StringAssert.Contains(updatedContent, "10.0.22621.3233", "Should contain new version");
        // Old version should be replaced
        Assert.IsFalse(updatedContent.Contains("10.0.22621.1000", StringComparison.Ordinal), "Should not contain old version");
    }

    [TestMethod]
    public void UpdatePackageVersionsUpdatesMultiplePackagesReturnsTrue()
    {
        // Arrange
        var propsFilePath = Path.Combine(_testTempDirectory, "Directory.Packages.props");
        var originalContent = @"<Project>
  <ItemGroup>
    <PackageVersion Include=""Microsoft.Windows.SDK.BuildTools"" Version=""10.0.22621.1000"" />
    <PackageVersion Include=""Microsoft.Windows.CsWinRT"" Version=""2.0.0"" />
    <PackageVersion Include=""Microsoft.Windows.SDK.Contracts"" Version=""10.0.22621.1000"" />
  </ItemGroup>
</Project>";
        File.WriteAllText(propsFilePath, originalContent);

        var packageVersions = new Dictionary<string, string>
        {
            { "Microsoft.Windows.SDK.BuildTools", "10.0.22621.3233" },
            { "Microsoft.Windows.CsWinRT", "2.1.1" },
            { "Microsoft.Windows.SDK.Contracts", "10.0.26100.1742" }
        };

        // Act
        var result = _directoryPackagesService.UpdatePackageVersions(new DirectoryInfo(_testTempDirectory), packageVersions, TestTaskContext);

        // Assert
        Assert.IsTrue(result, "Should return true when update is successful");
        
        var updatedContent = File.ReadAllText(propsFilePath);
        StringAssert.Contains(updatedContent, "10.0.22621.3233", "Should contain new BuildTools version");
        StringAssert.Contains(updatedContent, "2.1.1", "Should contain new CsWinRT version");
        StringAssert.Contains(updatedContent, "10.0.26100.1742", "Should contain new Contracts version");
    }

    [TestMethod]
    public void UpdatePackageVersionsPreservesWhitespaceReturnsTrue()
    {
        // Arrange
        var propsFilePath = Path.Combine(_testTempDirectory, "Directory.Packages.props");
        var originalContent = @"<Project>
  <ItemGroup>
    <!-- This is a comment -->
    <PackageVersion Include=""Microsoft.Windows.SDK.BuildTools"" Version=""10.0.22621.1000"" />
    
    <PackageVersion Include=""Microsoft.Windows.CsWinRT"" Version=""2.0.0"" />
  </ItemGroup>
</Project>";
        File.WriteAllText(propsFilePath, originalContent);

        var packageVersions = new Dictionary<string, string>
        {
            { "Microsoft.Windows.SDK.BuildTools", "10.0.22621.3233" }
        };

        // Act
        var result = _directoryPackagesService.UpdatePackageVersions(new DirectoryInfo(_testTempDirectory), packageVersions, TestTaskContext);

        // Assert
        Assert.IsTrue(result, "Should return true when update is successful");
        
        var updatedContent = File.ReadAllText(propsFilePath);
        StringAssert.Contains(updatedContent, "<!-- This is a comment -->", "Should preserve comments");
        StringAssert.Contains(updatedContent, "10.0.22621.3233", "Should contain new version");
    }

    [TestMethod]
    public void UpdatePackageVersionsNoChangesNeededReturnsTrue()
    {
        // Arrange
        var propsFilePath = Path.Combine(_testTempDirectory, "Directory.Packages.props");
        var originalContent = @"<Project>
  <ItemGroup>
    <PackageVersion Include=""Microsoft.Windows.SDK.BuildTools"" Version=""10.0.22621.3233"" />
  </ItemGroup>
</Project>";
        File.WriteAllText(propsFilePath, originalContent);

        var packageVersions = new Dictionary<string, string>
        {
            { "Microsoft.Windows.SDK.BuildTools", "10.0.22621.3233" }
        };

        // Act
        var result = _directoryPackagesService.UpdatePackageVersions(new DirectoryInfo(_testTempDirectory), packageVersions, TestTaskContext);

        // Assert
        Assert.IsTrue(result, "Should return true even when no changes are needed");
        
        var updatedContent = File.ReadAllText(propsFilePath);
        Assert.AreEqual(originalContent, updatedContent, "Content should remain unchanged");
    }

    [TestMethod]
    public void UpdatePackageVersionsPartialMatchUpdatesOnlyMatchingPackages()
    {
        // Arrange
        var propsFilePath = Path.Combine(_testTempDirectory, "Directory.Packages.props");
        var originalContent = @"<Project>
  <ItemGroup>
    <PackageVersion Include=""Microsoft.Windows.SDK.BuildTools"" Version=""10.0.22621.1000"" />
    <PackageVersion Include=""SomeOtherPackage"" Version=""1.0.0"" />
  </ItemGroup>
</Project>";
        File.WriteAllText(propsFilePath, originalContent);

        var packageVersions = new Dictionary<string, string>
        {
            { "Microsoft.Windows.SDK.BuildTools", "10.0.22621.3233" }
            // Note: SomeOtherPackage not in winapp.yaml
        };

        // Act
        var result = _directoryPackagesService.UpdatePackageVersions(new DirectoryInfo(_testTempDirectory), packageVersions, TestTaskContext);

        // Assert
        Assert.IsTrue(result, "Should return true when update is successful");
        
        var updatedContent = File.ReadAllText(propsFilePath);
        StringAssert.Contains(updatedContent, "10.0.22621.3233", "Should update matching package");
        StringAssert.Contains(updatedContent, @"<PackageVersion Include=""SomeOtherPackage"" Version=""1.0.0"" />", 
            "Should leave non-matching package unchanged");
    }

    [TestMethod]
    public void UpdatePackageVersionsEmptyFileReturnsFalse()
    {
        // Arrange
        var propsFilePath = Path.Combine(_testTempDirectory, "Directory.Packages.props");
        File.WriteAllText(propsFilePath, "");

        var packageVersions = new Dictionary<string, string>
        {
            { "Microsoft.Windows.SDK.BuildTools", "10.0.22621.3233" }
        };

        // Act
        var result = _directoryPackagesService.UpdatePackageVersions(new DirectoryInfo(_testTempDirectory), packageVersions, TestTaskContext);

        // Assert
        Assert.IsFalse(result, "Should return false for empty/invalid XML file");
    }

    [TestMethod]
    public void UpdatePackageVersionsNoPackageVersionElementsReturnsFalse()
    {
        // Arrange
        var propsFilePath = Path.Combine(_testTempDirectory, "Directory.Packages.props");
        var originalContent = @"<Project>
  <ItemGroup>
    <!-- No PackageVersion elements -->
  </ItemGroup>
</Project>";
        File.WriteAllText(propsFilePath, originalContent);

        var packageVersions = new Dictionary<string, string>
        {
            { "Microsoft.Windows.SDK.BuildTools", "10.0.22621.3233" }
        };

        // Act
        var result = _directoryPackagesService.UpdatePackageVersions(new DirectoryInfo(_testTempDirectory), packageVersions, TestTaskContext);

        // Assert
        Assert.IsFalse(result, "Should return false when no PackageVersion elements exist");
    }

    [TestMethod]
    public void UpdatePackageVersionsMultipleItemGroupsUpdatesAll()
    {
        // Arrange
        var propsFilePath = Path.Combine(_testTempDirectory, "Directory.Packages.props");
        var originalContent = @"<Project>
  <ItemGroup>
    <PackageVersion Include=""Microsoft.Windows.SDK.BuildTools"" Version=""10.0.22621.1000"" />
  </ItemGroup>
  <ItemGroup>
    <PackageVersion Include=""Microsoft.Windows.CsWinRT"" Version=""2.0.0"" />
  </ItemGroup>
</Project>";
        File.WriteAllText(propsFilePath, originalContent);

        var packageVersions = new Dictionary<string, string>
        {
            { "Microsoft.Windows.SDK.BuildTools", "10.0.22621.3233" },
            { "Microsoft.Windows.CsWinRT", "2.1.1" }
        };

        // Act
        var result = _directoryPackagesService.UpdatePackageVersions(new DirectoryInfo(_testTempDirectory), packageVersions, TestTaskContext);

        // Assert
        Assert.IsTrue(result, "Should return true when update is successful");
        
        var updatedContent = File.ReadAllText(propsFilePath);
        StringAssert.Contains(updatedContent, "10.0.22621.3233", "Should update first ItemGroup");
        StringAssert.Contains(updatedContent, "2.1.1", "Should update second ItemGroup");
    }
}

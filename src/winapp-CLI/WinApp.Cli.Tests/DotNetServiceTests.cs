// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class DotNetServiceTests : BaseCommandTests
{
    private string _testTempDirectory = null!;
    private IDotNetService _dotNetService = null!;

    [TestInitialize]
    public void Setup()
    {
        // Create a temporary directory for testing
        _testTempDirectory = Path.Combine(Path.GetTempPath(), $"winappDotNetServiceTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testTempDirectory);

        _dotNetService = GetRequiredService<IDotNetService>();
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

    #region FindCsproj Tests

    [TestMethod]
    public void FindCsproj_NoFiles_ReturnsEmpty()
    {
        // Arrange
        var directory = new DirectoryInfo(_testTempDirectory);

        // Act
        var result = _dotNetService.FindCsproj(directory);

        // Assert
        Assert.IsEmpty(result);
    }

    [TestMethod]
    public void FindCsproj_SingleCsprojFile_ReturnsSingleFile()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "TestProject.csproj");
        File.WriteAllText(csprojPath, "<Project></Project>");
        var directory = new DirectoryInfo(_testTempDirectory);

        // Act
        var result = _dotNetService.FindCsproj(directory);

        // Assert
        Assert.HasCount(1, result);
        Assert.AreEqual("TestProject.csproj", result[0].Name);
    }

    [TestMethod]
    public void FindCsproj_MultipleCsprojFiles_ReturnsAllFiles()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_testTempDirectory, "ProjectA.csproj"), "<Project></Project>");
        File.WriteAllText(Path.Combine(_testTempDirectory, "ProjectB.csproj"), "<Project></Project>");
        var directory = new DirectoryInfo(_testTempDirectory);

        // Act
        var result = _dotNetService.FindCsproj(directory);

        // Assert
        Assert.HasCount(2, result);
        Assert.IsTrue(result.All(f => f.Name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void FindCsproj_DirectoryDoesNotExist_ReturnsEmpty()
    {
        // Arrange
        var nonExistentDirectory = new DirectoryInfo(Path.Combine(_testTempDirectory, "NonExistent"));

        // Act
        var result = _dotNetService.FindCsproj(nonExistentDirectory);

        // Assert
        Assert.IsEmpty(result);
    }

    [TestMethod]
    public void FindCsproj_CsprojInSubdirectory_ReturnsEmpty()
    {
        // Arrange - csproj is in a subdirectory, not the root
        var subDir = Path.Combine(_testTempDirectory, "SubFolder");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "SubProject.csproj"), "<Project></Project>");
        var directory = new DirectoryInfo(_testTempDirectory);

        // Act
        var result = _dotNetService.FindCsproj(directory);

        // Assert
        Assert.IsEmpty(result, "Should not find csproj files in subdirectories");
    }

    [TestMethod]
    public void FindCsproj_OtherFileTypes_ReturnsEmpty()
    {
        // Arrange - only non-csproj files
        File.WriteAllText(Path.Combine(_testTempDirectory, "Project.sln"), "");
        File.WriteAllText(Path.Combine(_testTempDirectory, "Project.fsproj"), "<Project></Project>");
        File.WriteAllText(Path.Combine(_testTempDirectory, "Project.vbproj"), "<Project></Project>");
        var directory = new DirectoryInfo(_testTempDirectory);

        // Act
        var result = _dotNetService.FindCsproj(directory);

        // Assert
        Assert.IsEmpty(result);
    }

    #endregion

    #region GetTargetFramework Tests

    [TestMethod]
    public void GetTargetFramework_ValidTfm_ReturnsValue()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
  </PropertyGroup>
</Project>");

        // Act
        var result = _dotNetService.GetTargetFramework(new FileInfo(csprojPath));

        // Assert
        Assert.AreEqual("net8.0-windows10.0.19041.0", result);
    }

    [TestMethod]
    public void GetTargetFramework_PlainNetTfm_ReturnsValue()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>");

        // Act
        var result = _dotNetService.GetTargetFramework(new FileInfo(csprojPath));

        // Assert
        Assert.AreEqual("net8.0", result);
    }

    [TestMethod]
    public void GetTargetFramework_NoTargetFramework_ReturnsNull()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
</Project>");

        // Act
        var result = _dotNetService.GetTargetFramework(new FileInfo(csprojPath));

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void GetTargetFramework_FileDoesNotExist_ReturnsNull()
    {
        // Arrange
        var nonExistentFile = new FileInfo(Path.Combine(_testTempDirectory, "NonExistent.csproj"));

        // Act
        var result = _dotNetService.GetTargetFramework(nonExistentFile);

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void GetTargetFramework_WithWhitespace_ReturnsTrimmedValue()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>  net10.0-windows10.0.19041.0  </TargetFramework>
  </PropertyGroup>
</Project>");

        // Act
        var result = _dotNetService.GetTargetFramework(new FileInfo(csprojPath));

        // Assert
        Assert.AreEqual("net10.0-windows10.0.19041.0", result);
    }

    [TestMethod]
    public void GetTargetFramework_MultilineContent_ReturnsValue()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>
      net9.0-windows10.0.22621.0
    </TargetFramework>
  </PropertyGroup>
</Project>");

        // Act
        var result = _dotNetService.GetTargetFramework(new FileInfo(csprojPath));

        // Assert
        Assert.IsNotNull(result);
        Assert.Contains("net9.0-windows10.0.22621.0", result);
    }

    [TestMethod]
    public void GetTargetFramework_MultiTargeted_ReturnsFirstTfm()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net9.0-windows10.0.19041.0</TargetFrameworks>
  </PropertyGroup>
</Project>");

        // Act
        var result = _dotNetService.GetTargetFramework(new FileInfo(csprojPath));

        // Assert
        Assert.AreEqual("net8.0", result);
    }

    #endregion

    #region IsMultiTargeted Tests

    [TestMethod]
    public void IsMultiTargeted_SingleTarget_ReturnsFalse()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
  </PropertyGroup>
</Project>");

        // Act & Assert
        Assert.IsFalse(_dotNetService.IsMultiTargeted(new FileInfo(csprojPath)));
    }

    [TestMethod]
    public void IsMultiTargeted_MultipleTargets_ReturnsTrue()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net9.0-windows10.0.19041.0</TargetFrameworks>
  </PropertyGroup>
</Project>");

        // Act & Assert
        Assert.IsTrue(_dotNetService.IsMultiTargeted(new FileInfo(csprojPath)));
    }

    [TestMethod]
    public void IsMultiTargeted_FileDoesNotExist_ReturnsFalse()
    {
        // Arrange
        var nonExistentFile = new FileInfo(Path.Combine(_testTempDirectory, "NonExistent.csproj"));

        // Act & Assert
        Assert.IsFalse(_dotNetService.IsMultiTargeted(nonExistentFile));
    }

    [TestMethod]
    public void IsMultiTargeted_NoTargetFramework_ReturnsFalse()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
</Project>");

        // Act & Assert
        Assert.IsFalse(_dotNetService.IsMultiTargeted(new FileInfo(csprojPath)));
    }

    #endregion

    #region IsTargetFrameworkSupported Tests

    [TestMethod]
    public void IsTargetFrameworkSupported_ValidWindowsTfm_ReturnsTrue()
    {
        // Act & Assert
        Assert.IsTrue(_dotNetService.IsTargetFrameworkSupported("net8.0-windows10.0.19041.0"));
        Assert.IsTrue(_dotNetService.IsTargetFrameworkSupported("net9.0-windows10.0.22621.0"));
        Assert.IsTrue(_dotNetService.IsTargetFrameworkSupported("net10.0-windows10.0.19041.0"));
    }

    [TestMethod]
    public void IsTargetFrameworkSupported_MinimumSupportedVersion_ReturnsTrue()
    {
        // Minimum supported Windows SDK is 10.0.17763.0
        Assert.IsTrue(_dotNetService.IsTargetFrameworkSupported("net6.0-windows10.0.17763.0"));
    }

    [TestMethod]
    public void IsTargetFrameworkSupported_BelowMinimumWindowsSdk_ReturnsFalse()
    {
        // Below minimum Windows SDK version (10.0.17763.0)
        Assert.IsFalse(_dotNetService.IsTargetFrameworkSupported("net8.0-windows10.0.17762.0"));
        Assert.IsFalse(_dotNetService.IsTargetFrameworkSupported("net8.0-windows10.0.16299.0"));
    }

    [TestMethod]
    public void IsTargetFrameworkSupported_BelowMinimumNetVersion_ReturnsFalse()
    {
        // Below minimum .NET version (6.0)
        Assert.IsFalse(_dotNetService.IsTargetFrameworkSupported("net5.0-windows10.0.19041.0"));
        Assert.IsFalse(_dotNetService.IsTargetFrameworkSupported("net4.8-windows10.0.19041.0"));
    }

    [TestMethod]
    public void IsTargetFrameworkSupported_PlainNetTfm_ReturnsFalse()
    {
        // Plain .NET TFM without Windows specifier
        Assert.IsFalse(_dotNetService.IsTargetFrameworkSupported("net8.0"));
        Assert.IsFalse(_dotNetService.IsTargetFrameworkSupported("net10.0"));
    }

    [TestMethod]
    public void IsTargetFrameworkSupported_PlainWindowsTfm_ReturnsFalse()
    {
        // Windows TFM without SDK version
        Assert.IsFalse(_dotNetService.IsTargetFrameworkSupported("net8.0-windows"));
    }

    [TestMethod]
    public void IsTargetFrameworkSupported_NullOrEmpty_ReturnsFalse()
    {
        Assert.IsFalse(_dotNetService.IsTargetFrameworkSupported(null!));
        Assert.IsFalse(_dotNetService.IsTargetFrameworkSupported(""));
        Assert.IsFalse(_dotNetService.IsTargetFrameworkSupported("   "));
    }

    [TestMethod]
    public void IsTargetFrameworkSupported_InvalidFormat_ReturnsFalse()
    {
        Assert.IsFalse(_dotNetService.IsTargetFrameworkSupported("invalid"));
        Assert.IsFalse(_dotNetService.IsTargetFrameworkSupported("netstandard2.0"));
        Assert.IsFalse(_dotNetService.IsTargetFrameworkSupported("netcoreapp3.1"));
    }

    [TestMethod]
    public void IsTargetFrameworkSupported_CaseInsensitive_ReturnsTrue()
    {
        Assert.IsTrue(_dotNetService.IsTargetFrameworkSupported("NET8.0-WINDOWS10.0.19041.0"));
        Assert.IsTrue(_dotNetService.IsTargetFrameworkSupported("Net8.0-Windows10.0.19041.0"));
    }

    #endregion

    #region GetRecommendedTargetFramework Tests

    [TestMethod]
    public void GetRecommendedTargetFramework_NullInput_ReturnsDefault()
    {
        // Act
        var result = _dotNetService.GetRecommendedTargetFramework(null);

        // Assert
        Assert.AreEqual("net10.0-windows10.0.26100.0", result);
    }

    [TestMethod]
    public void GetRecommendedTargetFramework_EmptyInput_ReturnsDefault()
    {
        // Act
        var result = _dotNetService.GetRecommendedTargetFramework("");

        // Assert
        Assert.AreEqual("net10.0-windows10.0.26100.0", result);
    }

    [TestMethod]
    public void GetRecommendedTargetFramework_WhitespaceInput_ReturnsDefault()
    {
        // Act
        var result = _dotNetService.GetRecommendedTargetFramework("   ");

        // Assert
        Assert.AreEqual("net10.0-windows10.0.26100.0", result);
    }

    [TestMethod]
    public void GetRecommendedTargetFramework_AlreadySupported_ReturnsSame()
    {
        // Arrange - already a fully supported TFM
        var input = "net8.0-windows10.0.26100.0";

        // Act
        var result = _dotNetService.GetRecommendedTargetFramework(input);

        // Assert
        Assert.AreEqual(input, result);
    }

    [TestMethod]
    public void GetRecommendedTargetFramework_PlainNetTfm_AddsWindowsSdk()
    {
        // Arrange - plain .NET TFM needs Windows SDK added
        var input = "net8.0";

        // Act
        var result = _dotNetService.GetRecommendedTargetFramework(input);

        // Assert
        Assert.AreEqual("net8.0-windows10.0.26100.0", result);
    }

    [TestMethod]
    public void GetRecommendedTargetFramework_PlainWindowsTfm_AddsSdkVersion()
    {
        // Arrange - Windows TFM without SDK version
        var input = "net9.0-windows";

        // Act
        var result = _dotNetService.GetRecommendedTargetFramework(input);

        // Assert
        Assert.AreEqual("net9.0-windows10.0.26100.0", result);
    }

    [TestMethod]
    public void GetRecommendedTargetFramework_OldWindowsSdk_UpdatesSdkVersion()
    {
        // Arrange - supported .NET version but old Windows SDK
        var input = "net8.0-windows10.0.17762.0"; // Below minimum 10.0.17763.0

        // Act
        var result = _dotNetService.GetRecommendedTargetFramework(input);

        // Assert
        Assert.AreEqual("net8.0-windows10.0.26100.0", result);
    }

    [TestMethod]
    public void GetRecommendedTargetFramework_OldNetVersion_ReturnsDefault()
    {
        // Arrange - .NET version below minimum (6.0)
        var input = "net5.0-windows10.0.26100.0";

        // Act
        var result = _dotNetService.GetRecommendedTargetFramework(input);

        // Assert
        Assert.AreEqual("net10.0-windows10.0.26100.0", result);
    }

    [TestMethod]
    public void GetRecommendedTargetFramework_PreservesHigherNetVersion()
    {
        // Arrange - higher .NET version should be preserved
        var input = "net10.0";

        // Act
        var result = _dotNetService.GetRecommendedTargetFramework(input);

        // Assert
        Assert.AreEqual("net10.0-windows10.0.26100.0", result);
    }

    [TestMethod]
    public void GetRecommendedTargetFramework_InvalidFormat_ReturnsDefault()
    {
        // Act
        var result = _dotNetService.GetRecommendedTargetFramework("invalid-tfm");

        // Assert
        Assert.AreEqual("net10.0-windows10.0.26100.0", result);
    }

    [TestMethod]
    public void GetRecommendedTargetFramework_NetStandard_ReturnsDefault()
    {
        // Act
        var result = _dotNetService.GetRecommendedTargetFramework("netstandard2.0");

        // Assert
        Assert.AreEqual("net10.0-windows10.0.26100.0", result);
    }

    #endregion

    #region SetTargetFramework Tests

    [TestMethod]
    public void SetTargetFramework_ReplacesExistingTfm()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
  </PropertyGroup>
</Project>");

        // Act
        _dotNetService.SetTargetFramework(new FileInfo(csprojPath), "net10.0-windows10.0.19041.0");

        // Assert
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>");
        Assert.IsFalse(content.Contains("net6.0", StringComparison.Ordinal), "Old TFM should be removed");
    }

    [TestMethod]
    public void SetTargetFramework_InsertsWhenMissing()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
</Project>");

        // Act
        _dotNetService.SetTargetFramework(new FileInfo(csprojPath), "net10.0-windows10.0.19041.0");

        // Assert
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>");
    }

    [TestMethod]
    public void SetTargetFramework_PreservesOtherElements()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net6.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");

        // Act
        _dotNetService.SetTargetFramework(new FileInfo(csprojPath), "net10.0-windows10.0.19041.0");

        // Assert
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<OutputType>Exe</OutputType>");
        StringAssert.Contains(content, "<Nullable>enable</Nullable>");
        StringAssert.Contains(content, "<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>");
    }

    [TestMethod]
    public void SetTargetFramework_HandlesMultiplePropertyGroups()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <PropertyGroup Condition=""'$(Configuration)'=='Debug'"">
    <DefineConstants>DEBUG</DefineConstants>
  </PropertyGroup>
</Project>");

        // Act
        _dotNetService.SetTargetFramework(new FileInfo(csprojPath), "net10.0-windows10.0.19041.0");

        // Assert
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>");
        StringAssert.Contains(content, "<DefineConstants>DEBUG</DefineConstants>");
    }

    [TestMethod]
    public void SetTargetFramework_UpdatesInPlace()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        var originalContent = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
  </PropertyGroup>
</Project>";
        File.WriteAllText(csprojPath, originalContent);

        // Act - update to a different TFM
        _dotNetService.SetTargetFramework(new FileInfo(csprojPath), "net10.0-windows10.0.22621.0");

        // Assert
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<TargetFramework>net10.0-windows10.0.22621.0</TargetFramework>");
        Assert.IsFalse(content.Contains("net8.0", StringComparison.Ordinal));
    }

    #endregion

    #region AddOrUpdatePackageReferenceAsync Tests

    [TestMethod]
    public async Task AddOrUpdatePackageReferenceAsync_InvalidProject_ThrowsException()
    {
        // Arrange - create an invalid csproj file
        var csprojPath = Path.Combine(_testTempDirectory, "Invalid.csproj");
        File.WriteAllText(csprojPath, "This is not valid XML");

        // Act & Assert
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await _dotNetService.AddOrUpdatePackageReferenceAsync(
                new FileInfo(csprojPath),
                "Microsoft.WindowsAppSDK",
                "1.5.0",
                TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task AddOrUpdatePackageReferenceAsync_NonExistentProject_ThrowsException()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testTempDirectory, "NonExistent.csproj");

        // Act & Assert
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await _dotNetService.AddOrUpdatePackageReferenceAsync(
                new FileInfo(nonExistentPath),
                "Microsoft.WindowsAppSDK",
                "1.5.0",
                TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task AddOrUpdatePackageReferenceAsync_ValidProject_AddsPackage()
    {
        // Arrange - create a valid SDK-style project
        var csprojPath = Path.Combine(_testTempDirectory, "ValidProject.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
  </PropertyGroup>
</Project>");

        // Act
        await _dotNetService.AddOrUpdatePackageReferenceAsync(
            new FileInfo(csprojPath),
            "Newtonsoft.Json",
            "13.0.3",
            TestContext.CancellationToken);

        // Assert - verify the package was added
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "Newtonsoft.Json");
    }

    [TestMethod]
    public async Task AddOrUpdatePackageReferenceAsync_InvalidPackageName_ThrowsException()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>");

        // Act & Assert
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await _dotNetService.AddOrUpdatePackageReferenceAsync(
                new FileInfo(csprojPath),
                "This.Package.Does.Not.Exist.12345",
                "1.0.0",
                TestContext.CancellationToken));
    }

    #endregion

    #region EnsureRuntimeIdentifierAsync Tests (RuntimeIdentifierElementRegex)

    [TestMethod]
    public async Task EnsureRuntimeIdentifierAsync_NoRuntimeIdentifier_InsertsOne()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "NoRid.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
  </PropertyGroup>
</Project>");

        // Act
        var result = await _dotNetService.EnsureRuntimeIdentifierAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "Should return true when RuntimeIdentifier was inserted");
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<RuntimeIdentifier Condition=");
    }

    [TestMethod]
    public async Task EnsureRuntimeIdentifierAsync_HasRuntimeIdentifier_DoesNotModify()
    {
        // Arrange — singular <RuntimeIdentifier>
        var csprojPath = Path.Combine(_testTempDirectory, "HasRid.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  </PropertyGroup>
</Project>");

        // Act
        var result = await _dotNetService.EnsureRuntimeIdentifierAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false when RuntimeIdentifier already exists");
    }

    [TestMethod]
    public async Task EnsureRuntimeIdentifierAsync_HasRuntimeIdentifiers_StillInserts()
    {
        // Arrange — plural <RuntimeIdentifiers> should NOT prevent inserting singular <RuntimeIdentifier>
        var csprojPath = Path.Combine(_testTempDirectory, "HasRids.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>
  </PropertyGroup>
</Project>");

        // Act
        var result = await _dotNetService.EnsureRuntimeIdentifierAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "Should insert RuntimeIdentifier even when RuntimeIdentifiers (plural) exists");
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<RuntimeIdentifier Condition=");
        // Verify it's inserted right after </RuntimeIdentifiers>
        var ridsEnd = content.IndexOf("</RuntimeIdentifiers>", StringComparison.Ordinal);
        var ridStart = content.IndexOf("<RuntimeIdentifier Condition=", StringComparison.Ordinal);
        Assert.IsTrue(ridStart > ridsEnd, "RuntimeIdentifier should be placed after RuntimeIdentifiers");
        // Nothing but whitespace between them
        var between = content[(ridsEnd + "</RuntimeIdentifiers>".Length)..ridStart];
        // The comment is expected between the elements
        Assert.IsTrue(between.Contains("<!-- Added by winapp"), $"Expected comment between elements, got: '{between}'");
    }

    [TestMethod]
    public async Task EnsureRuntimeIdentifierAsync_HasRuntimeIdentifierWithCondition_DoesNotModify()
    {
        // Arrange — <RuntimeIdentifier with a Condition attribute (whitespace after tag name)
        var csprojPath = Path.Combine(_testTempDirectory, "HasRidCondition.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <RuntimeIdentifier Condition=""'$(RuntimeIdentifier)' == ''"">win-x64</RuntimeIdentifier>
  </PropertyGroup>
</Project>");

        // Act
        var result = await _dotNetService.EnsureRuntimeIdentifierAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false when RuntimeIdentifier with attributes already exists");
    }

    [TestMethod]
    public async Task EnsureRuntimeIdentifierAsync_HasRuntimeIdentifiersWithCondition_StillInserts()
    {
        // Arrange — plural <RuntimeIdentifiers with a Condition attribute should NOT block insertion
        var csprojPath = Path.Combine(_testTempDirectory, "HasRidsCondition.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <RuntimeIdentifiers Condition=""'$(RuntimeIdentifiers)' == ''"">win-x64;win-arm64</RuntimeIdentifiers>
  </PropertyGroup>
</Project>");

        // Act
        var result = await _dotNetService.EnsureRuntimeIdentifierAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "Should insert RuntimeIdentifier even when RuntimeIdentifiers (plural) with attributes exists");
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<RuntimeIdentifier Condition=");
        // Verify it's inserted right after </RuntimeIdentifiers>
        var ridsEnd = content.IndexOf("</RuntimeIdentifiers>", StringComparison.Ordinal);
        var ridStart = content.IndexOf("<RuntimeIdentifier Condition=", StringComparison.Ordinal);
        Assert.IsTrue(ridStart > ridsEnd, "RuntimeIdentifier should be placed after RuntimeIdentifiers");
    }

    [TestMethod]
    public async Task EnsureRuntimeIdentifierAsync_FileDoesNotExist_ReturnsFalse()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "NonExistent.csproj");

        // Act
        var result = await _dotNetService.EnsureRuntimeIdentifierAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task EnsureRuntimeIdentifierAsync_DoesNotMatchSimilarElementNames()
    {
        // Arrange — contains <RuntimeIdentifierGraph> which should NOT prevent insertion
        var csprojPath = Path.Combine(_testTempDirectory, "SimilarName.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
  </PropertyGroup>
  <!-- RuntimeIdentifierGraph should not be confused with RuntimeIdentifier -->
</Project>");

        // Act
        var result = await _dotNetService.EnsureRuntimeIdentifierAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "Should insert RuntimeIdentifier — RuntimeIdentifierGraph is not RuntimeIdentifier");
    }

    #endregion

    #region HasPackageReferenceAsync Tests

    [TestMethod]
    public async Task HasPackageReferenceAsync_FileDoesNotExist_ReturnsFalse()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "NonExistent.csproj");

        // Act
        var result = await _dotNetService.HasPackageReferenceAsync(new FileInfo(csprojPath), "Microsoft.WindowsAppSDK", TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false for non-existent file");
    }

    [TestMethod]
    public async Task HasPackageReferenceAsync_WithMatchingPackage_ReturnsTrue()
    {
        // Arrange
        var fake = new FakeDotNetService
        {
            PackageListResult = new DotNetPackageListJson(
            [
                new DotNetProject(
                [
                    new DotNetFramework("net10.0-windows10.0.26100.0",
                        [new DotNetPackage("Microsoft.WindowsAppSDK", "1.6.0", "1.6.0")],
                        [])
                ])
            ])
        };

        // Act
        var result = await fake.HasPackageReferenceAsync(
            new FileInfo("dummy.csproj"), "Microsoft.WindowsAppSDK", TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "Should detect existing PackageReference");
    }

    [TestMethod]
    public async Task HasPackageReferenceAsync_CaseInsensitive_ReturnsTrue()
    {
        // Arrange
        var fake = new FakeDotNetService
        {
            PackageListResult = new DotNetPackageListJson(
            [
                new DotNetProject(
                [
                    new DotNetFramework("net10.0-windows10.0.26100.0",
                        [new DotNetPackage("microsoft.windowsappsdk", "1.6.0", "1.6.0")],
                        [])
                ])
            ])
        };

        // Act
        var result = await fake.HasPackageReferenceAsync(
            new FileInfo("dummy.csproj"), "Microsoft.WindowsAppSDK", TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "Package name comparison should be case-insensitive");
    }

    [TestMethod]
    public async Task HasPackageReferenceAsync_DifferentPackage_ReturnsFalse()
    {
        // Arrange
        var fake = new FakeDotNetService
        {
            PackageListResult = new DotNetPackageListJson(
            [
                new DotNetProject(
                [
                    new DotNetFramework("net10.0-windows10.0.26100.0",
                        [new DotNetPackage("Newtonsoft.Json", "13.0.3", "13.0.3")],
                        [])
                ])
            ])
        };

        // Act
        var result = await fake.HasPackageReferenceAsync(
            new FileInfo("dummy.csproj"), "Microsoft.WindowsAppSDK", TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false when a different package is referenced");
    }

    [TestMethod]
    public async Task HasPackageReferenceAsync_NullPackageListResult_ReturnsFalse()
    {
        // Arrange
        var fake = new FakeDotNetService { PackageListResult = null };

        // Act
        var result = await fake.HasPackageReferenceAsync(
            new FileInfo("dummy.csproj"), "Microsoft.WindowsAppSDK", TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false when package list is null");
    }

    [TestMethod]
    public async Task HasPackageReferenceAsync_EmptyProjects_ReturnsFalse()
    {
        // Arrange
        var fake = new FakeDotNetService
        {
            PackageListResult = new DotNetPackageListJson([])
        };

        // Act
        var result = await fake.HasPackageReferenceAsync(
            new FileInfo("dummy.csproj"), "Microsoft.WindowsAppSDK", TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false when project list is empty");
    }

    [TestMethod]
    public async Task HasPackageReferenceAsync_TransitiveOnly_ReturnsFalse()
    {
        // Arrange — package exists only as a transitive dependency, not a top-level reference
        var fake = new FakeDotNetService
        {
            PackageListResult = new DotNetPackageListJson(
            [
                new DotNetProject(
                [
                    new DotNetFramework("net10.0-windows10.0.26100.0",
                        [],
                        [new DotNetPackage("Microsoft.WindowsAppSDK", "1.6.0", "1.6.0")])
                ])
            ])
        };

        // Act
        var result = await fake.HasPackageReferenceAsync(
            new FileInfo("dummy.csproj"), "Microsoft.WindowsAppSDK", TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false when package is only a transitive dependency");    }

    [TestMethod]
    public async Task HasPackageReferenceAsync_FastPath_DetectsInlinePackageReference_WithoutDotnetRestore()
    {
        // Arrange — write an SDK-style csproj with an inline PackageReference. Use a non-existent
        // SDK so a `dotnet list package` fallback would fail; success here proves the XML fast path
        // returned true without invoking dotnet (#463).
        var csprojPath = Path.Combine(_testTempDirectory, "Inline.csproj");
        await File.WriteAllTextAsync(csprojPath, """
<Project Sdk="DefinitelyNotARealSdk/0.0.0">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="1.6.0" />
  </ItemGroup>
</Project>
""", TestContext.CancellationToken);

        // Act
        var result = await _dotNetService.HasPackageReferenceAsync(
            new FileInfo(csprojPath), "Microsoft.WindowsAppSDK", TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "XML fast path should detect inline <PackageReference Include=\"Microsoft.WindowsAppSDK\"/>.");
    }

    [TestMethod]
    public async Task HasPackageReferenceAsync_FastPath_CaseInsensitiveOnIncludeAttribute()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Cased.csproj");
        await File.WriteAllTextAsync(csprojPath, """
<Project Sdk="DefinitelyNotARealSdk/0.0.0">
  <ItemGroup>
    <PackageReference Include="microsoft.windowsappsdk" Version="1.6.0" />
  </ItemGroup>
</Project>
""", TestContext.CancellationToken);

        // Act
        var result = await _dotNetService.HasPackageReferenceAsync(
            new FileInfo(csprojPath), "Microsoft.WindowsAppSDK", TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "XML fast path should be case-insensitive on the Include attribute.");
    }

    #endregion

    #region EnsureAssetContentItemsAsync

    [TestMethod]
    public async Task EnsureAssetContentItems_AddsMissingAssetsGlob()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
");

        // Act
        var result = await _dotNetService.EnsureAssetContentItemsAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "Should modify csproj when no asset Content items exist");
        var content = File.ReadAllText(csprojPath);
        Assert.IsTrue(content.Contains(@"<Content Include=""Assets\**\*"" />"),
            "Should add Assets glob Content item");
        Assert.IsTrue(content.Contains("</Project>"),
            "Should preserve </Project> closing tag");
    }

    [TestMethod]
    public async Task EnsureAssetContentItems_SkipsWhenAlreadyPresent()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Content Include=""Assets\**\*"" />
  </ItemGroup>
</Project>
");

        // Act
        var result = await _dotNetService.EnsureAssetContentItemsAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should not modify csproj when asset Content items already exist");
    }

    [TestMethod]
    public async Task EnsureAssetContentItems_SkipsWhenIndividualAssetPresent()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Test.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <Content Include=""Assets\StoreLogo.png"" />
  </ItemGroup>
</Project>
");

        // Act
        var result = await _dotNetService.EnsureAssetContentItemsAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should not modify csproj when individual asset Content items exist");
    }

    [TestMethod]
    public async Task EnsureAssetContentItems_ReturnsFalseForMissingFile()
    {
        // Act
        var result = await _dotNetService.EnsureAssetContentItemsAsync(
            new FileInfo(Path.Combine(_testTempDirectory, "NonExistent.csproj")),
            TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result);
    }

    #endregion

    #region UpdatePublishProfileAsync Tests

    [TestMethod]
    public async Task UpdatePublishProfile_WithPlatformProfile_AddsExistsCondition()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Pub.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <PublishProfile>win-$(Platform).pubxml</PublishProfile>
  </PropertyGroup>
</Project>");

        // Act
        var result = await _dotNetService.UpdatePublishProfileAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "Should return true when the PublishProfile was rewritten");
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content,
            @"<PublishProfile Condition=""Exists('Properties\PublishProfiles\win-$(Platform).pubxml')"">win-$(Platform).pubxml</PublishProfile>");
    }

    [TestMethod]
    public async Task UpdatePublishProfile_WithoutPlatformToken_DoesNotModify()
    {
        // Arrange — a PublishProfile that does not reference $(Platform) should be ignored
        var csprojPath = Path.Combine(_testTempDirectory, "Pub.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <PublishProfile>Release.pubxml</PublishProfile>
  </PropertyGroup>
</Project>");

        // Act
        var result = await _dotNetService.UpdatePublishProfileAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false when there is no $(Platform) PublishProfile");
        var content = File.ReadAllText(csprojPath);
        Assert.IsFalse(content.Contains("Condition="), "Content should be untouched");
    }

    [TestMethod]
    public async Task UpdatePublishProfile_MissingFile_ReturnsFalse()
    {
        // Act
        var result = await _dotNetService.UpdatePublishProfileAsync(
            new FileInfo(Path.Combine(_testTempDirectory, "Missing.csproj")),
            TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result);
    }

    #endregion

    #region EnsureEnableMsixToolingAsync Tests

    [TestMethod]
    public async Task EnsureEnableMsixTooling_AlreadyTrue_DoesNotModify()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Msix.csproj");
        var original = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <EnableMsixTooling>true</EnableMsixTooling>
  </PropertyGroup>
</Project>";
        File.WriteAllText(csprojPath, original);

        // Act
        var result = await _dotNetService.EnsureEnableMsixToolingAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false when EnableMsixTooling is already true");
        Assert.AreEqual(original, File.ReadAllText(csprojPath), "Content should be unchanged");
    }

    [TestMethod]
    public async Task EnsureEnableMsixTooling_FalseWithoutComment_SetsTrueAndAddsComment()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Msix.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <EnableMsixTooling>false</EnableMsixTooling>
  </PropertyGroup>
</Project>");

        // Act
        var result = await _dotNetService.EnsureEnableMsixToolingAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "Should return true when flipping false to true");
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<EnableMsixTooling>true</EnableMsixTooling>");
        StringAssert.Contains(content, "Enables targets that generate package layout");
        Assert.IsFalse(content.Contains("<EnableMsixTooling>false"), "Old false value should be gone");
    }

    [TestMethod]
    public async Task EnsureEnableMsixTooling_FalseWithExistingComment_SetsTrueWithoutDuplicateComment()
    {
        // Arrange — an XML comment already sits directly above the element
        var csprojPath = Path.Combine(_testTempDirectory, "Msix.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <!-- existing note -->
    <EnableMsixTooling>false</EnableMsixTooling>
  </PropertyGroup>
</Project>");

        // Act
        var result = await _dotNetService.EnsureEnableMsixToolingAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "Should return true when flipping false to true");
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<EnableMsixTooling>true</EnableMsixTooling>");
        StringAssert.Contains(content, "<!-- existing note -->");
        Assert.IsFalse(content.Contains("Enables targets that generate package layout"),
            "Should not inject a second comment when one already exists");
    }

    [TestMethod]
    public async Task EnsureEnableMsixTooling_UnexpectedValue_DoesNotModify()
    {
        // Arrange — a non true/false value is left untouched
        var csprojPath = Path.Combine(_testTempDirectory, "Msix.csproj");
        var original = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <EnableMsixTooling>maybe</EnableMsixTooling>
  </PropertyGroup>
</Project>";
        File.WriteAllText(csprojPath, original);

        // Act
        var result = await _dotNetService.EnsureEnableMsixToolingAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false for an unexpected existing value");
        Assert.AreEqual(original, File.ReadAllText(csprojPath));
    }

    [TestMethod]
    public async Task EnsureEnableMsixTooling_InsertsAfterRuntimeIdentifier()
    {
        // Arrange — no EnableMsixTooling, but a RuntimeIdentifier is present
        var csprojPath = Path.Combine(_testTempDirectory, "Msix.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  </PropertyGroup>
</Project>");

        // Act
        var result = await _dotNetService.EnsureEnableMsixToolingAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result);
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<EnableMsixTooling>true</EnableMsixTooling>");
        // Should be inserted after the RuntimeIdentifier element
        var ridIdx = content.IndexOf("</RuntimeIdentifier>", StringComparison.Ordinal);
        var msixIdx = content.IndexOf("<EnableMsixTooling>true", StringComparison.Ordinal);
        Assert.IsTrue(ridIdx >= 0 && msixIdx > ridIdx, "EnableMsixTooling should follow RuntimeIdentifier");
    }

    [TestMethod]
    public async Task EnsureEnableMsixTooling_InsertsAfterTargetFramework_WhenNoRuntimeIdentifier()
    {
        // Arrange — no EnableMsixTooling and no RuntimeIdentifier, but a TargetFramework is present
        var csprojPath = Path.Combine(_testTempDirectory, "Msix.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
  </PropertyGroup>
</Project>");

        // Act
        var result = await _dotNetService.EnsureEnableMsixToolingAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result);
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<EnableMsixTooling>true</EnableMsixTooling>");
        var tfmIdx = content.IndexOf("</TargetFramework>", StringComparison.Ordinal);
        var msixIdx = content.IndexOf("<EnableMsixTooling>true", StringComparison.Ordinal);
        Assert.IsTrue(tfmIdx >= 0 && msixIdx > tfmIdx, "EnableMsixTooling should follow TargetFramework");
    }

    [TestMethod]
    public async Task EnsureEnableMsixTooling_InsertsAfterPropertyGroup_WhenNoRidOrTfm()
    {
        // Arrange — no EnableMsixTooling, no RuntimeIdentifier, no TargetFramework
        var csprojPath = Path.Combine(_testTempDirectory, "Msix.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
</Project>");

        // Act
        var result = await _dotNetService.EnsureEnableMsixToolingAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result);
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<EnableMsixTooling>true</EnableMsixTooling>");
        var pgIdx = content.IndexOf("<PropertyGroup>", StringComparison.Ordinal);
        var msixIdx = content.IndexOf("<EnableMsixTooling>true", StringComparison.Ordinal);
        Assert.IsTrue(pgIdx >= 0 && msixIdx > pgIdx, "EnableMsixTooling should be inserted inside the PropertyGroup");
    }

    [TestMethod]
    public async Task EnsureEnableMsixTooling_NoPropertyGroup_DoesNotModify()
    {
        // Arrange — nothing to anchor the insertion to
        var csprojPath = Path.Combine(_testTempDirectory, "Msix.csproj");
        var original = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Some.Package"" Version=""1.0.0"" />
  </ItemGroup>
</Project>";
        File.WriteAllText(csprojPath, original);

        // Act
        var result = await _dotNetService.EnsureEnableMsixToolingAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false when there is no anchor to insert after");
        Assert.AreEqual(original, File.ReadAllText(csprojPath));
    }

    [TestMethod]
    public async Task EnsureEnableMsixTooling_MissingFile_ReturnsFalse()
    {
        // Act
        var result = await _dotNetService.EnsureEnableMsixToolingAsync(
            new FileInfo(Path.Combine(_testTempDirectory, "Missing.csproj")),
            TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result);
    }

    #endregion

    #region RemoveWindowsPackageTypeNoneAsync Tests

    [TestMethod]
    public async Task RemoveWindowsPackageTypeNone_WhenPresent_RemovesElement()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Wpt.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <WindowsPackageType>None</WindowsPackageType>
  </PropertyGroup>
</Project>");

        // Act
        var result = await _dotNetService.RemoveWindowsPackageTypeNoneAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "Should return true when the element was removed");
        var content = File.ReadAllText(csprojPath);
        Assert.IsFalse(content.Contains("WindowsPackageType"), "WindowsPackageType element should be gone");
        StringAssert.Contains(content, "<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>");
    }

    [TestMethod]
    public async Task RemoveWindowsPackageTypeNone_WhenAbsent_ReturnsFalse()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Wpt.csproj");
        var original = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>";
        File.WriteAllText(csprojPath, original);

        // Act
        var result = await _dotNetService.RemoveWindowsPackageTypeNoneAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false when there is no WindowsPackageType element");
        Assert.AreEqual(original, File.ReadAllText(csprojPath));
    }

    [TestMethod]
    public async Task RemoveWindowsPackageTypeNone_MissingFile_ReturnsFalse()
    {
        // Act
        var result = await _dotNetService.RemoveWindowsPackageTypeNoneAsync(
            new FileInfo(Path.Combine(_testTempDirectory, "Missing.csproj")),
            TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result);
    }

    #endregion

    #region AnnotatePackageReferencesAsync Tests

    [TestMethod]
    public async Task AnnotatePackageReferences_AddsCommentAbovePackage()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Ann.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Microsoft.WindowsAppSDK"" Version=""1.0.0"" />
  </ItemGroup>
</Project>");
        var comments = new Dictionary<string, string>
        {
            ["Microsoft.WindowsAppSDK"] = "Windows App SDK runtime",
        };

        // Act
        var result = await _dotNetService.AnnotatePackageReferencesAsync(
            new FileInfo(csprojPath), comments, TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "Should return true when a comment was inserted");
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<!-- Windows App SDK runtime -->");
        var commentIdx = content.IndexOf("<!-- Windows App SDK runtime -->", StringComparison.Ordinal);
        var pkgIdx = content.IndexOf("<PackageReference Include=\"Microsoft.WindowsAppSDK\"", StringComparison.Ordinal);
        Assert.IsTrue(commentIdx >= 0 && commentIdx < pkgIdx, "Comment should appear above the PackageReference");
    }

    [TestMethod]
    public async Task AnnotatePackageReferences_SkipsWhenCommentAlreadyPresent()
    {
        // Arrange — a comment already sits directly above the package reference
        var csprojPath = Path.Combine(_testTempDirectory, "Ann.csproj");
        var original = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <!-- Windows App SDK runtime -->
    <PackageReference Include=""Microsoft.WindowsAppSDK"" Version=""1.0.0"" />
  </ItemGroup>
</Project>";
        File.WriteAllText(csprojPath, original);
        var comments = new Dictionary<string, string>
        {
            ["Microsoft.WindowsAppSDK"] = "Windows App SDK runtime",
        };

        // Act
        var result = await _dotNetService.AnnotatePackageReferencesAsync(
            new FileInfo(csprojPath), comments, TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false when the package already has a comment");
        Assert.AreEqual(original, File.ReadAllText(csprojPath));
    }

    [TestMethod]
    public async Task AnnotatePackageReferences_SkipsWhenPackageNotFound()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Ann.csproj");
        var original = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Some.Other.Package"" Version=""1.0.0"" />
  </ItemGroup>
</Project>";
        File.WriteAllText(csprojPath, original);
        var comments = new Dictionary<string, string>
        {
            ["Microsoft.WindowsAppSDK"] = "Windows App SDK runtime",
        };

        // Act
        var result = await _dotNetService.AnnotatePackageReferencesAsync(
            new FileInfo(csprojPath), comments, TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false when the target package is not referenced");
        Assert.AreEqual(original, File.ReadAllText(csprojPath));
    }

    [TestMethod]
    public async Task AnnotatePackageReferences_AnnotatesMultiplePackages()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Ann.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Microsoft.WindowsAppSDK"" Version=""1.0.0"" />
    <PackageReference Include=""Microsoft.Windows.SDK.BuildTools"" Version=""1.0.0"" />
  </ItemGroup>
</Project>");
        var comments = new Dictionary<string, string>
        {
            ["Microsoft.WindowsAppSDK"] = "SDK runtime",
            ["Microsoft.Windows.SDK.BuildTools"] = "Build tools",
        };

        // Act
        var result = await _dotNetService.AnnotatePackageReferencesAsync(
            new FileInfo(csprojPath), comments, TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result);
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<!-- SDK runtime -->");
        StringAssert.Contains(content, "<!-- Build tools -->");
    }

    [TestMethod]
    public async Task AnnotatePackageReferences_EmptyDictionary_ReturnsFalse()
    {
        // Arrange
        var csprojPath = Path.Combine(_testTempDirectory, "Ann.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Microsoft.WindowsAppSDK"" Version=""1.0.0"" />
  </ItemGroup>
</Project>");

        // Act
        var result = await _dotNetService.AnnotatePackageReferencesAsync(
            new FileInfo(csprojPath), new Dictionary<string, string>(), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false for an empty comment set");
    }

    [TestMethod]
    public async Task AnnotatePackageReferences_MissingFile_ReturnsFalse()
    {
        // Arrange
        var comments = new Dictionary<string, string> { ["X"] = "Y" };

        // Act
        var result = await _dotNetService.AnnotatePackageReferencesAsync(
            new FileInfo(Path.Combine(_testTempDirectory, "Missing.csproj")),
            comments, TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result);
    }

    #endregion

    #region Additional branch coverage

    [TestMethod]
    public void IsTargetFrameworkSupported_WindowsVersionUnparseable_ReturnsFalse()
    {
        // A windows TFM whose SDK version has too many components is not a valid Version
        var result = _dotNetService.IsTargetFrameworkSupported("net8.0-windows1.2.3.4.5");

        Assert.IsFalse(result, "An unparseable Windows SDK version should be unsupported");
    }

    [TestMethod]
    public void GetRecommendedTargetFramework_PlainWindowsTfmWithUnsupportedNet_ReturnsRecommended()
    {
        // net5.0-windows matches the plain-windows TFM shape but 5.0 < 6.0, so it falls through
        var result = _dotNetService.GetRecommendedTargetFramework("net5.0-windows");

        Assert.AreEqual("net10.0-windows10.0.26100.0", result);
    }

    [TestMethod]
    public void GetRecommendedTargetFramework_PlainNetTfmWithUnsupportedNet_ReturnsRecommended()
    {
        // net5.0 matches the plain .NET TFM shape but 5.0 < 6.0, so it falls through
        var result = _dotNetService.GetRecommendedTargetFramework("net5.0");

        Assert.AreEqual("net10.0-windows10.0.26100.0", result);
    }

    [TestMethod]
    public async Task EnsureRuntimeIdentifier_NoTfmOrRids_InsertsAtStartOfPropertyGroup()
    {
        // Arrange — a PropertyGroup with neither TargetFramework nor RuntimeIdentifiers
        var csprojPath = Path.Combine(_testTempDirectory, "NoTfm.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
</Project>");

        // Act
        var result = await _dotNetService.EnsureRuntimeIdentifierAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(result, "Should insert a RuntimeIdentifier at the start of the PropertyGroup");
        var content = File.ReadAllText(csprojPath);
        StringAssert.Contains(content, "<RuntimeIdentifier Condition=");
        var pgIdx = content.IndexOf("<PropertyGroup>", StringComparison.Ordinal);
        var ridIdx = content.IndexOf("<RuntimeIdentifier Condition=", StringComparison.Ordinal);
        var outputIdx = content.IndexOf("<OutputType>", StringComparison.Ordinal);
        Assert.IsTrue(ridIdx > pgIdx && ridIdx < outputIdx,
            "RuntimeIdentifier should be inserted at the start of the PropertyGroup, before existing children");
    }

    [TestMethod]
    public async Task EnsureAssetContentItems_NoProjectClosingTag_ReturnsFalse()
    {
        // Arrange — malformed csproj with no </Project> to anchor the insertion
        var csprojPath = Path.Combine(_testTempDirectory, "NoClose.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>");

        // Act
        var result = await _dotNetService.EnsureAssetContentItemsAsync(
            new FileInfo(csprojPath), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(result, "Should return false when there is no </Project> to insert before");
    }

    #endregion

    #region Process cancellation Tests

    [TestMethod]
    public async Task RunDotnetProcessAsync_Cancellation_KillsProcessTree()
    {
        // Spawn a long-lived process tree (powershell -> ping) so the entireProcessTree kill path is
        // genuinely exercised, then cancel and assert BOTH the root and its descendant are terminated.
        // Before the kill-on-cancel fix, WaitForExitAsync only stopped awaiting and left the child
        // running; asserting the descendant also exits guards the tree-kill (a bare Kill() would leave
        // ping alive and still pass a root-only assertion).
        //
        // The descendant PID is captured via a fully-managed handshake: the PowerShell root starts ping
        // with -PassThru, writes ping's PID to a temp file, then blocks on Wait-Process. The test polls
        // that file — no WMI or Win32 process enumeration required.
        var pidFile = Path.Join(Path.GetTempPath(), $"winapp_treekill_{Guid.NewGuid():N}.pid");
        var script = BuildTreeKillScript(pidFile, "ping.exe");

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var cts = new CancellationTokenSource();
        Task<(int, string, string)>? runTask = null;
        int? rootPid = null;
        int? pingPid = null;
        try
        {
            var pidTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            runTask = DotNetService.RunDotnetProcessAsync(
                startInfo,
                cts.Token,
                onProcessStarted: p => pidTcs.TrySetResult(p.Id));

            // Wait until the root process is actually running (capture its id before the service disposes it).
            // Assign the awaited (non-null) id into a non-nullable local, then mirror it into the nullable
            // placeholder the finally block reads — so the id is never dereferenced through int?.Value.
            var rootPidValue = await pidTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);
            rootPid = rootPidValue;
            Assert.IsFalse(Process.GetProcessById(rootPidValue).HasExited, "The root process should be running before cancellation.");

            // Capture the ping descendant via the PID file the root script writes. The run task is passed
            // in so a root that dies before publishing fails here with its own stderr rather than after a
            // silent wait. The budget is generous because it costs nothing on success (a healthy spawn
            // publishes in well under a second) and only bounds a genuinely wedged agent.
            var pingPidValue = await WaitForPidFileAsync(pidFile, TimeSpan.FromSeconds(30), TestContext.CancellationToken, runTask);
            pingPid = pingPidValue;
            Assert.IsFalse(Process.GetProcessById(pingPidValue).HasExited, "The ping descendant should be running before cancellation.");

            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(async () => await runTask);

            // Both the root of the spawned tree AND its ping descendant must be gone. Kill(entireProcessTree)
            // reaps the whole tree; GetProcessById throws ArgumentException once an id no longer maps to a
            // running process.
            Assert.IsTrue(await WaitForProcessExitAsync(rootPidValue, TestContext.CancellationToken),
                "The spawned root process (powershell.exe) should be killed on cancellation.");
            Assert.IsTrue(await WaitForProcessExitAsync(pingPidValue, TestContext.CancellationToken),
                "The ping descendant should be killed on cancellation (proves the whole process tree was terminated).");
        }
        finally
        {
            // If an assertion or WaitForPidFileAsync threw before (or during) cancellation, the spawned
            // powershell/ping tree could still be running — and the root script would keep re-creating
            // the PID file. Cancel, await the run task, and force-kill any survivors so a failed run
            // can't pollute subsequent tests for up to a minute.
            cts.Cancel();
            if (runTask is not null)
            {
                try
                {
                    await runTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected once the token is cancelled — cleanup must not mask the real failure.
                }
                catch (Exception) when (cts.IsCancellationRequested)
                {
                    // A run that already failed (or failed due to cancellation) — swallow only while
                    // tearing down; the original assertion failure, if any, still surfaces.
                }
            }

            KillProcessTreeIfRunning(rootPid);
            KillProcessTreeIfRunning(pingPid);

            if (File.Exists(pidFile))
            {
                File.Delete(pidFile);
            }
        }
    }

    [TestMethod]
    public async Task RunDotnetProcessAsync_StreamsStdoutAndStderrLive_WhileTaskStillPending()
    {
        // Guards the live-streaming contract that FakeDotNetService structurally cannot: the fake
        // replays every line synchronously *after* the run has produced its result, so a regression
        // where the real OutputDataReceived/ErrorDataReceived callbacks stop firing live — or stderr
        // stops being forwarded — would leave every fake-backed test (including the NewCommand verbose
        // scaffold tests) green. This drives a real child that writes one stdout line and one stderr
        // line, flushes, then blocks reading stdin. Both callbacks MUST therefore fire while the run
        // task is still pending, and the exact streamed text MUST also survive in the returned buffers.
        const string stdoutMarker = "WINAPP_STREAM_STDOUT_a1b2c3";
        const string stderrMarker = "WINAPP_STREAM_STDERR_a1b2c3";

        var script =
            $"[Console]::Out.WriteLine('{stdoutMarker}'); " +
            $"[Console]::Error.WriteLine('{stderrMarker}'); " +
            "[Console]::Out.Flush(); [Console]::Error.Flush(); " +
            "[void][Console]::In.ReadLine()";

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        var stdoutTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Process? child = null;
        int? childPid = null;

        var runTask = DotNetService.RunDotnetProcessAsync(
            startInfo,
            TestContext.CancellationToken,
            onProcessStarted: p =>
            {
                child = p;
                childPid = p.Id;
            },
            onOutputLine: line =>
            {
                if (line.Contains(stdoutMarker, StringComparison.Ordinal))
                {
                    stdoutTcs.TrySetResult(line);
                }
            },
            onErrorLine: line =>
            {
                if (line.Contains(stderrMarker, StringComparison.Ordinal))
                {
                    stderrTcs.TrySetResult(line);
                }
            });

        try
        {
            // Both callbacks must fire from the live pipe readers...
            var streamedStdout = await stdoutTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);
            var streamedStderr = await stderrTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);

            // ...*before* the run completes. The child is blocked reading stdin, so the run task cannot
            // have finished. If it had, the callbacks would be a post-completion replay (the fake's
            // behavior) rather than the live streaming this feature promises.
            Assert.IsFalse(
                runTask.IsCompleted,
                "Callbacks must fire while the run task is still pending (live streaming), not after it completes.");

            // Unblock the child so it exits cleanly (write a line, then close stdin to guarantee EOF).
            Assert.IsNotNull(child, "onProcessStarted must have supplied the child process.");
            await child!.StandardInput.WriteLineAsync();
            child.StandardInput.Close();

            var (exitCode, output, error) = await runTask.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);

            Assert.AreEqual(0, exitCode, $"Child should exit 0. stderr was: {error}");
            StringAssert.Contains(
                output,
                streamedStdout,
                "The exact stdout line streamed live must also remain in the returned Output buffer.");
            StringAssert.Contains(
                error,
                streamedStderr,
                "The exact stderr line streamed live must also remain in the returned Error buffer.");
        }
        finally
        {
            KillProcessTreeIfRunning(childPid);
        }
    }

    /// <summary>
    /// Builds the root script for the tree-kill test: start <paramref name="childExe"/>, publish its PID
    /// to <paramref name="pidFile"/>, then block until it exits.
    /// </summary>
    /// <remarks>
    /// <c>$ErrorActionPreference = 'Stop'</c> is what keeps the published PID file honest, and it is the
    /// fix for a CI flake rather than boilerplate. A <c>Start-Process</c> failure is a NON-terminating
    /// error by default, so the script used to carry straight on to <c>Set-Content</c> with <c>$p</c>
    /// still null, publish a 0-byte file, and leave the reader polling a file that would never parse
    /// until its whole budget expired — reporting that the file "was not written" about a file that had
    /// in fact been written, just empty. Stopping on the error means the root exits instead, with the
    /// real reason on stderr for <see cref="WaitForPidFileAsync"/> to surface.
    /// <para>
    /// Shared with the regression test so the script proven not to publish an empty file is the very
    /// script the tree-kill test runs, and the two cannot drift apart.
    /// </para>
    /// </remarks>
    private static string BuildTreeKillScript(string pidFile, string childExe)
    {
        // Escape single quotes for the single-quoted PowerShell string literals so a temp path
        // containing an apostrophe (e.g. a profile like O'Brien) still produces a valid script.
        var pidFileLiteral = pidFile.Replace("'", "''");
        var childExeLiteral = childExe.Replace("'", "''");

        return
            "$ErrorActionPreference = 'Stop'; " +
            $"$p = Start-Process -FilePath '{childExeLiteral}' -ArgumentList '-n','60','127.0.0.1' -PassThru -WindowStyle Hidden; " +
            "if ($null -eq $p) { throw 'Start-Process returned no process object' }; " +
            $"Set-Content -LiteralPath '{pidFileLiteral}' -Value $p.Id; " +
            "Wait-Process -Id $p.Id";
    }

    /// <summary>Best-effort force-kill of a process (and its tree) by id, ignoring already-exited processes.</summary>
    private static void KillProcessTreeIfRunning(int? pid)
    {
        if (pid is null)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid.Value);
            process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            // No process with this id is running (already exited / id freed) — nothing to clean up.
        }
        catch (InvalidOperationException)
        {
            // The process has already exited between lookup and Kill — nothing to clean up.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The OS refused to terminate the process (access/termination failure) — best-effort only.
        }
        catch (NotSupportedException)
        {
            // Killing the process tree isn't supported in this environment — best-effort only.
        }
    }

    /// <summary>Polls until the process with <paramref name="pid"/> has exited (or its id is reused/freed).</summary>
    private static async Task<bool> WaitForProcessExitAsync(int pid, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                if (Process.GetProcessById(pid).HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(20, cancellationToken);
        }

        return false;
    }

    /// <summary>
    /// Polls for the PID file written by the spawned PowerShell root and returns the descendant PID it
    /// contains. This is the managed alternative to enumerating the process table for a child of the
    /// root: the root itself reports its child's id, so the test needs no WMI or Win32 interop.
    /// </summary>
    /// <remarks>
    /// The read is retried rather than allowed to throw. <see cref="File.Exists"/> turns true the moment
    /// <c>Set-Content</c> creates the file, but PowerShell holds the destination open <em>exclusively</em>
    /// while it writes, and opening it for reading in that window fails with a sharing violation
    /// ("The process cannot access the file ... because it is being used by another process"). Letting
    /// that escape made this test flaky on busy CI agents.
    /// <para>
    /// Retrying is the fix rather than a workaround: because the writer's handle is exclusive, no
    /// <see cref="FileShare"/> mode on the reader can open it during the window (measured: a
    /// <c>FileShare.ReadWrite | FileShare.Delete</c> reader still takes the violation), and publishing
    /// the file by atomic rename only reduces the window rather than closing it.
    /// </para>
    /// <para>
    /// Only <see cref="IOException"/> is caught, which covers both the sharing violation and the
    /// <see cref="FileNotFoundException"/> raised while the file is being replaced. A permissions or
    /// path error surfaces as <see cref="UnauthorizedAccessException"/> and is deliberately left to
    /// propagate, so a genuine failure is reported immediately instead of being turned into a
    /// misleading "was not written within ..." timeout.
    /// </para>
    /// An empty or partially written file is covered by the existing <see cref="int.TryParse(string, out int)"/>
    /// guard, which simply keeps polling until the value parses.
    /// <para>
    /// <paramref name="rootTask"/> is the optional launcher task for the process that publishes the file.
    /// The PID can only ever arrive from that process, so once it has exited the wait is already lost and
    /// running out the clock just delays the failure and throws away the reason. Awaiting the completed
    /// task yields its exit code and captured stderr, turning a blind "not written" timeout into the
    /// actual error the script reported.
    /// </para>
    /// </remarks>
    private static async Task<int> WaitForPidFileAsync(
        string pidFile,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Task<(int ExitCode, string Output, string Error)>? rootTask = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        var everExisted = false;
        string? lastRead = null;
        while (DateTime.UtcNow < deadline)
        {
            string? text = null;
            try
            {
                if (File.Exists(pidFile))
                {
                    everExisted = true;
                    text = (await File.ReadAllTextAsync(pidFile, cancellationToken)).Trim();
                }
            }
            catch (IOException)
            {
                // Still being written (or replaced) by the root script — fall through and retry.
            }

            if (text is not null)
            {
                lastRead = text;
                if (int.TryParse(text, out var pid))
                {
                    return pid;
                }
            }

            if (rootTask is { IsCompleted: true })
            {
                // Awaiting a faulted task rethrows the original exception, which is at least as
                // informative as anything this method could compose.
                var (exitCode, output, error) = await rootTask;
                throw new InvalidOperationException(
                    $"The root process exited with code {exitCode} before publishing a descendant PID to " +
                    $"'{pidFile}'. stderr: {DescribeStream(error)} | stdout: {DescribeStream(output)}");
            }

            await Task.Delay(20, cancellationToken);
        }

        var state = (everExisted, lastRead) switch
        {
            (false, _) => "it was never created",
            (true, null) => "it was created but could never be read (the writer held it exclusively for the whole wait)",
            _ => $"it was created but never contained a parsable PID (last read: '{lastRead}')",
        };
        throw new TimeoutException($"The descendant PID file '{pidFile}' was not usable within {timeout}: {state}.");
    }

    /// <summary>Renders a captured stdio stream for a failure message, collapsing it onto one line.</summary>
    private static string DescribeStream(string? stream)
        => string.IsNullOrWhiteSpace(stream) ? "(empty)" : stream.Trim().ReplaceLineEndings(" ");

    #endregion

    #region PID file read (flake regression)

    [TestMethod]
    public async Task WaitForPidFile_WriterHoldsFileExclusively_RetriesInsteadOfThrowing()
    {
        // Deterministic reproduction of the CI flake. Previously this only failed when a poll happened
        // to land inside PowerShell's write window -- a sub-millisecond target against a 20ms poll, so
        // it survived 400 consecutive local runs while still failing on loaded agents. Holding the file
        // the way Set-Content does removes the timing luck.
        //
        // FileShare.None is what Set-Content actually uses. A reader cannot get in with any share mode
        // (measured: a FileShare.ReadWrite | FileShare.Delete reader still takes the violation), which
        // is why the helper retries instead of opening the file differently.
        //
        // The assertion is on the exception TYPE, and nothing releases the handle: the old code threw
        // IOException on the very first poll, while the retry swallows it and runs out the clock. That
        // keeps the test free of any background task -- an earlier version released the handle from a
        // Task.Run continuation, which made it depend on prompt thread-pool scheduling and could time
        // out on a loaded agent for reasons unrelated to the behavior under test.
        var pidFile = Path.Join(_tempDirectory.FullName, $"pidfile_{Guid.NewGuid():N}.pid");
        await File.WriteAllTextAsync(pidFile, "4242", TestContext.CancellationToken);

        using var exclusive = new FileStream(pidFile, FileMode.Open, FileAccess.Write, FileShare.None);

        await Assert.ThrowsAsync<TimeoutException>(
            async () => await WaitForPidFileAsync(pidFile, TimeSpan.FromMilliseconds(300), TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task WaitForPidFile_NeverWritten_ThrowsTimeout()
    {
        // The retry must not turn a genuine "never appeared" case into an endless wait.
        var pidFile = Path.Join(_tempDirectory.FullName, $"absent_{Guid.NewGuid():N}.pid");

        await Assert.ThrowsAsync<TimeoutException>(
            async () => await WaitForPidFileAsync(pidFile, TimeSpan.FromMilliseconds(200), TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task WaitForPidFile_PopulatedFile_ReturnsThePid()
    {
        // The straightforward success path, kept as its own test because every other test in this
        // region asserts a timeout. Without this, nothing at unit level proves a readable file is
        // actually parsed -- only the tree-kill test does, and it exercises the whole launcher.
        var pidFile = Path.Join(_tempDirectory.FullName, $"populated_{Guid.NewGuid():N}.pid");
        await File.WriteAllTextAsync(pidFile, "4242", TestContext.CancellationToken);

        var pid = await WaitForPidFileAsync(pidFile, TimeSpan.FromSeconds(5), TestContext.CancellationToken);

        Assert.AreEqual(4242, pid);
    }

    [TestMethod]
    public async Task WaitForPidFile_EmptyFile_IsNotParsedAsAPid()
    {
        // Set-Content creates the file before its contents land, so a reader can legitimately observe
        // an empty file. That case is handled by the parse guard, not the exception filter: an empty
        // read must keep polling rather than yield a bogus PID.
        //
        // Deliberately no background writer. An earlier version of this test populated the file from a
        // Task.Run continuation and asserted the value came back, which made it depend on the thread
        // pool scheduling that continuation promptly -- it timed out on a loaded CI agent running 4
        // test workers, reintroducing exactly the kind of flakiness this file is meant to remove.
        // Timing out on a permanently empty file proves the guard without racing anything, and
        // WaitForPidFile_PopulatedFile_ReturnsThePid covers the value actually being read.
        var pidFile = Path.Join(_tempDirectory.FullName, $"empty_{Guid.NewGuid():N}.pid");
        await File.WriteAllTextAsync(pidFile, string.Empty, TestContext.CancellationToken);

        await Assert.ThrowsAsync<TimeoutException>(
            async () => await WaitForPidFileAsync(pidFile, TimeSpan.FromMilliseconds(200), TestContext.CancellationToken));

        Assert.IsTrue(File.Exists(pidFile), "The file existed throughout; the timeout came from the parse guard, not a missing file.");
    }

    [TestMethod]
    public async Task WaitForPidFile_RootExitedWithoutPublishing_FailsFastWithTheRootsError()
    {
        // The PID can only ever come from the root process, so once it has exited the wait is already
        // lost. The old helper had no idea the root was gone and kept polling to the end of its budget,
        // then reported "was not written" -- true, but silent about why. This is the diagnosis half of
        // the CI flake fix: the failure must name the root's exit code and stderr.
        var pidFile = Path.Join(_tempDirectory.FullName, $"rootdied_{Guid.NewGuid():N}.pid");
        var rootTask = Task.FromResult((ExitCode: 1, Output: "some stdout", Error: "Start-Process : cannot find the file"));

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await WaitForPidFileAsync(pidFile, TimeSpan.FromSeconds(30), TestContext.CancellationToken, rootTask));
        sw.Stop();

        StringAssert.Contains(ex.Message, "exited with code 1");
        StringAssert.Contains(ex.Message, "cannot find the file", "The root's stderr is the whole point of the diagnostic.");
        Assert.IsLessThan(TimeSpan.FromSeconds(5), sw.Elapsed,
            "A dead root must fail fast rather than wait out the full timeout.");
    }

    [TestMethod]
    public async Task WaitForPidFile_NeverWritten_TimeoutSaysSo()
    {
        // The timeout message has to distinguish "never created" from "created but unusable", because
        // the two point at completely different causes and the old wording ("was not written") was
        // actively misleading for the second one.
        var pidFile = Path.Join(_tempDirectory.FullName, $"missing_{Guid.NewGuid():N}.pid");

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            async () => await WaitForPidFileAsync(pidFile, TimeSpan.FromMilliseconds(200), TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "never created");
    }

    [TestMethod]
    public async Task WaitForPidFile_EmptyFile_TimeoutReportsItWasCreatedButUnparsable()
    {
        // The exact shape of the CI flake: Set-Content published a 0-byte file, so the file WAS written
        // and the old "was not written within 00:00:15" message sent anyone reading it down the wrong path.
        var pidFile = Path.Join(_tempDirectory.FullName, $"emptymsg_{Guid.NewGuid():N}.pid");
        await File.WriteAllTextAsync(pidFile, string.Empty, TestContext.CancellationToken);

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            async () => await WaitForPidFileAsync(pidFile, TimeSpan.FromMilliseconds(200), TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "never contained a parsable PID");
    }

    [TestMethod]
    public async Task TreeKillScript_WhenTheChildCannotStart_PublishesNoPidFileAtAll()
    {
        // Root-cause regression guard. Start-Process failure is a NON-terminating error, so without
        // $ErrorActionPreference = 'Stop' the script ran on to Set-Content with a null $p and left a
        // 0-byte PID file behind -- which the reader then polled until it timed out. Measured against
        // the unhardened script, this produced a zero-length file every time.
        //
        // Driving the real script through a real PowerShell is the point: the bug lived in PowerShell's
        // error semantics, so nothing short of running it can prove the behaviour.
        var pidFile = Path.Join(_tempDirectory.FullName, $"nostart_{Guid.NewGuid():N}.pid");
        var script = BuildTreeKillScript(pidFile, "winapp_no_such_binary_" + Guid.NewGuid().ToString("N") + ".exe");

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var process = Process.Start(startInfo)!;
        var stderr = await process.StandardError.ReadToEndAsync(TestContext.CancellationToken);
        await process.WaitForExitAsync(TestContext.CancellationToken);

        Assert.IsFalse(
            File.Exists(pidFile),
            $"A failed child start must abort the script before Set-Content, leaving no PID file to mislead the reader. stderr: {stderr}");
        Assert.AreNotEqual(0, process.ExitCode, "The script must report the failure through its exit code.");
        StringAssert.Contains(stderr, "Start-Process", "The reason has to reach stderr, which is what the reader surfaces.");
    }

    #endregion

    #region RunDotnetCommandAsync cancellation (process-tree kill)

    [TestMethod]
    [DoNotParallelize]
    public Task RunDotnetCommandAsync_CancelledMidBuild_KillsChildProcessTree()
        // The non-streaming (buffered) runner is used by classification/evaluate/discovery.
        => AssertLauncherKillsGrandchildTreeOnCancelAsync(
            async (dir, args, ct) => await _dotNetService.RunDotnetCommandAsync(dir, args, ct));

    [TestMethod]
    [DoNotParallelize]
    public Task RunDotnetInheritedAsync_CancelledMidBuild_KillsChildProcessTree()
        // The inherited-stdio runner (native terminal-logger build path) shares the SAME RunDotnetCoreAsync
        // kill-on-cancel policy as the buffered/streaming launchers. This proves the shared tree-kill (H1)
        // reaches grandchildren on the inherit path too — no weaker forked cancel path.
        => AssertLauncherKillsGrandchildTreeOnCancelAsync(
            (dir, args, ct) => _dotNetService.RunDotnetInheritedAsync(dir, args, cancellationToken: ct));

    /// <summary>
    /// Shared grandchild-reap proof for every RunDotnetCoreAsync launcher. On Ctrl+C the runner must kill
    /// the whole dotnet/MSBuild process tree, not just the top-level <c>dotnet</c>. Drives a real
    /// <c>dotnet msbuild</c> target that Execs a powershell "sleeper"; the sleeper writes its own PID to a
    /// file then blocks for two minutes. If cancellation only reaped the parent, the powershell grandchild
    /// would be orphaned and survive. Asserting the recorded PID is dead after cancellation proves
    /// <c>Kill(entireProcessTree: true)</c> reached the descendant. Passing the launcher in keeps the two
    /// callers (buffered + inherited) on one behavior-identical assertion so neither can drift.
    /// </summary>
    private async Task AssertLauncherKillsGrandchildTreeOnCancelAsync(
        Func<DirectoryInfo, string, CancellationToken, Task> launch)
    {
        var dir = new DirectoryInfo(_testTempDirectory);
        var pidFile = Path.Combine(_testTempDirectory, "sleeper.pid");
        var sleeperScript = Path.Combine(_testTempDirectory, "sleeper.ps1");
        await File.WriteAllTextAsync(
            sleeperScript,
            $"$PID | Set-Content -LiteralPath '{pidFile}' -NoNewline; Start-Sleep -Seconds 120",
            TestContext.CancellationToken);

        var projPath = Path.Combine(_testTempDirectory, "sleeper.proj");
        await File.WriteAllTextAsync(
            projPath,
            $@"<Project>
  <Target Name=""Sleep"">
    <Exec Command=""powershell -NoProfile -ExecutionPolicy Bypass -File &quot;&quot;{sleeperScript}&quot;&quot;"" />
  </Target>
</Project>",
            TestContext.CancellationToken);

        using var cts = new CancellationTokenSource();
        // -nodereuse:false keeps the worker node (and thus the Exec'd powershell) a descendant of the
        // process we start, so entireProcessTree can reach it rather than a detached reused node.
        var runTask = launch(
            dir, $"msbuild \"{projPath}\" -t:Sleep -nologo -nodereuse:false", cts.Token);

        // Wait until the grandchild is up (it wrote its PID). If it never starts, dotnet/powershell
        // are not usable here — treat as inconclusive rather than a failure.
        var grandchildPid = await WaitForRunningPidFromFileAsync(
            pidFile, "powershell", TimeSpan.FromSeconds(60), TestContext.CancellationToken);
        if (grandchildPid is null)
        {
            await cts.CancelAsync();
            try { await runTask; } catch { /* ignored */ }
            Assert.Inconclusive("Sleeper grandchild never started; dotnet msbuild/powershell unavailable in this environment.");
            return;
        }

        // Act: cancel the in-flight command.
        await cts.CancelAsync();

        // Assert 1: cancellation surfaces as OperationCanceledException (TaskCanceledException derives
        // from it), matching the streaming sibling's contract.
        Exception? thrown = null;
        try
        {
            await runTask;
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        Assert.IsInstanceOfType<OperationCanceledException>(
            thrown, "Cancelling an in-flight dotnet command must surface OperationCanceledException.");

        // Assert 2: the powershell grandchild is gone — the whole tree was killed, not just the parent.
        var died = await WaitForProcessExitAsync(
            grandchildPid.Value, "powershell", TimeSpan.FromSeconds(30), TestContext.CancellationToken);
        if (!died)
        {
            // Best-effort cleanup so a surviving sleeper doesn't leak across the suite.
            try
            {
                using var survivor = Process.GetProcessById(grandchildPid.Value);
                survivor.Kill(entireProcessTree: true);
            }
            catch { /* ignored */ }
        }

        Assert.IsTrue(
            died,
            $"Grandchild powershell (PID {grandchildPid}) survived cancellation — the dotnet process tree was not killed.");
    }

    /// <summary>
    /// Polls <paramref name="pidFile"/> until it contains the PID of a running process whose name
    /// matches <paramref name="expectedName"/>, or the timeout elapses (returns null).
    /// </summary>
    private static async Task<int?> WaitForRunningPidFromFileAsync(
        string pidFile, string expectedName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(pidFile))
            {
                var text = (await File.ReadAllTextAsync(pidFile, cancellationToken)).Trim();
                if (int.TryParse(text, out var pid) && IsNamedProcessRunning(pid, expectedName))
                {
                    return pid;
                }
            }

            await Task.Delay(200, cancellationToken);
        }

        return null;
    }

    /// <summary>Polls until the named process has exited (returns true) or the timeout elapses.</summary>
    private static async Task<bool> WaitForProcessExitAsync(
        int pid, string expectedName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsNamedProcessRunning(pid, expectedName))
            {
                return true;
            }

            await Task.Delay(200, cancellationToken);
        }

        return !IsNamedProcessRunning(pid, expectedName);
    }

    /// <summary>
    /// True only when a live process with <paramref name="pid"/> exists AND its name matches
    /// <paramref name="expectedName"/> — the name check guards against PID reuse after the tree is
    /// killed reporting a false survivor.
    /// </summary>
    private static bool IsNamedProcessRunning(int pid, string expectedName)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited
                && string.Equals(process.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false; // No process with this id is running.
        }
        catch (InvalidOperationException)
        {
            return false; // Process has exited between lookup and inspection.
        }
    }

    #endregion

    #region ParseSdkMajorVersion

    [TestMethod]
    [DataRow("10.0.302", 10)]
    [DataRow("9.0.100", 9)]
    [DataRow("8.0.100", 8)]
    [DataRow("10.0.100-preview.1.24101.2", 10)]
    [DataRow("  10.0.302  ", 10)]
    [DataRow("10.0.302\n", 10)]
    public void ParseSdkMajorVersion_ValidOutput_ReturnsMajor(string output, int expected)
    {
        Assert.AreEqual(expected, DotNetService.ParseSdkMajorVersion(output));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow(null)]
    [DataRow("not-a-version")]
    [DataRow("x.y.z")]
    public void ParseSdkMajorVersion_InvalidOutput_ReturnsNull(string? output)
    {
        Assert.IsNull(DotNetService.ParseSdkMajorVersion(output));
    }

    #endregion
}

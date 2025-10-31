// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class PackageCommandTests : BaseCommandTests
{
    private IMsixService _msixService = null!;
    private IWorkspaceSetupService _workspaceSetupService = null!;
    private ICertificateService _certificateService = null!;

    /// <summary>
    /// Standard test manifest content for use across multiple tests
    /// </summary>
    private const string StandardTestManifestContent = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
         xmlns:uap=""http://schemas.microsoft.com/appx/manifest/uap/windows10"">
  <Identity Name=""TestPackage""
            Publisher=""CN=TestPublisher""
            Version=""1.0.0.0"" />
  <Properties>
    <DisplayName>Test Package</DisplayName>
    <PublisherDisplayName>Test Publisher</PublisherDisplayName>
    <Description>Test package for integration testing</Description>
    <Logo>Assets\Logo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name=""Windows.Universal"" MinVersion=""10.0.18362.0"" MaxVersionTested=""10.0.26100.0"" />
  </Dependencies>
  <Applications>
    <Application Id=""TestApp"" Executable=""TestApp.exe"" EntryPoint=""TestApp.App"">
      <uap:VisualElements DisplayName=""Test App"" Description=""Test application""
                          BackgroundColor=""#777777"" Square150x150Logo=""Assets\Logo.png"" Square44x44Logo=""Assets\Logo.png"" />
    </Application>
  </Applications>
</Package>";

    [TestInitialize]
    public void Setup()
    {
        _msixService = GetRequiredService<IMsixService>();
        _workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();
        _certificateService = GetRequiredService<ICertificateService>();
    }

    [TestCleanup]
    public void Cleanup()
    {
        // Clean up test certificates from the certificate store
        // This prevents test certificates from accumulating in the CurrentUser\My store
        // and potentially interfering with other tests or system operations.
        // The cleanup logic matches the pattern used in SignCommandTests.cs
        var testCertificatePublishers = new[]
        {
            "CN=TestPublisher",
            "CN=WrongPublisher",
            "CN=ExternalTestPublisher",
            "CN=DifferentPublisher",
            "CN=TestCertificatePublisher",
            "CN=PasswordTestPublisher",
            "CN=CommonValidationPublisher",
            "CN=CertificatePublisher"
        };

        foreach (var publisher in testCertificatePublishers)
        {
            CleanupInvalidTestCertificatesFromStore(publisher);
        }
    }

    /// <summary>
    /// Creates a minimal test package structure with manifest and basic files
    /// </summary>
    private void CreateTestPackageStructure(DirectoryInfo packageDir)
    {
        packageDir.Create();

        // Use the shared standard test manifest content
        File.WriteAllText(Path.Combine(packageDir.FullName, "AppxManifest.xml"), StandardTestManifestContent);

        // Create Assets directory and a fake logo
        var assetsDir = Path.Combine(packageDir.FullName, "Assets");
        Directory.CreateDirectory(assetsDir);
        File.WriteAllText(Path.Combine(assetsDir, "Logo.png"), "fake png content");

        // Create a fake executable
        File.WriteAllText(Path.Combine(packageDir.FullName, "TestApp.exe"), "fake exe content");
    }

    /// <summary>
    /// Creates external test manifest content with different identity for external manifest tests
    /// </summary>
    private static string CreateExternalTestManifest()
    {
        return @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
         xmlns:uap=""http://schemas.microsoft.com/appx/manifest/uap/windows10"">
  <Identity Name=""ExternalTestPackage""
            Publisher=""CN=ExternalTestPublisher""
            Version=""1.0.0.0"" />
  <Properties>
    <DisplayName>External Test Package</DisplayName>
    <PublisherDisplayName>External Test Publisher</PublisherDisplayName>
    <Description>Test package with external manifest</Description>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name=""Windows.Universal"" MinVersion=""10.0.18362.0"" MaxVersionTested=""10.0.26100.0"" />
  </Dependencies>
  <Applications>
    <Application Id=""ExternalTestApp"" Executable=""TestApp.exe"" EntryPoint=""ExternalTestApp.App"">
      <uap:VisualElements DisplayName=""External Test App"" Description=""Test application with external manifest""
                          BackgroundColor=""#333333"" Square150x150Logo=""Assets\Logo.png"" Square44x44Logo=""Assets\Logo.png"" />
    </Application>
  </Applications>
</Package>";
    }

    /// <summary>
    /// Removes test certificates from the CurrentUser\My certificate store
    /// This ensures test certificates don't accumulate and interfere with other tests
    /// </summary>
    /// <param name="subjectName">Certificate subject name to clean up (e.g., "CN=TestPublisher")</param>
    private static void CleanupInvalidTestCertificatesFromStore(string subjectName)
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);

            var certificates = store.Certificates.Find(X509FindType.FindBySubjectName, subjectName.Replace("CN=", ""), false);

            foreach (X509Certificate2 cert in certificates)
            {
                // Remove all test certificates - we don't need datetime logic
                store.Remove(cert);
            }
        }
        catch
        {
            // Ignore cleanup errors - not critical for test functionality
            // The certificate store cleanup is a best-effort operation
        }
    }

    [TestMethod]
    public async Task PackageCommand_ToolDiscovery_FindsCommonBuildTools()
    {
        // This test verifies that common build tools can be discovered after installation
        var commonTools = new[] { "makeappx.exe", "makepri.exe", "mt.exe", "signtool.exe" };
        var foundTools = new List<string>();
        var missingTools = new List<string>();

        // Ensure BuildTools are installed
        var buildToolsPath = await _buildToolsService.EnsureBuildToolsAsync();
        if (buildToolsPath == null)
        {
            Assert.Fail("Cannot run test - BuildTools installation failed.");
            return;
        }

        // Check each common tool
        foreach (var tool in commonTools)
        {
            var toolPath = _buildToolsService.GetBuildToolPath(tool);
            if (toolPath != null)
            {
                foundTools.Add(tool);
                Console.WriteLine($"Found {tool} at: {toolPath}");
            }
            else
            {
                missingTools.Add(tool);
                Console.WriteLine($"Missing: {tool}");
            }
        }

        // Assert - We should find at least some of the common tools
        Assert.IsNotEmpty(foundTools, $"Should find at least some common build tools. Found: [{string.Join(", ", foundTools)}], Missing: [{string.Join(", ", missingTools)}]");

        // Specifically check for makeappx since it's commonly used
        Assert.Contains("makeappx.exe", foundTools, "makeappx.exe should be available in BuildTools");
    }

    [TestMethod]
    [DataRow(null, @"TestPackage.msix", DisplayName = "Null output path defaults to current directory with package name")]
    [DataRow("", @"TestPackage.msix", DisplayName = "Empty output path defaults to current directory with package name")]
    [DataRow("CustomPackage.msix", @"CustomPackage.msix", DisplayName = "Full filename with .msix extension uses as-is")]
    [DataRow("output", @"output\TestPackage.msix", DisplayName = "Directory path without .msix extension combines with package name")]
    [DataRow(@"C:\temp\output", @"C:\temp\output\TestPackage.msix", DisplayName = "Absolute directory path combines with package name")]
    [DataRow(@"C:\temp\AbsolutePackage.msix", @"C:\temp\AbsolutePackage.msix", DisplayName = "Absolute .msix file path uses as-is")]
    public async Task CreateMsixPackageAsync_OutputPathHandling_WorksCorrectly(string? outputPath, string expectedRelativePath)
    {
        // Cleanup
        if (File.Exists(expectedRelativePath))
        {
            File.Delete(expectedRelativePath);
        }

        // Arrange
        var packageDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "TestPackage"));
        CreateTestPackageStructure(packageDir);

        // Create a minimal winapp.yaml to satisfy config requirements
        await File.WriteAllTextAsync(_configService.ConfigPath.FullName, "packages: []");

        var currentDir = GetRequiredService<ICurrentDirectoryProvider>().GetCurrentDirectory();

        // Convert expected relative path to absolute path based on current directory
        string expectedMsixPath;
        if (Path.IsPathRooted(expectedRelativePath))
        {
            // Already absolute - use as-is
            expectedMsixPath = expectedRelativePath;
        }
        else
        {
            // Relative - make absolute based on current directory
            expectedMsixPath = Path.GetFullPath(Path.Combine(currentDir, expectedRelativePath));
        }

        var result = await _msixService.CreateMsixPackageAsync(
            inputFolder: packageDir,
            outputPath: string.IsNullOrEmpty(outputPath) ? null : new FileInfo(Path.IsPathRooted(outputPath) ? outputPath : Path.Combine(currentDir, outputPath)),
            packageName: "TestPackage",
            skipPri: true,
            autoSign: false,
            cancellationToken: CancellationToken.None
        );

        // If we get here without exception, verify the path is correct
        Assert.AreEqual(expectedMsixPath, result.MsixPath.FullName,
            $"Output path calculation incorrect. Input: '{outputPath}', Expected: '{expectedMsixPath}', Actual: '{result.MsixPath}'");
    }

    [TestMethod]
    public async Task CreateMsixPackageAsync_InvalidInputFolder_ThrowsDirectoryNotFoundException()
    {
        // Arrange - Use non-existent directory
        var nonExistentDir = Path.Combine(_tempDirectory.FullName, "NonExistentPackage");

        // Act & Assert
        await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(async () =>
        {
            await _msixService.CreateMsixPackageAsync(
                inputFolder: new DirectoryInfo(nonExistentDir),
                outputPath: _tempDirectory,
                packageName: "TestPackage",
                skipPri: true,
                autoSign: false,
                cancellationToken: CancellationToken.None
            );
        }, "Expected DirectoryNotFoundException when input folder does not exist.");
    }

    [TestMethod]
    public async Task CreateMsixPackageAsync_MissingManifest_ThrowsFileNotFoundException()
    {
        // Arrange - Create directory without manifest
        var packageDir = Path.Combine(_tempDirectory.FullName, "TestPackageNoManifest");
        Directory.CreateDirectory(packageDir);

        // Create a fake executable but no manifest
        File.WriteAllText(Path.Combine(packageDir, "TestApp.exe"), "fake exe content");

        // Act & Assert
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(async () =>
        {
            await _msixService.CreateMsixPackageAsync(
                inputFolder: new DirectoryInfo(packageDir),
                outputPath: _tempDirectory,
                packageName: "TestPackage",
                skipPri: true,
                autoSign: false,
                cancellationToken: CancellationToken.None
            );
        }, "Expected FileNotFoundException when manifest file is missing.");
    }

    [TestMethod]
    public async Task CreateMsixPackageAsync_ExternalManifestWithAssets_CopiesManifestAndAssets()
    {
        // Arrange - Create input folder without manifest
        var packageDir = Path.Combine(_tempDirectory.FullName, "InputPackage");
        Directory.CreateDirectory(packageDir);
        
        // Create the executable in the input folder
        File.WriteAllText(Path.Combine(packageDir, "TestApp.exe"), "fake exe content");

        // Create external manifest directory with manifest and assets
        var externalManifestDir = Path.Combine(_tempDirectory.FullName, "ExternalManifest");
        Directory.CreateDirectory(externalManifestDir);
        
        // Create assets directory in external location
        var externalAssetsDir = Path.Combine(externalManifestDir, "Assets");
        Directory.CreateDirectory(externalAssetsDir);
        
        // Create asset files
        File.WriteAllText(Path.Combine(externalAssetsDir, "Logo.png"), "external logo content");
        File.WriteAllText(Path.Combine(externalAssetsDir, "StoreLogo.png"), "external store logo content");
        
        // Create external manifest that references the assets
        var externalManifestPath = Path.Combine(externalManifestDir, "AppxManifest.xml");
        await File.WriteAllTextAsync(externalManifestPath, CreateExternalTestManifest());

        // Create minimal winapp.yaml
        await File.WriteAllTextAsync(_configService.ConfigPath.FullName, "packages: []");

        var result = await _msixService.CreateMsixPackageAsync(
            inputFolder: new DirectoryInfo(packageDir),
            outputPath: _tempDirectory,
            packageName: "ExternalTestPackage",
            skipPri: true,
            autoSign: false,
            manifestPath: new FileInfo(externalManifestPath),
            cancellationToken: CancellationToken.None
        );

        // If successful, verify the package was created correctly
        Assert.IsNotNull(result, "Result should not be null");
        Assert.Contains("ExternalTestPackage", result.MsixPath.FullName, "Package name should reflect external manifest");
        
        // Verify that assets were accessible during processing
        // The external manifest and assets should still exist
        Assert.IsTrue(File.Exists(externalManifestPath), "External manifest should still exist");
        Assert.IsTrue(File.Exists(Path.Combine(externalAssetsDir, "Logo.png")), "External Logo.png should still exist");
        Assert.IsTrue(File.Exists(Path.Combine(externalAssetsDir, "StoreLogo.png")), "External StoreLogo.png should still exist");
    }

    [TestMethod]
    public async Task CreateMsixPackageAsync_WithSigningAndMatchingPublishers_ShouldSucceed()
    {
        // Arrange - Create package structure
        var packageDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "SigningTestPackage"));
        CreateTestPackageStructure(packageDir);

        // Create a certificate with the same publisher as the manifest
        var certPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "matching_cert.pfx"));
        const string testPassword = "testpassword123";
        const string testPublisher = "CN=TestPublisher"; // This matches StandardTestManifestContent

        var certResult = await _certificateService.GenerateDevCertificateAsync(
            testPublisher, certPath, testPassword);

        // Create minimal winapp.yaml
        await File.WriteAllTextAsync(_configService.ConfigPath.FullName, "packages: []");

        // Act & Assert - This should succeed because publishers match
        var result = await _msixService.CreateMsixPackageAsync(
            inputFolder: packageDir,
            outputPath: _tempDirectory,
            packageName: "SigningTestPackage",
            skipPri: true,
            autoSign: true,
            certificatePath: certPath,
            certificatePassword: testPassword,
            cancellationToken: CancellationToken.None
        );

        // Verify the package was created and signed
        Assert.IsNotNull(result, "Result should not be null");
        Assert.IsTrue(result.Signed, "Package should be marked as signed");
        Assert.IsTrue(result.MsixPath.Exists, "MSIX package file should exist");
    }

    [TestMethod]
    public async Task CreateMsixPackageAsync_WithSigningAndMismatchedPublishers_ShouldFail()
    {
        // Arrange - Create package structure
        var packageDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "MismatchedSigningTest"));
        CreateTestPackageStructure(packageDir);

        // Create a certificate with a DIFFERENT publisher than the manifest
        var certPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "mismatched_cert.pfx"));
        const string testPassword = "testpassword123";
        const string wrongPublisher = "CN=WrongPublisher"; // This does NOT match StandardTestManifestContent

        var certResult = await _certificateService.GenerateDevCertificateAsync(
            wrongPublisher, certPath, testPassword);

        // Create minimal winapp.yaml
        await File.WriteAllTextAsync(_configService.ConfigPath.FullName, "packages: []");

        // Act & Assert - This should fail because publishers don't match
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await _msixService.CreateMsixPackageAsync(
                inputFolder: packageDir,
                outputPath: _tempDirectory,
                packageName: "MismatchedSigningTest",
                skipPri: true,
                autoSign: true,
                certificatePath: certPath,
                certificatePassword: testPassword,
                cancellationToken: CancellationToken.None
            );
        });

        // Verify the error message contains the expected format
        Assert.Contains("Publisher in", ex.Message, "Error should mention manifest publisher");
        Assert.Contains("does not match the publisher in the certificate", ex.Message, "Error should mention certificate publisher mismatch");
        Assert.Contains("CN=TestPublisher", ex.Message, "Error should show manifest publisher");
        Assert.Contains("CN=WrongPublisher", ex.Message, "Error should show certificate publisher");
    }

    [TestMethod]
    public async Task CreateMsixPackageAsync_WithExternalManifestAndMismatchedCertificate_ShouldFail()
    {
        // Arrange - Create input folder and external manifest with different publisher
        var packageDir = Path.Combine(_tempDirectory.FullName, "ExternalMismatchTest");
        Directory.CreateDirectory(packageDir);
        File.WriteAllText(Path.Combine(packageDir, "TestApp.exe"), "fake exe content");

        // Create external manifest with specific publisher
        var externalManifestDir = Path.Combine(_tempDirectory.FullName, "ExternalManifestForMismatch");
        Directory.CreateDirectory(externalManifestDir);
        var externalManifestPath = Path.Combine(externalManifestDir, "AppxManifest.xml");
        await File.WriteAllTextAsync(externalManifestPath, CreateExternalTestManifest()); // Uses "CN=ExternalTestPublisher"

        // Create certificate with different publisher
        var certPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "external_mismatch_cert.pfx"));
        const string testPassword = "testpassword123";
        const string wrongPublisher = "CN=DifferentPublisher";

        await _certificateService.GenerateDevCertificateAsync(
            wrongPublisher, certPath, testPassword);

        // Create minimal winapp.yaml
        await File.WriteAllTextAsync(_configService.ConfigPath.FullName, "packages: []");

        // Act & Assert - Should fail due to publisher mismatch
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await _msixService.CreateMsixPackageAsync(
                inputFolder: new DirectoryInfo(packageDir),
                outputPath: _tempDirectory,
                packageName: "ExternalMismatchTest",
                skipPri: true,
                autoSign: true,
                certificatePath: certPath,
                certificatePassword: testPassword,
                manifestPath: new FileInfo(externalManifestPath),
                cancellationToken: CancellationToken.None
            );
        });

        // Verify error message format
        Assert.Contains("CN=ExternalTestPublisher", ex.Message, "Error should show external manifest publisher");
        Assert.Contains("CN=DifferentPublisher", ex.Message, "Error should show certificate publisher");
    }

    [TestMethod]
    public void CertificateService_ExtractPublisherFromCertificate_ShouldReturnCorrectPublisher()
    {
        // This test uses a pre-generated certificate to test publisher extraction
        // We need to create a test certificate first

        // Arrange
        var certPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "publisher_test_cert.pfx"));
        const string testPassword = "testpassword123";
        const string expectedPublisher = "TestCertificatePublisher";
        const string testPublisherCN = $"CN={expectedPublisher}";

        // Create a test certificate using the existing certificate service
        var certResult = _certificateService.GenerateDevCertificateAsync(
            testPublisherCN, certPath, testPassword).GetAwaiter().GetResult();

        // Act
        var extractedPublisher = CertificateService.ExtractPublisherFromCertificate(certPath, testPassword);

        // Assert
        Assert.AreEqual(expectedPublisher, extractedPublisher, "Extracted publisher should match the expected publisher");
    }

    [TestMethod]
    public void CertificateService_ExtractPublisherFromCertificate_WithNonExistentFile_ShouldThrow()
    {
        // Arrange
        var nonExistentPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "nonexistent.pfx"));

        // Act & Assert
        Assert.ThrowsExactly<FileNotFoundException>(() =>
        {
            CertificateService.ExtractPublisherFromCertificate(nonExistentPath, "password");
        });
    }

    [TestMethod]
    public void CertificateService_ExtractPublisherFromCertificate_WithWrongPassword_ShouldThrow()
    {
        // Arrange - Create test certificate
        var certPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "password_test_cert.pfx"));
        const string correctPassword = "correct123";
        const string wrongPassword = "wrong123";

        _certificateService.GenerateDevCertificateAsync(
            "CN=PasswordTestPublisher", certPath, correctPassword).GetAwaiter().GetResult();

        // Act & Assert
        Assert.ThrowsExactly<InvalidOperationException>(() =>
        {
            CertificateService.ExtractPublisherFromCertificate(certPath, wrongPassword);
        });
    }

    [TestMethod]
    public async Task CertificateService_ValidatePublisherMatch_WithMatchingPublishers_ShouldSucceed()
    {
        // Arrange - Create certificate and manifest with same publisher
        var certPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "matching_validation_cert.pfx"));
        var manifestPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "matching_validation_manifest.xml"));
        const string testPassword = "testpassword123";
        const string commonPublisher = "CN=CommonValidationPublisher";

        // Create certificate
        await _certificateService.GenerateDevCertificateAsync(
            commonPublisher, certPath, testPassword);

        // Create manifest with same publisher
        var manifestContent = StandardTestManifestContent.Replace(
            "CN=TestPublisher", commonPublisher);
        await File.WriteAllTextAsync(manifestPath.FullName, manifestContent);

        // Act & Assert - Should not throw
        await CertificateService.ValidatePublisherMatchAsync(certPath, testPassword, manifestPath);
    }

    [TestMethod]
    public async Task CertificateService_ValidatePublisherMatch_WithMismatchedPublishers_ShouldThrow()
    {
        // Arrange - Create certificate and manifest with different publishers
        var certPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "mismatch_validation_cert.pfx"));
        var manifestPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "mismatch_validation_manifest.xml"));
        const string testPassword = "testpassword123";

        // Create certificate with one publisher
        await _certificateService.GenerateDevCertificateAsync(
            "CN=CertificatePublisher", certPath, testPassword);

        // Create manifest with different publisher
        var manifestContent = StandardTestManifestContent.Replace(
            "CN=TestPublisher", "CN=ManifestPublisher");
        await File.WriteAllTextAsync(manifestPath.FullName, manifestContent);

        // Act & Assert
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await CertificateService.ValidatePublisherMatchAsync(certPath, testPassword, manifestPath);
        });

        // Verify error message format matches requirement
        Assert.Contains($"Error: Publisher in {manifestPath} (CN=ManifestPublisher)", ex.Message);
        Assert.Contains($"does not match the publisher in the certificate {certPath} (CN=CertificatePublisher)", ex.Message);
    }

    [TestMethod]
    public async Task CreateMsixPackageAsync_WithWindowsAppSdkDependency_AddsPackageDependencyOnNewLine()
    {
        // Arrange - Create package structure with a manifest that has Dependencies but no WinAppSDK dependency
        var packageDir = _tempDirectory.CreateSubdirectory("WinAppSdkDependencyTest");

        File.WriteAllText(Path.Combine(packageDir.FullName, "AppxManifest.xml"), StandardTestManifestContent);

        // Create Assets directory and files
        var assetsDir = Path.Combine(packageDir.FullName, "Assets");
        Directory.CreateDirectory(assetsDir);
        File.WriteAllText(Path.Combine(assetsDir, "Logo.png"), "fake png content");
        File.WriteAllText(Path.Combine(packageDir.FullName, "TestApp.exe"), "fake exe content");

        // Create winapp.yaml with Windows App SDK package to trigger dependency injection
        var configContent = @"packages:
  - name: Microsoft.WindowsAppSDK
    version: 2.0.250930001-experimental1";
        await File.WriteAllTextAsync(_configService.ConfigPath.FullName, configContent);

        // Restore
        await _workspaceSetupService.SetupWorkspaceAsync(new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            RequireExistingConfig = true,
            ForceLatestBuildTools = false
        }, CancellationToken.None);

        // Act - Create package (this should trigger the Windows App SDK dependency injection)
        var result = await _msixService.CreateMsixPackageAsync(
            inputFolder: packageDir,
            outputPath: _tempDirectory,
            packageName: "WinAppSdkDependencyTest",
            skipPri: true,
            autoSign: false,
            selfContained: false,
            cancellationToken: CancellationToken.None
        );

        // Assert - Read the manifest from the package and verify PackageDependency is on its own line
        Assert.IsNotNull(result, "Result should not be null");
        Assert.IsTrue(result.MsixPath.Exists, "MSIX package file should exist");

        // Extract and read the manifest from the created package
        var extractDir = Path.Combine(_tempDirectory.FullName, "extracted");
        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(result.MsixPath.FullName, extractDir);

        var extractedManifestPath = Path.Combine(extractDir, "AppxManifest.xml");
        Assert.IsTrue(File.Exists(extractedManifestPath), "Extracted manifest should exist");

        var finalManifestContent = await File.ReadAllTextAsync(extractedManifestPath);

        // Verify the PackageDependency exists
        Assert.Contains("<PackageDependency Name=\"Microsoft.WindowsAppRuntime", finalManifestContent,
            "Manifest should contain Windows App SDK PackageDependency");

        // Verify it's on its own line (not on the same line as </Dependencies>)
        // The pattern we're checking: there should be a newline after the PackageDependency closing tag
        // and before the </Dependencies> tag
        var dependenciesSectionPattern = @"<Dependencies>.*?</Dependencies>";
        var dependenciesMatch = Regex.Match(finalManifestContent, dependenciesSectionPattern, RegexOptions.Singleline);
        Assert.IsTrue(dependenciesMatch.Success, "Should find Dependencies section");

        var dependenciesSection = dependenciesMatch.Value;

        // Check that PackageDependency and </Dependencies> are NOT on the same line
        var lines = dependenciesSection.Split('\n');
        var packageDependencyLine = lines.FirstOrDefault(l => l.Contains("<PackageDependency"));
        var closingTagLine = lines.FirstOrDefault(l => l.Trim() == "</Dependencies>");

        Assert.IsNotNull(packageDependencyLine, "Should find PackageDependency line");
        Assert.IsNotNull(closingTagLine, "Should find closing Dependencies tag line");

        // Verify they are different lines
        Assert.AreNotEqual(packageDependencyLine, closingTagLine,
            "PackageDependency and </Dependencies> should be on separate lines");

        // Also verify proper formatting - there should be whitespace/newline between them
        var packageDependencyIndex = dependenciesSection.IndexOf("<PackageDependency", StringComparison.InvariantCulture);
        var closingBracketIndex = dependenciesSection.IndexOf("/>", packageDependencyIndex, StringComparison.InvariantCulture) + 2;
        var closingTagIndex = dependenciesSection.IndexOf("</Dependencies>", closingBracketIndex, StringComparison.InvariantCulture);

        var betweenContent = dependenciesSection.Substring(closingBracketIndex, closingTagIndex - closingBracketIndex);
        Assert.Contains("\n", betweenContent,
            "There should be a newline between PackageDependency closing and </Dependencies> tag");
    }
}

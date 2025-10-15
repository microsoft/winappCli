using System.Security.Cryptography.X509Certificates;
using Winsdk.Cli.Services;

namespace Winsdk.Cli.Tests;

[TestClass]
public class PackageCommandTests : BaseCommandTests
{
    private string _tempDirectory = null!;
    private string _testWinsdkDirectory = null!;
    private IConfigService _configService = null!;
    private IBuildToolsService _buildToolsService = null!;
    private IMsixService _msixService = null!;
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
        // Create a temporary directory for testing
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"WinsdkPackageTest_{Guid.NewGuid():N}");
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
        _msixService = GetRequiredService<IMsixService>();
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

    /// <summary>
    /// Creates a minimal test package structure with manifest and basic files
    /// </summary>
    private void CreateTestPackageStructure(string packageDir)
    {
        Directory.CreateDirectory(packageDir);

        // Use the shared standard test manifest content
        File.WriteAllText(Path.Combine(packageDir, "AppxManifest.xml"), StandardTestManifestContent);

        // Create Assets directory and a fake logo
        var assetsDir = Path.Combine(packageDir, "Assets");
        Directory.CreateDirectory(assetsDir);
        File.WriteAllText(Path.Combine(assetsDir, "Logo.png"), "fake png content");

        // Create a fake executable
        File.WriteAllText(Path.Combine(packageDir, "TestApp.exe"), "fake exe content");
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
        var buildToolsPath = await _buildToolsService.EnsureBuildToolsAsync(quiet: true);
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
        // Arrange
        var packageDir = Path.Combine(_tempDirectory, "TestPackage");
        CreateTestPackageStructure(packageDir);

        // Create a minimal winsdk.yaml to satisfy config requirements
        await File.WriteAllTextAsync(_configService.ConfigPath, "packages: []");

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
            expectedMsixPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), expectedRelativePath));
        }

        var result = await _msixService.CreateMsixPackageAsync(
            inputFolder: packageDir,
            outputPath: outputPath,
            packageName: "TestPackage",
            skipPri: true,
            autoSign: false,
            verbose: true,
            cancellationToken: CancellationToken.None
        );

        // If we get here without exception, verify the path is correct
        Assert.AreEqual(expectedMsixPath, result.MsixPath,
            $"Output path calculation incorrect. Input: '{outputPath}', Expected: '{expectedMsixPath}', Actual: '{result.MsixPath}'");
    }

    [TestMethod]
    public async Task CreateMsixPackageAsync_InvalidInputFolder_ThrowsDirectoryNotFoundException()
    {
        // Arrange - Use non-existent directory
        var nonExistentDir = Path.Combine(_tempDirectory, "NonExistentPackage");

        // Act & Assert
        await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(async () =>
        {
            await _msixService.CreateMsixPackageAsync(
                inputFolder: nonExistentDir,
                outputPath: null,
                packageName: "TestPackage",
                skipPri: true,
                autoSign: false,
                verbose: true,
                cancellationToken: CancellationToken.None
            );
        }, "Expected DirectoryNotFoundException when input folder does not exist.");
    }

    [TestMethod]
    public async Task CreateMsixPackageAsync_MissingManifest_ThrowsFileNotFoundException()
    {
        // Arrange - Create directory without manifest
        var packageDir = Path.Combine(_tempDirectory, "TestPackageNoManifest");
        Directory.CreateDirectory(packageDir);

        // Create a fake executable but no manifest
        File.WriteAllText(Path.Combine(packageDir, "TestApp.exe"), "fake exe content");

        // Act & Assert
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(async () =>
        {
            await _msixService.CreateMsixPackageAsync(
                inputFolder: packageDir,
                outputPath: null,
                packageName: "TestPackage",
                skipPri: true,
                autoSign: false,
                verbose: true,
                cancellationToken: CancellationToken.None
            );
        }, "Expected FileNotFoundException when manifest file is missing.");
    }

    [TestMethod]
    public async Task CreateMsixPackageAsync_ExternalManifestWithAssets_CopiesManifestAndAssets()
    {
        // Arrange - Create input folder without manifest
        var packageDir = Path.Combine(_tempDirectory, "InputPackage");
        Directory.CreateDirectory(packageDir);
        
        // Create the executable in the input folder
        File.WriteAllText(Path.Combine(packageDir, "TestApp.exe"), "fake exe content");

        // Create external manifest directory with manifest and assets
        var externalManifestDir = Path.Combine(_tempDirectory, "ExternalManifest");
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

        // Create minimal winsdk.yaml
        await File.WriteAllTextAsync(_configService.ConfigPath, "packages: []");

        var result = await _msixService.CreateMsixPackageAsync(
            inputFolder: packageDir,
            outputPath: null,
            packageName: "ExternalTestPackage",
            skipPri: true,
            autoSign: false,
            manifestPath: externalManifestPath,
            verbose: true,
            cancellationToken: CancellationToken.None
        );

        // If successful, verify the package was created correctly
        Assert.IsNotNull(result, "Result should not be null");
        Assert.Contains("ExternalTestPackage", result.MsixPath, "Package name should reflect external manifest");
        
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
        var packageDir = Path.Combine(_tempDirectory, "SigningTestPackage");
        CreateTestPackageStructure(packageDir);

        // Create a certificate with the same publisher as the manifest
        var certPath = Path.Combine(_tempDirectory, "matching_cert.pfx");
        const string testPassword = "testpassword123";
        const string testPublisher = "CN=TestPublisher"; // This matches StandardTestManifestContent

        var certResult = await _certificateService.GenerateDevCertificateAsync(
            testPublisher, certPath, testPassword, verbose: true);

        // Create minimal winsdk.yaml
        await File.WriteAllTextAsync(_configService.ConfigPath, "packages: []");

        // Act & Assert - This should succeed because publishers match
        var result = await _msixService.CreateMsixPackageAsync(
            inputFolder: packageDir,
            outputPath: null,
            packageName: "SigningTestPackage",
            skipPri: true,
            autoSign: true,
            certificatePath: certPath,
            certificatePassword: testPassword,
            verbose: true,
            cancellationToken: CancellationToken.None
        );

        // Verify the package was created and signed
        Assert.IsNotNull(result, "Result should not be null");
        Assert.IsTrue(result.Signed, "Package should be marked as signed");
        Assert.IsTrue(File.Exists(result.MsixPath), "MSIX package file should exist");
    }

    [TestMethod]
    public async Task CreateMsixPackageAsync_WithSigningAndMismatchedPublishers_ShouldFail()
    {
        // Arrange - Create package structure
        var packageDir = Path.Combine(_tempDirectory, "MismatchedSigningTest");
        CreateTestPackageStructure(packageDir);

        // Create a certificate with a DIFFERENT publisher than the manifest
        var certPath = Path.Combine(_tempDirectory, "mismatched_cert.pfx");
        const string testPassword = "testpassword123";
        const string wrongPublisher = "CN=WrongPublisher"; // This does NOT match StandardTestManifestContent

        var certResult = await _certificateService.GenerateDevCertificateAsync(
            wrongPublisher, certPath, testPassword, verbose: true);

        // Create minimal winsdk.yaml
        await File.WriteAllTextAsync(_configService.ConfigPath, "packages: []");

        // Act & Assert - This should fail because publishers don't match
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await _msixService.CreateMsixPackageAsync(
                inputFolder: packageDir,
                outputPath: null,
                packageName: "MismatchedSigningTest",
                skipPri: true,
                autoSign: true,
                certificatePath: certPath,
                certificatePassword: testPassword,
                verbose: true,
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
        var packageDir = Path.Combine(_tempDirectory, "ExternalMismatchTest");
        Directory.CreateDirectory(packageDir);
        File.WriteAllText(Path.Combine(packageDir, "TestApp.exe"), "fake exe content");

        // Create external manifest with specific publisher
        var externalManifestDir = Path.Combine(_tempDirectory, "ExternalManifestForMismatch");
        Directory.CreateDirectory(externalManifestDir);
        var externalManifestPath = Path.Combine(externalManifestDir, "AppxManifest.xml");
        await File.WriteAllTextAsync(externalManifestPath, CreateExternalTestManifest()); // Uses "CN=ExternalTestPublisher"

        // Create certificate with different publisher
        var certPath = Path.Combine(_tempDirectory, "external_mismatch_cert.pfx");
        const string testPassword = "testpassword123";
        const string wrongPublisher = "CN=DifferentPublisher";

        await _certificateService.GenerateDevCertificateAsync(
            wrongPublisher, certPath, testPassword, verbose: true);

        // Create minimal winsdk.yaml
        await File.WriteAllTextAsync(_configService.ConfigPath, "packages: []");

        // Act & Assert - Should fail due to publisher mismatch
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await _msixService.CreateMsixPackageAsync(
                inputFolder: packageDir,
                outputPath: null,
                packageName: "ExternalMismatchTest",
                skipPri: true,
                autoSign: true,
                certificatePath: certPath,
                certificatePassword: testPassword,
                manifestPath: externalManifestPath,
                verbose: true,
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
        var certPath = Path.Combine(_tempDirectory, "publisher_test_cert.pfx");
        const string testPassword = "testpassword123";
        const string expectedPublisher = "TestCertificatePublisher";
        const string testPublisherCN = $"CN={expectedPublisher}";

        // Create a test certificate using the existing certificate service
        var certResult = _certificateService.GenerateDevCertificateAsync(
            testPublisherCN, certPath, testPassword, verbose: true).GetAwaiter().GetResult();

        // Act
        var extractedPublisher = CertificateService.ExtractPublisherFromCertificate(certPath, testPassword);

        // Assert
        Assert.AreEqual(expectedPublisher, extractedPublisher, "Extracted publisher should match the expected publisher");
    }

    [TestMethod]
    public void CertificateService_ExtractPublisherFromCertificate_WithNonExistentFile_ShouldThrow()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_tempDirectory, "nonexistent.pfx");

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
        var certPath = Path.Combine(_tempDirectory, "password_test_cert.pfx");
        const string correctPassword = "correct123";
        const string wrongPassword = "wrong123";

        _certificateService.GenerateDevCertificateAsync(
            "CN=PasswordTestPublisher", certPath, correctPassword, verbose: true).GetAwaiter().GetResult();

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
        var certPath = Path.Combine(_tempDirectory, "matching_validation_cert.pfx");
        var manifestPath = Path.Combine(_tempDirectory, "matching_validation_manifest.xml");
        const string testPassword = "testpassword123";
        const string commonPublisher = "CN=CommonValidationPublisher";

        // Create certificate
        await _certificateService.GenerateDevCertificateAsync(
            commonPublisher, certPath, testPassword, verbose: true);

        // Create manifest with same publisher
        var manifestContent = StandardTestManifestContent.Replace(
            "CN=TestPublisher", commonPublisher);
        await File.WriteAllTextAsync(manifestPath, manifestContent);

        // Act & Assert - Should not throw
        await CertificateService.ValidatePublisherMatchAsync(certPath, testPassword, manifestPath);
    }

    [TestMethod]
    public async Task CertificateService_ValidatePublisherMatch_WithMismatchedPublishers_ShouldThrow()
    {
        // Arrange - Create certificate and manifest with different publishers
        var certPath = Path.Combine(_tempDirectory, "mismatch_validation_cert.pfx");
        var manifestPath = Path.Combine(_tempDirectory, "mismatch_validation_manifest.xml");
        const string testPassword = "testpassword123";

        // Create certificate with one publisher
        await _certificateService.GenerateDevCertificateAsync(
            "CN=CertificatePublisher", certPath, testPassword, verbose: true);

        // Create manifest with different publisher
        var manifestContent = StandardTestManifestContent.Replace(
            "CN=TestPublisher", "CN=ManifestPublisher");
        await File.WriteAllTextAsync(manifestPath, manifestContent);

        // Act & Assert
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await CertificateService.ValidatePublisherMatchAsync(certPath, testPassword, manifestPath);
        });

        // Verify error message format matches requirement
        Assert.Contains($"Error: Publisher in {manifestPath} (CN=ManifestPublisher)", ex.Message);
        Assert.Contains($"does not match the publisher in the certificate {certPath} (CN=CertificatePublisher)", ex.Message);
    }
}

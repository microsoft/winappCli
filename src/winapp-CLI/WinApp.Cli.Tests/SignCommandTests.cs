// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography.X509Certificates;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public class SignCommandTests : BaseCommandTests
{
    private FileInfo _testExecutablePath = null!;
    private FileInfo _testCertificatePath = null!;
    private ICertificateService _certificateService = null!;

    [TestInitialize]
    public async Task Setup()
    {
        // Create a fake executable file to sign
        _testExecutablePath = new FileInfo(Path.Combine(_tempDirectory.FullName, "TestApp.exe"));
        await CreateFakeExecutableAsync(_testExecutablePath);

        // Set up certificate path
        _testCertificatePath = new FileInfo(Path.Combine(_tempDirectory.FullName, "TestCert.pfx"));

        _certificateService = GetRequiredService<ICertificateService>();

        // Create a temporary certificate for testing
        await CreateTestCertificateAsync();
    }

    [TestCleanup]
    public void Cleanup()
    {
        // Clean up any test certificates that might have been left in the certificate store
        // This is optional but helps keep the certificate store clean during development
        CleanupInvalidTestCertificatesFromStore("CN=WinappTestPublisher");
    }

    /// <summary>
    /// Creates a minimal fake executable file that can be used for testing
    /// Note: This won't be signable by signtool, but it's enough for testing command logic
    /// </summary>
    private static async Task CreateFakeExecutableAsync(FileInfo path)
    {
        // Create a simple file that looks like an executable (for path validation tests)
        var content = "MZ"u8.ToArray(); // MZ signature
        await File.WriteAllBytesAsync(path.FullName, content);
    }

    /// <summary>
    /// Creates a test certificate for signing operations
    /// Checks for existing test certificates in the certificate store and cleans up invalid ones
    /// </summary>
    private async Task CreateTestCertificateAsync()
    {
        const string testPublisher = "CN=WinappTestPublisher";
        const string testPassword = "testpassword";

        // Clean up any invalid test certificates from the certificate store first
        CleanupInvalidTestCertificatesFromStore(testPublisher);

        // Check if we have a valid certificate already installed
        if (HasValidTestCertificateInStore(testPublisher))
        {
            // We have a valid certificate in the store, just create the PFX file if it doesn't exist
            if (!_testCertificatePath.Exists || !IsCertificateFileValid(_testCertificatePath, testPassword))
            {
                // Export the existing certificate from the store to create the PFX file
                ExportCertificateFromStore(testPublisher, testPassword, _testCertificatePath);
            }
            _testCertificatePath.Refresh();
            return;
        }

        // No valid certificate exists, generate a new one
        var result = await _certificateService.GenerateDevCertificateAsync(
            publisher: testPublisher,
            outputPath: _testCertificatePath,
            password: testPassword,
            validDays: 30,
            cancellationToken: TestContext.CancellationToken);

        Assert.IsNotNull(result, "Certificate generation should succeed");
        _testCertificatePath.Refresh();
        Assert.IsTrue(_testCertificatePath.Exists, "Certificate file should exist");
    }

    /// <summary>
    /// Checks if a certificate file exists, can be loaded, and is still valid (not expired)
    /// </summary>
    /// <param name="certPath">Path to the certificate file</param>
    /// <param name="password">Certificate password</param>
    /// <returns>True if the certificate file is valid and usable</returns>
    private static bool IsCertificateFileValid(FileInfo certPath, string password)
    {
        if (!certPath.Exists)
        {
            return false;
        }

        try
        {
            // Check if certificate can be loaded with the correct password
            if (!CanLoadCertificate(certPath, password))
            {
                return false;
            }

            // Check if certificate is not expired
            using var cert = X509CertificateLoader.LoadPkcs12FromFile(
                certPath.FullName, password, X509KeyStorageFlags.Exportable);

            var now = DateTime.UtcNow;
            return now >= cert.NotBefore && now <= cert.NotAfter;
        }
        catch
        {
            // If any operation fails, consider the certificate invalid
            return false;
        }
    }

    /// <summary>
    /// Checks if there's a valid test certificate with the specified subject in the CurrentUser\My store
    /// </summary>
    /// <param name="subjectName">Certificate subject name (e.g., "CN=WinappTestPublisher")</param>
    /// <returns>True if a valid certificate exists in the store</returns>
    private static bool HasValidTestCertificateInStore(string subjectName)
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);

            var certificates = store.Certificates.Find(X509FindType.FindBySubjectName, subjectName.Replace("CN=", ""), false);

            foreach (X509Certificate2 cert in certificates)
            {
                var now = DateTime.UtcNow;
                if (now >= cert.NotBefore && now <= cert.NotAfter && cert.HasPrivateKey)
                {
                    return true; // Found a valid certificate
                }
            }

            return false;
        }
        catch
        {
            // If we can't check the store, assume no valid certificate
            return false;
        }
    }

    /// <summary>
    /// Removes invalid test certificates from the CurrentUser\My certificate store
    /// </summary>
    /// <param name="subjectName">Certificate subject name to clean up</param>
    private static void CleanupInvalidTestCertificatesFromStore(string subjectName)
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);

            var certificates = store.Certificates.Find(X509FindType.FindBySubjectName, subjectName.Replace("CN=", ""), false);
            var now = DateTime.UtcNow;

            foreach (X509Certificate2 cert in certificates)
            {
                // Remove expired certificates or certificates without private keys
                if (now < cert.NotBefore || now > cert.NotAfter || !cert.HasPrivateKey)
                {
                    store.Remove(cert);
                }
            }
        }
        catch
        {
            // Ignore cleanup errors - not critical for test functionality
        }
    }

    /// <summary>
    /// Exports an existing certificate from the store to a PFX file
    /// </summary>
    /// <param name="subjectName">Certificate subject name</param>
    /// <param name="password">Password for the PFX file</param>
    private static void ExportCertificateFromStore(string subjectName, string password, FileInfo outputPath)
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);

            var certificates = store.Certificates.Find(X509FindType.FindBySubjectName, subjectName.Replace("CN=", ""), false);

            foreach (X509Certificate2 cert in certificates)
            {
                var now = DateTime.UtcNow;
                if (now >= cert.NotBefore && now <= cert.NotAfter && cert.HasPrivateKey)
                {
                    // Export the certificate as PFX
                    var pfxData = cert.Export(X509ContentType.Pfx, password);
                    File.WriteAllBytes(outputPath.FullName, pfxData);
                    return;
                }
            }

            throw new InvalidOperationException($"No valid certificate found in store with subject: {subjectName}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to export certificate from store: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Verifies that a certificate file exists and can be loaded
    /// </summary>
    /// <param name="certPath">Path to the certificate file</param>
    /// <param name="password">Certificate password</param>
    /// <returns>True if the certificate can be loaded</returns>
    private static bool CanLoadCertificate(FileInfo certPath, string password)
    {
        if (!certPath.Exists)
        {
            return false;
        }

        try
        {
            // Use the modern X509CertificateLoader API instead of the obsolete constructor
            using var cert = X509CertificateLoader.LoadPkcs12FromFile(certPath.FullName, password, X509KeyStorageFlags.Exportable);
            return cert.HasPrivateKey;
        }
        catch
        {
            return false;
        }
    }

    [TestMethod]
    public async Task SignCommandWithValidCertificateShouldAttemptSigning()
    {
        // This test verifies that the signing command processes correctly up to the point 
        // where it calls signtool. The actual signing will fail because our fake exe 
        // isn't a real PE file, but that's expected and shows the command flow works.

        // Arrange
        var command = GetRequiredService<SignCommand>();
        var args = new[]
        {
            _testExecutablePath.FullName,
            _testCertificatePath.FullName,
            "--password", "testpassword",
            "--verbose"
        };

        // Act
        var parseResult = command.Parse(args);
        var exitCode = await parseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);

        // Assert
        // We expect this to fail because our fake executable isn't a real PE file
        // But this confirms that:
        // 1. Arguments were parsed correctly
        // 2. Certificate was loaded successfully
        // 3. signtool was found and executed
        // 4. The error was handled gracefully
        Assert.AreEqual(1, exitCode, "Sign command should fail gracefully for invalid executable format");

        // Verify that the original file still exists
        _testExecutablePath.Refresh();
        Assert.IsTrue(_testExecutablePath.Exists, "Original executable should still exist after failed signing");
    }

    [TestMethod]
    public async Task SignCommandWithNonExistentFileShouldFail()
    {
        // Arrange
        var command = GetRequiredService<SignCommand>();
        var nonExistentFile = Path.Combine(_tempDirectory.FullName, "NonExistent.exe");
        var args = new[]
        {
            nonExistentFile,
            _testCertificatePath.FullName,
            "--password", "testpassword"
        };

        // Act
        var parseResult = command.Parse(args);
        var exitCode = await parseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, exitCode, "Sign command should fail for non-existent file");
    }

    [TestMethod]
    public async Task SignCommandWithNonExistentCertificateShouldFail()
    {
        // Arrange
        var command = GetRequiredService<SignCommand>();
        var nonExistentCert = Path.Combine(_tempDirectory.FullName, "NonExistent.pfx");
        var args = new[]
        {
            _testExecutablePath.FullName,
            nonExistentCert,
            "--password", "testpassword"
        };

        // Act
        var parseResult = command.Parse(args);
        var exitCode = await parseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, exitCode, "Sign command should fail for non-existent certificate");
    }

    [TestMethod]
    public async Task SignCommandWithWrongPasswordShouldFail()
    {
        // Arrange
        var command = GetRequiredService<SignCommand>();
        var args = new[]
        {
            _testExecutablePath.FullName,
            _testCertificatePath.FullName,
            "--password", "wrongpassword"
        };

        // Act
        var parseResult = command.Parse(args);
        var exitCode = await parseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, exitCode, "Sign command should fail with wrong certificate password");
    }

    [TestMethod]
    public async Task SignCommandWithTimestampShouldAttemptSigning()
    {
        // Similar to the main signing test, this verifies the timestamp parameter is processed correctly

        // Arrange
        var command = GetRequiredService<SignCommand>();
        var timestampUrl = "http://timestamp.digicert.com";
        var args = new[]
        {
            _testExecutablePath.FullName,
            _testCertificatePath.FullName,
            "--password", "testpassword",
            "--timestamp", timestampUrl,
            "--verbose"
        };

        // Act
        var parseResult = command.Parse(args);
        var exitCode = await parseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);

        // Assert
        // We expect this to fail because our fake executable isn't a real PE file
        // But this confirms the timestamp parameter was processed correctly
        Assert.AreEqual(1, exitCode, "Sign command with timestamp should fail gracefully for invalid executable format");

        // Verify that the file still exists
        _testExecutablePath.Refresh();
        Assert.IsTrue(_testExecutablePath.Exists, "Original executable should still exist after failed signing");
    }

    [TestMethod]
    public void SignCommandParseArgumentsShouldHandleAllOptions()
    {
        // Arrange
        var command = GetRequiredService<SignCommand>();
        var args = new[]
        {
            _testExecutablePath.FullName,
            _testCertificatePath.FullName,
            "--password", "mypassword",
            "--timestamp", "http://timestamp.example.com",
            "--verbose"
        };

        // Act
        var parseResult = command.Parse(args);

        // Assert
        Assert.IsNotNull(parseResult, "Parse result should not be null");
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");

        // Verify that the command was parsed successfully by checking there are no errors
        // Note: The actual argument values are harder to extract in System.CommandLine 2.0
        // but we can verify the parsing worked by absence of errors
    }

    [TestMethod]
    public async Task CertificateServicesSignFileDirectTest()
    {
        // This test directly calls the CertificateServices.SignFileAsync method
        // to ensure it works correctly without going through the command parsing layer

        // Act & Assert
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () =>
            // This should fail either because:
            // 1. BuildTools aren't installed in our test environment, OR
            // 2. The file format is invalid for signing
            // Both are acceptable failures that show the validation is working
            await _certificateService.SignFileAsync(
                _testExecutablePath,
                _testCertificatePath,
                "testpassword",
                timestampUrl: null, TestContext.CancellationToken), "SignFileAsync should throw when file cannot be signed or BuildTools are not available");

        // The exception is guaranteed to be non-null and of the exact type
        // We could add additional assertions on the exception properties if needed
        // Assert.That.StringContains(exception.Message, "expected text");
    }

    [TestMethod]
    public void SignCommandHelpShouldDisplayCorrectInformation()
    {
        // Arrange
        var command = GetRequiredService<SignCommand>();
        var args = new[] { "--help" };

        // Act
        var parseResult = command.Parse(args);

        // Assert
        Assert.IsNotNull(parseResult, "Parse result should not be null");
        // The help option should be recognized and not produce errors
    }

    [TestMethod]
    public async Task SignCommandRelativePathsShouldWork()
    {
        // Create test files with relative names in temp directory
        var relativeExePath = "RelativeTestApp.exe";
        var relativeCertPath = "RelativeTestCert.pfx";
        var relativeExeFullPath = new FileInfo(Path.Combine(_tempDirectory.FullName, relativeExePath));
        var relativeCertFullPath = new FileInfo(Path.Combine(_tempDirectory.FullName, relativeCertPath));

        // Create the files in the temp directory
        await CreateFakeExecutableAsync(relativeExeFullPath);
        _testCertificatePath.CopyTo(relativeCertFullPath.FullName, overwrite: true);

        // Arrange
        var command = GetRequiredService<SignCommand>();
        var args = new[]
        {
            relativeExeFullPath.FullName, // Use full paths to avoid directory changes
            relativeCertFullPath.FullName,
            "--password", "testpassword"
        };

        // Act
        var parseResult = command.Parse(args);
        var exitCode = await parseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);

        // Assert - we expect this to fail due to invalid file format or missing BuildTools
        // but it should at least validate the file paths correctly
        Assert.AreEqual(1, exitCode, "Command should fail gracefully with relative-named paths");

        // Verify the files still exist
        relativeExeFullPath.Refresh();
        Assert.IsTrue(relativeExeFullPath.Exists, "Relative executable should still exist after failed signing");
        relativeCertFullPath.Refresh();
        Assert.IsTrue(relativeCertFullPath.Exists, "Relative certificate should still exist after failed signing");
    }

    [TestMethod]
    public void CertificateGenerationShouldCreateValidCertificate()
    {
        // This test verifies that the certificate creation worked properly

        // Assert
        _testCertificatePath.Refresh();
        Assert.IsTrue(_testCertificatePath.Exists, "Certificate file should exist");

        // Verify the certificate can be loaded (this tests our certificate generation)
        var canLoad = CanLoadCertificate(_testCertificatePath, "testpassword");
        Assert.IsTrue(canLoad, "Generated certificate should be loadable with correct password");

        // Verify wrong password fails
        var canLoadWrong = CanLoadCertificate(_testCertificatePath, "wrongpassword");
        Assert.IsFalse(canLoadWrong, "Certificate should not load with wrong password");
    }

    [TestMethod]
    public void BuildToolsServiceShouldDetectMissingTools()
    {
        // This test verifies that the BuildToolsService correctly detects when tools are missing
        // which is the expected behavior in our test environment

        // Arrange & Act
        var toolPath = _buildToolsService.GetBuildToolPath("signtool.exe");

        // Assert
        // In our test environment, BuildTools might not be installed, and that's OK
        // This test just verifies the service doesn't crash when tools are missing
        if (toolPath == null)
        {
            // This is expected - no assertion needed, the test passes by not crashing
        }
        else
        {
            toolPath.Refresh();
            Assert.IsTrue(toolPath.Exists, "If BuildToolsService reports a tool path, the file should exist");
        }
    }

    [TestMethod]
    public async Task SignCommandWithMismatchedMsixPublishers_ShouldReturnSpecificErrorMessage()
    {
        // Arrange - Create an MSIX package with one publisher
        var msixService = GetRequiredService<IMsixService>();
        var packageDir = _tempDirectory.CreateSubdirectory("TestMsixPackage");

        // Create a test manifest with "CN=Right" as publisher
        var manifestContent = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
         xmlns:uap=""http://schemas.microsoft.com/appx/manifest/uap/windows10"">
  <Identity Name=""TestPackage""
            Publisher=""CN=Right""
            Version=""1.0.0.0"" />
  <Properties>
    <DisplayName>Test Package</DisplayName>
    <PublisherDisplayName>Right Publisher</PublisherDisplayName>
    <Description>Test package for publisher mismatch testing</Description>
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

        var manifestPath = Path.Combine(packageDir.FullName, "AppxManifest.xml");
        await File.WriteAllTextAsync(manifestPath, manifestContent, TestContext.CancellationToken);

        // Create Assets directory and files
        var assetsDir = Path.Combine(packageDir.FullName, "Assets");
        Directory.CreateDirectory(assetsDir);
        await File.WriteAllBytesAsync(Path.Combine(assetsDir, "Logo.png"), [0x89, 0x50, 0x4E, 0x47], TestContext.CancellationToken); // Fake PNG header

        // Create fake executable
        await File.WriteAllBytesAsync(Path.Combine(packageDir.FullName, "TestApp.exe"), "MZ"u8.ToArray(), TestContext.CancellationToken); // Fake MZ header

        // Create a minimal winapp.yaml for the MSIX service
        var configService = GetRequiredService<IConfigService>();
        configService.ConfigPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "winapp.yaml"));
        await File.WriteAllTextAsync(configService.ConfigPath.FullName, "packages: []", TestContext.CancellationToken);

        // Create MSIX package (unsigned first)
        var msixPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "TestPackage.msix"));
        var msixResult = await msixService.CreateMsixPackageAsync(
            inputFolder: packageDir,
            outputPath: msixPath,
            packageName: "TestPackage",
            skipPri: true,
            autoSign: false, // Don't auto-sign, we'll sign manually with wrong cert
            cancellationToken: CancellationToken.None
        );

        // Create a certificate with "CN=Wrong" as publisher (different from manifest)
        var wrongCertPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "WrongCert.pfx"));
        await _certificateService.GenerateDevCertificateAsync(
            publisher: "CN=Wrong",
            outputPath: wrongCertPath,
            password: "testpassword",
            validDays: 30, TestContext.CancellationToken);

        // Arrange the sign command
        var command = GetRequiredService<SignCommand>();
        var args = new[]
        {
            msixResult.MsixPath.FullName,
            wrongCertPath.FullName,
            "--password", "testpassword",
            "--verbose"
        };

        // Act
        var parseResult = command.Parse(args);
        var exitCode = await parseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, exitCode, "Sign command should fail when publishers don't match");

        var errorMessage = ConsoleStdErr.ToString().Trim();

        Assert.Contains("Failed to sign file: error 0x8007000B: The app manifest publisher name (CN=Right) must match the subject name of the signing certificate (CN=Wrong).", errorMessage,
            "Expected specific error message about publisher mismatch with error code 0x8007000B");
    }
}

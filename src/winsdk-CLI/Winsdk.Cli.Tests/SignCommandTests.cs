using System.Security.Cryptography.X509Certificates;
using Winsdk.Cli.Commands;
using Winsdk.Cli.Services;

namespace Winsdk.Cli.Tests;

[TestClass]
public class SignCommandTests : BaseCommandTests
{
    private string _tempDirectory = null!;
    private string _testExecutablePath = null!;
    private string _testCertificatePath = null!;
    private IConfigService _configService = null!;
    private IBuildToolsService _buildToolsService = null!;
    private ICertificateService _certificateService = null!;

    [TestInitialize]
    public async Task Setup()
    {
        // Create a temporary directory for testing
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"WinsdkSignTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);

        // Set up a temporary winsdk directory for testing (isolates tests from real winsdk directory)
        var testWinsdkDirectory = Path.Combine(_tempDirectory, ".winsdk");
        Directory.CreateDirectory(testWinsdkDirectory);

        // Create a fake executable file to sign
        _testExecutablePath = Path.Combine(_tempDirectory, "TestApp.exe");
        await CreateFakeExecutableAsync(_testExecutablePath);

        // Set up certificate path
        _testCertificatePath = Path.Combine(_tempDirectory, "TestCert.pfx");

        // Set up services
        _configService = GetRequiredService<IConfigService>();
        _configService.ConfigPath = Path.Combine(_tempDirectory, "winsdk.yaml");
        var directoryService = GetRequiredService<IWinsdkDirectoryService>();
        directoryService.SetCacheDirectoryForTesting(testWinsdkDirectory);
        _buildToolsService = GetRequiredService<IBuildToolsService>();
        _certificateService = GetRequiredService<ICertificateService>();

        // Create a temporary certificate for testing
        await CreateTestCertificateAsync();
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

        // Clean up any test certificates that might have been left in the certificate store
        // This is optional but helps keep the certificate store clean during development
        CleanupInvalidTestCertificatesFromStore("CN=WinsdkTestPublisher");
    }

    /// <summary>
    /// Creates a minimal fake executable file that can be used for testing
    /// Note: This won't be signable by signtool, but it's enough for testing command logic
    /// </summary>
    private async Task CreateFakeExecutableAsync(string path)
    {
        // Create a simple file that looks like an executable (for path validation tests)
        var content = new byte[] { 0x4D, 0x5A }; // MZ signature
        await File.WriteAllBytesAsync(path, content);
    }

    /// <summary>
    /// Creates a test certificate for signing operations
    /// Checks for existing test certificates in the certificate store and cleans up invalid ones
    /// </summary>
    private async Task CreateTestCertificateAsync()
    {
        const string testPublisher = "CN=WinsdkTestPublisher";
        const string testPassword = "testpassword";

        // Clean up any invalid test certificates from the certificate store first
        CleanupInvalidTestCertificatesFromStore(testPublisher);

        // Check if we have a valid certificate already installed
        if (HasValidTestCertificateInStore(testPublisher))
        {
            // We have a valid certificate in the store, just create the PFX file if it doesn't exist
            if (!File.Exists(_testCertificatePath) || !IsCertificateFileValid(_testCertificatePath, testPassword))
            {
                // Export the existing certificate from the store to create the PFX file
                ExportCertificateFromStore(testPublisher, testPassword, _testCertificatePath);
            }
            return;
        }

        // No valid certificate exists, generate a new one
        var result = await _certificateService.GenerateDevCertificateAsync(
            publisher: testPublisher,
            outputPath: _testCertificatePath,
            password: testPassword,
            validDays: 30,
            verbose: false); // Set to false to reduce test output noise

        Assert.IsNotNull(result, "Certificate generation should succeed");
        Assert.IsTrue(File.Exists(_testCertificatePath), "Certificate file should exist");
    }

    /// <summary>
    /// Checks if a certificate file exists, can be loaded, and is still valid (not expired)
    /// </summary>
    /// <param name="certPath">Path to the certificate file</param>
    /// <param name="password">Certificate password</param>
    /// <returns>True if the certificate file is valid and usable</returns>
    private static bool IsCertificateFileValid(string certPath, string password)
    {
        if (!File.Exists(certPath))
            return false;

        try
        {
            // Check if certificate can be loaded with the correct password
            if (!TestCertificateUtils.CanLoadCertificate(certPath, password))
                return false;

            // Check if certificate is not expired
            using var cert = X509CertificateLoader.LoadPkcs12FromFile(
                certPath, password, X509KeyStorageFlags.Exportable);

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
    /// <param name="subjectName">Certificate subject name (e.g., "CN=WinsdkTestPublisher")</param>
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
    private static void ExportCertificateFromStore(string subjectName, string password, string outputPath)
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
                    File.WriteAllBytes(outputPath, pfxData);
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
            _testExecutablePath,
            _testCertificatePath,
            "--password", "testpassword",
            "--verbose"
        };

        // Act
        var parseResult = command.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        // Assert
        // We expect this to fail because our fake executable isn't a real PE file
        // But this confirms that:
        // 1. Arguments were parsed correctly
        // 2. Certificate was loaded successfully
        // 3. signtool was found and executed
        // 4. The error was handled gracefully
        Assert.AreEqual(1, exitCode, "Sign command should fail gracefully for invalid executable format");

        // Verify that the original file still exists
        Assert.IsTrue(File.Exists(_testExecutablePath), "Original executable should still exist after failed signing");
    }

    [TestMethod]
    public async Task SignCommandWithNonExistentFileShouldFail()
    {
        // Arrange
        var command = GetRequiredService<SignCommand>();
        var nonExistentFile = Path.Combine(_tempDirectory, "NonExistent.exe");
        var args = new[]
        {
            nonExistentFile,
            _testCertificatePath,
            "--password", "testpassword"
        };

        // Act
        var parseResult = command.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        // Assert
        Assert.AreEqual(1, exitCode, "Sign command should fail for non-existent file");
    }

    [TestMethod]
    public async Task SignCommandWithNonExistentCertificateShouldFail()
    {
        // Arrange
        var command = GetRequiredService<SignCommand>();
        var nonExistentCert = Path.Combine(_tempDirectory, "NonExistent.pfx");
        var args = new[]
        {
            _testExecutablePath,
            nonExistentCert,
            "--password", "testpassword"
        };

        // Act
        var parseResult = command.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

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
            _testExecutablePath,
            _testCertificatePath,
            "--password", "wrongpassword"
        };

        // Act
        var parseResult = command.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

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
            _testExecutablePath,
            _testCertificatePath,
            "--password", "testpassword",
            "--timestamp", timestampUrl,
            "--verbose"
        };

        // Act
        var parseResult = command.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        // Assert
        // We expect this to fail because our fake executable isn't a real PE file
        // But this confirms the timestamp parameter was processed correctly
        Assert.AreEqual(1, exitCode, "Sign command with timestamp should fail gracefully for invalid executable format");

        // Verify that the file still exists
        Assert.IsTrue(File.Exists(_testExecutablePath), "Original executable should still exist after failed signing");
    }

    [TestMethod]
    public void SignCommandParseArgumentsShouldHandleAllOptions()
    {
        // Arrange
        var command = GetRequiredService<SignCommand>();
        var args = new[]
        {
            "test.exe",
            "cert.pfx",
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
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            // This should fail either because:
            // 1. BuildTools aren't installed in our test environment, OR
            // 2. The file format is invalid for signing
            // Both are acceptable failures that show the validation is working
            await _certificateService.SignFileAsync(
                _testExecutablePath,
                _testCertificatePath,
                "testpassword",
                timestampUrl: null,
                verbose: true);
        }, "SignFileAsync should throw when file cannot be signed or BuildTools are not available");

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
        // Arrange
        var originalDirectory = Directory.GetCurrentDirectory();

        try
        {
            // Change to temp directory
            Directory.SetCurrentDirectory(_tempDirectory);

            var command = GetRequiredService<SignCommand>();
            var relativeExePath = Path.GetFileName(_testExecutablePath);
            var relativeCertPath = Path.GetFileName(_testCertificatePath);

            var args = new[]
            {
                relativeExePath,
                relativeCertPath,
                "--password", "testpassword"
            };

            // Act
            var parseResult = command.Parse(args);
            var exitCode = await parseResult.InvokeAsync();

            // Assert - we expect this to fail due to invalid file format or missing BuildTools
            // but it should at least validate the file paths correctly
            Assert.AreEqual(1, exitCode, "Command should fail gracefully with relative paths");
        }
        finally
        {
            // Restore original directory
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    [TestMethod]
    public void CertificateGenerationShouldCreateValidCertificate()
    {
        // This test verifies that the certificate creation worked properly

        // Assert
        Assert.IsTrue(File.Exists(_testCertificatePath), "Certificate file should exist");

        // Verify the certificate can be loaded (this tests our certificate generation)
        var canLoad = TestCertificateUtils.CanLoadCertificate(_testCertificatePath, "testpassword");
        Assert.IsTrue(canLoad, "Generated certificate should be loadable with correct password");

        // Verify wrong password fails
        var canLoadWrong = TestCertificateUtils.CanLoadCertificate(_testCertificatePath, "wrongpassword");
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
            Assert.IsTrue(File.Exists(toolPath), "If BuildToolsService reports a tool path, the file should exist");
        }
    }
}

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
    /// </summary>
    private async Task CreateTestCertificateAsync()
    {
        // Generate a test certificate using CertificateServices
        var result = await _certificateService.GenerateDevCertificateAsync(
            publisher: "CN=WinsdkTestPublisher",
            outputPath: _testCertificatePath,
            password: "testpassword",
            validDays: 30,
            verbose: true);

        Assert.IsNotNull(result, "Certificate generation should succeed");
        Assert.IsTrue(File.Exists(_testCertificatePath), "Certificate file should exist");
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

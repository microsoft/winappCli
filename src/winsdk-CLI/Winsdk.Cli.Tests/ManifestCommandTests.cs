// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Winsdk.Cli.Commands;
using Winsdk.Cli.Services;

namespace Winsdk.Cli.Tests;

[TestClass]
public class ManifestCommandTests : BaseCommandTests
{
    private string _tempDirectory = null!;
    private string _testLogoPath = null!;

    [TestInitialize]
    public void Setup()
    {
        // Create a temporary directory for testing
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"WinsdkManifestTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);

        // Set up a temporary winsdk directory for testing (isolates tests from real winsdk directory)
        var testWinsdkDirectory = Path.Combine(_tempDirectory, ".winsdk");
        Directory.CreateDirectory(testWinsdkDirectory);

        // Create a fake logo file for testing
        _testLogoPath = Path.Combine(_tempDirectory, "testlogo.png");
        CreateFakeLogoFile(_testLogoPath);
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
    /// Creates a minimal fake logo file for testing
    /// </summary>
    private void CreateFakeLogoFile(string path)
    {
        // Create a minimal PNG-like file (just enough for file existence tests)
        var pngHeader = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }; // PNG signature
        File.WriteAllBytes(path, pngHeader);
    }

    [TestMethod]
    public void ManifestCommandShouldHaveGenerateSubcommand()
    {
        // Arrange & Act
        var manifestCommand = GetRequiredService<ManifestCommand>();

        // Assert
        Assert.IsNotNull(manifestCommand, "ManifestCommand should be created");
        Assert.AreEqual("manifest", manifestCommand.Name, "Command name should be 'manifest'");
        Assert.IsTrue(manifestCommand.Subcommands.Any(c => c.Name == "generate"), "Should have 'generate' subcommand");
    }

    [TestMethod]
    public async Task ManifestGenerateCommandWithDefaultsShouldCreateManifest()
    {
        // Arrange
        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[]
        {
            _tempDirectory,
            "--yes" // Skip interactive prompts
        };

        // Act
        var parseResult = generateCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        // Assert
        Assert.AreEqual(0, exitCode, "Generate command should complete successfully");

        // Verify manifest was created
        var expectedManifestPath = Path.Combine(_tempDirectory, "appxmanifest.xml");
        Assert.IsTrue(File.Exists(expectedManifestPath), "AppxManifest.xml should be created");

        // Verify Assets directory was created
        var assetsDir = Path.Combine(_tempDirectory, "Assets");
        Assert.IsTrue(Directory.Exists(assetsDir), "Assets directory should be created");
    }

    [TestMethod]
    public async Task ManifestGenerateCommandWithCustomOptionsShouldUseThoseValues()
    {
        // Arrange
        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[]
        {
            _tempDirectory,
            "--package-name", "TestPackage",
            "--publisher-name", "CN=TestPublisher",
            "--version", "2.0.0.0",
            "--description", "Test Application",
            "--entrypoint", "TestApp.exe",
            "--yes" // Skip interactive prompts
        };

        // Act
        var parseResult = generateCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        // Assert
        Assert.AreEqual(0, exitCode, "Generate command should complete successfully");

        // Verify manifest was created
        var manifestPath = Path.Combine(_tempDirectory, "appxmanifest.xml");
        Assert.IsTrue(File.Exists(manifestPath), "AppxManifest.xml should be created");

        // Verify manifest content contains our custom values
        var manifestContent = await File.ReadAllTextAsync(manifestPath);
        Assert.Contains("TestPackage", manifestContent, "Manifest should contain custom package name");
        Assert.Contains("CN=TestPublisher", manifestContent, "Manifest should contain custom publisher");
        Assert.Contains("2.0.0.0", manifestContent, "Manifest should contain custom version");
        Assert.Contains("Test Application", manifestContent, "Manifest should contain custom description");
        Assert.Contains("TestApp.exe", manifestContent, "Manifest should contain custom executable");
    }

    [TestMethod]
    public async Task ManifestGenerateCommandWithSparseOptionShouldCreateSparseManifest()
    {
        // Arrange
        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[]
        {
            _tempDirectory,
            "--template", "sparse",
            "--yes" // Skip interactive prompts
        };

        // Act
        var parseResult = generateCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        // Assert
        Assert.AreEqual(0, exitCode, "Generate command should complete successfully");

        // Verify manifest was created
        var manifestPath = Path.Combine(_tempDirectory, "appxmanifest.xml");
        Assert.IsTrue(File.Exists(manifestPath), "AppxManifest.xml should be created");

        // Verify sparse package specific content
        var manifestContent = await File.ReadAllTextAsync(manifestPath);
        Assert.Contains("uap10:AllowExternalContent", manifestContent, "Sparse manifest should contain AllowExternalContent");
        Assert.Contains("packagedClassicApp", manifestContent, "Sparse manifest should contain packagedClassicApp");
    }

    [TestMethod]
    public async Task ManifestGenerateCommandWithLogoShouldCopyLogo()
    {
        // Arrange
        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[]
        {
            _tempDirectory,
            "--logo-path", _testLogoPath,
            "--yes" // Skip interactive prompts
        };

        // Act
        var parseResult = generateCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        // Assert
        Assert.AreEqual(0, exitCode, "Generate command should complete successfully");

        // Verify manifest was created
        var manifestPath = Path.Combine(_tempDirectory, "appxmanifest.xml");
        Assert.IsTrue(File.Exists(manifestPath), "AppxManifest.xml should be created");

        // Verify logo was copied to Assets directory
        var assetsDir = Path.Combine(_tempDirectory, "Assets");
        var copiedLogoPath = Path.Combine(assetsDir, "testlogo.png");
        Assert.IsTrue(File.Exists(copiedLogoPath), "Logo should be copied to Assets directory");
    }

    [TestMethod]
    public async Task ManifestGenerateCommandShouldFailIfManifestAlreadyExists()
    {
        // Arrange - Create an existing manifest
        var existingManifestPath = Path.Combine(_tempDirectory, "appxmanifest.xml");
        await File.WriteAllTextAsync(existingManifestPath, "<Package>Existing</Package>");

        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[]
        {
            _tempDirectory,
            "--yes" // Skip interactive prompts
        };

        // Act
        var parseResult = generateCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        // Assert
        Assert.AreEqual(1, exitCode, "Generate command should fail when manifest already exists");
    }

    [TestMethod]
    public void ManifestGenerateCommandParseArgumentsShouldHandleAllOptions()
    {
        // Arrange
        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[]
        {
            "/test/directory",
            "--package-name", "TestPkg",
            "--publisher-name", "CN=TestPub",
            "--version", "1.2.3.4",
            "--description", "Test Description",
            "--entrypoint", "test.exe",
            "--template", "sparse",
            "--logo-path", "/test/logo.png",
            "--yes",
            "--verbose"
        };

        // Act
        var parseResult = generateCommand.Parse(args);

        // Assert
        Assert.IsNotNull(parseResult, "Parse result should not be null");
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
    }

    [TestMethod]
    public void ManifestGenerateCommandShouldUseCurrentDirectoryAsDefault()
    {
        // Arrange
        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[]
        {
            "--yes" // Skip interactive prompts - no directory argument
        };

        // Act
        var parseResult = generateCommand.Parse(args);

        // Assert
        Assert.IsNotNull(parseResult, "Parse result should not be null");
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");

        // The command should use current directory as default when no directory is specified
        // This is validated by the DefaultValueFactory in the command definition
    }

    [TestMethod]
    public async Task ManifestGenerateCommandWithVerboseOptionShouldProduceOutput()
    {
        // Arrange
        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[]
        {
            _tempDirectory,
            "--verbose",
            "--yes" // Skip interactive prompts
        };

        var parseResult = generateCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        // Assert
        Assert.AreEqual(0, exitCode, "Generate command should complete successfully");

        var output = ConsoleStdOut.ToString();
        Assert.Contains("Generating manifest", output, "Verbose output should contain generation message");
    }

    [TestMethod]
    [DataRow("My@App#Name", "MyAppName", DisplayName = "Should remove invalid characters")]
    [DataRow("_InvalidStart", "AppInvalidStart", DisplayName = "Should replace leading underscore")]
    [DataRow("", "DefaultPackage", DisplayName = "Should use default for empty string")]
    [DataRow("  ", "DefaultPackage", DisplayName = "Should use default for whitespace")]
    [DataRow("Ab", "Ab1", DisplayName = "Should pad short names")]
    [DataRow("VeryLongPackageNameThatExceedsFiftyCharacterLimit123456", "VeryLongPackageNameThatExceedsFiftyCharacterLimit1", DisplayName = "Should truncate long names")]
    [DataRow("Valid-Package_Name.1", "Valid-Package_Name.1", DisplayName = "Should keep valid names unchanged")]
    public void CleanPackageNameShouldSanitizeInvalidCharacters(string input, string expected)
    {
        // Act
        var result = ManifestService.CleanPackageName(input);

        // Assert
        Assert.AreEqual(expected, result, $"CleanPackageName('{input}') should return '{expected}'");
    }

    [TestMethod]
    public async Task ManifestGenerateCommandWithNonExistentLogoShouldIgnoreLogo()
    {
        // Arrange
        var nonExistentLogoPath = Path.Combine(_tempDirectory, "nonexistent.png");
        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[]
        {
            _tempDirectory,
            "--logo-path", nonExistentLogoPath,
            "--yes" // Skip interactive prompts
        };

        // Act
        var parseResult = generateCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        // Assert
        Assert.AreEqual(0, exitCode, "Generate command should complete successfully even with non-existent logo");

        // Verify manifest was created
        var manifestPath = Path.Combine(_tempDirectory, "appxmanifest.xml");
        Assert.IsTrue(File.Exists(manifestPath), "AppxManifest.xml should be created");

        // Verify no logo was copied (since it doesn't exist)
        var assetsDir = Path.Combine(_tempDirectory, "Assets");
        var wouldBeCopiedLogoPath = Path.Combine(assetsDir, "nonexistent.png");
        Assert.IsFalse(File.Exists(wouldBeCopiedLogoPath), "Non-existent logo should not be copied");
    }

    [TestMethod]
    public void ManifestCommandHelpShouldDisplayCorrectInformation()
    {
        // Arrange
        var manifestCommand = GetRequiredService<ManifestCommand>();
        var args = new[] { "--help" };

        // Act
        var parseResult = manifestCommand.Parse(args);

        // Assert
        Assert.IsNotNull(parseResult, "Parse result should not be null");
        // The help option should be recognized and not produce errors
    }

    [TestMethod]
    public void ManifestGenerateCommandHelpShouldDisplayCorrectInformation()
    {
        // Arrange
        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[] { "--help" };

        // Act
        var parseResult = generateCommand.Parse(args);

        // Assert
        Assert.IsNotNull(parseResult, "Parse result should not be null");
        // The help option should be recognized and not produce errors
    }

    [TestMethod]
    public async Task ManifestGenerateCommandWithHostedAppTemplateShouldCreateHostedAppManifest()
    {
        // Arrange - Create a Python script file
        var pythonScriptPath = Path.Combine(_tempDirectory, "app.py");
        await File.WriteAllTextAsync(pythonScriptPath, "# Python script\nprint('Hello, World!')");

        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[]
        {
            _tempDirectory,
            "--template", "hostedapp",
            "--entrypoint", "app.py"
        };

        // Act
        var parseResult = generateCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        // Assert
        Assert.AreEqual(0, exitCode, "Generate command should complete successfully");

        // Verify manifest was created
        var manifestPath = Path.Combine(_tempDirectory, "appxmanifest.xml");
        Assert.IsTrue(File.Exists(manifestPath), "AppxManifest.xml should be created");

        // Verify hosted app specific content
        var manifestContent = await File.ReadAllTextAsync(manifestPath);
        Assert.Contains("uap10:HostId", manifestContent, "HostedApp manifest should contain HostId");
        Assert.Contains("uap10:Parameters", manifestContent, "HostedApp manifest should contain Parameters");
        Assert.Contains("uap10:HostRuntimeDependency", manifestContent, "HostedApp manifest should contain HostRuntimeDependency");
        Assert.Contains("Python314", manifestContent, "HostedApp manifest should reference Python314 host");
        Assert.Contains("app.py", manifestContent, "HostedApp manifest should reference the Python script");
    }

    [TestMethod]
    public async Task CreateDebugIdentityForHostedAppShouldSucceed()
    {
        // Arrange - Create a Python script file and manifest
        var pythonScriptPath = Path.Combine(_tempDirectory, "app.py");
        await File.WriteAllTextAsync(pythonScriptPath, "# Python script\nprint('Hello, World!')");

        // First, generate a hosted app manifest
        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var generateArgs = new[]
        {
            _tempDirectory,
            "--template", "hostedapp",
            "--entrypoint", "app.py"
        };

        var generateParseResult = generateCommand.Parse(generateArgs);
        var generateExitCode = await generateParseResult.InvokeAsync();
        Assert.AreEqual(0, generateExitCode, "Manifest generation should succeed");

        var manifestPath = Path.Combine(_tempDirectory, "appxmanifest.xml");
        Assert.IsTrue(File.Exists(manifestPath), "Manifest should exist");

        // Act - Create debug identity
        var debugIdentityCommand = GetRequiredService<CreateDebugIdentityCommand>();
        var debugArgs = new[]
        {
            pythonScriptPath,
            "--manifest", manifestPath,
            "--no-install" // Skip actual installation in test
        };

        var debugParseResult = debugIdentityCommand.Parse(debugArgs);
        var debugExitCode = await debugParseResult.InvokeAsync();

        // Assert
        Assert.AreEqual(0, debugExitCode, "Create debug identity should complete successfully");
    }

    [TestMethod]
    public async Task ManifestGenerateCommandWithHostedAppTemplateAndJavaScriptShouldSucceed()
    {
        // Arrange - Create a JavaScript file
        var jsScriptPath = Path.Combine(_tempDirectory, "app.js");
        await File.WriteAllTextAsync(jsScriptPath, "// JavaScript\nconsole.log('Hello, World!');");

        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[]
        {
            _tempDirectory,
            "--template", "hostedapp",
            "--entrypoint", "app.js"
        };

        // Act
        var parseResult = generateCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        // Assert
        Assert.AreEqual(0, exitCode, "Generate command should complete successfully");

        // Verify manifest was created
        var manifestPath = Path.Combine(_tempDirectory, "appxmanifest.xml");
        Assert.IsTrue(File.Exists(manifestPath), "AppxManifest.xml should be created");

        // Verify hosted app specific content for Node.js
        var manifestContent = await File.ReadAllTextAsync(manifestPath);
        Assert.Contains("Nodejs22", manifestContent, "HostedApp manifest should reference Nodejs22 host");
        Assert.Contains("app.js", manifestContent, "HostedApp manifest should reference the JavaScript file");
    }

    [TestMethod]
    public async Task ManifestGenerateCommandWithHostedAppTemplateAndNonExistentEntryShouldFail()
    {
        // Arrange - Don't create the Python file
        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[]
        {
            _tempDirectory,
            "--template", "hostedapp",
            "--entrypoint", "nonexistent.py"
        };

        // Act
        var parseResult = generateCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        // Assert
        Assert.AreNotEqual(0, exitCode, "Generate command should fail when entry point file doesn't exist");
    }

    [TestMethod]
    public async Task ManifestGenerateCommandWithHostedAppTemplateAndUnsupportedTypeShouldFail()
    {
        // Arrange - Create a file with unsupported extension
        var unsupportedFilePath = Path.Combine(_tempDirectory, "app.exe");
        await File.WriteAllTextAsync(unsupportedFilePath, "fake exe content");

        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[]
        {
            _tempDirectory,
            "--template", "hostedapp",
            "--entrypoint", "app.exe"
        };

        // Act
        var parseResult = generateCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        // Assert
        Assert.AreNotEqual(0, exitCode, "Generate command should fail for unsupported hosted app entry point type");
    }
}

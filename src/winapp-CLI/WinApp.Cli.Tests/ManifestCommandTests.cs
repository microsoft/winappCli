// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class ManifestCommandTests : BaseCommandTests
{
    private string _testLogoPath = null!;

    [TestInitialize]
    public void Setup()
    {
        // Create a fake logo file for testing
        _testLogoPath = Path.Combine(_tempDirectory.FullName, "testlogo.png");
        PngHelper.CreateTestImage(_testLogoPath);
    }

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        return services
            .AddSingleton<IDevModeService, FakeDevModeService>();
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
            _tempDirectory.FullName
        };

        // Act
        var parseResult = generateCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Generate command should complete successfully");

        // Verify manifest was created
        var expectedManifestPath = Path.Combine(_tempDirectory.FullName, "Package.appxmanifest");
        Assert.IsTrue(File.Exists(expectedManifestPath), "Package.appxmanifest should be created");

        // Verify Assets directory was created
        var assetsDir = Path.Combine(_tempDirectory.FullName, "Assets");
        Assert.IsTrue(Directory.Exists(assetsDir), "Assets directory should be created");

        // The taskbar draws the icon on an accent-colored plate unless it finds the unplated
        // variant for the current theme; the light taskbar only accepts altform-lightunplated.
        Assert.IsTrue(File.Exists(Path.Join(assetsDir, "AppList.targetsize-24_altform-unplated.png")),
            "Dark-theme unplated taskbar icon should be generated");
        Assert.IsTrue(File.Exists(Path.Join(assetsDir, "AppList.targetsize-24_altform-lightunplated.png")),
            "Light-theme unplated taskbar icon should be generated");
    }

    [TestMethod]
    public async Task ManifestGenerateCommandWithCustomOptionsShouldUseThoseValues()
    {
        var exeFilePath = Path.Combine(_tempDirectory.FullName, "TestApp.exe");
        await File.WriteAllTextAsync(exeFilePath, "fake exe content", TestContext.CancellationToken);

        // Arrange
        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[]
        {
            _tempDirectory.FullName,
            "--package-name", "TestPackage",
            "--publisher-name", "CN=TestPublisher",
            "--version", "2.0.0.0",
            "--description", "Test Application",
            "--executable", exeFilePath
        };

        // Act
        var parseResult = generateCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Generate command should complete successfully");

        // Verify manifest was created
        var manifestPath = Path.Combine(_tempDirectory.FullName, "Package.appxmanifest");
        Assert.IsTrue(File.Exists(manifestPath), "Package.appxmanifest should be created");

        // Verify manifest content contains our custom values
        var manifestContent = await File.ReadAllTextAsync(manifestPath, TestContext.CancellationToken);
        Assert.Contains("TestPackage", manifestContent, "Manifest should contain custom package name");
        Assert.Contains("CN=TestPublisher", manifestContent, "Manifest should contain custom publisher");
        Assert.Contains("2.0.0.0", manifestContent, "Manifest should contain custom version");
        Assert.Contains("Test Application", manifestContent, "Manifest should contain custom description");
        Assert.Contains("$targetnametoken$.exe", manifestContent, "Manifest should contain placeholder executable name");
    }

    [TestMethod]
    public async Task ManifestGenerateCommandWithSparseOptionShouldCreateSparseManifest()
    {
        // Arrange
        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[]
        {
            _tempDirectory.FullName,
            "--template", "sparse"
        };

        // Act
        var parseResult = generateCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Generate command should complete successfully");

        // Verify manifest was created
        var manifestPath = Path.Combine(_tempDirectory.FullName, "Package.appxmanifest");
        Assert.IsTrue(File.Exists(manifestPath), "Package.appxmanifest should be created");
        var manifestContent = await File.ReadAllTextAsync(manifestPath, TestContext.CancellationToken);
        Assert.Contains("uap10:AllowExternalContent", manifestContent, "Sparse manifest should contain AllowExternalContent");
        Assert.Contains("uap10:RuntimeBehavior=\"win32App\"", manifestContent, "Sparse manifest should declare win32App runtime behavior");
    }

    [TestMethod]
    public async Task ManifestGenerateCommandWithLogoShouldGenerateAssets()
    {
        // Arrange
        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[]
        {
            _tempDirectory.FullName,
            "--logo-path", _testLogoPath
        };

        // Act
        var parseResult = generateCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Generate command should complete successfully");

        // Verify manifest was created
        var manifestPath = Path.Combine(_tempDirectory.FullName, "Package.appxmanifest");
        Assert.IsTrue(File.Exists(manifestPath), "Package.appxmanifest should be created");
        var assetsDir = Path.Combine(_tempDirectory.FullName, "Assets");
        Assert.IsTrue(Directory.Exists(assetsDir), "Assets directory should be created");

        // Verify expected MSIX asset files were generated
        var expectedAssets = new[]
        {
            "AppList.png",
            "MedTile.png",
            "WideTile.png",
            "StoreLogo.png"
        };

        foreach (var assetName in expectedAssets)
        {
            var assetPath = Path.Combine(assetsDir, assetName);
            Assert.IsTrue(File.Exists(assetPath), $"Asset {assetName} should be generated");

            // Since the source image is a 1x1 transparent pixel, all generated assets
            // should be fully transparent (empty canvases)
            Assert.IsTrue(PngHelper.IsFullyTransparent(assetPath),
                $"Asset {assetName} should be fully transparent when generated from a transparent source");
        }
    }

    [TestMethod]
    public async Task ManifestGenerateCommandShouldFailIfManifestAlreadyExists()
    {
        // Arrange - Create an existing manifest
        var existingManifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        await File.WriteAllTextAsync(existingManifestPath, "<Package>Existing</Package>", TestContext.CancellationToken);

        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[]
        {
            _tempDirectory.FullName
        };

        // Act
        var parseResult = generateCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, exitCode, "Generate command should fail when manifest already exists");
    }

    [TestMethod]
    public async Task ManifestGenerateCommandParseArgumentsShouldHandleAllOptions()
    {
        var exeFilePath = Path.Combine(_tempDirectory.FullName, "app.exe");
        await File.WriteAllTextAsync(exeFilePath, "fake exe content", TestContext.CancellationToken);

        // Arrange
        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[]
        {
            _tempDirectory.FullName,
            "--package-name", "TestPkg",
            "--publisher-name", "CN=TestPub",
            "--version", "1.2.3.4",
            "--description", "Test Description",
            "--executable", exeFilePath,
            "--template", "sparse",
            "--logo-path", _testLogoPath,
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

        // Act
        var parseResult = generateCommand.Parse([]);

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
            _tempDirectory.FullName,
            "--verbose"
        };

        DefaultAnswers();

        var exitCode = await ParseAndInvokeWithCaptureAsync(generateCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Generate command should complete successfully");

        Assert.Contains("Generating manifest", TestAnsiConsole.Output, "Verbose output should contain generation message");
    }

    [TestMethod]
    [DataRow("My@App#Name", "MyAppName", DisplayName = "Should remove invalid characters")]
    [DataRow("_InvalidStart", "InvalidStart", DisplayName = "Should remove underscores")]
    [DataRow("", "DefaultPackage", DisplayName = "Should use default for empty string")]
    [DataRow("  ", "DefaultPackage", DisplayName = "Should use default for whitespace")]
    [DataRow("Ab", "Ab1", DisplayName = "Should pad short names")]
    [DataRow("VeryLongPackageNameThatExceedsFiftyCharacterLimit123456", "VeryLongPackageNameThatExceedsFiftyCharacterLimit1", DisplayName = "Should truncate long names")]
    [DataRow("Valid-Package.Name.1", "Valid-Package.Name.1", DisplayName = "Should keep valid names unchanged")]
    [DataRow("Test_With_Underscores", "TestWithUnderscores", DisplayName = "Should remove all underscores")]
    [DataRow("Name With Spaces", "NameWithSpaces", DisplayName = "Should remove spaces")]
    [DataRow("Mixed_Under-scores.And.Dashes", "MixedUnder-scores.And.Dashes", DisplayName = "Should remove underscores but keep dashes and periods")]
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
        var nonExistentLogoPath = Path.Combine(_tempDirectory.FullName, "nonexistent.png");
        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[]
        {
            _tempDirectory.FullName,
            "--logo-path", nonExistentLogoPath
        };

        // Act
        var parseResult = generateCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Generate command should complete successfully even with non-existent logo");

        // Verify manifest was created
        var manifestPath = Path.Combine(_tempDirectory.FullName, "Package.appxmanifest");
        Assert.IsTrue(File.Exists(manifestPath), "Package.appxmanifest should be created");

        // Verify no logo was copied (since it doesn't exist)
        var assetsDir = Path.Combine(_tempDirectory.FullName, "Assets");
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
    public async Task CreateDebugIdentityWithKeepIdentityShouldPreserveOriginalIdentity()
    {
        // Arrange - Use .bat instead of .exe to avoid EmbedMsixIdentityToExeAsync which requires mt.exe from Build Tools
        var entryPointPath = Path.Combine(_tempDirectory.FullName, "TestApp.bat");
        await File.WriteAllTextAsync(entryPointPath, "@echo off", TestContext.CancellationToken);

        var manifestContent = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
         xmlns:uap=""http://schemas.microsoft.com/appx/manifest/uap/windows10"">
  <Identity Name=""MyTestPackage""
            Publisher=""CN=TestPublisher""
            Version=""1.0.0.0"" />
  <Properties>
    <DisplayName>Test Package</DisplayName>
    <PublisherDisplayName>Test Publisher</PublisherDisplayName>
    <Description>Test package</Description>
    <Logo>Assets\Logo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name=""Windows.Universal"" MinVersion=""10.0.18362.0"" MaxVersionTested=""10.0.26100.0"" />
  </Dependencies>
  <Applications>
    <Application Id=""TestApp"" Executable=""TestApp.bat"" EntryPoint=""TestApp.App"">
      <uap:VisualElements DisplayName=""Test App"" Description=""Test application""
                          BackgroundColor=""#777777"" Square150x150Logo=""Assets\Logo.png"" Square44x44Logo=""Assets\Logo.png"" />
    </Application>
  </Applications>
</Package>";

        var manifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        await File.WriteAllTextAsync(manifestPath, manifestContent, TestContext.CancellationToken);

        // Create minimal assets so the command doesn't fail
        var assetsDir = Path.Combine(_tempDirectory.FullName, "Assets");
        Directory.CreateDirectory(assetsDir);
        PngHelper.CreateTestImage(Path.Combine(assetsDir, "Logo.png"));

        // Act - Create debug identity with --keep-identity
        var debugIdentityCommand = GetRequiredService<CreateDebugIdentityCommand>();
        var debugArgs = new[]
        {
            entryPointPath,
            "--manifest", manifestPath,
            "--no-install",
            "--keep-identity"
        };

        var debugParseResult = debugIdentityCommand.Parse(debugArgs);
        var debugExitCode = await debugParseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, debugExitCode, "Create debug identity with --keep-identity should complete successfully");

        // Verify the debug manifest preserves the original identity without .debug suffix
        var debugManifestPath = Path.Combine(_testWinappDirectory.FullName, "debug", "appxmanifest.xml");
        Assert.IsTrue(File.Exists(debugManifestPath), "Debug manifest should be created");
        var debugManifestContent = await File.ReadAllTextAsync(debugManifestPath, TestContext.CancellationToken);
        Assert.Contains("Name=\"MyTestPackage\"", debugManifestContent, "Debug manifest should keep the original package name");
        Assert.Contains("Id=\"TestApp\"", debugManifestContent, "Debug manifest should keep the original application ID");
        Assert.DoesNotContain(".debug", debugManifestContent, "Debug manifest should NOT contain .debug suffix when --keep-identity is used");
    }

    [TestMethod]
    public async Task CreateDebugIdentityShouldNotModifyOriginalManifestWithPlaceholders()
    {
        // Arrange - Use .bat instead of .exe to avoid EmbedMsixIdentityToExeAsync which requires mt.exe from Build Tools
        var entryPointPath = Path.Combine(_tempDirectory.FullName, "MyApp.bat");
        await File.WriteAllTextAsync(entryPointPath, "@echo off", TestContext.CancellationToken);

        var manifestContent = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
         xmlns:uap=""http://schemas.microsoft.com/appx/manifest/uap/windows10"">
  <Identity Name=""$targetnametoken$""
            Publisher=""CN=TestPublisher""
            Version=""1.0.0.0"" />
  <Properties>
    <DisplayName>$targetnametoken$</DisplayName>
    <PublisherDisplayName>Test Publisher</PublisherDisplayName>
    <Description>Test package</Description>
    <Logo>Assets\Logo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name=""Windows.Universal"" MinVersion=""10.0.18362.0"" MaxVersionTested=""10.0.26100.0"" />
  </Dependencies>
  <Applications>
    <Application Id=""App"" Executable=""$targetnametoken$.exe"" EntryPoint=""$targetentrypoint$"">
      <uap:VisualElements DisplayName=""$targetnametoken$"" Description=""Test application""
                          BackgroundColor=""#777777"" Square150x150Logo=""Assets\Logo.png"" Square44x44Logo=""Assets\Logo.png"" />
    </Application>
  </Applications>
</Package>";

        var manifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        await File.WriteAllTextAsync(manifestPath, manifestContent, TestContext.CancellationToken);

        // Create minimal assets so the command doesn't fail
        var assetsDir = Path.Combine(_tempDirectory.FullName, "Assets");
        Directory.CreateDirectory(assetsDir);
        PngHelper.CreateTestImage(Path.Combine(assetsDir, "Logo.png"));

        // Act - Create debug identity
        var debugIdentityCommand = GetRequiredService<CreateDebugIdentityCommand>();
        var debugArgs = new[]
        {
            entryPointPath,
            "--manifest", manifestPath,
            "--no-install"
        };

        var debugParseResult = debugIdentityCommand.Parse(debugArgs);
        var debugExitCode = await debugParseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, debugExitCode, "Create debug identity should complete successfully");

        // Verify the ORIGINAL manifest was NOT modified - it should still contain placeholders
        var originalManifestAfter = await File.ReadAllTextAsync(manifestPath, TestContext.CancellationToken);
        Assert.AreEqual(manifestContent, originalManifestAfter, "Original appxmanifest.xml should not be modified by create-debug-identity");
        Assert.Contains("$targetnametoken$", originalManifestAfter, "Original manifest should still contain $targetnametoken$ placeholder");
        Assert.Contains("$targetentrypoint$", originalManifestAfter, "Original manifest should still contain $targetentrypoint$ placeholder");

        // Verify the debug manifest WAS created with resolved placeholders
        var debugManifestPath = Path.Combine(_testWinappDirectory.FullName, "debug", "appxmanifest.xml");
        Assert.IsTrue(File.Exists(debugManifestPath), "Debug manifest should be created");
        var debugManifestContent = await File.ReadAllTextAsync(debugManifestPath, TestContext.CancellationToken);
        Assert.DoesNotContain("$targetnametoken$", debugManifestContent, "Debug manifest should have resolved $targetnametoken$ placeholder");
        Assert.DoesNotContain("$targetentrypoint$", debugManifestContent, "Debug manifest should have resolved $targetentrypoint$ placeholder");
        Assert.Contains("Executable=\"MyApp.bat\"", debugManifestContent, "Debug manifest should contain the resolved executable name");
    }

    [TestMethod]
    public async Task ManifestGenerateCommandWithEntrypointShouldUseVersionInfoFromExecutable()
    {
        // Arrange - Use winapp.exe which has version info
        var winappAssemblyPath = typeof(ManifestService).Assembly.Location;

        if (string.IsNullOrEmpty(winappAssemblyPath) || !File.Exists(winappAssemblyPath))
        {
            Assert.Inconclusive("winapp assembly not found");
            return;
        }

        // Copy to temp directory with .exe extension
        var exeFilePath = Path.Combine(_tempDirectory.FullName, "winapp.exe");
        File.Copy(winappAssemblyPath, exeFilePath);

        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[]
        {
            _tempDirectory.FullName,
            "--executable", exeFilePath
        };

        // Act
        var parseResult = generateCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Generate command should complete successfully");

        // Verify manifest was created
        var manifestPath = Path.Combine(_tempDirectory.FullName, "Package.appxmanifest");
        Assert.IsTrue(File.Exists(manifestPath), "Package.appxmanifest should be created");

        var manifestContent = await File.ReadAllTextAsync(manifestPath, TestContext.CancellationToken);

        // Verify the executable placeholder is in the manifest (template uses $targetnametoken$.exe)
        Assert.Contains("Executable=\"$targetnametoken$.exe\"", manifestContent, "Manifest should contain placeholder executable name");

        var fileVersionInfo = FileVersionInfo.GetVersionInfo(exeFilePath);
        Assert.Contains($"Description=\"{fileVersionInfo.Comments}\"", manifestContent,
            "Manifest description should contain FileDescription from executable");

        Assert.Contains("Name=\"winapp\"", manifestContent,
            "Manifest should contain package name derived from FileDescription");

        // Verify Publisher is set to Microsoft Corporation as per the assembly info
        Assert.Contains("CN=Microsoft Corporation", manifestContent,
            "Manifest publisher should be set to Microsoft Corporation from executable");
    }

    [TestMethod]
    public void DetermineIcoOutputPathShouldDefaultToAppIcoWhenNoExistingIco()
    {
        var assetsDir = _tempDirectory.CreateSubdirectory("AssetsEmpty");

        var result = ManifestService.DetermineIcoOutputPath(assetsDir, TestTaskContext);

        Assert.AreEqual(Path.Combine(assetsDir.FullName, "app.ico"), result);
    }

    [TestMethod]
    public void DetermineIcoOutputPathShouldDefaultToAppIcoWhenDirectoryDoesNotExist()
    {
        var nonExistentDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "NonExistent"));

        var result = ManifestService.DetermineIcoOutputPath(nonExistentDir, TestTaskContext);

        Assert.AreEqual(Path.Combine(nonExistentDir.FullName, "app.ico"), result);
    }

    [TestMethod]
    public void DetermineIcoOutputPathShouldReuseSingleExistingIco()
    {
        var assetsDir = _tempDirectory.CreateSubdirectory("AssetsSingle");
        var existingIco = Path.Combine(assetsDir.FullName, "AppIcon.ico");
        File.WriteAllBytes(existingIco, [0]);

        var result = ManifestService.DetermineIcoOutputPath(assetsDir, TestTaskContext);

        Assert.AreEqual(existingIco, result);
    }

    [TestMethod]
    public void DetermineIcoOutputPathShouldPreferAppIconWhenMultipleExist()
    {
        var assetsDir = _tempDirectory.CreateSubdirectory("AssetsMulti");
        File.WriteAllBytes(Path.Combine(assetsDir.FullName, "AppIcon.ico"), [0]);
        File.WriteAllBytes(Path.Combine(assetsDir.FullName, "favicon.ico"), [0]);

        var result = ManifestService.DetermineIcoOutputPath(assetsDir, TestTaskContext);

        Assert.AreEqual(Path.Combine(assetsDir.FullName, "AppIcon.ico"), result);
    }

    [TestMethod]
    public void DetermineIcoOutputPathShouldPreferAppOverIconInName()
    {
        var assetsDir = _tempDirectory.CreateSubdirectory("AssetsHeuristic");
        File.WriteAllBytes(Path.Combine(assetsDir.FullName, "app.ico"), [0]);
        File.WriteAllBytes(Path.Combine(assetsDir.FullName, "icon.ico"), [0]);

        // "app" is preferred over "icon" in the heuristic
        var result = ManifestService.DetermineIcoOutputPath(assetsDir, TestTaskContext);

        Assert.AreEqual(Path.Combine(assetsDir.FullName, "app.ico"), result);
    }

    [TestMethod]
    public void DetermineIcoOutputPathShouldCreateAppIcoWhenNoHeuristicMatch()
    {
        var assetsDir = _tempDirectory.CreateSubdirectory("AssetsUnrelated");
        File.WriteAllBytes(Path.Combine(assetsDir.FullName, "tray.ico"), [0]);
        File.WriteAllBytes(Path.Combine(assetsDir.FullName, "cursor.ico"), [0]);

        var result = ManifestService.DetermineIcoOutputPath(assetsDir, TestTaskContext);

        // Unrelated ICO files should not be touched; fall back to app.ico
        Assert.AreEqual(Path.Combine(assetsDir.FullName, "app.ico"), result);
    }
}

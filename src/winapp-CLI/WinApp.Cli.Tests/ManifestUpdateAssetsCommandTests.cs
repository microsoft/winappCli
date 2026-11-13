// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

[TestClass]
public class ManifestUpdateAssetsCommandTests : BaseCommandTests
{
    private string _testManifestPath = null!;
    private string _testImagePath = null!;
    private string _testLightImagePath = null!;

    [TestInitialize]
    public void Setup()
    {
        _testManifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        CreateTestManifest(_testManifestPath);

        _testImagePath = Path.Combine(_tempDirectory.FullName, "testlogo.png");
        PngHelper.CreateTestImage(_testImagePath);

        _testLightImagePath = Path.Combine(_tempDirectory.FullName, "testlogo-light.png");
        PngHelper.CreateTestImage(_testLightImagePath);
    }

    private static void CreateTestManifest(string path)
    {
        var manifestContent = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package 
  xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
  xmlns:uap=""http://schemas.microsoft.com/appx/manifest/uap/windows10"">
  <Identity Name=""TestPackage"" Publisher=""CN=TestPublisher"" Version=""1.0.0.0"" />
  <Properties>
    <DisplayName>TestPackage</DisplayName>
    <PublisherDisplayName>TestPublisher</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Applications>
    <Application Id=""TestApp"" Executable=""test.exe"">
      <uap:VisualElements
        DisplayName=""TestPackage""
        Description=""Test Application""
        BackgroundColor=""transparent""
        Square150x150Logo=""Assets\Square150x150Logo.png""
        Square44x44Logo=""Assets\Square44x44Logo.png"">
        <uap:DefaultTile Wide310x150Logo=""Assets\Wide310x150Logo.png"" />
      </uap:VisualElements>
    </Application>
  </Applications>
</Package>";
        File.WriteAllText(path, manifestContent);
    }

    [TestMethod]
    public void ManifestUpdateAssetsCommandShouldBeAvailable()
    {
        var manifestCommand = GetRequiredService<ManifestCommand>();

        Assert.IsNotNull(manifestCommand, "ManifestCommand should be created");
        Assert.IsTrue(manifestCommand.Subcommands.Any(c => c.Name == "update-assets"),
            "Should have 'update-assets' subcommand");
    }

    [TestMethod]
    public async Task ManifestUpdateAssetsCommandShouldGenerateAssets()
    {
        var updateAssetsCommand = GetRequiredService<ManifestUpdateAssetsCommand>();
        var args = new[]
        {
            _testImagePath,
            "--manifest", _testManifestPath,
        };

        var parseResult = updateAssetsCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        Assert.AreEqual(0, exitCode, "Update-assets command should complete successfully");

        var assetsDir = Path.Combine(_tempDirectory.FullName, "Assets");
        Assert.IsTrue(Directory.Exists(assetsDir), "Assets directory should be created");

        var expectedAssets = new[]
        {
            "Square44x44Logo.png",
            "Square150x150Logo.png",
            "Wide310x150Logo.png",
            "StoreLogo.png",
            "app.ico",
        };

        foreach (var asset in expectedAssets)
        {
            var assetPath = Path.Combine(assetsDir, asset);
            Assert.IsTrue(File.Exists(assetPath), $"Asset {asset} should be generated");
        }
    }

    [TestMethod]
    public void ManifestUpdateAssetsCommandShouldFailWithNonExistentManifest()
    {
        var updateAssetsCommand = GetRequiredService<ManifestUpdateAssetsCommand>();
        var nonExistentManifest = Path.Combine(_tempDirectory.FullName, "nonexistent.xml");
        var args = new[]
        {
            _testImagePath,
            "--manifest", nonExistentManifest,
        };

        var parseResult = updateAssetsCommand.Parse(args);

        Assert.IsNotEmpty(parseResult.Errors, "Should have parse errors for non-existent manifest");
    }

    [TestMethod]
    public void ManifestUpdateAssetsCommandShouldFailWithNonExistentImage()
    {
        var updateAssetsCommand = GetRequiredService<ManifestUpdateAssetsCommand>();
        var nonExistentImage = Path.Combine(_tempDirectory.FullName, "nonexistent.png");
        var args = new[]
        {
            nonExistentImage,
            "--manifest", _testManifestPath,
        };

        var parseResult = updateAssetsCommand.Parse(args);

        Assert.IsNotEmpty(parseResult.Errors, "Should have parse errors for non-existent image");
    }

    [TestMethod]
    public async Task ManifestUpdateAssetsCommandShouldGenerateCorrectSizes()
    {
        var updateAssetsCommand = GetRequiredService<ManifestUpdateAssetsCommand>();
        var args = new[]
        {
            _testImagePath,
            "--manifest", _testManifestPath,
        };

        var parseResult = updateAssetsCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        Assert.AreEqual(0, exitCode, "Update-assets command should complete successfully");

        var assetsDir = Path.Combine(_tempDirectory.FullName, "Assets");
        AssertImageDimensions(Path.Combine(assetsDir, "Square44x44Logo.scale-125.png"), 55, 55);
        AssertImageDimensions(Path.Combine(assetsDir, "Square44x44Logo.scale-150.png"), 66, 66);
        AssertImageDimensions(Path.Combine(assetsDir, "Square44x44Logo.scale-200.png"), 88, 88);
        AssertImageDimensions(Path.Combine(assetsDir, "Square44x44Logo.scale-400.png"), 176, 176);
        AssertImageDimensions(Path.Combine(assetsDir, "Square150x150Logo.scale-200.png"), 300, 300);
        AssertImageDimensions(Path.Combine(assetsDir, "Square44x44Logo.targetsize-20.png"), 20, 20);
        AssertImageDimensions(Path.Combine(assetsDir, "Square44x44Logo.targetsize-20_altform-unplated.png"), 20, 20);
        AssertImageDimensions(Path.Combine(assetsDir, "Square44x44Logo.targetsize-256.png"), 256, 256);
        AssertImageDimensions(Path.Combine(assetsDir, "Square44x44Logo.targetsize-256_altform-unplated.png"), 256, 256);
    }

    [TestMethod]
    public async Task ManifestUpdateAssetsCommandShouldGenerateLightThemeAssets()
    {
        var updateAssetsCommand = GetRequiredService<ManifestUpdateAssetsCommand>();
        var args = new[]
        {
            _testImagePath,
            "--manifest", _testManifestPath,
            "--light-image", _testLightImagePath,
        };

        var parseResult = updateAssetsCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        Assert.AreEqual(0, exitCode, "Update-assets command should complete successfully with light image");

        var assetsDir = Path.Combine(_tempDirectory.FullName, "Assets");
        var expectedAssets = new[]
        {
            "Square44x44Logo.scale-100_altform-colorful_theme-light.png",
            "Square44x44Logo.scale-200_altform-colorful_theme-light.png",
            "StoreLogo.scale-400_altform-colorful_theme-light.png",
            "Square44x44Logo.targetsize-20_altform-lightunplated.png",
            "Square44x44Logo.targetsize-256_altform-lightunplated.png",
        };

        foreach (var asset in expectedAssets)
        {
            Assert.IsTrue(File.Exists(Path.Combine(assetsDir, asset)), $"Asset {asset} should be generated");
        }
    }

    [TestMethod]
    public async Task ManifestUpdateAssetsCommandShouldGenerateIcoWithExpectedFrames()
    {
        var updateAssetsCommand = GetRequiredService<ManifestUpdateAssetsCommand>();
        var args = new[]
        {
            _testImagePath,
            "--manifest", _testManifestPath,
        };

        var parseResult = updateAssetsCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        Assert.AreEqual(0, exitCode, "Update-assets command should complete successfully");

        var icoPath = Path.Combine(_tempDirectory.FullName, "Assets", "app.ico");
        Assert.IsTrue(File.Exists(icoPath), "app.ico should be generated");

        using var stream = File.OpenRead(icoPath);
        using var reader = new BinaryReader(stream);
        Assert.AreEqual((ushort)0, reader.ReadUInt16(), "ICO reserved field should be 0");
        Assert.AreEqual((ushort)1, reader.ReadUInt16(), "ICO type should be 1");
        Assert.AreEqual((ushort)5, reader.ReadUInt16(), "ICO should contain 5 image frames");
    }

    [TestMethod]
    public async Task ManifestUpdateAssetsCommandShouldOverwriteExistingAssets()
    {
        var assetsDir = Path.Combine(_tempDirectory.FullName, "Assets");
        Directory.CreateDirectory(assetsDir);

        var existingAssetPath = Path.Combine(assetsDir, "Square150x150Logo.png");
        File.WriteAllText(existingAssetPath, "old content");
        var oldLength = new FileInfo(existingAssetPath).Length;

        var updateAssetsCommand = GetRequiredService<ManifestUpdateAssetsCommand>();
        var args = new[]
        {
            _testImagePath,
            "--manifest", _testManifestPath,
        };

        var parseResult = updateAssetsCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        Assert.AreEqual(0, exitCode, "Update-assets command should complete successfully");
        Assert.IsTrue(File.Exists(existingAssetPath), "Asset should still exist");

        var newLength = new FileInfo(existingAssetPath).Length;
        Assert.AreNotEqual(oldLength, newLength, "Asset should be overwritten with new content");
    }

    [TestMethod]
    public void ManifestUpdateAssetsCommandHelpShouldDisplayCorrectInformation()
    {
        var updateAssetsCommand = GetRequiredService<ManifestUpdateAssetsCommand>();
        var args = new[] { "--help" };

        var parseResult = updateAssetsCommand.Parse(args);

        Assert.IsNotNull(parseResult, "Parse result should not be null");
    }

    [TestMethod]
    public async Task ManifestUpdateAssetsCommandShouldLogProgress()
    {
        var updateAssetsCommand = GetRequiredService<ManifestUpdateAssetsCommand>();
        var args = new[]
        {
            _testImagePath,
            "--manifest", _testManifestPath,
            "--verbose",
        };

        var exitCode = await ParseAndInvokeWithCaptureAsync(updateAssetsCommand, args);

        Assert.AreEqual(0, exitCode, "Update-assets command should complete successfully");
        Assert.Contains("Updating assets for manifest", TestAnsiConsole.Output, "Should log update message");
        Assert.Contains("generated", TestAnsiConsole.Output.ToLowerInvariant(), "Should log generation progress");
    }

    [TestMethod]
    public async Task ManifestUpdateAssetsCommandShouldInferManifestFromCurrentDirectory()
    {
        var updateAssetsCommand = GetRequiredService<ManifestUpdateAssetsCommand>();
        var args = new[]
        {
            _testImagePath,
        };

        var parseResult = updateAssetsCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        Assert.AreEqual(0, exitCode, "Update-assets command should complete successfully when manifest is inferred");

        var assetsDir = Path.Combine(_tempDirectory.FullName, "Assets");
        Assert.IsTrue(Directory.Exists(assetsDir), "Assets directory should be created");
        Assert.IsTrue(File.Exists(Path.Combine(assetsDir, "app.ico")), "app.ico should be generated when manifest is inferred");
    }

    [TestMethod]
    public async Task ManifestUpdateAssetsCommandShouldGenerateAssetsFromSvg()
    {
        var svgImagePath = Path.Combine(_tempDirectory.FullName, "testlogo.svg");
        PngHelper.CreateTestSvgImage(svgImagePath);

        var updateAssetsCommand = GetRequiredService<ManifestUpdateAssetsCommand>();
        var args = new[]
        {
            svgImagePath,
            "--manifest", _testManifestPath,
        };

        var parseResult = updateAssetsCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        Assert.AreEqual(0, exitCode, "Update-assets command should complete successfully with SVG source");

        var assetsDir = Path.Combine(_tempDirectory.FullName, "Assets");
        Assert.IsTrue(Directory.Exists(assetsDir), "Assets directory should be created");

        var expectedAssets = new[]
        {
            "Square44x44Logo.png",
            "Square150x150Logo.png",
            "Wide310x150Logo.png",
            "StoreLogo.png",
            "app.ico",
        };

        foreach (var asset in expectedAssets)
        {
            var assetPath = Path.Combine(assetsDir, asset);
            Assert.IsTrue(File.Exists(assetPath), $"Asset {asset} should be generated from SVG source");
        }
    }

    [TestMethod]
    public async Task ManifestUpdateAssetsCommandShouldGenerateCorrectSizesFromSvg()
    {
        var svgImagePath = Path.Combine(_tempDirectory.FullName, "testlogo.svg");
        PngHelper.CreateTestSvgImage(svgImagePath);

        var updateAssetsCommand = GetRequiredService<ManifestUpdateAssetsCommand>();
        var args = new[]
        {
            svgImagePath,
            "--manifest", _testManifestPath,
        };

        var parseResult = updateAssetsCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        Assert.AreEqual(0, exitCode, "Update-assets command should complete successfully with SVG source");

        var assetsDir = Path.Combine(_tempDirectory.FullName, "Assets");
        AssertImageDimensions(Path.Combine(assetsDir, "Square44x44Logo.scale-125.png"), 55, 55);
        AssertImageDimensions(Path.Combine(assetsDir, "Square44x44Logo.scale-200.png"), 88, 88);
        AssertImageDimensions(Path.Combine(assetsDir, "Square150x150Logo.scale-200.png"), 300, 300);
        AssertImageDimensions(Path.Combine(assetsDir, "Square44x44Logo.targetsize-32.png"), 32, 32);
        AssertImageDimensions(Path.Combine(assetsDir, "Square44x44Logo.targetsize-32_altform-unplated.png"), 32, 32);
    }

    private static void AssertImageDimensions(string imagePath, int expectedWidth, int expectedHeight)
    {
        Assert.IsTrue(File.Exists(imagePath), $"Asset {Path.GetFileName(imagePath)} should exist");
        using var bitmap = new System.Drawing.Bitmap(imagePath);
        Assert.AreEqual(expectedWidth, bitmap.Width, $"{Path.GetFileName(imagePath)} width mismatch");
        Assert.AreEqual(expectedHeight, bitmap.Height, $"{Path.GetFileName(imagePath)} height mismatch");
    }

    private static void CreateNewNamingManifest(string path)
    {
        var manifestContent = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package 
  xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
  xmlns:uap=""http://schemas.microsoft.com/appx/manifest/uap/windows10"">
  <Identity Name=""TestPackage"" Publisher=""CN=TestPublisher"" Version=""1.0.0.0"" />
  <Properties>
    <DisplayName>TestPackage</DisplayName>
    <PublisherDisplayName>TestPublisher</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Applications>
    <Application Id=""TestApp"" Executable=""test.exe"">
      <uap:VisualElements
        DisplayName=""TestPackage""
        Description=""Test Application""
        BackgroundColor=""transparent""
        Square150x150Logo=""Assets\MedTile.png""
        Square44x44Logo=""Assets\AppList.png"">
        <uap:DefaultTile Wide310x150Logo=""Assets\WideTile.png"" />
      </uap:VisualElements>
    </Application>
  </Applications>
</Package>";
        File.WriteAllText(path, manifestContent);
    }

    [TestMethod]
    public async Task ManifestUpdateAssetsCommandShouldGenerateAssetsWithNewNaming()
    {
        var newNamingManifest = Path.Combine(_tempDirectory.FullName, "appxmanifest-new.xml");
        CreateNewNamingManifest(newNamingManifest);

        var updateAssetsCommand = GetRequiredService<ManifestUpdateAssetsCommand>();
        var args = new[]
        {
            _testImagePath,
            "--manifest", newNamingManifest,
        };

        var parseResult = updateAssetsCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        Assert.AreEqual(0, exitCode, "Update-assets command should succeed with new naming manifest");

        var assetsDir = Path.Combine(_tempDirectory.FullName, "Assets");

        // Base assets use new names
        var expectedBaseAssets = new[] { "AppList.png", "MedTile.png", "WideTile.png", "StoreLogo.png", "app.ico" };
        foreach (var asset in expectedBaseAssets)
        {
            Assert.IsTrue(File.Exists(Path.Combine(assetsDir, asset)), $"Asset {asset} should be generated");
        }

        // Scale variants use new names
        AssertImageDimensions(Path.Combine(assetsDir, "AppList.scale-200.png"), 88, 88);
        AssertImageDimensions(Path.Combine(assetsDir, "MedTile.scale-200.png"), 300, 300);
        AssertImageDimensions(Path.Combine(assetsDir, "WideTile.scale-200.png"), 620, 300);

        // Targetsize variants generated for AppList (44x44 app icon)
        AssertImageDimensions(Path.Combine(assetsDir, "AppList.targetsize-48.png"), 48, 48);
        AssertImageDimensions(Path.Combine(assetsDir, "AppList.targetsize-48_altform-unplated.png"), 48, 48);
        AssertImageDimensions(Path.Combine(assetsDir, "AppList.targetsize-256.png"), 256, 256);

        // No targetsize variants for non-app-icon assets
        Assert.IsFalse(File.Exists(Path.Combine(assetsDir, "MedTile.targetsize-48.png")),
            "MedTile should not have targetsize variants");
    }

    [TestMethod]
    public async Task ManifestUpdateAssetsCommandShouldReplaceExistingIcoByName()
    {
        // Pre-create an Assets directory with an existing ICO file (simulating a project template)
        var assetsDir = Path.Combine(_tempDirectory.FullName, "Assets");
        Directory.CreateDirectory(assetsDir);
        File.WriteAllBytes(Path.Combine(assetsDir, "AppIcon.ico"), [0x00]);

        var updateAssetsCommand = GetRequiredService<ManifestUpdateAssetsCommand>();
        var args = new[]
        {
            _testImagePath,
            "--manifest", _testManifestPath,
        };

        var parseResult = updateAssetsCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        Assert.AreEqual(0, exitCode, "Update-assets command should complete successfully");

        // The existing AppIcon.ico should be replaced (size > 1 byte placeholder)
        var replacedIco = Path.Combine(assetsDir, "AppIcon.ico");
        Assert.IsTrue(File.Exists(replacedIco), "AppIcon.ico should still exist");
        Assert.IsTrue(new FileInfo(replacedIco).Length > 1, "AppIcon.ico should be regenerated with real content");

        // No duplicate app.ico should be created
        Assert.IsFalse(File.Exists(Path.Combine(assetsDir, "app.ico")),
            "app.ico should NOT be created when an existing ICO file is present");
    }

    [TestMethod]
    public async Task ManifestUpdateAssets_NoManifestFound_ReturnsError()
    {
        // Remove the manifest the setup created so the command's auto-detection finds nothing.
        File.Delete(_testManifestPath);
        var command = GetRequiredService<ManifestUpdateAssetsCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_testImagePath]);

        Assert.AreEqual(1, exitCode);
        Assert.Contains("Could not find Package.appxmanifest", ConsoleStdErr.ToString());
    }

    [TestMethod]
    public async Task ManifestUpdateAssets_InvalidSourceImage_ReturnsError()
    {
        // A file that exists (passes AcceptExistingOnly) but is not a decodable image causes the
        // asset generation to throw, which the handler catches and reports.
        var badImage = Path.Combine(_tempDirectory.FullName, "bad.png");
        await File.WriteAllTextAsync(badImage, "this is not a real PNG", TestContext.CancellationToken);
        var command = GetRequiredService<ManifestUpdateAssetsCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [badImage, "--manifest", _testManifestPath]);

        Assert.AreEqual(1, exitCode);
        Assert.Contains("Error updating assets", ConsoleStdErr.ToString());
    }
}

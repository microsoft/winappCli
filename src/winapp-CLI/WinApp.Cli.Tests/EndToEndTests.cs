// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// End-to-end integration tests that simulate complete workflows
/// including creating, building, initializing, and packaging real .NET applications
/// </summary>
[TestClass]
public class EndToEndTests : BaseCommandTests
{
    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        return services
            .AddSingleton<IDevModeService, FakeDevModeService>();
    }

    [TestMethod]
    public async Task E2E_WinFormsApp_CreateBuildManifestAndPackage_ShouldSucceed()
    {
        // This is a comprehensive end-to-end test that:
        // 1. Creates a new WinForms app using 'dotnet new winforms'
        // 2. Builds it using 'dotnet build'
        // 3. Runs 'winapp manifest generate' to create the manifest
        // 4. Runs 'winapp package' to create an MSIX package
        // 5. Verifies the entire workflow completed successfully

        // Arrange
        var projectDir = _tempDirectory.CreateSubdirectory("WinFormsApp");
        var projectName = "TestWinFormsApp";

        // Step 1: Create a new WinForms application
        var createResult = await RunDotnetCommandAsync(projectDir, $"new winforms -n {projectName} -o .");
        Assert.AreEqual(0, createResult.ExitCode, $"Failed to create WinForms app: {createResult.Output}");
        Assert.IsTrue(File.Exists(Path.Combine(projectDir.FullName, $"{projectName}.csproj")),
            "Project file should be created");

        // Step 2: Build the application
        var buildResult = await RunDotnetCommandAsync(projectDir, "build -c Release");
        Assert.AreEqual(0, buildResult.ExitCode, $"Failed to build WinForms app: {buildResult.Output}");

        // Verify the build output exists
        var binFolder = new DirectoryInfo(Path.Combine(projectDir.FullName, "bin", "Release"));
        Assert.IsTrue(binFolder.Exists, "Build output directory should exist");

        // Find the build output folder (handles runtime-specific subfolders like win-x64)
        var buildOutputFolder = FindBuildOutputFolder(binFolder);
        Assert.IsNotNull(buildOutputFolder, "Build output folder should exist");

        var exePath = Path.Combine(buildOutputFolder.FullName, $"{projectName}.exe");
        Assert.IsTrue(File.Exists(exePath), "Built executable should exist");

        // Step 3: Run 'winapp manifest generate' to create the manifest
        var manifestGenerateCommand = GetRequiredService<ManifestGenerateCommand>();
        var manifestArgs = new[]
        {
            projectDir.FullName,
            "--package-name", projectName,
            "--publisher-name", "CN=TestPublisher",
            "--executable", exePath
        };

        var manifestExitCode = await ParseAndInvokeWithCaptureAsync(manifestGenerateCommand, manifestArgs);
        Assert.AreEqual(0, manifestExitCode, "Manifest generate command should complete successfully");

        // Verify manifest generated the necessary files
        var manifestPath = Path.Combine(projectDir.FullName, "Package.appxmanifest");
        Assert.IsTrue(File.Exists(manifestPath), "Manifest generate should create Package.appxmanifest");

        var assetsDir = Path.Combine(projectDir.FullName, "Assets");
        Assert.IsTrue(Directory.Exists(assetsDir), "Manifest generate should create Assets directory");

        // Step 5: Run 'winapp package' to create MSIX package
        var packageCommand = GetRequiredService<PackageCommand>();
        var packageOutputPath = Path.Combine(_tempDirectory.FullName, $"{projectName}.msix");
        var packageArgs = new[]
        {
            buildOutputFolder.FullName,  // Input folder with built binaries
            "--output", packageOutputPath,
            "--manifest", manifestPath,
            "--skip-pri"                     // Skip PRI generation for faster tests
        };

        var packageParseResult = packageCommand.Parse(packageArgs);
        var packageExitCode = await packageParseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(0, packageExitCode, "Package command should complete successfully");

        // Step 6: Verify the MSIX package was created
        Assert.IsTrue(File.Exists(packageOutputPath), "MSIX package should be created");

        var fileInfo = new FileInfo(packageOutputPath);
        Assert.IsGreaterThan(0, fileInfo.Length, "MSIX package should not be empty");

        // Verify the MSIX contains expected files
        using var archive = await ZipFile.OpenReadAsync(packageOutputPath, TestContext.CancellationToken);
        var entries = archive.Entries.Select(e => e.FullName).ToList();

        Assert.IsTrue(entries.Any(e => e.EndsWith("AppxManifest.xml", StringComparison.OrdinalIgnoreCase)),
            "MSIX should contain AppxManifest.xml");
        Assert.IsTrue(entries.Any(e => e.EndsWith($"{projectName}.exe", StringComparison.OrdinalIgnoreCase)),
            $"MSIX should contain {projectName}.exe");
        Assert.IsTrue(entries.Any(e => e.Contains("Assets/", StringComparison.OrdinalIgnoreCase)),
            "MSIX should contain Assets folder");
    }

    [TestMethod]
    public async Task E2E_WinFormsApp_WithCustomManifestOptions_ShouldPackageSuccessfully()
    {
        // This test generates a manifest with custom options to verify parameter handling

        // Arrange
        var projectDir = _tempDirectory.CreateSubdirectory("WinFormsAppCustom");
        var projectName = "TestWinFormsAppCustom";

        // Step 1: Create WinForms app
        var createResult = await RunDotnetCommandAsync(projectDir, $"new winforms -n {projectName} -o .");
        Assert.AreEqual(0, createResult.ExitCode, $"Failed to create WinForms app: {createResult.Output}");

        // Step 2: Build the application
        var buildResult = await RunDotnetCommandAsync(projectDir, "build -c Release");
        Assert.AreEqual(0, buildResult.ExitCode, $"Failed to build WinForms app: {buildResult.Output}");

        var binFolder = new DirectoryInfo(Path.Combine(projectDir.FullName, "bin", "Release"));
        var buildOutputFolder = FindBuildOutputFolder(binFolder);
        Assert.IsNotNull(buildOutputFolder, "Build output folder should exist");

        var exePath = Path.Combine(buildOutputFolder.FullName, $"{projectName}.exe");
        Assert.IsTrue(File.Exists(exePath), "Built executable should exist");

        // Step 3: Generate manifest with custom options
        var manifestGenerateCommand = GetRequiredService<ManifestGenerateCommand>();
        var manifestArgs = new[]
        {
            projectDir.FullName,
            "--package-name", "net10.0-windows",
            "--publisher-name", "CN=TestPublisher",
            "--version", "2.5.0.0",
            "--description", "Custom test application",
            "--executable", exePath
        };

        var manifestExitCode = await ParseAndInvokeWithCaptureAsync(manifestGenerateCommand, manifestArgs);

        Assert.AreEqual(0, manifestExitCode, "Manifest generate command should complete successfully");

        // Verify custom options were applied
        var manifestPath = Path.Combine(projectDir.FullName, "Package.appxmanifest");
        Assert.IsTrue(File.Exists(manifestPath), "Manifest should be created");

        var manifestContent = await File.ReadAllTextAsync(manifestPath, TestContext.CancellationToken);
        Assert.IsTrue(manifestContent.Contains("Id=\"net10.A0Windows\"", StringComparison.OrdinalIgnoreCase),
            "Manifest should contain custom package name");
        Assert.IsTrue(manifestContent.Contains("CN=TestPublisher", StringComparison.Ordinal),
            "Manifest should contain custom publisher");
        Assert.IsTrue(manifestContent.Contains("2.5.0.0", StringComparison.Ordinal),
            "Manifest should contain custom version");

        // Step 4: Package the application

        var packageCommand = GetRequiredService<PackageCommand>();
        var packageOutputPath = Path.Combine(_tempDirectory.FullName, $"{projectName}.msix");
        var packageArgs = new[]
        {
            buildOutputFolder.FullName,
            "--output", packageOutputPath,
            "--manifest", manifestPath
        };

        var packageParseResult = packageCommand.Parse(packageArgs);
        var packageExitCode = await packageParseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(0, packageExitCode, "Package command should complete successfully");

        Assert.IsTrue(File.Exists(packageOutputPath), "MSIX package should be created");
    }

    [TestMethod]
    [DataRow("stable", DisplayName = "WinAppSDK Stable")]
    [DataRow("experimental", DisplayName = "WinAppSDK Experimental")]
    public async Task E2E_DotNetApp_PackageShouldIncludeRuntimeDependency(string sdkMode)
    {
        // Verifies that 'winapp init' sets up the project correctly (TFM, PackageReferences,
        // manifest) and that the package command discovers Windows App SDK from the .csproj
        // (via `dotnet list package --format json`) and injects the correct
        // <PackageDependency> into the MSIX manifest.

        // Step 1: Create WinForms app in _tempDirectory (FindCsproj searches here)
        var projectName = "TestApp";
        var createResult = await RunDotnetCommandAsync(_tempDirectory, $"new winforms -n {projectName} -o .");
        Assert.AreEqual(0, createResult.ExitCode, $"Failed to create WinForms app: {createResult.Output}");

        // Step 2: Run 'winapp init' to set up TFM, PackageReferences, manifest, and build tools
        var initCommand = GetRequiredService<InitCommand>();
        var initExitCode = await ParseAndInvokeWithCaptureAsync(initCommand,
        [
            _tempDirectory.FullName,
            "--use-defaults",
            "--setup-sdks", sdkMode
        ]);
        Assert.AreEqual(0, initExitCode, "winapp init should succeed");

        // Read the WinAppSDK version that init installed (for verification later)
        var csprojPath = Path.Combine(_tempDirectory.FullName, $"{projectName}.csproj");
        var csprojContent = await File.ReadAllTextAsync(csprojPath, TestContext.CancellationToken);
        var winAppSdkVersion = ExtractPackageVersion(csprojContent, "Microsoft.WindowsAppSDK");
        Assert.IsNotNull(winAppSdkVersion, "csproj should contain WindowsAppSDK version after init");

        var manifestPath = Path.Combine(_tempDirectory.FullName, "Package.appxmanifest");
        Assert.IsTrue(File.Exists(manifestPath), "winapp init should create Package.appxmanifest");

        // Step 3: Build
        var buildResult = await RunDotnetCommandAsync(_tempDirectory, "build -c Release");
        Assert.AreEqual(0, buildResult.ExitCode, $"Failed to build: {buildResult.Output}");

        var binFolder = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "bin", "Release"));
        var buildOutputFolder = FindBuildOutputFolder(binFolder);
        Assert.IsNotNull(buildOutputFolder, "Build output folder should exist");

        // Step 4: Install WinAppSDK packages to test cache (build tools + runtime)
        await EnsureWinAppSdkPackagesInTestCacheAsync();

        // Step 5: Package (no winapp.yaml — uses csproj for WinAppSDK detection)
        var packageCommand = GetRequiredService<PackageCommand>();
        var packageOutputPath = Path.Combine(_tempDirectory.FullName, $"{projectName}.msix");
        var packageParseResult = packageCommand.Parse(
        [
            buildOutputFolder.FullName,
            "--output", packageOutputPath,
            "--manifest", manifestPath,
            "--skip-pri"
        ]);
        var packageExitCode = await packageParseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(0, packageExitCode, "Package command should succeed");

        // Step 6: Verify manifest in MSIX has the runtime dependency
        Assert.IsTrue(File.Exists(packageOutputPath), "MSIX should be created");

        var extractDir = Path.Combine(_tempDirectory.FullName, "extracted");
        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(packageOutputPath, extractDir);

        var finalManifest = await File.ReadAllTextAsync(
            Path.Combine(extractDir, "AppxManifest.xml"), TestContext.CancellationToken);

        var versionParts = winAppSdkVersion.Split('.');

        // Assert only on what's invariant across SDK naming-convention churn:
        //   * The PackageDependency name MUST start with "Microsoft.WindowsAppRuntime.{major}".
        //     Whether the framework is "Microsoft.WindowsAppRuntime.2" (2.0.1+ stable, major-only),
        //     "Microsoft.WindowsAppRuntime.1.8" (per-minor SxS in 1.x), or
        //     "Microsoft.WindowsAppRuntime.2-experimentalN" (2.x experimental) is the SDK's call,
        //     not ours — the CLI faithfully propagates whatever name appears in MSIX.inventory.
        //   * MinVersion's first three components must match the SDK version
        //     (the inventory ships a Major.Minor.Patch.Revision quad; the leading triple is the SDK version).
        var expectedNamePrefix = $"Name=\"Microsoft.WindowsAppRuntime.{versionParts[0]}";
        var expectedMinVersionPrefix = versionParts.Length >= 3
            ? $"MinVersion=\"{versionParts[0]}.{versionParts[1]}.{versionParts[2].Split('-')[0]}."
            : $"MinVersion=\"{versionParts[0]}.";

        Assert.Contains("<PackageDependency", finalManifest,
            "Manifest should contain a PackageDependency element");
        Assert.Contains(expectedNamePrefix, finalManifest,
            $"PackageDependency Name should start with Microsoft.WindowsAppRuntime.{versionParts[0]}");
        Assert.Contains(expectedMinVersionPrefix, finalManifest,
            $"PackageDependency MinVersion should be derived from SDK version {winAppSdkVersion}");
    }

    [TestMethod]
    [DataRow("stable", DisplayName = "WinAppSDK Stable")]
    [DataRow("experimental", DisplayName = "WinAppSDK Experimental")]
    public async Task E2E_DotNetApp_WithWin2D_PackageShouldIncludeActivatableClasses(string sdkMode)
    {
        // Verifies that Win2D activatable classes (InProcessServer entries) are discovered
        // from the NuGet package .winmd files and injected into the MSIX manifest.
        // Uses 'winapp init' to set up TFM, PackageReferences, and manifest, then
        // adds Win2D as an additional package.

        var projectName = "TestAppWin2D";
        var createResult = await RunDotnetCommandAsync(_tempDirectory, $"new winforms -n {projectName} -o .");
        Assert.AreEqual(0, createResult.ExitCode, $"Failed to create WinForms app: {createResult.Output}");

        // Run 'winapp init' to set up TFM, PackageReferences, and manifest
        var initCommand = GetRequiredService<InitCommand>();
        var initExitCode = await ParseAndInvokeWithCaptureAsync(initCommand,
        [
            _tempDirectory.FullName,
            "--use-defaults",
            "--setup-sdks", sdkMode
        ]);
        Assert.AreEqual(0, initExitCode, "winapp init should succeed");

        // Add Win2D (init only adds WinAppSDK + BuildTools)
        var addWin2dResult = await RunDotnetCommandAsync(_tempDirectory, "add package Microsoft.Graphics.Win2D --version 1.3.0");
        Assert.AreEqual(0, addWin2dResult.ExitCode, $"Failed to add Win2D: {addWin2dResult.Output}");

        var manifestPath = Path.Combine(_tempDirectory.FullName, "Package.appxmanifest");
        Assert.IsTrue(File.Exists(manifestPath), "winapp init should create Package.appxmanifest");

        var buildResult = await RunDotnetCommandAsync(_tempDirectory, "build -c Release");
        Assert.AreEqual(0, buildResult.ExitCode, $"Failed to build: {buildResult.Output}");

        var binFolder = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "bin", "Release"));
        var buildOutputFolder = FindBuildOutputFolder(binFolder);
        Assert.IsNotNull(buildOutputFolder, "Build output folder should exist");

        // Install WinAppSDK packages to test cache (build tools + runtime)
        await EnsureWinAppSdkPackagesInTestCacheAsync();

        // Package
        var packageCommand = GetRequiredService<PackageCommand>();
        var packageOutputPath = Path.Combine(_tempDirectory.FullName, $"{projectName}.msix");
        var packageParseResult = packageCommand.Parse(
        [
            buildOutputFolder.FullName,
            "--output", packageOutputPath,
            "--manifest", manifestPath,
            "--skip-pri"
        ]);
        var packageExitCode = await packageParseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(0, packageExitCode, "Package command should succeed");

        // Verify manifest has Win2D InProcessServer entries
        Assert.IsTrue(File.Exists(packageOutputPath), "MSIX should be created");

        var extractDir = Path.Combine(_tempDirectory.FullName, "extracted");
        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(packageOutputPath, extractDir);

        var finalManifest = await File.ReadAllTextAsync(
            Path.Combine(extractDir, "AppxManifest.xml"), TestContext.CancellationToken);

        Assert.Contains("windows.activatableClass.inProcessServer", finalManifest,
            "Manifest should contain inProcessServer extension category");
        Assert.Contains("<InProcessServer>", finalManifest,
            "Manifest should contain InProcessServer element");
        Assert.Contains("Microsoft.Graphics.Canvas.dll", finalManifest,
            "Manifest should reference Microsoft.Graphics.Canvas.dll");
        Assert.Contains("Microsoft.Graphics.Canvas.CanvasDevice", finalManifest,
            "Manifest should register CanvasDevice activatable class");
    }

    [TestMethod]
    [DataRow("stable", DisplayName = "WinAppSDK Stable")]
    [DataRow("experimental", DisplayName = "WinAppSDK Experimental")]
    public async Task E2E_DotNetApp_SelfContained_ShouldBundleRuntimeAndEmbedActivatableClassesInExe(string sdkMode)
    {
        // Verifies that --self-contained:
        // 1. Does NOT add <PackageDependency> or <InProcessServer> to the MSIX AppxManifest
        // 2. DOES embed Win2D activatable classes into the exe's SxS (side-by-side) manifest

        var projectName = "TestAppSelfContained";
        var createResult = await RunDotnetCommandAsync(_tempDirectory, $"new winforms -n {projectName} -o .");
        Assert.AreEqual(0, createResult.ExitCode, $"Failed to create WinForms app: {createResult.Output}");

        // Run 'winapp init' to set up TFM, PackageReferences, and manifest
        var initCommand = GetRequiredService<InitCommand>();
        var initExitCode = await ParseAndInvokeWithCaptureAsync(initCommand,
        [
            _tempDirectory.FullName,
            "--use-defaults",
            "--setup-sdks", sdkMode
        ]);
        Assert.AreEqual(0, initExitCode, "winapp init should succeed");

        // Add Win2D (init only adds WinAppSDK + BuildTools)
        var addWin2dResult = await RunDotnetCommandAsync(_tempDirectory, "add package Microsoft.Graphics.Win2D --version 1.3.0");
        Assert.AreEqual(0, addWin2dResult.ExitCode, $"Failed to add Win2D: {addWin2dResult.Output}");

        var manifestPath = Path.Combine(_tempDirectory.FullName, "Package.appxmanifest");
        Assert.IsTrue(File.Exists(manifestPath), "winapp init should create Package.appxmanifest");

        var buildResult = await RunDotnetCommandAsync(_tempDirectory, "build -c Release");
        Assert.AreEqual(0, buildResult.ExitCode, $"Failed to build: {buildResult.Output}");

        var binFolder = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "bin", "Release"));
        var buildOutputFolder = FindBuildOutputFolder(binFolder);
        Assert.IsNotNull(buildOutputFolder, "Build output folder should exist");

        // Install WinAppSDK packages to test cache (build tools + runtime)
        await EnsureWinAppSdkPackagesInTestCacheAsync();

        // Package with --self-contained
        var packageCommand = GetRequiredService<PackageCommand>();
        var packageOutputPath = Path.Combine(_tempDirectory.FullName, $"{projectName}.msix");
        var packageParseResult = packageCommand.Parse(
        [
            buildOutputFolder.FullName,
            "--output", packageOutputPath,
            "--manifest", manifestPath,
            "--skip-pri",
            "--self-contained"
        ]);
        var packageExitCode = await packageParseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(0, packageExitCode, "Package command should succeed");

        // Extract the MSIX
        Assert.IsTrue(File.Exists(packageOutputPath), "MSIX should be created");

        var extractDir = Path.Combine(_tempDirectory.FullName, "extracted");
        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(packageOutputPath, extractDir);

        // Verify AppxManifest does NOT have PackageDependency or InProcessServer
        var finalManifest = await File.ReadAllTextAsync(
            Path.Combine(extractDir, "AppxManifest.xml"), TestContext.CancellationToken);

        Assert.DoesNotContain("<PackageDependency", finalManifest,
            "Self-contained package should NOT have PackageDependency (runtime is bundled)");
        Assert.DoesNotContain("inProcessServer", finalManifest,
            "Self-contained package should NOT have InProcessServer in AppxManifest (goes in SxS manifest)");

        // Verify Win2D activatable classes ARE embedded in the exe's SxS manifest.
        // The packaging step uses mt.exe to merge a WinRT activation manifest into the
        // executable's Win32 resource section. We verify by reading the exe bytes and
        // searching for known XML fragments from the embedded manifest.
        var exeInMsix = Path.Combine(extractDir, $"{projectName}.exe");
        Assert.IsTrue(File.Exists(exeInMsix), "Exe should be in MSIX");

        var exeBytes = await File.ReadAllBytesAsync(exeInMsix);
        var exeText = Encoding.ASCII.GetString(exeBytes);

        Assert.Contains("Microsoft.Graphics.Canvas.CanvasDevice", exeText,
            "Exe SxS manifest should contain Win2D CanvasDevice activatable class");
        Assert.Contains("winrt.v1", exeText,
            "Exe SxS manifest should contain WinRT manifest namespace");
    }

    [TestMethod]
    public async Task E2E_DotNetProject_InitWithSetupSdksNone_SkipsPackageReferences_ShouldSucceed()
    {
        // This test verifies that --setup-sdks none skips adding package references
        // but still creates manifest and certificate

        // Arrange
        var projectDir = _tempDirectory.CreateSubdirectory("DotNetConsoleAppNoSdk");
        var projectName = "TestConsoleAppNoSdk";

        // Step 1: Create a new console application
        var createResult = await RunDotnetCommandAsync(projectDir, $"new console -n {projectName} -o .");
        Assert.AreEqual(0, createResult.ExitCode, $"Failed to create console app: {createResult.Output}");

        var csprojPath = Path.Combine(projectDir.FullName, $"{projectName}.csproj");


        // Step 2: Run 'winapp init --setup-sdks none --use-defaults'
        var initCommand = GetRequiredService<InitCommand>();
        var initArgs = new[]
        {
            projectDir.FullName,
            "--setup-sdks", "none",
            "--use-defaults"
        };

        var initExitCode = await ParseAndInvokeWithCaptureAsync(initCommand, initArgs);
        Assert.AreEqual(0, initExitCode, "Init command should complete successfully");

        // Step 3: Verify that csproj was NOT modified (no package references added)
        var updatedCsprojContent = await File.ReadAllTextAsync(csprojPath, TestContext.CancellationToken);

        // When --setup-sdks none, we should not add WindowsAppSDK
        Assert.IsFalse(
            updatedCsprojContent.Contains("Microsoft.WindowsAppSDK", StringComparison.OrdinalIgnoreCase),
            "csproj should NOT contain Microsoft.WindowsAppSDK when --setup-sdks none is used");

        // Step 4: Manifest should still be created
        var manifestPath = Path.Combine(projectDir.FullName, "Package.appxmanifest");
        Assert.IsTrue(File.Exists(manifestPath), "Manifest should be created even with --setup-sdks none");

        Console.WriteLine("Successfully initialized .NET project with --setup-sdks none");
    }

    [TestMethod]
    public async Task E2E_DotNetProject_InitDetectsCsprojAndAddsPackageReferences_ShouldSucceed()
    {
        // This test verifies the .NET project workflow:
        // 1. Creates a new console app with a .csproj
        // 2. Runs 'winapp init --use-defaults' which should detect the .csproj
        // 3. Verifies that NuGet package references were added to the .csproj
        // 4. Verifies manifest and assets were created

        // Arrange
        var projectDir = _tempDirectory.CreateSubdirectory("DotNetConsoleApp");
        var projectName = "TestConsoleApp";

        // Step 1: Create a new console application using dotnet CLI
        var createResult = await RunDotnetCommandAsync(projectDir, $"new console -n {projectName} -o .");
        Assert.AreEqual(0, createResult.ExitCode, $"Failed to create console app: {createResult.Output}");

        var csprojPath = Path.Combine(projectDir.FullName, $"{projectName}.csproj");
        Assert.IsTrue(File.Exists(csprojPath), "Project file should be created");

        // The template's TFM follows whichever SDK the machine resolves (e.g. net11.0 under SDK 11).
        var originalTargetFramework = XDocument.Load(csprojPath).Descendants("TargetFramework").Single().Value.Trim();
        Assert.MatchesRegex(@"^net\d+\.\d+$", originalTargetFramework,
            "dotnet new console should produce a plain netX.Y TargetFramework");

        // Step 2: Run 'winapp init --use-defaults' to detect csproj and set up the project
        var initCommand = GetRequiredService<InitCommand>();
        var initArgs = new[]
        {
            projectDir.FullName,
            "--use-defaults"  // Non-interactive mode
        };

        var initExitCode = await ParseAndInvokeWithCaptureAsync(initCommand, initArgs);
        Assert.AreEqual(0, initExitCode, "Init command should complete successfully");

        // Step 3: Verify that NuGet package references were added to the .csproj
        var updatedCsprojContent = await File.ReadAllTextAsync(csprojPath, TestContext.CancellationToken);

        // The init command should add WindowsAppSDK package reference
        Assert.IsTrue(
            updatedCsprojContent.Contains("Microsoft.WindowsAppSDK", StringComparison.OrdinalIgnoreCase),
            "csproj should contain Microsoft.WindowsAppSDK package reference");

        // Step 4: Verify the TargetFramework was updated to a Windows TFM while preserving the .NET version
        Assert.IsTrue(
            updatedCsprojContent.Contains("-windows", StringComparison.OrdinalIgnoreCase),
            "csproj TargetFramework should be updated to Windows TFM");

        // Verify the .NET version was preserved: e.g. net10.0 -> net10.0-windows10.0.26100.0, never downgraded
        Assert.Contains($"<TargetFramework>{originalTargetFramework}-windows", updatedCsprojContent,
            $"csproj TargetFramework should keep {originalTargetFramework} and add the Windows SDK version");

        // Step 5: Verify manifest and assets were created
        var manifestPath = Path.Combine(projectDir.FullName, "Package.appxmanifest");
        Assert.IsTrue(File.Exists(manifestPath), "Manifest should be created");

        var assetsDir = Path.Combine(projectDir.FullName, "Assets");
        Assert.IsTrue(Directory.Exists(assetsDir), "Assets directory should be created");

        // Verify the manifest contains expected content
        var manifestContent = await File.ReadAllTextAsync(manifestPath, TestContext.CancellationToken);
        Assert.IsTrue(manifestContent.Contains("Identity", StringComparison.OrdinalIgnoreCase),
            "Manifest should contain Identity element");

        Console.WriteLine("Successfully set up .NET project with winapp init");
    }

    [TestMethod]
    public async Task E2E_NodeWrapper_PackageWithMissingInputFolder_ShouldSurfaceErrorOutput()
    {
        // Arrange
        var missingInput = Path.Combine(_tempDirectory.FullName, "missing-package-input");

        // Act
        var result = await RunNodeWinappCommandAsync(
            ["package", missingInput],
            _tempDirectory,
            TestContext.CancellationToken);

        // Assert
        Assert.AreNotEqual(0, result.ExitCode, "Command should fail for non-existent package input folder.");
        var combinedOutput = $"{result.Output}\n{result.Error}";
        Assert.IsTrue(
            combinedOutput.Contains("Input folder(s) not found:", StringComparison.OrdinalIgnoreCase)
                || combinedOutput.Contains("Directory does not exist", StringComparison.OrdinalIgnoreCase),
            $"Expected package missing-folder error to be surfaced. Output: {combinedOutput}");
    }

    [TestMethod]
    public async Task E2E_NodeSubcommand_AddElectronDebugIdentityWithoutElectronExe_ShouldSurfaceNodeError()
    {
        // Arrange
        var emptyProjectDir = _tempDirectory.CreateSubdirectory("NodeNoElectronProject");
        var manifestPath = Path.Combine(emptyProjectDir.FullName, "appxmanifest.xml");
        await File.WriteAllTextAsync(manifestPath, "<Package />", TestContext.CancellationToken);

        // Act
        var result = await RunNodeWinappCommandAsync(
            ["node", "add-electron-debug-identity", "--manifest", manifestPath],
            emptyProjectDir,
            TestContext.CancellationToken);

        // Assert
        Assert.AreNotEqual(0, result.ExitCode, "Command should fail when electron.exe does not exist.");
        var combinedOutput = $"{result.Output}\n{result.Error}";
        // Either branch of the new error messaging is acceptable: "Electron is not installed"
        // (no node_modules/electron) or "binary was not found" (node_modules/electron present
        // but exe missing, e.g. Electron 42+ before `install-electron` runs).
        Assert.IsTrue(
            combinedOutput.Contains("Electron is not installed", StringComparison.OrdinalIgnoreCase)
                || combinedOutput.Contains("binary was not found", StringComparison.OrdinalIgnoreCase),
            $"Expected Node-layer electron missing error to be surfaced. Output: {combinedOutput}");
    }

    [TestMethod]
    public async Task E2E_NodeSubcommand_ClearElectronDebugIdentityWithoutElectronArtifacts_ShouldSurfaceNodeError()
    {
        // Arrange
        var emptyProjectDir = _tempDirectory.CreateSubdirectory("NodeNoElectronArtifacts");

        // Act
        var result = await RunNodeWinappCommandAsync(
            ["node", "clear-electron-debug-identity"],
            emptyProjectDir,
            TestContext.CancellationToken);

        // Assert
        Assert.AreNotEqual(0, result.ExitCode, "Command should fail when electron executable and backup are missing.");
        var combinedOutput = $"{result.Output}\n{result.Error}";
        Assert.IsTrue(
            combinedOutput.Contains("Neither Electron executable nor backup found in:", StringComparison.OrdinalIgnoreCase),
            $"Expected Node-layer missing-electron-artifacts error to be surfaced. Output: {combinedOutput}");
    }

    [TestMethod]
    public async Task E2E_NodeSubcommand_AddElectronDebugIdentityWithMissingManifest_ShouldSurfaceNativeErrorWithoutGenericWrapper()
    {
        // Arrange
        var projectDir = _tempDirectory.CreateSubdirectory("NodeMissingManifestPath");
        var electronDistDir = Directory.CreateDirectory(Path.Combine(projectDir.FullName, "node_modules", "electron", "dist"));
        var electronExePath = Path.Combine(electronDistDir.FullName, "electron.exe");
        await File.WriteAllTextAsync(electronExePath, "fake electron executable", TestContext.CancellationToken);

        var missingManifestPath = Path.Combine(projectDir.FullName, "don", "appxmanifest.xml");

        // Act
        var result = await RunNodeWinappCommandAsync(
            ["node", "add-electron-debug-identity", "--manifest", missingManifestPath],
            projectDir,
            TestContext.CancellationToken);

        // Assert
        Assert.AreNotEqual(0, result.ExitCode, "Command should fail for non-existent manifest path.");
        var combinedOutput = $"{result.Output}\n{result.Error}";
        Assert.IsTrue(
            combinedOutput.Contains("File does not exist", StringComparison.OrdinalIgnoreCase)
                || combinedOutput.Contains("AppX manifest not found", StringComparison.OrdinalIgnoreCase),
            $"Expected native manifest-not-found error to be surfaced. Output: {combinedOutput}");
        Assert.IsFalse(
            combinedOutput.Contains("Failed to add Electron debug identity: winapp-cli exited with code", StringComparison.OrdinalIgnoreCase),
            $"Did not expect duplicate generic wrapper error output. Output: {combinedOutput}");
    }

    [TestMethod]
    public async Task E2E_NodeSubcommand_AddElectronDebugIdentityWithInvalidManifest_ShouldSurfaceManifestError()
    {
        // Arrange
        var projectDir = _tempDirectory.CreateSubdirectory("NodeInvalidManifestPath");
        var electronDistDir = Directory.CreateDirectory(Path.Combine(projectDir.FullName, "node_modules", "electron", "dist"));
        var electronExePath = Path.Combine(electronDistDir.FullName, "electron.exe");
        await File.WriteAllTextAsync(electronExePath, "fake electron executable", TestContext.CancellationToken);

        var invalidManifestPath = Path.Combine(projectDir.FullName, "appxmanifest.xml");
        await File.WriteAllTextAsync(invalidManifestPath, "<Package></Package>", TestContext.CancellationToken);

        // Act
        var result = await RunNodeWinappCommandAsync(
            ["node", "add-electron-debug-identity", "--manifest", invalidManifestPath, "--no-install"],
            projectDir,
            TestContext.CancellationToken);

        // Assert
        Assert.AreNotEqual(0, result.ExitCode, "Command should fail for invalid AppX manifest.");
        var combinedOutput = $"{result.Output}\n{result.Error}";
        Assert.IsTrue(
            combinedOutput.Contains("No Identity element found in AppX manifest", StringComparison.OrdinalIgnoreCase),
            $"Expected invalid-manifest diagnostic to be surfaced. Output: {combinedOutput}");
        Assert.IsFalse(
            combinedOutput.Contains("PowerShell command failed with exit code", StringComparison.OrdinalIgnoreCase),
            $"Did not expect generic PowerShell exit-code prefix in output. Output: {combinedOutput}");
    }

        [TestMethod]
        public async Task E2E_CreateDebugIdentityWithInvalidIdentityName_ShouldSurfaceDecodedPowerShellError()
        {
                // Arrange
                var projectDir = _tempDirectory.CreateSubdirectory("NodeInvalidIdentityNameManifest");
                var electronDistDir = Directory.CreateDirectory(Path.Combine(projectDir.FullName, "node_modules", "electron", "dist"));
                var electronExePath = Path.Combine(electronDistDir.FullName, "electron.exe");

                // Use a real PE file so mt.exe succeeds and registration reaches PowerShell Add-AppxPackage.
                var sourceExePath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                File.Copy(sourceExePath, electronExePath, overwrite: true);

                var assetsDir = Directory.CreateDirectory(Path.Combine(projectDir.FullName, "Assets"));
                await File.WriteAllTextAsync(Path.Combine(assetsDir.FullName, "Square150x150Logo.png"), "placeholder", TestContext.CancellationToken);
                await File.WriteAllTextAsync(Path.Combine(assetsDir.FullName, "Square44x44Logo.png"), "placeholder", TestContext.CancellationToken);
                await File.WriteAllTextAsync(Path.Combine(assetsDir.FullName, "StoreLogo.png"), "placeholder", TestContext.CancellationToken);

                var invalidManifestPath = Path.Combine(projectDir.FullName, "appxmanifest.xml");
                var invalidManifestContent = """
                        <?xml version="1.0" encoding="utf-8"?>
                        <Package
                                xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                                xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                                xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
                                IgnorableNamespaces="uap rescap">
                            <Identity Name="my-  app" Publisher="CN=TestPublisher" Version="1.0.0.0" />
                            <Properties>
                                <DisplayName>Test App</DisplayName>
                                <PublisherDisplayName>Test Publisher</PublisherDisplayName>
                                <Logo>Assets/StoreLogo.png</Logo>
                            </Properties>
                            <Resources>
                                <Resource Language="en-us" />
                            </Resources>
                            <Applications>
                                <Application Id="App" Executable="electron.exe" EntryPoint="Windows.FullTrustApplication">
                                    <uap:VisualElements
                                        DisplayName="Test App"
                                        Description="Test App"
                                        BackgroundColor="transparent"
                                        Square150x150Logo="Assets/Square150x150Logo.png"
                                        Square44x44Logo="Assets/Square44x44Logo.png">
                                    </uap:VisualElements>
                                </Application>
                            </Applications>
                            <Capabilities>
                                <rescap:Capability Name="runFullTrust" />
                            </Capabilities>
                        </Package>
                        """;
                await File.WriteAllTextAsync(invalidManifestPath, invalidManifestContent, TestContext.CancellationToken);

                var repoRoot = FindRepositoryRoot();
                Assert.IsNotNull(repoRoot, "Could not find repository root containing version.json.");

                var cliProjectPath = Path.Combine(repoRoot.FullName, "src", "winapp-CLI", "WinApp.Cli", "WinApp.Cli.csproj");
                Assert.IsTrue(File.Exists(cliProjectPath), $"Native CLI project not found at: {cliProjectPath}");

                // Act
                var result = await RunDotnetCommandAsync(
                    projectDir,
                    $"run --project \"{cliProjectPath}\" -- create-debug-identity \"{electronExePath}\" --manifest \"{invalidManifestPath}\"");

                    // Assert
                    Assert.AreNotEqual(0, result.ExitCode, "Command should fail for manifest with invalid Identity Name value.");
                    var combinedOutput = $"{result.Output}\n{result.Error}";
                    Assert.IsTrue(
                        combinedOutput.Contains("Failed to add package identity:", StringComparison.OrdinalIgnoreCase),
                        $"Expected add-package-identity failure message to be surfaced. Output: {combinedOutput}");

                    // Ensure this fails for the intended reason (invalid identity name), not due to
                    // a malformed VisualElements rewrite (e.g. missing AppListEntry on non-self-closing tags).
                    var looksLikeIdentityNameValidationError =
                        combinedOutput.Contains("Name", StringComparison.OrdinalIgnoreCase)
                        && (combinedOutput.Contains("violates pattern constraint", StringComparison.OrdinalIgnoreCase)
                            || combinedOutput.Contains("failed to parse", StringComparison.OrdinalIgnoreCase)
                            || combinedOutput.Contains("invalid", StringComparison.OrdinalIgnoreCase)
                            || combinedOutput.Contains("not valid", StringComparison.OrdinalIgnoreCase));

                    Assert.IsTrue(
                        looksLikeIdentityNameValidationError,
                        $"Expected invalid Identity Name validation to be the failure reason. Output: {combinedOutput}");

                    Assert.IsFalse(
                        combinedOutput.Contains("AppListEntry", StringComparison.OrdinalIgnoreCase),
                        $"Did not expect failure to be caused by missing AppListEntry (VisualElements rewrite regression). Output: {combinedOutput}");

                    Assert.IsFalse(
                        combinedOutput.Contains("Get-AppPackageLog", StringComparison.OrdinalIgnoreCase)
                            || combinedOutput.Contains("At line:", StringComparison.OrdinalIgnoreCase)
                            || combinedOutput.Contains("FullyQualifiedErrorId", StringComparison.OrdinalIgnoreCase)
                            || combinedOutput.Contains("CategoryInfo", StringComparison.OrdinalIgnoreCase)
                            || combinedOutput.Contains("_x000D__x000A_", StringComparison.OrdinalIgnoreCase)
                            || combinedOutput.Contains("<Objs Version=", StringComparison.OrdinalIgnoreCase)
                            || combinedOutput.Contains("#< CLIXML", StringComparison.OrdinalIgnoreCase),
                        $"Did not expect raw CLIXML output. Output: {combinedOutput}");
        }

    /// <summary>
    /// Finds the build output folder under bin/Release, handling both direct TFM layouts
    /// (e.g., net10.0-windows10.0.26100.0/) and runtime-specific layouts
    /// (e.g., net10.0-windows10.0.26100.0/win-x64/).
    /// </summary>
    private static DirectoryInfo? FindBuildOutputFolder(DirectoryInfo binReleaseFolder)
    {
        var tfmFolder = binReleaseFolder.GetDirectories("net*-windows*").FirstOrDefault();
        if (tfmFolder == null)
        {
            return null;
        }

        // Check for a runtime-specific subfolder (e.g., win-x64, win-arm64)
        var runtimeFolder = tfmFolder.GetDirectories("win-*").FirstOrDefault();
        return runtimeFolder ?? tfmFolder;
    }

    /// <summary>
    /// Helper method to run dotnet commands
    /// </summary>
    private static async Task<(int ExitCode, string Output, string Error)> RunDotnetCommandAsync(
        DirectoryInfo workingDirectory,
        string arguments)
    {
        var processStartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            WorkingDirectory = workingDirectory.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new System.Diagnostics.Process { StartInfo = processStartInfo };

        var outputBuilder = new System.Text.StringBuilder();
        var errorBuilder = new System.Text.StringBuilder();

        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                outputBuilder.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                errorBuilder.AppendLine(e.Data);
            }
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync();

        var output = outputBuilder.ToString();
        var error = errorBuilder.ToString();

        return (process.ExitCode, output, error);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunNodeWinappCommandAsync(
        string[] args,
        DirectoryInfo workingDirectory,
        CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot();
        Assert.IsNotNull(repoRoot, "Could not find repository root containing version.json.");

        var cliPath = Path.Combine(repoRoot.FullName, "src", "winapp-npm", "dist", "cli.js");
        Assert.IsTrue(File.Exists(cliPath), $"Node CLI entry point not found at: {cliPath}. Run 'npm run build' in src/winapp-npm.");

        var processStartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "node",
            Arguments = BuildNodeArguments(cliPath, args),
            WorkingDirectory = workingDirectory.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new System.Diagnostics.Process { StartInfo = processStartInfo };
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                outputBuilder.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                errorBuilder.AppendLine(e.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Fail("Node.js executable was not found on PATH for this test run.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString());
    }

    private static DirectoryInfo? FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var markerPath = Path.Combine(current.FullName, "version.json");
            if (File.Exists(markerPath))
            {
                return current;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string BuildNodeArguments(string cliPath, string[] args)
    {
        static string Escape(string value)
            => value.Contains(' ') || value.Contains('"')
                ? $"\"{value.Replace("\"", "\\\"")}\""
                : value;

        var escaped = args.Select(Escape);
        return $"{Escape(cliPath)} {string.Join(" ", escaped)}";
    }

    /// <summary>
    /// Extracts a NuGet package version from a .csproj file's PackageReference elements.
    /// Returns null if the package is not found.
    /// </summary>
    private static string? ExtractPackageVersion(string csprojContent, string packageName)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            csprojContent,
            $@"<PackageReference\s+Include=""{System.Text.RegularExpressions.Regex.Escape(packageName)}""\s+Version=""([^""]+)""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Installs the Windows App SDK (with dependencies including runtime and build tools)
    /// into the test NuGet cache at the exact versions resolved by the project's .csproj.
    /// <para>
    /// This is necessary because the test infrastructure overrides the NuGet cache directory
    /// to an isolated temp location, while <c>dotnet restore</c> installs packages to the
    /// real global NuGet cache. The CLI's <c>BuildToolsService</c> and
    /// <c>WorkspaceSetupService</c> look for packages (makeappx.exe, MSIX runtime files)
    /// in the test cache and fail if they're not present.
    /// </para>
    /// </summary>
    private async Task EnsureWinAppSdkPackagesInTestCacheAsync()
    {
        var dotNetService = GetRequiredService<IDotNetService>();
        var csprojFiles = dotNetService.FindCsproj(_tempDirectory);
        if (csprojFiles.Count == 0)
        {
            return;
        }

        var packageList = await dotNetService.GetPackageListAsync(csprojFiles[0], cancellationToken: TestContext.CancellationToken);
        var frameworks = packageList?.Projects?
            .SelectMany(p => p.Frameworks ?? [])
            .ToList();

        if (frameworks == null || frameworks.Count == 0)
        {
            return;
        }

        // Copy packages from the real NuGet cache (populated by 'dotnet build' above)
        // into the test cache. This avoids expensive HTTP downloads that timeout when
        // 12+ tests run in parallel and all try to download large packages simultaneously.
        var allPackages = frameworks
            .SelectMany(f => (f.TopLevelPackages ?? []).Concat(f.TransitivePackages ?? []))
            .DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var pkg in allPackages)
        {
            await EnsurePackageInTestCacheAsync(pkg.Id, pkg.ResolvedVersion, TestContext.CancellationToken);
        }
    }
}

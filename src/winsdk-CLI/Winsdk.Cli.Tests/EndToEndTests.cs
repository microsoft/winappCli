// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using Winsdk.Cli.Commands;

namespace Winsdk.Cli.Tests;

/// <summary>
/// End-to-end integration tests that simulate complete workflows
/// including creating, building, initializing, and packaging real .NET applications
/// </summary>
[TestClass]
public class EndToEndTests : BaseCommandTests
{
    [TestMethod]
    public async Task E2E_WinFormsApp_CreateBuildManifestAndPackage_ShouldSucceed()
    {
        // This is a comprehensive end-to-end test that:
        // 1. Creates a new WinForms app using 'dotnet new winforms'
        // 2. Builds it using 'dotnet build'
        // 3. Runs 'winsdk manifest generate' to create the manifest
        // 4. Runs 'winsdk package' to create an MSIX package
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
        
        // Find the target framework folder (e.g., net9.0-windows)
        var targetFrameworkFolder = binFolder.GetDirectories("net*-windows").FirstOrDefault();
        Assert.IsNotNull(targetFrameworkFolder, "Target framework folder should exist");

        var exePath = Path.Combine(targetFrameworkFolder.FullName, $"{projectName}.exe");
        Assert.IsTrue(File.Exists(exePath), "Built executable should exist");

        // Step 3: Run 'winsdk manifest generate' to create the manifest
        var manifestGenerateCommand = GetRequiredService<ManifestGenerateCommand>();
        var manifestArgs = new[]
        {
            projectDir.FullName,
            "--package-name", projectName,
            "--publisher-name", "CN=TestPublisher",
            "--entrypoint", exePath,
            "--yes"  // Skip interactive prompts
        };

        var manifestParseResult = manifestGenerateCommand.Parse(manifestArgs);
        var manifestExitCode = await manifestParseResult.InvokeAsync();
        Assert.AreEqual(0, manifestExitCode, "Manifest generate command should complete successfully");

        // Verify manifest generated the necessary files
        var manifestPath = Path.Combine(projectDir.FullName, "appxmanifest.xml");
        Assert.IsTrue(File.Exists(manifestPath), "Manifest generate should create appxmanifest.xml");

        var assetsDir = Path.Combine(projectDir.FullName, "Assets");
        Assert.IsTrue(Directory.Exists(assetsDir), "Manifest generate should create Assets directory");

        // Step 5: Run 'winsdk package' to create MSIX package
        var packageCommand = GetRequiredService<PackageCommand>();
        var packageOutputPath = Path.Combine(_tempDirectory.FullName, $"{projectName}.msix");
        var packageArgs = new[]
        {
            targetFrameworkFolder.FullName,  // Input folder with built binaries
            "--output", packageOutputPath,
            "--manifest", manifestPath,
            "--skip-pri"                     // Skip PRI generation for faster tests
        };

        var packageParseResult = packageCommand.Parse(packageArgs);
        var packageExitCode = await packageParseResult.InvokeAsync();
        Assert.AreEqual(0, packageExitCode, "Package command should complete successfully");

        // Step 6: Verify the MSIX package was created
        Assert.IsTrue(File.Exists(packageOutputPath), "MSIX package should be created");

        var fileInfo = new FileInfo(packageOutputPath);
        Assert.IsGreaterThan(0, fileInfo.Length, "MSIX package should not be empty");

        // Verify the MSIX contains expected files
        using var archive = ZipFile.OpenRead(packageOutputPath);
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
        var targetFrameworkFolder = binFolder.GetDirectories("net*-windows").FirstOrDefault();
        Assert.IsNotNull(targetFrameworkFolder, "Target framework folder should exist");

        var exePath = Path.Combine(targetFrameworkFolder.FullName, $"{projectName}.exe");
        Assert.IsTrue(File.Exists(exePath), "Built executable should exist");

        // Step 3: Generate manifest with custom options
        var manifestGenerateCommand = GetRequiredService<ManifestGenerateCommand>();
        var manifestArgs = new[]
        {
            projectDir.FullName,
            "--package-name", "CustomPackageName",
            "--publisher-name", "CN=TestPublisher",
            "--version", "2.5.0.0",
            "--description", "Custom test application",
            "--entrypoint", exePath,
            "--yes"
        };

        var manifestParseResult = manifestGenerateCommand.Parse(manifestArgs);
        var manifestExitCode = await manifestParseResult.InvokeAsync();
        Assert.AreEqual(0, manifestExitCode, "Manifest generate command should complete successfully");

        // Verify custom options were applied
        var manifestPath = Path.Combine(projectDir.FullName, "appxmanifest.xml");
        Assert.IsTrue(File.Exists(manifestPath), "Manifest should be created");
        
        var manifestContent = await File.ReadAllTextAsync(manifestPath);
        Assert.IsTrue(manifestContent.Contains("CustomPackageName", StringComparison.OrdinalIgnoreCase), 
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
            targetFrameworkFolder.FullName,
            "--output", packageOutputPath,
            "--manifest", manifestPath
        };

        var packageParseResult = packageCommand.Parse(packageArgs);
        var packageExitCode = await packageParseResult.InvokeAsync();
        Assert.AreEqual(0, packageExitCode, "Package command should complete successfully");

        Assert.IsTrue(File.Exists(packageOutputPath), "MSIX package should be created");
    }

    [TestMethod]
    public async Task E2E_HostedApp_PythonScript_ManifestAndDebugIdentity_ShouldSucceed()
    {
        // This test verifies the hosted app workflow for Python scripts:
        // 1. Creates a simple Python script (main.py)
        // 2. Runs 'winsdk manifest generate --template hostedapp --entrypoint main.py'
        // 3. Runs 'winsdk create-debug-identity'
        // 4. Verifies the debug identity was created successfully

        // Arrange
        var projectDir = _tempDirectory.CreateSubdirectory("PythonHostedApp");
        var scriptName = "main.py";
        var scriptPath = Path.Combine(projectDir.FullName, scriptName);

        // Step 1: Create a simple Python script
        var pythonScript = @"# Simple Python hosted app
import sys

def main():
    print(""Hello from Python hosted app!"")
    print(f""Python version: {sys.version}"")
    return 0

if __name__ == ""__main__"":
    sys.exit(main())
";
        await File.WriteAllTextAsync(scriptPath, pythonScript);
        Assert.IsTrue(File.Exists(scriptPath), "Python script should be created");

        // Step 2: Run 'winsdk manifest generate --template hostedapp --entrypoint main.py'
        var manifestGenerateCommand = GetRequiredService<ManifestGenerateCommand>();
        var manifestArgs = new[]
        {
            projectDir.FullName,
            "--template", "hostedapp",
            "--entrypoint", scriptPath,
            "--yes"  // Skip interactive prompts
        };

        var manifestParseResult = manifestGenerateCommand.Parse(manifestArgs);
        var manifestExitCode = await manifestParseResult.InvokeAsync();
        Assert.AreEqual(0, manifestExitCode, "Manifest generate command should complete successfully");

        // Verify manifest was created with hosted app configuration
        var manifestPath = Path.Combine(projectDir.FullName, "appxmanifest.xml");
        Assert.IsTrue(File.Exists(manifestPath), "Manifest should be created");

        var manifestContent = await File.ReadAllTextAsync(manifestPath);
        Assert.IsTrue(manifestContent.Contains("Python314", StringComparison.OrdinalIgnoreCase) || 
                      manifestContent.Contains("Python", StringComparison.OrdinalIgnoreCase),
            "Manifest should contain Python runtime dependency");
        Assert.IsTrue(manifestContent.Contains(scriptName, StringComparison.OrdinalIgnoreCase),
            "Manifest should reference the Python script");

        var assetsDir = Path.Combine(projectDir.FullName, "Assets");
        Assert.IsTrue(Directory.Exists(assetsDir), "Assets directory should be created");

        // Step 3: Run 'winsdk create-debug-identity'
        var createDebugIdentityCommand = GetRequiredService<CreateDebugIdentityCommand>();
        var debugIdentityArgs = new[]
        {
            "--manifest", manifestPath
        };

        var debugIdentityParseResult = createDebugIdentityCommand.Parse(debugIdentityArgs);
        var debugIdentityExitCode = await debugIdentityParseResult.InvokeAsync();
        Assert.AreEqual(0, debugIdentityExitCode, "Create debug identity command should complete successfully");

        // Verify the debug identity package was created (sparse package registration)
        // The create-debug-identity command should have registered the package
        Console.WriteLine($"Successfully created debug identity for Python hosted app: {scriptName}");
    }

    /// <summary>
    /// Helper method to run dotnet commands
    /// </summary>
    private async Task<(int ExitCode, string Output, string Error)> RunDotnetCommandAsync(
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
}

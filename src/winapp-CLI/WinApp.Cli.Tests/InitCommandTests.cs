// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for the InitCommand including SDK installation mode handling
/// </summary>
[TestClass]
public class InitCommandTests : BaseCommandTests
{
    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        return services
            .AddSingleton<IPowerShellService, FakePowerShellService>();
    }

    [TestMethod]
    public async Task InitCommand_WithConfigOnly_CreatesConfigFile()
    {
        // Arrange
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--config-only" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Init command should complete successfully");

        // Verify winapp.yaml was created in the config directory
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        Assert.IsTrue(File.Exists(configPath), $"winapp.yaml should be created at {configPath}");

        // Verify config contains packages section
        var configContent = await File.ReadAllTextAsync(configPath);
        Assert.Contains("packages:", configContent, "Config should contain packages section");
    }

    [TestMethod]
    public async Task InitCommand_WithSetupSdksNone_CompletesSuccessfully()
    {
        // Arrange
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--setup-sdks", "none", "--no-prompt" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Init command should complete successfully");

        // When SdkInstallMode is None, the command returns early after "Configuration processed"
        // The .winapp directory and config file are NOT created (this is by design)
    }

    [TestMethod]
    public async Task InitCommand_WithNoGitignore_DoesNotModifyGitignore()
    {
        // Arrange
        var gitignorePath = Path.Combine(_tempDirectory.FullName, ".gitignore");
        await File.WriteAllTextAsync(gitignorePath, "# Original content\n*.log");

        var initCommand = GetRequiredService<InitCommand>();
        // Use config-only to avoid long-running SDK installation
        var args = new[] { _tempDirectory.FullName, "--config-only", "--no-gitignore" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Init command should complete successfully");

        // Verify .gitignore was not modified
        var gitignoreContent = await File.ReadAllTextAsync(gitignorePath);
        Assert.DoesNotContain(".winapp", gitignoreContent, ".gitignore should not contain .winapp when --no-gitignore is used");
    }

    [TestMethod]
    public async Task InitCommand_WithConfigDir_CreatesConfigInSpecifiedDirectory()
    {
        // Arrange
        var configDir = _tempDirectory.CreateSubdirectory("config");
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--config-dir", configDir.FullName, "--config-only" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Init command should complete successfully");

        // Verify winapp.yaml was created in the specified config directory
        var configPath = Path.Combine(configDir.FullName, "winapp.yaml");
        Assert.IsTrue(File.Exists(configPath), $"winapp.yaml should be created at {configPath}");
    }

    [TestMethod]
    public async Task InitCommand_ConfigOnly_ExistingConfigValidated()
    {
        // Arrange - Create existing config
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        await File.WriteAllTextAsync(configPath, @"packages:
  - name: Microsoft.Windows.SDK.BuildTools
    version: 10.0.26100.1
");

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--config-only" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Init command should complete successfully");

        // Verify existing config was not overwritten (same content)
        var configContent = await File.ReadAllTextAsync(configPath);
        Assert.Contains("10.0.26100.1", configContent, "Existing config version should be preserved");
    }
}

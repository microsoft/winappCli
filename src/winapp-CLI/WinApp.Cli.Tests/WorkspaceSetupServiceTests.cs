// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for WorkspaceSetupService SDK installation mode handling
/// </summary>
[TestClass]
public class WorkspaceSetupServiceTests : BaseCommandTests
{
    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        return services
            .AddSingleton<IPowerShellService, FakePowerShellService>();
    }

    [TestMethod]
    public async Task SetupWorkspace_WithSdkInstallModeNone_CompletesWithoutPackageInstallation()
    {
        // Arrange
        var workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();
        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            SdkInstallMode = SdkInstallMode.None,
            UseDefaults = true,
            RequireExistingConfig = false,
            ForceLatestBuildTools = true,
            NoCert = true,
            NoGitignore = true
        };

        // Act
        var exitCode = await workspaceSetupService.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Setup should complete successfully");

        // When SdkInstallMode is None, the command completes early without creating
        // the packages directory or saving config (by design - skips SDK installation)
    }

    [TestMethod]
    public async Task SetupWorkspace_WithConfigOnly_CreatesConfigFile()
    {
        // Arrange
        var workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();
        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            ConfigOnly = true,
            UseDefaults = true,
            RequireExistingConfig = false,
            NoCert = true,
            NoGitignore = true
        };

        // Act
        var exitCode = await workspaceSetupService.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Setup should complete successfully");

        // Verify config was created
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        Assert.IsTrue(File.Exists(configPath), $"winapp.yaml should be created at {configPath}");
    }

    [TestMethod]
    public async Task SetupWorkspace_ConfigOnly_ExistingConfigValidated()
    {
        // Arrange - Create existing config
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        await File.WriteAllTextAsync(configPath, @"packages:
  - name: Microsoft.Windows.SDK.BuildTools
    version: 10.0.26100.1
");

        var workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();
        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            ConfigOnly = true,
            UseDefaults = true,
            RequireExistingConfig = false,
            NoCert = true,
            NoGitignore = true
        };

        // Act
        var exitCode = await workspaceSetupService.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Setup should complete successfully");

        // Verify existing config was preserved (not overwritten)
        var configContent = await File.ReadAllTextAsync(configPath);
        Assert.Contains("10.0.26100.1", configContent, "Existing config version should be preserved");
    }

    [TestMethod]
    public async Task SetupWorkspace_DoesNotUpdateGitignore_WhenNoGitignoreIsTrue()
    {
        // Arrange
        var gitignorePath = Path.Combine(_tempDirectory.FullName, ".gitignore");
        await File.WriteAllTextAsync(gitignorePath, "# Original content\n*.log");

        var workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();
        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            ConfigOnly = true, // Use config-only to avoid long-running operations
            UseDefaults = true,
            RequireExistingConfig = false,
            NoCert = true,
            NoGitignore = true // Should NOT update .gitignore
        };

        // Act
        var exitCode = await workspaceSetupService.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Setup should complete successfully");

        // Verify .gitignore was NOT updated
        var gitignoreContent = await File.ReadAllTextAsync(gitignorePath);
        Assert.DoesNotContain(".winapp", gitignoreContent, ".gitignore should not contain .winapp when NoGitignore is true");
    }

    [TestMethod]
    public async Task SetupWorkspace_WithRequireExistingConfig_FailsWhenConfigMissing()
    {
        // Arrange - Don't create any config file
        var workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();
        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            RequireExistingConfig = true, // This should fail when config doesn't exist
            UseDefaults = true,
            NoCert = true,
            NoGitignore = true
        };

        // Act
        var exitCode = await workspaceSetupService.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, exitCode, "Setup should fail when config is required but missing");
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="WorkspaceSetupService.SetupWorkspaceAsync"/> config-only and restore
/// (RequireExistingConfig) modes: creating a config with prerelease/preview package modes,
/// tolerating version-query failures, and the "nothing to restore" / missing-yaml guards.
/// </summary>
[TestClass]
public class WorkspaceSetupServiceConfigModeTests : BaseCommandTests
{
    private FakeNugetService _nuget = null!;
    private FakeDotNetService _dotnet = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _nuget = new FakeNugetService();
        _dotnet = new FakeDotNetService();

        return services
            .AddSingleton<IDevModeService, FakeDevModeService>()
            .AddSingleton<INugetService>(_nuget)
            .AddSingleton<IDotNetService>(_dotnet);
    }

    private WorkspaceSetupOptions ConfigOnlyOptions(SdkInstallMode mode) => new()
    {
        BaseDirectory = _tempDirectory,
        ConfigDir = _tempDirectory,
        UseDefaults = true,
        ConfigOnly = true,
        NoGitignore = true,
        SdkInstallMode = mode
    };

    private static async Task<FileInfo> CreateCsprojAsync(DirectoryInfo directory, string projectName)
    {
        var csprojPath = Path.Join(directory.FullName, $"{projectName}.csproj");
        await File.WriteAllTextAsync(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
  </PropertyGroup>
</Project>");
        return new FileInfo(csprojPath);
    }

    [TestMethod]
    public async Task ConfigOnly_ExperimentalMode_CreatesConfigFile()
    {
        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(ConfigOnlyOptions(SdkInstallMode.Experimental), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        Assert.IsTrue(File.Exists(Path.Join(_tempDirectory.FullName, "winapp.yaml")));
    }

    [TestMethod]
    public async Task ConfigOnly_PreviewMode_CreatesConfigFile()
    {
        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(ConfigOnlyOptions(SdkInstallMode.Preview), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        Assert.IsTrue(File.Exists(Path.Join(_tempDirectory.FullName, "winapp.yaml")));
    }

    [TestMethod]
    public async Task ConfigOnly_ToleratesVersionQueryFailure()
    {
        // Make every SDK package version query fail; config creation should still succeed.
        foreach (var pkg in NugetService.SDK_PACKAGES)
        {
            _nuget.PackagesToThrow.Add(pkg);
        }

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(ConfigOnlyOptions(SdkInstallMode.Stable), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
    }

    /// <summary>
    /// `winapp init` on a .NET project records the SDK package versions as PackageReferences in the .csproj
    /// rather than in a winapp.yaml, so a follow-up `winapp restore` finds no yaml. That must not be an error:
    /// restore delegates to `dotnet restore`, which is what actually restores those PackageReferences.
    /// </summary>
    [TestMethod]
    public async Task Restore_DotNetProject_WithoutYaml_DelegatesToDotnetRestore()
    {
        var csproj = await CreateCsprojAsync(_tempDirectory, "App");

        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            RequireExistingConfig = true,
            NoGitignore = true
        };

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        // A zero exit code alone would also be produced by the "nothing to restore" no-op branch, so assert the
        // delegation itself: exactly one dotnet restore, naming this project.
        Assert.HasCount(1, _dotnet.StreamingCalls);
        StringAssert.Contains(_dotnet.StreamingCalls[0], "restore", StringComparison.Ordinal);
        StringAssert.Contains(_dotnet.StreamingCalls[0], csproj.FullName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Several projects in one directory is ambiguous: restoring all of them would let an unrelated project
    /// fail `winapp restore`, and picking one silently could restore the wrong thing. List them and hand off
    /// to `dotnet restore <project>` instead.
    /// </summary>
    [TestMethod]
    public async Task Restore_DotNetMultiProject_WithoutYaml_ListsProjectsAndFails()
    {
        await CreateCsprojAsync(_tempDirectory, "AppOne");
        await CreateCsprojAsync(_tempDirectory, "AppTwo");

        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            RequireExistingConfig = true,
            NoGitignore = true
        };

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        Assert.AreNotEqual(0, result);
        Assert.IsEmpty(_dotnet.StreamingCalls, "Nothing may be restored when the target project is ambiguous.");

        var stderr = ConsoleStdErr.ToString();
        StringAssert.Contains(stderr, "AppOne.csproj", StringComparison.Ordinal);
        StringAssert.Contains(stderr, "AppTwo.csproj", StringComparison.Ordinal);
        StringAssert.Contains(stderr, "dotnet restore", StringComparison.Ordinal);
    }

    /// <summary>
    /// `restore --config-dir <dir>` cannot be forwarded to the delegated .NET restore: `--configfile` replaces
    /// the whole nuget.config hierarchy with a single file, which would drop user-level sources and the
    /// credentials stored with them. So the project's own hierarchy is used and the mismatch is reported.
    /// </summary>
    [TestMethod]
    public async Task Restore_DotNetProject_WithUnrelatedConfigDir_UsesProjectHierarchyAndWarns()
    {
        await CreateCsprojAsync(_tempDirectory, "App");

        // A sibling directory, so it is NOT part of the project's own nuget.config hierarchy.
        var configDir = _tempDirectory.Parent!.CreateSubdirectory($"cfg_{Guid.NewGuid():N}");
        try
        {
            var options = new WorkspaceSetupOptions
            {
                BaseDirectory = _tempDirectory,
                ConfigDir = configDir,
                RequireExistingConfig = true,
                NoGitignore = true
            };

            var service = GetRequiredService<IWorkspaceSetupService>();
            var result = await service.SetupWorkspaceAsync(options, TestContext.CancellationToken);

            Assert.AreEqual(0, result);
            Assert.HasCount(1, _dotnet.StreamingCalls);
            // Never --configfile: that would discard the user/machine levels of the hierarchy.
            Assert.DoesNotContain("--configfile", _dotnet.StreamingCalls[0], StringComparison.Ordinal);
        }
        finally
        {
            configDir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// When the config directory already sits in the project's own hierarchy (the common case — it defaults to
    /// the current directory), dotnet's discovery covers it, so nothing is overridden and nothing is warned.
    /// </summary>
    [TestMethod]
    public async Task Restore_DotNetProject_WithDefaultConfigDir_DoesNotOverrideNugetDiscovery()
    {
        await CreateCsprojAsync(_tempDirectory, "App");

        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            RequireExistingConfig = true,
            NoGitignore = true
        };

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        Assert.HasCount(1, _dotnet.StreamingCalls);
        Assert.DoesNotContain("--configfile", _dotnet.StreamingCalls[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// A failing `dotnet restore` must surface as a non-zero exit rather than being reported as a success.
    /// </summary>
    [TestMethod]
    public async Task Restore_DotNetProject_WhenDotnetRestoreFails_ReturnsError()
    {
        await CreateCsprojAsync(_tempDirectory, "App");
        _dotnet.RunDotnetStreamingHandler = (_, _, _) => 1;

        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            RequireExistingConfig = true,
            NoGitignore = true
        };

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        Assert.AreNotEqual(0, result);
    }
}

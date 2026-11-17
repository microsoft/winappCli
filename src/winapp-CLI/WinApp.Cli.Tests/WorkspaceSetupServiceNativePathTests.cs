// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using System.IO.Compression;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Integration tests that drive <see cref="WorkspaceSetupService.SetupWorkspaceAsync"/> down the
/// native / C++ path (no .csproj present) with fakes for every heavy dependency. These exercise
/// the largest previously-uncovered block: SDK package install, header/lib/runtime layout,
/// C++/WinRT projection, winmds lockfile write, BuildTools setup, license copy, the winapp.yaml
/// save sub-task, and the .gitignore update sub-task — plus the main error branches.
/// </summary>
[TestClass]
public class WorkspaceSetupServiceNativePathTests : BaseCommandTests
{
    private FakeNugetService _nuget = null!;
    private FakePackageInstallationService _install = null!;
    private FakeCppWinrtService _cppwinrt = null!;
    private FakePackageLayoutService _layout = null!;
    private FakeWinmdsLockfileService _lockfile = null!;
    private FakePackageRegistrationService _reg = null!;
    private FakeBuildToolsService _buildTools = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _nuget = new FakeNugetService();
        _install = new FakePackageInstallationService();
        _cppwinrt = new FakeCppWinrtService();
        _layout = new FakePackageLayoutService();
        _lockfile = new FakeWinmdsLockfileService();
        _reg = new FakePackageRegistrationService();
        _buildTools = new FakeBuildToolsService();

        return services
            .AddSingleton<IDevModeService, FakeDevModeService>()
            .AddSingleton<INugetService>(_nuget)
            .AddSingleton<IPackageInstallationService>(_install)
            .AddSingleton<ICppWinrtService>(_cppwinrt)
            .AddSingleton<IPackageLayoutService>(_layout)
            .AddSingleton<IBuildToolsService>(_buildTools)
            .AddSingleton<IWinmdsLockfileService>(_lockfile)
            .AddSingleton<IPackageRegistrationService>(_reg);
    }

    #region Helper methods

    private void CreateExistingManifest()
    {
        File.WriteAllText(Path.Combine(_tempDirectory.FullName, "Package.appxmanifest"), @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"">
  <Identity Name=""Test"" Version=""1.0.0.0"" Publisher=""CN=Test"" />
</Package>");
    }

    private WorkspaceSetupOptions BaseOptions(bool noGitignore = true) => new()
    {
        BaseDirectory = _tempDirectory,
        ConfigDir = _tempDirectory,
        UseDefaults = true,
        RequireExistingConfig = false,
        NoGitignore = noGitignore,
        SdkInstallMode = SdkInstallMode.Stable
    };

    /// <summary>Default set of installed versions returned by the fake package installer.</summary>
    private void SeedInstalledVersions()
    {
        _install.InstallResult = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BuildToolsService.WINAPP_SDK_PACKAGE] = "1.6.0",
            [BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE] = "6000.0.0.0",
            [BuildToolsService.CPP_SDK_PACKAGE] = "1.0.0",
            ["Microsoft.Windows.CppWinRT"] = "2.0.0"
        };
    }

    /// <summary>Provides at least one winmd so the projection step proceeds.</summary>
    private void SeedWinmds()
    {
        _layout.Winmds = [new FileInfo(Path.Combine(_tempDirectory.FullName, "Windows.winmd"))];
    }

    /// <summary>Populates the runtime package MSIX layout in the fake NuGet cache.</summary>
    private void SeedRuntimeMsix()
    {
        var arch = WorkspaceSetupService.GetSystemArchitecture();
        var cache = _nuget.GetNuGetGlobalPackagesDir();
        var archDir = new DirectoryInfo(Path.Combine(
            cache.FullName, BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE.ToLowerInvariant(), "6000.0.0.0",
            "tools", "MSIX", $"win10-{arch}"));
        archDir.Create();
        File.WriteAllLines(Path.Combine(archDir.FullName, "msix.inventory"),
            new[] { $"runtime.msix=Microsoft.WindowsAppRuntime.1.6_6000.0.0.0_{arch}" });

        using var zip = ZipFile.Open(Path.Combine(archDir.FullName, "runtime.msix"), ZipArchiveMode.Create);
        var entry = zip.CreateEntry("AppxManifest.xml");
        using var writer = new StreamWriter(entry.Open());
        writer.Write(@"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"">
  <Identity Name=""Microsoft.WindowsAppRuntime.1.6"" Version=""6000.0.0.0"" Publisher=""CN=Test"" />
</Package>");
    }

    #endregion

    [TestMethod]
    public async Task SetupNative_HappyPath_InstallsSdkAndSavesConfig()
    {
        CreateExistingManifest();
        SeedInstalledVersions();
        SeedWinmds();
        SeedRuntimeMsix();
        _reg.FakeInstalledVersion = null;

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(BaseOptions(), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        Assert.AreEqual(1, _cppwinrt.RunWithRspCallCount);
        Assert.AreEqual(1, _lockfile.WriteCallCount);
        Assert.IsTrue(_reg.InstallPackageCalls.Count > 0, "Expected runtime MSIX install.");
        // winapp.yaml should have been saved (native path persists versions to config).
        Assert.IsTrue(File.Exists(Path.Combine(_tempDirectory.FullName, "winapp.yaml")), "Expected winapp.yaml to be written.");
    }

    [TestMethod]
    public async Task SetupNative_ExperimentalMode_LogsPrereleaseInclusion()
    {
        // Native init with the Experimental SDK mode selected: exercises the experimental/prerelease
        // startup logging branch (SdkInstallMode == Experimental) on the workspace-init path.
        CreateExistingManifest();
        SeedInstalledVersions();
        SeedWinmds();
        SeedRuntimeMsix();
        _reg.FakeInstalledVersion = "9999.0.0.0";

        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            UseDefaults = true,
            RequireExistingConfig = false,
            NoGitignore = true,
            SdkInstallMode = SdkInstallMode.Experimental
        };

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        Assert.AreEqual(0, result);
    }

    [TestMethod]
    public async Task SetupNative_CopiesLicense_WhenPresentInPackage()
    {
        CreateExistingManifest();
        SeedInstalledVersions();
        SeedWinmds();
        SeedRuntimeMsix();
        _reg.FakeInstalledVersion = "9999.0.0.0"; // skip actual install for speed

        // Create a license.txt in the WinAppSDK package dir so the license-copy branch runs.
        var pkgDir = _nuget.GetNuGetPackageDir(BuildToolsService.WINAPP_SDK_PACKAGE, "1.6.0");
        pkgDir.Create();
        await File.WriteAllTextAsync(Path.Combine(pkgDir.FullName, "license.txt"), "LICENSE", TestContext.CancellationToken);

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(BaseOptions(), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        var copyright = Path.Combine(_tempDirectory.FullName, ".winapp", "share", BuildToolsService.WINAPP_SDK_PACKAGE, "copyright");
        Assert.IsTrue(File.Exists(copyright), "Expected the SDK license to be copied to the share/copyright path.");
    }

    [TestMethod]
    public async Task SetupNative_UpdatesGitignore_WhenGitignoreExists()
    {
        CreateExistingManifest();
        SeedInstalledVersions();
        SeedWinmds();
        SeedRuntimeMsix();
        _reg.FakeInstalledVersion = "9999.0.0.0";

        var gitignorePath = Path.Combine(_tempDirectory.FullName, ".gitignore");
        await File.WriteAllTextAsync(gitignorePath, "bin/\nobj/\n", TestContext.CancellationToken);

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(BaseOptions(noGitignore: false), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        var gitignoreText = await File.ReadAllTextAsync(gitignorePath, TestContext.CancellationToken);
        Assert.Contains(".winapp", gitignoreText);
    }

    [TestMethod]
    public async Task SetupNative_GitignoreAlreadyUpToDate_LeavesItUnchanged()
    {
        CreateExistingManifest();
        SeedInstalledVersions();
        SeedWinmds();
        SeedRuntimeMsix();
        _reg.FakeInstalledVersion = "9999.0.0.0";

        // .gitignore already contains the .winapp entry -> the sub-task reports "up to date".
        var gitignorePath = Path.Combine(_tempDirectory.FullName, ".gitignore");
        await File.WriteAllTextAsync(gitignorePath, "bin/\n.winapp\n", TestContext.CancellationToken);

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(BaseOptions(noGitignore: false), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        var gitignoreText = await File.ReadAllTextAsync(gitignorePath, TestContext.CancellationToken);
        // A single .winapp entry should remain (no duplicate section appended).
        Assert.AreEqual(1, gitignoreText.Split('\n').Count(l => l.Trim() == ".winapp"));
    }

    [TestMethod]
    public async Task SetupNative_ReportsBuildToolsPath_WhenResolved()
    {
        CreateExistingManifest();
        SeedInstalledVersions();
        SeedWinmds();
        SeedRuntimeMsix();
        _reg.FakeInstalledVersion = "9999.0.0.0";
        _buildTools.BuildToolsResult = new DirectoryInfo(_tempDirectory.FullName); // non-null -> "BuildTools ready" branch

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(BaseOptions(), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
    }

    [TestMethod]
    public async Task SetupNative_Fails_WhenPackageInstallReturnsNull()
    {
        CreateExistingManifest();
        _install.ReturnNull = true;
        SeedWinmds();

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(BaseOptions(), TestContext.CancellationToken);

        Assert.AreNotEqual(0, result);
    }

    [TestMethod]
    public async Task SetupNative_Fails_WhenCppWinrtExeNotFound()
    {
        CreateExistingManifest();
        SeedInstalledVersions();
        SeedWinmds();
        _cppwinrt.ReturnNullExe = true;

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(BaseOptions(), TestContext.CancellationToken);

        Assert.AreNotEqual(0, result);
    }

    [TestMethod]
    public async Task SetupNative_Fails_WhenNoWinmdsFound()
    {
        CreateExistingManifest();
        SeedInstalledVersions();
        _layout.Winmds = []; // no winmd metadata

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(BaseOptions(), TestContext.CancellationToken);

        Assert.AreNotEqual(0, result);
    }

    #region Restore (RequireExistingConfig) native path

    private async Task WriteConfigAsync(params string[] nameVersionPairs)
    {
        var sb = new System.Text.StringBuilder("packages:\n");
        for (var i = 0; i < nameVersionPairs.Length; i += 2)
        {
            sb.Append($"  - name: {nameVersionPairs[i]}\n    version: {nameVersionPairs[i + 1]}\n");
        }
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory.FullName, "winapp.yaml"), sb.ToString());
    }

    private WorkspaceSetupOptions RestoreOptions() => new()
    {
        BaseDirectory = _tempDirectory,
        ConfigDir = _tempDirectory,
        RequireExistingConfig = true,
        NoGitignore = true
    };

    [TestMethod]
    public async Task RestoreNative_FromConfig_InstallsPinnedPackagesAndUpdatesProps()
    {
        // Restore uses the package set declared in winapp.yaml (no build-tools pin -> force latest).
        await WriteConfigAsync(
            BuildToolsService.WINAPP_SDK_PACKAGE, "1.6.0",
            BuildToolsService.CPP_SDK_PACKAGE, "1.0.0");
        SeedInstalledVersions();
        SeedWinmds();
        SeedRuntimeMsix();
        _reg.FakeInstalledVersion = "9999.0.0.0";

        // A Directory.Packages.props present exercises the props-update sub-task (config is non-null on restore).
        await File.WriteAllTextAsync(
            Path.Combine(_tempDirectory.FullName, "Directory.Packages.props"),
            "<Project><ItemGroup><PackageVersion Include=\"Microsoft.WindowsAppSDK\" Version=\"1.5.0\" /></ItemGroup></Project>");

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(RestoreOptions(), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        Assert.AreSequenceEqual(
            new[] { BuildToolsService.WINAPP_SDK_PACKAGE, BuildToolsService.CPP_SDK_PACKAGE },
            _install.LastRequestedPackages,
            SequenceOrder.InAnyOrder,
            "Restore should install exactly the packages declared in winapp.yaml.");
        // No BUILD_TOOLS pin in winapp.yaml -> the build-tools setup must force the latest version.
        Assert.IsTrue(
            _buildTools.EnsureBuildToolsForceLatest.Count > 0 && _buildTools.EnsureBuildToolsForceLatest[^1],
            "With no pinned BUILD_TOOLS version, restore must force the latest build tools (forceLatest=true).");
    }

    [TestMethod]
    public async Task RestoreNative_WithPinnedBuildTools_InstallsPinnedVersion()
    {
        await WriteConfigAsync(
            BuildToolsService.WINAPP_SDK_PACKAGE, "1.6.0",
            BuildToolsService.BUILD_TOOLS_PACKAGE, "10.0.26100.1");
        SeedInstalledVersions();
        SeedWinmds();
        SeedRuntimeMsix();
        _reg.FakeInstalledVersion = "9999.0.0.0";

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(RestoreOptions(), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        // The winapp.yaml pins BUILD_TOOLS to 10.0.26100.1, so the build-tools setup must install that
        // exact version rather than forcing the latest.
        Assert.IsTrue(_buildTools.EnsureBuildToolsForceLatest.Count > 0, "BuildTools setup sub-task should have run.");
        Assert.IsFalse(
            _buildTools.EnsureBuildToolsForceLatest[^1],
            "A pinned BUILD_TOOLS version must be installed as-is (forceLatest=false), not force the latest.");
    }

    [TestMethod]
    public async Task RestoreNative_NoConfig_CompletesCleanly()
    {
        // Restore against a native project with no winapp.yaml: InitializeConfiguration reports
        // "nothing to restore" and setup completes without error.
        SeedInstalledVersions();
        SeedWinmds();
        SeedRuntimeMsix();
        _reg.FakeInstalledVersion = "9999.0.0.0";

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(RestoreOptions(), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
    }

    [TestMethod]
    public async Task RestoreNative_ConfigWithNoPackages_CompletesCleanly()
    {
        // winapp.yaml exists but declares no packages -> "nothing to restore", completes cleanly.
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory.FullName, "winapp.yaml"), "packages:\n");
        SeedInstalledVersions();
        SeedWinmds();
        SeedRuntimeMsix();
        _reg.FakeInstalledVersion = "9999.0.0.0";

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(RestoreOptions(), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
    }

    #endregion
}

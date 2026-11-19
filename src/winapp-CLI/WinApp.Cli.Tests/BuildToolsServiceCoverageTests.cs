// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Services;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Tests;

/// <summary>
/// Coverage-focused tests for <see cref="BuildToolsService"/> exercising the defensive
/// filesystem-layout branches of the package/architecture resolver and the .csproj
/// version-lookup error path. Uses <see cref="FakeNugetService"/> so the NuGet cache is the
/// isolated test cache directory, and <see cref="FakeDotNetService"/> so no real
/// <c>dotnet</c> process is launched.
/// </summary>
[TestClass]
[DoNotParallelize]
public class BuildToolsServiceResolverCoverageTests : BaseCommandTests
{
    private FakeDotNetService _fakeDotNetService = null!;

    private static readonly string[] KnownArchitectures = ["x64", "x86", "arm64"];

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeDotNetService = new FakeDotNetService();
        return services
            .AddSingleton<IDotNetService>(_fakeDotNetService)
            .AddSingleton<INugetService, FakeNugetService>();
    }

    private string BuildToolsPackageRoot()
    {
        return Path.Combine(_testCacheDirectory.FullName, "packages", "microsoft.windows.sdk.buildtools");
    }

    [TestMethod]
    public void GetBuildToolPath_WithPackageDirButNoVersions_ReturnsNull()
    {
        // Package directory exists but contains no version subdirectories.
        Directory.CreateDirectory(BuildToolsPackageRoot());

        var result = _buildToolsService.GetBuildToolPath("mt.exe");

        Assert.IsNull(result);
    }

    [TestMethod]
    public void GetBuildToolPath_WithVersionButNoBinSubdir_ReturnsNull()
    {
        // Version directory exists but has no "bin" subdirectory.
        Directory.CreateDirectory(Path.Combine(BuildToolsPackageRoot(), "10.0.26100.1"));

        var result = _buildToolsService.GetBuildToolPath("mt.exe");

        Assert.IsNull(result);
    }

    [TestMethod]
    public void GetBuildToolPath_WithBinButNoVersionFolder_ReturnsNull()
    {
        // "bin" exists but contains no folder matching the N.N.N.N version pattern.
        Directory.CreateDirectory(Path.Combine(BuildToolsPackageRoot(), "10.0.26100.1", "bin", "not-a-version"));

        var result = _buildToolsService.GetBuildToolPath("mt.exe");

        Assert.IsNull(result);
    }

    [TestMethod]
    public void GetBuildToolPath_WithVersionFolderButNoArchitecture_ReturnsNull()
    {
        // A valid version folder exists under bin, but with no architecture subdirectory at all.
        Directory.CreateDirectory(Path.Combine(BuildToolsPackageRoot(), "10.0.26100.1", "bin", "10.0.26100.0"));

        var result = _buildToolsService.GetBuildToolPath("mt.exe");

        Assert.IsNull(result);
    }

    [TestMethod]
    public void GetBuildToolPath_WhenSystemArchMissing_FallsBackToAnotherArchitecture()
    {
        // Only a NON-system architecture is present; the resolver should fall back to it.
        var systemArch = WorkspaceSetupService.GetSystemArchitecture();
        var fallbackArch = KnownArchitectures.First(a => a != systemArch);

        var archBinDir = Path.Combine(BuildToolsPackageRoot(), "10.0.26100.1", "bin", "10.0.26100.0", fallbackArch);
        Directory.CreateDirectory(archBinDir);
        var toolPath = Path.Combine(archBinDir, "mt.exe");
        File.WriteAllText(toolPath, "fallback mt.exe");

        var result = _buildToolsService.GetBuildToolPath("mt.exe");

        Assert.IsNotNull(result);
        Assert.AreEqual(toolPath, result.FullName);
        Assert.Contains(fallbackArch, result.FullName);
    }

    [TestMethod]
    public void GetBuildToolPath_WithPinnedVersionNotInstalled_ReturnsNull()
    {
        // Latest version is installed, but winapp.yaml pins a version that is not on disk.
        var installedArchDir = Path.Combine(BuildToolsPackageRoot(), "10.0.26100.1", "bin", "10.0.26100.0", "x64");
        Directory.CreateDirectory(installedArchDir);
        File.WriteAllText(Path.Combine(installedArchDir, "mt.exe"), "installed mt.exe");

        File.WriteAllText(_configService.ConfigPath.FullName, @"packages:
  - name: Microsoft.Windows.SDK.BuildTools
    version: 9.9.9.9
");

        var result = _buildToolsService.GetBuildToolPath("mt.exe");

        // A pinned-but-missing version is a strict failure, not a silent fall back to latest.
        Assert.IsNull(result);
    }

    [TestMethod]
    public void GetBuildToolPath_WhenCsprojPackageListThrows_SwallowsAndFallsBackToLatest()
    {
        // Arrange - a valid latest install on disk.
        var archDir = Path.Combine(BuildToolsPackageRoot(), "10.0.26100.1", "bin", "10.0.26100.0", "x64");
        Directory.CreateDirectory(archDir);
        var toolPath = Path.Combine(archDir, "mt.exe");
        File.WriteAllText(toolPath, "latest mt.exe");

        // A .csproj in the current directory forces the csproj version-lookup path...
        File.WriteAllText(
            Path.Combine(_tempDirectory.FullName, "TestApp.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        // ...and GetPackageListAsync throws, which must be swallowed by the resolver.
        _fakeDotNetService.ThrowOnGetPackageList = true;

        // Act
        var result = _buildToolsService.GetBuildToolPath("mt.exe");

        // Assert - the throw was caught and resolution continued to the latest version.
        Assert.IsTrue(_fakeDotNetService.GetPackageListCallCount >= 1,
            "The csproj version-lookup path should have been exercised.");
        Assert.IsNotNull(result);
        Assert.AreEqual(toolPath, result.FullName);
    }

    [TestMethod]
    public void GetBuildToolPath_ReadsCsprojPinWithoutRestoring()
    {
        // On SDK 10+ 'dotnet list package' restores unless told not to, which can block for minutes
        // offline; resolving a build tool must only read the existing restore.
        Directory.CreateDirectory(Path.Combine(BuildToolsPackageRoot(), "10.0.26100.1", "bin", "10.0.26100.0", "x64"));
        File.WriteAllText(Path.Combine(_tempDirectory.FullName, "TestApp.csproj"), "<Project />");

        _buildToolsService.GetBuildToolPath("makepri.exe");

        Assert.IsTrue(_fakeDotNetService.LastGetPackageListNoRestore);
    }
}

/// <summary>
/// Coverage-focused tests for <see cref="BuildToolsService"/> installation flows using a
/// deterministic <see cref="ConfigurablePackageInstallationService"/> so the install
/// success / failure / installed-but-missing-bin branches are exercised without any network.
/// </summary>
[TestClass]
[DoNotParallelize]
public class BuildToolsServiceInstallCoverageTests : BaseCommandTests
{
    private ConfigurablePackageInstallationService _fakeInstall = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeInstall = new ConfigurablePackageInstallationService();
        return services
            .AddSingleton<IDotNetService, FakeDotNetService>()
            .AddSingleton<INugetService, FakeNugetService>()
            .AddSingleton<IPackageInstallationService>(_fakeInstall);
    }

    private string BuildToolsPackageRoot()
    {
        return Path.Combine(_testCacheDirectory.FullName, "packages", "microsoft.windows.sdk.buildtools");
    }

    private DirectoryInfo CreateInstalledBin(string toolName)
    {
        var archDir = Path.Combine(BuildToolsPackageRoot(), "10.0.26100.1", "bin", "10.0.26100.0", "x64");
        Directory.CreateDirectory(archDir);
        File.WriteAllText(Path.Combine(archDir, toolName), "fake tool");
        return new DirectoryInfo(archDir);
    }

    [TestMethod]
    public async Task EnsureBuildToolsAsync_WhenInstallFails_ReturnsNull()
    {
        _fakeInstall.EnsurePackageResult = false;

        var result = await _buildToolsService.EnsureBuildToolsAsync(TestTaskContext, cancellationToken: TestContext.CancellationToken);

        Assert.IsNull(result);
        Assert.Contains(BuildToolsService.BUILD_TOOLS_PACKAGE, _fakeInstall.EnsuredPackages);
    }

    [TestMethod]
    public async Task EnsureBuildToolsAsync_WhenInstallSucceedsButBinMissing_ReturnsNull()
    {
        // Install "succeeds" but produces no bin layout, so the post-install lookup fails.
        _fakeInstall.EnsurePackageResult = true;

        var result = await _buildToolsService.EnsureBuildToolsAsync(TestTaskContext, cancellationToken: TestContext.CancellationToken);

        Assert.IsNull(result);
        Assert.Contains(BuildToolsService.BUILD_TOOLS_PACKAGE, _fakeInstall.EnsuredPackages);
    }

    [TestMethod]
    public async Task EnsureBuildToolsAsync_WhenInstallSucceedsAndBinCreated_ReturnsBinPath()
    {
        _fakeInstall.EnsurePackageResult = true;
        _fakeInstall.OnEnsurePackage = (_, _) => CreateInstalledBin("mt.exe");

        var result = await _buildToolsService.EnsureBuildToolsAsync(TestTaskContext, cancellationToken: TestContext.CancellationToken);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Exists);
        Assert.Contains("x64", result.FullName);
    }

    [TestMethod]
    public async Task EnsureBuildToolAvailableAsync_WhenInstallFails_ThrowsInvalidOperation()
    {
        _fakeInstall.EnsurePackageResult = false;

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await _buildToolsService.EnsureBuildToolAvailableAsync("mt.exe", TestTaskContext, TestContext.CancellationToken));

        Assert.Contains("Could not install or find", ex.Message);
    }

    [TestMethod]
    public async Task EnsureBuildToolAvailableAsync_WhenInstalledAfterMissing_ReturnsToolViaExeRetry()
    {
        // Tool requested without extension; nothing on disk until the install callback runs.
        _fakeInstall.EnsurePackageResult = true;
        _fakeInstall.OnEnsurePackage = (_, _) => CreateInstalledBin("mt.exe");

        var result = await _buildToolsService.EnsureBuildToolAvailableAsync("mt", TestTaskContext, TestContext.CancellationToken);

        Assert.IsNotNull(result);
        Assert.AreEqual("mt.exe", result.Name);
    }

    [TestMethod]
    public async Task EnsureBuildToolsAsync_WithPinnedVersionInConfig_InstallsPinnedVersion()
    {
        // A winapp.yaml pin + no existing install exercises the pinned-version config load path.
        const string pinnedVersion = "10.0.26100.1";
        File.WriteAllText(_configService.ConfigPath.FullName, $@"packages:
  - name: Microsoft.Windows.SDK.BuildTools
    version: {pinnedVersion}
");

        _fakeInstall.EnsurePackageResult = true;
        _fakeInstall.OnEnsurePackage = (_, _) =>
        {
            var archDir = Path.Combine(BuildToolsPackageRoot(), pinnedVersion, "bin", "10.0.26100.0", "x64");
            Directory.CreateDirectory(archDir);
            File.WriteAllText(Path.Combine(archDir, "mt.exe"), "fake tool");
        };

        var result = await _buildToolsService.EnsureBuildToolsAsync(TestTaskContext, cancellationToken: TestContext.CancellationToken);

        Assert.IsNotNull(result);
        Assert.Contains(pinnedVersion, result.FullName);
    }

    [TestMethod]
    public async Task RunBuildToolAsync_WhenToolExitsNonZero_ThrowsWithProcessDetails()
    {
        // A failing tool populates the InvalidBuildToolException's captured process details.
        var binDir = CreateInstalledBin("placeholder.txt");
        var failingTool = Path.Combine(binDir.FullName, "failing.cmd");
        File.WriteAllText(failingTool, "@echo captured-stdout-marker\r\n@exit /b 1");

        var ex = await Assert.ThrowsExactlyAsync<BuildToolsService.InvalidBuildToolException>(async () =>
            await _buildToolsService.RunBuildToolAsync(new GenericTool("failing.cmd"), "", TestTaskContext, true, cancellationToken: TestContext.CancellationToken));

        Assert.IsTrue(ex.ProcessId > 0, "Expected a captured process id.");
        Assert.Contains("captured-stdout-marker", ex.Stdout);
    }
}

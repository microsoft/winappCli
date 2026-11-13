// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using System.IO.Compression;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Integration tests that drive <see cref="WorkspaceSetupService.SetupWorkspaceAsync"/> down the
/// .NET (.csproj) path with fakes for NuGet, dotnet, dev mode, and package registration. These
/// exercise deterministic branches that the happy-path tests don't reach: existing-package
/// preservation, required/optional package-add failures, WindowsPackageType removal, the shared
/// Windows App SDK Runtime install sub-task (found / already-installed / error), the
/// Directory.Packages.props sub-task, and TargetFramework auto-update.
/// </summary>
[TestClass]
public class WorkspaceSetupServiceDotNetIntegrationTests : BaseCommandTests
{
    private FakeNugetService _nuget = null!;
    private FakeDotNetService _dotnet = null!;
    private FakePackageRegistrationService _reg = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _nuget = new FakeNugetService();
        _dotnet = new FakeDotNetService();
        _reg = new FakePackageRegistrationService();

        return services
            .AddSingleton<IDevModeService, FakeDevModeService>()
            .AddSingleton<INugetService>(_nuget)
            .AddSingleton<IDotNetService>(_dotnet)
            .AddSingleton<IPackageRegistrationService>(_reg);
    }

    #region Helper methods

    private static async Task<FileInfo> CreateCsprojAsync(DirectoryInfo directory, string projectName, string targetFramework, string? extraProperties = null)
    {
        var csprojPath = Path.Combine(directory.FullName, $"{projectName}.csproj");
        var content = $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>{targetFramework}</TargetFramework>
    {extraProperties}
  </PropertyGroup>
</Project>";
        await File.WriteAllTextAsync(csprojPath, content);
        return new FileInfo(csprojPath);
    }

    /// <summary>Creates an existing Package.appxmanifest so manifest generation is skipped (keeps tests fast).</summary>
    private void CreateExistingManifest()
    {
        var manifestPath = Path.Combine(_tempDirectory.FullName, "Package.appxmanifest");
        File.WriteAllText(manifestPath, @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"">
  <Identity Name=""Test"" Version=""1.0.0.0"" Publisher=""CN=Test"" />
</Package>");
    }

    private WorkspaceSetupOptions BaseOptions() => new()
    {
        BaseDirectory = _tempDirectory,
        ConfigDir = _tempDirectory,
        UseDefaults = true,
        RequireExistingConfig = false,
        NoGitignore = true,
        SdkInstallMode = SdkInstallMode.Stable
    };

    /// <summary>
    /// Populates the fake NuGet cache with a Windows App SDK MSIX layout so that
    /// FindWindowsAppSdkMsixDirectory (via usedVersions) resolves to it.
    /// </summary>
    private void PopulateSdkMsixCache(string version, string identityName, string identityVersion)
    {
        var arch = WorkspaceSetupService.GetSystemArchitecture();
        var cache = _nuget.GetNuGetGlobalPackagesDir();
        var msixArchDir = new DirectoryInfo(Path.Combine(
            cache.FullName, BuildToolsService.WINAPP_SDK_PACKAGE.ToLowerInvariant(), version,
            "tools", "MSIX", $"win10-{arch}"));
        msixArchDir.Create();

        File.WriteAllLines(
            Path.Combine(msixArchDir.FullName, "msix.inventory"),
            new[] { $"runtime.msix={identityName}_{identityVersion}_{arch}" });

        var msixPath = Path.Combine(msixArchDir.FullName, "runtime.msix");
        using var zip = ZipFile.Open(msixPath, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("AppxManifest.xml");
        using var writer = new StreamWriter(entry.Open());
        writer.Write($@"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"">
  <Identity Name=""{identityName}"" Version=""{identityVersion}"" Publisher=""CN=Test"" />
</Package>");
    }

    private static DotNetPackageListJson PackageListWith(params (string Id, string Version)[] packages)
    {
        var pkgs = packages
            .Select(p => new DotNetPackage(p.Id, p.Version, p.Version))
            .ToList();
        return new DotNetPackageListJson(
        [
            new DotNetProject(
            [
                new DotNetFramework("net10.0-windows10.0.26100.0", pkgs, [])
            ])
        ]);
    }

    #endregion

    #region Windows App SDK Runtime install sub-task

    [TestMethod]
    public async Task SetupDotNet_InstallsRuntime_WhenMsixFoundInCache()
    {
        await CreateCsprojAsync(_tempDirectory, "App", "net10.0-windows10.0.26100.0");
        CreateExistingManifest();
        PopulateSdkMsixCache("1.6.0", "Microsoft.WindowsAppRuntime.1.6", "6000.0.0.0");
        _reg.FakeInstalledVersion = null; // not installed yet

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(BaseOptions(), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        Assert.IsTrue(_reg.InstallPackageCalls.Count > 0, "Expected the runtime MSIX to be installed.");
    }

    [TestMethod]
    public async Task SetupDotNet_SkipsRuntime_WhenNewerVersionAlreadyInstalled()
    {
        await CreateCsprojAsync(_tempDirectory, "App", "net10.0-windows10.0.26100.0");
        CreateExistingManifest();
        PopulateSdkMsixCache("1.6.0", "Microsoft.WindowsAppRuntime.1.6", "6000.0.0.0");
        _reg.FakeInstalledVersion = "9999.0.0.0"; // newer already installed

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(BaseOptions(), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        Assert.AreEqual(0, _reg.InstallPackageCalls.Count);
    }

    [TestMethod]
    public async Task SetupDotNet_RuntimeInstallError_DoesNotFailOverallSetup()
    {
        await CreateCsprojAsync(_tempDirectory, "App", "net10.0-windows10.0.26100.0");
        CreateExistingManifest();
        PopulateSdkMsixCache("1.6.0", "Microsoft.WindowsAppRuntime.1.6", "6000.0.0.0");
        _reg.FakeInstalledVersion = null;
        _reg.InstallPackageThrows = new InvalidOperationException("boom");

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(BaseOptions(), TestContext.CancellationToken);

        // Runtime install failures are surfaced as a sub-task result but do not abort setup.
        Assert.AreEqual(0, result);
        // The error path is only genuinely exercised if the install was actually attempted: the fake
        // records the call before throwing, so a non-empty list proves we reached (and failed) the install.
        Assert.IsTrue(
            _reg.InstallPackageCalls.Count > 0,
            "The runtime MSIX install must have been attempted (and then thrown) to exercise this error path.");
    }

    #endregion

    #region NuGet package add branches

    [TestMethod]
    public async Task SetupDotNet_PreservesExistingWinAppSdkVersion()
    {
        await CreateCsprojAsync(_tempDirectory, "App", "net10.0-windows10.0.26100.0");
        CreateExistingManifest();
        // Project already references WinAppSDK at a specific version -> should be preserved, not re-added.
        _dotnet.PackageListResult = PackageListWith((DotNetService.WINAPP_SDK_NUGET_PACKAGE, "2.0.0"));

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(BaseOptions(), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        Assert.IsFalse(
            _dotnet.AddedPackages.Any(p => string.Equals(p.PackageName, DotNetService.WINAPP_SDK_NUGET_PACKAGE, StringComparison.OrdinalIgnoreCase)),
            "Existing WinAppSDK version should have been preserved rather than re-added.");
    }

    [TestMethod]
    public async Task SetupDotNet_Fails_WhenRequiredPackageAddThrows()
    {
        await CreateCsprojAsync(_tempDirectory, "App", "net10.0-windows10.0.26100.0");
        CreateExistingManifest();
        _dotnet.PackagesToThrowOnAdd.Add(DotNetService.WINAPP_SDK_NUGET_PACKAGE); // required package

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(BaseOptions(), TestContext.CancellationToken);

        Assert.AreNotEqual(0, result);
    }

    [TestMethod]
    public async Task SetupDotNet_Continues_WhenOptionalPackageAddThrows()
    {
        await CreateCsprojAsync(_tempDirectory, "App", "net10.0-windows10.0.26100.0");
        CreateExistingManifest();
        // Mark the (optional) WinApp build-tools package as already referenced so it is opted-in,
        // then make its add throw. The required WinAppSDK add still succeeds.
        _dotnet.PackageListResult = PackageListWith((DotNetService.WINDOWS_SDK_BUILD_TOOLS_WINAPP_PACKAGE, "1.0.0"));
        _dotnet.PackagesToThrowOnAdd.Add(DotNetService.WINDOWS_SDK_BUILD_TOOLS_WINAPP_PACKAGE);

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(BaseOptions(), TestContext.CancellationToken);

        // Optional package failure is non-fatal.
        Assert.AreEqual(0, result);
    }

    [TestMethod]
    public async Task SetupDotNet_Fails_WhenRequiredPackageVersionQueryThrows()
    {
        await CreateCsprojAsync(_tempDirectory, "App", "net10.0-windows10.0.26100.0");
        CreateExistingManifest();
        // The version lookup (not the add) fails for the required Windows App SDK package.
        _nuget.PackagesToThrow.Add(DotNetService.WINAPP_SDK_NUGET_PACKAGE);

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(BaseOptions(), TestContext.CancellationToken);

        Assert.AreNotEqual(0, result, "A required package version-query failure should abort setup.");
    }

    [TestMethod]
    public async Task SetupDotNet_Continues_WhenGetPackageListThrows()
    {
        await CreateCsprojAsync(_tempDirectory, "App", "net10.0-windows10.0.26100.0");
        CreateExistingManifest();
        // Querying existing packages fails (e.g. implicit restore error); this is non-fatal —
        // setup proceeds without preserved versions.
        _dotnet.ThrowOnGetPackageList = true;

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(BaseOptions(), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        Assert.IsTrue(
            _dotnet.AddedPackages.Any(p => p.PackageName == DotNetService.WINAPP_SDK_NUGET_PACKAGE),
            "Setup should still add the Windows App SDK even when the existing-package query fails.");
    }

    #endregion

    #region csproj mutation branches

    [TestMethod]
    public async Task SetupDotNet_RemovesWindowsPackageTypeNone_WhenWinAppSdkReferenced()
    {
        var csproj = await CreateCsprojAsync(
            _tempDirectory, "App", "net10.0-windows10.0.26100.0",
            extraProperties: "<WindowsPackageType>None</WindowsPackageType>");
        CreateExistingManifest();
        _dotnet.PackageListResult = PackageListWith((DotNetService.WINAPP_SDK_NUGET_PACKAGE, "1.6.0"));

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(BaseOptions(), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        var csprojText = await File.ReadAllTextAsync(csproj.FullName, TestContext.CancellationToken);
        Assert.DoesNotMatchRegex(new System.Text.RegularExpressions.Regex("WindowsPackageType"), csprojText);
    }

    [TestMethod]
    public async Task SetupDotNet_UpdatesTargetFramework_WhenUnsupported()
    {
        var csproj = await CreateCsprojAsync(_tempDirectory, "App", "net8.0"); // unsupported for WinAppSDK
        CreateExistingManifest();

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(BaseOptions(), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        var csprojText = await File.ReadAllTextAsync(csproj.FullName, TestContext.CancellationToken);
        Assert.Contains("-windows", csprojText);
    }

    #endregion

    #region Directory.Packages.props sub-task

    [TestMethod]
    public async Task SetupDotNet_RunsDirectoryPackagesProps_SubTask_WhenFileExists()
    {
        await CreateCsprojAsync(_tempDirectory, "App", "net10.0-windows10.0.26100.0");
        CreateExistingManifest();
        // A central package management file in the config dir triggers the update sub-task.
        var propsPath = Path.Combine(_tempDirectory.FullName, "Directory.Packages.props");
        await File.WriteAllTextAsync(propsPath, @"<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
  </ItemGroup>
</Project>", TestContext.CancellationToken);

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(BaseOptions(), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
    }

    #endregion

    #region Manifest generation

    [TestMethod]
    public async Task SetupDotNet_GeneratesManifest_WhenNoneExists()
    {
        await CreateCsprojAsync(_tempDirectory, "App", "net10.0-windows10.0.26100.0");
        // No existing manifest -> generation runs (UseDefaults supplies manifest info).

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(BaseOptions(), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
    }

    #endregion
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using System.IO.Compression;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for the pure/deterministic MSIX &amp; Windows App SDK runtime helpers on
/// <see cref="WorkspaceSetupService"/>: MSIX inventory parsing, system-architecture
/// detection, NuGet-cache MSIX directory discovery, and runtime package installation.
/// These exercise real file/zip logic with fakes for NuGet and package registration,
/// so no network access or Windows PackageManager calls are required.
/// </summary>
[TestClass]
public class WorkspaceSetupServiceMsixTests : BaseCommandTests
{
    private FakeNugetService _fakeNugetService = null!;
    private FakePackageRegistrationService _fakePackageRegistrationService = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeNugetService = new FakeNugetService();
        _fakePackageRegistrationService = new FakePackageRegistrationService();

        return services
            .AddSingleton<INugetService>(_fakeNugetService)
            .AddSingleton<IPackageRegistrationService>(_fakePackageRegistrationService);
    }

    #region Helper methods

    private static readonly string Arch = WorkspaceSetupService.GetSystemArchitecture();

    /// <summary>Returns the architecture-specific MSIX subdirectory (win10-{arch}) under a root.</summary>
    private static string ArchDir(DirectoryInfo msixDir) => Path.Combine(msixDir.FullName, $"win10-{Arch}");

    /// <summary>Writes an msix.inventory file with the given raw lines into the arch dir.</summary>
    private static void WriteInventory(DirectoryInfo msixDir, params string[] lines)
    {
        var archDir = ArchDir(msixDir);
        Directory.CreateDirectory(archDir);
        File.WriteAllLines(Path.Combine(archDir, "msix.inventory"), lines);
    }

    /// <summary>Creates a valid MSIX (zip) with an AppxManifest.xml declaring the given identity.</summary>
    private static void CreateMsixWithManifest(DirectoryInfo msixDir, string fileName, string identityName, string version)
    {
        var archDir = ArchDir(msixDir);
        Directory.CreateDirectory(archDir);
        var path = Path.Combine(archDir, fileName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("AppxManifest.xml");
        using var writer = new StreamWriter(entry.Open());
        writer.Write($@"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"">
  <Identity Name=""{identityName}"" Version=""{version}"" Publisher=""CN=Test"" ProcessorArchitecture=""{Arch}"" />
</Package>");
    }

    /// <summary>Creates a valid zip that intentionally omits AppxManifest.xml.</summary>
    private static void CreateMsixWithoutManifest(DirectoryInfo msixDir, string fileName)
    {
        var archDir = ArchDir(msixDir);
        Directory.CreateDirectory(archDir);
        var path = Path.Combine(archDir, fileName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("readme.txt");
        using var writer = new StreamWriter(entry.Open());
        writer.Write("no manifest here");
    }

    /// <summary>Creates a file with the .msix name that is NOT a valid zip archive.</summary>
    private static void CreateCorruptMsix(DirectoryInfo msixDir, string fileName)
    {
        var archDir = ArchDir(msixDir);
        Directory.CreateDirectory(archDir);
        File.WriteAllText(Path.Combine(archDir, fileName), "this is not a zip file");
    }

    /// <summary>Creates a NuGet-cache MSIX layout: {packages}/{pkgid-lower}/{version}/tools/MSIX.</summary>
    private DirectoryInfo CreateNuGetCacheMsixDir(string packageId, string version)
    {
        var cache = _fakeNugetService.GetNuGetGlobalPackagesDir();
        var msixDir = new DirectoryInfo(Path.Combine(cache.FullName, packageId.ToLowerInvariant(), version, "tools", "MSIX"));
        msixDir.Create();
        return msixDir;
    }

    #endregion

    #region ParseMsixInventoryAsync

    [TestMethod]
    public async Task ParseMsixInventory_ReturnsNull_WhenArchitectureDirectoryMissing()
    {
        // msixDir exists but has no win10-{arch} subdirectory
        var msixDir = _tempDirectory.CreateSubdirectory("msix");
        msixDir.CreateSubdirectory("win10-otherarch");

        var result = await WindowsAppRuntimeService.ParseMsixInventoryAsync(
            TestTaskContext, msixDir, TestContext.CancellationToken);

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task ParseMsixInventory_ReturnsNull_WhenInventoryFileMissing()
    {
        var msixDir = _tempDirectory.CreateSubdirectory("msix");
        Directory.CreateDirectory(ArchDir(msixDir)); // arch dir but no msix.inventory

        var result = await WindowsAppRuntimeService.ParseMsixInventoryAsync(
            TestTaskContext, msixDir, TestContext.CancellationToken);

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task ParseMsixInventory_ReturnsEntries_WhenInventoryHasValidLines()
    {
        var msixDir = _tempDirectory.CreateSubdirectory("msix");
        WriteInventory(msixDir,
            "runtime.msix=Microsoft.WindowsAppRuntime.1.6_6000.0.0.0_x64",
            "",
            "  ",
            "ddlm.msix=Microsoft.WinAppRuntime.DDLM_6000.0.0.0_x64");

        var result = await WindowsAppRuntimeService.ParseMsixInventoryAsync(
            TestTaskContext, msixDir, TestContext.CancellationToken);

        Assert.IsNotNull(result);
        Assert.AreEqual(2, result.Count);
        Assert.AreEqual("runtime.msix", result[0].FileName);
        Assert.AreEqual("Microsoft.WindowsAppRuntime.1.6_6000.0.0.0_x64", result[0].PackageIdentity);
    }

    [TestMethod]
    public async Task ParseMsixInventory_ReturnsNull_WhenNoValidEntries()
    {
        var msixDir = _tempDirectory.CreateSubdirectory("msix");
        WriteInventory(msixDir, "", "   ", "no-equals-sign-here");

        var result = await WindowsAppRuntimeService.ParseMsixInventoryAsync(
            TestTaskContext, msixDir, TestContext.CancellationToken);

        Assert.IsNull(result);
    }

    #endregion

    #region GetSystemArchitecture

    [TestMethod]
    public void GetSystemArchitecture_ReturnsKnownArchitectureString()
    {
        var arch = WorkspaceSetupService.GetSystemArchitecture();

        var valid = new[] { "x64", "arm64", "x86" };
        Assert.Contains(arch, valid);
    }

    #endregion

    #region FindWindowsAppSdkMsixDirectory

    [TestMethod]
    public void FindWindowsAppSdkMsixDirectory_FindsRuntimePackage_FromUsedVersions()
    {
        var expected = CreateNuGetCacheMsixDir(BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE, "1.6.0");
        var service = GetRequiredService<IWindowsAppRuntimeService>();

        var usedVersions = new Dictionary<string, string>
        {
            [BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE] = "1.6.0"
        };
        var result = service.FindWindowsAppSdkMsixDirectory(usedVersions);

        Assert.IsNotNull(result);
        Assert.AreEqual(expected.FullName, result.FullName);
    }

    [TestMethod]
    public void FindWindowsAppSdkMsixDirectory_FallsBackToMainPackage_FromUsedVersions()
    {
        // Only the main package is present in the cache; runtime version is listed but absent.
        var expected = CreateNuGetCacheMsixDir(BuildToolsService.WINAPP_SDK_PACKAGE, "1.6.0");
        var service = GetRequiredService<IWindowsAppRuntimeService>();

        var usedVersions = new Dictionary<string, string>
        {
            [BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE] = "9.9.9",
            [BuildToolsService.WINAPP_SDK_PACKAGE] = "1.6.0"
        };
        var result = service.FindWindowsAppSdkMsixDirectory(usedVersions);

        Assert.IsNotNull(result);
        Assert.AreEqual(expected.FullName, result.FullName);
    }

    [TestMethod]
    public void FindWindowsAppSdkMsixDirectory_GeneralScan_PicksHighestRuntimeVersion()
    {
        // No usedVersions -> general scan of the runtime package dir; multiple versions exercise
        // the VersionStringComparer descending ordering (highest wins).
        CreateNuGetCacheMsixDir(BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE, "1.6.0");
        var expected = CreateNuGetCacheMsixDir(BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE, "1.7.0");
        var service = GetRequiredService<IWindowsAppRuntimeService>();

        var result = service.FindWindowsAppSdkMsixDirectory();

        Assert.IsNotNull(result);
        Assert.AreEqual(expected.FullName, result.FullName);
    }

    [TestMethod]
    public void FindWindowsAppSdkMsixDirectory_GeneralScan_FallsBackToMainPackage()
    {
        // Runtime package dir absent; only the main package dir present -> main-package fallback scan.
        var expected = CreateNuGetCacheMsixDir(BuildToolsService.WINAPP_SDK_PACKAGE, "1.6.0");
        var service = GetRequiredService<IWindowsAppRuntimeService>();

        var result = service.FindWindowsAppSdkMsixDirectory();

        Assert.IsNotNull(result);
        Assert.AreEqual(expected.FullName, result.FullName);
    }

    [TestMethod]
    public void FindWindowsAppSdkMsixDirectory_ReturnsNull_WhenNothingInCache()
    {
        // Ensure the packages dir exists but contains no SDK packages.
        _fakeNugetService.GetNuGetGlobalPackagesDir();
        var service = GetRequiredService<IWindowsAppRuntimeService>();

        var result = service.FindWindowsAppSdkMsixDirectory();

        Assert.IsNull(result);
    }

    #endregion

    #region InstallWindowsAppRuntimeAsync

    [TestMethod]
    public async Task InstallWindowsAppRuntime_ReturnsZero_WhenInventoryMissing()
    {
        var msixDir = _tempDirectory.CreateSubdirectory("msix"); // no inventory at all
        var service = GetRequiredService<IWindowsAppRuntimeService>();

        var (installed, errors, _) = await service.InstallWindowsAppRuntimeAsync(
            msixDir, TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual(0, installed);
        Assert.AreEqual(0, errors);
        Assert.AreEqual(0, _fakePackageRegistrationService.InstallPackageCalls.Count);
    }

    [TestMethod]
    public async Task InstallWindowsAppRuntime_ReturnsZero_WhenReferencedMsixFilesMissing()
    {
        // Inventory lists a file that does not exist on disk -> packagesToCheck ends up empty.
        var msixDir = _tempDirectory.CreateSubdirectory("msix");
        WriteInventory(msixDir, "missing.msix=Microsoft.WindowsAppRuntime.1.6_6000.0.0.0_x64");
        var service = GetRequiredService<IWindowsAppRuntimeService>();

        var (installed, errors, _) = await service.InstallWindowsAppRuntimeAsync(
            msixDir, TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual(0, installed);
        Assert.AreEqual(0, errors);
        Assert.AreEqual(0, _fakePackageRegistrationService.InstallPackageCalls.Count);
    }

    [TestMethod]
    public async Task InstallWindowsAppRuntime_InstallsPackage_WhenNotAlreadyInstalled()
    {
        var msixDir = _tempDirectory.CreateSubdirectory("msix");
        WriteInventory(msixDir, "runtime.msix=Microsoft.WindowsAppRuntime.1.6_6000.0.0.0_x64");
        CreateMsixWithManifest(msixDir, "runtime.msix", "Microsoft.WindowsAppRuntime.1.6", "6000.0.0.0");

        _fakePackageRegistrationService.FakeInstalledVersion = null; // not installed
        var service = GetRequiredService<IWindowsAppRuntimeService>();

        var (installed, errors, _) = await service.InstallWindowsAppRuntimeAsync(
            msixDir, TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual(1, installed);
        Assert.AreEqual(0, errors);
        Assert.AreEqual(1, _fakePackageRegistrationService.InstallPackageCalls.Count);
        Assert.IsTrue(_fakePackageRegistrationService.InstallPackageCalls[0].EndsWith("runtime.msix", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task InstallWindowsAppRuntime_SkipsPackage_WhenNewerVersionInstalled()
    {
        var msixDir = _tempDirectory.CreateSubdirectory("msix");
        WriteInventory(msixDir, "runtime.msix=Microsoft.WindowsAppRuntime.1.6_6000.0.0.0_x64");
        CreateMsixWithManifest(msixDir, "runtime.msix", "Microsoft.WindowsAppRuntime.1.6", "6000.0.0.0");

        _fakePackageRegistrationService.FakeInstalledVersion = "9999.0.0.0"; // newer already installed
        var service = GetRequiredService<IWindowsAppRuntimeService>();

        var (installed, errors, _) = await service.InstallWindowsAppRuntimeAsync(
            msixDir, TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual(0, installed);
        Assert.AreEqual(0, errors);
        Assert.AreEqual(0, _fakePackageRegistrationService.InstallPackageCalls.Count);
    }

    [TestMethod]
    public async Task InstallWindowsAppRuntime_CountsError_WhenInstallThrows()
    {
        var msixDir = _tempDirectory.CreateSubdirectory("msix");
        WriteInventory(msixDir, "runtime.msix=Microsoft.WindowsAppRuntime.1.6_6000.0.0.0_x64");
        CreateMsixWithManifest(msixDir, "runtime.msix", "Microsoft.WindowsAppRuntime.1.6", "6000.0.0.0");

        _fakePackageRegistrationService.FakeInstalledVersion = null;
        _fakePackageRegistrationService.InstallPackageThrows = new InvalidOperationException("boom");
        var service = GetRequiredService<IWindowsAppRuntimeService>();

        var (installed, errors, _) = await service.InstallWindowsAppRuntimeAsync(
            msixDir, TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual(0, installed);
        Assert.AreEqual(1, errors);
    }

    [TestMethod]
    public async Task InstallWindowsAppRuntime_UsesInventoryIdentity_WhenManifestMissing()
    {
        // Zip without an AppxManifest.xml -> ReadMsixIdentity returns (null, null) ->
        // fallback to parsing the inventory identity string "Name_Version".
        var msixDir = _tempDirectory.CreateSubdirectory("msix");
        WriteInventory(msixDir, "runtime.msix=Microsoft.WindowsAppRuntime.1.6_6000.0.0.0_x64");
        CreateMsixWithoutManifest(msixDir, "runtime.msix");

        _fakePackageRegistrationService.FakeInstalledVersion = null;
        var service = GetRequiredService<IWindowsAppRuntimeService>();

        var (installed, errors, _) = await service.InstallWindowsAppRuntimeAsync(
            msixDir, TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual(1, installed);
        Assert.AreEqual(0, errors);
        // Fallback parsed the package name from the inventory identity before "_".
        Assert.IsTrue(
            _fakePackageRegistrationService.GetInstalledVersionCalls.Any(c => c.PackageName == "Microsoft.WindowsAppRuntime.1.6"),
            "Expected GetInstalledVersion to be called with the package name parsed from the inventory identity.");
    }

    [TestMethod]
    public async Task InstallWindowsAppRuntime_HandlesCorruptMsix_ViaInventoryFallback()
    {
        // A file that is not a valid zip exercises the ReadMsixIdentity catch, then falls back
        // to the inventory identity string and still installs.
        var msixDir = _tempDirectory.CreateSubdirectory("msix");
        WriteInventory(msixDir, "runtime.msix=Microsoft.WindowsAppRuntime.1.6_6000.0.0.0_x64");
        CreateCorruptMsix(msixDir, "runtime.msix");

        _fakePackageRegistrationService.FakeInstalledVersion = null;
        var service = GetRequiredService<IWindowsAppRuntimeService>();

        var (installed, errors, _) = await service.InstallWindowsAppRuntimeAsync(
            msixDir, TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual(1, installed);
        Assert.AreEqual(0, errors);
    }

    #endregion
}


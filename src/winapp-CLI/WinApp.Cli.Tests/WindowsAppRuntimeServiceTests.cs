// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="WindowsAppRuntimeService"/> NuGet-cache runtime discovery, in particular the
/// <c>requireExactVersion</c> gate (C40): project-mode unpackaged launches must resolve the runtime the
/// app was actually built against and never fall back to an unrelated cached WinAppSDK version, because
/// the presence gate derived from that fallback would otherwise pass against the wrong runtime family.
/// </summary>
[TestClass]
public class WindowsAppRuntimeServiceTests
{
    private DirectoryInfo _cacheRoot = null!;
    private FakeNugetService _fakeNuget = null!;
    private FakePackageRegistrationService _fakeRegistration = null!;
    private WindowsAppRuntimeService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _cacheRoot = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "winapp-wart-" + Guid.NewGuid().ToString("N")));
        _cacheRoot.Create();
        _fakeNuget = new FakeNugetService { CacheDirectory = _cacheRoot };
        _fakeRegistration = new FakePackageRegistrationService();
        _service = new WindowsAppRuntimeService(_fakeRegistration, _fakeNuget);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (_cacheRoot.Exists)
            {
                _cacheRoot.Delete(recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup; never fail a test on cleanup.
        }
    }

    /// <summary>Creates a valid MSIX package layout (<c>&lt;id&gt;/&lt;version&gt;/tools/MSIX</c>) in the fake NuGet cache.</summary>
    private void SeedCachedRuntime(string packageId, string version)
    {
        var packagesDir = _fakeNuget.GetNuGetGlobalPackagesDir();
        Directory.CreateDirectory(Path.Combine(packagesDir.FullName, packageId.ToLowerInvariant(), version, "tools", "MSIX"));
    }

    [TestMethod]
    public void FindWindowsAppSdkMsixDirectory_DefaultScan_ReturnsUnrelatedCachedRuntime()
    {
        // Only an UNRELATED runtime version is cached; the app's exact version is absent.
        SeedCachedRuntime(BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE, "9.9.9-unrelated");
        var usedVersions = new Dictionary<string, string>
        {
            [BuildToolsService.WINAPP_SDK_PACKAGE] = "1.6.240701",
        };

        var result = _service.FindWindowsAppSdkMsixDirectory(usedVersions, requireExactVersion: false);

        // Legacy/packaged behavior: the general scan happily returns the unrelated cached runtime.
        Assert.IsNotNull(result, "default (tolerant) scan should fall back to any cached runtime");
        Assert.Contains("9.9.9-unrelated", result!.FullName);
    }

    [TestMethod]
    public void FindWindowsAppSdkMsixDirectory_RequireExact_DoesNotReturnUnrelatedCachedRuntime()
    {
        // Same cache: only an unrelated runtime version is present, the app's exact version is absent.
        SeedCachedRuntime(BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE, "9.9.9-unrelated");
        var usedVersions = new Dictionary<string, string>
        {
            [BuildToolsService.WINAPP_SDK_PACKAGE] = "1.6.240701",
        };

        var result = _service.FindWindowsAppSdkMsixDirectory(usedVersions, requireExactVersion: true);

        // C40: exact-version callers must NOT accept the unrelated runtime — return null so the caller
        // surfaces "exact version unavailable" instead of installing/gating on the wrong runtime.
        Assert.IsNull(result, "requireExactVersion must skip the general scan when the exact version is absent");
    }

    [TestMethod]
    public void FindWindowsAppSdkMsixDirectory_RequireExact_ReturnsExactMatchWhenPresent()
    {
        // The exact runtime the app needs IS restored to the cache (the normal post-build state).
        SeedCachedRuntime(BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE, "1.6.240701");
        var usedVersions = new Dictionary<string, string>
        {
            [BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE] = "1.6.240701",
        };

        var result = _service.FindWindowsAppSdkMsixDirectory(usedVersions, requireExactVersion: true);

        Assert.IsNotNull(result, "requireExactVersion must still return the exact match when it is cached");
        Assert.Contains("1.6.240701", result!.FullName);
    }

    private static TaskContext NewTaskContext() =>
        new(new GroupableTask("wart-test", null), null, new TestConsole(), NullLogger<WindowsAppRuntimeService>.Instance, new Lock());

    /// <summary>
    /// Seeds a minimal <c>win10-{arch}/msix.inventory</c> plus a placeholder (non-zip) package file, so
    /// <see cref="WindowsAppRuntimeService.InstallWindowsAppRuntimeAsync"/> produces exactly one package to
    /// evaluate. The placeholder file is intentionally not a real MSIX zip: <c>ReadMsixIdentity</c> fails and
    /// the code falls back to parsing the package name/version from the inventory identity string.
    /// </summary>
    private DirectoryInfo SeedInventory(string arch, string fileName, string identity)
    {
        var msixDir = Directory.CreateDirectory(Path.Combine(_cacheRoot.FullName, "msix-" + Guid.NewGuid().ToString("N")));
        var archDir = Directory.CreateDirectory(Path.Combine(msixDir.FullName, $"win10-{arch}"));
        File.WriteAllText(Path.Combine(archDir.FullName, fileName), "not-a-real-msix-zip");
        File.WriteAllText(Path.Combine(archDir.FullName, "msix.inventory"), $"{fileName}={identity}\n");
        return msixDir;
    }

    [TestMethod]
    public async Task InstallWindowsAppRuntime_ProjectMode_ChecksInstalledVersionForTargetArch_NotHostArch()
    {
        // Cross-arch scenario: the app resolves to arm64, but the SAME-name runtime Framework is already
        // registered for the host (x64) arch and absent for arm64. Project mode must filter the
        // "already installed?" check by the target arch so it doesn't wrongly skip the arm64 install.
        var msixDir = SeedInventory("arm64", "Framework.msix", "Microsoft.WindowsAppRuntime.1.6_1.6.240701_arm64__8wekyb3d8bbwe");
        _fakeRegistration.GetInstalledVersionFunc = (_, requestedArch) => requestedArch == "x64" ? "9.9.9" : null;

        var (installedCount, errorCount, _) = await _service.InstallWindowsAppRuntimeAsync(
            msixDir, NewTaskContext(), CancellationToken.None, architecture: "arm64");

        Assert.AreEqual(0, errorCount);
        Assert.AreEqual(1, installedCount, "cross-arch runtime must install even though a host-arch package with the same name is registered");
        Assert.AreEqual(1, _fakeRegistration.InstallPackageCalls.Count);
        Assert.Contains(
            ("Microsoft.WindowsAppRuntime.1.6", (string?)"arm64"),
            _fakeRegistration.GetInstalledVersionCalls,
            "project mode must query the installed version for the resolved target arch");
        Assert.DoesNotContain(
            ("Microsoft.WindowsAppRuntime.1.6", (string?)null),
            _fakeRegistration.GetInstalledVersionCalls,
            "project mode must not fall back to the arch-agnostic (null) installed-version check");
    }

    [TestMethod]
    public async Task InstallWindowsAppRuntime_FolderMode_ChecksInstalledVersionArchAgnostic()
    {
        // Folder mode (no explicit arch) is byte-for-byte legacy behavior: the installed-version check is
        // arch-agnostic (null filter), so a present same-or-newer package skips the install.
        var hostArch = RunArchHelper.DefaultArchitecture();
        var msixDir = SeedInventory(hostArch, "Framework.msix", "Microsoft.WindowsAppRuntime.1.6_1.6.240701_" + hostArch + "__8wekyb3d8bbwe");
        _fakeRegistration.GetInstalledVersionFunc = (_, _) => "9.9.9";

        var (installedCount, errorCount, _) = await _service.InstallWindowsAppRuntimeAsync(
            msixDir, NewTaskContext(), CancellationToken.None, architecture: null);

        Assert.AreEqual(0, errorCount);
        Assert.AreEqual(0, installedCount, "a registered same-or-newer package must skip the install in folder mode");
        Assert.AreEqual(0, _fakeRegistration.InstallPackageCalls.Count);
        Assert.Contains(
            ("Microsoft.WindowsAppRuntime.1.6", (string?)null),
            _fakeRegistration.GetInstalledVersionCalls,
            "folder mode must keep the arch-agnostic (null) installed-version check");
    }
}

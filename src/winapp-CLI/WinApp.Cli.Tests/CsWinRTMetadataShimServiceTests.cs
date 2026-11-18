// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Unit tests for the temporary <see cref="CsWinRTMetadataShimService"/>. The Windows SDK registry lookup
/// and <c>WindowsSDKVersion</c> variable are stubbed via the service's seams, a fake SDK root is laid out in
/// a temp directory, and the NuGet cache is simulated with <see cref="FakeNugetService"/>, so these run
/// deterministically on any host regardless of its real SDK/registry state.
/// </summary>
[TestClass]
public class CsWinRTMetadataShimServiceTests
{
    private DirectoryInfo _tempDir = null!;
    private FakeNugetService _nuget = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = new DirectoryInfo(Path.Join(Path.GetTempPath(), $"CsWinRTShimTests_{Guid.NewGuid():N}"));
        _tempDir.Create();
        _nuget = new FakeNugetService { CacheDirectory = new DirectoryInfo(Path.Join(_tempDir.FullName, "nuget")) };
        _nuget.CacheDirectory.Create();
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { _tempDir.Delete(true); } catch { /* ignore */ }
    }

    private CsWinRTMetadataShimService NewService(string? sdkRoot, string? windowsSdkVersionVariable = null)
        => new(_nuget, NullLogger<CsWinRTMetadataShimService>.Instance)
        {
            GetWindowsSdkRoot = () => sdkRoot,
            GetWindowsSdkVersionVariable = () => windowsSdkVersionVariable,
        };

    /// <summary>Creates a fake Windows SDK root with a <c>Platform.xml</c> for each installed version.</summary>
    private string CreateSdkRoot(params string[] installedVersions)
    {
        var root = Path.Join(_tempDir.FullName, "Windows Kits", "10");
        Directory.CreateDirectory(root);
        foreach (var platform in installedVersions.Select(version => Path.Join(root, "Platforms", "UAP", version)))
        {
            Directory.CreateDirectory(platform);
            File.WriteAllText(Path.Join(platform, "Platform.xml"), "<ApplicationPlatform />");
        }

        return root;
    }

    /// <summary>Creates a ref-pack version folder with (optionally) the sentinel FoundationContract winmd.</summary>
    private DirectoryInfo CreateRefPackVersion(string version, bool withSentinel = true)
    {
        var cache = _nuget.GetNuGetGlobalPackagesDir();
        var winmd = new DirectoryInfo(Path.Combine(cache.FullName, "microsoft.windows.sdk.net.ref", version, "winmd"));
        winmd.Create();
        if (withSentinel)
        {
            File.WriteAllText(Path.Combine(winmd.FullName, "Windows.Foundation.FoundationContract.winmd"), string.Empty);
        }
        return winmd;
    }

    [TestMethod]
    public void ResolveMetadataFolder_TargetedSdkInstalled_ReturnsNull()
    {
        CreateRefPackVersion("10.0.26100.57");
        var service = NewService(CreateSdkRoot("10.0.26100.0"));

        var result = service.ResolveMetadataFolder("net10.0-windows10.0.26100.0");

        Assert.IsNull(result, "an installed targeted SDK means cswinrt's own resolution works; do not inject.");
    }

    [TestMethod]
    public void ResolveMetadataFolder_SdkRegisteredButTargetedVersionMissing_InjectsMatchingRefPack()
    {
        // A box with only a newer SDK: cswinrt would fail to read 10.0.22621.0's Platform.xml.
        var expected = CreateRefPackVersion("10.0.22621.57");
        CreateRefPackVersion("10.0.26100.57");
        var service = NewService(CreateSdkRoot("10.0.26100.0"));

        var result = service.ResolveMetadataFolder("net9.0-windows10.0.22621.0");

        Assert.AreEqual(expected.FullName, result, "the ref pack matching the targeted platform replaces the missing SDK.");
    }

    [TestMethod]
    public void ResolveMetadataFolder_SdkRegisteredAndNoTfm_ReturnsNull()
    {
        CreateRefPackVersion("10.0.26100.57");
        var service = NewService(CreateSdkRoot("10.0.26100.0"));

        var result = service.ResolveMetadataFolder(targetFrameworkMoniker: null);

        Assert.IsNull(result, "without a targeted version, a registered SDK is trusted as before.");
    }

    [TestMethod]
    public void IsWindowsSdkAbsent_WindowsSdkVersionVariableWins_OverTfm()
    {
        // CsWinRT prefers $(WindowsSDKVersion) (set by VS developer prompts, with a trailing backslash).
        var root = CreateSdkRoot("10.0.26100.0");

        Assert.IsFalse(NewService(root, @"10.0.26100.0\").IsWindowsSdkAbsent("net9.0-windows10.0.22621.0"));
        Assert.IsTrue(NewService(root, @"10.0.22621.0\").IsWindowsSdkAbsent("net10.0-windows10.0.26100.0"));
    }

    [TestMethod]
    public void IsWindowsSdkAbsent_NoSdkRegistered_ReturnsTrue()
    {
        Assert.IsTrue(NewService(sdkRoot: null).IsWindowsSdkAbsent("net10.0-windows10.0.26100.0"));
        Assert.IsTrue(NewService(sdkRoot: null).IsWindowsSdkAbsent(targetFrameworkMoniker: null));
    }

    [TestMethod]
    [DataRow("sdk+")]
    [DataRow("local")]
    [DataRow(@"C:\winmds")]
    public void ResolveMetadataFolder_RegisteredSdk_ProjectChoseOwnMetadata_ReturnsNull(string projectMetadata)
    {
        // A project-set CsWinRTWindowsMetadata that isn't a missing SDK version is the project's choice; the
        // shim's global -p: must not override it.
        CreateRefPackVersion("10.0.22621.57");
        var service = NewService(CreateSdkRoot("10.0.26100.0"));

        Assert.IsTrue(service.IsWindowsSdkAbsent("net9.0-windows10.0.22621.0"), "the default would read a missing SDK");
        Assert.IsNull(service.ResolveMetadataFolder("net9.0-windows10.0.22621.0", projectMetadata));
    }

    [TestMethod]
    [DataRow("local")]
    [DataRow(@"C:\winmds")]
    public void ResolveMetadataFolder_NoSdk_ProjectMetadataWithoutSdk_ReturnsNull(string projectMetadata)
    {
        // 'local' and winmd folders don't need a Windows SDK, so they're respected even on an SDK-less host.
        CreateRefPackVersion("10.0.22621.57");
        var service = NewService(sdkRoot: null);

        Assert.IsNull(service.ResolveMetadataFolder("net9.0-windows10.0.22621.0", projectMetadata));
    }

    [TestMethod]
    public void ResolveMetadataFolder_NoSdk_ProjectAsksForSdk_Injects()
    {
        // 'sdk' needs a registered SDK, which this host lacks, so the shim still rescues the build.
        var expected = CreateRefPackVersion("10.0.22621.57");
        var service = NewService(sdkRoot: null);

        Assert.AreEqual(expected.FullName, service.ResolveMetadataFolder("net9.0-windows10.0.22621.0", "sdk"));
    }

    [TestMethod]
    public void ResolveMetadataFolder_ProjectVersionInstalled_ReturnsNull()
    {
        CreateRefPackVersion("10.0.22621.57");
        var service = NewService(CreateSdkRoot("10.0.26100.0"));

        Assert.IsNull(service.ResolveMetadataFolder("net9.0-windows10.0.22621.0", "10.0.26100.0"),
            "a project that asks for an installed version builds as before.");
    }

    [TestMethod]
    public void ResolveMetadataFolder_TfmSdkInstalledButProjectPinsMissingVersion_InjectsPinnedRefPack()
    {
        var expected = CreateRefPackVersion("10.0.22621.57");
        CreateRefPackVersion("10.0.26100.57");
        var service = NewService(CreateSdkRoot("10.0.26100.0"));

        Assert.AreEqual(expected.FullName, service.ResolveMetadataFolder("net10.0-windows10.0.26100.0", "10.0.22621.0"),
            "cswinrt reads the pinned 22621, so its missing SDK is replaced with the 22621 ref pack.");
    }

    [TestMethod]
    public void ResolveMetadataFolder_RequestedVersionDiffersFromTfm_PrefersRequestedRefPack()
    {
        // WindowsSDKVersion asks for 22621, which is missing, while the TFM says 26100.
        var expected = CreateRefPackVersion("10.0.22621.57");
        CreateRefPackVersion("10.0.26100.57");
        var service = NewService(CreateSdkRoot("10.0.26100.0"), @"10.0.22621.0\");

        Assert.AreEqual(expected.FullName, service.ResolveMetadataFolder("net10.0-windows10.0.26100.0"),
            "the ref pack for the version cswinrt was asked for wins over the TFM's.");
    }

    [TestMethod]
    [DataRow(null, null, "net9.0-windows10.0.22621.0", true, "10.0.22621.0")]
    [DataRow(null, null, "net10.0-windows10.0.22621", true, "10.0.22621.0")]
    [DataRow(null, null, "net8.0-windows10.0.19041.1", true, "10.0.19041.1")]
    [DataRow(null, @"10.0.26100.0\", "net9.0-windows10.0.22621.0", true, "10.0.26100.0")]
    [DataRow(null, "not-a-version", "net9.0-windows10.0.22621.0", true, null)]
    [DataRow(null, null, "net10.0", true, null)]
    [DataRow(null, null, null, true, null)]
    [DataRow("10.0.19041.0", @"10.0.26100.0\", "net9.0-windows10.0.22621.0", true, "10.0.19041.0")]
    [DataRow("10.0.19041.0+", null, "net9.0-windows10.0.22621.0", true, "10.0.19041.0")]
    [DataRow("sdk+", null, "net9.0-windows10.0.22621.0", true, null)]
    [DataRow("local", null, "net9.0-windows10.0.22621.0", false, null)]
    [DataRow(@"C:\winmds", null, "net9.0-windows10.0.22621.0", false, null)]
    public void ResolveCsWinRTMetadataRequest_MatchesWhatCsWinRTReads(
        string? projectMetadata, string? variable, string? tfm, bool readsSdk, string? version)
    {
        var request = CsWinRTMetadataShimService.ResolveCsWinRTMetadataRequest(projectMetadata, variable, tfm);

        Assert.AreEqual(readsSdk, request.ReadsWindowsSdk);
        Assert.AreEqual(version, request.SdkVersion);
    }

    [TestMethod]
    public void ResolveMetadataFolder_SdkAbsent_ReturnsHighestWinmdFolder()
    {
        CreateRefPackVersion("10.0.19041.57");
        var expected = CreateRefPackVersion("10.0.26100.57");
        var service = NewService(sdkRoot: null);

        var result = service.ResolveMetadataFolder(targetFrameworkMoniker: null);

        Assert.AreEqual(expected.FullName, result, "with no TFM hint the highest usable ref-pack winmd folder wins.");
    }

    [TestMethod]
    public void ResolveMetadataFolder_SdkAbsent_PrefersTfmMatchOverHighest()
    {
        var expected = CreateRefPackVersion("10.0.19041.57");
        CreateRefPackVersion("10.0.26100.57"); // higher, but doesn't match the targeted platform
        var service = NewService(sdkRoot: null);

        var result = service.ResolveMetadataFolder("net10.0-windows10.0.19041.0");

        Assert.AreEqual(expected.FullName, result, "a ref-pack version matching the project's platform is preferred over the highest.");
    }

    [TestMethod]
    public void ResolveMetadataFolder_TfmMatchMissingSentinel_FallsBackToHighestUsable()
    {
        CreateRefPackVersion("10.0.19041.57", withSentinel: false); // matches TFM but unusable
        var expected = CreateRefPackVersion("10.0.26100.57");
        var service = NewService(sdkRoot: null);

        var result = service.ResolveMetadataFolder("net10.0-windows10.0.19041.0");

        Assert.AreEqual(expected.FullName, result, "an unusable TFM match is skipped in favour of the highest usable version.");
    }

    [TestMethod]
    public void ResolveMetadataFolder_RefPackNotRestored_ReturnsNull()
    {
        // No ref-pack folders created at all.
        var service = NewService(sdkRoot: null);

        var result = service.ResolveMetadataFolder("net10.0-windows10.0.26100.0");

        Assert.IsNull(result, "a missing ref pack must no-op (not throw) so the normal build error can surface.");
    }

    [TestMethod]
    public void ResolveMetadataFolder_NoVersionHasSentinel_ReturnsNull()
    {
        CreateRefPackVersion("10.0.26100.57", withSentinel: false);
        var service = NewService(sdkRoot: null);

        var result = service.ResolveMetadataFolder(targetFrameworkMoniker: null);

        Assert.IsNull(result, "when no version contains FoundationContract.winmd there is nothing safe to inject.");
    }

    [TestMethod]
    public void ResolveMetadataFolder_PrereleaseLosesTieToStable()
    {
        var stable = CreateRefPackVersion("10.0.26100.57");
        CreateRefPackVersion("10.0.26100.57-preview");
        var service = NewService(sdkRoot: null);

        var result = service.ResolveMetadataFolder(targetFrameworkMoniker: null);

        Assert.AreEqual(stable.FullName, result, "a stable build wins the tie against a same-version prerelease.");
    }

    [TestMethod]
    [DataRow("net10.0-windows10.0.19041.0", "10.0.19041")]
    [DataRow("net8.0-windows10.0.26100.0", "10.0.26100")]
    [DataRow("net10.0-windows10.0.22621", "10.0.22621")]
    [DataRow("net10.0", null)]
    [DataRow("", null)]
    [DataRow(null, null)]
    public void ExtractPlatformVersionPrefix_ParsesMoniker(string? tfm, string? expected)
    {
        Assert.AreEqual(expected, CsWinRTMetadataShimService.ExtractPlatformVersionPrefix(tfm));
    }

    [TestMethod]
    public void SelectBestRefPackVersionDir_NoUsable_ReturnsNull()
    {
        var result = CsWinRTMetadataShimService.SelectBestRefPackVersionDir(
            ["10.0.19041.57", "10.0.26100.57"], platformVersionPrefix: null, isUsable: _ => false);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void SelectBestRefPackVersionDir_IgnoresUnparseableNames()
    {
        var result = CsWinRTMetadataShimService.SelectBestRefPackVersionDir(
            ["not-a-version", "10.0.19041.57"], platformVersionPrefix: null, isUsable: _ => true);

        Assert.AreEqual("10.0.19041.57", result);
    }
}

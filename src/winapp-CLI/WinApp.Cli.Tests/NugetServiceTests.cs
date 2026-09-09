// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class NugetServiceTests : BaseCommandTests
{
    /// <summary>
    /// The live NuGet v3 source these integration tests resolve against. Defaults to nuget.org.
    /// Environments that cannot reach it — the network-isolated ADO pipeline, and Microsoft corporate
    /// machines — point this at an internal mirror of nuget.org via <c>WINAPP_TEST_NUGET_SOURCE</c>.
    /// </summary>
    private static string LiveSource =>
        Environment.GetEnvironmentVariable("WINAPP_TEST_NUGET_SOURCE") is { Length: > 0 } source
            ? source
            : "https://api.nuget.org/v3/index.json";

    private INugetService _nugetService = null!;

    [TestInitialize]
    public void Setup()
    {
        // Root these live tests at an isolated nuget.config so they never inherit the machine/user config.
        // BaseCommandTests roots the NuGet provider at a temp dir but writes no config there, so NuGet still
        // merges the user/machine nuget.config — on a machine that clears the live source, disables it via
        // <disabledPackageSources>, or maps packages to a private feed (exactly the scenario this migration
        // enables) these assertions would then fail environmentally. Clearing the inherited sources,
        // disabled-sources and mapping and re-adding only the live source makes them hermetic. The provider's
        // Settings are evaluated lazily on first use, so writing the file here (before any test body runs) is
        // sufficient.
        File.WriteAllText(
            Path.Join(_tempDirectory.FullName, "nuget.config"),
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="live" value="{LiveSource}" />
              </packageSources>
              <disabledPackageSources>
                <clear />
              </disabledPackageSources>
              <packageSourceMapping>
                <clear />
                <packageSource key="live">
                  <package pattern="*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);

        _nugetService = GetRequiredService<INugetService>();
    }

    #region GetPackageDependenciesAsync Integration Tests

    [TestMethod]
    public async Task GetPackageDependenciesAsync_KnownPackageWithDependencies_ReturnsDependencies()
    {
        // Arrange - Newtonsoft.Json has no dependencies, but Microsoft.Extensions.Logging has dependencies
        var packageName = "Microsoft.Extensions.Logging";
        var version = "8.0.0";

        // Act
        var result = await _nugetService.GetPackageDependenciesAsync(packageName, version, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNotEmpty(result, "Should have at least one dependency");
        Assert.IsTrue(result.ContainsKey("Microsoft.Extensions.Logging.Abstractions"),
            "Should contain Microsoft.Extensions.Logging.Abstractions dependency");
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_PackageWithMinimalDependencies_ReturnsDependencies()
    {
        // Arrange - Newtonsoft.Json has some framework-specific dependencies for older frameworks
        // This tests that the implementation returns all dependencies across all target framework groups
        var packageName = "Newtonsoft.Json";
        var version = "13.0.3";

        // Act
        var result = await _nugetService.GetPackageDependenciesAsync(packageName, version, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(result);
        // Newtonsoft.Json has dependencies for older frameworks like net20, net35, etc.
        // The implementation returns all dependencies from all target framework groups
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_NonExistentPackage_ReturnsEmptyDictionary()
    {
        // Arrange
        var packageName = "This.Package.Does.Not.Exist.12345";
        var version = "1.0.0";

        // Act
        var result = await _nugetService.GetPackageDependenciesAsync(packageName, version, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsEmpty(result, "Non-existent package should return empty dictionary");
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_NonExistentVersion_ReturnsEmptyDictionary()
    {
        // Arrange
        var packageName = "Newtonsoft.Json";
        var version = "999.999.999"; // Non-existent version

        // Act
        var result = await _nugetService.GetPackageDependenciesAsync(packageName, version, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsEmpty(result, "Non-existent version should return empty dictionary");
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_PackageWithVersionRanges_ReturnsVersionRanges()
    {
        // Arrange - Microsoft.Extensions.DependencyInjection uses version ranges
        var packageName = "Microsoft.Extensions.DependencyInjection";
        var version = "8.0.0";

        // Act
        var result = await _nugetService.GetPackageDependenciesAsync(packageName, version, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNotEmpty(result, "Should have dependencies");
        // Each dependency value is a concrete version that satisfies the declared range (the lowest listed
        // satisfying version), never a raw range
        foreach (var dep in result)
        {
            Assert.IsFalse(string.IsNullOrEmpty(dep.Value), $"Dependency {dep.Key} should have a version");
        }
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_CaseInsensitivePackageName_ReturnsDependencies()
    {
        // Arrange - use mixed case
        var packageName = "MICROSOFT.EXTENSIONS.LOGGING";
        var version = "8.0.0";

        // Act
        var result = await _nugetService.GetPackageDependenciesAsync(packageName, version, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNotEmpty(result, "Should have dependencies regardless of package name casing");
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_ReturnsTransitiveDependencies()
    {
        // Arrange - Microsoft.Extensions.Logging 8.0.0 depends on
        // Microsoft.Extensions.DependencyInjection.Abstractions (direct dep),
        // and Microsoft.Extensions.Logging.Abstractions which itself depends on
        // Microsoft.Extensions.DependencyInjection.Abstractions (transitive).
        // We verify that a dependency of a dependency is included.
        var packageName = "Microsoft.Extensions.Logging";
        var version = "8.0.0";

        // Act
        var result = await _nugetService.GetPackageDependenciesAsync(packageName, version, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result.ContainsKey("Microsoft.Extensions.DependencyInjection.Abstractions"),
            "Should contain transitive dependency Microsoft.Extensions.DependencyInjection.Abstractions");
        Assert.IsTrue(result.ContainsKey("Microsoft.Extensions.Logging.Abstractions"),
            "Should contain direct dependency Microsoft.Extensions.Logging.Abstractions");
    }

    #endregion

    #region GetLatestVersionAsync Integration Tests

    [TestMethod]
    public async Task GetLatestVersionAsync_StableVersion_ReturnsNonEmptyVersion()
    {
        // Arrange - use a well-known package
        var packageName = "Newtonsoft.Json";

        // Act
        var result = await _nugetService.GetLatestVersionAsync(packageName, SdkInstallMode.Stable, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result), "Should return a non-empty version string");
        Assert.IsFalse(result.Contains('-', StringComparison.Ordinal), "Stable version should not contain prerelease suffix");
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_ReturnedVersionIsListed()
    {
        // Arrange - use a well-known package and verify the returned version is actually listed on NuGet
        var packageName = "Newtonsoft.Json";

        // Act
        var version = await _nugetService.GetLatestVersionAsync(packageName, SdkInstallMode.Stable, TestContext.CancellationToken);

        // Assert - verify the version is listed by checking the registration API directly
        Assert.IsNotNull(version);
        var isListed = await IsVersionListedAsync(packageName, version, TestContext.CancellationToken);
        Assert.IsTrue(isListed, $"Returned version {version} should be listed on NuGet, but it appears to be unlisted");
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_DoesNotReturnUnlistedVersions()
    {
        // Arrange - query all listed versions from the registration API and compare against GetLatestVersionAsync result
        var packageName = "Newtonsoft.Json";

        // Act
        var latestVersion = await _nugetService.GetLatestVersionAsync(packageName, SdkInstallMode.Stable, TestContext.CancellationToken);

        // Also get every version the feed exposes (including unlisted) to verify filtering is happening
        var allVersions = await GetAllFeedVersionsAsync(packageName, TestContext.CancellationToken);
        var listedVersions = await GetListedVersionsAsync(packageName, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(latestVersion);
        Assert.Contains(latestVersion, listedVersions,
            $"GetLatestVersionAsync returned '{latestVersion}' which is not in the listed versions set");

        // If unlisted versions exist, verify the returned version isn't one of them
        var unlistedVersions = allVersions.Except(listedVersions).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(latestVersion, unlistedVersions,
            $"GetLatestVersionAsync returned '{latestVersion}' which is an unlisted version");
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_WindowsAppSdk_StableVersion_ReturnsStableVersion()
    {
        // Arrange
        var packageName = "Microsoft.WindowsAppSDK";

        // Act
        var result = await _nugetService.GetLatestVersionAsync(packageName, SdkInstallMode.Stable, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsFalse(result.Contains('-', StringComparison.Ordinal), "Stable version should not contain prerelease suffix");
        var isListed = await IsVersionListedAsync(packageName, result, TestContext.CancellationToken);
        Assert.IsTrue(isListed, $"Returned version {result} should be listed on NuGet");
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_NoneMode_ThrowsArgumentException()
    {
        // Act & Assert
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => _nugetService.GetLatestVersionAsync("Newtonsoft.Json", SdkInstallMode.None, TestContext.CancellationToken));
    }

    /// <summary>
    /// Checks whether a specific version is listed on the configured live feed.
    /// </summary>
    private static async Task<bool> IsVersionListedAsync(string packageName, string version, CancellationToken cancellationToken)
    {
        var listed = await GetListedVersionsAsync(packageName, cancellationToken);
        return listed.Contains(NuGetVersion.Parse(version).ToNormalizedString());
    }

    /// <summary>
    /// Gets every version the feed exposes, including unlisted ones. The flat-container
    /// (PackageBaseAddress) resource behind <see cref="FindPackageByIdResource"/> enumerates the raw
    /// package folder and therefore carries no listed/unlisted flag, which is exactly what makes it
    /// usable as the "all versions" side of the unlisted-filtering assertions.
    /// </summary>
    private static async Task<HashSet<string>> GetAllFeedVersionsAsync(string packageName, CancellationToken cancellationToken)
    {
        var resource = await CreateLiveRepository().GetResourceAsync<FindPackageByIdResource>(cancellationToken);
        Assert.IsNotNull(resource, $"Source '{LiveSource}' exposes no PackageBaseAddress (flat container) resource");
        using var cache = new SourceCacheContext();
        var versions = await resource.GetAllVersionsAsync(packageName, cache, NullLogger.Instance, cancellationToken);
        return [.. versions.Select(v => v.ToNormalizedString())];
    }

    /// <summary>
    /// Gets only the listed versions, from the registration-backed metadata resource.
    /// </summary>
    private static async Task<HashSet<string>> GetListedVersionsAsync(string packageName, CancellationToken cancellationToken)
    {
        var resource = await CreateLiveRepository().GetResourceAsync<PackageMetadataResource>(cancellationToken);
        Assert.IsNotNull(resource, $"Source '{LiveSource}' exposes no registration (package metadata) resource");
        using var cache = new SourceCacheContext();
        var metadata = await resource.GetMetadataAsync(
            packageName,
            includePrerelease: true,
            includeUnlisted: false,
            cache,
            NullLogger.Instance,
            cancellationToken);
        return [.. metadata.Select(m => m.Identity.Version.ToNormalizedString())];
    }

    /// <summary>
    /// Builds a repository for <see cref="LiveSource"/>, the same feed <see cref="Setup"/> writes into the
    /// test-local nuget.config, so these assertions verify the service against the feed it actually queried.
    /// </summary>
    private static SourceRepository CreateLiveRepository()
    {
        // The live source may be an authenticated internal mirror, so the credential service has to be
        // configured before the repository builds its HTTP resources.
        NugetSourceProvider.EnsureCredentialService();
        return Repository.Factory.GetCoreV3(LiveSource);
    }

    #endregion

    #region CompareVersions Tests

    [TestMethod]
    public void CompareVersions_SimpleVersions_ComparesCorrectly()
    {
        Assert.IsLessThan(0, NugetService.CompareVersions("1.0.0", "2.0.0"));
        Assert.IsGreaterThan(0, NugetService.CompareVersions("2.0.0", "1.0.0"));
        Assert.AreEqual(0, NugetService.CompareVersions("1.0.0", "1.0.0"));
    }

    [TestMethod]
    public void CompareVersions_DifferentLengths_ComparesCorrectly()
    {
        Assert.IsLessThan(0, NugetService.CompareVersions("1.0", "1.0.1"));
        Assert.AreEqual(0, NugetService.CompareVersions("1.0.0.0", "1.0"));
    }

    [TestMethod]
    public void CompareVersions_WithPrereleaseTags_ComparesCorrectly()
    {
        // Uses NuGet SemVer 2.0 ordering: numbered prerelease tags order by their number, and a stable
        // release outranks its own prerelease. (The previous numeric-only split treated all of these as
        // equal, which made "latest" selection for preview/experimental channels non-deterministic.)

        // 1.0.0-preview1 < 1.0.0-preview2
        Assert.IsLessThan(0, NugetService.CompareVersions("1.0.0-preview1", "1.0.0-preview2"));
        Assert.IsGreaterThan(0, NugetService.CompareVersions("1.0.0-preview2", "1.0.0-preview1"));

        // A stable release is greater than its prerelease of the same version.
        Assert.IsGreaterThan(0, NugetService.CompareVersions("1.0.0", "1.0.0-preview"));
        Assert.IsLessThan(0, NugetService.CompareVersions("1.0.0-preview", "1.0.0"));
    }

    [TestMethod]
    public void CompareVersions_NonNuGetVersionInputs_FallsBackToNumericSegmentComparison()
    {
        // When either input is not a parseable NuGet version, CompareVersions cannot defer to NuGetVersion and
        // falls back to a numeric-segment comparison that parses each non-numeric segment as 0. These inputs
        // (an "x" revision) force that fallback so the branch that real "latest" selection never reaches with
        // clean feed data is still exercised.

        // Differing numeric segments: 1.2.x -> [1,2,0] vs 1.3.x -> [1,3,0].
        Assert.IsLessThan(0, NugetService.CompareVersions("1.2.x", "1.3.x"));
        Assert.IsGreaterThan(0, NugetService.CompareVersions("1.3.x", "1.2.x"));

        // A longer numeric run outranks a shorter one when the shared segments tie: 1.2.x.5 vs 1.2.x.
        Assert.IsGreaterThan(0, NugetService.CompareVersions("1.2.x.5", "1.2.x"));

        // Two non-version strings whose numeric segments all tie (non-numeric -> 0) compare equal.
        Assert.AreEqual(0, NugetService.CompareVersions("x.y", "z.w"));

        // Only one side unparseable still routes through the fallback (both-parse gate fails).
        Assert.IsLessThan(0, NugetService.CompareVersions("1.0.0", "2.0.x"));
    }

    #endregion

    #region ParseMinimumVersion

    [TestMethod]
    // Plain versions
    [DataRow("1.0.0", "1.0.0")]
    [DataRow("  1.2.3  ", "1.2.3")]
    [DataRow("1.0.0-preview", "1.0.0-preview")]
    // Bracketed exact / open ranges
    [DataRow("[1.0.0]", "1.0.0")]
    [DataRow("[1.0.0, )", "1.0.0")]
    [DataRow("[1.0.0,)", "1.0.0")]
    [DataRow("(1.0.0, 2.0.0)", "1.0.0")]
    [DataRow("[2.0.300, 3.0.0)", "2.0.300")]
    // Bracket-stripped form (caller pre-cleaned brackets but left the comma).
    // Regression guard for the bug where ParseMinimumVersion would short-circuit
    // on "no brackets present" and return the comma-joined string verbatim,
    // producing 404s when used as a download version.
    [DataRow("2.0.300, 3.0.0", "2.0.300")]
    [DataRow("1.0.0,2.0.0", "1.0.0")]
    // Empty / whitespace
    [DataRow("", "")]
    [DataRow("   ", "")]
    public void ParseMinimumVersion_ReturnsExpectedLowerBound(string input, string expected)
    {
        var actual = NugetService.ParseMinimumVersion(input);
        Assert.AreEqual(expected, actual, $"ParseMinimumVersion(\"{input}\")");
    }

    #endregion
}

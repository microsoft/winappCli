// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Net;
using System.Net.Sockets;
using System.Text;
using NuGet.Versioning;
using WinApp.Cli.Services;
using static WinApp.Cli.Tests.NugetFeedTestHelpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// NuGet.Client-backed download/install/authentication tests: real download + extract from local folder
/// feeds (including transitive dependencies), multi-source download failover, and authenticated private
/// feeds via an in-process HTTP Basic-auth feed (<see cref="BasicAuthNuGetFeed"/>) — including resolving the
/// latest version against a feed that exposes only <c>PackageBaseAddress</c> (no registration resource).
/// Source/configuration and version-selection behavior lives in <see cref="NugetServiceFeedTests"/>; the
/// shared feed-authoring helpers live in <see cref="NugetFeedTestHelpers"/> (imported via <c>using static</c>).
/// </summary>
[TestClass]
public class NugetServiceDownloadTests : BaseCommandTests
{
    #region Download / install Tests

    [TestMethod]
    public async Task InstallPackageAsync_DependencyHasExclusiveLowerBound_InstallsLowestSatisfyingVersion()
    {
        // NUGET_PACKAGES takes precedence over the globalPackagesFolder written by WriteLocalFeedConfig.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NUGET_PACKAGES")))
        {
            Assert.Inconclusive("NUGET_PACKAGES is set in the environment; it overrides the config's globalPackagesFolder, so the local feed would not be exercised.");
        }

        var root = CreateFeedTestDirectory();
        try
        {
            var feed = new DirectoryInfo(Path.Join(root.FullName, "feed"));
            feed.Create();
            var packages = new DirectoryInfo(Path.Join(root.FullName, "packages"));

            // Root's .nuspec declares its dependency with an EXCLUSIVE lower bound (1.0.0, 2.0.0]. Installing
            // the lower bound 1.0.0 would be wrong (the range excludes it); the install must resolve and
            // extract the lowest listed satisfying version, 1.5.0.
            WriteNupkgToFeed(feed, "Install.Root", "1.0.0", ("Install.Child", "(1.0.0, 2.0.0]"));
            WriteNupkgToFeed(feed, "Install.Child", "1.0.0");
            WriteNupkgToFeed(feed, "Install.Child", "1.5.0");
            WriteNupkgToFeed(feed, "Install.Child", "2.0.0");

            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            var installed = await service.InstallPackageAsync("Install.Root", "1.0.0", TestTaskContext, TestContext.CancellationToken);

            Assert.IsTrue(installed.TryGetValue("Install.Child", out var childVersion), "The transitive dependency must be resolved and installed.");
            Assert.AreEqual("1.5.0", childVersion, "The exclusive lower bound (1.0.0) must be excluded; the lowest satisfying version (1.5.0) is installed.");
            Assert.IsTrue(service.GetNuGetPackageDir("Install.Child", "1.5.0").Exists, "The resolved dependency version must be extracted on disk.");
            Assert.IsFalse(service.GetNuGetPackageDir("Install.Child", "1.0.0").Exists, "The excluded lower-bound version must NOT be installed.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task InstallPackageAsync_RequiredTransitiveDependencyUnresolvable_FailsInsteadOfReportingSuccess()
    {
        // NUGET_PACKAGES takes precedence over the globalPackagesFolder written by WriteLocalFeedConfig.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NUGET_PACKAGES")))
        {
            Assert.Inconclusive("NUGET_PACKAGES is set in the environment; it overrides the config's globalPackagesFolder, so the local feed would not be exercised.");
        }

        var root = CreateFeedTestDirectory();
        try
        {
            var feed = new DirectoryInfo(Path.Join(root.FullName, "feed"));
            feed.Create();
            var packages = new DirectoryInfo(Path.Join(root.FullName, "packages"));

            // Root's .nuspec requires Child [5.0.0, ), but the feed only carries Child 1.0.0. The root package
            // itself downloads fine, but a REQUIRED transitive dependency cannot be resolved. The install must
            // FAIL (throw) rather than return the root as a success with the child missing — otherwise
            // `restore` reports an incomplete installation as complete.
            WriteNupkgToFeed(feed, "Install.Root", "1.0.0", ("Install.Child", "[5.0.0, )"));
            WriteNupkgToFeed(feed, "Install.Child", "1.0.0");

            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await service.InstallPackageAsync("Install.Root", "1.0.0", TestTaskContext, TestContext.CancellationToken));

            // The failure must name the unresolvable dependency (surfacing the gap), proving the incomplete
            // install was reported as an error rather than a silent partial success.
            StringAssert.Contains(ex.Message, "Install.Child", StringComparison.Ordinal);

            // The root package WAS still downloaded best-effort before the dependency gap failed the operation.
            Assert.IsTrue(service.GetNuGetPackageDir("Install.Root", "1.0.0").Exists,
                "The root package should have been downloaded before the missing dependency failed the install.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task InstallPackageAsync_LocalFeed_InstallsPackageAndNormalizedTransitiveDependency()
    {
        // NUGET_PACKAGES takes precedence over the globalPackagesFolder written by WriteLocalFeedConfig.
        // If it is set, the install targets the shared cache and could pass on a pre-populated cache
        // without ever exercising the local feed, so skip to keep the assertion meaningful.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NUGET_PACKAGES")))
        {
            Assert.Inconclusive("NUGET_PACKAGES is set in the environment; it overrides the config's globalPackagesFolder, so the local feed would not be exercised.");
        }

        var root = CreateFeedTestDirectory();
        try
        {
            var feed = new DirectoryInfo(Path.Join(root.FullName, "feed"));
            feed.Create();
            var packages = new DirectoryInfo(Path.Join(root.FullName, "packages"));

            // Package A depends on Package B; both live only in the local feed (no network).
            WriteNupkgToFeed(feed, "Winapp.TestA", "1.0.0", ("Winapp.TestB", "1.0.0"));
            WriteNupkgToFeed(feed, "Winapp.TestB", "1.0.0");

            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            // Request with a "1.0" shorthand to also exercise version normalization end-to-end.
            var installed = await service.InstallPackageAsync("Winapp.TestA", "1.0", TestTaskContext, TestContext.CancellationToken);

            // The package and its transitive dependency are both recorded with canonical (normalized) versions.
            Assert.IsTrue(installed.ContainsKey("Winapp.TestA"), "Main package should be recorded as installed.");
            Assert.AreEqual("1.0.0", installed["Winapp.TestA"], "Main package version should be normalized.");
            Assert.IsTrue(installed.ContainsKey("Winapp.TestB"), "Transitive dependency should be resolved and installed.");
            Assert.AreEqual("1.0.0", installed["Winapp.TestB"], "Dependency version should be normalized.");

            // Both are extracted into the configured global packages folder using the normalized layout.
            Assert.IsTrue(service.GetNuGetPackageDir("Winapp.TestA", "1.0.0").Exists, "Main package should be extracted on disk.");
            Assert.IsTrue(service.GetNuGetPackageDir("Winapp.TestB", "1.0.0").Exists, "Dependency should be extracted on disk.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task InstallPackageAsync_PackageMissingFromFeed_ThrowsActionableError()
    {
        // NUGET_PACKAGES overrides the config's globalPackagesFolder; skip when it is set so the install
        // is exercised against the isolated local feed rather than the shared cache.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NUGET_PACKAGES")))
        {
            Assert.Inconclusive("NUGET_PACKAGES is set in the environment; it overrides the config's globalPackagesFolder, so the local feed would not be exercised.");
        }

        var root = CreateFeedTestDirectory();
        try
        {
            var feed = new DirectoryInfo(Path.Join(root.FullName, "feed"));
            feed.Create();
            var packages = new DirectoryInfo(Path.Join(root.FullName, "packages"));

            // The feed exists but does not contain the requested package: the content download fails on
            // every eligible source, which must surface as an actionable error rather than silently succeeding.
            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await service.InstallPackageAsync("Winapp.DoesNotExist", "1.0.0", TestTaskContext, TestContext.CancellationToken));

            StringAssert.Contains(ex.Message, "Winapp.DoesNotExist", StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task InstallPackageAsync_FirstSourcesUnusable_FailsOverToSourceWithPackage()
    {
        // NUGET_PACKAGES overrides the config's globalPackagesFolder; skip when it is set so the install
        // is exercised against the isolated local feeds rather than the shared cache.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NUGET_PACKAGES")))
        {
            Assert.Inconclusive("NUGET_PACKAGES is set in the environment; it overrides the config's globalPackagesFolder, so the local feeds would not be exercised.");
        }

        var root = CreateFeedTestDirectory();
        try
        {
            // Three eligible sources (all mapped to '*'), queried in listed order:
            //   1. "broken" - an unreachable HTTPS feed whose service index cannot be loaded, so acquiring
            //                 FindPackageByIdResource throws FatalProtocolException (resource-acquisition
            //                 failover).
            //   2. "empty"  - a real local folder feed that does NOT contain the package, so
            //                 CopyNupkgToStreamAsync returns false (CopyNupkg failover).
            //   3. "good"   - a local folder feed that DOES contain the package.
            // A downloader that stopped at the first failing source (instead of continuing the loop) would
            // throw; a successful install proves it fails over past both an unreachable source and a
            // missing one and installs from the third. The broken source uses the reserved '.invalid' TLD
            // (RFC 6761), which never resolves, keeping the test deterministic and offline.
            var empty = new DirectoryInfo(Path.Join(root.FullName, "empty"));
            empty.Create();
            var good = new DirectoryInfo(Path.Join(root.FullName, "good"));
            good.Create();
            var packages = new DirectoryInfo(Path.Join(root.FullName, "packages"));

            WriteNupkgToFeed(good, "Failover.Pkg", "1.0.0");

            WriteNuGetConfig(root, $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <config>
                    <add key="globalPackagesFolder" value="{packages.FullName}" />
                  </config>
                  <packageSources>
                    <clear />
                    <add key="broken" value="https://nuget.invalid/v3/index.json" />
                    <add key="empty" value="{empty.FullName}" />
                    <add key="good" value="{good.FullName}" />
                  </packageSources>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="broken">
                      <package pattern="*" />
                    </packageSource>
                    <packageSource key="empty">
                      <package pattern="*" />
                    </packageSource>
                    <packageSource key="good">
                      <package pattern="*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            var installed = await service.InstallPackageAsync("Failover.Pkg", "1.0.0", TestTaskContext, TestContext.CancellationToken);

            Assert.IsTrue(installed.ContainsKey("Failover.Pkg"), "The package should install after failing over past the unreachable and empty sources.");
            Assert.AreEqual("1.0.0", installed["Failover.Pkg"], "The installed version should match the one served by the good source.");
            Assert.IsTrue(service.GetNuGetPackageDir("Failover.Pkg", "1.0.0").Exists, "The package should be extracted from the failover source on disk.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    #endregion

    #region Authenticated (private) feed Tests

    [TestMethod]
    public async Task InstallPackageAsync_AuthenticatedFeed_WithConfiguredCredentials_InstallsPackage()
    {
        // NUGET_PACKAGES takes precedence over the config's globalPackagesFolder; if it is set the install
        // would target the shared cache and could pass on a pre-populated cache without ever contacting
        // the authenticated feed, so skip to keep the assertion meaningful.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NUGET_PACKAGES")))
        {
            Assert.Inconclusive("NUGET_PACKAGES is set; it overrides the config's globalPackagesFolder, so the authenticated feed would not be exercised.");
        }

        // Set up the (non-interactive in a test host) credential service exactly as production does.
        NugetSourceProvider.EnsureCredentialService();

        using var feed = new BasicAuthNuGetFeed("winapp-user", "s3cret-token!", ("Auth.Pkg", "1.0.0"));
        var root = CreateFeedTestDirectory();
        try
        {
            var packages = new DirectoryInfo(Path.Join(root.FullName, "packages"));

            // The private HTTP feed rejects anonymous requests with 401; the matching credentials live in
            // <packageSourceCredentials>, which is exactly how a user authenticates a company mirror. A
            // regression in credential loading (or in how NugetSourceProvider builds the source) would
            // surface here as a 401 failure instead of a successful install.
            WriteNuGetConfig(root, $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <config>
                    <add key="globalPackagesFolder" value="{packages.FullName}" />
                  </config>
                  <packageSources>
                    <clear />
                    <add key="private" value="{feed.IndexUrl}" allowInsecureConnections="true" />
                  </packageSources>
                  <packageSourceCredentials>
                    <private>
                      <add key="Username" value="{feed.Username}" />
                      <add key="ClearTextPassword" value="{feed.Password}" />
                    </private>
                  </packageSourceCredentials>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="private">
                      <package pattern="*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            var installed = await service.InstallPackageAsync("Auth.Pkg", "1.0.0", TestTaskContext, TestContext.CancellationToken);

            Assert.IsTrue(installed.ContainsKey("Auth.Pkg"), "The package served by the authenticated feed should install.");
            Assert.AreEqual("1.0.0", installed["Auth.Pkg"], "The installed version should match the one served by the feed.");
            Assert.IsTrue(service.GetNuGetPackageDir("Auth.Pkg", "1.0.0").Exists, "The package should be extracted from the authenticated feed on disk.");
            Assert.IsTrue(feed.ReceivedAuthenticatedRequest, "The feed should have served at least one request carrying the configured credentials.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task InstallPackageAsync_AuthenticatedFeed_WithoutCredentials_FailsNonInteractively()
    {
        NugetSourceProvider.EnsureCredentialService();

        using var feed = new BasicAuthNuGetFeed("winapp-user", "s3cret-token!", ("Auth.Pkg", "1.0.0"));
        var root = CreateFeedTestDirectory();
        try
        {
            var packages = new DirectoryInfo(Path.Join(root.FullName, "packages"));

            // Same private feed, but NO <packageSourceCredentials>. The feed answers 401 and, because the
            // credential service is non-interactive (redirected input / CI), NuGet cannot prompt — the
            // install must fail deterministically (surfacing the source failure) rather than hang waiting
            // for console input.
            WriteNuGetConfig(root, $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <config>
                    <add key="globalPackagesFolder" value="{packages.FullName}" />
                  </config>
                  <packageSources>
                    <clear />
                    <add key="private" value="{feed.IndexUrl}" allowInsecureConnections="true" />
                  </packageSources>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="private">
                      <package pattern="*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            // Bound the wait so a hypothetical interactive hang fails the test instead of blocking the run.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(60));

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await service.InstallPackageAsync("Auth.Pkg", "1.0.0", TestTaskContext, cts.Token));

            // The failure must reference the package and the unauthorized source, proving the anonymous
            // request reached the feed and was rejected (not masked as a plain "package not found").
            StringAssert.Contains(ex.Message, "Auth.Pkg", StringComparison.Ordinal);
            StringAssert.Contains(ex.Message, "private", StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_FlatContainerOnlyFeed_ResolvesViaFindPackageById()
    {
        // A private v3 feed can advertise ONLY a PackageBaseAddress (flat container) resource and no
        // registration resource — BasicAuthNuGetFeed is exactly that shape (its service index lists only
        // PackageBaseAddress/3.0.0). Such a feed can still restore packages, so latest-version resolution
        // must work against it too: with no PackageMetadataResource available, NugetService falls back to
        // FindPackageByIdResource.GetAllVersionsAsync (the flat container) instead of skipping the source and
        // reporting "no versions found". This does not download, so no NUGET_PACKAGES guard is needed.
        NugetSourceProvider.EnsureCredentialService();

        // Three versions of one package; the highest stable (2.0.0) must be selected as "latest".
        using var feed = new BasicAuthNuGetFeed(
            "winapp-user",
            "s3cret-token!",
            ("Flat.Pkg", "1.0.0"),
            ("Flat.Pkg", "2.0.0"),
            ("Flat.Pkg", "1.5.0"));
        var root = CreateFeedTestDirectory();
        try
        {
            WriteNuGetConfig(root, $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="private" value="{feed.IndexUrl}" allowInsecureConnections="true" />
                  </packageSources>
                  <packageSourceCredentials>
                    <private>
                      <add key="Username" value="{feed.Username}" />
                      <add key="ClearTextPassword" value="{feed.Password}" />
                    </private>
                  </packageSourceCredentials>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="private">
                      <package pattern="*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            var latest = await service.GetLatestVersionAsync("Flat.Pkg", SdkInstallMode.Stable, TestContext.CancellationToken);

            Assert.AreEqual("2.0.0", latest, "Latest resolution against a PackageBaseAddress-only feed must enumerate versions via the flat container and pick the highest.");
            Assert.IsTrue(feed.ReceivedAuthenticatedRequest, "The flat-container feed should have served the versions request carrying the configured credentials.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_RegistrationVersionedOnlyFeed_ExcludesUnlistedFromLatest()
    {
        // A private v3 feed can advertise its registration resource under ONLY the
        // RegistrationsBaseUrl/Versioned service type (the FIRST entry in NuGet's
        // ServiceTypes.RegistrationsBaseUrl). Such a feed IS registration-backed, so "latest" resolution must
        // go through the registration/metadata resource and EXCLUDE unlisted versions — not fall back to the
        // flat container (which carries no listed flag and would pick the unlisted 2.0.0 as latest). This
        // guards against classifying a Versioned-only registration feed as flat-container-only.
        NugetSourceProvider.EnsureCredentialService();

        // 2.0.0 is UNLISTED; 1.0.0 is listed. The registration path must pick 1.0.0 as latest.
        using var feed = new BasicAuthNuGetFeed(
            "winapp-user",
            "s3cret-token!",
            advertiseRegistration: true,
            ("Reg.Pkg", "1.0.0", true),
            ("Reg.Pkg", "2.0.0", false));
        var root = CreateFeedTestDirectory();
        try
        {
            WriteNuGetConfig(root, $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="private" value="{feed.IndexUrl}" allowInsecureConnections="true" />
                  </packageSources>
                  <disabledPackageSources>
                    <clear />
                  </disabledPackageSources>
                  <packageSourceCredentials>
                    <private>
                      <add key="Username" value="{feed.Username}" />
                      <add key="ClearTextPassword" value="{feed.Password}" />
                    </private>
                  </packageSourceCredentials>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="private">
                      <package pattern="*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            var latest = await service.GetLatestVersionAsync("Reg.Pkg", SdkInstallMode.Stable, TestContext.CancellationToken);

            Assert.AreEqual("1.0.0", latest, "A RegistrationsBaseUrl/Versioned feed must resolve latest via the registration/metadata resource and exclude the unlisted 2.0.0 — not fall back to the flat container.");
            Assert.IsTrue(feed.ReceivedAuthenticatedRequest, "The registration feed should have served the request carrying the configured credentials.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_PlainHttpSourceWithoutOptIn_IsRejected()
    {
        // SDK packages are executable tools, so a plain-HTTP feed is a code-substitution vector. NuGet's
        // low-level protocol APIs don't enforce restore's insecure-source policy, so NugetSourceProvider
        // must: an http:// source is refused unless it explicitly opts in with allowInsecureConnections.
        // This is the same feed shape as the flat-container test, but WITHOUT the attribute — it must be
        // rejected and never contacted (proving the opt-in in the other tests is what makes them work).
        NugetSourceProvider.EnsureCredentialService();

        using var feed = new BasicAuthNuGetFeed("winapp-user", "s3cret-token!", ("Flat.Pkg", "1.0.0"));
        var root = CreateFeedTestDirectory();
        try
        {
            WriteNuGetConfig(root, $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="private" value="{feed.IndexUrl}" />
                  </packageSources>
                  <disabledPackageSources>
                    <clear />
                  </disabledPackageSources>
                  <packageSourceCredentials>
                    <private>
                      <add key="Username" value="{feed.Username}" />
                      <add key="ClearTextPassword" value="{feed.Password}" />
                    </private>
                  </packageSourceCredentials>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="private">
                      <package pattern="*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await service.GetLatestVersionAsync("Flat.Pkg", SdkInstallMode.Stable, TestContext.CancellationToken));

            // The rejection must name the insecure-HTTP reason (not a generic "package not found"), and the
            // feed must never have been contacted.
            StringAssert.Contains(ex.Message, "HTTP", StringComparison.OrdinalIgnoreCase);
            Assert.IsFalse(feed.ReceivedAuthenticatedRequest, "An insecure HTTP source without allowInsecureConnections must be rejected before any request is sent.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_ExactPinnedDependencyIsUnlisted_StillResolvesFromRegistrationFeed()
    {
        // Regression (a deterministic replacement for a test that used to depend on a specific nuget.org
        // experimental version staying unlisted): a package can pin an EXACT dependency version whose publisher
        // has UNLISTED it — the Windows App SDK experimental meta-packages pin their .Runtime/.Foundation
        // sub-packages to exact, unlisted versions. Resolving a declared dependency range must therefore
        // consider unlisted versions (unlike a "latest version" decision, which excludes them on purpose);
        // otherwise the pinned dependency is silently dropped and, downstream, the Windows App Runtime
        // PackageDependency is never injected into the packaged manifest. Served from an in-process registration
        // feed that honors each version's listed flag, so the behavior is deterministic and offline.
        NugetSourceProvider.EnsureCredentialService();

        // Dep.Pkg 9.9.9 is UNLISTED; Dep.Pkg 1.0.0 is listed. Root.Pkg 1.0.0 pins Dep.Pkg to EXACTLY [9.9.9]
        // (the unlisted version). "Latest" for Dep.Pkg must exclude 9.9.9 (returns 1.0.0), while the exact pin
        // in Root.Pkg's nuspec must still resolve to the unlisted 9.9.9.
        using var feed = new BasicAuthNuGetFeed(
            "winapp-user",
            "s3cret-token!",
            advertiseRegistration: true,
            ("Dep.Pkg", "1.0.0", true, Array.Empty<(string Id, string Version)>()),
            ("Dep.Pkg", "9.9.9", false, Array.Empty<(string Id, string Version)>()),
            ("Root.Pkg", "1.0.0", true, [("Dep.Pkg", "[9.9.9]")]));
        var root = CreateFeedTestDirectory();
        try
        {
            WriteNuGetConfig(root, $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="private" value="{feed.IndexUrl}" allowInsecureConnections="true" />
                  </packageSources>
                  <disabledPackageSources>
                    <clear />
                  </disabledPackageSources>
                  <packageSourceCredentials>
                    <private>
                      <add key="Username" value="{feed.Username}" />
                      <add key="ClearTextPassword" value="{feed.Password}" />
                    </private>
                  </packageSourceCredentials>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="private">
                      <package pattern="*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            // Premise: 9.9.9 really is treated as unlisted — "latest" excludes it and returns the listed 1.0.0.
            // (If range resolution used the same listed-only filter, the exact pin below would fail to resolve.)
            var latestDep = await service.GetLatestVersionAsync("Dep.Pkg", SdkInstallMode.Stable, TestContext.CancellationToken);
            Assert.AreEqual("1.0.0", latestDep, "The unlisted 9.9.9 must be excluded from 'latest'; only the listed 1.0.0 remains.");

            // The exact pin to the unlisted 9.9.9 must still resolve when building the dependency graph.
            var deps = await service.GetPackageDependenciesAsync("Root.Pkg", "1.0.0", TestContext.CancellationToken);

            Assert.IsTrue(deps.TryGetValue("Dep.Pkg", out var resolved),
                "The exact-pinned dependency must be resolved, not silently skipped because it is unlisted.");
            Assert.AreEqual("9.9.9", resolved, "The unlisted, exactly-pinned dependency version must be selected.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// A minimal in-process NuGet v3 feed that requires HTTP Basic authentication. By default it advertises
    /// only a flat container (<c>PackageBaseAddress/3.0.0</c>) and serves what
    /// <see cref="NugetPackageDownloader"/> needs to download a leaf package — the service index, the
    /// flat-container versions list, and the .nupkg content. When constructed with
    /// <c>advertiseRegistration: true</c> it ALSO advertises a <c>RegistrationsBaseUrl/Versioned</c> resource
    /// and serves a registration index that honors each version's listed flag, so a test can verify that a feed
    /// whose only registration service type is <c>.../Versioned</c> is treated as registration-backed (and its
    /// unlisted versions excluded from "latest"). Any unauthenticated request is answered with <c>401</c> +
    /// <c>WWW-Authenticate: Basic</c> so the standard 401→retry-with-credentials flow is exercised. Bound to
    /// <c>127.0.0.1</c> on an ephemeral port; no admin URL ACL is required.
    /// </summary>
    private sealed class BasicAuthNuGetFeed : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serveLoop;
        private readonly string _expectedAuthorization;
        private readonly Dictionary<string, byte[]> _nupkgsByPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string[]> _versionsById = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _listedByPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly bool _advertiseRegistration;

        public string Username { get; }

        public string Password { get; }

        public string BaseUrl { get; }

        public string IndexUrl => BaseUrl + "v3/index.json";

        // Set once the feed serves a request that carried the expected Basic credentials, so a test can
        // prove authentication actually happened rather than inferring it from a successful install.
        public bool ReceivedAuthenticatedRequest { get; private set; }

        public BasicAuthNuGetFeed(string username, string password, params (string Id, string Version)[] packages)
            : this(username, password, advertiseRegistration: false, [.. packages.Select(p => (p.Id, p.Version, Listed: true))])
        {
        }

        // Extended shape: optionally advertise a RegistrationsBaseUrl/Versioned resource and honor a per-version
        // listed flag, so a test can exercise a registration-backed feed whose ONLY registration service type is
        // ".../Versioned" (the first entry in NuGet's ServiceTypes.RegistrationsBaseUrl) and verify unlisted
        // versions are excluded from "latest" resolution.
        public BasicAuthNuGetFeed(string username, string password, bool advertiseRegistration, params (string Id, string Version, bool Listed)[] packages)
            : this(username, password, advertiseRegistration, [.. packages.Select(p => (p.Id, p.Version, p.Listed, Dependencies: Array.Empty<(string Id, string Version)>()))])
        {
        }

        // Richest shape: additionally bake a dependency group into each package's .nupkg, so a test can serve a
        // root package that pins an exact (possibly unlisted) dependency version and assert the dependency-graph
        // resolution picks it up. Dependencies are read back from the flat-container nuspec by
        // FindPackageByIdResource.GetDependencyInfoAsync, exactly as a real feed serves them.
        public BasicAuthNuGetFeed(string username, string password, bool advertiseRegistration, params (string Id, string Version, bool Listed, (string Id, string Version)[] Dependencies)[] packages)
        {
            Username = username;
            Password = password;
            _advertiseRegistration = advertiseRegistration;
            _expectedAuthorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));

            foreach (var (id, version, listed, dependencies) in packages)
            {
                var lowerId = id.ToLowerInvariant();
                var lowerVersion = version.ToLowerInvariant();
                _nupkgsByPath[$"{lowerId}/{lowerVersion}"] = BuildNupkgBytes(id, version, dependencies);
                _versionsById[lowerId] = _versionsById.TryGetValue(lowerId, out var existing)
                    ? [.. existing, version]
                    : [version];
                _listedByPath[$"{lowerId}/{lowerVersion}"] = listed;
            }

            (_listener, BaseUrl) = StartListener();
            _serveLoop = Task.Run(() => ServeAsync(_cts.Token));
        }

        private static (HttpListener Listener, string BaseUrl) StartListener()
        {
            for (var attempt = 0; ; attempt++)
            {
                // Reserve an ephemeral loopback port, then hand it to HttpListener. The brief gap between
                // closing the probe socket and binding the listener is a benign test-only race; retry a
                // few times if the port is taken in between.
                using var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();

                var baseUrl = $"http://127.0.0.1:{port}/";
                var listener = new HttpListener();
                listener.Prefixes.Add(baseUrl);
                try
                {
                    listener.Start();
                    return (listener, baseUrl);
                }
                catch (HttpListenerException) when (attempt < 4)
                {
                    listener.Close();
                }
            }
        }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException)
                {
                    // Dispose() stops and closes the listener, which is how this loop is terminated; any of
                    // these means the listener is gone, so exit rather than spin. Anything else is a real
                    // defect in the fake feed and is allowed to surface.
                    break;
                }

                try
                {
                    Handle(context);
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException)
                {
                    try
                    {
                        context.Response.Abort();
                    }
                    catch (Exception abortEx) when (abortEx is HttpListenerException or ObjectDisposedException)
                    {
                        // Best-effort; the client may have already disconnected.
                    }
                }
            }
        }

        private void Handle(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            if (!string.Equals(request.Headers["Authorization"], _expectedAuthorization, StringComparison.Ordinal))
            {
                response.StatusCode = 401;
                response.AddHeader("WWW-Authenticate", "Basic realm=\"winapp-tests\"");
                response.Close();
                return;
            }

            ReceivedAuthenticatedRequest = true;

            var path = request.Url!.AbsolutePath.TrimStart('/');
            var (body, contentType) = Resolve(path);
            if (body is null)
            {
                response.StatusCode = 404;
                response.Close();
                return;
            }

            response.StatusCode = 200;
            response.ContentType = contentType;
            response.ContentLength64 = body.Length;
            response.OutputStream.Write(body, 0, body.Length);
            response.Close();
        }

        private (byte[]? Body, string ContentType) Resolve(string path)
        {
            if (path == "v3/index.json")
            {
                var resources = new List<string>
                {
                    $$"""{"@id":"{{BaseUrl}}flat/","@type":"PackageBaseAddress/3.0.0"}""",
                };

                // Optionally advertise a registration resource whose ONLY service type is
                // RegistrationsBaseUrl/Versioned — the first entry in NuGet's ServiceTypes.RegistrationsBaseUrl
                // and the one a hand-maintained subset is most likely to omit.
                if (_advertiseRegistration)
                {
                    resources.Add($$"""{"@id":"{{BaseUrl}}reg/","@type":"RegistrationsBaseUrl/Versioned"}""");
                }

                var json = $$"""{"version":"3.0.0","resources":[{{string.Join(",", resources)}}]}""";
                return (Encoding.UTF8.GetBytes(json), "application/json");
            }

            if (_advertiseRegistration
                && path.StartsWith("reg/", StringComparison.Ordinal)
                && path.EndsWith("/index.json", StringComparison.Ordinal))
            {
                var regId = path["reg/".Length..^"/index.json".Length];
                if (_versionsById.TryGetValue(regId, out var regVersions))
                {
                    // Serve a single inline registration page (so no separate page fetch is needed) with one
                    // leaf per version, each carrying the listed flag the registration API exposes.
                    var ordered = regVersions.OrderBy(NuGetVersion.Parse).ToArray();
                    var leaves = ordered.Select(v =>
                    {
                        var listed = !_listedByPath.TryGetValue($"{regId}/{v.ToLowerInvariant()}", out var l) || l;
                        // NuGet treats a registration entry as unlisted when its published year is <= 1900 (the
                        // canonical sentinel); set the explicit "listed" flag too so the filter fires regardless
                        // of which signal the client honors.
                        var published = listed ? "2020-01-01T00:00:00+00:00" : "1900-01-01T00:00:00+00:00";
                        // $$$ raw string (triple-brace interpolation) so the two trailing literal '}}' that close
                        // catalogEntry + leaf are unambiguous.
                        return $$$"""
                            {"@id":"{{{BaseUrl}}}reg/{{{regId}}}/{{{v}}}.json","packageContent":"{{{BaseUrl}}}flat/{{{regId}}}/{{{v}}}/{{{regId}}}.{{{v}}}.nupkg","catalogEntry":{"@id":"{{{BaseUrl}}}catalog/{{{regId}}}/{{{v}}}.json","id":"{{{regId}}}","version":"{{{v}}}","listed":{{{(listed ? "true" : "false")}}},"published":"{{{published}}}"}}
                            """;
                    });
                    var json = $$"""{"count":1,"items":[{"@id":"{{BaseUrl}}reg/{{regId}}/page.json","count":{{ordered.Length}},"lower":"{{ordered[0]}}","upper":"{{ordered[^1]}}","items":[{{string.Join(",", leaves)}}]}]}""";
                    return (Encoding.UTF8.GetBytes(json), "application/json");
                }
            }

            if (!path.StartsWith("flat/", StringComparison.Ordinal))
            {
                return (null, "application/json");
            }

            var rest = path["flat/".Length..];
            if (rest.EndsWith("/index.json", StringComparison.Ordinal))
            {
                var id = rest[..^"/index.json".Length];
                if (_versionsById.TryGetValue(id, out var versions))
                {
                    var json = "{\"versions\":[" + string.Join(",", versions.Select(v => $"\"{v}\"")) + "]}";
                    return (Encoding.UTF8.GetBytes(json), "application/json");
                }
            }
            else if (rest.EndsWith(".nupkg", StringComparison.Ordinal))
            {
                // flat/{id}/{version}/{id}.{version}.nupkg
                var parts = rest.Split('/');
                if (parts.Length == 3 && _nupkgsByPath.TryGetValue($"{parts[0]}/{parts[1]}", out var bytes))
                {
                    return (bytes, "application/octet-stream");
                }
            }

            return (null, "application/json");
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                // Best-effort teardown.
            }

            try
            {
                _serveLoop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is AggregateException or OperationCanceledException)
            {
                // The loop observes cancellation/disposal; ignore any teardown race.
            }

            _cts.Dispose();
        }
    }

    #endregion
}

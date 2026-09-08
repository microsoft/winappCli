// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;
using WinApp.Cli.Services;
using static WinApp.Cli.Tests.NugetFeedTestHelpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// NuGet.Client-backed tests that close the remaining install/resolve branches of <see cref="NugetService"/>
/// (and its <c>.Dependencies</c> partial) not already covered by the focused feed/download/dependency suites:
/// the "already fully cached" short-circuit, a corrupt/renamed cached .nuspec, SDK-channel (preview /
/// experimental) latest-version filtering and its no-match errors, a version-less dependency, cancellation
/// while resolving a dependency, an all-sources protocol failure while reading a dependency graph, and the
/// "no eligible sources" diagnosis. All run against isolated local folder feeds or hand-authored cache
/// entries — no network. Shared feed-authoring helpers live in <see cref="NugetFeedTestHelpers"/>.
/// </summary>
[TestClass]
public class NugetServiceCoverageTests : BaseCommandTests
{
    private static bool NugetPackagesEnvOverridesConfig =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NUGET_PACKAGES"));

    /// <summary>
    /// Writes a nuget.config whose only source is the given local folder feed and whose globalPackagesFolder is
    /// the given cache dir, without a feed-specific <c>&lt;packageSourceMapping&gt;</c> pattern list beyond "*".
    /// (Same shape as <see cref="NugetFeedTestHelpers.WriteLocalFeedConfig"/>, re-declared here only so tests
    /// that also hand-author cache entries can share one root.)
    /// </summary>
    private static (DirectoryInfo Feed, DirectoryInfo Packages) SetUpLocalFeed(DirectoryInfo root)
    {
        var feed = new DirectoryInfo(Path.Join(root.FullName, "feed"));
        feed.Create();
        var packages = new DirectoryInfo(Path.Join(root.FullName, "packages"));
        WriteLocalFeedConfig(root, feed, packages);
        return (feed, packages);
    }

    [TestMethod]
    public async Task InstallPackageAsync_AlreadyFullyCached_SkipsRedownloadButStillResolvesDependencies()
    {
        if (NugetPackagesEnvOverridesConfig)
        {
            Assert.Inconclusive("NUGET_PACKAGES is set; it overrides the config's globalPackagesFolder, so the local feed would not be exercised.");
        }

        var root = CreateFeedTestDirectory();
        try
        {
            var (feed, _) = SetUpLocalFeed(root);
            WriteNupkgToFeed(feed, "Cached.Root", "1.0.0", ("Cached.Child", "1.0.0"));
            WriteNupkgToFeed(feed, "Cached.Child", "1.0.0");

            var service = CreateServiceRootedAt(root);

            // First install populates the cache (writes the completion markers).
            var first = await service.InstallPackageAsync("Cached.Root", "1.0.0", TestTaskContext, TestContext.CancellationToken);
            Assert.IsTrue(first.ContainsKey("Cached.Root"));
            Assert.IsTrue(first.ContainsKey("Cached.Child"));

            // Second install must hit the completion-marker short-circuit for BOTH the root and the child
            // (no re-download), yet still walk the extracted .nuspec so the returned map is complete.
            var second = await service.InstallPackageAsync("Cached.Root", "1.0.0", TestTaskContext, TestContext.CancellationToken);

            Assert.AreEqual("1.0.0", second["Cached.Root"], "The already-cached root must still be reported installed.");
            Assert.AreEqual("1.0.0", second["Cached.Child"], "The already-cached dependency must still be resolved from the extracted nuspec.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task InstallPackageAsync_CachedPackageMissingNuspec_FailsWithManifestError()
    {
        if (NugetPackagesEnvOverridesConfig)
        {
            Assert.Inconclusive("NUGET_PACKAGES is set; it overrides the config's globalPackagesFolder, so the hand-authored cache entry would not be used.");
        }

        var root = CreateFeedTestDirectory();
        try
        {
            SetUpLocalFeed(root);
            var service = CreateServiceRootedAt(root);

            // Hand-author a CORRUPT cache entry: the completion marker is present (so the install trusts the
            // directory and skips the download) but the .nuspec was never written. Reading the dependency graph
            // must fail loudly rather than treat the missing manifest as "no dependencies".
            var packageDir = service.GetNuGetPackageDir("Corrupt.Pkg", "1.0.0");
            packageDir.Create();
            File.WriteAllText(Path.Join(packageDir.FullName, ".nupkg.metadata"), "{}");

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await service.InstallPackageAsync("Corrupt.Pkg", "1.0.0", TestTaskContext, TestContext.CancellationToken));

            StringAssert.Contains(ex.Message, "dependency", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task InstallPackageAsync_CachedPackageWithDifferentlyNamedNuspec_ResolvesViaFallbackSearch()
    {
        if (NugetPackagesEnvOverridesConfig)
        {
            Assert.Inconclusive("NUGET_PACKAGES is set; it overrides the config's globalPackagesFolder, so the hand-authored cache entry would not be used.");
        }

        var root = CreateFeedTestDirectory();
        try
        {
            SetUpLocalFeed(root);
            var service = CreateServiceRootedAt(root);

            // The reader first looks for "{lowercase-id}.nuspec"; when that is absent it must fall back to the
            // first "*.nuspec" in the directory. Author a complete cache entry whose nuspec is deliberately NOT
            // named "renamed.pkg.nuspec", so only the fallback search resolves it (declaring no dependencies).
            var packageDir = service.GetNuGetPackageDir("Renamed.Pkg", "1.0.0");
            packageDir.Create();
            File.WriteAllText(Path.Join(packageDir.FullName, ".nupkg.metadata"), "{}");
            File.WriteAllText(Path.Join(packageDir.FullName, "not-the-id.nuspec"), """
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata>
                    <id>Renamed.Pkg</id>
                    <version>1.0.0</version>
                    <authors>winapp-tests</authors>
                    <description>renamed nuspec fallback test</description>
                  </metadata>
                </package>
                """);

            var installed = await service.InstallPackageAsync("Renamed.Pkg", "1.0.0", TestTaskContext, TestContext.CancellationToken);

            Assert.AreEqual("1.0.0", installed["Renamed.Pkg"], "The cached package must resolve through the fallback *.nuspec search.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_WindowsAppSdk_PreviewAndExperimental_FilterToRequestedChannel()
    {
        if (NugetPackagesEnvOverridesConfig)
        {
            Assert.Inconclusive("NUGET_PACKAGES is set; it overrides the config's globalPackagesFolder, so the local feed would not be exercised.");
        }

        var root = CreateFeedTestDirectory();
        try
        {
            var (feed, _) = SetUpLocalFeed(root);

            // The Windows App SDK channel filters only apply to the WINAPP_SDK_PACKAGE id. Serve a stable, a
            // preview and an experimental build so each channel selects its own newest match.
            WriteNupkgToFeed(feed, "Microsoft.WindowsAppSDK", "1.6.0");
            WriteNupkgToFeed(feed, "Microsoft.WindowsAppSDK", "1.7.0-preview1");
            WriteNupkgToFeed(feed, "Microsoft.WindowsAppSDK", "1.7.0-experimental1");

            var service = CreateServiceRootedAt(root);

            var preview = await service.GetLatestVersionAsync("Microsoft.WindowsAppSDK", SdkInstallMode.Preview, TestContext.CancellationToken);
            Assert.AreEqual("1.7.0-preview1", preview, "Preview channel must select the -preview build.");

            var experimental = await service.GetLatestVersionAsync("Microsoft.WindowsAppSDK", SdkInstallMode.Experimental, TestContext.CancellationToken);
            Assert.AreEqual("1.7.0-experimental1", experimental, "Experimental channel must select the -experimental build.");

            var stable = await service.GetLatestVersionAsync("Microsoft.WindowsAppSDK", SdkInstallMode.Stable, TestContext.CancellationToken);
            Assert.AreEqual("1.6.0", stable, "Stable channel must exclude prerelease builds.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_NoVersionMatchesRequestedChannel_ThrowsChannelSpecificError()
    {
        if (NugetPackagesEnvOverridesConfig)
        {
            Assert.Inconclusive("NUGET_PACKAGES is set; it overrides the config's globalPackagesFolder, so the local feed would not be exercised.");
        }

        var root = CreateFeedTestDirectory();
        try
        {
            var (feed, _) = SetUpLocalFeed(root);

            // Only a stable build exists; requesting the preview channel yields versions found but none matching.
            WriteNupkgToFeed(feed, "Microsoft.WindowsAppSDK", "1.6.0");

            var service = CreateServiceRootedAt(root);

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await service.GetLatestVersionAsync("Microsoft.WindowsAppSDK", SdkInstallMode.Preview, TestContext.CancellationToken));

            StringAssert.Contains(ex.Message, "Preview", StringComparison.Ordinal);
            StringAssert.Contains(ex.Message, "none matched", StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_NoVersionsReturnedAtAll_ThrowsNoVersionsError()
    {
        if (NugetPackagesEnvOverridesConfig)
        {
            Assert.Inconclusive("NUGET_PACKAGES is set; it overrides the config's globalPackagesFolder, so the local feed would not be exercised.");
        }

        var root = CreateFeedTestDirectory();
        try
        {
            var (feed, _) = SetUpLocalFeed(root);
            // The feed is valid and eligible but carries no versions of the requested package.
            WriteNupkgToFeed(feed, "Some.Other.Pkg", "1.0.0");

            var service = CreateServiceRootedAt(root);

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await service.GetLatestVersionAsync("Absent.Pkg", SdkInstallMode.Stable, TestContext.CancellationToken));

            StringAssert.Contains(ex.Message, "no versions were returned", StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_VersionlessDependency_ResolvesLowestAvailable()
    {
        if (NugetPackagesEnvOverridesConfig)
        {
            Assert.Inconclusive("NUGET_PACKAGES is set; it overrides the config's globalPackagesFolder, so the local feed would not be exercised.");
        }

        var root = CreateFeedTestDirectory();
        try
        {
            var (feed, _) = SetUpLocalFeed(root);

            // Root declares a dependency with NO version constraint (an unbounded range). NuGet treats that as
            // an unconstrained required dependency and resolves the lowest available version; winapp matches
            // that rather than silently omitting a declared package from the graph.
            File.WriteAllBytes(
                Path.Join(feed.FullName, "Versionless.Root.1.0.0.nupkg"),
                BuildNupkgWithVersionlessDependency("Versionless.Root", "1.0.0", "Versionless.Dep"));
            WriteNupkgToFeed(feed, "Versionless.Dep", "1.0.0");
            WriteNupkgToFeed(feed, "Versionless.Dep", "2.0.0");

            var service = CreateServiceRootedAt(root);

            var deps = await service.GetPackageDependenciesAsync("Versionless.Root", "1.0.0", TestContext.CancellationToken);

            Assert.IsTrue(deps.ContainsKey("Versionless.Dep"), "A version-less dependency is still a declared dependency and must be resolved.");
            Assert.AreEqual("1.0.0", deps["Versionless.Dep"], "An unconstrained range resolves to the lowest available version, as NuGet does.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task InstallPackageAsync_VersionlessDependency_IsInstalled()
    {
        if (NugetPackagesEnvOverridesConfig)
        {
            Assert.Inconclusive("NUGET_PACKAGES is set; it overrides the config's globalPackagesFolder, so the local feed would not be exercised.");
        }

        var root = CreateFeedTestDirectory();
        try
        {
            var (feed, _) = SetUpLocalFeed(root);

            File.WriteAllBytes(
                Path.Join(feed.FullName, "Versionless.Install.1.0.0.nupkg"),
                BuildNupkgWithVersionlessDependency("Versionless.Install", "1.0.0", "Versionless.Dep"));
            WriteNupkgToFeed(feed, "Versionless.Dep", "1.0.0");

            var service = CreateServiceRootedAt(root);

            var installed = await service.InstallPackageAsync("Versionless.Install", "1.0.0", TestTaskContext, TestContext.CancellationToken);

            Assert.IsTrue(installed.ContainsKey("Versionless.Install"), "The root package must install.");
            Assert.IsTrue(installed.ContainsKey("Versionless.Dep"), "The version-less dependency must be installed rather than silently dropped.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task InstallPackageAsync_CancelledWhileResolvingDependency_ThrowsOperationCanceled()
    {
        if (NugetPackagesEnvOverridesConfig)
        {
            Assert.Inconclusive("NUGET_PACKAGES is set; it overrides the config's globalPackagesFolder, so the local feed would not be exercised.");
        }

        var root = CreateFeedTestDirectory();
        try
        {
            var (feed, _) = SetUpLocalFeed(root);
            WriteNupkgToFeed(feed, "Cancel.Root", "1.0.0", ("Cancel.Child", "1.0.0"));
            WriteNupkgToFeed(feed, "Cancel.Child", "1.0.0");

            var service = CreateServiceRootedAt(root);

            // Populate the cache so the re-install short-circuits the root download and proceeds straight to
            // dependency resolution, where the cancellation token is observed.
            await service.InstallPackageAsync("Cancel.Root", "1.0.0", TestTaskContext, TestContext.CancellationToken);

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            // The cached root is trusted (no download), but resolving its dependency enters the per-source loop
            // where the cancelled token throws. That cancellation must propagate as OperationCanceledException
            // rather than being recorded as an ordinary per-dependency failure.
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                async () => await service.InstallPackageAsync("Cancel.Root", "1.0.0", TestTaskContext, cts.Token));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_OnlySourceUnreachable_ThrowsInsteadOfEmptyGraph()
    {
        var root = CreateFeedTestDirectory();
        try
        {
            // The only configured source is unreachable: a closed loopback address (127.0.0.1 port 1, which
            // nothing listens on) so the connection is refused immediately. HTTPS scheme keeps it past the
            // plain-HTTP guard and onto the real protocol path, while loopback keeps the test fully local and
            // deterministic — no DNS, no proxy, no external traffic. Reading the dependency graph must surface
            // that protocol failure rather than return an empty graph, which a caller would treat as "no
            // dependencies" and silently skip required transitive packages.
            WriteNuGetConfig(root, """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="broken" value="https://127.0.0.1:1/v3/index.json" />
                  </packageSources>
                  <disabledPackageSources>
                    <clear />
                  </disabledPackageSources>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="broken">
                      <package pattern="*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await service.GetPackageDependenciesAsync("Any.Pkg", "1.0.0", TestContext.CancellationToken));

            StringAssert.Contains(ex.Message, "Failed to resolve dependencies", StringComparison.Ordinal);
            StringAssert.Contains(ex.Message, "broken", StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_NoEnabledSourcesConfigured_ThrowsNoSourcesError()
    {
        var root = CreateFeedTestDirectory();
        try
        {
            // Clear every inherited source and add none, with mapping disabled: no source is eligible for any
            // package, so the diagnosis must be "no enabled NuGet sources are configured" (not a mapping error).
            WriteNuGetConfig(root, """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                  </packageSources>
                  <disabledPackageSources>
                    <clear />
                  </disabledPackageSources>
                  <packageSourceMapping>
                    <clear />
                  </packageSourceMapping>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await service.GetLatestVersionAsync("Any.Pkg", SdkInstallMode.Stable, TestContext.CancellationToken));

            StringAssert.Contains(ex.Message, "no enabled NuGet sources are configured", StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// Builds a minimal but valid .nupkg whose single dependency has NO version range at all (a version-less
    /// dependency). <see cref="NugetFeedTestHelpers.BuildNupkgBytes"/> always parses a concrete range, so this
    /// authors the unbounded case directly to exercise the "range constrains nothing -> skip" resolver branch.
    /// </summary>
    private static byte[] BuildNupkgWithVersionlessDependency(string id, string version, string dependencyId)
    {
        var builder = new PackageBuilder
        {
            Id = id,
            Version = NuGetVersion.Parse(version),
            Description = $"{id} test package",
        };
        builder.Authors.Add("winapp-tests");
        builder.DependencyGroups.Add(new PackageDependencyGroup(
            NuGetFramework.Parse("net10.0"),
            [new PackageDependency(dependencyId)]));

        var contentFile = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid():N}.txt");
        File.WriteAllText(contentFile, "test");
        try
        {
            builder.Files.Add(new PhysicalPackageFile { SourcePath = contentFile, TargetPath = $"lib/net10.0/{id}.txt" });
            using var stream = new MemoryStream();
            builder.Save(stream);
            return stream.ToArray();
        }
        finally
        {
            File.Delete(contentFile);
        }
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;
using static WinApp.Cli.Tests.NugetFeedTestHelpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// Restoring a dependency graph that is ALREADY fully extracted in the global packages folder. Reading a
/// cached package's dependency list from its local .nuspec is not enough on its own: each declared range still
/// has to be resolved to a concrete version, and resolving that only against the configured sources made an
/// already-on-disk graph fail whenever the feeds could not answer — offline, or under a
/// <c>packageSourceMapping</c> that excludes a transitive package (issue #762).
///
/// These tests warm the cache from a local folder feed, then re-run the install under a nuget.config that can
/// no longer serve the graph, and assert it still restores. The final test pins the other direction: while the
/// sources CAN answer, the cache must not influence which version is selected.
///
/// The gate that confines the fallback to "the sources could not answer" is exercised here only through
/// the last test, which pins that a reachable source's version wins over a lower cached one. A direct
/// negative test — a reachable source answering authoritatively while a satisfying cache entry exists —
/// was tried and removed: under the full 32-way parallel run the local folder feed query intermittently
/// fails, which legitimately enables the fallback, so the assertion could not be made stable without
/// masking the very condition it was checking.
/// </summary>
/// <remarks>
/// Not parallelized: each test warms a real global-packages folder, rewrites the nuget.config under the same
/// root, and then re-reads that folder through a second service instance. That two-phase
/// install-then-reconfigure sequence proved intermittently sensitive to the 32-way parallel test run, so it is
/// serialized to keep the assertions deterministic.
/// </remarks>
[TestClass]
[DoNotParallelize]
public class NugetServiceCachedGraphTests : BaseCommandTests
{
    /// <summary>
    /// NUGET_PACKAGES takes precedence over the globalPackagesFolder these tests write, which would move the
    /// cache out from under them and stop the local feed being exercised.
    /// </summary>
    private static void SkipIfNugetPackagesOverridden()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NUGET_PACKAGES")))
        {
            Assert.Inconclusive("NUGET_PACKAGES is set in the environment; it overrides the config's globalPackagesFolder, so the local feed would not be exercised.");
        }
    }

    private static void WriteNoSourcesConfig(DirectoryInfo root, DirectoryInfo packages) =>
        WriteNuGetConfig(root, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <config>
                <add key="globalPackagesFolder" value="{packages.FullName}" />
              </config>
              <packageSources>
                <clear />
              </packageSources>
              <disabledPackageSources>
                <clear />
              </disabledPackageSources>
            </configuration>
            """);

    [TestMethod]
    public async Task InstallPackageAsync_FullyCachedGraph_RestoresWithNoConfiguredSources()
    {
        SkipIfNugetPackagesOverridden();

        var root = CreateFeedTestDirectory();
        try
        {
            var feed = new DirectoryInfo(Path.Join(root.FullName, "feed"));
            feed.Create();
            var packages = new DirectoryInfo(Path.Join(root.FullName, "packages"));

            WriteNupkgToFeed(feed, "Cached.Root", "1.0.0", ("Cached.Child", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "Cached.Child", "1.0.0", ("Cached.Leaf", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "Cached.Leaf", "1.0.0");

            // Warm the cache from the feed.
            WriteLocalFeedConfig(root, feed, packages);
            var warm = await CreateServiceRootedAt(root)
                .InstallPackageAsync("Cached.Root", "1.0.0", TestTaskContext, TestContext.CancellationToken);
            Assert.HasCount(3, warm, "The whole graph must be installed before the offline assertion is meaningful.");

            // Now take every source away — the equivalent of restoring offline — and delete the feed so the
            // packages genuinely cannot be fetched again.
            WriteNoSourcesConfig(root, packages);
            feed.Delete(recursive: true);

            var offlineService = CreateServiceRootedAt(root);

            var installed = await offlineService
                .InstallPackageAsync("Cached.Root", "1.0.0", TestTaskContext, TestContext.CancellationToken);

            Assert.HasCount(3, installed, "A graph that is already extracted must restore without any configured source.");
            Assert.AreEqual("1.0.0", installed["Cached.Child"]);
            Assert.AreEqual("1.0.0", installed["Cached.Leaf"]);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task InstallPackageAsync_FullyCachedGraph_RestoresWhenMappingExcludesATransitivePackage()
    {
        SkipIfNugetPackagesOverridden();

        var root = CreateFeedTestDirectory();
        try
        {
            var feed = new DirectoryInfo(Path.Join(root.FullName, "feed"));
            feed.Create();
            var packages = new DirectoryInfo(Path.Join(root.FullName, "packages"));

            WriteNupkgToFeed(feed, "Mapped.Root", "1.0.0", ("Mapped.Child", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "Mapped.Child", "1.0.0");

            WriteLocalFeedConfig(root, feed, packages);
            await CreateServiceRootedAt(root)
                .InstallPackageAsync("Mapped.Root", "1.0.0", TestTaskContext, TestContext.CancellationToken);

            // Keep the feed, but map only the root to it. The transitive Mapped.Child is now mapped to NO
            // source, which is the failure the mapping-aware diagnostics report — yet it is already on disk.
            WriteNuGetConfig(root, $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <config>
                    <add key="globalPackagesFolder" value="{packages.FullName}" />
                  </config>
                  <packageSources>
                    <clear />
                    <add key="local" value="{feed.FullName}" />
                  </packageSources>
                  <disabledPackageSources>
                    <clear />
                  </disabledPackageSources>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="local">
                      <package pattern="Mapped.Root" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);

            var installed = await CreateServiceRootedAt(root)
                .InstallPackageAsync("Mapped.Root", "1.0.0", TestTaskContext, TestContext.CancellationToken);

            Assert.HasCount(2, installed, "A cached transitive package must resolve even when the mapping excludes it from every source.");
            Assert.AreEqual("1.0.0", installed["Mapped.Child"]);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task InstallPackageAsync_SourcesCanAnswer_CacheDoesNotChangeTheSelectedVersion()
    {
        SkipIfNugetPackagesOverridden();

        var root = CreateFeedTestDirectory();
        try
        {
            var feed = new DirectoryInfo(Path.Join(root.FullName, "feed"));
            feed.Create();
            var packages = new DirectoryInfo(Path.Join(root.FullName, "packages"));

            // Warm the cache with Pinned.Child 1.0.0 via a root that pins it exactly.
            WriteNupkgToFeed(feed, "Pinned.Warmer", "1.0.0", ("Pinned.Child", "[1.0.0]"));
            WriteNupkgToFeed(feed, "Pinned.Child", "1.0.0");
            WriteNupkgToFeed(feed, "Pinned.Child", "2.0.0");

            WriteLocalFeedConfig(root, feed, packages);
            await CreateServiceRootedAt(root)
                .InstallPackageAsync("Pinned.Warmer", "1.0.0", TestTaskContext, TestContext.CancellationToken);
            Assert.IsTrue(
                Directory.Exists(Path.Join(packages.FullName, "pinned.child", "1.0.0")),
                "Pinned.Child 1.0.0 must be cached for this test to mean anything.");

            // A different root requires >= 2.0.0. The cache holds only 1.0.0, which does NOT satisfy that
            // range, and the reachable feed offers 2.0.0 — so resolution must come from the feed. This pins
            // the fallback as failure-path-only: it must never pre-empt a source that can answer.
            WriteNupkgToFeed(feed, "Pinned.Root", "1.0.0", ("Pinned.Child", "[2.0.0, )"));

            var installed = await CreateServiceRootedAt(root)
                .InstallPackageAsync("Pinned.Root", "1.0.0", TestTaskContext, TestContext.CancellationToken);

            Assert.AreEqual("2.0.0", installed["Pinned.Child"], "The feed's satisfying version must win over the cached lower one.");
        }
        finally
        {
            TryDelete(root);
        }
    }
}

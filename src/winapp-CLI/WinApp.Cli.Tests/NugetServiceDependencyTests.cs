// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;
using static WinApp.Cli.Tests.NugetFeedTestHelpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// NuGet.Client-backed dependency-resolution tests for <see cref="NugetService.GetPackageDependenciesAsync"/>:
/// resolving a dependency's declared <c>VersionRange</c> to the lowest listed satisfying version (inclusive
/// / exclusive lower bounds, upper-bound-only and unlisted lower bounds), surfacing a source-query failure
/// instead of silently dropping a dependency, FAILING (rather than silently dropping) when a bounded range
/// cannot be resolved — no source offers a satisfying version, or a <c>packageSourceMapping</c> exclusion
/// leaves the transitive package with no eligible source — and scoping the process-wide dependency cache to
/// the effective configuration (feeds, their configured order, global folder and packageSourceMapping).
/// Source-eligibility / version-selection tests live in <see cref="NugetServiceFeedTests"/>; the shared
/// feed-authoring helpers live in <see cref="NugetFeedTestHelpers"/> (imported via <c>using static</c>).
/// </summary>
[TestClass]
public class NugetServiceDependencyTests : BaseCommandTests
{
    [TestMethod]
    public async Task GetPackageDependenciesAsync_ExclusiveLowerBoundRange_ResolvesLowestListedSatisfyingVersion()
    {
        var root = CreateFeedTestDirectory();
        try
        {
            var feed = new DirectoryInfo(Path.Join(root.FullName, "feed"));
            feed.Create();
            var packages = new DirectoryInfo(Path.Join(root.FullName, "packages"));

            // Root depends on Child with an EXCLUSIVE lower bound: (1.0.0, 2.0.0]. The declared lower bound
            // 1.0.0 does NOT satisfy the range, so reducing the range to MinVersion would resolve to a
            // disallowed version. The lowest LISTED version that satisfies the range is 1.5.0 (1.0.0 is
            // excluded; 2.0.0 is a higher valid match).
            WriteNupkgToFeed(feed, "Range.Root", "1.0.0", ("Range.Child", "(1.0.0, 2.0.0]"));
            WriteNupkgToFeed(feed, "Range.Child", "1.0.0");
            WriteNupkgToFeed(feed, "Range.Child", "1.5.0");
            WriteNupkgToFeed(feed, "Range.Child", "2.0.0");

            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            var deps = await service.GetPackageDependenciesAsync("Range.Root", "1.0.0", TestContext.CancellationToken);

            Assert.IsTrue(deps.TryGetValue("Range.Child", out var childVersion), "The dependency must be resolved, not skipped.");
            Assert.AreEqual("1.5.0", childVersion, "The exclusive lower bound (1.0.0) must be excluded and the lowest satisfying listed version selected.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_UpperBoundOnlyRange_ResolvesInsteadOfSkipping()
    {
        var root = CreateFeedTestDirectory();
        try
        {
            var feed = new DirectoryInfo(Path.Join(root.FullName, "feed"));
            feed.Create();
            var packages = new DirectoryInfo(Path.Join(root.FullName, "packages"));

            // Root depends on Child with an UPPER-BOUND-ONLY range: (, 2.0.0]. MinVersion is null for such a
            // range, so the old MinVersion-based logic silently DROPPED the dependency. The lowest listed
            // version that satisfies "<= 2.0.0" is 1.0.0 (3.0.0 is excluded).
            WriteNupkgToFeed(feed, "Upper.Root", "1.0.0", ("Upper.Child", "(, 2.0.0]"));
            WriteNupkgToFeed(feed, "Upper.Child", "1.0.0");
            WriteNupkgToFeed(feed, "Upper.Child", "2.0.0");
            WriteNupkgToFeed(feed, "Upper.Child", "3.0.0");

            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            var deps = await service.GetPackageDependenciesAsync("Upper.Root", "1.0.0", TestContext.CancellationToken);

            Assert.IsTrue(deps.TryGetValue("Upper.Child", out var childVersion), "An upper-bound-only dependency must be resolved, not silently skipped.");
            Assert.AreEqual("1.0.0", childVersion, "The lowest listed version satisfying '<= 2.0.0' must be selected.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_InclusiveLowerBoundNotListed_ResolvesNextHigherListedVersion()
    {
        var root = CreateFeedTestDirectory();
        try
        {
            var feed = new DirectoryInfo(Path.Join(root.FullName, "feed"));
            feed.Create();
            var packages = new DirectoryInfo(Path.Join(root.FullName, "packages"));

            // Root depends on Child with a plain inclusive lower bound (1.0.0 == [1.0.0, )). The declared
            // lower bound 1.0.0 is NOT listed on the feed; only 1.1.0 and 2.0.0 are. NuGet's lowest-applicable
            // resolution must select the lowest LISTED satisfying version (1.1.0) — reducing the range to its
            // MinVersion would instead request the missing 1.0.0 and drop the dependency.
            WriteNupkgToFeed(feed, "Low.Root", "1.0.0", ("Low.Child", "1.0.0"));
            WriteNupkgToFeed(feed, "Low.Child", "1.1.0");
            WriteNupkgToFeed(feed, "Low.Child", "2.0.0");

            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            var deps = await service.GetPackageDependenciesAsync("Low.Root", "1.0.0", TestContext.CancellationToken);

            Assert.IsTrue(deps.TryGetValue("Low.Child", out var childVersion), "The dependency must be resolved against listed versions, not skipped.");
            Assert.AreEqual("1.1.0", childVersion, "The unlisted lower bound (1.0.0) must resolve up to the lowest listed satisfying version (1.1.0).");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_SatisfyingSourceUnreachable_SurfacesErrorInsteadOfSkipping()
    {
        var root = CreateFeedTestDirectory();
        try
        {
            // The local feed serves Root (so its dependency group is read) but lists NO versions of Child.
            // A second eligible source that could have satisfied Child is unreachable (reserved '.invalid'
            // TLD, RFC 6761). Turning that source failure into an empty version list would silently drop the
            // dependency; instead the range resolver must surface the error so the graph caller sees it.
            var feed = new DirectoryInfo(Path.Join(root.FullName, "feed"));
            feed.Create();
            WriteNupkgToFeed(feed, "Broken.Root", "1.0.0", ("Broken.Child", "[1.0.0, )"));

            WriteNuGetConfig(root, $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="local" value="{feed.FullName}" />
                    <add key="broken" value="https://nuget.invalid/v3/index.json" />
                  </packageSources>
                  <disabledPackageSources>
                    <clear />
                  </disabledPackageSources>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="local">
                      <package pattern="*" />
                    </packageSource>
                    <packageSource key="broken">
                      <package pattern="*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await service.GetPackageDependenciesAsync("Broken.Root", "1.0.0", TestContext.CancellationToken));

            // The error must name the dependency and the unreachable source, proving the feed/auth failure
            // was not masked as "no satisfying version" (which would silently omit the dependency).
            StringAssert.Contains(ex.Message, "Broken.Child", StringComparison.Ordinal);
            StringAssert.Contains(ex.Message, "could not be queried", StringComparison.Ordinal);
            StringAssert.Contains(ex.Message, "broken", StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_DifferentConfigRoots_DoNotShareCache()
    {
        // Two independent workspaces resolve the SAME package id/version but their private feeds declare
        // DIFFERENT dependencies. The process-wide dependency cache must be scoped to the effective config
        // (feeds/global folder), so the second lookup returns ITS feed's dependency rather than the first
        // one's cached result.
        var rootA = CreateFeedTestDirectory();
        var rootB = CreateFeedTestDirectory();
        try
        {
            var feedA = new DirectoryInfo(Path.Join(rootA.FullName, "feed"));
            feedA.Create();
            WriteNupkgToFeed(feedA, "Scoped.Root", "1.0.0", ("Scoped.ChildA", "1.0.0"));
            WriteNupkgToFeed(feedA, "Scoped.ChildA", "1.0.0");
            WriteLocalFeedConfig(rootA, feedA, new DirectoryInfo(Path.Join(rootA.FullName, "packages")));

            var feedB = new DirectoryInfo(Path.Join(rootB.FullName, "feed"));
            feedB.Create();
            WriteNupkgToFeed(feedB, "Scoped.Root", "1.0.0", ("Scoped.ChildB", "1.0.0"));
            WriteNupkgToFeed(feedB, "Scoped.ChildB", "1.0.0");
            WriteLocalFeedConfig(rootB, feedB, new DirectoryInfo(Path.Join(rootB.FullName, "packages")));

            var depsA = await CreateServiceRootedAt(rootA).GetPackageDependenciesAsync("Scoped.Root", "1.0.0", TestContext.CancellationToken);
            var depsB = await CreateServiceRootedAt(rootB).GetPackageDependenciesAsync("Scoped.Root", "1.0.0", TestContext.CancellationToken);

            Assert.IsTrue(depsA.ContainsKey("Scoped.ChildA"), "Workspace A must resolve its own feed's dependency.");
            Assert.IsFalse(depsA.ContainsKey("Scoped.ChildB"), "Workspace A must not see workspace B's dependency.");
            Assert.IsTrue(depsB.ContainsKey("Scoped.ChildB"), "Workspace B must resolve its own feed's dependency, not A's cached result.");
            Assert.IsFalse(depsB.ContainsKey("Scoped.ChildA"), "Workspace B must not receive workspace A's cached dependency.");
        }
        finally
        {
            TryDelete(rootA);
            TryDelete(rootB);
        }
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_SameSourcesDifferentMapping_DoNotShareCache()
    {
        // The two config roots reference the SAME two feeds (identical names + URLs) and the SAME global
        // packages folder, so a fingerprint of just sources/global-folder would collide. They differ ONLY in
        // their packageSourceMapping: root A routes everything to feed X (whose Root depends on Map.ChildX),
        // root B routes everything to feed Y (whose Root depends on Map.ChildY). The cache scope must include
        // the full mapping rules so B does not receive A's cached dependency.
        var shared = CreateFeedTestDirectory();
        var rootA = new DirectoryInfo(Path.Join(shared.FullName, "rootA"));
        var rootB = new DirectoryInfo(Path.Join(shared.FullName, "rootB"));
        rootA.Create();
        rootB.Create();
        try
        {
            var feedX = new DirectoryInfo(Path.Join(shared.FullName, "feedX"));
            var feedY = new DirectoryInfo(Path.Join(shared.FullName, "feedY"));
            feedX.Create();
            feedY.Create();
            var packages = new DirectoryInfo(Path.Join(shared.FullName, "packages"));

            // Same package id/version in both feeds, but each declares a different dependency.
            WriteNupkgToFeed(feedX, "Map.Root", "1.0.0", ("Map.ChildX", "1.0.0"));
            WriteNupkgToFeed(feedX, "Map.ChildX", "1.0.0");
            WriteNupkgToFeed(feedY, "Map.Root", "1.0.0", ("Map.ChildY", "1.0.0"));
            WriteNupkgToFeed(feedY, "Map.ChildY", "1.0.0");

            // Both configs list the SAME two sources (x, y) and the SAME global folder; only the mapping's
            // '*' target differs (x vs y).
            string Config(string mappedSource) => $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <config>
                    <add key="globalPackagesFolder" value="{packages.FullName}" />
                  </config>
                  <packageSources>
                    <clear />
                    <add key="x" value="{feedX.FullName}" />
                    <add key="y" value="{feedY.FullName}" />
                  </packageSources>
                  <disabledPackageSources>
                    <clear />
                  </disabledPackageSources>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="{mappedSource}">
                      <package pattern="*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """;

            WriteNuGetConfig(rootA, Config("x"));
            WriteNuGetConfig(rootB, Config("y"));

            var depsA = await CreateServiceRootedAt(rootA).GetPackageDependenciesAsync("Map.Root", "1.0.0", TestContext.CancellationToken);
            var depsB = await CreateServiceRootedAt(rootB).GetPackageDependenciesAsync("Map.Root", "1.0.0", TestContext.CancellationToken);

            Assert.IsTrue(depsA.ContainsKey("Map.ChildX"), "Root A maps '*' to feed X, so it must resolve feed X's dependency.");
            Assert.IsFalse(depsA.ContainsKey("Map.ChildY"), "Root A must not see feed Y's dependency.");
            Assert.IsTrue(depsB.ContainsKey("Map.ChildY"), "Root B maps '*' to feed Y and must resolve feed Y's dependency, not A's cached result.");
            Assert.IsFalse(depsB.ContainsKey("Map.ChildX"), "Root B must not receive root A's cached dependency despite identical sources/global folder.");
        }
        finally
        {
            TryDelete(shared);
        }
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_SameSourcesReversedOrder_DoNotShareCache()
    {
        // Both config roots reference the SAME two feeds (identical names + URLs), the SAME global packages
        // folder and the SAME packageSourceMapping (both feeds mapped to '*'); they differ ONLY in the ORDER
        // the sources are listed. Dependency resolution is first-source-wins (FetchDirectDependenciesAsync
        // returns the graph from the first eligible source that has the package), so the two orders resolve
        // DIFFERENT graphs and must not share the process-wide dependency cache. If ConfigScopeKey sorted the
        // sources by name, both roots would collapse to one cache key and root B would receive root A's
        // cached graph — the regression this test guards against.
        var shared = CreateFeedTestDirectory();
        var rootA = new DirectoryInfo(Path.Join(shared.FullName, "rootA"));
        var rootB = new DirectoryInfo(Path.Join(shared.FullName, "rootB"));
        rootA.Create();
        rootB.Create();
        try
        {
            var feedX = new DirectoryInfo(Path.Join(shared.FullName, "feedX"));
            var feedY = new DirectoryInfo(Path.Join(shared.FullName, "feedY"));
            feedX.Create();
            feedY.Create();
            var packages = new DirectoryInfo(Path.Join(shared.FullName, "packages"));

            // Same package id/version in both feeds, each declaring a DIFFERENT dependency, so the resolved
            // graph reveals which source "won".
            WriteNupkgToFeed(feedX, "Order.Root", "1.0.0", ("Order.ChildX", "1.0.0"));
            WriteNupkgToFeed(feedX, "Order.ChildX", "1.0.0");
            WriteNupkgToFeed(feedY, "Order.Root", "1.0.0", ("Order.ChildY", "1.0.0"));
            WriteNupkgToFeed(feedY, "Order.ChildY", "1.0.0");

            var feedByKey = new Dictionary<string, string>
            {
                ["x"] = feedX.FullName,
                ["y"] = feedY.FullName,
            };

            // Identical sources (same names/URLs), global folder and mapping (both feeds mapped to '*', so
            // both are eligible and first-source-wins decides); only the <add> ORDER differs between the two.
            string Config(string firstKey, string secondKey) => $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <config>
                    <add key="globalPackagesFolder" value="{packages.FullName}" />
                  </config>
                  <packageSources>
                    <clear />
                    <add key="{firstKey}" value="{feedByKey[firstKey]}" />
                    <add key="{secondKey}" value="{feedByKey[secondKey]}" />
                  </packageSources>
                  <disabledPackageSources>
                    <clear />
                  </disabledPackageSources>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="x">
                      <package pattern="*" />
                    </packageSource>
                    <packageSource key="y">
                      <package pattern="*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """;

            WriteNuGetConfig(rootA, Config("x", "y"));
            WriteNuGetConfig(rootB, Config("y", "x"));

            var depsA = await CreateServiceRootedAt(rootA).GetPackageDependenciesAsync("Order.Root", "1.0.0", TestContext.CancellationToken);
            var depsB = await CreateServiceRootedAt(rootB).GetPackageDependenciesAsync("Order.Root", "1.0.0", TestContext.CancellationToken);

            Assert.IsTrue(depsA.ContainsKey("Order.ChildX"), "Root A lists feed X first, so first-source-wins must resolve feed X's dependency.");
            Assert.IsFalse(depsA.ContainsKey("Order.ChildY"), "Root A must not see feed Y's dependency.");
            Assert.IsTrue(depsB.ContainsKey("Order.ChildY"), "Root B lists feed Y first, so it must resolve feed Y's dependency, not root A's cached result.");
            Assert.IsFalse(depsB.ContainsKey("Order.ChildX"), "Root B must not receive root A's cached dependency despite identical sources (only their order differs).");
        }
        finally
        {
            TryDelete(shared);
        }
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_BoundedRangeNoSatisfyingVersion_FailsInsteadOfSilentlyDropping()
    {
        var root = CreateFeedTestDirectory();
        try
        {
            var feed = new DirectoryInfo(Path.Join(root.FullName, "feed"));
            feed.Create();
            var packages = new DirectoryInfo(Path.Join(root.FullName, "packages"));

            // Root depends on Child with a bounded range [5.0.0, ) that NO available version satisfies (the
            // feed only carries Child 1.0.0). A required transitive package that cannot be resolved must FAIL
            // resolution, not be silently omitted (which would report an incomplete graph as success).
            WriteNupkgToFeed(feed, "Missing.Root", "1.0.0", ("Missing.Child", "[5.0.0, )"));
            WriteNupkgToFeed(feed, "Missing.Child", "1.0.0");

            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await service.GetPackageDependenciesAsync("Missing.Root", "1.0.0", TestContext.CancellationToken));

            // The error must name the dependency and its unsatisfiable range, proving the missing package was
            // surfaced rather than dropped.
            StringAssert.Contains(ex.Message, "Missing.Child", StringComparison.Ordinal);
            StringAssert.Contains(ex.Message, "[5.0.0, )", StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_DependencyExcludedByMapping_FailsInsteadOfSilentlyDropping()
    {
        var root = CreateFeedTestDirectory();
        try
        {
            var feed = new DirectoryInfo(Path.Join(root.FullName, "feed"));
            feed.Create();
            var packages = new DirectoryInfo(Path.Join(root.FullName, "packages"));

            // The feed physically carries both packages, but packageSourceMapping only routes 'Excl.Root*' to
            // it. The transitive 'Excl.Child' matches NO mapping pattern, so it has zero eligible sources.
            // That must FAIL resolution (with the mapping-specific reason) rather than being silently dropped —
            // otherwise the graph would report success with a required package that can never be restored.
            WriteNupkgToFeed(feed, "Excl.Root", "1.0.0", ("Excl.Child", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "Excl.Child", "1.0.0");

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
                      <package pattern="Excl.Root*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await service.GetPackageDependenciesAsync("Excl.Root", "1.0.0", TestContext.CancellationToken));

            // The error must name the excluded dependency and point at the packageSourceMapping gap.
            StringAssert.Contains(ex.Message, "Excl.Child", StringComparison.Ordinal);
            StringAssert.Contains(ex.Message, "packageSourceMapping", StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_CyclicGraph_ThrowsActionableErrorInsteadOfStackOverflow()
    {
        var root = CreateFeedTestDirectory();
        try
        {
            var feed = new DirectoryInfo(Path.Join(root.FullName, "feed"));
            feed.Create();
            var packages = new DirectoryInfo(Path.Join(root.FullName, "packages"));

            // A cyclic graph: Cycle.A depends on Cycle.B, which depends back on Cycle.A. A cache entry is only
            // published after a package's whole subtree resolves, so without explicit cycle detection Cycle.A
            // would re-enter resolution before it is cached and recurse until the stack overflows. Resolution
            // must instead fail fast with an actionable error that names the offending chain.
            WriteNupkgToFeed(feed, "Cycle.A", "1.0.0", ("Cycle.B", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "Cycle.B", "1.0.0", ("Cycle.A", "[1.0.0, )"));

            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await service.GetPackageDependenciesAsync("Cycle.A", "1.0.0", TestContext.CancellationToken));

            // The message must call out the cycle and name the chain (A -> B -> A) so it is actionable.
            StringAssert.Contains(ex.Message, "Circular package dependency", StringComparison.Ordinal);
            StringAssert.Contains(ex.Message, "Cycle.A -> Cycle.B -> Cycle.A", StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }
}

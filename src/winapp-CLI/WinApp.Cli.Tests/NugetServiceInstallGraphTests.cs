// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;
using static WinApp.Cli.Tests.NugetFeedTestHelpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// NuGet.Client-backed install-graph integrity tests for <see cref="NugetService.InstallPackageAsync"/>:
/// failing (rather than silently reporting success) when a diamond dependency graph pins the same package to
/// conflicting versions, and re-downloading a package whose global-cache folder exists but is incomplete (no
/// <c>.nupkg.metadata</c> completion marker) instead of trusting the bare directory. Version selection and
/// transitive-download happy paths live in <see cref="NugetServiceDownloadTests"/>; the shared feed-authoring
/// helpers live in <see cref="NugetFeedTestHelpers"/> (imported via <c>using static</c>).
/// </summary>
[TestClass]
public class NugetServiceInstallGraphTests : BaseCommandTests
{
    [TestMethod]
    public async Task InstallPackageAsync_DiamondPinsConflictingVersions_FailsInsteadOfReportingSuccess()
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

            // Diamond graph: Root depends on both A and B; A pins Diamond.C to EXACTLY 1.0.0 while B pins it
            // to EXACTLY 2.0.0. Whichever branch installs first fixes Diamond.C's version; the other branch's
            // exact pin can then never be satisfied. A package-id-only "already installed" short-circuit would
            // skip the second constraint and report a complete install with an invalid graph, so the install
            // must instead fail and name the conflicting package.
            WriteNupkgToFeed(feed, "Diamond.Root", "1.0.0", ("Diamond.A", "[1.0.0, )"), ("Diamond.B", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "Diamond.A", "1.0.0", ("Diamond.C", "[1.0.0]"));
            WriteNupkgToFeed(feed, "Diamond.B", "1.0.0", ("Diamond.C", "[2.0.0]"));
            WriteNupkgToFeed(feed, "Diamond.C", "1.0.0");
            WriteNupkgToFeed(feed, "Diamond.C", "2.0.0");

            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await service.InstallPackageAsync("Diamond.Root", "1.0.0", TestTaskContext, TestContext.CancellationToken));

            // The failure must name the conflicting package and describe the unsatisfiable constraint rather
            // than being swallowed as a successful install.
            StringAssert.Contains(ex.Message, "Diamond.C", StringComparison.Ordinal);
            StringAssert.Contains(ex.Message, "cannot be resolved to a single version", StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task InstallPackageAsync_DiamondWithHigherLowerBound_NeverSucceedsWithUnsatisfyingVersion()
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

            // A needs DiffMin.C [1.0.0, ), B needs [2.0.0, ), and both 1.0.0 and 2.0.0 exist — so only 2.0.0
            // satisfies BOTH branches. winapp resolves as it installs, so whichever branch it reaches first
            // fixes the version; the invariant under test is that it must never report SUCCESS having selected
            // a version that does not satisfy every branch. It previously did exactly that: an intersection
            // check saw that *some* version could satisfy both and kept 1.0.0, leaving the consumer without
            // the APIs B declared it needed.
            WriteNupkgToFeed(feed, "DiffMin.Root", "1.0.0", ("DiffMin.A", "[1.0.0, )"), ("DiffMin.B", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "DiffMin.A", "1.0.0", ("DiffMin.C", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "DiffMin.B", "1.0.0", ("DiffMin.C", "[2.0.0, )"));
            WriteNupkgToFeed(feed, "DiffMin.C", "1.0.0");
            WriteNupkgToFeed(feed, "DiffMin.C", "2.0.0");

            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            string? resolvedC = null;
            InvalidOperationException? failure = null;
            try
            {
                var installed = await service.InstallPackageAsync("DiffMin.Root", "1.0.0", TestTaskContext, TestContext.CancellationToken);
                resolvedC = installed.TryGetValue("DiffMin.C", out var c) ? c : null;
            }
            catch (InvalidOperationException ex)
            {
                failure = ex;
            }

            // Asserted independently of which branch resolves first, so the test cannot pass for the wrong
            // reason if traversal order changes.
            if (failure is not null)
            {
                StringAssert.Contains(failure.Message, "DiffMin.C", StringComparison.Ordinal);
                StringAssert.Contains(failure.Message, "cannot be resolved to a single version", StringComparison.Ordinal);
            }
            else
            {
                Assert.AreEqual(
                    "2.0.0",
                    resolvedC,
                    "Reporting success is only valid when the selected version satisfies every branch; 2.0.0 is the only such version here.");
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// Upgrading a shared dependency must retract the requirements of the version it replaced. This graph is
    /// satisfiable (C 2.0.0 with D 2.0.0), but resolution reaches C through the `>= 1.0.0` branch first, so C
    /// 1.0.0 and its pinned D [1.0.0] are selected before the `>= 2.0.0` branch forces C up to 2.0.0. C 1.0.0
    /// is then no longer part of the graph, so its D [1.0.0] pin must stop counting. While constraints were
    /// accumulated in an append-only list, that stale pin was combined with C 2.0.0's D [2.0.0] and the install
    /// failed with a conflict that does not exist in the resolved graph.
    /// </summary>
    [TestMethod]
    public async Task InstallPackageAsync_UpgradeChangesPinnedTransitive_DoesNotFailOnReplacedVersionsConstraint()
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

            WriteNupkgToFeed(feed, "Retract.Root", "1.0.0", ("Retract.A", "[1.0.0, )"), ("Retract.B", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "Retract.A", "1.0.0", ("Retract.C", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "Retract.B", "1.0.0", ("Retract.C", "[2.0.0, )"));
            // Each version of C pins a DIFFERENT exact version of D, so keeping the replaced version's pin
            // makes the two mutually unsatisfiable.
            WriteNupkgToFeed(feed, "Retract.C", "1.0.0", ("Retract.D", "[1.0.0]"));
            WriteNupkgToFeed(feed, "Retract.C", "2.0.0", ("Retract.D", "[2.0.0]"));
            WriteNupkgToFeed(feed, "Retract.D", "1.0.0");
            WriteNupkgToFeed(feed, "Retract.D", "2.0.0");

            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            Dictionary<string, string> installed;
            try
            {
                installed = await service.InstallPackageAsync("Retract.Root", "1.0.0", TestTaskContext, TestContext.CancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                // Asserted independently of which branch resolves first: this graph has a valid solution, so
                // failing is wrong no matter what order the walk happens to take.
                Assert.Fail($"Graph is satisfiable (C 2.0.0 + D 2.0.0) but install reported a conflict: {ex.Message}");
                return;
            }

            Assert.AreEqual("2.0.0", installed.GetValueOrDefault("Retract.C"), "C must end up at the only version satisfying both branches.");
            Assert.AreEqual("2.0.0", installed.GetValueOrDefault("Retract.D"), "D must follow the pin declared by the version of C that is actually selected.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// A package pulled in only by a version that a later upgrade replaced must not survive in the resolved
    /// graph. `WorkspaceSetupService` copies headers, libs, WinMDs and runtimes for every entry returned by
    /// the install, so an orphan left behind here publishes assets from a package the resolution rejected.
    /// Here C 1.0.0 requires Orphan.Removed, C 2.0.0 requires nothing, and the `>= 2.0.0` branch forces C up.
    /// </summary>
    [TestMethod]
    public async Task InstallPackageAsync_UpgradeDropsDependency_DoesNotReturnTheOrphanedDescendant()
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

            WriteNupkgToFeed(feed, "Orphan.Root", "1.0.0", ("Orphan.A", "[1.0.0, )"), ("Orphan.B", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "Orphan.A", "1.0.0", ("Orphan.C", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "Orphan.B", "1.0.0", ("Orphan.C", "[2.0.0, )"));
            WriteNupkgToFeed(feed, "Orphan.C", "1.0.0", ("Orphan.Removed", "[1.0.0]"));
            WriteNupkgToFeed(feed, "Orphan.C", "2.0.0");
            WriteNupkgToFeed(feed, "Orphan.Removed", "1.0.0");

            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            var installed = await service.InstallPackageAsync("Orphan.Root", "1.0.0", TestTaskContext, TestContext.CancellationToken);

            Assert.AreEqual("2.0.0", installed.GetValueOrDefault("Orphan.C"), "C must end up at the only version satisfying both branches.");
            // Packages still reachable from the root must be untouched by pruning.
            Assert.AreEqual("1.0.0", installed.GetValueOrDefault("Orphan.A"));
            Assert.AreEqual("1.0.0", installed.GetValueOrDefault("Orphan.B"));
            Assert.IsFalse(
                installed.ContainsKey("Orphan.Removed"),
                "Orphan.Removed is required only by the replaced C 1.0.0, so it is not part of the resolved graph and its assets must not be copied.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// A constraint declared by a package that an upgrade orphaned must stop applying immediately, not just at
    /// the final prune. Here C 1.0.0 pulls in D, which pins E [1.0.0]; the `>= 2.0.0` branch upgrades C to a
    /// version with no D at all, so D leaves the graph. A later branch legitimately requiring E [2.0.0] must
    /// not be rejected by dead D's pin — the resolver has to judge reachability while it walks, since the
    /// conflict is decided long before pruning runs.
    /// </summary>
    [TestMethod]
    public async Task InstallPackageAsync_OrphanedSubtreeConstraint_DoesNotBlockALaterBranch()
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

            // Walk order: A pulls C 1.0.0 (and through it D 1.0.0, pinning E [1.0.0]); B forces C to 2.0.0,
            // which drops D entirely; Z then requires E [2.0.0].
            WriteNupkgToFeed(feed, "Dead.Root", "1.0.0", ("Dead.A", "[1.0.0, )"), ("Dead.B", "[1.0.0, )"), ("Dead.Z", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "Dead.A", "1.0.0", ("Dead.C", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "Dead.B", "1.0.0", ("Dead.C", "[2.0.0, )"));
            WriteNupkgToFeed(feed, "Dead.C", "1.0.0", ("Dead.D", "[1.0.0]"));
            WriteNupkgToFeed(feed, "Dead.C", "2.0.0");
            WriteNupkgToFeed(feed, "Dead.D", "1.0.0", ("Dead.E", "[1.0.0]"));
            WriteNupkgToFeed(feed, "Dead.Z", "1.0.0", ("Dead.E", "[2.0.0]"));
            WriteNupkgToFeed(feed, "Dead.E", "1.0.0");
            WriteNupkgToFeed(feed, "Dead.E", "2.0.0");

            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            Dictionary<string, string> installed;
            try
            {
                installed = await service.InstallPackageAsync("Dead.Root", "1.0.0", TestTaskContext, TestContext.CancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                Assert.Fail($"Graph is satisfiable once dead D's pin is retracted, but install reported a conflict: {ex.Message}");
                return;
            }

            Assert.AreEqual("2.0.0", installed.GetValueOrDefault("Dead.C"));
            Assert.AreEqual("2.0.0", installed.GetValueOrDefault("Dead.E"), "E must follow the only live requirement, from Z.");
            Assert.IsFalse(installed.ContainsKey("Dead.D"), "D was required only by the replaced C 1.0.0 and must not survive.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// A dependency failure recorded while walking a version that a later upgrade replaced describes a branch
    /// that is no longer in the graph, so it must not fail the install. Here C 1.0.0 requires a package the
    /// feed does not have, and C is then upgraded to a version that needs nothing — the final graph is
    /// complete, so the walk-order artifact of having tried C 1.0.0 first must not be fatal.
    /// </summary>
    [TestMethod]
    public async Task InstallPackageAsync_FailureUnderReplacedVersion_DoesNotFailTheInstall()
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

            // A single chain, so the walk order is fixed regardless of how dependency sets enumerate:
            // Root -> A -> C 1.0.0, which records the failure for the absent Stale.Missing and also pulls in D;
            // D then requires C >= 2.0.0, upgrading C to a version that needs nothing. The final graph is
            // Root -> A -> C 2.0.0, which is complete.
            WriteNupkgToFeed(feed, "Stale.Root", "1.0.0", ("Stale.A", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "Stale.A", "1.0.0", ("Stale.C", "[1.0.0, )"));
            // Stale.Missing is never published to the feed, so walking C 1.0.0 records a dependency failure.
            WriteNupkgToFeed(feed, "Stale.C", "1.0.0", ("Stale.Missing", "[5.0.0, )"), ("Stale.D", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "Stale.C", "2.0.0");
            WriteNupkgToFeed(feed, "Stale.D", "1.0.0", ("Stale.C", "[2.0.0, )"));

            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            Dictionary<string, string> installed;
            try
            {
                installed = await service.InstallPackageAsync("Stale.Root", "1.0.0", TestTaskContext, TestContext.CancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                Assert.Fail($"The resolved graph (C 2.0.0) has no missing dependency, but install failed: {ex.Message}");
                return;
            }

            Assert.AreEqual("2.0.0", installed.GetValueOrDefault("Stale.C"));
            Assert.IsFalse(installed.ContainsKey("Stale.Missing"));
            Assert.IsFalse(installed.ContainsKey("Stale.D"), "D was required only by the replaced C 1.0.0.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// The companion to the test above: a failure under the version that IS selected must still fail the
    /// install, so retracting stale failures cannot be used to hide a genuinely incomplete graph.
    /// </summary>
    [TestMethod]
    public async Task InstallPackageAsync_FailureUnderSelectedVersion_StillFailsTheInstall()
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

            WriteNupkgToFeed(feed, "Live.Root", "1.0.0", ("Live.C", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "Live.C", "1.0.0", ("Live.Missing", "[5.0.0, )"));

            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => service.InstallPackageAsync("Live.Root", "1.0.0", TestTaskContext, TestContext.CancellationToken));

            StringAssert.Contains(ex.Message, "Live.Missing", StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// A conflict is a snapshot of one moment in an order-dependent walk, so it can stop being true. Here Q
    /// requires X [2.0.0] while X is pinned to 1.0.0 by P, which records a conflict against Q. A later branch
    /// then upgrades A, orphaning P and removing that pin, and pulls X up to 2.0.0 — satisfying the very
    /// requirement Q was rejected for. Q itself is still selected and reachable, so retracting failures by
    /// declaring package alone cannot catch this; the requirement has to be re-checked against the final
    /// selection.
    /// </summary>
    [TestMethod]
    public async Task InstallPackageAsync_ConflictLaterSatisfiedByAnotherBranch_DoesNotFailTheInstall()
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

            // Chained so the walk order is fixed: Root -> A 1.0 -> P -> X 1.0 -> Q. Q then asks for X [2.0.0]
            // while P still pins X [1.0.0], which is the conflict. Q also pulls in Z, which requires A >= 2.0.0
            // and so replaces A 1.0 with an A 2.0 that drops P entirely and requires X [2.0.0] itself.
            WriteNupkgToFeed(feed, "Late.Root", "1.0.0", ("Late.A", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "Late.A", "1.0.0", ("Late.P", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "Late.A", "2.0.0", ("Late.X", "[2.0.0]"));
            WriteNupkgToFeed(feed, "Late.P", "1.0.0", ("Late.X", "[1.0.0]"));
            WriteNupkgToFeed(feed, "Late.X", "1.0.0", ("Late.Q", "[1.0.0, )"));
            // X 2.0.0 keeps requiring Q so Q stays reachable and selected in the FINAL graph — otherwise the
            // stale conflict would be retracted just by Q disappearing, and the requirement re-check this test
            // exists for would never be exercised.
            WriteNupkgToFeed(feed, "Late.X", "2.0.0", ("Late.Q", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "Late.Q", "1.0.0", ("Late.X", "[2.0.0]"), ("Late.Z", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "Late.Z", "1.0.0", ("Late.A", "[2.0.0, )"));

            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            Dictionary<string, string> installed;
            try
            {
                installed = await service.InstallPackageAsync("Late.Root", "1.0.0", TestTaskContext, TestContext.CancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                Assert.Fail($"Every requirement in the final graph is satisfied, but install reported a failure: {ex.Message}");
                return;
            }

            Assert.AreEqual("2.0.0", installed.GetValueOrDefault("Late.A"));
            Assert.AreEqual("2.0.0", installed.GetValueOrDefault("Late.X"), "X must end up at the version Q asked for.");
            Assert.AreEqual("1.0.0", installed.GetValueOrDefault("Late.Q"), "Q must still be part of the final graph, so its stale conflict is retired by re-checking the requirement rather than by Q disappearing.");
            Assert.IsFalse(installed.ContainsKey("Late.P"), "P was required only by the replaced A 1.0.0.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// An upgrade that cannot be installed must fail the operation, not vanish. The upgrade path removes the
    /// previously selected version before installing the replacement, and that install had no error handling
    /// of its own — so a failed download propagated to whichever ancestor happened to be inside a try, was
    /// recorded against that ancestor's own (satisfied) dependency, and was then retired as satisfied. The
    /// package was left out of the graph entirely and the install reported success.
    /// </summary>
    [TestMethod]
    public async Task InstallPackageAsync_UpgradeDownloadFails_FailsInsteadOfSilentlyDroppingThePackage()
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

            // Chained so the walk order is fixed: Root -> A -> Shared 1.0.0, then A -> B which requires
            // Shared >= 2.0.0 and triggers the upgrade.
            WriteNupkgToFeed(feed, "Drop.Root", "1.0.0", ("Drop.A", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "Drop.A", "1.0.0", ("Drop.Shared", "[1.0.0, )"), ("Drop.B", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "Drop.B", "1.0.0", ("Drop.Shared", "[2.0.0, )"));
            WriteNupkgToFeed(feed, "Drop.Shared", "1.0.0");

            // Shared 2.0.0 is a valid package, so version resolution selects it normally. Extraction is what
            // fails: a FILE is planted where the global-packages folder for that version must be created, so
            // the failure happens inside the upgrade's install rather than during version resolution.
            WriteNupkgToFeed(feed, "Drop.Shared", "2.0.0");

            WriteLocalFeedConfig(root, feed, packages);

            var blockedDir = Path.Join(packages.FullName, "drop.shared");
            Directory.CreateDirectory(blockedDir);
            File.WriteAllText(Path.Join(blockedDir, "2.0.0"), "not a directory");

            var service = CreateServiceRootedAt(root);

            Dictionary<string, string>? installed = null;
            InvalidOperationException? failure = null;
            try
            {
                installed = await service.InstallPackageAsync("Drop.Root", "1.0.0", TestTaskContext, TestContext.CancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                failure = ex;
            }

            if (failure is null)
            {
                // Reporting success is only acceptable if the graph is actually complete. Silently omitting the
                // shared package is the bug under test.
                Assert.Fail(
                    "Install reported success after the upgrade failed. Installed: "
                    + string.Join(", ", installed!.Select(kv => $"{kv.Key}={kv.Value}")));
                return;
            }

            StringAssert.Contains(failure.Message, "Drop.Shared", StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task InstallPackageAsync_CacheFolderExistsWithoutCompletionMarker_ReDownloadsInsteadOfTrustingIt()
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

            WriteNupkgToFeed(feed, "Partial.Pkg", "1.0.0");
            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            // Simulate an interrupted extraction: the version folder exists but the ".nupkg.metadata"
            // completion marker (written last by NuGet) was never produced, so the entry is incomplete.
            var packageDir = service.GetNuGetPackageDir("Partial.Pkg", "1.0.0");
            packageDir.Create();
            var marker = Path.Join(packageDir.FullName, ".nupkg.metadata");
            Assert.IsFalse(File.Exists(marker), "Precondition: the incomplete cache folder must have no completion marker.");

            var installed = await service.InstallPackageAsync("Partial.Pkg", "1.0.0", TestTaskContext, TestContext.CancellationToken);

            // The bare directory must NOT have short-circuited the install: the package should now be fully
            // extracted, evidenced by both the completion marker and the extracted nuspec being present.
            Assert.IsTrue(installed.ContainsKey("Partial.Pkg"), "The package must be reported installed.");
            Assert.IsTrue(File.Exists(marker), "The completion marker must exist, proving the package was actually (re-)downloaded rather than skipped.");
            Assert.IsTrue(File.Exists(Path.Join(packageDir.FullName, "Partial.Pkg.nuspec")), "The extracted nuspec must exist, proving real extraction into the previously-empty folder.");
        }
        finally
        {
            TryDelete(root);
        }
    }
}

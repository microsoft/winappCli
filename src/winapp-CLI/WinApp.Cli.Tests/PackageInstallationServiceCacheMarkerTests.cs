// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.Services;
using static WinApp.Cli.Tests.NugetFeedTestHelpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// End-to-end tests for <see cref="PackageInstallationService"/> against a real <see cref="NugetService"/>
/// rooted at a local folder feed. Focused on the "already installed" short-circuits: they must gate on the
/// shared <see cref="INugetService.IsPackageInstalled"/> completion-marker predicate, not the bare directory,
/// so a partial cache entry left by an interrupted extraction is re-downloaded instead of being reported as a
/// complete install with a truncated dependency graph.
/// </summary>
[TestClass]
public class PackageInstallationServiceCacheMarkerTests : BaseCommandTests
{
    [TestMethod]
    public async Task InstallPackagesAsync_CacheFolderExistsWithoutCompletionMarker_ReDownloadsInsteadOfSkipping()
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

            WriteNupkgToFeed(feed, "Solo.Pkg", "1.0.0");
            WriteLocalFeedConfig(root, feed, packages);

            var nuget = CreateServiceRootedAt(root);
            var installer = new PackageInstallationService(
                _configService,
                nuget,
                GetRequiredService<ILogger<PackageInstallationService>>());

            var projectDir = new DirectoryInfo(Path.Join(root.FullName, "project"));
            projectDir.Create();

            // Simulate an interrupted extraction: the version folder exists but the ".nupkg.metadata"
            // completion marker (written last by NuGet) was never produced, so the entry is incomplete. The
            // pre-fix code short-circuited on the bare directory here and reported success without ever
            // downloading the package.
            var packageDir = nuget.GetNuGetPackageDir("Solo.Pkg", "1.0.0");
            packageDir.Create();
            var marker = Path.Join(packageDir.FullName, ".nupkg.metadata");
            Assert.IsFalse(File.Exists(marker), "Precondition: the incomplete cache folder must have no completion marker.");

            var installed = await installer.InstallPackagesAsync(
                projectDir,
                ["Solo.Pkg"],
                TestTaskContext,
                ignoreConfig: true,
                cancellationToken: TestContext.CancellationToken);

            // The marker-less directory must NOT have short-circuited the install: the package should now be
            // fully extracted, evidenced by both the completion marker and the extracted nuspec being present.
            Assert.IsTrue(installed.ContainsKey("Solo.Pkg"), "The package must be reported installed.");
            Assert.IsTrue(File.Exists(marker), "The completion marker must exist, proving the package was actually (re-)downloaded rather than skipped.");
            Assert.IsTrue(File.Exists(Path.Join(packageDir.FullName, "Solo.Pkg.nuspec")), "The extracted nuspec must exist, proving real extraction into the previously-empty folder.");
        }
        finally
        {
            TryDelete(root);
        }
    }
}

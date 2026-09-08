// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal sealed class PackageInstallationService(
    IConfigService configService,
    INugetService nugetService,
    ILogger<PackageInstallationService> logger) : IPackageInstallationService
{
    /// <summary>
    /// Initialize workspace and ensure required directories exist
    /// </summary>
    /// <param name="rootDirectory">The Root Directory path</param>
    public void InitializeWorkspace(DirectoryInfo rootDirectory)
    {
        if (!rootDirectory.Exists)
        {
            rootDirectory.Create();
        }
    }

    /// <summary>
    /// Install a single package if not already present
    /// </summary>
    /// <param name="rootDirectory">The Root Directory path</param>
    /// <param name="packageName">Name of the package to install</param>
    /// <param name="version">Version to install (if null, gets latest)</param>
    /// <param name="sdkInstallMode">SDK install mode</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The installed version</returns>
    private async Task<string> InstallPackageAsync(
        DirectoryInfo rootDirectory,
        string packageName,
        TaskContext taskContext,
        string? version = null,
        SdkInstallMode sdkInstallMode = SdkInstallMode.Stable,
        CancellationToken cancellationToken = default)
    {
        // Get version if not specified
        if (version == null)
        {
            version = await nugetService.GetLatestVersionAsync(packageName, sdkInstallMode, cancellationToken);
        }

        // Report the root package's cache state up front so the user still sees a "already present" line
        // rather than an "Installing..." line for a package that is already on disk.
        if (nugetService.IsPackageInstalled(packageName, version))
        {
            taskContext.AddStatusMessage($"{UiSymbols.Skip} {packageName} {version} already present");
        }
        else
        {
            taskContext.AddStatusMessage($"{UiSymbols.Package} Installing {packageName} {version}...");
        }

        // Always delegate, including on a cache hit. InstallPackageAsync short-circuits a completed root via
        // the completion marker (so this does not re-download it) but still walks the graph from the root's
        // extracted local .nuspec. Returning early here instead would report success for a root whose
        // transitive packages were deleted or never finished installing, leaving the graph unrepaired.
        await nugetService.InstallPackageAsync(packageName, version, taskContext, cancellationToken);
        return version;
    }

    /// <summary>
    /// Install multiple packages
    /// </summary>
    /// <param name="rootDirectory">The Root Directory path</param>
    /// <param name="packages">List of packages to install</param>
    /// <param name="sdkInstallMode">SDK install mode</param>
    /// <param name="ignoreConfig">Ignore configuration file for version management</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dictionary of installed packages and their versions</returns>
    public async Task<Dictionary<string, string>> InstallPackagesAsync(
        DirectoryInfo rootDirectory,
        IEnumerable<string> packages,
        TaskContext taskContext,
        SdkInstallMode sdkInstallMode = SdkInstallMode.Stable,
        bool ignoreConfig = false,
        CancellationToken cancellationToken = default)
    {
        var allInstalledVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Load pinned config if available
        WinappConfig? pinnedConfig = null;
        if (!ignoreConfig && configService.Exists())
        {
            pinnedConfig = configService.Load();
        }

        foreach (var packageName in packages)
        {
            // Resolve version: check pinned config first, then get latest
            string version;
            if (pinnedConfig != null && !ignoreConfig)
            {
                var pinnedVersion = pinnedConfig.GetVersion(packageName);
                if (!string.IsNullOrWhiteSpace(pinnedVersion))
                {
                    version = pinnedVersion!;
                }
                else
                {
                    version = await nugetService.GetLatestVersionAsync(packageName, sdkInstallMode, cancellationToken);
                }
            }
            else
            {
                version = await nugetService.GetLatestVersionAsync(packageName, sdkInstallMode, cancellationToken);
            }

            // Normalize the resolved version to NuGet's canonical on-disk form (e.g. a "1.0" pin becomes
            // "1.0.0"). allInstalledVersions is returned to callers that build global-cache paths by
            // concatenating this value, so a shorthand pin would otherwise point them at a folder the
            // NuGet writer never created.
            version = NugetService.NormalizeVersion(version);

            // Install the package (and its transitive graph). InstallPackageAsync already short-circuits a
            // fully-cached package via the completion marker and, on that marker hit, reads its dependency
            // LIST from the package's extracted local .nuspec rather than from the configured feeds. Each
            // dependency's declared range is still resolved to a concrete version against the feeds, falling
            // back to a completed cache entry when they cannot answer, so an already-extracted graph restores
            // offline. Resolving cache hits here through the feed-based GetPackageDependenciesAsync instead
            // would break documented cache reuse under a private nuget.config (a cached id/version absent or
            // unmapped on the current feed would fail the restore) and could install a graph that diverges
            // from what is actually on disk.
            taskContext.AddStatusMessage($"{UiSymbols.Bullet} {packageName} {version}");

            var installedVersions = await nugetService.InstallPackageAsync(packageName, version, taskContext, cancellationToken);
            foreach (var (pkg, ver) in installedVersions)
            {
                if (allInstalledVersions.TryGetValue(pkg, out var existingVersion))
                {
                    if (NugetService.CompareVersions(ver, existingVersion) > 0)
                    {
                        allInstalledVersions[pkg] = ver;
                    }
                }
                else
                {
                    allInstalledVersions[pkg] = ver;
                }
            }
        }

        return allInstalledVersions;
    }

    /// <summary>
    /// Install a single package and verify it was installed correctly
    /// </summary>
    /// <param name="rootDirectory">The Root Directory path</param>
    /// <param name="packageName">Name of the package to install</param>
    /// <param name="version">Specific version to install (if null, gets latest or uses pinned version from config)</param>
    /// <param name="sdkInstallMode">SDK install mode</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the package was installed successfully, false otherwise</returns>
    public async Task<bool> EnsurePackageAsync(
        DirectoryInfo rootDirectory,
        string packageName,
        TaskContext taskContext,
        string? version = null,
        SdkInstallMode sdkInstallMode = SdkInstallMode.Stable,
        CancellationToken cancellationToken = default)
    {
        try
        {
            InitializeWorkspace(rootDirectory);

            await InstallPackageAsync(
                rootDirectory,
                packageName,
                taskContext,
                version: version,
                sdkInstallMode,
                cancellationToken);

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError("Failed to install {PackageName}: {ErrorMessage}", packageName, NugetErrorMessage.Redact(ex.Message));
            return false;
        }
    }
}

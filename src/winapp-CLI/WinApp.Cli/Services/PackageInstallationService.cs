// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal sealed class PackageInstallationService(
    IConfigService configService,
    INugetService nugetService,
    IPackageCacheService cacheService,
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

        var packagesDir = rootDirectory.CreateSubdirectory("packages");
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
        var packagesDir = new DirectoryInfo(Path.Combine(rootDirectory.FullName, "packages"));

        // Ensure nuget.exe is available
        await nugetService.EnsureNugetExeAsync(rootDirectory, cancellationToken);

        // Get version if not specified
        if (version == null)
        {
            version = await nugetService.GetLatestVersionAsync(packageName, sdkInstallMode, cancellationToken);
        }

        // Check if already installed
        var expectedFolder = Path.Combine(packagesDir.FullName, $"{packageName}.{version}");
        if (Directory.Exists(expectedFolder))
        {
            taskContext.AddStatusMessage($"{UiSymbols.Skip} {packageName} {version} already present");
            return version;
        }

        // Install the package
        taskContext.AddStatusMessage($"{UiSymbols.Package} Installing {packageName} {version}...");

        await nugetService.InstallPackageAsync(rootDirectory, packageName, version, packagesDir, taskContext, cancellationToken);
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
        var packagesDir = new DirectoryInfo(Path.Combine(rootDirectory.FullName, "packages"));
        var allInstalledVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Ensure nuget.exe is available once for all packages
        taskContext.AddDebugMessage($"{UiSymbols.Wrench} Ensuring nuget.exe is available...");
        await nugetService.EnsureNugetExeAsync(rootDirectory, cancellationToken);

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

            // Check if already installed
            var expectedFolder = Path.Combine(packagesDir.FullName, $"{packageName}.{version}");
            if (Directory.Exists(expectedFolder))
            {
                taskContext.AddStatusMessage($"{UiSymbols.Skip} {packageName} {version} already present");
                
                // Add the main package to installed versions
                allInstalledVersions[packageName] = version;
                
                // Try to get cached information about what else was installed with this package
                try
                {
                    var cachedPackages = await cacheService.GetCachedPackageAsync(packageName, version, taskContext, cancellationToken);
                    foreach (var (cachedPkg, cachedVer) in cachedPackages)
                    {
                        if (allInstalledVersions.TryGetValue(cachedPkg, out var existingVersion))
                        {
                            if (NugetService.CompareVersions(cachedVer, existingVersion) > 0)
                            {
                                allInstalledVersions[cachedPkg] = cachedVer;
                            }
                        }
                        else
                        {
                            allInstalledVersions[cachedPkg] = cachedVer;
                        }
                    }
                }
                catch (KeyNotFoundException)
                {
                    // Package not in cache yet, that's okay - just continue with main package
                }
                
                continue;
            }

            // Install the package
            taskContext.AddStatusMessage($"{UiSymbols.Bullet} {packageName} {version}");

            var installedVersions = await nugetService.InstallPackageAsync(rootDirectory, packageName, version, packagesDir, taskContext, cancellationToken);
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

            // Update cache with this package installation
            await cacheService.UpdatePackageAsync(packageName, version, installedVersions, taskContext, cancellationToken);
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

            var installedVersion = await InstallPackageAsync(
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
            logger.LogError("Failed to install {PackageName}: {ErrorMessage}", packageName, ex.Message);
            return false;
        }
    }
}

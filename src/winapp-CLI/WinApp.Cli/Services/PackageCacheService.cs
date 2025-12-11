// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

[JsonSerializable(typeof(PackageCache))]
[JsonSerializable(typeof(Dictionary<string, Dictionary<string, string>>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class PackageCacheJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Manages a JSON cache of installed packages in the .winapp/packages folder
/// </summary>
internal sealed class PackageCacheService : IPackageCacheService
{
    private const string CacheFileName = "package-cache.json";
    private readonly FileInfo _cacheFilePath;

    public PackageCacheService(IWinappDirectoryService directoryService)
    {
        var globalWinappDirectory = directoryService.GetGlobalWinappDirectory();
        var packagesDir = Path.Combine(globalWinappDirectory.FullName, "packages");
        _cacheFilePath = new FileInfo(Path.Combine(packagesDir, CacheFileName));
    }

    /// <summary>
    /// Load the package cache from disk
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The cached package information</returns>
    public async Task<PackageCache> LoadAsync(TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        _cacheFilePath.Refresh();
        if (!_cacheFilePath.Exists)
        {
            return new PackageCache();
        }

        try
        {
            using var fileStream = _cacheFilePath.OpenRead();
            return await JsonSerializer.DeserializeAsync(fileStream, PackageCacheJsonContext.Default.PackageCache, cancellationToken) ?? new PackageCache();
        }
        catch (Exception ex)
        {
            taskContext.StatusError($"Warning: Failed to load package cache: {ex.Message}");
            return new PackageCache();
        }
    }

    /// <summary>
    /// Save the package cache to disk
    /// </summary>
    /// <param name="cache">The cache to save</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task SaveAsync(PackageCache cache, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        try
        {
            // Ensure the packages directory exists
            if (_cacheFilePath.Directory?.Exists != true)
            {
                _cacheFilePath.Directory?.Create();
            }

            using var stream = _cacheFilePath.Open(FileMode.Create, FileAccess.Write);
            _cacheFilePath.Refresh();
            await JsonSerializer.SerializeAsync(stream, cache, PackageCacheJsonContext.Default.PackageCache, cancellationToken);

            taskContext.AddStatusMessage($"{UiSymbols.Save} Package cache updated");
        }
        catch (Exception ex)
        {
            taskContext.StatusError($"Warning: Failed to save package cache: {ex.Message}");
        }
    }

    /// <summary>
    /// Update the cache with a package installation
    /// </summary>
    /// <param name="packageName">The main package name that was requested</param>
    /// <param name="version">The main package version that was requested</param>
    /// <param name="installedPackages">Dictionary of all packages that were installed (including dependencies)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task UpdatePackageAsync(string packageName, string version, Dictionary<string, string> installedPackages, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        var cache = await LoadAsync(taskContext, cancellationToken);
        var packageKey = $"{packageName}.{version}";

        // Filter out the main package from the installed packages to avoid self-reference
        var filteredPackages = installedPackages
            .Where(kvp => !kvp.Key.Equals(packageName, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        // Store only the dependencies/related packages, not the main package itself
        cache.InstalledPackages[packageKey] = filteredPackages;

        await SaveAsync(cache, taskContext, cancellationToken);
    }

    /// <summary>
    /// Get cached package installation info
    /// </summary>
    /// <param name="packageName">Name of the package</param>
    /// <param name="version">Version of the package</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dictionary of installed packages if cached, throws if not found</returns>
    public async Task<Dictionary<string, string>> GetCachedPackageAsync(string packageName, string version, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        var cache = await LoadAsync(taskContext, cancellationToken);
        var packageKey = $"{packageName}.{version}";
        if (cache.InstalledPackages.TryGetValue(packageKey, out var cachedPackages))
        {
            return cachedPackages;
        }
        
        throw new KeyNotFoundException($"Package {packageName} version {version} not found in cache");
    }
}

/// <summary>
/// Represents the overall package cache structure
/// </summary>
internal sealed class PackageCache
{
    public Dictionary<string, Dictionary<string, string>> InstalledPackages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}


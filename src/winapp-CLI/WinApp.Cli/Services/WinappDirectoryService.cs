// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace WinApp.Cli.Services;

/// <summary>
/// Service responsible for resolving winapp directory paths
/// </summary>
internal class WinappDirectoryService(ICurrentDirectoryProvider currentDirectoryProvider) : IWinappDirectoryService
{
    private const string CacheConfigFileName = "cache-config.json";
    private DirectoryInfo? _globalOverride;
    private DirectoryInfo? _packagesOverride;

    /// <summary>
    /// Method to override the cache directory for testing purposes
    /// </summary>
    /// <param name="cacheDirectory">The directory to use as the winapp cache</param>
    public void SetCacheDirectoryForTesting(DirectoryInfo? cacheDirectory)
    {
        _globalOverride = cacheDirectory;
        _packagesOverride = cacheDirectory != null ? new DirectoryInfo(Path.Combine(cacheDirectory.FullName, "packages")) : null;
    }

    public DirectoryInfo GetGlobalWinappDirectory()
    {
        // Instance override takes precedence (for testing)
        if (_globalOverride != null)
        {
            return _globalOverride;
        }

        // Allow override via environment variable (useful for CI/CD)
        var cacheDirectory = Environment.GetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY");
        if (!string.IsNullOrEmpty(cacheDirectory))
        {
            return new DirectoryInfo(cacheDirectory);
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var winappDir = Path.Combine(userProfile, ".winapp");
        return new DirectoryInfo(winappDir);
    }

    public DirectoryInfo GetPackagesDirectory()
    {
        // Test override takes precedence
        if (_packagesOverride != null)
        {
            return _packagesOverride;
        }

        // Check for custom cache location in configuration
        var customLocation = GetCustomCacheLocationFromConfig();
        if (!string.IsNullOrEmpty(customLocation))
        {
            return new DirectoryInfo(customLocation);
        }

        // Default to packages subdirectory in global winapp directory
        var globalWinappDirectory = GetGlobalWinappDirectory();
        return new DirectoryInfo(Path.Combine(globalWinappDirectory.FullName, "packages"));
    }

    private string? GetCustomCacheLocationFromConfig()
    {
        try
        {
            var globalWinappDir = GetGlobalWinappDirectory();
            var configPath = Path.Combine(globalWinappDir.FullName, CacheConfigFileName);
            
            if (!File.Exists(configPath))
            {
                return null;
            }

            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize(json, CacheConfigurationJsonContext.Default.CacheConfiguration);
            return config?.CustomCacheLocation;
        }
        catch
        {
            // If we can't read the config, fall back to default
            return null;
        }
    }

    public DirectoryInfo GetLocalWinappDirectory(DirectoryInfo? baseDirectory = null)
    {
        baseDirectory ??= new DirectoryInfo(currentDirectoryProvider.GetCurrentDirectory());

        var originalBaseDir = new DirectoryInfo(baseDirectory.FullName);
        var dir = originalBaseDir;
        while (dir != null)
        {
            var winappDirectory = Path.Combine(dir.FullName, ".winapp");
            if (Directory.Exists(winappDirectory))
            {
                return new DirectoryInfo(winappDirectory);
            }
            dir = dir.Parent;
        }

        return new DirectoryInfo(Path.Combine(originalBaseDir.FullName, ".winapp"));
    }
}

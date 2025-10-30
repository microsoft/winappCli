// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Winsdk.Cli.Services;

/// <summary>
/// Service responsible for resolving winsdk directory paths
/// </summary>
internal class WinsdkDirectoryService(ICurrentDirectoryProvider currentDirectoryProvider) : IWinsdkDirectoryService
{
    private DirectoryInfo? _globalOverride;

    /// <summary>
    /// Method to override the cache directory for testing purposes
    /// </summary>
    /// <param name="cacheDirectory">The directory to use as the winsdk cache</param>
    public void SetCacheDirectoryForTesting(DirectoryInfo? cacheDirectory)
    {
        _globalOverride = cacheDirectory;
    }

    public DirectoryInfo GetGlobalWinsdkDirectory()
    {
        // Instance override takes precedence (for testing)
        if (_globalOverride != null)
        {
            return _globalOverride;
        }

        // Allow override via environment variable (useful for CI/CD)
        var cacheDirectory = Environment.GetEnvironmentVariable("WINSDK_CACHE_DIRECTORY");
        if (!string.IsNullOrEmpty(cacheDirectory))
        {
            return new DirectoryInfo(cacheDirectory);
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var winsdkDir = Path.Combine(userProfile, ".winsdk");
        return new DirectoryInfo(winsdkDir);
    }

    public DirectoryInfo GetLocalWinsdkDirectory(DirectoryInfo? baseDirectory = null)
    {
        baseDirectory ??= new DirectoryInfo(currentDirectoryProvider.GetCurrentDirectory());

        var originalBaseDir = new DirectoryInfo(baseDirectory.FullName);
        var dir = originalBaseDir;
        while (dir != null)
        {
            var winsdkDirectory = Path.Combine(dir.FullName, ".winsdk");
            if (Directory.Exists(winsdkDirectory))
            {
                return new DirectoryInfo(winsdkDirectory);
            }
            dir = dir.Parent;
        }

        return new DirectoryInfo(Path.Combine(originalBaseDir.FullName, ".winsdk"));
    }
}

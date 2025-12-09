// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

/// <summary>
/// Service responsible for resolving winapp directory paths
/// </summary>
internal class WinappDirectoryService(ICurrentDirectoryProvider currentDirectoryProvider) : IWinappDirectoryService
{
    private DirectoryInfo? _globalOverride;
    private string? _userProfileOverride;
    private static bool _hasShownLegacyWarning;

    /// <summary>
    /// Method to override the cache directory for testing purposes
    /// </summary>
    /// <param name="cacheDirectory">The directory to use as the winapp cache</param>
    public void SetCacheDirectoryForTesting(DirectoryInfo? cacheDirectory)
    {
        _globalOverride = cacheDirectory;
    }

    /// <summary>
    /// Method to override the user profile path for testing purposes
    /// </summary>
    /// <param name="userProfilePath">The path to use as the user profile directory</param>
    public void SetUserProfileForTesting(string? userProfilePath)
    {
        _userProfileOverride = userProfilePath;
    }

    /// <summary>
    /// Checks if we're using the legacy global folder and prints a warning message (once per process).
    /// Should be called during init and restore operations.
    /// </summary>
    /// <param name="logger">Logger instance for outputting the warning</param>
    public void CheckAndWarnIfUsingLegacyGlobalFolder(Microsoft.Extensions.Logging.ILogger logger)
    {
        if (_hasShownLegacyWarning)
        {
            return;
        }

        // Check if WINAPP_CLI_CACHE_DIRECTORY is set.  If so, the warning is not applicable.
        var cacheDirectory = Environment.GetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY");
        if (!string.IsNullOrEmpty(cacheDirectory))
        {
            return;
        }

        var userProfile = _userProfileOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var newWinappDir = Path.Combine(userProfile, ".winappglobal");
        var legacyWinappDir = Path.Combine(userProfile, ".winapp");

        // Check if we're falling back to legacy location
        if (!Directory.Exists(newWinappDir) && Directory.Exists(legacyWinappDir))
        {
            var legacyPackagesDir = Path.Combine(legacyWinappDir, "packages");
            if (Directory.Exists(legacyPackagesDir))
            {
                logger.LogWarning(
                    "Falling back to legacy global folder location: {LegacyWinappDir}. WinAppCLI is in the process of " +
                    "migrating the default global dir to a new location: {NewWinappDir}. When you are only using " +
                    "WinAppCLI 0.1.8 and later, please move the folder to the new location.  You could also set it " +
                    "to a custom location by setting the WINAPP_CLI_CACHE_DIRECTORY environment variable.",
                    legacyWinappDir, newWinappDir);
                _hasShownLegacyWarning = true;
            }
        }
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

        var userProfile = _userProfileOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var newWinappDir = Path.Combine(userProfile, ".winappglobal");
        var legacyWinappDir = Path.Combine(userProfile, ".winapp");

        // Phase 1: Fallback to legacy location if new location doesn't exist and legacy has packages
        if (!Directory.Exists(newWinappDir) && Directory.Exists(legacyWinappDir))
        {
            // Only use legacy directory if it contains a "packages" subdirectory
            var legacyPackagesDir = Path.Combine(legacyWinappDir, "packages");
            if (Directory.Exists(legacyPackagesDir))
            {
                return new DirectoryInfo(legacyWinappDir);
            }
        }

        return new DirectoryInfo(newWinappDir);
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
                // Does this dir have a "packages" subdirectory? If so, it's probably the old global dir
                bool hasPackagesSubdir = Directory.Exists(Path.Combine(winappDirectory, "packages"));
                if (hasPackagesSubdir)
                {
                    // This looks like the old global dir, so skip it.
                }
                else
                {
                    return new DirectoryInfo(winappDirectory);
                }
            }
            dir = dir.Parent;
        }

        return new DirectoryInfo(Path.Combine(originalBaseDir.FullName, ".winapp"));
    }
}

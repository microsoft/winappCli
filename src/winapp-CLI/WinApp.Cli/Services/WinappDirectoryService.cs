// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

/// <summary>
/// Service responsible for resolving winapp directory paths
/// </summary>
internal class WinappDirectoryService(ICurrentDirectoryProvider currentDirectoryProvider) : IWinappDirectoryService
{
    private DirectoryInfo? _globalOverride;
    private string? _userProfileOverride;

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

        // Phase 1: Fallback to legacy location if new location doesn't exist
        if (!Directory.Exists(newWinappDir) && Directory.Exists(legacyWinappDir))
        {
            Console.WriteLine($"Falling back to legacy global folder location: {legacyWinappDir}");
            Console.WriteLine($"In the future the default location will be: {newWinappDir}");
            Console.WriteLine("When you are only using WinAppCLI 0.1.8 and later, please move the folder to the new location.");
            return new DirectoryInfo(legacyWinappDir);
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
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine(
                        $"Warning: Found .winapp folder in UserProfile directory: {winappDirectory}. " +
                        "The global winapp folder is now named .winappglobal. " +
                        "Please remove the .winapp folder from your UserProfile directory or rename to .winappglobal.");
                    Console.ResetColor();
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

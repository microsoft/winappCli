// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Service responsible for resolving winapp directory paths
/// </summary>
internal class WinappDirectoryService(ICurrentDirectoryProvider currentDirectoryProvider) : IWinappDirectoryService
{
    private DirectoryInfo? _globalOverride;

    internal Func<string> UserProfileProvider { get; set; } =
        () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Method to override the cache directory for testing purposes
    /// </summary>
    /// <param name="cacheDirectory">The directory to use as the winapp cache</param>
    public void SetCacheDirectoryForTesting(DirectoryInfo? cacheDirectory)
    {
        _globalOverride = cacheDirectory;
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

        var userProfile = UserProfileProvider();
        var winappDir = Path.Combine(userProfile, ".winapp");
        return new DirectoryInfo(winappDir);
    }

    /// <summary>Shared state independent of the cache override and package identity.</summary>
    internal static string GetUserStateDirectory(string? userProfile = null)
    {
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile) || !Path.IsPathFullyQualified(userProfile))
        {
            throw new IOException("The user profile folder could not be resolved to a fully qualified path.");
        }

        return Path.Combine(userProfile, ".winapp", "state");
    }

    public DirectoryInfo GetLocalWinappDirectory(DirectoryInfo? baseDirectory = null)
    {
        baseDirectory ??= new DirectoryInfo(currentDirectoryProvider.GetCurrentDirectory());

        DirectoryInfo globalWinappDirectory = GetGlobalWinappDirectory();
        var userWinappDirectory = Path.Combine(UserProfileProvider(), ".winapp");

        var originalBaseDir = new DirectoryInfo(baseDirectory.FullName);
        var dir = originalBaseDir;
        while (dir != null)
        {
            var winappDirectory = Path.Combine(dir.FullName, ".winapp");
            if (Directory.Exists(winappDirectory))
            {
                bool isGlobalWinAppDir =
                    string.Equals(winappDirectory, globalWinappDirectory.FullName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(winappDirectory, userWinappDirectory, StringComparison.OrdinalIgnoreCase);
                if (isGlobalWinAppDir)
                {
                    // We don't currently allow the global winapp directory to be used as a local winapp directory,
                    // so continue searching upwards.
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

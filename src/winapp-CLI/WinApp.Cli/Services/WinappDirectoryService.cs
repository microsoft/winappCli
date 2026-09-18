// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

/// <summary>
/// Service responsible for resolving winapp directory paths
/// </summary>
internal class WinappDirectoryService(ICurrentDirectoryProvider currentDirectoryProvider) : IWinappDirectoryService
{
    private DirectoryInfo? _globalOverride;
    internal DirectoryInfo? CacheDirectoryOverrideForTesting => _globalOverride;

    internal Func<string> UserProfileProvider { get; set; } =
        () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    internal Func<string?> CacheOverrideProvider { get; set; } =
        () => Environment.GetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY");

    public bool IsGlobalCacheOverridden => _globalOverride is not null || CacheOverrideProvider() is not null;

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
        var cacheDirectory = CacheOverrideProvider();
        if (cacheDirectory is not null)
        {
            if (string.IsNullOrWhiteSpace(cacheDirectory) || !Path.IsPathFullyQualified(cacheDirectory))
            {
                throw new InvalidOperationException("WINAPP_CLI_CACHE_DIRECTORY must be a fully qualified directory path.");
            }
            try { return new DirectoryInfo(cacheDirectory); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                throw new InvalidOperationException("WINAPP_CLI_CACHE_DIRECTORY is not a valid directory path.", ex);
            }
        }

        var userProfile = UserProfileProvider();
        if (string.IsNullOrWhiteSpace(userProfile) || !Path.IsPathFullyQualified(userProfile))
        {
            throw new IOException("The user profile folder could not be resolved for the default winapp cache.");
        }
        var winappDir = Path.Combine(userProfile, ".winapp");
        return new DirectoryInfo(winappDir);
    }

    public DirectoryInfo GetLocalCacheDirectory()
    {
        var cwd = Path.GetFullPath(currentDirectoryProvider.GetCurrentDirectory());
        // The invocation directory is the sandbox boundary. Do not inspect its parents.
        if (PathSafety.IsNetworkPath(cwd)
            || Windows.Win32.PInvoke.GetDriveType(Path.GetPathRoot(cwd)!) == Windows.Win32.PInvoke.DRIVE_REMOTE
            || PathSafety.IsReparsePoint(cwd))
        {
            throw new IOException("The invocation directory is not a verifiable local cache location.");
        }

        var path = Path.Combine(cwd, ".winapp", "cache");
        CacheStorage.ValidateLocalPath(path, cwd);
        return new DirectoryInfo(path);
    }

    /// <summary>
    /// Shared operational state, independent of cache overrides and package identity.
    /// Unlike LocalAppData, the profile-root .winapp directory is not MSIX-virtualized.
    /// </summary>
    internal static string GetUserStateDirectory(string? userProfile = null)
    {
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile) || !Path.IsPathFullyQualified(userProfile))
        {
            throw new IOException("The user profile folder could not be resolved to a fully qualified path.");
        }

        return ValidateStateDirectory(Path.Combine(userProfile, ".winapp", "state"));
    }

    internal static string ValidateStateDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new IOException("The state directory must be a fully qualified local path.");
        }

        if (PathSafety.IsNetworkPath(path)
            || PathSafety.RedirectsToNetwork(path))
        {
            throw new IOException(
                $"The state directory '{path}' is not a verifiable local path. Network storage cannot safely coordinate winapp processes.");
        }

        return Path.GetFullPath(path);
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

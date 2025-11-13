// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Interface for managing the winapp package cache
/// </summary>
internal interface ICacheService
{
    /// <summary>
    /// Get the current package cache directory path
    /// </summary>
    DirectoryInfo GetCacheDirectory();

    /// <summary>
    /// Move the package cache to a new location
    /// </summary>
    /// <param name="newPath">The new path for the cache</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task MoveCacheAsync(string newPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clear all contents of the package cache
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task ClearCacheAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the custom cache location from configuration, if set
    /// </summary>
    string? GetCustomCacheLocation();

    /// <summary>
    /// Set a custom cache location in configuration
    /// </summary>
    /// <param name="path">The custom cache path</param>
    void SetCustomCacheLocation(string path);

    /// <summary>
    /// Remove the custom cache location from configuration
    /// </summary>
    void RemoveCustomCacheLocation();
}

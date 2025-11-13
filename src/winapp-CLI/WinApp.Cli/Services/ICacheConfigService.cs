// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Interface for managing cache configuration
/// </summary>
internal interface ICacheConfigService
{
    /// <summary>
    /// Gets the custom cache path if configured, otherwise returns null
    /// </summary>
    string? GetCustomCachePath();

    /// <summary>
    /// Sets a custom cache path
    /// </summary>
    /// <param name="path">The path to set as custom cache location</param>
    void SetCustomCachePath(string path);

    /// <summary>
    /// Clears the custom cache path configuration
    /// </summary>
    void ClearCustomCachePath();
}

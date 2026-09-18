// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Interface for resolving .winapp directory paths
/// </summary>
internal interface IWinappDirectoryService
{
    DirectoryInfo GetGlobalWinappDirectory();
    /// <summary>Whether cache configuration is authoritative and must not silently fall back.</summary>
    bool IsGlobalCacheOverridden { get; }
    /// <summary>Validates and resolves invocation-CWD .winapp\cache without creating it or searching parents.</summary>
    DirectoryInfo GetLocalCacheDirectory();
    DirectoryInfo GetLocalWinappDirectory(DirectoryInfo? baseDirectory = null);
    void SetCacheDirectoryForTesting(DirectoryInfo? cacheDirectory);
}

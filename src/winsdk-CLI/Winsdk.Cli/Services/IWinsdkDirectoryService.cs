// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Winsdk.Cli.Services;

/// <summary>
/// Interface for resolving winsdk directory paths
/// </summary>
internal interface IWinsdkDirectoryService
{
    DirectoryInfo GetGlobalWinsdkDirectory();
    DirectoryInfo GetLocalWinsdkDirectory(DirectoryInfo? baseDirectory = null);
    void SetCacheDirectoryForTesting(DirectoryInfo? cacheDirectory);
}

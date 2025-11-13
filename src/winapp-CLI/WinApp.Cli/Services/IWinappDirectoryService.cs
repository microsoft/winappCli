// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Interface for resolving .winapp directory paths
/// </summary>
internal interface IWinappDirectoryService
{
    DirectoryInfo GetGlobalWinappDirectory();
    DirectoryInfo GetLocalWinappDirectory(DirectoryInfo? baseDirectory = null);
    DirectoryInfo GetPackagesCacheDirectory();
    void SetCacheDirectoryForTesting(DirectoryInfo? cacheDirectory);
}

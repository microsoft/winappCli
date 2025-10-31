// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

internal interface IPackageCacheService
{
    Task<PackageCache> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(PackageCache cache, CancellationToken cancellationToken = default);
    Task UpdatePackageAsync(string packageName, string version, Dictionary<string, string> installedPackages, CancellationToken cancellationToken = default);
    Task<Dictionary<string, string>> GetCachedPackageAsync(string packageName, string version, CancellationToken cancellationToken = default);
}
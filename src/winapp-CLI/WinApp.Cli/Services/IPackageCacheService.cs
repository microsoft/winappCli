// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

internal interface IPackageCacheService
{
    Task<PackageCache> LoadAsync(TaskContext taskContext, CancellationToken cancellationToken = default);
    Task SaveAsync(PackageCache cache, TaskContext taskContext, CancellationToken cancellationToken = default);
    Task UpdatePackageAsync(string packageName, string version, Dictionary<string, string> installedPackages, TaskContext taskContext, CancellationToken cancellationToken = default);
    Task<Dictionary<string, string>> GetCachedPackageAsync(string packageName, string version, TaskContext taskContext, CancellationToken cancellationToken = default);
}

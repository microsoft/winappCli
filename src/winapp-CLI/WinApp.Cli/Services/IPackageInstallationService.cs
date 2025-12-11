// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

internal interface IPackageInstallationService
{
    void InitializeWorkspace(DirectoryInfo rootDirectory);
    
    Task<Dictionary<string, string>> InstallPackagesAsync(
        DirectoryInfo rootDirectory,
        IEnumerable<string> packages,
        TaskContext taskContext,
        bool includeExperimental = false,
        bool ignoreConfig = false,
        CancellationToken cancellationToken = default);
    
    Task<bool> EnsurePackageAsync(
        DirectoryInfo rootDirectory,
        string packageName,
        TaskContext taskContext,
        string? version = null,
        bool includeExperimental = false,
        CancellationToken cancellationToken = default);
}

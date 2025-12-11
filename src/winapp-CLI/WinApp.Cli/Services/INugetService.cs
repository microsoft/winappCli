// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

internal interface INugetService
{
    Task EnsureNugetExeAsync(DirectoryInfo winappDir, CancellationToken cancellationToken = default);
    Task<string> GetLatestVersionAsync(string packageName, bool includePrerelease, CancellationToken cancellationToken = default);
    Task<Dictionary<string, string>> InstallPackageAsync(DirectoryInfo winappDir, string package, string version, DirectoryInfo outputDir, TaskContext taskContext, CancellationToken cancellationToken = default);
}

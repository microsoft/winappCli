// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Deterministic <see cref="IPackageInstallationService"/> fake used to drive the install
/// success/failure branches of services (e.g. <c>BuildToolsService.EnsureBuildToolsAsync</c>)
/// without shelling out to NuGet or touching the network.
/// </summary>
internal sealed class ConfigurablePackageInstallationService : IPackageInstallationService
{
    /// <summary>The value returned by <see cref="EnsurePackageAsync"/>.</summary>
    public bool EnsurePackageResult { get; set; } = true;

    /// <summary>
    /// Optional callback invoked from <see cref="EnsurePackageAsync"/> before it returns,
    /// e.g. to materialize a fake bin layout on disk to simulate a successful install.
    /// </summary>
    public Action<DirectoryInfo, string>? OnEnsurePackage { get; set; }

    /// <summary>Packages requested through <see cref="EnsurePackageAsync"/>, in order.</summary>
    public List<string> EnsuredPackages { get; } = [];

    public Task<Dictionary<string, string>> InstallPackagesAsync(
        DirectoryInfo rootDirectory,
        IEnumerable<string> packages,
        TaskContext taskContext,
        SdkInstallMode sdkInstallMode = SdkInstallMode.Stable,
        bool ignoreConfig = false,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new Dictionary<string, string>());
    }

    public Task<bool> EnsurePackageAsync(
        DirectoryInfo rootDirectory,
        string packageName,
        TaskContext taskContext,
        string? version = null,
        SdkInstallMode sdkInstallMode = SdkInstallMode.Stable,
        CancellationToken cancellationToken = default)
    {
        EnsuredPackages.Add(packageName);
        OnEnsurePackage?.Invoke(rootDirectory, packageName);
        return Task.FromResult(EnsurePackageResult);
    }
}

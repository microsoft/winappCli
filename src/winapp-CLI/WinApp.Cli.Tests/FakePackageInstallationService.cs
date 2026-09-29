// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake package-installation service that records calls and returns predictable version
/// dictionaries without touching NuGet or the filesystem.
/// </summary>
internal sealed class FakePackageInstallationService : IPackageInstallationService
{
    public List<(DirectoryInfo Root, string[] Packages, bool IgnoreConfig)> InstallPackagesCalls { get; } = [];
    public List<(DirectoryInfo Root, string PackageName, string? Version)> EnsurePackageCalls { get; } = [];

    /// <summary>Flattened names of every package passed to <see cref="InstallPackagesAsync"/>, for convenient assertions.</summary>
    public List<string> InstalledPackages => InstallPackagesCalls.SelectMany(c => c.Packages).ToList();

    /// <summary>Version map returned by <see cref="InstallPackagesAsync"/>. When null, echoes the requested packages.</summary>
    public Dictionary<string, string>? InstalledVersions { get; set; }

    /// <summary>Result returned by <see cref="EnsurePackageAsync"/>.</summary>
    public bool EnsurePackageResult { get; set; } = true;

    /// <summary>
    /// When true, <see cref="InstallPackagesAsync"/> returns <c>null</c> to exercise the
    /// "package install failed" error branch in <see cref="WorkspaceSetupService"/>.
    /// </summary>
    public bool ReturnNull { get; set; }

    /// <summary>
    /// Explicit version map returned from <see cref="InstallPackagesAsync"/> when non-empty
    /// (and <see cref="InstalledVersions"/> is unset). Lets the native-path setup tests seed the
    /// exact SDK/runtime versions that the rest of the workspace-setup flow then consumes.
    /// </summary>
    public Dictionary<string, string> InstallResult { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Package names passed to the most recent <see cref="InstallPackagesAsync"/> call.</summary>
    public string[] LastRequestedPackages { get; private set; } = [];

    public Task<Dictionary<string, string>> InstallPackagesAsync(
        DirectoryInfo rootDirectory,
        IEnumerable<string> packages,
        TaskContext taskContext,
        SdkInstallMode sdkInstallMode = SdkInstallMode.Stable,
        bool ignoreConfig = false,
        CancellationToken cancellationToken = default)
    {
        var packageArray = packages.ToArray();
        LastRequestedPackages = packageArray;
        InstallPackagesCalls.Add((rootDirectory, packageArray, ignoreConfig));
        if (ReturnNull)
        {
            return Task.FromResult<Dictionary<string, string>>(null!);
        }

        var result = InstalledVersions
            ?? (InstallResult.Count > 0 ? InstallResult : packageArray.ToDictionary(p => p, _ => "1.0.0"));
        return Task.FromResult(result);
    }

    public Task<bool> EnsurePackageAsync(
        DirectoryInfo rootDirectory,
        string packageName,
        TaskContext taskContext,
        string? version = null,
        SdkInstallMode sdkInstallMode = SdkInstallMode.Stable,
        CancellationToken cancellationToken = default)
    {
        EnsurePackageCalls.Add((rootDirectory, packageName, version));
        return Task.FromResult(EnsurePackageResult);
    }
}

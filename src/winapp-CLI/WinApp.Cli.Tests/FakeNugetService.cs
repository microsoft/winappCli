// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake NuGet service that returns predictable versions without network calls.
/// Tracks which packages were queried for test assertions.
/// </summary>
internal class FakeNugetService : INugetService
{
    public string DefaultVersion { get; set; } = "1.6.0";
    public List<string> QueriedPackages { get; } = [];
    public List<(string Package, string Version)> InstalledPackages { get; } = [];

    /// <summary>
    /// Set this to the test cache directory to enable NuGet cache path resolution in tests.
    /// </summary>
    public DirectoryInfo? CacheDirectory { get; set; }

    /// <summary>
    /// Packages listed here will cause <see cref="GetLatestVersionAsync"/> to throw an exception,
    /// simulating a transient NuGet failure for that specific package.
    /// </summary>
    public HashSet<string> PackagesToThrow { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When set alongside <see cref="CancelOnQuery"/>, querying this package cancels that token source and
    /// throws, simulating a user Ctrl+C landing during a latest-version lookup for that package.
    /// </summary>
    public string? CancelOnQueryPackage { get; set; }

    /// <summary>
    /// The token source cancelled when <see cref="CancelOnQueryPackage"/> is queried. Wire this to the same
    /// token passed into the command so the handler observes a genuine cancellation.
    /// </summary>
    public CancellationTokenSource? CancelOnQuery { get; set; }

    /// <summary>
    /// Overrides the map returned from <see cref="InstallPackageAsync"/> for a given package
    /// (e.g. to simulate a package that pulls in additional installed packages). When a package
    /// is not listed here, install returns just <c>{ [package] = version }</c>.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> InstallReturns { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<string> GetLatestVersionAsync(string packageName, SdkInstallMode sdkInstallMode, CancellationToken cancellationToken = default)
    {
        QueriedPackages.Add(packageName);
        if (CancelOnQueryPackage is not null && string.Equals(packageName, CancelOnQueryPackage, StringComparison.OrdinalIgnoreCase))
        {
            CancelOnQuery?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (PackagesToThrow.Contains(packageName))
        {
            throw new InvalidOperationException($"Simulated NuGet failure for {packageName}");
        }
        return Task.FromResult(DefaultVersion);
    }

    public Task<Dictionary<string, string>> InstallPackageAsync(string package, string version, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        InstalledPackages.Add((package, version));

        // When a cache directory is configured, create the package folder AND the completion marker so
        // subsequent "already present" checks (INugetService.IsPackageInstalled) behave like a real,
        // fully-extracted NuGet cache entry.
        if (CacheDirectory != null)
        {
            MarkInstalled(package, version);
        }

        return Task.FromResult(InstallReturns.TryGetValue(package, out var configured)
            ? new Dictionary<string, string>(configured)
            : new Dictionary<string, string> { [package] = version });
    }

    /// <summary>
    /// When true, <see cref="GetPackageDependenciesAsync"/> throws, simulating an offline/blocked
    /// NuGet source. Used to prove the runtime package is resolved from the restored package list
    /// without a network round-trip.
    /// </summary>
    public bool ThrowOnGetPackageDependencies { get; set; }

    /// <summary>
    /// Number of times <see cref="GetPackageDependenciesAsync"/> was invoked (network probe counter).
    /// </summary>
    public int GetPackageDependenciesCallCount { get; private set; }

    public Task<Dictionary<string, string>> GetPackageDependenciesAsync(string packageName, string version, CancellationToken cancellationToken = default)
    {
        GetPackageDependenciesCallCount++;
        if (ThrowOnGetPackageDependencies)
        {
            throw new InvalidOperationException($"Simulated offline NuGet source for {packageName} v{version}");
        }
        return Task.FromResult(new Dictionary<string, string>());
    }

    public DirectoryInfo GetNuGetGlobalPackagesDir()
    {
        if (CacheDirectory == null)
        {
            throw new InvalidOperationException("FakeNugetService.CacheDirectory must be set before calling GetNuGetGlobalPackagesDir");
        }
        var dir = new DirectoryInfo(Path.Join(CacheDirectory.FullName, "packages"));
        if (!dir.Exists)
        {
            dir.Create();
        }
        return dir;
    }

    public DirectoryInfo GetNuGetPackageDir(string packageName, string version)
    {
        var cache = GetNuGetGlobalPackagesDir();
        return new DirectoryInfo(Path.Join(cache.FullName, packageName.ToLowerInvariant(), version));
    }

    public bool IsPackageInstalled(string packageName, string version)
    {
        var dir = GetNuGetPackageDir(packageName, version);
        return dir.Exists && File.Exists(Path.Join(dir.FullName, ".nupkg.metadata"));
    }

    /// <summary>
    /// Creates the on-disk package directory AND the ".nupkg.metadata" completion marker, so
    /// <see cref="IsPackageInstalled"/> reports the package as a complete, already-extracted cache entry.
    /// </summary>
    public void MarkInstalled(string packageName, string version)
    {
        var dir = GetNuGetPackageDir(packageName, version);
        dir.Create();
        File.WriteAllText(Path.Join(dir.FullName, ".nupkg.metadata"), "{}");
    }
}

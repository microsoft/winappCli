// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

internal interface INugetService
{
    /// <summary>
    /// Resolves the latest listed version of <paramref name="packageName"/> for the given install
    /// channel from the sources configured in the user's nuget.config (honoring
    /// <c>&lt;packageSourceMapping&gt;</c>). Throws if an eligible source cannot be queried, so the
    /// result is never a partial "latest".
    /// </summary>
    /// <remarks>
    /// Against a flat-container-only v3 feed (one that exposes PackageBaseAddress but no registration
    /// resource), version enumeration cannot distinguish listed from unlisted packages, so the selected
    /// "latest" may include an unlisted version. Registration-backed feeds (nuget.org and most private feeds)
    /// are unaffected. See the private-feed notes in docs/usage.md.
    /// </remarks>
    Task<string> GetLatestVersionAsync(string packageName, SdkInstallMode sdkInstallMode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads and extracts <paramref name="package"/> (and its transitive dependencies) into the NuGet
    /// global packages folder, resolving sources and credentials from the user's nuget.config hierarchy.
    /// Returns a map of every installed package ID to its normalized on-disk version.
    /// </summary>
    Task<Dictionary<string, string>> InstallPackageAsync(string package, string version, TaskContext taskContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the dependencies of a specific package version by reading the package's .nuspec from the
    /// sources configured in the user's nuget.config (honoring <c>&lt;packageSourceMapping&gt;</c>).
    /// Returns a dictionary mapping each dependency package ID to the normalized concrete version selected
    /// to satisfy its declared range from the configured sources — the lowest available version that
    /// satisfies the range (which may be higher than the range's lower bound, and for a floating range is
    /// the highest match), not the range minimum.
    /// </summary>
    /// <remarks>
    /// The flattened result is first-resolution-wins per dependency id: winapp does not globally reconcile a
    /// diamond where two branches require the same id at different versions, so the returned version can
    /// satisfy one branch's range but not another's. This matches winapp's curated-SDK-graph scope; see the
    /// private-feed notes in docs/usage.md.
    /// </remarks>
    Task<Dictionary<string, string>> GetPackageDependenciesAsync(string packageName, string version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the NuGet global packages directory, resolved from the user's nuget.config — the
    /// NUGET_PACKAGES environment variable or the <c>globalPackagesFolder</c> setting — falling back to
    /// ~/.nuget/packages/.
    /// </summary>
    DirectoryInfo GetNuGetGlobalPackagesDir();

    /// <summary>
    /// Returns the directory for a specific package version in the NuGet global packages cache, using
    /// NuGet's normalized on-disk layout: {cache}/{lowercase-id}/{normalized-version}/.
    /// </summary>
    DirectoryInfo GetNuGetPackageDir(string packageName, string version);

    /// <summary>
    /// Reports whether a specific package version is FULLY installed in the NuGet global packages cache —
    /// the version directory exists AND contains NuGet's ".nupkg.metadata" completion marker. Returns false
    /// for a partial folder left by an interrupted extraction so callers re-download instead of trusting it.
    /// </summary>
    bool IsPackageInstalled(string packageName, string version);
}

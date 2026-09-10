// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Provides methods for registering, unregistering, and querying MSIX packages
/// using the Windows PackageManager API.
/// </summary>
internal interface IPackageRegistrationService
{
    /// <summary>
    /// Registers a loose-layout MSIX package from an AppxManifest.xml path.
    /// Uses DevelopmentMode to allow registration without signing.
    /// </summary>
    /// <param name="manifestPath">Path to the AppxManifest.xml in the loose layout.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RegisterLooseLayoutAsync(string manifestPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a sparse MSIX package with an external location.
    /// The package references files at the external location rather than containing them.
    /// </summary>
    /// <param name="manifestPath">Path to the AppxManifest.xml file.</param>
    /// <param name="externalLocation">External directory containing the app files.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RegisterSparseAsync(string manifestPath, string externalLocation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unregisters an installed package by name. Returns true if a package was found and removed.
    /// </summary>
    /// <param name="packageName">The package identity name (e.g. <c>MyCompany.MyApp</c>).</param>
    /// <param name="preserveAppData">
    /// When true, preserves the package's application data (LocalState, RoamingState, Settings, etc.)
    /// during removal. Only supported for packages registered in development mode.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if a package was unregistered, false if no matching package was found.</returns>
    Task<bool> UnregisterAsync(string packageName, bool preserveAppData = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unregisters a single installed package by its full name (e.g.
    /// <c>MyCompany.MyApp_1.0.0.0_neutral__1abcd2efgh3jk</c>). Use this when you've
    /// already enumerated installed packages via <see cref="FindDevPackages"/> and want
    /// to apply removal policy per-package without affecting other packages that share
    /// the same identity name.
    /// </summary>
    /// <param name="packageFullName">The package full name (Id.FullName).</param>
    /// <param name="preserveAppData">
    /// When true, preserves the package's application data during removal. Only supported
    /// for packages registered in development mode.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when Windows confirmed the removal; <see langword="false"/> when it
    /// reported an error. A caller that tells the user something was removed must check this — Windows
    /// reports a refused removal as error text rather than an exception, so ignoring the result claims
    /// success for a package that is still registered.
    /// </returns>
    Task<bool> UnregisterByFullNameAsync(string packageFullName, bool preserveAppData = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs an MSIX/APPX package file, optionally forcing application shutdown.
    /// Used for installing framework dependencies like Windows App Runtime.
    /// </summary>
    /// <param name="packagePath">Path to the .msix or .appx file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task InstallPackageAsync(string packagePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a package with the given name is installed and returns its version,
    /// or null if not found.
    /// </summary>
    /// <param name="packageName">The package identity name.</param>
    /// <param name="architecture">
    /// Optional architecture filter (<c>x64</c> / <c>arm64</c> / <c>x86</c>). When provided, only a
    /// package whose identity architecture matches is considered installed — important for the
    /// runtime install, where an x64 host may still need x86/arm64 Framework/DDLM packages.
    /// When <c>null</c>, any architecture matches. In all cases, when several servicing versions of
    /// the package are registered, the <em>highest</em> matching version is returned (registration
    /// enumeration order is not guaranteed newest-first).
    /// </param>
    /// <returns>The installed version, or null if not found.</returns>
    string? GetInstalledVersion(string packageName, string? architecture = null);

    /// <summary>
    /// Returns <c>true</c> when at least one package registered for the current user has an identity
    /// Name that starts with <paramref name="namePrefix"/> (ordinal, case-insensitive), optionally
    /// filtered by architecture and excluding names that contain <paramref name="excludeNameSubstring"/>.
    /// Used to detect a registered Windows App Runtime (Framework / DDLM) for a target architecture
    /// without knowing the exact versioned package name.
    /// </summary>
    /// <param name="namePrefix">The package-name prefix to match (e.g. <c>Microsoft.WindowsAppRuntime.</c>).</param>
    /// <param name="architecture">Optional architecture filter (<c>x64</c> / <c>arm64</c> / <c>x86</c>); <c>null</c> matches any.</param>
    /// <param name="excludeNameSubstring">Optional substring; matching names that contain it are ignored (e.g. <c>.CBS.</c>).</param>
    /// <returns><c>true</c> if a matching package is installed.</returns>
    bool IsPackageInstalled(string namePrefix, string? architecture = null, string? excludeNameSubstring = null);

    /// <summary>
    /// Returns the <em>highest</em> installed package Version among all packages whose identity Name
    /// starts with <paramref name="namePrefix"/> (ordinal, case-insensitive), optionally filtered by
    /// architecture and excluding names that contain <paramref name="excludeNameSubstring"/>; or
    /// <c>null</c> when none match. Unlike <see cref="GetInstalledVersion"/> (exact-name), this matches a
    /// family by prefix — used to gate a side-by-side runtime (e.g. the DDLM, whose name embeds its full
    /// version) on the newest registered release without knowing its exact versioned identity.
    /// </summary>
    /// <param name="namePrefix">The package-name prefix to match (e.g. <c>Microsoft.WinAppRuntime.DDLM.</c>).</param>
    /// <param name="architecture">Optional architecture filter (<c>x64</c> / <c>arm64</c> / <c>x86</c>); <c>null</c> matches any.</param>
    /// <param name="excludeNameSubstring">Optional substring; matching names that contain it are ignored.</param>
    /// <returns>The highest matching installed version, or null if none match.</returns>
    string? GetHighestInstalledVersion(string namePrefix, string? architecture = null, string? excludeNameSubstring = null);

    /// <summary>
    /// Finds all installed packages matching the given name that were registered in
    /// development mode (sideloaded). Returns package metadata including the full name
    /// and install location for safety checks.
    /// </summary>
    /// <param name="packageName">The package identity name to search for.</param>
    /// <returns>A list of matching dev-mode packages.</returns>
    List<DevPackageInfo> FindDevPackages(string packageName);

    /// <summary>
    /// Finds every development-mode package whose install location no longer resolves — a registration
    /// left behind after its files were deleted.
    /// </summary>
    /// <remarks>
    /// These can never launch: Windows keeps the identity and its Start-menu entry, but activation
    /// silently does nothing. They accumulate whenever a build output, project tree, or (for a
    /// file-based app) the SDK's <c>%TEMP%\dotnet\runfile</c> directory is cleaned while the package
    /// stays registered.
    /// </remarks>
    /// <returns>A list of dev-mode packages whose files are gone.</returns>
    List<DevPackageInfo> FindOrphanedDevPackages();
}

/// <summary>
/// Information about a development-mode registered package.
/// </summary>
internal sealed record DevPackageInfo(
    string FullName,
    string Name,
    string Version,
    string? InstallLocation,
    bool IsDevelopmentMode);

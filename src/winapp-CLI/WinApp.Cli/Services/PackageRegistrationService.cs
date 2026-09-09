// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;
using Windows.Management.Deployment;

namespace WinApp.Cli.Services;

/// <summary>
/// Manages MSIX package registration, unregistration, and installation using
/// the Windows <see cref="PackageManager"/> WinRT API directly, without
/// shelling out to PowerShell.
/// </summary>
internal sealed class PackageRegistrationService(ILogger<PackageRegistrationService> logger) : IPackageRegistrationService
{
    // HRESULT 0x800704EC = ERROR_ACCESS_DISABLED_BY_POLICY (group policy blocks sideloading)
    // Note: 0x80073CFB ("Reinstallation of the package was blocked") is intentionally NOT
    // treated as a developer-mode error — it more commonly indicates a conflicting installed
    // package (e.g., a previously installed signed MSIX with the same identity).
    private const int ERROR_ACCESS_DISABLED_BY_POLICY = unchecked((int)0x800704EC);

    // OS-boundary seams. Each defaults to the real WinRT PackageManager call and is
    // overridable in tests so the surrounding mapping, filtering, and error-handling
    // logic can be exercised without a real package, admin rights, or the packageQuery
    // capability (which the unit-test host lacks). Mirrors the Func<> seam precedent
    // used by UpdateNotificationService.
    internal Func<Uri, CancellationToken, Task<DeploymentOutcome>> RegisterLooseImpl { get; set; } = DefaultRegisterLoose;

    internal Func<Uri, Uri, CancellationToken, Task<DeploymentOutcome>> RegisterSparseImpl { get; set; } = DefaultRegisterSparse;

    internal Func<IReadOnlyList<InstalledPackageView>> EnumerateUserPackagesImpl { get; set; } = DefaultEnumerateUserPackages;

    internal Func<string, bool, CancellationToken, Task<RemovalOutcome>> RemovePackageImpl { get; set; } = DefaultRemovePackage;

    internal Func<Uri, CancellationToken, Task<InstallOutcome>> AddPackageImpl { get; set; } = DefaultAddPackage;

    /// <inheritdoc />
    public async Task RegisterLooseLayoutAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(manifestPath);
        LongPathHelper.ValidatePathLength(fullPath);

        // WinRT PackageManager doesn't support paths exceeding MAX_PATH or symlinks.
        // Convert to 8.3 short path as a workaround; throw if shortening fails.
        var manifestUri = new Uri(LongPathHelper.GetShortPathOrThrow(fullPath));

        try
        {
            var result = await RegisterLooseImpl(manifestUri, cancellationToken);

            if (!result.IsRegistered)
            {
                throw BuildRegistrationException(
                    "Failed to register package",
                    result.ErrorText,
                    result.ExtendedErrorHResult,
                    packageIdentityName: TryReadIdentityName(fullPath));
            }

            logger.LogDebug("Package registered from loose layout: {ManifestPath}", manifestPath);
        }
        catch (Exception ex) when (IsSideloadPolicyError(ex))
        {
            throw new InvalidOperationException(
                "Sideloading is blocked by Group Policy on this machine. " +
                "Contact your IT administrator to allow trusted app sideloading.", ex);
        }
        catch (Exception ex) when (ex is not InvalidOperationException && ex is not OperationCanceledException)
        {
            throw BuildRegistrationException(
                "Failed to register package",
                ex.Message,
                ex.HResult,
                packageIdentityName: TryReadIdentityName(fullPath),
                inner: ex);
        }
    }

    /// <inheritdoc />
    public async Task RegisterSparseAsync(string manifestPath, string externalLocation, CancellationToken cancellationToken = default)
    {
        var fullManifestPath = Path.GetFullPath(manifestPath);
        LongPathHelper.ValidatePathLength(fullManifestPath);
        var fullExternalPath = Path.GetFullPath(externalLocation);
        LongPathHelper.ValidatePathLength(fullExternalPath);

        // WinRT PackageManager doesn't support paths exceeding MAX_PATH or symlinks.
        // Convert to 8.3 short paths as a workaround; throw if shortening fails.
        var manifestUri = new Uri(LongPathHelper.GetShortPathOrThrow(fullManifestPath));
        var shortExternalPath = LongPathHelper.GetShortPathOrThrow(fullExternalPath + Path.DirectorySeparatorChar);
        // Documented coverage ceiling: this re-append runs only when Win32 GetShortPathName strips
        // the trailing separator, which depends on 8.3 short-name generation being enabled on the
        // volume — env-dependent, so it can't be exercised deterministically in a unit test.
        if (!Path.EndsInDirectorySeparator(shortExternalPath))
        {
            shortExternalPath += Path.DirectorySeparatorChar;
        }

        var externalUri = new Uri(shortExternalPath);

        try
        {
            var result = await RegisterSparseImpl(manifestUri, externalUri, cancellationToken);

            if (!result.IsRegistered)
            {
                throw BuildRegistrationException(
                    "Failed to register sparse package",
                    result.ErrorText,
                    result.ExtendedErrorHResult,
                    packageIdentityName: TryReadIdentityName(fullManifestPath));
            }

            logger.LogDebug("Sparse package registered: {ManifestPath} (external: {ExternalLocation})", manifestPath, externalLocation);
        }
        catch (Exception ex) when (IsSideloadPolicyError(ex))
        {
            throw new InvalidOperationException(
                "Sideloading is blocked by Group Policy on this machine. " +
                "Contact your IT administrator to allow trusted app sideloading.", ex);
        }
        catch (Exception ex) when (ex is not InvalidOperationException && ex is not OperationCanceledException)
        {
            throw BuildRegistrationException(
                "Failed to register sparse package",
                ex.Message,
                ex.HResult,
                packageIdentityName: TryReadIdentityName(fullManifestPath),
                inner: ex);
        }
    }

    /// <inheritdoc />
    public async Task<bool> UnregisterAsync(string packageName, bool preserveAppData = true, CancellationToken cancellationToken = default)
    {
        // FindPackagesForUser with name+publisher requires both to match.
        // Enumerate all user packages, then filter by name.
        var matchingPackages = EnumerateUserPackagesImpl()
            .Where(p => string.Equals(p.Name, packageName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matchingPackages.Count == 0)
        {
            return false;
        }

        foreach (var pkg in matchingPackages)
        {
            var fullName = pkg.FullName;
            logger.LogDebug("Removing package: {PackageFullName} (preserveAppData={PreserveAppData})", fullName, preserveAppData);

            var result = await RemovePackageImpl(fullName, preserveAppData, cancellationToken);

            if (!string.IsNullOrEmpty(result.ErrorText))
            {
                logger.LogWarning("Warning removing package {PackageFullName}: {Error}", fullName, result.ErrorText);
            }
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> UnregisterByFullNameAsync(string packageFullName, bool preserveAppData = true, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Removing package: {PackageFullName} (preserveAppData={PreserveAppData})", packageFullName, preserveAppData);

        var result = await RemovePackageImpl(packageFullName, preserveAppData, cancellationToken);

        if (!string.IsNullOrEmpty(result.ErrorText))
        {
            logger.LogWarning("Warning removing package {PackageFullName}: {Error}", packageFullName, result.ErrorText);
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public async Task InstallPackageAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(packagePath);
        LongPathHelper.ValidatePathLength(fullPath);

        // WinRT PackageManager doesn't support paths exceeding MAX_PATH or symlinks.
        // Convert to 8.3 short path as a workaround; throw if shortening fails.
        var packageUri = new Uri(LongPathHelper.GetShortPathOrThrow(fullPath));

        var result = await AddPackageImpl(packageUri, cancellationToken);

        if (!string.IsNullOrEmpty(result.ErrorText))
        {
            throw new InvalidOperationException(
                $"Failed to install package '{Path.GetFileName(packagePath)}': {result.ErrorText} (0x{result.ExtendedErrorHResult:X8})");
        }

        logger.LogDebug("Installed package: {PackagePath}", packagePath);
    }

    /// <inheritdoc />
    public string? GetInstalledVersion(string packageName, string? architecture = null)
    {
        var wantedArch = MapArchitecture(architecture);

        // Multiple servicing versions of the same package can be registered at once and the enumeration
        // order is not guaranteed newest-first, so track the HIGHEST matching version. Returning an
        // arbitrary (possibly older) match could make the runtime gate reinstall or fail even when a
        // newer, sufficient version is already present.
        (ushort Major, ushort Minor, ushort Build, ushort Revision)? best = null;

        foreach (var pkg in EnumerateUserPackagesImpl())
        {
            if (!string.Equals(pkg.Name, packageName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Arch filter: when an architecture was requested, only a matching-arch package counts
            // as "installed" so the caller doesn't skip installing the Framework/DDLM for a
            // different target arch (e.g. building x86 on an x64 host).
            if (wantedArch is not null && pkg.Architecture != wantedArch.Value)
            {
                continue;
            }

            var candidate = (pkg.VersionMajor, pkg.VersionMinor, pkg.VersionBuild, pkg.VersionRevision);
            if (best is null || candidate.CompareTo(best.Value) > 0)
            {
                best = candidate;
            }
        }

        return best is { } v ? $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}" : null;
    }

    /// <inheritdoc />
    public bool IsPackageInstalled(string namePrefix, string? architecture = null, string? excludeNameSubstring = null)
    {
        var wantedArch = MapArchitecture(architecture);

        foreach (var pkg in EnumerateUserPackagesImpl())
        {
            if (!pkg.Name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (excludeNameSubstring is not null &&
                pkg.Name.Contains(excludeNameSubstring, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (wantedArch is not null && pkg.Architecture != wantedArch.Value)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public string? GetHighestInstalledVersion(string namePrefix, string? architecture = null, string? excludeNameSubstring = null)
    {
        var wantedArch = MapArchitecture(architecture);

        // Track the HIGHEST matching version across every side-by-side servicing package whose name
        // starts with the prefix (enumeration order is not guaranteed newest-first), so a stale older
        // release can never mask a newer one that satisfies the caller's gate.
        (ushort Major, ushort Minor, ushort Build, ushort Revision)? best = null;

        foreach (var pkg in EnumerateUserPackagesImpl())
        {
            if (!pkg.Name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (excludeNameSubstring is not null &&
                pkg.Name.Contains(excludeNameSubstring, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (wantedArch is not null && pkg.Architecture != wantedArch.Value)
            {
                continue;
            }

            var candidate = (pkg.VersionMajor, pkg.VersionMinor, pkg.VersionBuild, pkg.VersionRevision);
            if (best is null || candidate.CompareTo(best.Value) > 0)
            {
                best = candidate;
            }
        }

        return best is { } v ? $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}" : null;
    }

    /// <summary>
    /// Maps a winapp architecture string (<c>x64</c> / <c>arm64</c> / <c>x86</c>) to the WinRT
    /// <see cref="Windows.System.ProcessorArchitecture"/> used by installed package identities.
    /// A <c>null</c> result means "no arch filtering" — which is how folder-mode <c>run</c>
    /// (architecture == null) preserves its pre-project-mode behavior of matching any installed
    /// package, including Neutral-arch ones. Exposed as <c>internal</c> for unit tests.
    /// </summary>
    internal static Windows.System.ProcessorArchitecture? MapArchitecture(string? architecture)
    {
        if (string.IsNullOrWhiteSpace(architecture))
        {
            return null;
        }

        return architecture.Trim().ToLowerInvariant() switch
        {
            "x64" => Windows.System.ProcessorArchitecture.X64,
            "arm64" => Windows.System.ProcessorArchitecture.Arm64,
            "x86" => Windows.System.ProcessorArchitecture.X86,
            _ => null,
        };
    }

    /// <inheritdoc />
    public List<DevPackageInfo> FindDevPackages(string packageName)
    {
        var results = new List<DevPackageInfo>();

        foreach (var pkg in EnumerateUserPackagesImpl())
        {
            if (!string.Equals(pkg.Name, packageName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            results.Add(ToDevPackageInfo(pkg));
        }

        return results;
    }

    /// <inheritdoc />
    public List<DevPackageInfo> FindOrphanedDevPackages()
    {
        var results = new List<DevPackageInfo>();

        foreach (var pkg in EnumerateUserPackagesImpl())
        {
            if (!pkg.IsDevelopmentMode)
            {
                continue;
            }

            var info = ToDevPackageInfo(pkg);

            // An empty install location means the accessor threw, which for a registered package means
            // its files are no longer reachable. Treat a location that simply does not exist the same
            // way; both leave a registration that can never launch.
            if (string.IsNullOrEmpty(info.InstallLocation) || !DirectoryExists(info.InstallLocation))
            {
                results.Add(info);
            }
        }

        return results;
    }

    private static DevPackageInfo ToDevPackageInfo(InstalledPackageView pkg)
    {
        string? installLocation = null;
        try
        {
            installLocation = pkg.InstalledLocationAccessor();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately broad, with a filter rather than a bare catch. Reading the location is a
            // best-effort probe of an OPTIONAL value with a total fallback ("unknown"), and it is a WinRT
            // call: a package whose files are gone throws, and the HRESULT it surfaces as is not
            // contractual. FindOrphanedDevPackages walks EVERY installed package, so letting an
            // unanticipated type escape would abort the whole sweep over one bad entry — which is exactly
            // the state this method exists to report. Cancellation still propagates.
        }

        return new DevPackageInfo(
            FullName: pkg.FullName,
            Name: pkg.Name,
            Version: $"{pkg.VersionMajor}.{pkg.VersionMinor}.{pkg.VersionBuild}.{pkg.VersionRevision}",
            InstallLocation: installLocation,
            IsDevelopmentMode: pkg.IsDevelopmentMode);
    }

    /// <summary>
    /// Existence probe for an install location. A path that cannot even be evaluated (malformed, or on
    /// a device that is gone) counts as missing rather than throwing out of the enumeration.
    /// </summary>
    private static bool DirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool IsSideloadPolicyError(Exception ex)
    {
        return ex.HResult == ERROR_ACCESS_DISABLED_BY_POLICY;
    }

    // HRESULT 0x80073CFB — most commonly raised when a package with the same identity is
    // already installed (e.g., via a signed MSIX) and cannot be re-registered as dev-mode
    // loose files. Officially: "Reinstallation of the package was blocked."
    internal const int ERROR_INSTALL_PACKAGE_ALREADY_EXISTS = unchecked((int)0x80073CFB);

    // HRESULT 0x80073CF9 — ERROR_INSTALL_FAILED (winerror.h: 15609), the *generic* deployment
    // failure. It is not the registration-specific code, which is 0x80073CF6
    // (ERROR_INSTALL_REGISTRATION_FAILURE). WinRT surfaces it with no error text
    // ("Unknown error"), so callers get an opaque HRESULT and no way to act on it.
    //
    // One recurring cause is a layout entry that exceeds MAX_PATH: LongPathHelper
    // .ValidatePathLength only guards the *manifest* path, so a manifest can sit comfortably
    // under 260 characters while a nested payload file (deep TFM/RID output folders are
    // typical: bin/x64/Debug/net10.0-windows10.0.x/win-x64/...) does not. Because the HRESULT
    // is generic, the hint below offers that cause rather than asserting it.
    internal const int ERROR_INSTALL_FAILED = unchecked((int)0x80073CF9);

    /// <summary>
    /// Builds a user-facing exception describing a failed package registration.
    /// When the HRESULT indicates a duplicate-identity conflict (0x80073CFB) and a
    /// <paramref name="packageIdentityName"/> is supplied, the hint embeds the actual
    /// identity so the user can copy/paste the remediation command directly.
    /// </summary>
    internal static InvalidOperationException BuildRegistrationException(
        string prefix,
        string? errorText,
        int? hresult,
        string? packageIdentityName = null,
        Exception? inner = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(prefix).Append(": ");
        sb.Append(string.IsNullOrEmpty(errorText) ? "Unknown error" : errorText);
        if (hresult.HasValue)
        {
            sb.Append(" (0x").Append(hresult.Value.ToString("X8")).Append(')');
        }

        if (hresult == ERROR_INSTALL_PACKAGE_ALREADY_EXISTS)
        {
            var identityToken = string.IsNullOrWhiteSpace(packageIdentityName)
                ? "<PackageName>"
                : packageIdentityName;

            sb.AppendLine();
            sb.Append(
                "Hint: a package with the same identity may already be installed " +
                "(e.g., from a signed MSIX). Try removing it first:");
            sb.AppendLine();
            sb.Append("  Get-AppxPackage ").Append(identityToken).Append(" | Remove-AppxPackage");
        }
        else if (hresult == ERROR_INSTALL_FAILED)
        {
            // Kept flow-agnostic on purpose: this builder also serves the sparse path
            // (winapp create-debug-identity), which has no --output-appx-directory and no
            // staged layout, so the remediation is scoped rather than issued as a command.
            // Deliberately does not suggest enabling system long-path support: that only
            // relaxes our own ValidatePathLength gate. PackageManager still cannot open a
            // >MAX_PATH payload file (see the 8.3 shortening at the top of this file), so
            // the policy change costs an admin round-trip and fixes nothing here.
            sb.AppendLine();
            sb.Append(
                "Hint: one common cause is a file inside the package layout exceeding the " +
                "Windows MAX_PATH limit of 260 characters. The manifest path itself is " +
                "validated before registration, but a nested payload file in a deep " +
                "build-output folder can still exceed it. Try staging the layout under a " +
                "shorter path; with winapp run, use --output-appx-directory.");
        }

        return inner is null
            ? new InvalidOperationException(sb.ToString())
            : new InvalidOperationException(sb.ToString(), inner);
    }

    /// <summary>
    /// Best-effort read of the AppxManifest Identity/@Name attribute. Returns null on any
    /// I/O or parse failure; callers should treat null as "identity unknown" and fall back
    /// to a generic placeholder.
    /// </summary>
    private static string? TryReadIdentityName(string manifestPath)
    {
        try
        {
            return AppxManifestDocument.Load(manifestPath).IdentityName;
        }
        catch
        {
            return null;
        }
    }

    // ---- Real WinRT PackageManager seam implementations (documented coverage ceiling) --------
    // The Default* methods below are the production Windows.Management.Deployment.PackageManager
    // calls that the RegisterLooseImpl / RegisterSparseImpl / RemovePackageImpl / AddPackageImpl /
    // EnumerateUserPackagesImpl seams default to; tests inject those seams to cover all surrounding
    // logic. The lines left uncovered here are an OS boundary that cannot be exercised without
    // machine-mutating MSIX deployment:
    //  * RegisterLoose / RegisterSparse / AddPackage - their failure paths ARE covered by the
    //    deterministic _RealDefault_*_Throws/_Fails tests; only the *Outcome returned AFTER a
    //    successful WinRT call stays uncovered (needs a real, valid package to actually deploy).
    //  * RemovePackage - has no real-default test: removing a non-existent package has no
    //    observable effect and its failure mode (throw vs error-result) is host-dependent, so such
    //    a test would be vacuous or flaky. The warning / no-warning logic is covered via the seam.
    private static async Task<DeploymentOutcome> DefaultRegisterLoose(Uri manifestUri, CancellationToken cancellationToken)
    {
        var pm = new PackageManager();
        var result = await pm.RegisterPackageAsync(
            manifestUri,
            null,
            DeploymentOptions.DevelopmentMode | DeploymentOptions.ForceApplicationShutdown
        ).AsTask(cancellationToken);
        return new DeploymentOutcome(result.IsRegistered, result.ErrorText, result.ExtendedErrorCode?.HResult);
    }

    private static async Task<DeploymentOutcome> DefaultRegisterSparse(Uri manifestUri, Uri externalUri, CancellationToken cancellationToken)
    {
        var pm = new PackageManager();
        var options = new RegisterPackageOptions
        {
            ExternalLocationUri = externalUri,
            DeveloperMode = true,
            ForceUpdateFromAnyVersion = true,
        };
        var result = await pm.RegisterPackageByUriAsync(manifestUri, options).AsTask(cancellationToken);
        return new DeploymentOutcome(result.IsRegistered, result.ErrorText, result.ExtendedErrorCode?.HResult);
    }

    private static IReadOnlyList<InstalledPackageView> DefaultEnumerateUserPackages()
    {
        // Behavior note (intentional): this eagerly snapshots Id.Version and IsDevelopmentMode for
        // every package into plain-data InstalledPackageView records, rather than reading those
        // WinRT accessors lazily only for the name-matching package as earlier inline code did.
        // The snapshot decouples callers from live WinRT Package objects (so seams can inject test
        // data); the only observable difference — a corrupted-metadata *unrelated* package could
        // abort the whole enumeration — is effectively unreachable for real user packages. The
        // matching InstalledLocation accessor stays deferred (a Func) because it is the one member
        // that legitimately throws for a valid package whose files were removed.
        // Documented coverage ceiling: the live accessor lambda (() => p.InstalledLocation?.Path)
        // and the eager Id.Version / IsDevelopmentMode reads only execute over a real installed
        // Package, so they stay uncovered; the seam-injected views cover the projection logic.
        // The (userId, name, publisher) overload rejects empty/null publisher because
        // string.Empty marshals as a null HSTRING in WinRT interop, so use the single
        // (userSecurityId) overload with the current user and filter by name in callers.
        var pm = new PackageManager();
        var views = new List<InstalledPackageView>();
        foreach (var p in pm.FindPackagesForUser(string.Empty))
        {
            var v = p.Id.Version;
            views.Add(new InstalledPackageView(
                p.Id.Name,
                p.Id.FullName,
                v.Major,
                v.Minor,
                v.Build,
                v.Revision,
                p.IsDevelopmentMode,
                () => p.InstalledLocation?.Path,
                p.Id.Architecture));
        }

        return views;
    }

    private static async Task<RemovalOutcome> DefaultRemovePackage(string packageFullName, bool preserveAppData, CancellationToken cancellationToken)
    {
        var pm = new PackageManager();
        var removalOptions = preserveAppData
            ? RemovalOptions.PreserveApplicationData
            : RemovalOptions.None;
        var result = await pm.RemovePackageAsync(packageFullName, removalOptions).AsTask(cancellationToken);
        return new RemovalOutcome(result.ErrorText);
    }

    private static async Task<InstallOutcome> DefaultAddPackage(Uri packageUri, CancellationToken cancellationToken)
    {
        var pm = new PackageManager();
        var result = await pm.AddPackageAsync(
            packageUri,
            null,
            DeploymentOptions.ForceApplicationShutdown
        ).AsTask(cancellationToken);
        return new InstallOutcome(result.ErrorText, result.ExtendedErrorCode?.HResult);
    }

    /// <summary>Outcome of a package register/deploy operation, decoupled from WinRT types.</summary>
    internal readonly record struct DeploymentOutcome(bool IsRegistered, string? ErrorText, int? ExtendedErrorHResult);

    /// <summary>Outcome of a package removal, decoupled from WinRT types.</summary>
    internal readonly record struct RemovalOutcome(string? ErrorText);

    /// <summary>Outcome of a package install/add, decoupled from WinRT types.</summary>
    internal readonly record struct InstallOutcome(string? ErrorText, int? ExtendedErrorHResult);

    /// <summary>A projection of an installed package, decoupled from WinRT <c>Package</c>.</summary>
    internal sealed record InstalledPackageView(
        string Name,
        string FullName,
        ushort VersionMajor,
        ushort VersionMinor,
        ushort VersionBuild,
        ushort VersionRevision,
        bool IsDevelopmentMode,
        Func<string?> InstalledLocationAccessor,
        Windows.System.ProcessorArchitecture Architecture = Windows.System.ProcessorArchitecture.Unknown);
}

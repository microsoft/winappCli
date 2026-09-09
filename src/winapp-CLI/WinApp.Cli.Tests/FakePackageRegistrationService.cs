// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake package registration service that records calls without actually
/// registering, unregistering, or installing MSIX packages.
/// </summary>
internal class FakePackageRegistrationService : IPackageRegistrationService
{
    public List<string> RegisterLooseLayoutCalls { get; } = [];
    public List<(string ManifestPath, string ExternalLocation)> RegisterSparseCalls { get; } = [];
    public List<(string PackageName, bool PreserveAppData)> UnregisterCalls { get; } = [];
    public List<(string PackageFullName, bool PreserveAppData)> UnregisterByFullNameCalls { get; } = [];
    public List<string> InstallPackageCalls { get; } = [];
    public List<(string PackageName, string? Architecture)> GetInstalledVersionCalls { get; } = [];
    public List<string> FindDevPackagesCalls { get; } = [];

    /// <summary>
    /// When set, <see cref="UnregisterAsync"/> returns this value.
    /// Defaults to false (no package found).
    /// </summary>
    public bool FakeUnregisterResult { get; set; }

    /// <summary>
    /// When set, <see cref="GetInstalledVersion"/> returns this value.
    /// Defaults to null (package not installed).
    /// </summary>
    public string? FakeInstalledVersion { get; set; }

    /// <summary>
    /// When set, <see cref="GetInstalledVersion"/> resolves the installed version per (name, arch), so a
    /// test can model e.g. "an OLDER patch of the required Framework family is registered". Falls back to
    /// <see cref="FakeInstalledVersion"/> when null.
    /// </summary>
    public Func<string, string?, string?>? GetInstalledVersionFunc { get; set; }

    /// <summary>
    /// When set, <see cref="FindDevPackages"/> returns these values.
    /// Defaults to empty list.
    /// </summary>
    public List<DevPackageInfo> FakeDevPackages { get; set; } = [];

    public Task RegisterLooseLayoutAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        if (RegisterLooseLayoutThrows is not null)
        {
            throw RegisterLooseLayoutThrows;
        }
        RegisterLooseLayoutCalls.Add(manifestPath);
        return Task.CompletedTask;
    }

    public Task RegisterSparseAsync(string manifestPath, string externalLocation, CancellationToken cancellationToken = default)
    {
        if (RegisterSparseThrows is not null)
        {
            throw RegisterSparseThrows;
        }
        RegisterSparseCalls.Add((manifestPath, externalLocation));
        return Task.CompletedTask;
    }

    /// <summary>
    /// When set to a non-null exception, <see cref="RegisterLooseLayoutAsync"/> throws it.
    /// Used to exercise the exception-wrapping path in
    /// <c>MsixService.RegisterLooseLayoutPackageAsync</c>.
    /// </summary>
    public Exception? RegisterLooseLayoutThrows { get; set; }

    /// <summary>
    /// When set to a non-null exception, <see cref="RegisterSparseAsync"/> throws it.
    /// Used to exercise the exception-wrapping path in
    /// <c>MsixService.RegisterSparsePackageAsync</c>.
    /// </summary>
    public Exception? RegisterSparseThrows { get; set; }

    public Task<bool> UnregisterAsync(string packageName, bool preserveAppData = true, CancellationToken cancellationToken = default)
    {
        UnregisterCalls.Add((packageName, preserveAppData));
        return Task.FromResult(FakeUnregisterResult);
    }

    /// <summary>
    /// When set to a non-null exception, <see cref="UnregisterByFullNameAsync"/> throws it
    /// instead of recording the call. Useful for testing exception-propagation paths
    /// (e.g. cancellation surfacing from MsixService.UnregisterExistingPackageAsync).
    /// </summary>
    public Exception? UnregisterByFullNameThrows { get; set; }

    public Action<string, bool>? OnUnregisterByFullName { get; set; }

    public Func<CancellationToken, Task>? WaitDuringUnregisterByFullNameAsync { get; set; }

    public async Task UnregisterByFullNameAsync(string packageFullName, bool preserveAppData = true, CancellationToken cancellationToken = default)
    {
        if (UnregisterByFullNameThrows is not null)
        {
            throw UnregisterByFullNameThrows;
        }
        UnregisterByFullNameCalls.Add((packageFullName, preserveAppData));
        if (WaitDuringUnregisterByFullNameAsync is not null)
        {
            await WaitDuringUnregisterByFullNameAsync(cancellationToken);
        }
        OnUnregisterByFullName?.Invoke(packageFullName, preserveAppData);
    }

    /// <summary>
    /// When set to a non-null exception, <see cref="InstallPackageAsync"/> throws it
    /// (after recording the call) instead of completing. Used to exercise the per-package
    /// install-failure/error-count path in
    /// <c>WindowsAppRuntimeService.InstallWindowsAppRuntimeAsync</c>.
    /// </summary>
    public Exception? InstallPackageThrows { get; set; }

    public Task InstallPackageAsync(
        string packagePath,
        CancellationToken cancellationToken = default) =>
        InstallPackageAsync(packagePath, forceApplicationShutdown: true, cancellationToken);

    public Task InstallPackageAsync(
        string packagePath,
        bool forceApplicationShutdown,
        CancellationToken cancellationToken = default)
    {
        InstallPackageCalls.Add(packagePath);
        if (InstallPackageThrows is not null)
        {
            throw InstallPackageThrows;
        }
        return Task.CompletedTask;
    }

    public string? GetInstalledVersion(string packageName, string? architecture = null)
    {
        GetInstalledVersionCalls.Add((packageName, architecture));
        return GetInstalledVersionFunc is not null
            ? GetInstalledVersionFunc(packageName, architecture)
            : FakeInstalledVersion;
    }

    /// <summary>
    /// Records (namePrefix, architecture, excludeNameSubstring) calls. Returns
    /// <see cref="FakeIsPackageInstalled"/> (default false).
    /// </summary>
    public List<(string NamePrefix, string? Architecture, string? ExcludeNameSubstring)> IsPackageInstalledCalls { get; } = [];

    /// <summary>When set, <see cref="IsPackageInstalled"/> returns this value. Defaults to false.</summary>
    public bool FakeIsPackageInstalled { get; set; }

    /// <summary>
    /// When set, <see cref="IsPackageInstalled"/> uses this predicate (keyed on the name prefix) to
    /// decide the result, so a test can model e.g. "Framework present but DDLM missing". Falls back
    /// to <see cref="FakeIsPackageInstalled"/> when null.
    /// </summary>
    public Func<string, bool>? IsPackageInstalledPredicate { get; set; }

    public bool IsPackageInstalled(string namePrefix, string? architecture = null, string? excludeNameSubstring = null)
    {
        IsPackageInstalledCalls.Add((namePrefix, architecture, excludeNameSubstring));
        return IsPackageInstalledPredicate?.Invoke(namePrefix) ?? FakeIsPackageInstalled;
    }

    /// <summary>
    /// Records (namePrefix, architecture, excludeNameSubstring) calls. Returns
    /// <see cref="GetHighestInstalledVersionFunc"/> (keyed on the name prefix) when set, otherwise
    /// <see cref="FakeHighestInstalledVersion"/> (default null).
    /// </summary>
    public List<(string NamePrefix, string? Architecture, string? ExcludeNameSubstring)> GetHighestInstalledVersionCalls { get; } = [];

    /// <summary>When set, <see cref="GetHighestInstalledVersion"/> returns this value. Defaults to null.</summary>
    public string? FakeHighestInstalledVersion { get; set; }

    /// <summary>
    /// When set, <see cref="GetHighestInstalledVersion"/> resolves the highest installed version per
    /// (namePrefix, arch), so a test can model e.g. "only an OLDER DDLM than required is registered".
    /// Falls back to <see cref="FakeHighestInstalledVersion"/> when null.
    /// </summary>
    public Func<string, string?, string?>? GetHighestInstalledVersionFunc { get; set; }

    public string? GetHighestInstalledVersion(string namePrefix, string? architecture = null, string? excludeNameSubstring = null)
    {
        GetHighestInstalledVersionCalls.Add((namePrefix, architecture, excludeNameSubstring));
        return GetHighestInstalledVersionFunc is not null
            ? GetHighestInstalledVersionFunc(namePrefix, architecture)
            : FakeHighestInstalledVersion;
    }

    /// <summary>
    /// When set to a non-null exception, <see cref="FindDevPackages"/> throws it
    /// instead of returning <see cref="FakeDevPackages"/>. Use to exercise the
    /// non-fatal catch path (and OperationCanceled propagation) in
    /// <c>MsixService.IsExistingRegistrationUpToDate</c>.
    /// </summary>
    public Exception? FindDevPackagesThrows { get; set; }

    /// <summary>
    /// Records name lookups. Returns <see cref="FakeRegisteredPackages"/> filtered by name, or a
    /// single entry derived from <see cref="GetInstalledVersion"/> when no explicit set was given —
    /// so existing tests that only model a version keep working.
    /// </summary>
    public List<string> FindInstalledPackagesByNameCalls { get; } = [];

    /// <summary>Registered identities a test wants reported, including publisher and architecture.</summary>
    public List<RegisteredPackageIdentity> FakeRegisteredPackages { get; } = [];

    public IReadOnlyList<RegisteredPackageIdentity> FindInstalledPackagesByName(string packageName)
    {
        FindInstalledPackagesByNameCalls.Add(packageName);

        if (FakeRegisteredPackages.Count > 0)
        {
            return
            [
                .. FakeRegisteredPackages
                    .Where(package => string.Equals(package.Name, packageName, StringComparison.OrdinalIgnoreCase)),
            ];
        }

        var version = GetInstalledVersionFunc is not null
            ? GetInstalledVersionFunc(packageName, null)
            : FakeInstalledVersion;

        return version is null
            ? []
            : [new RegisteredPackageIdentity(packageName, version, null, "neutral")];
    }

    public List<DevPackageInfo> FindDevPackages(string packageName)
    {
        FindDevPackagesCalls.Add(packageName);
        if (FindDevPackagesThrows is not null)
        {
            throw FindDevPackagesThrows;
        }
        return FakeDevPackages;
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>Receipts are hints; every destructive operation also proves the live Windows registration.</summary>
internal static class DevelopmentRegistrationStore
{
    internal static string ReceiptPath(DirectoryInfo layout) => layout.FullName.TrimEnd(Path.DirectorySeparatorChar) + ".winapp-registration.json";
    internal static string PendingPath(DirectoryInfo layout) => layout.FullName.TrimEnd(Path.DirectorySeparatorChar) + ".winapp-registration.pending.json";

    internal static bool IsStateFile(string path, DirectoryInfo layout) =>
        string.Equals(path, ReceiptPath(layout), StringComparison.OrdinalIgnoreCase) ||
        string.Equals(path, PendingPath(layout), StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(ReceiptPath(layout) + ".", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(PendingPath(layout) + ".", StringComparison.OrdinalIgnoreCase);

    internal static DevelopmentRegistration? Read(DirectoryInfo layout)
    {
        var receipt = ReadJson(ReceiptPath(layout), DevelopmentRegistrationJsonContext.Default.DevelopmentRegistration);
        if (receipt is not null)
        {
            Validate(receipt, layout);
        }
        return receipt;
    }

    internal static PendingDevelopmentRegistration? ReadPending(DirectoryInfo layout)
    {
        var pending = ReadJson(PendingPath(layout), DevelopmentRegistrationJsonContext.Default.PendingDevelopmentRegistration);
        if (pending is not null)
        {
            if (pending.SchemaVersion != DevelopmentRegistration.CurrentSchemaVersion)
            {
                throw new InvalidOperationException($"Unsupported pending registration schema beside '{layout.FullName}'. Update winapp or inspect the state before retrying.");
            }
            Validate(pending.Candidate, layout);
            if (pending.Prior is not null)
            {
                Validate(pending.Prior, layout);
            }
        }
        return pending;
    }

    internal static IReadOnlyList<DevelopmentRegistration> FindByOwner(DirectoryInfo stateRoot, string canonicalOwner) =>
        FindAll(stateRoot).Where(receipt => SamePath(receipt.Identity.OwnerPath, canonicalOwner)).ToList();

    internal static IReadOnlyList<DevelopmentRegistration> FindAll(DirectoryInfo stateRoot)
    {
        var index = Path.Combine(stateRoot.FullName, "development-registrations");
        EnsureRealPath(index);
        if (File.Exists(index))
        {
            throw new InvalidOperationException($"The development registration index '{index}' is not a directory. Inspect the ownership state before retrying.");
        }
        if (!Directory.Exists(index))
        {
            return [];
        }
        var receipts = new List<DevelopmentRegistration>();
        foreach (var file in Directory.EnumerateFiles(index, "*.path"))
        {
            EnsureRealPath(file);
            var hint = File.ReadAllText(file);
            if (!Path.IsPathFullyQualified(hint))
            {
                throw new InvalidOperationException($"The development registration hint '{file}' does not contain an absolute layout path. Inspect the ownership state before retrying.");
            }
            var layout = new DirectoryInfo(hint);
            if (!string.Equals(file, IndexPath(stateRoot, layout), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"The development registration hint '{file}' does not match its recorded layout. Inspect the ownership state before retrying.");
            }
            var pending = ReadPending(layout);
            var receipt = pending is null ? Read(layout) : pending.Candidate with { IsPending = true };
            if (receipt is not null)
            {
                receipts.Add(receipt);
            }
        }
        return receipts;
    }

    internal static void Begin(DirectoryInfo stateRoot, DirectoryInfo layout, PendingDevelopmentRegistration pending)
    {
        Validate(pending.Candidate, layout);
        WriteIndex(stateRoot, layout);
        WriteAtomic(PendingPath(layout), pending, DevelopmentRegistrationJsonContext.Default.PendingDevelopmentRegistration);
    }

    internal static void Commit(DirectoryInfo stateRoot, DirectoryInfo layout, DevelopmentRegistration receipt)
    {
        var now = DateTimeOffset.UtcNow;
        receipt = receipt with { UpdatedAtUtc = now, RegisteredAtUtc = receipt.RegisteredAtUtc ?? now };
        Validate(receipt, layout);
        WriteIndex(stateRoot, layout);
        WriteAtomic(ReceiptPath(layout), receipt, DevelopmentRegistrationJsonContext.Default.DevelopmentRegistration);
        ClearPending(layout);
    }

    internal static void ClearPending(DirectoryInfo layout)
    {
        EnsureRealPath(PendingPath(layout));
        File.Delete(PendingPath(layout));
    }

    internal static void Remove(DirectoryInfo stateRoot, DirectoryInfo layout)
    {
        var receipt = ReceiptPath(layout);
        var pending = PendingPath(layout);
        var index = IndexPath(stateRoot, layout);
        foreach (var path in new[] { receipt, pending, index })
        {
            EnsureRealPath(path);
            if (Directory.Exists(path))
            {
                throw new InvalidOperationException($"Ownership metadata path '{path}' is a directory. Inspect it before retrying cleanup.");
            }
        }
        try
        {
            File.Delete(index);
        }
        catch (DirectoryNotFoundException)
        {
            // Explicit layout cleanup does not require this optional discovery hint.
        }
        ClearPending(layout);
        File.Delete(receipt);
    }

    /// <summary>
    /// Returns true only after removing the exact owned package and observing it absent; false when
    /// it was already absent. Superseded receipts, pending work, or live metadata disagreement throw
    /// without removing a package.
    /// </summary>
    internal static async Task<bool> RemoveOwnedAsync(
        IPackageRegistrationService packages,
        DirectoryInfo stateRoot,
        DevelopmentRegistration registration,
        bool preserveAppData,
        CancellationToken ct = default)
    {
        var layout = new DirectoryInfo(registration.Identity.LayoutPath);
        Validate(registration, layout);
        using var layoutLease = LayoutLease.Acquire(stateRoot, layout, ct);
        using var familyLease = LayoutLease.AcquireFamilies([registration.Identity.PackageFamilyName], ct);
        ct.ThrowIfCancellationRequested();
        var current = Read(layout);
        if (registration.IsPending || ReadPending(layout) is not null)
        {
            throw new InvalidOperationException($"An interrupted registration at '{layout.FullName}' needs recovery. Run the app again before unregistering it.");
        }
        if (current is not null && !SameReceipt(current, registration))
        {
            throw new InvalidOperationException(
                $"The ownership receipt at '{layout.FullName}' changed or a newer run superseded revision {registration.Identity.Revision} " +
                $"with revision {current.Identity.Revision}. Nothing was removed. Select the current deployment and retry.");
        }
        var identity = current?.Identity ?? registration.Identity;
        var installed = FindExact(packages, identity);
        if (installed is null)
        {
            VerifyNoReplacement(packages, identity);
            ct.ThrowIfCancellationRequested();
            Remove(stateRoot, layout);
            return false;
        }
        if (current is null)
        {
            throw new InvalidOperationException(
                $"Ownership metadata for '{identity.PackageFullName}' at '{layout.FullName}' is missing, but the package is still registered. " +
                "Nothing was removed. Restore the receipt or run the app again before retrying.");
        }
        VerifyLive(installed, current.Identity);
        VerifyManifest(current);
        VerifyNoReplacement(packages, current.Identity, allowExact: true);
        var removed = await RemoveExactAsync(packages, current.Identity, preserveAppData, ct);
        VerifyNoReplacement(packages, current.Identity);
        ct.ThrowIfCancellationRequested();
        Remove(stateRoot, layout);
        return removed;
    }

    private static void VerifyNoReplacement(IPackageRegistrationService packages, DevelopmentIdentity identity, bool allowExact = false)
    {
        bool IsOther(DevPackageInfo package) =>
            !allowExact || !string.Equals(package.FullName, identity.PackageFullName, StringComparison.OrdinalIgnoreCase);

        var replacement = packages.FindDevPackages(identity.EffectivePackageName)
            .FirstOrDefault(package => IsOther(package) &&
                (string.Equals(package.Publisher, identity.Publisher, StringComparison.Ordinal) ||
                 string.Equals(package.PackageFamilyName, identity.PackageFamilyName, StringComparison.OrdinalIgnoreCase)));
        if (replacement is null)
        {
            var occupants = packages.FindPackagesAtLocation(identity.LayoutPath);
            replacement = occupants.FirstOrDefault(IsOther);
        }
        if (replacement is not null)
        {
            throw new InvalidOperationException(
                $"Ownership records '{identity.PackageFullName}', but Windows reports '{replacement.FullName}' " +
                $"at '{replacement.InstallLocation}'. Nothing else was removed. Resolve this registration conflict before retrying.");
        }
    }

    internal static async Task<bool> RemoveExactAsync(IPackageRegistrationService service, DevelopmentIdentity identity, bool preserveData, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var live = FindExact(service, identity);
        if (live is null)
        {
            return false;
        }
        VerifyLive(live, identity);
        if (!await service.UnregisterByFullNameAsync(live.FullName, preserveData, cancellationToken) ||
            FindExact(service, identity) is not null)
        {
            throw new InvalidOperationException($"Windows did not remove '{live.FullName}'. Close the app and retry; its ownership receipt has been retained.");
        }
        return true;
    }

    internal static DevPackageInfo? FindExact(IPackageRegistrationService service, DevelopmentIdentity identity)
    {
        var matches = service.FindDevPackages(identity.EffectivePackageName)
            .Where(package => string.Equals(package.FullName, identity.PackageFullName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 1)
        {
            throw new InvalidOperationException("Windows returned ambiguous package identity metadata. No package was removed.");
        }
        return matches.SingleOrDefault();
    }

    internal static void VerifyLive(DevPackageInfo live, DevelopmentIdentity identity)
    {
        if (!live.IsDevelopmentMode ||
            !string.Equals(live.FullName, identity.PackageFullName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(live.Name, identity.EffectivePackageName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(live.Version, identity.Version, StringComparison.Ordinal) ||
            !string.Equals(live.Publisher, identity.Publisher, StringComparison.Ordinal) ||
            !string.Equals(live.PackageFamilyName, identity.PackageFamilyName, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(live.InstallLocation) || !SamePath(live.InstallLocation, identity.LayoutPath))
        {
            throw new InvalidOperationException($"The live package '{live.FullName}' does not match the owned development registration at '{identity.LayoutPath}'. No package was removed. Use a separate output directory.");
        }
    }

    internal static void VerifyManifest(DevelopmentRegistration registration)
    {
        if (!string.Equals(HashManifest(new DirectoryInfo(registration.Identity.LayoutPath)), registration.ManifestHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The manifest at '{registration.Identity.LayoutPath}' no longer matches its ownership receipt. Restore the registered manifest before retrying; no package was removed.");
        }
    }

    internal static string HashManifest(DirectoryInfo layout) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(layout.FullName, "appxmanifest.xml"))));

    internal static bool SamePath(string left, string right) =>
        string.Equals(DevelopmentIdentityHelper.CanonicalizePath(left), DevelopmentIdentityHelper.CanonicalizePath(right), StringComparison.OrdinalIgnoreCase);

    internal static bool SameReceipt(DevelopmentRegistration left, DevelopmentRegistration right) =>
        left.Identity.Revision == right.Identity.Revision &&
        string.Equals(left.Identity.PackageFullName, right.Identity.PackageFullName, StringComparison.OrdinalIgnoreCase) &&
        SamePath(left.Identity.OwnerPath, right.Identity.OwnerPath) &&
        SamePath(left.Identity.LayoutPath, right.Identity.LayoutPath) &&
        string.Equals(left.ManifestHash, right.ManifestHash, StringComparison.Ordinal);

    private static void Validate(DevelopmentRegistration receipt, DirectoryInfo layout)
    {
        var identity = receipt.Identity;
        if (receipt.SchemaVersion != DevelopmentRegistration.CurrentSchemaVersion ||
            !string.Equals(receipt.AlgorithmVersion, DevelopmentIdentityHelper.SeedVersion, StringComparison.Ordinal) ||
            identity is null || string.IsNullOrWhiteSpace(identity.OwnerPath) || !Path.IsPathFullyQualified(identity.OwnerPath) ||
            string.IsNullOrWhiteSpace(identity.LayoutPath) || !Path.IsPathFullyQualified(identity.LayoutPath) || !SamePath(identity.LayoutPath, layout.FullName) ||
            identity.Mode is not ("Original" or "Unique") ||
            string.IsNullOrWhiteSpace(identity.OriginalPackageName) || string.IsNullOrWhiteSpace(identity.ApplicationId) ||
            identity.ResourceId is null || identity.Aliases is null ||
            identity.Revision < 1 || string.IsNullOrWhiteSpace(identity.PackageFullName) ||
            string.IsNullOrWhiteSpace(identity.EffectivePackageName) || string.IsNullOrWhiteSpace(identity.Publisher) ||
            !string.Equals(DevelopmentIdentityHelper.ComputeFamilyName(identity.EffectivePackageName, identity.Publisher),
                identity.PackageFamilyName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(identity.PackageFullName,
                $"{identity.EffectivePackageName}_{identity.Version}_{identity.Architecture}_{identity.ResourceId}_{identity.PackageFamilyName[(identity.EffectivePackageName.Length + 1)..]}",
                StringComparison.OrdinalIgnoreCase) ||
            receipt.ManifestHash is not { Length: 64 } || receipt.ManifestHash.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new InvalidOperationException($"Invalid development registration receipt beside '{layout.FullName}'. No package was changed. Restore or inspect the receipt before retrying.");
        }
        var expectedName = identity.Mode == "Unique"
            ? DevelopmentIdentityHelper.DeriveName(
                DevelopmentIdentityHelper.CanonicalizePath(identity.OwnerPath),
                identity.OriginalPackageName, identity.Publisher)
            : identity.OriginalPackageName;
        if (!string.Equals(identity.EffectivePackageName, expectedName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The identity in '{ReceiptPath(layout)}' does not match its recorded owner and algorithm. Nothing was removed.");
        }
    }

    private static T? ReadJson<T>(string path, JsonTypeInfo<T> typeInfo) where T : class
    {
        EnsureRealPath(path);
        if (Directory.Exists(path))
        {
            throw new InvalidOperationException($"Ownership state '{path}' is a directory, not a receipt. No package was changed. Inspect the path before retrying.");
        }
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), typeInfo)
                ?? throw new JsonException("The document is null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Cannot read ownership state '{path}'. No package was changed. Restore or inspect this file before retrying.", ex);
        }
    }

    private static void WriteAtomic<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        EnsureRealPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, typeInfo);
                stream.Flush(flushToDisk: true);
            }
            EnsureRealPath(path);
            File.Move(staging, path, overwrite: true);
        }
        finally
        {
            File.Delete(staging);
        }
    }

    private static string IndexPath(DirectoryInfo root, DirectoryInfo layout)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            DevelopmentIdentityHelper.CanonicalizePath(layout.FullName).ToUpperInvariant())));
        return Path.Combine(root.FullName, "development-registrations", key + ".path");
    }

    private static void WriteIndex(DirectoryInfo root, DirectoryInfo layout)
    {
        var path = IndexPath(root, layout);
        EnsureRealPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            File.WriteAllText(staging, DevelopmentIdentityHelper.CanonicalizePath(layout.FullName));
            File.Move(staging, path, overwrite: true);
        }
        finally { File.Delete(staging); }
    }

    internal static void EnsureRealPath(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        for (var current = fullPath; current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"Ownership state cannot use the symbolic link or junction '{current}'. Use a real directory.");
            }
            if (!string.Equals(current, fullPath, StringComparison.OrdinalIgnoreCase) &&
                (attributes & FileAttributes.Directory) == 0)
            {
                throw new InvalidOperationException($"Ownership path ancestor '{current}' is not a directory. Inspect the ownership state before retrying.");
            }
        }
    }
}

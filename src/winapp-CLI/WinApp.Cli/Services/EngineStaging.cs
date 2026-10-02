// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

internal static unsafe partial class EngineStaging
{
    private static readonly object StagedLock = new();
    private static readonly Dictionary<(string Root, string Source), string> StagedBySource = new();
    public static bool IsPackaged { get; } = ComputeHasPackageIdentity();

    // Foreign-load engines deliberately use the non-AppData default, not the general cache override.
    internal static string EngineRoot => Path.Combine(WinappDirectoryService.GetDefaultGlobalWinappDirectory().FullName, "engine");

    public static string StageForForeignLoad(string sourcePath)
    {
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("The DevTools engine is missing. Reinstall winapp with its matching engine files.", source);
        }
        return IsPackaged ? StageTo(source, EngineRoot) : source;
    }

    internal static string StageTo(string sourcePath, string engineRoot, string? profile = null)
    {
        var source = Path.GetFullPath(sourcePath);
        var root = Path.GetFullPath(engineRoot);
        if (!Path.IsPathFullyQualified(engineRoot) || PathSafety.IsNetworkPath(root))
        {
            throw new IOException($"DevTools staging requires a fully qualified local directory: {engineRoot}");
        }
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("DevTools staging source was not found.", source);
        }
        if (PathSafety.HasReparsePointOnPath(root, Path.GetPathRoot(root)!))
        {
            throw new IOException($"DevTools staging path is redirected or inaccessible: {root}");
        }
        lock (StagedLock)
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new IOException("DevTools staging could not determine the current user's SID.");
            EnsureTrustedDirectory(root, user, profile);
            var key = (root.ToUpperInvariant(), source.ToUpperInvariant());
            if (StagedBySource.TryGetValue(key, out var previous) && File.Exists(previous))
            {
                EnsureTrustedDirectory(Path.GetDirectoryName(previous)!, user, profile);
                VerifyFile(previous, user);
                return previous;
            }
            var directory = Path.Combine(root, $"{Environment.ProcessId:x}-{Guid.NewGuid():N}");
            if (Directory.Exists(directory))
            {
                throw new IOException($"DevTools staging destination already exists: {directory}");
            }
            EnsureTrustedDirectory(directory, user, profile);
            var destination = Path.Combine(directory, Path.GetFileName(source));
            var temporary = destination + ".tmp";
            try
            {
                File.Copy(source, temporary, overwrite: false);
                VerifyFile(temporary, user);
                File.Move(temporary, destination);
                VerifyFile(destination, user);
                StagedBySource[key] = destination;
                return destination;
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
    }

    private static void EnsureTrustedDirectory(string path, SecurityIdentifier user, string? profile)
    {
        var chain = new Stack<DirectoryInfo>();
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            chain.Push(directory);
        }
        var directories = chain.ToArray();
        profile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var profileWriters = ProfileWriters(profile, user);
        for (var index = 0; index < directories.Length; index++)
        {
            var directory = directories[index];
            directory.Refresh();
            if (!directory.Exists)
            {
                // The preceding existing parent was checked as a creation namespace.
                directory.Create(RestrictedSecurity(user));
                directory.Refresh();
            }
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw Untrusted(directory.FullName, null);
            }
            var nextExists = index + 1 < directories.Length && Directory.Exists(directories[index + 1].FullName);
            var systemAncestor = !directory.FullName.Equals(profile, StringComparison.OrdinalIgnoreCase) &&
                IsUnder(profile, directory.FullName);
            var inProfile = directory.FullName.Equals(profile, StringComparison.OrdinalIgnoreCase) || IsUnder(directory.FullName, profile);
            if (!IsTrustedSecurity(directory.GetAccessControl(), user, directory: true,
                allowCreateChildren: nextExists, allowSystemOwner: systemAncestor,
                alsoTrusted: inProfile ? profileWriters : null, out var principal))
            {
                throw Untrusted(directory.FullName, principal);
            }
        }
    }

    private static bool IsUnder(string path, string ancestor) =>
        path.StartsWith(Path.EndsInDirectorySeparator(ancestor) ? ancestor : ancestor + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    // A principal granted inheritable write access to the whole profile can already change everything the user
    // runs from it, including the app being inspected, so staging under the profile cannot exclude it.
    internal static IReadOnlySet<SecurityIdentifier> ProfileWriters(string profile, SecurityIdentifier user)
    {
        var writers = new HashSet<SecurityIdentifier>();
        if (!Directory.Exists(profile))
        {
            return writers;
        }
        foreach (FileSystemAccessRule rule in new DirectoryInfo(profile).GetAccessControl()
            .GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference is SecurityIdentifier sid &&
                (rule.InheritanceFlags & InheritanceFlags.ContainerInherit) != 0 &&
                (rule.PropagationFlags & PropagationFlags.NoPropagateInherit) == 0 &&
                (rule.FileSystemRights & (FileSystemRights.WriteData | FileSystemRights.Delete)) != 0 &&
                !IsSelfOrPrivileged(sid, user))
            {
                writers.Add(sid);
            }
        }
        return writers;
    }

    private static DirectorySecurity RestrictedSecurity(SecurityIdentifier user)
    {
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static void VerifyFile(string path, SecurityIdentifier user)
    {
        var file = new FileInfo(path);
        SecurityIdentifier? principal = null;
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0 ||
            !IsTrustedSecurity(file.GetAccessControl(), user, false, false, false, null, out principal))
        {
            throw Untrusted(path, principal);
        }
    }

    // Other ordinary OS users are the boundary; same-user, SYSTEM and administrator changes are not.
    internal static bool IsTrustedSecurity(FileSystemSecurity security, SecurityIdentifier user, bool directory,
        bool allowCreateChildren = false, bool allowSystemOwner = false) =>
        IsTrustedSecurity(security, user, directory, allowCreateChildren, allowSystemOwner, null, out _);

    internal static bool IsTrustedSecurity(FileSystemSecurity security, SecurityIdentifier user, bool directory,
        bool allowCreateChildren, bool allowSystemOwner, IReadOnlySet<SecurityIdentifier>? alsoTrusted,
        out SecurityIdentifier? principal)
    {
        principal = null;
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.DiscretionaryAcl is null ||
            security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
            !(IsSelfOrPrivileged(owner, user) || (allowSystemOwner && owner.Value == TrustedInstallerSid)))
        {
            principal = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            return false;
        }
        var unsafeRights = FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership |
            FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes;
        if (!directory || !allowCreateChildren)
        {
            unsafeRights |= FileSystemRights.WriteData | FileSystemRights.AppendData;
        }
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0)
            {
                continue;
            }
            if (rule.IdentityReference is not SecurityIdentifier sid)
            {
                return false;
            }
            if (!IsSelfOrPrivileged(sid, user) && !(allowSystemOwner && sid.Value == TrustedInstallerSid) &&
                alsoTrusted?.Contains(sid) != true && (rule.FileSystemRights & unsafeRights) != 0)
            {
                principal = sid;
                return false;
            }
        }
        return true;
    }

    private const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    private static bool IsSelfOrPrivileged(SecurityIdentifier sid, SecurityIdentifier user) =>
        sid == user || sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
        sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);

    private static IOException Untrusted(string path, SecurityIdentifier? principal) =>
        new($"DevTools staging path is redirected, foreign-owned or writable by another user: {path}" +
            (principal is null ? "" : $" ({AccountName(principal)})") + ". " +
            "Use a user-owned profile directory with restricted write permissions. Existing permissions and files were not repaired.");

    private static string AccountName(SecurityIdentifier sid)
    {
        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return sid.Value;
        }
    }

    private static bool ComputeHasPackageIdentity()
    {
        uint length = 0;
        var result = GetCurrentPackageFullName(&length, null);
        return result switch
        {
            15700 => false,
            0 or 122 => true,
            _ => throw new Win32Exception(result),
        };
    }

    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentPackageFullName(uint* packageFullNameLength, char* packageFullName);
}

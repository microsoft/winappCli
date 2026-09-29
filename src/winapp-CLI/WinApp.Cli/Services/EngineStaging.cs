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

    internal static string StageTo(string sourcePath, string engineRoot)
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
            EnsureTrustedDirectory(root, user);
            var key = (root.ToUpperInvariant(), source.ToUpperInvariant());
            if (StagedBySource.TryGetValue(key, out var previous) && File.Exists(previous))
            {
                EnsureTrustedDirectory(Path.GetDirectoryName(previous)!, user);
                VerifyFile(previous, user);
                return previous;
            }
            var directory = Path.Combine(root, $"{Environment.ProcessId:x}-{Guid.NewGuid():N}");
            if (Directory.Exists(directory))
            {
                throw new IOException($"DevTools staging destination already exists: {directory}");
            }
            EnsureTrustedDirectory(directory, user);
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

    private static void EnsureTrustedDirectory(string path, SecurityIdentifier user)
    {
        var chain = new Stack<DirectoryInfo>();
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            chain.Push(directory);
        }
        var directories = chain.ToArray();
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
                throw Untrusted(directory.FullName);
            }
            var nextExists = index + 1 < directories.Length && Directory.Exists(directories[index + 1].FullName);
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var systemAncestor = !directory.FullName.Equals(profile, StringComparison.OrdinalIgnoreCase) &&
                profile.StartsWith(Path.EndsInDirectorySeparator(directory.FullName) ? directory.FullName :
                    directory.FullName + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
            if (!IsTrustedSecurity(directory.GetAccessControl(), user, directory: true,
                allowCreateChildren: nextExists, allowSystemOwner: systemAncestor))
            {
                throw Untrusted(directory.FullName);
            }
        }
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
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0 ||
            !IsTrustedSecurity(file.GetAccessControl(), user, directory: false))
        {
            throw Untrusted(path);
        }
    }

    // Other ordinary OS users are the boundary; same-user, SYSTEM and administrator changes are not.
    internal static bool IsTrustedSecurity(FileSystemSecurity security, SecurityIdentifier user, bool directory,
        bool allowCreateChildren = false, bool allowSystemOwner = false)
    {
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.DiscretionaryAcl is null ||
            security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
            !(IsSelfOrPrivileged(owner, user) || (allowSystemOwner && owner.Value == TrustedInstallerSid)))
        {
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
            if (rule.IdentityReference is not SecurityIdentifier sid ||
                (!IsSelfOrPrivileged(sid, user) && !(allowSystemOwner && sid.Value == TrustedInstallerSid) &&
                 (rule.FileSystemRights & unsafeRights) != 0))
            {
                return false;
            }
        }
        return true;
    }

    private const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    private static bool IsSelfOrPrivileged(SecurityIdentifier sid, SecurityIdentifier user) =>
        sid == user || sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
        sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);

    private static IOException Untrusted(string path) =>
        new($"DevTools staging path is redirected, foreign-owned or writable by another user: {path}. " +
            "Use a user-owned profile directory with restricted write permissions. Existing permissions and files were not repaired.");

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

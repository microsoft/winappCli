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
            throw Redirected(root);
        }
        lock (StagedLock)
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new IOException("DevTools staging could not determine the current user's SID.");
            EnsureDirectory(root, user);
            var key = (root.ToUpperInvariant(), source.ToUpperInvariant());
            if (StagedBySource.TryGetValue(key, out var previous) && File.Exists(previous))
            {
                VerifyPrivate(new DirectoryInfo(Path.GetDirectoryName(previous)!), user);
                VerifyPrivate(new FileInfo(previous), user);
                return previous;
            }
            var directory = Path.Combine(root, $"{Environment.ProcessId:x}-{Guid.NewGuid():N}");
            if (Directory.Exists(directory))
            {
                throw new IOException($"DevTools staging destination already exists: {directory}");
            }
            EnsureDirectory(directory, user);
            VerifyPrivate(new DirectoryInfo(directory), user);
            var destination = Path.Combine(directory, Path.GetFileName(source));
            var temporary = destination + ".tmp";
            try
            {
                File.Copy(source, temporary, overwrite: false);
                VerifyPrivate(new FileInfo(temporary), user);
                File.Move(temporary, destination);
                VerifyPrivate(new FileInfo(destination), user);
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

    // Existing ancestors are not judged by their ACLs: whoever can write the user's profile can already change
    // what the user runs. Missing directories are created for the user only, and no directory may be a redirect.
    private static void EnsureDirectory(string path, SecurityIdentifier user)
    {
        var chain = new Stack<DirectoryInfo>();
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            chain.Push(directory);
        }
        foreach (var directory in chain)
        {
            if (!directory.Exists)
            {
                directory.Create(RestrictedSecurity(user));
            }
            directory.Refresh();
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw Redirected(directory.FullName);
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

    private static void VerifyPrivate(FileSystemInfo item, SecurityIdentifier user)
    {
        if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw Redirected(item.FullName);
        }
        FileSystemSecurity security = item is DirectoryInfo directory ? directory.GetAccessControl() : ((FileInfo)item).GetAccessControl();
        if (!IsTrustedSecurity(security, user))
        {
            throw new IOException($"DevTools staging could not make this path private to the current user: {item.FullName}. " +
                "Existing permissions and files were not repaired.");
        }
    }

    // Other ordinary OS users are the boundary; same-user, SYSTEM and administrator changes are not.
    internal static bool IsTrustedSecurity(FileSystemSecurity security, SecurityIdentifier user)
    {
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.DiscretionaryAcl is null ||
            security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !IsSelfOrPrivileged(owner, user))
        {
            return false;
        }
        const FileSystemRights unsafeRights = FileSystemRights.WriteData | FileSystemRights.AppendData |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership |
            FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0)
            {
                continue;
            }
            if (rule.IdentityReference is not SecurityIdentifier sid ||
                (!IsSelfOrPrivileged(sid, user) && (rule.FileSystemRights & unsafeRights) != 0))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsSelfOrPrivileged(SecurityIdentifier sid, SecurityIdentifier user) =>
        sid == user || sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
        sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);

    private static IOException Redirected(string path) =>
        new($"DevTools staging path is redirected by a junction or symbolic link: {path}. " +
            "Use a profile directory that is not redirected.");

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

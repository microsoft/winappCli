// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.AccessControl;
using System.Security.Principal;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>Verifies the namespace and secrets before target state can be used.</summary>
internal static class TargetStateDirectorySecurity
{
    internal static DirectoryInfo EnsureTrusted(string targetsRoot, string targetRoot, bool create)
    {
        targetsRoot = Path.TrimEndingDirectorySeparator(targetsRoot);
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new IOException("The current Windows user could not be identified.");
        var target = new DirectoryInfo(targetRoot);
        var ancestors = new Stack<DirectoryInfo>();
        for (DirectoryInfo? directory = target; directory is not null; directory = directory.Parent)
        {
            ancestors.Push(directory);
        }

        // Check from the volume down: a private leaf is not safe when another user can replace
        // a parent. Never follow a reparse point before checking the next path component.
        while (ancestors.TryPop(out var directory))
        {
            if (!TryGetAttributes(directory.FullName, out var attributes))
            {
                if (!create)
                {
                    return target;
                }

                directory.Create(PrivateStateSecurity(user));
                attributes = File.GetAttributes(directory.FullName);
            }

            RejectReparsePoint(directory.FullName, attributes);
            if (!attributes.HasFlag(FileAttributes.Directory))
            {
                throw new IOException($"'{directory.FullName}' is not a directory.");
            }

            Verify(directory, user,
                allowAncestorAccess: !TargetPathSafety.IsInsideRoot(targetsRoot, directory.FullName));
        }

        VerifyContents(target, user);
        target.Refresh();
        return target;
    }

    private static DirectorySecurity PrivateStateSecurity(SecurityIdentifier user)
    {
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        // The Sandbox broker maps bootstrap/result folders as SYSTEM, not as the host CLI user.
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static void VerifyContents(DirectoryInfo target, SecurityIdentifier user)
    {
        var pending = new Stack<FileSystemInfo>();
        pending.Push(target);
        while (pending.TryPop(out var item))
        {
            try
            {
                if (!TryGetAttributes(item.FullName, out var attributes))
                {
                    continue;
                }

                RejectReparsePoint(item.FullName, attributes);
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    var directory = new DirectoryInfo(item.FullName);
                    Verify(directory, user, allowAncestorAccess: false);
                    foreach (var child in directory.EnumerateFileSystemInfos())
                    {
                        pending.Push(child);
                    }
                }
                else
                {
                    Verify(new FileInfo(item.FullName), user, allowAncestorAccess: false);
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Another trusted winapp process may prune a bootstrap or atomically replace state.
            }
        }
    }

    private static void Verify(FileSystemInfo item, SecurityIdentifier user, bool allowAncestorAccess)
    {
        FileSystemSecurity security = item is DirectoryInfo directory
            ? directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : ((FileInfo)item).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (!StatePathSecurity.IsTrusted(security, user, allowAncestorAccess))
        {
            throw new IOException($"'{item.FullName}' is owned by or grants unsafe access to another user.");
        }
    }

    private static void RejectReparsePoint(string path, FileAttributes attributes)
    {
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException($"'{path}' is a junction or symbolic link. Target state requires a direct local path.");
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }
}

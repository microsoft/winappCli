// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.AccessControl;
using System.Security.Principal;

namespace WinApp.Cli.Helpers;

internal static class StatePathSecurity
{
    private static readonly SecurityIdentifier TrustedInstaller = new(
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    internal static void VerifyAncestors(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UntrustedStatePathException("The current Windows user could not be identified.");
        var leaf = new DirectoryInfo(path);
        var ancestors = new Stack<DirectoryInfo>();
        for (DirectoryInfo? directory = leaf; directory is not null; directory = directory.Parent)
        {
            ancestors.Push(directory);
        }

        while (ancestors.TryPop(out var directory))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(directory.FullName);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return;
            }
            RejectReparsePoint(directory.FullName, attributes);
            if (!attributes.HasFlag(FileAttributes.Directory))
            {
                throw new IOException($"'{directory.FullName}' is not a directory.");
            }
            // The caller secures its leaf. Ancestors must never be repaired: they may hold unrelated data.
            if (directory.FullName != leaf.FullName
                && !IsTrusted(directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access),
                    user, allowAncestorAccess: true))
            {
                throw new UntrustedStatePathException($"State ancestor '{directory.FullName}' is owned by or grants unsafe access to another user.");
            }
        }
    }

    internal static void RejectReparsePoint(string path)
    {
        try
        {
            RejectReparsePoint(path, File.GetAttributes(path));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // The caller can create this path after validating its existing ancestors.
        }
    }

    private static void RejectReparsePoint(string path, FileAttributes attributes)
    {
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UntrustedStatePathException($"State path '{path}' is a link or reparse point.");
        }
    }

    internal static bool IsTrusted(FileSystemSecurity security, SecurityIdentifier user, bool allowAncestorAccess = false)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
            || !(IsSelfOrPrivileged(owner, user) || (allowAncestorAccess && owner == TrustedInstaller)))
        {
            return false;
        }
        if (new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl is null)
        {
            return false;
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow
                || rule.IdentityReference is SecurityIdentifier sid
                    && (IsSelfOrPrivileged(sid, user) || (allowAncestorAccess && sid == TrustedInstaller)))
            {
                continue;
            }
            if (!allowAncestorAccess)
            {
                return false;
            }

            // Public traversal and sibling creation are safe; replacing a verified child is not.
            const FileSystemRights harmlessAncestorRights = FileSystemRights.ReadAndExecute
                | FileSystemRights.Synchronize | FileSystemRights.CreateDirectories;
            if (!rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)
                && (rule.FileSystemRights & ~harmlessAncestorRights) != 0)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsSelfOrPrivileged(SecurityIdentifier sid, SecurityIdentifier user) =>
        sid == user
        || sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
        || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
}

internal sealed class UntrustedStatePathException(string message) : IOException(message);

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.AccessControl;
using System.Security.Principal;

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

/// <summary>
/// Keeps a host folder shareable by <c>wsb share</c>.
/// </summary>
/// <remarks>
/// Measured on a live Sandbox: <c>wsb share</c> fails with <c>E_ACCESSDENIED</c> when neither the
/// folder nor its parent grants SYSTEM (directly or through <c>BUILTIN\Administrators</c>), even
/// though the user running <c>wsb</c> has full control. A profile folder normally inherits those
/// grants, but a parent whose DACL was restricted to the current user -- as winapp 0.7.0 did to
/// <c>%USERPROFILE%\.winapp\state</c> when UI coordination created it first -- removes them from
/// everything beneath it. Granting SYSTEM gives nothing away: it already holds every privilege needed
/// to read the folder regardless of its DACL.
/// </remarks>
internal static class SandboxShareAccess
{
    private static readonly SecurityIdentifier s_localSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier s_administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>Grants SYSTEM access to <paramref name="path"/> when nothing already does.</summary>
    /// <remarks>
    /// Best effort. A folder whose permissions cannot be changed is left as it is, and the share that
    /// follows reports the failure with the folder it could not map.
    /// </remarks>
    internal static void EnsureHostServiceAccess(string path)
    {
        var directory = new DirectoryInfo(path);

        try
        {
            var security = directory.GetAccessControl();
            if (GrantsHostService(security))
            {
                return;
            }

            security.AddAccessRule(new FileSystemAccessRule(
                s_localSystem,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            directory.SetAccessControl(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                     or IOException
                                     or PrivilegeNotHeldException
                                     or InvalidOperationException)
        {
            // The share reports an accurate error if this mattered.
        }
    }

    /// <summary>Whether SYSTEM can already list and read the folder this security describes.</summary>
    internal static bool GrantsHostService(FileSystemSecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);

        const FileSystemRights Needed = FileSystemRights.ListDirectory | FileSystemRights.ReadAttributes;

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow &&
                (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                (rule.FileSystemRights & Needed) == Needed &&
                rule.IdentityReference is SecurityIdentifier sid &&
                (sid == s_localSystem || sid == s_administrators))
            {
                return true;
            }
        }

        return false;
    }
}

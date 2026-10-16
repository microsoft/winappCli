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
            // One folder per engine build: launches of the same build share it, and a new build gets its own.
            var hash = Sha256(source);
            var directory = Path.Combine(root, hash);
            var destination = Path.Combine(directory, Path.GetFileName(source));
            if (!TryReuse(directory, destination, hash, user))
            {
                var temporary = Path.Combine(root, $"{hash}.{Guid.NewGuid():N}.tmp");
                try
                {
                    EnsureDirectory(temporary, user);
                    VerifyPrivate(new DirectoryInfo(temporary), user);
                    var staged = Path.Combine(temporary, Path.GetFileName(source));
                    File.Copy(source, staged, overwrite: false);
                    VerifyPrivate(new FileInfo(staged), user);
                    if (Sha256(staged) != hash)
                    {
                        throw new IOException($"DevTools staging copied a different engine than {source}.");
                    }
                    try
                    {
                        Directory.Move(temporary, directory);
                    }
                    catch (IOException) when (Directory.Exists(directory))
                    {
                        // Another launch staged the same engine first; its copy is verified below.
                    }
                }
                finally
                {
                    TryDelete(temporary);
                }
                if (!TryReuse(directory, destination, hash, user))
                {
                    throw new IOException($"DevTools staging could not verify the engine copy at {destination}.");
                }
            }
            StagedBySource[key] = destination;
            RemoveUnused(root, StagedBySource.Values.Select(Path.GetDirectoryName).OfType<string>());
            return destination;
        }
    }

    // A staged engine is reused only while it is exactly the engine being staged. A changed or incomplete one is
    // removed so it can be staged again; if it cannot be removed because an app has it loaded, staging stops.
    private static bool TryReuse(string directory, string destination, string hash, SecurityIdentifier user)
    {
        var info = new DirectoryInfo(directory);
        if (!info.Exists)
        {
            return false;
        }
        VerifyPrivate(info, user);
        if (File.Exists(destination))
        {
            VerifyPrivate(new FileInfo(destination), user);
            if (Sha256(destination) == hash)
            {
                // Marks it in use, so another winapp cleaning up does not remove it before the app loads it.
                Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow);
                return true;
            }
        }
        if (!TryDelete(directory))
        {
            throw new IOException($"The staged DevTools engine at {directory} was changed and is in use. Close the apps using it, then retry.");
        }
        return false;
    }

    internal static readonly TimeSpan UnusedAge = TimeSpan.FromMinutes(10);

    // Older engine copies, and per-launch copies from earlier versions, are removed once no app has them loaded.
    // A loaded engine cannot be deleted, so it is skipped and removed by a later launch. Recent folders are left
    // alone: another winapp may have just staged one for an app that has not loaded it yet.
    private static void RemoveUnused(string root, IEnumerable<string> keep)
    {
        var kept = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
        foreach (var directory in new DirectoryInfo(root).EnumerateDirectories())
        {
            if (kept.Contains(directory.FullName) || (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
                DateTime.UtcNow - directory.LastWriteTimeUtc < UnusedAge)
            {
                continue;
            }
            TryDelete(directory.FullName);
        }
    }

    private static bool TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream))[..32];
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

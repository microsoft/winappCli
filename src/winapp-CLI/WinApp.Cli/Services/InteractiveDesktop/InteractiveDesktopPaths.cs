// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services.InteractiveDesktop;

/// <summary>
/// Resolves the coordination file set for the current user and Windows session (spec §7):
/// <code>
/// %LOCALAPPDATA%\Microsoft\WinAppCli\locks\
///   interactive-desktop-{session}.state.lock
///   interactive-desktop-{session}.state.json
///   interactive-desktop-{session}.active.lock
///   participants\interactive-desktop-{session}-{pid}-{startTicks}.lease
/// </code>
/// </summary>
/// <remarks>
/// The session scope matters because Windows gives every signed-in session its own foreground window,
/// focus and input stream. Two sessions on one machine (fast user switching, several RDP sessions) do
/// not interfere, so they must not queue behind each other.
/// </remarks>
internal interface IInteractiveDesktopPaths
{
    /// <summary>Directory holding the lock, state and participants for this user + session.</summary>
    string LockDirectory { get; }

    /// <summary>Directory holding one lease file per queued or active participant.</summary>
    string ParticipantsDirectory { get; }

    /// <summary>Short lock taken around every state read or update.</summary>
    string StateLockPath { get; }

    /// <summary>The coordination state document.</summary>
    string StatePath { get; }

    /// <summary>Long lock held only across a desktop-sensitive section.</summary>
    string ActiveLockPath { get; }

    /// <summary>Lease path for one participant process.</summary>
    string LeasePath(int processId, long startTicksUtc);

    /// <summary>Glob matching every lease belonging to this user + session.</summary>
    string LeaseSearchPattern { get; }

    /// <summary>Parses a lease file name back into the owning process identity.</summary>
    bool TryParseLeaseFileName(string fileName, out int processId, out long startTicksUtc);

    /// <summary>Creates the lock and participants directories, restricted to the current user.</summary>
    void EnsureDirectories();
}

/// <inheritdoc cref="IInteractiveDesktopPaths"/>
internal sealed class InteractiveDesktopPaths : IInteractiveDesktopPaths
{
    /// <summary>
    /// Redirects the whole coordination file set. Exists so multiprocess and file-level tests never
    /// touch the developer's live desktop coordination state. It relocates coordination; it never
    /// disables it, so a test still exercises the real locking protocol.
    /// </summary>
    /// <remarks>
    /// Coordination is scoped to whichever directory this resolves to, so every winapp process that
    /// must cooperate on one desktop has to agree on it. Two processes given different values each
    /// coordinate correctly within their own namespace and not at all with each other, which is why
    /// this is an unadvertised test seam rather than a user-facing knob, and why the recovery hints
    /// below always say "the same directory for every winapp process".
    /// </remarks>
    internal const string LockDirectoryOverrideVariable = "WINAPP_UI_LOCK_DIRECTORY";

    private const string FilePrefix = "interactive-desktop-";
    private const string LeaseExtension = ".lease";

    private readonly string _sessionToken;
    private bool _directoriesVerified;

    public InteractiveDesktopPaths(IProcessInspector processInspector)
    {
        _sessionToken = processInspector.CurrentSessionId.ToString(CultureInfo.InvariantCulture);
        LockDirectory = ResolveLockDirectory();
        ParticipantsDirectory = Path.Combine(LockDirectory, "participants");
        StateLockPath = Path.Combine(LockDirectory, $"{FilePrefix}{_sessionToken}.state.lock");
        StatePath = Path.Combine(LockDirectory, $"{FilePrefix}{_sessionToken}.state.json");
        ActiveLockPath = Path.Combine(LockDirectory, $"{FilePrefix}{_sessionToken}.active.lock");
        LeaseSearchPattern = $"{FilePrefix}{_sessionToken}-*{LeaseExtension}";
    }

    public string LockDirectory { get; }

    public string ParticipantsDirectory { get; }

    public string StateLockPath { get; }

    public string StatePath { get; }

    public string ActiveLockPath { get; }

    public string LeaseSearchPattern { get; }

    public string LeasePath(int processId, long startTicksUtc)
        => Path.Combine(
            ParticipantsDirectory,
            $"{FilePrefix}{_sessionToken}-{processId.ToString(CultureInfo.InvariantCulture)}-" +
            $"{ProcessInspector.FormatStartTicks(startTicksUtc)}{LeaseExtension}");

    public bool TryParseLeaseFileName(string fileName, out int processId, out long startTicksUtc)
    {
        processId = 0;
        startTicksUtc = 0;

        var expectedPrefix = $"{FilePrefix}{_sessionToken}-";
        if (!fileName.StartsWith(expectedPrefix, StringComparison.Ordinal)
            || !fileName.EndsWith(LeaseExtension, StringComparison.Ordinal))
        {
            return false;
        }

        var body = fileName[expectedPrefix.Length..^LeaseExtension.Length];
        var separator = body.LastIndexOf('-');
        if (separator <= 0 || separator == body.Length - 1)
        {
            return false;
        }

        return int.TryParse(body[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out processId)
            && long.TryParse(body[(separator + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out startTicksUtc);
    }

    public void EnsureDirectories()
    {
        // Verified once per process: the check is a DACL read per directory, and every state-lock
        // acquisition and lease open calls this.
        if (_directoriesVerified)
        {
            return;
        }

        EnsureRestrictedDirectory(LockDirectory);
        EnsureRestrictedDirectory(ParticipantsDirectory);
        EnsureTrustedStateFiles();
        _directoriesVerified = true;
    }

    /// <summary>
    /// Discards any of this session's state or lock files that another identity can still reach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs on <em>both</em> paths — a directory that had to be repaired and one that was already
    /// current-user-only — because securing a directory does not secure what is already inside it. An
    /// explicit ACE on a child survives a protected DACL being written to its parent, so a file
    /// seeded before the directory was locked down stays writable by whoever seeded it. That is true
    /// however the directory came to be secure, including by a build of this feature that predates
    /// this check.
    /// </para>
    /// <para>
    /// Scoped to these three files, which is what keeps it off the latency budget: three ACL reads
    /// once per process, against roughly five milliseconds for a full sweep of a deep participants
    /// directory. They are the ones whose <em>content or lock state</em> is trusted — the scheduler
    /// document, the transaction lock, and the desktop lock — so a foreign grant on any of them lets
    /// another user mint an owner, block every transaction, or hold the desktop.
    /// </para>
    /// <para>
    /// Leases are deliberately not swept here. A lease is inert on its own: liveness is only consulted
    /// for participants named in the state document, so a seeded lease for a process that appears
    /// nowhere in state is never opened. Its one reachable effect is to make corruption recovery
    /// refuse to reset state, which fails closed rather than open. Leases seeded before a repair are
    /// still discarded by <see cref="DiscardUntrustedArtifacts"/>, and the participants directory's own
    /// DACL stops new ones appearing.
    /// </para>
    /// </remarks>
    private void EnsureTrustedStateFiles()
    {
        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is null)
        {
            return;
        }

        foreach (var path in new[] { StatePath, StateLockPath, ActiveLockPath })
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                continue;
            }

            try
            {
                if (IsFileReachableOnlyByThisUser(file.GetAccessControl(), currentUser))
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Its permissions cannot even be read, which is not a file to trust either.
                throw UntrustedArtifact(path, ex.Message);
            }

            try
            {
                file.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw UntrustedArtifact(path, ex.Message);
            }
        }
    }

    private static UiCoordinationException UntrustedArtifact(string path, string reason)
        => new(
            UiCoordinationErrorCodes.Unavailable,
            $"The UI coordination file '{path}' is reachable by another user and could not be replaced: {reason}",
            "Close any winapp process using this directory and delete its contents, or point WINAPP_UI_LOCK_DIRECTORY at a directory this user owns.");

    private static string ResolveLockDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable(LockDirectoryOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return ValidateLockDirectory(overridePath.Trim(), LockDirectoryOverrideVariable);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new UiCoordinationException(
                UiCoordinationErrorCodes.Unavailable,
                "The local application data folder could not be resolved, so UI turn coordination has nowhere to store its state.",
                "Ensure LOCALAPPDATA is set for this user, or set WINAPP_UI_LOCK_DIRECTORY to the same fully qualified local directory for every winapp process on this desktop.");
        }

        return ValidateLockDirectory(
            Path.Combine(localAppData, "Microsoft", "WinAppCli", "locks"),
            "LOCALAPPDATA");
    }

    private static string ValidateLockDirectory(string path, string source)
    {
        // A relative path would resolve against the caller's working directory, so two processes in
        // different directories would coordinate against different files and silently not cooperate.
        if (!Path.IsPathFullyQualified(path))
        {
            throw new UiCoordinationException(
                UiCoordinationErrorCodes.Unavailable,
                $"The UI coordination directory resolved from {source} is not a fully qualified path.",
                "Set WINAPP_UI_LOCK_DIRECTORY to the same fully qualified local directory for every winapp process on this desktop, such as C:\\Temp\\winapp-locks.");
        }

        // Byte-range locking over SMB is advisory and unreliable for the exclusive-share protocol this
        // coordinator depends on, so a network path would produce silent overlap instead of exclusion.
        if (PathSafety.IsNetworkPath(path))
        {
            throw new UiCoordinationException(
                UiCoordinationErrorCodes.Unavailable,
                $"The UI coordination directory resolved from {source} is a network path, which cannot provide reliable exclusive file locks.",
                "Set WINAPP_UI_LOCK_DIRECTORY to a fully qualified path on a local drive.");
        }

        return Path.GetFullPath(path);
    }

    /// <remarks>
    /// The parent (<c>%LOCALAPPDATA%</c>) is already restricted to the current user, but that is not
    /// enough on its own: <c>WINAPP_UI_LOCK_DIRECTORY</c> can point at a shared location such as
    /// <c>C:\Temp</c>, and a directory created by an earlier run may have inherited permissive rules.
    /// Coordination state is not a secret, but a foreign writer could corrupt it or hold a lease and
    /// stall this user's UI workflows indefinitely, so an existing directory is inspected and repaired
    /// rather than trusted.
    /// </remarks>
    private static void EnsureRestrictedDirectory(string path)
    {
        var directoryInfo = new DirectoryInfo(path);

        if (!directoryInfo.Exists)
        {
            try
            {
                directoryInfo.Create(BuildCurrentUserOnlySecurity());
                return;
            }
            catch (UnauthorizedAccessException ex)
            {
                throw Unavailable(path, ex);
            }
            catch (IOException ex)
            {
                // Another winapp process can win the create race; that is success, not failure. Fall
                // through so the existing directory still gets its DACL verified below.
                directoryInfo.Refresh();
                if (!directoryInfo.Exists)
                {
                    throw Unavailable(path, ex);
                }
            }
        }

        RepairAccessRulesIfNeeded(directoryInfo);
    }

    /// <summary>
    /// Re-applies the current-user-only DACL when the existing one is inherited or grants any other
    /// identity. A no-op in the overwhelmingly common case, so the per-process check stays cheap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Together with <see cref="DiscardUntrustedArtifacts"/> this establishes the invariant the rest
    /// of coordination relies on: <em>after this returns, no coordination artifact in the directory is
    /// reachable by another user.</em>
    /// </para>
    /// <para>
    /// The steady-state case is covered by that invariant rather than by re-checking every file on
    /// every command. A foreign-owned or foreign-granted child cannot appear in a directory that is
    /// already current-user-only: creating a file there needs write access to the directory, which
    /// only this user has, and re-permissioning an existing file needs <c>WRITE_DAC</c> on it, which
    /// only its owner — this user — has. So the only way such a child exists is that it predates the
    /// directory being secured, and that is exactly the moment this method hands to
    /// <see cref="DiscardUntrustedArtifacts"/>, which removes it or fails closed. By induction every
    /// directory that reaches the "already secure" fast path was made secure by a pass that had
    /// already cleared it, or was created empty by us.
    /// </para>
    /// <para>
    /// That is what keeps the normal path free: one DACL read per directory per process, and no
    /// per-file work at all.
    /// </para>
    /// </remarks>
    private static void RepairAccessRulesIfNeeded(DirectoryInfo directoryInfo)
    {
        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is null)
        {
            // Without an identity there is nothing to scope the DACL to; inherited parent permissions
            // are the best available protection.
            return;
        }

        try
        {
            var existing = directoryInfo.GetAccessControl();
            if (IsCurrentUserOnly(existing, currentUser))
            {
                return;
            }

            directoryInfo.SetAccessControl(BuildCurrentUserOnlySecurity());

            // Verify rather than assume. Taking ownership can be refused without throwing on every
            // Windows configuration, and a directory whose owner is still a stranger remains
            // re-permissionable behind our back — so confirm the repair actually took effect and fail
            // closed if it did not.
            directoryInfo.Refresh();
            if (!IsCurrentUserOnly(directoryInfo.GetAccessControl(), currentUser))
            {
                throw new UiCoordinationException(
                    UiCoordinationErrorCodes.Unavailable,
                    $"The UI coordination directory '{directoryInfo.FullName}' is still owned or reachable by another user after repair.",
                    "Point WINAPP_UI_LOCK_DIRECTORY at a directory this user owns, or remove the override to use the default location under %LOCALAPPDATA%.");
            }

            // Repairing the directory does NOT repair what was already inside it. An explicit ACE on a
            // child file survives a protected DACL being written to its parent — inheritance changes
            // only propagate inherited ACEs — so a file placed here before the repair stays writable by
            // whoever placed it, and coordination would keep reading and trusting it.
            //
            // Only reached when the directory actually needed repair, which for the default location
            // under %LOCALAPPDATA% is the first run and never again.
            DiscardUntrustedArtifacts(directoryInfo);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException or InvalidOperationException)
        {
            // The directory is reachable but cannot be secured — for example it belongs to another user.
            // Coordinating through storage a third party can tamper with is worse than not running.
            throw new UiCoordinationException(
                UiCoordinationErrorCodes.Unavailable,
                $"The UI coordination directory '{directoryInfo.FullName}' could not be restricted to the current user: {ex.Message}",
                "Point WINAPP_UI_LOCK_DIRECTORY at a directory this user owns, or remove the override to use the default location under %LOCALAPPDATA%.");
        }
    }

    /// <summary>
    /// Deletes the coordination artifacts already inside a directory whose permissions were just
    /// repaired, and refuses to continue if any of them survive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called only after a repair, because only then can the directory have held files written under
    /// somebody else's permissions. A file keeps its own explicit ACEs when its parent's DACL is
    /// replaced — inheritance changes propagate only inherited ACEs — so "the directory is now
    /// current-user-only" says nothing about what is already in it. An <c>Everyone</c> grant placed on
    /// a pre-seeded <c>state.json</c> or <c>active.lock</c> survives, and coordination would go on
    /// reading and trusting a file another user can still rewrite.
    /// </para>
    /// <para>
    /// Everything here is reconstructible — state is rebuilt fresh, locks are just handles, leases are
    /// <c>DeleteOnClose</c> — so discarding is safe. A file that will <em>not</em> go is a different
    /// matter: it is held open by a process in a namespace that was just proven untrusted, which is
    /// not liveness evidence worth acting on. That fails closed rather than being accepted, because
    /// coordinating through storage a third party can tamper with is worse than not running.
    /// </para>
    /// <para>
    /// Scoped to this feature's own file-name shapes and to the top level of the directory. A
    /// <c>WINAPP_UI_LOCK_DIRECTORY</c> override may point at a path holding unrelated files; those are
    /// never read, so they are not a trust question, and deleting them would be destroying data that
    /// was not ours to touch.
    /// </para>
    /// </remarks>
    private static void DiscardUntrustedArtifacts(DirectoryInfo directoryInfo)
    {
        foreach (var pattern in s_coordinationArtifactPatterns)
        {
            FileInfo[] artifacts;
            try
            {
                artifacts = directoryInfo.GetFiles(pattern, SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new UiCoordinationException(
                    UiCoordinationErrorCodes.Unavailable,
                    $"The UI coordination directory '{directoryInfo.FullName}' could not be inspected after its permissions were repaired: {ex.Message}",
                    "Point WINAPP_UI_LOCK_DIRECTORY at a directory this user owns, or remove the override to use the default location under %LOCALAPPDATA%.");
            }

            foreach (var artifact in artifacts)
            {
                try
                {
                    artifact.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new UiCoordinationException(
                        UiCoordinationErrorCodes.Unavailable,
                        $"The UI coordination file '{artifact.FullName}' predates this directory being secured and could not be removed: {ex.Message}",
                        "Close any winapp process using this directory and delete its contents, or point WINAPP_UI_LOCK_DIRECTORY at a directory this user owns.");
                }
            }
        }
    }

    /// <summary>
    /// File-name shapes this feature writes: the per-session state and lock files, their publish
    /// temporaries and quarantined copies, and participant leases. Everything else in the directory
    /// belongs to somebody else and is left alone.
    /// </summary>
    private static readonly string[] s_coordinationArtifactPatterns =
    [
        $"{FilePrefix}*",
        "state.corrupt-*",
    ];

    /// <summary>
    /// Whether a coordination <em>file</em> can only be written by this user or by an identity that
    /// already outranks the protection entirely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not the directory rule. We create directories and set their owner explicitly, so
    /// requiring the owner to be exactly this user is both achievable and meaningful there. Files are
    /// created by ordinary I/O and inherit the system's default owner, which on an elevated process is
    /// <c>BUILTIN\Administrators</c> rather than the running user. Applying the directory rule to files
    /// deleted a perfectly good state document on every command in exactly that configuration.
    /// </para>
    /// <para>
    /// SYSTEM and Administrators are excluded from the check because they are not a boundary this can
    /// defend: either can already take ownership of any file and grant themselves whatever they like.
    /// Treating their presence as hostile buys no security and costs a false positive that destroys
    /// live state. The identity that matters is another <em>standard</em> user, and any ACE naming one
    /// still fails this.
    /// </para>
    /// </remarks>
    private static bool IsFileReachableOnlyByThisUser(FileSecurity security, SecurityIdentifier currentUser)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
            || !IsSelfOrPrivileged(owner, currentUser))
        {
            return false;
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.IdentityReference is not SecurityIdentifier sid || !IsSelfOrPrivileged(sid, currentUser))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSelfOrPrivileged(SecurityIdentifier sid, SecurityIdentifier currentUser)
        => sid == currentUser
            || sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
            || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);

    /// <summary>
    /// Whether <paramref name="security"/> describes an object only the current user can reach or
    /// re-permission.
    /// </summary>
    /// <remarks>
    /// Owner is checked as well as the DACL because the owner of an object implicitly holds
    /// <c>WRITE_DAC</c>: a foreign owner can rewrite even a protected, current-user-only DACL and grant
    /// itself access at any time. That matters most for a <c>WINAPP_UI_LOCK_DIRECTORY</c> override under
    /// a shared path, where another user may have created the directory first.
    /// <para>
    /// This is the strict <em>directory</em> rule. Files use
    /// <see cref="IsFileReachableOnlyByThisUser"/>, which differs for reasons documented there.
    /// </para>
    /// </remarks>
    internal static bool IsCurrentUserOnly(FileSystemSecurity security, SecurityIdentifier currentUser)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
            || owner != currentUser)
        {
            return false;
        }

        if (!security.AreAccessRulesProtected)
        {
            // Inherited rules can grant anyone the parent grants, which for a shared override directory
            // includes other users.
            return false;
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.IdentityReference is not SecurityIdentifier sid || sid != currentUser)
            {
                return false;
            }
        }

        return true;
    }

    private static DirectorySecurity BuildCurrentUserOnlySecurity()
    {
        var security = new DirectorySecurity();
        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is not null)
        {
            security.SetOwner(currentUser);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        return security;
    }

    private static UiCoordinationException Unavailable(string path, Exception ex)
        => new(
            UiCoordinationErrorCodes.Unavailable,
            $"The UI coordination directory '{path}' could not be created: {ex.Message}",
            "Check that the current user can write to the directory, or set WINAPP_UI_LOCK_DIRECTORY to a writable local directory.");
}

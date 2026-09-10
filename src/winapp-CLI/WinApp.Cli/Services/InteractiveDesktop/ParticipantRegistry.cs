// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace WinApp.Cli.Services.InteractiveDesktop;

/// <summary>
/// A process-held proof of liveness for one queued or active participant (spec §7.4). Opened
/// <c>FileShare.None</c> + <c>FileOptions.DeleteOnClose</c> and held for the command's entire
/// participation.
/// </summary>
/// <remarks>
/// This replaces heartbeat writes entirely. Windows closes the handle and deletes the file on normal
/// exit <em>and</em> on forced termination, so liveness needs no timestamps and no periodic I/O — and
/// a merely <em>suspended</em> process still holds its lease, so it is correctly treated as alive and
/// keeps its place in the queue.
/// </remarks>
internal interface IParticipantLease : IDisposable
{
    /// <summary>Full path of the lease file, for diagnostics.</summary>
    string Path { get; }
}

/// <summary>
/// Opens participant leases and answers liveness questions about recorded participants, which is the
/// only mechanism by which coordination state entries are pruned.
/// </summary>
internal interface IParticipantRegistry
{
    /// <summary>
    /// Opens this process's lease. Must be called while holding <c>state.lock</c> and before publishing
    /// any participation, so no published entry ever lacks liveness proof (spec §9).
    /// </summary>
    IParticipantLease OpenLease(int processId, long startTicksUtc);

    /// <summary>
    /// Whether the given participant is still alive. A lease that can be opened proves the holder is
    /// gone (and the stale file is removed); a lease that cannot be opened proves it is alive.
    /// </summary>
    bool IsParticipantLive(int processId, long startTicksUtc);

    /// <summary>
    /// Whether any lease in the participants directory is currently held. Used by corruption recovery,
    /// which must never reset state that a live participant is relying on (spec §12.3).
    /// </summary>
    bool AnyLiveParticipant();
}

/// <inheritdoc cref="IParticipantRegistry"/>
internal sealed class ParticipantRegistry(
    IInteractiveDesktopPaths paths,
    IProcessInspector processInspector,
    ILogger<ParticipantRegistry> logger) : IParticipantRegistry
{
    public IParticipantLease OpenLease(int processId, long startTicksUtc)
    {
        paths.EnsureDirectories();
        var path = paths.LeasePath(processId, startTicksUtc);

        try
        {
            // FileMode.Create rather than CreateNew: a same-identity file can only be a stale leftover
            // from a power loss (a live holder's file is deleted when its handle closes), and a leftover
            // is openable precisely because nobody holds it.
            var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            return new ParticipantLease(stream, path);
        }
        catch (IOException ex)
        {
            throw new UiCoordinationException(
                UiCoordinationErrorCodes.Unavailable,
                $"The UI coordination participant lease '{path}' could not be opened: {ex.Message}",
                "Retry the command. If it keeps failing, check that the coordination directory is on a local writable drive.");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UiCoordinationException(
                UiCoordinationErrorCodes.Unavailable,
                $"The UI coordination participant lease '{path}' could not be opened: {ex.Message}",
                "Check that the current user can write to the coordination directory.");
        }
    }

    public bool IsParticipantLive(int processId, long startTicksUtc)
    {
        var path = paths.LeasePath(processId, startTicksUtc);
        if (!File.Exists(path))
        {
            // The holder's handle closed (normal exit or kill), so Windows already removed the file.
            return false;
        }

        if (IsLeaseFileHeld(path))
        {
            return true;
        }

        // The lease is openable, so the holder is gone. Cross-check the PID/start pair as well: a
        // recycled PID must not resurrect a dead participant, and a lease left by a power loss belongs
        // to a process that no longer exists.
        var alive = processInspector.IsProcessAlive(processId, startTicksUtc);
        if (alive is true)
        {
            // The process exists with a matching start time but is not holding its lease — it either has
            // not opened it yet or has already torn it down. Neither is a participant we may prune,
            // because the registration protocol opens the lease before publishing and removes the entry
            // before closing it. Treat as live and let the owning process finish its own teardown.
            return true;
        }

        return false;
    }

    public bool AnyLiveParticipant()
    {
        if (!Directory.Exists(paths.ParticipantsDirectory))
        {
            return false;
        }

        IEnumerable<string> leaseFiles;
        try
        {
            leaseFiles = Directory.EnumerateFiles(paths.ParticipantsDirectory, paths.LeaseSearchPattern);
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }

        foreach (var leaseFile in leaseFiles)
        {
            if (IsLeaseFileHeld(leaseFile))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Probes one lease file. A sharing violation means a live holder; a successful open means the file
    /// is an orphan, which this method removes via <c>DeleteOnClose</c> so the participants directory
    /// does not accumulate leftovers after a power loss.
    /// </summary>
    private bool IsLeaseFileHeld(string leaseFilePath)
    {
        try
        {
            using var probe = new FileStream(
                leaseFilePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            return false;
        }
        catch (FileNotFoundException)
        {
            // The holder's handle closed between enumeration and this probe, so Windows removed the
            // file. That is proof of death, not of life — unlike a sharing violation below.
            return false;
        }
        catch (IOException)
        {
            // Sharing violation: another process holds this lease FileShare.None. That is the liveness
            // proof — including for a suspended process, which still owns its handle.
            //
            // Any other I/O failure also lands here deliberately. Unlike lock acquisition (see
            // CoordinationLockIo), a probe that cannot prove death must assume life: pruning a live
            // participant would strand its turn, whereas an over-cautious "live" only delays reclaim.
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            // A lease we cannot probe cannot be proven dead, and pruning a live participant would strand
            // its ownership. Fail safe by treating it as held.
            logger.LogDebug("Participant lease '{Path}' could not be probed: {Message}", leaseFilePath, ex.Message);
            return true;
        }
    }

    private sealed class ParticipantLease(FileStream stream, string path) : IParticipantLease
    {
        public string Path { get; } = path;

        public void Dispose() => stream.Dispose();
    }
}

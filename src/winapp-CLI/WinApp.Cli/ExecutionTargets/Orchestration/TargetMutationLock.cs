// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>
/// Held ownership of a target's mutation lock. Disposing releases it.
/// </summary>
/// <remarks>
/// Safe to dispose from any thread. This matters because the lock is held across <c>await</c>
/// boundaries during Sandbox creation and deployment, so acquisition and release routinely happen
/// on different thread-pool threads.
/// </remarks>
internal sealed class TargetMutationLease : IDisposable
{
    private FileStream? _stream;

    internal TargetMutationLease(FileStream stream, bool wasAbandoned)
    {
        _stream = stream;
        WasAbandoned = wasAbandoned;
    }

    /// <summary>
    /// True when the previous owner died without releasing the lock.
    /// </summary>
    /// <remarks>
    /// This is a recovery signal, not an error: the guest environment may have been left
    /// half-mutated. The caller must verify epoch and dirty state and reconcile before mutating
    /// further (spec §"Host coordination and state").
    /// </remarks>
    public bool WasAbandoned { get; }

    /// <summary>
    /// True once this lease has been released (by any thread), so a caller that captured the
    /// lease before release cannot go on believing it still holds exclusive access.
    /// </summary>
    /// <remarks>
    /// Backed by the same field <see cref="Dispose"/> already clears atomically, rather than a
    /// second flag, so the two can never disagree about whether the lease is still held.
    /// <see cref="Interlocked.Exchange(ref FileStream?, FileStream?)"/> in <see cref="Dispose"/>
    /// is a full fence, so a plain volatile read here is enough to observe it from any thread.
    /// </remarks>
    internal bool IsReleased => Volatile.Read(ref _stream) is null;

    /// <inheritdoc/>
    public void Dispose()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        if (stream is null)
        {
            return;
        }

        using (stream)
        {
            try
            {
                // Clearing the owner record marks this as a clean release, so the next acquirer
                // knows the environment was left consistent.
                stream.SetLength(0);
                stream.Flush();
            }
            catch (IOException)
            {
                // The file is going away with the handle. The next acquirer then sees a non-empty
                // record and treats it as abandoned, which is the safe direction to fail.
            }
        }
    }
}

/// <summary>Serializes guest-mutating operations for one target across host processes.</summary>
internal interface ITargetMutationLock
{
    /// <summary>
    /// Acquires the lock, or returns <see langword="null"/> if <paramref name="timeout"/> elapses.
    /// </summary>
    TargetMutationLease? TryAcquire(
        ExecutionTargetRef target,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// File-backed implementation of <see cref="ITargetMutationLock"/> (spec §"Host coordination and
/// state").
/// </summary>
/// <remarks>
/// There is no persistent host coordinator process, so mutual exclusion has to come from the OS.
/// The lock covers Sandbox creation and repair, guest winapp replacement, shared runtime
/// installation, deployment synchronization, and package registration. It deliberately does
/// <em>not</em> cover host build, running applications, or read-only UI Automation, so a long build
/// or a running app never blocks another workflow.
/// <para>
/// An exclusively opened file is used rather than a named <see cref="Mutex"/>. A Windows mutex is
/// thread-affine: it must be released by the exact thread that acquired it. Because this lock is
/// held across <c>await</c> boundaries, the continuation that releases it frequently runs on a
/// different thread-pool thread, where <c>ReleaseMutex</c> throws and the mutex stays held until
/// that original thread exits — blocking every other winapp process and eventually surfacing as a
/// false abandonment. A file handle has no thread affinity, and the kernel closes it when the
/// process dies, which also provides crash recovery.
/// </para>
/// <para>
/// The lock is per-target, so future targets serialize independently, and it lives in that target's
/// own state root, so it is scoped to the same user as the state it protects.
/// </para>
/// <para>
/// This lock is unrelated to Cooperative UI Turns. It protects guest environment and deployment
/// mutations, not the interactive desktop.
/// </para>
/// </remarks>
internal sealed class TargetMutationLock(ITargetStateDirectoryProvider directoryProvider) : ITargetMutationLock
{
    /// <summary>File name of the lock inside the target state root.</summary>
    internal const string LockFileName = "mutation.lock";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>Resolves the lock file path for <paramref name="target"/>.</summary>
    internal string GetLockFilePath(ExecutionTargetRef target) =>
        TargetPathSafety.CombineInsideRoot(directoryProvider.GetTargetRoot(target).FullName, LockFileName);

    /// <inheritdoc/>
    public TargetMutationLease? TryAcquire(
        ExecutionTargetRef target,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var path = GetLockFilePath(target);
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (TryOpenExclusive(path) is { } stream)
            {
                return Claim(stream);
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return null;
            }

            // Sleeping rather than spinning: contention here means another winapp process is doing
            // real work such as installing a runtime, which takes far longer than this interval.
            Thread.Sleep(PollInterval);
        }
    }

    private static FileStream? TryOpenExclusive(string path)
    {
        try
        {
            return new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
        }
        catch (IOException)
        {
            // Held by another process.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // Transiently locked, or the directory is being replaced.
            return null;
        }
    }

    /// <summary>
    /// Records this process as the owner and reports whether the previous one released cleanly.
    /// </summary>
    private static TargetMutationLease Claim(FileStream stream)
    {
        var wasAbandoned = stream.Length > 0;

        stream.SetLength(0);
        stream.Seek(0, SeekOrigin.Begin);

        var owner = string.Create(
            CultureInfo.InvariantCulture,
            $"{Environment.ProcessId} {DateTimeOffset.UtcNow:O}");
        stream.Write(Encoding.UTF8.GetBytes(owner));
        stream.Flush();

        return new TargetMutationLease(stream, wasAbandoned);
    }
}

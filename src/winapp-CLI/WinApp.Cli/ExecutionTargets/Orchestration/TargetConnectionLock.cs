// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>Held ownership of the target's connection-establishment critical section.</summary>
internal sealed class TargetConnectionLease : IDisposable
{
    private FileStream? _stream;

    internal TargetConnectionLease(FileStream stream)
    {
        _stream = stream;
    }

    public void Dispose()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        if (stream is null)
        {
            return;
        }

        using (stream)
        {
        }
    }
}

/// <summary>Serializes the one guest-agent channel across host processes.</summary>
internal interface ITargetConnectionLock
{
    TargetConnectionLease? TryAcquire(
        ExecutionTargetRef target,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// File-backed lock for bootstrap material and the one channel the guest agent accepts.
/// </summary>
/// <remarks>
/// Separate from the mutation lock: read-only UI inspection still does not block host builds or hold
/// guest mutation state. It only waits for the previous command's channel to close, which the agent's
/// one-connection protocol requires. The file handle has no thread affinity and is released by the
/// kernel if the host process dies.
/// </remarks>
internal sealed class TargetConnectionLock(ITargetStateDirectoryProvider directoryProvider)
    : ITargetConnectionLock
{
    internal const string LockFileName = "agent-connect.lock";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    public TargetConnectionLease? TryAcquire(
        ExecutionTargetRef target,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var path = TargetPathSafety.CombineInsideRoot(
            directoryProvider.GetTargetRoot(target).FullName,
            LockFileName);
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return new TargetConnectionLease(new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    return null;
                }

                Thread.Sleep(PollInterval);
            }
        }
    }
}

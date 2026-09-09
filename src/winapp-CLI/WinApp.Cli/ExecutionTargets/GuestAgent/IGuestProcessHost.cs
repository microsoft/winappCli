// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.ExecutionTargets.GuestAgent;

/// <summary>One child process the guest agent is running on behalf of a host operation.</summary>
/// <remarks>
/// An interface rather than the concrete host so the agent's dispatch, streaming, cancellation, and
/// epoch-fencing behaviour can be exercised without starting real processes — the same reason the
/// transport is an interface.
/// </remarks>
internal interface IGuestProcessHost : IAsyncDisposable
{
    /// <summary>The child's process ID, meaningful only within the current target epoch.</summary>
    int ProcessId { get; }

    /// <summary>UTC ticks when the child started, used to detect process-ID reuse.</summary>
    long StartTicksUtc { get; }

    /// <summary>Forwards a chunk of standard input to the child.</summary>
    Task WriteStandardInputAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>Signals end of standard input.</summary>
    void CloseStandardInput();

    /// <summary>Waits for the child to exit and for its output to be fully drained.</summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Asks the child to stop, then terminates its whole tree if it does not.</summary>
    Task<int> StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken);
}

/// <summary>Starts child processes for the guest agent.</summary>
internal interface IGuestProcessHostFactory
{
    /// <summary>Starts a child process for <paramref name="request"/>.</summary>
    /// <param name="request">What to run.</param>
    /// <param name="onOutput">Receives each stdout and stderr chunk, in order.</param>
    IGuestProcessHost Start(GuestExecRequest request, Action<GuestStreamId, ReadOnlyMemory<byte>> onOutput);
}

/// <summary>Starts real Windows processes inside Job Objects.</summary>
/// <remarks>
/// Supplies the running winapp binary as the containment barrier. Only the agent can assert that
/// its own executable understands the barrier verb, which is why the path is provided here rather
/// than discovered inside the process host.
/// </remarks>
internal sealed class GuestProcessHostFactory : IGuestProcessHostFactory
{
    /// <summary>The winapp binary used as the per-operation containment barrier.</summary>
    public string? BarrierExecutable { get; init; } = Environment.ProcessPath;

    /// <inheritdoc/>
    public IGuestProcessHost Start(
        GuestExecRequest request,
        Action<GuestStreamId, ReadOnlyMemory<byte>> onOutput) =>
        GuestProcessHost.Start(request, onOutput, BarrierExecutable);
}

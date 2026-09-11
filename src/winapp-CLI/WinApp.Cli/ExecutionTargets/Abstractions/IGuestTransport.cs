// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.ExecutionTargets.Abstractions;

/// <summary>
/// The narrow byte-level channel a target backend hands to orchestration (spec §"Transport and
/// command channel").
/// </summary>
/// <remarks>
/// This interface is deliberately tiny: send a frame, receive a frame, close. Everything with
/// meaning — handshakes, operations, streaming, cancellation, artifacts, epoch fencing — lives in
/// the single target-neutral <c>GuestCommandChannel</c> above it.
/// <para>
/// Binary streams are carried as a sequence of typed frames rather than a separate raw-stream API.
/// That keeps one framing, one size limit, and one validation path for every byte that crosses the
/// boundary, and it keeps the fake used by contract tests trivial to implement correctly.
/// </para>
/// <para>
/// Implementations are not required to be thread-safe. The command channel serializes access.
/// </para>
/// </remarks>
internal interface IGuestTransport : IAsyncDisposable
{
    /// <summary>False once the peer closed the connection or a fatal transport error occurred.</summary>
    bool IsConnected { get; }

    /// <summary>
    /// Sends one complete frame.
    /// </summary>
    /// <exception cref="ExecutionTargetException">
    /// Transport failed. The connection must be treated as dead.
    /// </exception>
    ValueTask SendFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

    /// <summary>
    /// Receives one complete frame, or <see langword="null"/> when the peer closed the connection
    /// gracefully.
    /// </summary>
    /// <remarks>
    /// A graceful close is reported as <see langword="null"/> rather than an exception so callers
    /// can distinguish "the guest finished and went away" from "the channel broke", which the spec
    /// requires in order to keep infrastructure failures separable from application outcomes.
    /// </remarks>
    /// <exception cref="ExecutionTargetException">
    /// Transport failed, or the peer sent a frame that violates framing limits.
    /// </exception>
    ValueTask<ReadOnlyMemory<byte>?> ReceiveFrameAsync(CancellationToken cancellationToken);
}

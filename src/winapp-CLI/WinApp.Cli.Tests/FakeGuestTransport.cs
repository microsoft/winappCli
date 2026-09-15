// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Threading.Channels;
using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.Tests;

/// <summary>
/// An in-memory <see cref="IGuestTransport"/> plus a scriptable peer, standing in for a real guest.
/// </summary>
/// <remarks>
/// This is the seam acceptance criterion 13 relies on: deployment, execution, streaming,
/// cancellation, and error handling must all be verifiable without invoking Windows Sandbox. If any
/// of that logic ever reached for a <c>wsb</c> command or a Sandbox path, these tests could not
/// pass — which is what makes them a structural guard on the provider boundary rather than just
/// convenient mocks.
/// </remarks>
internal sealed class FakeGuestTransport : IGuestTransport
{
    private readonly Channel<ReadOnlyMemory<byte>> _toClient =
        Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

    private readonly Channel<ReadOnlyMemory<byte>> _toPeer =
        Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

    /// <inheritdoc/>
    public bool IsConnected { get; private set; } = true;

    /// <summary>Frames the channel under test has sent, for the peer to read.</summary>
    public ChannelReader<ReadOnlyMemory<byte>> PeerInbox => _toPeer.Reader;

    /// <inheritdoc/>
    public ValueTask SendFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TransportFailed,
                "The fake transport is closed.");
        }

        // Copy: the caller may reuse its buffer once the send completes.
        _toPeer.Writer.TryWrite(payload.ToArray());
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public async ValueTask<ReadOnlyMemory<byte>?> ReceiveFrameAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _toClient.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            IsConnected = false;
            return null;
        }
    }

    /// <summary>Delivers a frame to the channel under test, as the guest would.</summary>
    public void PeerSend(byte[] payload) => _toClient.Writer.TryWrite(payload);

    /// <summary>Closes the connection cleanly, as a guest that finished and went away would.</summary>
    public void PeerClose() => _toClient.Writer.TryComplete();

    /// <summary>Breaks the connection, as a transport failure would.</summary>
    public void Break()
    {
        IsConnected = false;
        _toClient.Writer.TryComplete();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        _toClient.Writer.TryComplete();
        _toPeer.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>
/// An authenticated, encrypted <see cref="IGuestTransport"/> over any duplex byte stream
/// (spec §"Transport and command channel").
/// </summary>
/// <remarks>
/// The Windows Sandbox backend composes this over a TCP stream to the guest IP, but nothing here
/// is Sandbox-specific: a future backend can reuse it over a Hyper-V socket or any other duplex
/// stream, which is exactly the separation the spec requires.
/// <para>
/// Both peers authenticate with a pre-shared key generated fresh for each guest boot and delivered
/// through the read-only bootstrap folder. Only a peer holding that key can produce a frame that
/// authenticates, so an unrelated host or network caller cannot drive the agent.
/// </para>
/// <para>
/// Sequence numbers are tracked locally per direction and never transmitted, so replayed or
/// reordered frames fail authentication rather than relying on the parser to notice.
/// </para>
/// </remarks>
internal sealed class GuestSecureChannel : IGuestTransport
{
    private readonly Stream _stream;
    private readonly GuestSessionKeys _keys;
    private readonly GuestRole _role;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private ulong _sendSequence;
    private ulong _receiveSequence;
    private bool _disposed;

    private GuestSecureChannel(Stream stream, GuestSessionKeys keys, GuestRole role, int negotiatedVersion)
    {
        _stream = stream;
        _keys = keys;
        _role = role;
        NegotiatedVersion = negotiatedVersion;

        // Sequence 0 is consumed by each side's handshake confirmation frame.
        _sendSequence = 1;
        _receiveSequence = 1;
        IsConnected = true;
    }

    /// <inheritdoc/>
    public bool IsConnected { get; private set; }

    /// <summary>Protocol revision both peers agreed on.</summary>
    public int NegotiatedVersion { get; }

    /// <summary>
    /// Performs the handshake and returns a connected channel.
    /// </summary>
    /// <param name="stream">Duplex stream to the peer. Owned by the returned channel.</param>
    /// <param name="role">Which end this process is.</param>
    /// <param name="preSharedKey">Per-boot shared secret.</param>
    /// <param name="targetId">Target both sides must agree they are serving.</param>
    /// <param name="targetEpoch">Generation both sides must agree on.</param>
    /// <param name="cancellationToken">Cancels the handshake.</param>
    /// <exception cref="ExecutionTargetException">
    /// The peer failed to authenticate, disagreed about the target or epoch, or has no overlapping
    /// protocol range.
    /// </exception>
    public static async Task<GuestSecureChannel> EstablishAsync(
        Stream stream,
        GuestRole role,
        ReadOnlyMemory<byte> preSharedKey,
        string targetId,
        string targetEpoch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var localHello = BuildHello();
        try
        {
            await stream.WriteAsync(localHello, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            throw ClassifyHandshakeFailure(ex);
        }

        var remoteHello = new byte[GuestProtocol.HelloSize];
        try
        {
            await stream.ReadExactlyAsync(remoteHello, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            throw ClassifyHandshakeFailure(ex);
        }

        var negotiatedVersion = NegotiateVersion(remoteHello);

        // The host's random always comes first in the salt so both sides derive identical keys
        // regardless of which one is deriving.
        var (hostHello, guestHello) = role == GuestRole.Host
            ? (localHello, remoteHello)
            : (remoteHello, localHello);

        var keys = GuestSessionKeys.Derive(
            preSharedKey.Span,
            hostHello.AsSpan(GuestProtocol.HelloSize - GuestProtocol.HandshakeRandomSize),
            guestHello.AsSpan(GuestProtocol.HelloSize - GuestProtocol.HandshakeRandomSize));

        var channel = new GuestSecureChannel(stream, keys, role, negotiatedVersion);
        try
        {
            await channel.ConfirmAsync(hostHello, guestHello, targetId, targetEpoch, cancellationToken)
                .ConfigureAwait(false);
            return channel;
        }
        catch
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc/>
    public async ValueTask SendFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendFrameCoreAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask<ReadOnlyMemory<byte>?> ReceiveFrameAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var header = new byte[GuestFrameCodec.LengthPrefixSize];
        var read = await ReadAtLeastAsync(header, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            // A clean close between frames is how a peer says "I am done", not a failure.
            IsConnected = false;
            return null;
        }

        if (!GuestFrameCodec.TryReadBodyLength(header, out var bodyLength, out var headerError))
        {
            throw FramingFailure(headerError);
        }

        var frame = new byte[GuestFrameCodec.LengthPrefixSize + bodyLength];
        header.CopyTo(frame.AsSpan());
        try
        {
            await _stream.ReadExactlyAsync(frame.AsMemory(GuestFrameCodec.LengthPrefixSize), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            IsConnected = false;
            throw TransportFailure("The connection closed in the middle of a frame.", ex);
        }

        var plaintext = new byte[bodyLength - GuestFrameCodec.TagSize];
        var sequence = _receiveSequence;
        if (!_keys.GetReceiveCodec(_role).TryDecode(frame, sequence, plaintext, out var written, out var error))
        {
            throw FramingFailure(error);
        }

        _receiveSequence = checked(sequence + 1);
        return plaintext.AsMemory(0, written);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsConnected = false;
        _keys.Dispose();
        _sendLock.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask SendFrameCoreAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var buffer = new byte[GuestFrameCodec.GetEncodedSize(payload.Length)];
        var sequence = _sendSequence;
        var written = _keys.GetSendCodec(_role).Encode(payload.Span, sequence, buffer);
        _sendSequence = checked(sequence + 1);

        try
        {
            await _stream.WriteAsync(buffer.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            IsConnected = false;
            throw TransportFailure("Sending to the guest failed.", ex);
        }
    }

    /// <summary>
    /// Exchanges the encrypted confirmation frames that authenticate both peers and bind the
    /// session to one target and epoch.
    /// </summary>
    private async Task ConfirmAsync(
        byte[] hostHello,
        byte[] guestHello,
        string targetId,
        string targetEpoch,
        CancellationToken cancellationToken)
    {
        var transcript = ComputeTranscript(hostHello, guestHello);
        var confirmation = new GuestHandshakeConfirmation
        {
            Transcript = transcript,
            TargetId = targetId,
            TargetEpoch = targetEpoch,
            Role = _role.ToString(),
            NegotiatedVersion = NegotiatedVersion,
        };

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            confirmation,
            GuestProtocolJsonContext.Default.GuestHandshakeConfirmation);

        // Both confirmations use sequence 0; SendFrameCoreAsync advances to 1 afterwards.
        _sendSequence = 0;
        _receiveSequence = 0;
        await SendFrameCoreAsync(payload, cancellationToken).ConfigureAwait(false);

        var received = await ReceiveFrameAsync(cancellationToken).ConfigureAwait(false)
            ?? throw AuthenticationFailure("The peer closed the connection before confirming the handshake.");

        GuestHandshakeConfirmation? remote;
        try
        {
            remote = JsonSerializer.Deserialize(
                received.Span,
                GuestProtocolJsonContext.Default.GuestHandshakeConfirmation);
        }
        catch (JsonException ex)
        {
            throw AuthenticationFailure("The peer sent a malformed handshake confirmation.", ex);
        }

        if (remote is null)
        {
            throw AuthenticationFailure("The peer sent an empty handshake confirmation.");
        }

        Validate(remote, transcript, targetId, targetEpoch);
    }

    private void Validate(
        GuestHandshakeConfirmation remote,
        string expectedTranscript,
        string targetId,
        string targetEpoch)
    {
        // Fixed-time comparison keeps the transcript check from leaking a match prefix.
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(remote.Transcript),
                System.Text.Encoding.UTF8.GetBytes(expectedTranscript)))
        {
            throw AuthenticationFailure("The peer's handshake transcript did not match.");
        }

        var expectedRemoteRole = _role == GuestRole.Host ? GuestRole.Guest : GuestRole.Host;
        if (!string.Equals(remote.Role, expectedRemoteRole.ToString(), StringComparison.Ordinal))
        {
            throw AuthenticationFailure("The peer claimed the wrong role, so both ends would send on the same key.");
        }

        if (!string.Equals(remote.TargetId, targetId, StringComparison.Ordinal))
        {
            throw AuthenticationFailure("The peer is serving a different execution target.");
        }

        if (!string.Equals(remote.TargetEpoch, targetEpoch, StringComparison.Ordinal))
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TargetStale,
                "The guest belongs to a different Windows Sandbox generation than this command expected.",
                userAction: "Retry the command so it reconnects to the current Sandbox.");
        }

        if (remote.NegotiatedVersion != NegotiatedVersion)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.AgentIncompatible,
                "The guest agent selected a different protocol version than the host.");
        }
    }

    private static byte[] BuildHello()
    {
        var hello = new byte[GuestProtocol.HelloSize];
        GuestProtocol.HandshakeMagic.CopyTo(hello);
        BinaryPrimitives.WriteUInt16BigEndian(hello.AsSpan(4), (ushort)GuestProtocol.MinimumVersion);
        BinaryPrimitives.WriteUInt16BigEndian(hello.AsSpan(6), (ushort)GuestProtocol.CurrentVersion);
        RandomNumberGenerator.Fill(hello.AsSpan(8, GuestProtocol.HandshakeRandomSize));
        return hello;
    }

    /// <summary>
    /// Picks the newest revision both peers support, or fails when the ranges do not overlap.
    /// </summary>
    private static int NegotiateVersion(byte[] remoteHello)
    {
        if (!remoteHello.AsSpan(0, 4).SequenceEqual(GuestProtocol.HandshakeMagic))
        {
            throw TransportFailure("The peer is not a winapp guest agent.", innerException: null);
        }

        var remoteMinimum = (int)BinaryPrimitives.ReadUInt16BigEndian(remoteHello.AsSpan(4));
        var remoteMaximum = (int)BinaryPrimitives.ReadUInt16BigEndian(remoteHello.AsSpan(6));

        var negotiated = Math.Min(GuestProtocol.CurrentVersion, remoteMaximum);
        if (negotiated < Math.Max(GuestProtocol.MinimumVersion, remoteMinimum))
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.AgentIncompatible,
                $"The guest agent speaks protocol {remoteMinimum}-{remoteMaximum}, but this winapp speaks {GuestProtocol.MinimumVersion}-{GuestProtocol.CurrentVersion}.",
                userAction: "Update winapp on the host so it matches the guest agent.",
                nextCommand: new ExecutionTargetNextCommand { Command = "winapp update", Advisory = false });
        }

        return negotiated;
    }

    private static string ComputeTranscript(byte[] hostHello, byte[] guestHello)
    {
        Span<byte> buffer = stackalloc byte[GuestProtocol.HelloSize * 2];
        hostHello.CopyTo(buffer);
        guestHello.CopyTo(buffer[GuestProtocol.HelloSize..]);

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(buffer, hash);
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Reads up to <paramref name="buffer"/>'s length, returning 0 only for a clean close before
    /// any byte arrived.
    /// </summary>
    private async ValueTask<int> ReadAtLeastAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            int read;
            try
            {
                read = await _stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                IsConnected = false;
                throw TransportFailure("Reading from the guest failed.", ex);
            }

            if (read == 0)
            {
                if (total == 0)
                {
                    return 0;
                }

                IsConnected = false;
                throw TransportFailure("The connection closed in the middle of a frame header.", innerException: null);
            }

            total += read;
        }

        return total;
    }

    private static ExecutionTargetException FramingFailure(GuestFrameError error) =>
        error == GuestFrameError.AuthenticationFailed
            ? AuthenticationFailure("A message from the guest failed authentication.")
            : ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TransportFailed,
                "The guest sent a malformed message.",
                userAction: "Retry the command. If it keeps failing, stop the Sandbox and try again.",
                context: new Dictionary<string, string> { ["framing"] = error.ToString() });

    private static ExecutionTargetException AuthenticationFailure(string message, Exception? innerException = null) =>
        ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.TransportFailed,
            message,
            userAction: "Retry the command so winapp re-establishes a trusted connection to the guest.",
            innerException: innerException);

    private static ExecutionTargetException TransportFailure(string message, Exception? innerException) =>
        ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.TransportFailed,
            message,
            userAction: "Retry the command.",
            innerException: innerException);

    /// <summary>Context key marking a peer that accepted the connection and then closed it.</summary>
    /// <remarks>
    /// Distinguished from every other handshake failure because the two mean opposite things to the
    /// host. A refused or unanswered connection means the agent is gone and should be repaired; a
    /// connection the agent <em>accepted</em> and then dropped means the agent was alive and
    /// declining this one — usually because it is at its channel ceiling. Repairing on that would
    /// replace a working agent underneath the channels it is still serving.
    /// </remarks>
    public const string ClosedDuringHandshakeKey = "closedDuringHandshake";

    /// <summary>
    /// Turns a handshake I/O failure into a coded one, marking the peer-closed case.
    /// </summary>
    /// <remarks>
    /// Every failure here becomes an <see cref="ExecutionTargetException"/>. That matters as much as
    /// the classification: callers that decide between reporting and repairing catch only coded
    /// failures, so a raw <see cref="IOException"/> escaping would bypass that decision entirely and
    /// surface to the user as an uncoded I/O error instead.
    /// <para>
    /// The peer-closed case covers both shapes the same event takes on the wire. A peer that closes
    /// a drained socket sends FIN, which surfaces as <see cref="EndOfStreamException"/>. A peer that
    /// closes one with the hello still unread — which is exactly what dropping a connection at the
    /// tracked ceiling does — sends RST instead, which surfaces as a
    /// <see cref="SocketException"/> for a reset or aborted connection, usually wrapped in an
    /// <see cref="IOException"/>. Recognising only the first would leave the second uncoded.
    /// </para>
    /// </remarks>
    private static ExecutionTargetException ClassifyHandshakeFailure(Exception exception) =>
        IsPeerClosed(exception)
            ? PeerClosedDuringHandshake(exception)
            : ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TransportFailed,
                "The connection to the guest agent failed during the handshake.",
                userAction: "Retry the command.",
                innerException: exception);

    /// <summary>Whether this failure is the peer closing a connection it had accepted.</summary>
    private static bool IsPeerClosed(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is EndOfStreamException)
            {
                return true;
            }

            if (current is SocketException socket &&
                socket.SocketErrorCode is SocketError.ConnectionReset
                    or SocketError.ConnectionAborted
                    or SocketError.Shutdown)
            {
                return true;
            }
        }

        return false;
    }

    private static ExecutionTargetException PeerClosedDuringHandshake(Exception innerException) =>
        ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.TransportFailed,
            "The peer closed the connection during the handshake.",
            userAction: "Retry the command.",
            context: new Dictionary<string, string> { [ClosedDuringHandshakeKey] = "true" },
            innerException: innerException);
}

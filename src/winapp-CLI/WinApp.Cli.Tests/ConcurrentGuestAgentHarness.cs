// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Threading.Channels;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// Runs the real guest accept loop, with real per-connection secure channels, in one process.
/// </summary>
/// <remarks>
/// Every connection here completes the same handshake a real host does — its own randoms, its own
/// derived keys, its own directional sequence numbers — over an in-memory duplex pair rather than a
/// socket. That is what makes the concurrency guarantees testable as security properties: a test can
/// tamper with one channel's bytes, replay a frame, or drop a connection and observe what the other
/// channels do, with no Sandbox, listener, or network stack involved.
/// </remarks>
internal sealed class ConcurrentGuestAgentHarness : IAsyncDisposable
{
    /// <summary>Target both ends of every connection must agree they are serving.</summary>
    public const string TargetId = "sandbox-default-6b0d287c0c51bc40";

    /// <summary>
    /// How long a host waits to be accepted and authenticated.
    /// </summary>
    /// <remarks>
    /// A deadline rather than an unbounded wait so that a regression to serving one channel at a
    /// time fails these tests instead of hanging them: the second connection would simply never be
    /// accepted.
    /// </remarks>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Generation both ends of every connection must agree on.</summary>
    public static readonly ExecutionTargetEpoch AgentEpoch = ExecutionTargetEpoch.Create("sandbox-1", "nonce-a");

    private static readonly GuestSessionInfo InteractiveSession = new(SessionId: 1, "WinSta0", HasInputDesktop: true);

    private static readonly GuestAgentIdentity Identity = new(
        Version: "9.9.9",
        BinaryHash: "abc123",
        Architecture: "arm64",
        ProtocolMinimum: GuestProtocol.MinimumVersion,
        ProtocolMaximum: GuestProtocol.CurrentVersion);

    private readonly byte[] _preSharedKey = RandomNumberGenerator.GetBytes(GuestProtocol.PreSharedKeySize);
    private readonly PendingConnections _source;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _serving;

    private int _closedConnections;

    /// <summary>Creates a running agent that serves at most <paramref name="limits"/> channels.</summary>
    public ConcurrentGuestAgentHarness(
        GuestConnectionLimits? limits = null,
        int maxOperationsPerConnection = GuestCommandServer.DefaultMaxConcurrentOperations)
    {
        _source = new PendingConnections
        {
            Authenticate = (stream, cancellationToken) => GuestSecureChannel.EstablishAsync(
                stream, GuestRole.Guest, _preSharedKey, TargetId, AgentEpoch.Value, cancellationToken),
        };

        Acceptor = new GuestConnectionAcceptor(
            _source,
            (transport, refusal) => new GuestCommandServer(
                transport,
                AgentEpoch,
                Processes,
                new StaticGuestSessionProbe(InteractiveSession),
                Identity)
            {
                AdmissionRefusal = refusal,
                MaxConcurrentOperations = maxOperationsPerConnection,
            },
            limits,
            connectionClosed: () => Interlocked.Increment(ref _closedConnections));

        _serving = Acceptor.RunAsync(_shutdown.Token);
    }

    /// <summary>The accept loop under test.</summary>
    public GuestConnectionAcceptor Acceptor { get; }

    /// <summary>Child processes the agent started, across every channel.</summary>
    public FakeGuestProcessHostFactory Processes { get; } = new();

    /// <summary>How many connections have finished.</summary>
    public int ClosedConnections => Volatile.Read(ref _closedConnections);

    /// <summary>Opens a channel and returns the ordinary host-side command channel over it.</summary>
    public async Task<HostChannel> ConnectAsync(CancellationToken cancellationToken)
    {
        var connection = await ConnectRawAsync(cancellationToken).ConfigureAwait(false);
        return connection.IntoCommandChannel();
    }

    /// <summary>
    /// Opens a channel and returns raw framing access to it, for tests that need to choose their own
    /// operation identities or write bytes the host channel would never produce.
    /// </summary>
    public async Task<RawHostConnection> ConnectRawAsync(CancellationToken cancellationToken)
    {
        var (client, server) = DuplexStreamPair.Create();
        var recording = new RecordingStream(client);

        // Offered before the host handshake starts: both halves are a real exchange, so each blocks
        // until the other has written.
        _source.Offer(server);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ConnectTimeout);

        try
        {
            var transport = await GuestSecureChannel.EstablishAsync(
                recording,
                GuestRole.Host,
                _preSharedKey,
                TargetId,
                AgentEpoch.Value,
                deadline.Token).ConfigureAwait(false);

            return new RawHostConnection(transport, recording);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await recording.DisposeAsync().ConfigureAwait(false);
            throw new TimeoutException(
                "The guest agent did not accept and authenticate this connection, which is what serving one channel at a time looks like.");
        }
    }

    /// <summary>
    /// Offers a connection that is accepted but never authenticates, as a stalled peer would.
    /// </summary>
    /// <returns>The host end, which the caller keeps open and eventually disposes.</returns>
    public Stream OfferStalledConnection()
    {
        var (client, server) = DuplexStreamPair.Create();
        _source.Offer(server);
        return client;
    }

    /// <summary>Shuts the agent down and waits for the accept loop to finish draining.</summary>
    public async Task ShutdownAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _serving.ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await ShutdownAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Already shutting down.
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    /// <summary>Connections offered to the agent, standing in for a listener's accept queue.</summary>
    private sealed class PendingConnections : IGuestConnectionSource
    {
        private readonly Channel<Stream> _pending = Channel.CreateUnbounded<Stream>();

        public required Func<Stream, CancellationToken, Task<GuestSecureChannel>> Authenticate { get; init; }

        public void Offer(Stream guestSide) => _pending.Writer.TryWrite(guestSide);

        public async Task<IGuestPendingConnection> AcceptAsync(CancellationToken cancellationToken)
        {
            var stream = await _pending.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return new Pending(stream, Authenticate);
        }

        private sealed class Pending(
            Stream stream,
            Func<Stream, CancellationToken, Task<GuestSecureChannel>> authenticate) : IGuestPendingConnection
        {
            public async Task<IGuestTransport> AuthenticateAsync(CancellationToken cancellationToken)
            {
                try
                {
                    return await authenticate(stream, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }

            public void Dispose() => stream.Dispose();
        }
    }
}

/// <summary>One host channel and the stream underneath it.</summary>
internal sealed class HostChannel(GuestCommandChannel channel, RecordingStream stream) : IAsyncDisposable
{
    /// <summary>The ordinary host command channel.</summary>
    public GuestCommandChannel Channel { get; } = channel;

    /// <summary>The bytes this channel writes, for tamper and replay tests.</summary>
    public RecordingStream Stream { get; } = stream;

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => Channel.DisposeAsync();
}

/// <summary>One host channel with raw frame access.</summary>
internal sealed class RawHostConnection(IGuestTransport transport, RecordingStream stream) : IAsyncDisposable
{
    /// <summary>The bytes this channel writes, for tamper and replay tests.</summary>
    public RecordingStream Stream { get; } = stream;

    /// <summary>Hands the transport to an ordinary host command channel and starts it.</summary>
    public HostChannel IntoCommandChannel()
    {
        var channel = new GuestCommandChannel(transport, ConcurrentGuestAgentHarness.AgentEpoch);
        channel.Start();
        return new HostChannel(channel, Stream);
    }

    /// <summary>Sends a control message with an operation identity the test chose.</summary>
    public ValueTask SendAsync(GuestMessage message, CancellationToken cancellationToken) =>
        transport.SendFrameAsync(GuestPayloadCodec.EncodeJson(message), cancellationToken);

    /// <summary>Sends a chunk of one operation's standard input.</summary>
    public ValueTask SendStandardInputAsync(Guid operationId, byte[] data, CancellationToken cancellationToken) =>
        transport.SendFrameAsync(
            GuestPayloadCodec.EncodeStream(operationId, GuestStreamId.StandardInput, data),
            cancellationToken);

    /// <summary>Reads the next control message, skipping stream frames.</summary>
    public async Task<GuestMessage> ReceiveMessageAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var frame = await transport.ReceiveFrameAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The guest closed the connection.");

            if (GuestPayloadCodec.TryDecodeJson(frame.Span) is { } message)
            {
                return message;
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => transport.DisposeAsync();
}

/// <summary>
/// A stream that remembers the last frame written through it, so a test can replay or corrupt it.
/// </summary>
/// <remarks>
/// The secure channel writes exactly one frame per call, so the recorded buffer is a complete,
/// correctly authenticated frame — which is what makes a replay test meaningful rather than a test
/// that the parser rejects truncated input.
/// </remarks>
internal sealed class RecordingStream(Stream inner) : Stream
{
    private byte[]? _lastFrame;

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => true;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Writes the last frame a second time, bypassing the channel's sequence counter.</summary>
    public ValueTask ReplayLastFrameAsync(CancellationToken cancellationToken) =>
        inner.WriteAsync(
            _lastFrame ?? throw new InvalidOperationException("Nothing has been sent on this channel yet."),
            cancellationToken);

    /// <summary>Writes bytes the channel never produced.</summary>
    public ValueTask InjectAsync(byte[] bytes, CancellationToken cancellationToken) =>
        inner.WriteAsync(bytes, cancellationToken);

    /// <inheritdoc/>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer, cancellationToken);

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    /// <inheritdoc/>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _lastFrame = buffer.ToArray();
        return inner.WriteAsync(buffer, cancellationToken);
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        _lastFrame = buffer.AsSpan(offset, count).ToArray();
        inner.Write(buffer, offset, count);
    }

    /// <inheritdoc/>
    public override void Flush() => inner.Flush();

    /// <inheritdoc/>
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

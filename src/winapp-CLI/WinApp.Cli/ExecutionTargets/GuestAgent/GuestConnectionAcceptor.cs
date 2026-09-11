// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Net.Sockets;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.ExecutionTargets.GuestAgent;

/// <summary>Somewhere inbound guest connections arrive from.</summary>
/// <remarks>
/// An interface rather than a <see cref="TcpListener"/> so the whole admission, isolation, and
/// shutdown contract is testable over in-memory streams, with no listener, socket, or Sandbox.
/// </remarks>
internal interface IGuestConnectionSource
{
    /// <summary>Waits for the next inbound connection, before it has authenticated.</summary>
    Task<IGuestPendingConnection> AcceptAsync(CancellationToken cancellationToken);
}

/// <summary>An accepted connection that has not yet proved who it is.</summary>
/// <remarks>
/// <see cref="IDisposable.Dispose"/> must be idempotent: the acceptor releases a connection when it
/// fails to authenticate and again once its server has finished with it.
/// </remarks>
internal interface IGuestPendingConnection : IDisposable
{
    /// <summary>
    /// Completes the secure handshake, transferring ownership of the connection to the transport.
    /// </summary>
    Task<IGuestTransport> AuthenticateAsync(CancellationToken cancellationToken);
}

/// <summary>Creates the server for one connection; a non-null refusal makes it serve nothing.</summary>
internal delegate GuestCommandServer GuestConnectionServerFactory(
    IGuestTransport transport,
    ExecutionTargetErrorInfo? refusal);

/// <summary>Bounds on what the agent will serve at once.</summary>
/// <param name="MaxConnections">Host channels served concurrently before new ones are refused.</param>
/// <param name="HandshakeTimeout">How long an accepted peer has to authenticate.</param>
/// <param name="RefusedConnectionLifetime">
/// How long a refused connection is kept alive to deliver its refusal before it is dropped.
/// </param>
internal sealed record GuestConnectionLimits(
    int MaxConnections,
    TimeSpan HandshakeTimeout,
    TimeSpan RefusedConnectionLifetime)
{
    /// <summary>
    /// Concurrent host channels the agent serves.
    /// </summary>
    /// <remarks>
    /// Chosen to cover the workflows the specification requires to overlap — a running application
    /// plus inspection, input, capture, and a short command, from several terminals — with room to
    /// spare, while staying small enough that the guest's process and handle use is obviously
    /// bounded. It is a refusal threshold, not a queue: a host past it is told so immediately rather
    /// than left waiting.
    /// </remarks>
    public const int DefaultMaxConnections = 8;

    /// <summary>Defaults used by the agent.</summary>
    public static GuestConnectionLimits Default { get; } = new(
        DefaultMaxConnections,
        HandshakeTimeout: TimeSpan.FromSeconds(30),
        RefusedConnectionLifetime: TimeSpan.FromSeconds(10));

    /// <summary>
    /// Connections tracked at once, including ones being refused or still authenticating.
    /// </summary>
    /// <remarks>
    /// The ceiling that keeps the agent's work bounded rather than merely limited. Above it a
    /// connection is dropped before any handshake, so a peer that opens sockets in a loop cannot
    /// make the agent allocate tasks, buffers, and key material without limit. It is a multiple of
    /// <see cref="MaxConnections"/> so ordinary refusals — which are short-lived — never reach it.
    /// </remarks>
    public int MaxTrackedConnections => MaxConnections * 4;
}

/// <summary>
/// The agent's accept loop: authenticates, admits, and isolates concurrent host channels
/// (spec §"Transport and command channel", §"Coordination between commands").
/// </summary>
/// <remarks>
/// Serving one channel at a time made a running application block every other workflow, because the
/// host holding the channel holds it for as long as its application lives. Channels are therefore
/// dispatched concurrently, and every property that made one channel safe is preserved per channel
/// rather than shared: each derives its own session keys from its own handshake randoms, tracks its
/// own directional sequence numbers, and owns its own operation identities, standard input, and
/// cancellation. Nothing about one channel is reachable from another.
/// <para>
/// What <em>is</em> shared is deliberately immutable or independently safe: the managed-root file
/// service holds only a path, the session probe re-reads the live session every time, the process
/// factory creates a self-contained host per operation, and the agent identity is a record. So a
/// second channel adds no shared mutable state to protect.
/// </para>
/// <para>
/// Ownership is structured throughout. Every connection is tracked from the moment it is accepted,
/// each connection task is total — it reports rather than throws — and shutdown waits for all of
/// them, so no operation, socket, or child process outlives the agent unnoticed.
/// </para>
/// </remarks>
internal sealed class GuestConnectionAcceptor(
    IGuestConnectionSource source,
    GuestConnectionServerFactory serverFactory,
    GuestConnectionLimits? limits = null,
    Action? connectionClosed = null)
{
    private readonly GuestConnectionLimits _limits = limits ?? GuestConnectionLimits.Default;
    private readonly ConcurrentDictionary<long, Task> _connections = new();

    private long _nextConnectionId;
    private int _admitted;

    /// <summary>Channels currently admitted, for diagnostics and tests.</summary>
    public int AdmittedConnections => Volatile.Read(ref _admitted);

    /// <summary>
    /// Accepts and serves connections until <paramref name="cancellationToken"/> fires, then waits
    /// for every one of them to finish.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var pending = await source.AcceptAsync(cancellationToken).ConfigureAwait(false);

                // Pruned before admitting, so a connection that has already finished frees its slot
                // for the peer arriving now rather than at some later sweep.
                PruneCompleted();
                Track(pending, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
        {
            // The listener is gone, so no further channel can arrive. Channels already being served
            // are still drained below rather than abandoned mid-operation.
            System.Diagnostics.Trace.TraceWarning(
                "The guest agent stopped accepting connections: {0}", ex.Message);
        }
        finally
        {
            await DrainAsync().ConfigureAwait(false);
        }
    }

    private void Track(IGuestPendingConnection pending, CancellationToken cancellationToken)
    {
        if (_connections.Count >= _limits.MaxTrackedConnections)
        {
            // Dropped before the handshake: there is no authenticated peer to send a refusal to,
            // and pretending otherwise would mean doing the very work this ceiling exists to cap.
            pending.Dispose();
            return;
        }

        var id = Interlocked.Increment(ref _nextConnectionId);

        // Started with CancellationToken.None so the task is always created and therefore always
        // tracked; the token it serves under is passed in and observed inside.
        _connections[id] = Task.Run(
            () => ServeAsync(pending, cancellationToken),
            CancellationToken.None);
    }

    /// <summary>
    /// Serves one connection to completion. Total by construction: it never faults, so one channel's
    /// failure can never escape into the accept loop or another channel.
    /// </summary>
    private async Task ServeAsync(IGuestPendingConnection pending, CancellationToken cancellationToken)
    {
        var admitted = false;

        try
        {
            var transport = await AuthenticateAsync(pending, cancellationToken).ConfigureAwait(false);
            if (transport is null)
            {
                return;
            }

            // Admission is spent only on a peer that has proved it holds the pre-shared key.
            // Reserving the slot at accept time instead would let anything able to open a socket and
            // then stall consume the whole bound without knowing the key, refusing real hosts.
            admitted = TryAdmit();

            GuestCommandServer server;
            try
            {
                server = serverFactory(transport, admitted ? null : Busy());
            }
            catch
            {
                // The server owns the transport once it exists; until then this method does.
                await transport.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            await using (server.ConfigureAwait(false))
            {
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (!admitted)
                {
                    // A refused peer gets long enough to read its refusal and go away. Without this a
                    // host that ignored it would hold a tracked slot for as long as it liked.
                    lifetime.CancelAfter(_limits.RefusedConnectionLifetime);
                }

                await server.RunAsync(lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Reported, never rethrown. Isolation is the whole point of this method: a channel that
            // fails — transport, protocol, or an unexpected bug — must take nothing else with it.
            System.Diagnostics.Trace.TraceWarning("A guest connection ended unexpectedly: {0}", ex.Message);
        }
        finally
        {
            if (admitted)
            {
                Interlocked.Decrement(ref _admitted);
            }

            // Idempotent, and only ever reached once the server above has finished with the
            // transport, so this releases the connection rather than closing one still in use.
            pending.Dispose();
            connectionClosed?.Invoke();
        }
    }

    /// <summary>Authenticates a peer, or returns null when it never proved who it was.</summary>
    private async Task<IGuestTransport?> AuthenticateAsync(
        IGuestPendingConnection pending,
        CancellationToken cancellationToken)
    {
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshake.CancelAfter(_limits.HandshakeTimeout);

        try
        {
            return await pending.AuthenticateAsync(handshake.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is ExecutionTargetException or OperationCanceledException or IOException or SocketException
                or ObjectDisposedException)
        {
            // A peer that cannot authenticate never reaches a server, so it can neither run an
            // operation nor observe one. Its connection is simply closed.
            pending.Dispose();
            return null;
        }
    }

    private bool TryAdmit()
    {
        while (true)
        {
            var current = Volatile.Read(ref _admitted);
            if (current >= _limits.MaxConnections)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _admitted, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    private ExecutionTargetErrorInfo Busy() => new()
    {
        Code = ExecutionTargetErrorCodes.AgentBusy,
        Message =
            $"The Windows Sandbox agent is already serving {_limits.MaxConnections} winapp commands.",
        UserAction = "Wait for one of the running commands to finish, then retry.",
    };

    /// <summary>
    /// Removes connections that have finished.
    /// </summary>
    /// <remarks>
    /// Entries are removed here rather than by the connection task itself, which would have to
    /// remove itself before it had actually completed and could therefore be missed by shutdown.
    /// Only completed tasks are removed, so what remains is exactly what still has to be waited for.
    /// </remarks>
    private void PruneCompleted()
    {
        foreach (var (id, task) in _connections)
        {
            if (task.IsCompleted)
            {
                _connections.TryRemove(id, out _);
            }
        }
    }

    /// <summary>Waits for every tracked connection, so shutdown leaves nothing running.</summary>
    private async Task DrainAsync()
    {
        while (!_connections.IsEmpty)
        {
            foreach (var (id, task) in _connections)
            {
                await task.ConfigureAwait(false);
                _connections.TryRemove(id, out _);
            }
        }
    }
}

/// <summary>Inbound TCP connections to the agent's listener.</summary>
internal sealed class GuestTcpConnectionSource(TcpListener listener, GuestBootstrapMaterial material)
    : IGuestConnectionSource
{
    /// <inheritdoc/>
    public async Task<IGuestPendingConnection> AcceptAsync(CancellationToken cancellationToken)
    {
        var client = await GuestTcpTransport.AcceptClientAsync(listener, cancellationToken).ConfigureAwait(false);
        return new PendingTcpConnection(client, material);
    }

    private sealed class PendingTcpConnection(TcpClient client, GuestBootstrapMaterial material)
        : IGuestPendingConnection
    {
        /// <inheritdoc/>
        public Task<IGuestTransport> AuthenticateAsync(CancellationToken cancellationToken) =>
            GuestTcpTransport.EstablishAsync(client, material, cancellationToken);

        /// <inheritdoc/>
        public void Dispose() => client.Dispose();
    }
}

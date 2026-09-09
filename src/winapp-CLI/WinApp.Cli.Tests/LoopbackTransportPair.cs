// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

/// <summary>
/// Two <see cref="IGuestTransport"/> ends wired to each other, so the host command channel and the
/// guest command server can be run against one another in one process.
/// </summary>
/// <remarks>
/// This is stronger than testing either half against a scripted peer: it proves the two
/// implementations actually agree — on message types, on operation identity, on stream framing, and
/// on epoch fencing — rather than each agreeing with a fixture that encodes the same assumption
/// twice.
/// </remarks>
internal sealed class LoopbackTransportPair
{
    private readonly Channel<ReadOnlyMemory<byte>> _hostToGuest =
        Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

    private readonly Channel<ReadOnlyMemory<byte>> _guestToHost =
        Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

    /// <summary>Creates the pair.</summary>
    public LoopbackTransportPair()
    {
        Host = new End(_hostToGuest.Writer, _guestToHost.Reader);
        Guest = new End(_guestToHost.Writer, _hostToGuest.Reader);
    }

    /// <summary>The end the host command channel owns.</summary>
    public IGuestTransport Host { get; }

    /// <summary>The end the guest command server owns.</summary>
    public IGuestTransport Guest { get; }

    /// <summary>One direction's send and the other's receive.</summary>
    private sealed class End(
        ChannelWriter<ReadOnlyMemory<byte>> outbound,
        ChannelReader<ReadOnlyMemory<byte>> inbound) : IGuestTransport
    {
        /// <inheritdoc/>
        public bool IsConnected { get; private set; } = true;

        /// <inheritdoc/>
        public ValueTask SendFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            if (!outbound.TryWrite(payload.ToArray()))
            {
                throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.TransportFailed,
                    "The loopback transport is closed.");
            }

            return ValueTask.CompletedTask;
        }

        /// <inheritdoc/>
        public async ValueTask<ReadOnlyMemory<byte>?> ReceiveFrameAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await inbound.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                IsConnected = false;
                return null;
            }
        }

        /// <inheritdoc/>
        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            outbound.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A scriptable stand-in for a real guest child process.</summary>
/// <remarks>
/// Lets the server's dispatch, streaming, cancellation, and cleanup behaviour be verified without
/// launching processes — including the outcomes a real process cannot be made to produce on demand,
/// such as ignoring a graceful stop until its timeout elapses.
/// </remarks>
internal sealed class FakeGuestProcessHost : IGuestProcessHost
{
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<GuestStreamId, ReadOnlyMemory<byte>> _onOutput;
    private readonly Action<GuestExecRequest, int>? _onExit;

    /// <summary>Creates a host that reports <paramref name="processId"/>.</summary>
    public FakeGuestProcessHost(
        GuestExecRequest request,
        Action<GuestStreamId, ReadOnlyMemory<byte>> onOutput,
        int processId,
        Action<GuestExecRequest, int>? onExit = null)
    {
        Request = request;
        _onOutput = onOutput;
        _onExit = onExit;
        ProcessId = processId;
    }

    /// <summary>The request this host was started for.</summary>
    public GuestExecRequest Request { get; }

    /// <inheritdoc/>
    public int ProcessId { get; }

    /// <inheritdoc/>
    public long StartTicksUtc { get; } = DateTime.UtcNow.Ticks;

    /// <summary>Standard input the server forwarded, in order.</summary>
    public List<byte[]> StandardInput { get; } = [];

    /// <summary>True once end of standard input was signalled.</summary>
    public bool StandardInputClosed { get; private set; }

    /// <summary>True once a graceful stop was requested.</summary>
    public bool StopRequested { get; private set; }

    /// <summary>True once this host was disposed by the server.</summary>
    public bool Disposed { get; private set; }

    /// <summary>Emits a chunk on one of the child's output streams.</summary>
    public void Emit(GuestStreamId stream, string text) =>
        _onOutput(stream, Encoding.UTF8.GetBytes(text));

    /// <summary>Completes the child with <paramref name="exitCode"/>.</summary>
    public void Exit(int exitCode)
    {
        if (_exit.TrySetResult(exitCode))
        {
            _onExit?.Invoke(Request, exitCode);
        }
    }

    /// <inheritdoc/>
    public Task WriteStandardInputAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        StandardInput.Add(data.ToArray());
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void CloseStandardInput() => StandardInputClosed = true;

    /// <inheritdoc/>
    public Task<int> WaitForExitAsync(CancellationToken cancellationToken) =>
        _exit.Task.WaitAsync(cancellationToken);

    /// <inheritdoc/>
    public Task<int> StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken)
    {
        StopRequested = true;

        // A real host force-terminates after the timeout; the exit code below stands in for that.
        _exit.TrySetResult(-1);
        return Task.FromResult(-1);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _exit.TrySetResult(-1);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Hands out <see cref="FakeGuestProcessHost"/> instances and records them.</summary>
internal sealed class FakeGuestProcessHostFactory : IGuestProcessHostFactory
{
    private int _nextProcessId = 4000;

    /// <summary>Every host started, in order.</summary>
    public ConcurrentQueue<FakeGuestProcessHost> Started { get; } = new();

    /// <summary>Set to fail the next start with this error instead of returning a host.</summary>
    public ExecutionTargetErrorInfo? FailWith { get; set; }

    /// <summary>Completes each time a host is started, so tests need no polling.</summary>
    public SemaphoreSlim StartSignal { get; } = new(0);

    /// <summary>
    /// Optional side effect to run when a host starts, for suites whose guest winapp is faked but
    /// whose later requests depend on what a real one would have left behind on disk.
    /// </summary>
    /// <remarks>
    /// Runs before the host is handed back, so a directory it creates is already there for the next
    /// request the agent validates. Null by default: suites that do not need it are unaffected.
    /// </remarks>
    public Action<GuestExecRequest>? OnStart { get; set; }

    /// <summary>Optional side effect to run when a scripted guest process exits.</summary>
    public Action<GuestExecRequest, int>? OnExit { get; set; }

    /// <inheritdoc/>
    public IGuestProcessHost Start(
        GuestExecRequest request,
        Action<GuestStreamId, ReadOnlyMemory<byte>> onOutput)
    {
        if (FailWith is { } error)
        {
            throw new ExecutionTargetException(error);
        }

        OnStart?.Invoke(request);

        var host = new FakeGuestProcessHost(
            request,
            onOutput,
            Interlocked.Increment(ref _nextProcessId),
            OnExit);
        Started.Enqueue(host);
        StartSignal.Release();
        return host;
    }

    /// <summary>Waits for the next started host.</summary>
    public async Task<FakeGuestProcessHost> WaitForNextAsync(CancellationToken cancellationToken)
    {
        await StartSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
        Started.TryDequeue(out var host);
        return host!;
    }
}

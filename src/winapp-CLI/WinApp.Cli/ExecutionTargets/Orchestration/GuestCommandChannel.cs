// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>Callbacks for one running guest operation.</summary>
/// <param name="OnOperationId">
/// Invoked with the operation's identity as soon as it is registered, before the request is sent.
/// This is what lets a caller stream standard input into an operation the channel named itself.
/// </param>
/// <param name="OnStarted">Invoked once the guest reports the process started.</param>
/// <param name="OnStandardOutput">Invoked for each stdout chunk, in order.</param>
/// <param name="OnStandardError">Invoked for each stderr chunk, in order.</param>
internal sealed record GuestExecCallbacks(
    Action<Guid>? OnOperationId = null,
    Action<GuestProcessStart>? OnStarted = null,
    Action<ReadOnlyMemory<byte>>? OnStandardOutput = null,
    Action<ReadOnlyMemory<byte>>? OnStandardError = null);

/// <summary>A guest process that has just started.</summary>
/// <param name="ProcessId">Guest process ID, meaningful only within the current target epoch.</param>
/// <param name="StartTicksUtc">
/// UTC ticks when it started. Carried alongside the ID because process IDs are reused: without it, a
/// later command cannot tell this process from an unrelated one that inherited its number.
/// </param>
internal sealed record GuestProcessStart(int ProcessId, long StartTicksUtc);

/// <summary>Outcome of a completed guest operation.</summary>
/// <param name="ExitCode">The guest process's exit code.</param>
/// <param name="ProcessId">The guest process ID, valid only within the current target epoch.</param>
internal sealed record GuestExecResult(int ExitCode, int ProcessId);

/// <summary>
/// The single target-neutral command channel over an <see cref="IGuestTransport"/>
/// (spec §"Transport and command channel").
/// </summary>
/// <remarks>
/// Everything meaningful lives here rather than in a backend: capability negotiation, structured
/// execution, stream forwarding, cancellation, and epoch fencing. Because it depends only on
/// <see cref="IGuestTransport"/>, the whole contract is exercised against a fake transport with no
/// Windows Sandbox involved — which is what proves orchestration does not depend on WSB APIs.
/// <para>
/// One receive pump owns the transport's read side and dispatches to per-operation state. Frames
/// for an unknown operation are dropped rather than treated as fatal, because a late frame from a
/// cancelled operation is normal and must not tear down the channel.
/// </para>
/// </remarks>
internal sealed class GuestCommandChannel : IAsyncDisposable, ITargetOperationExecutor
{
    private readonly IGuestTransport _transport;
    private readonly string _targetEpoch;
    private readonly ConcurrentDictionary<Guid, OperationState> _operations = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    private Task? _receivePump;
    private ExecutionTargetErrorInfo? _fatalError;

    /// <summary>Creates a channel over <paramref name="transport"/> fenced to <paramref name="targetEpoch"/>.</summary>
    public GuestCommandChannel(IGuestTransport transport, ExecutionTargetEpoch targetEpoch)
    {
        _transport = transport;
        _targetEpoch = targetEpoch.Value;
    }

    /// <summary>Starts the receive pump. Must be called before any operation.</summary>
    public void Start() => _receivePump ??= Task.Run(() => ReceiveLoopAsync(_shutdown.Token));

    /// <summary>Asks the guest to describe what it supports.</summary>
    /// <remarks>
    /// Commands validate required capabilities before deployment or execution rather than inferring
    /// them from the provider name, so a future backend reuses this unchanged.
    /// </remarks>
    public async Task<ExecutionTargetCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        var state = Register(operationId);

        try
        {
            await SendAsync(
                new GuestMessage
                {
                    Type = GuestMessageTypes.CapabilitiesRequest,
                    OperationId = operationId.ToString(),
                    TargetEpoch = _targetEpoch,
                },
                cancellationToken).ConfigureAwait(false);

            var message = await state.Capabilities.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return message;
        }
        finally
        {
            _operations.TryRemove(operationId, out _);
        }
    }

    /// <summary>
    /// Runs one guest process, streaming its output, and returns its exit code.
    /// </summary>
    /// <remarks>
    /// Cancellation asks the guest to terminate the process gracefully first; the guest enforces its
    /// own timeout before killing the process tree, so a well-behaved child can still flush and exit
    /// cleanly. The exit code returned is the guest application's, kept distinguishable from the
    /// infrastructure failures reported as <see cref="ExecutionTargetException"/>.
    /// </remarks>
    public async Task<GuestExecResult> ExecuteAsync(
        GuestExecRequest request,
        GuestExecCallbacks? callbacks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var operationId = Guid.NewGuid();
        var state = Register(operationId);
        state.Callbacks = callbacks;

        try
        {
            await SendAsync(
                new GuestMessage
                {
                    Type = GuestMessageTypes.ExecRequest,
                    OperationId = operationId.ToString(),
                    TargetEpoch = _targetEpoch,
                    Exec = request,
                },
                cancellationToken).ConfigureAwait(false);

            // Announced only after the request is on the wire. Publishing it earlier would let a
            // caller send standard input that overtakes the request it belongs to, and the guest
            // would drop those bytes as belonging to an operation it has not heard of.
            callbacks?.OnOperationId?.Invoke(operationId);

            var exitCode = await state.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new GuestExecResult(exitCode, state.ProcessId);
        }
        catch (OperationCanceledException)
        {
            // Ask the guest to stop before surfacing cancellation. This is done here rather than
            // from a CancellationToken registration because registrations fire last-in-first-out:
            // WaitAsync's own registration would run first, and unwinding this method would dispose
            // ours before it ever ran, silently leaving the guest process running.
            await RequestCancelAsync(operationId).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _operations.TryRemove(operationId, out _);
        }
    }

    /// <summary>Sends a chunk of standard input to a running operation.</summary>
    public async Task SendStandardInputAsync(
        Guid operationId,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        // Split so one write can never exceed the frame limit and stall the channel.
        var remaining = data;
        while (!remaining.IsEmpty)
        {
            var take = Math.Min(remaining.Length, GuestPayloadCodec.MaxStreamChunkSize);
            var payload = GuestPayloadCodec.EncodeStream(operationId, GuestStreamId.StandardInput, remaining.Span[..take]);
            await SendRawAsync(payload, cancellationToken).ConfigureAwait(false);
            remaining = remaining[take..];
        }
    }

    /// <summary>Signals that no more standard input will arrive for an operation.</summary>
    public Task CloseStandardInputAsync(Guid operationId, CancellationToken cancellationToken) =>
        SendAsync(
            new GuestMessage
            {
                Type = GuestMessageTypes.StdinClosed,
                OperationId = operationId.ToString(),
                TargetEpoch = _targetEpoch,
            },
            cancellationToken);

    /// <summary>Lists what a managed guest location actually contains.</summary>
    /// <remarks>
    /// The guest's own view is authoritative. Reconciling against a host-side memory of what was
    /// last transferred would leave a Sandbox that was restarted, or a deployment someone edited,
    /// silently out of sync.
    /// </remarks>
    public async Task<IReadOnlyList<GuestFileInfo>> ListFilesAsync(
        GuestPathScope scope,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        var state = Register(operationId);

        try
        {
            await SendAsync(
                new GuestMessage
                {
                    Type = GuestMessageTypes.ListFilesRequest,
                    OperationId = operationId.ToString(),
                    TargetEpoch = _targetEpoch,
                    Scope = scope,
                },
                cancellationToken).ConfigureAwait(false);

            return await state.Files.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operations.TryRemove(operationId, out _);
        }
    }

    /// <summary>Streams one file into a managed guest location and waits for it to be verified.</summary>
    /// <remarks>
    /// The guest verifies size and hash before publishing, so this returning successfully means the
    /// file is present and correct — not merely that bytes were sent.
    /// </remarks>
    public async Task PutFileAsync(
        GuestPathScope scope,
        GuestFileInfo file,
        Stream content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(content);

        var operationId = Guid.NewGuid();
        var state = Register(operationId);

        try
        {
            await SendAsync(
                new GuestMessage
                {
                    Type = GuestMessageTypes.PutFileRequest,
                    OperationId = operationId.ToString(),
                    TargetEpoch = _targetEpoch,
                    Scope = scope,
                    File = file,
                },
                cancellationToken).ConfigureAwait(false);

            var buffer = new byte[GuestPayloadCodec.MaxStreamChunkSize];

            while (true)
            {
                var read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var payload = GuestPayloadCodec.EncodeStream(
                    operationId,
                    GuestStreamId.StandardInput,
                    buffer.AsSpan(0, read));

                await SendRawAsync(payload, cancellationToken).ConfigureAwait(false);
            }

            await SendAsync(
                new GuestMessage
                {
                    Type = GuestMessageTypes.StdinClosed,
                    OperationId = operationId.ToString(),
                    TargetEpoch = _targetEpoch,
                },
                cancellationToken).ConfigureAwait(false);

            await state.FileCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Tell the guest before unwinding. Without this the guest keeps the destination's
            // temporary file open indefinitely, and an immediate retry of the same transfer fails
            // on a file the caller believes was abandoned. Requested here rather than from a
            // CancellationToken registration for the same reason execution does: registrations fire
            // last-in-first-out, so WaitAsync's own would run first and unwinding would dispose
            // ours before it executed.
            await RequestCancelAsync(operationId).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _operations.TryRemove(operationId, out _);
        }
    }

    /// <summary>Streams one managed guest file out to <paramref name="destination"/>.</summary>
    /// <remarks>
    /// The caller verifies size and hash before publishing anything to a requested output path, so
    /// an interrupted copy-back never surfaces as a partially written result.
    /// </remarks>
    public async Task GetFileAsync(
        GuestPathScope scope,
        string relativePath,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);

        var operationId = Guid.NewGuid();
        var state = Register(operationId);
        state.Callbacks = new GuestExecCallbacks(
            OnStandardOutput: data => destination.Write(data.Span));

        try
        {
            await SendAsync(
                new GuestMessage
                {
                    Type = GuestMessageTypes.GetFileRequest,
                    OperationId = operationId.ToString(),
                    TargetEpoch = _targetEpoch,
                    Scope = scope,
                    Paths = [relativePath],
                },
                cancellationToken).ConfigureAwait(false);

            await state.FileCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operations.TryRemove(operationId, out _);
        }
    }

    /// <summary>Removes paths from a managed guest location.</summary>
    public async Task DeleteFilesAsync(
        GuestPathScope scope,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);

        if (relativePaths.Count == 0)
        {
            return;
        }

        var operationId = Guid.NewGuid();
        var state = Register(operationId);

        try
        {
            await SendAsync(
                new GuestMessage
                {
                    Type = GuestMessageTypes.DeleteFilesRequest,
                    OperationId = operationId.ToString(),
                    TargetEpoch = _targetEpoch,
                    Scope = scope,
                    Paths = [.. relativePaths],
                },
                cancellationToken).ConfigureAwait(false);

            await state.FileCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operations.TryRemove(operationId, out _);
        }
    }

    /// <summary>Discards an entire managed guest scope, for an explicit clean reinstall.</summary>
    /// <remarks>
    /// Scoped to one deployment's own folder. Package deployment otherwise preserves per-user
    /// application state, so wiping anything wider would silently destroy data the user did not ask
    /// to lose.
    /// </remarks>
    public async Task DeleteScopeAsync(GuestPathScope scope, CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        var state = Register(operationId);

        try
        {
            await SendAsync(
                new GuestMessage
                {
                    Type = GuestMessageTypes.RemoveScopeRequest,
                    OperationId = operationId.ToString(),
                    TargetEpoch = _targetEpoch,
                    Scope = scope,
                },
                cancellationToken).ConfigureAwait(false);

            await state.FileCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operations.TryRemove(operationId, out _);
        }
    }

    /// <summary>
    /// Asks the guest to stop every running process of a package before a redeploy mutates the
    /// layout it was registered from.
    /// </summary>
    /// <remarks>
    /// Scoped to exactly the package this deployment owns, in two independent ways: the guest
    /// resolves the family name to whatever full name is actually registered right now, and then
    /// verifies that package's own install location against <paramref name="expectedRegisteredLocation"/>
    /// before terminating anything. The second check is what keeps a different deployment's
    /// legitimately running application untouched when it happens to share the same package
    /// identity — a family name match alone does not prove it is <em>this</em> deployment's
    /// registration. The guest reports a structured failure — never a raw OS message — when it
    /// cannot prove the stop happened or the location does not match, and that failure is surfaced
    /// here before anything is deleted or overwritten.
    /// </remarks>
    public async Task StopPackageProcessesAsync(
        string packageFamilyName,
        string expectedRegisteredLocation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFamilyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRegisteredLocation);

        var operationId = Guid.NewGuid();
        var state = Register(operationId);

        try
        {
            await SendAsync(
                new GuestMessage
                {
                    Type = GuestMessageTypes.StopPackageRequest,
                    OperationId = operationId.ToString(),
                    TargetEpoch = _targetEpoch,
                    PackageFamilyName = packageFamilyName,
                    ExpectedRegisteredLocation = expectedRegisteredLocation,
                },
                cancellationToken).ConfigureAwait(false);

            await state.FileCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operations.TryRemove(operationId, out _);
        }
    }

    /// <inheritdoc/>
    public async Task<GuestPackageRegistration?> GetRegisteredPackageAsync(
        string packageName,
        string publisher,
        string packageFamilyName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(publisher);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFamilyName);

        var operationId = Guid.NewGuid();
        var state = Register(operationId);

        try
        {
            await SendAsync(
                new GuestMessage
                {
                    Type = GuestMessageTypes.QueryPackageRequest,
                    OperationId = operationId.ToString(),
                    TargetEpoch = _targetEpoch,
                    PackageName = packageName,
                    PackagePublisher = publisher,
                    PackageFamilyName = packageFamilyName,
                },
                cancellationToken).ConfigureAwait(false);

            return await state.RegisteredPackage.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operations.TryRemove(operationId, out _);
        }
    }

    /// <inheritdoc/>
    public async Task UnregisterPackageAsync(
        string packageFamilyName,
        string packageFullName,
        string expectedRegisteredLocation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFamilyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFullName);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRegisteredLocation);

        var operationId = Guid.NewGuid();
        var state = Register(operationId);

        try
        {
            await SendAsync(
                new GuestMessage
                {
                    Type = GuestMessageTypes.UnregisterPackageRequest,
                    OperationId = operationId.ToString(),
                    TargetEpoch = _targetEpoch,
                    PackageFamilyName = packageFamilyName,
                    PackageFullName = packageFullName,
                    ExpectedRegisteredLocation = expectedRegisteredLocation,
                },
                cancellationToken).ConfigureAwait(false);

            await state.FileCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operations.TryRemove(operationId, out _);
        }
    }

    /// <summary>    /// Asks the guest to stop one specific tracked process before a redeploy mutates files it may
    /// still have open.
    /// </summary>
    /// <remarks>
    /// The PID alone never identifies a process across a rerun: it can be reused the instant the
    /// original exits. <paramref name="startTicksUtc"/> is what lets the guest prove this is still
    /// the exact process winapp launched rather than something unrelated that inherited its number
    /// — and refuse to touch anything when it cannot prove that.
    /// </remarks>
    public async Task StopTrackedProcessAsync(int processId, long startTicksUtc, CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        var state = Register(operationId);

        try
        {
            await SendAsync(
                new GuestMessage
                {
                    Type = GuestMessageTypes.StopProcessRequest,
                    OperationId = operationId.ToString(),
                    TargetEpoch = _targetEpoch,
                    ProcessId = processId,
                    ProcessStartTicksUtc = startTicksUtc,
                },
                cancellationToken).ConfigureAwait(false);

            await state.FileCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operations.TryRemove(operationId, out _);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> IsTrackedProcessRunningAsync(
        int processId,
        long startTicksUtc,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        var state = Register(operationId);

        try
        {
            await SendAsync(
                new GuestMessage
                {
                    Type = GuestMessageTypes.QueryProcessRequest,
                    OperationId = operationId.ToString(),
                    TargetEpoch = _targetEpoch,
                    ProcessId = processId,
                    ProcessStartTicksUtc = startTicksUtc,
                },
                cancellationToken).ConfigureAwait(false);

            return await state.ProcessRunning.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operations.TryRemove(operationId, out _);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_shutdown.IsCancellationRequested)
        {
            // Idempotent: disposing twice must not throw on the already-disposed shutdown source.
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_receivePump is { } pump)
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: shutdown cancels the pump.
            }
        }

        FailPendingOperations(_fatalError ?? new ExecutionTargetErrorInfo
        {
            Code = ExecutionTargetErrorCodes.TransportFailed,
            Message = "The connection to the guest was closed.",
        });

        _shutdown.Dispose();
        _sendLock.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private OperationState Register(Guid operationId)
    {
        var state = new OperationState();
        _operations[operationId] = state;
        return state;
    }

    /// <summary>
    /// Best-effort request for the guest to terminate an operation gracefully.
    /// </summary>
    /// <remarks>
    /// Failures are swallowed: cancellation is usually why the channel is going away, and a dead
    /// channel must not turn cancellation into a different, more confusing error.
    /// </remarks>
    private async Task RequestCancelAsync(Guid operationId)
    {
        try
        {
            await SendAsync(
                new GuestMessage
                {
                    Type = GuestMessageTypes.CancelRequest,
                    OperationId = operationId.ToString(),
                    TargetEpoch = _targetEpoch,
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (ExecutionTargetException)
        {
            // The channel is already gone; the guest process dies with it.
        }
        catch (ObjectDisposedException)
        {
            // The channel was disposed concurrently with cancellation.
        }
    }

    private Task SendAsync(GuestMessage message, CancellationToken cancellationToken) =>
        SendRawAsync(GuestPayloadCodec.EncodeJson(message), cancellationToken);

    private async Task SendRawAsync(byte[] payload, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _transport.SendFrameAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await _transport.ReceiveFrameAsync(cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    // The guest closed cleanly. Any operation still waiting will never complete.
                    FailPendingOperations(new ExecutionTargetErrorInfo
                    {
                        Code = ExecutionTargetErrorCodes.Terminated,
                        Message = "The guest closed the connection before the operation completed.",
                        UserAction = "Retry the command.",
                    });
                    return;
                }

                Dispatch(frame.Value);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (ExecutionTargetException ex)
        {
            _fatalError = ex.Error;
            FailPendingOperations(ex.Error);
        }
    }

    private void Dispatch(ReadOnlyMemory<byte> frame)
    {
        if (!GuestPayloadCodec.TryGetKind(frame.Span, out var kind))
        {
            return;
        }

        if (kind == GuestPayloadKind.Stream)
        {
            DispatchStream(frame);
            return;
        }

        var message = GuestPayloadCodec.TryDecodeJson(frame.Span);
        if (message?.OperationId is null || !Guid.TryParse(message.OperationId, out var operationId))
        {
            return;
        }

        // A frame for an operation we no longer track is normal after cancellation, so it is
        // dropped rather than treated as a protocol violation.
        if (!_operations.TryGetValue(operationId, out var state))
        {
            return;
        }

        switch (message.Type)
        {
            case GuestMessageTypes.CapabilitiesResponse when message.Capabilities is { } capabilities:
                state.Capabilities.TrySetResult(capabilities);
                break;

            case GuestMessageTypes.ExecStarted when message.ProcessId is { } processId:
                state.ProcessId = processId;
                state.Callbacks?.OnStarted?.Invoke(
                    new GuestProcessStart(processId, message.ProcessStartTicksUtc ?? 0));
                break;

            case GuestMessageTypes.ExecCompleted when message.ExitCode is { } exitCode:
                state.Completion.TrySetResult(exitCode);
                break;

            case GuestMessageTypes.OperationFailed when message.Error is { } error:
                Fail(state, error);
                break;

            case GuestMessageTypes.ListFilesResponse when message.Files is { } files:
                state.Files.TrySetResult(files);
                break;

            case GuestMessageTypes.QueryPackageResponse
                when message.PackageRegistered is true && message.RegisteredPackage is { } package:
                state.RegisteredPackage.TrySetResult(package);
                break;

            case GuestMessageTypes.QueryPackageResponse when message.PackageRegistered is false:
                state.RegisteredPackage.TrySetResult(null);
                break;

            case GuestMessageTypes.QueryPackageResponse:
                Fail(
                    state,
                    new ExecutionTargetErrorInfo
                    {
                        Code = ExecutionTargetErrorCodes.TransportFailed,
                        Message = "The guest returned an incomplete package registration response.",
                        UserAction = "Retry the command.",
                    });
                break;

            case GuestMessageTypes.QueryProcessResponse when message.ProcessRunning is { } running:
                state.ProcessRunning.TrySetResult(running);
                break;

            case GuestMessageTypes.FileCompleted:
                state.FileCompletion.TrySetResult(true);
                break;

            default:
                break;
        }
    }

    private void DispatchStream(ReadOnlyMemory<byte> frame)
    {
        if (!GuestPayloadCodec.TryDecodeStream(frame, out var operationId, out var stream, out var data))
        {
            return;
        }

        if (!_operations.TryGetValue(operationId, out var state))
        {
            return;
        }

        switch (stream)
        {
            case GuestStreamId.StandardOutput:
                state.Callbacks?.OnStandardOutput?.Invoke(data);
                break;

            case GuestStreamId.StandardError:
                state.Callbacks?.OnStandardError?.Invoke(data);
                break;

            default:
                // Standard input only flows host to guest; a guest sending it is ignored.
                break;
        }
    }

    private void FailPendingOperations(ExecutionTargetErrorInfo error)
    {
        foreach (var state in _operations.Values)
        {
            Fail(state, error);
        }
    }

    private static void Fail(OperationState state, ExecutionTargetErrorInfo error)
    {
        var exception = new ExecutionTargetException(error);
        state.Completion.TrySetException(exception);
        state.Capabilities.TrySetException(exception);
        state.Files.TrySetException(exception);
        state.FileCompletion.TrySetException(exception);
        state.RegisteredPackage.TrySetException(exception);
        state.ProcessRunning.TrySetException(exception);
    }

    /// <summary>Per-operation state owned by the receive pump.</summary>
    private sealed class OperationState
    {
        public TaskCompletionSource<int> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ExecutionTargetCapabilities> Capabilities { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<IReadOnlyList<GuestFileInfo>> Files { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> FileCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<GuestPackageRegistration?> RegisteredPackage { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ProcessRunning { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GuestExecCallbacks? Callbacks { get; set; }

        public int ProcessId { get; set; }
    }
}

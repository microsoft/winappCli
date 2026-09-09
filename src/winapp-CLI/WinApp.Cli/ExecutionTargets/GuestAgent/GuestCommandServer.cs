// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services;

namespace WinApp.Cli.ExecutionTargets.GuestAgent;

/// <summary>
/// The guest half of the command channel: receives host operations and runs them
/// (spec §"Guest winapp agent mode").
/// </summary>
/// <remarks>
/// This is the mirror of <c>GuestCommandChannel</c> and, like it, depends only on
/// <see cref="IGuestTransport"/>. Both halves can therefore be run against each other over an
/// in-memory transport, which is what makes the whole protocol — dispatch, streaming, cancellation,
/// fencing, failure envelopes — testable without a Sandbox.
/// <para>
/// The agent implements no application semantics. Every operation becomes an ordinary guest winapp
/// child process, which is precisely what keeps guest behaviour identical to local behaviour instead
/// of a second implementation that drifts.
/// </para>
/// </remarks>
internal sealed class GuestCommandServer : IAsyncDisposable
{
    private readonly IGuestTransport _transport;
    private readonly string _targetEpoch;
    private readonly IGuestProcessHostFactory _processes;
    private readonly IGuestSessionProbe _sessionProbe;
    private readonly GuestAgentIdentity _identity;
    private readonly GuestFileService? _files;
    private readonly string? _guestWinapp;
    private readonly IAppLauncherService? _appLauncher;
    private readonly IPackageRegistrationService? _packageRegistration;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, RunningOperation> _operations = new();
    private readonly ConcurrentDictionary<Guid, GuestFileWrite> _writes = new();
    private bool _disposed;

    /// <summary>Creates a server bound to one connection and one target generation.</summary>
    public GuestCommandServer(
        IGuestTransport transport,
        ExecutionTargetEpoch targetEpoch,
        IGuestProcessHostFactory processes,
        IGuestSessionProbe sessionProbe,
        GuestAgentIdentity identity,
        GuestFileService? files = null,
        string? guestWinapp = null,
        IAppLauncherService? appLauncher = null,
        IPackageRegistrationService? packageRegistration = null)
    {
        _transport = transport;
        _targetEpoch = targetEpoch.Value;
        _processes = processes;
        _sessionProbe = sessionProbe;
        _identity = identity;
        _files = files;
        _guestWinapp = guestWinapp;
        _appLauncher = appLauncher;
        _packageRegistration = packageRegistration;
    }

    /// <summary>How long a cancelled child gets to exit before its job is terminated.</summary>
    public TimeSpan GracefulStopTimeout { get; init; } = GuestProcessHost.DefaultGracefulStopTimeout;

    /// <summary>Most operations and transfers one connection may have in flight at once.</summary>
    /// <remarks>
    /// A bound, not a queue: an admitted connection that asks for more gets an immediate, actionable
    /// refusal rather than an unbounded set of child processes and open file handles. It is per
    /// connection because operation identity is per connection, so one host's traffic cannot consume
    /// another's budget.
    /// </remarks>
    public int MaxConcurrentOperations { get; init; } = DefaultMaxConcurrentOperations;

    /// <summary>Default value of <see cref="MaxConcurrentOperations"/>.</summary>
    internal const int DefaultMaxConcurrentOperations = 32;

    /// <summary>
    /// How long shutdown waits for operations to finish reporting before closing the connection.
    /// </summary>
    /// <remarks>
    /// Generous enough that an operation stopping normally always completes within it, and finite so
    /// that one whose final frames are blocked on an unresponsive peer cannot hold the agent open.
    /// </remarks>
    public TimeSpan ShutdownDrainTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When set, every request on this connection is answered with this error instead of served.
    /// </summary>
    /// <remarks>
    /// This is how the agent refuses a connection past its channel bound. Refusing here rather than
    /// by dropping the socket is deliberate: the refusal is only produced after the secure handshake
    /// has authenticated the peer, and it reaches the host as an ordinary failure envelope with a
    /// user action, instead of as a connection reset the host can only report as a transport error.
    /// </remarks>
    public ExecutionTargetErrorInfo? AdmissionRefusal { get; init; }

    /// <summary>
    /// Serves operations until the host disconnects or <paramref name="cancellationToken"/> fires.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await _transport.ReceiveFrameAsync(cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    return;
                }

                await DispatchAsync(frame.Value, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        finally
        {
            await StopAllOperationsAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAllOperationsAsync().ConfigureAwait(false);
        _sendLock.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private async Task DispatchAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        if (!GuestPayloadCodec.TryGetKind(frame.Span, out var kind))
        {
            return;
        }

        if (kind == GuestPayloadKind.Stream)
        {
            await DispatchStreamAsync(frame, cancellationToken).ConfigureAwait(false);
            return;
        }

        var message = GuestPayloadCodec.TryDecodeJson(frame.Span);
        if (message?.OperationId is null || !Guid.TryParse(message.OperationId, out var operationId))
        {
            return;
        }

        // Fence every request on the generation the host believes it is talking to. A request built
        // against a previous Sandbox must not be applied to this one, whatever it asks for.
        if (!IsCurrentEpoch(message.TargetEpoch))
        {
            await SendAsync(
                new GuestMessage
                {
                    Type = GuestMessageTypes.OperationFailed,
                    OperationId = message.OperationId,
                    TargetEpoch = _targetEpoch,
                    Error = new ExecutionTargetErrorInfo
                    {
                        Code = ExecutionTargetErrorCodes.TargetStale,
                        Message = "The request was built for a different Windows Sandbox generation.",
                        UserAction = "Retry the command so it targets the current Sandbox.",
                    },
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // Refused connections never reach the dispatch table. The check sits after the epoch fence
        // so a stale request still gets the more specific answer, and before every handler so a
        // refused peer cannot start a process, open a file, or learn capabilities.
        if (AdmissionRefusal is { } refusal)
        {
            await SendFailureAsync(operationId, refusal).ConfigureAwait(false);
            return;
        }

        switch (message.Type)
        {
            case GuestMessageTypes.CapabilitiesRequest:
                await SendCapabilitiesAsync(message.OperationId, cancellationToken).ConfigureAwait(false);
                break;

            case GuestMessageTypes.ExecRequest when message.Exec is { } exec:
                StartOperation(operationId, exec);
                break;

            case GuestMessageTypes.StdinClosed:
                if (_operations.TryGetValue(operationId, out var forClose))
                {
                    forClose.Host.CloseStandardInput();
                }
                else if (_writes.TryRemove(operationId, out var completedWrite))
                {
                    // End of a file transfer: verify and publish, or report exactly how far it got.
                    await CompleteWriteAsync(operationId, completedWrite, cancellationToken).ConfigureAwait(false);
                }

                break;

            case GuestMessageTypes.CancelRequest:
                if (_operations.TryGetValue(operationId, out var forCancel))
                {
                    await forCancel.CancelAsync().ConfigureAwait(false);
                }
                else if (_writes.TryRemove(operationId, out var abandonedWrite))
                {
                    // A cancelled upload must release its handle and discard the partial file here.
                    // Leaving it would keep the destination locked, so an immediate retry of the
                    // same transfer would fail on a file the caller believes was abandoned.
                    await abandonedWrite.DisposeAsync().ConfigureAwait(false);
                }

                break;

            case GuestMessageTypes.ListFilesRequest when message.Scope is { } listScope:
                await HandleListAsync(operationId, listScope, cancellationToken).ConfigureAwait(false);
                break;

            case GuestMessageTypes.PutFileRequest when message.Scope is { } putScope && message.File is { } file:
                BeginWrite(operationId, putScope, file);
                break;

            case GuestMessageTypes.GetFileRequest when message.Scope is { } getScope && message.Paths is [var path]:
                await HandleGetAsync(operationId, getScope, path, cancellationToken).ConfigureAwait(false);
                break;

            case GuestMessageTypes.DeleteFilesRequest when message.Scope is { } deleteScope && message.Paths is { } paths:
                await HandleDeleteAsync(operationId, deleteScope, paths, cancellationToken).ConfigureAwait(false);
                break;

            case GuestMessageTypes.RemoveScopeRequest when message.Scope is { } removeScope:
                await HandleRemoveScopeAsync(operationId, removeScope, cancellationToken).ConfigureAwait(false);
                break;

            case GuestMessageTypes.StopPackageRequest when message.PackageFamilyName is { } packageFamilyName
                && message.ExpectedRegisteredLocation is { } expectedRegisteredLocation:
                await HandleStopPackageAsync(
                    operationId, packageFamilyName, expectedRegisteredLocation, cancellationToken).ConfigureAwait(false);
                break;

            case GuestMessageTypes.QueryPackageRequest when message.PackageName is { } packageName
                && message.PackagePublisher is { } publisher
                && message.PackageFamilyName is { } queryFamilyName:
                await HandleQueryPackageAsync(
                    operationId, packageName, publisher, queryFamilyName, cancellationToken).ConfigureAwait(false);
                break;

            case GuestMessageTypes.UnregisterPackageRequest
                when message.PackageFamilyName is { } unregisterFamilyName
                && message.PackageFullName is { } packageFullName
                && message.ExpectedRegisteredLocation is { } unregisterLocation:
                await HandleUnregisterPackageAsync(
                    operationId,
                    unregisterFamilyName,
                    packageFullName,
                    unregisterLocation,
                    cancellationToken).ConfigureAwait(false);
                break;

            case GuestMessageTypes.StopProcessRequest when message.ProcessId is { } stopProcessId:
                await HandleStopProcessAsync(
                    operationId, stopProcessId, message.ProcessStartTicksUtc ?? 0, cancellationToken).ConfigureAwait(false);
                break;

            case GuestMessageTypes.QueryProcessRequest when message.ProcessId is { } queryProcessId:
                await HandleQueryProcessAsync(
                    operationId,
                    queryProcessId,
                    message.ProcessStartTicksUtc ?? 0,
                    cancellationToken).ConfigureAwait(false);
                break;

            default:
                // An unknown or malformed message is ignored rather than fatal: the host is
                // authenticated, so this is a version skew, and one unusable message must not take
                // down operations that are working.
                break;
        }
    }

    private async Task DispatchStreamAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        if (!GuestPayloadCodec.TryDecodeStream(frame, out var operationId, out var stream, out var data))
        {
            return;
        }

        if (stream != GuestStreamId.StandardInput)
        {
            // Output only ever flows guest to host; a host sending it is ignored.
            return;
        }

        if (_writes.TryGetValue(operationId, out var write))
        {
            try
            {
                await write.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            }
            catch (ExecutionTargetException ex)
            {
                _writes.TryRemove(operationId, out _);

                // Same ordering as the completion path: discard the partial file before reporting,
                // so the failure the host observes is already true on disk.
                await write.DisposeAsync().ConfigureAwait(false);
                await SendFailureAsync(operationId, ex.Error).ConfigureAwait(false);
            }

            return;
        }

        if (_operations.TryGetValue(operationId, out var operation))
        {
            await operation.Host.WriteStandardInputAsync(data, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool IsCurrentEpoch(string? epoch) =>
        string.IsNullOrEmpty(epoch) || string.Equals(epoch, _targetEpoch, StringComparison.Ordinal);

    private async Task SendCapabilitiesAsync(string operationId, CancellationToken cancellationToken)
    {
        var session = _sessionProbe.Probe();
        var readiness = GuestAgentReadiness.Evaluate(session);

        await SendAsync(
            new GuestMessage
            {
                Type = GuestMessageTypes.CapabilitiesResponse,
                OperationId = operationId,
                TargetEpoch = _targetEpoch,
                Capabilities = new ExecutionTargetCapabilities
                {
                    Architecture = _identity.Architecture,

                    // Capability, not readiness: the guest can do these in principle. Whether input
                    // can be delivered right now is re-verified immediately before each
                    // foreground-sensitive command, because the client can be closed at any moment.
                    SupportsInteractiveDesktop = GuestAgentReadiness.SupportsReadOnlyAutomation(session),
                    SupportsRealInput = readiness == GuestReadinessFailure.None,
                    SupportsScreenCapture = readiness == GuestReadinessFailure.None,
                    CooperativeUiTurnsVersion = GuestOwnerContext.CooperativeUiTurnsVersion,
                    SupportsInternalSystemSetup = true,

                    // Windows Sandbox discards everything on teardown, so deployments and runtimes
                    // must be reconciled after every new epoch.
                    PersistentStorage = false,

                    // Reported rather than assumed by the host, so the guest layout stays the
                    // guest's business and target-neutral orchestration never encodes it.
                    ManagedRoot = _files?.ManagedRoot,
                },
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns the actual contents of a managed guest location.</summary>
    private async Task HandleListAsync(Guid operationId, GuestPathScope scope, CancellationToken cancellationToken)
    {
        try
        {
            var files = await RequireFiles().ListAsync(scope, cancellationToken).ConfigureAwait(false);

            await SendAsync(
                new GuestMessage
                {
                    Type = GuestMessageTypes.ListFilesResponse,
                    OperationId = operationId.ToString(),
                    TargetEpoch = _targetEpoch,
                    Files = files,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (ExecutionTargetException ex)
        {
            await SendFailureAsync(operationId, ex.Error).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await SendFailureAsync(operationId, FileFailure(ex)).ConfigureAwait(false);
        }
    }

    /// <summary>Opens a destination for an incoming file; content follows as stream frames.</summary>
    private void BeginWrite(Guid operationId, GuestPathScope scope, GuestFileInfo file)
    {
        if (IsAtOperationLimit())
        {
            _ = SendFailureAsync(operationId, OperationLimitReached());
            return;
        }

        try
        {
            _writes[operationId] = RequireFiles().BeginWrite(scope, file);
        }
        catch (ExecutionTargetException ex)
        {
            _ = SendFailureAsync(operationId, ex.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = SendFailureAsync(operationId, FileFailure(ex));
        }
    }

    /// <summary>Verifies and publishes a completed transfer.</summary>
    /// <remarks>
    /// Cleanup happens before the outcome is reported, not in a trailing <c>finally</c>. Reporting
    /// first would let a host that retries immediately race the temporary file it was told did not
    /// survive — and would make "no partial file is left behind" true only eventually.
    /// </remarks>
    private async Task CompleteWriteAsync(Guid operationId, GuestFileWrite write, CancellationToken cancellationToken)
    {
        ExecutionTargetErrorInfo? failure = null;

        try
        {
            await write.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ExecutionTargetException ex)
        {
            failure = ex.Error;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = FileFailure(ex);
        }

        await write.DisposeAsync().ConfigureAwait(false);

        if (failure is null)
        {
            await SendFileCompletedAsync(operationId, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SendFailureAsync(operationId, failure).ConfigureAwait(false);
    }

    /// <summary>Streams one managed guest file back to the host.</summary>
    private async Task HandleGetAsync(
        Guid operationId,
        GuestPathScope scope,
        string relativePath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var source = RequireFiles().OpenRead(scope, relativePath);
            var buffer = new byte[GuestPayloadCodec.MaxStreamChunkSize];

            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var payload = GuestPayloadCodec.EncodeStream(
                    operationId,
                    GuestStreamId.StandardOutput,
                    buffer.AsSpan(0, read));

                await SendRawAsync(payload, cancellationToken).ConfigureAwait(false);
            }

            await SendFileCompletedAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (ExecutionTargetException ex)
        {
            await SendFailureAsync(operationId, ex.Error).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await SendFailureAsync(operationId, FileFailure(ex)).ConfigureAwait(false);
        }
    }

    /// <summary>Removes paths a reconciliation determined should no longer exist.</summary>
    private async Task HandleDeleteAsync(
        Guid operationId,
        GuestPathScope scope,
        List<string> relativePaths,
        CancellationToken cancellationToken)
    {
        try
        {
            RequireFiles().Delete(scope, relativePaths);
            await SendFileCompletedAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (ExecutionTargetException ex)
        {
            await SendFailureAsync(operationId, ex.Error).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await SendFailureAsync(operationId, FileFailure(ex)).ConfigureAwait(false);
        }
    }

    /// <summary>Discards an entire managed scope, for an explicit clean reinstall.</summary>
    private async Task HandleRemoveScopeAsync(
        Guid operationId,
        GuestPathScope scope,
        CancellationToken cancellationToken)
    {
        try
        {
            RequireFiles().RemoveScope(scope);
            await SendFileCompletedAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (ExecutionTargetException ex)
        {
            await SendFailureAsync(operationId, ex.Error).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await SendFailureAsync(operationId, FileFailure(ex)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops every running process of the package a deployment registered, before a redeploy
    /// mutates the layout it came from.
    /// </summary>
    /// <remarks>
    /// The family name is resolved to whatever full name is actually registered right now, so this
    /// always targets the guest's live inventory rather than a value the host cached. When the
    /// inventory query <em>confirms</em> nothing is registered under that family any more, there is
    /// nothing that could be running under it, so that is reported as success. A query that instead
    /// *fails* -- a transient COM error, a denied inventory read -- is never treated the same way:
    /// it means winapp does not know whether the package is installed and possibly running, and
    /// that has to fail closed exactly like a termination it could not confirm, not be quietly
    /// read as "nothing to stop".
    /// <para>
    /// A family name match alone is not ownership: two deployments built from different source
    /// paths can share the same package identity, and only one of them can be genuinely registered
    /// at a time. Before anything is terminated, the currently registered package's own install
    /// location is compared against the location the requesting deployment itself expects. A
    /// mismatch means the family currently belongs to a different deployment's live registration --
    /// possibly a legitimately running application -- and that is refused exactly like an unproven
    /// stop, never resolved by guessing.
    /// </para>
    /// <para>
    /// A failure here is reported rather than swallowed, unlike the best-effort cleanup
    /// <c>IAppLauncherService.TerminatePackageProcesses</c> performs on an interactive Ctrl+C: the
    /// caller is about to mutate files this package may still have open, so it must know when the
    /// stop could not be proven rather than silently continuing.
    /// </para>
    /// </remarks>
    private async Task HandleStopPackageAsync(
        Guid operationId,
        string packageFamilyName,
        string expectedRegisteredLocation,
        CancellationToken cancellationToken)
    {
        try
        {
            var launcher = RequireAppLauncher();

            // Strict lookup: an inventory query failure must never be read as "confirmed absent".
            // Only a query that actually completed and found nothing may skip the termination call.
            var registered = launcher.GetRegisteredPackageOrThrow(packageFamilyName);

            if (registered is null)
            {
                await SendFileCompletedAsync(operationId, cancellationToken).ConfigureAwait(false);
                return;
            }

            var actualLocation = registered.InstallLocation;

            if (actualLocation is null || !TargetPathSafety.PathsEqual(actualLocation, expectedRegisteredLocation))
            {
                // Something else -- most plausibly a different deployment that legitimately owns
                // this family right now -- is registered here, or the inventory could not report a
                // location to compare at all. Both are treated identically: refused, not adopted
                // and not terminated, exactly like any Sandbox or package winapp cannot prove it
                // owns. A location that could not be determined is proof failure, not proof of
                // absence, and must never be read as "safe to proceed".
                await SendFailureAsync(
                    operationId,
                    new ExecutionTargetErrorInfo
                    {
                        Code = ExecutionTargetErrorCodes.StaleHandle,
                        Message =
                            $"The package currently registered as '{packageFamilyName}' is not the one this deployment registered, so winapp cannot safely stop it before redeploying.",
                        UserAction = "Close the running application manually in Windows Sandbox, then retry.",
                        Context = new Dictionary<string, string>
                        {
                            ["packageFamilyName"] = packageFamilyName,
                            ["expectedRegisteredLocation"] = expectedRegisteredLocation,
                            ["actualRegisteredLocation"] = actualLocation ?? "(unknown)",
                        },
                    }).ConfigureAwait(false);
                return;
            }

            launcher.StopPackageProcessesOrThrow(registered.FullName);

            await SendFileCompletedAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (ExecutionTargetException ex)
        {
            await SendFailureAsync(operationId, ex.Error).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately broad: whether the inventory lookup or the termination call itself
            // failed, and whatever exception type either one happens to surface (COM, WinRT
            // projection, or otherwise), the caller is about to mutate files this package may still
            // have open. An unrecognised exception type must fail closed the same way a recognised
            // one does, never propagate uncaught and tear down the connection instead.
            await SendFailureAsync(
                operationId,
                new ExecutionTargetErrorInfo
                {
                    Code = ExecutionTargetErrorCodes.StaleHandle,
                    Message =
                        $"Could not confirm that the previous run of '{packageFamilyName}' was stopped before redeploying.",
                    UserAction = "Close the running application in Windows Sandbox, then retry.",
                    Context = new Dictionary<string, string> { ["packageFamilyName"] = packageFamilyName },
                }).ConfigureAwait(false);
        }
    }

    /// <summary>Returns the guest OS's authoritative registration without changing it.</summary>
    private async Task HandleQueryPackageAsync(
        Guid operationId,
        string packageName,
        string publisher,
        string packageFamilyName,
        CancellationToken cancellationToken)
    {
        try
        {
            var registered = RequireAppLauncher().GetRegisteredPackageOrThrow(packageFamilyName);

            if (registered is not null &&
                (!string.Equals(registered.Name, packageName, StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(registered.Publisher, publisher, StringComparison.OrdinalIgnoreCase)))
            {
                throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.TargetAmbiguous,
                    $"Windows returned a package whose identity does not match '{packageName}'.",
                    userAction: "Inspect the package registration in the target, then retry.");
            }

            await SendAsync(
                new GuestMessage
                {
                    Type = GuestMessageTypes.QueryPackageResponse,
                    OperationId = operationId.ToString(),
                    TargetEpoch = _targetEpoch,
                    PackageRegistered = registered is not null,
                    RegisteredPackage = registered is null
                        ? null
                        : new GuestPackageRegistration(
                            registered.FullName,
                            registered.IsDevelopmentMode,
                            registered.InstallLocation),
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (ExecutionTargetException ex)
        {
            await SendFailureAsync(operationId, ex.Error).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await SendFailureAsync(
                operationId,
                new ExecutionTargetErrorInfo
                {
                    Code = ExecutionTargetErrorCodes.StaleHandle,
                    Message = $"Could not query the package registered as '{packageName}' in the target.",
                    UserAction = "Retry the command.",
                    Context = new Dictionary<string, string>
                    {
                        ["packageName"] = packageName,
                        ["packageFamilyName"] = packageFamilyName,
                        ["reason"] = ex.Message,
                    },
                }).ConfigureAwait(false);
        }
    }

    /// <summary>Unregisters only the package whose full name the host already proved it owns.</summary>
    private async Task HandleUnregisterPackageAsync(
        Guid operationId,
        string packageFamilyName,
        string packageFullName,
        string expectedRegisteredLocation,
        CancellationToken cancellationToken)
    {
        try
        {
            var registered = RequireAppLauncher().GetRegisteredPackageOrThrow(packageFamilyName);
            if (registered is null)
            {
                await SendFileCompletedAsync(operationId, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!registered.IsDevelopmentMode ||
                !string.Equals(registered.FullName, packageFullName, StringComparison.OrdinalIgnoreCase) ||
                registered.InstallLocation is null ||
                !TargetPathSafety.PathsEqual(registered.InstallLocation, expectedRegisteredLocation))
            {
                throw ExecutionTargetException.Create(
                    registered.IsDevelopmentMode
                        ? ExecutionTargetErrorCodes.PackageConflict
                        : ExecutionTargetErrorCodes.ProvisionedPackageConflict,
                    $"The registration for '{packageFamilyName}' changed before it could be removed.",
                    userAction: "Inspect the package registration in Windows Sandbox, then retry.");
            }

            await RequirePackageRegistration()
                .UnregisterByFullNameAsync(packageFullName, preserveAppData: false, cancellationToken)
                .ConfigureAwait(false);
            await SendFileCompletedAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (ExecutionTargetException ex)
        {
            await SendFailureAsync(operationId, ex.Error).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await SendFailureAsync(
                operationId,
                new ExecutionTargetErrorInfo
                {
                    Code = ExecutionTargetErrorCodes.PackageConflict,
                    Message = $"Could not unregister package '{packageFullName}' in the target.",
                    UserAction = "Close the application in Windows Sandbox, then retry.",
                    Context = new Dictionary<string, string>
                    {
                        ["packageFullName"] = packageFullName,
                        ["reason"] = ex.Message,
                    },
                }).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops one specific tracked process, verified by PID and start time, before a redeploy
    /// mutates files it may still have open.
    /// </summary>
    /// <remarks>
    /// A PID alone is never proof: the process winapp launched may have already exited and the
    /// number reused by something completely unrelated. <paramref name="expectedStartTicksUtc"/> is
    /// compared against the live process's own start time, and a mismatch is treated exactly like
    /// "already gone" — the original is provably not there any more, and the process that now holds
    /// the PID was never winapp's to touch.
    /// <para>
    /// <see cref="StopTrackedProcessImpl"/> is a replaceable delegate, and killing a real process
    /// tree can itself throw (<c>Process.Kill(entireProcessTree: true)</c> aggregates a partial
    /// failure into an <see cref="AggregateException"/>). Whatever it throws is caught here and
    /// converted to the same structured, fail-closed response an <see
    /// cref="ProcessStopOutcome.Unproven"/> outcome produces: this is one operation on one
    /// connection, and it must never be allowed to escape into the dispatch loop that is serving
    /// every other request on this Sandbox, tearing the whole connection down over one process this
    /// deployment happened to be unable to stop.
    /// </para>
    /// </remarks>
    private async Task HandleStopProcessAsync(
        Guid operationId,
        int processId,
        long expectedStartTicksUtc,
        CancellationToken cancellationToken)
    {
        ProcessStopOutcome outcome;

        try
        {
            outcome = StopTrackedProcessImpl(processId, expectedStartTicksUtc);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            outcome = ProcessStopOutcome.Unproven;
        }

        if (outcome == ProcessStopOutcome.Unproven)
        {
            await SendFailureAsync(
                operationId,
                new ExecutionTargetErrorInfo
                {
                    Code = ExecutionTargetErrorCodes.StaleHandle,
                    Message = $"Could not confirm that the previous process (PID {processId}) was stopped before redeploying.",
                    UserAction = "Close the application in Windows Sandbox, then retry.",
                    Context = new Dictionary<string, string>
                    {
                        ["processId"] = processId.ToString(CultureInfo.InvariantCulture),
                    },
                }).ConfigureAwait(false);
            return;
        }

        await SendFileCompletedAsync(operationId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reports liveness only when both the PID and its start time still match.</summary>
    private Task HandleQueryProcessAsync(
        Guid operationId,
        int processId,
        long expectedStartTicksUtc,
        CancellationToken cancellationToken) =>
        SendAsync(
            new GuestMessage
            {
                Type = GuestMessageTypes.QueryProcessResponse,
                OperationId = operationId.ToString(),
                TargetEpoch = _targetEpoch,
                ProcessRunning = IsExactProcessRunning(processId, expectedStartTicksUtc),
            },
            cancellationToken);

    internal static bool IsExactProcessRunning(int processId, long expectedStartTicksUtc)
    {
        if (processId <= 0 || expectedStartTicksUtc <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited &&
                process.StartTime.ToUniversalTime().Ticks == expectedStartTicksUtc;
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Outcome of attempting to stop one tracked process.</summary>
    internal enum ProcessStopOutcome
    {
        /// <summary>The tracked process was not present (already exited, or its PID was reused).</summary>
        AlreadyGone,

        /// <summary>The tracked process was found, verified, and stopped.</summary>
        Stopped,

        /// <summary>The tracked process could not be verified as stopped.</summary>
        Unproven,
    }

    /// <summary>
    /// Verification-then-kill seam for <see cref="HandleStopProcessAsync"/>. Overridable in tests
    /// to exercise the stale-PID and stop-failure paths deterministically, without racing a real
    /// process's exit.
    /// </summary>
    internal Func<int, long, ProcessStopOutcome> StopTrackedProcessImpl { get; set; } = DefaultStopTrackedProcess;

    internal static ProcessStopOutcome DefaultStopTrackedProcess(int processId, long expectedStartTicksUtc)
    {
        Process process;

        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            // Nothing has this PID any more. Our tracked process is provably gone.
            return ProcessStopOutcome.AlreadyGone;
        }

        using (process)
        {
            long actualStartTicksUtc;

            try
            {
                actualStartTicksUtc = process.StartTime.ToUniversalTime().Ticks;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Exited between the lookup and reading its start time. Provably gone either way.
                return ProcessStopOutcome.AlreadyGone;
            }

            if (actualStartTicksUtc != expectedStartTicksUtc)
            {
                // A different process now holds this PID. The one winapp tracked has already
                // exited — there is nothing of ours left to stop, and this process, which winapp
                // never started, must never be touched.
                return ProcessStopOutcome.AlreadyGone;
            }

            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Deliberately broad, unlike the narrower catches above: killing a real process
                // tree can throw AggregateException (Process.Kill(entireProcessTree: true)
                // aggregates a partial per-process failure) in addition to Win32Exception or
                // InvalidOperationException, and every one of those means the same thing here --
                // the stop could not be proven -- so every one of them must produce the same
                // fail-closed outcome rather than only the two types this used to recognise.
                return ProcessStopOutcome.Unproven;
            }

            return process.HasExited ? ProcessStopOutcome.Stopped : ProcessStopOutcome.Unproven;
        }
    }

    /// <summary>The app launcher, or a clear failure when this agent was built without one.</summary>
    private IAppLauncherService RequireAppLauncher() =>
        _appLauncher ?? throw ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.TransportFailed,
            "This guest agent was not configured with application launch/termination support.",
            userAction: "Retry the command.");

    /// <summary>The package service, or a clear failure when this agent was built without one.</summary>
    private IPackageRegistrationService RequirePackageRegistration() =>
        _packageRegistration ?? throw ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.TransportFailed,
            "This guest agent was not configured with package registration support.",
            userAction: "Retry the command.");

    private Task SendFileCompletedAsync(Guid operationId, CancellationToken cancellationToken) =>        SendAsync(
            new GuestMessage
            {
                Type = GuestMessageTypes.FileCompleted,
                OperationId = operationId.ToString(),
                TargetEpoch = _targetEpoch,
            },
            cancellationToken);

    /// <summary>The file service, or a clear failure when this agent was built without one.</summary>
    private GuestFileService RequireFiles() =>
        _files ?? throw ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.TransportFailed,
            "This guest agent was not configured with managed storage.",
            userAction: "Retry the command.");

    private static ExecutionTargetErrorInfo FileFailure(Exception ex) =>
        GuestFileService.IsSharingViolation(ex)
            ? new ExecutionTargetErrorInfo
            {
                Code = ExecutionTargetErrorCodes.TransferInterrupted,
                Message = "A guest file operation failed because a running process still has the file open.",
                UserAction = "Close the application that is still running in Windows Sandbox, then retry.",
            }
            : new ExecutionTargetErrorInfo
            {
                Code = ExecutionTargetErrorCodes.TransferInterrupted,
                Message = $"A guest file operation failed: {ex.Message}",
                UserAction = "Retry the command.",
            };

    /// <summary>Whether this connection already owns as much in-flight work as it may.</summary>
    private bool IsAtOperationLimit() => _operations.Count + _writes.Count >= MaxConcurrentOperations;

    private ExecutionTargetErrorInfo OperationLimitReached() => new()
    {
        Code = ExecutionTargetErrorCodes.AgentBusy,
        Message =
            $"This connection to the Windows Sandbox agent already has {MaxConcurrentOperations} operations in flight.",
        UserAction = "Wait for one of this command's operations to finish, then retry.",
    };

    private void StartOperation(Guid operationId, GuestExecRequest request)
    {
        RunningOperation operation;

        if (IsAtOperationLimit())
        {
            _ = SendFailureAsync(operationId, OperationLimitReached());
            return;
        }

        // Readiness is re-verified here, immediately before the process starts, rather than taken
        // from the capability handshake. The user can close the Sandbox window at any moment, which
        // silently removes real input and screen capture while leaving UI Automation working -- so
        // a command that would inject input must be refused now, not admitted on the strength of a
        // check that was true when the channel opened.
        if (request.RequiresRealInput)
        {
            var readiness = GuestAgentReadiness.Evaluate(_sessionProbe.Probe());

            if (readiness != GuestReadinessFailure.None)
            {
                _ = SendFailureAsync(operationId, GuestAgentReadiness.Describe(readiness));
                return;
            }
        }

        if (!TryResolveExecutable(request, out var resolved, out var resolutionError))
        {
            _ = SendFailureAsync(operationId, resolutionError);
            return;
        }

        try
        {
            var outputSends = new ConcurrentQueue<Task>();
            var host = _processes.Start(
                resolved,
                (stream, data) => outputSends.Enqueue(ForwardOutputAsync(operationId, stream, data)));

            operation = new RunningOperation(host, outputSends, GracefulStopTimeout, request.Detach);
        }
        catch (ExecutionTargetException ex)
        {
            _ = SendAsync(
                new GuestMessage
                {
                    Type = GuestMessageTypes.OperationFailed,
                    OperationId = operationId.ToString(),
                    TargetEpoch = _targetEpoch,
                    Error = ex.Error,
                },
                CancellationToken.None);
            return;
        }

        _operations[operationId] = operation;
        _ = Task.Run(() => RunOperationAsync(operationId, operation));
    }

    /// <summary>
    /// Decides which binary an operation actually starts.
    /// </summary>
    /// <remarks>
    /// The guest's own winapp is named by a flag rather than a path, so a host cannot point "guest
    /// winapp" at some other binary and have the agent run it with the agent's own privileges and
    /// forwarded owner context. An empty executable is refused outright rather than handed to
    /// process creation to reject with an OS error the caller cannot act on.
    /// </remarks>
    private bool TryResolveExecutable(
        GuestExecRequest request,
        out GuestExecRequest resolved,
        out ExecutionTargetErrorInfo error)
    {
        error = null!;

        // Checked first and for every request shape: a missing working directory produces the same
        // "could not start" failure from Process.Start regardless of whether the executable path was
        // otherwise fine, and that generic message blames the executable (or, for guest winapp, the
        // deployment) rather than the actual cause. Naming the directory here is what keeps
        // `target exec --cwd <missing>` from being misdiagnosed as a bad executable or deployment.
        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory) && !Directory.Exists(request.WorkingDirectory))
        {
            resolved = request;
            error = new ExecutionTargetErrorInfo
            {
                Code = ExecutionTargetErrorCodes.ArtifactFailed,
                Message = $"The working directory '{request.WorkingDirectory}' does not exist inside Windows Sandbox.",
                UserAction = "Check --cwd, then retry.",
                Context = new Dictionary<string, string> { ["workingDirectory"] = request.WorkingDirectory },
            };
            return false;
        }

        if (request.UseGuestWinapp)
        {
            if (string.IsNullOrWhiteSpace(_guestWinapp))
            {
                resolved = request;
                error = new ExecutionTargetErrorInfo
                {
                    Code = ExecutionTargetErrorCodes.AgentIncompatible,
                    Message = "The guest agent cannot locate its own winapp binary.",
                    UserAction = "Restart Windows Sandbox so winapp reinstalls the guest agent.",
                };
                return false;
            }

            resolved = new GuestExecRequest
            {
                Executable = _guestWinapp,
                Arguments = request.Arguments,
                WorkingDirectory = request.WorkingDirectory,
                Environment = request.Environment,
                RequiresRealInput = request.RequiresRealInput,
            };
            return true;
        }

        if (string.IsNullOrWhiteSpace(request.Executable))
        {
            resolved = request;
            error = new ExecutionTargetErrorInfo
            {
                Code = ExecutionTargetErrorCodes.TargetAmbiguous,
                Message = "The request did not name an executable to run inside the guest.",
                UserAction = "Put the executable and its arguments after '--'.",
            };
            return false;
        }

        resolved = request;
        return true;
    }

    private async Task RunOperationAsync(Guid operationId, RunningOperation operation)
    {
        try
        {
            await SendAsync(
                new GuestMessage
                {
                    Type = GuestMessageTypes.ExecStarted,
                    OperationId = operationId.ToString(),
                    TargetEpoch = _targetEpoch,
                    ProcessId = operation.Host.ProcessId,
                    ProcessStartTicksUtc = operation.Host.StartTicksUtc,
                },
                CancellationToken.None).ConfigureAwait(false);

            if (operation.Detach)
            {
                await SendAsync(
                    new GuestMessage
                    {
                        Type = GuestMessageTypes.ExecCompleted,
                        OperationId = operationId.ToString(),
                        TargetEpoch = _targetEpoch,
                        ExitCode = 0,
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }

            var exitCode = await operation.Host.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            // GuestProcessHost has drained the child pipes at this point, so no more tasks can be
            // enqueued. Wait for every encoded frame to cross the transport before completion makes
            // the host remove the operation; otherwise a fast process loses its trailing output.
            await Task.WhenAll(operation.OutputSends.ToArray()).ConfigureAwait(false);

            if (!operation.Detach)
            {
                await SendAsync(
                    new GuestMessage
                    {
                        Type = GuestMessageTypes.ExecCompleted,
                        OperationId = operationId.ToString(),
                        TargetEpoch = _targetEpoch,
                        ExitCode = exitCode,
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (ExecutionTargetException ex)
        {
            await SendFailureAsync(operationId, ex.Error).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            await SendFailureAsync(
                operationId,
                new ExecutionTargetErrorInfo
                {
                    Code = ExecutionTargetErrorCodes.TransportFailed,
                    Message = "The guest lost track of a running process.",
                    UserAction = "Retry the command.",
                }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Total by construction. Nothing awaits this task, so an escaping exception would become
            // an unobserved fault and leave the requesting host waiting for a result that can no
            // longer arrive. It is reported to that host and to diagnostics instead.
            System.Diagnostics.Trace.TraceWarning("A guest operation ended unexpectedly: {0}", ex.Message);

            await SendFailureAsync(
                operationId,
                new ExecutionTargetErrorInfo
                {
                    Code = ExecutionTargetErrorCodes.TransportFailed,
                    Message = "The guest agent failed while running this operation.",
                    UserAction = "Retry the command.",
                }).ConfigureAwait(false);
        }
        finally
        {
            _operations.TryRemove(operationId, out _);
            await operation.Host.DisposeAsync().ConfigureAwait(false);
            operation.MarkFinished();
        }
    }

    private async Task SendFailureAsync(Guid operationId, ExecutionTargetErrorInfo error)
    {
        try
        {
            await SendAsync(
                new GuestMessage
                {
                    Type = GuestMessageTypes.OperationFailed,
                    OperationId = operationId.ToString(),
                    TargetEpoch = _targetEpoch,
                    Error = error,
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (ExecutionTargetException)
        {
            // The connection is already gone; the host will observe the closed channel instead.
        }
    }

    /// <summary>Forwards one output chunk, splitting it to fit the frame limit.</summary>
    private async Task ForwardOutputAsync(Guid operationId, GuestStreamId stream, ReadOnlyMemory<byte> data)
    {
        var remaining = data;

        try
        {
            while (!remaining.IsEmpty)
            {
                var take = Math.Min(remaining.Length, GuestPayloadCodec.MaxStreamChunkSize);
                var payload = GuestPayloadCodec.EncodeStream(operationId, stream, remaining.Span[..take]);
                await SendRawAsync(payload, CancellationToken.None).ConfigureAwait(false);
                remaining = remaining[take..];
            }
        }
        catch (ExecutionTargetException)
        {
            // The host went away mid-stream. The operation's own completion path reports it.
        }
        catch (ObjectDisposedException)
        {
            // The server shut down while output was still draining.
        }
    }

    private Task SendAsync(GuestMessage message, CancellationToken cancellationToken) =>
        SendRawAsync(GuestPayloadCodec.EncodeJson(message), cancellationToken);

    private async Task SendRawAsync(byte[] payload, CancellationToken cancellationToken)
    {
        // One lock over the whole send keeps frames — and therefore the sequence numbers the AEAD
        // nonce is derived from — strictly ordered even with several operations streaming at once.
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

    /// <summary>
    /// Stops every running operation, so nothing outlives the connection that asked for it.
    /// </summary>
    /// <remarks>
    /// Cancellation is requested for all of them first and awaited afterwards, so operations stop in
    /// parallel rather than one graceful timeout after another.
    /// <para>
    /// Completion is awaited rather than merely requested. Returning as soon as a stop was asked for
    /// would leave child processes and their handles being disposed on background tasks after the
    /// agent believed it had shut down — which is exactly the kind of "mostly cleaned up" that
    /// leaves a Sandbox holding files the next deployment has to replace.
    /// </para>
    /// <para>
    /// The wait is bounded because an operation's final frames are sent with no cancellation: a peer
    /// that stopped reading can block that send until its socket buffer drains, which may be never.
    /// On timeout this returns anyway, and disposing the transport immediately afterwards is what
    /// unblocks it. Bounded rather than unbounded is the difference between an agent that always
    /// shuts down and one that a single stalled host can wedge.
    /// </para>
    /// </remarks>
    private async Task StopAllOperationsAsync()
    {
        var stopping = new List<Task>();

        foreach (var (id, operation) in _operations.ToArray())
        {
            if (operation.Detach)
            {
                continue;
            }

            _operations.TryRemove(id, out _);
            stopping.Add(StopOperationAsync(operation));
        }

        if (stopping.Count > 0)
        {
            try
            {
                await Task.WhenAll(stopping).WaitAsync(ShutdownDrainTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "A guest operation did not finish reporting within {0}; closing the connection anyway.",
                    ShutdownDrainTimeout);
            }
        }

        // An unfinished transfer is discarded rather than published. A partially received file that
        // survived would be indistinguishable from a legitimate one on the next hash comparison.
        foreach (var (id, write) in _writes.ToArray())
        {
            _writes.TryRemove(id, out _);
            await write.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Stops one operation and waits for it to finish reporting and releasing its child.</summary>
    private static async Task StopOperationAsync(RunningOperation operation)
    {
        try
        {
            // Bounded by the host's own graceful timeout, after which it terminates the job.
            await operation.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // The process already died with the connection.
        }

        await operation.Completion.ConfigureAwait(false);
    }
    /// <summary>One in-flight operation and its child process.</summary>
    private sealed class RunningOperation(
        IGuestProcessHost host,
        ConcurrentQueue<Task> outputSends,
        TimeSpan gracefulTimeout,
        bool detach)
    {
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The child process running this operation.</summary>
        public IGuestProcessHost Host { get; } = host;

        /// <summary>Output frames that must be sent before completion is published.</summary>
        public ConcurrentQueue<Task> OutputSends { get; } = outputSends;

        /// <summary>Whether this process remains owned by the agent after its host channel closes.</summary>
        public bool Detach { get; } = detach;

        /// <summary>
        /// Completes when the operation has fully reported its outcome and released its child.
        /// </summary>
        /// <remarks>
        /// Created with the operation rather than assigned once its task is scheduled, so a shutdown
        /// that races the very first moment of an operation still has something to wait on.
        /// </remarks>
        public Task Completion => _finished.Task;

        /// <summary>Marks this operation as fully finished.</summary>
        public void MarkFinished() => _finished.TrySetResult();

        /// <summary>
        /// Requests graceful termination, then terminates the process tree after the timeout.
        /// </summary>
        public Task<int> CancelAsync() => Host.StopAsync(gracefulTimeout, CancellationToken.None);
    }
}

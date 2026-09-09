// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>
/// Everything shared orchestration is allowed to ask an acquired execution target to do.
/// </summary>
/// <remarks>
/// <para>
/// This is the boundary between "what a command needs" and "how a provider delivers it". Above it,
/// deployment, runtime provisioning, application launch, file transfer, and UI routing see only
/// these operations and the capabilities the target reported. Below it, a provider is free to be a
/// guest agent over an authenticated socket, a worker on a separate desktop, or something else
/// again.
/// </para>
/// <para>
/// The shape is taken from what the Windows Sandbox implementation already proved it needs rather
/// than from what a second provider might one day want. A speculative operation union would have to
/// be redesigned the first time a real provider disagreed with it; this one is known to be
/// sufficient because a working provider is written against it.
/// </para>
/// <para>
/// Implementations are safe for concurrent use: a foreground command holds one of these for as long
/// as its application runs, and other commands must not be blocked behind it.
/// </para>
/// </remarks>
internal interface ITargetOperationExecutor
{
    /// <summary>Asks the target to describe what it supports right now.</summary>
    /// <remarks>
    /// Commands validate required capabilities against this rather than inferring them from the
    /// provider's name, which is what lets a future provider reuse every caller unchanged.
    /// </remarks>
    Task<ExecutionTargetCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken);

    /// <summary>Runs one process on the target, streaming its output, and returns its exit code.</summary>
    /// <remarks>
    /// The exit code returned is the target application's own, kept distinguishable from the
    /// infrastructure failures reported as <see cref="ExecutionTargetException"/>.
    /// </remarks>
    Task<GuestExecResult> ExecuteAsync(
        GuestExecRequest request,
        GuestExecCallbacks? callbacks,
        CancellationToken cancellationToken);

    /// <summary>Sends a chunk of standard input to a running operation.</summary>
    Task SendStandardInputAsync(Guid operationId, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>Signals that no more standard input will arrive for an operation.</summary>
    Task CloseStandardInputAsync(Guid operationId, CancellationToken cancellationToken);

    /// <summary>Lists what a managed target location actually contains.</summary>
    Task<IReadOnlyList<GuestFileInfo>> ListFilesAsync(GuestPathScope scope, CancellationToken cancellationToken);

    /// <summary>Streams one file into a managed target location and waits for it to be verified.</summary>
    Task PutFileAsync(GuestPathScope scope, GuestFileInfo file, Stream content, CancellationToken cancellationToken);

    /// <summary>Streams one file out of a managed target location.</summary>
    Task GetFileAsync(GuestPathScope scope, string relativePath, Stream destination, CancellationToken cancellationToken);

    /// <summary>Removes named files from a managed target location.</summary>
    Task DeleteFilesAsync(GuestPathScope scope, IReadOnlyList<string> relativePaths, CancellationToken cancellationToken);

    /// <summary>Removes a whole managed target location.</summary>
    Task DeleteScopeAsync(GuestPathScope scope, CancellationToken cancellationToken);

    /// <summary>Stops processes belonging to a registered package before it is replaced or removed.</summary>
    Task StopPackageProcessesAsync(
        string packageFamilyName,
        string expectedRegisteredLocation,
        CancellationToken cancellationToken);

    /// <summary>Queries the target OS for the package actually registered under an identity.</summary>
    Task<GuestPackageRegistration?> GetRegisteredPackageAsync(
        string packageName,
        string publisher,
        string packageFamilyName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Unregisters one exact package full name only while its development registration remains at
    /// the expected location.
    /// </summary>
    Task UnregisterPackageAsync(
        string packageFamilyName,
        string packageFullName,
        string expectedRegisteredLocation,
        CancellationToken cancellationToken);

    /// <summary>Stops one process this host started, identified by ID and start time.</summary>
    Task StopTrackedProcessAsync(int processId, long startTicksUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Reports whether the exact PID/start-time identity is alive in the current target epoch.
    /// </summary>
    Task<bool> IsTrackedProcessRunningAsync(
        int processId,
        long startTicksUtc,
        CancellationToken cancellationToken);
}

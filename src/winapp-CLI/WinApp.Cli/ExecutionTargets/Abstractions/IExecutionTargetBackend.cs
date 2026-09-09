// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.ExecutionTargets.Abstractions;

/// <summary>
/// How a target instance is currently classified during reconciliation (spec §"Ensure and reuse").
/// </summary>
internal enum TargetLifecycleState
{
    /// <summary>No managed instance exists.</summary>
    Terminated,

    /// <summary>An instance is coming up but is not yet usable.</summary>
    Starting,

    /// <summary>The instance runs but has no connected interactive desktop.</summary>
    Running,

    /// <summary>The instance runs and its interactive client is connected and usable.</summary>
    InteractiveReady,

    /// <summary>
    /// The instance runs but its interactive client is gone. UI Automation still works; real input
    /// and Windows Graphics Capture do not, so foreground-sensitive commands must fail or reconnect.
    /// </summary>
    StaleClient,

    /// <summary>
    /// The instance is shutting down. Because Windows permits only one Sandbox, a new instance must
    /// not be started until teardown completes.
    /// </summary>
    TearingDown,
}

/// <summary>Result of a cheap, non-mutating platform capability probe.</summary>
/// <param name="IsSupported">Whether this backend can run on this host at all.</param>
/// <param name="Error">Why not, when <paramref name="IsSupported"/> is false.</param>
internal sealed record TargetSupportResult(bool IsSupported, ExecutionTargetErrorInfo? Error)
{
    /// <summary>The supported result.</summary>
    public static TargetSupportResult Supported { get; } = new(true, null);

    /// <summary>Builds an unsupported result carrying the reason.</summary>
    public static TargetSupportResult Unsupported(ExecutionTargetErrorInfo error) => new(false, error);
}

/// <summary>What a caller needs from the target before it will run.</summary>
/// <param name="RequireInteractiveDesktop">
/// True when the command needs real input or screen capture, which requires a connected interactive
/// client. False for read-only work such as UI Automation inspection.
/// </param>
internal sealed record EnsureTargetOptions(bool RequireInteractiveDesktop)
{
    /// <summary>Defaults for foreground-sensitive work.</summary>
    public static EnsureTargetOptions Interactive { get; } = new(RequireInteractiveDesktop: true);

    /// <summary>Defaults for read-only work that does not touch the desktop.</summary>
    public static EnsureTargetOptions ReadOnly { get; } = new(RequireInteractiveDesktop: false);
}

/// <summary>A live, connected target instance.</summary>
/// <param name="Epoch">Generation identity every request and result is fenced against.</param>
/// <param name="Transport">Provider-neutral channel to the guest agent.</param>
/// <param name="Reused">
/// True when an existing instance was reused. Drives the "Reusing Windows Sandbox..." progress line.
/// </param>
internal sealed record TargetConnection(
    ExecutionTargetEpoch Epoch,
    IGuestTransport Transport,
    bool Reused);

/// <summary>
/// Everything a target backend is responsible for (spec §"Target backend").
/// </summary>
/// <remarks>
/// A backend owns environment acquisition and transport only. Deployment, runtime provisioning,
/// UI forwarding, and artifact handling all live above this boundary and must never reference
/// provider APIs, paths, IDs, or window titles — contract tests against a fake transport enforce
/// that separation.
/// </remarks>
internal interface IExecutionTargetBackend
{
    /// <summary>The target this backend serves.</summary>
    ExecutionTargetRef Target { get; }

    /// <summary>
    /// Cheaply determines whether this host can run the target, without mutating anything.
    /// </summary>
    /// <remarks>
    /// Called before application build so a missing prerequisite fails fast rather than after a
    /// long build. There is never a silent fallback to local execution.
    /// </remarks>
    Task<TargetSupportResult> ProbeSupportAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Ensures a running, connected target and returns a transport to its guest agent, reusing an
    /// existing managed instance when one is healthy.
    /// </summary>
    /// <exception cref="ExecutionTargetException">
    /// The target could not be ensured. An instance that winapp cannot prove it owns is reported,
    /// never adopted and never stopped.
    /// </exception>
    Task<TargetConnection> EnsureConnectedAsync(
        EnsureTargetOptions options,
        CancellationToken cancellationToken);

    /// <summary>
    /// Non-sensitive provider detail for failure envelopes, such as an instance ID or client state.
    /// </summary>
    /// <remarks>
    /// Returned as loose key/value pairs so the shared failure envelope can carry provider context
    /// without orchestration knowing what any particular key means.
    /// </remarks>
    IReadOnlyDictionary<string, string> DescribeForDiagnostics();
}

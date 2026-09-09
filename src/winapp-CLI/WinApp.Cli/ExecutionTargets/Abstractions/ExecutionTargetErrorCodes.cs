// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Immutable;

namespace WinApp.Cli.ExecutionTargets.Abstractions;

/// <summary>
/// Stable failure codes for execution-target infrastructure (spec §"Failure model").
/// </summary>
/// <remarks>
/// These values are part of the public contract: once released a code's meaning never changes and
/// a code is never removed or renamed. <c>ExecutionTargetErrorCodeTests</c> pins the released set
/// so adding or renaming one is a deliberate, reviewed act rather than an accident.
/// <para>
/// Infrastructure codes are deliberately distinct from guest application exit codes so a caller can
/// always tell "winapp could not run your app" from "your app failed".
/// </para>
/// </remarks>
internal static class ExecutionTargetErrorCodes
{
    /// <summary>The host cannot run Sandbox at all: unsupported OS build, edition, or feature state.</summary>
    public const string Unsupported = "sandbox_unsupported";

    /// <summary>
    /// A Sandbox is running that winapp could neither prove it owns nor prepare for use. Never
    /// stopped.
    /// </summary>
    /// <remarks>
    /// <c>--on sandbox</c> adopts a running instance rather than refusing one, so this now reports the
    /// narrower case where adoption itself was not possible — several instances are listed, or the
    /// candidate could not be resolved or prepared.
    /// </remarks>
    public const string UnmanagedInstance = "sandbox_unmanaged_instance";

    /// <summary>Creating the managed Sandbox failed.</summary>
    public const string StartFailed = "sandbox_start_failed";

    /// <summary>No connected interactive Sandbox session, so real input and capture are unavailable.</summary>
    public const string NoInteractiveSession = "sandbox_no_interactive_session";

    /// <summary>Dynamic pre-input readiness checks failed; no input was reported as delivered.</summary>
    public const string InputNotReady = "sandbox_input_not_ready";

    /// <summary>The Sandbox went away underneath an active command.</summary>
    public const string Terminated = "sandbox_terminated";

    /// <summary>The guest agent's protocol version is not compatible with this host.</summary>
    public const string AgentIncompatible = "sandbox_agent_incompatible";

    /// <summary>Staging, self-testing, or activating a replacement guest agent failed.</summary>
    public const string AgentUpgradeFailed = "sandbox_agent_upgrade_failed";

    /// <summary>The guest agent is already serving as many channels or operations as it allows.</summary>
    public const string AgentBusy = "sandbox_agent_busy";

    /// <summary>The host/guest command channel could not be established or was lost.</summary>
    public const string TransportFailed = "sandbox_transport_failed";

    /// <summary>A file or artifact transfer stopped before completion. No destination is published.</summary>
    public const string TransferInterrupted = "sandbox_transfer_interrupted";

    /// <summary>A required shared runtime could not be provisioned in the guest.</summary>
    public const string RuntimeProvisionFailed = "sandbox_runtime_provision_failed";

    /// <summary>The deployment is dirty; it never launches or reports healthy until repaired.</summary>
    public const string DeploymentDirty = "sandbox_deployment_dirty";

    /// <summary>Another active deployment already owns this package identity.</summary>
    public const string PackageConflict = "sandbox_package_conflict";

    /// <summary>An inbox or provisioned guest package blocks development registration.</summary>
    public const string ProvisionedPackageConflict = "sandbox_provisioned_package_conflict";

    /// <summary>The command did not identify exactly one target and cannot guess one.</summary>
    public const string TargetAmbiguous = "sandbox_target_ambiguous";

    /// <summary>Persisted target state refers to an instance that no longer exists.</summary>
    public const string TargetStale = "sandbox_target_stale";

    /// <summary>A process ID or window handle from a previous epoch was supplied.</summary>
    public const string StaleHandle = "sandbox_stale_handle";

    /// <summary>Producing, verifying, or publishing a declared output artifact failed.</summary>
    public const string ArtifactFailed = "sandbox_artifact_failed";

    /// <summary>
    /// Enabling the Windows Sandbox optional feature needs elevation that was denied or unavailable.
    /// </summary>
    public const string SetupRequiresElevation = "sandbox_setup_requires_elevation";

    /// <summary>The optional feature was enabled and Windows requires a restart to finish.</summary>
    public const string SetupRequiresRestart = "sandbox_setup_requires_restart";

    /// <summary>Prerequisite setup failed outright: servicing error, policy, or an offline Store.</summary>
    public const string SetupFailed = "sandbox_setup_failed";

    /// <summary>
    /// Setup was still in progress when winapp stopped waiting. Retrying resumes it rather than
    /// starting it again.
    /// </summary>
    public const string SetupIncomplete = "sandbox_setup_incomplete";

    /// <summary>
    /// The value after <c>--on</c>, or the selector a <c>winapp target</c> verb was given, does not
    /// name a target this build can run against.
    /// </summary>
    /// <remarks>
    /// Target-neutral by design: it is raised before any provider is chosen, so it must not carry a
    /// provider's name. Every other code in this list describes something that went wrong once a
    /// specific provider was already selected.
    /// </remarks>
    public const string TargetInvalid = "target_invalid";

    /// <summary>
    /// A <c>winapp target</c> command line could not be parsed: an unknown option, a missing
    /// argument, or a value of the wrong type.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="TargetInvalid"/>, which is about the target a well-formed command
    /// line named. This one is raised before a handler runs at all, and exists so that
    /// <c>--json</c> callers get the same error envelope for a bad command line as for a failed
    /// one instead of human help text on stdout.
    /// </remarks>
    public const string TargetInvalidArguments = "target_invalid_arguments";

    /// <summary>
    /// Every released code, in the order the spec lists them. Used by the snapshot test and by
    /// diagnostics that need to present the full set.
    /// </summary>
    public static ImmutableArray<string> All { get; } =
    [
        Unsupported,
        UnmanagedInstance,
        StartFailed,
        NoInteractiveSession,
        InputNotReady,
        Terminated,
        AgentIncompatible,
        AgentUpgradeFailed,
        AgentBusy,
        TransportFailed,
        TransferInterrupted,
        RuntimeProvisionFailed,
        DeploymentDirty,
        PackageConflict,
        ProvisionedPackageConflict,
        TargetAmbiguous,
        TargetStale,
        StaleHandle,
        ArtifactFailed,
        SetupRequiresElevation,
        SetupRequiresRestart,
        SetupFailed,
        SetupIncomplete,
        TargetInvalid,
        TargetInvalidArguments,
    ];
}

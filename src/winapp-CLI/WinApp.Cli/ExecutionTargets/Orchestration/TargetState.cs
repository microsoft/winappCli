// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>
/// Persisted ownership record for one execution target (spec §"Ownership", §"Host coordination and
/// state").
/// </summary>
/// <remarks>
/// Only what winapp must know to prove ownership and fence stale work is stored here. Deployment
/// and package records live in their own files under the same target root so a deployment write
/// never has to rewrite — or risk corrupting — target ownership.
/// </remarks>
internal sealed record TargetState
{
    /// <summary>
    /// Schema version of this record. Migrations are monotonic; a host that reads a version it does
    /// not understand fails closed rather than guessing or overwriting a newer host's state.
    /// </summary>
    public required int SchemaVersion { get; init; }

    /// <summary>
    /// Monotonic revision, incremented on every commit. Enables optimistic concurrency so two host
    /// processes cannot silently clobber each other's view.
    /// </summary>
    public required long Revision { get; init; }

    /// <summary>Target family this record belongs to.</summary>
    public required string TargetKind { get; init; }

    /// <summary>Target identity this record belongs to.</summary>
    public required string TargetId { get; init; }

    /// <summary>
    /// Provider instance identity, for Windows Sandbox the exact instance ID. Null when no managed
    /// instance exists.
    /// </summary>
    public string? InstanceId { get; init; }

    /// <summary>
    /// Random nonce generated when this instance booted. Combined with <see cref="InstanceId"/> it
    /// produces the epoch, so a provider that reuses IDs still yields a fresh epoch per boot.
    /// </summary>
    public string? BootNonce { get; init; }

    /// <summary>
    /// Instance ID winapp assigned to a start it has not yet confirmed.
    /// </summary>
    /// <remarks>
    /// Written <em>before</em> the provider is asked to start anything, which is the whole point: a
    /// start that fails after creating an instance leaves no other evidence of which instance was
    /// winapp's. Cleared once ownership is recorded, and left in place across a crash so the next
    /// command reconciles that exact ID rather than inferring one from a list.
    /// </remarks>
    public string? PendingInstanceId { get; init; }

    /// <summary>When the unconfirmed start was requested, for diagnostics and staleness.</summary>
    public DateTimeOffset? PendingStartedUtc { get; init; }

    /// <summary>
    /// How winapp came to own the current instance: created, adopted, or recovered.
    /// </summary>
    /// <remarks>
    /// Recorded because it changes what a later command may assume. Only an instance winapp itself
    /// bootstrapped has connection material on disk that a warm reconnect can use; one that was
    /// adopted or recovered has to be bootstrapped before it can be talked to.
    /// </remarks>
    public string? InstanceOrigin { get; init; }

    /// <summary>
    /// The epoch whose guest bootstrap ran all the way to an authenticated agent connection.
    /// </summary>
    /// <remarks>
    /// Ownership is recorded before the guest is prepared, so "recorded and still listed" does not
    /// mean "ready to talk to". This is the separate, later fact: a command killed between claiming
    /// an instance and finishing its first bootstrap leaves one with no connected client, no
    /// Developer Mode, and no agent, and only this marker tells the next command to bootstrap it
    /// rather than reconnect to something that was never there.
    /// </remarks>
    public string? BootstrappedEpoch { get; init; }

    /// <summary>Version of the guest agent last known to be installed.</summary>
    public string? AgentVersion { get; init; }

    /// <summary>Hash of the guest agent binary last known to be installed.</summary>
    public string? AgentBinaryHash { get; init; }

    /// <summary>
    /// IP address the managed instance last answered on.
    /// </summary>
    /// <remarks>
    /// A cache, not a source of truth: it saves a <c>wsb</c> query when the next command reconnects
    /// to an agent that is already running, and is ignored whenever it does not belong to the
    /// current <see cref="InstanceId"/>. Nothing secret lives here — the pre-shared key stays in the
    /// bootstrap material, which the guest already has to be able to read.
    /// </remarks>
    public string? GuestAddress { get; init; }

    /// <summary>
    /// Host window handle of the Sandbox client winapp recorded connecting, or null.
    /// </summary>
    /// <remarks>
    /// Recorded so a later winapp process can capture the desktop this target renders into without
    /// having to guess. A handle alone would not be safe to trust across processes — Windows recycles
    /// them — so it is only ever used together with <see cref="ClientProcessId"/> and
    /// <see cref="ClientProcessStartTicksUtc"/>, and only when that exact triple is still one of the
    /// client windows open right now.
    /// </remarks>
    public long? ClientWindowHandle { get; init; }

    /// <summary>Host process that owns <see cref="ClientWindowHandle"/>, or null.</summary>
    public int? ClientProcessId { get; init; }

    /// <summary>
    /// UTC ticks that client process started, 0 when Windows would not report it, or null when
    /// there is no recorded client.
    /// </summary>
    public long? ClientProcessStartTicksUtc { get; init; }

    /// <summary>
    /// Whether the client identity was proven through the exact <c>wsb connect</c> launcher winapp
    /// created. False for legacy records and adopted/manual clients.
    /// </summary>
    public bool ClientOwnedByWinapp { get; init; }

    /// <summary>UTC timestamp of the last commit, for diagnostics only.</summary>
    public DateTimeOffset? UpdatedUtc { get; init; }
}

/// <summary>Source-generated serializer context for persisted target state.</summary>
[JsonSerializable(typeof(TargetState))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class TargetStateJsonContext : JsonSerializerContext
{
}

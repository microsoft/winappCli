// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.InteractiveDesktop;

/// <summary>
/// Stable error codes for coordination failures. These appear in the <c>--json</c> error envelope,
/// alongside the command-level codes in <c>UiJsonError</c>, and are declared here only — a duplicate
/// set of constants drifted from these once already and shipped a code no code path emitted.
/// </summary>
internal static class UiCoordinationErrorCodes
{
    /// <summary><c>WINAPP_UI_WORKFLOW_ID</c> was set but empty/whitespace or longer than 256 UTF-16 units.</summary>
    public const string InvalidWorkflowId = "invalid_ui_workflow_id";

    /// <summary>
    /// Coordination state could not be read, published, or safely recovered — for example an unknown
    /// newer schema version, or corrupt state while a live participant may exist. Turn-participating
    /// and mutating commands fail closed rather than acting uncoordinated.
    /// </summary>
    public const string Unavailable = "desktop_coordination_unavailable";

    /// <summary>64 live global waiters already queued after pruning dead entries (spec §8).</summary>
    public const string QueueCapacityExceeded = "queue_capacity_exceeded";

    /// <summary>The command was cancelled while queued and never reached execution.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>
    /// <c>ui yield</c> found the workflow's turn still busy: a command of this same workflow is running
    /// or queued under it, so the turn is not idle and there is nothing safe to release.
    /// </summary>
    public const string TurnBusy = "ui_turn_busy";
}

/// <summary>
/// Raised when coordination cannot proceed safely. Carries the stable error code so command handlers
/// emit the right envelope without string matching.
/// </summary>
internal sealed class UiCoordinationException(string code, string message, string? recoveryHint = null)
    : Exception(message)
{
    /// <summary>One of <see cref="UiCoordinationErrorCodes"/>.</summary>
    public string Code { get; } = code;

    /// <summary>Optional actionable next step surfaced alongside the error.</summary>
    public string? RecoveryHint { get; } = recoveryHint;
}

/// <summary>
/// What happened to a command's turn, attached to command-completion telemetry in privacy-minimized
/// bucketed form (spec §16) and used by <c>--verbose</c> waiting output.
/// </summary>
internal enum UiTurnAction
{
    /// <summary>The command started a new turn because no other workflow held or wanted the desktop.</summary>
    New,

    /// <summary>The command joined a turn its owner already held.</summary>
    Continuation,

    /// <summary>The command waited in the global queue behind another owner before acquiring the turn.</summary>
    Queued,

    /// <summary>
    /// The command claimed the turn without queueing because another owner's idle grace had already
    /// expired when it registered (spec §10.7). Distinct from <see cref="New"/>, where the desktop was
    /// genuinely unowned, and from <see cref="Queued"/>, where the command had to wait its turn.
    /// </summary>
    HandoffAfterIdle,

    /// <summary>A non-owner observation that ran concurrently without claiming the turn.</summary>
    Detached,
}

/// <summary>How a coordinated command finished, for telemetry (spec §16).</summary>
internal enum UiCoordinationOutcome
{
    /// <summary>The command reached execution and returned, including with a non-zero exit code.</summary>
    Completed,

    /// <summary>The command was cancelled while queued and never executed.</summary>
    Cancelled,

    /// <summary>Coordination failed closed (unavailable, queue capacity, invalid workflow id).</summary>
    CoordinationFailure,

    /// <summary>Corrupt state was safely quarantined and rebuilt before the command proceeded.</summary>
    CorruptionRecovery,
}

/// <summary>
/// Privacy-minimized summary of one command's coordination, attached to existing command-completion
/// telemetry. Contains no owner ids or hashes, no PIDs, no process/app/window/selector text, no queue
/// entries, no command arguments and no state-file contents (spec §16).
/// </summary>
internal sealed record UiCoordinationSummary(
    UiOwnerKind IdentitySource,
    UiTurnMode Mode,
    UiTurnAction TurnAction,
    UiCoordinationOutcome Outcome,
    long WaitedMs,
    int QueueDepth,
    long TurnAgeMs)
{
    /// <summary>
    /// Coarse wait bucket. Exact durations could correlate a user's workflow timing across events, so
    /// only the bucket is reported.
    /// </summary>
    public string WaitBucket => Bucket(WaitedMs, [0, 100, 1_000, 5_000, 30_000, 120_000]);

    /// <summary>Coarse queue-depth bucket.</summary>
    public string QueueDepthBucket => Bucket(QueueDepth, [0, 1, 2, 4, 8, 16]);

    /// <summary>
    /// Coarse turn-age bucket: how long the owning workflow had held the desktop when this command
    /// finished, spanning every command in that turn. Distinct from <see cref="WaitBucket"/>, which
    /// covers only this command's own queue wait.
    /// </summary>
    public string TurnAgeBucket => Bucket(TurnAgeMs, [0, 1_000, 5_000, 30_000, 120_000, 600_000]);

    private static string Bucket(long value, long[] edges)
    {
        for (var i = edges.Length - 1; i >= 0; i--)
        {
            if (value >= edges[i])
            {
                return i == edges.Length - 1
                    ? $"{edges[i]}+"
                    : $"{edges[i]}-{edges[i + 1] - 1}";
            }
        }

        return "0";
    }
}

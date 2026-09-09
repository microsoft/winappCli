// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.InteractiveDesktop;

/// <summary>
/// How a <c>winapp ui</c> command participates in cooperative desktop turns (issue #764).
/// </summary>
/// <remarks>
/// Windows exposes a single foreground window, keyboard focus, cursor and <c>SendInput</c> stream, so
/// concurrent <c>winapp.exe</c> processes can dismiss each other's transient UI even when they target
/// different apps. Every UI command declares one of these modes; the coordinator uses it to decide
/// whether the command claims the workflow turn, waits behind a forward barrier, or runs concurrently.
/// </remarks>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<UiTurnMode>))]
internal enum UiTurnMode
{
    /// <summary>
    /// Reads the UI without changing it. Does not claim a free turn: a non-owner runs immediately and
    /// detached (no lease, no queue entry); the current owner registers so the observation pins and
    /// renews that owner's turn while it reads transient UI.
    /// </summary>
    Observe,

    /// <summary>
    /// Claims or waits for the workflow turn without taking <c>active.lock</c>. Several same-owner
    /// <c>TurnShared</c> commands may overlap unless an earlier <see cref="DesktopExclusive"/> forward
    /// barrier is waiting or running.
    /// </summary>
    /// <remarks>
    /// This is the mode for work that changes the app but not the desktop: <c>ui record</c>, which pins
    /// the owner for the whole capture while same-owner input continues, and the background-safe UIA
    /// mutations <c>set-value</c>, <c>scroll-into-view</c> and <c>scroll --direction</c>/<c>--to</c>.
    /// Because they never take <c>active.lock</c> they remain usable on a locked or headless session,
    /// but they still wait behind a foreign workflow's turn rather than editing or scrolling content out
    /// from under somebody else's click.
    /// </remarks>
    TurnShared,

    /// <summary>
    /// Claims or waits for the workflow turn, creates a forward barrier in the owner's command stream,
    /// and takes <c>active.lock</c> for its desktop-sensitive section (foreground, focus, cursor,
    /// <c>SendInput</c>, synthetic pointer input, restore, live-screen capture).
    /// </summary>
    DesktopExclusive,
}

/// <summary>How the logical workflow owner behind a command was resolved.</summary>
/// <remarks>
/// The two values are the two tiers. Arbitration applies to both; only <see cref="Workflow"/> carries
/// continuity across invocations.
/// </remarks>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<UiOwnerKind>))]
internal enum UiOwnerKind
{
    /// <summary>
    /// Resolved from <c>WINAPP_UI_WORKFLOW_ID</c>. Groups cooperating processes explicitly and earns a
    /// post-command idle grace so a burst of commands reads as one workflow.
    /// </summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("workflow")]
    Workflow,

    /// <summary>
    /// A unique one-command owner minted when no workflow id is set. It queues and arbitrates like any
    /// other owner, but receives no post-command idle grace and hands the desktop off the moment it
    /// completes.
    /// </summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("anonymous")]
    Anonymous,
}

/// <summary>Whether a registered owner command is waiting behind the barrier or currently executing.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<UiCommandStatus>))]
internal enum UiCommandStatus
{
    /// <summary>Admitted with an arrival ticket but blocked by an earlier <see cref="UiTurnMode.DesktopExclusive"/> command.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("waiting")]
    Waiting,

    /// <summary>Eligible and executing. Counts as owner activity, so it blocks handoff to another owner.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("running")]
    Running,
}

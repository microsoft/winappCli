// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;

namespace WinApp.Cli.Services.InteractiveDesktop;

/// <summary>What this command is waiting behind, from the waiter's point of view.</summary>
internal enum UiWaitReason
{
    /// <summary>Another workflow holds the turn and has a command running or queued under it.</summary>
    OtherWorkflowActive,

    /// <summary>Another workflow holds the turn but is idle inside its post-command grace.</summary>
    OtherWorkflowGrace,

    /// <summary>An earlier command of this command's own workflow must finish first.</summary>
    OwnWorkflow,

    /// <summary>Nobody holds the turn yet, but commands from other workflows queued earlier.</summary>
    Queued,
}

/// <summary>
/// A snapshot of why this command is waiting, read under <c>state.lock</c> and rendered outside it.
/// </summary>
/// <param name="QueueDepth">Live global waiters, including this command when it is queued globally.</param>
/// <param name="CommandsAhead">How many commands must finish before this one becomes eligible.</param>
/// <param name="ActiveProcessId">PID of a <c>winapp</c> process currently holding the turn, when any.</param>
/// <param name="ActiveOperation">
/// The command name holding things up, e.g. <c>ui record</c>. Never arguments, and never anything that
/// identifies the other workflow.
/// </param>
/// <param name="Reason">What this command is waiting behind.</param>
/// <param name="HeldForMs">How long the current turn has been held, when known.</param>
/// <param name="GraceRemainingMs">Time left in the holder's idle grace, for <see cref="UiWaitReason.OtherWorkflowGrace"/>.</param>
/// <param name="WaitersAhead">Other workflows' commands queued ahead of this one.</param>
internal readonly record struct UiWaitDiagnostics(
    int QueueDepth,
    int CommandsAhead,
    int? ActiveProcessId,
    string? ActiveOperation,
    UiWaitReason Reason = UiWaitReason.Queued,
    long? HeldForMs = null,
    long? GraceRemainingMs = null,
    int WaitersAhead = 0);

/// <summary>
/// Renders the "still waiting for the desktop" notice to stderr (spec §14). Nothing is written for the
/// first second, because the overwhelmingly common case — a tight script burst where the previous
/// command has just finished — clears well inside that window and a flash of status would be noise.
/// </summary>
/// <remarks>
/// The notice explains what holds the desktop and for how long, and how to run alongside it, without
/// revealing the other workflow's identity: no workflow id, owner key, PID or arguments outside
/// <c>--verbose</c>, and only the command name in the default text.
/// </remarks>
internal sealed class UiCoordinationWaitReporter(
    TextWriter writer,
    UiCoordinationOutputMode outputMode,
    string operation)
{
    /// <summary>Delay before the first status line.</summary>
    internal const int FirstReportAfterMs = 1_000;

    /// <summary>Minimum gap between subsequent status lines, so a long wait does not spam the console.</summary>
    internal const int RepeatIntervalMs = 5_000;

    private long _lastReportedAtMs = -1;

    /// <summary>
    /// Whether <see cref="ReportIfDue"/> would print at <paramref name="elapsedMs"/>.
    /// </summary>
    /// <remarks>
    /// Lets a caller skip building diagnostics — which walks the whole queue — on the iterations where
    /// nothing would be shown. At the old poll rate that waste was invisible; woken only on demand, it
    /// would be most of the work a waiter does.
    /// </remarks>
    public bool IsReportDue(long elapsedMs)
    {
        if (!outputMode.AllowsWaitingStatus || elapsedMs < FirstReportAfterMs)
        {
            return false;
        }

        return _lastReportedAtMs < 0 || elapsedMs - _lastReportedAtMs >= RepeatIntervalMs;
    }

    /// <summary>
    /// Writes a waiting notice when one is due. Silent under <c>--json</c> and <c>--quiet</c>, and
    /// silent for the first <see cref="FirstReportAfterMs"/> milliseconds in every mode.
    /// </summary>
    public void ReportIfDue(long elapsedMs, UiWaitDiagnostics diagnostics)
    {
        if (!IsReportDue(elapsedMs))
        {
            return;
        }

        _lastReportedAtMs = elapsedMs;
        var line = BuildLine(elapsedMs, diagnostics);
        if (outputMode.Verbose)
        {
            line += " " + BuildVerboseDetails(diagnostics);
        }

        writer.WriteLine(line);
    }

    internal string BuildLine(long elapsedMs, UiWaitDiagnostics diagnostics)
    {
        var waited = FormatDuration(elapsedMs);
        var holder = string.IsNullOrEmpty(diagnostics.ActiveOperation)
            ? null
            : $"'{diagnostics.ActiveOperation}'";

        string reason;
        string? advice = null;
        switch (diagnostics.Reason)
        {
            case UiWaitReason.OwnWorkflow:
                reason = holder is null
                    ? "an earlier command in this workflow must finish first."
                    : $"an earlier {holder} in this workflow must finish first.";
                break;

            case UiWaitReason.OtherWorkflowActive:
                reason = "another workflow is using the desktop"
                    + (holder is null ? "" : $" ({holder})")
                    + (diagnostics.HeldForMs is { } held ? $" and has held it for {FormatDuration(held)}" : "")
                    + ".";
                advice = SharingAdvice;
                break;

            case UiWaitReason.OtherWorkflowGrace:
                reason = "another workflow just finished a command and keeps the desktop"
                    + (diagnostics.GraceRemainingMs is { } left
                        ? $" for up to {FormatDuration(Math.Max(left, 1_000))} more"
                        : " briefly")
                    + " in case it continues.";
                break;

            default:
                reason = "the desktop is being handed to another workflow.";
                break;
        }

        if (diagnostics.Reason != UiWaitReason.OwnWorkflow && diagnostics.WaitersAhead > 0)
        {
            reason += diagnostics.WaitersAhead == 1
                ? " 1 command from another workflow is queued ahead of this one."
                : $" {diagnostics.WaitersAhead} commands from other workflows are queued ahead of this one.";
        }

        return $"'{operation}' has waited {waited} for the desktop: {reason}"
            + (advice is null ? "" : " " + advice)
            + " Press Ctrl+C to cancel.";
    }

    /// <summary>How to run alongside the holder instead of behind it.</summary>
    internal const string SharingAdvice =
        "If both commands belong to the same task (for example, a recording and the input it captures), "
        + "run them with the same WINAPP_UI_WORKFLOW_ID.";

    private static string BuildVerboseDetails(UiWaitDiagnostics diagnostics)
    {
        var active = diagnostics.ActiveProcessId is { } activePid
            ? $"held by winapp PID {activePid}"
            : "no active winapp command";
        return $"[{active}; queue depth {diagnostics.QueueDepth}, {diagnostics.CommandsAhead} ahead]";
    }

    /// <summary>Formats a duration as <c>3s</c>, <c>2m 14s</c> or <c>1h 5m</c>.</summary>
    internal static string FormatDuration(long ms)
    {
        var total = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        if (total.TotalHours >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)total.TotalHours}h {total.Minutes}m");
        }

        if (total.TotalMinutes >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{total.Minutes}m {total.Seconds}s");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{total.Seconds}s");
    }
}

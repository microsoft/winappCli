// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using Spectre.Console;

namespace WinApp.Cli.Services.InteractiveDesktop;

/// <summary>
/// A snapshot of why this command is waiting, read under <c>state.lock</c> and rendered outside it.
/// </summary>
/// <param name="QueueDepth">Live global waiters, including this command when it is queued globally.</param>
/// <param name="CommandsAhead">How many commands must finish before this one becomes eligible.</param>
/// <param name="ActiveProcessId">PID of a <c>winapp</c> process currently holding the turn, when any.</param>
/// <param name="ActiveOperation">That process's command name, e.g. <c>ui record</c>.</param>
internal readonly record struct UiWaitDiagnostics(
    int QueueDepth,
    int CommandsAhead,
    int? ActiveProcessId,
    string? ActiveOperation);

/// <summary>
/// Renders the "still waiting for the desktop" status (spec §14). Nothing is written for the first
/// second, because the overwhelmingly common case — a tight script burst where the previous command
/// has just finished — clears well inside that window and a flash of status would be noise.
/// </summary>
internal sealed class UiCoordinationWaitReporter(
    IAnsiConsole console,
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
    /// Writes a waiting status when one is due. Silent under <c>--json</c> and <c>--quiet</c>, and
    /// silent for the first <see cref="FirstReportAfterMs"/> milliseconds in every mode.
    /// </summary>
    public void ReportIfDue(long elapsedMs, UiWaitDiagnostics diagnostics)
    {
        if (!outputMode.AllowsWaitingStatus || elapsedMs < FirstReportAfterMs)
        {
            return;
        }

        if (_lastReportedAtMs >= 0 && elapsedMs - _lastReportedAtMs < RepeatIntervalMs)
        {
            return;
        }

        _lastReportedAtMs = elapsedMs;
        console.MarkupLine(outputMode.Verbose
            ? BuildVerboseLine(elapsedMs, diagnostics)
            : "[grey]Waiting for the desktop — another winapp ui workflow is using it. Press Ctrl+C to cancel.[/]");
    }

    private string BuildVerboseLine(long elapsedMs, UiWaitDiagnostics diagnostics)
    {
        var seconds = (elapsedMs / 1000.0).ToString("F1", CultureInfo.InvariantCulture);
        var active = diagnostics.ActiveProcessId is { } activePid
            ? $"held by winapp PID {activePid}" +
              (string.IsNullOrEmpty(diagnostics.ActiveOperation)
                  ? ""
                  : $" running {Markup.Escape(diagnostics.ActiveOperation)}")
            : "no active winapp command";

        return "[grey]Waiting for the desktop for " + seconds + "s — " +
               Markup.Escape(operation) + "; " + active + "; " +
               $"queue depth {diagnostics.QueueDepth}, {diagnostics.CommandsAhead} ahead" +
               ". Press Ctrl+C to cancel.[/]";
    }
}

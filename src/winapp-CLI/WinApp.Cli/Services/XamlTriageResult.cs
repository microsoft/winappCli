// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>Outcome of a WinUI triage pass.</summary>
internal enum XamlTriageOutcome
{
    /// <summary>Triage was not run or produced nothing to record (e.g. the pass threw and failed open).</summary>
    None,

    /// <summary>
    /// Triage tooling was unavailable or the pass could not produce a breakdown; an explanatory note
    /// is recorded in the log, but no actual triage output was produced.
    /// </summary>
    Skipped,

    /// <summary>Triage produced a stowed-exception / dispatch-chain breakdown that was written to the log.</summary>
    Succeeded,

    /// <summary>
    /// The triage pass stopped early (for example, the debugger extension crashed), but the output it
    /// produced before stopping was written to the log.
    /// </summary>
    Partial,
}

/// <summary>
/// Structured result of <see cref="IXamlTriageService.TryAnalyzeAsync"/>. Distinguishes a real triage
/// breakdown from a graceful skip so the console can surface an accurate verdict instead of always
/// claiming triage was "written to the debug log".
/// </summary>
/// <param name="Outcome">Whether triage succeeded, was skipped, or produced nothing.</param>
/// <param name="LogText">
/// Text to append to the debug log (the full breakdown for <see cref="XamlTriageOutcome.Succeeded"/>,
/// or the skip explanation for <see cref="XamlTriageOutcome.Skipped"/>). <c>null</c> for
/// <see cref="XamlTriageOutcome.None"/>.
/// </param>
/// <param name="Verdict">
/// Optional one-line headline (e.g. error code + message) surfaced in the console on success. <c>null</c>
/// when no concise verdict could be extracted.
/// </param>
/// <param name="Reason">
/// Optional short reason the pass was skipped or stopped early, shown in the console. <c>null</c> when
/// the log text alone explains it.
/// </param>
internal sealed record XamlTriageResult(XamlTriageOutcome Outcome, string? LogText, string? Verdict, string? Reason = null)
{
    /// <summary>A successful triage breakdown with optional one-line verdict for the console.</summary>
    public static XamlTriageResult Succeeded(string logText, string? verdict) =>
        new(XamlTriageOutcome.Succeeded, logText, verdict);

    /// <summary>Output from a triage pass that stopped early, with the reason it stopped.</summary>
    public static XamlTriageResult Partial(string logText, string? verdict, string reason) =>
        new(XamlTriageOutcome.Partial, logText, verdict, reason);

    /// <summary>A graceful skip whose explanatory note is still recorded in the log.</summary>
    public static XamlTriageResult Skipped(string logText, string? reason = null) =>
        new(XamlTriageOutcome.Skipped, logText, null, reason);

    /// <summary>Nothing to record (triage not applicable or failed open).</summary>
    public static XamlTriageResult None { get; } = new(XamlTriageOutcome.None, null, null);
}

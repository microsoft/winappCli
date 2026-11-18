// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Produces WinUI-specific crash triage for a minidump by hosting DbgEng and running
/// the WinUI team's WinDbg JavaScript extension (<c>!xamlstowed</c> / <c>!xamltriage</c>).
/// <para>
/// Most WinUI crashes originate inside a XAML event handler and surface as a stowed
/// exception (<c>0xC000027B</c>) wrapping a managed exception. The standard ClrMD/DbgEng
/// passes only reliably recover the faulting user frame; this service recovers the
/// originating HRESULT, its ErrorContext chain, and the native dispatch stack
/// (Microsoft.UI.Xaml → CXcpDispatcher → CoreMessagingXP → CLR host).
/// </para>
/// </summary>
internal interface IXamlTriageService
{
    /// <summary>
    /// Runs the WinUI triage pass against a dump and returns a structured result: a real
    /// stowed-exception breakdown, partial output from a pass that stopped early, a graceful skip
    /// (with an explanatory note for the log), or nothing when triage was not applicable.
    /// </summary>
    /// <remarks>
    /// This method never throws for missing tooling or unsupported scenarios — it
    /// degrades gracefully, logging diagnostics and returning a
    /// <see cref="XamlTriageOutcome.Skipped"/> note or <see cref="XamlTriageResult.None"/>.
    /// </remarks>
    /// <param name="dumpPath">Path to the minidump file (must contain Microsoft.UI.Xaml).</param>
    /// <param name="useSymbols">
    /// When true, symbol resolution against the Microsoft Symbol Server is enabled so the
    /// full native dispatch chain resolves to function names. First run downloads symbols.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="XamlTriageResult"/> describing the outcome.</returns>
    Task<XamlTriageResult> TryAnalyzeAsync(string dumpPath, bool useSymbols, CancellationToken cancellationToken = default);
}

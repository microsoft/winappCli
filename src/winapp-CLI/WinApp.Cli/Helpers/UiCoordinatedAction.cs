// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Base for every <c>winapp ui</c> command handler, splitting invocation into two phases so cooperative
/// desktop coordination can never be entered by a command that was going to fail anyway.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Preflight"/> runs first and performs only local validation — syntax, types, ranges,
/// required options, path shape. It must not resolve a session, touch UI Automation, or contact the
/// target app. A malformed command therefore never opens a participant lease, takes an arrival ticket,
/// or joins an indefinite queue (spec §10).
/// </para>
/// <para>
/// <see cref="ExecuteAsync"/> then runs under the workflow turn. The turn and the forward barrier wrap
/// the whole body, but <c>active.lock</c> does not: the body takes it only around the moment it touches
/// the shared desktop, via <see cref="IDesktopSection.EnterAsync"/>. That keeps output formatting, PNG
/// encoding, file publication and logging outside the exclusive section.
/// </para>
/// </remarks>
internal abstract class UiCoordinatedAction(IInteractiveDesktopLock coordinator, ILogger logger)
    : AsynchronousCommandLineAction
{
    /// <summary>Command name used for local diagnostics, e.g. <c>ui click</c>. Never includes arguments.</summary>
    protected abstract string Operation { get; }

    /// <summary>
    /// Local-only validation. Return <see langword="null"/> to continue, or an exit code after emitting
    /// the appropriate human and <c>--json</c> error. Must not contact the target app.
    /// </summary>
    protected abstract int? Preflight(ParseResult parseResult);

    /// <summary>
    /// The command's coordination mode, resolved after <see cref="Preflight"/> so it may read
    /// already-validated options such as <c>--wheel</c> or <c>--capture-screen</c>.
    /// </summary>
    protected abstract UiTurnMode ResolveMode(ParseResult parseResult);

    /// <summary>The command's work. Runs under the workflow turn.</summary>
    protected abstract Task<int> ExecuteAsync(ParseResult parseResult, IUiTurn turn, CancellationToken cancellationToken);

    /// <summary>
    /// Whether <paramref name="ex"/> belongs to coordination and must escape a handler's catch-all.
    /// </summary>
    /// <remarks>
    /// Handler bodies call into coordination — <see cref="IDesktopSection.EnterAsync"/> — from inside
    /// their broad
    /// <c>catch (Exception)</c>. Letting that catch win would be doubly wrong: the user would see
    /// <c>internal_error</c> instead of <c>cancelled</c> or the real coordination code, and the
    /// coordinator would see a normal body completion and renew the owner's idle grace on a command
    /// that never actually ran. Handlers therefore filter their catch-all with
    /// <c>when (!UiCoordinatedAction.IsCoordinationFault(ex))</c>.
    /// </remarks>
    internal static bool IsCoordinationFault(Exception ex)
        => ex is OperationCanceledException or UiCoordinationException;

    public sealed override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
    {
        if (Preflight(parseResult) is { } preflightExitCode)
        {
            return preflightExitCode;
        }

        try
        {
            return await coordinator.RunCoordinatedAsync(
                ResolveMode(parseResult),
                Operation,
                parseResult,
                (turn, token) => ExecuteAsync(parseResult, turn, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (UiCoordinationException ex)
        {
            var json = UiCoordinationOutputMode.FromParseResult(parseResult).Json;
            logger.LogError("{Symbol} {Message}", UiSymbols.Error, ex.Message);
            if (ex.RecoveryHint is { } hint)
            {
                logger.LogError("{Symbol} {Hint}", UiSymbols.Error, hint);
            }

            UiJsonError.Emit(
                json,
                ex.Code,
                ex.Message,
                errorOut: parseResult.InvocationConfiguration.Error,
                recoveryHint: ex.RecoveryHint);
            return 1;
        }
    }
}

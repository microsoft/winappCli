// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using WinApp.Cli.Commands;

namespace WinApp.Cli.Services.InteractiveDesktop;

/// <summary>
/// The output mode a coordinated command is running in, resolved once from the parse result.
/// </summary>
/// <param name="Json">
/// <c>--json</c> is in effect: stay silent until the final result or a structured error, so a consumer
/// parsing stdout never has to skip progress lines.
/// </param>
/// <param name="Verbose">
/// <c>--verbose</c> is in effect: include local diagnostics (the active <c>winapp</c> PID, its
/// operation, queue depth, commands ahead, elapsed wait).
/// </param>
/// <param name="Quiet"><c>--quiet</c> is in effect: emit nothing while waiting.</param>
internal readonly record struct UiCoordinationOutputMode(bool Json, bool Verbose, bool Quiet)
{
    /// <summary>
    /// Reads the global output options from the selected command, guarding against commands that do not
    /// declare them. <c>Options.Contains</c> matters because <c>ParseResult.GetValue</c> for an option
    /// the selected command does not own is not meaningful.
    /// </summary>
    public static UiCoordinationOutputMode FromParseResult(ParseResult parseResult) => new(
        Json: TryGetFlag(parseResult, WinAppRootCommand.JsonOption),
        Verbose: TryGetFlag(parseResult, WinAppRootCommand.VerboseOption),
        Quiet: TryGetFlag(parseResult, WinAppRootCommand.QuietOption));

    private static bool TryGetFlag(ParseResult parseResult, Option<bool> option)
    {
        if (!parseResult.CommandResult.Command.Options.Contains(option))
        {
            return false;
        }

        try
        {
            return parseResult.GetValue(option);
        }
        catch (InvalidOperationException)
        {
            // A value that cannot be read must never break coordination; fall back to "not set".
            return false;
        }
    }

    /// <summary>Whether any human-facing waiting status may be written.</summary>
    public bool AllowsWaitingStatus => !Json && !Quiet;
}

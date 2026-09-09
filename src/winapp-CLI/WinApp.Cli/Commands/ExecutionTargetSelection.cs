// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.Commands;

/// <summary>
/// Marks a command tree that can run somewhere other than this machine.
/// </summary>
/// <remarks>
/// Implemented by the root of each target-aware tree (<c>run</c>, <c>unregister</c>, <c>ui</c>).
/// Descendants inherit it by being reached through that root, so a new <c>ui</c> verb is target-aware
/// automatically instead of having to remember a flag.
/// </remarks>
internal interface ITargetAwareCommand;

/// <summary>
/// The shared <c>--on</c> selector, and the fail-closed rules that make it safe.
/// </summary>
/// <remarks>
/// <para>
/// <c>--on</c> is registered recursively on the <em>root</em>, not on the target-aware commands. That
/// matters for safety rather than convenience: System.CommandLine binds an unrecognised token to a
/// nearby optional positional argument rather than failing, so a command that did not declare
/// <c>--on</c> would happily swallow <c>--on sandbox</c> into its <c>selector</c> argument and then
/// run the command on this desktop, reporting success. Parsing the token everywhere and rejecting it
/// where it is meaningless removes that failure mode: a command either honours <c>--on</c> or says it
/// cannot.
/// </para>
/// <para>
/// There is deliberately no short alias. <c>-o</c> is already <c>--output</c>, and <c>--target</c>
/// already means the UI element a verb acts on.
/// </para>
/// </remarks>
internal static class ExecutionTargetSelection
{
    /// <summary>Selects where a target-aware command runs.</summary>
    public static Option<string?> OnOption { get; } = new("--on")
    {
        Description =
            "Run this command on the named execution target instead of this machine. " +
            "Supported: 'sandbox' (the Windows Sandbox winapp manages) and 'local' (the default). " +
            "There is no fallback: if the target cannot be prepared, the command fails rather than " +
            "running here.",
        Recursive = true,
    };

    /// <summary>The selector as typed, or null when <c>--on</c> was not supplied.</summary>
    public static string? RawSelector(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        if (parseResult.GetResult(OnOption) is not { Implicit: false } result)
        {
            return null;
        }

        try
        {
            return result.GetValueOrDefault<string?>();
        }
        catch (InvalidOperationException)
        {
            // The option was written without its value. System.CommandLine records that as a parse
            // error and refuses to bind, and its own error path reports it; treating it as "not
            // supplied" here keeps this method total rather than making every caller guard.
            return null;
        }
    }

    /// <summary>Whether the user supplied <c>--on</c> at all.</summary>
    public static bool WasSupplied(ParseResult parseResult) =>
        parseResult.GetResult(OnOption) is { Implicit: false };

    /// <summary>Whether the selected command can run anywhere but this machine.</summary>
    public static bool IsTargetAware(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        for (Command? command = parseResult.CommandResult.Command; command is not null;
             command = command.Parents.OfType<Command>().FirstOrDefault())
        {
            if (command is ITargetAwareCommand)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves the target this invocation runs on, defaulting to this machine.
    /// </summary>
    /// <remarks>
    /// Only ever called after <see cref="Validate"/> has accepted the command line, so an invalid
    /// selector can never reach a handler and fall back to local execution.
    /// </remarks>
    /// <exception cref="ExecutionTargetException">The selector does not name a usable target.</exception>
    public static ExecutionTargetRef Resolve(ParseResult parseResult)
    {
        var selector = RawSelector(parseResult);

        return selector is null
            ? ExecutionTargetRef.Local
            : ExecutionTargetSelector.Parse(selector);
    }

    /// <summary>
    /// Checks everything about target selection that must fail before dispatch.
    /// </summary>
    /// <remarks>
    /// Returns the failure rather than throwing so the caller can render it in whichever shape the
    /// invoked command promises.
    /// </remarks>
    /// <returns>The failure, or null when the command line is acceptable.</returns>
    public static ExecutionTargetErrorInfo? Validate(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        // A command line the parser already rejected — `--on` with no value, for example — is
        // reported by System.CommandLine's own error path, which does not dispatch. Reading a value
        // out of a result that failed to bind would only turn a clear parse error into an opaque
        // one.
        if (parseResult.Errors.Count > 0)
        {
            return null;
        }

        if (!WasSupplied(parseResult))
        {
            return null;
        }

        if (!IsTargetAware(parseResult))
        {
            // Explicitly refused rather than ignored. Silently dropping it would run the command
            // here after the user asked for somewhere else, which is the one outcome '--on' exists
            // to prevent.
            var name = DescribeCommand(parseResult);

            return ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TargetInvalid,
                $"'winapp {name}' always runs on this machine, so it does not accept --on.",
                userAction:
                    "Remove --on. To run an app somewhere else, use 'winapp run', 'winapp ui', or " +
                    "'winapp unregister'; to run an arbitrary command there, use 'winapp target exec'.",
                example: "winapp run . --on sandbox",
                context: new Dictionary<string, string> { ["command"] = name }).Error;
        }

        return ExecutionTargetSelector.TryParse(RawSelector(parseResult), out _, out var error)
            ? null
            : error;
    }

    /// <summary>The invoked command's path under <c>winapp</c>, for an error message.</summary>
    private static string DescribeCommand(ParseResult parseResult)
    {
        var names = new List<string>();

        for (Command? command = parseResult.CommandResult.Command; command is not null;
             command = command.Parents.OfType<Command>().FirstOrDefault())
        {
            if (command is RootCommand)
            {
                break;
            }

            names.Insert(0, command.Name);
        }

        return names.Count == 0 ? "(root)" : string.Join(' ', names);
    }
}

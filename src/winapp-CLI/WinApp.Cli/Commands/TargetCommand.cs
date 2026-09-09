// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Commands;

/// <summary>
/// The <c>winapp target</c> namespace: generic escape hatches for agents and scripts.
/// </summary>
/// <remarks>
/// Deliberately limited to <c>exec</c>, <c>push</c>, and <c>pull</c>. Lifecycle, images, snapshots,
/// ports, providers, shells, and package-manager verbs are not here — a target's lifecycle stays
/// with that target's own tooling, and every additional verb would be a second way to do something
/// winapp already does through its ordinary commands.
/// <para>
/// Each verb takes the target as its first argument rather than as <c>--on</c>. These verbs exist
/// only to act on a target, so there is no default worth having and nothing to select against on
/// this machine: <c>winapp target exec sandbox -- dotnet --info</c> reads as one thought.
/// </para>
/// </remarks>
internal class TargetCommand : Command, IShortDescription
{
    /// <inheritdoc/>
    public string ShortDescription => "Run commands and copy files on an execution target";

    /// <summary>Composes the namespace.</summary>
    public TargetCommand(
        TargetExecCommand execCommand,
        TargetPushCommand pushCommand,
        TargetPullCommand pullCommand,
        TargetSnapshotCommand snapshotCommand,
        TargetScreenshotCommand screenshotCommand,
        TargetRecordCommand recordCommand)
        : base(
            "target",
            "Run commands and copy files on an execution target such as the Windows Sandbox winapp manages. " +
            "Use these to prepare dependencies or diagnose an application that 'winapp run --on <target>' cannot resolve on its own.")
    {
        Subcommands.Add(execCommand);
        Subcommands.Add(pushCommand);
        Subcommands.Add(pullCommand);
        Subcommands.Add(snapshotCommand);
        Subcommands.Add(screenshotCommand);
        Subcommands.Add(recordCommand);
    }
}

/// <summary>Shared pieces of every <c>winapp target</c> verb.</summary>
internal static class TargetVerb
{
    /// <summary>Creates the selector argument. Each verb owns its own instance.</summary>
    /// <remarks>
    /// Required, and validated against the kinds this build actually implements before any other
    /// argument is interpreted. That ordering is what makes an omitted selector safe: in
    /// <c>winapp target push .\setup.ps1 C:\Setup\setup.ps1</c> the parser binds <c>.\setup.ps1</c>
    /// here, and because it is not a target kind the command fails naming the problem instead of
    /// copying the wrong file to the wrong place.
    /// </remarks>
    public static Argument<string> NewSelectorArgument() => new("target")
    {
        Description = "Execution target to act on. Currently: 'sandbox'.",
        Arity = ArgumentArity.ExactlyOne,
    };

    /// <summary>
    /// Resolves the selector and proves this build has a provider that serves it.
    /// </summary>
    /// <exception cref="ExecutionTargetException">
    /// The selector is malformed, names an unimplemented kind, or names the local machine, which
    /// these verbs do not act on.
    /// </exception>
    public static ExecutionTargetRef Resolve(ExecutionTargetOrchestrator orchestrator, string? selector)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);

        var target = ExecutionTargetSelector.Parse(selector);

        if (target.IsLocal)
        {
            // Running a command on this machine is what a shell is for, and copying a file to
            // itself is a no-op dressed up as work. Refusing is clearer than either.
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TargetInvalid,
                "'winapp target' acts on a separate execution target, not on this machine.",
                userAction: $"Name a target such as '{ExecutionTargetRef.SandboxKind}', or run the command directly.",
                example: "winapp target exec sandbox -- dotnet --info",
                context: new Dictionary<string, string> { ["selector"] = selector ?? string.Empty });
        }

        if (!orchestrator.Target.Matches(target.Kind, target.Id))
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TargetInvalid,
                $"No provider in this build serves the '{target.Selector}' target.",
                userAction: $"Use '{orchestrator.Target.Selector}'.",
                context: new Dictionary<string, string> { ["selector"] = target.Selector });
        }

        return orchestrator.Target;
    }
}

/// <summary>
/// Runs one command on the target as the interactive user.
/// </summary>
/// <remarks>
/// Everything after <c>--</c> is passed through as a structured argument array, never an
/// interpolated command line, so quoting and spacing survive exactly and nothing can be
/// reinterpreted as an extra argument.
/// <para>
/// Arguments, paths, and stream contents are excluded from telemetry entirely.
/// </para>
/// </remarks>
internal class TargetExecCommand : Command, IShortDescription
{
    /// <inheritdoc/>
    public string ShortDescription => "Run a command on an execution target";

    /// <summary>Which target to run on.</summary>
    public static Argument<string> SelectorArgument { get; } = TargetVerb.NewSelectorArgument();

    /// <summary>Working directory inside the target.</summary>
    public static Option<string?> WorkingDirectoryOption { get; } = new("--cwd")
    {
        Description = "Working directory on the target.",
    };

    /// <summary>Executable and arguments, taken verbatim after <c>--</c>.</summary>
    public static Argument<string[]> CommandArgument { get; } = new("command")
    {
        Description = "Executable and arguments to run on the target, after '--'.",
        Arity = ArgumentArity.OneOrMore,
    };

    /// <summary>Creates the command.</summary>
    public TargetExecCommand()
        : base(
            "exec",
            "Run a command on an execution target, as that target's interactive user. " +
            "Streams stdin, stdout, and stderr, and returns the command's own exit code. " +
            "Does not provide a full terminal, so interactive console applications may see redirected pipes.")
    {
        Arguments.Add(SelectorArgument);
        Options.Add(WorkingDirectoryOption);
        Options.Add(WinAppRootCommand.JsonOption);
        Arguments.Add(CommandArgument);
    }

    /// <summary>Forwards the command to the target and relays its streams.</summary>
    public class Handler(ExecutionTargetOrchestrator orchestrator, IAnsiConsole console)
        : AsynchronousCommandLineAction
    {
        /// <inheritdoc/>
        public override async Task<int> InvokeAsync(
            ParseResult parseResult,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(parseResult);

            var command = parseResult.GetValue(CommandArgument) ?? [];
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);

            ExecutionTargetRef reference;

            try
            {
                // Resolved before anything else is interpreted: an unrecognised selector must never
                // reach the point where the remaining tokens are treated as an executable.
                reference = TargetVerb.Resolve(orchestrator, parseResult.GetValue(SelectorArgument));

                if (command.Length == 0)
                {
                    throw ExecutionTargetException.Create(
                        ExecutionTargetErrorCodes.TargetInvalid,
                        $"No command was given to run on the '{reference.Selector}' target.",
                        userAction: "Put the executable and its arguments after '--'.",
                        example: $"winapp target exec {reference.Selector} -- dotnet --info");
                }
            }
            catch (ExecutionTargetException ex)
            {
                return TargetOutput.RejectSelection(console, json, ex.Error);
            }

            try
            {
                // Read-only from the target's point of view: running a command does not change
                // deployment or package state, so it must not block a deployment or wait for one.
                await using var target = await orchestrator
                    .PrepareAsync(PrepareTargetOptions.ReadOnly, cancellationToken)
                    .ConfigureAwait(false);

                var request = new GuestExecRequest
                {
                    Executable = command[0],
                    Arguments = [.. command.Skip(1)],
                    WorkingDirectory = parseResult.GetValue(WorkingDirectoryOption),

                    // Forwarded so a script run through exec can invoke the target's own 'winapp ui'
                    // commands without losing its workflow ownership.
                    Environment = GuestOwnerContext.WithOwner(
                        environment: null,
                        GuestOwnerContext.ResolveGuestToken(target.Reference.StateKey, target.Epoch.Value)),
                };

                var result = await target.Operations.ExecuteAsync(
                    request,
                    new GuestExecCallbacks(
                        // Documented as streaming stdin as well as stdout and stderr. Started from
                        // the operation ID so input a caller piped in before winapp began is not
                        // sent for an operation the target has not heard of.
                        OnOperationId: GuestStandardInputPump.Attach(target.Operations, cancellationToken),
                        OnStandardOutput: data => WriteRaw(Console.OpenStandardOutput(), data),
                        OnStandardError: data => WriteRaw(Console.OpenStandardError(), data)),
                    cancellationToken).ConfigureAwait(false);

                // The target application's own exit code, kept distinguishable from the
                // infrastructure failures reported through the error envelope.
                return result.ExitCode;
            }
            catch (ExecutionTargetException ex)
            {
                return TargetOutput.Fail(console, json, ex.Error);
            }
        }

        private static void WriteRaw(Stream stream, ReadOnlyMemory<byte> data)
        {
            // Written as bytes rather than decoded text: command output can be binary or split
            // mid-character at a chunk boundary, and decoding per chunk would corrupt both.
            stream.Write(data.Span);
            stream.Flush();
        }
    }
}

/// <summary>Copies from this machine onto a target.</summary>
internal class TargetPushCommand : Command, IShortDescription
{
    /// <inheritdoc/>
    public string ShortDescription => "Copy files from this machine to an execution target";

    /// <summary>Which target to copy to.</summary>
    public static Argument<string> SelectorArgument { get; } = TargetVerb.NewSelectorArgument();

    /// <summary>Where to copy from, on this machine.</summary>
    public static Argument<string> SourceArgument { get; } = new("source")
    {
        Description = "File or directory on this machine to copy.",
    };

    /// <summary>Where to copy to, on the target.</summary>
    public static Argument<string> DestinationArgument { get; } = new("destination")
    {
        Description = "Destination path on the target, relative to its managed work area.",
    };

    /// <summary>Creates the command.</summary>
    public TargetPushCommand()
        : base(
            "push",
            "Copy files or directories from this machine to an execution target. " +
            "Directory structure and useful timestamps are preserved, unchanged files are skipped, " +
            "and changed files are replaced atomically.")
    {
        Arguments.Add(SelectorArgument);
        Arguments.Add(SourceArgument);
        Arguments.Add(DestinationArgument);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    /// <summary>Performs the copy.</summary>
    public class Handler(ExecutionTargetOrchestrator orchestrator, IAnsiConsole console)
        : AsynchronousCommandLineAction
    {
        /// <inheritdoc/>
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(parseResult);

            return TargetTransfer.RunAsync(
                orchestrator,
                console,
                parseResult,
                SelectorArgument,
                TargetTransferDirection.ToTarget,
                hostPath: parseResult.GetValue(SourceArgument),
                targetPath: parseResult.GetValue(DestinationArgument),
                cancellationToken);
        }
    }
}

/// <summary>Copies from a target back onto this machine.</summary>
internal class TargetPullCommand : Command, IShortDescription
{
    /// <inheritdoc/>
    public string ShortDescription => "Copy files from an execution target to this machine";

    /// <summary>Which target to copy from.</summary>
    public static Argument<string> SelectorArgument { get; } = TargetVerb.NewSelectorArgument();

    /// <summary>Where to copy from, on the target.</summary>
    public static Argument<string> SourceArgument { get; } = new("source")
    {
        Description = "File or directory on the target to copy, relative to its managed work area.",
    };

    /// <summary>Where to copy to, on this machine.</summary>
    public static Argument<string> DestinationArgument { get; } = new("destination")
    {
        Description = "Destination path on this machine.",
    };

    /// <summary>Creates the command.</summary>
    public TargetPullCommand()
        : base(
            "pull",
            "Copy files or directories from an execution target to this machine. " +
            "Directory structure and useful timestamps are preserved, unchanged files are skipped, " +
            "and changed files are replaced atomically.")
    {
        Arguments.Add(SelectorArgument);
        Arguments.Add(SourceArgument);
        Arguments.Add(DestinationArgument);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    /// <summary>Performs the copy.</summary>
    public class Handler(ExecutionTargetOrchestrator orchestrator, IAnsiConsole console)
        : AsynchronousCommandLineAction
    {
        /// <inheritdoc/>
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(parseResult);

            return TargetTransfer.RunAsync(
                orchestrator,
                console,
                parseResult,
                SelectorArgument,
                TargetTransferDirection.FromTarget,
                hostPath: parseResult.GetValue(DestinationArgument),
                targetPath: parseResult.GetValue(SourceArgument),
                cancellationToken);
        }
    }
}

/// <summary>The half of <c>push</c> and <c>pull</c> that is the same in both directions.</summary>
/// <remarks>
/// The verb is the direction, so the only thing that differs between the two commands is which
/// argument names which side. Sharing everything else means one implementation of containment,
/// hashing, atomic replacement, and reporting rather than two that can drift.
/// </remarks>
internal static class TargetTransfer
{
    /// <summary>Prepares the target and performs one directed copy.</summary>
    public static async Task<int> RunAsync(
        ExecutionTargetOrchestrator orchestrator,
        IAnsiConsole console,
        ParseResult parseResult,
        Argument<string> selectorArgument,
        TargetTransferDirection direction,
        string? hostPath,
        string? targetPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(parseResult);

        var json = parseResult.GetValue(WinAppRootCommand.JsonOption);

        ExecutionTargetRef reference;
        TargetTransferRequest request;

        try
        {
            reference = TargetVerb.Resolve(orchestrator, parseResult.GetValue(selectorArgument));
            request = TargetTransferRequest.Create(direction, hostPath, targetPath);
        }
        catch (ExecutionTargetException ex)
        {
            return TargetOutput.RejectSelection(console, json, ex.Error);
        }

        try
        {
            // Copying changes target storage, so it takes the mutation lock -- otherwise a copy
            // could interleave with a deployment writing the same managed roots.
            await using var target = await orchestrator
                .PrepareAsync(
                    PrepareTargetOptions.Mutating with { RequireInteractiveDesktop = false },
                    cancellationToken)
                .ConfigureAwait(false);

            var result = await TargetFileTransferService
                .CopyAsync(target.Operations, request, cancellationToken)
                .ConfigureAwait(false);

            // Target paths resolve under a managed root, so the effective destination is stated
            // rather than left for the user to infer from a path they did not type.
            var resolved = direction == TargetTransferDirection.ToTarget
                ? TargetFileTransferService.DescribeTargetPath(
                    TargetFileTransferService.NormalizeTargetRelative(request.TargetPath))
                : null;

            if (json)
            {
                console.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(
                    new TargetTransferOutput
                    {
                        ExecutionTarget = ExecutionTargetScope.For(reference, target.Epoch),
                        Transferred = result.Transferred,
                        Skipped = result.Skipped,
                        Bytes = result.Bytes,
                        TargetPath = resolved,
                    },
                    TargetJsonContext.Default.TargetTransferOutput));
            }
            else if (resolved is not null)
            {
                console.MarkupLineInterpolated(
                    $"Copied {result.Transferred} file(s), skipped {result.Skipped} unchanged, to {resolved} on {reference.Selector}.");
            }
            else
            {
                console.MarkupLineInterpolated(
                    $"Copied {result.Transferred} file(s), skipped {result.Skipped} unchanged.");
            }

            return 0;
        }
        catch (ExecutionTargetException ex)
        {
            return TargetOutput.Fail(console, json, ex.Error);
        }
    }
}

/// <summary>Machine-readable result of a copy.</summary>
internal sealed class TargetTransferOutput
{
    /// <summary>Which target, and which incarnation of it, the copy acted on.</summary>
    public required ExecutionTargetScope ExecutionTarget { get; init; }

    /// <summary>Files whose content moved.</summary>
    public int Transferred { get; init; }

    /// <summary>Files already identical at the destination.</summary>
    public int Skipped { get; init; }

    /// <summary>Total bytes transferred.</summary>
    public long Bytes { get; init; }

    /// <summary>
    /// Where a push actually landed, fully qualified on the target.
    /// </summary>
    /// <remarks>
    /// Reported so the effective location is never implicit: it is exactly the path a following
    /// <c>target exec --cwd</c> should use. Null for a pull, where the caller already named the
    /// destination.
    /// </remarks>
    public string? TargetPath { get; init; }
}

/// <summary>Error envelope emitted by the <c>target</c> commands.</summary>
internal sealed class TargetErrorOutput
{
    /// <summary>The structured failure.</summary>
    public required ExecutionTargetErrorInfo Error { get; init; }
}

/// <summary>Source-generated serializer context for <c>target</c> command output.</summary>
[JsonSerializable(typeof(TargetTransferOutput))]
[JsonSerializable(typeof(TargetSnapshotOutput))]
[JsonSerializable(typeof(TargetErrorOutput))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class TargetJsonContext : JsonSerializerContext
{
}

/// <summary>Shared failure reporting for execution-target commands.</summary>
internal static class TargetOutput
{
    /// <summary>Reports an infrastructure failure and returns the process exit code.</summary>
    /// <remarks>
    /// Always on stderr, in both modes. Under <c>--json</c> stdout carries the command's result and
    /// nothing else, so a caller can parse it without stripping diagnostics first.
    /// </remarks>
    public static int Fail(IAnsiConsole console, bool json, ExecutionTargetErrorInfo error)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(error);

        if (json)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(
                new TargetErrorOutput { Error = error },
                TargetJsonContext.Default.TargetErrorOutput));
        }
        else
        {
            Console.Error.WriteLine(error.Message);

            if (error.UserAction is { } action)
            {
                Console.Error.WriteLine(action);
            }

            if (error.NextCommand is { } next)
            {
                // Advisory commands are shown, never run: only the user knows whether the side
                // effect is acceptable.
                Console.Error.WriteLine(next.Advisory ? $"You may want to run: {next.Command}" : $"Try: {next.Command}");
            }
        }

        // Distinct from any plausible target application exit code, so a caller can tell "winapp
        // could not run your command" from "your command failed".
        return TargetInfrastructureExitCode;
    }

    /// <summary>
    /// Reports a command line that names no usable target, and returns the process exit code.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Fail"/> because the two are different failures with different
    /// contracts. This one happens before anything is prepared: nothing was attempted, and the
    /// command line itself is what is wrong. It exits 1, the code every other malformed winapp
    /// command line already returns, rather than the code that means "the target could not be
    /// used" — a caller distinguishing those two needs them to stay distinct.
    /// </remarks>
    public static int RejectSelection(IAnsiConsole console, bool json, ExecutionTargetErrorInfo error)
    {
        Fail(console, json, error);
        return InvalidCommandLineExitCode;
    }

    /// <summary>Exit code for a command line that does not name a usable target.</summary>
    internal const int InvalidCommandLineExitCode = 1;

    /// <summary>
    /// Reports an unusable option on an otherwise well-formed target command line, before any work.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="RejectSelection"/> for the options rather than the selector.
    /// Kept separate from <see cref="Fail"/> for the same reason: nothing was prepared and nothing
    /// was attempted, so this exits 1 like every other rejected command line rather than 70, which
    /// means the target itself could not be used.
    /// </remarks>
    public static int RejectOptions(IAnsiConsole console, bool json, ExecutionTargetErrorInfo error)
    {
        Fail(console, json, error);
        return InvalidCommandLineExitCode;
    }

    /// <summary>
    /// Reports a <c>target</c> command line the parser rejected, in the same envelope every other
    /// target failure uses.
    /// </summary>
    /// <remarks>
    /// Needed because a parse failure happens before any handler exists to report it. Without this,
    /// <c>winapp target record sandbox --json --fps abc</c> would print human help text and a caller
    /// that asked for JSON would have to parse prose to find out what went wrong. Same sink (stderr),
    /// same shape, and the same exit code a malformed command line gets everywhere else.
    /// </remarks>
    public static int RejectCommandLine(string message)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(
            new TargetErrorOutput
            {
                Error = new ExecutionTargetErrorInfo
                {
                    Code = ExecutionTargetErrorCodes.TargetInvalidArguments,
                    Message = message,
                    UserAction = "Fix the command line, then retry.",
                },
            },
            TargetJsonContext.Default.TargetErrorOutput));

        return InvalidCommandLineExitCode;
    }

    /// <summary>Exit code used for execution-target infrastructure failures.</summary>
    internal const int TargetInfrastructureExitCode = 70;
}

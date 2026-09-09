// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text;
using Spectre.Console;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Commands;

/// <summary>
/// What a routed <c>winapp ui</c> verb actually needs from the guest.
/// </summary>
/// <remarks>
/// <para>
/// Routing every verb as if it injected real input has a visible cost: requiring an interactive
/// desktop makes the backend reconnect the Sandbox client, which tears down the session the previous
/// command left running and shows the user "the connection was lost, reconnect?". For a verb that
/// only reads the UI Automation tree, all of that is unnecessary — and when the reconnect races the
/// command, the command simply hangs.
/// </para>
/// <para>
/// The split is drawn conservatively. Only verbs that are unambiguously read-only are treated as
/// such; anything that changes the UI, injects input, or captures pixels keeps the stricter
/// treatment even where it might technically work through UI Automation alone. Being too strict
/// costs a reconnect, while being too lax would let a command report input it never delivered, and
/// those two mistakes are not equally bad.
/// </para>
/// </remarks>
/// <param name="RequiresInteractiveDesktop">
/// Whether the guest must have a connected client with a usable input desktop.
/// </param>
/// <param name="RequiresRealInput">
/// Whether the guest should re-probe input readiness before starting the command.
/// </param>
internal sealed record TargetUiRequirements(bool RequiresInteractiveDesktop, bool RequiresRealInput)
{
    /// <summary>Verbs that only read UI Automation state.</summary>
    /// <remarks>
    /// Each of these resolves elements and reads properties. None moves a pointer, synthesizes a
    /// keystroke, changes a control's value, or captures the screen, so none needs a connected
    /// client — which is exactly what makes them safe to run against a Sandbox someone is already
    /// looking at.
    /// </remarks>
    private static readonly HashSet<string> ReadOnlyVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "status",
        "inspect",
        "search",
        "get-property",
        "get-focused",
        "list-windows",
        "wait-for",
    };

    /// <summary>Requirements for a verb that reads without touching anything.</summary>
    public static TargetUiRequirements ReadOnly { get; } = new(false, false);

    /// <summary>Requirements for a verb that injects input or captures the screen.</summary>
    public static TargetUiRequirements Interactive { get; } = new(true, true);

    /// <summary>Classifies the parsed command.</summary>
    /// <remarks>
    /// Taken from the parsed command name rather than by scanning raw tokens, so an argument that
    /// merely contains a verb's name — a selector or a file path — cannot change how the command is
    /// gated. An unrecognized verb gets the stricter treatment.
    /// </remarks>
    public static TargetUiRequirements For(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        return ReadOnlyVerbs.Contains(parseResult.CommandResult.Command.Name)
            ? ReadOnly
            : Interactive;
    }
}

/// <summary>
/// The single place a <c>winapp ui</c> command is diverted onto an execution target.
/// </summary>
/// <remarks>
/// Interception happens before any local UI service runs, rather than inside each verb. Twenty-odd
/// handlers each remembering to check a selector is twenty-odd chances for one of them to perform UI
/// Automation, window discovery, capture, or input injection on the host desktop when the user asked
/// for somewhere else — which is the one thing <c>--on</c> exists to prevent.
/// <para>
/// The target's own winapp parses and executes the ordinary command. The host rewrites only
/// routing-specific arguments, relays the standard streams and exit code, brings any declared output
/// file back, and rewrites the target path in the result so the caller sees the path they asked for.
/// </para>
/// </remarks>
internal sealed class ExecutionTargetUiRouter(
    ExecutionTargetOrchestrator orchestrator,
    IAnsiConsole console)
{
    /// <summary>
    /// Whether this invocation must run somewhere other than this machine.
    /// </summary>
    /// <remarks>
    /// Decided from the parsed result rather than by scanning raw tokens, and only for a <c>ui</c>
    /// command: the parser already knows which spelling of <c>--on</c> was used and what it resolved
    /// to. An application value never opts in by itself — a name, a PID, or a window handle carries
    /// no scope, and inferring one would resolve a host window against the target or the reverse.
    /// </remarks>
    public static bool ShouldRoute(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        if (!IsUiCommand(parseResult.CommandResult.Command))
        {
            return false;
        }

        // Safe to resolve without catching: Program validates target selection before dispatch, so
        // an invalid selector has already failed by the time anything reaches here.
        return !ExecutionTargetSelection.Resolve(parseResult).IsLocal;
    }

    /// <summary>Runs the command in the guest and returns its exit code.</summary>
    public async Task<int> RouteAsync(
        IReadOnlyList<string> arguments,
        TargetUiRequirements requirements,
        bool isJson,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(requirements);

        var operationScope = TargetArtifactService.ScopeFor(Guid.NewGuid());

        // Ctrl+C has to reach the guest process, not just end this one. Cancelling the operation
        // asks the guest to stop the command gracefully and only then terminate its tree, which is
        // what lets a recording finalize its file instead of being killed mid-write.
        using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler onCancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            interrupt.Cancel();
        };

        Console.CancelKeyPress += onCancel;

        try
        {
            return await RouteCoreAsync(arguments, requirements, isJson, operationScope, interrupt.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller never pressed Ctrl+C, so this is an internal timeout somewhere in
            // preparation or transport. Reported as a target failure with a code and an action,
            // because surfacing it as a bare "OperationCanceled" tells the user nothing about which
            // step gave up or what to do about it.
            return TargetOutput.Fail(console, isJson, ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TransportFailed,
                $"The {orchestrator.Target.Selector} target did not respond while preparing this command.",
                userAction:
                    "Retry the command. If it keeps failing, check that the Sandbox window is still " +
                    "connected, or close it so winapp can start a fresh one.",
                context: orchestrator.DescribeForDiagnostics().ToDictionary(
                    pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)).Error);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private async Task<int> RouteCoreAsync(
        IReadOnlyList<string> arguments,
        TargetUiRequirements requirements,
        bool isJson,
        GuestPathScope operationScope,
        CancellationToken cancellationToken)
    {
        try
        {
            // Read-only inspection neither needs a connected client nor should force one: requiring
            // an interactive desktop reconnects the Sandbox window, which interrupts whatever the
            // user or a previous command left running there.
            await using var target = await orchestrator
                .PrepareAsync(
                    requirements.RequiresInteractiveDesktop
                        ? PrepareTargetOptions.Interactive
                        : PrepareTargetOptions.ReadOnly,
                    cancellationToken)
                .ConfigureAwait(false);

            var routed = UiArgvRouter.Rewrite(
                arguments,
                GuestPaths.Resolve(target.Capabilities, operationScope),
                Path.GetFullPath);

            var owner = GuestOwnerContext.WithOwner(
                environment: null,
                GuestOwnerContext.ResolveGuestToken(
                    target.Reference.StateKey, target.Epoch.Value));

            // Buffered only when the guest path would otherwise appear in the result. Streaming is
            // the default so a long-running verb still shows progress as it happens.
            using var buffered = routed.Artifact is null ? null : new MemoryStream();

            if (requirements.RequiresRealInput)
            {
                // Re-check immediately before the guest operation. The client can be minimized after
                // preparation, and the guest cannot observe that host-side state.
                _ = orchestrator.ResolveDesktopSurface(
                    routed.Artifact is null ? TargetDesktopUse.RealInput : TargetDesktopUse.PixelCapture);
            }

            var result = await target.Operations.ExecuteAsync(
                new GuestExecRequest
                {
                    UseGuestWinapp = true,
                    Arguments = routed.Arguments,
                    Environment = owner,

                    // Declared from the verb's own requirements. Asserting real input for a
                    // read-only inspection would make the guest re-probe an input desktop it does
                    // not need, and fail the command when the Sandbox window happens to be
                    // disconnected.
                    RequiresRealInput = requirements.RequiresRealInput,
                },
                new GuestExecCallbacks(
                    OnOperationId: GuestStandardInputPump.Attach(target.Operations, cancellationToken),
                    OnStandardOutput: data =>
                    {
                        if (buffered is not null)
                        {
                            buffered.Write(data.Span);
                            return;
                        }

                        WriteRaw(Console.OpenStandardOutput(), data);
                    },
                    OnStandardError: data => WriteRaw(Console.OpenStandardError(), data)),
                cancellationToken).ConfigureAwait(false);

            if (routed.Artifact is { } artifact)
            {
                await PublishArtifactAsync(
                    target.Operations, operationScope, artifact, buffered!, result.ExitCode, cancellationToken)
                    .ConfigureAwait(false);
            }

            return result.ExitCode;
        }
        catch (ExecutionTargetException ex)
        {
            return TargetOutput.Fail(console, isJson, ex.Error);
        }
    }

    /// <summary>
    /// Publishes the declared output, then emits the guest's result with its path corrected.
    /// </summary>
    /// <remarks>
    /// Ordering matters: the result is emitted only after the file is at the requested path, so a
    /// caller that parses the output and immediately opens the file never loses that race. A guest
    /// command that failed publishes nothing — there is no output to copy, and reporting a stale
    /// file from a previous run as this run's result would be worse than reporting nothing.
    /// </remarks>
    private async Task PublishArtifactAsync(
        ITargetOperationExecutor channel,
        GuestPathScope scope,
        RoutedArtifact artifact,
        MemoryStream buffered,
        int exitCode,
        CancellationToken cancellationToken)
    {
        try
        {
            if (exitCode == 0)
            {
                await TargetArtifactService
                    .PublishAsync(channel, scope, artifact, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            // Written whatever happened, because it is the guest's own result and suppressing it
            // would hide why the command failed.
            EmitWithHostPaths(buffered, artifact);

            await TargetArtifactService.TryRemoveAsync(channel, scope, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Replaces the guest staging path in the guest's output with the path the caller asked for.
    /// </summary>
    /// <remarks>
    /// A literal replacement rather than a JSON-aware edit, deliberately: the guest path was
    /// injected by this router, so it is a unique string that appears exactly where the guest
    /// reported the file — in a JSON field, in a human-readable line, or both. Rewriting the text
    /// keeps every verb's output shape untouched, and works for the verbs that print rather than
    /// serialize.
    /// </remarks>
    private void EmitWithHostPaths(MemoryStream buffered, RoutedArtifact artifact)
    {
        var bytes = buffered.ToArray();
        if (bytes.Length == 0)
        {
            return;
        }

        string text;

        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Not text at all. Relayed byte-for-byte rather than mangled by a rewrite that assumed
            // it was.
            WriteRaw(Console.OpenStandardOutput(), bytes);
            return;
        }

        var rewritten = RewriteOutputPaths(text, artifact);

        console.Profile.Out.Writer.Write(rewritten);
        console.Profile.Out.Writer.Flush();
    }

    /// <summary>Rewrites guest paths in human-readable or JSON-escaped command output.</summary>
    internal static string RewriteOutputPaths(string text, RoutedArtifact artifact) =>
        text
            .Replace(
                artifact.GuestFullPath.Replace(@"\", @"\\"),
                artifact.HostDestination.Replace(@"\", @"\\"),
                StringComparison.OrdinalIgnoreCase)
            .Replace(artifact.GuestFullPath, artifact.HostDestination, StringComparison.OrdinalIgnoreCase)
            .Replace(
                artifact.GuestFullPath.Replace('\\', '/'),
                artifact.HostDestination,
                StringComparison.OrdinalIgnoreCase);

    private static bool IsUiCommand(Command? command)
    {
        while (command is not null)
        {
            if (command.Name == "ui")
            {
                return true;
            }

            command = command.Parents.OfType<Command>().FirstOrDefault();
        }

        return false;
    }

    private static void WriteRaw(Stream stream, ReadOnlyMemory<byte> data)
    {
        stream.Write(data.Span);
        stream.Flush();
    }
}

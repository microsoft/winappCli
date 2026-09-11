// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text;
using System.Text.Json;
using Spectre.Console;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Commands;

/// <summary>Desktop and input requirements of the parsed guest UI verb.</summary>
internal sealed record TargetUiRequirements(bool RequiresInteractiveDesktop, bool RequiresRealInput)
{
    private static readonly HashSet<string> ReadOnlyVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "inspect", "search", "get-property", "get-value", "get-focused",
        "list-windows", "wait-for", "yield",
    };

    public string? CommandName { get; init; }
    public bool GuestDesktopCapture { get; init; }
    public static TargetUiRequirements ReadOnly { get; } = new(false, false);
    public static TargetUiRequirements Interactive { get; } = new(true, true);

    public static TargetUiRequirements For(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        var name = parseResult.CommandResult.Command.Name;
        return (ReadOnlyVerbs.Contains(name) ? ReadOnly : Interactive) with { CommandName = name };
    }
}

/// <summary>Routes UI commands before any local UI service executes.</summary>
internal sealed class ExecutionTargetUiRouter(
    ExecutionTargetOrchestrator orchestrator,
    IAnsiConsole console)
{
    private const int MaximumCaptureOutputBytes = 1024 * 1024;
    internal static readonly TimeSpan ArtifactCompletionTimeout = TimeSpan.FromMinutes(5);

    public static bool ShouldRoute(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        return IsUiCommand(parseResult.CommandResult.Command) &&
            !ExecutionTargetSelection.Resolve(parseResult).IsLocal;
    }

    public async Task<int> RouteAsync(
        IReadOnlyList<string> arguments,
        TargetUiRequirements requirements,
        bool isJson,
        CancellationToken cancellationToken)
    {
        using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler onCancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            interrupt.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            return await RouteCoreAsync(arguments, requirements, isJson,
                TargetArtifactService.ScopeFor(Guid.NewGuid()), interrupt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!interrupt.IsCancellationRequested)
        {
            return TargetOutput.Fail(console, isJson, ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.TransportFailed,
                $"The {orchestrator.Target.Selector} target timed out while preparing this command.",
                userAction: "Check that the Sandbox window is connected, then retry.",
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
            // Resolve defaults once on the host and reject collisions before starting the target.
            var preflight = UiArgvRouter.Rewrite(
                arguments, @"C:\winapp-artifact-preflight", Path.GetFullPath, requirements.CommandName);
            if (preflight.Artifact is { } destination)
            {
                TargetArtifactService.ValidateDestination(destination);
            }

            await using var target = await orchestrator.PrepareAsync(
                requirements.RequiresInteractiveDesktop ? PrepareTargetOptions.Interactive : PrepareTargetOptions.ReadOnly,
                cancellationToken).ConfigureAwait(false);

            var routed = UiArgvRouter.Rewrite(
                arguments, GuestPaths.Resolve(target.Capabilities, operationScope),
                Path.GetFullPath, requirements.CommandName, preflight.Artifact?.HostDestination);
            if (requirements.GuestDesktopCapture)
            {
                // The hidden capture command owns its result schema. Send scope as data, not --on,
                // so the guest captures its own desktop rather than recursively routing elsewhere.
                var separator = routed.Arguments.IndexOf("--");
                routed.Arguments.InsertRange(separator < 0 ? routed.Arguments.Count : separator,
                [
                    "--target-kind", target.Reference.Kind,
                    "--target-name", target.Reference.Id,
                    "--target-epoch", target.Epoch.Value,
                ]);
            }
            var owner = GuestOwnerContext.WithWorkflow(
                environment: null,
                GuestOwnerContext.ResolveGuestToken(target.Reference.StateKey, target.Epoch.Value));

            using var buffered = routed.Artifact is null ? null : new MemoryStream();
            var errors = routed.Artifact is { } capture
                ? new ArtifactErrorRelay(Console.Error, capture)
                : null;
            if (requirements.RequiresRealInput && !requirements.GuestDesktopCapture)
            {
                _ = orchestrator.ResolveDesktopSurface(
                    routed.Artifact is null ? TargetDesktopUse.RealInput : TargetDesktopUse.PixelCapture);
            }

            GuestExecResult result;
            try
            {
                result = await target.Operations.ExecuteAsync(
                    new GuestExecRequest
                    {
                        UseGuestWinapp = true,
                        Arguments = routed.Arguments,
                        Environment = UiCommandAdvice.WithTarget(owner, target.Reference),
                        RequiresRealInput = requirements.RequiresRealInput,
                    },
                    new GuestExecCallbacks(
                        OnOperationId: GuestStandardInputPump.Attach(target.Operations, cancellationToken),
                        OnStandardOutput: data =>
                        {
                            if (buffered is not null)
                            {
                                if (buffered.Length + data.Length > MaximumCaptureOutputBytes)
                                {
                                    throw new IOException("Capture diagnostics exceeded the 1 MiB limit.");
                                }
                                buffered.Write(data.Span);
                            }
                            else
                            {
                                WriteRaw(Console.OpenStandardOutput(), data);
                            }
                        },
                        OnStandardError: data =>
                        {
                            if (errors is not null)
                            {
                                errors.Write(data);
                            }
                            else
                            {
                                WriteRaw(Console.OpenStandardError(), data);
                            }
                        },
                        ReturnResultOnCancellation: routed.Artifact?.IsRecording == true),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (routed.Artifact is not null &&
                                       ex is ExecutionTargetException or OperationCanceledException)
            {
                errors!.Complete(recoveryDirectory: null, delivered: false);
                throw ExecutionTargetException.Create(
                    ExecutionTargetErrorCodes.ArtifactFailed,
                    cancellationToken.IsCancellationRequested
                        ? "Recording was stopped, but the guest did not confirm finalization."
                        : "The guest capture did not complete.",
                    userAction: TargetArtifactService.RecoveryAction(Path.GetDirectoryName(routed.Artifact.GuestFullPath)!),
                    innerException: ex);
            }

            if (routed.Artifact is { } artifact)
            {
                await PublishArtifactAsync(
                    target.Operations, operationScope, artifact, buffered!, errors!, result.ExitCode)
                    .ConfigureAwait(false);
            }
            return result.ExitCode;
        }
        catch (ExecutionTargetException ex)
        {
            return TargetOutput.Fail(console, isJson, ex.Error);
        }
    }

    internal async Task PublishArtifactAsync(
        ITargetOperationExecutor channel,
        GuestPathScope scope,
        RoutedArtifact artifact,
        MemoryStream buffered,
        ArtifactErrorRelay errors,
        int exitCode)
    {
        // User stop cancels sampling. Finalization acknowledgement and artifact delivery outlive it.
        using var completion = new CancellationTokenSource(ArtifactCompletionTimeout);
        string? recovery = null;
        var delivered = false;
        try
        {
            recovery = await TargetArtifactService.PublishAsync(
                channel, scope, artifact, completion.Token, partial: exitCode != 0).ConfigureAwait(false);
            delivered = exitCode == 0 || recovery is not null;
            var text = Encoding.UTF8.GetString(buffered.ToArray());
            console.Profile.Out.Writer.Write(delivered
                ? RewriteOutputPaths(text, artifact, recovery)
                : text);
            console.Profile.Out.Writer.Flush();
            if (delivered)
            {
                await TargetArtifactService.TryRemoveAsync(channel, scope, completion.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            errors.Complete(recovery, delivered);
        }
    }

    internal static string RewriteOutputPaths(string text, RoutedArtifact artifact, string? recoveryDirectory = null)
    {
        if (recoveryDirectory is not null)
        {
            return ReplacePath(text, Path.GetDirectoryName(artifact.GuestFullPath)!, recoveryDirectory);
        }
        return ReplacePath(
            ReplacePath(text, artifact.GuestFullPath, artifact.HostDestination),
            artifact.GuestFramesDirectory, artifact.HostFramesDirectory);
    }

    private static string ReplacePath(string text, string guestPath, string hostPath) =>
        text.Replace(JsonEncodedText.Encode(guestPath).ToString(), JsonEncodedText.Encode(hostPath).ToString(),
                StringComparison.OrdinalIgnoreCase)
            .Replace(guestPath.Replace(@"\", @"\\"), hostPath.Replace(@"\", @"\\"), StringComparison.OrdinalIgnoreCase)
            .Replace(guestPath, hostPath, StringComparison.OrdinalIgnoreCase)
            .Replace(guestPath.Replace('\\', '/'), hostPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Streams readiness lines; holds final diagnostics until their artifacts are delivered.</summary>
    internal sealed class ArtifactErrorRelay(TextWriter output, RoutedArtifact artifact)
    {
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly StringBuilder _line = new();
        private readonly StringBuilder _held = new();

        public void Write(ReadOnlyMemory<byte> data)
        {
            var chars = new char[Encoding.UTF8.GetMaxCharCount(data.Length)];
            var count = _decoder.GetChars(data.Span, chars.AsSpan(), flush: false);
            for (var index = 0; index < count; index++)
            {
                _line.Append(chars[index]);
                if (_line.Length + _held.Length > MaximumCaptureOutputBytes)
                {
                    throw new IOException("Capture diagnostics exceeded the 1 MiB limit.");
                }
                if (chars[index] == '\n')
                {
                    var line = _line.ToString();
                    _line.Clear();
                    if (IsStartedEvent(line))
                    {
                        output.Write(RewriteOutputPaths(line, artifact));
                        output.Flush();
                    }
                    else
                    {
                        _held.Append(line);
                    }
                }
            }
        }

        public void Complete(string? recoveryDirectory, bool delivered)
        {
            var text = _held.ToString() + _line;
            output.Write(delivered ? RewriteOutputPaths(text, artifact, recoveryDirectory) : text);
            output.Flush();
            _held.Clear();
            _line.Clear();
        }

        private static bool IsStartedEvent(string line)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("event", out var value) &&
                    value.ValueKind == JsonValueKind.String && value.GetString() == "recording-started";
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }

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

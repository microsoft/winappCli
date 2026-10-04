// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text;
using System.Text.Json;
using Spectre.Console;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;
using WinApp.Cli.Services.DevTools;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Commands;

internal sealed class ExecutionTargetDevToolsRouter(
    ExecutionTargetOrchestrator orchestrator,
    GuestDevToolsHost host,
    CommentStore store,
    ICurrentDirectoryProvider currentDirectory,
    IAnsiConsole console,
    Func<CancellationToken, Task<GuestDevToolsCapabilities?>>? capabilityReader = null)
{
    internal static bool ShouldRoute(ParseResult parsed)
    {
        if (!ExecutionTargetSelection.IsCommandInvocation(parsed) || !ExecutionTargetSelection.IsTargetAware(parsed) ||
            ExecutionTargetSelection.Resolve(parsed).IsLocal)
        {
            return false;
        }
        for (Command? command = parsed.CommandResult.Command; command is not null;
            command = command.Parents.OfType<Command>().FirstOrDefault())
        {
            if (command is DevToolsCommand)
            {
                return true;
            }
        }
        return false;
    }

    internal async Task<int> RouteAsync(ParseResult parsed, CancellationToken cancellationToken, TextWriter? outputWriter = null)
    {
        var json = parsed.GetValue(WinAppRootCommand.JsonOption);
        var stdout = outputWriter ?? console.Profile.Out.Writer;
        var admitted = false;
        using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; interrupt.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            ValidateBeforeConnection(parsed);
            CommentStoreLocation? commentLocation = null;
            Comment? selectedComment = null;
            if (parsed.CommandResult.Command is DevToolsCommentsUpdateCommand or DevToolsCommentsDeleteCommand)
            {
                var sourceRoot = parsed.GetValue(CommentsSharedOptions.SourceRootOption) ?? currentDirectory.GetCurrentDirectory();
                commentLocation = store.Locate(sourceRoot);
                var id = parsed.CommandResult.Command is DevToolsCommentsUpdateCommand
                    ? parsed.GetValue(DevToolsCommentsUpdateCommand.IdArgument)
                    : parsed.GetValue(DevToolsCommentsDeleteCommand.IdArgument);
                if (string.IsNullOrWhiteSpace(id))
                {
                    throw new InvalidOperationException("A comment id is required.");
                }
                selectedComment = store.Get(commentLocation.StorePath, id);
                if (selectedComment is null)
                {
                    throw new InvalidOperationException(CommentsSharedOptions.NotFoundMessage(id, commentLocation));
                }
            }
            admitted = true;
            var expected = await (capabilityReader?.Invoke(interrupt.Token) ??
                GuestDevTools.ReadCapabilitiesAsync(Environment.ProcessPath, interrupt.Token)).ConfigureAwait(false);
            var inspection = await orchestrator.InspectAsync(interrupt.Token).ConfigureAwait(false);
            await using var target = inspection.Target;
            if (target is null)
            {
                throw new InvalidOperationException("The Sandbox agent is not connected. Start a guest app with 'winapp run <project> --on sandbox --devtools'. No target was provisioned.");
            }
            if (parsed.CommandResult.Command is DevToolsListCommand)
            {
                var payload = await GuestDevToolsCommandRequest.DiscoverAsync(target, host, expected,
                    parsed.GetValue(DevToolsListCommand.IncludeAvailableOption), interrupt.Token).ConfigureAwait(false);
                if (json)
                {
                    WriteScopedJson(JsonSerializer.SerializeToUtf8Bytes(payload, DevToolsProtocolJsonContext.Default.DevToolsListPayload), target, stdout);
                }
                else
                {
                    console.WriteLine($"Sandbox: {payload.Apps.Count} app(s).");
                    foreach (var app in payload.Apps)
                    {
                        console.WriteLine($"{app.Pid}: {app.ProcessName} ({app.Status})");
                        if (app.AppSelector is not null)
                        {
                            console.WriteLine("winapp " + WindowsCommandLine.JoinArguments([
                                "devtools", app.Attached ? "inspect" : "attach", "--on", target.Reference.Selector, "--app",
                                app.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)]));
                        }
                    }
                }
                return 0;
            }

            var appSelector = GuestDevToolsCommandRequest.AppSelector(parsed)!;
            var application = await host.ResolveApplicationAsync(target, appSelector, expected, interrupt.Token).ConfigureAwait(false);
            if (commentLocation is not null && selectedComment is not null)
            {
                var sources = application.Sources ?? throw new InvalidOperationException("This app has no host-owned source mapping.");
                var binding = new GuestCommentBinding(target.Reference, target.Epoch, application.Process,
                    new FileInfo(sources.ProjectPath), sources.Files.Select(file => file.RelativePath), sources.GuestRoot);
                binding.VerifyHostPaths();
                if (!string.Equals(commentLocation.StorePath, binding.StorePath, StringComparison.OrdinalIgnoreCase) ||
                    !CommentStoreLocator.SameProject(selectedComment.ProjectRoot, binding.ProjectRoot))
                {
                    throw new InvalidOperationException(
                        "The selected app does not own this locally saved comment. Choose its host project with --source-root, or omit --app and --on to update only the saved comment.");
                }
            }

            using var output = new MemoryStream();
            using var error = new MemoryStream();
            var result = await target.Operations.ExecuteAsync(GuestDevToolsCommandRequest.Create(parsed, application),
                new GuestExecCallbacks(OnStandardOutput: bytes => Capture(output, bytes),
                    OnStandardError: bytes => Capture(error, bytes)), interrupt.Token).ConfigureAwait(false);
            if (error.Length > 0)
            {
                await parsed.InvocationConfiguration.Error.WriteAsync(Encoding.UTF8.GetString(error.ToArray())).ConfigureAwait(false);
            }
            if (json)
            {
                if (output.Length == 0)
                {
                    throw new IOException($"The guest command returned no JSON result (exit {result.ExitCode}).");
                }
                WriteScopedJson(output.ToArray(), target, stdout, application.Selector,
                    parsed.CommandResult.Command.Parents.Any(parent => parent is DevToolsCommentsCommand) &&
                        application.Sources is { } source ? Path.GetDirectoryName(source.ProjectPath) : null, application.Sources);
            }
            else
            {
                console.WriteLine($"Target: {target.Reference.Selector}; app: {application.Process.ProcessId}");
                console.Profile.Out.Writer.Write(Encoding.UTF8.GetString(output.ToArray()));
            }
            return result.ExitCode;
        }
        catch (ExecutionTargetException ex)
        {
            if (json)
            {
                Program.EmitDevToolsJsonError(parsed, ex.Error.Message + " " + ex.Error.UserAction,
                    ex.Error.Code, stdout);
                return TargetOutput.TargetInfrastructureExitCode;
            }
            return TargetOutput.Fail(console, json, ex.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            if (json)
            {
                Program.EmitDevToolsJsonError(parsed, ex.Message, admitted ? "guest-unavailable" : "invalid-arguments",
                    stdout);
                return 1;
            }
            return DevToolsCommentsAddCommand.Fail(console, json, ex.Message);
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    internal static void ValidateBeforeConnection(ParseResult parsed)
    {
        if (!ShouldRoute(parsed) || parsed.GetValue(WinAppRootCommand.GuestInspectionOption) is not null ||
            parsed.GetValue(WinAppRootCommand.GuestCommentsOption) is not null ||
            parsed.GetValue(DevToolsListCommand.GuestDiscoveryOption))
        {
            throw new InvalidOperationException("Only public command invocations can be routed to Sandbox DevTools.");
        }
        if (ExecutionTargetSelection.Validate(parsed) is { } targetError)
        {
            throw new InvalidOperationException(targetError.Message);
        }
        if (parsed.CommandResult.Command is DevToolsListCommand)
        {
            return;
        }
        GuestDevToolsCommandRequest.Validate(parsed);
        if (parsed.GetResult(DevToolsAttachCommand.PidOption) is { Implicit: false })
        {
            throw new InvalidOperationException("Guest attachment requires --app from 'devtools list --on sandbox --include-available', not a bare --pid.");
        }
        var selector = GuestDevToolsCommandRequest.AppSelector(parsed);
        if (string.IsNullOrWhiteSpace(selector))
        {
            throw new InvalidOperationException("Supply --app with a guest PID or name from 'devtools list --on sandbox'. No local process is selected implicitly.");
        }
        if (selector.StartsWith("guest:", StringComparison.Ordinal) || selector.StartsWith("guest-process:", StringComparison.Ordinal))
        {
            var scope = GuestDevToolsSelector.Parse(selector, ExecutionTargetSelection.Resolve(parsed));
            if (parsed.CommandResult.Command.Parents.Any(parent => parent is DevToolsCommentsCommand) && scope.LaunchId is null)
            {
                throw new InvalidOperationException("Live comments require a host-owned guest launch, not a late-attached process.");
            }
        }
        else if (selector.StartsWith("sandbox:", StringComparison.OrdinalIgnoreCase) ||
            int.TryParse(selector, out var pid) && pid <= 0)
        {
            throw new InvalidOperationException("Use --on sandbox --app <positive PID or name>; sandbox:<pid> is not an app selector.");
        }
    }

    private static void Capture(MemoryStream destination, ReadOnlyMemory<byte> bytes)
    {
        if (destination.Length + bytes.Length > 8 * 1024 * 1024)
        {
            throw new IOException("The guest command exceeded its 8 MiB output limit.");
        }
        destination.Write(bytes.Span);
    }

    private static void WriteScopedJson(
        byte[] data, PreparedTarget target, TextWriter stdout, string? selector = null, string? hostProjectRoot = null,
        GuestSourceManifest? sources = null)
    {
        using var document = JsonDocument.Parse(data);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The guest command did not return a JSON object.");
        }
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name == "comment" && hostProjectRoot is not null && property.Value.ValueKind == JsonValueKind.Object)
                {
                    writer.WriteStartObject("comment");
                    foreach (var field in property.Value.EnumerateObject())
                    {
                        if (field.Name is not ("projectRoot" or "mappedSourceCandidate"))
                        {
                            field.WriteTo(writer);
                        }
                    }
                    writer.WriteString("projectRoot", hostProjectRoot);
                    if (sources is not null)
                    {
                        var view = property.Value.Deserialize(CommentsJsonContext.Default.CommentView);
                        if (view is not null)
                        {
                            view.ProjectRoot = hostProjectRoot;
                            var candidate = CommentViewBuilder.FindMappedCandidate(view, sources);
                            if (candidate is not null)
                            {
                                writer.WritePropertyName("mappedSourceCandidate");
                                JsonSerializer.Serialize(writer, candidate, CommentsJsonContext.Default.MappedSourceCandidate);
                            }
                        }
                    }
                    writer.WriteEndObject();
                }
                else if (property.Name is not ("executionTarget" or "appSelector"))
                {
                    property.WriteTo(writer);
                }
            }
            writer.WriteStartObject("executionTarget");
            writer.WriteString("kind", target.Reference.Kind);
            writer.WriteString("id", target.Reference.Id);
            writer.WriteString("epoch", target.Epoch.Value);
            writer.WriteEndObject();
            if (selector is not null)
            {
                writer.WriteString("appSelector", selector);
            }
            writer.WriteEndObject();
        }
        stdout.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
    }

}

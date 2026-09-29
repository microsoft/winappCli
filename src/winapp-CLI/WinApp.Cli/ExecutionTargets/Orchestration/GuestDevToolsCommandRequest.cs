// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using WinApp.Cli.Commands;
using WinApp.Cli.Services.DevTools;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

internal static class GuestDevToolsCommandRequest
{
    private static readonly string[] HostOnlyOptions = ["--on", "--app", "--pid", "--source-root"];

    internal static async Task<DevToolsListPayload> DiscoverAsync(
        PreparedTarget target, GuestDevToolsHost host, GuestDevToolsCapabilities? expected, bool includeAvailable,
        CancellationToken cancellationToken)
    {
        GuestDevTools.RequireMatchingEngines(expected,
            await target.Operations.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false));
        List<string> arguments = ["devtools", "list", "--json", "--guest-discovery"];
        if (includeAvailable)
        {
            arguments.Add("--include-available");
        }
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        var result = await target.Operations.ExecuteAsync(new GuestExecRequest
        {
            UseGuestWinapp = true,
            Arguments = arguments,
        }, new GuestExecCallbacks(
            OnStandardOutput: bytes => Capture(output, bytes),
            OnStandardError: bytes => Capture(error, bytes)), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new IOException($"Guest DevTools discovery failed (exit {result.ExitCode}): {Encoding.UTF8.GetString(error.ToArray())}");
        }
        var payload = JsonSerializer.Deserialize(output.ToArray(), DevToolsProtocolJsonContext.Default.DevToolsListPayload)
            ?? throw new InvalidDataException("The guest did not return a DevTools discovery result.");
        if (payload.Apps is null)
        {
            throw new InvalidDataException("The guest did not return an application list.");
        }
        foreach (var app in payload.Apps)
        {
            app.AppSelector = null;
            if (app.Pid <= 0 || !long.TryParse(app.StartTicksUtc, NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
                start <= 0 || start > DateTime.MaxValue.Ticks)
            {
                app.Status = "identity-unavailable";
                continue;
            }
            if (!await target.Operations.IsTrackedProcessRunningAsync(app.Pid, start, cancellationToken).ConfigureAwait(false))
            {
                app.Status = "exited";
                continue;
            }
            app.AppSelector = host.FindSelector(target, new(app.Pid, start));
        }
        return payload;
    }

    private static void Capture(MemoryStream destination, ReadOnlyMemory<byte> bytes)
    {
        if (destination.Length + bytes.Length > 8 * 1024 * 1024)
        {
            throw new IOException("Guest DevTools discovery exceeded its 8 MiB output limit.");
        }
        destination.Write(bytes.Span);
    }

    internal static async Task<(GuestExecResult Result, GuestDevToolsApplication Application)> ExecuteAsync(
        ParseResult parsed, PreparedTarget target, GuestDevToolsHost host, GuestDevToolsCapabilities? expected,
        string selector, GuestExecCallbacks callbacks, CancellationToken cancellationToken)
    {
        Validate(parsed);
        var application = await host.ResolveApplicationAsync(target, selector, expected, cancellationToken).ConfigureAwait(false);
        var result = await target.Operations.ExecuteAsync(Create(parsed, application), callbacks, cancellationToken).ConfigureAwait(false);
        return (result, application);
    }

    internal static GuestExecRequest Create(ParseResult parsed, GuestDevToolsApplication application)
    {
        Validate(parsed);
        var commands = GetCommands(parsed);
        var comments = commands.Any(command => command.Command is DevToolsCommentsCommand);
        var source = application.Sources?.GuestRoot;
        if (application.BindingId is not null && (source is null || !Path.IsPathFullyQualified(source)))
        {
            throw new InvalidOperationException("The host-owned launch has no verified guest source snapshot.");
        }
        if (comments && application.BindingId is null)
        {
            throw new InvalidOperationException("Persistent host comments require a host-owned guest launch, not a late-attach process selector.");
        }
        return Build(parsed, application, comments, source);
    }

    internal static void Validate(ParseResult parsed)
    {
        if (!ExecutionTargetSelection.IsCommandInvocation(parsed) || !ExecutionTargetSelection.IsTargetAware(parsed) ||
            parsed.GetValue(WinAppRootCommand.GuestInspectionOption) is not null ||
            parsed.GetValue(WinAppRootCommand.GuestCommentsOption) is not null)
        {
            throw new InvalidOperationException("Only a valid public DevTools command can be forwarded to a guest.");
        }
        var commands = GetCommands(parsed);
        if (!commands.Any(command => command.Command is DevToolsCommand) ||
            parsed.CommandResult.Command is not (DevToolsLiveCommand or DevToolsAttachCommand) &&
            !commands.Any(command => command.Command is DevToolsCommentsCommand))
        {
            throw new InvalidOperationException("This command does not support scoped guest execution.");
        }
        if (parsed.GetValue(SharedUiOptions.WindowOption) is not null ||
            (parsed.GetResult(CommentsSharedOptions.SourceRootOption) is { Implicit: false } &&
                parsed.CommandResult.Command is not (DevToolsCommentsUpdateCommand or DevToolsCommentsDeleteCommand)) ||
            parsed.GetResult(DevToolsCommentsListCommand.ProjectOption) is { Implicit: false })
        {
            throw new InvalidOperationException("Guest DevTools uses --app; --window and source-capture overrides cannot change its verified scope.");
        }
        if (parsed.CommandResult.Command is DevToolsInspectCommand &&
            (parsed.GetValue(DevToolsInspectCommand.DepthOption) <= 0 ||
                (parsed.GetValue(DevToolsInspectCommand.AncestorsOption) &&
                    string.IsNullOrWhiteSpace(parsed.GetValue(DevToolsInspectCommand.SelectorArgument)))))
        {
            throw new InvalidOperationException("Inspection requires --depth 1 or greater; --ancestors requires an element selector.");
        }
        if (parsed.CommandResult.Command is DevToolsInspectCommand or DevToolsSearchCommand or
            DevToolsGetPropertyCommand or DevToolsSetPropertyCommand)
        {
            var readMany = parsed.CommandResult.Command is DevToolsInspectCommand or DevToolsSearchCommand;
            var error = DevToolsQueryOptions.Validate(parsed, readMany, out _);
            if (error is not null)
            {
                throw new InvalidOperationException(error);
            }
            if (DevToolsQueryOptions.HasCriteria(parsed) &&
                (parsed.CommandResult.Command is DevToolsGetPropertyCommand && parsed.GetValue(DevToolsGetPropertyCommand.SelectorArgument) is not null ||
                 parsed.CommandResult.Command is DevToolsSetPropertyCommand && parsed.GetValue(DevToolsSetPropertyCommand.SelectorArgument) is not null))
            {
                throw new InvalidOperationException("Use a positional selector or query criteria, not both.");
            }
        }
        switch (parsed.CommandResult.Command)
        {
            case DevToolsSearchCommand when (string.IsNullOrWhiteSpace(parsed.GetValue(DevToolsSearchCommand.QueryArgument)) &&
                !DevToolsQueryOptions.HasCriteria(parsed)) ||
                parsed.GetValue(DevToolsSearchCommand.MaxOption) <= 0:
                throw new InvalidOperationException("Search requires a query and --max 1 or greater.");
            case DevToolsSetPropertyCommand when string.IsNullOrWhiteSpace(parsed.GetValue(SharedDevToolsOptions.PropertyOption)) ||
                (DevToolsQueryOptions.HasCriteria(parsed)
                    ? parsed.GetValue(DevToolsQueryOptions.Value) is null
                    : parsed.GetValue(DevToolsSetPropertyCommand.ValueArgument) is null):
                throw new InvalidOperationException("set-property requires --property and a value.");
            case DevToolsDiagnoseBindingCommand when string.IsNullOrWhiteSpace(parsed.GetValue(DevToolsDiagnoseBindingCommand.PropertyArgument)):
                throw new InvalidOperationException("diagnose-binding requires a property.");
            case DevToolsCallCommand:
                var method = parsed.GetValue(DevToolsCallCommand.MethodArgument);
                var parameters = DevToolsCallParams.Parse(parsed.GetValue(DevToolsCallCommand.ParamsArgument) ?? []);
                if (string.IsNullOrWhiteSpace(method) || method.Trim().StartsWith("Internal.", StringComparison.Ordinal) || !parameters.Ok)
                {
                    throw new InvalidOperationException(parameters.Error ?? "Provide a public DevTools method; Internal methods cannot be called.");
                }
                break;
            case DevToolsCommentsAddCommand:
                if (string.IsNullOrWhiteSpace(parsed.GetValue(DevToolsCommentsAddCommand.TextOption)) ||
                    parsed.GetValue(DevToolsCommentsAddCommand.KindOption) is { } kind && !CommentKind.IsValid(kind) ||
                    parsed.GetValue(DevToolsCommentsAddCommand.FromSelectionOption) &&
                        !string.IsNullOrWhiteSpace(parsed.GetValue(DevToolsCommentsAddCommand.FromElementOption)) ||
                    !DevToolsCommentsAddCommand.TryParseBounds(parsed.GetValue(DevToolsCommentsAddCommand.BoundsOption), out _))
                {
                    throw new InvalidOperationException("Comment text, kind and bounds must be valid; --from-selection and --from-element are alternatives.");
                }
                if (parsed.GetValue(DevToolsCommentsAddCommand.FileOption) is { } file)
                {
                    _ = GuestCommentBinding.ValidateRelativeSource(file);
                }
                break;
            case DevToolsCommentsUpdateCommand when parsed.GetValue(DevToolsCommentsUpdateCommand.StatusOption) is not { } newStatus ||
                !CommentStatus.IsValid(newStatus):
                throw new InvalidOperationException("Update status must be open, resolved, stale or dismissed.");
        }
    }

    internal static string? AppSelector(ParseResult parsed) => GetCommands(parsed)
        .SelectMany(command => command.Children.OfType<OptionResult>())
        .FirstOrDefault(option => option.Option.Name == "--app")?.GetValueOrDefault<string?>();

    private static List<CommandResult> GetCommands(ParseResult parsed)
    {
        var commands = new List<CommandResult>();
        for (var current = parsed.CommandResult; current is not null; current = current.Parent as CommandResult)
        {
            commands.Insert(0, current);
        }
        return commands;
    }

    private static GuestExecRequest Build(ParseResult parsed, GuestDevToolsApplication application, bool comments, string? source)
    {
        var arguments = ForwardOriginalTokens(parsed);
        var pid = application.Process.ProcessId.ToString(CultureInfo.InvariantCulture);
        if (parsed.CommandResult.Command is DevToolsAttachCommand)
        {
            AddBeforeSeparator(arguments, "--pid=" + pid);
        }
        else if (parsed.CommandResult.Command is DevToolsLiveCommand ||
            parsed.CommandResult.Command is DevToolsCommentsDeleteCommand or DevToolsCommentsUpdateCommand ||
            (parsed.CommandResult.Command is DevToolsCommentsAddCommand &&
                (parsed.GetValue(DevToolsCommentsAddCommand.FromSelectionOption) ||
                    parsed.GetValue(DevToolsCommentsAddCommand.FromElementOption) is not null)))
        {
            AddBeforeSeparator(arguments, "--app=" + pid);
        }
        if (comments)
        {
            AddBeforeSeparator(arguments, "--source-root=" + source);
        }
        AddBeforeSeparator(arguments, "--guest-inspection=" + string.Create(CultureInfo.InvariantCulture,
            $"{application.Process.ProcessId}.{application.Process.StartTicksUtc}.{application.BindingId}.{application.Epoch}"));
        return new GuestExecRequest
        {
            UseGuestWinapp = true,
            Arguments = arguments,
            Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["WINAPP_DEVTOOLS_SOURCE_ROOT"] = source ?? string.Empty,
            },
        };
    }

    private static void AddBeforeSeparator(List<string> arguments, string option)
    {
        var separator = arguments.IndexOf("--");
        if (separator < 0)
        {
            arguments.Add(option);
            return;
        }
        arguments.Insert(separator, option);
    }

    private static List<string> ForwardOriginalTokens(ParseResult parsed)
    {
        var hostOnly = new HashSet<Token>(ReferenceEqualityComparer.Instance);
        for (SymbolResult? result = parsed.CommandResult; result is not null; result = result.Parent)
        {
            if (result is not CommandResult command)
            {
                continue;
            }
            foreach (var option in command.Children.OfType<OptionResult>())
            {
                if (!HostOnlyOptions.Contains(option.Option.Name))
                {
                    continue;
                }
                if (option.IdentifierToken is { } identifier)
                {
                    hostOnly.Add(identifier);
                }
                hostOnly.UnionWith(option.Tokens);
            }
        }
        return parsed.Tokens.Where(token => !hostOnly.Contains(token)).Select(token => token.Value).ToList();
    }
}

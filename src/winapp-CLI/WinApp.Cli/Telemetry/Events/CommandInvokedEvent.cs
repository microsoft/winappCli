// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Diagnostics.Telemetry;
using Microsoft.Diagnostics.Telemetry.Internal;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using System.Diagnostics.Tracing;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.Telemetry.Events;

internal record CommandExecutionContext(Dictionary<string, string?> Arguments, Dictionary<string, string?> Options);

[JsonSerializable(typeof(CommandExecutionContext))]
[JsonSourceGenerationOptions]
internal partial class CommandInvokedEventJsonContext : JsonSerializerContext
{
}

[EventData]
internal class CommandInvokedEvent : EventBase
{
    internal CommandInvokedEvent(CommandResult commandResult, DateTime startedTime)
    {
        CommandName = commandResult.Command.GetType().FullName!;
        Context = CreateContext(commandResult.Children);
        ExecutionTargetKind = ResolveTargetKind(commandResult);
        StartedTime = startedTime;
    }

    /// <summary>
    /// The execution target kind this invocation resolved to, from a closed set.
    /// </summary>
    /// <remarks>
    /// Deliberately not the selector the user typed. A selector can carry a provider-defined ID —
    /// a machine name, a desktop name, a VM name — which is exactly the kind of value the rest of
    /// this event redacts to <c>[string]</c>. The kind is one of a handful of names this build
    /// reserves, so it carries no user data while still answering the only question telemetry needs
    /// to ask: is anyone using targets other than the local machine.
    /// </remarks>
    private static string ResolveTargetKind(CommandResult commandResult)
    {
        for (CommandResult? command = commandResult; command is not null; command = command.Parent as CommandResult)
        {
            foreach (var option in command.Children.OfType<OptionResult>())
            {
                if (option.Option != ExecutionTargetSelection.OnOption || option.Implicit)
                {
                    continue;
                }

                if (option.Errors.Any())
                {
                    return InvalidTargetKind;
                }

                return ExecutionTargetSelector.TryParse(
                    option.GetValueOrDefault<string?>(), out var target, out _)
                    ? target!.Kind
                    : InvalidTargetKind;
            }
        }

        return ExecutionTargetRef.LocalKind;
    }

    /// <summary>Recorded when the selector did not name a target, so no kind exists to record.</summary>
    private const string InvalidTargetKind = "invalid";

    internal static string CreateContext(IEnumerable<SymbolResult> children)
    {
        try
        {
            var argumentsDict = children
                .OfType<ArgumentResult>()
                .ToDictionary(a => a.Argument.Name, GetValue);
            var optionsDict = children
                .OfType<OptionResult>()
                .ToDictionary(o => o.Option.Name, GetValue);
            var commandExecutionContext = new CommandExecutionContext(argumentsDict, optionsDict);
            return JsonSerializer.Serialize(commandExecutionContext, CommandInvokedEventJsonContext.Default.CommandExecutionContext);
        }
        catch (Exception ex)
        {
            return $"[error parsing context]: {ex.Message}";
        }
    }

    private static string? GetValue(OptionResult o)
    {
        return o.Option is HelpOption
            ? "true"
            : !o.Errors.Any() ? GetValue(o.Option.ValueType, o.Implicit, () => o.GetValueOrDefault<object?>()) : "[error]";
    }

    private static string? GetValue(ArgumentResult a)
    {
        return !a.Errors.Any()
            ? GetValue(a.Argument.ValueType, a.Implicit, () => a.GetValueOrDefault<object?>())
            : (a.Errors.Any(e => e.Message.StartsWith("Required argument missing for command:")) ? null : "[error]");
    }

    private static string? GetValue(Type valueType, bool isImplicit, Func<object?> value)
    {
        return isImplicit ? null : ((valueType == typeof(string) ||
                                     valueType == typeof(FileInfo) ||
                                     valueType == typeof(DirectoryInfo)) ? "[string]" : value != null ? value() : null)?.ToString();
    }

    public string CommandName { get; private set; }

    public string Context { get; private set; }

    /// <summary>Resolved execution target kind, from the closed reserved set.</summary>
    public string ExecutionTargetKind { get; private set; }

    public DateTime StartedTime { get; private set; }

    public override PartA_PrivTags PartA_PrivTags => PrivTags.ProductAndServiceUsage;

    public override void ReplaceSensitiveStrings(Func<string, string> replaceSensitiveStrings)
    {
        CommandName = replaceSensitiveStrings(CommandName);
        Context = replaceSensitiveStrings(Context);
    }

    public static void Log(CommandResult commandResult)
    {
        TelemetryFactory.Get<ITelemetry>().Log("CommandInvoked_Event", LogLevel.Critical, new CommandInvokedEvent(commandResult, DateTime.Now), TelemetryCorrelation.CurrentId);
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using Spectre.Console;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Commands;

internal class DevToolsDiagnoseBindingCommand : DevToolsLiveCommand
{
    public override string ShortDescription => "Explain a live binding's state, path, and failure";

    public static Argument<string?> SelectorArgument { get; } = new("selector")
    {
        Description = "Element to inspect: the selector printed in brackets, an x:Name, or a handle.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public static Argument<string?> PropertyArgument { get; } = new("property")
    {
        Description = "The bound dependency property, e.g. IsEnabled or Text.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public DevToolsDiagnoseBindingCommand()
        : base("diagnose-binding", "Explain a live binding's path, source, and any failure.")
    {
        Arguments.Add(SelectorArgument);
        Arguments.Add(PropertyArgument);
    }

    public class Handler(
        IDevToolsTargetResolver resolver,
        IAnsiConsole ansiConsole) : LiveHandler(resolver, ansiConsole)
    {
        protected override Task<int> RunAsync(
            DevToolsTarget target,
            ParseResult parseResult,
            bool json,
            CancellationToken cancellationToken)
        {
            var property = parseResult.GetValue(PropertyArgument);
            if (string.IsNullOrWhiteSpace(property))
            {
                return Task.FromResult(Fail(
                    json,
                    target.Pid,
                    "Provide the bound property, e.g. `winapp devtools diagnose-binding SaveButton IsEnabled`."));
            }

            var handle = RequireHandle(
                target,
                parseResult.GetValue(SelectorArgument),
                json,
                "Run `winapp devtools inspect` to list them.",
                out var exitCode, cancellationToken);
            if (handle is null)
            {
                return Task.FromResult(exitCode);
            }

            var response = target.Tap!.RequestBindingDiagnosis(handle, property, cancellationToken);
            if (!response.Ok)
            {
                return Task.FromResult(Fail(json, target.Pid, response.Error!));
            }

            using var doc = response.TryParseResult();
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Task.FromResult(Fail(json, target.Pid, "The DevTools agent returned an unreadable binding report."));
            }

            var root = doc.RootElement;
            var state = ReadString(root, "state");

            // ONE verdict for both shapes. "unavailable" means the question could not be answered, not that
            // the binding is healthy — and `--json` used to report ok:true / exit 0 for exactly that, which is
            // the reading most likely to make an agent conclude a broken binding is fine.
            var diagnosed = state.Length > 0 && state != "unavailable";

            if (json)
            {
                WriteJson(DevToolsJson.Result(target.Pid, diagnosed, response.ResultJson, writer =>
                {
                    WriteIdentity(writer, target, handle, cancellationToken);
                    writer.WriteString("property", property);
                    WriteWarning(writer);
                    if (!diagnosed)
                    {
                        var reason = ReadString(root, "reason");
                        DevToolsJson.WriteError(
                            writer,
                            "binding-unavailable",
                            state.Length == 0
                                ? "The agent did not report a binding state for this property."
                                : reason.Length > 0
                                    ? $"The binding could not be diagnosed: {reason}"
                                    : "The binding could not be diagnosed.");
                    }
                }));
                return Task.FromResult(diagnosed ? 0 : 1);
            }

            DevToolsRender.WriteMarkupLine(Console, DevToolsRender.Header(handle, Describe(target, handle, cancellationToken), property));

            var colour = state switch
            {
                "broken" or "failed" => "red",
                "ok" or "resolved" => "green",
                _ => "yellow",
            };
            DevToolsRender.WriteMarkupLine(Console, $"  state: [{colour}]{Markup.Escape(state.Length == 0 ? "unknown" : state)}[/]");

            // The remaining fields are all optional on the wire and each says something different, so print
            // exactly the ones the agent supplied rather than inventing placeholders for the rest.
            foreach (var (field, label) in Fields)
            {
                var value = ReadString(root, field);
                if (value.Length > 0)
                {
                    DevToolsRender.WriteMarkupLine(Console, $"  {label}: {Markup.Escape(value)}");
                }
            }

            if (state.Length == 0)
            {
                Console.MarkupLine("[grey]The agent did not report a binding state for this property.[/]");
            }

            return Task.FromResult(diagnosed ? 0 : 1);
        }

        private static readonly (string Field, string Label)[] Fields =
        [
            ("path", "path"),
            ("source", "source"),
            ("kind", "kind"),
            ("mode", "mode"),
            ("segment", "failed segment"),
            ("reason", "reason"),
            ("resolvedValue", "resolved value"),
            ("resolvedType", "resolved type"),
            ("targetProperty", "target property"),
            ("targetType", "target type"),
        ];

        private static string ReadString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using Spectre.Console;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Commands;

internal class DevToolsDiagnoseBindingCommand : DevToolsLiveCommand, IHelpExamples
{
    public override string ShortDescription => "Explain a live binding's state, path, and failure";

    public IReadOnlyList<string> Examples { get; } =
    [
        "winapp devtools diagnose-binding <selector> Text -a <app>",
        "winapp devtools diagnose-binding <selector> IsEnabled -a <app> --json",
    ];

    public static Argument<string?> SelectorArgument { get; } = new("selector")
    {
        Description = "Element to inspect: the selector printed in brackets, an x:Name, an AutomationId, or a handle.",
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

            // No binding: a Reactor element's C# may set the property instead. Answer "why does this show X?" from the
            // element record, its call site and the rendering component's props, state and contexts.
            if (state is "" or "none" or "unavailable" &&
                ReactorDiagnosis(target, handle, property, json, cancellationToken) is int reactorExit)
            {
                return Task.FromResult(reactorExit);
            }

            // ONE verdict for both shapes. "unavailable" means the question could not be answered, not that
            // the binding is healthy, and `--json` used to report ok:true / exit 0 for exactly that, which is
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
            // A healthy binding's reason is a scope disclaimer, not a finding; JSON keeps it.
            var healthy = state is "evaluated" or "ok" or "resolved" or "none";
            foreach (var (field, label) in Fields)
            {
                var value = ReadString(root, field);
                if (value.Length > 0 && !(healthy && field == "reason"))
                {
                    DevToolsRender.WriteMarkupLine(Console, $"  {label}: {Markup.Escape(value)}");
                }
            }

            if (state.Length == 0)
            {
                Console.MarkupLine("[grey]The agent did not report a binding state for this property.[/]");
            }
            else if (state == "none" && ReadString(root, "reason").Length == 0)
            {
                Console.MarkupLine("  No binding: the XAML sets this property to a literal value.");
            }

            return Task.FromResult(diagnosed ? 0 : 1);
        }

        /// <summary>
        /// The Reactor answer to "why does this show X?": the WinUI value, the element record value or modifier (or the
        /// ThemeRef) that sets it, where the element was created, and the props, state and contexts of the component
        /// that rendered it, with values the shown value contains marked. Null when Reactor does not set the property.
        /// </summary>
        private int? ReactorDiagnosis(DevToolsTarget target, string handle, string property, bool json, CancellationToken cancellationToken)
        {
            var tap = target.Tap!;
            var reactor = ReactorElementInfo.Read(tap, handle, cancellationToken);
            var owned = reactor?.OwnerOf(property);
            var themeResource = reactor?.ThemeResourceOf(property);
            if (reactor is null || (owned is null && themeResource is null))
            {
                return null;
            }

            var properties = tap.RequestProperties(handle, cancellationToken);
            var row = properties.Ok ? DevToolsPropertyRow.Find(DevToolsPropertyRow.Parse(properties.ResultJson), property) : null;
            var (component, componentJson, _, _) = ReactorComponentState.Read(tap, handle, cancellationToken);
            var shown = row?.Value;
            var name = owned?.Property ?? property;

            if (json)
            {
                WriteJson(DevToolsJson.Serialize(writer =>
                {
                    writer.WriteBoolean("ok", true);
                    writer.WriteNumber("processId", target.Pid);
                    WriteIdentity(writer, target, handle, cancellationToken);
                    writer.WriteString("property", name);
                    writer.WriteString("kind", "reactor");
                    if (shown is not null) { writer.WriteString("value", shown); }
                    if (row?.ValueSource is { } valueSource) { writer.WriteString("valueSource", valueSource); }
                    writer.WriteStartObject("reactor");
                    if (owned is not null)
                    {
                        writer.WriteString("from", owned.From);
                        writer.WriteString("name", owned.Name);
                        writer.WriteString("value", owned.Value);
                        if (owned.Redacted) { writer.WriteBoolean("redacted", true); }
                    }
                    else
                    {
                        writer.WriteString("from", "themeRef");
                    }
                    if (themeResource is not null) { writer.WriteString("resourceKey", themeResource); }
                    if (reactor.Element is not null) { writer.WriteString("element", reactor.Element); }
                    if (reactor.File is not null)
                    {
                        writer.WriteString("file", reactor.File);
                        writer.WriteNumber("line", reactor.Line);
                        if (reactor.Column > 0) { writer.WriteNumber("column", reactor.Column); }
                    }
                    if (reactor.RenderedBy is { } renderedBy) { writer.WriteString("component", renderedBy); }
                    writer.WriteEndObject();
                    if (componentJson is not null && component is not null)
                    {
                        writer.WritePropertyName("renderedBy");
                        using var raw = JsonDocument.Parse(componentJson);
                        raw.RootElement.WriteTo(writer);
                        writer.WriteStartArray("likelySources");
                        foreach (var (kind, label, value) in Sources(component))
                        {
                            if (ReactorComponentState.Feeds(value, shown))
                            {
                                writer.WriteStartObject();
                                writer.WriteString("kind", kind);
                                writer.WriteString("name", label);
                                writer.WriteString("value", value);
                                writer.WriteEndObject();
                            }
                        }
                        writer.WriteEndArray();
                    }
                }));
                return 0;
            }

            DevToolsRender.WriteMarkupLine(Console, DevToolsRender.Header(handle, Describe(target, handle, cancellationToken), name));
            DevToolsRender.WriteMarkupLine(Console, "  kind: [blue]reactor[/] [grey](set in C#, not a binding)[/]");
            if (shown is not null)
            {
                DevToolsRender.WriteMarkupLine(Console, $"  shows: [green]{Markup.Escape(shown)}[/]" +
                    (row?.ValueSource is { } source ? $" [grey][[{Markup.Escape(source)}]][/]" : ""));
            }
            var element = reactor.Element ?? "element";
            if (owned is not null)
            {
                var how = owned.From == "modifier" ? $".{owned.Name}({owned.Value})" : $"{element} {owned.Name} = {owned.Value}";
                DevToolsRender.WriteMarkupLine(Console, $"  ← {Markup.Escape(how)} [grey](the element's C#)[/]");
            }
            if (themeResource is not null)
            {
                DevToolsRender.WriteMarkupLine(Console, $"  ← ThemeRef {Markup.Escape(themeResource)} [grey](follows the theme; see get-resource)[/]");
            }
            if (reactor.ShortSource is { } site)
            {
                DevToolsRender.WriteMarkupLine(Console, $"  ← created at {Markup.Escape(site)}" +
                    (reactor.RenderedBy is { } by ? $" in <{Markup.Escape(by)}>" : ""));
            }
            if (component is not null)
            {
                DevToolsRender.WriteMarkupLine(Console, $"  ← <{Markup.Escape(component.Name)}> as it last rendered" +
                    (shown is { Length: > 0 } ? " [grey](green: values the shown value contains)[/]:" : ":"));
                foreach (var line in component.Lines("    ", shown))
                {
                    DevToolsRender.WriteMarkupLine(Console, line);
                }
            }
            return 0;
        }

        private static IEnumerable<(string Kind, string Name, string Value)> Sources(ReactorComponentState component)
        {
            foreach (var (name, value, redacted) in component.Props ?? [])
            {
                if (!redacted) { yield return ("prop", name, value); }
            }
            foreach (var hook in component.ValueHooks)
            {
                if (!hook.Redacted && hook.Kind != "context") { yield return ("hook", $"[{hook.Index}] {hook.Kind}", hook.Value!); }
            }
            foreach (var context in component.Contexts)
            {
                if (!context.Redacted) { yield return ("context", context.Name, context.Value); }
            }
        }

        private static readonly (string Field, string Label)[] Fields =
        [
            ("path", "path"),
            ("source", "source"),
            ("kind", "kind"),
            ("mode", "mode"),
            ("segment", "failed segment"),
            ("reason", "reason"),
            ("sourceValue", "source value"),
            ("converterEvaluation", "converter"),
            ("resolvedValue", "resolved value"),
            ("resolvedType", "resolved type"),
            ("targetValue", "target value"),
            ("targetUnavailable", "target unavailable"),
            ("targetProperty", "target property"),
            ("targetType", "target type"),
        ];

        private static string ReadString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
    }
}

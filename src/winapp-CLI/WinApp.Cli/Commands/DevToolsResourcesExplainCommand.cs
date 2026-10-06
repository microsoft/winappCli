// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using Spectre.Console;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Commands;

/// <summary>
/// <c>winapp devtools resources explain &lt;selector&gt; &lt;property&gt;</c>: the lookup chain behind one live value,
/// from the Style setter or local value that set it, through the resource key it names, to the dictionary and
/// theme branch that defines that key — ending at the file and line to edit.
/// </summary>
internal class DevToolsResourcesExplainCommand : DevToolsLiveCommand, IHelpExamples
{
    public override string ShortDescription => "Show which setter, resource key, and theme produced a value";

    public IReadOnlyList<string> Examples { get; } =
    [
        "winapp devtools resources explain <selector> Background -a <app>",
        "winapp devtools resources explain <selector> Foreground -a <app> --json",
    ];

    public static Argument<string> SelectorArgument { get; } = new("selector")
    {
        Description = "Element to explain: the selector printed in brackets, an x:Name, an AutomationId, or a handle.",
    };

    public static Argument<string> PropertyArgument { get; } = new("property")
    {
        Description = "The property to explain, e.g. Background.",
    };

    public DevToolsResourcesExplainCommand()
        : base("explain", "Show where a live property value comes from: the Style setter or local value that set it, " +
            "the resource key it uses, the dictionary and theme branch that define the key, and the file and line to edit. " +
            "Reads the app's XAML from the project folder; the value itself is read live.")
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
            var property = parseResult.GetValue(PropertyArgument)!.Trim();
            var handle = RequireHandle(target, parseResult.GetValue(SelectorArgument), json,
                "Run `winapp devtools inspect` to list them.", out var exitCode, cancellationToken);
            if (handle is null)
            {
                return Task.FromResult(exitCode);
            }

            var response = target.Tap!.RequestProperties(handle, cancellationToken);
            if (!response.Ok)
            {
                return Task.FromResult(Fail(json, target.Pid, response.Error!));
            }

            using var doc = response.TryParseResult();
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("props", out var props) || props.ValueKind != JsonValueKind.Array)
            {
                return Task.FromResult(Fail(json, target.Pid, "The DevTools agent returned an unreadable property list."));
            }

            JsonElement? raw = null;
            DevToolsPropertyRow? row = null, styleRow = null, themeRow = null;
            var total = 0;
            foreach (var element in props.EnumerateArray())
            {
                if (DevToolsPropertyRow.FromElement(element) is not { } parsed)
                {
                    return Task.FromResult(Fail(json, target.Pid, "The DevTools agent returned an invalid property row."));
                }
                total++;
                if (row is null && parsed.Name.Equals(property, StringComparison.OrdinalIgnoreCase))
                {
                    (raw, row) = (element, parsed);
                }
                styleRow ??= parsed.Name == "Style" ? parsed : null;
                themeRow ??= parsed.Name == "ActualTheme" ? parsed : null;
            }
            if (row is null)
            {
                return Task.FromResult(Fail(json, target.Pid,
                    $"'{property}' is not among the {total} {(total == 1 ? "property" : "properties")} the agent reports for this element. " +
                    "Run `winapp devtools get-property <selector> --all` to see which it reads."));
            }
            if (row.Redacted)
            {
                return Task.FromResult(Fail(json, target.Pid, $"'{row.Name}' holds a secret; DevTools does not read it.", "redacted"));
            }

            var sourceRoot = DevToolsJson.SourceRoot(target.Tap!, cancellationToken);
            var index = sourceRoot is not null && Directory.Exists(sourceRoot) ? XamlResourceIndex.Build(sourceRoot) : null;
            var theme = themeRow?.Value is { Length: > 0 } t && !t.Equals("Default", StringComparison.OrdinalIgnoreCase) ? t : null;
            var explanation = DevToolsResourceExplainer.Explain(raw!.Value, row, styleRow, theme, index);
            var notes = explanation.Notes.ToList();
            notes.AddRange(OverrideNotes(target.Tap!, explanation, cancellationToken));
            if (index is { Truncated: true })
            {
                notes.Add($"The project has more than {XamlResourceIndex.MaximumFiles} XAML files; only the first were read.");
            }

            if (json)
            {
                WriteJson(DevToolsJson.Serialize(writer =>
                {
                    writer.WriteBoolean("ok", true);
                    writer.WriteNumber("processId", target.Pid);
                    WriteIdentity(writer, target, handle, cancellationToken);
                    WriteWarning(writer);
                    WriteExplanation(writer, explanation, notes, raw.Value, sourceRoot);
                }));
                return Task.FromResult(0);
            }

            DevToolsRender.WriteMarkupLine(Console, DevToolsRender.Header(handle, Describe(target, handle, cancellationToken)));
            foreach (var line in Render(explanation, notes))
            {
                DevToolsRender.WriteMarkupLine(Console, line);
            }
            return Task.FromResult(0);
        }
    }

    // A `resources set` override makes the live value differ from the file the trace points at; say so.
    private static IEnumerable<string> OverrideNotes(VisualTreeTap tap, ResourceExplanation explanation, CancellationToken cancellationToken)
    {
        foreach (var resource in explanation.Resources.Where(r => !r.Framework))
        {
            var response = tap.RequestResourceList(resource.Key, null, cancellationToken);
            if (response.Ok && DevToolsResourcesListCommand.Parse(response.ResultJson) is { } listing &&
                listing.Entries.FirstOrDefault(e => e.Key == resource.Key && e.Overridden) is { } live)
            {
                yield return $"{resource.Key} was changed by `winapp devtools resources set` and is now {live.Value}; the file still has the old value. " +
                    $"Run `winapp devtools resources reset {resource.Key}` to restore it.";
            }
        }
    }

    private const string Step = "  [grey]←[/] ";

    internal static IEnumerable<string> Render(ResourceExplanation explanation, IReadOnlyList<string> notes)
    {
        var origin = explanation.Origin;
        var shown = explanation.Value.Length > 0 ? Markup.Escape(explanation.Value)
            : explanation.ValueType.EndsWith(".Object", StringComparison.Ordinal) ? "[grey]null[/]" : "[grey](no text form)[/]";
        yield return $"{Markup.Escape(explanation.Property)} = {shown}  " +
            $"[grey]({Markup.Escape(explanation.ValueType)})[/]";

        switch (origin.Kind)
        {
            case "style":
                var name = origin.StyleKey ?? $"implicit Style for {origin.TargetType ?? "this type"}";
                var kind = origin.StyleKind is null || origin.StyleKey is null ? string.Empty : $" ({origin.StyleKind})";
                var styleWhere = origin.StyleDefinedIn == "winui"
                    ? "  [grey]WinUI resources[/]"
                    : Where(origin.At ?? origin.StyleAt) is { Length: > 0 } w ? "  " + w : string.Empty;
                yield return Step + $"{Label("Style setter")}{Markup.Escape(name)}{kind}{styleWhere}";
                if (origin.BasedOn is { Count: > 0 } basedOn)
                {
                    yield return $"{Indent}[grey]via BasedOn {Markup.Escape(string.Join(" → ", basedOn))}[/]";
                }
                if (origin.Authored is { } setter)
                {
                    yield return $"{Indent}[grey]{Markup.Escape(explanation.Property)} = {Markup.Escape(setter)}[/]";
                }
                break;
            case "defaultStyle":
                yield return Step + $"{Label("Default style")}WinUI default style for {Markup.Escape(origin.TargetType ?? "this control")}";
                break;
            case "local":
                yield return Step + $"{Label("Local value")}{Markup.Escape(origin.Authored ?? explanation.Value)}{Spaced(origin.At)}";
                break;
            case "default":
                yield return Step + $"{Label("Default value")}nothing sets it";
                break;
            default:
                yield return Step + $"{Label(origin.Source)}{Spaced(origin.At)}".TrimEnd();
                break;
        }

        foreach (var resource in explanation.Resources)
        {
            var label = resource.Kind switch
            {
                "themeResource" => "ThemeResource",
                "staticResource" => "StaticResource",
                _ => "alias of",
            };
            var theme = resource.Kind == "themeResource" && explanation.Theme is { } t ? $"   [grey]element theme: {Markup.Escape(t)}[/]" : string.Empty;
            yield return Step + $"{Label(label)}{Markup.Escape(resource.Key)}{theme}";

            var applied = resource.Definitions.FirstOrDefault(d => d.Applies);
            if (applied is not null)
            {
                var branch = applied.Theme is { } b ? $" [grey][[{Markup.Escape(b)}]][/]" : string.Empty;
                var value = applied.AliasOf is null && applied.Value is { } v ? $"  [grey]= {Markup.Escape(v)}[/]" : string.Empty;
                yield return Step + $"{Label("defined in")}{Where(applied.At)}{branch}{value}";
            }
            else if (resource.Framework)
            {
                yield return Step + $"{Label("defined in")}WinUI default resources";
            }

            var others = resource.Definitions.Where(d => !d.Applies).ToList();
            foreach (var other in others.Take(3))
            {
                var why = !other.InScope
                    ? "not in scope here"
                    : other.Theme is { } ot ? $"{ot} theme only" : "not applied";
                yield return $"{Indent}[grey]also in {Markup.Escape(other.At.ToString())} ({Markup.Escape(why)})[/]";
            }
            if (others.Count > 3)
            {
                yield return $"{Indent}[grey]and {others.Count - 3} more definitions[/]";
            }
        }

        if (ChangeIt(explanation) is { } change)
        {
            yield return string.Empty;
            yield return $"Change it: {Markup.Escape(change)}";
        }
        foreach (var note in notes)
        {
            yield return $"[grey]{Markup.Escape(note)}[/]";
        }
    }

    private const string Indent = "                   ";

    private static string Label(string label) => Markup.Escape(label.PadRight(15));

    private static string Where(ExplainLocation? at) => at is null ? string.Empty : $"[grey]{Markup.Escape(at.ToString())}[/]";

    private static string Spaced(ExplainLocation? at) => at is null ? string.Empty : "  " + Where(at);

    // The plain-language next step: edit the definition that applies, else the setter or element, and for a
    // WinUI resource, point at the app-wide override.
    internal static string? ChangeIt(ResourceExplanation explanation)
    {
        var origin = explanation.Origin;
        var framework = explanation.Resources is [.., { Framework: true } last] ? last.Key : null;
        string? edit = null;
        if (explanation.ChangeAt is { } at)
        {
            var scope = explanation.Resources is [.., { Framework: false } definition] && at != origin.At
                ? $"changes {definition.Key} everywhere it is used"
                : origin.Kind == "style"
                    ? "affects elements that use this Style"
                    : "affects this element only";
            edit = $"edit {at} ({scope})";
        }
        var overrideKey = framework is null ? null : $"override {framework} in App.xaml's resources (affects every control that uses it)";
        return (edit, overrideKey) switch
        {
            (null, null) => null,
            ({ } e, null) => e,
            (null, { } o) => char.ToUpperInvariant(o[0]) + o[1..],
            ({ } e, { } o) => $"{e}, or {o}",
        };
    }

    private static void WriteExplanation(
        Utf8JsonWriter writer, ResourceExplanation explanation, IReadOnlyList<string> notes, JsonElement raw, string? sourceRoot)
    {
        writer.WriteString("property", explanation.Property);
        writer.WriteString("value", explanation.Value);
        writer.WriteString("valueType", explanation.ValueType);
        writer.WriteString("valueSource", explanation.Origin.Source);
        if (explanation.Theme is { } theme)
        {
            writer.WriteString("theme", theme);
        }

        var origin = explanation.Origin;
        writer.WriteStartObject("origin");
        writer.WriteString("kind", origin.Kind);
        WriteOptional(writer, "styleKind", origin.StyleKind);
        WriteOptional(writer, "styleKey", origin.StyleKey);
        WriteOptional(writer, "styleDefinedIn", origin.StyleDefinedIn);
        WriteOptional(writer, "targetType", origin.TargetType);
        WriteLocation(writer, "style", origin.StyleAt, sourceRoot);
        WriteLocation(writer, "at", origin.At, sourceRoot);
        WriteOptional(writer, "authored", origin.Authored);
        if (origin.BasedOn is { } basedOn)
        {
            writer.WriteStartArray("basedOn");
            foreach (var key in basedOn)
            {
                writer.WriteStringValue(key);
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();

        writer.WriteStartArray("resources");
        foreach (var resource in explanation.Resources)
        {
            writer.WriteStartObject();
            writer.WriteString("key", resource.Key);
            writer.WriteString("kind", resource.Kind);
            writer.WriteString("definedIn", resource.Framework ? "winui" : "app");
            writer.WriteStartArray("definitions");
            foreach (var definition in resource.Definitions)
            {
                writer.WriteStartObject();
                WriteLocationFields(writer, definition.At, sourceRoot);
                writer.WriteString("scope", definition.Scope);
                WriteOptional(writer, "theme", definition.Theme);
                writer.WriteBoolean("inScope", definition.InScope);
                writer.WriteBoolean("applies", definition.Applies);
                WriteOptional(writer, "value", definition.Value);
                WriteOptional(writer, "aliasOf", definition.AliasOf);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        if (ChangeIt(explanation) is { } change)
        {
            writer.WriteStartObject("changeIt");
            writer.WriteString("summary", change);
            if (explanation.ChangeAt is { } at)
            {
                WriteLocationFields(writer, at, sourceRoot);
            }
            if (explanation.Resources is [.., { Framework: true } framework])
            {
                writer.WriteString("overrideKey", framework.Key);
            }
            writer.WriteEndObject();
        }

        writer.WriteStartArray("notes");
        foreach (var note in notes)
        {
            writer.WriteStringValue(note);
        }
        writer.WriteEndArray();

        if (raw.TryGetProperty("chain", out var chain) && chain.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray("chain");
            foreach (var entry in chain.EnumerateArray())
            {
                DevToolsJson.WriteChainEntry(writer, entry, sourceRoot);
            }
            writer.WriteEndArray();
        }
    }

    private static void WriteLocation(Utf8JsonWriter writer, string name, ExplainLocation? at, string? sourceRoot)
    {
        if (at is null)
        {
            return;
        }
        writer.WriteStartObject(name);
        WriteLocationFields(writer, at, sourceRoot);
        writer.WriteEndObject();
    }

    private static void WriteLocationFields(Utf8JsonWriter writer, ExplainLocation at, string? sourceRoot)
    {
        writer.WriteString("file", at.File);
        if (DevToolsJson.ProjectPath(sourceRoot, at.File) is { } path)
        {
            writer.WriteString("path", path);
        }
        writer.WriteNumber("line", at.Line);
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }
}

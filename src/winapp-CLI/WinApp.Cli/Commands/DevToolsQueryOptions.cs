// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using Spectre.Console;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Commands;

internal static class DevToolsQueryOptions
{
    public static Option<string?> OfType { get; } = new("--of-type")
    {
        Description = "Match an exact XAML runtime type; short names must be unambiguous.",
    };
    public static Option<string[]> With { get; } = new("--with")
    {
        Description = "Property<operator>Literal predicate. Repeat for AND; quote the whole argument.",
        Arity = ArgumentArity.OneOrMore,
        AllowMultipleArgumentsPerToken = false,
    };
    public static Option<string?> Fields { get; } = new("--fields")
    {
        Description = "Comma-separated runtime property names to return on every match.",
    };
    public static Option<string?> Value { get; } = new("--value")
    {
        Description = "New literal value for a query-targeted set; omit the positional selector.",
    };

    public static void Add(Command command, bool fields = false, bool write = false)
    {
        command.Options.Add(OfType);
        command.Options.Add(With);
        if (fields)
        {
            command.Options.Add(Fields);
        }

        if (write)
        {
            command.Options.Add(Value);
        }
    }

    public static bool HasCriteria(ParseResult parse) =>
        parse.GetValue(OfType) is not null || parse.GetValue(With) is { Length: > 0 };

    public static bool HasReadQuery(ParseResult parse) => HasCriteria(parse) || parse.GetValue(Fields) is not null;

    internal static bool PropertyName(string name) => name.Length is > 0 and <= 256 &&
        name.Split('.').All(part => part.Length > 0 && part.All(c => char.IsLetterOrDigit(c) || c == '_'));

    internal static string? Validate(ParseResult parse, bool fields, out string[] selectedFields)
    {
        selectedFields = [];
        if (parse.GetValue(OfType) is string type && string.IsNullOrWhiteSpace(type))
        {
            return "--of-type requires a runtime type.";
        }
        var predicates = parse.GetValue(With) ?? [];
        if (predicates.Length > 16)
        {
            return "At most 16 --with predicates are supported.";
        }

        foreach (var predicate in predicates)
        {
            var position = predicate.IndexOfAny(['=', '!', '<', '>', '*']);
            if (position <= 0 || predicate.Length > 4096 || !PropertyName(predicate[..position]))
            {
                return "Expected Property<operator>Literal, for example --with 'FontSize>=20'.";
            }
            var length = position + 1 < predicate.Length && predicate[position + 1] == '=' ? 2 : 1;
            if (predicate.Substring(position, length) is not ("==" or "!=" or "<" or "<=" or ">" or ">=" or "*="))
            {
                return "Supported predicate operators: ==, !=, <, <=, >, >=, *=.";
            }
            if (predicate[position] is '<' or '>' && position + length < predicate.Length &&
                predicate[position + length] is '=' or '!' or '<' or '>' or '*')
            {
                return "Malformed ordered predicate operator. Use <, <=, >, or >= followed by a numeric literal.";
            }
        }
        if (fields && parse.GetValue(Fields) is string list)
        {
            selectedFields = list.Split(',').Select(field => field.Trim()).ToArray();
            if (selectedFields.Length > 32 || selectedFields.Any(field => !PropertyName(field)))
            {
                return "--fields requires 1 to 32 comma-separated runtime property names.";
            }
            selectedFields = selectedFields.Distinct(StringComparer.Ordinal).ToArray();
        }
        return null;
    }

    public static int Run(DevToolsTarget target, ParseResult parse, IAnsiConsole console, bool json,
        CancellationToken cancellationToken, bool tree = false, bool exact = false, bool write = false,
        string? rootHandle = null, int depth = -1, bool authored = false, string? text = null, int max = int.MaxValue,
        DevToolsProtocolResponse? existingResponse = null)
    {
        var validation = Validate(parse, !exact, out var fields);
        if (validation is not null)
        {
            return Fail(validation, "bad-args");
        }

        if (write && cancellationToken.IsCancellationRequested)
        {
            return Fail("The request was cancelled before dispatch. No mutation was attempted.", "not-applied");
        }
        DevToolsProtocolResponse response;
        try
        {
            response = existingResponse ?? Request(target, parse, exact, cancellationToken, write, rootHandle, depth, authored, text, tree);
        }
        catch (OperationCanceledException) when (write)
        {
            return Fail("The write was cancelled while awaiting acknowledgement. Its outcome is indeterminate; read the target property to reconcile.", "indeterminate");
        }
        if (!response.Ok)
        {
            var error = response.Error!;
            if (write && error.Token == "no-response")
            {
                return Fail("The write outcome is indeterminate. Read the target property to reconcile; do not retry automatically.", "indeterminate");
            }
            return Fail(error.Message, error.Token);
        }
        using var document = response.TryParseResult();
        if (document is null || !ValidResult(document.RootElement, write))
        {
            return Fail(write ? "The write acknowledgement is unreadable; its outcome is indeterminate." : "The agent returned an invalid query result.",
                write ? "indeterminate" : "invalid-response");
        }
        var result = document.RootElement;
        var rows = result.GetProperty("matches");
        if (rows.EnumerateArray().Any(row => fields.Any(field =>
            row.GetProperty("fields").EnumerateArray().Count(value => value.GetProperty("name").GetString() == field) != 1)))
        {
            return Fail("The agent did not report every requested field exactly once.", "invalid-response");
        }
        var ok = result.GetProperty("ok");
        var shown = rows.EnumerateArray().Take(max).ToArray();
        var warnings = fields.Where(field => rows.GetArrayLength() > 0 && rows.EnumerateArray().All(row =>
            row.GetProperty("fields").EnumerateArray().Any(value =>
                value.GetProperty("name").GetString() == field && value.GetProperty("valueState").GetString() == "unavailable")))
            .Select(field => $"{field} is unavailable on every returned match.").ToArray();
        if (json)
        {
            console.Profile.Out.Writer.WriteLine(DevToolsJson.Serialize(writer =>
            {
                writer.WriteNumber("processId", target.Pid);
                writer.WriteNumber("matchCount", rows.GetArrayLength());
                writer.WriteBoolean("hasMore", shown.Length < rows.GetArrayLength());
                foreach (var property in result.EnumerateObject())
                {
                    if (property.Name is "matches" or "context")
                    {
                        continue;
                    }

                    if (property.Name == "diagnostics")
                    {
                        // Preserve the agent's bounded UTF-8 representation without expanding Unicode escapes.
                        writer.WritePropertyName(property.Name);
                        writer.WriteRawValue(property.Value.GetRawText());
                    }
                    else
                    {
                        property.WriteTo(writer);
                    }
                }
                writer.WriteStartArray("matches");
                foreach (var row in shown)
                {
                    WriteRow(writer, row);
                }

                writer.WriteEndArray();
                if (tree && result.TryGetProperty("context", out var context))
                {
                    writer.WriteStartArray("context");
                    foreach (var row in context.EnumerateArray())
                    {
                        WriteRow(writer, row);
                    }

                    writer.WriteEndArray();
                }
                writer.WriteStartArray("warnings");
                foreach (var warning in warnings)
                {
                    writer.WriteStringValue(warning);
                }

                writer.WriteEndArray();
            }));
        }
        else
        {
            {
                var displayed = shown.ToList();
                if (tree && result.TryGetProperty("context", out var context))
                {
                    displayed.AddRange(context.EnumerateArray());
                }

                var handles = displayed.Select(row => row.GetProperty("handle").GetString()!).ToHashSet(StringComparer.Ordinal);
                var visited = new HashSet<string>(StringComparer.Ordinal);
                void Render(JsonElement row, int indent)
                {
                    var handle = row.GetProperty("handle").GetString()!;
                    if (!visited.Add(handle))
                    {
                        return;
                    }

                    var values = row.GetProperty("fields").EnumerateArray().Select(FormatField);
                    var contextLabel = row.GetProperty("context").GetBoolean() ? " (context)" : "";
                    DevToolsRender.WriteTextLine(console, new string(' ', Math.Min(indent, 32) * 2) +
                        $"[{Selector(row)}] {DevToolsFormat.ShortTypeName(row.GetProperty("type").GetString()!)}{contextLabel} " + string.Join(" ", values));
                    if (tree)
                    {
                        foreach (var child in displayed.Where(child =>
                        child.TryGetProperty("parentHandle", out var parent) && parent.GetString() == handle))
                        {
                            Render(child, indent + 1);
                        }
                    }
                }
                foreach (var row in displayed.Where(row => !tree || !row.TryGetProperty("parentHandle", out var parent) ||
                    !handles.Contains(parent.GetString()!)))
                {
                    Render(row, 0);
                }

                foreach (var warning in warnings)
                {
                    console.WriteLine(warning);
                }

                if (result.TryGetProperty("fallback", out var fallback))
                {
                    console.WriteLine(fallback.GetString()!);
                }

                console.WriteLine($"{rows.GetArrayLength()} matches; {result.GetProperty("unevaluated").GetInt32()} unevaluated; " +
                    (result.GetProperty("complete").GetBoolean() ? "complete within requested scope." : "incomplete."));
                if (shown.Length < rows.GetArrayLength())
                {
                    console.WriteLine($"Showing {shown.Length}; --max limits output, not matching.");
                }

                if (result.TryGetProperty("before", out var before))
                {
                    DevToolsRender.WriteTextLine(console, "Before: " + FormatField(before));
                }

                if (result.TryGetProperty("after", out var after))
                {
                    DevToolsRender.WriteTextLine(console, "After: " + FormatField(after));
                }

                if (write)
                {
                    console.WriteLine("Write status: " + result.GetProperty("status").GetString());
                }

                if (result.TryGetProperty("error", out var error))
                {
                    console.WriteLine("Error: " + error.GetString());
                }

                if (result.TryGetProperty("reasons", out var reasons))
                {
                    foreach (var reason in reasons.EnumerateObject())
                    {
                        console.WriteLine($"{reason.Name}: {reason.Value.GetInt32()}");
                    }
                }
                if (result.TryGetProperty("diagnostics", out var diagnostics))
                {
                    foreach (var candidate in diagnostics.GetProperty("candidates").EnumerateArray())
                    {
                        var census = candidate.GetProperty("census");
                        DevToolsRender.WriteTextLine(console, $"[{candidate.GetProperty("selector").GetString()}] " +
                            $"census name={census.GetProperty("name").GetRawText()} type={census.GetProperty("type").GetRawText()}; " +
                            $"{candidate.GetProperty("phase").GetString()}: {candidate.GetProperty("reason").GetString()}");
                        foreach (var predicate in candidate.GetProperty("predicates").EnumerateArray())
                        {
                            DevToolsRender.WriteTextLine(console, $"  {predicate.GetProperty("property").GetString()} {predicate.GetProperty("operator").GetString()}: " +
                                $"{predicate.GetProperty("truth").GetString()}; state={predicate.GetProperty("valueState").GetString()}; " +
                                $"type={predicate.GetProperty("valueType").GetString()}; reason={predicate.GetProperty("reason").GetString()}");
                        }
                    }
                    if (diagnostics.GetProperty("truncated").GetBoolean())
                    {
                        console.WriteLine($"Candidate diagnostics truncated; {diagnostics.GetProperty("omitted").GetInt32()} omitted. Query completeness is unchanged.");
                    }
                }
            }
        }

        return ok.GetBoolean() ? 0 : 1;

        int Fail(string message, string token)
        {
            if (json)
            {
                console.Profile.Out.Writer.WriteLine(DevToolsJson.Error(target.Pid, message, token));
            }
            else
            {
                console.WriteLine("Error: " + message);
            }

            return 1;
        }
    }

    internal static DevToolsProtocolResponse Request(DevToolsTarget target, ParseResult parse, bool exact, CancellationToken cancellationToken,
        bool write = false, string? rootHandle = null, int depth = -1, bool authored = false, string? text = null, bool tree = false)
    {
        var error = Validate(parse, !exact, out var fields);
        if (error is not null)
        {
            return DevToolsProtocolResponse.Failure(new(-32602, "bad-args", error));
        }
        return target.Tap!.Request(write ? "Property.setQuery" : "VisualTree.query", writer =>
        {
            if (parse.GetValue(OfType) is string type)
            {
                writer.WriteString("ofType", type);
            }

            writer.WriteStartArray("with");
            foreach (var predicate in parse.GetValue(With) ?? [])
            {
                writer.WriteStringValue(predicate);
            }

            writer.WriteEndArray();
            if (!write)
            {
                writer.WriteString("mode", exact ? "one" : "many");
                writer.WriteNumber("depth", depth);
                writer.WriteBoolean("authored", authored);
                writer.WriteStartArray("fields");
                foreach (var field in fields)
                {
                    writer.WriteStringValue(field);
                }

                writer.WriteEndArray();
                if (!string.IsNullOrEmpty(text))
                {
                    writer.WriteString(tree ? "filter" : "text", tree ? DevToolsFormat.NormalizeQuery(text) : text);
                }
            }
            if (rootHandle is not null)
            {
                writer.WriteString("rootHandle", rootHandle);
            }

            if (exact && parse.GetValue(SharedDevToolsOptions.PropertyOption) is string property)
            {
                writer.WriteString("property", property);
            }

            if (write)
            {
                writer.WriteString("value", parse.GetValue(Value));
                if (parse.GetValue(SharedDevToolsOptions.TypeOption) is string writeType)
                {
                    writer.WriteString("writeType", writeType);
                }
            }
        }, cancellationToken: cancellationToken);
    }

    internal static bool ValidResult(JsonElement result, bool write)
    {
        static bool String(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String;
        static bool Boolean(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False;
        static bool Count(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out var count) && count is >= 0 and <= int.MaxValue;
        static bool Field(JsonElement field) => field.ValueKind == JsonValueKind.Object &&
            String(field, "name") && String(field, "valueType") && String(field, "valueState") && String(field, "bindingState") &&
            field.TryGetProperty("value", out var value) &&
            value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null;
        static bool Node(JsonElement row) =>
            row.ValueKind == JsonValueKind.Object && String(row, "handle") && String(row, "name") &&
            String(row, "type") && Boolean(row, "uniqueName") && Boolean(row, "context") &&
            row.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array &&
            fields.EnumerateArray().All(Field) &&
            (!row.TryGetProperty("parentHandle", out _) || String(row, "parentHandle")) &&
            (!row.TryGetProperty("id", out _) || String(row, "id"));
        if (result.ValueKind != JsonValueKind.Object || !Boolean(result, "ok") || !String(result, "status") ||
            !Boolean(result, "complete") || !Boolean(result, "truncated") ||
            !Count(result, "evaluated") || !Count(result, "unevaluated") ||
            !Count(result, "scopeNodes") || !Count(result, "censusNodes") ||
            !Count(result, "propertyReads") || !Count(result, "propertyRows") || !Count(result, "uiThreadMicroseconds") ||
            !result.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Object ||
            !String(scope, "rootHandle") || !String(scope, "ofType") || !Boolean(scope, "authored") ||
            !scope.TryGetProperty("depth", out var depth) || depth.ValueKind != JsonValueKind.Number ||
            !depth.TryGetInt32(out var depthValue) || depthValue < -1 ||
            !result.TryGetProperty("reasons", out var reasons) || reasons.ValueKind != JsonValueKind.Object ||
            reasons.EnumerateObject().Any(reason => !Count(reasons, reason.Name)))
        {
            return false;
        }
        foreach (var key in new[] { "error", "fallback" })
        {
            if (result.TryGetProperty(key, out _) && !String(result, key))
            {
                return false;
            }
        }
        foreach (var key in new[] { "before", "after" })
        {
            if (result.TryGetProperty(key, out var field) && !Field(field))
            {
                return false;
            }
        }
        foreach (var key in new[] { "matches", "context" })
        {
            if (!result.TryGetProperty(key, out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                return false;
            }
            foreach (var row in rows.EnumerateArray())
            {
                if (!Node(row))
                {
                    return false;
                }
            }
        }
        if (result.TryGetProperty("diagnostics", out var diagnostics))
        {
            if (diagnostics.ValueKind != JsonValueKind.Object || !Count(diagnostics, "omitted") ||
                !Boolean(diagnostics, "truncated") ||
                diagnostics.GetProperty("truncated").GetBoolean() != (diagnostics.GetProperty("omitted").GetInt32() > 0) ||
                !diagnostics.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array ||
                candidates.GetArrayLength() > 32 || System.Text.Encoding.UTF8.GetByteCount(diagnostics.GetRawText()) > 64 * 1024)
            {
                return false;
            }
            foreach (var candidate in candidates.EnumerateArray())
            {
                if (candidate.ValueKind != JsonValueKind.Object || !String(candidate, "selector") ||
                    !String(candidate, "phase") || candidate.GetProperty("phase").GetString() is not ("initial" or "final") ||
                    !String(candidate, "reason") || !candidate.TryGetProperty("census", out var census) || !Node(census) ||
                    candidate.GetProperty("selector").GetString() != census.GetProperty("handle").GetString() ||
                    !DevToolsSelector.IsHandle(candidate.GetProperty("selector").GetString()!) ||
                    !candidate.TryGetProperty("predicates", out var predicates) || predicates.ValueKind != JsonValueKind.Array ||
                    predicates.GetArrayLength() > 16)
                {
                    return false;
                }
                foreach (var predicate in predicates.EnumerateArray())
                {
                    if (predicate.ValueKind != JsonValueKind.Object || !String(predicate, "property") ||
                        !String(predicate, "operator") || !String(predicate, "truth") ||
                        predicate.GetProperty("truth").GetString() is not ("true" or "false" or "unknown") ||
                        !String(predicate, "reason") || !String(predicate, "valueType") || !String(predicate, "valueState"))
                    {
                        return false;
                    }
                }
            }
        }
        if (result.GetProperty("complete").GetBoolean() &&
            (result.GetProperty("truncated").GetBoolean() || result.GetProperty("unevaluated").GetInt32() != 0))
        {
            return false;
        }
        return !write || !result.GetProperty("ok").GetBoolean() ||
            (result.GetProperty("status").GetString() == "applied" &&
             result.GetProperty("complete").GetBoolean() &&
             result.GetProperty("matches").GetArrayLength() == 1 &&
             result.TryGetProperty("before", out _) && result.TryGetProperty("after", out _));
    }

    private static string FormatField(JsonElement field)
    {
        var state = field.GetProperty("valueState").GetString();
        var value = state is "value" or "binding" ? field.GetProperty("value").GetRawText() :
            state == "null" ? "null" : state == "object" ? $"<{field.GetProperty("valueType").GetString()}>" :
            state == "unavailable" ? "<n/a>" : $"<{state}>";
        return $"{field.GetProperty("name").GetString()}={value}" +
            (field.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True ? "..." : "") +
            (field.GetProperty("bindingState").GetString() == "known" ? " [binding]" : "");
    }

    private static string Selector(JsonElement row) => DevToolsSelector.Display(
        row.GetProperty("handle").GetString()!, row.GetProperty("type").GetString()!, row.GetProperty("name").GetString()!,
        row.TryGetProperty("id", out var id) ? id.GetString() : null, row.GetProperty("uniqueName").GetBoolean());

    private static void WriteRow(Utf8JsonWriter writer, JsonElement row)
    {
        writer.WriteStartObject();
        writer.WriteString("selector", Selector(row));
        foreach (var property in row.EnumerateObject())
        {
            property.WriteTo(writer);
        }

        writer.WriteEndObject();
    }
}

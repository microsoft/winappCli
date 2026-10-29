// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using Spectre.Console;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Commands;

/// <summary>
/// <c>winapp devtools get-property &lt;selector&gt;</c>: read an element's live dependency-property values and
/// say WHERE each effective value came from (local set, style, theme resource, binding, default). That value
/// source is the part a plain UI-Automation read cannot answer, and it is usually the answer to "why is this
/// not the colour/size I wrote".
/// </summary>
internal class DevToolsGetPropertyCommand : DevToolsLiveCommand, IHelpExamples
{
    public override string ShortDescription => "Read an element's live properties and their value sources";

    public IReadOnlyList<string> Examples { get; } =
    [
        "winapp devtools get-property <selector> -a <app>",
        "winapp devtools get-property <selector> Text -a <app>",
        "winapp devtools get-property <selector> --all -a <app>",
    ];

    public static Argument<string?> SelectorArgument { get; } = new("selector")
    {
        Description = "Element to read: the selector printed in brackets, an x:Name, an AutomationId, or a handle.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public static Argument<string?> PropertyArgument { get; } = new("property")
    {
        Description = "One property to read, e.g. Text. Same as --property.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public DevToolsGetPropertyCommand()
        : base("get-property", "Read an element's live property values and where they come from.")
    {
        Arguments.Add(SelectorArgument);
        Arguments.Add(PropertyArgument);
        Options.Add(SharedDevToolsOptions.PropertyOption);
        Options.Add(AllOption);
        AutomationPeerOption.AcceptOnlyFromAmong("create");
        Options.Add(AutomationPeerOption);
        DevToolsQueryOptions.Add(this);
    }

    public static Option<bool> AllOption { get; } = new("--all")
    {
        Description = "Include default-valued properties, not only explicitly set ones.",
    };

    public static Option<string?> AutomationPeerOption { get; } = new("--automation-peer")
    {
        Description = "Call the framework's peer-creation API instead of reading properties. Only accepts create; may run app code and change state.",
    };

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
            var selector = parseResult.GetValue(SelectorArgument);
            var property = parseResult.GetValue(SharedDevToolsOptions.PropertyOption);
            if (parseResult.GetValue(PropertyArgument) is { } positional)
            {
                if (property is not null)
                {
                    return Task.FromResult(Fail(json, target.Pid, "Pass the property once: positionally or with --property.", "bad-args"));
                }
                property = positional;
            }
            if (parseResult.GetValue(AutomationPeerOption) is not null)
            {
                if (property is not null || parseResult.GetValue(AllOption) || DevToolsQueryOptions.HasReadQuery(parseResult))
                {
                    return Task.FromResult(Fail(json, target.Pid,
                        "--automation-peer requires one positional selector and cannot be combined with property or query options.", "bad-args"));
                }
                var peerHandle = RequireHandle(target, selector, json, "Run `winapp devtools inspect` to list them.",
                    out var peerExit, cancellationToken, forMutation: true);
                return Task.FromResult(peerHandle is null ? peerExit
                    : ReadAutomationPeer(target, peerHandle, json, cancellationToken));
            }
            string? handle;
            DevToolsProtocolResponse response;
            VisualTreeNode? queryNode = null;
            JsonElement? queryResult = null;
            if (DevToolsQueryOptions.HasCriteria(parseResult))
            {
                if (selector is not null)
                {
                    return Task.FromResult(Fail(json, target.Pid, "Use a positional selector or query criteria, not both.", "bad-args"));
                }
                var query = DevToolsQueryOptions.Request(target, parseResult, exact: true, cancellationToken);
                using var result = query.TryParseResult();
                if (result is null || !DevToolsQueryOptions.ValidResult(result.RootElement, write: false) ||
                    !result.RootElement.GetProperty("ok").GetBoolean())
                {
                    return Task.FromResult(DevToolsQueryOptions.Run(target, parseResult, Console, json, cancellationToken,
                        exact: true, existingResponse: query));
                }
                if (!result.RootElement.GetProperty("complete").GetBoolean() ||
                    result.RootElement.GetProperty("matches").GetArrayLength() != 1 ||
                    !result.RootElement.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
                {
                    return Task.FromResult(Fail(json, target.Pid, "The query did not return one complete property read.", "invalid-response"));
                }
                handle = result.RootElement.GetProperty("matches")[0].GetProperty("handle").GetString()!;
                if (!properties.TryGetProperty("handle", out var readHandle) || readHandle.ValueKind != JsonValueKind.String ||
                    readHandle.GetString() != handle)
                {
                    return Task.FromResult(Fail(json, target.Pid, "The property read does not match the query target.", "invalid-response"));
                }
                response = DevToolsProtocolResponse.Success(properties.GetRawText());
                var queryMatch = result.RootElement.GetProperty("matches")[0];
                queryNode = new(handle, queryMatch.GetProperty("name").GetString()!, queryMatch.GetProperty("type").GetString()!,
                    null, queryMatch.TryGetProperty("id", out var id) ? id.GetString() : null,
                    queryMatch.GetProperty("uniqueName").GetBoolean(), 0, [], 0);
                queryResult = result.RootElement.Clone();
            }
            else
            {
                handle = RequireHandle(target, selector, json, "Run `winapp devtools inspect` to list them.", out var exitCode, cancellationToken);
                if (handle is null)
                {
                    return Task.FromResult(exitCode);
                }
                response = target.Tap!.RequestProperties(handle, cancellationToken);
            }
            if (!response.Ok)
            {
                return Task.FromResult(Fail(json, target.Pid, response.Error!));
            }

            // Parse and SELECT once, then branch on output shape. Emitting the raw payload before applying
            // --property/compact filtering made `--json` disagree with the human path about what was asked
            // for: `-p Foreground --json` returned all 249 rows.
            // The document stays alive for the whole command so the JSON path can copy each RETAINED row
            // through verbatim. Rebuilding a row from the parsed record instead would drop every wire field
            // the CLI does not model (editKind, chain, children, enumValues, valueState), and `chain` in
            // particular is the strongest "why did this value win?" evidence the protocol carries.
            using var doc = response.TryParseResult();
            if (doc is null
                || doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("props", out var propsElement)
                || propsElement.ValueKind != JsonValueKind.Array)
            {
                return Task.FromResult(Fail(json, target.Pid, "The DevTools agent returned an unreadable property list."));
            }

            var all = new List<(JsonElement Raw, DevToolsPropertyRow Row)>(propsElement.GetArrayLength());
            foreach (var element in propsElement.EnumerateArray())
            {
                if (DevToolsPropertyRow.FromElement(element) is DevToolsPropertyRow row)
                {
                    all.Add((element, row));
                }
                else
                {
                    return Task.FromResult(Fail(json, target.Pid, "The DevTools agent returned an invalid property row."));
                }
            }

            var total = all.Count;
            var singleProperty = !string.IsNullOrWhiteSpace(property);
            List<(JsonElement Raw, DevToolsPropertyRow Row)> selected;
            if (singleProperty)
            {
                // --property searches the FULL set: a caller asking for a specific property means it, even
                // when it is sitting at its default.
                selected = all
                    .Where(p => string.Equals(p.Row.Name, property, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (selected.Count == 0)
                {
                    return Task.FromResult(Fail(
                        json,
                        target.Pid,
                        $"'{property}' is not among the {total} {(total == 1 ? "property" : "properties")} the agent reports for this element. " +
                        DevToolsPropertyRow.UnknownPropertyHint(all.Select(p => p.Row.Name), property!, selector)));
                }
            }
            else if (parseResult.GetValue(AllOption))
            {
                selected = all;
            }
            else
            {
                selected = all.Where(p => p.Row.IsSet).ToList();
            }

            if (json)
            {
                // authoredState is a per-ELEMENT fact and is REQUIRED by the result contract: "available"
                // means the source was read and the property simply is not authored here, while
                // noSourceInfo/noFile/stale mean we could not look. Dropping it turns "we don't know" into
                // "there is nothing", which is the confidently-wrong answer the field exists to prevent.
                var authoredState = doc.RootElement.TryGetProperty("authoredState", out var stateElement)
                    && stateElement.ValueKind == JsonValueKind.String
                        ? stateElement.GetString()
                        : null;

                WriteJson(DevToolsJson.Serialize(writer =>
                {
                    writer.WriteBoolean("ok", true);
                    writer.WriteNumber("processId", target.Pid);
                    if (queryNode is not null)
                    {
                        writer.WriteString("selector", queryNode.Selector);
                        writer.WriteString("handle", handle);
                        writer.WriteStartObject("query");
                        foreach (var evidence in queryResult!.Value.EnumerateObject())
                        {
                            if (evidence.Name is "scope" or "scopeNodes" or "censusNodes" or "evaluated" or "unevaluated" or "complete" or "truncated")
                            {
                                evidence.WriteTo(writer);
                            }
                        }
                        writer.WriteEndObject();
                    }
                    else
                    {
                        WriteIdentity(writer, target, handle, cancellationToken);
                    }
                    WriteWarning(writer);
                    if (singleProperty)
                    {
                        writer.WriteString("property", property!);
                    }

                    if (authoredState is not null)
                    {
                        writer.WriteString("authoredState", authoredState);
                    }

                    writer.WriteNumber("totalProperties", total);
                    writer.WriteNumber("count", selected.Count);
                    writer.WriteStartArray("properties");
                    var sourceRoot = selected.Any(p => DevToolsJson.HasChainFile(p.Raw))
                        ? DevToolsJson.SourceRoot(target.Tap!, cancellationToken)
                        : null;
                    foreach (var (raw, row) in selected)
                    {
                        writer.WriteStartObject();
                        DevToolsJson.WritePropertyFields(writer, raw, sourceRoot);

                        // Derived, appended AFTER the wire fields so the protocol payload is never narrowed:
                        // `source` is the authoredKind-first provenance the human line shows, and `isSet` is
                        // the compact view's predicate. Neither name collides with a wire field.
                        if (row.SourceLabel is string label)
                        {
                            writer.WriteString("source", label);
                        }

                        writer.WriteBoolean("isSet", row.IsSet);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }));
                return Task.FromResult(0);
            }

            DevToolsRender.WriteMarkupLine(Console, DevToolsRender.Header(handle, queryNode ?? Describe(target, handle, cancellationToken)));
            foreach (var (_, row) in selected)
            {
                var source = row.SourceLabel is string label
                    ? $" [grey][[{Markup.Escape(label)}]][/]"
                    : string.Empty;
                DevToolsRender.WriteMarkupLine(Console, $"  {Markup.Escape(row.Name)}: {Markup.Escape(row.Value)}{source}");
            }

            if (selected.Count == 0)
            {
                Console.MarkupLine(
                    "[grey]Nothing is set on this element. Every property the agent reads is at its default. " +
                    "Use --all to list them.[/]");
                return Task.FromResult(0);
            }

            if (selected.Count < total && !singleProperty)
            {
                Console.WriteLine();
                Console.MarkupLineInterpolated(
                    $"[grey]Showing {selected.Count} of {total} {(total == 1 ? "property" : "properties")}: the ones something set. Use --all for the rest.[/]");
            }

            return Task.FromResult(0);
        }

        private int ReadAutomationPeer(DevToolsTarget target, string handle, bool json, CancellationToken cancellationToken)
        {
            var response = target.Tap!.RequestAutomationPeer(handle, cancellationToken);
            if (!response.Ok)
            {
                return Fail(json, target.Pid, response.Error!);
            }
            using var document = response.TryParseResult();
            if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Fail(json, target.Pid, "The agent returned unreadable automation-peer facts.", "invalid-response");
            }
            var facts = document.RootElement;
            if (!facts.TryGetProperty("handle", out var returnedHandle) || returnedHandle.ValueKind != JsonValueKind.String ||
                returnedHandle.GetString() != handle ||
                !facts.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String ||
                state.GetString() is not ("present" or "none" or "not-supported" or "error") ||
                !facts.TryGetProperty("apiInvoked", out var invoked) || invoked.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !facts.TryGetProperty("mayHaveChangedAppState", out var changed) || changed.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                changed.GetBoolean() != invoked.GetBoolean())
            {
                return Fail(json, target.Pid, "The agent returned inconsistent automation-peer facts.", "invalid-response");
            }
            var validObservation = state.GetString() switch
            {
                "present" or "none" => invoked.GetBoolean(),
                "not-supported" => !invoked.GetBoolean(),
                "error" => facts.TryGetProperty("errorHResult", out var error) && error.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrEmpty(error.GetString()),
                _ => false,
            };
            if (!validObservation)
            {
                return Fail(json, target.Pid, "The agent returned inconsistent automation-peer observations.", "invalid-response");
            }
            var failed = state.GetString() == "error";
            if (json)
            {
                WriteJson(DevToolsJson.Serialize(writer =>
                {
                    writer.WriteBoolean("ok", !failed);
                    writer.WriteNumber("processId", target.Pid);
                    writer.WriteString("handle", handle);
                    WriteWarning(writer);
                    writer.WritePropertyName("automationPeer");
                    facts.WriteTo(writer);
                }));
            }
            else
            {
                Console.MarkupLineInterpolated($"Automation peer API result: {state.GetString()}.");
                if (failed)
                {
                    Console.MarkupLineInterpolated($"Peer API error: {facts.GetProperty("errorHResult").GetString()}.");
                }
                if (invoked.GetBoolean())
                {
                    Console.WriteLine("The peer-creation API was called and may have changed app state, even if no peer was returned.");
                }
                Console.WriteLine("This does not say whether a returned peer was new or cached.");
                Console.WriteLine("This is not proof of UIA exposure or accessibility conformance.");
            }
            return failed ? 1 : 0;
        }
    }
}

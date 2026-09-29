// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using Spectre.Console;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Commands;

/// <summary>
/// <c>winapp devtools call &lt;Domain.method&gt; [name=value ...]</c> — the advanced escape hatch: invoke any
/// method the app's own <c>DevTools.negotiate</c> advertises.
/// <para>
/// This exists so a protocol capability is usable the day it lands, without waiting for a curated command. The
/// curated commands stay the documented path because they carry better defaults, smaller output, and
/// workflow-specific guidance; <c>call</c> carries none of that on purpose.
/// </para>
/// <para>
/// The accepted set is exactly what the target app advertises. <c>Internal.*</c> verbs are compiled into the
/// same registry so the access gate covers them, but they are deliberately not advertised — they are CLI
/// back-channels, not protocol surface — so this command rejects them by name before anything reaches the wire.
/// </para>
/// </summary>
internal class DevToolsCallCommand : DevToolsLiveCommand
{
    public override string ShortDescription => "Call any advertised DevTools protocol method (advanced)";

    public static Argument<string?> MethodArgument { get; } = new("method")
    {
        Description = "The DevTools method to call, e.g. DevTools.ping, Layout.get, Overlay.highlight.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public static Argument<string[]> ParamsArgument { get; } = new("params")
    {
        Description = "Method parameters. name=value sends a string; name:=value sends raw JSON (e.g. appAuthoredOnly:=true, depth:=4).",
        Arity = ArgumentArity.ZeroOrMore,
    };

    public DevToolsCallCommand()
        : base("call", "Call an advertised DevTools protocol method (advanced).")
    {
        Arguments.Add(MethodArgument);
        Arguments.Add(ParamsArgument);
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
            var method = parseResult.GetValue(MethodArgument);
            var rawParams = parseResult.GetValue(ParamsArgument) ?? [];

            if (string.IsNullOrWhiteSpace(method))
            {
                return Task.FromResult(Fail(json, target.Pid, "Provide a method, e.g. `winapp devtools call DevTools.ping`."));
            }

            method = method.Trim();
            var tap = target.Tap!;

            var advertised = tap.GetPublicMethods(cancellationToken);
            if (advertised is null)
            {
                return Task.FromResult(Fail(
                    json,
                    target.Pid,
                    tap.LastError ?? new DevToolsProtocolError(-32603, "internal", "The DevTools agent did not advertise a valid method list.")));
            }

            // Match the advertisement exactly. The registry's names are PascalCase Domain.method, and a
            // near-miss must not be silently corrected into a different verb.
            if (method.StartsWith("Internal.", StringComparison.Ordinal) || !advertised.Contains(method, StringComparer.Ordinal))
            {
                var suggestion = advertised
                    .FirstOrDefault(m => string.Equals(m, method, StringComparison.OrdinalIgnoreCase));
                // Don't paste 40 method names into an error. The list is one command away, through this same
                // command, so point at it instead.
                var hint = suggestion is not null
                    ? $" Did you mean {suggestion}?"
                    : " Run `winapp devtools call DevTools.negotiate` to list the methods it advertises.";
                var reason = method.StartsWith("Internal.", StringComparison.Ordinal)
                    ? $"'{method}' is an internal CLI back-channel, not part of the DevTools protocol surface."
                    : $"'{method}' is not advertised by this app's DevTools agent.";
                return Task.FromResult(Fail(
                    json,
                    target.Pid,
                    method.StartsWith("Internal.", StringComparison.Ordinal) ? reason : reason + hint,
                    "unknown-method"));
            }

            var parsed = DevToolsCallParams.Parse(rawParams);
            if (!parsed.Ok)
            {
                return Task.FromResult(Fail(json, target.Pid, parsed.Error!));
            }
            if (target.Window is not null && parsed.Values.Any(value => value.Name == "window"))
            {
                return Task.FromResult(Fail(json, target.Pid, "Use --window alone; a window parameter cannot override the selected HWND."));
            }
            if (target.Root is not null && parsed.Values.Any(value => value.Name == "root"))
            {
                return Task.FromResult(Fail(json, target.Pid, "Use --root alone; a root parameter cannot override the selected subtree."));
            }

            var response = tap.Request(
                method,
                parsed.Values.Count == 0
                    ? null
                    : writer =>
                    {
                        foreach (var (name, value) in parsed.Values)
                        {
                            value.Write(writer, name);
                        }
                    }, cancellationToken: cancellationToken);

            if (!response.Ok)
            {
                var invalidHandle = parsed.Values.Any(value => value.Name == "handle" &&
                    (value.Value.Kind != JsonValueKind.String ||
                    !DevToolsSelector.IsHandle(value.Value.Raw?.GetString() ?? value.Value.Text)));
                if (response.Error!.Token == "bad-args" && invalidHandle)
                {
                    return Task.FromResult(Fail(json, target.Pid, response.Error with
                    {
                        Message = response.Error.Message +
                            " Raw call does not resolve element selectors. Copy the numeric handle string from " +
                            "'winapp devtools inspect --json' and pass handle=<handle>; do not use the selector or name.",
                    }));
                }
                // A method that wanted a boolean or a number and got a string says `bad-args`. That token
                // alone sends the caller looking at the method rather than at the ':=' they omitted.
                if (response.Error!.Token == "bad-args" && parsed.HasStringValues)
                {
                    return Task.FromResult(Fail(
                        json,
                        target.Pid,
                        response.Error with
                        {
                            Message = response.Error.Message +
                                " Note that name=value sends a STRING; use name:=value for a boolean, a number, or null " +
                                "(e.g. appAuthoredOnly:=true).",
                        }));
                }

                return Task.FromResult(Fail(json, target.Pid, response.Error!));
            }

            if (json)
            {
                // --json is the raw protocol result, with no human shaping applied at all.
                WriteJson(DevToolsJson.Result(target.Pid, response.ResultJson, w => w.WriteString("method", method)));
                return Task.FromResult(0);
            }

            RenderResult(response.ResultJson, method);
            return Task.FromResult(0);
        }

        private void RenderResult(string? resultJson, string method)
        {
            using var doc = resultJson is null ? null : Parse(resultJson);
            if (doc is null)
            {
                Console.MarkupLineInterpolated($"[grey]{method} returned no result.[/]");
                return;
            }

            var root = doc.RootElement;
            switch (root.ValueKind)
            {
                case JsonValueKind.Null or JsonValueKind.Undefined:
                    Console.MarkupLineInterpolated($"[grey]{method} acknowledged (no result).[/]");
                    return;
                case JsonValueKind.Object:
                    foreach (var property in root.EnumerateObject())
                    {
                        DevToolsRender.WriteMarkupLine(Console, $"{Markup.Escape(property.Name)}: {Markup.Escape(Scalar(property.Value))}");
                    }

                    return;
                case JsonValueKind.Array:
                    var count = 0;
                    foreach (var item in root.EnumerateArray())
                    {
                        count++;
                        DevToolsRender.WriteMarkupLine(Console, Markup.Escape(Scalar(item)));
                    }

                    if (count == 0)
                    {
                        Console.MarkupLineInterpolated($"[grey]{method} returned an empty list.[/]");
                    }

                    return;
                default:
                    DevToolsRender.WriteMarkupLine(Console, Markup.Escape(Scalar(root)));
                    return;
            }
        }

        private static JsonDocument? Parse(string json)
        {
            try
            {
                return JsonDocument.Parse(json, TapWireJson.DocumentOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string Scalar(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Null => "null",
            JsonValueKind.Object or JsonValueKind.Array => element.GetRawText(),
            _ => element.ToString(),
        };
    }
}

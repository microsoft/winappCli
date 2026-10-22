// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text.Json;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Commands;

/// <summary>
/// <c>winapp devtools set-state &lt;selector&gt; &lt;hook&gt; &lt;value&gt;</c>: write one state hook of a live
/// Reactor component, as if its own setter ran, and report the value read back after the re-render.
/// </summary>
internal class DevToolsSetStateCommand : DevToolsLiveCommand, IHelpExamples
{
    public override string ShortDescription => "Change a Reactor component's state live";

    public IReadOnlyList<string> Examples { get; } =
    [
        "winapp devtools set-state \"<Counter>\" count 42 -a <app>",
        "winapp devtools set-state \"<Counter>\" 0 42 -a <app>",
        "winapp devtools set-state \"<TaskList>\" 1 Done -a <app> --json",
    ];

    public static Argument<string?> SelectorArgument { get; } = new("selector")
    {
        Description = "A Reactor component (<Counter>) or any element it renders.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public static Argument<string?> HookArgument { get; } = new("hook")
    {
        Description = "The hook's index, or its C# variable name when Reactor records it, as `get-state` lists it.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public static Argument<string?> ValueArgument { get; } = new("value")
    {
        Description = "The new value: a number, text, true/false, an enum name, or null for a nullable.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public DevToolsSetStateCommand()
        : base("set-state", "Change a Reactor component's state hook live and read it back; source files stay unchanged.")
    {
        Arguments.Add(SelectorArgument);
        Arguments.Add(HookArgument);
        Arguments.Add(ValueArgument);
    }

    public class Handler(IDevToolsTargetResolver resolver, IAnsiConsole ansiConsole) : LiveHandler(resolver, ansiConsole)
    {
        protected override Task<int> RunAsync(DevToolsTarget target, ParseResult parseResult, bool json, CancellationToken cancellationToken)
        {
            var hookText = parseResult.GetValue(HookArgument);
            var value = parseResult.GetValue(ValueArgument);
            if (hookText is null || value is null)
            {
                return Task.FromResult(Fail(json, target.Pid,
                    "Provide the hook (its name or index) and the new value, e.g. `winapp devtools set-state \"<Counter>\" count 42`. " +
                    "Run `winapp devtools get-state <selector>` to list the hooks.", "bad-args"));
            }
            var handle = RequireHandle(target, parseResult.GetValue(SelectorArgument), json,
                "Run `winapp devtools inspect` to list components.", out var exitCode, cancellationToken, forMutation: true);
            if (handle is null)
            {
                return Task.FromResult(exitCode);
            }
            var byIndex = int.TryParse(hookText, NumberStyles.None, CultureInfo.InvariantCulture, out var hook);
            // The component's hooks say which index a name is at, and which hooks hold a password. After a late
            // attach a hook's name may come only from C# the CLI found, so a password hook is refused here too.
            var (state, _, problem, token) = ReactorComponentState.Read(target.Tap!, handle, cancellationToken);
            if (!byIndex)
            {
                if (state is null)
                {
                    return Task.FromResult(Fail(json, target.Pid, problem!, token!));
                }
                var (found, why) = state.FindHook(hookText);
                if (found is null)
                {
                    return Task.FromResult(Fail(json, target.Pid,
                        why + " Run `winapp devtools get-state <selector>` to list the hooks.", "bad-hook"));
                }
                hook = found.Index;
            }
            if (state?.Hooks.FirstOrDefault(h => h.Index == hook) is { Redacted: true })
            {
                return Task.FromResult(Fail(json, target.Pid, $"Hook {hook} holds a secret; DevTools does not write it.", "secret"));
            }
            var hookName = hookText.Length > 0 && !char.IsAsciiDigit(hookText[0]) ? hookText : null;

            var response = target.Tap!.RequestReactorSetState(handle, hook, value, cancellationToken);
            return Task.FromResult(ReportAgentAnswer(target, handle, json, response, (writer, root) =>
            {
                foreach (var member in root.EnumerateObject())
                {
                    if (member.Name != "state")
                    {
                        member.WriteTo(writer);
                    }
                }
                if (hookName is not null)
                {
                    writer.WriteString("name", hookName);
                }
            }, root =>
            {
                var component = ReactorDescription.ReadString(root, "component") ?? "component";
                var kind = ReactorDescription.ReadString(root, "kind");
                var type = ReactorDescription.ReadString(root, "type");
                var before = ReactorDescription.ReadString(root, "before") ?? "?";
                var after = ReactorDescription.ReadString(root, "after");
                var changed = root.TryGetProperty("changed", out var c) && c.ValueKind == JsonValueKind.True;
                var label = $"<{Markup.Escape(component)}> hook [[{hook}]]" + (hookName is null ? "" : $" [bold]{Markup.Escape(hookName)}[/]") +
                    $" [grey]({Markup.Escape(kind ?? "state")} {Markup.Escape(type ?? "")})[/]";
                var lines = new List<string>
                {
                    changed
                        ? $"{UiSymbols.Check} {label}: {Markup.Escape(before)} [grey]->[/] [green]{Markup.Escape(after ?? "?")}[/]"
                        : $"{UiSymbols.Check} {label}: already {Markup.Escape(after ?? before)}, unchanged.",
                };
                if (root.TryGetProperty("rendered", out var rendered) && rendered.ValueKind == JsonValueKind.False)
                {
                    lines.Add("   [grey]The component was still rendering when this was read back; run get-state again to see where it settled.[/]");
                }
                lines.Add("   [grey]Live only: the component's C# starts from its initial value again on the next launch.[/]");
                return lines;
            }, cancellationToken));
        }
    }
}

/// <summary>Error tokens and messages for Reactor and resource answers that are not <c>ok</c>.</summary>
internal static class ReactorWrites
{
    /// <summary>The error token and message for an answer that is not <c>ok</c>.</summary>
    internal static (string Token, string Message) Problem(JsonElement root, string? state)
    {
        var reason = ReactorDescription.ReadString(root, "reason");
        return state switch
        {
            "absent" => ("reactor-absent", "This app does not use Microsoft.UI.Reactor."),
            "refused" => (ReactorDescription.ReadString(root, "code") ?? "refused", Sentence(reason ?? "the app refused the change")),
            "threw" => ("handler-threw", $"The handler threw {ReactorDescription.ReadString(root, "exception")}: {ReactorDescription.ReadString(root, "message")}"),
            _ when root.TryGetProperty("indeterminate", out var i) && i.ValueKind == JsonValueKind.True =>
                ("indeterminate", "The app did not answer in time, so the change may or may not have been applied. Read it back before retrying."),
            _ when ReactorDescription.IsBusy(reason) => ("reactor-unavailable", ReactorDescription.BusyMessage),
            _ => ("reactor-unavailable", "Unavailable: " + Sentence(reason ?? "the managed DevTools agent did not answer")),
        };
    }

    private static string Sentence(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..] + (text.EndsWith('.') ? "" : ".");
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using Spectre.Console;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Commands;

/// <summary>
/// <c>winapp devtools get-state &lt;selector&gt;</c>: the props, hooks and contexts of the Microsoft.UI.Reactor
/// component that renders an element: the component mounted at it, or the nearest one above it.
/// </summary>
internal class DevToolsGetStateCommand : DevToolsLiveCommand, IHelpExamples
{
    public override string ShortDescription => "Show a Reactor component's props, hooks and contexts";

    public IReadOnlyList<string> Examples { get; } =
    [
        "winapp devtools get-state \"<Counter>\" -a <app>",
        "winapp devtools get-state \"<Counter>/Button\" -a <app> --json",
    ];

    public static Argument<string?> SelectorArgument { get; } = new("selector")
    {
        Description = "A Reactor component (<Counter>) or any element it renders: the selector printed in brackets, key=<k>, App.cs:35, or a handle.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public DevToolsGetStateCommand()
        : base("get-state", "Show the props, hooks (state) and contexts of the Reactor component that renders an element.")
    {
        Arguments.Add(SelectorArgument);
    }

    public class Handler(
        IDevToolsTargetResolver resolver,
        IAnsiConsole ansiConsole) : LiveHandler(resolver, ansiConsole)
    {
        protected override Task<int> RunAsync(DevToolsTarget target, ParseResult parseResult, bool json, CancellationToken cancellationToken)
        {
            var handle = RequireHandle(target, parseResult.GetValue(SelectorArgument), json,
                "Run `winapp devtools inspect` to list components.", out var exitCode, cancellationToken);
            if (handle is null)
            {
                return Task.FromResult(exitCode);
            }

            var (state, raw, problem, token) = ReactorComponentState.Read(target.Tap!, handle, cancellationToken);
            if (state is null)
            {
                return Task.FromResult(Fail(json, target.Pid, problem!, token!));
            }

            if (json)
            {
                WriteJson(DevToolsJson.Serialize(writer =>
                {
                    writer.WriteBoolean("ok", true);
                    writer.WriteNumber("processId", target.Pid);
                    WriteIdentity(writer, target, handle, cancellationToken);
                    WriteWarning(writer);
                    using var doc = JsonDocument.Parse(raw!);
                    foreach (var member in doc.RootElement.EnumerateObject())
                    {
                        if (member.Name != "state")
                        {
                            member.WriteTo(writer);
                        }
                    }
                }));
                return Task.FromResult(0);
            }

            DevToolsRender.WriteMarkupLine(Console, DevToolsRender.Header(handle, Describe(target, handle, cancellationToken)));
            if (!state.Exact)
            {
                DevToolsRender.WriteMarkupLine(Console, "  [grey]Rendered by:[/]");
            }
            foreach (var line in state.Lines())
            {
                DevToolsRender.WriteMarkupLine(Console, line);
            }
            if (state.Parked)
            {
                DevToolsRender.WriteMarkupLine(Console, $"[grey]{Markup.Escape(ReactorComponentState.ParkedNote)}[/]");
            }
            if (state.Hooks.Any(h => h.Editable))
            {
                Console.WriteLine();
                DevToolsRender.WriteMarkupLine(Console, state.Hooks.Any(h => h.Editable && h.Name is not null)
                    ? "[grey]Change a settable hook live with `winapp devtools set-state <selector> <name|index> <value>`.[/]"
                    : "[grey]Change a settable hook live with `winapp devtools set-state <selector> <index> <value>`.[/]");
            }
            return Task.FromResult(0);
        }
    }
}

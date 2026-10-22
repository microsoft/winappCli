// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Commands;

/// <summary>
/// <c>winapp devtools invoke-handler &lt;selector&gt; &lt;event&gt;</c>: run the event handler a Reactor element's
/// C# set (<c>OnClick</c>) directly, on the app's UI thread.
/// </summary>
internal class DevToolsInvokeHandlerCommand : DevToolsLiveCommand, IHelpExamples
{
    public override string ShortDescription => "Run a Reactor element's event handler";

    public IReadOnlyList<string> Examples { get; } =
    [
        "winapp devtools invoke-handler \"<Counter>/Button\" OnClick -a <app>",
        "winapp devtools invoke-handler key=title OnChanged --value \"Ship it\" -a <app>",
    ];

    public static Argument<string?> SelectorArgument { get; } = new("selector")
    {
        Description = "The Reactor element whose handler to run: the selector printed in brackets, <Counter>/Button, key=<k>, App.cs:35, or a handle.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public static Argument<string?> EventArgument { get; } = new("event")
    {
        Description = "The handler, as get-property lists it: OnClick (or Click).",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public static Option<string?> ValueOption { get; } = new("--value")
    {
        Description = "The handler's one argument, when it takes text, a number, true/false or an enum value.",
    };

    public DevToolsInvokeHandlerCommand()
        : base("invoke-handler", "Run the event handler a Reactor element's C# set, on the app's UI thread.")
    {
        Arguments.Add(SelectorArgument);
        Arguments.Add(EventArgument);
        Options.Add(ValueOption);
    }

    public class Handler(IDevToolsTargetResolver resolver, IAnsiConsole ansiConsole) : LiveHandler(resolver, ansiConsole)
    {
        protected override Task<int> RunAsync(DevToolsTarget target, ParseResult parseResult, bool json, CancellationToken cancellationToken)
        {
            var handlerEvent = parseResult.GetValue(EventArgument);
            if (string.IsNullOrWhiteSpace(handlerEvent))
            {
                return Task.FromResult(Fail(json, target.Pid,
                    "Provide the handler, e.g. `winapp devtools invoke-handler \"<Counter>/Button\" OnClick`. " +
                    "`winapp devtools get-property <selector>` lists an element's handlers.", "bad-args"));
            }
            var selector = parseResult.GetValue(SelectorArgument);
            var handle = RequireHandle(target, selector, json,
                "Run `winapp devtools inspect` to list elements.", out var exitCode, cancellationToken, forMutation: true);
            if (handle is null)
            {
                return Task.FromResult(exitCode);
            }
            // Read before the handler runs: a handler may remove its own element, which then has no selector to print.
            var element = Describe(target, handle, cancellationToken);
            var response = target.Tap!.RequestReactorInvokeHandler(handle, handlerEvent.Trim(), parseResult.GetValue(ValueOption), cancellationToken);
            return Task.FromResult(ReportAgentAnswer(target, handle, json, response, (writer, root) =>
            {
                foreach (var member in root.EnumerateObject())
                {
                    if (member.Name != "state")
                    {
                        member.WriteTo(writer);
                    }
                }
            }, root =>
            [
                $"{UiSymbols.Check} {(element is null ? DevToolsRender.Handle(selector!.Trim()) : DevToolsRender.Header(handle, element))}: ran " +
                    $"{Markup.Escape(ReactorDescription.ReadString(root, "event") ?? handlerEvent)} → " +
                    Markup.Escape(ReactorDescription.ReadString(root, "method") ?? "handler") +
                    (root.TryGetProperty("rendered", out var rendered) && rendered.ValueKind == JsonValueKind.False
                        ? " [grey](still rendering when this returned)[/]" : ""),
            ], cancellationToken));
        }
    }
}

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
/// <c>winapp devtools rerender &lt;selector&gt;</c>: ask a Reactor component to render again. A render reapplies the
/// values whose C# changed; it does not undo a live <c>set-property</c> edit whose C# value is unchanged.
/// </summary>
internal class DevToolsRerenderCommand : DevToolsLiveCommand, IHelpExamples
{
    public override string ShortDescription => "Re-render a Reactor component";

    public IReadOnlyList<string> Examples { get; } =
    [
        "winapp devtools rerender \"<Counter>\" -a <app>",
    ];

    public static Argument<string?> SelectorArgument { get; } = new("selector")
    {
        Description = "A Reactor component (<Counter>) or any element it renders.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public DevToolsRerenderCommand()
        : base("rerender", "Re-render the Reactor component that renders an element. Reapplies C# values that changed; " +
            "a live set-property edit whose C# value did not change stays.")
    {
        Arguments.Add(SelectorArgument);
    }

    public class Handler(IDevToolsTargetResolver resolver, IAnsiConsole ansiConsole) : LiveHandler(resolver, ansiConsole)
    {
        protected override Task<int> RunAsync(DevToolsTarget target, ParseResult parseResult, bool json, CancellationToken cancellationToken)
        {
            var handle = RequireHandle(target, parseResult.GetValue(SelectorArgument), json,
                "Run `winapp devtools inspect` to list components.", out var exitCode, cancellationToken, forMutation: true);
            if (handle is null)
            {
                return Task.FromResult(exitCode);
            }
            var response = target.Tap!.RequestReactorRerender(handle, cancellationToken);
            return Task.FromResult(ReportAgentAnswer(target, handle, json, response, (writer, root) =>
            {
                foreach (var member in root.EnumerateObject().Where(member => member.Name != "state"))
                {
                    member.WriteTo(writer);
                }
                writer.WriteString("note", Note);
            }, root =>
                root.TryGetProperty("parked", out var parked) && parked.ValueKind == JsonValueKind.True
                ? [
                    $"{UiSymbols.Check} <{Markup.Escape(ReactorDescription.ReadString(root, "component") ?? "component")}> rendered again, off screen.",
                    $"   [grey]{Markup.Escape(ReactorComponentState.ParkedNote)}[/]",
                ]
                : [
                    $"{UiSymbols.Check} <{Markup.Escape(ReactorDescription.ReadString(root, "component") ?? "component")}> rendered again." +
                        (root.TryGetProperty("rendered", out var rendered) && rendered.ValueKind == JsonValueKind.False
                            ? " [grey](still rendering when this returned)[/]" : ""),
                    $"   [grey]{Markup.Escape(Note)}[/]",
                ], cancellationToken));
        }
    }

    internal const string Note =
        "A re-render reapplies only C# values that changed. A property edited live with set-property keeps its edit until its C# value changes or the app restarts.";
}

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
/// <c>winapp devtools get-resource &lt;key&gt; [selector]</c>: where a XAML resource key resolves from an element (or
/// the window), and its value: the element's resources, its ancestors', then the application's, for the element's
/// theme.
/// </summary>
internal class DevToolsGetResourceCommand : DevToolsLiveCommand, IHelpExamples
{
    public override string ShortDescription => "Show where a resource key resolves and its value";

    public IReadOnlyList<string> Examples { get; } =
    [
        "winapp devtools get-resource AccentFillColorDefaultBrush -a <app>",
        "winapp devtools get-resource CardBackgroundFillColorDefaultBrush <selector> -a <app> --json",
    ];

    public static Argument<string?> KeyArgument { get; } = new("key")
    {
        Description = "The resource key, e.g. AccentFillColorDefaultBrush.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public static Argument<string?> SelectorArgument { get; } = new("selector")
    {
        Description = "Where the lookup starts (default: the window's root): the selector printed in brackets, an x:Name, a Reactor <Component>, or a handle.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public DevToolsGetResourceCommand()
        : base("get-resource", "Show where a XAML resource key resolves from an element, and its value for that element's theme.")
    {
        Arguments.Add(KeyArgument);
        Arguments.Add(SelectorArgument);
    }

    public class Handler(IDevToolsTargetResolver resolver, IAnsiConsole ansiConsole) : LiveHandler(resolver, ansiConsole)
    {
        protected override Task<int> RunAsync(DevToolsTarget target, ParseResult parseResult, bool json, CancellationToken cancellationToken)
        {
            var key = parseResult.GetValue(KeyArgument);
            if (string.IsNullOrWhiteSpace(key))
            {
                return Task.FromResult(Fail(json, target.Pid, "Provide the resource key, e.g. `winapp devtools get-resource AccentFillColorDefaultBrush`.", "bad-args"));
            }
            var selector = parseResult.GetValue(SelectorArgument);
            var handle = RequireHandleOrRoot(target, selector, json, out var exitCode, cancellationToken, forMutation: false);
            if (handle is null)
            {
                return Task.FromResult(exitCode);
            }
            var response = target.Tap!.RequestResourceGet(handle, key.Trim(), selector is not null, cancellationToken);
            using (var answer = response.TryParseResult())
            {
                if (answer?.RootElement is { ValueKind: JsonValueKind.Object } found &&
                    ReactorDescription.ReadString(found, "state") == "ok" &&
                    !(found.TryGetProperty("found", out var hit) && hit.ValueKind == JsonValueKind.True))
                {
                    var theme = ReactorDescription.ReadString(found, "theme");
                    return Task.FromResult(Fail(json, target.Pid,
                        $"No resource named {key.Trim()} is visible from here{(theme is null ? "" : $" (theme {theme})")}.", "no-resource"));
                }
            }
            return Task.FromResult(ReportAgentAnswer(target, handle, json, response, (writer, root) =>
            {
                foreach (var member in root.EnumerateObject())
                {
                    if (member.Name != "state")
                    {
                        member.WriteTo(writer);
                    }
                }
            }, root => ResourceTargets.Describe(root), cancellationToken));
        }

    }
}

/// <summary>
/// <c>winapp devtools set-resource &lt;key&gt; &lt;value&gt; [selector] --scope element|app</c>: change a XAML resource
/// live, in memory, and make <c>{ThemeResource}</c> references (and Reactor ThemeRefs) read it again.
/// </summary>
internal class DevToolsSetResourceCommand : DevToolsLiveCommand, IHelpExamples
{
    public override string ShortDescription => "Change a resource live";

    public IReadOnlyList<string> Examples { get; } =
    [
        "winapp devtools set-resource AccentFillColorDefaultBrush \"#FFC42B1C\" --scope app -a <app>",
        "winapp devtools set-resource CardBackgroundFillColorDefaultBrush \"#FF202020\" <selector> --scope element -a <app>",
    ];

    public static Argument<string?> KeyArgument { get; } = new("key")
    {
        Description = "The resource key, e.g. AccentFillColorDefaultBrush.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public static Argument<string?> ValueArgument { get; } = new("value")
    {
        Description = "The new value: a color (#RRGGBB or #AARRGGBB) for a brush or color, otherwise text the current value's type parses.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public static Argument<string?> SelectorArgument { get; } = new("selector")
    {
        Description = "The element whose resources change (--scope element), or that picks the window (--scope app).",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public static Option<string?> ScopeOption { get; } = new("--scope")
    {
        Description = "element: add or replace the key in the element's own resources (default with a selector). " +
            "app: replace it where the application defines it, for the current theme (default without one).",
    };

    public DevToolsSetResourceCommand()
        : base("set-resource", "Change a XAML resource live, in memory; {ThemeResource} users and Reactor ThemeRefs update, {StaticResource} users keep their value.")
    {
        Arguments.Add(KeyArgument);
        Arguments.Add(ValueArgument);
        Arguments.Add(SelectorArgument);
        ScopeOption.AcceptOnlyFromAmong("element", "app");
        Options.Add(ScopeOption);
    }

    public class Handler(IDevToolsTargetResolver resolver, IAnsiConsole ansiConsole) : LiveHandler(resolver, ansiConsole)
    {
        protected override Task<int> RunAsync(DevToolsTarget target, ParseResult parseResult, bool json, CancellationToken cancellationToken)
        {
            var key = parseResult.GetValue(KeyArgument);
            var value = parseResult.GetValue(ValueArgument);
            if (string.IsNullOrWhiteSpace(key) || value is null)
            {
                return Task.FromResult(Fail(json, target.Pid,
                    "Provide the key and the new value, e.g. `winapp devtools set-resource AccentFillColorDefaultBrush \"#FFC42B1C\" --scope app`.", "bad-args"));
            }
            var selector = parseResult.GetValue(SelectorArgument);
            var scope = parseResult.GetValue(ScopeOption) ?? (selector is null ? "app" : "element");
            if (scope == "element" && selector is null)
            {
                return Task.FromResult(Fail(json, target.Pid, "--scope element needs the element whose resources to change: pass its selector.", "bad-args"));
            }
            var handle = RequireHandleOrRoot(target, selector, json, out var exitCode, cancellationToken, forMutation: true);
            if (handle is null)
            {
                return Task.FromResult(exitCode);
            }
            var response = target.Tap!.RequestResourceSet(handle, key.Trim(), value, scope, cancellationToken);
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
            {
                var before = ReactorDescription.ReadString(root, "before");
                var after = ReactorDescription.ReadString(root, "after") ?? "?";
                var where = ReactorDescription.ReadString(root, "written") ?? scope;
                return
                [
                    before is null
                        ? $"{UiSymbols.Check} {Markup.Escape(key)}: added [green]{Markup.Escape(after)}[/] [grey]({Markup.Escape(where)})[/]"
                        : $"{UiSymbols.Check} {Markup.Escape(key)}: {Markup.Escape(before)} [grey]->[/] [green]{Markup.Escape(after)}[/] [grey]({Markup.Escape(where)})[/]",
                    "   [grey]Live only; {StaticResource} users keep the value they loaded. Restart the app to undo.[/]",
                ];
            }, cancellationToken));
        }

    }
}

/// <summary>Shared targeting and rendering for the resource commands.</summary>
internal static class ResourceTargets
{
    public static IEnumerable<string> Describe(JsonElement root)
    {
        var key = ReactorDescription.ReadString(root, "key") ?? "";
        var theme = ReactorDescription.ReadString(root, "theme");
        if (!(root.TryGetProperty("found", out var found) && found.ValueKind == JsonValueKind.True))
        {
            yield return $"{UiSymbols.Warning} No resource named {Markup.Escape(key)} is visible from here" +
                (theme is null ? "." : $" (theme {Markup.Escape(theme)}).");
            yield break;
        }
        var owner = ReactorDescription.ReadString(root, "owner");
        var scope = ReactorDescription.ReadString(root, "scope") ?? "";
        yield return $"{Markup.Escape(key)} = [green]{Markup.Escape(ReactorDescription.ReadString(root, "value") ?? "")}[/]";
        yield return $"  [grey]from {Markup.Escape(scope)}{(owner is null ? "" : " " + Markup.Escape(owner))}: " +
            $"{Markup.Escape(ReactorDescription.ReadString(root, "dictionary") ?? "")}{(theme is null ? "" : $" · theme {Markup.Escape(theme)}")}[/]";
    }
}

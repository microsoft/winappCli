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
/// <c>winapp devtools resources set &lt;key&gt; &lt;value&gt;</c>: replace an app resource in the running app so every
/// element that uses the key picks up the new value. The app's original is kept until <c>resources reset</c>.
/// </summary>
internal class DevToolsResourcesSetCommand : DevToolsLiveCommand, IHelpExamples
{
    public override string ShortDescription => "Replace an app resource live; reset restores it";

    public IReadOnlyList<string> Examples { get; } =
    [
        "winapp devtools resources set <key> \"#FF0067C0\" -a <app>",
        "winapp devtools resources set <key> 12 -a <app>",
        "winapp devtools resources set <key> \"#FF101010\" --theme Dark -a <app>",
    ];

    public static Argument<string> KeyArgument { get; } = new("key")
    {
        Description = "The resource key (x:Key) to replace, e.g. CardBackgroundBrush.",
    };

    public static Argument<string> ValueArgument { get; } = new("value")
    {
        Description = "The new value in XAML attribute form, e.g. #FF0067C0, 12, or 4,8,4,8.",
    };

    public static Option<string?> ThemeOption { get; } = new("--theme")
    {
        Description = "Which theme dictionary to change: Light, Dark, or HighContrast. Defaults to the app's current theme.",
    };

    public DevToolsResourcesSetCommand()
        : base("set", "Replace a resource defined in Application.Resources (or its merged or theme dictionaries) " +
            "in the running app. Elements that use the key update live; App.xaml stays unchanged. " +
            "The change lasts until `resources reset` or the app exits.")
    {
        Arguments.Add(KeyArgument);
        Arguments.Add(ValueArgument);
        Options.Add(ThemeOption);
        Options.Add(SharedDevToolsOptions.TypeOption);
    }

    internal sealed record Outcome(
        string Key, string Theme, string Dictionary, string? ThemeDictionary, string Previous, string Original,
        string Value, string ValueType, bool Verified, bool LiveUpdate);

    internal static Outcome? Parse(string? resultJson)
    {
        if (string.IsNullOrEmpty(resultJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(resultJson, TapWireJson.DocumentOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || String(root, "key") is not { Length: > 0 } key)
            {
                return null;
            }

            return new Outcome(
                key,
                String(root, "theme") ?? string.Empty,
                String(root, "dictionary") ?? string.Empty,
                String(root, "themeDictionary"),
                String(root, "previous") ?? string.Empty,
                String(root, "original") ?? string.Empty,
                String(root, "value") ?? string.Empty,
                String(root, "valueType") ?? string.Empty,
                root.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.True,
                root.TryGetProperty("liveUpdate", out var l) && l.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The lines printed under the before -> after line.</summary>
    internal static IReadOnlyList<string> Notes(Outcome outcome, bool themeRequested, string? definedAt)
    {
        var notes = new List<string>();
        if (themeRequested && outcome.ThemeDictionary is not null)
        {
            notes.Add($"This changed the {outcome.ThemeDictionary} theme dictionary; it shows while the app uses that theme.");
        }
        else
        {
            notes.Add($"Elements that use {outcome.Key} through {{ThemeResource}} or {{StaticResource}} now show the new value.");
        }
        if (!outcome.LiveUpdate)
        {
            notes.Add("The app was not started with `winapp run --devtools`, so elements that already use this key may keep the old value.");
        }
        if (!string.Equals(outcome.Previous, outcome.Original, StringComparison.Ordinal))
        {
            notes.Add($"The app's own value is {outcome.Original}; `winapp devtools resources reset {outcome.Key}` restores it.");
        }
        notes.Add(definedAt is null
            ? "The change lasts until `winapp devtools resources reset` or the app exits; App.xaml is unchanged."
            : $"The change lasts until `winapp devtools resources reset` or the app exits. To keep it, edit {definedAt}.");
        return notes;
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

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
            var key = parseResult.GetValue(KeyArgument)!.Trim();
            var value = parseResult.GetValue(ValueArgument)!;
            var theme = parseResult.GetValue(ThemeOption);
            var type = parseResult.GetValue(SharedDevToolsOptions.TypeOption);
            if (key.Length == 0)
            {
                return Task.FromResult(Fail(json, target.Pid, "Provide the resource key to replace.", "bad-args"));
            }

            var tap = target.Tap!;
            var response = tap.RequestResourceSet(key, value, theme, string.IsNullOrWhiteSpace(type) ? null : type, cancellationToken);
            if (!response.Ok)
            {
                return Task.FromResult(Fail(json, target.Pid, response.Error!));
            }

            var outcome = Parse(response.ResultJson);
            if (outcome is null)
            {
                return Task.FromResult(Fail(json, target.Pid, "The DevTools agent returned an unreadable result."));
            }

            var sourceRoot = DevToolsJson.SourceRoot(tap, cancellationToken);
            var index = sourceRoot is not null && Directory.Exists(sourceRoot) ? XamlResourceIndex.Build(sourceRoot) : null;
            var definition = DevToolsResourcesListCommand.DefinitionFor(index, outcome.Key, outcome.ThemeDictionary);
            var definedAt = DevToolsResourcesListCommand.Location(definition);

            if (json)
            {
                WriteJson(DevToolsJson.Result(target.Pid, outcome.Verified, response.ResultJson, writer =>
                {
                    if (definition is not null)
                    {
                        writer.WriteStartObject("definedAt");
                        writer.WriteString("file", definition.File);
                        if (DevToolsJson.ProjectPath(sourceRoot, definition.File) is { } path)
                        {
                            writer.WriteString("path", path);
                        }
                        writer.WriteNumber("line", definition.Line);
                        writer.WriteEndObject();
                    }
                    if (!outcome.Verified)
                    {
                        DevToolsJson.WriteError(writer, "write-unconfirmed",
                            $"WinUI accepted the new value, but {outcome.Key} reads back as {outcome.Value}.");
                    }
                }));
                return Task.FromResult(outcome.Verified ? 0 : 1);
            }

            var branch = outcome.ThemeDictionary is null ? string.Empty : $" [grey]({Markup.Escape(outcome.ThemeDictionary)})[/]";
            if (!outcome.Verified)
            {
                DevToolsRender.WriteMarkupLine(Console,
                    $"{UiSymbols.Warning} {Markup.Escape(outcome.Key)}{branch}: WinUI accepted {Markup.Escape(value)}, " +
                    $"but the key reads back as {Markup.Escape(outcome.Value)}.");
                return Task.FromResult(1);
            }

            DevToolsRender.WriteMarkupLine(Console,
                $"{UiSymbols.Check} {Markup.Escape(outcome.Key)}{branch}: {Markup.Escape(outcome.Previous)} [grey]->[/] [green]{Markup.Escape(outcome.Value)}[/]");
            foreach (var note in Notes(outcome, theme is not null, definedAt))
            {
                DevToolsRender.WriteMarkupLine(Console, $"   [grey]{Markup.Escape(note)}[/]");
            }

            return Task.FromResult(0);
        }
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using Spectre.Console;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Commands;

/// <summary>
/// <c>winapp devtools resources list [--key &lt;glob&gt;] [--theme &lt;theme&gt;]</c>: the keyed resources the running
/// app can resolve from <c>Application.Resources</c>, with their live values and the XAML line that defines each one.
/// </summary>
internal class DevToolsResourcesListCommand : DevToolsLiveCommand, IHelpExamples
{
    public override string ShortDescription => "List app resources with their live values";

    public IReadOnlyList<string> Examples { get; } =
    [
        "winapp devtools resources list --key \"*Brush\" -a <app>",
        "winapp devtools resources list --key \"Accent*\" --defaults --theme Dark -a <app>",
    ];

    public static Option<string?> KeyOption { get; } = new("--key")
    {
        Description = "Only keys that match this pattern; * matches any run of characters and ? one character.",
    };

    public static Option<string?> ThemeOption { get; } = new("--theme")
    {
        Description = "Theme dictionaries to read: Light, Dark, or HighContrast. Defaults to the app's current theme.",
    };

    public static Option<bool> DefaultsOption { get; } = new("--defaults")
    {
        Description = "Also list WinUI default resources (from XamlControlsResources) that the app does not define itself.",
    };

    public DevToolsResourcesListCommand()
        : base("list", "List the keyed resources in Application.Resources, its merged dictionaries, and the theme " +
            "dictionary for the current theme, with each live value and the XAML file and line that defines it.")
    {
        Options.Add(KeyOption);
        Options.Add(ThemeOption);
        Options.Add(DefaultsOption);
    }

    internal sealed record Entry(
        string Key, string Value, string ValueType, string Dictionary, string? ThemeDictionary, bool Framework, bool Overridden);

    internal sealed record Listing(string Theme, IReadOnlyList<Entry> Entries, int Total, bool Truncated);

    internal static Listing? Parse(string? resultJson)
    {
        if (string.IsNullOrEmpty(resultJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(resultJson, TapWireJson.DocumentOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var entries = new List<Entry>();
            foreach (var item in resources.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || String(item, "key") is not { Length: > 0 } key)
                {
                    continue;
                }

                entries.Add(new Entry(
                    key,
                    String(item, "value") ?? string.Empty,
                    String(item, "valueType") ?? string.Empty,
                    String(item, "dictionary") ?? string.Empty,
                    String(item, "themeDictionary"),
                    Bool(item, "framework"),
                    Bool(item, "overridden")));
            }

            var total = root.TryGetProperty("total", out var t) && t.TryGetInt32(out var n) ? n : entries.Count;
            return new Listing(String(root, "theme") ?? string.Empty, entries, total, Bool(root, "truncated"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The definition behind a live entry: same key and theme branch, preferring app-wide dictionaries.</summary>
    internal static XamlResourceIndex.Definition? DefinitionFor(XamlResourceIndex? index, string key, string? themeDictionary)
    {
        if (index is null)
        {
            return null;
        }

        var candidates = index.Find(key)
            .Where(d => string.Equals(d.Theme, themeDictionary, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return candidates.FirstOrDefault(d => d.AppScope) ?? candidates.FirstOrDefault();
    }

    internal static string? Location(XamlResourceIndex.Definition? definition) =>
        definition is null ? null : $"{definition.File}:{definition.Line}";

    internal const int MaxShownValue = 40;

    /// <summary>The value column of the text table, with a trailing space: shortened, and omitted when it only repeats the type.</summary>
    internal static string Shown(Entry entry)
    {
        var value = entry.Value.ReplaceLineEndings(" ");
        if (value == "{" + DevToolsFormat.ShortTypeName(entry.ValueType) + "}")
        {
            return string.Empty;
        }
        return (value.Length > MaxShownValue ? value[..(MaxShownValue - 1)] + "…" : value) + " ";
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

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
            var pattern = parseResult.GetValue(KeyOption);
            var includeDefaults = parseResult.GetValue(DefaultsOption);
            var tap = target.Tap!;
            var response = tap.RequestResourceList(pattern, parseResult.GetValue(ThemeOption), cancellationToken);
            if (!response.Ok)
            {
                return Task.FromResult(Fail(json, target.Pid, response.Error!));
            }

            var listing = Parse(response.ResultJson);
            if (listing is null)
            {
                return Task.FromResult(Fail(json, target.Pid, "The DevTools agent returned an unreadable resource list."));
            }

            var shown = listing.Entries.Where(e => includeDefaults || !e.Framework).ToList();
            var hiddenDefaults = listing.Entries.Count - shown.Count;
            string? sourceRoot = null;
            XamlResourceIndex? index = null;
            if (shown.Any(e => !e.Framework))
            {
                sourceRoot = DevToolsJson.SourceRoot(tap, cancellationToken);
                index = sourceRoot is not null && Directory.Exists(sourceRoot) ? XamlResourceIndex.Build(sourceRoot) : null;
            }

            if (json)
            {
                WriteJson(DevToolsJson.Serialize(writer =>
                {
                    writer.WriteBoolean("ok", true);
                    writer.WriteNumber("processId", target.Pid);
                    writer.WriteString("theme", listing.Theme);
                    writer.WriteStartArray("resources");
                    foreach (var entry in shown)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("key", entry.Key);
                        writer.WriteString("value", entry.Value);
                        writer.WriteString("valueType", entry.ValueType);
                        writer.WriteString("dictionary", entry.Dictionary);
                        if (entry.ThemeDictionary is not null)
                        {
                            writer.WriteString("themeDictionary", entry.ThemeDictionary);
                        }
                        writer.WriteBoolean("framework", entry.Framework);
                        writer.WriteBoolean("overridden", entry.Overridden);
                        if (!entry.Framework && DefinitionFor(index, entry.Key, entry.ThemeDictionary) is { } definition)
                        {
                            writer.WriteString("file", definition.File);
                            if (DevToolsJson.ProjectPath(sourceRoot, definition.File) is { } path)
                            {
                                writer.WriteString("path", path);
                            }
                            writer.WriteNumber("line", definition.Line);
                        }
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteNumber("hiddenDefaults", hiddenDefaults);
                    writer.WriteNumber("total", listing.Total);
                    writer.WriteBoolean("truncated", listing.Truncated);
                }));
                return Task.FromResult(0);
            }

            var scope = pattern is null ? string.Empty : $" matching {Markup.Escape(pattern)}";
            if (shown.Count == 0)
            {
                DevToolsRender.WriteMarkupLine(Console, $"No app resources{scope} ({Markup.Escape(listing.Theme)} theme).");
            }
            else
            {
                DevToolsRender.WriteMarkupLine(Console,
                    $"{shown.Count} resource{(shown.Count == 1 ? "" : "s")}{scope} [grey]({Markup.Escape(listing.Theme)} theme)[/]");
                var width = Math.Min(40, shown.Max(e => e.Key.Length));
                foreach (var entry in shown)
                {
                    var where = entry.Framework
                        ? "WinUI default"
                        : Location(DefinitionFor(index, entry.Key, entry.ThemeDictionary)) ?? entry.Dictionary;
                    var branch = entry.ThemeDictionary is null ? string.Empty : $" [{entry.ThemeDictionary}]";
                    var changed = entry.Overridden ? " [yellow]changed by DevTools[/]" : string.Empty;
                    DevToolsRender.WriteMarkupLine(Console,
                        $"  {Markup.Escape(entry.Key.PadRight(width))}  {Markup.Escape(Shown(entry))}" +
                        $"[grey]{Markup.Escape(DevToolsFormat.ShortTypeName(entry.ValueType))} · {Markup.Escape(where)}{Markup.Escape(branch)}[/]{changed}");
                }
            }

            if (listing.Truncated)
            {
                DevToolsRender.WriteMarkupLine(Console,
                    $"[grey]Only the first {listing.Entries.Count} of {listing.Total} matches were read; narrow them with --key.[/]");
            }
            if (hiddenDefaults > 0)
            {
                DevToolsRender.WriteMarkupLine(Console,
                    $"[grey]{hiddenDefaults} WinUI default resource{(hiddenDefaults == 1 ? "" : "s")} also match; add --defaults to list them.[/]");
            }

            return Task.FromResult(0);
        }
    }
}

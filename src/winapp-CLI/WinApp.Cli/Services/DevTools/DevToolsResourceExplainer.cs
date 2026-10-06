// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace WinApp.Cli.Services.DevTools;

/// <summary>A project-relative XAML position: <c>/</c>-separated file and 1-based line.</summary>
internal sealed record ExplainLocation(string File, int Line)
{
    public override string ToString() => $"{File}:{Line}";
}

/// <summary>
/// What set the winning value. <see cref="Kind"/> is <c>style</c>, <c>defaultStyle</c>, <c>local</c>,
/// <c>default</c>, or <c>other</c>; <see cref="Source"/> is the agent's value-source label for it.
/// </summary>
/// <param name="StyleKind"><c>explicit</c>, <c>implicit</c>, or <c>default</c> (the WinUI default style).</param>
/// <param name="StyleAt">Where the applied Style is declared.</param>
/// <param name="At">Where the value is authored: the Setter for a style, the element for a local value.</param>
/// <param name="Authored">The authored value text, e.g. <c>{ThemeResource ButtonForeground}</c>.</param>
/// <param name="BasedOn">Style keys followed through <c>BasedOn</c> to reach the setter, in order.</param>
/// <param name="StyleDefinedIn"><c>app</c> or <c>winui</c>, for a Style: whether the app or WinUI declares it.</param>
internal sealed record ExplainOrigin(
    string Kind,
    string Source,
    string? StyleKind = null,
    string? StyleKey = null,
    string? TargetType = null,
    ExplainLocation? StyleAt = null,
    ExplainLocation? At = null,
    string? Authored = null,
    IReadOnlyList<string>? BasedOn = null,
    string? StyleDefinedIn = null);

/// <param name="Scope"><c>app</c> (App.xaml or a dictionary it merges) or <c>file</c> (one page's or control's resources).</param>
/// <param name="InScope">Whether the definition can be reached from where the value is authored.</param>
/// <param name="Applies">In scope AND its theme branch matches the element's theme.</param>
internal sealed record ExplainDefinition(
    ExplainLocation At, string? Theme, string Scope, bool InScope, bool Applies, string? Value, string? AliasOf);

/// <summary>One resource-lookup hop: the key the value references, or an alias it forwards to.</summary>
/// <param name="Kind"><c>themeResource</c>, <c>staticResource</c>, or <c>alias</c>.</param>
/// <param name="Framework">No app definition applies, so the value comes from WinUI's own resources.</param>
internal sealed record ExplainResource(string Key, string Kind, IReadOnlyList<ExplainDefinition> Definitions, bool Framework);

internal sealed record ResourceExplanation(
    string Property,
    string Value,
    string ValueType,
    string? Theme,
    ExplainOrigin Origin,
    IReadOnlyList<ExplainResource> Resources,
    ExplainLocation? ChangeAt,
    IReadOnlyList<string> Notes);

/// <summary>
/// Explains where one live property value comes from: which Style setter or local value set it, which resource
/// key that names, and where the key is defined for the element's theme. The agent's value chain says WHICH Style
/// won (and where it is declared); this reads the app's XAML to find the Setter line and the key it uses.
/// </summary>
internal static class DevToolsResourceExplainer
{
    private const int MaximumHops = 8;

    private sealed record ChainEntry(string Source, bool Winner, string? TargetType, string? File, int Line, string? RuntimeFile);

    // WinUI's own XAML: ms-appx:///Microsoft.UI.Xaml/Themes/… or ms-resource:///Files/Microsoft.UI.Xaml;component/themes/….
    private static bool IsFrameworkFile(string? runtimeFile) =>
        runtimeFile is not null && (runtimeFile.Contains("/Microsoft.UI.Xaml/Themes/", StringComparison.OrdinalIgnoreCase) ||
            runtimeFile.Contains("/Microsoft.UI.Xaml;component/", StringComparison.OrdinalIgnoreCase));

    /// <param name="row">The raw <c>Property.get</c> row for the property, including its <c>chain</c>.</param>
    /// <param name="styleRow">The element's <c>Style</c> row: an explicit style's key is authored there.</param>
    /// <param name="theme">The element's <c>ActualTheme</c> (Light or Dark).</param>
    /// <param name="index">The app's resource index, or null when the project folder is unknown.</param>
    public static ResourceExplanation Explain(
        JsonElement row, DevToolsPropertyRow property, DevToolsPropertyRow? styleRow, string? theme, XamlResourceIndex? index)
    {
        var notes = new List<string>();
        var chain = ReadChain(row);
        var winner = chain.FirstOrDefault(c => c.Winner);
        var source = winner?.Source ?? property.ValueSource ?? "Default";
        var winnerAt = winner is { File: not null, Line: > 0 } ? new ExplainLocation(winner.File, winner.Line) : null;

        ExplainOrigin origin;
        (string Kind, string Key)? reference = null;
        if (property.AuthoredKind is "themeResource" or "staticResource" && property.AuthoredKey is { } authoredKey &&
            source.Equals("Local", StringComparison.OrdinalIgnoreCase))
        {
            origin = new ExplainOrigin("local", source, At: winnerAt, Authored: property.Authored);
            reference = (property.AuthoredKind, authoredKey);
        }
        else if (source.Equals("Style", StringComparison.OrdinalIgnoreCase))
        {
            (origin, reference) = FromStyle(property.Name, source, winner, winnerAt, styleRow, index, notes);
        }
        else if (source.Equals("Built-in style", StringComparison.OrdinalIgnoreCase))
        {
            origin = new ExplainOrigin("defaultStyle", source, StyleKind: "default", TargetType: ShortType(winner?.TargetType));
            notes.Add("The WinUI default style for this control sets it. Override the resource it uses in your app's resources, " +
                "or set the property on the element or in your own Style.");
        }
        else if (source.Equals("Local", StringComparison.OrdinalIgnoreCase))
        {
            origin = new ExplainOrigin("local", source, At: winnerAt, Authored: property.Authored);
            if (property.AuthoredKind is "binding" or "xBind" or "templateBinding" || property.Binding is not null)
            {
                notes.Add("A binding sets this value, not a resource. Run `winapp devtools diagnose-binding` to trace it.");
            }
        }
        else if (source.Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            origin = new ExplainOrigin("default", source);
            notes.Add("Nothing sets this property on the element; it has the property's default value.");
        }
        else
        {
            origin = new ExplainOrigin("other", source, At: winnerAt);
            notes.Add($"This value comes from {source}. `resources explain` traces Style setters and resource keys only.");
        }

        var resources = new List<ExplainResource>();
        if (reference is { } first)
        {
            if (index is null)
            {
                resources.Add(new ExplainResource(first.Key, first.Kind, [], Framework: false));
                notes.Add("The app's project folder is unknown, so resource definitions can't be looked up.");
            }
            else
            {
                Resolve(first, origin.At?.File, theme, index, resources, notes);
            }
        }

        return new ResourceExplanation(property.Name, property.Value, property.ValueType, theme, origin, resources,
            ChangeAt(origin, resources), notes);
    }

    private static (ExplainOrigin, (string, string)?) FromStyle(
        string propertyName, string source, ChainEntry? winner, ExplainLocation? winnerAt, DevToolsPropertyRow? styleRow,
        XamlResourceIndex? index, List<string> notes)
    {
        var explicitKey = styleRow is { AuthoredKey: { } key, Value.Length: > 0 } ? key : null;
        var targetType = ShortType(winner?.TargetType);
        var style = winnerAt is not null ? index?.StyleAt(winnerAt.File, winnerAt.Line) : null;
        if (style is null && winnerAt is null && explicitKey is not null && index?.StylesWithKey(explicitKey, null) is [var keyed])
        {
            style = keyed;
        }
        if (style is null && winnerAt is null && IsFrameworkFile(winner?.RuntimeFile))
        {
            var name = explicitKey ?? "This Style";
            notes.Add($"{name} is defined in WinUI's resources, not in your app. To change {propertyName}, set it on the element, " +
                $"or use your own Style{(explicitKey is null ? string.Empty : $" with BasedOn=\"{{StaticResource {explicitKey}}}\"")}.");
            return (new ExplainOrigin("style", source, explicitKey is null ? null : "explicit", explicitKey, targetType,
                StyleDefinedIn: "winui"), null);
        }
        if (style is null)
        {
            notes.Add(winnerAt is null
                ? "The app did not report where this Style is declared. Build it with XAML source info (a Debug build) to locate it."
                : index is null
                    ? "The app's project folder is unknown, so the Style's setters can't be read."
                    : $"No Style is declared at {winnerAt} in the current source. It may have changed since the app was built.");
            return (new ExplainOrigin("style", source, explicitKey is null ? null : "explicit", explicitKey, targetType,
                StyleAt: winnerAt), null);
        }

        var styleKind = style.Key is null ? "implicit" : "explicit";
        var basedOn = new List<string>();
        var visited = new HashSet<XamlResourceIndex.StyleEntry>();
        var current = style;
        XamlResourceIndex.Setter? setter = null;
        while (current is not null && visited.Add(current) && visited.Count <= MaximumHops)
        {
            setter = current.Setters.FirstOrDefault(s => SameProperty(s.Property, propertyName));
            if (setter is not null || current.BasedOn is not { } baseKey)
            {
                break;
            }
            basedOn.Add(baseKey);
            current = index!.StylesWithKey(baseKey, current.File) is [var match, ..] ? match : null;
            if (current is null)
            {
                notes.Add($"{style.Key ?? "The implicit style"} does not set {propertyName}; it inherits it from the WinUI style " +
                    $"{baseKey} through BasedOn.");
            }
        }

        if (setter is null && current is not null)
        {
            notes.Add($"No Setter for {propertyName} was found in the Style at {new ExplainLocation(current.File, current.Line)}.");
        }

        var origin = new ExplainOrigin("style", source, styleKind, style.Key ?? explicitKey, ShortType(style.TargetType) ?? targetType,
            StyleAt: new ExplainLocation(style.File, style.Line),
            At: setter is null ? null : new ExplainLocation(current!.File, setter.Line),
            Authored: setter?.Value,
            BasedOn: basedOn.Count == 0 ? null : basedOn,
            StyleDefinedIn: "app");
        return (origin, XamlResourceIndex.ResourceReference(setter?.Value));
    }

    // Follows the key, then any <StaticResource ResourceKey="…"/> aliases, recording each hop's definitions.
    private static void Resolve(
        (string Kind, string Key) reference, string? nearFile, string? theme, XamlResourceIndex index,
        List<ExplainResource> resources, List<string> notes)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var (kind, key) = reference;
        while (seen.Add(key) && resources.Count < MaximumHops)
        {
            var all = index.Find(key);
            var definitions = all
                .Select(d =>
                {
                    var inScope = d.AppScope || (nearFile is not null && string.Equals(d.File, nearFile, StringComparison.OrdinalIgnoreCase));
                    return new ExplainDefinition(new ExplainLocation(d.File, d.Line), d.Theme, d.AppScope ? "app" : "file",
                        inScope, inScope && ThemeApplies(d, theme, all), d.Value, d.AliasOf);
                })
                .OrderBy(d => !d.Applies)
                .ThenBy(d => !d.InScope)
                .ThenBy(d => nearFile is not null && string.Equals(d.At.File, nearFile, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(d => d.Scope == "app" && d.At.File.Equals("App.xaml", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToList();
            var applied = definitions.FirstOrDefault(d => d.Applies);
            resources.Add(new ExplainResource(key, kind, definitions, Framework: applied is null));
            if (applied is not null && definitions.Count(d => d.Applies) > 1 &&
                definitions.Where(d => d.Applies).Select(d => d.At.File).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            {
                notes.Add($"{key} is defined in more than one dictionary in scope; the nearest one wins " +
                    "(the element's own resources, then its page, then App.xaml).");
            }
            if (applied?.AliasOf is not { } alias)
            {
                break;
            }
            (kind, key, nearFile) = ("alias", alias, applied.At.File);
        }
    }

    // A ThemeDictionaries branch applies when it names the element's theme. "Default" stands in for a theme
    // the same dictionary has no branch for. HighContrast branches only apply with a high-contrast theme on.
    private static bool ThemeApplies(XamlResourceIndex.Definition definition, string? theme, IReadOnlyList<XamlResourceIndex.Definition> all)
    {
        if (definition.Theme is null)
        {
            return true;
        }
        if (theme is not null && definition.Theme.Equals(theme, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return definition.Theme.Equals("Default", StringComparison.OrdinalIgnoreCase) && (theme is null ||
            !all.Any(d => string.Equals(d.File, definition.File, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(d.Theme, theme, StringComparison.OrdinalIgnoreCase)));
    }

    private static ExplainLocation? ChangeAt(ExplainOrigin origin, IReadOnlyList<ExplainResource> resources) =>
        resources is [.., { Framework: false } last] && last.Definitions.FirstOrDefault(d => d.Applies) is { } definition
            ? definition.At
            : origin.At;

    /// <summary>Where the app Style that sets this property is declared, when the chain names an app file.</summary>
    internal static ExplainLocation? AppStyleLocation(JsonElement row) =>
        ReadChain(row).FirstOrDefault(c => c.Source.Equals("Style", StringComparison.OrdinalIgnoreCase) && c.File is not null) is { } entry
            ? new ExplainLocation(entry.File!, entry.Line)
            : null;

    private static bool SameProperty(string setterProperty, string property) =>
        setterProperty.Equals(property, StringComparison.OrdinalIgnoreCase) ||
        (setterProperty.LastIndexOf('.') is var dot and > 0 &&
            (setterProperty[(dot + 1)..].Equals(property, StringComparison.OrdinalIgnoreCase) ||
             StripPrefix(setterProperty).Equals(property, StringComparison.OrdinalIgnoreCase)));

    private static string StripPrefix(string name) => name.IndexOf(':') is var colon and >= 0 ? name[(colon + 1)..] : name;

    private static string? ShortType(string? type) =>
        type is null ? null : StripPrefix(type.LastIndexOf('.') is var dot and >= 0 ? type[(dot + 1)..] : type);

    // Reads the chain's location in the vocabulary DevToolsJson.WriteChainEntry uses: the authored, project-relative
    // file and line when the agent confirmed them, else the ms-appx path. The runtime line is NOT trusted on its own:
    // it can point into the compiler-generated XAML rather than the source.
    private static List<ChainEntry> ReadChain(JsonElement row)
    {
        var entries = new List<ChainEntry>();
        if (!row.TryGetProperty("chain", out var chain) || chain.ValueKind != JsonValueKind.Array)
        {
            return entries;
        }
        foreach (var entry in chain.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
        {
            var line = entry.TryGetProperty("authoredLineNumber", out var a) && a.ValueKind == JsonValueKind.Number &&
                a.TryGetInt32(out var n) ? n : 0;
            var file = line > 0 && String(entry, "authoredFileName") is { Length: > 0 } authored
                ? XamlResourceIndex.NormalizeFile(authored)
                : null;
            entries.Add(new ChainEntry(String(entry, "source") ?? string.Empty,
                entry.TryGetProperty("winner", out var w) && w.ValueKind == JsonValueKind.True,
                String(entry, "targetType"), file, file is null ? 0 : line, String(entry, "file")));
        }
        return entries;
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

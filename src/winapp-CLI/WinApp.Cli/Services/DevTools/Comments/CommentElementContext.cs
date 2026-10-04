// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WinApp.Cli.Services.DevTools.Comments;

/// <summary>
/// The style and brush resources behind a live element, so an agent fixing "make this warmer" can go straight to
/// the resource instead of searching for it. Built from one <c>Property.get</c> reply; a resource key is reported
/// only where it is known: authored on the element, or on a setter of a style declared in the project.
/// </summary>
internal static partial class CommentElementContext
{
    private static readonly string[] BrushProperties = ["Foreground", "Background", "BorderBrush", "Fill", "Stroke"];
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static (CommentStyleContext? Style, List<CommentBrushContext>? Brushes) Read(string? propsJson, string? sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(propsJson))
        {
            return (null, null);
        }
        using var doc = JsonDocument.Parse(propsJson, TapWireJson.DocumentOptions);
        if (!doc.RootElement.TryGetProperty("props", out var props) || props.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }
        var rows = props.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Object).ToList();
        var styles = new Dictionary<(string, int), XElement?>();
        CommentStyleContext? appStyle = null;
        var brushes = new List<CommentBrushContext>();
        foreach (var name in BrushProperties)
        {
            var element = rows.FirstOrDefault(r => DevToolsPropertyRow.FromElement(r)?.Name == name);
            if (element.ValueKind != JsonValueKind.Object || DevToolsPropertyRow.FromElement(element) is not { IsSet: true } row)
            {
                continue;
            }
            var brush = new CommentBrushContext
            {
                Property = name,
                Value = Redacted(element) || row.Value.Length == 0 ? null : row.Value,
                Source = row.ValueSource,
            };
            if (row.AuthoredKind is "themeResource" or "staticResource")
            {
                brush.ResourceKey = row.AuthoredKey;
                brush.ResourceKind = row.AuthoredKind;
            }
            else if (WinningStyle(element, sourceRoot, styles) is var (file, line, style) && style is not null)
            {
                brush.File = file;
                brush.Line = line;
                var setter = style.Elements().FirstOrDefault(e => e.Name.LocalName == "Setter" &&
                    PropertyName((string?)e.Attribute("Property")) == name);
                if (setter is not null)
                {
                    brush.Line = ((System.Xml.IXmlLineInfo)setter).LineNumber;
                    (brush.ResourceKind, brush.ResourceKey) = ResourceReference((string?)setter.Attribute("Value"));
                }
                appStyle ??= new CommentStyleContext
                {
                    Key = (string?)style.Attribute(Xaml + "Key"),
                    Kind = style.Attribute(Xaml + "Key") is null ? "implicit" : "resource",
                    TargetType = (string?)style.Attribute("TargetType"),
                    File = file,
                    Line = line,
                };
            }
            brushes.Add(brush);
        }

        var styleRow = rows.Select(r => DevToolsPropertyRow.FromElement(r)).FirstOrDefault(r => r?.Name == "Style");
        var styleContext = styleRow?.AuthoredKind is "themeResource" or "staticResource"
            ? new CommentStyleContext { Key = styleRow.AuthoredKey, Kind = styleRow.AuthoredKind }
            : appStyle;
        if (styleContext is not null && appStyle is not null && styleContext != appStyle && styleContext.Key == appStyle.Key)
        {
            (styleContext.TargetType, styleContext.File, styleContext.Line) = (appStyle.TargetType, appStyle.File, appStyle.Line);
        }
        else if (styleContext is { Key: { } key, File: null } && !string.IsNullOrEmpty(sourceRoot) &&
            FindKeyedStyle(sourceRoot, key) is var (file, line, targetType))
        {
            (styleContext.TargetType, styleContext.File, styleContext.Line) = (targetType, file, line);
        }
        return (styleContext, brushes.Count == 0 ? null : brushes);
    }

    // The one style in the project declared with this key, in any XAML file (a page's resources included).
    private static (string File, int Line, string? TargetType)? FindKeyedStyle(string sourceRoot, string key)
    {
        (string, int, string?)? found = null;
        try
        {
            foreach (var path in Directory.EnumerateFiles(sourceRoot, "*.xaml",
                new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }))
            {
                if (CommentAnchorResolver.IsBuildOutput(path) || !File.ReadAllText(path).Contains($"\"{key}\"", StringComparison.Ordinal))
                {
                    continue;
                }
                foreach (var style in XDocument.Load(path, LoadOptions.SetLineInfo).Descendants()
                    .Where(e => e.Name.LocalName == "Style" && (string?)e.Attribute(Xaml + "Key") == key))
                {
                    if (found is not null)
                    {
                        return null;
                    }
                    found = (Path.GetRelativePath(sourceRoot, path), ((System.Xml.IXmlLineInfo)style).LineNumber, (string?)style.Attribute("TargetType"));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
        return found;
    }

    /// <summary>The captured style and brushes in a few short lines, for human output.</summary>
    public static IEnumerable<string> Lines(CommentContext? context)
    {
        if (context?.Style is { } style)
        {
            var name = style.Key ?? (style.TargetType is null ? "implicit style" : $"implicit {style.TargetType} style");
            yield return $"Style: {name}{Where(style.File, style.Line)}";
        }
        foreach (var brush in context?.Brushes ?? [])
        {
            var resource = brush.ResourceKey is null ? string.Empty
                : $" from {(brush.ResourceKind == "themeResource" ? "ThemeResource" : "StaticResource")} {brush.ResourceKey}";
            yield return $"{brush.Property}: {brush.Value ?? "(not shown)"}{resource} ({brush.Source}{Where(brush.File, brush.Line)})";
        }
    }

    private static string Where(string? file, int? line) =>
        file is null ? string.Empty : $", {file}{(line is > 0 ? $":{line}" : string.Empty)}";

    private static bool Redacted(JsonElement row) =>
        row.TryGetProperty("redacted", out var redacted) && redacted.ValueKind == JsonValueKind.True;

    // The style that set the winning value, when that style is declared in a project file.
    private static (string File, int Line, XElement? Style)? WinningStyle(JsonElement row, string? sourceRoot,
        Dictionary<(string, int), XElement?> styles)
    {
        if (string.IsNullOrEmpty(sourceRoot) || !row.TryGetProperty("chain", out var chain) || chain.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var winner = chain.EnumerateArray().FirstOrDefault(c => c.ValueKind == JsonValueKind.Object &&
            c.TryGetProperty("winner", out var w) && w.ValueKind == JsonValueKind.True);
        if (winner.ValueKind != JsonValueKind.Object ||
            !winner.TryGetProperty("source", out var source) || source.GetString() != "Style" ||
            !winner.TryGetProperty("file", out var uri) || uri.ValueKind != JsonValueKind.String ||
            !winner.TryGetProperty("line", out var lineValue) || lineValue.ValueKind != JsonValueKind.Number ||
            !lineValue.TryGetInt32(out var line) || line <= 0)
        {
            return null;
        }
        // The confirmed start of the style's tag; the runtime position is its end, which differs for a multi-line tag.
        if (winner.TryGetProperty("authoredLineNumber", out var start) && start.ValueKind == JsonValueKind.Number &&
            start.TryGetInt32(out var startLine) && startLine > 0)
        {
            line = startLine;
        }
        string? path;
        try { path = CommentAnchorResolver.ResolveKnownSourcePath(sourceRoot, new CommentAnchor { SourceUri = uri.GetString() }); }
        catch (IOException) { path = null; }
        if (path is null)
        {
            return null;
        }
        var file = Path.GetRelativePath(sourceRoot, path);
        if (!styles.TryGetValue((path, line), out var style))
        {
            try
            {
                style = XDocument.Load(path, LoadOptions.SetLineInfo).Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "Style" && ((System.Xml.IXmlLineInfo)e).LineNumber == line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                style = null;
            }
            styles[(path, line)] = style;
        }
        return (file, line, style);
    }

    private static string? PropertyName(string? property) =>
        property is null ? null : property[(property.LastIndexOf('.') + 1)..].Trim();

    private static (string? Kind, string? Key) ResourceReference(string? value)
    {
        var match = value is null ? null : ResourceMarkup().Match(value);
        return match is { Success: true }
            ? (match.Groups[1].Value == "ThemeResource" ? "themeResource" : "staticResource", match.Groups[2].Value)
            : (null, null);
    }

    [GeneratedRegex(@"^\s*\{\s*(ThemeResource|StaticResource)\s+(?:ResourceKey\s*=\s*)?([^\s,}]+)\s*\}\s*$")]
    private static partial Regex ResourceMarkup();
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace WinApp.Cli.Services.DevTools;

/// <summary>
/// A <c>{ThemeResource key}</c> or <c>{StaticResource key}</c> reference to assign with <c>devtools set-property</c>.
/// </summary>
/// <param name="Kind"><c>theme</c> or <c>static</c>, the same words <c>--resource</c> accepts.</param>
/// <param name="Key">The resource key, for example <c>AccentFillColorDefaultBrush</c>.</param>
internal sealed partial record DevToolsResourceReference(string Kind, string Key)
{
    public string Markup => $"{{{(Kind == "theme" ? "ThemeResource" : "StaticResource")} {Key}}}";

    /// <summary>The kind as property rows name it: <c>themeResource</c> or <c>staticResource</c>.</summary>
    public string RowKind => Kind == "theme" ? "themeResource" : "staticResource";

    /// <summary>Parses <c>{ThemeResource key}</c> or <c>{StaticResource key}</c>; anything else returns null.</summary>
    public static DevToolsResourceReference? Parse(string? value)
    {
        var match = value is null ? null : ResourceMarkup().Match(value);
        return match is { Success: true }
            ? new(match.Groups[1].Value == "ThemeResource" ? "theme" : "static", match.Groups[2].Value)
            : null;
    }

    /// <summary>
    /// Decides whether a set-property value is a resource reference. <paramref name="resource"/> is the
    /// <c>--resource</c> value; any <paramref name="type"/> makes the value a literal.
    /// </summary>
    /// <returns>An error message for invalid input, otherwise null.</returns>
    public static string? Resolve(string? resource, string? type, string? value, out DevToolsResourceReference? reference)
    {
        reference = null;
        if (resource is null)
        {
            reference = string.IsNullOrWhiteSpace(type) ? Parse(value) : null;
            return null;
        }
        if (!string.IsNullOrWhiteSpace(type))
        {
            return "--resource and --type can't be used together: a resource keeps its own type.";
        }
        var parsed = Parse(value);
        if (parsed is not null && parsed.Kind != resource)
        {
            return $"--resource {resource} doesn't match the value {value!.Trim()}. Pass the key alone, for example `--resource {resource} {parsed.Key}`.";
        }
        var key = parsed?.Key ?? value?.Trim();
        if (string.IsNullOrEmpty(key) || key.IndexOfAny([' ', '\t', '\r', '\n', '{', '}', ',', '|']) >= 0)
        {
            return "--resource needs a resource key as the value, for example `--resource theme AccentFillColorDefaultBrush`.";
        }
        reference = new(resource, key);
        return null;
    }

    [GeneratedRegex(@"^\s*\{\s*(ThemeResource|StaticResource)\s+(?:ResourceKey\s*=\s*)?([^\s,}|]+)\s*\}\s*$")]
    private static partial Regex ResourceMarkup();
}

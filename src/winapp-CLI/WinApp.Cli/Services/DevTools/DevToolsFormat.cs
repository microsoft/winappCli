// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.DevTools;

internal static class DevToolsFormat
{
    public static string ShortTypeName(string? type)
    {
        if (string.IsNullOrEmpty(type))
        {
            return "?";
        }

        var dot = type.LastIndexOf('.');
        return dot < 0 || dot == type.Length - 1 ? type : type[(dot + 1)..];
    }

    public static string? ShortFileName(string? file)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return null;
        }

        const string appx = "ms-appx:///";
        var trimmed = file.StartsWith(appx, StringComparison.OrdinalIgnoreCase) ? file[appx.Length..] : file;
        return trimmed.Length == 0 ? null : trimmed;
    }

    public static bool Matches(VisualTreeNode node, string normalizedQuery)
    {
        if (normalizedQuery.Length == 0)
        {
            return true;
        }

        var label = node.ShortType;
        if (node.Name.Length > 0)
        {
            label += " #" + node.Name;
        }

        if (ShortFileName(node.File) is string file)
        {
            label += " " + file;
        }

        return label.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeQuery(string? query) => (query ?? string.Empty).Trim().ToLowerInvariant();
}

// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

using System.Text.RegularExpressions;

/// <summary>
/// Detects Gallery-style live substitution placeholders (<c>$(Name)</c>) in sample
/// snippets. Those tokens require runtime option-control values, so an index consumer
/// must not surface them as pasteable XAML or C#.
/// </summary>
internal static partial class SampleSubstitutionPlaceholder
{
    [GeneratedRegex(@"\$\([^)]+\)")]
    private static partial Regex PlaceholderPattern();

    public static bool Contains(string? value)
        => !string.IsNullOrEmpty(value) && PlaceholderPattern().IsMatch(value);

    public static string ReplaceAll(string value, string replacement)
        => PlaceholderPattern().Replace(value, replacement);

    public static bool TryMatchAt(string value, int startIndex, out int length)
    {
        length = 0;
        if (string.IsNullOrEmpty(value) || startIndex < 0 || startIndex >= value.Length)
        {
            return false;
        }

        var match = PlaceholderPattern().Match(value, startIndex);
        if (!match.Success || match.Index != startIndex)
        {
            return false;
        }

        length = match.Length;
        return true;
    }
}

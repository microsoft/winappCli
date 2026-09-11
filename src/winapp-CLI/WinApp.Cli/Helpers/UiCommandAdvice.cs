// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.Helpers;

/// <summary>Formats owned command suggestions, never child output or arbitrary exception text.</summary>
internal static class UiCommandAdvice
{
    // Diagnostics only: the guest still executes locally, without an --on argument.
    internal const string TargetVariable = "WINAPP_INTERNAL_UI_DIAGNOSTIC_TARGET";

    public static Dictionary<string, string> WithTarget(
        IReadOnlyDictionary<string, string> environment, ExecutionTargetRef target)
    {
        var merged = new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase);
        if (target.IsLocal)
        {
            merged.Remove(TargetVariable);
        }
        else
        {
            merged[TargetVariable] = target.Selector;
        }
        return merged;
    }

    public static string Command(string arguments) => Format($"winapp ui {arguments}");

    public static string Format(string command)
    {
        var selector = Environment.GetEnvironmentVariable(TargetVariable);
        return string.IsNullOrEmpty(selector)
            ? command
            : $"{command} --on {QuoteSelector(selector)}";
    }

    private static string QuoteSelector(string selector)
    {
        if (selector.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':'))
        {
            return selector;
        }

        // PowerShell literal strings keep spaces, quotes, and shell expressions in one argv value.
        // PowerShell also treats typographic apostrophes as single-quote delimiters.
        return "'" + selector.Replace("'", "''", StringComparison.Ordinal)
            .Replace("\u2018", "\u2018\u2018", StringComparison.Ordinal)
            .Replace("\u2019", "\u2019\u2019", StringComparison.Ordinal)
            .Replace("\u201a", "\u201a\u201a", StringComparison.Ordinal)
            .Replace("\u201b", "\u201b\u201b", StringComparison.Ordinal) + "'";
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using WinApp.Cli.Commands;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Detects and reports an unknown command under <c>winapp ui</c> (for example <c>winapp ui dump</c>).
/// Runs before help and <c>--on</c> routing: help is a terminating action, so
/// <c>winapp ui dump --help</c> parses without errors and would otherwise print the group help and
/// exit 0, and <c>--on sandbox</c> would start a sandbox for a command that does not exist.
/// </summary>
internal static class UiUnknownCommand
{
    internal const string RecoveryHint = "Run 'winapp ui --help' to list commands.";

    private const int MaxSuggestions = 2;

    /// <summary>Words other UI automation tools use, mapped to the winapp command that does the job.</summary>
    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dump"] = "inspect",
        ["snapshot"] = "inspect",
        ["elements"] = "inspect",
        ["query"] = "search",
        ["locate"] = "search",
        ["type"] = "send-keys",
        ["keys"] = "send-keys",
        ["read"] = "get-value",
        ["text"] = "get-value",
        ["windows"] = "list-windows",
        ["wait"] = "wait-for",
        ["press"] = "invoke",
        ["activate"] = "invoke",
    };

    private static readonly string[] CommonCommands =
        ["status", "list-windows", "inspect", "search", "invoke", "set-value", "send-keys", "get-value", "wait-for"];

    /// <summary>
    /// Returns the token in command position when the selected command is the <c>ui</c> group and
    /// that token names no command; otherwise <see langword="null"/>.
    /// </summary>
    public static string? Find(ParseResult parseResult)
    {
        if (parseResult.CommandResult.Command is not UiCommand)
        {
            return null;
        }

        return parseResult.UnmatchedTokens.FirstOrDefault(token => !token.StartsWith('-'));
    }

    public static string[] Suggest(string token, IEnumerable<Command> commands)
    {
        var visible = commands.Where(c => !c.Hidden).Select(c => c.Name).ToList();
        var suggestions = new List<string>();
        if (Synonyms.TryGetValue(token, out var synonym) && visible.Contains(synonym))
        {
            suggestions.Add(synonym);
        }

        var lower = token.ToLowerInvariant();
        var threshold = Math.Max(2, lower.Length / 3);
        suggestions.AddRange(visible
            .Select(name => (Name: name, Distance: Distance(lower, name), Prefix: lower.Length >= 3 && name.StartsWith(lower, StringComparison.Ordinal)))
            .Where(c => c.Distance <= threshold || c.Prefix)
            .OrderBy(c => c.Prefix ? 0 : 1)
            .ThenBy(c => c.Distance)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => c.Name)
            .Where(name => !suggestions.Contains(name)));

        return suggestions.Take(MaxSuggestions).ToArray();
    }

    public static string Message(string token) => $"Unknown command '{token}'.";

    public static void WriteText(TextWriter error, string token, string[] suggestions)
    {
        var didYouMean = suggestions.Length switch
        {
            0 => "",
            1 => $" Did you mean '{suggestions[0]}'?",
            _ => $" Did you mean '{suggestions[0]}' or '{suggestions[1]}'?",
        };
        error.WriteLine(Message(token) + didYouMean);
        error.WriteLine($"Commands: {string.Join(", ", CommonCommands)}, ...");
        error.WriteLine("Run 'winapp ui --help' for the full list.");
    }

    internal static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}

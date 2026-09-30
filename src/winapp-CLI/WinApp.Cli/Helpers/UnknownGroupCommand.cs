// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using WinApp.Cli.Commands;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Detects and reports an unknown command under a compact-help group (for example <c>winapp ui dump</c>
/// or <c>winapp devtools set-text</c>). Runs before help and <c>--on</c> routing: help is a terminating
/// action, so <c>winapp ui dump --help</c> parses without errors and would otherwise print the group help
/// and exit 0, and <c>--on sandbox</c> would start a sandbox for a command that does not exist.
/// </summary>
internal static class UnknownGroupCommand
{
    private const int MaxSuggestions = 2;

    public static string RecoveryHint(Command group) => $"Run '{Path(group)} --help' to list commands.";

    /// <summary>
    /// Returns the token in command position when the selected command is a compact-help group and
    /// that token names no command; otherwise <see langword="null"/>.
    /// </summary>
    public static string? Find(ParseResult parseResult)
    {
        if (parseResult.CommandResult.Command is not ICompactHelpGroup)
        {
            return null;
        }

        // Only the command position counts: when the first unmatched token is an option ("ui -a Notepad"),
        // the command is missing rather than misspelled, and a real command name after "--" is not a typo.
        var unmatched = parseResult.UnmatchedTokens;
        var token = unmatched.Count > 0 ? unmatched[0] : null;
        if (token is null || token.StartsWith('-'))
        {
            return null;
        }

        var isKnown = parseResult.CommandResult.Command.Subcommands
            .Any(c => c.Name == token || c.Aliases.Contains(token));
        return isKnown ? null : token;
    }

    public static string[] Suggest(string token, Command group)
    {
        var visible = group.Subcommands.Where(c => !c.Hidden).Select(c => c.Name).ToList();
        var suggestions = new List<string>();
        if (((ICompactHelpGroup)group).Synonyms.TryGetValue(token, out var synonym) && visible.Contains(synonym))
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

    public static string DidYouMean(string[] suggestions) => suggestions.Length switch
    {
        0 => "",
        1 => $" Did you mean '{suggestions[0]}'?",
        _ => $" Did you mean '{suggestions[0]}' or '{suggestions[1]}'?",
    };

    public static void WriteText(TextWriter error, string token, string[] suggestions, Command group)
    {
        error.WriteLine(Message(token) + DidYouMean(suggestions));
        error.WriteLine($"Commands: {string.Join(", ", ((ICompactHelpGroup)group).CommonCommands)}, ...");
        error.WriteLine($"Run '{Path(group)} --help' for the full list.");
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

    private static string Path(Command group)
    {
        var names = new List<string>();
        for (var current = group; current is not null and not RootCommand; current = current.Parents.OfType<Command>().FirstOrDefault())
        {
            names.Insert(0, current.Name);
        }
        return "winapp " + string.Join(" ", names);
    }
}

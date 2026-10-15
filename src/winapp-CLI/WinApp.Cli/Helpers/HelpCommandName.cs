// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.RegularExpressions;
using WinApp.Cli.Commands;

namespace WinApp.Cli.Helpers;

internal static partial class HelpCommandName
{
    internal static string ForInvocation(ParseResult parseResult)
    {
        var caller = parseResult.GetValue(WinAppRootCommand.CallerOption);
        if (string.IsNullOrWhiteSpace(caller))
        {
            caller = Environment.GetEnvironmentVariable("WINAPP_CLI_CALLER");
        }

        return string.Equals(caller, "python", StringComparison.OrdinalIgnoreCase) ? "winapp-py" : "winapp";
    }

    internal static string GetPath(Command command, string name)
    {
        var parts = new List<string>();
        for (var current = command; current is not null; current = current.Parents.OfType<Command>().FirstOrDefault())
        {
            parts.Add(current is RootCommand ? name : current.Name);
        }

        parts.Reverse();
        return string.Join(" ", parts);
    }

    internal static string FormatReferences(string text, Command command, string name)
    {
        if (name == "winapp")
        {
            return text;
        }

        var root = command;
        while (root.Parents.OfType<Command>().FirstOrDefault() is { } parent)
        {
            root = parent;
        }

        var commands = new HashSet<string>(StringComparer.Ordinal) { "<command>" };
        foreach (var child in root.Subcommands)
        {
            commands.Add(child.Name);
            commands.UnionWith(child.Aliases);
        }

        return CommandReference().Replace(text, match =>
        {
            var identifier = match.Groups["name"].Value;
            var next = match.Groups["command"].Value;
            return (identifier == "winapp" || identifier == root.Name)
                && (next.StartsWith('-') || commands.Contains(next)) ? name : match.Value;
        });
    }

    [GeneratedRegex(@"(?<![\w./\\-])(?<name>[\w.-]+)(?=\s+(?<command><command>|--?[\w-]+|[\w-]+))", RegexOptions.CultureInvariant)]
    private static partial Regex CommandReference();
}

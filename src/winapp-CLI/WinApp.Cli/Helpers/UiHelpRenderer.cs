// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Help;
using System.Text;
using WinApp.Cli.Commands;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Plain-text help for the <c>winapp ui</c> group and its commands. Agents read this help
/// redirected, so it has no styling, keeps the golden path first, and collapses the options
/// every command shares into one line.
/// </summary>
internal static class UiHelpRenderer
{
    internal const int Width = 88;
    internal const string GlobalOptionsLine = "Global options: -v, -q, --on <target>, --cli-schema   (see winapp --help)";

    private const int MinNameColumn = 18;
    private const int MaxNameColumn = 24;

    public static bool AppliesTo(Command command) =>
        command is UiCommand || command.Parents.OfType<Command>().Any(AppliesTo);

    public static string Render(Command command) =>
        command is UiCommand group ? RenderGroup(group) : RenderCommand(command);

    internal static string RenderGroup(UiCommand group)
    {
        var sb = new StringBuilder();
        AppendDescription(sb, "winapp ui - ", group.Description ?? "");
        sb.AppendLine();
        sb.AppendLine("Usage: winapp ui <command> [options]    Details: winapp ui <command> --help");

        var byType = group.Subcommands.ToDictionary(c => c.GetType());
        foreach (var (category, types) in UiCommand.HelpCategories)
        {
            sb.AppendLine();
            sb.AppendLine(category);
            AppendRows(sb, types
                .Where(byType.ContainsKey)
                .Select(type => byType[type])
                .Where(sub => !sub.Hidden)
                .Select(sub => (sub.Name, sub is IShortDescription sd ? sd.ShortDescription : sub.Description ?? ""))
                .ToList());
        }

        sb.AppendLine();
        sb.AppendLine("Options:");
        AppendRows(sb,
        [
            ("--on <target>", "Run on 'sandbox' (Windows Sandbox) or 'local' (default)"),
            ("-h, --help", "Show help"),
        ]);
        return sb.ToString();
    }

    internal static string RenderCommand(Command command)
    {
        var path = GetCommandPath(command);
        var sb = new StringBuilder();
        var summary = command is IShortDescription sd ? sd.ShortDescription.TrimEnd('.') + "." : "";
        AppendDescription(sb, $"{path} - ", summary);
        if (!string.IsNullOrWhiteSpace(command.Description) && command.Description != summary)
        {
            AppendDescription(sb, "", command.Description);
        }

        sb.AppendLine();
        sb.AppendLine("Usage: " + ((command as IHelpExamples)?.Usage ?? BuildUsage(command, path)));

        if (command is IHelpExamples { Examples.Count: > 0 } examples)
        {
            sb.AppendLine();
            sb.AppendLine("Examples:");
            foreach (var example in examples.Examples)
            {
                sb.Append("  ").AppendLine(example);
            }
        }

        var arguments = command.Arguments.Where(a => !a.Hidden).ToList();
        if (arguments.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Arguments:");
            AppendRows(sb, arguments.Select(a => ($"<{a.Name}>", a.Description ?? "")).ToList());
        }

        var options = command.Options.Where(IsCommandOption)
            .OrderBy(o => o == WinAppRootCommand.JsonOption ? 1 : 0)
            .ToList();
        if (options.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Options:");
            AppendRows(sb, options.Select(o => (FormatOption(o), o.Description ?? "")).ToList());
        }

        sb.AppendLine();
        sb.AppendLine(GlobalOptionsLine);
        return sb.ToString();
    }

    private static bool IsCommandOption(Option option) =>
        !option.Hidden
        && option is not HelpOption
        && option != WinAppRootCommand.VerboseOption
        && option != WinAppRootCommand.QuietOption;

    private static string BuildUsage(Command command, string path)
    {
        var parts = new List<string> { path };
        parts.AddRange(command.Arguments.Where(a => !a.Hidden).Select(a => $"<{a.Name}>"));
        var hasApp = command.Options.Contains(SharedUiOptions.AppOption);
        var hasWindow = command.Options.Contains(SharedUiOptions.WindowOption);
        if (hasApp && hasWindow)
        {
            parts.Add("(-a <app> | -w <hwnd>)");
        }
        else if (hasApp)
        {
            parts.Add("[-a <app>]");
        }

        parts.Add("[options]");
        return string.Join(" ", parts);
    }

    private static string FormatOption(Option option)
    {
        var names = option.Aliases.Where(a => a != option.Name).OrderBy(a => a.Length).Append(option.Name);
        var label = string.Join(", ", names);
        var takesValue = option.Arity.MaximumNumberOfValues > 0 && option.ValueType != typeof(bool);
        return takesValue ? $"{label} <{option.HelpName ?? option.Name.TrimStart('-')}>" : label;
    }

    private static void AppendRows(StringBuilder sb, IReadOnlyList<(string Name, string Description)> rows)
    {
        var column = Math.Clamp(rows.Max(r => r.Name.Length) + 2, MinNameColumn, MaxNameColumn);
        foreach (var (name, description) in rows)
        {
            var lines = Wrap(description, Width - 2 - column);
            if (name.Length + 2 > column || lines.Count == 0)
            {
                // A label wider than the column (for example '--allow-system-keys') gets its own line.
                sb.Append("  ").AppendLine(name);
                foreach (var line in lines)
                {
                    sb.Append(' ', 2 + column).AppendLine(line);
                }
                continue;
            }

            for (var i = 0; i < lines.Count; i++)
            {
                sb.Append("  ").Append(i == 0 ? name.PadRight(column) : new string(' ', column)).AppendLine(lines[i]);
            }
        }
    }

    private static void AppendDescription(StringBuilder sb, string prefix, string text)
    {
        var first = true;
        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = first ? prefix + rawLine : rawLine;
            first = false;
            if (line.Length == 0)
            {
                sb.AppendLine();
                continue;
            }

            // Indented lines (the golden path block) are preformatted; only prose is wrapped.
            if (char.IsWhiteSpace(line[0]))
            {
                sb.AppendLine(line);
                continue;
            }

            foreach (var wrapped in Wrap(line, Width))
            {
                sb.AppendLine(wrapped);
            }
        }
    }

    internal static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return lines;
        }

        width = Math.Max(20, width);
        if (text.Length <= width)
        {
            lines.Add(text);
            return lines;
        }

        var current = new StringBuilder();
        foreach (var word in text.Split(' '))
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > width)
            {
                lines.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append(' ');
            }
            current.Append(word);
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }
        return lines;
    }

    private static string GetCommandPath(Command command)
    {
        var parts = new List<string>();
        for (var current = command; current is not null; current = current.Parents.OfType<Command>().FirstOrDefault())
        {
            parts.Add(current is RootCommand ? "winapp" : current.Name);
        }

        parts.Reverse();
        return string.Join(" ", parts);
    }
}

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

    public static string Render(Command command, string commandName = "winapp") =>
        command is UiCommand group ? RenderGroup(group, commandName) : RenderCommand(command, commandName);

    internal static string RenderGroup(UiCommand group, string commandName = "winapp")
    {
        var sb = new StringBuilder();
        var path = HelpCommandName.GetPath(group, commandName);
        AppendDescription(sb, $"{path} - ",
            HelpCommandName.FormatReferences((group.Description ?? "") + "\n" + UiCommand.GoldenPath, group, commandName));
        sb.AppendLine();
        sb.AppendLine($"Usage: {path} <command> [options]    Details: {path} <command> --help");

        var byType = group.Subcommands.ToDictionary(c => c.GetType());
        foreach (var (category, types) in UiCommand.HelpCategories)
        {
            sb.AppendLine();
            sb.AppendLine(category);
            AppendRows(sb, types
                .Where(byType.ContainsKey)
                .Select(type => byType[type])
                .Where(sub => !sub.Hidden)
                .Select(sub => (sub.Name, HelpCommandName.FormatReferences(
                    sub is IShortDescription sd ? sd.ShortDescription : sub.Description ?? "", sub, commandName)))
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

    internal static string RenderCommand(Command command, string commandName = "winapp")
    {
        var path = HelpCommandName.GetPath(command, commandName);
        var sb = new StringBuilder();
        var summary = command is IShortDescription sd ? sd.ShortDescription.TrimEnd('.') + "." : "";
        AppendDescription(sb, $"{path} - ", HelpCommandName.FormatReferences(summary, command, commandName));
        // Descriptions open with the summary so they stand alone in --cli-schema; don't repeat it here.
        var details = command.Description ?? "";
        if (summary.Length > 0 && details.StartsWith(summary, StringComparison.Ordinal))
        {
            details = details[summary.Length..].TrimStart();
        }
        if (!string.IsNullOrWhiteSpace(details))
        {
            AppendDescription(sb, "", HelpCommandName.FormatReferences(details, command, commandName));
        }

        sb.AppendLine();
        sb.AppendLine("Usage: " + HelpCommandName.FormatReferences(
            (command as IHelpExamples)?.Usage ?? BuildUsage(command, path), command, commandName));

        if (command is IHelpExamples { Examples.Count: > 0 } examples)
        {
            sb.AppendLine();
            sb.AppendLine("Examples:");
            foreach (var example in examples.Examples)
            {
                sb.Append("  ").AppendLine(HelpCommandName.FormatReferences(example, command, commandName));
            }
        }

        var arguments = command.Arguments.Where(a => !a.Hidden).ToList();
        if (arguments.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Arguments:");
            AppendRows(sb, arguments.Select(a => ($"<{a.Name}>",
                HelpCommandName.FormatReferences(a.Description ?? "", command, commandName))).ToList());
        }

        var options = command.Options.Where(IsCommandOption)
            .OrderBy(o => o == WinAppRootCommand.JsonOption ? 1 : 0)
            .ToList();
        if (options.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Options:");
            AppendRows(sb, options.Select(o => (FormatOption(o),
                HelpCommandName.FormatReferences(o.Description ?? "", command, commandName))).ToList());
        }

        sb.AppendLine();
        sb.AppendLine(HelpCommandName.FormatReferences(GlobalOptionsLine, command, commandName));
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
        var lines = text.Replace("\r\n", "\n").Split('\n').Select((line, index) => index == 0 ? prefix + line : line);
        foreach (var line in lines)
        {
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
}

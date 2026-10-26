// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Completions;
using System.CommandLine.Help;
using System.Text;
using System.Text.RegularExpressions;
using WinApp.Cli.Commands;

namespace WinApp.Cli.Helpers;

/// <summary>
/// The <c>--help</c> page of every command except the root. Agents read help redirected, so the
/// plain layout has no styling, puts examples first, and collapses the options every command
/// shares into one line. In a color terminal the same layout gets colors, dims everything after
/// the first sentence of long text, and adds blank lines between multi-line entries.
/// </summary>
internal static partial class CompactHelpRenderer
{
    internal const int Width = 88;
    internal const string GlobalOptionsLine = "Global options: -v, -q, --on <target>, --cli-schema   (see winapp --help)";
    internal const string LocalGlobalOptionsLine = "Global options: -v, -q, --cli-schema   (see winapp --help)";

    private const int MinNameColumn = 18;
    private const int MaxNameColumn = 24;
    private const int MaxGroupedNameColumn = 32;
    private const int MaxExampleCommand = 38;

    public static bool AppliesTo(Command command) => command is not RootCommand;

    /// <summary>Renders a command's help. <paramref name="styled"/> adds terminal colors and spacing.</summary>
    public static string Render(Command command, bool styled = false) => new Writer(styled).Render(command);

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex PlaceholderRegex();

    // A period followed by a new sentence; "e.g. win-x64" is not a sentence end.
    [GeneratedRegex(@"\.\s+(?=[A-Z'(])")]
    private static partial Regex SentenceEndRegex();

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

    private sealed class Writer(bool styled)
    {
        private const string Reset = "\u001b[0m";
        private readonly StringBuilder _sb = new();

        public string Render(Command command)
        {
            if (command is ICompactHelpGroup group)
            {
                RenderGroup(command, group);
            }
            else
            {
                RenderCommand(command);
            }
            return _sb.ToString();
        }

        private void RenderGroup(Command command, ICompactHelpGroup help)
        {
            var path = GetCommandPath(command);
            AppendTitle(path, command.Description ?? "");
            AppendParagraph(help.GoldenPath);
            _sb.AppendLine();
            _sb.AppendLine($"{Heading("Usage:")} {Code($"{path} <command> [options]")}    {Muted($"Details: {path} <command> --help")}");

            var byType = command.Subcommands.ToDictionary(c => c.GetType());
            foreach (var (category, types) in help.Categories)
            {
                AppendSection(category, types
                    .Where(byType.ContainsKey)
                    .Select(type => byType[type])
                    .Where(sub => !sub.Hidden)
                    .Select(sub => (sub.Name, ShortDescription(sub)))
                    .ToList());
            }

            AppendSection("Options:", help.GroupOptions);
        }

        private void RenderCommand(Command command)
        {
            var path = GetCommandPath(command);
            var summary = command is IShortDescription sd ? sd.ShortDescription.TrimEnd('.') + "." : "";
            // Descriptions open with the summary so they stand alone in --cli-schema; don't repeat it here.
            var details = command.Description ?? "";
            if (summary.Length > 0 && details.StartsWith(summary, StringComparison.Ordinal))
            {
                details = details[summary.Length..].TrimStart();
            }
            if (summary.Length == 0)
            {
                (summary, details) = (details, "");
            }
            AppendTitle(path, summary);
            if (!string.IsNullOrWhiteSpace(details))
            {
                if (styled)
                {
                    _sb.AppendLine();
                }
                AppendParagraph(details, dimAfterFirstSentence: true);
            }

            _sb.AppendLine();
            // ui and devtools pages keep the exact layout agents were built against; other pages add the
            // details System.CommandLine's default help showed (defaults, allowed values, optional arguments).
            var detailed = !InAgentGroup(command);
            var usage = (command as IHelpExamples)?.Usage ?? (command as ICompactHelpCommand)?.Usage ?? BuildUsage(command, path, detailed);
            _sb.AppendLine($"{Heading("Usage:")} {Code(usage)}");

            if (command is ICompactHelpCommand { Examples.Count: > 0 } described)
            {
                AppendExamples(described.Examples);
            }
            else if (command is IHelpExamples { Examples.Count: > 0 } examples)
            {
                AppendExamples(examples.Examples.Select(e => (e, "")).ToList());
            }

            AppendSection("Arguments:", command.Arguments.Where(a => !a.Hidden)
                .Select(a => ($"<{a.Name}>", a.Description ?? "")).ToList());

            AppendSection("Commands:", command.Subcommands.Where(c => !c.Hidden)
                .Select(c => (c.Name, ShortDescription(c))).ToList());

            var options = command.Options.Where(IsCommandOption)
                .OrderBy(o => o == WinAppRootCommand.JsonOption ? 1 : 0)
                .ToList();
            if (command is ICompactHelpCommand { OptionGroups.Count: > 0 } grouped)
            {
                var groups = grouped.OptionGroups
                    .Select(g => (g.Heading, Options: g.Options.Where(options.Contains).ToList()))
                    .Where(g => g.Options.Count > 0)
                    .ToList();
                // One column for every group, so descriptions line up down the whole page.
                var column = Math.Clamp(groups.SelectMany(g => g.Options).Select(o => FormatOption(o, detailed).Length + 2)
                    .Where(length => length <= MaxGroupedNameColumn).DefaultIfEmpty(MinNameColumn).Max(),
                    MinNameColumn, MaxGroupedNameColumn);
                foreach (var (heading, groupOptions) in groups)
                {
                    AppendSection(heading, groupOptions.Select(o => (FormatOption(o, detailed), DescribeOption(o, detailed))).ToList(), column);
                }
                options = options.Except(groups.SelectMany(g => g.Options)).ToList();
            }
            AppendSection("Options:", options.Select(o => (FormatOption(o, detailed), DescribeOption(o, detailed))).ToList());

            _sb.AppendLine();
            // Saved-comment reads always run locally and reject --on.
            _sb.AppendLine(Muted(command is DevToolsCommentsListCommand or DevToolsCommentsGetCommand || !AcceptsOn(command)
                ? LocalGlobalOptionsLine : GlobalOptionsLine));
        }

        private void AppendTitle(string path, string summary)
        {
            var lines = Wrap($"{path} - {summary}", Width);
            if (lines.Count == 0)
            {
                return;
            }
            _sb.AppendLine(styled ? $"\u001b[1;97m{path}{Reset}{lines[0][path.Length..]}" : lines[0]);
            foreach (var line in lines.Skip(1))
            {
                _sb.AppendLine(line);
            }
        }

        /// <summary>Wraps prose; indented lines (a group's golden path block) are preformatted.</summary>
        private void AppendParagraph(string text, bool dimAfterFirstSentence = false)
        {
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.Length == 0)
                {
                    _sb.AppendLine();
                }
                else if (char.IsWhiteSpace(line[0]))
                {
                    _sb.AppendLine(line);
                }
                else
                {
                    var wrapped = Wrap(line, Width);
                    foreach (var styledLine in dimAfterFirstSentence ? DimAfterFirstSentence(line, wrapped) : wrapped)
                    {
                        _sb.AppendLine(styledLine);
                    }
                }
            }
        }

        private void AppendExamples(IReadOnlyList<(string Command, string Description)> examples)
        {
            _sb.AppendLine();
            _sb.AppendLine(Heading("Examples:"));
            var column = examples.Select(e => e.Command.Length).Where(length => length <= MaxExampleCommand).DefaultIfEmpty(0).Max() + 2;
            foreach (var (command, description) in examples)
            {
                if (description.Length == 0)
                {
                    _sb.Append("  ").AppendLine(Code(command));
                }
                else if (command.Length > MaxExampleCommand)
                {
                    _sb.Append("  ").AppendLine(Code(command));
                    _sb.Append(' ', 2 + column).AppendLine(description);
                }
                else
                {
                    _sb.Append("  ").Append(Code(command)).Append(' ', column - command.Length).AppendLine(description);
                }
            }
        }

        private void AppendSection(string heading, IReadOnlyList<(string Name, string Description)> rows, int? fixedColumn = null)
        {
            if (rows.Count == 0)
            {
                return;
            }
            _sb.AppendLine();
            _sb.AppendLine(Heading(heading));

            var column = fixedColumn ?? Math.Clamp(rows.Max(r => r.Name.Length) + 2, MinNameColumn, MaxNameColumn);
            var entries = rows.Select(r => (r.Name, Lines: DimAfterFirstSentence(r.Description, Wrap(r.Description, Width - 2 - column)))).ToList();
            // In a terminal, a blank line between entries keeps multi-line descriptions apart.
            var spaced = styled && entries.Any(e => e.Lines.Count > 1 || e.Name.Length + 2 > column);
            for (var i = 0; i < entries.Count; i++)
            {
                if (spaced && i > 0)
                {
                    _sb.AppendLine();
                }
                var (name, lines) = entries[i];
                if (name.Length + 2 > column || lines.Count == 0)
                {
                    // A label wider than the column (for example '--allow-system-keys') gets its own line.
                    _sb.Append("  ").AppendLine(Code(name));
                    foreach (var line in lines)
                    {
                        _sb.Append(' ', 2 + column).AppendLine(line);
                    }
                    continue;
                }

                for (var j = 0; j < lines.Count; j++)
                {
                    _sb.Append("  ").Append(j == 0 ? Code(name) + new string(' ', column - name.Length) : new string(' ', column)).AppendLine(lines[j]);
                }
            }
        }

        /// <summary>Keeps the first sentence at normal brightness and dims the rest, so long text skims easily.</summary>
        private List<string> DimAfterFirstSentence(string text, List<string> lines)
        {
            if (!styled)
            {
                return lines;
            }
            var match = SentenceEndRegex().Match(text);
            var cut = match.Success ? match.Index + 1 : text.Length;
            var position = 0;
            var result = new List<string>(lines.Count);
            foreach (var line in lines)
            {
                var start = position;
                var end = position + line.Length;
                result.Add(end <= cut ? line
                    : start >= cut ? Muted(line)
                    : line[..(cut - start)] + Muted(line[(cut - start)..]));
                position = end + 1;
            }
            return result;
        }

        private string Heading(string text) => styled ? $"\u001b[1;38;2;99;141;255m{text}{Reset}" : text;

        private string Code(string text) => !styled ? text
            : "\u001b[97m" + PlaceholderRegex().Replace(text, m => $"\u001b[2m{m.Value}\u001b[22;97m") + Reset;

        private string Muted(string text) => styled && text.Length > 0 ? $"\u001b[90m{text}{Reset}" : text;
    }

    private static string ShortDescription(Command command) =>
        command is IShortDescription sd ? sd.ShortDescription : command.Description ?? "";

    private static bool AcceptsOn(Command command) =>
        command.Options.Contains(ExecutionTargetSelection.OnOption)
        || command.Parents.OfType<Command>().Any(AcceptsOn);

    private static bool IsCommandOption(Option option) =>
        !option.Hidden
        && option is not HelpOption
        && option != WinAppRootCommand.VerboseOption
        && option != WinAppRootCommand.QuietOption
        && !ExecutionTargetSelection.IsSelectorOption(option);

    private static bool InAgentGroup(Command command) =>
        command is ICompactHelpGroup || command.Parents.OfType<Command>().Any(InAgentGroup);

    private static string BuildUsage(Command command, string path, bool detailed)
    {
        var parts = new List<string> { path };
        var arguments = command.Arguments.Where(a => !a.Hidden);
        parts.AddRange(detailed
            ? arguments.Select(a =>
            {
                var name = a.Arity.MaximumNumberOfValues > 1 ? $"<{a.Name}>..." : $"<{a.Name}>";
                return a.Arity.MinimumNumberOfValues == 0 ? $"[{name}]" : name;
            })
            : arguments.Select(a => $"<{a.Name}>"));
        if (command.Subcommands.Any(c => !c.Hidden))
        {
            parts.Add(command.Action is null ? "<command>" : "[<command>]");
        }
        var hasApp = command.Options.Contains(SharedUiOptions.AppOption);
        var hasWindow = command.Options.Contains(SharedUiOptions.WindowOption);
        if (hasApp && hasWindow)
        {
            parts.Add(command.Parents.OfType<ICompactHelpGroup>().FirstOrDefault()?.TargetUsage ?? "(-a <app> | -w <hwnd>)");
        }
        else if (hasApp)
        {
            parts.Add("[-a <app>]");
        }

        parts.Add("[options]");
        if (detailed && !command.TreatUnmatchedTokensAsErrors)
        {
            // Commands such as 'store' and 'tool' forward everything else to another program.
            parts.Add("[[--] <additional arguments>...]");
        }
        return string.Join(" ", parts);
    }

    private static string FormatOption(Option option, bool detailed)
    {
        var names = option.Aliases.Where(a => a != option.Name).OrderBy(a => a.Length).Append(option.Name);
        var label = string.Join(", ", names);
        var takesValue = option.Arity.MaximumNumberOfValues > 0 && option.ValueType != typeof(bool);
        var placeholder = option.HelpName ?? (detailed ? AllowedValues(option) : null) ?? option.Name.TrimStart('-');
        return takesValue ? $"{label} <{placeholder}>" : label;
    }

    /// <summary>The fixed set of values an option accepts (an enum or AcceptOnlyFromAmong), if short.</summary>
    private static string? AllowedValues(Option option)
    {
        var values = option.GetCompletions(CompletionContext.Empty).Select(c => c.Label).ToList();
        return values.Count is > 0 and <= 6 ? string.Join("|", values) : null;
    }

    /// <summary>The option's description, plus whether it's required and its default value (unless the
    /// description already names it).</summary>
    private static string DescribeOption(Option option, bool detailed)
    {
        var description = option.Description ?? "";
        if (!detailed)
        {
            return description;
        }
        if (option.Required)
        {
            description = $"{description} (REQUIRED)".TrimStart();
        }
        if (!option.HasDefaultValue)
        {
            return description;
        }
        var value = option.GetDefaultValue() switch
        {
            null or bool or "" => null,
            System.Collections.IEnumerable and not string => null,
            var v => v.ToString(),
        };
        return value is null
            || (description.Contains("default", StringComparison.OrdinalIgnoreCase) && description.Contains(value, StringComparison.OrdinalIgnoreCase))
            ? description
            : $"{description} [default: {value}]".TrimStart();
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

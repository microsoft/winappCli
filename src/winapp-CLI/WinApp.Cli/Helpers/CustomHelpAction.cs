// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using Spectre.Console;
using Spectre.Console.Rendering;
using WinApp.Cli.Commands;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Custom help action that renders the root command help screen with
/// categorized command tables and short descriptions, styled with Spectre.Console.
/// The <c>ui</c> and <c>devtools</c> groups and their commands get plain-text help from <see cref="CompactHelpRenderer"/>.
/// Other commands use the default System.CommandLine help rendering.
/// </summary>
internal sealed class CustomHelpAction : SynchronousCommandLineAction
{
    private readonly (string Category, Type[] CommandTypes)[] _categories;
    private readonly Command _targetCommand;
    private readonly IAnsiConsole _ansiConsole;

    public override bool Terminating => true;

    /// <summary>
    /// Gets the flat set of all command types covered by the help categories.
    /// </summary>
    internal IReadOnlyCollection<Type> CategorizedCommandTypes =>
        _categories.SelectMany(c => c.CommandTypes).ToArray();

    /// <summary>
    /// Creates a new custom help action with the specified command categories.
    /// </summary>
    /// <param name="targetCommand">The command this custom help applies to (e.g. root command).</param>
    /// <param name="ansiConsole">The Spectre.Console instance to render help output to.</param>
    /// <param name="categories">Ordered list of (category name, command types) tuples.</param>
    public CustomHelpAction(Command targetCommand, IAnsiConsole ansiConsole, params (string Category, Type[] CommandTypes)[] categories)
    {
        _targetCommand = targetCommand;
        _ansiConsole = ansiConsole;
        _categories = categories;
    }

    public override int Invoke(ParseResult parseResult)
    {
        var command = parseResult.CommandResult.Command;

        if (CompactHelpRenderer.AppliesTo(command))
        {
            parseResult.InvocationConfiguration.Output.Write(CompactHelpRenderer.Render(command, styled: BannerHelper.UseEmoji));
            return 0;
        }

        // Only use custom rendering for the target command; fall back to default for others
        if (command != _targetCommand)
        {
            var defaultHelp = new HelpAction();
            return defaultHelp.Invoke(parseResult);
        }

        var useColor = BannerHelper.UseEmoji;
        var commandPath = GetCommandPath(command);

        // Description
        if (!string.IsNullOrEmpty(command.Description))
        {
            _ansiConsole.WriteLine();
            WriteIndented(new Markup($"[dim]{Markup.Escape(command.Description)}[/]"));
        }

        // Usage
        _ansiConsole.WriteLine();
        _ansiConsole.MarkupLine($" Usage: [white]{commandPath} <command>[/] [dim][[options]][/]");
        _ansiConsole.MarkupLine($"        [white]{commandPath} --help[/]");
        _ansiConsole.WriteLine();
        _ansiConsole.MarkupLine($" Use '[white]{commandPath} <command> --help[/]' to get detailed help for any command.");
        if (command is RootCommand)
        {
            WriteQuickStart();
            _ansiConsole.WriteLine();
            _ansiConsole.MarkupLine(" All commands:");
        }

        // Build a lookup from command type -> Command object
        var subcommandByType = new Dictionary<Type, Command>();
        foreach (var sub in command.Subcommands)
        {
            subcommandByType[sub.GetType()] = sub;
        }

        // Render each category
        foreach (var (category, commandTypes) in _categories)
        {
            _ansiConsole.WriteLine();

            var grid = new Grid();
            grid.AddColumn(new GridColumn().PadLeft(1).PadRight(2).NoWrap());
            grid.AddColumn(new GridColumn());

            foreach (var type in commandTypes)
            {
                if (subcommandByType.TryGetValue(type, out var sub))
                {
                    var desc = sub is IShortDescription sd ? sd.ShortDescription : sub.Description ?? "";
                    grid.AddRow(
                        $"[white]{Markup.Escape(sub.Name)}[/]",
                        Markup.Escape(desc)
                    );
                }
            }

            var panel = new Panel(grid);
            panel.Border = BoxBorder.Rounded;
            panel.BorderStyle = new Style(Color.Grey35);
            panel.Header = new PanelHeader(
                useColor ? $"[rgb(99,141,255)]{Markup.Escape(category)}[/]" : $"[bold]{Markup.Escape(category)}[/]");
            panel.Padding = new Padding(1, 0, 1, 0);

            WriteIndented(panel);
        }

        // Global options
        _ansiConsole.WriteLine();

        var optGrid = new Grid();
        optGrid.AddColumn(new GridColumn().PadLeft(1).PadRight(2).NoWrap());
        optGrid.AddColumn(new GridColumn());

        foreach (var option in command.Options)
        {
            if (option.Hidden)
            {
                continue;
            }

            // Build alias string: include the canonical name first, then additional aliases
            var allAliases = new List<string> { option.Name };
            foreach (var alias in option.Aliases)
            {
                if (alias != option.Name)
                {
                    allAliases.Add(alias);
                }
            }
            var aliasText = string.Join(", ", allAliases);
            var desc = option.Description ?? "";

            optGrid.AddRow(
                $"[white]{Markup.Escape(aliasText)}[/]",
                Markup.Escape(desc)
            );
        }

        var optPanel = new Panel(optGrid);
        optPanel.Border = BoxBorder.Rounded;
        optPanel.BorderStyle = new Style(Color.Grey35);
        optPanel.Header = new PanelHeader(
            useColor ? "[rgb(99,141,255)]Options[/]" : "[bold]Options[/]");
        optPanel.Padding = new Padding(1, 0, 1, 0);

        WriteIndented(optPanel);
        _ansiConsole.WriteLine();

        return 0;
    }

    /// <summary>
    /// Common workflows shown at the top of root help. A step without a description is
    /// too long to share the column and is printed on its own.
    /// </summary>
    internal static readonly (string Title, (string Command, string Description)[] Steps)[] QuickStart =
    [
        ("Build a WinUI app",
        [
            ("winapp new", "create a project, then cd into its folder"),
            ("winapp run .", "build and launch it"),
            ("winapp find-ui \"<control>\"", "get working XAML for a control"),
            ("winapp find-api \"<type>\"", "look up Windows APIs you can call"),
            ("winapp devtools --help", "inspect and live-edit the running XAML"),
            ("winapp run . --profile <folder>", "record layout and rendering timings"),
            ("winapp perf analyze <folder>", "see what was slow"),
        ]),
        ("Add Windows features to an existing app (.NET, C++, Rust, Flutter...)",
        [
            ("winapp init", "add a manifest and Windows SDKs"),
            ("winapp run <project|folder>", "launch with package identity"),
        ]),
        ("Package for install",
        [
            ("winapp cert generate", "make a test signing certificate"),
            ("winapp cert install devcert.pfx", "trust it on this machine (admin)"),
            ("winapp package <.csproj|folder> --cert devcert.pfx", ""),
        ]),
        ("Automate any app's UI",
        [
            ("winapp ui --help", "click, type, and read any app's UI"),
        ]),
    ];

    private void WriteQuickStart()
    {
        var width = QuickStart
            .SelectMany(g => g.Steps)
            .Where(s => s.Description.Length > 0)
            .Max(s => s.Command.Length) + 2;

        _ansiConsole.WriteLine();
        _ansiConsole.MarkupLine(" Quick start:");
        foreach (var (title, steps) in QuickStart)
        {
            _ansiConsole.WriteLine();
            _ansiConsole.MarkupLine($"   {Markup.Escape(title)}");
            foreach (var (stepCommand, description) in steps)
            {
                var commandText = $"[white]{Markup.Escape(stepCommand)}[/]";
                _ansiConsole.MarkupLine(description.Length == 0
                    ? $"     {commandText}"
                    : $"     {commandText}{new string(' ', width - stepCommand.Length)}{Markup.Escape(description)}");
            }
        }
    }

    /// <summary>
    /// Renders a Spectre renderable with a 1-space left indent.
    /// </summary>
    private void WriteIndented(IRenderable renderable)
    {
        _ansiConsole.Write(new Padder(renderable, new Padding(1, 0, 0, 0)));
    }

    private static string GetCommandPath(Command command)
    {
        // For root command, use "winapp"; for subcommands, build the path
        var parts = new List<string>();
        var current = command;
        while (current != null)
        {
            if (current is RootCommand)
            {
                parts.Add("winapp");
            }
            else
            {
                parts.Add(current.Name);
            }
            current = current.Parents.OfType<Command>().FirstOrDefault();
        }

        parts.Reverse();
        return string.Join(" ", parts);
    }
}

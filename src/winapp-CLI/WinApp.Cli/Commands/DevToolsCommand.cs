// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

internal class DevToolsCommand : Command, IShortDescription, ITargetAwareCommand, ICompactHelpGroup
{
    public string ShortDescription => "Inspect and change a running WinUI app's XAML live (no source edits)";

    internal const string GoldenPath =
        "Inspect and change a running WinUI 3 app's XAML live. Changes are not written to source.\n" +
        "\n" +
        "  winapp run . --devtools --detach                     launch your app with DevTools\n" +
        "  winapp devtools attach --pid <pid>                   or add DevTools to a running app\n" +
        "  winapp devtools list                                 apps with DevTools attached\n" +
        "  winapp devtools search <text> -a <app>               find elements by text or x:Name\n" +
        "  winapp devtools get-property <selector> -a <app>     read its live properties\n" +
        "  winapp devtools set-property <selector> <prop> <value> -a <app>\n" +
        "                                                       change a property live\n" +
        "  winapp devtools comments list                        comments left in the app\n" +
        "\n" +
        "  -a <app>     Process name, window title, or PID. Optional when only one app has\n" +
        "               DevTools attached.\n" +
        "  <selector>   The name in brackets from search or inspect, an x:Name, or a handle.\n" +
        "\n" +
        "Use 'winapp ui' to click, type, and read values in any app. Use 'winapp devtools' for a\n" +
        "WinUI app's XAML tree, properties, bindings, and live edits.";

    /// <summary>Command-list categories for <c>winapp devtools --help</c>, in display order.</summary>
    internal static readonly (string Category, Type[] CommandTypes)[] HelpCategories =
    [
        ("Discover", [typeof(DevToolsListCommand), typeof(DevToolsAttachCommand), typeof(DevToolsInspectCommand), typeof(DevToolsSearchCommand)]),
        ("Read", [typeof(DevToolsGetPropertyCommand), typeof(DevToolsGetLayoutCommand), typeof(DevToolsGetSourceCommand)]),
        ("Change live", [typeof(DevToolsSetPropertyCommand)]),
        ("Bindings", [typeof(DevToolsDiagnoseBindingCommand)]),
        ("Comments", [typeof(DevToolsCommentsCommand)]),
        ("Protocol", [typeof(DevToolsCallCommand)]),
    ];

    public DevToolsCommand(
        DevToolsCommentsCommand commentsCommand,
        DevToolsListCommand listCommand,
        DevToolsAttachCommand attachCommand,
        DevToolsInspectCommand inspectCommand,
        DevToolsSearchCommand searchCommand,
        DevToolsGetPropertyCommand getPropertyCommand,
        DevToolsGetLayoutCommand getLayoutCommand,
        DevToolsGetSourceCommand getSourceCommand,
        DevToolsDiagnoseBindingCommand diagnoseBindingCommand,
        DevToolsSetPropertyCommand setPropertyCommand,
        DevToolsCallCommand callCommand)
        : base("devtools", GoldenPath)
    {
        // Lets an unknown command ('winapp devtools set-text --json') report its error as JSON.
        Options.Add(WinAppRootCommand.JsonOption);

        Subcommands.Add(inspectCommand);
        Subcommands.Add(searchCommand);
        Subcommands.Add(getPropertyCommand);
        Subcommands.Add(getLayoutCommand);
        Subcommands.Add(getSourceCommand);
        Subcommands.Add(diagnoseBindingCommand);
        Subcommands.Add(setPropertyCommand);
        Subcommands.Add(callCommand);
        Subcommands.Add(commentsCommand);
        Subcommands.Add(listCommand);
        Subcommands.Add(attachCommand);
    }

    IReadOnlyList<(string Category, Type[] CommandTypes)> ICompactHelpGroup.Categories => HelpCategories;

    IReadOnlyList<(string Name, string Description)> ICompactHelpGroup.GroupOptions { get; } =
    [
        ("--on <target>", "Run on 'sandbox' (Windows Sandbox) or 'local' (default)"),
        ("-h, --help", "Show help"),
    ];

    string ICompactHelpGroup.TargetUsage => "[-a <app> | -w <hwnd>]";

    IReadOnlyDictionary<string, string> ICompactHelpGroup.Synonyms { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["tree"] = "inspect",
        ["dump"] = "inspect",
        ["find"] = "search",
        ["query"] = "search",
        ["get"] = "get-property",
        ["read"] = "get-property",
        ["set"] = "set-property",
        ["edit"] = "set-property",
        ["change"] = "set-property",
        ["set-text"] = "set-property",
        ["binding"] = "diagnose-binding",
        ["source"] = "get-source",
        ["layout"] = "get-layout",
        ["apps"] = "list",
    };

    IReadOnlyList<string> ICompactHelpGroup.CommonCommands { get; } =
        ["list", "attach", "inspect", "search", "get-property", "set-property", "diagnose-binding", "comments"];
}

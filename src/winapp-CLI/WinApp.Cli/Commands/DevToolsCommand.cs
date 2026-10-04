// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

internal class DevToolsCommand : Command, IShortDescription, ITargetAwareCommand, ICompactHelpGroup
{
    public string ShortDescription => "Inspect and change a running WinUI app's XAML live (no source edits)";

    /// <summary>Group-level examples, shown after the workflow; each must parse (see DevToolsHelpTests).</summary>
    internal static readonly string[] Examples =
    [
        "winapp devtools get-property <selector> Text",
        "winapp devtools get-property <selector> --all",
        "winapp devtools search --of-type TextBlock --all --fields Text",
        "winapp devtools search --of-type Button --with IsEnabled==False",
        "winapp devtools set-property --of-type TextBlock --with Text==OK -p Text --value Done",
        "winapp devtools inspect <selector> --depth 3",
        "winapp devtools diagnose-binding <selector> Text",
    ];

    internal static readonly string GoldenPath =
        "Inspect and change a running WinUI 3 app's XAML live. Changes are not written to source.\n" +
        "\n" +
        "  winapp run . --devtools --detach                         launch your app with DevTools\n" +
        "  winapp devtools attach --pid <pid>                       or attach to a running app\n" +
        "  winapp devtools list                                     apps with DevTools attached\n" +
        "  winapp devtools search <text>                            find by text or x:Name\n" +
        "  winapp devtools get-property <selector>                  read its live properties\n" +
        "  winapp devtools set-property <selector> <prop> <value>   change a property live\n" +
        "  winapp devtools comments list                            comments left in the app\n" +
        "\n" +
        "Examples:\n" +
        string.Concat(Examples.Select(example => "  " + example + "\n")) +
        "\n" +
        "  -a <app>     Add to a command to choose the app: process name, window title, or PID.\n" +
        "               Needed only when more than one app has DevTools attached.\n" +
        "  <selector>   The name in brackets, an x:Name or AutomationId, or a handle.\n" +
        "\n" +
        "Use 'winapp ui' to click, type, and read values in any app. Use 'winapp devtools' for a\n" +
        "WinUI app's XAML tree, properties, bindings, and live edits. Both take an AutomationId.";

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

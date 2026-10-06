// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

/// <summary><c>winapp devtools resources</c>: where a running app's styles and resources come from.</summary>
internal class DevToolsResourcesCommand : Command, IShortDescription, ICompactHelpGroup
{
    public string ShortDescription => "Trace, list, and live-edit styles and resources";

    public DevToolsResourcesCommand(
        DevToolsResourcesExplainCommand explainCommand,
        DevToolsResourcesListCommand listCommand,
        DevToolsResourcesSetCommand setCommand,
        DevToolsResourcesResetCommand resetCommand,
        DevToolsResourcesCopyStyleCommand copyStyleCommand)
        : base("resources", "Find which Style setter, resource key, and theme produced a live property value, " +
            "and the XAML file and line to edit. List app resources, try new values live, or copy a Style into your XAML to edit it.\n" +
            "\n" +
            "  winapp devtools resources explain <selector> Background\n" +
            "  winapp devtools resources set <key> \"#FF0067C0\"")
    {
        Options.Add(WinAppRootCommand.JsonOption);

        Subcommands.Add(explainCommand);
        Subcommands.Add(listCommand);
        Subcommands.Add(setCommand);
        Subcommands.Add(resetCommand);
        Subcommands.Add(copyStyleCommand);
    }

    IReadOnlyList<(string Category, Type[] CommandTypes)> ICompactHelpGroup.Categories { get; } =
    [
        ("Read", [typeof(DevToolsResourcesExplainCommand), typeof(DevToolsResourcesListCommand)]),
        ("Change", [typeof(DevToolsResourcesSetCommand), typeof(DevToolsResourcesResetCommand), typeof(DevToolsResourcesCopyStyleCommand)]),
    ];

    IReadOnlyList<(string Name, string Description)> ICompactHelpGroup.GroupOptions { get; } = [("-h, --help", "Show help")];

    string ICompactHelpGroup.TargetUsage => "[-a <app>]";

    IReadOnlyDictionary<string, string> ICompactHelpGroup.Synonyms { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["why"] = "explain",
        ["trace"] = "explain",
        ["ls"] = "list",
        ["override"] = "set",
        ["replace"] = "set",
        ["restore"] = "reset",
        ["undo"] = "reset",
        ["edit-style"] = "copy-style",
        ["edit-copy"] = "copy-style",
    };

    IReadOnlyList<string> ICompactHelpGroup.CommonCommands { get; } = ["explain", "set"];
}

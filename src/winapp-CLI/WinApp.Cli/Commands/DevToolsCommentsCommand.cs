// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

// Headless JSON contract for the UI-comments loop; .winapp/ui-comments.json is the source of truth.
internal class DevToolsCommentsCommand : Command, IShortDescription, ICompactHelpGroup
{
    public string ShortDescription => "Manage source-anchored UI review comments";

    public DevToolsCommentsCommand(
        DevToolsCommentsAddCommand addCommand,
        DevToolsCommentsListCommand listCommand,
        DevToolsCommentsGetCommand getCommand,
        DevToolsCommentsUpdateCommand updateCommand,
        DevToolsCommentsDeleteCommand deleteCommand)
        : base("comments", "Read and resolve the review comments people leave on a running app's elements. " +
            "Comments are saved in .winapp/ui-comments.json with the XAML file and line they point at.\n" +
            "\n" +
            "  winapp devtools comments list\n" +
            "  winapp devtools comments get <id>\n" +
            "  winapp devtools comments update <id> --status resolved --note \"<text>\"")
    {
        Options.Add(WinAppRootCommand.JsonOption);

        Subcommands.Add(addCommand);
        Subcommands.Add(listCommand);
        Subcommands.Add(getCommand);
        Subcommands.Add(updateCommand);
        Subcommands.Add(deleteCommand);
    }

    IReadOnlyList<(string Category, Type[] CommandTypes)> ICompactHelpGroup.Categories { get; } =
    [
        ("Read", [typeof(DevToolsCommentsListCommand), typeof(DevToolsCommentsGetCommand)]),
        ("Write", [typeof(DevToolsCommentsUpdateCommand), typeof(DevToolsCommentsAddCommand), typeof(DevToolsCommentsDeleteCommand)]),
    ];

    IReadOnlyList<(string Name, string Description)> ICompactHelpGroup.GroupOptions { get; } = [("-h, --help", "Show help")];

    string ICompactHelpGroup.TargetUsage => "[-a <app>]";

    IReadOnlyDictionary<string, string> ICompactHelpGroup.Synonyms { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["show"] = "get",
        ["resolve"] = "update",
        ["remove"] = "delete",
        ["new"] = "add",
    };

    IReadOnlyList<string> ICompactHelpGroup.CommonCommands { get; } = ["list", "get", "update", "add", "delete"];
}

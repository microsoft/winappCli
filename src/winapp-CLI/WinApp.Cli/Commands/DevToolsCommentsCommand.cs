// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

// Headless JSON contract for the UI-comments loop; .winapp/ui-comments.json is the source of truth.
internal class DevToolsCommentsCommand : Command, IShortDescription
{
    public string ShortDescription => "Manage source-anchored UI review comments";

    public DevToolsCommentsCommand(
        DevToolsCommentsAddCommand addCommand,
        DevToolsCommentsListCommand listCommand,
        DevToolsCommentsGetCommand getCommand,
        DevToolsCommentsUpdateCommand updateCommand,
        DevToolsCommentsDeleteCommand deleteCommand)
        : base("comments", "Manage saved UI review comments anchored to app source.")
    {
        Subcommands.Add(addCommand);
        Subcommands.Add(listCommand);
        Subcommands.Add(getCommand);
        Subcommands.Add(updateCommand);
        Subcommands.Add(deleteCommand);
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

internal class DevToolsCommand : Command, IShortDescription, ITargetAwareCommand
{
    public string ShortDescription => "Inspect and modify a running app's XAML tree (and UI comments)";

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
        : base("devtools", "Inspect live WinUI elements, edit properties, and review UI comments.")
    {
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
}

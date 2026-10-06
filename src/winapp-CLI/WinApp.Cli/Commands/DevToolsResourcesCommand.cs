// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

/// <summary><c>winapp devtools resources</c>: where a running app's styles and resources come from.</summary>
internal class DevToolsResourcesCommand : Command, IShortDescription, ICompactHelpGroup
{
    public string ShortDescription => "Trace a value to its Style setter and resource definition";

    public DevToolsResourcesCommand(DevToolsResourcesExplainCommand explainCommand)
        : base("resources", "Find which Style setter, resource key, and theme produced a live property value, " +
            "and the XAML file and line to edit.\n" +
            "\n" +
            "  winapp devtools resources explain <selector> Background")
    {
        Options.Add(WinAppRootCommand.JsonOption);

        Subcommands.Add(explainCommand);
    }

    IReadOnlyList<(string Category, Type[] CommandTypes)> ICompactHelpGroup.Categories { get; } =
    [
        ("Read", [typeof(DevToolsResourcesExplainCommand)]),
    ];

    IReadOnlyList<(string Name, string Description)> ICompactHelpGroup.GroupOptions { get; } = [("-h, --help", "Show help")];

    string ICompactHelpGroup.TargetUsage => "[-a <app>]";

    IReadOnlyDictionary<string, string> ICompactHelpGroup.Synonyms { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["why"] = "explain",
        ["trace"] = "explain",
    };

    IReadOnlyList<string> ICompactHelpGroup.CommonCommands { get; } = ["explain"];
}

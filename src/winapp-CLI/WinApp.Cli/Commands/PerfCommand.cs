// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

internal sealed class PerfCommand : Command, IShortDescription
{
    public string ShortDescription => "Record, repeat, compare, and open app performance evidence";

    public PerfCommand(
        PerfRecordCommand recordCommand,
        PerfScenarioCommand scenarioCommand,
        PerfCompareCommand compareCommand,
        PerfOpenCommand openCommand)
        : base("perf", "Record, repeat, compare, and open target-scoped performance evidence for Windows apps")
    {
        Subcommands.Add(recordCommand);
        Subcommands.Add(scenarioCommand);
        Subcommands.Add(compareCommand);
        Subcommands.Add(openCommand);
    }
}

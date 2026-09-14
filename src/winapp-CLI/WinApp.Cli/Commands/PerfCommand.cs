// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

internal sealed class PerfCommand : Command, IShortDescription
{
    public string ShortDescription => "Record and open target-scoped app performance evidence";

    public PerfCommand(PerfRecordCommand recordCommand, PerfOpenCommand openCommand)
        : base("perf", "Record and open target-scoped performance evidence for Windows apps")
    {
        Subcommands.Add(recordCommand);
        Subcommands.Add(openCommand);
    }
}

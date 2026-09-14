// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

internal sealed class PerfCommand : Command, IShortDescription
{
    public string ShortDescription => "Record target-scoped app startup and performance evidence";

    public PerfCommand(PerfRecordCommand recordCommand)
        : base("perf", "Record target-scoped performance evidence for Windows apps")
    {
        Subcommands.Add(recordCommand);
    }
}

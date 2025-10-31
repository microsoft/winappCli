// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Diagnostics.Telemetry;
using Microsoft.Diagnostics.Telemetry.Internal;
using System.CommandLine.Parsing;
using System.Diagnostics.Tracing;

namespace WinApp.Cli.Telemetry.Events;

[EventData]
internal class CommandCompletedEvent : EventBase
{
    internal CommandCompletedEvent(CommandResult commandResult, DateTime finishedTime, int exitCode)
    {
        CommandName = commandResult.Command.GetType().FullName!;
        FinishedTime = finishedTime;
        ExitCode = exitCode;
    }

    public string CommandName { get; private set; }

    public DateTime FinishedTime { get; private set; }

    public int ExitCode { get; }

    public override PartA_PrivTags PartA_PrivTags => PrivTags.ProductAndServiceUsage;

    public override void ReplaceSensitiveStrings(Func<string, string> replaceSensitiveStrings)
    {
        CommandName = replaceSensitiveStrings(CommandName);
    }

    public static void Log(CommandResult commandResult, int exitCode)
    {
        TelemetryFactory.Get<ITelemetry>().Log("CommandCompleted_Event", LogLevel.Critical, new CommandCompletedEvent(commandResult, DateTime.Now, exitCode));
    }
}

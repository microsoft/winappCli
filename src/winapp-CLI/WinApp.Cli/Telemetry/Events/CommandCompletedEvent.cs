// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Diagnostics.Telemetry;
using Microsoft.Diagnostics.Telemetry.Internal;
using System.CommandLine.Parsing;
using System.Diagnostics.Tracing;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Telemetry.Events;

[EventData]
internal class CommandCompletedEvent : EventBase
{
    internal CommandCompletedEvent(CommandResult commandResult, DateTime finishedTime, int exitCode)
    {
        CommandName = commandResult.Command.GetType().FullName!;
        FinishedTime = finishedTime;
        ExitCode = exitCode;

        // Cooperative desktop turns (issue #764). Populated only for `winapp ui` commands, which are
        // the only ones that coordinate. Everything here is a coarse bucket or a fixed enum name —
        // never an owner id or hash, a PID, a process/app/window/selector string, a queue entry, a
        // command argument, or any part of the state file (spec §16).
        if (UiCoordinationTelemetryScope.Current is { } coordination)
        {
            UiIdentitySource = coordination.IdentitySource.ToString();
            UiTurnMode = coordination.Mode.ToString();
            UiTurnAction = coordination.TurnAction.ToString();
            UiCoordinationOutcome = coordination.Outcome.ToString();
            UiWaitBucket = coordination.WaitBucket;
            UiQueueDepthBucket = coordination.QueueDepthBucket;
            UiTurnAgeBucket = coordination.TurnAgeBucket;
        }
    }

    public string CommandName { get; private set; }

    public DateTime FinishedTime { get; private set; }

    public int ExitCode { get; }

    /// <summary>How the owner was resolved: <c>Workflow</c> (from <c>WINAPP_UI_WORKFLOW_ID</c>) or <c>Anonymous</c>.</summary>
    public string? UiIdentitySource { get; }

    /// <summary>Coordination mode: <c>Observe</c>, <c>TurnShared</c>, or <c>DesktopExclusive</c>.</summary>
    public string? UiTurnMode { get; }

    /// <summary>How the turn was obtained: new, continuation, queued, handoff-after-idle, or detached.</summary>
    public string? UiTurnAction { get; }

    /// <summary>Completed, cancelled, coordination failure, or corruption recovery.</summary>
    public string? UiCoordinationOutcome { get; }

    /// <summary>Coarse bucket for time spent waiting for the desktop, never an exact duration.</summary>
    public string? UiWaitBucket { get; }

    /// <summary>Coarse bucket for how many commands were queued, never the queue contents.</summary>
    public string? UiQueueDepthBucket { get; }

    /// <summary>Coarse bucket for how long the turn had been held.</summary>
    public string? UiTurnAgeBucket { get; }

    public override PartA_PrivTags PartA_PrivTags => PrivTags.ProductAndServiceUsage;

    public override void ReplaceSensitiveStrings(Func<string, string> replaceSensitiveStrings)
    {
        CommandName = replaceSensitiveStrings(CommandName);

        // The coordination fields are fixed enum names and bucket labels produced by this build, so
        // they cannot contain user paths or identifiers and need no scrubbing.
    }

    public static void Log(CommandResult commandResult, int exitCode)
    {
        TelemetryFactory.Get<ITelemetry>().Log("CommandCompleted_Event", LogLevel.Critical, new CommandCompletedEvent(commandResult, DateTime.Now, exitCode), TelemetryCorrelation.CurrentId);
    }
}

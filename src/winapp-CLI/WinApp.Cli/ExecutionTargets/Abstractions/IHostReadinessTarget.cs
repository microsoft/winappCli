// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.ExecutionTargets.Abstractions;

/// <summary>Values reported in <see cref="TargetHostCheck.Status"/>.</summary>
internal static class TargetHostCheckStatus
{
    /// <summary>The prerequisite was observed and is met.</summary>
    public const string Passed = "passed";

    /// <summary>The prerequisite was observed and is not met.</summary>
    public const string Failed = "failed";

    /// <summary>The prerequisite could not be observed, or did not need to be.</summary>
    public const string NotChecked = "notChecked";
}

/// <summary>One host prerequisite for using a target.</summary>
internal sealed record TargetHostCheck
{
    /// <summary>Stable check name, such as <c>osVersion</c>.</summary>
    public required string Name { get; init; }

    /// <summary>One of the <see cref="TargetHostCheckStatus"/> values.</summary>
    public required string Status { get; init; }

    /// <summary>What was observed.</summary>
    public string? Detail { get; init; }

    /// <summary>What the user can do about a failed check.</summary>
    public string? Fix { get; init; }

    /// <summary>A command the user may choose to run for a failed check. Never run by winapp.</summary>
    public ExecutionTargetNextCommand? NextCommand { get; init; }
}

/// <summary>Whether this host meets a target's prerequisites, check by check.</summary>
internal sealed record TargetHostReadiness
{
    /// <summary>True when the host can use the target now.</summary>
    public required bool Ready { get; init; }

    /// <summary>Each prerequisite, in the order a user should address them.</summary>
    public required TargetHostCheck[] Checks { get; init; }
}

/// <summary>A backend that can report host prerequisites without changing anything.</summary>
/// <remarks>
/// Read-only: no installs, no elevation, no restarts, and no target instance is started.
/// </remarks>
internal interface IHostReadinessTarget
{
    /// <summary>Reports host prerequisites, or null when they cannot be described.</summary>
    Task<TargetHostReadiness?> DescribeHostAsync(CancellationToken cancellationToken);
}

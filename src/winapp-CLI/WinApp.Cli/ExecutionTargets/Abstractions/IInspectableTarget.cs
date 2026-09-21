// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.ExecutionTargets.Abstractions;

/// <summary>What an inspect-only attach found, without changing anything to find it.</summary>
/// <param name="Running">True when the target winapp manages is running right now.</param>
/// <param name="Epoch">
/// Which generation is running. <see cref="ExecutionTargetEpoch.None"/> when nothing is.
/// </param>
/// <param name="Connection">
/// A live command channel to the running agent, or null when there is no agent to talk to — the
/// instance was never bootstrapped, or its agent is not answering. Null is an ordinary outcome
/// here, not a failure: an inspect-only attach reports what it found instead of repairing it.
/// </param>
internal sealed record TargetAttachment(
    bool Running,
    ExecutionTargetEpoch Epoch,
    TargetConnection? Connection)
{
    /// <summary>Nothing winapp manages is running.</summary>
    public static TargetAttachment NotRunning { get; } =
        new(false, ExecutionTargetEpoch.None, null);
}

/// <summary>
/// A backend that can be examined without being created, started, or repaired.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="IExecutionTargetBackend.EnsureConnectedAsync"/>, which is
/// allowed to do whatever it takes to hand back a working channel — create the instance, reconnect
/// its client, replace the agent. A command that only reports state must not do any of that: a
/// caller running <c>winapp target snapshot</c> to find out whether a target is up would otherwise
/// bring one up by asking, and the answer would be about the target the question created.
/// <para>
/// Optional on purpose. A backend that cannot answer "is it running?" without starting something
/// simply does not implement this, and the commands that need it say so rather than quietly
/// falling back to a preparing path.
/// </para>
/// </remarks>
internal interface IInspectableTarget
{
    /// <summary>
    /// Reports whether the managed target is running, and attaches to its agent if one answers.
    /// </summary>
    /// <remarks>
    /// Creates nothing, starts nothing, connects no client, and replaces no agent. The caller owns
    /// <see cref="TargetAttachment.Connection"/> and must dispose its transport.
    /// </remarks>
    Task<TargetAttachment> TryAttachAsync(CancellationToken cancellationToken);
}

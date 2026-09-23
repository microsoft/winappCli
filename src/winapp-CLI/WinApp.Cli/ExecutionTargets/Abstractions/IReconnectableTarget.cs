// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.ExecutionTargets.Abstractions;

/// <summary>A backend that can authenticate an existing connection before preparing a target.</summary>
/// <remarks>
/// Called under the connection lock. Cached ownership is only a hint: success requires the peer
/// to authenticate the recorded target and generation. Null means preparation must reconcile and
/// repair normally, not that the target is stopped. Unlike <see cref="IInspectableTarget"/>, this
/// does not report authoritative lifecycle state. It creates, starts, and repairs nothing.
/// </remarks>
internal interface IReconnectableTarget
{
    /// <summary>Attempts a bounded reconnect, returning an authenticated channel or null.</summary>
    /// <remarks>The caller owns the returned transport. Busy and ownership errors must propagate.</remarks>
    Task<TargetConnection?> TryReconnectAsync(CancellationToken cancellationToken);
}

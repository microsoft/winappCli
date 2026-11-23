// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Abstraction over the pre-injection foreground check for testability. The real implementation
/// performs live <c>GetForegroundWindow</c> P/Invoke (see <see cref="ForegroundGuard"/>); fakes can
/// force the proceed/abort decision so coordinate-gesture verbs (click, hover, drag, scroll --wheel,
/// send-keys --via send-input) can be unit-tested without a live, unlocked desktop.
/// </summary>
public interface IForegroundGuard
{
    /// <summary>
    /// Classifies whether the target window is in the foreground before an OS-wide input injection.
    /// See <see cref="ForegroundGuard.CheckForeground(long)"/> for the production semantics.
    /// </summary>
    ForegroundCheck CheckForeground(long targetHwnd);

    /// <summary>
    /// Checks foreground ownership and optionally retains the observations from a rejected check.
    /// Implementations without snapshot support still perform their original check and return null;
    /// callers must not substitute a later foreground query for missing observations.
    /// </summary>
    ForegroundCheck CheckForeground(long targetHwnd, out ForegroundCheckSnapshot? snapshot)
    {
        snapshot = null;
        return CheckForeground(targetHwnd);
    }

    /// <summary>
    /// Returns <see langword="true"/> when running in a remote (RDP / Terminal Services) session, where
    /// synthetic touch/pen injection may report success without actually reaching the target so callers
    /// attach a delivery-uncertainty warning. See <see cref="ForegroundGuard.IsRemoteSession"/>.
    /// </summary>
    bool IsRemoteSession();
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Production implementation — delegates to the <see cref="ForegroundGuard"/> static helper so the
/// well-tested foreground-classification logic stays the single source of truth.
/// </summary>
internal class RealForegroundGuard : IForegroundGuard
{
    public ForegroundCheck CheckForeground(long targetHwnd)
        => ForegroundGuard.CheckForeground(targetHwnd);

    public bool IsRemoteSession() => ForegroundGuard.IsRemoteSession();
}

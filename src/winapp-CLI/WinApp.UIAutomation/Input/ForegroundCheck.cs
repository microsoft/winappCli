// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>The outcome of the pre-injection foreground check.</summary>
public enum ForegroundCheck
{
    /// <summary>No target to verify, or the target already holds the foreground — inject.</summary>
    Proceed,

    /// <summary>No foreground window exists at all — the session is locked / on a secure desktop.</summary>
    NoInteractiveDesktop,

    /// <summary>A different window holds the foreground — refuse to avoid injecting into it.</summary>
    ForegroundNotTarget,
}

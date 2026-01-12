// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Specifies the SDK installation mode for workspace setup
/// </summary>
internal enum SdkInstallMode
{
    /// <summary>
    /// Install stable SDK packages (Windows SDK, WinAppSDK)
    /// </summary>
    Stable,

    /// <summary>
    /// Install preview SDK packages
    /// </summary>
    Preview,

    /// <summary>
    /// Install experimental SDK packages
    /// </summary>
    Experimental,

    /// <summary>
    /// Skip SDK installation entirely
    /// </summary>
    None
}

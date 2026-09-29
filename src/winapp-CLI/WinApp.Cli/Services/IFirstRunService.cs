// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

internal enum FirstRunNotice
{
    /// <summary>The marker exists; nothing was shown.</summary>
    None,

    /// <summary>First run: the full welcome banner and telemetry notice were shown and the marker was saved.</summary>
    Shown,

    /// <summary>
    /// The marker could not be saved (the global winapp directory is not writable), so a short
    /// telemetry notice was written to stderr. It repeats on every run.
    /// </summary>
    Unsaved,
}

internal interface IFirstRunService
{
    public FirstRunNotice CheckAndDisplayFirstRunNotice();
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Performance;

internal static class WprRecordingSafety
{
    internal const int MaximumDurationSeconds = 300;
    internal const long MinimumFreeBytes = 1024L * 1024 * 1024;

    public static string? Validate(int durationSeconds, long availableBytes)
    {
        if (durationSeconds < 1 || durationSeconds > MaximumDurationSeconds)
        {
            return $"--with-wpr requires --duration-sec between 1 and {MaximumDurationSeconds} to bound file-mode trace growth.";
        }

        return availableBytes < MinimumFreeBytes
            ? "--with-wpr requires at least 1 GiB free on the output volume."
            : null;
    }
}

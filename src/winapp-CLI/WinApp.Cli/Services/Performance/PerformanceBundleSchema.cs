// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Performance;

internal static class PerformanceBundleSchema
{
    public const string CurrentVersion = "0.8";

    public static bool IsSupported(string? version) =>
        version is "0.1" or "0.2" or "0.3" or "0.5" or "0.6" or "0.7" or CurrentVersion;
}

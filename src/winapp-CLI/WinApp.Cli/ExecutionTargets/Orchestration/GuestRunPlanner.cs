// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>Constructs the locked registration phase; launching is a separate guest command.</summary>
internal static class GuestRunPlanner
{
    public static List<string> BuildRegistrationArguments(
        string payloadPath, string layoutPath, bool clean, bool json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(layoutPath);
        var arguments = new List<string>
        {
            "run", payloadPath, "--managed-appx-directory", layoutPath, "--no-launch",
        };
        if (clean)
        {
            arguments.Add("--clean");
        }
        if (json)
        {
            arguments.Add("--json");
        }
        return arguments;
    }

    public static void EnsureSupportedForUnpackaged(bool debugOutput)
    {
        if (debugOutput)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.Unsupported,
                "--debug-output is not available for an unpackaged app running in Windows Sandbox.",
                userAction: "Run it without --debug-output, or make the app packaged so guest winapp can debug it.",
                example: "winapp run . --on sandbox");
        }
    }
}

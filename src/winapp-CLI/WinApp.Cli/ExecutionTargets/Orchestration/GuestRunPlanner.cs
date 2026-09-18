// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>Constructs the locked registration phase; launching is a separate guest command.</summary>
internal static class GuestRunPlanner
{
    internal static void EnsureUniqueIdentitySupported(ExecutionTargetCapabilities capabilities, bool uniqueIdentity)
    {
        if (uniqueIdentity &&
            capabilities.DevelopmentIdentityVersion != ExecutionTargetCapabilities.CurrentDevelopmentIdentityVersion)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.AgentIncompatible,
                "The running Windows Sandbox agent does not support --unique-identity ownership checks. No package was registered or removed.",
                userAction: "Save any guest work and close Windows Sandbox, then retry with this winapp version to start a compatible agent.");
        }
    }

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

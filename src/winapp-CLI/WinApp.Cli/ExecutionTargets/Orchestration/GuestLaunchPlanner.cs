// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>Launch-only options. Package mutation options belong to the locked host workflow.</summary>
internal sealed record GuestLaunchOptions(
    bool WithAlias = false,
    bool DebugOutput = false,
    bool Detach = false,
    bool Json = false,
    string? AppArguments = null,
    bool AliasIsExplicit = true,
    bool Symbols = false);

/// <summary>Builds a verify-and-launch request that cannot register or remove packages.</summary>
internal static class GuestLaunchPlanner
{
    public const string Verb = "guest-launch";

    public static List<string> BuildLaunchArguments(
        string packageName,
        string publisher,
        string applicationId,
        string expectedLayoutPath,
        string payloadPath,
        string targetSelector,
        GuestLaunchOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(publisher);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedLayoutPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSelector);
        ArgumentNullException.ThrowIfNull(options);

        var arguments = new List<string>
        {
            Verb,
            "--package-name", packageName,
            "--publisher", publisher,
            "--application-id", applicationId,
            "--expected-layout", expectedLayoutPath,
            "--payload", payloadPath,
            "--target-selector", targetSelector,
        };
        if (options.WithAlias)
        {
            arguments.Add(options.AliasIsExplicit ? "--with-alias" : "--prefer-alias");
        }
        if (options.DebugOutput)
        {
            arguments.Add("--debug-output");
        }
        if (options.Symbols)
        {
            arguments.Add("--symbols");
        }
        if (options.Detach)
        {
            arguments.Add("--detach");
        }
        if (options.Json)
        {
            arguments.Add("--json");
        }
        if (!string.IsNullOrEmpty(options.AppArguments))
        {
            arguments.Add("--args");
            arguments.Add(options.AppArguments);
        }
        return arguments;
    }
}

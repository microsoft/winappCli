// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

/// <summary>Checks host prerequisites without enabling features, installing clients, or restarting.</summary>
internal interface IWindowsSandboxSetup
{
    Task<WindowsSandboxHostFacts> InspectAsync(CancellationToken cancellationToken);

    /// <summary>Returns a ready host or an actionable prerequisite error. Does not perform setup.</summary>
    Task<WindowsSandboxHostFacts> EnsureReadyAsync(CancellationToken cancellationToken);
}

internal sealed class WindowsSandboxSetup(IWindowsSandboxHostProbe probe) : IWindowsSandboxSetup
{
    internal const string EnableFeatureCommand =
        "dism.exe /Online /Enable-Feature /FeatureName:" + WindowsSandboxReadiness.FeatureName + " /All /NoRestart";

    internal Func<bool> SupportsSandboxCli { get; set; } =
        () => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100);

    public Task<WindowsSandboxHostFacts> InspectAsync(CancellationToken cancellationToken) =>
        probe.ProbeAsync(cancellationToken);

    public async Task<WindowsSandboxHostFacts> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var facts = await probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (facts.State == WindowsSandboxSetupState.Ready)
        {
            return facts;
        }

        if (!facts.IsWindows || !SupportsSandboxCli())
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.Unsupported,
                "Windows Sandbox execution requires Windows 11 24H2 or newer.",
                userAction: "Use a supported Windows edition with hardware virtualization enabled.");
        }

        if (facts.State == WindowsSandboxSetupState.RestartRequired)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.SetupRequiresRestart,
                "Windows reports a pending restart, and Windows Sandbox is not ready.",
                userAction: "Save your work and restart Windows when you are ready, then retry. " +
                    "If Sandbox is still unavailable, enable Windows Sandbox in 'Turn Windows features on or off'.",
                context: Details(facts));
        }

        if (facts.State == WindowsSandboxSetupState.FeaturePayloadMissing)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.SetupRequired,
                "Windows Sandbox must be enabled before this command can run.",
                userAction: "Enable Windows Sandbox in 'Turn Windows features on or off', or run the suggested " +
                    "command from an administrator terminal. Save your work and restart Windows when ready, then retry. " +
                    "If you have already enabled the feature, restart before enabling it again.",
                nextCommand: new ExecutionTargetNextCommand
                {
                    Command = EnableFeatureCommand,
                    Advisory = true,
                },
                context: Details(facts));
        }

        throw ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.SetupIncomplete,
            "Windows Sandbox feature files are present, but the Sandbox client is not ready.",
            userAction: "Open Windows Sandbox from the Start menu and finish any installation or update it requests. " +
                "If Windows asks for a restart, save your work and restart when ready. Then retry this command.",
            context: Details(facts));
    }

    private static Dictionary<string, string> Details(WindowsSandboxHostFacts facts)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["setupState"] = facts.State.ToString(),
            ["featurePayloadPresent"] = facts.FeaturePayloadPresent ? "true" : "false",
            ["packageRegistered"] = facts.PackageRegistered ? "true" : "false",
            ["aliasPresent"] = facts.AliasPresent ? "true" : "false",
            ["restartPending"] = facts.RestartPending?.ToString().ToLowerInvariant() ?? "unknown",
            ["wsbVersion"] = facts.Version ?? "none",
        };
        if (facts.PackageStatus is { } status) { details["packageStatus"] = status; }
        if (facts.Detail is { } detail) { details["detail"] = detail; }
        return details;
    }
}

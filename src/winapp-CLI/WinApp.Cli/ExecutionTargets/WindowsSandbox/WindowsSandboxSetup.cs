// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

/// <summary>Checks host prerequisites without enabling features, installing clients, or restarting.</summary>
internal interface IWindowsSandboxSetup
{
    Task<WindowsSandboxHostFacts> InspectAsync(CancellationToken cancellationToken);

    /// <summary>Reports each host prerequisite as passed, failed, or not checked. Does not perform setup.</summary>
    Task<TargetHostReadiness> DescribeHostAsync(CancellationToken cancellationToken);

    /// <summary>Returns a ready host or an actionable prerequisite error. Does not perform setup.</summary>
    Task<WindowsSandboxHostFacts> EnsureReadyAsync(CancellationToken cancellationToken);
}

internal sealed class WindowsSandboxSetup(IWindowsSandboxHostProbe probe) : IWindowsSandboxSetup
{
    internal const string EnableFeatureCommand =
        "dism.exe /Online /Enable-Feature /FeatureName:" + WindowsSandboxReadiness.FeatureName + " /All /NoRestart";

    private const string OsFix = "Use a supported Windows edition with hardware virtualization enabled.";

    private const string RestartFix = "Save your work and restart Windows when you are ready, then retry. " +
        "If Sandbox is still unavailable, enable Windows Sandbox in 'Turn Windows features on or off'.";

    private const string EnableFeatureFix =
        "Enable Windows Sandbox in 'Turn Windows features on or off', or run the suggested " +
        "command from an administrator terminal. Save your work and restart Windows when ready, then retry. " +
        "If you have already enabled the feature, restart before enabling it again.";

    private const string ClientFix =
        "Open Windows Sandbox from the Start menu and finish any installation or update it requests. " +
        "If Windows asks for a restart, save your work and restart when ready. Then retry this command.";

    internal Func<bool> SupportsSandboxCli { get; set; } =
        () => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100);

    /// <summary>OS version shown in the readiness report; a seam so tests do not depend on the host.</summary>
    internal Func<string> OsVersion { get; set; } = () => Environment.OSVersion.Version.ToString();

    public Task<WindowsSandboxHostFacts> InspectAsync(CancellationToken cancellationToken) =>
        probe.ProbeAsync(cancellationToken);

    public async Task<TargetHostReadiness> DescribeHostAsync(CancellationToken cancellationToken)
    {
        var facts = await probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        return Describe(facts);
    }

    /// <summary>Maps observed facts to user-facing checks, in the order a user should address them.</summary>
    internal TargetHostReadiness Describe(WindowsSandboxHostFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var ready = facts.State == WindowsSandboxSetupState.Ready;

        if (!facts.IsWindows)
        {
            const string notWindows = "This host is not Windows.";
            return new TargetHostReadiness
            {
                Ready = false,
                Checks =
                [
                    Failed("osVersion", "This host is not Windows. Windows Sandbox requires Windows 11 24H2 or newer.", OsFix),
                    NotChecked("sandboxFeature", notWindows),
                    NotChecked("sandboxClient", notWindows),
                    NotChecked("wsb", notWindows),
                    NotChecked("restartPending", notWindows),
                ],
            };
        }

        var osVersion = OsVersion();
        var osSupported = ready || SupportsSandboxCli();
        var osCheck = osSupported
            ? Passed("osVersion", $"Windows {osVersion}")
            : Failed("osVersion", $"Windows {osVersion}. Windows Sandbox execution requires Windows 11 24H2 (build 26100) or newer.", OsFix);

        // Match run: on an unsupported build, Sandbox setup advice cannot help, so only the OS fix is shown.
        if (!osSupported)
        {
            const string waitingOnOs = "Update Windows first.";
            return new TargetHostReadiness
            {
                Ready = false,
                Checks =
                [
                    osCheck,
                    NotChecked("sandboxFeature", waitingOnOs),
                    NotChecked("sandboxClient", waitingOnOs),
                    NotChecked("wsb", waitingOnOs),
                    NotChecked("restartPending", waitingOnOs),
                ],
            };
        }

        TargetHostCheck featureCheck;
        if (facts.FeaturePayloadPresent)
        {
            featureCheck = Passed("sandboxFeature", $"Windows Sandbox feature ({WindowsSandboxReadiness.FeatureName}) is enabled.");
        }
        else if (ready)
        {
            // A healthy client that answers proves the feature works even when its files are not visible.
            featureCheck = Passed("sandboxFeature", "Windows Sandbox is available.");
        }
        else
        {
            featureCheck = Failed(
                "sandboxFeature",
                $"Windows Sandbox feature ({WindowsSandboxReadiness.FeatureName}) is not enabled.",
                EnableFeatureFix,
                new ExecutionTargetNextCommand { Command = EnableFeatureCommand, Advisory = true });
        }

        // The client and wsb.exe come from the feature, so their failures mean nothing until it is enabled.
        const string waitingOnFeature = "Enable the Windows Sandbox feature first.";

        TargetHostCheck clientCheck;
        if (facts.PackageRegistered)
        {
            clientCheck = facts.IsPackageHealthy
                ? Passed("sandboxClient", $"Windows Sandbox client is installed for this user (status: {facts.PackageStatus ?? "unknown"}).")
                : Failed("sandboxClient", $"Windows Sandbox client is installed but not usable (status: {facts.PackageStatus}).", ClientFix);
        }
        else if (facts.Detail is { } detail)
        {
            clientCheck = NotChecked("sandboxClient", $"Could not check the Windows Sandbox client: {detail}.");
        }
        else if (!facts.FeaturePayloadPresent)
        {
            clientCheck = NotChecked("sandboxClient", waitingOnFeature);
        }
        else
        {
            clientCheck = Failed("sandboxClient", "Windows Sandbox client is not installed for this user.", ClientFix);
        }

        TargetHostCheck wsbCheck;
        if (!string.IsNullOrWhiteSpace(facts.Version))
        {
            wsbCheck = Passed("wsb", $"wsb.exe answered with version {facts.Version}.");
        }
        else if (!facts.FeaturePayloadPresent)
        {
            wsbCheck = NotChecked("wsb", waitingOnFeature);
        }
        else
        {
            wsbCheck = Failed(
                "wsb",
                facts.AliasPresent ? "wsb.exe did not answer 'wsb --version'." : "wsb.exe was not found.",
                ClientFix);
        }

        return new TargetHostReadiness
        {
            Ready = ready,
            Checks = [osCheck, featureCheck, clientCheck, wsbCheck, RestartCheck(facts.RestartPending, ready)],
        };
    }

    private static TargetHostCheck RestartCheck(bool? restartPending, bool ready)
    {
        if (restartPending == false)
        {
            return Passed("restartPending", "No Windows restart is pending.");
        }

        if (ready)
        {
            return NotChecked("restartPending", "Windows Sandbox is ready.");
        }

        return restartPending == true
            ? Failed("restartPending", "Windows reports a pending restart.", RestartFix)
            : NotChecked("restartPending", "Could not read Windows restart state.");
    }

    private static TargetHostCheck Passed(string name, string detail) =>
        new() { Name = name, Status = TargetHostCheckStatus.Passed, Detail = detail };

    private static TargetHostCheck NotChecked(string name, string detail) =>
        new() { Name = name, Status = TargetHostCheckStatus.NotChecked, Detail = detail };

    private static TargetHostCheck Failed(
        string name, string detail, string fix, ExecutionTargetNextCommand? nextCommand = null) =>
        new() { Name = name, Status = TargetHostCheckStatus.Failed, Detail = detail, Fix = fix, NextCommand = nextCommand };

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
                userAction: OsFix);
        }

        if (facts.State == WindowsSandboxSetupState.RestartRequired)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.SetupRequiresRestart,
                "Windows reports a pending restart, and Windows Sandbox is not ready.",
                userAction: RestartFix,
                context: Details(facts));
        }

        if (facts.State == WindowsSandboxSetupState.FeaturePayloadMissing)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.SetupRequired,
                "Windows Sandbox must be enabled before this command can run.",
                userAction: EnableFeatureFix,
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
            userAction: ClientFix,
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

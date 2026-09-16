// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

[TestClass]
public class WindowsSandboxSetupTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ReadyHost_ReturnsWithoutSetupOrRestartChecks()
    {
        var facts = Facts() with { Version = "0.8.107.0", RestartPending = true };
        var probe = new FixedProbe(facts);
        var setup = new WindowsSandboxSetup(probe) { SupportsSandboxCli = () => false };
        Assert.AreSame(facts, await setup.EnsureReadyAsync(TestContext.CancellationToken));
        Assert.AreEqual(1, probe.Calls);
    }

    [TestMethod]
    public async Task MissingFeature_ReportsAdvisoryEnableCommandWithoutPerformingSetup()
    {
        var probe = new FixedProbe(Facts());
        var setup = new WindowsSandboxSetup(probe) { SupportsSandboxCli = () => true };
        var error = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            setup.EnsureReadyAsync(TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.SetupRequired, error.Error.Code);
        Assert.AreEqual(
            "dism.exe /Online /Enable-Feature /FeatureName:Containers-DisposableClientVM /All /NoRestart",
            error.Error.NextCommand!.Command);
        Assert.IsTrue(error.Error.NextCommand.Advisory);
        StringAssert.Contains(error.Error.UserAction!, "administrator terminal");
        StringAssert.Contains(error.Error.UserAction!, "restart Windows when ready");
        Assert.AreEqual(1, probe.Calls, "A missing prerequisite must fail promptly, not poll an installer.");
        using var json = JsonDocument.Parse(ExecutionTargetErrorSerializer.Serialize(error.Error));
        Assert.IsTrue(json.RootElement.GetProperty("error").GetProperty("nextCommand").GetProperty("advisory").GetBoolean());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PendingRestart_ReportsObservedStateWithoutAnEnableOrRestartCommand(bool payloadPresent)
    {
        var probe = new FixedProbe(Facts() with { FeaturePayloadPresent = payloadPresent, RestartPending = true });
        var error = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            new WindowsSandboxSetup(probe) { SupportsSandboxCli = () => true }
                .EnsureReadyAsync(TestContext.CancellationToken));
        Assert.AreEqual(ExecutionTargetErrorCodes.SetupRequiresRestart, error.Error.Code);
        Assert.IsNull(error.Error.NextCommand, "Restart timing belongs to the user, not an automatically offered command.");
        Assert.AreEqual("true", error.Error.Context!["restartPending"]);
        StringAssert.Contains(error.Error.UserAction!, "Save your work");
        StringAssert.Contains(error.Error.UserAction!, "when you are ready");
        Assert.AreEqual(1, probe.Calls);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(false)]
    public async Task UnknownOrAbsentRestart_DoesNotClaimWindowsRequiresOne(bool? pending)
    {
        var error = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            new WindowsSandboxSetup(new FixedProbe(Facts() with { RestartPending = pending }))
                { SupportsSandboxCli = () => true }
                .EnsureReadyAsync(TestContext.CancellationToken));
        Assert.AreEqual(ExecutionTargetErrorCodes.SetupRequired, error.Error.Code);
    }

    [TestMethod]
    [DataRow(null, null)]
    [DataRow("Servicing", "0.8.107.0")]
    [DataRow("PackageOffline", null)]
    public async Task FeaturePresent_ClientNotReady_ReturnsManualInitializationGuidance(string? packageStatus, string? version)
    {
        var facts = Facts() with { FeaturePayloadPresent = true, PackageStatus = packageStatus, Version = version };
        var probe = new FixedProbe(facts);
        var error = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            new WindowsSandboxSetup(probe) { SupportsSandboxCli = () => true }
                .EnsureReadyAsync(TestContext.CancellationToken));
        Assert.AreEqual(ExecutionTargetErrorCodes.SetupIncomplete, error.Error.Code);
        StringAssert.Contains(error.Error.UserAction!, "Start menu");
        Assert.IsNull(error.Error.NextCommand);
        Assert.IsFalse(error.Error.UserAction.Contains("Enable-Feature", StringComparison.Ordinal));
        Assert.AreEqual(1, probe.Calls);
    }

    [TestMethod]
    public async Task RetryAfterUserSetup_RechecksReadiness()
    {
        var probe = new FixedProbe(Facts());
        var setup = new WindowsSandboxSetup(probe) { SupportsSandboxCli = () => true };
        await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() => setup.EnsureReadyAsync(TestContext.CancellationToken));
        probe.Facts = Facts() with { FeaturePayloadPresent = true, Version = "0.8.107.0" };
        Assert.AreEqual(WindowsSandboxSetupState.Ready, (await setup.EnsureReadyAsync(TestContext.CancellationToken)).State);
        Assert.AreEqual(2, probe.Calls);
    }

    [TestMethod]
    public async Task Inspect_ReportsMissingFeatureWithoutSetupOrError()
    {
        var probe = new FixedProbe(Facts());
        var observed = await new WindowsSandboxSetup(probe).InspectAsync(TestContext.CancellationToken);
        Assert.AreEqual(WindowsSandboxSetupState.FeaturePayloadMissing, observed.State);
        Assert.AreEqual(1, probe.Calls);
    }

    [TestMethod]
    public async Task Cancellation_DoesNotProbe()
    {
        var probe = new FixedProbe(Facts());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new WindowsSandboxSetup(probe).EnsureReadyAsync(cancelled.Token));
        Assert.AreEqual(0, probe.Calls);
    }

    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public async Task UnsupportedHost_ReportsRequirements(bool windows, bool supportedVersion)
    {
        var setup = new WindowsSandboxSetup(new FixedProbe(Facts() with { IsWindows = windows }))
        {
            SupportsSandboxCli = () => supportedVersion,
        };
        var error = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() => setup.EnsureReadyAsync(TestContext.CancellationToken));
        Assert.AreEqual(ExecutionTargetErrorCodes.Unsupported, error.Error.Code);
        Assert.IsNull(error.Error.NextCommand);
    }

    [TestMethod]
    public async Task Probe_NotReady_RecordsPendingRestartWithoutLaunchingProvider()
    {
        var runner = new RecordingProcessRunner();
        var probe = new WindowsSandboxHostProbe(runner)
        {
            IsWindows = () => true,
            FileExists = _ => false,
            QueryPackage = () => new(false, null),
            ResolveAlias = _ => null,
            QueryRestartPending = () => true,
        };
        var facts = await probe.ProbeAsync(TestContext.CancellationToken);
        Assert.AreEqual(WindowsSandboxSetupState.RestartRequired, facts.State);
        Assert.IsEmpty(runner.Requests);
    }

    [TestMethod]
    public async Task Probe_Ready_DoesNotQueryIrrelevantMachineRestartState()
    {
        var runner = new RecordingProcessRunner { Result = new(0, "0.8.107.0", "") };
        var probe = new WindowsSandboxHostProbe(runner)
        {
            IsWindows = () => true,
            FileExists = _ => true,
            QueryPackage = () => new(true, "Ok"),
            ResolveAlias = _ => @"C:\test\wsb.exe",
            QueryRestartPending = () => throw new InvalidOperationException("Not needed for a working Sandbox"),
        };
        var facts = await probe.ProbeAsync(TestContext.CancellationToken);
        Assert.AreEqual(WindowsSandboxSetupState.Ready, facts.State);
        Assert.AreEqual("--version", runner.Requests.Single().Arguments.Single());
    }

    private static WindowsSandboxHostFacts Facts() => new()
    {
        IsWindows = true,
        FeaturePayloadPresent = false,
        PackageRegistered = false,
        AliasPresent = false,
    };

    private sealed class FixedProbe(WindowsSandboxHostFacts facts) : IWindowsSandboxHostProbe
    {
        public WindowsSandboxHostFacts Facts { get; set; } = facts;
        public int Calls { get; private set; }
        public Task<WindowsSandboxHostFacts> ProbeAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Facts);
        }
    }
}

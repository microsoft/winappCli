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
    public async Task DescribeHost_MissingFeature_FailsFeatureWithAdvisoryCommandAndDefersClientChecks()
    {
        var probe = new FixedProbe(Facts() with { RestartPending = false });
        var setup = new WindowsSandboxSetup(probe) { SupportsSandboxCli = () => true, OsVersion = () => "10.0.26100.0" };

        var host = await setup.DescribeHostAsync(TestContext.CancellationToken);

        Assert.IsFalse(host.Ready);
        CollectionAssert.AreEqual(
            AllCheckNames,
            host.Checks.Select(c => c.Name).ToArray());
        Assert.AreEqual(TargetHostCheckStatus.Passed, Check(host, "osVersion").Status);
        var feature = Check(host, "sandboxFeature");
        Assert.AreEqual(TargetHostCheckStatus.Failed, feature.Status);
        Assert.AreEqual(WindowsSandboxSetup.EnableFeatureCommand, feature.NextCommand!.Command);
        Assert.IsTrue(feature.NextCommand.Advisory);
        Assert.AreEqual(TargetHostCheckStatus.NotChecked, Check(host, "sandboxClient").Status);
        Assert.AreEqual(TargetHostCheckStatus.NotChecked, Check(host, "wsb").Status);
        Assert.AreEqual(TargetHostCheckStatus.Passed, Check(host, "restartPending").Status);
        Assert.AreEqual(1, probe.Calls);

        var error = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            setup.EnsureReadyAsync(TestContext.CancellationToken));
        Assert.AreEqual(error.Error.UserAction, feature.Fix, "Snapshot and run-time errors must give the same advice.");
    }

    [TestMethod]
    public async Task DescribeHost_FeatureEnabledButClientMissing_FailsClientAndWsb()
    {
        var setup = new WindowsSandboxSetup(new FixedProbe(Facts() with { FeaturePayloadPresent = true }))
            { SupportsSandboxCli = () => true };

        var host = await setup.DescribeHostAsync(TestContext.CancellationToken);

        Assert.IsFalse(host.Ready);
        Assert.AreEqual(TargetHostCheckStatus.Passed, Check(host, "sandboxFeature").Status);
        var client = Check(host, "sandboxClient");
        Assert.AreEqual(TargetHostCheckStatus.Failed, client.Status);
        StringAssert.Contains(client.Fix!, "Start menu");
        Assert.IsNull(client.NextCommand, "The client cannot be installed by a command winapp offers.");
        var wsb = Check(host, "wsb");
        Assert.AreEqual(TargetHostCheckStatus.Failed, wsb.Status);
        StringAssert.Contains(wsb.Detail!, "not found");
        Assert.AreEqual(TargetHostCheckStatus.NotChecked, Check(host, "restartPending").Status);

        var error = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            setup.EnsureReadyAsync(TestContext.CancellationToken));
        Assert.AreEqual(error.Error.UserAction, client.Fix);
    }

    [TestMethod]
    public async Task DescribeHost_UnhealthyClient_ReportsItsStatus()
    {
        var facts = Facts() with
        {
            FeaturePayloadPresent = true,
            PackageRegistered = true,
            PackageStatus = "Servicing",
            AliasPresent = true,
        };
        var host = await new WindowsSandboxSetup(new FixedProbe(facts)) { SupportsSandboxCli = () => true }
            .DescribeHostAsync(TestContext.CancellationToken);

        var client = Check(host, "sandboxClient");
        Assert.AreEqual(TargetHostCheckStatus.Failed, client.Status);
        StringAssert.Contains(client.Detail!, "Servicing");
        StringAssert.Contains(Check(host, "wsb").Detail!, "did not answer");
    }

    [TestMethod]
    public async Task DescribeHost_UnreadablePackage_IsNotCheckedRatherThanMissing()
    {
        var facts = Facts() with { FeaturePayloadPresent = true, Detail = "package query failed: boom" };
        var host = await new WindowsSandboxSetup(new FixedProbe(facts)) { SupportsSandboxCli = () => true }
            .DescribeHostAsync(TestContext.CancellationToken);

        var client = Check(host, "sandboxClient");
        Assert.AreEqual(TargetHostCheckStatus.NotChecked, client.Status);
        StringAssert.Contains(client.Detail!, "boom");
        Assert.IsNull(client.Fix);
    }

    [TestMethod]
    public async Task DescribeHost_ReadyHost_PassesEverythingAndSkipsRestart()
    {
        var facts = Facts() with
        {
            FeaturePayloadPresent = true,
            PackageRegistered = true,
            PackageStatus = "Ok",
            AliasPresent = true,
            Version = "0.8.107.0",
        };
        var host = await new WindowsSandboxSetup(new FixedProbe(facts)) { SupportsSandboxCli = () => true }
            .DescribeHostAsync(TestContext.CancellationToken);

        Assert.IsTrue(host.Ready);
        Assert.IsTrue(host.Checks.Take(4).All(c => c.Status == TargetHostCheckStatus.Passed));
        StringAssert.Contains(Check(host, "wsb").Detail!, "0.8.107.0");
        Assert.AreEqual(TargetHostCheckStatus.NotChecked, Check(host, "restartPending").Status);
        Assert.IsTrue(host.Checks.All(c => c.Fix is null));
    }

    [TestMethod]
    [DataRow(true, TargetHostCheckStatus.Failed)]
    [DataRow(null, TargetHostCheckStatus.NotChecked)]
    public async Task DescribeHost_RestartState_IsReportedAsObserved(bool? pending, string expected)
    {
        var host = await new WindowsSandboxSetup(new FixedProbe(Facts() with { RestartPending = pending }))
            { SupportsSandboxCli = () => true }
            .DescribeHostAsync(TestContext.CancellationToken);

        var restart = Check(host, "restartPending");
        Assert.AreEqual(expected, restart.Status);
        Assert.AreEqual(pending == true, restart.Fix is not null);
        Assert.IsNull(restart.NextCommand, "Restart timing belongs to the user.");
    }

    [TestMethod]
    public async Task DescribeHost_PendingRestart_GivesTheSameFixAsRun()
    {
        var setup = new WindowsSandboxSetup(new FixedProbe(Facts() with { RestartPending = true }))
            { SupportsSandboxCli = () => true };

        var host = await setup.DescribeHostAsync(TestContext.CancellationToken);
        var error = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            setup.EnsureReadyAsync(TestContext.CancellationToken));

        Assert.AreEqual(error.Error.UserAction, Check(host, "restartPending").Fix);
    }

    [TestMethod]
    public async Task DescribeHost_OldWindows_FailsOsVersion()
    {
        var host = await new WindowsSandboxSetup(new FixedProbe(Facts() with { RestartPending = true }))
            { SupportsSandboxCli = () => false, OsVersion = () => "10.0.22631.0" }
            .DescribeHostAsync(TestContext.CancellationToken);

        var os = Check(host, "osVersion");
        Assert.AreEqual(TargetHostCheckStatus.Failed, os.Status);
        StringAssert.Contains(os.Detail!, "10.0.22631.0");
        StringAssert.Contains(os.Detail!, "24H2");

        foreach (var name in new[] { "sandboxFeature", "sandboxClient", "wsb", "restartPending" })
        {
            var check = Check(host, name);
            Assert.AreEqual(TargetHostCheckStatus.NotChecked, check.Status, name);
            Assert.IsNull(check.Fix, name);
            Assert.IsNull(check.NextCommand, name);
        }
    }

    [TestMethod]
    public async Task DescribeHost_ReadyWithoutVisiblePayload_DoesNotAskToEnableTheFeature()
    {
        var host = await new WindowsSandboxSetup(new FixedProbe(Facts() with
        {
            PackageRegistered = true,
            PackageStatus = "Ok",
            AliasPresent = true,
            Version = "0.8.107.0",
        })).DescribeHostAsync(TestContext.CancellationToken);

        Assert.IsTrue(host.Ready);
        Assert.IsTrue(host.Checks.All(c => c.Status != TargetHostCheckStatus.Failed));
        Assert.IsNull(Check(host, "sandboxFeature").NextCommand);
    }

    [TestMethod]
    public async Task DescribeHost_NotWindows_ChecksNothingElse()
    {
        var host = await new WindowsSandboxSetup(new FixedProbe(Facts() with { IsWindows = false }))
            .DescribeHostAsync(TestContext.CancellationToken);

        Assert.IsFalse(host.Ready);
        Assert.AreEqual(TargetHostCheckStatus.Failed, host.Checks[0].Status);
        Assert.IsTrue(host.Checks.Skip(1).All(c => c.Status == TargetHostCheckStatus.NotChecked));
    }

    private static TargetHostCheck Check(TargetHostReadiness host, string name) =>
        host.Checks.Single(c => c.Name == name);

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

    private static readonly string[] AllCheckNames = ["osVersion", "sandboxFeature", "sandboxClient", "wsb", "restartPending"];

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

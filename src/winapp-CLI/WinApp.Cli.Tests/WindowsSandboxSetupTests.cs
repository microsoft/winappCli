// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.WindowsSandbox;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="WindowsSandboxSetup"/>: what <c>--on sandbox</c> installs on the user's behalf,
/// what it refuses to do, and what it reports when only Windows or the user can finish the job.
/// </summary>
/// <remarks>
/// Driven by a scripted probe and a fake clock. The real thing waits up to ten minutes for a Store
/// download, so the bound, the progress cadence, and the resume behaviour are all asserted without
/// any real time passing and without touching the machine's feature or package state.
/// </remarks>
[TestClass]
public class WindowsSandboxSetupTests
{
    private ScriptedHostProbe _probe = null!;
    private ScriptedFeatureEnabler _enabler = null!;
    private RecordingProgress _progress = null!;
    private DateTimeOffset _now;
    private int _bootstrapperLaunches;
    private bool _bootstrapperRunning;

    [TestInitialize]
    public void Setup()
    {
        _probe = new ScriptedHostProbe();
        _enabler = new ScriptedFeatureEnabler();
        _progress = new RecordingProgress();
        _now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _bootstrapperLaunches = 0;
        _bootstrapperRunning = false;
    }

    [TestMethod]
    public async Task AlreadyReady_DoesNothingAtAll()
    {
        _probe.Enqueue(Facts(version: "0.8.107.0", payload: true, alias: true));

        var facts = await NewSetup().EnsureReadyAsync(TestContext.CancellationToken);

        Assert.AreEqual(WindowsSandboxSetupState.Ready, facts.State);
        Assert.AreEqual(0, _enabler.Attempts, "A ready host must not be asked for elevation.");
        Assert.AreEqual(0, _bootstrapperLaunches, "A ready host must not have a Sandbox started for it.");
        CollectionAssert.AreEqual(Array.Empty<string>(), _progress.Messages);
    }

    [TestMethod]
    public async Task ClientNotInitialized_LaunchesTheBootstrapperAndWaitsForWsbToAnswer()
    {
        _probe.Enqueue(Facts(payload: true));
        _probe.Enqueue(Facts(payload: true, package: true, packageStatus: "Servicing"));
        _probe.Enqueue(Facts(payload: true, package: true, alias: true, version: "0.8.107.0"));

        var facts = await NewSetup().EnsureReadyAsync(TestContext.CancellationToken);

        Assert.AreEqual(WindowsSandboxSetupState.Ready, facts.State);
        Assert.AreEqual(1, _bootstrapperLaunches);
        Assert.AreEqual(0, _enabler.Attempts, "An enabled feature must never be enabled again.");
        CollectionAssert.Contains(_progress.Messages, WindowsSandboxSetup.InstallingClientMessage);
    }

    [TestMethod]
    public async Task ClientNotInitialized_DoesNotRelaunchAnAlreadyRunningBootstrapper()
    {
        // Retrying must continue an installation, not stack another OS update window on top of it.
        _bootstrapperRunning = true;
        _probe.Enqueue(Facts(payload: true));
        _probe.Enqueue(Facts(payload: true, package: true, alias: true, version: "0.8.107.0"));

        await NewSetup().EnsureReadyAsync(TestContext.CancellationToken);

        Assert.AreEqual(0, _bootstrapperLaunches);
    }

    [TestMethod]
    public async Task LongInstallation_KeepsSayingItIsStillWorking()
    {
        // A terminal that goes quiet for minutes is indistinguishable from a hang, and the first
        // thing a user does about a hang is kill the command.
        _probe.Enqueue(Facts(payload: true));
        for (var i = 0; i < 40; i++)
        {
            _probe.Enqueue(Facts(payload: true, package: true, packageStatus: "Servicing"));
        }

        _probe.Enqueue(Facts(payload: true, package: true, alias: true, version: "0.8.107.0"));

        await NewSetup().EnsureReadyAsync(TestContext.CancellationToken);

        Assert.IsGreaterThanOrEqualTo(
            1,
            _progress.Messages.Count(m => m == WindowsSandboxSetup.StillInstallingClientMessage),
            "A long installation must keep reporting that it is still running.");
    }

    [TestMethod]
    public async Task InstallationExceedsTheBound_ReportsWhatItSawAndThatRetryingResumes()
    {
        _probe.Enqueue(Facts(payload: true));
        _probe.Default = Facts(payload: true, package: true, packageStatus: "PackageOffline", alias: true);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => NewSetup().EnsureReadyAsync(TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.SetupIncomplete, failure.Error.Code);
        Assert.AreEqual("PackageOffline", failure.Error.Context!["packageStatus"]);
        Assert.AreEqual("true", failure.Error.Context["aliasPresent"]);
        Assert.AreEqual("none", failure.Error.Context["wsbVersion"]);
        StringAssert.Contains(
            failure.Error.UserAction!,
            "retrying continues the installation",
            StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task TimedOutInstallation_IsResumedRatherThanRestartedByTheNextCommand()
    {
        _probe.Enqueue(Facts(payload: true));
        _probe.Default = Facts(payload: true, package: true, packageStatus: "Servicing");

        await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => NewSetup().EnsureReadyAsync(TestContext.CancellationToken));

        Assert.AreEqual(1, _bootstrapperLaunches);

        // The next command finds the OS bootstrapper still working.
        _bootstrapperRunning = true;
        _probe.Reset();
        _probe.Enqueue(Facts(payload: true, package: true, packageStatus: "Servicing"));
        _probe.Enqueue(Facts(payload: true, package: true, alias: true, version: "0.8.107.0"));

        await NewSetup().EnsureReadyAsync(TestContext.CancellationToken);

        Assert.AreEqual(1, _bootstrapperLaunches, "A resumed installation must not be launched again.");
    }

    [TestMethod]
    public async Task Cancellation_StopsWaiting()
    {
        using var cancellation = new CancellationTokenSource();

        _probe.Enqueue(Facts(payload: true));
        _probe.Default = Facts(payload: true, package: true, packageStatus: "Servicing");

        var setup = NewSetup();
        setup.Delay = (_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => setup.EnsureReadyAsync(cancellation.Token));
    }

    [TestMethod]
    public async Task FeaturePayloadMissing_EnablesTheFeatureAutomatically()
    {
        // --on sandbox is explicit consent. There is no second flag and no prompt of winapp's own; the
        // only dialog is the one Windows raises for elevation.
        _probe.Enqueue(Facts());
        _enabler.Result = new FeatureEnableResult(FeatureEnableOutcome.Enabled, 0);
        _probe.Enqueue(Facts(payload: true, package: true, alias: true, version: "0.8.107.0"));

        var facts = await NewSetup().EnsureReadyAsync(TestContext.CancellationToken);

        Assert.AreEqual(WindowsSandboxSetupState.Ready, facts.State);
        Assert.AreEqual(1, _enabler.Attempts);
        Assert.AreEqual(WindowsSandboxReadiness.FeatureName, _enabler.RequestedFeature);
        CollectionAssert.Contains(_progress.Messages, WindowsSandboxSetup.EnablingFeatureMessage);
    }

    [TestMethod]
    public async Task FeatureEnabledButNeedsARestart_SaysSoAndDoesNotRestart()
    {
        _probe.Enqueue(Facts());
        _enabler.Result = new FeatureEnableResult(FeatureEnableOutcome.RestartRequired, 3010);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => NewSetup().EnsureReadyAsync(TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.SetupRequiresRestart, failure.Error.Code);
        StringAssert.Contains(failure.Error.UserAction!, "Restart Windows", StringComparison.Ordinal);
        Assert.AreEqual(0, _bootstrapperLaunches, "Nothing may run before the restart Windows asked for.");
    }

    [TestMethod]
    public async Task ElevationDeclined_GivesTheExactCommandToRunElevated()
    {
        _probe.Enqueue(Facts());
        _enabler.Result = new FeatureEnableResult(FeatureEnableOutcome.ElevationUnavailable, null, "cancelled");

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => NewSetup().EnsureReadyAsync(TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.SetupRequiresElevation, failure.Error.Code);
        Assert.AreEqual(
            "dism.exe /Online /Enable-Feature /FeatureName:Containers-DisposableClientVM /All /NoRestart",
            failure.Error.NextCommand!.Command);
        Assert.IsTrue(failure.Error.NextCommand.Advisory, "Changing machine configuration stays the user's call.");
    }

    [TestMethod]
    public async Task ServicingRefused_ReportsTheThingsThatActuallyCauseIt()
    {
        _probe.Enqueue(Facts());
        _enabler.Result = new FeatureEnableResult(FeatureEnableOutcome.Failed, 50, "dism.exe exited with 50");

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => NewSetup().EnsureReadyAsync(TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.SetupFailed, failure.Error.Code);
        Assert.AreEqual("dism.exe exited with 50", failure.Error.Context!["detail"]);
        StringAssert.Contains(failure.Error.UserAction!, "edition", StringComparison.Ordinal);
        StringAssert.Contains(failure.Error.UserAction!, "virtualization", StringComparison.Ordinal);
        StringAssert.Contains(failure.Error.UserAction!, "policy", StringComparison.Ordinal);
    }

    /// <summary>
    /// Regression: a host whose feature is already enabled is never told to enable it.
    /// </summary>
    /// <remarks>
    /// This is the live failure the whole classifier exists for. The machine had the feature on and
    /// had rebooted; winapp reported "feature not installed" and offered
    /// <c>Enable-WindowsOptionalFeature</c>, which would have done nothing.
    /// </remarks>
    [TestMethod]
    public async Task EnabledFeatureWithUninitializedClient_NeverSuggestsEnablingTheFeature()
    {
        _probe.Enqueue(Facts(payload: true));
        _probe.Default = Facts(payload: true, package: true, packageStatus: "Servicing");

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => NewSetup().EnsureReadyAsync(TestContext.CancellationToken));

        Assert.AreEqual(0, _enabler.Attempts, "The feature is already enabled; enabling it again does nothing.");
        Assert.AreNotEqual(ExecutionTargetErrorCodes.SetupRequiresElevation, failure.Error.Code);

        var rendered = string.Join(
            '\n',
            failure.Error.Message,
            failure.Error.UserAction,
            failure.Error.NextCommand?.Command);

        StringAssert.DoesNotMatch(rendered, new System.Text.RegularExpressions.Regex("Enable-WindowsOptionalFeature"));
        StringAssert.DoesNotMatch(rendered, new System.Text.RegularExpressions.Regex("Enable-Feature"));
    }

    [TestMethod]
    public async Task FeatureReportedEnabledButItsFilesAreNotThere_SaysWhatItMeasured()
    {
        // Servicing said success and the payload still is not there. winapp cannot see why, so it
        // must not claim a restart is needed or that the client is installing -- neither was
        // observed.
        _probe.Enqueue(Facts());
        _enabler.Result = new FeatureEnableResult(FeatureEnableOutcome.Enabled, 0);
        _probe.Default = Facts();

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => NewSetup().EnsureReadyAsync(TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.SetupIncomplete, failure.Error.Code);
        Assert.AreEqual("false", failure.Error.Context!["featurePayloadPresent"]);
        Assert.AreEqual(
            0,
            _bootstrapperLaunches,
            "A client bootstrapper that is not on disk must not be launched, nor blamed for the failure.");
    }

    [TestMethod]
    public async Task AliasResolvesButNeverAnswers_StillRunsSetup()
    {
        // Regression, and the reason the support probe no longer short-circuits on "a wsb.exe file
        // resolves". The alias is a zero-byte APPEXECLINK, so it resolves on a host whose package
        // never initialized -- exactly the state this whole change exists to fix. Only a version
        // reply may end the probe early.
        _probe.Enqueue(Facts(payload: true, package: true, alias: true, version: null));
        _probe.Enqueue(Facts(payload: true, package: true, alias: true, version: "0.8.107.0"));

        var facts = await NewSetup().EnsureReadyAsync(TestContext.CancellationToken);

        Assert.AreEqual(WindowsSandboxSetupState.Ready, facts.State);
        Assert.AreEqual(1, _bootstrapperLaunches, "A silent alias must still trigger client initialization.");
    }

    [TestMethod]
    public async Task WindowsTooOldForTheSandboxCli_IsRefusedImmediately()
    {
        // Older Windows can have the Sandbox feature and its System32 payload but no `wsb.exe`, so
        // the payload signal alone would send the user into a ten-minute wait for a client this
        // build cannot deliver.
        _probe.Enqueue(Facts(payload: true, package: true));

        var setup = NewSetup();
        setup.SupportsSandboxCli = () => false;

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => setup.EnsureReadyAsync(TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.Unsupported, failure.Error.Code);
        Assert.AreEqual(0, _bootstrapperLaunches);
        Assert.AreEqual(0, _enabler.Attempts);
    }

    [TestMethod]
    public async Task WindowsTooOldButWsbAlreadyAnswers_IsStillReady()
    {
        // A host where `wsb` works is usable whatever its build number says. The version gate must
        // never refuse a machine that is demonstrably fine.
        _probe.Enqueue(Facts(payload: true, package: true, alias: true, version: "0.8.107.0"));

        var setup = NewSetup();
        setup.SupportsSandboxCli = () => false;

        var facts = await setup.EnsureReadyAsync(TestContext.CancellationToken);

        Assert.AreEqual(WindowsSandboxSetupState.Ready, facts.State);
    }

    [TestMethod]
    public async Task WorkingClientWithAnUnhealthyPackage_SaysSoWithoutAnInstallationWait()
    {
        // A client that answers but whose package Windows reports as Servicing is mid-update, not
        // missing. Treating it as missing would launch the OS installer and wait ten minutes on a
        // machine that is already nearly there.
        _probe.Enqueue(Facts(payload: true, package: true, alias: true, version: "0.8.107.0", packageStatus: "Servicing"));

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => NewSetup().EnsureReadyAsync(TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.SetupIncomplete, failure.Error.Code);
        Assert.AreEqual("Servicing", failure.Error.Context!["packageStatus"]);
        Assert.AreEqual(0, _bootstrapperLaunches, "A client that already answers must not be reinstalled.");
        Assert.AreEqual(0, _enabler.Attempts);
    }

    [TestMethod]
    public async Task UnobservablePackageStatus_IsNotHeldAgainstAWorkingClient()
    {
        // A status winapp could not read is not evidence of a problem.
        _probe.Enqueue(Facts(payload: true, package: true, alias: true, version: "0.8.107.0"));

        var facts = await NewSetup().EnsureReadyAsync(TestContext.CancellationToken);

        Assert.AreEqual(WindowsSandboxSetupState.Ready, facts.State);
    }

    [TestMethod]
    public async Task NonWindowsHost_IsUnsupportedAndNothingIsAttempted()
    {
        _probe.Enqueue(Facts(isWindows: false));

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => NewSetup().EnsureReadyAsync(TestContext.CancellationToken));

        Assert.AreEqual(ExecutionTargetErrorCodes.Unsupported, failure.Error.Code);
        Assert.AreEqual(0, _enabler.Attempts);
        Assert.AreEqual(0, _bootstrapperLaunches);
    }

    private WindowsSandboxSetup NewSetup()
    {
        var setup = new WindowsSandboxSetup(_probe, _enabler, _progress)
        {
            UtcNow = () => _now,
            LaunchClientBootstrapper = _ => _bootstrapperLaunches++,
            IsBootstrapperRunning = () => _bootstrapperRunning,
        };

        setup.Delay = (delay, token) =>
        {
            token.ThrowIfCancellationRequested();
            _now += delay;
            return Task.CompletedTask;
        };

        return setup;
    }

    private static WindowsSandboxHostFacts Facts(
        bool isWindows = true,
        bool payload = false,
        bool package = false,
        bool alias = false,
        string? version = null,
        string? packageStatus = null) =>
        new()
        {
            IsWindows = isWindows,
            FeaturePayloadPresent = payload,
            PackageRegistered = package,
            PackageStatus = packageStatus,
            AliasPresent = alias,
            ExecutablePath = alias ? @"C:\Users\test\AppData\Local\Microsoft\WindowsApps\wsb.exe" : null,
            Version = version,
        };

    /// <summary>A probe that replays a scripted sequence of host observations.</summary>
    private sealed class ScriptedHostProbe : IWindowsSandboxHostProbe
    {
        private readonly Queue<WindowsSandboxHostFacts> _scripted = new();

        /// <summary>Returned once the script runs out; models a state that is not changing.</summary>
        public WindowsSandboxHostFacts? Default { get; set; }

        public void Enqueue(WindowsSandboxHostFacts facts) => _scripted.Enqueue(facts);

        public void Reset()
        {
            _scripted.Clear();
            Default = null;
        }

        public Task<WindowsSandboxHostFacts> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_scripted.Count > 0
                ? _scripted.Dequeue()
                : Default ?? throw new InvalidOperationException("The probe script ran out."));
    }

    /// <summary>A feature enabler that records what it was asked for without touching Windows.</summary>
    private sealed class ScriptedFeatureEnabler : IWindowsFeatureEnabler
    {
        public int Attempts { get; private set; }

        public string? RequestedFeature { get; private set; }

        public FeatureEnableResult Result { get; set; } = new(FeatureEnableOutcome.Enabled, 0);

        public Task<FeatureEnableResult> EnableAsync(string featureName, CancellationToken cancellationToken)
        {
            Attempts++;
            RequestedFeature = featureName;
            return Task.FromResult(Result);
        }
    }

    /// <summary>Captures the progress lines the user would have seen, in order.</summary>
    private sealed class RecordingProgress : ITargetProgress
    {
        public List<string> Messages { get; } = [];

        public void Report(string message) => Messages.Add(message);
    }

    /// <summary>MSTest injects this; used for per-test cancellation.</summary>
    public TestContext TestContext { get; set; } = null!;
}

/// <summary>
/// Tests for <see cref="WindowsFeatureEnabler"/>: what it asks Windows to do, and how it classifies
/// what Windows answers.
/// </summary>
/// <remarks>
/// The launcher is replaced throughout, so no test here enables or disables a Windows feature. What
/// is asserted is the invocation itself — an absolute trusted binary, a fixed argument list,
/// <c>/NoRestart</c> — and the mapping from exit code to outcome.
/// </remarks>
[TestClass]
public class WindowsFeatureEnablerTests
{
    [TestMethod]
    public async Task Invocation_UsesTheAbsoluteSystemDismWithAFixedArgumentList()
    {
        ProcessStartInfo? captured = null;
        var enabler = new WindowsFeatureEnabler
        {
            Launcher = (startInfo, _) =>
            {
                captured = startInfo;
                return Task.FromResult(0);
            },
        };

        await enabler.EnableAsync(WindowsSandboxReadiness.FeatureName, TestContext.CancellationToken);

        Assert.IsNotNull(captured);
        Assert.AreEqual(
            Path.Join(Environment.SystemDirectory, "dism.exe"),
            captured.FileName,
            "A privileged launch must name an absolute trusted binary, never a PATH lookup.");
        Assert.IsTrue(Path.IsPathFullyQualified(captured.FileName));

        CollectionAssert.AreEqual(
            ExpectedDismArguments,
            captured.ArgumentList.ToArray(),
            "Arguments are passed as a list so no value can ever be smuggled in as another argument.");
    }

    /// <summary>The exact privileged invocation, pinned; hoisted for CA1861.</summary>
    private static readonly string[] ExpectedDismArguments =
    [
        "/Online",
        "/Enable-Feature",
        "/FeatureName:Containers-DisposableClientVM",
        "/All",
        "/NoRestart",
        "/Quiet",
    ];

    [TestMethod]
    public async Task Invocation_AsksForElevationAndNeverRestarts()
    {
        ProcessStartInfo? captured = null;
        var enabler = new WindowsFeatureEnabler
        {
            Launcher = (startInfo, _) =>
            {
                captured = startInfo;
                return Task.FromResult(0);
            },
        };

        await enabler.EnableAsync(WindowsSandboxReadiness.FeatureName, TestContext.CancellationToken);

        Assert.AreEqual("runas", captured!.Verb, "Elevation is what raises the consent dialog.");
        Assert.IsTrue(captured.UseShellExecute, "The runas verb requires ShellExecute.");
        CollectionAssert.Contains(
            captured.ArgumentList.ToArray(),
            "/NoRestart",
            "winapp must never restart the machine on the user's behalf.");
    }

    [TestMethod]
    public async Task FeatureName_MustBeAPlainName()
    {
        var enabler = new WindowsFeatureEnabler { Launcher = (_, _) => Task.FromResult(0) };

        await Assert.ThrowsExactlyAsync<ExecutionTargetException>(
            () => enabler.EnableAsync(@"Containers /Remove-Feature", TestContext.CancellationToken));
    }

    [TestMethod]
    [DataRow(0, (int)FeatureEnableOutcome.Enabled)]
    [DataRow(3010, (int)FeatureEnableOutcome.RestartRequired)]
    [DataRow(740, (int)FeatureEnableOutcome.ElevationUnavailable)]
    [DataRow(50, (int)FeatureEnableOutcome.Failed)]
    [DataRow(-2146498529, (int)FeatureEnableOutcome.Failed)]
    public async Task ExitCode_IsMappedToTheOutcomeTheCallerActsOn(int exitCode, int expected)
    {
        var enabler = new WindowsFeatureEnabler { Launcher = (_, _) => Task.FromResult(exitCode) };

        var result = await enabler.EnableAsync(WindowsSandboxReadiness.FeatureName, TestContext.CancellationToken);

        Assert.AreEqual((FeatureEnableOutcome)expected, result.Outcome);
        Assert.AreEqual(exitCode, result.ExitCode);
    }

    [TestMethod]
    public async Task UserDismissesTheConsentDialog_IsElevationUnavailable()
    {
        var enabler = new WindowsFeatureEnabler
        {
            Launcher = (_, _) => throw new System.ComponentModel.Win32Exception(
                WindowsFeatureEnabler.ErrorCancelled,
                "The operation was canceled by the user."),
        };

        var result = await enabler.EnableAsync(WindowsSandboxReadiness.FeatureName, TestContext.CancellationToken);

        Assert.AreEqual(FeatureEnableOutcome.ElevationUnavailable, result.Outcome);
    }

    [TestMethod]
    public async Task NoInteractiveSessionToElevateIn_IsElevationUnavailable()
    {
        // A build agent or service has nowhere to show a consent dialog. That is not a broken host,
        // so it must produce the "run this elevated" guidance rather than a generic failure.
        var enabler = new WindowsFeatureEnabler
        {
            Launcher = (_, _) => throw new System.ComponentModel.Win32Exception(
                WindowsFeatureEnabler.ErrorElevationRequired,
                "The requested operation requires elevation."),
        };

        var result = await enabler.EnableAsync(WindowsSandboxReadiness.FeatureName, TestContext.CancellationToken);

        Assert.AreEqual(FeatureEnableOutcome.ElevationUnavailable, result.Outcome);
    }

    [TestMethod]
    public async Task LaunchFailure_IsReportedAsAFailureRatherThanThrowing()
    {
        var enabler = new WindowsFeatureEnabler
        {
            Launcher = (_, _) => throw new InvalidOperationException("Windows did not start the servicing tool."),
        };

        var result = await enabler.EnableAsync(WindowsSandboxReadiness.FeatureName, TestContext.CancellationToken);

        Assert.AreEqual(FeatureEnableOutcome.Failed, result.Outcome);
        Assert.IsNotNull(result.Detail);
    }

    /// <summary>MSTest injects this; used for per-test cancellation.</summary>
    public TestContext TestContext { get; set; } = null!;
}

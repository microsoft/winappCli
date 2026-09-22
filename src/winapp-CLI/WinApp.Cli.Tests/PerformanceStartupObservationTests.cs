// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;
using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Tests;

[TestClass]
public class PerformanceStartupObservationTests
{
    [TestMethod]
    public void PerformanceTimestamp_ComputesElapsedTimeFromFrequency()
    {
        var elapsed = new PerformanceTimestamp(1_500).ElapsedSince(
            new PerformanceTimestamp(1_000),
            frequency: 1_000);

        Assert.AreEqual(TimeSpan.FromMilliseconds(500), elapsed);
    }

    [TestMethod]
    public void PerformanceClock_CalibrationUsesMonotonicCounterAndBoundedUtc()
    {
        var beforeUtc = DateTimeOffset.UtcNow;
        var calibration = new PerformanceClock().Calibrate();
        var afterUtc = DateTimeOffset.UtcNow;

        Assert.IsGreaterThan(0, calibration.Timestamp.Counter);
        Assert.IsGreaterThan(0, calibration.Frequency);
        Assert.IsGreaterThanOrEqualTo(beforeUtc, calibration.Utc);
        Assert.IsLessThanOrEqualTo(afterUtc, calibration.Utc);
        Assert.IsGreaterThanOrEqualTo(TimeSpan.Zero, calibration.Uncertainty);
    }

    [TestMethod]
    public void PackageProcessSnapshot_CurrentUnpackagedTestHostHasNoPackageIdentity()
    {
        using var current = Process.GetCurrentProcess();

        Assert.IsNull(PackageProcessSnapshot.TryGetPackageFamilyName(current));
    }

    [TestMethod]
    public void WindowResponseProbe_DistinguishesTimeoutInvalidHandleAccessDeniedAndOtherFailures()
    {
        AssertProbeResult(1460, WindowResponseProbeOutcome.Timeout);
        AssertProbeResult(1400, WindowResponseProbeOutcome.InvalidWindowHandle);
        AssertProbeResult(5, WindowResponseProbeOutcome.AccessDenied);
        AssertProbeResult(87, WindowResponseProbeOutcome.Win32Failure);

        var responsive = new WindowResponseProbe(new FakeWindowResponseNative(
            new(Succeeded: true, Win32ErrorCode: 1460))).Probe(7);
        Assert.AreEqual(WindowResponseProbeOutcome.Responsive, responsive.Outcome);
        Assert.IsNull(responsive.Win32ErrorCode);
    }

    [TestMethod]
    public void CapturePackageBaseline_PreservesProcessGenerationsBeforeActivation()
    {
        ProcessIdentity[] expected =
        {
            Identity(10, 100),
            Identity(20, 200),
        };
        var packageProcesses = new FakePackageProcessSnapshot(expected);

        var baseline = StartupObservationSession.CapturePackageBaseline(
            "Contoso.App_abcdefghijklm",
            packageProcesses);

        Assert.AreEqual("Contoso.App_abcdefghijklm", baseline.PackageFamilyName);
        CollectionAssert.AreEquivalent(expected, baseline.Processes.ToArray());
        Assert.AreEqual("Contoso.App_abcdefghijklm", packageProcesses.RequestedFamilyName);
    }

    [TestMethod]
    public void Observe_RecordsOrderedLaunchMilestonesWithPollingResolution()
    {
        var processProbe = new FakeProcessIdentityProbe();
        processProbe.Set(42, Identity(42, 100));
        var systemQuery = new FakeSystemUiQuery();
        var ownership = new TargetOwnership(processProbe, systemQuery);
        var windows = new FakeTopLevelWindowProbe();
        windows.Windows.Add(new(9001, 42, IsVisible: true));
        systemQuery.ProcessIdByHwnd[9001] = 42;
        var clock = new FakePerformanceClock(1_000, 1_250);
        using var session = new StartupObservationSession(
            clock,
            windows,
            ownership,
            new PerformanceTimestamp(1_000),
            []);

        var update = session.Observe(42, []);

        CollectionAssert.AreEqual(
            new[]
            {
                StartupEventType.ProcessObserved,
                StartupEventType.WindowObserved,
                StartupEventType.WindowVisible,
            },
            update.Events.Select(startupEvent => startupEvent.Type).ToArray());
        Assert.IsTrue(update.Events.All(startupEvent =>
            startupEvent.BoundaryResolution == TimeSpan.FromMilliseconds(250)));
        Assert.AreEqual(StartupLaunchDisposition.Launched, update.Disposition);
        Assert.AreEqual(Identity(42, 100), update.Events[0].Process);
        Assert.AreEqual(9001, update.Events[1].WindowHandle);
    }

    [TestMethod]
    public void Observe_InvisibleWindowSeparatesObservedFromVisibleMilestones()
    {
        var fixture = CreateFixture(activationProcessId: 42);
        fixture.Windows.Windows.Add(new(9001, 42, IsVisible: false));
        fixture.SystemQuery.ProcessIdByHwnd[9001] = 42;

        var first = fixture.Session.Observe(42, []);
        fixture.Windows.Windows[0] = new(9001, 42, IsVisible: true);
        var second = fixture.Session.Observe(42, []);

        CollectionAssert.AreEqual(
            new[] { StartupEventType.ProcessObserved, StartupEventType.WindowObserved },
            first.Events.Select(startupEvent => startupEvent.Type).ToArray());
        CollectionAssert.AreEqual(
            new[] { StartupEventType.WindowVisible },
            second.Events.Select(startupEvent => startupEvent.Type).ToArray());
        Assert.AreEqual(StartupLaunchDisposition.Pending, first.Disposition);
        Assert.AreEqual(StartupLaunchDisposition.Launched, second.Disposition);
        fixture.Dispose();
    }

    [TestMethod]
    public void Observe_InitialTimeoutThenSuccessReportsOnlyFirstResponsiveBoundary()
    {
        var fixture = CreateFixture(activationProcessId: 42);
        fixture.Windows.Windows.Add(new(
            9001,
            42,
            IsVisible: true,
            Response: Response(WindowResponseProbeOutcome.Timeout, 1460)));
        fixture.SystemQuery.ProcessIdByHwnd[9001] = 42;

        var failed = fixture.Session.Observe(42, []);
        fixture.Windows.Windows[0] = new(
            9001,
            42,
            IsVisible: true,
            Response: Response(WindowResponseProbeOutcome.Responsive));
        var recovered = fixture.Session.Observe(42, []);
        var stable = fixture.Session.Observe(42, []);

        Assert.IsFalse(failed.Events.Any(startupEvent =>
            startupEvent.Type == StartupEventType.WindowResponseFailed));
        CollectionAssert.AreEqual(
            new[] { StartupEventType.WindowResponsive },
            recovered.Events.Select(startupEvent => startupEvent.Type).ToArray());
        Assert.IsEmpty(stable.Events);
        fixture.Dispose();
    }

    [TestMethod]
    public void Observe_ResponsiveWindowReportsFirstSuccessOnlyOnce()
    {
        var fixture = CreateFixture(activationProcessId: 42);
        fixture.Windows.Windows.Add(new(
            9001,
            42,
            IsVisible: true,
            Response: Response(WindowResponseProbeOutcome.Responsive),
            ThreadId: 73));
        fixture.SystemQuery.ProcessIdByHwnd[9001] = 42;

        var first = fixture.Session.Observe(42, []);
        var second = fixture.Session.Observe(42, []);

        CollectionAssert.Contains(
            first.Events.Select(startupEvent => startupEvent.Type).ToArray(),
            StartupEventType.WindowResponsive);
        Assert.IsTrue(first.Events
            .Where(startupEvent => startupEvent.WindowHandle == 9001)
            .All(startupEvent => startupEvent.WindowThreadId == 73));
        Assert.IsFalse(second.Events.Any(startupEvent =>
            startupEvent.Type == StartupEventType.WindowResponsive));
        fixture.Dispose();
    }

    [TestMethod]
    public void Observe_ResponseTransitionsAreTrackedPerOwnedWindow()
    {
        var fixture = CreateFixture(activationProcessId: 42);
        fixture.Windows.Windows.Add(new(
            9001,
            42,
            IsVisible: true,
            Response: Response(WindowResponseProbeOutcome.Responsive)));
        fixture.Windows.Windows.Add(new(
            9002,
            42,
            IsVisible: true,
            Response: Response(WindowResponseProbeOutcome.Responsive)));
        fixture.SystemQuery.ProcessIdByHwnd[9001] = 42;
        fixture.SystemQuery.ProcessIdByHwnd[9002] = 42;

        fixture.Session.Observe(42, []);
        fixture.Windows.Windows[0] = new(
            9001,
            42,
            IsVisible: true,
            Response: Response(WindowResponseProbeOutcome.Timeout, 1460));
        var update = fixture.Session.Observe(42, []);

        Assert.IsTrue(update.Events.Any(startupEvent =>
            startupEvent.Type == StartupEventType.WindowResponseFailed
            && startupEvent.WindowHandle == 9001));
        Assert.IsFalse(update.Events.Any(startupEvent =>
            startupEvent.Type == StartupEventType.WindowResponseFailed
            && startupEvent.WindowHandle == 9002));
        fixture.Dispose();
    }

    [TestMethod]
    public void Observe_InvalidOrInaccessibleWindowRecordsProbeFailureNotTimeout()
    {
        var fixture = CreateFixture(activationProcessId: 42);
        fixture.Windows.Windows.Add(new(
            9001,
            42,
            IsVisible: true,
            Response: Response(WindowResponseProbeOutcome.InvalidWindowHandle, 1400)));
        fixture.Windows.Windows.Add(new(
            9002,
            42,
            IsVisible: true,
            Response: Response(WindowResponseProbeOutcome.AccessDenied, 5)));
        fixture.SystemQuery.ProcessIdByHwnd[9001] = 42;
        fixture.SystemQuery.ProcessIdByHwnd[9002] = 42;

        var update = fixture.Session.Observe(42, []);

        Assert.IsFalse(update.Events.Any(startupEvent =>
            startupEvent.Type == StartupEventType.WindowResponseFailed));
        var failures = update.Events
            .Where(startupEvent =>
                startupEvent.Type == StartupEventType.WindowResponseProbeFailed)
            .OrderBy(startupEvent => startupEvent.WindowHandle)
            .ToArray();
        Assert.HasCount(2, failures);
        Assert.AreEqual(WindowResponseProbeOutcome.InvalidWindowHandle, failures[0].ResponseProbeOutcome);
        Assert.AreEqual(1400, failures[0].Win32ErrorCode);
        Assert.AreEqual(WindowResponseProbeOutcome.AccessDenied, failures[1].ResponseProbeOutcome);
        Assert.AreEqual(5, failures[1].Win32ErrorCode);
        fixture.Dispose();
    }

    [TestMethod]
    public void Observe_PreExistingWindowClassifiesAttachedLateWhenRedirectorAlreadyExited()
    {
        var processProbe = new FakeProcessIdentityProbe();
        var existing = Identity(10, 100);
        processProbe.Set(10, existing);
        var systemQuery = new FakeSystemUiQuery();
        systemQuery.ProcessIdByHwnd[7001] = 10;
        var ownership = new TargetOwnership(processProbe, systemQuery);
        var windows = new FakeTopLevelWindowProbe();
        windows.Windows.Add(new(7001, 10, IsVisible: true));
        var clock = new FakePerformanceClock(1_000, 1_100);
        using var session = new StartupObservationSession(
            clock,
            windows,
            ownership,
            new PerformanceTimestamp(1_000),
            [existing]);

        var update = session.Observe(
            activationProcessId: 20,
            packageCandidateProcesses: [existing]);

        Assert.AreEqual(StartupLaunchDisposition.AttachedLate, update.Disposition);
        Assert.AreEqual(ProcessAdmissionStatus.NotFound, update.ProcessFailures[20]);
        var visible = update.Events.Single(
            startupEvent => startupEvent.Type == StartupEventType.WindowVisible);
        Assert.AreEqual(existing, visible.Process);
        Assert.IsTrue(visible.WasPresentBeforeActivation);
    }

    [TestMethod]
    public void Observe_NewLaunchWindowSupersedesEarlierPreExistingWindow()
    {
        var processProbe = new FakeProcessIdentityProbe();
        var existing = Identity(10, 100);
        var launched = Identity(20, 200);
        processProbe.Set(10, existing);
        processProbe.Set(20, launched);
        var systemQuery = new FakeSystemUiQuery();
        systemQuery.ProcessIdByHwnd[7001] = 10;
        systemQuery.ProcessIdByHwnd[7002] = 20;
        var windows = new FakeTopLevelWindowProbe();
        windows.Windows.Add(new(7001, 10, IsVisible: true));
        var clock = new FakePerformanceClock(1_000, 1_100, 1_200);
        using var session = new StartupObservationSession(
            clock,
            windows,
            new TargetOwnership(processProbe, systemQuery),
            new PerformanceTimestamp(1_000),
            [existing]);

        var first = session.Observe(20, [existing, launched]);
        windows.Windows.Add(new(7002, 20, IsVisible: true));
        var second = session.Observe(20, [existing, launched]);

        Assert.AreEqual(StartupLaunchDisposition.AttachedLate, first.Disposition);
        Assert.AreEqual(StartupLaunchDisposition.Launched, second.Disposition);
        Assert.IsTrue(second.Events.Any(startupEvent =>
            startupEvent.Type == StartupEventType.WindowVisible
            && startupEvent.Process == launched
            && !startupEvent.WasPresentBeforeActivation));
    }

    [TestMethod]
    public void Observe_ReportsProcessExitOnceWithRawExitCode()
    {
        var fixture = CreateFixture(activationProcessId: 42);
        fixture.Session.Observe(42, []);
        fixture.ProcessProbe.GetState(42).HasExited = true;
        fixture.ProcessProbe.GetState(42).ExitCode = unchecked((int)0xC0000005);

        var firstExit = fixture.Session.Observe(42, []);
        var secondExit = fixture.Session.Observe(42, []);

        var exit = firstExit.Events.Single(
            startupEvent => startupEvent.Type == StartupEventType.ProcessExited);
        Assert.AreEqual(unchecked((int)0xC0000005), exit.ExitCode);
        Assert.IsEmpty(secondExit.Events);
        fixture.Dispose();
    }

    [TestMethod]
    public void Observe_DoesNotAdmitWindowFromUnknownProcess()
    {
        var fixture = CreateFixture(activationProcessId: 42);
        fixture.Windows.Windows.Add(new(9001, 99, IsVisible: true));
        fixture.SystemQuery.ProcessIdByHwnd[9001] = 99;

        var update = fixture.Session.Observe(42, []);

        Assert.IsFalse(update.Events.Any(startupEvent =>
            startupEvent.Type is StartupEventType.WindowObserved or StartupEventType.WindowVisible));
        Assert.AreEqual(StartupLaunchDisposition.Pending, update.Disposition);
        fixture.Dispose();
    }

    [TestMethod]
    public void Observe_RejectsPackageCandidateWhenPidGenerationChangedAfterSnapshot()
    {
        var fixture = CreateFixture(activationProcessId: 42);
        var snapshotted = Identity(99, 100);
        fixture.ProcessProbe.Set(99, Identity(99, 200));

        var update = fixture.Session.Observe(42, [snapshotted]);

        Assert.AreEqual(
            ProcessAdmissionStatus.ProcessGenerationChanged,
            update.ProcessFailures[99]);
        Assert.IsFalse(fixture.Session.Events.Any(startupEvent =>
            startupEvent.Process == Identity(99, 200)));
        fixture.Dispose();
    }

    [TestMethod]
    public void Observe_ReusedWindowHandleProducesMilestoneForNewProcessGeneration()
    {
        var fixture = CreateFixture(activationProcessId: 42);
        fixture.Windows.Windows.Add(new(9001, 42, IsVisible: true));
        fixture.SystemQuery.ProcessIdByHwnd[9001] = 42;
        fixture.Session.Observe(42, []);

        fixture.ProcessProbe.GetState(42).HasExited = true;
        fixture.Session.Observe(42, []);
        fixture.ProcessProbe.Set(42, Identity(42, 200));

        var update = fixture.Session.Observe(42, []);

        Assert.IsTrue(update.Events.Any(startupEvent =>
            startupEvent.Type == StartupEventType.WindowObserved
            && startupEvent.Process == Identity(42, 200)));
        fixture.Dispose();
    }

    private static Fixture CreateFixture(int activationProcessId)
    {
        var processProbe = new FakeProcessIdentityProbe();
        processProbe.Set(activationProcessId, Identity(activationProcessId, 100));
        var systemQuery = new FakeSystemUiQuery();
        var ownership = new TargetOwnership(processProbe, systemQuery);
        var windows = new FakeTopLevelWindowProbe();
        var clock = new FakePerformanceClock(1_000, 1_100, 1_200, 1_300, 1_400);
        var session = new StartupObservationSession(
            clock,
            windows,
            ownership,
            new PerformanceTimestamp(1_000),
            []);
        return new(processProbe, systemQuery, windows, session);
    }

    private static ProcessIdentity Identity(int processId, long startTimeUtcTicks) =>
        new(processId, startTimeUtcTicks);

    private static WindowResponseProbeResult Response(
        WindowResponseProbeOutcome outcome,
        int? win32ErrorCode = null) =>
        new(outcome, win32ErrorCode);

    private static void AssertProbeResult(
        int win32ErrorCode,
        WindowResponseProbeOutcome expected)
    {
        var result = new WindowResponseProbe(new FakeWindowResponseNative(
            new(Succeeded: false, win32ErrorCode))).Probe(7);

        Assert.AreEqual(expected, result.Outcome);
        Assert.AreEqual(win32ErrorCode, result.Win32ErrorCode);
    }

    private sealed record Fixture(
        FakeProcessIdentityProbe ProcessProbe,
        FakeSystemUiQuery SystemQuery,
        FakeTopLevelWindowProbe Windows,
        StartupObservationSession Session) : IDisposable
    {
        public void Dispose() => Session.Dispose();
    }

    private sealed class FakePerformanceClock(
        long frequency,
        params long[] timestamps) : IPerformanceClock
    {
        private readonly Queue<long> _timestamps = new(timestamps);

        public long Frequency { get; } = frequency;

        public PerformanceTimestamp GetTimestamp() => new(_timestamps.Dequeue());

        public PerformanceClockCalibration Calibrate() =>
            new(GetTimestamp(), DateTimeOffset.UnixEpoch, Frequency, TimeSpan.Zero);
    }

    private sealed class FakeTopLevelWindowProbe : ITopLevelWindowProbe
    {
        public List<TopLevelWindowSnapshot> Windows { get; } = [];

        public IReadOnlyList<TopLevelWindowSnapshot> Snapshot(IReadOnlySet<int> processIds) =>
            Windows.Where(window => processIds.Contains(window.ProcessId)).ToArray();
    }

    private sealed class FakeWindowResponseNative(WindowMessageProbeResult result)
        : IWindowResponseNative
    {
        public WindowMessageProbeResult SendNullMessage(long windowHandle, TimeSpan timeout) =>
            result;
    }

    private sealed class FakePackageProcessSnapshot(
        IReadOnlyList<ProcessIdentity> processes) : IPackageProcessSnapshot
    {
        public string? RequestedFamilyName { get; private set; }

        public IReadOnlyList<ProcessIdentity> Capture(string packageFamilyName)
        {
            RequestedFamilyName = packageFamilyName;
            return processes;
        }
    }

    private sealed class FakeProcessState(ProcessIdentity identity)
    {
        public ProcessIdentity Identity { get; } = identity;

        public bool HasExited { get; set; }

        public int? ExitCode { get; set; }
    }

    private sealed class FakeProcessIdentityProbe : IProcessIdentityProbe
    {
        private readonly Dictionary<int, FakeProcessState> _states = [];

        public void Set(int processId, ProcessIdentity identity) =>
            _states[processId] = new(identity);

        public FakeProcessState GetState(int processId) => _states[processId];

        public ProcessObservationResult Observe(int processId)
        {
            return _states.TryGetValue(processId, out var state)
                ? new(new FakeObservedProcess(state), ProcessObservationFailure.None)
                : new(null, ProcessObservationFailure.NotFound);
        }
    }

    private sealed class FakeObservedProcess(FakeProcessState state) : IObservedProcess
    {
        public ProcessIdentity Identity => state.Identity;

        public bool HasExited => state.HasExited;

        public bool TryGetExitCode(out int exitCode)
        {
            if (state.HasExited && state.ExitCode is { } value)
            {
                exitCode = value;
                return true;
            }

            exitCode = default;
            return false;
        }

        public ProcessResourceCounters CaptureResourceCounters() => new(
            Identity,
            IsTerminal: state.HasExited);

        public void Dispose()
        {
        }
    }
}

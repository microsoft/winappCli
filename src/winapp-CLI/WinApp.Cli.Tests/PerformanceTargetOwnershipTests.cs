// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;
using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Tests;

[TestClass]
public class PerformanceTargetOwnershipTests
{
    [TestMethod]
    public void ProcessIdentityProbe_ObservesCurrentProcessWithStableGeneration()
    {
        var probe = new ProcessIdentityProbe();
        using var current = Process.GetCurrentProcess();

        var observation = probe.Observe(current.Id);

        Assert.IsTrue(observation.Succeeded);
        Assert.AreEqual(
            new ProcessIdentity(current.Id, current.StartTime.ToUniversalTime().Ticks),
            observation.Process!.Identity);
        Assert.IsFalse(observation.Process.HasExited);
        observation.Process.Dispose();
    }

    [TestMethod]
    public void ProcessIdentityProbe_MissingProcessReportsNotFound()
    {
        var observation = new ProcessIdentityProbe().Observe(int.MaxValue);

        Assert.IsFalse(observation.Succeeded);
        Assert.AreEqual(ProcessObservationFailure.NotFound, observation.Failure);
    }

    [TestMethod]
    public async Task ProcessIdentityProbe_RetainedHandlePreservesExitCode()
    {
        using var child = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = "/d /c \"timeout /t 1 /nobreak >nul & exit /b 37\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        var observation = new ProcessIdentityProbe().Observe(child.Id);
        Assert.IsTrue(observation.Succeeded);

        using var observed = observation.Process!;
        await child.WaitForExitAsync();

        Assert.IsTrue(observed.HasExited);
        Assert.IsTrue(observed.TryGetExitCode(out var exitCode));
        Assert.AreEqual(37, exitCode);
    }

    [TestMethod]
    public void AdmitWindow_RequiresPreviouslyEvidencedProcess()
    {
        var probe = new FakeProcessIdentityProbe();
        probe.Set(42, Identity(42, 100));
        var systemQuery = new FakeSystemUiQuery { ProcessIdForWindowResult = 42 };
        using var ownership = new TargetOwnership(probe, systemQuery);

        var status = ownership.AdmitWindow(9001);

        Assert.AreEqual(WindowAdmissionStatus.ProcessNotOwned, status);
        Assert.IsEmpty(ownership.Windows);
    }

    [TestMethod]
    public void AdmitWindow_AssociatesExactOwnedProcessGeneration()
    {
        var probe = new FakeProcessIdentityProbe();
        probe.Set(42, Identity(42, 100));
        var systemQuery = new FakeSystemUiQuery { ProcessIdForWindowResult = 42 };
        using var ownership = new TargetOwnership(probe, systemQuery);

        Assert.AreEqual(
            ProcessAdmissionStatus.Added,
            ownership.AdmitProcess(42, ProcessOwnershipEvidence.Launched));
        Assert.AreEqual(WindowAdmissionStatus.Added, ownership.AdmitWindow(9001));

        var window = ownership.Windows.Single();
        Assert.AreEqual(9001, window.WindowHandle);
        Assert.AreEqual(Identity(42, 100), window.Process);
    }

    [TestMethod]
    public void AdmitWindow_RejectsReusedPidGeneration()
    {
        var probe = new FakeProcessIdentityProbe();
        probe.Set(42, Identity(42, 100));
        var systemQuery = new FakeSystemUiQuery { ProcessIdForWindowResult = 42 };
        using var ownership = new TargetOwnership(probe, systemQuery);
        Assert.AreEqual(
            ProcessAdmissionStatus.Added,
            ownership.AdmitProcess(42, ProcessOwnershipEvidence.Launched));

        probe.Set(42, Identity(42, 200));

        Assert.AreEqual(
            WindowAdmissionStatus.ProcessGenerationChanged,
            ownership.AdmitWindow(9001));
        Assert.IsEmpty(ownership.Windows);
    }

    [TestMethod]
    public void AdmitProcess_ReplacesExitedGenerationAndRemovesItsWindows()
    {
        var probe = new FakeProcessIdentityProbe();
        var first = probe.Set(42, Identity(42, 100));
        var systemQuery = new FakeSystemUiQuery { ProcessIdForWindowResult = 42 };
        using var ownership = new TargetOwnership(probe, systemQuery);
        ownership.AdmitProcess(42, ProcessOwnershipEvidence.Launched);
        ownership.AdmitWindow(9001);

        first.HasExitedValue = true;
        probe.Set(42, Identity(42, 200));

        Assert.AreEqual(
            ProcessAdmissionStatus.ReplacedExitedGeneration,
            ownership.AdmitProcess(42, ProcessOwnershipEvidence.PackageIdentity));
        Assert.IsEmpty(ownership.Windows);
        Assert.AreEqual(Identity(42, 200), ownership.Processes.Single().Identity);
    }

    [TestMethod]
    public void AdmitProcess_DoesNotReplaceConflictingLiveGeneration()
    {
        var probe = new FakeProcessIdentityProbe();
        var first = probe.Set(42, Identity(42, 100));
        var systemQuery = new FakeSystemUiQuery();
        using var ownership = new TargetOwnership(probe, systemQuery);
        ownership.AdmitProcess(42, ProcessOwnershipEvidence.Launched);

        probe.Set(42, Identity(42, 200));

        Assert.AreEqual(
            ProcessAdmissionStatus.ConflictingLiveGeneration,
            ownership.AdmitProcess(42, ProcessOwnershipEvidence.PackageIdentity));
        Assert.AreEqual(Identity(42, 100), ownership.Processes.Single().Identity);
        Assert.IsFalse(first.Disposed);
        Assert.IsTrue(probe.LastObservation!.Disposed);
    }

    [TestMethod]
    public void AdmitProcess_ExpectedGenerationRejectsReusedPid()
    {
        var probe = new FakeProcessIdentityProbe();
        probe.Set(42, Identity(42, 200));
        using var ownership = new TargetOwnership(probe, new FakeSystemUiQuery());

        var status = ownership.AdmitProcess(
            Identity(42, 100),
            ProcessOwnershipEvidence.PackageIdentity);

        Assert.AreEqual(ProcessAdmissionStatus.ProcessGenerationChanged, status);
        Assert.IsEmpty(ownership.Processes);
        Assert.IsTrue(probe.LastObservation!.Disposed);
    }

    private static ProcessIdentity Identity(int processId, long startTimeUtcTicks) =>
        new(processId, startTimeUtcTicks);

    private sealed class FakeProcessIdentityProbe : IProcessIdentityProbe
    {
        private readonly Dictionary<int, FakeProcessState> _processes = [];

        public FakeObservedProcess? LastObservation { get; private set; }

        public FakeObservedProcess Set(int processId, ProcessIdentity identity)
        {
            var state = new FakeProcessState(identity);
            _processes[processId] = state;
            return new FakeObservedProcess(state);
        }

        public ProcessObservationResult Observe(int processId)
        {
            if (!_processes.TryGetValue(processId, out var state))
            {
                return new(null, ProcessObservationFailure.NotFound);
            }

            // Each observation represents a newly opened handle to the same process generation.
            LastObservation = new FakeObservedProcess(state);
            return new(LastObservation, ProcessObservationFailure.None);
        }
    }

    private sealed class FakeProcessState(ProcessIdentity identity)
    {
        public ProcessIdentity Identity { get; } = identity;

        public bool HasExitedValue { get; set; }
    }

    private sealed class FakeObservedProcess(FakeProcessState state) : IObservedProcess
    {
        public ProcessIdentity Identity => state.Identity;

        public bool HasExitedValue
        {
            get => state.HasExitedValue;
            set => state.HasExitedValue = value;
        }

        public bool Disposed { get; private set; }

        public bool HasExited => state.HasExitedValue;

        public bool TryGetExitCode(out int exitCode)
        {
            exitCode = default;
            return false;
        }

        public void Dispose() => Disposed = true;
    }
}

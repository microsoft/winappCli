// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class PerfWindowMonitorTests
{
    [TestMethod]
    public void MonitorRecordsMilestonesAndResponseTransitionsWithoutCallingStartupTimeoutARecovery()
    {
        var source = new FakeWindowSource();
        var evidence = new PerfWindowEvidence();
        var monitor = new PerfWindowMonitor(source, evidence);
        source.Enqueue([Window(7, visible: false)]);
        source.Enqueue([Window(7, visible: true, PerfWindowProbeOutcome.Timeout)]);
        source.Enqueue([Window(7, visible: true, PerfWindowProbeOutcome.Responsive)]);
        source.Enqueue([Window(7, visible: true, PerfWindowProbeOutcome.Timeout)]);
        source.Enqueue([Window(7, visible: true, PerfWindowProbeOutcome.Timeout)]);
        source.Enqueue([Window(7, visible: true, PerfWindowProbeOutcome.Responsive)]);
        source.Enqueue([]);

        for (var index = 0; index < 7; index++)
        {
            monitor.Observe(42, 100 + index * 10);
        }

        CollectionAssert.AreEqual(
            new[]
            {
                PerfWindowEventTypes.Observed,
                PerfWindowEventTypes.Visible,
                PerfWindowEventTypes.Responsive,
                PerfWindowEventTypes.ResponseDelayed,
                PerfWindowEventTypes.ResponseRecovered,
                PerfWindowEventTypes.Removed,
            },
            evidence.Events.Select(value => value.Type).ToArray());
        var delay = evidence.Events.Single(value => value.Type == PerfWindowEventTypes.ResponseDelayed);
        Assert.AreEqual(nameof(PerfWindowProbeOutcome.Timeout), delay.ProbeOutcome);
        Assert.AreEqual(1460, delay.Win32Error);
        Assert.AreEqual((uint)73, delay.ThreadId);
    }

    [TestMethod]
    public void MonitorReportsProbeErrorsOnceWithoutCallingThemResponseDelays()
    {
        var source = new FakeWindowSource();
        var evidence = new PerfWindowEvidence();
        var monitor = new PerfWindowMonitor(source, evidence);
        source.Enqueue([Window(7, true, PerfWindowProbeOutcome.AccessDenied)]);
        source.Enqueue([Window(7, true, PerfWindowProbeOutcome.AccessDenied)]);
        source.Enqueue([Window(7, true, PerfWindowProbeOutcome.InvalidWindowHandle)]);

        monitor.Observe(42, 100);
        monitor.Observe(42, 200);
        monitor.Observe(42, 300);

        Assert.HasCount(2, evidence.Events.Where(value => value.Type == PerfWindowEventTypes.ProbeFailed).ToArray());
        Assert.IsFalse(evidence.Events.Any(value => value.Type == PerfWindowEventTypes.ResponseDelayed));
    }

    [TestMethod]
    public void MonitorEndsAResponseDelayWhenTheWindowBecomesHidden()
    {
        var source = new FakeWindowSource();
        var evidence = new PerfWindowEvidence();
        var monitor = new PerfWindowMonitor(source, evidence);
        source.Enqueue([Window(7, true, PerfWindowProbeOutcome.Responsive)]);
        source.Enqueue([Window(7, true, PerfWindowProbeOutcome.Timeout)]);
        source.Enqueue([Window(7, false)]);

        monitor.Observe(42, 100);
        monitor.Observe(42, 200);
        monitor.Observe(42, 300);

        CollectionAssert.Contains(
            evidence.Events.Select(value => value.Type).ToArray(),
            PerfWindowEventTypes.Hidden);
        var capture = Capture();
        capture.WindowEvidence = evidence;
        var experience = PerfWindowExperienceBuilder.Create(capture, new(-900, 1_500));
        Assert.AreEqual(1, experience.ResponseDelayCount);
        Assert.AreEqual(1, experience.UnrecoveredResponseDelayCount);
        Assert.AreEqual(100, experience.LongestObservedResponseDelayMs);
    }

    [TestMethod]
    public void MonitorStartsANewDelayWhenAHiddenWindowIsShownStillUnresponsive()
    {
        var source = new FakeWindowSource();
        var evidence = new PerfWindowEvidence();
        var monitor = new PerfWindowMonitor(source, evidence);
        source.Enqueue([Window(7, true, PerfWindowProbeOutcome.Responsive)]);
        source.Enqueue([Window(7, true, PerfWindowProbeOutcome.Timeout)]);
        source.Enqueue([Window(7, false)]);
        source.Enqueue([Window(7, true, PerfWindowProbeOutcome.Timeout)]);

        for (var index = 0; index < 4; index++)
        {
            monitor.Observe(42, 100 + index * 100);
        }

        Assert.HasCount(2, evidence.Events
            .Where(value => value.Type == PerfWindowEventTypes.ResponseDelayed)
            .ToArray());
    }

    [TestMethod]
    public void ExperienceUsesTheSelectedRangeAndDistinguishesUnrecoveredDelays()
    {
        var capture = Capture();
        capture.WindowEvidence.Events.AddRange(
        [
            Event(PerfWindowEventTypes.Observed, 1_100),
            Event(PerfWindowEventTypes.Visible, 1_200),
            Event(PerfWindowEventTypes.Responsive, 1_300),
            Event(PerfWindowEventTypes.ResponseDelayed, 1_500),
            Event(PerfWindowEventTypes.ResponseRecovered, 1_900),
            Event(PerfWindowEventTypes.ResponseDelayed, 2_100),
        ]);

        var full = PerfWindowExperienceBuilder.Create(capture, new(0, 1_500));
        Assert.AreEqual("complete", full.Status);
        Assert.AreEqual(100, full.FirstWindowObservedMs);
        Assert.AreEqual(200, full.FirstVisibleMs);
        Assert.AreEqual(300, full.FirstResponsiveMs);
        Assert.AreEqual(2, full.ResponseDelayCount);
        Assert.AreEqual(1, full.UnrecoveredResponseDelayCount);
        Assert.AreEqual(400, full.LongestObservedResponseDelayMs);

        var clipped = PerfWindowExperienceBuilder.Create(capture, new(600, 800));
        Assert.AreEqual("complete", clipped.Status,
            "A window observed before the range remains observed when no transition occurs inside the range.");
        Assert.IsNull(clipped.FirstVisibleMs);
        Assert.AreEqual(1, clipped.ResponseDelayCount);
        Assert.AreEqual(0, clipped.UnrecoveredResponseDelayCount);
        Assert.AreEqual(200, clipped.LongestObservedResponseDelayMs);
        Assert.AreEqual(PerfWindowEvidence.CadenceMs, clipped.RequestedCadenceMs);
        Assert.AreEqual(PerfWindowEvidence.TimeoutMs, clipped.ProbeTimeoutMs);
    }

    [TestMethod]
    public void ExperienceDoesNotCarryAWindowRemovedBeforeTheSelectedRange()
    {
        var capture = Capture();
        capture.WindowEvidence.Events.AddRange(
        [
            Event(PerfWindowEventTypes.Observed, 1_100),
            Event(PerfWindowEventTypes.Removed, 1_200),
        ]);

        var result = PerfWindowExperienceBuilder.Create(capture, new(300, 400));

        Assert.AreEqual("not-observed", result.Status);
    }

    [TestMethod]
    public void EvidenceIsBoundedAndRejectsInvalidCaptureData()
    {
        var evidence = new PerfWindowEvidence();
        for (var index = 0; index < PerfWindowEvidence.MaximumEvents + 1; index++)
        {
            evidence.Add(Event(PerfWindowEventTypes.Observed, index + 1));
        }
        Assert.HasCount(PerfWindowEvidence.MaximumEvents, evidence.Events);
        Assert.IsTrue(evidence.Truncated);
        Assert.IsTrue(evidence.IsValid());

        evidence.Events[0] = evidence.Events[0] with { Type = "invented" };
        Assert.IsFalse(evidence.IsValid());
    }

    [TestMethod]
    public void MaximumWindowAndResourceEvidenceFitsTheCaptureManifestLimit()
    {
        var capture = Capture();
        capture.ProcessResources = new()
        {
            Samples = Enumerable.Range(0, PerfProcessResources.MaximumSamples)
                .Select(index => new PerfResourceSample(
                    long.MaxValue - index,
                    double.MaxValue,
                    double.MaxValue,
                    new(long.MaxValue, long.MaxValue, int.MaxValue, int.MaxValue)))
                .ToList(),
        };
        for (var index = 0; index < PerfWindowEvidence.MaximumEvents; index++)
        {
            capture.WindowEvidence.Add(new(
                PerfWindowEventTypes.ProbeFailed,
                long.MaxValue - index,
                long.MaxValue - index,
                uint.MaxValue,
                nameof(PerfWindowProbeOutcome.InvalidWindowHandle),
                int.MaxValue));
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            capture,
            PerfJsonContext.Default.PerfCaptureDocument);

        Assert.IsLessThan(240 * 1024, bytes.Length,
            "The bounded evidence must leave room below capture.json's 256-KiB limit.");
    }

    [TestMethod]
    public void CaptureWithoutWindowEvidenceLoadsAsNotRecorded()
    {
        var directory = Directory.CreateTempSubdirectory("WinApp-Perf-Window-Compat-");
        try
        {
            var capture = Capture();
            capture.Directory = directory.FullName;
            capture.Save();
            var path = Path.Join(directory.FullName, "capture.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.IsTrue(json.Remove("windowEvidence"));
            File.WriteAllText(path, json.ToJsonString());

            var loaded = PerfCaptureDocument.Load(directory.FullName);

            Assert.IsEmpty(loaded.WindowEvidence.Events);
            Assert.IsTrue(loaded.WindowEvidence.IsValid());
            Assert.IsFalse(loaded.WindowEvidenceRecorded);
            Assert.AreEqual(
                "not-recorded",
                PerfWindowExperienceBuilder.Create(loaded, new(0, 1_500)).Status);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void ResponseProbeDistinguishesTimeoutAndOtherWin32Failures()
    {
        AssertProbe(1460, PerfWindowProbeOutcome.Timeout);
        AssertProbe(1400, PerfWindowProbeOutcome.InvalidWindowHandle);
        AssertProbe(5, PerfWindowProbeOutcome.AccessDenied);
        AssertProbe(0, PerfWindowProbeOutcome.Win32Failure);
        AssertProbe(87, PerfWindowProbeOutcome.Win32Failure);
    }

    private static PerfWindowSnapshot Window(
        long handle,
        bool visible,
        PerfWindowProbeOutcome? outcome = null) =>
        new(
            handle,
            73,
            visible,
            outcome is null
                ? null
                : new(outcome.Value, outcome == PerfWindowProbeOutcome.Responsive ? null :
                    outcome == PerfWindowProbeOutcome.Timeout ? 1460 :
                    outcome == PerfWindowProbeOutcome.InvalidWindowHandle ? 1400 :
                    outcome == PerfWindowProbeOutcome.AccessDenied ? 5 : 87));

    private static PerfWindowEvent Event(string type, long qpc) =>
        new(type, qpc, 7, 73);

    private static PerfCaptureDocument Capture() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Directory = @"C:\unused",
        SessionId = Guid.NewGuid(),
        SessionName = "WinApp-Perf-Test",
        ReadyQpc = 1_000,
        StopQpc = 2_500,
        Frequency = 1_000,
    };

    private static void AssertProbe(int error, PerfWindowProbeOutcome expected)
    {
        var result = new PerfWindowResponseProbe(
            new FakeWindowMessageNative(new(false, error))).Probe(7);
        Assert.AreEqual(expected, result.Outcome);
        Assert.AreEqual(error, result.Win32Error);
    }

    private sealed class FakeWindowSource : IPerfWindowSnapshotSource
    {
        private readonly Queue<IReadOnlyList<PerfWindowSnapshot>> values = [];

        public void Enqueue(IReadOnlyList<PerfWindowSnapshot> value) => values.Enqueue(value);

        public IReadOnlyList<PerfWindowSnapshot> Snapshot(int processId)
        {
            Assert.AreEqual(42, processId);
            return values.Dequeue();
        }
    }

    private sealed class FakeWindowMessageNative(
        PerfWindowMessageResult result) : IPerfWindowMessageNative
    {
        public PerfWindowMessageResult SendNullMessage(long windowHandle) => result;
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class ResourceSamplerTests
{
    [TestMethod]
    public void TrySample_DerivesRatesOnlyFromSameGenerationBaseline()
    {
        var process = new ProcessIdentity(42, 100);
        var clock = new FakePerformanceClock(1_000, 1_000, 1_500);
        var sampler = new ResourceSampler(clock, TimeSpan.FromMilliseconds(500), 4);

        var first = sampler.TrySample([Counters(process, cpuTicks: 10_000_000, readBytes: 100, writeBytes: 200)]);
        var second = sampler.TrySample([Counters(process, cpuTicks: 12_500_000, readBytes: 600, writeBytes: 1_200)]);

        Assert.IsNotNull(first);
        Assert.IsNull(first.Processes[0].CpuCoresUsed);
        Assert.IsNull(first.Processes[0].ReadBytesPerSecond);
        Assert.AreEqual(0, first.IntervalMs);
        Assert.IsNotNull(second);
        Assert.AreEqual(0.5, second.IntervalMs / 1_000);
        Assert.AreEqual(0.5, second.Processes[0].CpuCoresUsed);
        Assert.AreEqual(12.5, second.Processes[0].CpuPercentOfMachine);
        Assert.AreEqual(1_000, second.Processes[0].ReadBytesPerSecond);
        Assert.AreEqual(2_000, second.Processes[0].WriteBytesPerSecond);
    }

    [TestMethod]
    public void TrySample_SkipsBeforeCadenceWithoutReplacingBaseline()
    {
        var process = new ProcessIdentity(42, 100);
        var clock = new FakePerformanceClock(1_000, 1_000, 1_250, 1_500);
        var sampler = new ResourceSampler(clock, TimeSpan.FromMilliseconds(500), 1);

        Assert.IsNotNull(sampler.TrySample([Counters(process, cpuTicks: 0, readBytes: 0, writeBytes: 0)]));
        Assert.IsNull(sampler.TrySample([Counters(process, cpuTicks: 9_000_000, readBytes: 900, writeBytes: 900)]));
        var sample = sampler.TrySample([Counters(process, cpuTicks: 5_000_000, readBytes: 500, writeBytes: 1_000)]);

        Assert.IsNotNull(sample);
        Assert.AreEqual(1, sample.Processes[0].CpuCoresUsed);
        Assert.AreEqual(1_000, sample.Processes[0].ReadBytesPerSecond);
        Assert.AreEqual(2_000, sample.Processes[0].WriteBytesPerSecond);
    }

    [TestMethod]
    public void TrySample_NewGenerationAndDynamicProcessStartWithOwnBaselines()
    {
        var oldGeneration = new ProcessIdentity(42, 100);
        var newGeneration = new ProcessIdentity(42, 200);
        var helper = new ProcessIdentity(84, 300);
        var clock = new FakePerformanceClock(1_000, 1_000, 1_500);
        var sampler = new ResourceSampler(clock, TimeSpan.FromMilliseconds(500), 4);

        sampler.TrySample([Counters(oldGeneration, cpuTicks: 10_000_000, readBytes: 100, writeBytes: 100)]);
        var sample = sampler.TrySample(
        [
            Counters(newGeneration, cpuTicks: 20_000_000, readBytes: 2_000, writeBytes: 2_000),
            Counters(helper, cpuTicks: 30_000_000, readBytes: 3_000, writeBytes: 3_000),
        ]);

        Assert.IsNotNull(sample);
        Assert.HasCount(2, sample.Processes);
        Assert.IsTrue(sample.Processes.All(process => process.CpuCoresUsed is null));
        Assert.IsNull(sample.Aggregate.CpuCoresUsed);
    }

    [TestMethod]
    public void TrySample_MissingProcessFieldMakesOnlyThatAggregateIncomplete()
    {
        var complete = Counters(
            new ProcessIdentity(42, 100),
            cpuTicks: 10_000_000,
            readBytes: 100,
            writeBytes: 200);
        var incomplete = Counters(
            new ProcessIdentity(84, 200),
            cpuTicks: 20_000_000,
            readBytes: 300,
            writeBytes: 400) with
        {
            PrivateBytes = null,
        };
        var sampler = new ResourceSampler(
            new FakePerformanceClock(1_000, 1_000),
            TimeSpan.FromMilliseconds(500),
            4);

        var sample = sampler.TrySample([complete, incomplete]);

        Assert.IsNotNull(sample);
        Assert.AreEqual(1, sample.PartialProcessCount);
        Assert.IsNull(sample.Aggregate.PrivateBytes);
        Assert.AreEqual(6_000L, sample.Aggregate.WorkingSetBytes);
        Assert.AreEqual(400UL, sample.Aggregate.ReadBytes);
    }

    [TestMethod]
    public void TrySample_TerminalCountersBypassCadenceAndKeepExpectedCoverage()
    {
        var process = new ProcessIdentity(42, 100);
        var sampler = new ResourceSampler(
            new FakePerformanceClock(1_000, 1_000, 1_250),
            TimeSpan.FromMilliseconds(500),
            1);
        sampler.TrySample([Counters(process, cpuTicks: 0, readBytes: 0, writeBytes: 0)]);

        var terminal = sampler.TrySample(
        [
            Counters(process, cpuTicks: 2_500_000, readBytes: 64, writeBytes: 128) with
            {
                IsTerminal = true,
                PrivateBytes = null,
                WorkingSetBytes = null,
                ThreadCount = null,
                HandleCount = null,
                GdiObjectCount = null,
                UserObjectCount = null,
            },
        ]);

        Assert.IsNotNull(terminal);
        Assert.IsTrue(terminal.IsTerminal);
        Assert.AreEqual(250, terminal.IntervalMs);
        Assert.AreEqual(1, terminal.Processes[0].CpuCoresUsed);
        Assert.IsTrue(terminal.Processes[0].IsTerminal);
        Assert.IsFalse(terminal.Processes[0].IsPartial);
        Assert.AreEqual(0, terminal.PartialProcessCount);
    }

    [TestMethod]
    public void TrySample_DerivesGenerationSafePerThreadCpuAndRetainsState()
    {
        var process = new ProcessIdentity(42, 100);
        var firstThread = new ThreadResourceCounters(
            7,
            200,
            10_000_000,
            8_000_000,
            2_000_000,
            "Running",
            null);
        var secondThread = firstThread with
        {
            TotalProcessorTimeTicks = 12_500_000,
            UserProcessorTimeTicks = 10_000_000,
            KernelProcessorTimeTicks = 2_500_000,
            State = "Wait",
            WaitReason = "UserRequest",
        };
        var sampler = new ResourceSampler(
            new FakePerformanceClock(1_000, 1_000, 1_500),
            TimeSpan.FromMilliseconds(500),
            4);

        var first = sampler.TrySample(
            [Counters(process, 10_000_000, 0, 0) with { Threads = [firstThread] }]);
        var second = sampler.TrySample(
            [Counters(process, 12_500_000, 0, 0) with { Threads = [secondThread] }]);

        Assert.IsNotNull(first);
        Assert.AreEqual("complete", first.Processes[0].ThreadCaptureStatus);
        Assert.HasCount(1, first.Processes[0].Threads);
        Assert.IsNull(first.Processes[0].Threads[0].CpuCoresUsed);
        Assert.IsNotNull(second);
        var sample = second.Processes[0].Threads[0];
        Assert.AreEqual(0.5, sample.CpuCoresUsed);
        Assert.AreEqual(12.5, sample.CpuPercentOfMachine);
        Assert.AreEqual("Wait", sample.State);
        Assert.AreEqual("UserRequest", sample.WaitReason);
        Assert.IsFalse(sample.IsPartial);
    }

    [TestMethod]
    public void TrySample_DoesNotDeriveCpuAcrossReusedThreadId()
    {
        var process = new ProcessIdentity(42, 100);
        var sampler = new ResourceSampler(
            new FakePerformanceClock(1_000, 1_000, 1_500),
            TimeSpan.FromMilliseconds(500),
            1);
        var first = new ThreadResourceCounters(
            7,
            200,
            10_000_000,
            10_000_000,
            0,
            "Running",
            null);
        var reused = first with
        {
            ThreadStartTimeUtcTicks = 300,
            TotalProcessorTimeTicks = 1_000_000,
            UserProcessorTimeTicks = 1_000_000,
        };

        sampler.TrySample([Counters(process, 10_000_000, 0, 0) with { Threads = [first] }]);
        var sample = sampler.TrySample(
            [Counters(process, 11_000_000, 0, 0) with { Threads = [reused] }]);

        Assert.IsNotNull(sample);
        Assert.IsNull(sample.Processes[0].Threads[0].CpuCoresUsed);
    }

    private static ProcessResourceCounters Counters(
        ProcessIdentity process,
        long cpuTicks,
        ulong readBytes,
        ulong writeBytes) => new(
            process,
            TotalProcessorTimeTicks: cpuTicks,
            UserProcessorTimeTicks: cpuTicks,
            KernelProcessorTimeTicks: 0,
            PrivateBytes: 1_000,
            WorkingSetBytes: 3_000,
            ReadOperationCount: 1,
            WriteOperationCount: 2,
            OtherOperationCount: 3,
            ReadBytes: readBytes,
            WriteBytes: writeBytes,
            OtherBytes: 0,
            ThreadCount: 4,
            HandleCount: 5,
            GdiObjectCount: 6,
            UserObjectCount: 7);

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
}

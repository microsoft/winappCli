// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class PerformanceBundleWriterTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Join(Path.GetTempPath(), $"perf-bundle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public void Complete_WritesOneLinePerEventAndPublishesManifestLast()
    {
        var output = Path.Join(_root, "capture.winappperf");
        var calibration = new PerformanceClockCalibration(
            new PerformanceTimestamp(1_000),
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            1_000,
            TimeSpan.FromMilliseconds(0.25));
        using var writer = new PerformanceBundleWriter(output, calibration);
        var managedPaths = writer.CreateManagedPaths();
        Directory.CreateDirectory(Path.GetDirectoryName(managedPaths.TracePath)!);
        File.WriteAllBytes(managedPaths.TracePath, [1, 2, 3, 4]);
        writer.Write(
        [
            new(StartupEventType.ActivationRequested, new PerformanceTimestamp(1_100), TimeSpan.Zero),
            new(
                StartupEventType.ProcessObserved,
                new PerformanceTimestamp(1_250),
                TimeSpan.FromMilliseconds(150),
                new ProcessIdentity(42, 1234)),
        ]);
        writer.Write(ResourceSampleAt(
            counter: 1_250,
            intervalMs: 0,
            cpuCores: null,
            privateBytes: 1_000,
            readBytes: 100,
            writeBytes: 200));
        writer.Write(ResourceSampleAt(
            counter: 1_750,
            intervalMs: 500,
            cpuCores: 0.5,
            privateBytes: 1_500,
            readBytes: 600,
            writeBytes: 1_200));
        writer.Write(ResourceSampleAt(
            counter: 1_900,
            intervalMs: 150,
            cpuCores: 0.25,
            privateBytes: null,
            readBytes: 900,
            writeBytes: 1_700,
            isTerminal: true));

        var result = writer.Complete(
            "partial",
            "duration",
            StartupLaunchDisposition.Pending,
            42,
            new ResponseProbeManifest
            {
                CadenceMs = 250,
                TimeoutMs = 100,
                Method = "SendMessageTimeout(WM_NULL)",
            },
            new WprCollectorResult
            {
                Requested = false,
                Status = "not-requested",
                Profile = "FileIO.Verbose",
                Coverage = "not-requested",
                LossStatus = "not-applicable",
            },
            RecordedManagedTrace());

        Assert.AreEqual(output, result.Bundle);
        Assert.IsTrue(File.Exists(Path.Join(output, "manifest.json")));
        var lines = File.ReadAllLines(Path.Join(output, "timeline.ndjson"));
        Assert.HasCount(5, lines);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            Assert.IsNotNull(document.RootElement.GetProperty("type").GetString());
        }
        using (var firstEvent = JsonDocument.Parse(lines[0]))
        {
            Assert.AreEqual(0, firstEvent.RootElement.GetProperty("elapsedMs").GetDouble());
        }
        using (var resource = JsonDocument.Parse(lines[2]))
        {
            Assert.AreEqual("ResourceSample", resource.RootElement.GetProperty("type").GetString());
            Assert.AreEqual(150, resource.RootElement.GetProperty("elapsedMs").GetDouble());
            Assert.AreEqual(1_000, resource.RootElement
                .GetProperty("aggregate")
                .GetProperty("privateBytes")
                .GetInt64());
        }

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Join(output, "manifest.json")));
        Assert.AreEqual(5, manifest.RootElement.GetProperty("eventCount").GetInt32());
        Assert.AreEqual("partial", manifest.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(
            150,
            manifest.RootElement.GetProperty("startup").GetProperty("firstProcessMs").GetDouble());
        Assert.AreEqual(
            250,
            manifest.RootElement.GetProperty("responseProbe").GetProperty("cadenceMs").GetDouble());
        Assert.AreEqual(
            "not-requested",
            manifest.RootElement.GetProperty("wpr").GetProperty("status").GetString());
        Assert.AreEqual("0.2", manifest.RootElement.GetProperty("schemaVersion").GetString());
        Assert.AreEqual(
            "recorded",
            manifest.RootElement
                .GetProperty("managed")
                .GetProperty("dotNetTrace")
                .GetProperty("status")
                .GetString());
        var artifacts = manifest.RootElement.GetProperty("artifacts");
        Assert.AreEqual(1, artifacts.GetArrayLength());
        Assert.AreEqual(
            "traces/managed.nettrace",
            artifacts[0].GetProperty("path").GetString());
        var resources = manifest.RootElement.GetProperty("resources");
        Assert.AreEqual(3, resources.GetProperty("sampleCount").GetInt32());
        Assert.AreEqual(500, resources.GetProperty("averageIntervalMs").GetDouble());
        Assert.AreEqual(1, resources.GetProperty("terminalSampleCount").GetInt32());
        Assert.AreEqual(1, resources.GetProperty("processGenerationCount").GetInt32());
        var summary = resources.GetProperty("summary");
        Assert.AreEqual(0.375, summary.GetProperty("averageCpuCoresUsed").GetDouble());
        Assert.AreEqual(1_500, summary.GetProperty("peakPrivateBytes").GetInt64());
        Assert.AreEqual(500, summary.GetProperty("privateBytesChange").GetInt64());
        Assert.AreEqual(800UL, summary.GetProperty("readBytesDuringRecording").GetUInt64());
        Assert.AreEqual(1_500UL, summary.GetProperty("writeBytesDuringRecording").GetUInt64());
    }

    private static ManagedCollectorsResult RecordedManagedTrace() => new()
    {
        DotNetTrace = new()
        {
            Requested = true,
            Tool = "dotnet-trace",
            Status = "recorded",
            Coverage = "attached-after-activation",
            Artifact = "traces/managed.nettrace",
            FileSize = 4,
            RecommendedViewer = "PerfView or Visual Studio",
            LossStatus = "not-inspected",
        },
        DotNetCounters = new()
        {
            Requested = false,
            Tool = "dotnet-counters",
            Status = "not-requested",
            Coverage = "not-requested",
            LossStatus = "not-applicable",
        },
    };

    [TestMethod]
    public void Constructor_DoesNotOverwriteExistingBundle()
    {
        var output = Path.Join(_root, "capture.winappperf");
        Directory.CreateDirectory(output);

        Assert.ThrowsExactly<IOException>(() => new PerformanceBundleWriter(
            output,
            new PerformanceClock().Calibrate()));
    }

    [TestMethod]
    public void DisposeBeforeComplete_RemovesOnlyPrivateStagingDirectory()
    {
        var output = Path.Join(_root, "capture.winappperf");
        using (new PerformanceBundleWriter(output, new PerformanceClock().Calibrate()))
        {
        }

        Assert.IsFalse(Directory.Exists(output));
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(_root));
    }

    private static ResourceSample ResourceSampleAt(
        long counter,
        double intervalMs,
        double? cpuCores,
        long? privateBytes,
        ulong readBytes,
        ulong writeBytes,
        bool isTerminal = false)
    {
        var process = new ProcessResourceSample
        {
            ProcessId = 42,
            ProcessStartTimeUtcTicks = 1234,
            TotalProcessorTimeMs = 10,
            UserProcessorTimeMs = 8,
            KernelProcessorTimeMs = 2,
            CpuCoresUsed = cpuCores,
            CpuPercentOfMachine = cpuCores * 25,
            PrivateBytes = privateBytes,
            WorkingSetBytes = privateBytes + 1_000,
            ReadOperationCount = 1,
            WriteOperationCount = 2,
            OtherOperationCount = 3,
            ReadBytes = readBytes,
            WriteBytes = writeBytes,
            OtherBytes = 0,
            ReadBytesPerSecond = cpuCores is null ? null : 1_000,
            WriteBytesPerSecond = cpuCores is null ? null : 2_000,
            ThreadCount = 4,
            HandleCount = 5,
            GdiObjectCount = 6,
            UserObjectCount = 7,
            IsTerminal = isTerminal,
            IsPartial = false,
        };
        return new()
        {
            Timestamp = new PerformanceTimestamp(counter),
            IntervalMs = intervalMs,
            OwnedProcessCount = 1,
            PartialProcessCount = 0,
            IsTerminal = isTerminal,
            Processes = [process],
            Aggregate = new()
            {
                CpuCoresUsed = process.CpuCoresUsed,
                CpuPercentOfMachine = process.CpuPercentOfMachine,
                PrivateBytes = process.PrivateBytes,
                WorkingSetBytes = process.WorkingSetBytes,
                ReadBytes = process.ReadBytes,
                WriteBytes = process.WriteBytes,
                ReadBytesPerSecond = process.ReadBytesPerSecond,
                WriteBytesPerSecond = process.WriteBytesPerSecond,
                ThreadCount = process.ThreadCount,
                HandleCount = process.HandleCount,
                GdiObjectCount = process.GdiObjectCount,
                UserObjectCount = process.UserObjectCount,
            },
        };
    }
}

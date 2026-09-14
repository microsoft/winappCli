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
        writer.Write(
        [
            new(StartupEventType.ActivationRequested, new PerformanceTimestamp(1_100), TimeSpan.Zero),
            new(
                StartupEventType.ProcessObserved,
                new PerformanceTimestamp(1_250),
                TimeSpan.FromMilliseconds(150),
                new ProcessIdentity(42, 1234)),
        ]);

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
            });

        Assert.AreEqual(output, result.Bundle);
        Assert.IsTrue(File.Exists(Path.Join(output, "manifest.json")));
        var lines = File.ReadAllLines(Path.Join(output, "timeline.ndjson"));
        Assert.HasCount(2, lines);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            Assert.IsNotNull(document.RootElement.GetProperty("type").GetString());
        }
        using (var firstEvent = JsonDocument.Parse(lines[0]))
        {
            Assert.AreEqual(0, firstEvent.RootElement.GetProperty("elapsedMs").GetDouble());
        }

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Join(output, "manifest.json")));
        Assert.AreEqual(2, manifest.RootElement.GetProperty("eventCount").GetInt32());
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
    }

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
}

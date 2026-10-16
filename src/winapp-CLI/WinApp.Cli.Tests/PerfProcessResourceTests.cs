// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;
using WinApp.Cli.Commands;
using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class PerfProcessResourceTests
{
    private static PerfCaptureDocument Capture() => new()
    {
        Id = Guid.NewGuid().ToString("N"), Directory = @"C:\unused", SessionName = "test",
        Target = new(Environment.ProcessId, DateTime.UtcNow), State = "completed",
        ReadyQpc = 1000, StopQpc = 4000, Frequency = 1000,
        ProcessResources = new()
        {
            Samples =
            [
                new(1000, 100, 50, new(10, 20, 2, 5)),
                new(2000, 140, 70, new(30, 40, 8, 9)),
                new(3000, 160, 90, new(15, 25, 4, 6)),
                new(4000, 170, 95, new(12, 22, 3, 5)),
            ],
        },
        Markers = [new("start", 2000), new("end", 3000)],
    };

    [TestMethod]
    public void ResourcesSummarizeCpuDeltasAndFirstLastAndObservedMaxima()
    {
        var result = PerfQuery.ExecuteResources(Capture(), new(View: "resources"));
        var summary = result.ProcessResources!;
        Assert.AreEqual(4, summary.SampleCount);
        Assert.AreEqual(70, summary.UserCpuMs);
        Assert.AreEqual(45, summary.KernelCpuMs);
        Assert.AreEqual(115, summary.TotalCpuMs);
        Assert.AreEqual(new PerfResourceValues(10, 20, 2, 5), summary.First);
        Assert.AreEqual(new PerfResourceValues(12, 22, 3, 5), summary.Last);
        Assert.AreEqual(new PerfResourceValues(30, 40, 8, 9), summary.MaximumObserved);
        Assert.AreEqual(0, summary.FirstSampleMs);
        Assert.AreEqual(3000, summary.LastSampleMs);
        Assert.IsTrue(result.Coverage.Complete, "Resource queries do not require ETL or WinUI coverage.");
    }

    [TestMethod]
    public void MarkerRangeAndPagingRetainRangeWideSummaryWithoutInterpolation()
    {
        var result = PerfQuery.ExecuteResources(Capture(), new(View: "resources", Limit: 1,
            FromMarker: "start", ToMarker: "end"));
        Assert.AreEqual(2, result.Total);
        Assert.AreEqual(1, result.NextOffset);
        Assert.AreEqual(1000, result.Rows.Single().TimeMs);
        Assert.AreEqual(40, result.ProcessResources!.TotalCpuMs);
        var second = PerfQuery.ExecuteResources(Capture(), new(View: "resources", Limit: 1, Offset: 1,
            FromMarker: "start", ToMarker: "end"));
        Assert.AreEqual(2000, second.Rows.Single().TimeMs);
        Assert.IsNull(second.NextOffset);
        Assert.AreEqual(result.ProcessResources, second.ProcessResources);
        var single = PerfQuery.ExecuteResources(Capture(), new(View: "resources", FromMs: 500, ToMs: 1500));
        Assert.AreEqual(1, single.ProcessResources!.SampleCount);
        Assert.IsNull(single.ProcessResources.TotalCpuMs, "One sample cannot establish interval CPU consumption.");
        Assert.AreEqual(1000, single.ProcessResources.FirstSampleMs);
    }

    [TestMethod]
    public void EmptyRangeIsNotZeroCpuAndOldCapturesDoNotInventCounters()
    {
        var capture = Capture();
        var empty = PerfQuery.ExecuteResources(capture, new(View: "resources", FromMs: 500, ToMs: 600));
        Assert.IsEmpty(empty.Rows);
        Assert.AreEqual("not-observed", empty.ProcessResources!.Availability);
        Assert.IsNull(empty.ProcessResources.TotalCpuMs);
        capture.ProcessResources = null;
        Assert.AreEqual("not-recorded", PerfResourceSummary.Create(capture, new(0, 3000)).Availability);
        Assert.Throws<InvalidDataException>(() => PerfQuery.ExecuteResources(capture, new(View: "resources")));
    }

    [TestMethod]
    public void SamplingFailuresAreExplicitPartialEvidenceButEtwLossDoesNotInvalidateCounters()
    {
        var capture = Capture();
        capture.EventsLost = 99;
        Assert.IsTrue(PerfQuery.ExecuteResources(capture, new(View: "resources")).Coverage.Complete);
        capture.ProcessResources!.FailedSamples = 1;
        capture.ProcessResources.LastError = "Access denied";
        var result = PerfQuery.ExecuteResources(capture, new(View: "resources"));
        Assert.IsFalse(result.Coverage.Complete);
        StringAssert.Contains(result.Coverage.Reasons.Single(), "Access denied");
        Assert.AreEqual(4, result.Returned);
    }

    [TestMethod]
    public void ResourceTimelineRespectsExactJsonByteBudget()
    {
        var capture = Capture();
        capture.ProcessResources!.Samples = Enumerable.Range(0, 100)
            .Select(i => new PerfResourceSample(1000 + i * 20, i, i, new(100, 200, 3, 4))).ToList();
        var result = PerfQuery.ExecuteResources(capture, new(View: "resources", Limit: 100, MaxBytes: 4096));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, PerfJsonContext.Default.PerfQueryResult).Length +
            System.Text.Encoding.UTF8.GetByteCount(Environment.NewLine);
        Assert.IsLessThanOrEqualTo(4096, bytes);
        Assert.IsTrue(result.ByteBudgetLimited);
        Assert.AreEqual(result.Returned, result.NextOffset);
        Assert.AreEqual(100, result.ProcessResources!.SampleCount);
    }

    [TestMethod]
    [DataRow("--thread")]
    [DataRow("--id")]
    [DataRow("--sort")]
    public void ResourceViewRejectsInapplicableFilters(string option)
    {
        var options = option switch
        {
            "--thread" => new PerfQueryOptions(View: "resources", Thread: 1),
            "--id" => new PerfQueryOptions(View: "resources", Id: "c1"),
            _ => new PerfQueryOptions(View: "resources", Sort: "duration"),
        };
        Assert.Throws<ArgumentException>(() => PerfQuery.Validate(options));
    }

    [TestMethod]
    public void InvalidSamplesAndOversizedTimelinesAreRejected()
    {
        var resources = Capture().ProcessResources!;
        resources.Samples.Reverse();
        Assert.Throws<InvalidDataException>(resources.Validate);
        resources.Samples = [new(1, double.NaN, 0, new(1, 1, 1, 1))];
        Assert.Throws<InvalidDataException>(resources.Validate);
        resources.Samples = Enumerable.Repeat(new PerfResourceSample(1, 0, 0, new(1, 1, 1, 1)),
            PerfProcessResources.MaximumSamples + 1).ToList();
        Assert.Throws<InvalidDataException>(resources.Validate);
    }

    [TestMethod]
    public void NativeProcessCountersAreReadableAndCpuIsCumulative()
    {
        using var process = Process.GetCurrentProcess();
        var first = PerfProcessResources.Read(process);
        var until = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 20;
        while (Stopwatch.GetTimestamp() < until)
        {
            Thread.SpinWait(1000);
        }
        var last = PerfProcessResources.Read(process);
        Assert.IsGreaterThan(0L, last.Values.PrivateBytes);
        Assert.IsGreaterThan(0L, last.Values.WorkingSetBytes);
        Assert.IsGreaterThan(0, last.Values.ThreadCount);
        Assert.IsGreaterThan(0, last.Values.HandleCount);
        Assert.IsGreaterThan(first.UserCpuMs + first.KernelCpuMs, last.UserCpuMs + last.KernelCpuMs);
        new PerfProcessResources { Samples = [first, last] }.Validate();
    }

    [TestMethod]
    public void ResourceDocumentRoundTripsAndSummaryTextDistinguishesCpuFromElapsedTime()
    {
        var directory = Directory.CreateTempSubdirectory("WinApp-Resources-");
        try
        {
            var capture = Capture();
            capture.Directory = directory.FullName;
            capture.Save();
            var loaded = PerfCaptureDocument.Load(directory.FullName);
            Assert.AreEqual(capture.ProcessResources!.Samples[1], loaded.ProcessResources!.Samples[1]);
            using var console = new Spectre.Console.Testing.TestConsole();
            PerfCommand.PrintResources(console, PerfResourceSummary.Create(loaded, new(0, 3000)));
            StringAssert.Contains(console.Output, "CPU: 115");
            StringAssert.Contains(console.Output, "Observed max");
            StringAssert.Contains(console.Output, "not the entire startup");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}

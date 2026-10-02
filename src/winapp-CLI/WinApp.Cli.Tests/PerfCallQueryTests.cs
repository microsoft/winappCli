// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Tests;

[TestClass]
public class PerfCallQueryTests
{
    [TestMethod]
    public void TreeUsesExecutionParentsAndStablePagingWithAncestorContext()
    {
        using var fixture = new Fixture([
            Call("c1", 0, 40), Call("c2", 2, 30, "c1"),
            Call("c3", 5, 25, "c2"), Call("c4", 6, 20, "c3"),
            Call("c5", 30, 40, "c1"),
        ]);
        var result = fixture.Query(new(View: "call", Id: "c1", Limit: 1, Offset: 2));
        Assert.AreEqual("c1", result.RootCallId);
        Assert.AreEqual(new PerfRange(0, 40), result.Range);
        Assert.AreEqual("c3", result.Rows.Single().Id);
        Assert.AreEqual("c1,c2", string.Join(',', result.Rows[0].AncestorIds));
        Assert.IsTrue(result.Rows[0].DepthLimited);
        Assert.AreEqual(1, result.Rows[0].ChildCount);
        Assert.AreEqual(3, result.NextOffset);
        var full = fixture.Query(new(View: "call", Id: "c1", Depth: 3));
        Assert.AreEqual("c1,c2,c3,c4,c5", string.Join(',', full.Rows.Select(r => r.Id)));
    }

    [TestMethod]
    public void CallsAreLongestFirstAndFamilyFiltered()
    {
        using var fixture = new Fixture([
            Call("c1", 0, 10), Call("c2", 20, 50), Call("c3", 0, 100) with { Family = "frames" },
        ]);
        var result = fixture.Query(new(View: "calls", Family: "layout"));
        Assert.AreEqual("c2,c1", string.Join(',', result.Rows.Select(r => r.Id)));
    }

    [TestMethod]
    public void SummaryPartitionsPrimaryUiThreadWithoutDoubleCounting()
    {
        var parse = Call("parse", 25, 30, "layout") with
        {
            Name = "ParseXaml", Family = "parsing", BeginEvent = "parse-begin",
        };
        var calls = new[]
        {
            Frame("frame", 0, 100) with { Thread = 7 },
            Frame("other-frame", 0, 40) with { Thread = 8 },
            Call("callback", 10, 60, "frame") with { Name = "EventCallback", Family = "input", Thread = 7 },
            Call("layout", 20, 40, "callback") with { Thread = 7 },
            parse with { Thread = 7 },
            Call("image", 40, 50, "callback") with { Name = "ImageCacheDecode", Family = "images", Thread = 7 },
            Call("off-thread-image", 0, 90) with { Name = "OffThreadDecode", Family = "images", Thread = 8 },
            Call("render", 60, 80, "frame") with { Name = "RenderWalk", Family = "frames", Thread = 7 },
            Call("framework", 80, 90, "frame") with { Name = "PublicApiCall:SetValue", Family = "framework", Thread = 7 },
        };
        var parseEvent = PerfGcTests.Event("ParseXaml", 25, thread: 7) with
        {
            Id = "parse-begin", Provider = PerfProviders.Xaml, Family = "parsing", Phase = "begin",
            Fields = new() { ["URI"] = "Views/MainPage.xaml" },
        };
        using var fixture = new Fixture(calls, [parseEvent]);

        var result = fixture.Query(new(View: "summary", Limit: 100));

        Assert.AreEqual(7u, result.PrimaryUiThread);
        Assert.AreEqual(100d, result.Activity!.Sum(category => category.ObservedMs), 0.000001);
        AssertActivity(result, "Parsing", 5);
        AssertActivity(result, "Layout", 15);
        AssertActivity(result, "Render", 20);
        AssertActivity(result, "App callbacks", 20);
        AssertActivity(result, "Other observed XAML", 10);
        AssertActivity(result, "Image decode/load", 10);
        AssertActivity(result, "Unclassified", 20);
        Assert.AreEqual("Views/MainPage.xaml", result.ParsingResources!.Single().Resource);
        Assert.AreEqual(5d, result.ParsingResources!.Single().ObservedMs);
        Assert.IsTrue(result.Rows.Any(row => row.Id == "phase:OffThreadDecode"),
            "Automatic overview thread selection must not narrow the existing detailed summary.");
    }

    [TestMethod]
    public void FrameCountSelectsPrimaryThreadBeforeFrameUnionAndThreadOverrideWins()
    {
        var calls = new[]
        {
            Frame("long", 0, 80) with { Thread = 20 },
            Frame("first", 0, 10) with { Thread = 10 },
            Frame("second", 20, 30) with { Thread = 10 },
            Call("thread-10-layout", 0, 30, "first") with { Thread = 10 },
            Call("thread-20-layout", 0, 80, "long") with { Thread = 20 },
        };
        using var fixture = new Fixture(calls);

        Assert.AreEqual(10u, fixture.Query(new(View: "summary")).PrimaryUiThread);
        Assert.AreEqual(20u, fixture.Query(new(View: "summary", Thread: 20)).PrimaryUiThread);
        Assert.Throws<ArgumentException>(() => fixture.Query(new(View: "summary", Thread: 99)));
    }

    [TestMethod]
    public void ImageOnlyWorkerCannotBecomeAutomaticPrimaryUiThread()
    {
        var calls = new[]
        {
            Call("ui-layout", 0, 10) with { Thread = 7 },
            Call("worker-image", 0, 90) with { Name = "OffThreadDecode", Family = "images", Thread = 42 },
        };
        using var fixture = new Fixture(calls);

        var result = fixture.Query(new(View: "summary"));

        Assert.AreEqual(7u, result.PrimaryUiThread);
        AssertActivity(result, "Image decode/load", 0);
    }

    [TestMethod]
    public void ParsingPreviewIsBoundedAndParsingViewPagesAllResources()
    {
        var calls = new List<PerfCall>();
        var events = new List<PerfEvent>();
        for (var index = 0; index < 12; index++)
        {
            var id = "parse-" + index;
            calls.Add(Call(id, index * 2, index * 2 + 1) with
            {
                Name = "ParseXaml", Family = "parsing", BeginEvent = id + "-begin",
            });
            events.Add(PerfGcTests.Event("ParseXaml", index * 2) with
            {
                Id = id + "-begin", Provider = PerfProviders.Xaml, Family = "parsing", Phase = "begin",
                Fields = new() { ["URI"] = $"Resource-{index:D2}.xaml" },
            });
        }
        using var fixture = new Fixture(calls, events.ToArray());

        var summary = fixture.Query(new(View: "summary", Limit: 100));
        var page = fixture.Query(new(View: "parsing", Limit: 5, Offset: 5));

        Assert.AreEqual(10, summary.ParsingResources!.Count);
        Assert.AreEqual(12, summary.ParsingResourcesTotal);
        Assert.AreEqual(2, summary.ParsingResourcesOmitted);
        Assert.AreEqual(12, page.Total);
        Assert.AreEqual(5, page.Returned);
        Assert.AreEqual(10, page.NextOffset);
        Assert.IsTrue(page.Rows.All(row => row.Kind == "parsing-resource"));
    }

    [TestMethod]
    public void NestedParsingScopesAreUnionedByResource()
    {
        var calls = new[]
        {
            Call("load", 0, 10) with
            {
                Name = "ApplicationLoadComponent", Family = "parsing", BeginEvent = "load-begin",
            },
            Call("parse", 2, 8, "load") with
            {
                Name = "ParseXaml", Family = "parsing", BeginEvent = "parse-begin",
            },
        };
        var events = new[]
        {
            PerfGcTests.Event("ApplicationLoadComponent", 0) with
            {
                Id = "load-begin", Provider = PerfProviders.Xaml, Family = "parsing", Phase = "begin",
                Fields = new() { ["ComponentName"] = "Views/MainPage.xaml" },
            },
            PerfGcTests.Event("ParseXaml", 2) with
            {
                Id = "parse-begin", Provider = PerfProviders.Xaml, Family = "parsing", Phase = "begin",
                Fields = new() { ["URI"] = "Views/MainPage.xaml" },
            },
        };
        using var fixture = new Fixture(calls, events);

        var result = fixture.Query(new(View: "summary"));

        AssertActivity(result, "Parsing", 10);
        Assert.AreEqual(10d, result.ParsingResources!.Single().ObservedMs);
        Assert.AreEqual(2, result.ParsingResources!.Single().Count);
    }

    [TestMethod]
    public void MarkersClipActivityAndParsingTogether()
    {
        var parse = Call("parse", 10, 30) with
        {
            Name = "ParseXaml", Family = "parsing", BeginEvent = "parse-begin",
        };
        var parseEvent = PerfGcTests.Event("ParseXaml", 10) with
        {
            Id = "parse-begin", Provider = PerfProviders.Xaml, Family = "parsing", Phase = "begin",
            Fields = new() { ["URI"] = "Views/MainPage.xaml" },
        };
        using var fixture = new Fixture([parse], [parseEvent]);
        fixture.Capture.Markers.Add(new("start", 15));
        fixture.Capture.Markers.Add(new("end", 25));

        var result = fixture.Query(new(View: "summary", FromMarker: "start", ToMarker: "end"));

        Assert.AreEqual(new PerfRange(15, 25), result.Range);
        AssertActivity(result, "Parsing", 10);
        Assert.AreEqual(10d, result.ParsingResources!.Single().ObservedMs);
    }

    [TestMethod]
    public void ZeroLengthSummaryHasNoPercentages()
    {
        using var fixture = new Fixture([Frame("frame", 0, 10), Call("layout", 0, 10, "frame")]);

        var result = fixture.Query(new(View: "summary", FromMs: 5, ToMs: 5));

        Assert.IsTrue(result.Activity!.All(category => category.ObservedMs == 0));
        Assert.IsTrue(result.Activity!.All(category => category.RangePercent is null));
    }

    [TestMethod]
    public void OutOfRangeAndOtherThreadErrorsRemainCaptureContextNotLocalErrors()
    {
        using var fixture = new Fixture([Call("c1", 10, 20)],
            [
                PerfGcTests.Event("unrelated", 30) with { Provider = PerfProviders.Xaml, Family = "layout", DecodeError = "outside" },
                PerfGcTests.Event("unrelated", 15, thread: 99) with { Provider = PerfProviders.Xaml, Family = "layout", DecodeError = "other thread" },
            ]);
        fixture.Manifest.DecodeErrors = 2;
        fixture.Manifest.IncompleteReasons.Add("Two captured decoding errors.");
        var result = fixture.Query(new(View: "call", Id: "c1"));
        Assert.IsTrue(result.Coverage.Complete);
        Assert.AreEqual(0, result.Coverage.DecodeErrors);
        Assert.AreEqual(2, result.Coverage.CaptureDecodeErrors);
        Assert.IsNotEmpty(result.Coverage.CaptureReasons!);
    }

    [TestMethod]
    public void AnEarlierMissingEndRemainsUncertainInALaterRange()
    {
        using var fixture = new Fixture([
            new("c1", "Layout", "layout", 1, null, null, "v1", null, -20, null, null, null, "missing-end"),
        ]);
        var result = fixture.Query(new(View: "calls", FromMs: 40, ToMs: 50));
        Assert.AreEqual(1, result.Returned);
        Assert.IsFalse(result.Coverage.Complete);
        Assert.IsNull(result.Rows[0].InclusiveMs);
    }

    [TestMethod]
    public void GcOverlapIsAUnionAndDoesNotChangeExclusiveTime()
    {
        var gc = new[]
        {
            Pause("gc1", 2, 8), Pause("gc2", 5, 12, instance: 2),
            Pause("gc3", 15, 20, reason: 5),
            new PerfGcInterval { Id = "gc4", Kind = "collection", StartMs = 0, EndMs = 35, CollectionType = "background" },
        };
        using var fixture = new Fixture([Call("c1", 0, 40)], [PerfGcTests.Event("GCStart", 0)], gc);
        var result = fixture.Query(new(View: "call", Id: "c1"));
        var row = result.Rows.Single();
        Assert.AreEqual(10d, row.GcOverlap!.ObservedOverlapMs);
        Assert.AreEqual(40d, row.ExclusiveMs);
        Assert.AreEqual("gc1,gc2", string.Join(',', row.GcOverlap.IntervalIds));
        Assert.IsTrue(result.GcCoverage!.Complete);
    }

    [TestMethod]
    public void NoClrRecordsAreNotAZeroPauseReport()
    {
        using var fixture = new Fixture([Call("c1", 0, 10)]);
        var result = fixture.Query(new(View: "call", Id: "c1"));
        Assert.AreEqual("not-recorded", result.GcCoverage!.Availability);
        Assert.IsNull(result.Rows[0].GcOverlap);
        Assert.IsTrue(result.Coverage.Complete);
        Assert.Throws<InvalidDataException>(() => fixture.Query(new(View: "gc")));
    }

    [TestMethod]
    public void GcOnlyCaptureCannotReportSuccessfulEmptyUiHotspots()
    {
        using var fixture = new Fixture([], [PerfGcTests.Event("GCStart", 1)]);
        fixture.Manifest.Families.Clear();
        fixture.Manifest.Families["gc"] = 1;

        Assert.Throws<InvalidDataException>(() => fixture.Query(new(View: "hotspots")));
    }

    [TestMethod]
    public void EventsViewReturnsUnsupportedEvidenceForDiagnosis()
    {
        var unsupported = PerfGcTests.Event("UnknownEvent", 10) with
        {
            Provider = PerfProviders.Xaml,
            Family = "unknown",
            DecodeError = "unsupported descriptor",
        };
        using var fixture = new Fixture([], [unsupported]);
        fixture.Manifest.Families.Clear();
        fixture.Manifest.Families["unknown"] = 1;
        fixture.Manifest.DecodeErrors = 1;

        var result = fixture.Query(new(View: "events"));

        Assert.AreEqual("v10UnknownEvent1", result.Rows.Single().Id);
        Assert.IsFalse(result.Coverage.Complete);
    }

    [TestMethod]
    public void KnownEventLossDoesNotCertifySpanningGcPauseOverlap()
    {
        var intervals = new List<PerfGcInterval>();
        var analyzer = new PerfGcAnalyzer(intervals.Add);
        analyzer.Accept(PerfGcTests.Event("GCSuspendEEBegin", 0, reason: 1));
        analyzer.Accept(PerfGcTests.Event("GCSuspendEEEnd", 3));
        analyzer.Accept(PerfGcTests.Event("GCRestartEEBegin", 97));
        analyzer.Accept(PerfGcTests.Event("GCRestartEEEnd", 100));
        analyzer.Complete();

        using var fixture = new Fixture(
            [Call("c1", 0, 100)],
            [PerfGcTests.Event("GCStart", 0)],
            [intervals.Single()]);
        fixture.Capture.EventsLost = 1;
        fixture.Manifest.CaptureReasons.Add("ETW loss is nonzero or unknown.");

        var result = fixture.Query(new(View: "call", Id: "c1"));

        Assert.IsNull(result.Rows.Single().GcOverlap,
            "A pause reconstructed across known event loss cannot be used as certified overlap.");
    }

    [TestMethod]
    public void PartialGcDoesNotInvalidateUsableXamlButGcQueryIsPartial()
    {
        using var fixture = new Fixture([Call("c1", 0, 10)],
            [PerfGcTests.Event("GCStart", 1)], [
                new PerfGcInterval { Id = "gc1", Kind = "suspension", StartMs = -1, Reason = 1, Status = "missing-end" },
            ]);
        var call = fixture.Query(new(View: "call", Id: "c1"));
        Assert.IsTrue(call.Coverage.Complete);
        Assert.IsFalse(call.GcCoverage!.Complete);
        var gc = fixture.Query(new(View: "gc"));
        Assert.IsFalse(gc.Coverage.Complete);
        Assert.AreEqual(1, gc.GcCoverage!.IncompleteIntervals);
    }

    [TestMethod]
    public void ReferencedGcIntervalCanBeQueriedDirectly()
    {
        using var fixture = new Fixture([Call("c1", 0, 40)], [PerfGcTests.Event("GCStart", 0)],
            [Pause("gc1", 2, 8), Pause("gc2", 20, 25)]);
        var result = fixture.Query(new(View: "gc", Id: "gc2"));
        Assert.AreEqual(new PerfRange(20, 25), result.Range);
        Assert.AreEqual("gc2", result.Rows.Single().Id);
        Assert.Throws<ArgumentException>(() => fixture.Query(new(View: "gc", Id: "unknown")));
    }

    [TestMethod]
    public void GcDurationSortIsLongestFirstWithIncompleteIntervalsLast()
    {
        var incomplete = new PerfGcInterval
        {
            Id = "gc0", Kind = "suspension", StartMs = 5, Reason = 1, Status = "missing-end",
        };
        using var fixture = new Fixture([Call("c1", 0, 40)], [PerfGcTests.Event("GCStart", 0)],
            [Pause("gc1", 0, 2), incomplete, Pause("gc3", 10, 20), Pause("gc2", 30, 40)]);

        var chronological = fixture.Query(new(View: "gc", Limit: 100));
        Assert.AreEqual("gc1,gc0,gc3,gc2", string.Join(',', chronological.Rows.Select(r => r.Id)));

        var duration = fixture.Query(new(View: "gc", Sort: "duration", Limit: 100));
        Assert.AreEqual("gc3,gc2,gc1,gc0", string.Join(',', duration.Rows.Select(r => r.Id)));
        Assert.IsNull(duration.Rows[^1].InclusiveMs);
    }

    [TestMethod]
    public void HotspotsReturnSlowFramesWithRankedDirectOperationsAndGcOverlap()
    {
        var calls = new[]
        {
            Frame("f1", 0, 20),
            Frame("f2", 30, 50),
            Frame("fast", 60, 70),
            Frame("phase", 0, 50) with { Name = "RenderWalk" },
            Call("layout", 1, 6, "f1"),
            Call("render", 6, 18, "f1") with { Name = "RenderWalk", Family = "frames" },
            Call("nested", 7, 17, "render") with { Name = "MeasureElement" },
            Call("submit", 18, 20, "f1") with { Name = "SubmitFrame", Family = "frames" },
            Call("layout2", 32, 35, "f2"),
        };
        using var fixture = new Fixture(calls, [PerfGcTests.Event("GCStart", 0)], [Pause("gc1", 2, 8)]);

        var result = fixture.Query(new(View: "hotspots", Limit: 100));
        Assert.AreEqual("f1,f2", string.Join(',', result.Rows.Select(r => r.Id)));
        Assert.AreEqual("render,layout,submit",
            string.Join(',', result.Rows[0].DominantOperations!.Select(operation => operation.Id)));
        Assert.IsFalse(result.Rows[0].DominantOperations!.Any(operation => operation.Id == "nested"));
        Assert.AreEqual(6d, result.Rows[0].GcOverlap!.ObservedOverlapMs);
        Assert.AreEqual(3, result.Rows[0].ChildCount);

        var secondPage = fixture.Query(new(View: "hotspots", Limit: 1, Offset: 1));
        Assert.AreEqual("f2", secondPage.Rows.Single().Id);
        Assert.IsNull(secondPage.NextOffset);

        var clipped = fixture.Query(new(View: "hotspots", FromMs: 10, ToMs: 15, MinFrameMs: 19));
        Assert.AreEqual("f1", clipped.Rows.Single().Id);
        Assert.AreEqual(5d, clipped.Rows.Single().ClippedOverlapMs);
    }

    [TestMethod]
    public void HotspotByteBudgetProjectsEmbeddedOperationsAndMakesProgress()
    {
        var calls = new List<PerfCall> { Frame("f1", 0, 40) };
        calls.AddRange(Enumerable.Range(0, 4).Select(i => Call("child" + i, i, 30 - i, "f1") with
        {
            Name = new string('\u0001', 10000),
        }));
        using var fixture = new Fixture(calls);

        var result = fixture.Query(new(View: "hotspots", MaxBytes: 4096));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, PerfJsonContext.Default.PerfQueryResult);
        Assert.IsLessThanOrEqualTo(4096, bytes.Length + Encoding.UTF8.GetByteCount(Environment.NewLine));
        Assert.AreEqual(1, result.Returned);
        Assert.IsTrue(result.Rows[0].Projected);
        var operations = result.Rows[0].DominantOperations;
        Assert.IsNotNull(operations);
        Assert.IsNotEmpty(operations!);
        Assert.IsTrue(operations!.All(operation => operation.Name.Length <= 80));
    }

    [TestMethod]
    public void RequestsBeyondCaptureBoundsCannotClaimCompleteCoverage()
    {
        using var fixture = new Fixture([Call("c1", 0, 40)]);
        var result = fixture.Query(new(View: "calls", FromMs: -100, ToMs: 200));
        Assert.IsFalse(result.Coverage.Complete);
        Assert.IsTrue(result.Coverage.Reasons.Any(r => r.Contains("beyond", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ElementParentsAreSelectedForTheRequestedInterval()
    {
        var root = new PerfElement { Id = "e1", ObjectId = "a" };
        var child = new PerfElement
        {
            Id = "e2", ObjectId = "b", Parents = [new(0, "e1", "v1"), new(5, "e3", "v2")],
        };
        using var fixture = new Fixture([
            Call("c1", 7, 8) with { Name = "MeasureElement", ElementId = "e2" },
        ], elements: [root, child]);
        var result = fixture.Query(new(View: "element", Id: "e1", Depth: 1, FromMs: 6, ToMs: 9));
        Assert.AreEqual("e1", string.Join(',', result.Rows.Select(r => r.Id)));
    }

    [TestMethod]
    public void TreeByteBudgetAlwaysMakesProgressAndPreservesIdentifiers()
    {
        var calls = Enumerable.Range(0, 30).Select(i => Call("c" + i, i, i + 1) with
        {
            Name = new string('\u0001', 512),
        }).ToArray();
        using var fixture = new Fixture(calls);
        var ids = new HashSet<string>();
        int? offset = 0;
        while (offset is not null)
        {
            var result = fixture.Query(new(View: "calls", Offset: offset.Value, MaxBytes: 4096));
            Assert.IsLessThanOrEqualTo(4096,
                JsonSerializer.SerializeToUtf8Bytes(result, PerfJsonContext.Default.PerfQueryResult).Length +
                Encoding.UTF8.GetByteCount(Environment.NewLine));
            Assert.IsGreaterThan(0, result.Returned);
            foreach (var row in result.Rows)
            {
                Assert.IsTrue(ids.Add(row.Id));
                Assert.AreEqual(row.Id, row.Call!.Id);
            }
            offset = result.NextOffset;
        }
        Assert.AreEqual(30, ids.Count);
    }

    private static PerfCall Call(string id, double start, double end, string? parent = null) =>
        new(id, "Layout", "layout", 1, null, parent, "v" + id + "b", "v" + id + "e", start, end,
            end - start, end - start, "complete", end - start);

    private static PerfCall Frame(string id, double start, double end) =>
        Call(id, start, end) with { Name = "Frame", Family = "frames" };

    private static void AssertActivity(PerfQueryResult result, string category, double expected) =>
        Assert.AreEqual(expected, result.Activity!.Single(row => row.Category == category).ObservedMs, 0.000001, category);

    [TestMethod]
    public void HalfMillionCompleteCallsRemainBoundedAndPageable()
    {
        using var fixture = new Fixture(Enumerable.Range(0, 500_000).Select(i => Call("c" + i, 0, 1)));
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var result = fixture.Query(new(View: "calls", Limit: 100, MaxBytes: 1048576));
        timer.Stop();
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.AreEqual(500_000, result.Total);
        Assert.AreEqual(100, result.Returned);
        Assert.AreEqual(100, result.NextOffset);
        Assert.IsLessThan(1024L * 1024 * 1024, allocated, "The maximum complete-call fixture should allocate less than 1 GiB.");
        Assert.IsLessThanOrEqualTo(1048576, JsonSerializer.SerializeToUtf8Bytes(result, PerfJsonContext.Default.PerfQueryResult).Length);
        Console.WriteLine($"500,000-call query: {timer.Elapsed.TotalMilliseconds:F0} ms; allocated {allocated / (1024 * 1024)} MiB.");
    }

    private static PerfGcInterval Pause(string id, double start, double end, ushort instance = 1, uint reason = 1) =>
        new() { Id = id, Kind = "suspension", ClrInstanceId = instance, Reason = reason,
            StartMs = start, SuspendCompleteMs = start, RestartBeginMs = end, EndMs = end };

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("WinApp-Perf-Tree-");
        private readonly PerfCaptureDocument capture;
        public PerfCaptureDocument Capture => capture;
        public PerfAnalysisManifest Manifest { get; }

        public Fixture(IEnumerable<PerfCall> calls, PerfEvent[]? events = null, PerfGcInterval[]? gc = null, PerfElement[]? elements = null)
        {
            capture = new()
            {
                Id = Guid.NewGuid().ToString("N"), Directory = directory.FullName, SessionName = "fixture",
                State = "completed", ReadyQpc = 0, StopQpc = 100, Frequency = 1000, EventsLost = 0, BuffersLost = 0,
                Providers = events?.Any(e => e.Family == "gc") == true ? PerfProviders.All : PerfProviders.All.Where(p => p.Id != PerfProviders.Clr).ToArray(),
            };
            Manifest = new() { Fingerprint = "fixture", FirstEventMs = 0, LastEventMs = 100,
                Families = new() { ["layout"] = 1 }, Events = events?.Length ?? 0 };
            Write("calls.ndjson", calls, PerfJsonContext.Default.PerfCall);
            Write("events.ndjson", events ?? [], PerfJsonContext.Default.PerfEvent);
            Write("gc.ndjson", gc ?? [], PerfJsonContext.Default.PerfGcInterval);
            Write("elements.ndjson", elements ?? [], PerfJsonContext.Default.PerfElement);
        }

        public PerfQueryResult Query(PerfQueryOptions options) => PerfQuery.Execute(new(capture, Manifest, directory.FullName), options);

        private void Write<T>(string name, IEnumerable<T> values, JsonTypeInfo<T> type) =>
            File.WriteAllLines(Path.Join(directory.FullName, name), values.Select(v => JsonSerializer.Serialize(v, type)));

        public void Dispose() => directory.Delete(recursive: true);
    }
}

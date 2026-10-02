// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Buffers.Binary;
using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Tests;

[TestClass]
public class PerfGcTests
{
    [TestMethod]
    [DataRow(1, 2, 1, 26)]
    [DataRow(1, 1, 1, 18)]
    [DataRow(2, 1, 2, 10)]
    [DataRow(3, 1, 132, 2)]
    [DataRow(7, 1, 136, 2)]
    [DataRow(8, 1, 137, 2)]
    [DataRow(9, 1, 10, 10)]
    public void ModernManifestPayloadWidthsAndOpcodesAreExact(int id, int version, int opcode, int length)
    {
        var raw = new PerfRawEvent(123, 1, 2, PerfProviders.Clr, (ushort)id, (byte)version,
            (byte)opcode, Guid.Empty, 4, false, new byte[length], null, null, null);
        Assert.IsNull(ClrGcEventDecoder.Decode(raw, "v1", 0, 1000)!.DecodeError);
        Assert.IsNotNull(ClrGcEventDecoder.Decode(raw with { Payload = new byte[length + 1] }, "v1", 0, 1000)!.DecodeError);
        Assert.IsNotNull(ClrGcEventDecoder.Decode(raw with { Opcode = 99 }, "v1", 0, 1000)!.DecodeError);
        Assert.IsNotNull(ClrGcEventDecoder.Decode(raw with { Version = 99 }, "v1", 0, 1000)!.DecodeError);
    }

    [TestMethod]
    public void SuspendReasonIsUInt32AndInstanceIsUInt16()
    {
        var payload = new byte[10];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0xfedcba98);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 42);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8), 65000);
        var raw = new PerfRawEvent(0, 1, 1, PerfProviders.Clr, 9, 1, 10, Guid.Empty, 8, false, payload, null, null, null);
        var e = ClrGcEventDecoder.Decode(raw, "v1", 0, 1000)!;
        Assert.AreEqual("4275878552", e.Fields["Reason"]);
        Assert.AreEqual("42", e.Fields["Count"]);
        Assert.AreEqual("65000", e.Fields["ClrInstanceID"]);
    }

    [TestMethod]
    public void BackgroundCollectionLifetimeIsSeparateFromCrossThreadSuspensions()
    {
        var intervals = new List<PerfGcInterval>();
        var analyzer = new PerfGcAnalyzer(intervals.Add);
        foreach (var e in new[]
        {
            Event("GCSuspendEEBegin", 0, count: 0, reason: 6),
            Event("GCSuspendEEEnd", 1, thread: 2),
            Event("GCStart", 2, count: 1, type: 1),
            Event("GCRestartEEBegin", 3, thread: 3),
            Event("GCRestartEEEnd", 4, thread: 4),
            Event("GCSuspendEEBegin", 20, count: 1, reason: 1),
            Event("GCSuspendEEEnd", 21),
            Event("GCRestartEEBegin", 22),
            Event("GCRestartEEEnd", 23),
            Event("GCEnd", 100, count: 1, thread: 5),
        })
        {
            analyzer.Accept(e);
        }
        analyzer.Complete();
        Assert.AreEqual(3, intervals.Count);
        var collection = intervals.Single(g => g.Kind == "collection");
        Assert.AreEqual("background", collection.CollectionType);
        Assert.AreEqual(98d, collection.DurationMs);
        Assert.AreEqual(7d, intervals.Where(g => g.IsGcSuspension).Sum(g => g.DurationMs));
        Assert.AreEqual(2d, intervals.First(g => g.IsGcSuspension).FullySuspendedMs);
        Assert.AreEqual(0, analyzer.IncompleteIntervals);
    }

    [TestMethod]
    public void InstancesAndNonGcReasonsStayDistinct()
    {
        var intervals = new List<PerfGcInterval>();
        var analyzer = new PerfGcAnalyzer(intervals.Add);
        analyzer.Accept(Event("GCStart", 0, instance: 1));
        analyzer.Accept(Event("GCStart", 1, instance: 2));
        analyzer.Accept(Event("GCEnd", 2, instance: 2));
        analyzer.Accept(Event("GCEnd", 4, instance: 1));
        analyzer.Accept(Event("GCSuspendEEBegin", 5, reason: 5));
        analyzer.Accept(Event("GCSuspendEEEnd", 6));
        analyzer.Accept(Event("GCRestartEEBegin", 7));
        analyzer.Accept(Event("GCRestartEEEnd", 8));
        analyzer.Complete();
        Assert.AreEqual(4d, intervals.Single(g => g.Kind == "collection" && g.ClrInstanceId == 1).DurationMs);
        Assert.AreEqual(1d, intervals.Single(g => g.Kind == "collection" && g.ClrInstanceId == 2).DurationMs);
        Assert.IsFalse(intervals.Single(g => g.Kind == "suspension").IsGcSuspension);
    }

    [TestMethod]
    public void MissingOrReorderedBoundariesNeverBecomeZeroPauses()
    {
        var intervals = new List<PerfGcInterval>();
        var analyzer = new PerfGcAnalyzer(intervals.Add);
        analyzer.Accept(Event("GCRestartEEEnd", 1));
        analyzer.Accept(Event("GCSuspendEEBegin", 2, reason: 1));
        analyzer.Accept(Event("GCRestartEEBegin", 3));
        analyzer.Accept(Event("GCSuspendEEEnd", 4));
        analyzer.Accept(Event("GCRestartEEEnd", 5));
        analyzer.Accept(Event("GCStart", 6));
        analyzer.Complete();
        Assert.AreEqual(3, analyzer.IncompleteIntervals);
        Assert.IsTrue(intervals.All(g => g.DurationMs is null && g.FullySuspendedMs is null));
    }

    [TestMethod]
    public void UnsupportedClrEventsDoNotCorruptTheWinUiStack()
    {
        var calls = new List<PerfCall>();
        var ui = new PerfAnalyzer(calls.Add);
        var gc = new PerfGcAnalyzer(_ => { });
        ui.Accept(new("v1", 0, 0, 1, PerfProviders.Xaml, 41, 0, 1, Guid.Empty, "Layout", "layout", "begin", null, []));
        gc.Accept(Event("GCStart", 1) with { DecodeError = "unsupported version" });
        ui.Accept(new("v2", 10, 10, 1, PerfProviders.Xaml, 42, 0, 2, Guid.Empty, "Layout", "layout", "end", null, []));
        Assert.AreEqual(10d, calls.Single().DurationMs);
    }

    internal static PerfEvent Event(string name, double time, ushort instance = 1, uint count = 1,
        uint reason = 1, uint type = 0, uint thread = 1) =>
        new("v" + time + name + instance, (long)time, time, thread, PerfProviders.Clr, 1, 1, 0, Guid.Empty,
            name, "gc", "info", null, new()
            {
                ["ClrInstanceID"] = instance.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Count"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Depth"] = "2",
                ["Reason"] = reason.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Type"] = type.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
}

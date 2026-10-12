// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Buffers.Binary;
using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class PerfCompositionTests
{
    private static PerfRawEvent Raw(ushort id, byte opcode, byte[] payload, long qpc = 100) =>
        new(qpc, 1, 7, PerfProviders.DirectComposition, id, 0, opcode, Guid.Empty, 8,
            false, payload, null, null, null);

    [TestMethod]
    public void MainDeviceCommitPairsAsCompositionInsideFrameSubmission()
    {
        var calls = new List<PerfCall>();
        var analyzer = new PerfAnalyzer(calls.Add);
        foreach (var (id, opcode, time) in new (ushort, byte, long)[]
        {
            (100, 1, 0), (329, 1, 10), (330, 2, 15), (102, 2, 20),
        })
        {
            var decoded = WinUiEventDecoder.Decode(Raw(id, opcode, [], time) with
                { Provider = PerfProviders.Xaml }, "v" + id, 0, 1000)!;
            Assert.IsNull(decoded.DecodeError);
            analyzer.Accept(decoded);
        }
        analyzer.Complete();
        var commit = calls.Single(call => call.Name == "CommitMainDevice");
        Assert.AreEqual("composition", commit.Family);
        Assert.AreEqual(5, commit.DurationMs);
        Assert.AreEqual(5, commit.ExclusiveMs);
        Assert.AreEqual(15, calls.Single(call => call.Name == "SubmitFrame").ExclusiveMs);
        Assert.IsNull(commit.ElementId);
        Assert.AreEqual(0, analyzer.IncompleteCalls);
    }

    [TestMethod]
    [DataRow((ushort)2, (ushort)3, "DCompBeginDraw", 32)]
    [DataRow((ushort)4, (ushort)5, "DCompEndDraw", 16)]
    [DataRow((ushort)10, (ushort)11, "DCompUpdateToken", 16)]
    public void SurfaceScopesRetainResourceFieldsWithoutCreatingXamlElements(
        ushort startId, ushort stopId, string name, int length)
    {
        var payload = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 123);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 31);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8), 0xfedcba9876543210);
        if (length == 32)
        {
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(16), -10);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(24), 100);
        }
        // The manifest uses UInt64 resource IDs, regardless of the emitting process's pointer width.
        var start = WinUiEventDecoder.Decode(Raw(startId, 1, payload) with { PointerSize = 4 }, "v1", 0, 1000)!;
        var stop = WinUiEventDecoder.Decode(Raw(stopId, 2, [], 120), "v2", 0, 1000)!;
        Assert.IsNull(start.DecodeError);
        Assert.IsNull(stop.DecodeError);
        Assert.AreEqual(name, start.Name);
        Assert.AreEqual("123", start.Fields["channelHandle"]);
        Assert.AreEqual("31", start.Fields["resourceType"]);
        Assert.AreEqual("fedcba9876543210", start.Fields["resourcePointer"]);
        if (length == 32)
        {
            Assert.AreEqual("-10", start.Fields["left"]);
            Assert.AreEqual("100", start.Fields["right"]);
        }
        var calls = new List<PerfCall>();
        var analyzer = new PerfAnalyzer(calls.Add);
        analyzer.Accept(start);
        analyzer.Accept(stop);
        analyzer.Complete();
        Assert.AreEqual("composition", calls.Single().Family);
        Assert.AreEqual(20, calls.Single().DurationMs);
        Assert.IsNull(calls.Single().ElementId);
        Assert.IsEmpty(analyzer.Elements);
    }

    [TestMethod]
    public void DeviceCommitIsANotificationWithBatchIdsNotATimedOrDisplayedFrame()
    {
        var payload = new byte[20];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, 0x123456789abcdef0);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), 123);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), 9);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), 7);
        var commit = WinUiEventDecoder.Decode(Raw(25, 0, payload), "v1", 0, 1000)!;
        Assert.IsNull(commit.DecodeError);
        Assert.AreEqual(("DCompDeviceCommit", "composition", "info"), (commit.Name, commit.Family, commit.Phase));
        Assert.AreEqual("123456789abcdef0", commit.Fields["DeviceId"]);
        Assert.AreEqual("123", commit.Fields["ChannelHandle"]);
        Assert.AreEqual("9", commit.Fields["LastCommittedBatchId"]);
        Assert.AreEqual("7", commit.Fields["LastConfirmedBatchId"]);
        var calls = new List<PerfCall>();
        var analyzer = new PerfAnalyzer(calls.Add);
        analyzer.Accept(commit);
        analyzer.Complete();
        Assert.IsEmpty(calls);
        Assert.IsEmpty(analyzer.Elements);
    }

    [TestMethod]
    [DataRow((byte)1, (byte)1, 32)]
    [DataRow((byte)0, (byte)0, 32)]
    [DataRow((byte)0, (byte)1, 31)]
    [DataRow((byte)0, (byte)1, 33)]
    public void UnsupportedCompositionPayloadsNeverEstablishTimingBoundaries(byte version, byte opcode, int length)
    {
        var e = WinUiEventDecoder.Decode(Raw(2, opcode, new byte[length]) with { Version = version },
            "v1", 0, 1000)!;
        Assert.IsNotNull(e.DecodeError);
        Assert.AreEqual("unknown", e.Phase);
        Assert.IsEmpty(e.Fields);
        Assert.IsNull(e.ObjectId);
    }

    [TestMethod]
    public void UnknownCompositionDescriptorsAreNotGuessedAndMissingStopsHaveNoDuration()
    {
        Assert.IsNull(WinUiEventDecoder.Decode(Raw(999, 0, []), "v1", 0, 1000));
        var calls = new List<PerfCall>();
        var analyzer = new PerfAnalyzer(calls.Add);
        analyzer.Accept(WinUiEventDecoder.Decode(Raw(4, 1, new byte[16]), "v1", 0, 1000)!);
        analyzer.Complete();
        Assert.AreEqual("missing-end", calls.Single().Status);
        Assert.IsNull(calls.Single().DurationMs);
    }

    [TestMethod]
    public void CompositionCollectionIsOptionalAndRequestsOnlyBasicDiagnosticKeywordsAndSelectedIds()
    {
        var provider = PerfProviders.All.Single(provider => provider.Id == PerfProviders.DirectComposition);
        Assert.IsTrue(provider.Optional);
        Assert.AreEqual("3", provider.Keywords);
        CollectionAssert.AreEqual(new ushort[] { 2, 3, 4, 5, 10, 11, 25 }, provider.EventIds);
    }
}

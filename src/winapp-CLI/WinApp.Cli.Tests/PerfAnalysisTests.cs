// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WinApp.Cli.Commands;
using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Tests;

[TestClass]
public class PerfAnalysisTests
{
    private static PerfEvent Event(string name, string phase, double ms, string? address = "a",
        string family = "layout", uint thread = 1, Dictionary<string, string>? fields = null) =>
        new("v" + ms + name, (long)(ms * 10000), ms, thread, PerfProviders.Xaml, 47, 0,
            phase == "begin" ? (byte)1 : phase == "end" ? (byte)2 : (byte)0, Guid.Empty,
            name, family, phase, address, fields ?? []);

    [TestMethod]
    public void SelfTimeFlattensSameObjectOverridesWithoutDoubleSubtractingChildren()
    {
        var calls = new List<PerfCall>();
        var analyzer = new PerfAnalyzer(calls.Add);
        foreach (var e in new[]
        {
            Event("MeasureElement", "begin", 0), Event("MeasureOverride", "begin", 1),
            Event("MeasureElement", "begin", 2, "b"), Event("MeasureElement", "end", 8, "b"),
            Event("MeasureOverride", "end", 9), Event("MeasureElement", "end", 10),
        })
        {
            analyzer.Accept(e);
        }
        analyzer.Complete();
        var parent = calls.Single(c => c.StartMs == 0);
        Assert.AreEqual(10, parent.DurationMs);
        Assert.AreEqual(4, parent.SelfMs);
        Assert.AreEqual(6, calls.Single(c => c.StartMs == 2).SelfMs);
        Assert.AreEqual(0, analyzer.IncompleteCalls);
    }

    [TestMethod]
    public void MeasureInsideArrangeIsSubtractedOnce()
    {
        var calls = new List<PerfCall>();
        var analyzer = new PerfAnalyzer(calls.Add);
        foreach (var e in new[]
        {
            Event("ArrangeElement", "begin", 0), Event("MeasureElement", "begin", 2),
            Event("MeasureElement", "begin", 3, "b"), Event("MeasureElement", "end", 5, "b"),
            Event("MeasureElement", "end", 8), Event("ArrangeElement", "end", 10),
        })
        {
            analyzer.Accept(e);
        }
        analyzer.Complete();
        Assert.AreEqual(4, calls.Single(c => c.Name == "ArrangeElement").SelfMs);
        Assert.AreEqual(4, calls.Single(c => c.StartMs == 2).SelfMs);
    }

    [TestMethod]
    public void MissingNestedBoundaryNeverFabricatesDuration()
    {
        var calls = new List<PerfCall>();
        var analyzer = new PerfAnalyzer(calls.Add);
        analyzer.Accept(Event("ArrangeElement", "begin", 0));
        analyzer.Accept(Event("MeasureElement", "begin", 1));
        analyzer.Accept(Event("ArrangeElement", "end", 5));
        analyzer.Complete();
        Assert.AreEqual(2, analyzer.IncompleteCalls);
        Assert.IsTrue(calls.All(c => c.DurationMs is null && c.SelfMs is null));
    }

    [TestMethod]
    public void EqualNamedScopesOnDifferentThreadsDoNotPair()
    {
        var calls = new List<PerfCall>();
        var analyzer = new PerfAnalyzer(calls.Add);
        analyzer.Accept(Event("MeasureElement", "begin", 0, thread: 1));
        analyzer.Accept(Event("MeasureElement", "end", 1, thread: 2));
        analyzer.Complete();
        Assert.AreEqual(2, analyzer.IncompleteCalls);
    }

    [TestMethod]
    public void PointerReuseCreatesSeparateTraceLocalElementLifetimes()
    {
        var analyzer = new PerfAnalyzer(_ => { });
        var first = analyzer.Accept(Event("Created", "info", 0, family: "metadata"));
        analyzer.Accept(Event("Destroyed", "info", 1, family: "metadata"));
        var second = analyzer.Accept(Event("Created", "info", 2, family: "metadata"));
        Assert.AreNotEqual(first, second);
        Assert.AreEqual(1, analyzer.Elements[0].DestroyedMs);
    }

    [TestMethod]
    public void MatchedStopEventInheritsTheScopesElementIdentity()
    {
        var analyzer = new PerfAnalyzer(_ => { });
        analyzer.Accept(Event("Created", "info", 0, address: "a", family: "metadata"));
        var beginElement = analyzer.Accept(Event("ApplyTemplate", "begin", 1, address: "a"));

        var endElement = analyzer.Accept(Event("ApplyTemplate", "end", 2, address: null));

        Assert.AreEqual(beginElement, endElement,
            "An element-filtered events query must retain both boundaries of a matched element operation.");
    }

    [TestMethod]
    public void IntervalUnionDoesNotSumOverlappingScopes()
    {
        Assert.AreEqual(12, PerfAnalyzer.Union([(0, 10), (2, 5), (8, 12)]));
    }

    [TestMethod]
    public void FixedUInt64ElementIdsDoNotDependOnCollectorPointerWidth()
    {
        var payload = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, 0xfedcba9876543210);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(8), float.PositiveInfinity);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(12), 40);
        var raw = new PerfRawEvent(100, 1, 1, PerfProviders.Xaml, 47, 0, 1, Guid.Empty, 4,
            false, payload, null, null, null);
        var decoded = WinUiEventDecoder.Decode(raw, "v1", 0, 1000)!;
        Assert.AreEqual("fedcba9876543210", decoded.ObjectId);
        Assert.AreEqual("Infinity", decoded.Fields["Width"]);
        Assert.IsNull(decoded.DecodeError);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(decoded, PerfJsonContext.Default.PerfEvent));
        Assert.AreEqual(JsonValueKind.String, json.RootElement.GetProperty("qpc").ValueKind);
    }

    [TestMethod]
    [DataRow((byte)9, (byte)1)]
    [DataRow((byte)0, (byte)2)]
    public void UnsupportedVersionOrOpcodePreservesEvidenceWithoutGuessing(byte version, byte opcode)
    {
        var raw = new PerfRawEvent(0, 1, 1, PerfProviders.Xaml, 47, version, opcode, Guid.Empty, 8,
            false, new byte[16], null, null, null);
        var decoded = WinUiEventDecoder.Decode(raw, "v1", 0, 1000)!;
        Assert.IsNotNull(decoded.DecodeError);
        Assert.AreEqual("unknown", decoded.Phase);
        Assert.IsNull(decoded.ObjectId);
    }

    [TestMethod]
    public void MalformedUnicodeIsAnExplicitDecodeError()
    {
        var payload = new byte[12];
        payload[8] = 0;
        payload[9] = 0xd8;
        var raw = new PerfRawEvent(0, 1, 1, PerfProviders.Xaml, 213, 0, 0, Guid.Empty, 8,
            false, payload, null, null, null);
        Assert.IsNotNull(WinUiEventDecoder.Decode(raw, "v1", 0, 1000)!.DecodeError);
    }

    [TestMethod]
    public void OversizedRowsProduceValidBoundedJsonAndProgressingPagination()
    {
        var result = new PerfQueryResult
        {
            CaptureId = Guid.NewGuid().ToString("N"), View = "events", Range = new(0, 1),
            Coverage = new(true, "attached", 0, 0, 0, 0, [], []),
            Total = 10, Offset = 0,
            Rows = Enumerable.Range(0, 10).Select(i => new PerfQueryRow
            {
                Id = "v" + i, Kind = "event",
                Event = Event("MeasureElement", "info", i, fields: new() { ["payload"] = new string('\u0001', 10000) }),
            }).ToList(),
        };
        PerfQuery.Fit(result, 4096);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, PerfJsonContext.Default.PerfQueryResult);
        Assert.IsLessThanOrEqualTo(4096, bytes.Length + Encoding.UTF8.GetByteCount(Environment.NewLine));
        using var parsed = JsonDocument.Parse(bytes);
        Assert.AreEqual(1, result.Rows.Count);
        Assert.AreEqual(1, result.NextOffset);
        Assert.IsTrue(result.Rows[0].Projected);
        Assert.IsTrue(result.Rows[0].Event!.OmittedFields > 0);
    }

    [TestMethod]
    public void QueryBoundsAndIrrelevantFiltersAreRejected()
    {
        Assert.Throws<ArgumentException>(() => PerfQuery.Validate(new(Limit: 101)));
        Assert.Throws<ArgumentException>(() => PerfQuery.Validate(new(FromMs: 0, FromMarker: "start")));
        Assert.Throws<ArgumentException>(() => PerfQuery.Validate(new(View: "frames", Type: "Button")));
        Assert.Throws<ArgumentException>(() => PerfQuery.Validate(new(View: "element")));
        Assert.Throws<ArgumentException>(() => PerfQuery.Validate(new(MaxBytes: 4095)));
        Assert.Throws<ArgumentException>(() => PerfQuery.Validate(new(View: "hotspots", MinFrameMs: -1)));
        Assert.Throws<ArgumentException>(() => PerfQuery.Validate(new(View: "frames", MinFrameMs: 10)));
        Assert.Throws<ArgumentException>(() => PerfQuery.Validate(new(View: "calls", Sort: "duration")));
        Assert.Throws<ArgumentException>(() => PerfQuery.Validate(new(View: "gc", Sort: "count")));
    }

    [TestMethod]
    public void UnsafeTracePathsAreRejected()
    {
        var directory = Directory.CreateTempSubdirectory("WinApp-Perf-Manifest-");
        try
        {
            var capture = new PerfCaptureDocument
            {
                Id = Guid.NewGuid().ToString("N"), Directory = directory.FullName,
                SessionName = "test", TraceFiles = [@"..\other.etl"],
            };
            capture.Save();
            Assert.Throws<InvalidDataException>(() => PerfCaptureDocument.Load(directory.FullName));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [TestMethod]
    public async Task ControlMessagesAreBoundedBeforeAllocatingPayload()
    {
        using var stream = new MemoryStream();
        stream.Write(BitConverter.GetBytes(int.MaxValue));
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PerfControlChannel.ReadAsync(stream, PerfJsonContext.Default.PerfControlRequest, 4096, CancellationToken.None));
    }

    [TestMethod]
    public void AnalysisCacheJunctionIsRejectedWithoutDeletingItsTarget()
    {
        var root = Directory.CreateTempSubdirectory("WinApp-Perf-Cache-");
        var target = Directory.CreateTempSubdirectory("WinApp-Perf-Cache-Target-");
        var link = Path.Join(root.FullName, "analysis");
        var targetFile = Path.Join(target.FullName, "events.ndjson");
        File.WriteAllText(targetFile, "must remain");
        try
        {
            if (!TryCreateJunction(link, target.FullName))
            {
                Assert.Inconclusive("Could not create a junction on this machine.");
            }

            Assert.Throws<IOException>(() => PerfAnalysisStore.EnsureCacheOnly(link));
            Assert.IsTrue(File.Exists(targetFile), "Rejecting a redirected cache must not delete its target files.");
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
            root.Delete(recursive: true);
            target.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void ProfileOptionsPreserveArgumentsAfterSeparator()
    {
        var command = new RunCommand();
        var result = command.Parse([".", "--profile", @"C:\trace", "--detach", "--", "--profile", "app-value"]);
        Assert.IsEmpty(result.Errors);
        Assert.AreEqual(@"C:\trace", result.GetValue(RunCommand.ProfileOption));
        Assert.AreEqual(30, result.GetValue(RunCommand.ProfileDurationOption));
        Assert.AreEqual(128, result.GetValue(RunCommand.ProfileSizeOption));
    }

    [TestMethod]
    public void EditableCaptureMetadataCannotRedirectOwnedSessionCleanup()
    {
        var id = Guid.NewGuid().ToString("N");
        var session = Guid.NewGuid();
        var registration = new PerfControlRegistration(id, @"C:\capture", new string('a', 64), session, 30, 128);
        var capture = new PerfCaptureDocument
        {
            Id = id, Directory = registration.Directory, SessionId = session, SessionName = "WinApp-Perf-" + id,
        };
        PerfCaptureService.ValidateOwnership(registration, capture);
        capture.SessionName = "another-session";
        Assert.Throws<InvalidDataException>(() => PerfCaptureService.ValidateOwnership(registration, capture));
        capture.SessionName = "WinApp-Perf-" + id;
        capture.SessionId = Guid.NewGuid();
        Assert.Throws<InvalidDataException>(() => PerfCaptureService.ValidateOwnership(registration, capture));
    }

    [TestMethod]
    public void ProcessIdentityRejectsAReusedPid()
    {
        var identity = new PerfProcessIdentity(Environment.ProcessId, DateTime.MinValue);
        Assert.Throws<InvalidOperationException>(() => identity.Open());
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            process?.WaitForExit(5000);
            return process?.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    [TestMethod]
    public void QueryPercentilesExcludeClippedAndIncompleteCalls()
    {
        var directory = Directory.CreateTempSubdirectory("WinApp-Perf-Query-");
        try
        {
            var element = new PerfElement { Id = "e1", ObjectId = "a", Type = "Panel" };
            File.WriteAllText(Path.Join(directory.FullName, "events.ndjson"), "");
            File.WriteAllText(Path.Join(directory.FullName, "gc.ndjson"), "");
            File.WriteAllText(Path.Join(directory.FullName, "elements.ndjson"),
                JsonSerializer.Serialize(element, PerfJsonContext.Default.PerfElement) + "\n");
            var calls = new[]
            {
                new PerfCall("c1", "MeasureElement", "layout", 1, "e1", null, "v1", "v2", 0, 2, 2, 2, "complete"),
                new PerfCall("c2", "MeasureElement", "layout", 1, "e1", null, "v3", "v4", 3, 8, 5, 5, "complete"),
                new PerfCall("c3", "MeasureElement", "layout", 1, "e1", null, "v5", "v6", 0, 10, 10, 10, "complete"),
                new PerfCall("c4", "MeasureElement", "layout", 1, "e1", null, "v7", "v8", -5, 5, 10, 10, "complete"),
                new PerfCall("c5", "MeasureElement", "layout", 1, "e1", null, "v9", null, 1, null, null, null, "missing-end"),
            };
            File.WriteAllLines(Path.Join(directory.FullName, "calls.ndjson"),
                calls.Select(c => JsonSerializer.Serialize(c, PerfJsonContext.Default.PerfCall)));
            var capture = new PerfCaptureDocument
            {
                Id = Guid.NewGuid().ToString("N"), Directory = directory.FullName, SessionName = "test",
                Frequency = 1000, ReadyQpc = 0, StopQpc = 10, EventsLost = 0, BuffersLost = 0,
            };
            var manifest = new PerfAnalysisManifest
            {
                Fingerprint = "fixture", Families = new() { ["layout"] = 10 }, FirstEventMs = -5, LastEventMs = 10,
            };
            var result = PerfQuery.Execute(new(capture, manifest, directory.FullName),
                new(View: "elements", FromMs: 0, ToMs: 10));
            var row = result.Rows.Single();
            Assert.AreEqual(3, row.Count);
            Assert.AreEqual(17d / 3, row.MeanMs);
            Assert.AreEqual(10d, row.P95Ms);
            Assert.AreEqual(1, row.BoundaryOverlaps);
            Assert.AreEqual(5d, row.ClippedOverlapMs);
            Assert.AreEqual(10d, result.LayoutBusyMsByThread[1]);
        }
        finally
        {
            directory.Delete(true);
        }
    }
}

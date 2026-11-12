// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Tests;

[TestClass]
public class ReactorPerfTests
{
    private static PerfRawEvent Raw(ushort id, byte opcode, string name, Dictionary<string, string>? fields = null,
        double ms = 0, uint thread = 7) =>
        new((long)(ms * 1000), 1, thread, PerfProviders.Reactor, id, 0, opcode, Guid.Empty, 8, true, [], name,
            fields ?? [], null);

    private static PerfEvent Decode(PerfRawEvent raw) =>
        ReactorEventDecoder.Decode(raw, "v" + raw.Qpc + "-" + raw.EventId, 0, 1_000_000)!;

    private static PerfEvent RenderStart(double ms, string id, string name, string reason = "state", uint thread = 7) =>
        Decode(Raw(3, 1, "ComponentRender", new() { ["componentId"] = id, ["name"] = name, ["reason"] = reason }, ms, thread));

    private static PerfEvent RenderStop(double ms, string id, uint thread = 7) =>
        Decode(Raw(4, 2, "ComponentRender", new() { ["componentId"] = id }, ms, thread));

    private static PerfEvent PassStart(double ms) => Decode(Raw(1, 1, "RenderPass", new() { ["hostId"] = "1" }, ms));
    private static PerfEvent PassStop(double ms) => Decode(Raw(2, 2, "RenderPass", new() { ["hostId"] = "1" }, ms));
    private static PerfEvent ReconcileStart(double ms) =>
        Decode(Raw(5, 1, "Reconcile", new() { ["entry"] = "reconcile", ["element"] = "StackElement" }, ms));
    private static PerfEvent ReconcileStop(double ms) => Decode(Raw(6, 2, "Reconcile", [], ms));

    [TestMethod]
    public void DecodesEveryContractEventWithItsPhaseAndFields()
    {
        var expected = new (ushort Id, byte Opcode, string Event, string Name, string Phase, Dictionary<string, string> Fields)[]
        {
            (1, 1, "RenderPass", "ReactorRenderPass", "begin", new() { ["hostId"] = "3" }),
            (2, 2, "RenderPass", "ReactorRenderPass", "end", new() { ["hostId"] = "3" }),
            (3, 1, "ComponentRender", "ReactorRender:Row<Int32>", "begin",
                new() { ["componentId"] = "12", ["name"] = "Row<Int32>", ["reason"] = "mount" }),
            (4, 2, "ComponentRender", "ReactorRender", "end", new() { ["componentId"] = "12" }),
            (5, 1, "Reconcile", "ReactorReconcile", "begin", new() { ["entry"] = "mount", ["element"] = "ComponentElement<String>" }),
            (6, 2, "Reconcile", "ReactorReconcile", "end", []),
            (7, 1, "Effects", "ReactorEffects", "begin", new() { ["componentId"] = "1" }),
            (8, 2, "Effects", "ReactorEffects", "end", new() { ["componentId"] = "1" }),
            (9, 1, "ChildReconcile", "ReactorChildReconcile", "begin", new() { ["oldCount"] = "3", ["newCount"] = "4" }),
            (10, 2, "ChildReconcile", "ReactorChildReconcile", "end",
                new() { ["inserted"] = "1", ["removed"] = "0", ["moved"] = "2", ["replaced"] = "0" }),
            (11, 0, "StateChange", "ReactorStateChange", "info", new() { ["componentId"] = "4", ["hook"] = "UseState", ["changed"] = "true" }),
            (12, 0, "KeyedListDiagnostic", "ReactorKeyedListDiagnostic", "info", new() { ["kind"] = "duplicateKey", ["bailedOut"] = "false" }),
        };
        foreach (var (id, opcode, eventName, name, phase, fields) in expected)
        {
            var e = Decode(Raw(id, opcode, eventName, fields));
            Assert.IsNull(e.DecodeError, eventName);
            Assert.AreEqual(name, e.Name);
            Assert.AreEqual(phase, e.Phase);
            Assert.AreEqual("reactor", e.Family);
            Assert.IsNull(e.ObjectId, "Component IDs are not WinUI element addresses.");
            CollectionAssert.AreEquivalent(fields.ToArray(), e.Fields.ToArray());
        }
    }

    [TestMethod]
    public void IgnoresLaterEventIdsAndRejectsEventsThatDoNotMatchTheContract()
    {
        Assert.IsNull(ReactorEventDecoder.Decode(Raw(99, 1, "FutureSpan"), "v1", 0, 1000));

        var wrongName = Decode(Raw(1, 1, "SomethingElse", new() { ["hostId"] = "1" }));
        var wrongOpcode = Decode(Raw(1, 2, "RenderPass", new() { ["hostId"] = "1" }));
        var missingField = Decode(Raw(3, 1, "ComponentRender", new() { ["componentId"] = "1", ["name"] = "A" }));
        var manifest = ReactorEventDecoder.Decode(Raw(1, 1, "RenderPass") with { TraceLogging = false }, "v1", 0, 1000)!;
        var tdh = ReactorEventDecoder.Decode(Raw(1, 1, "RenderPass") with { DecodeError = "TDH failed" }, "v1", 0, 1000)!;
        foreach (var e in new[] { wrongName, wrongOpcode, missingField, manifest, tdh })
        {
            Assert.IsNotNull(e.DecodeError);
            Assert.AreEqual("unknown", e.Phase, "An undecodable event must never open or close a span.");
            Assert.IsEmpty(e.Fields);
        }
        StringAssert.Contains(missingField.DecodeError!, "reason");
        Assert.AreEqual("TDH failed", tdh.DecodeError);
    }

    [TestMethod]
    public void SpansPairPerThreadIntoAComponentTreeAndElementsAreAttributedToTheInnermostComponent()
    {
        var calls = new List<PerfCall>();
        var analyzer = new PerfAnalyzer(calls.Add);
        var created = new PerfEvent("vc", 0, 4, 7, PerfProviders.Xaml, 61, 0, 0, Guid.Empty, "Created", "metadata",
            "info", "00000000000000a1", []);
        var createdByRoot = created with { Id = "vr", TimeMs = 1.5, ObjectId = "00000000000000a2" };
        var createdOutside = created with { Id = "vo", TimeMs = 20, ObjectId = "00000000000000a3" };
        foreach (var e in new[]
        {
            PassStart(0), RenderStart(1, "1", "App"), createdByRoot, ReconcileStart(2),
            RenderStart(3, "2", "Panel", "parent"), created,
            // Another thread's spans never pair with this thread's.
            RenderStart(3.5, "9", "Other", thread: 8), RenderStop(5, "2", thread: 8),
            RenderStop(9, "2"), ReconcileStop(10), RenderStop(11, "1"), PassStop(12), createdOutside,
        })
        {
            analyzer.Accept(e);
        }
        analyzer.Complete();

        var pass = calls.Single(c => c.Name == "ReactorRenderPass");
        var app = calls.Single(c => c.Name == "ReactorRender:App");
        var reconcile = calls.Single(c => c.Name == "ReactorReconcile");
        var panel = calls.Single(c => c.Name == "ReactorRender:Panel");
        Assert.AreEqual(12d, pass.DurationMs);
        Assert.AreEqual(pass.Id, app.ParentCallId);
        Assert.AreEqual(app.Id, reconcile.ParentCallId);
        Assert.AreEqual(reconcile.Id, panel.ParentCallId);
        Assert.AreEqual(6d, panel.DurationMs);
        Assert.AreEqual(6d, panel.ExclusiveMs);
        Assert.AreEqual(10d - 8d, app.ExclusiveMs, "App's exclusive time subtracts its nested reconcile.");
        Assert.IsTrue(calls.All(c => c.Family == "reactor" && c.Status == "complete"));
        Assert.IsFalse(calls.Any(c => c.Name == "ReactorRender:Other"), "An unmatched span on another thread is not completed.");

        var elements = analyzer.Elements.ToDictionary(e => e.ObjectId);
        Assert.AreEqual("Panel", elements["00000000000000a1"].Component);
        Assert.AreEqual("2", elements["00000000000000a1"].ComponentId);
        Assert.AreEqual(4d, elements["00000000000000a1"].CreatedMs);
        Assert.AreEqual("App", elements["00000000000000a2"].Component);
        Assert.IsNull(elements["00000000000000a3"].Component);
        Assert.IsNull(elements["00000000000000a3"].CreatedMs, "Elements outside Reactor keep main's JSON shape.");
    }

    [TestMethod]
    public void AnElementSeenBeforeItsCreatedEventRecordsTheCreationTime()
    {
        var analyzer = new PerfAnalyzer(_ => { });
        var added = new PerfEvent("va", 0, 5, 7, PerfProviders.Diagnostics, 61, 0, 0, Guid.Empty, "Added", "metadata",
            "info", "00000000000000a1", new() { ["ParentId"] = "0000000000000000" });
        var created = added with { Id = "vc", TimeMs = 12, Provider = PerfProviders.Xaml, Name = "Created", Fields = [] };
        foreach (var e in new[] { added, RenderStart(11, "1", "Row"), created, RenderStop(14, "1") })
        {
            analyzer.Accept(e);
        }
        analyzer.Complete();

        var element = analyzer.Elements.Single();
        Assert.AreEqual(5d, element.FirstObservedMs);
        Assert.AreEqual(12d, element.CreatedMs);
        Assert.AreEqual("Row", element.Component);
    }

    [TestMethod]
    public void ARenderStopOnlyClosesTheRenderWithTheSameComponentId()
    {
        var calls = new List<PerfCall>();
        var analyzer = new PerfAnalyzer(calls.Add);
        foreach (var e in new[] { RenderStart(0, "1", "Outer"), RenderStart(1, "2", "Inner"), RenderStop(5, "1") })
        {
            analyzer.Accept(e);
        }
        analyzer.Complete();
        var outer = calls.Single(c => c.Name == "ReactorRender:Outer");
        Assert.AreEqual(5d, outer.DurationMs);
        Assert.IsNull(outer.ExclusiveMs, "Its inner render never stopped, so exclusive time is unknown.");
        Assert.IsFalse(calls.Any(c => c.Name == "ReactorRender:Inner"));
    }

    [TestMethod]
    public void RevisionTwoFieldsAreOptionalAndDecodeWhenPresent()
    {
        var revision2 = Decode(Raw(1, 1, "RenderPass",
            new() { ["hostId"] = "1", ["requesterId"] = "7", ["requesterName"] = "Counter", ["requestCount"] = "2" }));
        var partial = Decode(Raw(1, 1, "RenderPass", new() { ["hostId"] = "1", ["requesterId"] = "7" }));
        var reconcile = Decode(Raw(6, 2, "Reconcile",
            new() { ["inserted"] = "1", ["removed"] = "0", ["moved"] = "3", ["replaced"] = "0" }));
        var render = Decode(Raw(3, 1, "ComponentRender", new()
        {
            ["componentId"] = "1", ["name"] = "RenderEachTime in MainPage.Render", ["reason"] = "mount",
            ["callSiteFile"] = "MainPage.cs", ["callSiteLine"] = "40", ["callSiteColumn"] = "13",
            ["futureField"] = "ignored but kept",
        }));
        foreach (var e in new[] { revision2, partial, reconcile, render })
        {
            Assert.IsNull(e.DecodeError);
        }
        Assert.AreEqual("Counter", revision2.Fields["requesterName"]);
        Assert.AreEqual("ReactorRender:RenderEachTime in MainPage.Render", render.Name,
            "Function components are named by their render function in revision 2.");
        Assert.AreEqual("MainPage.cs:40", ReactorEventDecoder.CallSite(render.Fields));
        Assert.AreEqual("end", reconcile.Phase);

        var longName = Decode(Raw(3, 1, "ComponentRender", new()
        {
            ["componentId"] = "1", ["name"] = "A", ["reason"] = "mount", ["callSiteFile"] = new string('x', 2000), ["callSiteLine"] = "1",
        }));
        Assert.AreEqual(512, longName.Fields["callSiteFile"].Length);
    }

    [TestMethod]
    public void CallsKeepRevisionTwoDetailsAndElementsKeepTheComponentCallSite()
    {
        var calls = new List<PerfCall>();
        var analyzer = new PerfAnalyzer(calls.Add);
        var created = new PerfEvent("vc", 0, 4, 7, PerfProviders.Xaml, 61, 0, 0, Guid.Empty, "Created", "metadata",
            "info", "00000000000000a1", []);
        foreach (var e in new[]
        {
            Decode(Raw(1, 1, "RenderPass", new() { ["hostId"] = "1", ["requesterId"] = "0", ["requesterName"] = "", ["requestCount"] = "0" }, 0)),
            Decode(Raw(5, 1, "Reconcile", new() { ["entry"] = "reconcile", ["element"] = "StackElement" }, 1)),
            Decode(Raw(3, 1, "ComponentRender", new()
            {
                ["componentId"] = "2", ["name"] = "Row", ["reason"] = "mount",
                ["callSiteFile"] = "MainPage.cs", ["callSiteLine"] = "40", ["callSiteColumn"] = "13",
            }, 2)),
            created,
            RenderStop(5, "2"),
            Decode(Raw(3, 1, "ComponentRender", new()
            {
                ["componentId"] = "3", ["name"] = "Cell", ["reason"] = "mount",
                ["callSiteFile"] = "", ["callSiteLine"] = "0", ["callSiteColumn"] = "0",
            }, 6)),
            RenderStop(7, "3"),
            Decode(Raw(6, 2, "Reconcile", new() { ["inserted"] = "2", ["removed"] = "1", ["moved"] = "0", ["replaced"] = "0" }, 8)),
            PassStop(9),
        })
        {
            analyzer.Accept(e);
        }
        analyzer.Complete();

        var pass = calls.Single(c => c.Name == "ReactorRenderPass").Attributes!;
        Assert.AreEqual("0", pass["requesterId"]);
        Assert.IsFalse(pass.ContainsKey("requesterName"), "An empty requester name is not recorded.");
        Assert.AreEqual("2", calls.Single(c => c.Name == "ReactorReconcile").Attributes!["inserted"]);
        var row = calls.Single(c => c.Name == "ReactorRender:Row").Attributes!;
        Assert.AreEqual("MainPage.cs", row["callSiteFile"]);
        Assert.IsFalse(row.ContainsKey("name"), "The component name is already in the call name.");
        var cell = calls.Single(c => c.Name == "ReactorRender:Cell").Attributes!;
        Assert.IsFalse(cell.ContainsKey("callSiteLine"), "An empty call site (source map off) is dropped.");
        Assert.AreEqual("MainPage.cs:40", analyzer.Elements.Single().ComponentCallSite);
    }

    [TestMethod]
    public void RevisionOneCallsHaveNoRevisionTwoDetails()
    {
        var calls = new List<PerfCall>();
        var analyzer = new PerfAnalyzer(calls.Add);
        foreach (var e in new[] { PassStart(0), ReconcileStart(1), RenderStart(2, "1", "FuncElement"), RenderStop(3, "1"), ReconcileStop(4), PassStop(5) })
        {
            analyzer.Accept(e);
        }
        analyzer.Complete();

        Assert.AreEqual("1", calls.Single(c => c.Name == "ReactorRenderPass").Attributes!["hostId"]);
        Assert.IsFalse(calls.Single(c => c.Name == "ReactorRenderPass").Attributes!.ContainsKey("requesterId"));
        Assert.IsFalse(calls.Single(c => c.Name == "ReactorReconcile").Attributes!.ContainsKey("inserted"));
        Assert.IsNull(ReactorEventDecoder.CallSite(new() { ["componentId"] = "1" }));
    }

    [TestMethod]
    public void WinUiScopesStillPairByExactName()
    {
        var calls = new List<PerfCall>();
        var analyzer = new PerfAnalyzer(calls.Add);
        PerfEvent Xaml(string name, string phase, double ms) => new("v" + ms, (long)ms, ms, 7, PerfProviders.Xaml, 41, 0,
            phase == "begin" ? (byte)1 : (byte)2, Guid.Empty, name, "layout", phase, null, []);
        foreach (var e in new[] { Xaml("Layout", "begin", 0), Xaml("Layout:Other", "end", 1), Xaml("Layout", "end", 2) })
        {
            analyzer.Accept(e);
        }
        analyzer.Complete();
        Assert.AreEqual(2d, calls.Single().DurationMs);
        Assert.IsNull(calls.Single().Attributes, "Non-Reactor calls carry no Reactor details.");
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Tests;

[TestClass]
public class PerfScopeTests
{
    [TestMethod]
    public void ExclusiveTimeSubtractsFrameworkChildrenButElementSelfKeepsItsMeaning()
    {
        var calls = new List<PerfCall>();
        var analyzer = new PerfAnalyzer(calls.Add);
        foreach (var e in new[]
        {
            Scope("Frame", "frames", "begin", 0),
            Scope("Layout", "layout", "begin", 2),
            Scope("MeasureElement", "layout", "begin", 5, "a"),
            Scope("MeasureOverride", "layout", "begin", 6, "a"),
            Scope("MeasureOverride", "layout", "end", 20, "a"),
            Scope("MeasureElement", "layout", "end", 25, "a"),
            Scope("Layout", "layout", "end", 30),
            Scope("RenderWalk", "frames", "begin", 30),
            Scope("RenderWalk", "frames", "end", 40),
            Scope("Frame", "frames", "end", 40),
        })
        {
            analyzer.Accept(e);
        }
        analyzer.Complete();
        Assert.AreEqual(2d, calls.Single(c => c.Name == "Frame").ExclusiveMs);
        Assert.IsNull(calls.Single(c => c.Name == "Frame").SelfMs);
        Assert.AreEqual(8d, calls.Single(c => c.Name == "Layout").ExclusiveMs);
        var element = calls.Single(c => c.Name == "MeasureElement");
        Assert.AreEqual(20d, element.SelfMs);
        Assert.AreEqual(6d, element.ExclusiveMs);
        Assert.AreEqual(40d, calls.Sum(c => c.ExclusiveMs));
        Assert.AreEqual(calls.Single(c => c.Name == "Layout").Id, element.ParentCallId);
    }

    [TestMethod]
    [DataRow("WXM::InitializeForCurrentThread", "initialization")]
    [DataRow("SomeUnknownMethod", "framework")]
    public void GenericFrameworkScopesAreNotAutomaticallyLayout(string operation, string family)
    {
        var raw = new PerfRawEvent(10, 1, 2, PerfProviders.Operational, 0, 0, 0, Guid.Empty, 8,
            true, [], "PerfXamlEvent", new() { ["EventName"] = operation, ["IsStart"] = "true" }, null);
        var e = WinUiEventDecoder.Decode(raw, "v1", 0, 1000)!;
        Assert.AreEqual(family, e.Family);
        Assert.AreEqual("begin", e.Phase);
        Assert.AreEqual("PerfXamlEvent:" + operation, e.Name);
    }

    private static PerfEvent Scope(string name, string family, string phase, double time, string? element = null) =>
        new("v" + time + name + phase, (long)(time * 1000), time, 1, PerfProviders.Xaml, 0, 0, 0,
            Guid.Empty, name, family, phase, element, []);
}

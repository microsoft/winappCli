// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinApp.DevTools.Managed.Tests;

// A stand-in with the shape of Reactor's public reference edge (ReferenceEdgeSnapshot; ExpectedTargetTypeName is a
// friendly name such as "Button"). The agent reads it by member name, so this exercises the same reads without Reactor.
public sealed record ReferenceEdgeSnapshot(string Property, bool IsList, int Index, string? TargetAutomationId,
    string? ExpectedTargetTypeName, bool IsResolved, object? Target);

/// <summary>
/// References whose target Reactor hasn't created (on a tab that isn't open, say) have nothing in WinUI to point at;
/// DevTools shows them from Reactor's <c>ReactorDiagnostics.GetReferenceEdges</c> as "(not shown)".
/// </summary>
[TestClass]
public sealed class ReactorReferenceTests
{
    public static IReadOnlyList<ReferenceEdgeSnapshot> Snapshots() =>
    [
        new("XYFocusRight", false, 0, null, "Button", false, null),
        new("XYFocusDown", false, 0, null, "Button", true, new object()),
        new("LabeledBy", false, 0, "Title", null, false, null),
        new("FlowsTo", true, 1, null, "TextBlock", false, null),
    ];

    [TestMethod]
    public void Snapshots_OnlyEdgesWhoseTargetIsNotShownArePending_NamedByTheTextTypeName()
    {
        var pending = ReactorApi.PendingReferencesFrom(Snapshots());

        Assert.HasCount(3, pending);
        Assert.AreEqual(new ReactorPendingReference("XYFocusRight", false, 0, "Button", null), pending[0]);
        Assert.AreEqual(new ReactorPendingReference("LabeledBy", false, 0, null, "Title"), pending[1]);
        Assert.AreEqual(new ReactorPendingReference("FlowsTo", true, 1, "TextBlock", null), pending[2]);
    }

    [TestMethod]
    public void ThePendingLinesAreBuiltFromPropertyTypeAutomationIdAndListPosition()
    {
        using var answer = JsonDocument.Parse(ReactorProvider.DescribeReferences(ReactorApi.PendingReferencesFrom(Snapshots())));
        var shown = string.Join("|", answer.RootElement.GetProperty("edges").EnumerateArray().Select(edge => string.Join(",",
            new[] { "property", "targetType", "automationId", "list", "index" }
                .Select(name => edge.TryGetProperty(name, out var value) ? value.ToString() : "-"))));

        Assert.AreEqual("XYFocusRight,Button,-,-,-|LabeledBy,-,Title,-,-|FlowsTo,TextBlock,-,True,1", shown);
    }

    [TestMethod]
    public void AnUntypedSnapshotHasNoTargetType()
    {
        var pending = ReactorApi.PendingReferencesFrom(new object[]
        {
            new ReferenceEdgeSnapshot("reference#2", false, 0, null, null, false, null),
            new ReferenceEdgeSnapshot("binding#1", false, 0, null, "", false, null),
            new ReferenceEdgeSnapshot("XYFocusUp", false, 0, null, "ToggleButton", false, null),
        });

        Assert.HasCount(3, pending);
        Assert.IsNull(pending[0].TargetType, "an untyped ElementRef names no type");
        Assert.IsNull(pending[1].TargetType, "an empty name is no name");
        Assert.AreEqual("ToggleButton", pending[2].TargetType);
    }

    [TestMethod]
    public void TheAnswerNamesEachPendingTargetByTypeOrAutomationId()
    {
        using var answer = JsonDocument.Parse(ReactorProvider.DescribeReferences(ReactorApi.PendingReferencesFrom(Snapshots())));
        var root = answer.RootElement;

        Assert.AreEqual("ok", root.GetProperty("state").GetString());
        Assert.IsTrue(root.GetProperty("supported").GetBoolean());
        var edges = root.GetProperty("edges").EnumerateArray().ToArray();
        Assert.HasCount(3, edges);
        Assert.AreEqual("XYFocusRight", edges[0].GetProperty("property").GetString());
        Assert.AreEqual("Button", edges[0].GetProperty("targetType").GetString());
        Assert.IsFalse(edges[0].TryGetProperty("automationId", out _));
        Assert.AreEqual("Title", edges[1].GetProperty("automationId").GetString());
        Assert.IsFalse(edges[1].TryGetProperty("targetType", out _));
        Assert.AreEqual(1, edges[2].GetProperty("index").GetInt32());
        Assert.IsTrue(edges[2].GetProperty("list").GetBoolean());
    }

    [TestMethod]
    public void AnEdgeWithoutIsResolvedIsNotPending()
    {
        var pending = ReactorApi.PendingReferencesFrom(new object[]
        {
            new { Property = "LabeledBy", Target = (object?)null },
            new ReferenceEdgeSnapshot("DescribedBy", false, 0, null, null, false, null),
        });

        Assert.HasCount(1, pending);
        Assert.AreEqual("DescribedBy", pending[0].Property);
    }
}

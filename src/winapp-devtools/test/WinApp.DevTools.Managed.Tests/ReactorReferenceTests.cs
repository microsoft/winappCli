// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinApp.DevTools.Managed.Tests;

// Stand-ins with the shapes of Reactor's public reference edges. The agent reads them by member name, so these exercise
// the same reads without a Reactor that has the API.
//
// ReferenceEdgeSnapshot: the text-only shape Reactor ships (ExpectedTargetTypeName is a friendly name such as "Button").
public sealed record ReferenceEdgeSnapshot(string Property, bool IsList, int Index, string? TargetAutomationId,
    string? ExpectedTargetTypeName, bool IsResolved, object? Target);

// ReferenceEdgeDiagnostics: the shape of the local 0.0.0-devtools.7/.8 packages (a Type, plus Source, Slot and TargetRef).
public enum ReferenceEdgeSource { Modifier, Descriptor, Binding }

public sealed record ReferenceEdgeDiagnostics(string Property, ReferenceEdgeSource Source, int Slot, bool IsList, int Index,
    object? TargetRef, string? TargetAutomationId, Type? ExpectedTargetType, object? Target)
{
    public bool IsResolved => Target is not null;
}

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

    public static IReadOnlyList<ReferenceEdgeDiagnostics> Edges() =>
    [
        new("XYFocusRight", ReferenceEdgeSource.Modifier, 0, false, 0, new object(), null, typeof(Button), null),
        new("XYFocusDown", ReferenceEdgeSource.Modifier, 1, false, 0, new object(), null, typeof(Button), new object()),
        new("LabeledBy", ReferenceEdgeSource.Modifier, 2, false, 0, null, "Title", null, null),
        new("FlowsTo", ReferenceEdgeSource.Modifier, 3, true, 1, new object(), null, typeof(TextBlock), null),
    ];

    [TestMethod]
    public void Snapshots_OnlyEdgesWhoseTargetIsNotShownArePending_NamedByTheTextTypeName()
    {
        var pending = ReactorApi.PendingReferencesFrom(Snapshots());

        Assert.HasCount(3, pending);
        Assert.AreEqual(new ReactorPendingReference("XYFocusRight", null, false, 0, "Button", null), pending[0]);
        Assert.AreEqual(new ReactorPendingReference("LabeledBy", null, false, 0, null, "Title"), pending[1]);
        Assert.AreEqual(new ReactorPendingReference("FlowsTo", null, true, 1, "TextBlock", null), pending[2]);
    }

    [TestMethod]
    public void BothShapesDescribeTheSamePendingTargets()
    {
        // Only what "(not shown)" is built from: the property, the expected type, the AutomationId and the list position.
        static string Shown(System.Collections.IEnumerable edges)
        {
            using var answer = JsonDocument.Parse(ReactorProvider.DescribeReferences(ReactorApi.PendingReferencesFrom(edges)));
            return string.Join("|", answer.RootElement.GetProperty("edges").EnumerateArray().Select(edge => string.Join(",",
                new[] { "property", "targetType", "automationId", "list", "index" }
                    .Select(name => edge.TryGetProperty(name, out var value) ? value.ToString() : "-"))));
        }

        var current = Shown(Snapshots());
        Assert.AreEqual("XYFocusRight,Button,-,-,-|LabeledBy,-,Title,-,-|FlowsTo,TextBlock,-,True,1", current);
        Assert.AreEqual(current, Shown(Edges()), "the devtools.7/.8 shape shows the same lines");
    }

    [TestMethod]
    public void AnUntypedSnapshotHasNoTargetType_AndTheTextNameWinsOverAType()
    {
        var pending = ReactorApi.PendingReferencesFrom(new object[]
        {
            new ReferenceEdgeSnapshot("reference#2", false, 0, null, null, false, null),
            new ReferenceEdgeSnapshot("binding#1", false, 0, null, "", false, null),
            new { Property = "XYFocusUp", IsResolved = false, ExpectedTargetTypeName = "ToggleButton", ExpectedTargetType = typeof(Button) },
        });

        Assert.HasCount(3, pending);
        Assert.IsNull(pending[0].TargetType, "an untyped ElementRef names no type");
        Assert.IsNull(pending[1].TargetType, "an empty name is no name");
        Assert.AreEqual("ToggleButton", pending[2].TargetType);
    }

    [TestMethod]
    public void OnlyEdgesWhoseTargetIsNotShownArePending()
    {
        var pending = ReactorApi.PendingReferencesFrom(Edges());

        Assert.HasCount(3, pending);
        Assert.AreEqual(new ReactorPendingReference("XYFocusRight", "Modifier", false, 0, "Button", null), pending[0]);
        Assert.AreEqual(new ReactorPendingReference("LabeledBy", "Modifier", false, 0, null, "Title"), pending[1]);
        Assert.AreEqual(new ReactorPendingReference("FlowsTo", "Modifier", true, 1, "TextBlock", null), pending[2]);
    }

    [TestMethod]
    public void TheAnswerNamesEachPendingTargetByTypeOrAutomationId()
    {
        using var answer = JsonDocument.Parse(ReactorProvider.DescribeReferences(ReactorApi.PendingReferencesFrom(Edges())));
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
    public void AnEdgeWithoutIsResolvedIsPendingOnlyWithoutATarget()
    {
        var pending = ReactorApi.PendingReferencesFrom(new object[]
        {
            new { Property = "LabeledBy", Target = (object?)null },
            new { Property = "DescribedBy", Target = (object?)new object() },
            new { Target = (object?)null },
        });

        Assert.HasCount(1, pending);
        Assert.AreEqual("LabeledBy", pending[0].Property);
    }
}

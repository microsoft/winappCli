using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

internal static class AuditFixture
{
    public static readonly AuditTarget Target = new(1234, 42, "Fixture", DateTimeOffset.UtcNow, "Audit fixture", "[2A,2A]");

    public static AuditOptions Options(params string[] extra) =>
        AuditOptions.Parse(["scan", "--pid", "1234", "--no-screenshot", .. extra]);

    public static JsonElement Snapshot(bool partial = false, bool empty = false, int pid = 1234, object[]? nodes = null) =>
        JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1, capturedAtUtc = DateTimeOffset.UtcNow, processId = pid, windowHandle = 42,
            rootRuntimeId = "[2A,2A]", title = "Audit fixture", status = partial ? "partial" : "captured",
            maxDepth = 20, maxElements = 2000, depthLimitReached = partial, elementLimitReached = false,
            nodes = empty ? [] : nodes ?? new object[] { Node("n1", null, 0, "Window", "[2A,2A]"), Node("n2", "n1", 1, "Button", "[1,2,3]") },
            issues = Array.Empty<object>()
        });

    public static object Node(string id, string? parentId, int depth, string controlType, string runtimeId,
        object[]? runs = null, string? automationId = null, bool focusable = true, bool? offscreen = false) => new
        {
            id, parentId, depth, runtimeId, automationId, name = controlType == "Window" ? "Audit fixture" : "Label",
            controlType, frameworkId = "fixture", className = controlType,
            isPassword = false, isEnabled = true, isKeyboardFocusable = focusable, hasKeyboardFocus = false,
            isOffscreen = offscreen, bounds = new { left = 0, top = 0, width = 200, height = 60 },
            patterns = Array.Empty<string>(), textRuns = runs ?? [], issues = Array.Empty<object>()
        };

    public static object Field(double number) => new { status = "value", number, nativeType = "VT_I4" };
    public static object Run(uint foreground, uint background, double points = 12, double weight = 400,
        object? foregroundField = null) => new
        {
            id = "r1", bounds = Array.Empty<object>(),
            foregroundColor = foregroundField ?? Field(foreground),
            backgroundColor = Field(background),
            fontSize = new { status = "value", number = points, nativeType = "VT_R8" },
            fontWeight = Field(weight), isHidden = new { status = "value", boolean = false, nativeType = "VT_BOOL" }
        };

    public static AccessibilityScan Axe(bool failure = false)
    {
        var finding = ScanFixture.MissingName() with
        {
            ElementProperties = new Dictionary<string, string>
            {
                ["RuntimeId"] = "[1,2,3]", ["ControlType"] = "Button(50000)",
                ["ProcessId"] = "1234", ["IsPassword"] = "False"
            }
        };
        return failure ? ScanFixture.Create(finding) : ScanFixture.Create();
    }

    public static JsonElement Observation() => JsonSerializer.SerializeToElement(new
    {
        schemaVersion = 1, processId = 1234, windowHandle = 42,
        startedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1), endedAtUtc = DateTimeOffset.UtcNow,
        status = "completed", durationSec = 1, requestedTabSteps = 1, completedTabSteps = 2,
        maxEvents = 500, eventLimitReached = false, stopReason = "duration",
        focusTrace = new[]
        {
            new { sequence = 0, direction = "initial", observedAtUtc = DateTimeOffset.UtcNow, runtimeId = "[1,2,3]", isInTarget = true, repeat = false },
            new { sequence = 1, direction = "forward", observedAtUtc = DateTimeOffset.UtcNow, runtimeId = "[2A,2A]", isInTarget = true, repeat = false },
            new { sequence = 2, direction = "reverse", observedAtUtc = DateTimeOffset.UtcNow, runtimeId = "[1,2,3]", isInTarget = true, repeat = true }
        },
        events = Array.Empty<object>(), issues = Array.Empty<object>()
    });
}

[TestClass]
public sealed class AuditRulesTests
{
    [TestMethod]
    public void WindowScanDoesNotRequireAutomationIds()
    {
        var snapshot = AuditFixture.Snapshot();
        AuditRules.ValidateSnapshot(snapshot, AuditFixture.Target);
        var results = AuditRules.SnapshotResults(snapshot).ToArray();
        var identity = results.Single(result => result.CheckId == "uia.identifiers");
        Assert.AreEqual("observed", identity.Status);
        Assert.AreEqual("2", identity.Details["missingIdNodes"]);
        Assert.IsFalse(results.Any(result => result.Status == "rule-failure"));
    }

    [TestMethod]
    public void EmptyAndWrongWindowSnapshotsAreRejected()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => AuditRules.ValidateSnapshot(AuditFixture.Snapshot(empty: true), AuditFixture.Target));
        Assert.ThrowsExactly<InvalidDataException>(() => AuditRules.ValidateSnapshot(AuditFixture.Snapshot(pid: 2222), AuditFixture.Target));
        Assert.ThrowsExactly<InvalidDataException>(() => AuditRules.ValidateSnapshot(AuditFixture.Snapshot(), AuditFixture.Target with { WindowHandle = 99 }));
    }

    [TestMethod]
    public void PartialSnapshotIsNotPresentedAsComplete()
    {
        var snapshot = AuditFixture.Snapshot(partial: true);
        AuditRules.ValidateSnapshot(snapshot, AuditFixture.Target);
        Assert.AreEqual("inconclusive", AuditRules.SnapshotResults(snapshot).Single(result => result.CheckId == "capture.tree").Status);
    }

    [TestMethod]
    public void AxeFindingCanAssociateWithoutAnAutomationId()
    {
        var result = AuditRules.AxeResults(AuditFixture.Axe(true), AuditFixture.Snapshot(), AuditFixture.Target).Single();
        Assert.AreEqual("rule-failure", result.Status);
        Assert.AreEqual("NameNotNull", result.SourceRuleId);
        Assert.AreEqual("n2", result.ElementId);
    }

    [TestMethod]
    public void NoAxeFindingsIsNotAnAllRulesPass()
    {
        var result = AuditRules.AxeResults(AuditFixture.Axe(), AuditFixture.Snapshot(), AuditFixture.Target).Single();
        Assert.AreEqual("no-violations-reported", result.Status);
        StringAssert.Contains(result.Summary, "not an all-rules");
    }

    [TestMethod]
    public void ForeignProcessFindingIsNotAcceptedAsTargetEvidence()
    {
        var finding = ScanFixture.MissingName() with
        {
            ElementProperties = new Dictionary<string, string> { ["ProcessId"] = "5678", ["RuntimeId"] = "[1,2,3]" }
        };
        Assert.ThrowsExactly<InvalidDataException>(() =>
            AuditRules.AxeResults(ScanFixture.Create(finding), AuditFixture.Snapshot(), AuditFixture.Target).ToArray());
    }

    [TestMethod]
    public void NormalFocusLoopIsOnlyAnObservation()
    {
        var options = AuditFixture.Options("--tab-steps", "1", "--accept-input", "--observe-seconds", "1");
        var observation = AuditFixture.Observation();
        AuditRules.ValidateObservation(observation, AuditFixture.Target, options);
        var results = AuditRules.ObservationResults(observation, AuditFixture.Snapshot(), options).ToArray();
        Assert.IsTrue(results.All(result => result.Status == "observed"));
        StringAssert.Contains(results.Single(result => result.CheckId == "keyboard.repeats").Summary, "not keyboard-trap");
        Assert.AreEqual("", results.Single(result => result.CheckId == "keyboard.unvisited").Details["notVisitedNodeIds"]);
    }

    [TestMethod]
    public void DuplicateNodeIdsAndBrokenParentsAreRejected()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => AuditRules.ValidateSnapshot(AuditFixture.Snapshot(nodes:
            [AuditFixture.Node("n1", null, 0, "Window", "[1]"), AuditFixture.Node("n1", "n1", 1, "Button", "[2]")]), AuditFixture.Target));
        Assert.ThrowsExactly<InvalidDataException>(() => AuditRules.ValidateSnapshot(AuditFixture.Snapshot(nodes:
            [AuditFixture.Node("n1", null, 0, "Window", "[1]"), AuditFixture.Node("n2", "missing", 1, "Button", "[2]")]), AuditFixture.Target));
    }
}

[TestClass]
public sealed class TextContrastTests
{
    [TestMethod]
    public void KnownColorRatiosAndColorrefByteOrder()
    {
        Assert.AreEqual(21d, TextContrast.Ratio(0, 0xFFFFFF), 1e-10);
        Assert.AreEqual(1d, TextContrast.Ratio(0x808080, 0x808080), 1e-10);
        Assert.IsTrue(TextContrast.Ratio(0x0000FF, 0xFFFFFF) < TextContrast.Ratio(0xFF0000, 0xFFFFFF));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => TextContrast.Ratio(0xFF000000, 0));
    }

    [TestMethod]
    public void LargeTextUsesPointsAndRealBoldWeight()
    {
        Assert.AreEqual(4.5, TextContrast.Threshold(17.999, 400));
        Assert.AreEqual(3, TextContrast.Threshold(18, 400));
        Assert.AreEqual(4.5, TextContrast.Threshold(14, 600));
        Assert.AreEqual(3, TextContrast.Threshold(14, 700));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => TextContrast.Threshold(double.NaN, 400));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => TextContrast.Threshold(0, 400));
    }

    [TestMethod]
    public void PoorContrastInEnabledEditableTextHasSpecificTechnicalFailure()
    {
        var snapshot = AuditFixture.Snapshot(nodes:
        [
            AuditFixture.Node("n1", null, 0, "Window", "[1]"),
            AuditFixture.Node("n2", "n1", 1, "Edit", "[2]", [AuditFixture.Run(0x777777, 0xFFFFFF)])
        ]);
        var result = TextContrast.Evaluate(snapshot).Single();
        Assert.AreEqual("rule-failure", result.Status);
        Assert.AreEqual("below-threshold", result.Details["comparison"]);
        Assert.IsTrue(double.Parse(result.Details["ratio"], System.Globalization.CultureInfo.InvariantCulture) < 4.5);
    }

    [TestMethod]
    public void TextWithoutEstablishedExceptionsIsAnObservationNotAComplianceFailure()
    {
        var snapshot = AuditFixture.Snapshot(nodes:
            [AuditFixture.Node("n1", null, 0, "Text", "[1]", [AuditFixture.Run(0x777777, 0xFFFFFF)])]);
        var result = TextContrast.Evaluate(snapshot).Single();
        Assert.AreEqual("observed", result.Status);
        Assert.AreEqual("below-threshold", result.Details["comparison"]);
        StringAssert.Contains(result.Details["applicability"], "exceptions are not established");
    }

    [TestMethod]
    public void UnknownOffscreenDoesNotDiscardReliableNumericContrast()
    {
        var snapshot = AuditFixture.Snapshot(nodes:
            [AuditFixture.Node("n1", null, 0, "Edit", "[1]", [AuditFixture.Run(0xAAAAAA, 0xBEBEBE)], offscreen: null)]);
        var result = TextContrast.Evaluate(snapshot).Single();
        Assert.AreEqual("observed", result.Status);
        Assert.AreEqual("below-threshold", result.Details["comparison"]);
        Assert.IsTrue(result.Details.ContainsKey("ratio"));
        StringAssert.Contains(result.Details["applicability"], "Visibility");
    }

    [TestMethod]
    public void MissingFontDoesNotDiscardAColorRatioOrInventAThreshold()
    {
        var run = new
        {
            id = "r1", bounds = Array.Empty<object>(),
            foregroundColor = AuditFixture.Field(0), backgroundColor = AuditFixture.Field(0xFFFFFF),
            fontSize = new { status = "unsupported" }, fontWeight = new { status = "unsupported" },
            isHidden = new { status = "value", boolean = false }
        };
        var snapshot = AuditFixture.Snapshot(nodes:
            [AuditFixture.Node("n1", null, 0, "Edit", "[1]", [run])]);
        var result = TextContrast.Evaluate(snapshot).Single();
        Assert.AreEqual("observed", result.Status);
        Assert.AreEqual("21", result.Details["ratio"]);
        Assert.AreEqual("not-evaluated", result.Details["comparison"]);
    }

    [TestMethod]
    [DataRow("mixed")]
    [DataRow("unsupported")]
    [DataRow("error")]
    [DataRow("withheld")]
    public void UnknownColorsNeverDefaultToBlackOrPass(string status)
    {
        var snapshot = AuditFixture.Snapshot(nodes:
        [
            AuditFixture.Node("n1", null, 0, "Edit", "[1]",
                [AuditFixture.Run(0, 0xFFFFFF, foregroundField: new { status })])
        ]);
        var result = TextContrast.Evaluate(snapshot).Single();
        Assert.AreEqual("inconclusive", result.Status);
        Assert.IsFalse(result.Details.ContainsKey("ratio"));
    }
}

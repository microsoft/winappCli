using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

internal static class ScanFixture
{
    public static AccessibilityFinding MissingName(string id = "SaveButton", string runtimeId = "[1,2,3]") =>
        new("NameNotNull", "A focusable element must have a Name.",
            "Provide a concise accessible name.", "ObjectInformation",
            new Dictionary<string, string>
            {
                ["AutomationId"] = id, ["RuntimeId"] = runtimeId,
                ["ControlType"] = "Button(50000)", ["ProcessId"] = "1234"
            }, ["InvokePattern"]);

    public static AccessibilityScan Create(params AccessibilityFinding[] findings) =>
        AccessibilityScan.Create(1234, 42, "2.4.2", [new(findings.Length, findings)]);
}

[TestClass]
public sealed class ScannerTests
{
    [TestMethod]
    public void NormalizesRepeatedSdkGroupsByRuntimeIdentityWithinOneScan()
    {
        var finding = ScanFixture.MissingName();
        var result = AccessibilityScan.Create(1234, 42, "2.4.2",
            [new(1, [finding]), new(1, [finding]), new(0, [])]);
        Assert.AreEqual(2, result.RawErrorCount);
        Assert.HasCount(3, result.ResultGroupErrorCounts);
        Assert.HasCount(1, result.Findings);
        Assert.AreEqual("NameNotNull", result.Findings[0].RuleId);
        Assert.AreEqual("SaveButton", result.Findings[0].AutomationId);
    }

    [TestMethod]
    public void SameAutomationIdOnDifferentRuntimeElementsIsNotMerged()
    {
        var result = ScanFixture.Create(ScanFixture.MissingName(), ScanFixture.MissingName(runtimeId: "[4,5,6]"));
        Assert.HasCount(2, result.Findings);
    }

    [TestMethod]
    public void MissingRuntimeIdDoesNotHideSeparateFindings()
    {
        var finding = ScanFixture.MissingName() with
        {
            ElementProperties = new Dictionary<string, string> { ["AutomationId"] = "SaveButton" }
        };
        Assert.HasCount(2, ScanFixture.Create(finding, finding).Findings);
    }

    [TestMethod]
    public void EmptyOrInconsistentScansAreNeverAccepted()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => AccessibilityScan.Create(1234, 42, "2.4.2", []));
        Assert.ThrowsExactly<InvalidDataException>(() => AccessibilityScan.Create(1234, 42, "2.4.2", [new(1, [])]));
        Assert.ThrowsExactly<InvalidDataException>(() => AccessibilityScan.Create(1234, 0, "2.4.2", [new(0, [])]));
        Assert.ThrowsExactly<InvalidDataException>(() => AccessibilityScan.Create(1234, 42, "", [new(0, [])]));
    }

    [TestMethod]
    public void NonemptySuccessfulScanCanReportZeroViolations()
    {
        var result = ScanFixture.Create();
        Assert.HasCount(1, result.ResultGroupErrorCounts);
        Assert.AreEqual(0, result.RawErrorCount);
        Assert.HasCount(0, result.Findings);
    }
}

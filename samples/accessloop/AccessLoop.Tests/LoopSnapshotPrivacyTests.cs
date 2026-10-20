using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class LoopSnapshotPrivacyTests
{
    [TestMethod]
    public void ExplicitPrivacyWithholdingUsesSeparateStableControlWitnessWithoutRewritingTheSnapshot()
    {
        using var fixture = new LoopTestEvidence();
        var round = fixture.AddRound("after-02", "BasicIconButton", "ScrollBottomButton");
        Withhold(fixture, round, "withheld");

        var verified = LoopEvidence.VerifyRound(fixture.Report.Profile, round, fixture.Root);

        Assert.IsTrue(verified.NameChecks.All(check => check.Passed));
        Assert.IsTrue(verified.NameChecks.All(check => check.EvidenceSource.Contains("snapshot-name-privacy-withheld", StringComparison.Ordinal)));
        Assert.AreEqual("partial", verified.CaptureStatus);
        Assert.IsTrue(verified.States.SelectMany(state => state.Scan.Snapshot!.Value.GetProperty("nodes").EnumerateArray())
            .Where(node => fixture.Report.Profile.RepairTargets.Any(target => target.AutomationId == AuditJson.OptionalString(node, "automationId")))
            .All(node => node.GetProperty("name").ValueKind == System.Text.Json.JsonValueKind.Null));
    }

    [TestMethod]
    [DataRow("missing-reason")]
    [DataRow("unknown-target-privacy")]
    [DataRow("password-target")]
    public void UnknownOrPasswordTargetCannotUseTheHostPrivacyException(string condition)
    {
        using var fixture = new LoopTestEvidence();
        var round = fixture.AddRound("after-02", "BasicIconButton", "ScrollBottomButton");
        Withhold(fixture, round, condition);
        Assert.ThrowsExactly<InvalidDataException>(() => LoopEvidence.VerifyRound(fixture.Report.Profile, round, fixture.Root));
    }

    private static void Withhold(LoopTestEvidence fixture, LoopRoundResult round, string condition)
    {
        foreach (var state in round.States)
        {
            var scan = AuditFiles.ReadVerified(Path.Combine(state.Directory, "scan.json"));
            var snapshot = AuditTestFixture.Edit(scan.Snapshot!.Value, json =>
            {
                foreach (var node in json["nodes"]!.AsArray().OfType<JsonObject>())
                {
                    var id = node["automationId"]?.GetValue<string>();
                    if (!fixture.Report.Profile.RepairTargets.Any(target => target.AutomationId == id)) continue;
                    node["name"] = null;
                    node["issues"] = condition == "missing-reason" ? new JsonArray() : new JsonArray(new JsonObject
                    {
                        ["code"] = "privacy_withheld", ["message"] = "A host has indeterminate privacy; name withheld."
                    });
                    if (condition == "unknown-target-privacy") node["isPassword"] = null;
                    if (condition == "password-target") node["isPassword"] = true;
                }
            });
            fixture.SaveScan(state, scan with { Snapshot = snapshot, Status = "partial" });
        }
    }
}

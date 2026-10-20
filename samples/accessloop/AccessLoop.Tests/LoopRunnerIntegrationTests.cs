using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class LoopRunnerIntegrationTests
{
    [TestMethod]
    public async Task PresetIsActuallyReproducedBeforeSerialSourceRepairsAndUnchangedRouteChecks()
    {
        using var fixture = new LoopTestEvidence();
        var calls = new List<string>();
        var runner = new LoopRunner(new FixtureRunner(fixture, calls));
        var proposals = 0;
        await runner.RunAsync(fixture.Report, true, "reviewed-file", (candidate, target, evidence, directory, token) =>
        {
            proposals++;
            Assert.AreEqual(proposals + 1, calls.Count);
            Assert.IsNotNull(evidence.Axe);
            Assert.IsTrue(evidence.Axe.Findings.Any(finding => finding.AutomationId == target.AutomationId));
            return Task.FromResult(new RepairProposal(candidate.RelativePath, candidate.Sha256,
                target.AutomationId, target.ExpectedName, "Test-only reviewed Name proposal."));
        }, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "baseline", "reproduction", "after-01", "after-02" }, calls);
        Assert.AreEqual(2, proposals);
        Assert.AreEqual("passed", fixture.Report.ReproductionStatus, fixture.Report.Error);
        Assert.AreEqual("passed", fixture.Report.VerificationStatus, fixture.Report.Error);
        Assert.AreEqual("partial", fixture.Report.CaptureStatus);
        Assert.AreEqual("partial", fixture.Report.Status);
        Assert.IsTrue(fixture.Report.Repairs.All(repair => repair.Status == "fixed"));
        var original = LoopEvidence.VerifyRound(fixture.Report.Profile, fixture.Report.Rounds[0], fixture.Root);
        var repaired = LoopEvidence.VerifyRound(fixture.Report.Profile, fixture.Report.Rounds[^1], fixture.Root);
        Assert.AreEqual(2, original.NameChecks.Count(check => !check.Passed));
        Assert.AreEqual(0, repaired.NameChecks.Count(check => !check.Passed));
        Assert.AreEqual(3, original.Findings.Sum(finding => finding.Occurrences.Count));
        Assert.AreEqual(2, original.Findings.Count);
        Assert.AreEqual(0, repaired.Findings.Count);
        Assert.IsTrue(File.Exists(fixture.Report.Repairs[0].DiffFile));
    }

    [TestMethod]
    public async Task BaselineCanBeReproducedFromItsArchiveAfterTheWorkingCopyWasFixed()
    {
        using var fixture = new LoopTestEvidence();
        var runner = new LoopRunner(new FixtureRunner(fixture, []));
        await runner.RunAsync(fixture.Report, true, "reviewed-file", (candidate, target, evidence, directory, token) =>
            Task.FromResult(new RepairProposal(candidate.RelativePath, candidate.Sha256, target.AutomationId,
                target.ExpectedName, "Test-only reviewed Name proposal.")), CancellationToken.None);
        Assert.AreEqual("passed", fixture.Report.VerificationStatus, fixture.Report.Error);
        var baseline = LoopEvidence.VerifyRound(fixture.Report.Profile, fixture.Report.Rounds[0], fixture.Root);
        var replayRoot = Path.Combine(fixture.Root, "separate-reproduction");
        Directory.CreateDirectory(replayRoot);
        var archivedProject = Path.Combine(baseline.Round.SourceDirectory!, Path.GetFileName(fixture.Report.Profile.Project));
        var copy = LoopSourceWorkspace.Copy(archivedProject, Path.Combine(replayRoot, "workspace"));
        var profile = fixture.Report.Profile with { Project = copy };
        AuditFiles.WriteNewBytes(Path.Combine(replayRoot, "plan.json"), LoopInput.PlanBytes(profile));
        var report = new LoopReport
        {
            RunId = "independent-replay", Name = "Baseline replay", Directory = replayRoot, Profile = profile,
            PlanSha256 = LoopInput.PlanHash(profile), Tools = fixture.Report.Tools,
            ReplayOf = fixture.Root + "#baseline"
        };
        var replayRunner = new LoopRunner(new FixtureRunner(fixture, []));
        await replayRunner.ReplayAsync(report, baseline, CancellationToken.None);
        Assert.AreEqual("passed", report.ReproductionStatus, report.Error);
        Assert.AreEqual("failed", report.VerificationStatus);
        Assert.AreEqual(0, report.Repairs.Count);
        Assert.AreEqual(2, LoopEvidence.VerifyRound(profile, report.Rounds.Single(), replayRoot).NameChecks.Count(check => !check.Passed));
    }

    [TestMethod]
    public void AChangedReviewedKeyCannotKeepAKeyboardVerificationClaim()
    {
        using var fixture = new LoopTestEvidence();
        var round = fixture.AddRound("baseline");
        var action = round.Actions.Single(item => item.Step.Kind == "key");
        var inputIndex = action.Commands.FindIndex(command => command.Operation == "send-keys");
        var input = action.Commands[inputIndex];
        action.Commands[inputIndex] = input with { Arguments = ["send-keys", "enter", "--via", "send-input"] };
        LoopTestEvidence.SaveActions(round);
        Assert.AreEqual("Tab", action.Step.Keys);
        Assert.AreEqual("Tab", fixture.Report.Profile.Steps.Single(step => step.Id == action.Step.Id).Keys);
        var error = Assert.ThrowsExactly<InvalidDataException>(() => LoopEvidence.VerifyRound(fixture.Report.Profile, round, fixture.Root));
        StringAssert.Contains(error.Message, "frozen key");
    }

    private sealed class FixtureRunner(LoopTestEvidence fixture, List<string> calls) : ILoopRoundRunner
    {
        public Task<LoopRoundResult> RunAsync(LoopProfile profile, string roundId, string outputDirectory, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            calls.Add(roundId);
            var root = Path.GetDirectoryName(Path.GetDirectoryName(outputDirectory))!;
            var archive = Path.Combine(root, "sources", roundId);
            return Task.FromResult(fixture.BuildRound(profile, roundId, outputDirectory, archive));
        }
    }
}

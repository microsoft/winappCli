using System.Text;
using System.Text.Json;

namespace AccessLoop;

public delegate Task<RepairProposal> LoopRepairAdvisor(
    SourceCandidate candidate, LoopRepairTarget target, AuditReport evidence,
    string outputDirectory, CancellationToken token);

public sealed class LoopRunner(
    ILoopRoundRunner rounds, Action<LoopProgress>? progress = null, Action? verifyInput = null)
{
    public async Task RunAsync(LoopReport report, bool apply, string proposalSource,
        LoopRepairAdvisor? advisor, CancellationToken token)
    {
        var profile = report.Profile;
        try
        {
            var baseline = await CaptureAsync(report, "baseline", token);
            SaveProgress(report);
            var reproduced = await CaptureAsync(report, "reproduction", token);
            RequireEquivalent(baseline, reproduced);
            report.ReproductionStatus = "passed";
            var current = reproduced;
            if (!apply)
            {
                Finish(report, current);
                return;
            }
            if (advisor is null)
                throw new InvalidOperationException("Applying a repair requires an explicitly configured advisor.");
            if (current.Findings.Any(finding => !finding.MappedTarget))
                throw new InvalidDataException("The baseline includes unmapped findings. Review them before applying this prototype's bounded source repairs.");
            foreach (var target in profile.RepairTargets)
            {
                var name = current.NameChecks.Single(check => check.AutomationId == target.AutomationId);
                if (name.Passed)
                    continue;
                token.ThrowIfCancellationRequested();
                verifyInput?.Invoke();
                LoopSourceWorkspace.RequireSources(profile.Project, current.Round.Sources);
                var folder = Path.Combine(report.Directory, "repairs", $"{report.Repairs.Count + 1:D2}-{target.AutomationId}");
                Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
                AuditFiles.CreateNewDirectory(folder);
                var repair = new LoopRepairResult
                {
                    AutomationId = target.AutomationId, ExpectedName = target.ExpectedName, Directory = folder,
                    ProposalSource = proposalSource, Status = "proposing"
                };
                report.Repairs.Add(repair);
                SaveProgress(report);
                progress?.Invoke(new(current.Round.Id, "proposal-started", target.AutomationId));
                AppliedRepair? patch = null;
                try
                {
                    var source = SourceRepair.Locate(profile.Project, target.AutomationId);
                    if (source.Count != 1)
                        throw new InvalidDataException("The failed control must map to exactly one supported static XAML Button.");
                    var state = current.States.Single(item => item.State.StepId == target.CheckpointId);
                    var axe = state.Scan.Axe ?? throw new InvalidDataException("A real Axe result is required before asking for a repair.");
                    var failures = axe.Findings.Where(finding => finding.AutomationId == target.AutomationId).ToArray();
                    if (failures.Length != 1 || failures[0].RuleId is not ("NameNotNull" or "NameNotEmpty" or "NameNotWhiteSpace")
                        || !string.IsNullOrWhiteSpace(name.Actual))
                        throw new InvalidDataException("This prototype repairs only a corroborated missing-name failure, not arbitrary label differences.");
                    AuditFiles.WriteNew(Path.Combine(folder, "source-candidate.json"), source[0]);
                    var proposal = await advisor(source[0], target, state.Scan, folder, token);
                    verifyInput?.Invoke();
                    LoopSourceWorkspace.RequireSources(profile.Project, current.Round.Sources);
                    repair.Proposal = proposal;
                    AuditFiles.WriteNew(Path.Combine(folder, "proposal.json"), proposal);
                    patch = SourceRepair.Apply(source[0], proposal, target.ExpectedName);
                    var acceptedSources = LoopSourceWorkspace.WithPatch(profile.Project, current.Round.Sources, patch);
                    var diff = Path.Combine(folder, "repair.diff");
                    AuditFiles.WriteNewBytes(diff, Encoding.UTF8.GetBytes(patch.Diff));
                    repair.DiffFile = diff;
                    repair.Status = "applied";
                    progress?.Invoke(new(current.Round.Id, "repair-applied", target.AutomationId, Message: diff));
                    SaveProgress(report);
                    var afterId = $"after-{report.Repairs.Count:D2}";
                    repair.VerificationRoundId = afterId;
                    var after = await CaptureAsync(report, afterId, token);
                    if (!LoopEvidence.SameSources(acceptedSources, after.Round.Sources))
                        throw new InvalidDataException("The verification build did not use the accepted source version.");
                    RequireImprovement(current, after, target.AutomationId);
                    repair.Status = "fixed";
                    current = after;
                    progress?.Invoke(new(afterId, "repair-verified", target.AutomationId));
                    SaveProgress(report);
                }
                catch (Exception exception) when (AuditRunner.IsExecutionFailure(exception))
                {
                    repair.Error = exception.Message;
                    repair.Status = "failed";
                    if (patch is not null)
                    {
                        try
                        {
                            SourceRepair.Rollback(patch);
                            LoopSourceWorkspace.RequireSources(profile.Project, current.Round.Sources);
                            repair.Status = "rolled-back";
                        }
                        catch (Exception rollbackError) when (AuditRunner.IsExecutionFailure(rollbackError))
                        {
                            repair.Status = "rollback-blocked";
                            repair.Error += $" Rollback was refused: {rollbackError.Message}";
                        }
                    }
                    throw new InvalidOperationException($"Repair '{target.AutomationId}' was not verified: {repair.Error}", exception);
                }
            }
            Finish(report, current);
        }
        catch (Exception exception) when (AuditRunner.IsExecutionFailure(exception))
        {
            report.Status = "blocked";
            report.VerificationStatus = "inconclusive";
            if (report.ReproductionStatus == "not-run")
                report.ReproductionStatus = "inconclusive";
            report.CaptureStatus = "partial";
            report.Error = exception is OperationCanceledException ? "The loop was cancelled; completed evidence was retained." : exception.Message;
        }
        finally
        {
            report.FinishedAtUtc = DateTimeOffset.UtcNow;
            SaveProgress(report);
        }
    }

    public async Task ReplayAsync(LoopReport report, LoopVerifiedRound original, CancellationToken token)
    {
        try
        {
            var replay = await CaptureAsync(report, "replay", token);
            RequireEquivalent(original, replay);
            report.ReproductionStatus = "passed";
            Finish(report, replay);
        }
        catch (Exception exception) when (AuditRunner.IsExecutionFailure(exception))
        {
            report.Status = "blocked";
            report.ReproductionStatus = "inconclusive";
            report.VerificationStatus = "inconclusive";
            report.CaptureStatus = "partial";
            report.Error = exception is OperationCanceledException ? "Replay was cancelled; completed evidence was retained." : exception.Message;
        }
        finally
        {
            report.FinishedAtUtc = DateTimeOffset.UtcNow;
            SaveProgress(report);
        }
    }

    private async Task<LoopVerifiedRound> CaptureAsync(LoopReport report, string id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        verifyInput?.Invoke();
        if (LoopInput.PlanHash(report.Profile) != report.PlanSha256)
            throw new InvalidDataException("The in-memory acceptance route changed.");
        var project = LoopEvidence.BoundFile(report.Directory, report.Profile.Project);
        var sources = JourneyInput.CaptureSources(project);
        var sourceDirectory = Path.Combine(report.Directory, "sources", id);
        _ = LoopSourceWorkspace.Copy(project, sourceDirectory);
        var roundDirectory = Path.Combine(report.Directory, "rounds", id);
        Directory.CreateDirectory(Path.GetDirectoryName(roundDirectory)!);
        LoopRoundResult round;
        try
        {
            round = await rounds.RunAsync(report.Profile, id, roundDirectory, token);
        }
        catch (Exception exception) when (AuditRunner.IsExecutionFailure(exception))
        {
            round = new()
            {
                Id = id, Directory = roundDirectory, Status = "blocked",
                FinishedAtUtc = DateTimeOffset.UtcNow, Sources = sources, Error = exception.Message
            };
        }
        round.SourceDirectory = sourceDirectory;
        report.Rounds.Add(round);
        SaveProgress(report);
        if (round.Status != "completed")
            throw new InvalidOperationException($"Round '{id}' stopped: {round.Error ?? "The prescribed route was not completed."}");
        if (round.Id != id || Path.GetFullPath(round.Directory) != Path.GetFullPath(roundDirectory)
            || !LoopEvidence.SameSources(sources, round.Sources))
            throw new InvalidDataException("The executor changed the round's identity or source binding.");
        LoopSourceWorkspace.RequireSources(project, sources);
        verifyInput?.Invoke();
        return LoopEvidence.VerifyRound(report.Profile, round, report.Directory);
    }

    public static void RequireEquivalent(LoopVerifiedRound original, LoopVerifiedRound replay)
    {
        if (!LoopEvidence.SameSources(original.Round.Sources, replay.Round.Sources))
            throw new InvalidDataException("Reproduction must use the exact recorded source version.");
        if (original.Findings.Any(finding => !finding.MappedTarget) || replay.Findings.Any(finding => !finding.MappedTarget))
            throw new InvalidDataException("Unmapped findings cannot be proven to be the same controls across processes. Their reproduction needs review.");
        if (!NameSignature(original).SequenceEqual(NameSignature(replay), StringComparer.Ordinal)
            || !FindingSignature(original).SequenceEqual(FindingSignature(replay), StringComparer.Ordinal))
            throw new InvalidDataException("The replay did not reproduce the same name contracts and rule findings at the same checkpoints.");
    }

    public static void RequireImprovement(LoopVerifiedRound before, LoopVerifiedRound after, string targetId)
    {
        var fixedName = after.NameChecks.Single(check => check.AutomationId == targetId);
        if (!fixedName.Passed || after.Findings.Any(finding => finding.AutomationId == targetId
            && finding.RuleId is "NameNotNull" or "NameNotEmpty" or "NameNotWhiteSpace"))
            throw new InvalidDataException("The repaired target still fails its frozen name/rule checks.");
        foreach (var previous in before.NameChecks.Where(check => check.AutomationId != targetId))
        {
            var current = after.NameChecks.Single(check => check.AutomationId == previous.AutomationId);
            if (current.Actual != previous.Actual || current.Passed != previous.Passed)
                throw new InvalidDataException("An unrelated target's name contract changed during this repair.");
        }
        var expected = FindingSignature(before, excludingTarget: targetId);
        var observed = FindingSignature(after);
        if (!observed.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidDataException("The same route reported an unexpected new or changed rule finding.");
    }

    private static IEnumerable<string> NameSignature(LoopVerifiedRound round) =>
        round.NameChecks.Select(check => JsonSerializer.Serialize(new { check.AutomationId, check.Expected, check.Actual, check.Passed }, Json.Options))
            .Order(StringComparer.Ordinal);

    private static IEnumerable<string> FindingSignature(LoopVerifiedRound round, string? excludingTarget = null) =>
        round.Findings.Where(finding => excludingTarget is null || finding.AutomationId != excludingTarget)
            .SelectMany(finding => finding.Occurrences.Select(occurrence =>
                JsonSerializer.Serialize(new { occurrence.CheckpointId, occurrence.RuleId, occurrence.AutomationId, occurrence.ControlType }, Json.Options)))
            .Order(StringComparer.Ordinal);

    private static void Finish(LoopReport report, LoopVerifiedRound last)
    {
        report.VerificationStatus = last.NameChecks.All(check => check.Passed) && last.Findings.Count == 0 ? "passed" : "failed";
        report.CaptureStatus = report.Rounds.SelectMany(round => round.States).All(state => state.CaptureStatus == "completed")
            ? "completed" : "partial";
        report.Status = report.CaptureStatus == "partial" ? "partial"
            : report.VerificationStatus == "passed" ? "completed" : "failed";
    }

    internal static void SaveProgress(LoopReport report)
    {
        var temporary = Path.Combine(report.Directory, $"loop-progress-{Guid.NewGuid():N}.json");
        try
        {
            AuditFiles.WriteNew(temporary, report);
            File.Move(temporary, Path.Combine(report.Directory, "loop-progress.json"), overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}

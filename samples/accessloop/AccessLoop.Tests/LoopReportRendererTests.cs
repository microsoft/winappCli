using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed partial class LoopReportRendererTests
{
    [TestMethod]
    public void ReusableBuilderReturnsAnUnregisteredRoundAndPreservesItsSourceArchive()
    {
        var archive = Path.Combine(Root, "sources", "controller-baseline");
        var project = LoopTestEvidence.WriteSources(archive, []);
        var sources = JourneyInput.CaptureSources(project);
        var before = JsonSerializer.Serialize(report, Json.Options);

        var round = evidence.BuildRound(report.Profile, "controller-baseline",
            Path.Combine(Root, "rounds", "controller-baseline"), archive);
        var verified = LoopEvidence.VerifyRound(report.Profile, round, Root);

        Assert.AreEqual(before, JsonSerializer.Serialize(report, Json.Options));
        Assert.IsTrue(LoopEvidence.SameSources(sources, JourneyInput.CaptureSources(project)));
        Assert.AreEqual(4, verified.States.Count);
        Assert.AreEqual(3, verified.Findings.Sum(finding => finding.Occurrences.Count));
        Assert.IsTrue(round.Actions.All(action => action.Before!.Value.TryGetProperty("elementId", out _)));
    }

    [TestMethod]
    public void ReusableBuilderReadsActualNamesFromTheSelectedArchivedSourceVersion()
    {
        var baselineSource = LoopSourceWorkspace.Copy(report.Profile.Project, Path.Combine(Root, "sources", "controller-before"));
        var baseline = evidence.BuildRound(report.Profile, "controller-before", Path.Combine(Root, "rounds", "controller-before"),
            Path.GetDirectoryName(baselineSource)!);
        var candidate = SourceRepair.Locate(report.Profile.Project, "BasicIconButton").Single();
        _ = SourceRepair.Apply(candidate, new(candidate.RelativePath, candidate.Sha256, candidate.AutomationId,
            "Refresh preview", "Give the fixture button its reviewed name."), "Refresh preview");
        var afterSource = LoopSourceWorkspace.Copy(report.Profile.Project, Path.Combine(Root, "sources", "controller-after"));

        var after = evidence.BuildRound(report.Profile, "controller-after", Path.Combine(Root, "rounds", "controller-after"),
            Path.GetDirectoryName(afterSource)!);

        LoopRunner.RequireImprovement(LoopEvidence.VerifyRound(report.Profile, baseline, Root),
            LoopEvidence.VerifyRound(report.Profile, after, Root), "BasicIconButton");
        Assert.AreEqual(0, report.Rounds.Count);
    }

    [TestMethod]
    public void PassiveKeyboardNotTestedIsPreservedAlongsideLoopLevelKeyEvidence()
    {
        var round = Round("baseline");
        var original = round.States.ToDictionary(state => Path.Combine(state.Directory, "scan.json"), state =>
            File.ReadAllBytes(Path.Combine(state.Directory, "scan.json")));

        var html = Render();

        StringAssert.Contains(Section(html, "rounds"), "键盘 not-tested 保持原样");
        StringAssert.Contains(Section(html, "rounds"), "实际路线的按键与期望检查来自本轮 actions.json");
        Assert.IsTrue(round.Actions.Any(action => action.Step.Kind == "key" && action.Commands.Any(command => command.Operation == "send-keys")));
        foreach (var (path, bytes) in original)
        {
            Assert.AreEqual("not-tested", AuditFiles.ReadVerified(path).Results.Single(result => result.CheckId == "keyboard.path").Status);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        }
    }

    [TestMethod]
    public void RepeatedStaticTargetGroupsKeepEveryOriginalCheckpointAndScreenshotLink()
    {
        var baseline = Round("baseline");
        Round("reproduction");

        var html = Render();

        Count(html, "round-0-mapped", 2);
        Count(html, "round-0-unmapped", 0);
        Count(html, "round-0-failures", 3);
        Count(html, "current-pages", 3);
        Count(html, "current-states", 4);
        Count(html, "current-failures", 3);
        foreach (var state in baseline.States.Where(state => state.RuleFailureOccurrences > 0))
            StringAssert.Contains(html, LinkTo(state.Directory, "report.html") + "#result-0");
        StringAssert.Contains(html, "2 次出现");
        StringAssert.Contains(html, "实际动作、相同源码和问题出现记录已核对");
        StringAssert.Contains(html, "原始结果 1 与图片");
        Assert.IsFalse(html.Contains("<img", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("data:image", StringComparison.OrdinalIgnoreCase));
        foreach (var state in baseline.States)
            StringAssert.Contains(File.ReadAllText(Path.Combine(state.Directory, "report.html")), "data:image/png;base64,");
        AssertLinks(html);
    }

    [TestMethod]
    public void CachedSummariesNeverOverrideTheSharedVerifierOrMutateTheReport()
    {
        var round = Round("baseline");
        round.States[0].RuleFailureOccurrences = 9918273;
        round.States[0].CaptureStatus = "completed";
        report.CaptureStatus = "completed";
        var before = JsonSerializer.Serialize(report, Json.Options);

        var html = Render();

        AssertSharedResult(round, html);
        Assert.IsFalse(html.Contains("9918273", StringComparison.Ordinal));
        Assert.AreEqual(before, JsonSerializer.Serialize(report, Json.Options));
    }

    [TestMethod]
    public void PassingTargetFixesRemainPartialAndTruthfullySeparateCopilotFromReviewedFiles()
    {
        Round("baseline");
        Round("reproduction");
        Round("after-01", "BasicIconButton");
        Round("after-02", "BasicIconButton", "ScrollBottomButton");
        var first = Repair("BasicIconButton", "after-01", "copilot");
        var second = Repair("ScrollBottomButton", "after-02");
        report.VerificationStatus = "passed";

        var html = Render();
        var summary = Section(html, "summary");

        Count(html, "current-names", 2);
        Count(html, "current-failures", 0);
        StringAssert.Contains(summary, "同路线规则验证已满足");
        StringAssert.Contains(summary, "部分采集（partial）");
        StringAssert.Contains(summary, "不等于整应用或 WCAG 通过");
        Assert.IsFalse(summary.Contains(report.RunId, StringComparison.Ordinal));
        Assert.IsFalse(summary.Contains(report.PlanSha256, StringComparison.Ordinal));
        StringAssert.Contains(Section(html, "repairs"), "Copilot 提案（已保存响应）");
        StringAssert.Contains(Section(html, "repairs"), "人工审阅的提案文件（不是本次 AI 生成）");
        StringAssert.Contains(html, LinkTo(first.Directory, "repair.diff"));
        StringAssert.Contains(html, LinkTo(second.Directory, "proposal.json"));
        StringAssert.Contains(html, "href=\"#round-2\"");
        StringAssert.Contains(html, "href=\"#round-3\"");
        AssertLinks(html);
    }

    [TestMethod]
    [DataRow("duplicate-runtime")]
    [DataRow("wrong-native-id")]
    [DataRow("missing-optional-witness")]
    public void SharedGroupingIsDisplayedWithoutRendererReclassification(string condition)
    {
        var round = Round("baseline");
        var state = round.States.Single(state => state.StepId == "basic-expanded");
        var scan = AuditFiles.ReadVerified(Path.Combine(state.Directory, "scan.json"));
        if (condition == "duplicate-runtime")
        {
            scan = scan with
            {
                Snapshot = AuditTestFixture.Edit(scan.Snapshot!.Value, json =>
                {
                    var duplicate = json["nodes"]![1]!.DeepClone();
                    duplicate["id"] = "virtualized-node";
                    duplicate["automationId"] = "UnmappedVirtualizedItem";
                    json["nodes"]!.AsArray().Add(duplicate);
                })
            };
        }
        if (condition == "wrong-native-id")
            scan = scan with
            {
                Axe = scan.Axe! with
                {
                    Findings = scan.Axe.Findings.Select(finding => finding with
                    {
                        ElementProperties = new Dictionary<string, string>(finding.ElementProperties) { ["AutomationId"] = "DifferentNativeControl" }
                    }).ToArray()
                }
            };
        if (condition == "missing-optional-witness")
        {
            SetProfile(report.Profile with
            {
                Steps = report.Profile.Steps.Select(step => step.Id == "basic-expanded" ? step with { RequiredIds = ["DisplayMode"] } : step).ToArray()
            });
            state.BeforeControls.Remove("BasicIconButton");
            state.AfterControls.Remove("BasicIconButton");
        }
        SaveScan(state, scan);

        var html = Render();

        AssertSharedResult(round, html);
        if (condition == "missing-optional-witness")
        {
            Count(html, "round-0-unmapped", 1);
            StringAssert.Contains(html, "未映射／身份不唯一，不合并");
        }
    }

    [TestMethod]
    public void DuplicateOptionalAutomationIdsAndUnnamedVirtualizedControlsAreNotCollapsed()
    {
        var round = Round("baseline");
        var state = round.States.Single(state => state.StepId == "basic-expanded");
        SetProfile(report.Profile with
        {
            Steps = report.Profile.Steps.Select(step => step.Id == "basic-expanded" ? step with { RequiredIds = ["DisplayMode"] } : step).ToArray()
        });
        var scan = AuditFiles.ReadVerified(Path.Combine(state.Directory, "scan.json"));
        scan = scan with
        {
            Snapshot = AuditTestFixture.Edit(scan.Snapshot!.Value, json =>
            {
                var duplicate = json["nodes"]![1]!.DeepClone();
                duplicate["id"] = "duplicate-target";
                duplicate["runtimeId"] = "different-runtime";
                json["nodes"]!.AsArray().Add(duplicate);
            }),
            Results = [.. scan.Results, Failure("duplicate-target")],
            Axe = scan.Axe! with { Findings = [.. scan.Axe.Findings, NativeFailure("BasicIconButton", "different-runtime")], ResultGroupErrorCounts = [2] }
        };
        SaveScan(state, scan);

        var html = Render();

        Count(html, "round-0-failures", 4);
        Count(html, "round-0-unmapped", 2);
        StringAssert.Contains(html, LinkTo(state.Directory, "report.html") + "#result-0");
        StringAssert.Contains(html, LinkTo(state.Directory, "report.html") + "#result-4");
    }

    [TestMethod]
    [DataRow("scan")]
    [DataRow("scan-artifact")]
    [DataRow("actions")]
    [DataRow("command-response")]
    [DataRow("command-error")]
    [DataRow("missing-report")]
    [DataRow("missing-scan")]
    [DataRow("missing-state")]
    [DataRow("missing-action")]
    [DataRow("action-order")]
    [DataRow("changed-step")]
    [DataRow("failed-check")]
    [DataRow("missing-check")]
    [DataRow("wrong-checkpoint-time")]
    [DataRow("actions-object-not-array")]
    [DataRow("window")]
    [DataRow("process-start")]
    [DataRow("wrong-control")]
    [DataRow("unknown-round-status")]
    [DataRow("unknown-state-status")]
    public void MissingChangedOrUnboundRoundEvidenceNeverProducesCompletedOrZeroIssueCounts(string condition)
    {
        var round = Round("baseline");
        var state = round.States[0];
        var scan = Path.Combine(state.Directory, "scan.json");
        switch (condition)
        {
            case "scan": File.AppendAllText(scan, " "); break;
            case "scan-artifact": File.AppendAllText(Path.Combine(state.Directory, AuditTestFixture.SnapshotPath), " "); break;
            case "actions": File.AppendAllText(Path.Combine(round.Directory, "actions.json"), " "); break;
            case "command-response": File.AppendAllText(round.Actions[0].Commands[0].ResponseFile, " "); break;
            case "command-error": File.AppendAllText(round.Actions[0].Commands[0].ErrorFile!, "changed diagnostic"); break;
            case "missing-report": File.Delete(Path.Combine(state.Directory, "report.html")); break;
            case "missing-scan": File.Delete(scan); break;
            case "missing-state": round.States.RemoveAt(0); break;
            case "missing-action": round.Actions.RemoveAt(0); SaveActions(round); break;
            case "action-order": (round.Actions[0], round.Actions[1]) = (round.Actions[1], round.Actions[0]); SaveActions(round); break;
            case "changed-step": round.Actions[0] = round.Actions[0] with { Step = round.Actions[0].Step with { Name = "Changed route" } }; SaveActions(round); break;
            case "failed-check": round.Actions[1].Checks[0] = round.Actions[1].Checks[0] with { Passed = false }; SaveActions(round); break;
            case "missing-check": round.Actions[1].Checks.Clear(); SaveActions(round); break;
            case "wrong-checkpoint-time": state.FinishedAtUtc = round.FinishedAtUtc; break;
            case "actions-object-not-array":
                File.WriteAllText(Path.Combine(round.Directory, "actions.json"), "{\"actions\":[]}");
                round.ActionLogSha256 = AuditFiles.Hash(Path.Combine(round.Directory, "actions.json"));
                break;
            case "window": round.Target = round.Target! with { WindowHandle = 99 }; break;
            case "process-start": round.Target = round.Target! with { ProcessStartedAtUtc = round.Target.ProcessStartedAtUtc.AddSeconds(-1) }; break;
            case "wrong-control": state.AfterControls["BasicIconButton"] = Properties("DifferentControl", "Button", ""); break;
            case "unknown-round-status": round.Status = "future-status"; break;
            case "unknown-state-status": state.Status = "future-state"; break;
        }
        report.VerificationStatus = "passed";

        var html = Render();

        if (condition == "wrong-checkpoint-time")
            AssertSharedResult(round, html);
        else
        {
            Unknown(html);
            Assert.IsFalse(html.Contains("/report.html#result-", StringComparison.Ordinal));
            Assert.IsFalse(html.Contains("href=\"" + LinkTo(state.Directory, "report.html"), StringComparison.Ordinal));
            Assert.IsFalse(Section(html, "rounds").Contains("这轮已运行的规则未报告失败", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    [DataRow("missing-witness")]
    [DataRow("missing-focus-witness")]
    [DataRow("changed-response-bytes")]
    [DataRow("different-key")]
    [DataRow("extra-target")]
    [DataRow("wrong-argument-via")]
    [DataRow("different-response-key")]
    [DataRow("wrong-response-via")]
    [DataRow("wrong-response-window")]
    [DataRow("wrong-response-count")]
    [DataRow("corrective-target")]
    [DataRow("input-warning")]
    [DataRow("outside-response")]
    [DataRow("different-reference")]
    [DataRow("unfocused-before")]
    [DataRow("missing-input-command")]
    [DataRow("duplicate-input-command")]
    [DataRow("focus-fallback")]
    public void KeyboardAcceptanceIsDecidedByTheSharedVerifier(string condition)
    {
        var round = Round("baseline");
        _ = LoopEvidence.VerifyRound(report.Profile, round, Root);
        var action = round.Actions.Single(action => action.Step.Kind == "key");
        var before = JsonNode.Parse(action.Before!.Value.GetRawText())!.AsObject();
        var inputIndex = action.Commands.FindIndex(command => command.Operation == "send-keys");
        var input = action.Commands[inputIndex];
        switch (condition)
        {
            case "missing-witness": break;
            case "missing-focus-witness": before["properties"]!.AsObject().Remove("HasKeyboardFocus"); break;
            case "unfocused-before": before["properties"]!["HasKeyboardFocus"] = "False"; break;
            case "changed-response-bytes": File.AppendAllText(input.ResponseFile, " "); break;
            case "different-key":
                action.Commands[inputIndex] = input with { Arguments = ["send-keys", "enter", "--via", "send-input"] };
                break;
            case "extra-target":
                action.Commands[inputIndex] = input with { Arguments = [.. input.Arguments, "--target", "id:NavBasics"] };
                break;
            case "wrong-argument-via":
                action.Commands[inputIndex] = input with { Arguments = ["send-keys", "tab", "--via", "uia"] };
                break;
            case "outside-response":
                action.Commands[inputIndex] = input with { ResponseFile = audit.ScanPath, ResponseSha256 = AuditFiles.Hash(audit.ScanPath) };
                break;
            case "different-reference":
                var other = round.Actions[0].Commands[0];
                action.Commands[inputIndex] = input with { ResponseFile = other.ResponseFile, ResponseSha256 = other.ResponseSha256 };
                break;
            case "missing-input-command":
                action.Commands[inputIndex] = input with { Operation = "get-property", Arguments = ["get-property", "id:NavBasics"] };
                break;
            case "duplicate-input-command":
                action.Commands.Add(input);
                break;
            case "focus-fallback":
                action.Commands.Add(input with { Operation = "focus", Arguments = ["focus", "id:NavBasics"] });
                break;
            default:
                var response = JsonNode.Parse(File.ReadAllText(input.ResponseFile))!.AsObject();
                switch (condition)
                {
                    case "different-response-key": response["keys"] = "enter"; break;
                    case "wrong-response-via": response["via"] = "uia"; break;
                    case "wrong-response-window": response["hwnd"] = round.Target!.WindowHandle + 1; break;
                    case "wrong-response-count": response["actionCount"] = 2; break;
                    case "corrective-target": response["target"] = "id:NavBasics"; break;
                    case "input-warning": response["warnings"]!.AsArray().Add("input delivery was not confirmed"); break;
                    default: throw new AssertFailedException("Unknown keyboard evidence defect.");
                }
                File.WriteAllText(input.ResponseFile, response.ToJsonString(Json.Options));
                action.Commands[inputIndex] = input with { ResponseSha256 = AuditFiles.Hash(input.ResponseFile) };
                break;
        }
        action.Before = condition == "missing-witness" ? null : JsonSerializer.SerializeToElement(before, Json.Options);
        SaveActions(round);

        Assert.ThrowsExactly<InvalidDataException>(() => LoopEvidence.VerifyRound(report.Profile, round, Root));
        AssertSharedResult(round, Render());
    }

    [TestMethod]
    public void RequiredControlWitnessVerdictComesFromTheSharedVerifier()
    {
        var round = Round("baseline");
        var state = round.States.Single(state => state.StepId == "basic-expanded");
        state.BeforeControls["DisplayMode"] = AuditTestFixture.Edit(state.BeforeControls["DisplayMode"],
            json => json["properties"]!["ExpandCollapseState"] = "Expanded");
        state.AfterControls["DisplayMode"] = AuditTestFixture.Edit(state.AfterControls["DisplayMode"],
            json => json["properties"]!["ExpandCollapseState"] = "Collapsed");

        var html = Render();

        AssertSharedResult(round, html);
    }

    [TestMethod]
    [DataRow("snapshot-window")]
    [DataRow("snapshot-process")]
    [DataRow("scope-blocked")]
    public void NativeSnapshotScopeVerdictComesFromTheSharedVerifier(string condition)
    {
        var round = Round("baseline");
        var state = round.States[0];
        var scan = AuditFiles.ReadVerified(Path.Combine(state.Directory, "scan.json"));
        if (condition == "scope-blocked")
            scan = scan with { Results = [.. scan.Results, new() { CheckId = "capture.scope", Status = "blocked", Summary = "Scope was not verified." }] };
        else
            scan = scan with { Snapshot = AuditTestFixture.Edit(scan.Snapshot!.Value,
                json => json[condition == "snapshot-window" ? "windowHandle" : "processId"] = 999) };
        SaveScan(state, scan);

        AssertSharedResult(round, Render());
    }

    [TestMethod]
    [DataRow("changed-source")]
    [DataRow("missing-archive")]
    [DataRow("wrong-source-hash")]
    [DataRow("missing-source-file")]
    public void SourceFailuresStayVisibleAndNeverBorrowEarlierSuccessfulRound(string condition)
    {
        Round("baseline");
        var last = Round("reproduction");
        if (condition == "changed-source") File.AppendAllText(Path.Combine(last.SourceDirectory!, "MainWindow.xaml"), " ");
        if (condition == "missing-archive") last.SourceDirectory = null;
        if (condition == "wrong-source-hash") last.Sources = last.Sources.Select(source => source with { Sha256 = new string('A', 64) }).ToArray();
        if (condition == "missing-source-file") File.Delete(Path.Combine(last.SourceDirectory!, "MainWindow.xaml"));

        var html = Render();

        Unknown(html);
        Count(html, "round-0-failures", 3);
        StringAssert.Contains(Section(html, "summary"), "实际复现");
        StringAssert.Contains(Section(html, "rounds"), "问题数未知");
        Assert.IsFalse(Section(html, "story").Contains("实际动作、相同源码和问题出现记录已核对", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ArchivedBaselineStillRendersAfterTheDisposableWorkspaceIsRemoved()
    {
        var baseline = Round("baseline");
        Round("reproduction");
        Directory.Delete(Path.GetDirectoryName(report.Profile.Project)!, recursive: true);

        var html = Render();

        Count(html, "current-failures", 3);
        Count(html, "round-0-failures", 3);
        StringAssert.Contains(Section(html, "story"), "实际动作、相同源码和问题出现记录已核对");
        StringAssert.Contains(WebUtility.HtmlDecode(Section(html, "reproduce")), "--round 'baseline'");
        StringAssert.Contains(html, "不依赖当前工作副本或原始示例文件");
        foreach (var state in baseline.States.Where(state => state.RuleFailureOccurrences > 0))
            StringAssert.Contains(html, LinkTo(state.Directory, "report.html") + "#result-0");
        AssertLinks(html);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MovingTheRecordedProjectLocationDoesNotChangeArchivedAcceptance(bool outsideRun)
    {
        var round = Round("baseline");
        var originalHash = report.PlanSha256;
        var unavailableProject = Path.Combine(outsideRun ? audit.Root : Root, "removed-project", "Demo.csproj");
        report = report with { Profile = report.Profile with { Project = unavailableProject } };

        var html = Render();

        Assert.AreEqual(originalHash, LoopInput.PlanHash(report.Profile));
        Assert.IsFalse(File.Exists(unavailableProject));
        AssertSharedResult(round, html);
        Count(html, "current-failures", 3);
        AssertLinks(html);
    }

    [TestMethod]
    [DataRow("plan-bytes")]
    [DataRow("plan-sha")]
    [DataRow("preset-source")]
    [DataRow("changed-expected-name")]
    public void SavedPlanIsNotExecutionAndChangedAcceptanceIsNeverAccepted(string condition)
    {
        Round("baseline");
        if (condition == "plan-bytes") File.AppendAllText(Path.Combine(Root, "plan.json"), " ");
        if (condition == "plan-sha") report = report with { PlanSha256 = new string('A', 64) };
        if (condition == "preset-source") report = report with { PlanSource = "copilot" };
        if (condition == "changed-expected-name")
            report = report with { Profile = report.Profile with
            {
                RepairTargets = report.Profile.RepairTargets.Select(target => target with { ExpectedName = "New acceptance" }).ToArray()
            } };

        var html = Render();

        Unknown(html);
        Assert.IsFalse(html.Contains("href=\"./plan.json\"", StringComparison.Ordinal));
        StringAssert.Contains(html, "不提供看似已经可复现的完成结论");
    }

    [TestMethod]
    [DataRow("relative-round")]
    [DataRow("outside-round")]
    [DataRow("traversal-state")]
    [DataRow("outside-command")]
    [DataRow("relative-command")]
    [DataRow("outside-source")]
    [DataRow("traversal-source-entry")]
    public void ArtifactPathsMustBeAbsoluteContainedAndSafeBeforeTheyAreReadOrLinked(string condition)
    {
        var round = Round("baseline");
        var state = round.States[0];
        if (condition == "relative-round") report.Rounds[0] = round with { Directory = @"rounds\baseline" };
        if (condition == "outside-round") report.Rounds[0] = round with { Directory = audit.Source };
        if (condition == "traversal-state") round.States[0] = state with { Directory = Path.Combine(round.Directory, "states", "..", "states", Path.GetFileName(state.Directory)) };
        if (condition is "outside-command" or "relative-command")
        {
            round.Actions[0].Commands[0] = round.Actions[0].Commands[0] with
            {
                ResponseFile = condition == "outside-command" ? audit.ScanPath : "response.json"
            };
            SaveActions(round);
        }
        if (condition == "outside-source") round.SourceDirectory = audit.Source;
        if (condition == "traversal-source-entry") round.Sources = [new(@"..\MainWindow.xaml", new string('A', 64))];

        var html = Render();

        Unknown(html);
        Assert.IsFalse(html.Contains("/report.html#result-", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("href=\"file:", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    [DataRow("no-advisor-events")]
    [DataRow("different-advisor-response")]
    [DataRow("unknown-source")]
    [DataRow("missing-proposal")]
    [DataRow("missing-diff")]
    [DataRow("outside-diff")]
    [DataRow("wrong-after-round")]
    [DataRow("wrong-source-binding")]
    public void RepairClaimsNeedRealProposalArtifactsAndTheirVerificationRound(string condition)
    {
        Round("baseline");
        Round("reproduction");
        Round("after-01", "BasicIconButton");
        var repair = Repair("BasicIconButton", "after-01", "copilot");
        if (condition == "no-advisor-events") File.Delete(Path.Combine(repair.Directory, "advisor-events.jsonl"));
        if (condition == "different-advisor-response")
            File.WriteAllText(Path.Combine(repair.Directory, "advisor-response.txt"),
                JsonSerializer.Serialize(repair.Proposal! with { AccessibleName = "Different proposed name" }, Json.Options));
        if (condition == "unknown-source") repair.ProposalSource = "autonomous-planner";
        if (condition == "missing-proposal") File.Delete(Path.Combine(repair.Directory, "proposal.json"));
        if (condition == "missing-diff") File.Delete(repair.DiffFile!);
        if (condition == "outside-diff") repair.DiffFile = audit.ScanPath;
        if (condition == "wrong-after-round") repair.VerificationRoundId = "missing-round";
        if (condition == "wrong-source-binding")
        {
            repair.Proposal = repair.Proposal! with { SourceSha256 = new string('D', 64) };
            File.WriteAllText(Path.Combine(repair.Directory, "proposal.json"), JsonSerializer.Serialize(repair.Proposal, Json.Options));
        }

        var html = Render();

        StringAssert.Contains(Section(html, "repairs"), "未确认成功修复");
        Assert.IsFalse(Section(html, "repairs").Contains("本目标修复已通过后续同路线核对", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("href=\"" + LinkTo(repair.Directory, "repair.diff"), StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("Copilot 提案（已保存响应）", StringComparison.Ordinal));
        Count(html, "current-failures", 1);
    }

    [TestMethod]
    public void AClaimedPatchNeedsAnObservedSourceVersionChange()
    {
        Round("baseline");
        Round("reproduction");
        var after = Round("after-01", "BasicIconButton");
        var project = WriteSources(after.SourceDirectory!, []);
        after.Sources = JourneyInput.CaptureSources(project);
        Repair("BasicIconButton", "after-01");

        var html = Render();

        StringAssert.Contains(Section(html, "repairs"), "没有观察到提案所指源码的实际版本变化");
        Assert.IsFalse(Section(html, "repairs").Contains("本目标修复已通过后续同路线核对", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ARecordedRollbackIsNotPresentedAsAPersistedSuccessfulFix()
    {
        Round("baseline");
        Round("reproduction");
        Round("after-01", "BasicIconButton");
        var repair = Repair("BasicIconButton", "after-01");
        repair.Status = "rolled-back";
        repair.Error = "The patch was rolled back after an interruption.";

        var html = Render();

        StringAssert.Contains(Section(html, "summary"), "修复未确认保留");
        StringAssert.Contains(Section(html, "summary"), repair.Error);
        Assert.IsFalse(Section(html, "repairs").Contains("本目标修复已通过后续同路线核对", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ReparseDirectoryCannotTurnExternalOrDifferentEvidenceIntoLocalCoverage()
    {
        var round = Round("baseline");
        var link = Path.Combine(Root, "linked-round");
        try { Directory.CreateSymbolicLink(link, round.Directory); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("This host does not permit creating a fixture directory link: " + exception.Message);
            return;
        }
        try
        {
            report.Rounds[0] = round with { Directory = link };
            Unknown(Render());
        }
        finally { Directory.Delete(link); }
    }

    [TestMethod]
    public void ReplayInstructionsUseANewOwnedRunWithoutStaleWindowCommandsOrScripts()
    {
        Round("baseline");

        var html = Render();
        var reproduction = WebUtility.HtmlDecode(Section(html, "reproduce"));

        StringAssert.Contains(reproduction, "AccessLoop loop --replay '" + Root.Replace("'", "''", StringComparison.Ordinal) + "'");
        StringAssert.Contains(reproduction, "--round 'baseline' --accept-actions --output '.\\loop-replay-new'");
        Assert.IsFalse(reproduction.Contains("--window", StringComparison.Ordinal));
        Assert.IsFalse(reproduction.Contains("--pid", StringComparison.Ordinal));
        StringAssert.Contains(html, "只将页签标题焦点设为准备起点");
        StringAssert.Contains(html, "不把导航准备、滚动、Invoke");
        StringAssert.Contains(html, "不使用 --target");
        StringAssert.Contains(html, "业务数据与路线是预设");
        Assert.IsFalse(html.Contains("<script", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("<button", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("<iframe", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("https://", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(html, "default-src 'none'");
        AssertLinks(html);
    }

    [TestMethod]
    public void UnknownAndMissingResultsRemainUnknownEvenWhenCachedVerificationClaimsPassed()
    {
        report.Status = "future-status";
        report.VerificationStatus = "passed";
        report.ReproductionStatus = "passed";

        var html = Render();

        Unknown(html);
        StringAssert.Contains(html, "尚无轮次证据");
        StringAssert.Contains(html, "未知执行状态：future-status");
        StringAssert.Contains(html, "没有修复提案记录");
        AssertLinks(html);
    }

    [TestMethod]
    public void NullRoundRecordsAndDuplicatePageDeclarationsStayVisibleInsteadOfCrashing()
    {
        var round = Round("baseline");
        report.Rounds.Add(null!);
        report.Repairs.Add(null!);
        report = report with { Profile = report.Profile with { Pages = [.. report.Profile.Pages, report.Profile.Pages[0]] } };
        round.Actions.Add(null!);
        round.States.Add(null!);

        var html = Render();

        Unknown(html);
        StringAssert.Contains(html, "未知");
        AssertLinks(html);
    }
    [TestMethod]
    public void StandaloneReplayDoesNotReadOrAuthenticateAnExternalOriginalRun()
    {
        Round("replay");
        report = report with { ReplayOf = @"C:\untrusted\original#baseline" };
        report.ReproductionStatus = "passed";

        var html = Render();

        Count(html, "current-failures", 3);
        StringAssert.Contains(html, "不独立证明与旧轮次等价");
        Assert.IsFalse(html.Contains("href=\"C:", StringComparison.OrdinalIgnoreCase));
        AssertLinks(html);
    }

    [TestMethod]
    public void TrailingDirectorySeparatorDoesNotInvalidateAnOtherwiseBoundRun()
    {
        var round = Round("baseline");
        report = report with { Directory = Root + Path.DirectorySeparatorChar };
        report.Rounds[0] = round with { Directory = round.Directory + Path.DirectorySeparatorChar };

        var html = Render();

        Count(html, "current-failures", 3);
        AssertLinks(html);
    }

    [TestMethod]
    public void UntrustedTextIsEscapedAndEveryNavigationAnchorResolves()
    {
        static string Unsafe(string label) => $"<script>{label} & \"quoted\"</script>";
        var round = Round("baseline");
        report = report with { Name = Unsafe("title") };
        report.Error = Unsafe("reason");
        round.Status = Unsafe("status");
        round.Error = Unsafe("round-error");
        round.States[0].Error = Unsafe("state-error");

        var html = Render();

        foreach (var value in new[] { report.Name, report.Error, round.Status, round.Error, round.States[0].Error })
            StringAssert.Contains(html, WebUtility.HtmlEncode(value!));
        Assert.IsFalse(html.Contains("<script", StringComparison.OrdinalIgnoreCase));
        AssertLinks(html);
    }

    [TestMethod]
    public void WriteUsesCreateNewAndPreservesEveryOriginalFileAndReportField()
    {
        Round("baseline");
        Round("reproduction");
        var before = JsonSerializer.Serialize(report, Json.Options);
        var files = Directory.GetFiles(Root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);

        var html = Render();
        Assert.ThrowsExactly<IOException>(() => LoopReportRenderer.Write(Path.Combine(Root, "loop-report.html"), report));

        Assert.AreEqual(before, JsonSerializer.Serialize(report, Json.Options));
        foreach (var (path, bytes) in files) CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path), path);
        Assert.AreEqual(html, File.ReadAllText(Path.Combine(Root, "loop-report.html")));
        Assert.AreEqual(files.Count + 1, Directory.GetFiles(Root, "*", SearchOption.AllDirectories).Length);
        Assert.ThrowsExactly<InvalidDataException>(() => LoopReportRenderer.Write(Path.Combine(audit.Root, "outside.html"), report));
        Assert.IsFalse(File.Exists(Path.Combine(audit.Root, "outside.html")));
    }

    private string LinkTo(string directory, string file) => "./" + string.Join('/',
        Path.GetRelativePath(Root, Path.Combine(directory, file)).Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString));

    private void AssertLinks(string html)
    {
        var ids = Regex.Matches(html, "\\bid=\"([^\"]+)\"").Select(match => match.Groups[1].Value).ToArray();
        Assert.AreEqual(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        foreach (Match match in Regex.Matches(html, "href=\"([^\"]+)\""))
        {
            var href = WebUtility.HtmlDecode(match.Groups[1].Value);
            if (href.StartsWith('#'))
                Assert.IsTrue(ids.Contains(href[1..], StringComparer.Ordinal), href);
            else
            {
                Assert.IsTrue(href.StartsWith("./", StringComparison.Ordinal), href);
                var parts = href[2..].Split('#', 2);
                var path = Path.Combine(Root, Uri.UnescapeDataString(parts[0]).Replace('/', Path.DirectorySeparatorChar));
                Assert.IsTrue(File.Exists(path), href);
                if (parts.Length == 2)
                    StringAssert.Contains(File.ReadAllText(path), "id=\"" + parts[1] + "\"");
            }
        }
    }

    private static string Section(string html, string id)
    {
        var start = html.IndexOf($"<section id=\"{id}\">", StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, id);
        return html[start..html.IndexOf("</section>", start, StringComparison.Ordinal)];
    }
    private static void Count(string html, string id, int count) => StringAssert.Contains(html, $"id=\"{id}\">{count}</strong>");
    private void AssertSharedResult(LoopRoundResult round, string html)
    {
        LoopVerifiedRound expected;
        try { expected = LoopEvidence.VerifyRound(report.Profile, round, Root); }
        catch (InvalidDataException exception)
        {
            Unknown(html);
            StringAssert.Contains(html, WebUtility.HtmlEncode(exception.Message));
            Assert.IsFalse(html.Contains("/report.html#result-", StringComparison.Ordinal));
            return;
        }
        var index = report.Rounds.IndexOf(round);
        Count(html, $"round-{index}-mapped", expected.Findings.Count(finding => finding.MappedTarget));
        Count(html, $"round-{index}-unmapped", expected.Findings.Where(finding => !finding.MappedTarget).Sum(finding => finding.Occurrences.Count));
        Count(html, $"round-{index}-failures", expected.Findings.Sum(finding => finding.Occurrences.Count));
        if (index == report.Rounds.Count - 1)
        {
            Count(html, "current-failures", expected.Findings.Sum(finding => finding.Occurrences.Count));
            Count(html, "current-states", expected.States.Count);
        }
        foreach (var occurrence in expected.Findings.SelectMany(finding => finding.Occurrences))
            StringAssert.Contains(html, LinkTo(occurrence.StateDirectory, "report.html") + "#result-" + occurrence.ResultIndex);
    }

    private static void Unknown(string html)
    {
        StringAssert.Contains(html, "id=\"current-failures\">未知</strong>");
        StringAssert.Contains(html, "id=\"current-states\">未知</strong>");
        StringAssert.Contains(Section(html, "summary"), "未核验");
    }
}

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class WorkbenchPolicyTests
{
    [TestMethod]
    public void TwoScenariosCanFixOneIssueThenTheOtherWithoutClaimingTheSuitePassedEarly()
    {
        var before = TwoIssues();
        var catalog = WorkbenchPolicy.BuildCatalog(before);
        Assert.HasCount(2, catalog);
        Assert.IsTrue(catalog.All(finding => finding.Repairable && finding.Occurrences.Count == 2));
        var save = catalog.Single(finding => finding.AutomationId == "SaveButton");
        var export = catalog.Single(finding => finding.AutomationId == "ExportButton");

        var firstAfter = before.Select(result => Replay(result, Missing("ExportButton", "restarted-export"))).ToArray();
        var first = WorkbenchPolicy.Verify(save, before, firstAfter);
        Assert.IsTrue(first.Accepted);
        Assert.AreEqual("fixed", first.Status);
        Assert.HasCount(0, first.Errors);
        Assert.AreEqual(export.Id, first.RemainingFindings.Single().Id);
        Assert.AreEqual("failed", WorkbenchPolicy.SuiteStatus(firstAfter));
        Assert.AreEqual("export", WorkbenchPolicy.FindRepairOwner(first.RemainingFindings.Single(), firstAfter).Scenario.Name);

        var secondAfter = firstAfter.Select(result => Replay(result)).ToArray();
        var second = WorkbenchPolicy.Verify(first.RemainingFindings.Single(), firstAfter, secondAfter);
        Assert.IsTrue(second.Accepted);
        Assert.AreEqual("fixed", second.Status);
        Assert.HasCount(0, second.RemainingFindings);
        Assert.AreEqual("passed", WorkbenchPolicy.SuiteStatus(secondAfter));
    }

    [TestMethod]
    public void SharedIssueHasOneIdentityAndUsesTheFirstDeclaredCompatibleOwner()
    {
        var before = new[]
        {
            Observe(Scene("z-first"), Missing()),
            Observe(Scene("a-second"), Missing(runtimeId: "second-process"))
        };
        var selected = WorkbenchPolicy.BuildCatalog(before).Single();
        Assert.IsTrue(selected.StableIdentity);
        Assert.IsTrue(selected.Repairable);
        Assert.AreEqual("z-first", selected.OwnerScenarioName);
        Assert.HasCount(2, selected.Occurrences);
        Assert.AreSame(before[0], WorkbenchPolicy.FindRepairOwner(selected, before));
        var reordered = before.Reverse().ToArray();
        Assert.AreEqual(selected.Id, WorkbenchPolicy.BuildCatalog(reordered).Single().Id);
        Assert.AreSame(before[1], WorkbenchPolicy.FindRepairOwner(selected, reordered));
    }

    [TestMethod]
    public void RuntimePropertiesAndWindowsPathSpellingDoNotChangeStableIds()
    {
        var original = Observe(Scene() with { Project = Path.GetFullPath(@"Demo\..\Demo.csproj").ToLowerInvariant() }, Missing());
        var changedFinding = Missing(runtimeId: "different-runtime") with
        {
            ElementProperties = new Dictionary<string, string>
            {
                ["AutomationId"] = "SaveButton", ["RuntimeId"] = "different-runtime",
                ["Name"] = "A new name", ["ProcessId"] = "5678", ["NativeWindowHandle"] = "99"
            }
        };
        var restarted = Replay(original, changedFinding) with
        {
            Scenario = original.Scenario with { Project = original.Scenario.Project.ToUpperInvariant() }
        };
        var first = WorkbenchPolicy.BuildCatalog([original]).Single();
        var second = WorkbenchPolicy.BuildCatalog([restarted]).Single();
        Assert.AreEqual(first.Id, second.Id);
        Assert.AreEqual(Path.GetFullPath(original.Scenario.Project).ToUpperInvariant(), first.Project);
        StringAssert.Matches(first.Id, new System.Text.RegularExpressions.Regex("^AL-[0-9A-F]{16}$"));
    }

    [TestMethod]
    public void DifferentProjectsNeverMergeEvenWhenRulesAndAutomationIdsMatch()
    {
        var before = new[]
        {
            Observe(Scene("first") with { Project = Path.GetFullPath(@"First\Demo.csproj") }, Missing()),
            Observe(Scene("second") with
            {
                Project = Path.GetFullPath(@"Second\Demo.csproj"), ExpectedName = "Save a different document"
            }, Missing())
        };
        var catalog = WorkbenchPolicy.BuildCatalog(before);
        Assert.HasCount(2, catalog);
        Assert.AreNotEqual(catalog[0].Id, catalog[1].Id);
        Assert.IsTrue(catalog.All(finding => finding.Repairable && finding.Occurrences.Count == 1));
        var selected = catalog.Single(finding => finding.Occurrences[0].ScenarioName == "first");
        var verdict = WorkbenchPolicy.Verify(selected, before, [Replay(before[0]), Replay(before[1], Missing())]);
        Assert.IsTrue(verdict.Accepted);
        Assert.AreEqual("second", verdict.RemainingFindings.Single().Occurrences.Single().ScenarioName);
    }

    [TestMethod]
    public void AutomationIdsAndRuleIdsAreCaseSensitive()
    {
        var result = Observe(Scene(), Missing(), Missing("savebutton", "lower-id"),
            Missing(runtimeId: "original") with { RuleId = "namenotnull" });
        var catalog = WorkbenchPolicy.BuildCatalog([result]);
        Assert.HasCount(3, catalog);
        Assert.HasCount(3, catalog.Select(finding => finding.Id).Distinct().ToArray());
    }

    [TestMethod]
    public void DuplicateRuntimeElementsRemainVisibleAndBlockUnrelatedRepair()
    {
        var before = new[]
        {
            Observe(Scene("export", "ExportButton"),
                Missing(runtimeId: "first"), Missing(runtimeId: "second"), Missing("ExportButton", "third"))
        };
        var catalog = WorkbenchPolicy.BuildCatalog(before);
        var duplicate = catalog.Single(finding => finding.AutomationId == "SaveButton");
        var other = catalog.Single(finding => finding.AutomationId == "ExportButton");
        Assert.HasCount(2, duplicate.Occurrences);
        Assert.IsFalse(duplicate.StableIdentity);
        Assert.IsTrue(other.StableIdentity);
        Assert.IsFalse(duplicate.Repairable);
        Assert.IsFalse(other.Repairable);
        StringAssert.Contains(other.RepairReason!, "multiple runtime elements");
        Assert.ThrowsExactly<InvalidDataException>(() => WorkbenchPolicy.FindRepairOwner(other, before));
        Assert.AreEqual("failed", WorkbenchPolicy.SuiteStatus(before));
    }

    [TestMethod]
    public void DuplicateAutomationIdsAcrossDifferentRulesAreAlsoAmbiguous()
    {
        var result = Observe(Scene(), Missing(runtimeId: "first"),
            Missing(runtimeId: "second") with { RuleId = "IsKeyboardFocusableShouldBeTrue" });
        var catalog = WorkbenchPolicy.BuildCatalog([result]);
        Assert.HasCount(2, catalog);
        Assert.IsTrue(catalog.All(finding => !finding.StableIdentity && !finding.Repairable));
    }

    [TestMethod]
    public void MultipleRulesOnTheSelectedTargetRemainDistinctButRequireReview()
    {
        var other = Missing() with { RuleId = "IsKeyboardFocusableShouldBeTrue" };
        var before = Observe(Scene(), Missing(), other);
        var catalog = WorkbenchPolicy.BuildCatalog([before]);
        var selected = catalog.Single(finding => finding.RuleId == "NameNotNull");
        Assert.HasCount(2, catalog);
        Assert.IsTrue(catalog.All(finding => finding.StableIdentity));
        Assert.IsFalse(selected.Repairable);
        StringAssert.Contains(selected.RepairReason!, "exactly one Axe.Windows finding on target");
        Assert.ThrowsExactly<InvalidDataException>(() => WorkbenchPolicy.FindRepairOwner(selected, [before]));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    public void MissingAutomationIdsAreScanLocalAndPreventUnsafeComparison(string? automationId)
    {
        var properties = new Dictionary<string, string> { ["RuntimeId"] = "unnamed" };
        if (automationId is not null)
            properties["AutomationId"] = automationId;
        var unnamed = Missing() with { ElementProperties = properties };
        var before = Observe(Scene(), Missing(), unnamed);
        var first = WorkbenchPolicy.BuildCatalog([before]);
        var second = WorkbenchPolicy.BuildCatalog([before]);
        var scanLocal = first.Single(finding => !finding.StableIdentity);
        Assert.IsFalse(scanLocal.Repairable);
        StringAssert.StartsWith(scanLocal.Id, "AL-SESSION-");
        Assert.AreNotEqual(scanLocal.Id, second.Single(finding => !finding.StableIdentity).Id);
        var manualError = Assert.ThrowsExactly<InvalidDataException>(() => WorkbenchPolicy.FindRepairOwner(scanLocal, [before]));
        StringAssert.Contains(manualError.Message, "no AutomationId");
        var selected = first.Single(finding => finding.StableIdentity);
        Assert.IsFalse(selected.Repairable);
        StringAssert.Contains(selected.RepairReason!, "no AutomationId");
        Assert.ThrowsExactly<InvalidDataException>(() => WorkbenchPolicy.FindRepairOwner(selected, [before]));
        var verdict = WorkbenchPolicy.Verify(selected, [before], [Replay(before)]);
        Assert.IsFalse(verdict.Accepted);
        Assert.AreEqual("inconclusive", verdict.Status);
    }

    [TestMethod]
    public void MissingAutomationIdsAreNotCollapsedEvenWithoutRuntimeIds()
    {
        var unnamed = Missing() with { ElementProperties = new Dictionary<string, string>() };
        var catalog = WorkbenchPolicy.BuildCatalog([Observe(Scene(), unnamed, unnamed)]);
        Assert.HasCount(2, catalog);
        Assert.AreNotEqual(catalog[0].Id, catalog[1].Id);
        Assert.IsTrue(catalog.All(finding => !finding.StableIdentity && finding.Occurrences.Count == 1));
    }

    [TestMethod]
    public void KnownAutomationIdDoesNotRequireARuntimeIdAcrossRuns()
    {
        var finding = Missing() with
        {
            ElementProperties = new Dictionary<string, string> { ["AutomationId"] = "SaveButton" }
        };
        var before = Observe(Scene(), finding);
        var selected = WorkbenchPolicy.BuildCatalog([before]).Single();
        Assert.IsTrue(selected.StableIdentity);
        Assert.IsTrue(WorkbenchPolicy.Verify(selected, [before], [Replay(before)]).Accepted);
    }

    [TestMethod]
    [DataRow("NameNotNull")]
    [DataRow("NameNotEmpty")]
    [DataRow("NameNotWhiteSpace")]
    public void OnlySupportedMissingNameRulesCanAuthorizeTheNameProposal(string ruleId)
    {
        var before = Observe(Scene(), Missing() with { RuleId = ruleId });
        var selected = WorkbenchPolicy.BuildCatalog([before]).Single();
        Assert.IsTrue(selected.Repairable);
        Assert.AreSame(before, WorkbenchPolicy.FindRepairOwner(selected, [before]));
    }

    [TestMethod]
    public void UnsupportedRulesAndNameExpectationMismatchesAloneDoNotAuthorizeRepair()
    {
        var before = WithCheck(Observe(Scene(), Missing() with { RuleId = "IsKeyboardFocusableShouldBeTrue" }),
            "accessible-name", false);
        var selected = WorkbenchPolicy.BuildCatalog([before]).Single();
        Assert.IsFalse(selected.Repairable);
        StringAssert.Contains(selected.RepairReason!, "manual review");
        Assert.ThrowsExactly<InvalidDataException>(() => WorkbenchPolicy.FindRepairOwner(selected, [before]));

        var mismatchOnly = WithCheck(Observe(Scene()), "accessible-name", false);
        Assert.HasCount(0, WorkbenchPolicy.BuildCatalog([mismatchOnly]));
        Assert.AreEqual("failed", WorkbenchPolicy.SuiteStatus([mismatchOnly]));
        Assert.ThrowsExactly<InvalidDataException>(() => WorkbenchPolicy.FindRepairOwner(selected, [mismatchOnly]));
    }

    [TestMethod]
    public void ConflictingOwnerNamesRequireReview()
    {
        var before = new[]
        {
            Observe(Scene("first"), Missing()),
            Observe(Scene("second") with { ExpectedName = "Submit" }, Missing())
        };
        var selected = WorkbenchPolicy.BuildCatalog(before).Single();
        Assert.IsFalse(selected.Repairable);
        StringAssert.Contains(selected.RepairReason!, "disagree on expectedName");
        Assert.ThrowsExactly<InvalidDataException>(() => WorkbenchPolicy.FindRepairOwner(selected, before));
    }

    [TestMethod]
    [DataRow("target-enabled")]
    [DataRow("target-visible")]
    [DataRow("keyboard-reachable")]
    [DataRow("keyboard-activation")]
    public void EveryOwnerMustPassAllFourBehaviorGuards(string checkId)
    {
        var before = new[]
        {
            Observe(Scene("first"), Missing()),
            WithCheck(Observe(Scene("second"), Missing()), checkId, false)
        };
        var selected = WorkbenchPolicy.BuildCatalog(before).Single();
        Assert.IsFalse(selected.Repairable);
        Assert.ThrowsExactly<InvalidDataException>(() => WorkbenchPolicy.FindRepairOwner(selected, before));
    }

    [TestMethod]
    public void AnOwnerMustTargetTheFindingAndHaveItsOriginalNameAssertionFail()
    {
        var passedName = WithCheck(Observe(Scene(), Missing()), "accessible-name", true);
        var selected = WorkbenchPolicy.BuildCatalog([passedName]).Single();
        Assert.IsFalse(selected.Repairable);
        StringAssert.Contains(selected.RepairReason!, "failing accessible-name");

        var noOwner = Observe(Scene(), Missing("OtherButton"));
        var other = WorkbenchPolicy.BuildCatalog([noOwner]).Single();
        Assert.IsFalse(other.Repairable);
        StringAssert.Contains(other.RepairReason!, "No scenario targets");
    }

    [TestMethod]
    public void EveryOwnerMustActuallyObserveTheSelectedIssue()
    {
        var before = new[] { Observe(Scene("first"), Missing()), Observe(Scene("second")) };
        var selected = WorkbenchPolicy.BuildCatalog(before).Single();
        Assert.IsFalse(selected.Repairable);
        StringAssert.Contains(selected.RepairReason!, "exactly one occurrence");
    }

    [TestMethod]
    public void AnUnrelatedExistingNameAndGuardFailureCanRemainAfterTheSelectedFix()
    {
        var before = TwoIssues();
        before[1] = WithCheck(before[1], "target-enabled", false);
        var selected = WorkbenchPolicy.BuildCatalog(before).Single(finding => finding.AutomationId == "SaveButton");
        Assert.IsTrue(selected.Repairable);
        var after = before.Select(result => Replay(result, Missing("ExportButton"))).ToArray();
        after[1] = WithCheck(after[1], "target-enabled", false);
        var verdict = WorkbenchPolicy.Verify(selected, before, after);
        Assert.IsTrue(verdict.Accepted);
        Assert.AreEqual("fixed", verdict.Status);
        Assert.HasCount(1, verdict.RemainingFindings);
        Assert.AreEqual("failed", WorkbenchPolicy.SuiteStatus(after));
    }

    [TestMethod]
    [DataRow("accessible-name")]
    [DataRow("target-enabled")]
    [DataRow("target-visible")]
    [DataRow("keyboard-reachable")]
    [DataRow("keyboard-activation")]
    public void ANewFailedAssertionInAnyScenarioIsARegression(string checkId)
    {
        var before = new[] { Observe(Scene(), Missing()), Observe(Scene("export", "ExportButton"), Missing()) };
        var selected = WorkbenchPolicy.BuildCatalog(before).Single();
        var after = new[] { Replay(before[0]), WithCheck(Replay(before[1]), checkId, false) };
        var verdict = WorkbenchPolicy.Verify(selected, before, after);
        Assert.IsFalse(verdict.Accepted);
        Assert.AreEqual("regression", verdict.Status);
        Assert.IsTrue(verdict.Errors.Any(error => error.Contains(checkId, StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("OtherButton", "NameNotNull")]
    [DataRow("SaveButton", "NameNotEmpty")]
    [DataRow("SaveButton", "NameNotWhiteSpace")]
    public void ANewFindingOrAReplacementNameRuleIsARegression(string automationId, string ruleId)
    {
        var before = Observe(Scene(), Missing());
        var selected = WorkbenchPolicy.BuildCatalog([before]).Single();
        var after = Replay(before, Missing(automationId, "new-runtime") with { RuleId = ruleId });
        var verdict = WorkbenchPolicy.Verify(selected, [before], [after]);
        Assert.IsFalse(verdict.Accepted);
        Assert.AreEqual("regression", verdict.Status);
        Assert.AreEqual(ruleId, verdict.RemainingFindings.Single().RuleId);
    }

    [TestMethod]
    public void ExistingFindingAppearingInAnAdditionalScenarioIsARegression()
    {
        var before = new[]
        {
            Observe(Scene(), Missing(), Missing("OtherButton", "other")),
            Observe(Scene("overview", "OverviewButton"), Missing())
        };
        var selected = WorkbenchPolicy.BuildCatalog(before).Single(finding => finding.AutomationId == "SaveButton");
        var after = before.Select(result => Replay(result, Missing("OtherButton", "new-other"))).ToArray();
        var verdict = WorkbenchPolicy.Verify(selected, before, after);
        Assert.IsFalse(verdict.Accepted);
        Assert.AreEqual("regression", verdict.Status);
        Assert.IsTrue(verdict.Errors.Any(error => error.Contains("overview", StringComparison.Ordinal)
            && error.Contains("up from 0", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void IncreasedMultiplicityIsAmbiguousAndCannotBeAccepted()
    {
        var before = Observe(Scene(), Missing(), Missing("OtherButton", "other"));
        var selected = WorkbenchPolicy.BuildCatalog([before]).Single(finding => finding.AutomationId == "SaveButton");
        var after = Replay(before, Missing("OtherButton", "other-1"), Missing("OtherButton", "other-2"));
        var verdict = WorkbenchPolicy.Verify(selected, [before], [after]);
        Assert.IsFalse(verdict.Accepted);
        Assert.AreEqual("inconclusive", verdict.Status);
        Assert.HasCount(2, verdict.RemainingFindings.Single().Occurrences);
        StringAssert.Contains(verdict.Errors.Single(), "multiple runtime elements");
    }

    [TestMethod]
    public void ANewFindingWithoutAnAutomationIdMakesVerificationInconclusive()
    {
        var before = Observe(Scene(), Missing());
        var selected = WorkbenchPolicy.BuildCatalog([before]).Single();
        var unidentified = Missing() with { ElementProperties = new Dictionary<string, string> { ["Name"] = "Do not match by this name" } };
        var verdict = WorkbenchPolicy.Verify(selected, [before], [Replay(before, unidentified)]);
        Assert.IsFalse(verdict.Accepted);
        Assert.AreEqual("inconclusive", verdict.Status);
        Assert.IsFalse(verdict.RemainingFindings.Single().StableIdentity);
        StringAssert.Contains(verdict.Errors.Single(), "no AutomationId");
    }

    [TestMethod]
    public void SelectedIssueMustDisappearEvenFromScenariosWhereItWasPreviouslyAbsent()
    {
        var before = new[] { Observe(Scene(), Missing()), Observe(Scene("overview", "OverviewButton")) };
        var selected = WorkbenchPolicy.BuildCatalog(before).Single();
        var verdict = WorkbenchPolicy.Verify(selected, before, [Replay(before[0]), Replay(before[1], Missing())]);
        Assert.IsFalse(verdict.Accepted);
        Assert.AreEqual("regression", verdict.Status);
        StringAssert.Contains(verdict.Errors.Single(), "still present in: overview");
    }

    [TestMethod]
    public void AStillFailingOwnerNameAssertionIsNotAFixEvenIfTheAxeFindingDisappeared()
    {
        var before = Observe(Scene(), Missing());
        var selected = WorkbenchPolicy.BuildCatalog([before]).Single();
        var verdict = WorkbenchPolicy.Verify(selected, [before], [WithCheck(Replay(before), "accessible-name", false)]);
        Assert.IsFalse(verdict.Accepted);
        Assert.AreEqual("regression", verdict.Status);
        StringAssert.Contains(verdict.Errors.Single(), "intended accessible-name");
    }

    [TestMethod]
    public void EveryOwnerMustPassItsNameAssertionAfterRepair()
    {
        var before = new[] { Observe(Scene("first"), Missing()), Observe(Scene("second"), Missing()) };
        var selected = WorkbenchPolicy.BuildCatalog(before).Single();
        var after = new[] { Replay(before[0]), WithCheck(Replay(before[1]), "accessible-name", false) };
        var verdict = WorkbenchPolicy.Verify(selected, before, after);
        Assert.IsFalse(verdict.Accepted);
        Assert.AreEqual("regression", verdict.Status);
        StringAssert.Contains(verdict.Errors.Single(), "second");
    }

    [TestMethod]
    public void OtherExistingFindingsMayDisappearWithoutBlockingTheSelectedFix()
    {
        var before = TwoIssues();
        var selected = WorkbenchPolicy.BuildCatalog(before).Single(finding => finding.AutomationId == "SaveButton");
        var verdict = WorkbenchPolicy.Verify(selected, before, before.Select(result => Replay(result)).ToArray());
        Assert.IsTrue(verdict.Accepted);
        Assert.HasCount(0, verdict.RemainingFindings);
    }

    [TestMethod]
    public void SelectedTargetMustBeVerifiedInEveryPreviouslyAffectedScenario()
    {
        var before = TwoIssues();
        var selected = WorkbenchPolicy.BuildCatalog(before).Single(finding => finding.AutomationId == "SaveButton");
        var after = before.Select(result => Replay(result, Missing("ExportButton"))).ToArray();
        after[1] = after[1] with { Observation = after[1].Observation with { VerifiedTargets = ["ExportButton"] } };
        var verdict = WorkbenchPolicy.Verify(selected, before, after);
        Assert.IsFalse(verdict.Accepted);
        Assert.AreEqual("inconclusive", verdict.Status);
        StringAssert.Contains(verdict.Errors.Single(), "export");
        StringAssert.Contains(verdict.Errors.Single(), "missing target is not a fix");
    }

    [TestMethod]
    public void CallerSuppliedOccurrenceMetadataCannotHideAnUnverifiedBaselineTarget()
    {
        var before = TwoIssues();
        var selected = WorkbenchPolicy.BuildCatalog(before).Single(finding => finding.AutomationId == "SaveButton");
        var after = before.Select(result => Replay(result, Missing("ExportButton"))).ToArray();
        after[1] = after[1] with { Observation = after[1].Observation with { VerifiedTargets = [] } };
        var verdict = WorkbenchPolicy.Verify(selected with { Occurrences = [selected.Occurrences[0]] }, before, after);
        Assert.IsFalse(verdict.Accepted);
        Assert.AreEqual("inconclusive", verdict.Status);
    }

    [TestMethod]
    public void SuiteStatusUsesActualChecksAndScanRatherThanPassedOrFailedLabels()
    {
        var failed = Observe(Scene(), Missing());
        Assert.AreEqual("failed", WorkbenchPolicy.SuiteStatus(
            [failed with { Observation = failed.Observation with { Status = "passed" } }]));
        var passed = Observe(Scene());
        Assert.AreEqual("passed", WorkbenchPolicy.SuiteStatus(
            [passed with { Observation = passed.Observation with { Status = "failed" } }]));
        Assert.AreEqual("failed", WorkbenchPolicy.SuiteStatus([WithCheck(passed, "keyboard-reachable", false)]));
        Assert.AreEqual("inconclusive", WorkbenchPolicy.SuiteStatus([]));
    }

    [TestMethod]
    [DataRow("scan")]
    [DataRow("groups")]
    [DataRow("negative-count")]
    [DataRow("missing-findings")]
    [DataRow("bad-count")]
    [DataRow("missing-rule")]
    [DataRow("missing-check")]
    [DataRow("duplicate-check")]
    [DataRow("unknown-check")]
    [DataRow("error")]
    [DataRow("cancelled")]
    [DataRow("inconclusive")]
    [DataRow("phase")]
    [DataRow("version")]
    [DataRow("engine")]
    [DataRow("checkpoint")]
    [DataRow("process")]
    [DataRow("window")]
    public void IncompleteEvidenceIsInconclusiveAndNeverAuthorizesRepair(string defect)
    {
        var before = Observe(Scene(), Missing());
        var selected = WorkbenchPolicy.BuildCatalog([before]).Single();
        var observation = before.Observation;
        var scan = observation.Scan!;
        var broken = observation with
        {
            Scan = defect switch
            {
                "scan" => null,
                "groups" => scan with { ResultGroupErrorCounts = [] },
                "negative-count" => scan with { ResultGroupErrorCounts = [-1] },
                "missing-findings" => scan with { Findings = [] },
                "bad-count" => scan with { ResultGroupErrorCounts = [0] },
                "missing-rule" => scan with { Findings = [Missing() with { RuleId = "" }] },
                "version" => scan with { EngineVersion = "" },
                "engine" => scan with { Engine = "Not Axe.Windows" },
                "checkpoint" => scan with { Checkpoint = "after-action" },
                "process" => scan with { ProcessId = 9999 },
                "window" => scan with { WindowHandle = 0 },
                _ => scan
            },
            Error = defect == "error" ? "Cleanup failed." : null,
            Status = defect is "cancelled" or "inconclusive" ? defect : observation.Status,
            Phase = defect == "phase" ? "" : observation.Phase,
            Checks = defect switch
            {
                "missing-check" => observation.Checks.Skip(1).ToList(),
                "duplicate-check" => [.. observation.Checks.Skip(1), observation.Checks[1]],
                "unknown-check" => observation.Checks.Select(check =>
                    check.RuleId == "target-visible" ? check with { RuleId = "made-up-check" } : check).ToList(),
                _ => observation.Checks
            }
        };
        var incomplete = before with { Observation = broken };
        Assert.AreEqual("inconclusive", WorkbenchPolicy.SuiteStatus([incomplete]));
        Assert.ThrowsExactly<InvalidDataException>(() => WorkbenchPolicy.FindRepairOwner(selected, [incomplete]));
        var verdict = WorkbenchPolicy.Verify(selected, [before], [incomplete]);
        Assert.IsFalse(verdict.Accepted);
        Assert.AreEqual("inconclusive", verdict.Status);
        Assert.IsTrue(verdict.Errors.Count > 0);
    }

    [TestMethod]
    public void AnIncompleteUnrelatedScenarioBlocksAutomaticRepair()
    {
        var before = TwoIssues();
        before[1] = before[1] with { Observation = before[1].Observation with { Scan = null, Status = "inconclusive" } };
        var selected = WorkbenchPolicy.BuildCatalog(before).Single(finding => finding.AutomationId == "SaveButton");
        Assert.IsFalse(selected.Repairable);
        StringAssert.Contains(selected.RepairReason!, "export");
        Assert.ThrowsExactly<InvalidDataException>(() => WorkbenchPolicy.FindRepairOwner(selected, before));
    }

    [TestMethod]
    public void DuplicateBaselineScenarioNamesMakeVerificationInconclusive()
    {
        var original = Observe(Scene(), Missing());
        var selected = WorkbenchPolicy.BuildCatalog([original]).Single();
        var before = new[] { original, original with { Scenario = original.Scenario with { Name = "SAVE" } } };
        Assert.AreEqual("inconclusive", WorkbenchPolicy.SuiteStatus(before));
        Assert.ThrowsExactly<InvalidDataException>(() => WorkbenchPolicy.FindRepairOwner(selected, before));
        Assert.AreEqual("inconclusive", WorkbenchPolicy.Verify(selected, before, [Replay(original)]).Status);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("extra")]
    [DataRow("duplicate")]
    [DataRow("case-duplicate")]
    public void MissingExtraOrDuplicateScenariosCannotBeAccepted(string defect)
    {
        var before = TwoIssues();
        var selected = WorkbenchPolicy.BuildCatalog(before).Single(finding => finding.AutomationId == "SaveButton");
        var after = before.Select(result => Replay(result)).ToArray();
        after = defect switch
        {
            "missing" => [after[0]],
            "extra" => [.. after, Replay(Observe(Scene("extra", "OtherButton")))],
            "duplicate" => [after[0], after[0]],
            _ => [after[0], after[0] with { Scenario = after[0].Scenario with { Name = "SAVE" } }]
        };
        var verdict = WorkbenchPolicy.Verify(selected, before, after);
        Assert.IsFalse(verdict.Accepted);
        Assert.AreEqual("inconclusive", verdict.Status);
    }

    [TestMethod]
    [DataRow("SchemaVersion")]
    [DataRow("Name")]
    [DataRow("Project")]
    [DataRow("TargetId")]
    [DataRow("ExpectedName")]
    [DataRow("AnchorId")]
    [DataRow("ResultId")]
    [DataRow("InputText")]
    [DataRow("ExpectedResult")]
    [DataRow("MaxTabs")]
    [DataRow("TimeoutSeconds")]
    public void EveryDeclaredScenarioInputMustRemainUnchanged(string field)
    {
        var before = Observe(Scene(), Missing());
        var selected = WorkbenchPolicy.BuildCatalog([before]).Single();
        var after = Replay(before);
        var scenario = after.Scenario;
        after = after with
        {
            Scenario = field switch
            {
                "SchemaVersion" => scenario with { SchemaVersion = 2 },
                "Name" => scenario with { Name = "renamed" },
                "Project" => scenario with { Project = Path.GetFullPath("Different.csproj") },
                "TargetId" => scenario with { TargetId = "AnotherButton" },
                "ExpectedName" => scenario with { ExpectedName = "Different name" },
                "AnchorId" => scenario with { AnchorId = "AnotherInput" },
                "ResultId" => scenario with { ResultId = "AnotherStatus" },
                "InputText" => scenario with { InputText = "Different input" },
                "ExpectedResult" => scenario with { ExpectedResult = "Different result" },
                "MaxTabs" => scenario with { MaxTabs = scenario.MaxTabs + 1 },
                _ => scenario with { TimeoutSeconds = scenario.TimeoutSeconds + 1 }
            }
        };
        var verdict = WorkbenchPolicy.Verify(selected, [before], [after]);
        Assert.IsFalse(verdict.Accepted);
        Assert.AreEqual("inconclusive", verdict.Status);
    }

    [TestMethod]
    public void AChangedEngineVersionCannotProveAFix()
    {
        var before = Observe(Scene(), Missing());
        var selected = WorkbenchPolicy.BuildCatalog([before]).Single();
        var after = Replay(before);
        after = after with { Observation = after.Observation with { Scan = after.Observation.Scan! with { EngineVersion = "2.4.3" } } };
        Assert.AreEqual("passed", WorkbenchPolicy.SuiteStatus([after]));
        var verdict = WorkbenchPolicy.Verify(selected, [before], [after]);
        Assert.IsFalse(verdict.Accepted);
        Assert.AreEqual("inconclusive", verdict.Status);
        StringAssert.Contains(verdict.Errors.Single(), "different scan engine, version, or checkpoint");
    }

    [TestMethod]
    public void AnInvalidChangedProjectPathReturnsAnInconclusiveVerdict()
    {
        var before = Observe(Scene(), Missing());
        var selected = WorkbenchPolicy.BuildCatalog([before]).Single();
        var after = Replay(before) with { Scenario = before.Scenario with { Project = "\0.csproj" } };
        var verdict = WorkbenchPolicy.Verify(selected, [before], [after]);
        Assert.IsFalse(verdict.Accepted);
        Assert.AreEqual("inconclusive", verdict.Status);
        StringAssert.Contains(verdict.Errors.Single(), "Invalid project path");
    }

    [TestMethod]
    public void StaleOrMismatchedSelectionCannotAuthorizeRepair()
    {
        var before = Observe(Scene(), Missing());
        var selected = WorkbenchPolicy.BuildCatalog([before]).Single();
        foreach (var changed in new[]
        {
            selected with { Id = "AL-NOT-PRESENT" },
            selected with { AutomationId = "OtherButton" },
            selected with { Project = Path.GetFullPath("Other.csproj") },
            selected with { RuleId = "NameNotEmpty" },
            selected with { StableIdentity = false }
        })
        {
            Assert.ThrowsExactly<InvalidDataException>(() => WorkbenchPolicy.FindRepairOwner(changed, [before]));
            var verdict = WorkbenchPolicy.Verify(changed, [before], [Replay(before)]);
            Assert.IsFalse(verdict.Accepted);
            Assert.AreEqual("inconclusive", verdict.Status);
        }
    }

    [TestMethod]
    public void ReorderedScenariosAndChecksWithNewProcessAndWindowIdsCanPass()
    {
        var before = TwoIssues();
        var selected = WorkbenchPolicy.BuildCatalog(before).Single(finding => finding.AutomationId == "SaveButton");
        var after = before.Select(result => Replay(result)).Reverse().Select(result => result with
        {
            Observation = result.Observation with { Checks = result.Observation.Checks.AsEnumerable().Reverse().ToList() }
        }).ToArray();
        Assert.IsTrue(WorkbenchPolicy.Verify(selected, before, after).Accepted);
    }

    [TestMethod]
    public void RepeatedSdkGroupsDoNotCountAsNewOccurrences()
    {
        var other = Missing("OtherButton", "other");
        var before = Observe(Scene(), Missing(), other);
        var selected = WorkbenchPolicy.BuildCatalog([before]).Single(finding => finding.AutomationId == "SaveButton");
        var after = Replay(before, other);
        after = after with
        {
            Observation = after.Observation with
            {
                Scan = AccessibilityScan.Create(5678, 99, "2.4.2", [new(1, [other]), new(1, [other])])
            }
        };
        var verdict = WorkbenchPolicy.Verify(selected, [before], [after]);
        Assert.IsTrue(verdict.Accepted);
        Assert.HasCount(1, verdict.RemainingFindings.Single().Occurrences);
    }

    private static Scenario Scene(string name = "save", string targetId = "SaveButton") => new()
    {
        Name = name, Project = Path.GetFullPath("Demo.csproj"), TargetId = targetId,
        ExpectedName = targetId + " action", AnchorId = "Input", ResultId = "Status",
        InputText = "AccessLoop", ExpectedResult = "Activated " + targetId, MaxTabs = 3, TimeoutSeconds = 1
    };

    private static AccessibilityFinding Missing(string automationId = "SaveButton", string runtimeId = "original") =>
        ScanFixture.MissingName(automationId, runtimeId);

    private static ScenarioResult Observe(Scenario scenario, params AccessibilityFinding[] findings)
    {
        var namePasses = !findings.Any(finding => finding.AutomationId == scenario.TargetId
            && finding.RuleId is "NameNotNull" or "NameNotEmpty" or "NameNotWhiteSpace");
        return new(scenario, Path.Combine("before", scenario.Name), new Observation
        {
            Phase = "before", Status = findings.Length == 0 ? "passed" : "failed", ProcessId = 1234,
            Scan = ScanFixture.Create(findings),
            Checks =
            [
                new("accessible-name", namePasses, scenario.ExpectedName, namePasses ? scenario.ExpectedName : ""),
                new("target-enabled", true, "True", "True"),
                new("target-visible", true, "False", "False"),
                new("keyboard-reachable", true, scenario.TargetId, scenario.TargetId),
                new("keyboard-activation", true, scenario.ExpectedResult, scenario.ExpectedResult)
            ],
            VerifiedTargets = [scenario.TargetId]
        });
    }

    private static ScenarioResult Replay(ScenarioResult original, params AccessibilityFinding[] findings)
    {
        var result = Observe(original.Scenario, findings);
        return result with
        {
            EvidencePath = Path.Combine("after", result.Scenario.Name),
            Observation = result.Observation with
            {
                Phase = "after", ProcessId = 5678,
                Scan = result.Observation.Scan! with { ProcessId = 5678, WindowHandle = 99 },
                VerifiedTargets = original.Observation.Scan!.Findings.Select(finding => finding.AutomationId)
                    .Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).Append(original.Scenario.TargetId)
                    .Distinct(StringComparer.Ordinal).ToArray()
            }
        };
    }

    private static ScenarioResult WithCheck(ScenarioResult result, string checkId, bool passed) => result with
    {
        Observation = result.Observation with
        {
            Checks = result.Observation.Checks.Select(check => check.RuleId == checkId
                ? check with { Passed = passed } : check).ToList()
        }
    };

    private static ScenarioResult[] TwoIssues() =>
    [
        Observe(Scene(), Missing(runtimeId: "save-1"), Missing("ExportButton", "export-1")),
        Observe(Scene("export", "ExportButton"), Missing(runtimeId: "save-2"), Missing("ExportButton", "export-2"))
    ];
}

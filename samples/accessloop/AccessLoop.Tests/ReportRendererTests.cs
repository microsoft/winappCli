using System.Net;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class ReportRendererTests
{
    private string directory = null!;
    private const string SaveId = "AL-save-fixture";
    private const string ExportId = "AL-export-fixture";

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(AppContext.BaseDirectory, "report-renderer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(directory, recursive: true);

    [TestMethod]
    public void PartialRepairSeparatesSelectedSuccessFromFailedSuiteAndGroupsOccurrences()
    {
        var save = Finding(SaveId, "SaveDocumentButton", "save-document", "export-document");
        var export = Finding(ExportId, "ExportDocumentButton", "save-document", "export-document");
        var report = Report("fixed", "failed") with
        {
            SelectedFindingId = SaveId,
            PatchApplied = true,
            Findings = [save, export],
            RemainingFindings = [export],
            Before = [
                Result("save-document", @"before\01", save.Occurrences[0].Finding, export.Occurrences[0].Finding),
                Result("export-document", @"before\02", save.Occurrences[1].Finding, export.Occurrences[1].Finding)],
            After = [
                Result("save-document", @"after\01", export.Occurrences[0].Finding),
                Result("export-document", @"after\02", export.Occurrences[1].Finding)]
        };

        var html = Render(report);

        StringAssert.Contains(html, "<h2>Action outcome</h2><p class=\"outcome pass\">fixed</p>");
        StringAssert.Contains(html, "<h2>Whole-suite status</h2><p class=\"outcome fail\">failed</p>");
        StringAssert.Contains(html, "<dt>Grouped baseline findings</dt><dd>2</dd>");
        StringAssert.Contains(html, "<dt>Latest reported findings</dt><dd>1</dd>");
        StringAssert.Contains(html, "<dt>Baseline cases recorded</dt><dd>2</dd>");
        StringAssert.Contains(html, "<dt>Rebuilt cases recorded</dt><dd>2</dd>");
        Assert.AreEqual(2, Regex.Matches(html, "<article class=\"finding").Count);
        Assert.AreEqual(2, Regex.Matches(html, "<details>").Count);
        StringAssert.Contains(html, "<article class=\"finding selected\">");
        StringAssert.Contains(html, WebUtility.HtmlEncode("Fixed — selected repair verified"));
        StringAssert.Contains(html, ">Remaining</span>");
        StringAssert.Contains(html, "save-document, export-document");
        StringAssert.Contains(html, "The whole suite has not passed.");
        Assert.IsFalse(html.Contains("No Axe.Windows findings and all assertions passed", StringComparison.Ordinal));
    }

    [TestMethod]
    public void EncodesUntrustedReportFindingAndObservationText()
    {
        static string U(string field) => $"<script>{field} & \"quoted\"</script>";
        var details = new AccessibilityFinding(U("rule"), U("description"), U("fix"), U("standard"),
            new Dictionary<string, string>
            {
                ["AutomationId"] = U("automation"),
                ["ControlType"] = U("control")
            }, []);
        var result = Result("save-document", @"before\01", details);
        result = result with
        {
            Scenario = result.Scenario with
            {
                Name = U("case"),
                Project = U("project"),
                TargetId = U("target"),
                ExpectedName = U("expected-name")
            },
            Observation = result.Observation with
            {
                Status = U("observation-status"),
                Error = U("observation-error"),
                Scan = result.Observation.Scan! with
                {
                    Engine = U("engine"),
                    EngineVersion = U("engine-version"),
                    Checkpoint = U("checkpoint")
                },
                Checks = [new(U("assertion"), false, U("expected"), U("actual"))],
                FocusTrace = [new(1, U("focus-id"), U("focus-name"))],
                VerifiedTargets = [U("verified-id")]
            }
        };
        var finding = new WorkbenchFinding(U("finding-id"), U("project"), details.RuleId,
            details.AutomationId, false, [new(result.Scenario.Name, details)])
        {
            RepairReason = U("reason"),
            OwnerScenarioName = U("owner")
        };
        var report = Report(U("status"), U("suite-status")) with
        {
            SuiteName = U("suite"),
            InputPath = U("input"),
            Inputs = [new(U("snapshot"), U("hash"), @"inputs\01.json")],
            RuleVersion = U("rule-version"),
            WinappVersion = U("winapp"),
            Error = U("error"),
            VerificationErrors = [U("verification-error")],
            RepairSource = U("advisor"),
            SelectedFindingId = finding.Id,
            Before = [result],
            Findings = [finding],
            RemainingFindings = [finding]
        };

        var html = Render(report);

        foreach (var field in new[]
        {
            "rule", "description", "fix", "standard", "automation", "control", "case", "project",
            "target", "expected-name", "observation-status", "observation-error", "engine",
            "engine-version", "checkpoint", "assertion", "expected", "actual", "focus-id",
            "focus-name", "verified-id", "finding-id", "reason", "owner", "status", "suite-status",
            "suite", "input", "snapshot", "hash", "rule-version", "winapp", "error",
            "verification-error", "advisor"
        })
            StringAssert.Contains(html, WebUtility.HtmlEncode(U(field)), field);
        Assert.IsFalse(html.Contains("<script>", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("class=\"outcome <script>", StringComparison.Ordinal));
    }

    [TestMethod]
    public void LinksUseCaseEvidencePathsAndEncodeEachRelativeUrlSegment()
    {
        const string evidence = @"before\01 #&%";
        foreach (var file in new[] { "axe-scan.json", "observation.json", "tree.json", "window.png", "after-action.png" })
        {
            WriteEvidence(Path.Combine(evidence, file));
            WriteEvidence(Path.Combine(@"after\01", file));
        }
        foreach (var file in new[] { "source-candidates.json", "proposal.json", "repair.diff", @"inputs\01 #&%.json" })
            WriteEvidence(file);
        var result = Result("save-document", evidence, MissingName("SaveDocumentButton"));
        var report = Report() with
        {
            Inputs = [new("suite.json", "hash", @"inputs\01 #&%.json")],
            SelectedFindingId = SaveId,
            Before = [result with { Observation = result.Observation with { Phase = "not-a-directory" } }],
            After = [Result("save-document", @"after\01")]
        };

        var html = Render(report);

        foreach (var file in new[] { "axe-scan.json", "observation.json", "tree.json", "window.png", "after-action.png" })
        {
            StringAssert.Contains(html, $"href=\"./before/01%20%23%26%25/{file}\"");
            StringAssert.Contains(html, $"href=\"./after/01/{file}\"");
        }
        StringAssert.Contains(html, "src=\"./before/01%20%23%26%25/window.png\"");
        StringAssert.Contains(html, "src=\"./before/01%20%23%26%25/after-action.png\"");
        StringAssert.Contains(html, "href=\"./inputs/01%20%23%26%25.json\"");
        foreach (var file in new[] { "source-candidates.json", "proposal.json", "repair.diff" })
            StringAssert.Contains(html, $"href=\"./{file}\"");
        Assert.IsFalse(html.Contains("not-a-directory/", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("<script", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("https://", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DoesNotLinkToExistingEvidenceOutsideReportDirectory()
    {
        foreach (var file in new[] { "axe-scan.json", "observation.json", "tree.json", "window.png", "after-action.png", "input.json" })
            WriteEvidence(Path.Combine("outside", file));
        var output = Path.Combine(directory, "run");
        Directory.CreateDirectory(output);
        var report = Report() with
        {
            Inputs = [new("suite.json", "hash", @"..\outside\input.json")],
            Before = [Result("save-document", @"..\outside", MissingName("SaveDocumentButton"))]
        };

        ReportRenderer.Write(Path.Combine(output, "report.html"), report);
        var html = File.ReadAllText(Path.Combine(output, "report.html"));

        Assert.IsFalse(html.Contains("outside/", StringComparison.Ordinal));
        StringAssert.Contains(html, "Axe.Windows structured evidence (not captured)");
        StringAssert.Contains(html, "suite.json (not captured)");
        StringAssert.Contains(html, "Screenshot not captured.");
    }

    [TestMethod]
    [DataRow(@"C:\evidence")]
    [DataRow(@"\\server\share")]
    [DataRow("https://example.invalid")]
    [DataRow("javascript:alert(1)")]
    [DataRow(@".. \outside")]
    public void UnsafeEvidencePathsNeverBecomeLinks(string evidencePath)
    {
        var report = Report() with
        {
            Inputs = [new("suite.json", "hash", evidencePath)],
            Before = [Result("save-document", evidencePath, MissingName("SaveDocumentButton"))]
        };

        var html = Render(report);

        Assert.AreEqual(2, Regex.Matches(html, "href=").Count); // Skip link and local report.json only.
        Assert.IsFalse(html.Contains("src=", StringComparison.Ordinal));
    }

    [TestMethod]
    public void InconclusiveRerunNeverLabelsMissingFindingFixed()
    {
        var finding = Finding(SaveId, "SaveDocumentButton", "save-document");
        var after = Result("save-document", @"after\01");
        var report = Report("inconclusive", "inconclusive") with
        {
            SelectedFindingId = SaveId,
            Findings = [finding],
            Before = [Result("save-document", @"before\01", finding.Occurrences[0].Finding)],
            After = [after with { Observation = new() { Phase = "after", Status = "inconclusive" } }],
            VerificationErrors = ["The rebuilt scan did not complete."]
        };

        var html = Render(report);

        StringAssert.Contains(html, "Scan did not complete. Missing results are not a pass.");
        StringAssert.Contains(html, "No assertions recorded; no pass is implied.");
        StringAssert.Contains(html, WebUtility.HtmlEncode("Not observed in rerun — not confirmed fixed"));
        StringAssert.Contains(html, "The rebuilt scan did not complete.");
        Assert.IsFalse(html.Contains("selected repair verified", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("class=\"outcome pass\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RegressionKeepsNewFindingsAndExplainsRollbackEvidence()
    {
        var save = Finding(SaveId, "SaveDocumentButton", "save-document");
        var added = Finding(ExportId, "ExportDocumentButton", "save-document");
        var report = Report("regression", "failed") with
        {
            SelectedFindingId = SaveId,
            PatchRolledBack = true,
            Findings = [save],
            RemainingFindings = [added],
            Before = [Result("save-document", @"before\01", save.Occurrences[0].Finding)],
            After = [Result("save-document", @"after\01", added.Occurrences[0].Finding)],
            VerificationErrors = ["A new finding appeared."]
        };

        var html = Render(report);

        Assert.AreEqual(2, Regex.Matches(html, "<article class=\"finding").Count);
        StringAssert.Contains(html, "New in rerun");
        StringAssert.Contains(html, "Source edit: <strong>rolled back</strong>");
        StringAssert.Contains(html, "Rebuilt observations describe the attempted repair, not the restored source.");
        StringAssert.Contains(html, "A new finding appeared.");
        Assert.IsFalse(html.Contains("selected repair verified", StringComparison.Ordinal));
    }

    [TestMethod]
    public void EmptyReportDoesNotImplyCleanSuiteOrCompletedCases()
    {
        var html = Render(Report("inconclusive", "inconclusive"));

        StringAssert.Contains(html, "No case observations were recorded. This is not a pass.");
        StringAssert.Contains(html, "No findings were cataloged. Missing or incomplete scans are not a pass");
        StringAssert.Contains(html, "Whole-suite success is unproven.");
        Assert.IsFalse(html.Contains("class=\"outcome pass\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AwaitingSelectionKeepsIdsReadableAndReportsNoSourceEdit()
    {
        var findings = new[]
        {
            Finding(SaveId, "SaveDocumentButton", "save-document"),
            Finding(ExportId, "ExportDocumentButton", "export-document")
        };
        var html = Render(Report("awaiting-selection", "failed") with
        {
            Findings = findings,
            RemainingFindings = findings
        });

        StringAssert.Contains(html, "<code class=\"finding-id\">AL-save-fixture</code>");
        StringAssert.Contains(html, "<code class=\"finding-id\">AL-export-fixture</code>");
        StringAssert.Contains(html, "Source edit: <strong>not applied</strong>");
        StringAssert.Contains(html, "--finding &lt;ID&gt;");
        StringAssert.Contains(html, "This is a static evidence and review page. It cannot run commands or apply changes.");
    }

    [TestMethod]
    [DataRow("The selected finding is not present in the fresh baseline.")]
    [DataRow("The selected rule requires manual review.")]
    [DataRow("Found two source candidates; a unique static Button is required.")]
    public void NeedsReviewExplainsWhyItIsNotWaitingForApproval(string reason)
    {
        var html = Render(Report("needs-review", "failed") with { Error = reason });

        StringAssert.Contains(html, "<h2>Action outcome</h2><p class=\"outcome pending\">needs-review</p>");
        StringAssert.Contains(html, "No proposal is ready for approval.");
        StringAssert.Contains(html, WebUtility.HtmlEncode(reason));
        StringAssert.Contains(html, "Source edit: <strong>not applied</strong>");
        Assert.IsFalse(html.Contains("A proposal is ready for review.", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("href=\"./proposal.json\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AwaitingApprovalLinksTheActualProposalWithoutClaimingARepair()
    {
        WriteEvidence("proposal.json");
        WriteEvidence("source-candidates.json");
        var html = Render(Report("awaiting-approval", "failed") with
        {
            SelectedFindingId = SaveId,
            RepairSource = "reviewed-proposal"
        });

        StringAssert.Contains(html, "<h2>Action outcome</h2><p class=\"outcome pending\">awaiting-approval</p>");
        StringAssert.Contains(html, "A proposal is ready for review.");
        StringAssert.Contains(html, "href=\"./proposal.json\"");
        StringAssert.Contains(html, "href=\"./source-candidates.json\"");
        StringAssert.Contains(html, "Source edit: <strong>not applied</strong>");
        Assert.IsFalse(html.Contains("No proposal is ready for approval.", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("class=\"outcome pass\"", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void OriginalSourceBackupIsLinkedWithRecoveryCautionOnlyWhenPresent(bool available)
    {
        if (available)
            WriteEvidence("source.original.txt");
        var html = Render(Report("inconclusive", "inconclusive"));

        Assert.AreEqual(available, html.Contains("href=\"./source.original.txt\"", StringComparison.Ordinal));
        Assert.AreEqual(available, html.Contains("Only restore it after reviewing the current source and any concurrent edits.", StringComparison.Ordinal));
        Assert.AreEqual(available, html.Contains("Automatic rollback refuses to overwrite source modified after the patch.", StringComparison.Ordinal));
    }

    private RunReport Report(string status = "failed", string suiteStatus = "failed") => new()
    {
        SuiteName = "document-workflows",
        InputPath = Path.Combine(directory, "suite.json"),
        Inputs = [],
        WinappVersion = "0.6.0",
        Status = status,
        SuiteStatus = suiteStatus
    };

    private ScenarioResult Result(string name, string evidencePath, params AccessibilityFinding[] findings)
    {
        var export = name == "export-document";
        var scenario = new Scenario
        {
            Name = name,
            Project = Path.Combine(directory, "Demo.csproj"),
            TargetId = export ? "ExportDocumentButton" : "SaveDocumentButton",
            ExpectedName = export ? "Export document" : "Save document",
            AnchorId = "DocumentTitleInput",
            ResultId = "SaveStatus",
            InputText = "AccessLoop",
            ExpectedResult = export ? "Exported: AccessLoop" : "Saved: AccessLoop"
        };
        var missingName = findings.Any(f => f.AutomationId == scenario.TargetId);
        return new(scenario, evidencePath, new Observation
        {
            Phase = evidencePath.StartsWith("after", StringComparison.Ordinal) ? "after" : "before",
            Status = findings.Length > 0 ? "failed" : "passed",
            Scan = AccessibilityScan.Create(1234, 42, "2.4.2",
                [new(findings.Length, findings)]),
            Checks = [
                new("intended-name", !missingName, scenario.ExpectedName, missingName ? "" : scenario.ExpectedName),
                new("keyboard-activation", true, scenario.ExpectedResult, scenario.ExpectedResult)],
            FocusTrace = [new(0, scenario.AnchorId, "Document title"), new(export ? 2 : 1, scenario.TargetId, missingName ? "" : scenario.ExpectedName)]
        });
    }

    private WorkbenchFinding Finding(string id, string target, params string[] cases) => new(
        id, Path.Combine(directory, "Demo.csproj"), "NameNotNull", target, true,
        cases.Select(name => new FindingOccurrence(name, MissingName(target))).ToArray())
    {
        Repairable = true,
        RepairReason = "Name rule with passing behavior guards.",
        OwnerScenarioName = cases.FirstOrDefault()
    };

    private static AccessibilityFinding MissingName(string target) => new(
        "NameNotNull", "The button has no accessible name.", "Set an accessible name.", "WCAG",
        new Dictionary<string, string> { ["AutomationId"] = target, ["ControlType"] = "Button" }, []);

    private void WriteEvidence(string relativePath)
    {
        var path = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");
    }

    private string Render(RunReport report)
    {
        var path = Path.Combine(directory, "report.html");
        ReportRenderer.Write(path, report);
        return File.ReadAllText(path);
    }
}

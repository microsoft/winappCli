using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class JourneyReportRendererTests
{
    private Fixture fixture = null!;

    [TestInitialize]
    public void Initialize() => fixture = new();

    [TestCleanup]
    public void Cleanup() => fixture.Dispose();

    [TestMethod]
    public void CoverageIncludesMissingDeclaredPagesAndNeverTreatsPartialAuditAsComplete()
    {
        var original = fixture.Report.Rounds[0];
        fixture.Report.Rounds[0] = original with
        {
            Status = "inconclusive",
            Pages =
            [
                original.Pages[0],
                original.Pages[1] with
                {
                    Audit = original.Pages[1].Audit! with { Status = "partial", Error = "Provider withheld required evidence." }
                }
            ]
        };
        fixture.Report.Status = "completed";
        fixture.Report.VerificationStatus = "inconclusive";
        fixture.Report.CaptureStatus = "incomplete";

        var html = fixture.Render();

        Metric(html, "coverage-declared", 3);
        Metric(html, "coverage-reached", 2);
        Metric(html, "coverage-complete", 1);
        Metric(html, "coverage-partial", 1);
        Metric(html, "coverage-unvisited", 1);
        Contains(html, "Export");
        Contains(html, "未访问：此轮没有该声明页面的结果");
        Contains(html, "audit.Status=partial");
        Contains(html, "覆盖不完整");
        Contains(html, "Provider withheld required evidence.");
        Contains(html, "completed");
        Contains(html, "执行结束不代表覆盖完整");
    }

    [TestMethod]
    public void EarlyFailureKeepsEveryExpectedPageAndMissingMetadataVisible()
    {
        fixture.Report.Rounds.Clear();
        fixture.Report.RemainingFindings = [];
        fixture.Report.OriginalSources = [];
        fixture.Report.Candidates = [];
        fixture.Report.Plan = null;
        fixture.Report.PlanSource = "unplanned";
        fixture.Report.Status = "inconclusive";
        fixture.Report.VerificationStatus = "inconclusive";
        fixture.Report.CaptureStatus = "incomplete";
        fixture.Report.Error = "Build failed before navigation.";

        var html = fixture.Render();

        Metric(html, "coverage-declared", 3);
        Metric(html, "coverage-complete", 0);
        Metric(html, "coverage-partial", 0);
        Metric(html, "coverage-unvisited", 3);
        Badge(html, "verification-status", "inconclusive");
        Badge(html, "capture-status", "incomplete");
        foreach (var text in new[] { "Home", "Documents", "Export", "无轮次记录／未知", "未生成计划",
            "未提供源文件基线", "Build failed before navigation.", "未记录导航候选" })
            Contains(html, text);
        Assert.IsFalse(html.Contains("class=\"pass\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AcceptedRepairsShowActualTwoOneZeroAndBothOriginalAndFinalEvidence()
    {
        fixture.AcceptRepair(fixture.Save, "repair-01", [fixture.Export]);
        fixture.AcceptRepair(fixture.Export, "repair-02", []);
        fixture.SaveEvidence();

        var html = fixture.Render();

        StringAssert.Contains(html, "<p id=\"finding-trend\">" + H("2 → 1 → 0") + "</p>");
        Metric(html, "finding-fixed", 2);
        Metric(html, "finding-unresolved", 0);
        Metric(html, "finding-remaining", 0);
        Metric(html, "repair-attempts", 2);
        Metric(html, "coverage-complete", 3);
        Badge(html, "verification-status", "passed");
        Badge(html, "capture-status", "completed");
        StringAssert.Contains(html, "href=\"./baseline/documents/report.html#result-1\"");
        StringAssert.Contains(html, "href=\"./baseline/export/report.html#result-1\"");
        StringAssert.Contains(html, "href=\"./repair-02/documents/report.html\"");
        StringAssert.Contains(html, "href=\"./repair-02/export/scan.json\"");
        StringAssert.Contains(html, "href=\"./repair-01/source-candidates.json\"");
        StringAssert.Contains(html, "href=\"./repair-01/repair.diff\"");
        Contains(html, "实际修复记录（剩余数来自 Repairs）");
        Contains(html, "既有源文件 App.xaml");
        Contains(html, new string('B', 64));
        Contains(html, "未触及记录");
        Assert.IsFalse(html.Contains("<img", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("data:image", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void DisappearanceWithoutAcceptedRepairsIsNotCountedAsFixed()
    {
        fixture.Report.Rounds.Add(fixture.Round("verification-without-repair", []));
        fixture.Report.RemainingFindings = [];

        var html = fixture.Render();

        StringAssert.Contains(html, "<p id=\"finding-trend\">" + H("2 → 0") + "</p>");
        Assert.IsFalse(html.Contains(H("2 → 1 → 0"), StringComparison.Ordinal));
        Metric(html, "finding-fixed", 0);
        Metric(html, "finding-unresolved", 2);
        Metric(html, "repair-attempts", 0);
        Contains(html, "没有修复记录");
        Contains(html, "不是修复");
    }

    [TestMethod]
    public void RejectedZeroFindingRoundDoesNotHideRollbackOrRetainedFindings()
    {
        fixture.Report.Rounds.Add(fixture.Round("repair-01", []));
        fixture.Report.Repairs.Add(fixture.Repair(fixture.Save, "repair-01") with
        {
            Status = "regression",
            RolledBack = true,
            Errors = ["An unrelated control disappeared; selected repair rejected."],
            RemainingFindings = 2
        });
        fixture.Report.Status = "regression";
        fixture.SaveEvidence();

        var html = fixture.Render();

        Metric(html, "finding-fixed", 0);
        Metric(html, "finding-unresolved", 2);
        Metric(html, "finding-blocked", 1);
        Metric(html, "finding-remaining", 2);
        StringAssert.Contains(html, "<p id=\"finding-trend\">" + H("2 → 0") + "</p>");
        Contains(html, "true · 已记录回滚");
        Contains(html, "An unrelated control disappeared; selected repair rejected.");
        Contains(html, "回滚前的尝试");
        Contains(html, "不是回滚后源文件的新扫描");
        StringAssert.Contains(html, "href=\"./repair-01/documents/report.html\"");
        StringAssert.Contains(html, "href=\"./repair-01/source.original.txt\"");
        Contains(html, fixture.Report.Repairs[0].Diff!);
    }

    [TestMethod]
    [DataRow("blocked")]
    [DataRow("regression")]
    public void FailedRepairWithoutConfirmedRollbackNeverPromisesUnchangedSource(string status)
    {
        fixture.Report.Repairs.Add(fixture.Repair(fixture.Save, "repair-01") with
        {
            Status = status,
            Errors = ["Rollback refused/failed: source changed concurrently."],
            RemainingFindings = 2
        });

        var html = fixture.Render();

        Metric(html, "finding-fixed", 0);
        Contains(html, status);
        Contains(html, "false · 未记录已回滚，不证明源文件未变");
        Contains(html, "Rollback refused/failed: source changed concurrently.");
        Contains(html, "不要覆盖并发编辑");
    }

    [TestMethod]
    public void FixedRepairRecordCannotOverruleUnreliablePartialVerification()
    {
        fixture.AcceptRepair(fixture.Save, "repair-01", [fixture.Export]);
        var round = fixture.Report.Rounds[1];
        fixture.Report.Rounds[1] = round with
        {
            Pages = round.Pages.Select(page => page.PageId == "documents"
                ? page with { Audit = page.Audit! with { Status = "partial" } } : page).ToArray()
        };

        var html = fixture.Render();

        Metric(html, "finding-fixed", 0);
        Metric(html, "finding-unresolved", 2);
        Metric(html, "coverage-complete", 2);
        Contains(html, "audit.Status=partial");
    }

    [TestMethod]
    public void PrivacyOnlyPartialCaptureKeepsVerifiedRepairsAndSeparateStatuses()
    {
        fixture.AcceptRepair(fixture.Save, "repair-01", [fixture.Export]);
        fixture.AcceptRepair(fixture.Export, "repair-02", []);
        for (var index = 0; index < fixture.Report.Rounds.Count; index++)
        {
            var round = fixture.Report.Rounds[index];
            fixture.Report.Rounds[index] = round with
            {
                Pages = round.Pages.Select(page => page with { Audit = Fixture.PrivacyOnlyAudit(page.Audit!) }).ToArray()
            };
        }
        fixture.Report.Status = "partial";
        fixture.Report.VerificationStatus = "passed";
        fixture.Report.CaptureStatus = "partial";
        fixture.SaveEvidence();
        var model = JsonSerializer.Serialize(fixture.Report, Json.Options);
        var scanPath = Path.Combine(fixture.Output, "repair-02", "documents", "scan.json");
        var scan = File.ReadAllBytes(scanPath);

        var html = fixture.Render();

        Badge(html, "run-status", "partial");
        Badge(html, "verification-status", "passed");
        Badge(html, "capture-status", "partial");
        Metric(html, "coverage-declared", 3);
        Metric(html, "coverage-reached", 3);
        Metric(html, "coverage-required", 3);
        Metric(html, "coverage-complete", 0);
        Metric(html, "coverage-partial", 3);
        Metric(html, "coverage-unvisited", 0);
        Metric(html, "finding-fixed", 2);
        Metric(html, "finding-unresolved", 0);
        Metric(html, "finding-blocked", 0);
        StringAssert.Contains(html, "<p id=\"finding-trend\">" + H("2 → 1 → 0") + "</p>");
        Contains(html, "必要缺陷／功能验证已通过，采集仍为 partial");
        Contains(html, "退出码 2");
        Contains(html, "采集覆盖不完整");
        Contains(html, "audit.Status=partial");
        Assert.IsFalse(html.Contains(H("必要验证证据不足："), StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("class=\"pass\"", StringComparison.Ordinal));
        CollectionAssert.AreEqual(new[] { 0, 1, 1 },
            fixture.Report.Rounds[0].Pages.Select(page => page.Audit!.Axe!.Findings.Count).ToArray());
        CollectionAssert.AreEqual(scan, File.ReadAllBytes(scanPath));
        Assert.AreEqual(model, JsonSerializer.Serialize(fixture.Report, Json.Options));
        Assert.IsTrue(fixture.Report.Rounds.SelectMany(round => round.Pages)
            .All(page => page.Audit is { Status: "partial", Snapshot: { } snapshot }
                && snapshot.GetProperty("status").GetString() == "partial"));
    }

    [TestMethod]
    [DataRow("depth-truncation")]
    [DataRow("element-truncation")]
    [DataRow("capture-error")]
    [DataRow("root-mismatch")]
    [DataRow("missing-axe")]
    [DataRow("route")]
    [DataRow("control")]
    [DataRow("exercise")]
    public void PrivacyWithholdingNeverExcusesUnreliableRequiredEvidence(string failure)
    {
        fixture.AcceptRepair(fixture.Save, "repair-01", [fixture.Export]);
        var round = fixture.Report.Rounds[1];
        var page = round.Pages[1] with { Audit = Fixture.PrivacyOnlyAudit(round.Pages[1].Audit!) };
        page = failure switch
        {
            "depth-truncation" => page with
            {
                Audit = page.Audit! with
                {
                    Snapshot = AuditTestFixture.Edit(page.Audit!.Snapshot!.Value, json => json["depthLimitReached"] = true)
                }
            },
            "element-truncation" => page with
            {
                Audit = page.Audit! with
                {
                    Snapshot = AuditTestFixture.Edit(page.Audit!.Snapshot!.Value, json => json["elementLimitReached"] = true)
                }
            },
            "capture-error" => page with { Audit = page.Audit! with { Error = "Snapshot capture failed." } },
            "root-mismatch" => page with { Audit = page.Audit! with { Error = "Snapshot root identity changed." } },
            "missing-axe" => page with { Audit = page.Audit! with { Axe = null } },
            "route" => page with { Route = page.Route with { FromPageId = "unexpected-page" } },
            "control" => page with
            {
                ObservedControlIds = page.ObservedControlIds.Where(id => id != "SaveDocumentButton").ToArray()
            },
            "exercise" => page with { Exercise = page.Exercise! with { Error = "Real keyboard input unavailable." } },
            _ => throw new InvalidOperationException("Unexpected test case.")
        };
        fixture.Report.Rounds[1] = round with { Pages = [round.Pages[0], page, round.Pages[2]] };
        fixture.Report.Status = "partial";
        fixture.Report.VerificationStatus = "passed";
        fixture.Report.CaptureStatus = "partial";

        var html = fixture.Render();

        Metric(html, "finding-fixed", 0);
        Metric(html, "finding-unresolved", 2);
        Metric(html, "coverage-required", 2);
        Metric(html, "coverage-complete", 2);
        Metric(html, "coverage-partial", 1);
        Contains(html, "必要验证证据不足：");
        Contains(html, "audit.Status=partial");
        Assert.IsFalse(html.Contains(H("必要缺陷／功能验证已通过，采集仍为 partial"), StringComparison.Ordinal));
    }

    [TestMethod]
    public void OrphanRepairDoesNotInventAFixedBaselineFinding()
    {
        fixture.Report.Rounds.Clear();
        fixture.Report.Rounds.Add(fixture.Round("baseline", []));
        fixture.Report.Rounds.Add(fixture.Round("repair-01", []));
        fixture.Report.Repairs.Add(fixture.Repair(fixture.Save, "repair-01"));
        fixture.Report.RemainingFindings = [];

        var html = fixture.Render();

        Metric(html, "finding-fixed", 0);
        Metric(html, "finding-unresolved", 1);
        Contains(html, "缺少原始问题记录，无法判断");
    }

    [TestMethod]
    [DataRow("declared-order", "声明顺序（未调用 AI 规划）")]
    [DataRow("reviewed-plan", "已审阅计划（提供的 JSON；不推断作者或模型）")]
    [DataRow("copilot", "Copilot 有界规划（根据观察到的导航候选）")]
    [DataRow("unplanned", "未生成计划")]
    [DataRow("future-mode", "未识别来源（不推断是否使用 AI）")]
    public void PlanAttributionComesOnlyFromTheRecordedMode(string mode, string expected)
    {
        fixture.Report.PlanSource = mode;

        var html = fixture.Render();

        Contains(html, expected);
        Contains(html, mode);
        if (mode != "copilot")
            Assert.IsFalse(html.Contains(H("Copilot 有界规划（根据观察到的导航候选）"), StringComparison.Ordinal));
        StringAssert.Contains(html, "href=\"./routes.json\"");
        StringAssert.Contains(html, "href=\"./discovery/candidates.json\"");
        StringAssert.Contains(html, "href=\"./baseline-round.json\"");
    }

    [TestMethod]
    public void AppPlannerFindingCheckSourceAndErrorStringsAreHtmlEscaped()
    {
        static string U(string name) => $"<script>{name} & \"quoted\" 'text'</script>";
        var finding = fixture.Save with
        {
            Id = U("finding-id"), RuleId = U("rule"), AutomationId = U("automation"), Project = U("finding-project"),
            Repairable = false, RepairReason = U("repair-reason"),
            Occurrences =
            [
                new("documents", fixture.Save.Occurrences[0].Finding with
                {
                    Description = U("description"), HowToFix = U("guidance"),
                    ElementProperties = new Dictionary<string, string> { ["RuntimeId"] = U("runtime"), ["AutomationId"] = U("control") }
                })
            ]
        };
        var round = fixture.Report.Rounds[0];
        var page = round.Pages[1];
        fixture.Report = fixture.Report with
        {
            Name = U("name"), Project = U("project"), RunId = U("run"), InputSha256 = U("input-hash"),
            WinappVersion = U("version"), Error = U("run-error"), Errors = [U("errors")],
            VerificationStatus = U("verification-status"), CaptureStatus = U("capture-status"),
            Plan = new([U("plan-order")], U("plan-explanation")),
            Candidates = [new(U("candidate-page"), U("navigation"), U("candidate-name"), U("control-type"))],
            ExpectedPages = fixture.Pages.Select(expected => expected.Id == "documents" ? expected with { Title = U("page-title") } : expected).ToArray(),
            OriginalSources = [new(U("source-path"), U("source-hash"))],
            Rounds =
            [
                round with
                {
                    Error = U("round-error"), Findings = [finding],
                    Pages =
                    [
                        page with
                        {
                            Error = U("page-error"),
                            Checks = [new(U("check-id"), false, U("expected"), U("actual"))],
                            Audit = page.Audit! with
                            {
                                Error = U("audit-error"),
                                Target = page.Audit!.Target with { Title = U("window-title"), ProcessName = U("process"), RootRuntimeId = U("root-runtime") }
                            },
                            Exercise = page.Exercise! with
                            {
                                Error = U("exercise-error"), FocusTrace = [new(1, U("focus-id"), U("focus-name"))]
                            }
                        }
                    ]
                }
            ],
            RemainingFindings = [finding],
            Repairs =
            [
                fixture.Repair(finding, "repair-01") with
                {
                    Status = U("repair-status"), SourcePath = U("candidate-source"), Diff = U("diff"), Errors = [U("repair-error")]
                }
            ]
        };

        var html = fixture.Render();

        foreach (var name in new[]
        {
            "finding-id", "rule", "automation", "finding-project", "repair-reason", "description", "guidance", "runtime", "control",
            "name", "project", "run", "input-hash", "version", "run-error", "errors", "plan-order", "plan-explanation",
            "verification-status", "capture-status",
            "candidate-page", "navigation", "candidate-name", "control-type", "page-title", "source-path", "source-hash",
            "round-error", "page-error", "check-id", "expected", "actual", "audit-error", "window-title", "process", "root-runtime",
            "exercise-error", "focus-id", "focus-name", "repair-status", "candidate-source", "diff", "repair-error"
        })
            Contains(html, U(name));
        Assert.IsFalse(html.Contains("<script", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains(U("diff"), StringComparison.Ordinal));
    }

    [TestMethod]
    public void ExistingEvidenceUsesEncodedRelativeLinksAndNeverRemoteAssetsOrSourceLinks()
    {
        var round = fixture.Report.Rounds[0];
        fixture.Report.Rounds[0] = round with
        {
            Pages = round.Pages.Select(page => page.PageId == "documents"
                ? page with { EvidencePath = @"baseline\documents #&%" } : page).ToArray()
        };
        fixture.Report = fixture.Report with { Project = @"C:\source-owned\current-worktree\Demo.csproj" };
        fixture.SaveEvidence();

        var html = fixture.Render();

        StringAssert.Contains(html, "href=\"./baseline/documents%20%23%26%25/report.html\"");
        StringAssert.Contains(html, "href=\"./baseline/documents%20%23%26%25/report.html#result-1\"");
        StringAssert.Contains(html, "href=\"./baseline/documents%20%23%26%25/scan.json\"");
        Contains(html, fixture.Report.Project);
        foreach (Match match in Regex.Matches(html, "href=\"([^\"]+)\""))
            Assert.IsTrue(match.Groups[1].Value.StartsWith("./", StringComparison.Ordinal)
                || match.Groups[1].Value.StartsWith('#'), match.Value);
        foreach (var unexpected in new[] { "<script", "<img", "<iframe", "<link ", "file://", "https://", "http://" })
            Assert.IsFalse(html.Contains(unexpected, StringComparison.OrdinalIgnoreCase), unexpected);
        StringAssert.Contains(html, "<html lang=\"zh-CN\">");
        StringAssert.Contains(html, "<th scope=\"col\">");
        StringAssert.Contains(html, "href=\"#main\"");
    }

    [TestMethod]
    [DataRow(@"..\outside")]
    [DataRow(@"baseline\..\documents")]
    [DataRow(@".\baseline\documents")]
    [DataRow(@"C:\outside")]
    [DataRow(@"C:outside")]
    [DataRow(@"\\server\share")]
    [DataRow("file:///C:/outside")]
    [DataRow("https://example.invalid/evidence")]
    [DataRow("javascript:alert(1)")]
    [DataRow(@"baseline:stream")]
    [DataRow(@"baseline\\documents")]
    [DataRow(@"baseline\CON")]
    [DataRow(@"..\outside<script>")]
    public void UnsafeEvidenceIsDisplayedAsUnavailableAndNeverLinked(string unsafePath)
    {
        Directory.CreateDirectory(Path.Combine(fixture.Root, "outside"));
        File.WriteAllText(Path.Combine(fixture.Root, "outside", "report.html"), "outside evidence");
        var round = fixture.Report.Rounds[0];
        fixture.Report.Rounds[0] = round with
        {
            Pages = round.Pages.Select(page => page.PageId == "documents" ? page with { EvidencePath = unsafePath } : page).ToArray()
        };
        fixture.Report.Repairs.Add(fixture.Repair(fixture.Save, unsafePath) with { Status = "blocked" });

        var html = fixture.Render();

        Contains(html, unsafePath + @"\report.html");
        Contains(html, unsafePath + @"\scan.json");
        Contains(html, "页面报告不可用");
        Contains(html, "source-candidates.json不可用");
        Assert.IsFalse(html.Contains("href=\"./..", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("href=\"file:", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("href=\"javascript:", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("<script", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void MissingPageReportDoesNotHideAnAvailableScanOrGenerateABrokenLink()
    {
        File.Delete(Path.Combine(fixture.Output, "baseline", "documents", "report.html"));

        var html = fixture.Render();

        Contains(html, "页面报告不可用");
        Assert.IsFalse(html.Contains("href=\"./baseline/documents/report.html", StringComparison.Ordinal));
        StringAssert.Contains(html, "href=\"./baseline/documents/scan.json\"");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ReparsePointDirectoriesAndFilesAreNeverEvidenceLinks(bool directoryLink)
    {
        var target = Path.Combine(fixture.Root, "link-target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "report.html"), "outside report");
        File.WriteAllText(Path.Combine(target, "scan.json"), "{}");
        var linkDirectory = Path.Combine(fixture.Output, "linked");
        var linkPath = directoryLink ? linkDirectory : Path.Combine(linkDirectory, "report.html");
        try
        {
            if (directoryLink)
                Directory.CreateSymbolicLink(linkPath, target);
            else
            {
                Directory.CreateDirectory(linkDirectory);
                File.CreateSymbolicLink(linkPath, Path.Combine(target, "report.html"));
            }
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("Creating a test-owned symbolic link is unavailable: " + error.Message);
            return;
        }
        try
        {
            var round = fixture.Report.Rounds[0];
            fixture.Report.Rounds[0] = round with
            {
                Pages = round.Pages.Select(page => page.PageId == "documents" ? page with { EvidencePath = "linked" } : page).ToArray()
            };

            var html = fixture.Render();

            Contains(html, "页面报告不可用");
            Assert.IsFalse(html.Contains("href=\"./linked/report.html", StringComparison.Ordinal));
            Assert.IsFalse(html.Contains("href=\"./link-target", StringComparison.Ordinal));
        }
        finally
        {
            if (directoryLink) Directory.Delete(linkPath);
            else File.Delete(linkPath);
        }
    }

    [TestMethod]
    public void NormalObservationsAndUnknownOptionalChecksAreNotFindingInventoryEntries()
    {
        fixture.Report.Rounds.Clear();
        var round = fixture.Round("baseline", []);
        fixture.Report.Rounds.Add(round with
        {
            Pages = round.Pages.Select(page => page with
            {
                Audit = page.Audit! with
                {
                    Results =
                    [
                        new() { CheckId = "capture.scope", Status = "observed", Summary = "Requested process and window captured." },
                        new() { CheckId = "uia.identifiers", Status = "observed", Summary = "An AutomationId is absent." },
                        new() { CheckId = "text.contrast", Status = "inconclusive", Summary = "Provider value withheld." },
                        new() { CheckId = "visual.estimates", Status = "unsupported", Summary = "No pixel contrast rule." },
                        new() { CheckId = "content.meaning", Status = "not-tested", Summary = "Not requested." }
                    ]
                }
            }).ToArray()
        });
        fixture.Report.RemainingFindings = [];
        fixture.Report.Status = "passed";
        fixture.Report.VerificationStatus = "passed";
        fixture.Report.CaptureStatus = "completed";

        var html = fixture.Render();

        Metric(html, "finding-fixed", 0);
        Metric(html, "finding-unresolved", 0);
        Metric(html, "finding-remaining", 0);
        Contains(html, "未记录问题");
        Contains(html, "observed: 2");
        Contains(html, "inconclusive: 1");
        Contains(html, "unsupported: 1");
        Contains(html, "not-tested: 1");
        Contains(html, "这不是 WCAG 合规结论");
        Assert.IsFalse(html.Contains("<h3><code>capture.scope", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RuntimeIdentityMissingOrWithheldStaysExplicitAndManualRepairReasonIsRetained()
    {
        var finding = fixture.Save with
        {
            Repairable = false,
            RepairReason = "No unique source mapping; manual review required.",
            Occurrences =
            [
                new("documents", fixture.Save.Occurrences[0].Finding with
                {
                    ElementProperties = new Dictionary<string, string> { ["RuntimeId"] = "withheld" }
                })
            ]
        };
        var round = fixture.Report.Rounds[0];
        fixture.Report.Rounds[0] = round with
        {
            Findings = [finding],
            Pages = round.Pages.Select(page => page with
            {
                Audit = page.Audit! with { Target = page.Audit!.Target with { RootRuntimeId = null } }
            }).ToArray()
        };
        fixture.Report.RemainingFindings = [finding];

        var html = fixture.Render();

        StringAssert.Contains(html, "<dt>" + H("根 RuntimeId") + "</dt><dd>" + H("未提供／未知") + "</dd>");
        Contains(html, "withheld（隐私保留，不能当作有效值）");
        Contains(html, "No unique source mapping; manual review required.");
        Contains(html, "原始页面报告（无法唯一关联控件截图）");
        Metric(html, "finding-blocked", 1);
    }

    [TestMethod]
    public void RenderingAddsOnlyTheJourneyHtmlAndNeverChangesEvidenceOrTheReportModel()
    {
        var imagePath = Path.Combine(fixture.Output, "baseline", "documents", "screenshot.png");
        File.WriteAllBytes(imagePath, [1, 2, 3, 4]);
        var before = Directory.GetFiles(fixture.Output, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);
        var model = JsonSerializer.Serialize(fixture.Report, Json.Options);

        fixture.Render();

        Assert.AreEqual(before.Count + 1, Directory.GetFiles(fixture.Output, "*", SearchOption.AllDirectories).Length);
        foreach (var (path, bytes) in before)
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path), path);
        Assert.AreEqual(model, JsonSerializer.Serialize(fixture.Report, Json.Options));
    }

    [TestMethod]
    public void ExistingJourneyReportIsNeverOverwritten()
    {
        var original = fixture.Render();

        Assert.ThrowsExactly<IOException>(() => JourneyReportRenderer.Write(fixture.HtmlPath, fixture.Report));

        Assert.AreEqual(original, File.ReadAllText(fixture.HtmlPath));
    }

    private static void Metric(string html, string id, int value) =>
        StringAssert.Contains(html, $"<dd id=\"{id}\">{value}</dd>");
    private static void Badge(string html, string id, string value) =>
        StringAssert.Contains(html, $"<span class=\"badge\" id=\"{id}\">{H(value)}</span>");
    private static void Contains(string html, string text) => StringAssert.Contains(html, H(text));
    private static string H(string value) => WebUtility.HtmlEncode(value);

    private sealed class Fixture : IDisposable
    {
        private static readonly DateTimeOffset Time = new(2026, 9, 17, 7, 0, 0, TimeSpan.Zero);
        public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "journey-renderer-test-data", Guid.NewGuid().ToString("N"));
        public string Output => Path.Combine(Root, "evidence");
        public string HtmlPath => Path.Combine(Output, "journey-report.html");
        public JourneyReport Report { get; set; }
        public JourneyPageDefinition[] Pages { get; } =
        [
            new()
            {
                Id = "home", Title = "Home", NavigationId = "NavHome", ReadyId = "HomePageRoot",
                RequiredControlIds = ["HomePageRoot", "HomeHeading"]
            },
            new()
            {
                Id = "documents", Title = "Documents", NavigationId = "NavDocuments", ReadyId = "DocumentsPageRoot",
                RequiredControlIds = ["DocumentsPageRoot", "DocumentTitleInput", "SaveDocumentButton", "SaveStatus"],
                Exercise = new("SaveDocumentButton", "Save document", "DocumentTitleInput", "SaveStatus",
                    "Report", "Not saved", "Saved: Report")
            },
            new()
            {
                Id = "export", Title = "Export", NavigationId = "NavExport", ReadyId = "ExportPageRoot",
                RequiredControlIds = ["ExportPageRoot", "ExportTitleInput", "ExportDocumentButton", "ExportStatus"],
                Exercise = new("ExportDocumentButton", "Export document", "ExportTitleInput", "ExportStatus",
                    "Report", "Not exported", "Exported: Report")
            }
        ];
        public WorkbenchFinding Save { get; }
        public WorkbenchFinding Export { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Output);
            Save = Finding("documents", "SaveDocumentButton");
            Export = Finding("export", "ExportDocumentButton");
            Report = new()
            {
                Name = "Three-page demo", RunId = "journey-fixture", Project = Path.Combine(Root, "project", "Demo.csproj"),
                StartedAtUtc = Time, FinishedAtUtc = Time.AddSeconds(9), InputSha256 = new string('A', 64),
                WinappVersion = "current-native-fixture", Status = "awaiting-approval",
                VerificationStatus = "failed", CaptureStatus = "completed",
                PlanSource = "declared-order", Plan = new(["home", "documents", "export"], "Declared order, not a model decision."),
                Candidates = Pages.Select(page => new JourneyCandidate(page.Id, page.NavigationId, page.Title, "Button")).ToArray(),
                ExpectedPages = Pages,
                FrozenRoutes = Pages.Select(page => new JourneyRoute(page.Id, "home", page.NavigationId, page.ReadyId)).ToArray(),
                OriginalSources = [new("DocumentsPage.xaml", new string('A', 64)), new("既有源文件 App.xaml", new string('B', 64))],
                Rounds = [Round("baseline", [Save, Export])],
                RemainingFindings = [Save, Export]
            };
            SaveEvidence();
        }

        private WorkbenchFinding Finding(string page, string control) => new(
            "J-" + page, Path.Combine(Root, "project", "Demo.csproj"), "NameNotNull", control, true,
            [new(page, new("NameNotNull", "Accessible name is missing.", "Add the reviewed name.", "WCAG",
                new Dictionary<string, string>
                {
                    ["AutomationId"] = control, ["RuntimeId"] = "runtime-" + control, ["ControlType"] = "Button"
                }, ["Invoke"]))])
        { Repairable = true, OwnerScenarioName = page };

        public JourneyRound Round(string phase, WorkbenchFinding[] findings) => new()
        {
            Phase = phase, Status = findings.Length == 0 ? "passed" : "failed", Findings = findings,
            Pages = Pages.Select(expected => Page(expected, phase, findings)).ToArray()
        };

        private static JourneyPageResult Page(JourneyPageDefinition expected, string phase, WorkbenchFinding[] findings)
        {
            var native = findings.SelectMany(finding => finding.Occurrences)
                .Where(occurrence => occurrence.ScenarioName == expected.Id).Select(occurrence => occurrence.Finding).ToArray();
            var scan = AccessibilityScan.Create(42, 73, "2.4.2", [new(native.Length, native)]);
            var audit = new AuditReport
            {
                RunId = phase + "-" + expected.Id, StartedAtUtc = Time, FinishedAtUtc = Time.AddSeconds(1),
                Status = "completed", Mode = "snapshot", Target = new(42, 73, "JourneyDemo", Time, "Journey Demo", "runtime-root"),
                WinappVersion = "current-native-fixture", Axe = scan,
                Snapshot = JsonSerializer.SerializeToElement(new
                {
                    nodes = native.Select((finding, index) => new
                    {
                        id = "n" + index, runtimeId = finding.ElementProperties.GetValueOrDefault("RuntimeId"),
                        automationId = finding.AutomationId
                    }).ToArray()
                }),
                Results = new AuditCheckResult[]
                {
                    new() { CheckId = "capture.scope", Status = "observed", Summary = "Requested window captured." }
                }.Concat(native.Select((finding, index) => new AuditCheckResult
                {
                    CheckId = "axe.scan", Status = "rule-failure", Summary = finding.Description,
                    SourceRuleId = finding.RuleId, ElementId = "n" + index
                })).ToArray()
            };
            return new()
            {
                PageId = expected.Id, Status = native.Length == 0 ? "passed" : "failed",
                EvidencePath = Path.Combine(phase, expected.Id),
                Route = new(expected.Id, "home", expected.NavigationId, expected.ReadyId),
                ObservedControlIds = expected.RequiredControlIds,
                Checks = [new("page-ready", true, expected.ReadyId, expected.ReadyId)],
                Audit = audit,
                Exercise = expected.Exercise is not { } exercise ? null : new()
                {
                    Phase = phase, Status = native.Length == 0 ? "passed" : "failed", Scan = scan,
                    Checks =
                    [
                        new("accessible-name", native.Length == 0, exercise.ExpectedName, native.Length == 0 ? exercise.ExpectedName : ""),
                        new("target-enabled", true, "True", "True"),
                        new("target-visible", true, "False", "False"),
                        new("keyboard-reachable", true, exercise.TargetId, exercise.TargetId),
                        new("keyboard-activation", true, exercise.ExpectedResult, exercise.ExpectedResult)
                    ],
                    FocusTrace = [new(1, exercise.TargetId, native.Length == 0 ? exercise.ExpectedName : "")],
                    VerifiedTargets = expected.RequiredControlIds
                }
            };
        }

        public JourneyRepair Repair(WorkbenchFinding finding, string phase) => new()
        {
            FindingId = finding.Id, PageId = finding.OwnerScenarioName!, AutomationId = finding.AutomationId,
            Status = "fixed", EvidencePath = phase, SourcePath = finding.OwnerScenarioName == "documents" ? "DocumentsPage.xaml" : "ExportPage.xaml",
            Diff = "- <Button />\n+ <Button AutomationProperties.Name=\"Reviewed name\" />"
        };

        public static AuditReport PrivacyOnlyAudit(AuditReport audit) => audit with
        {
            Status = "partial",
            Snapshot = AuditTestFixture.Edit(audit.Snapshot!.Value, json =>
            {
                json["status"] = "partial";
                json["processId"] = audit.Target.ProcessId;
                json["windowHandle"] = audit.Target.WindowHandle;
                json["rootRuntimeId"] = audit.Target.RootRuntimeId;
                json["depthLimitReached"] = false;
                json["elementLimitReached"] = false;
                json["issues"] = new JsonArray();
                var nodes = json["nodes"]!.AsArray();
                foreach (var node in nodes)
                {
                    var element = node!.AsObject();
                    element["issues"] = new JsonArray();
                    element["isPassword"] = false;
                }
                nodes.Add(new JsonObject
                {
                    ["id"] = "host", ["runtimeId"] = "runtime-host", ["className"] = "InputSiteWindowClass",
                    ["name"] = null, ["textRuns"] = new JsonArray(),
                    ["issues"] = new JsonArray(new JsonObject
                    {
                        ["code"] = "privacy_unavailable", ["message"] = "IsPassword unavailable; name and text withheld."
                    })
                });
            }),
            Results = audit.Results.Append(new AuditCheckResult
            {
                CheckId = "capture.tree", Status = "inconclusive", Summary = "Host name and text withheld for privacy."
            }).ToArray()
        };

        public void AcceptRepair(WorkbenchFinding finding, string phase, WorkbenchFinding[] remaining)
        {
            Report.Rounds.Add(Round(phase, remaining));
            Report.Repairs.Add(Repair(finding, phase) with { RemainingFindings = remaining.Length });
            Report.RemainingFindings = remaining;
            Report.Status = remaining.Length == 0 ? "passed" : "needs-review";
            Report.VerificationStatus = remaining.Length == 0 ? "passed" : "failed";
            Report.CaptureStatus = "completed";
        }

        public void SaveEvidence()
        {
            foreach (var round in Report.Rounds)
            {
                File.WriteAllText(Path.Combine(Output, round.Phase + "-round.json"), JsonSerializer.Serialize(round, Json.Options));
                foreach (var page in round.Pages)
                {
                    var directory = Path.Combine(Output, page.EvidencePath);
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, "scan.json"), JsonSerializer.Serialize(page.Audit, Json.Options));
                    File.WriteAllText(Path.Combine(directory, "report.html"),
                        "<!doctype html><html lang=\"zh-CN\"><title>Original page evidence</title><article id=\"result-1\">Existing crop evidence</article></html>");
                }
            }
            foreach (var repair in Report.Repairs)
            {
                var directory = Path.Combine(Output, repair.EvidencePath);
                Directory.CreateDirectory(directory);
                foreach (var file in new[] { "source-candidates.json", "proposal.json", "repair-result.json", "repair.diff", "source.original.txt" })
                    File.WriteAllText(Path.Combine(directory, file), file + " · preserved fixture bytes");
            }
            foreach (var (file, value) in new (string, object?)[]
            {
                ("journey-report.json", Report), ("navigation-plan.json", Report.Plan), ("routes.json", Report.FrozenRoutes),
                ("source-baseline.json", Report.OriginalSources), ("journey-input.json", new { schemaVersion = 1 }),
                ("acceptance.json", new { pages = Report.ExpectedPages })
            })
                File.WriteAllText(Path.Combine(Output, file), JsonSerializer.Serialize(value, Json.Options));
            Directory.CreateDirectory(Path.Combine(Output, "discovery"));
            File.WriteAllText(Path.Combine(Output, "discovery", "candidates.json"), JsonSerializer.Serialize(Report.Candidates, Json.Options));
        }

        public string Render()
        {
            JourneyReportRenderer.Write(HtmlPath, Report);
            return File.ReadAllText(HtmlPath);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}

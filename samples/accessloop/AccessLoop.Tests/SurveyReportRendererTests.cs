using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class SurveyReportRendererTests
{
    private AuditTestFixture fixture = null!;

    [TestInitialize]
    public void Initialize() => fixture = new();

    [TestCleanup]
    public void Cleanup() => fixture.Dispose();

    [TestMethod]
    public void OverviewPrioritizesPageLinksAndReadableTimeWhileKeepingTraceabilityFolded()
    {
        var state = State(CapturedPage("First", NavigationReport())) with
        {
            Invocations =
            [
                new() { Number = 1, StartedAtUtc = AuditTestFixture.Time,
                    FinishedAtUtc = AuditTestFixture.Time.AddMilliseconds(938104), Status = "finished",
                    Resuming = false,
                    Restoration = new("restored-navigation", "Restored only navigation.") },
                new() { Number = 2, StartedAtUtc = AuditTestFixture.Time.AddMinutes(20),
                    FinishedAtUtc = AuditTestFixture.Time.AddMinutes(20).AddMilliseconds(75105),
                    Resuming = true, OperatorConfirmedUnchangedState = true, RetryPages = ["First"],
                    Status = "finished", Restoration = new("restored-navigation", "Restored only navigation.") }
            ]
        };

        var html = Render(state);
        var summary = Section(html, "progress");
        StringAssert.Contains(summary, "16 分 53 秒");
        StringAssert.Contains(summary, "15 分 38 秒");
        StringAssert.Contains(summary, "1 分 15 秒");
        StringAssert.Contains(summary, "含重试，不含段间暂停");
        var firstDisclosure = summary.IndexOf("<details", StringComparison.Ordinal);
        StringAssert.Contains(summary[..firstDisclosure], "id=\"captured-pages\"");
        StringAssert.Contains(summary[..firstDisclosure], "id=\"failure-entries\"");
        StringAssert.Contains(summary[..firstDisclosure], "id=\"failure-occurrences\"");
        StringAssert.Contains(summary[..firstDisclosure], "覆盖限制：");
        Assert.IsFalse(summary[..firstDisclosure].Contains("id=\"pending-pages\"", StringComparison.Ordinal));
        Assert.IsFalse(summary.Contains(state.RunId, StringComparison.Ordinal));
        StringAssert.Contains(html, "<details class=\"report-detail\"><summary>应用、工具、环境与文件指纹</summary><section id=\"traceability\">");
        StringAssert.Contains(html, "<details class=\"report-detail\"><summary>逐页重试历史与旧报告</summary><section id=\"history\">");
        StringAssert.Contains(html, "<details class=\"report-detail\"><summary>暂停、续跑、还原与详细耗时</summary><section id=\"segments\">");
        var pages = Section(html, "pages");
        StringAssert.Contains(pages, "打开单页报告与截图");
        StringAssert.Contains(pages, "<details><summary>页面详情与历史");
        Assert.IsTrue(html.IndexOf("id=\"pages\"", StringComparison.Ordinal) < html.IndexOf("id=\"coverage\"", StringComparison.Ordinal));
        StringAssert.Contains(summary, "1 页为部分采集");
        Count(html, "captured-pages", 1);
        Count(html, "failure-occurrences", 2);
    }

    [TestMethod]
    public void SharedNavigationOccurrencesCollapseButUnnamedContentAndOriginalResultIndexesRemainSeparate()
    {
        var first = CapturedPage("First", NavigationReport());
        var secondReport = NavigationReport() with
        {
            Snapshot = AuditTestFixture.Edit(NavigationReport().Snapshot!.Value, json => json["nodes"]![1]!["id"] = "n77"),
            Results = NavigationReport().Results.Select(result => result.ElementId == "n1" ? result with { ElementId = "n77" } : result).ToArray()
        };
        var second = CapturedPage("Second", secondReport);
        var state = State(first, second);

        var html = Render(state);

        Count(html, "captured-pages", 2);
        Count(html, "completed-captures", 0);
        Count(html, "partial-captures", 2);
        Count(html, "failure-occurrences", 4);
        Count(html, "failure-entries", 3);
        Count(html, "shared-navigation-entries", 1);
        Count(html, "shared-navigation-occurrences", 2);
        var separate = Section(html, "failures");
        StringAssert.Contains(separate, "无已公开名称");
        StringAssert.Contains(separate, "./pages/First/attempt-1/report.html#result-3");
        StringAssert.Contains(separate, "./pages/Second/attempt-1/report.html#result-3");
        Assert.IsFalse(separate.Contains("report.html#result-2", StringComparison.Ordinal));
        var shared = Section(html, "shared-navigation");
        StringAssert.Contains(shared, "<details><summary>展开 2 页的原始出现记录</summary>");
        StringAssert.Contains(shared, "./pages/First/attempt-1/report.html#result-2");
        StringAssert.Contains(shared, "./pages/Second/attempt-1/report.html#result-2");
        StringAssert.Contains(shared, "SharedNav");
        StringAssert.Contains(shared, "SharedRule");
        StringAssert.Contains(shared, "Axe.Windows");
        StringAssert.Contains(shared, "ListItem");
        StringAssert.Contains(html, "附加现有应用 · 只导航和审计");
        StringAssert.Contains(html, "不是经认证的独立根因数");
        StringAssert.Contains(html, "./pages/Second/attempt-1/scan.json");
        Assert.IsFalse(html.Contains("<img", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("data:image", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("<script", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("https://", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("DO-NOT-RENDER", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RootIdentityLearnedByTheFirstScanDoesNotInvalidateTheOriginalWindowBinding()
    {
        var first = CapturedPage("First", NavigationReport());
        var second = CapturedPage("Second", NavigationReport());
        var state = State(first, second) with { Target = fixture.Report.Target with { RootRuntimeId = null } };

        var html = Render(state);

        Count(html, "captured-pages", 2);
        Count(html, "failure-occurrences", 4);
        Count(html, "shared-navigation-entries", 1);
        StringAssert.Contains(html, "./pages/First/attempt-1/report.html#result-2");
    }

    [TestMethod]
    public void SharedNavigationIsNeverMergedAcrossDifferentObservedRoots()
    {
        var report = NavigationReport();
        var differentRoot = report with
        {
            Target = report.Target with { RootRuntimeId = "other-root" },
            Snapshot = AuditTestFixture.Edit(report.Snapshot!.Value, json =>
            {
                json["rootRuntimeId"] = "other-root";
                json["nodes"]![0]!["runtimeId"] = "other-root";
            })
        };
        var state = State(CapturedPage("First", report), CapturedPage("Second", differentRoot)) with
        {
            Target = fixture.Report.Target with { RootRuntimeId = null }
        };

        var html = Render(state);

        Count(html, "captured-pages", 2);
        Count(html, "failure-occurrences", 4);
        Count(html, "shared-navigation-entries", 0);
        Count(html, "failure-entries", 4);
    }

    [TestMethod]
    [DataRow("different-runtime")]
    [DataRow("different-type")]
    [DataRow("different-rule")]
    [DataRow("different-source")]
    [DataRow("duplicate-runtime")]
    [DataRow("duplicate-automation-id")]
    [DataRow("native-runtime-mismatch")]
    [DataRow("result-runtime-mismatch")]
    [DataRow("duplicate-native-association")]
    [DataRow("unknown-navigation-id")]
    [DataRow("snapshot-artifact-mismatch")]
    public void UncertainOrUnlistedNavigationAssociationsPreservePerPageOccurrences(string condition)
    {
        var report = NavigationReport();
        if (condition is "different-runtime" or "different-type" or "duplicate-runtime" or "duplicate-automation-id")
        {
            report = report with
            {
                Snapshot = AuditTestFixture.Edit(report.Snapshot!.Value, json =>
                {
                    if (condition == "different-runtime")
                        json["nodes"]![1]!["runtimeId"] = "replacement-runtime";
                    if (condition == "different-type")
                        json["nodes"]![1]!["controlType"] = "Button";
                    if (condition is "duplicate-runtime" or "duplicate-automation-id")
                    {
                        var duplicate = json["nodes"]![1]!.DeepClone();
                        duplicate["id"] = "n3";
                        if (condition == "duplicate-runtime")
                            duplicate["automationId"] = "OtherId";
                        else
                            duplicate["runtimeId"] = "other-runtime";
                        json["nodes"]!.AsArray().Add(duplicate);
                    }
                })
            };
            if (condition == "different-runtime")
            {
                report = report with
                {
                    Axe = report.Axe! with { Findings = report.Axe.Findings.Select(finding => finding.RuleId == "SharedRule"
                        ? finding with { ElementProperties = new Dictionary<string, string> { ["RuntimeId"] = "replacement-runtime", ["AutomationId"] = "SharedNav" } } : finding).ToArray() },
                    Results = report.Results.Select(result => result.SourceRuleId == "SharedRule"
                        ? result with { Details = new Dictionary<string, string> { ["runtimeId"] = "replacement-runtime" } } : result).ToArray()
                };
            }
        }
        if (condition == "native-runtime-mismatch")
            report = report with { Axe = report.Axe! with { Findings = report.Axe.Findings.Select(finding => finding.RuleId == "SharedRule"
                ? finding with { ElementProperties = new Dictionary<string, string> { ["RuntimeId"] = "wrong-runtime" } } : finding).ToArray() } };
        if (condition == "result-runtime-mismatch")
            report = report with { Results = report.Results.Select(result => result.SourceRuleId == "SharedRule"
                ? result with { Details = new Dictionary<string, string> { ["runtimeId"] = "wrong-runtime" } } : result).ToArray() };
        if (condition == "different-rule")
            report = report with
            {
                Results = report.Results.Select(result => result.SourceRuleId == "SharedRule"
                    ? result with { SourceRuleId = "OtherRule" } : result).ToArray(),
                Axe = report.Axe! with { Findings = report.Axe.Findings.Select(finding => finding.RuleId == "SharedRule"
                    ? finding with { RuleId = "OtherRule" } : finding).ToArray() }
            };
        if (condition == "different-source")
            report = report with { Catalog = report.Catalog.Select(check => check.Id == "axe.scan"
                ? check with { Source = "Other recorded source" } : check).ToArray() };
        if (condition == "duplicate-native-association")
            report = report with { Axe = report.Axe! with { Findings = [.. report.Axe.Findings, report.Axe.Findings[0]], ResultGroupErrorCounts = [3] } };
        var first = CapturedPage("First", NavigationReport());
        var second = CapturedPage("Second", report, matchingSnapshotArtifact: condition != "snapshot-artifact-mismatch");
        var state = State(first, second);
        if (condition == "unknown-navigation-id")
            state = state with { Inventory = state.Inventory! with { NavigationIds = [] } };

        var html = Render(state);

        Count(html, "captured-pages", 2);
        Count(html, "failure-occurrences", 4);
        Count(html, "failure-entries", 4);
        Count(html, "shared-navigation-entries", 0);
        StringAssert.Contains(Section(html, "failures"), "./pages/First/attempt-1/report.html#result-2");
        StringAssert.Contains(Section(html, "failures"), "./pages/Second/attempt-1/report.html#result-2");
    }

    [TestMethod]
    public void DuplicateOccurrencesWithinOnePageAreNotSilentlyCollapsedAcrossPages()
    {
        var report = NavigationReport();
        var first = CapturedPage("First", report with { Results = [.. report.Results, report.Results[2]] });
        var second = CapturedPage("Second", report);

        var html = Render(State(first, second));

        Count(html, "failure-occurrences", 5);
        Count(html, "failure-entries", 5);
        Count(html, "shared-navigation-entries", 0);
        StringAssert.Contains(html, "./pages/First/attempt-1/report.html#result-4");
    }

    [TestMethod]
    public void ActualVerifiedScanDataOverridesCachedCountsAndSeparatesPartialCoverageFromDefects()
    {
        var page = CapturedPage("First", fixture.Report);
        page.Attempts[0].RuleFailures = 912345;
        page.Attempts[0].Nodes = 823456;
        page.Attempts[0].CaptureStatus = "completed";

        var html = Render(State(page));

        Count(html, "captured-pages", 1);
        Count(html, "failure-occurrences", 1);
        Count(html, "partial-captures", 1);
        Count(html, "completed-captures", 0);
        StringAssert.Contains(Section(html, "pages"), WebUtility.HtmlEncode("partial · 1 次规则失败 · 2 节点"));
        Assert.IsFalse(html.Contains("912345", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("823456", StringComparison.Ordinal));
        StringAssert.Contains(html, "partial 表示采集或覆盖存在限制");
    }

    [TestMethod]
    public void MissingSnapshotNodesRemainUnknownRatherThanZeroNodes()
    {
        var report = fixture.Report with
        {
            Snapshot = null,
            Results = fixture.Report.Results.Select(result => result with { ElementId = null }).ToArray()
        };

        var html = Render(State(CapturedPage("First", report)));

        Count(html, "partial-captures", 1);
        StringAssert.Contains(Section(html, "pages"), "未提供节点数据");
        Assert.IsFalse(Section(html, "pages").Contains("0 节点", StringComparison.Ordinal));
        Count(html, "shared-navigation-entries", 0);
    }

    [TestMethod]
    public void GenuineCandidatesAreSeparateFromHealthyObservationsAndCoverageGaps()
    {
        var report = fixture.Report with
        {
            Axe = fixture.Report.Axe! with { Findings = [], ResultGroupErrorCounts = [0] },
            Results =
            [
                new() { CheckId = "capture.scope", Status = "observed", Summary = "Identity verified." },
                new() { CheckId = "text.contrast", Status = "observed", Summary = "Healthy numeric contrast.", Details = new Dictionary<string, string> { ["comparison"] = "meets-threshold" } },
                new() { CheckId = "text.contrast", Status = "observed", Summary = "Potential low contrast.", Details = new Dictionary<string, string> { ["comparison"] = "below-threshold" } },
                new() { CheckId = "text.contrast", Status = "inconclusive", Summary = "Withheld metadata; no assumed values." },
                new() { CheckId = "visual.estimates", Status = "unsupported", Summary = "Not implemented." },
                new() { CheckId = "axe.scan", Status = "no-violations-reported", Summary = "This scan reported no violations." }
            ]
        };

        var html = Render(State(CapturedPage("First", report)));

        Count(html, "failure-occurrences", 0);
        Count(html, "partial-captures", 1);
        StringAssert.Contains(Section(html, "failures"), "未报告违规；仅限这些页面与已运行检查");
        var candidates = Section(html, "candidates");
        StringAssert.Contains(candidates, "1 次候选观察");
        StringAssert.Contains(candidates, "Potential low contrast.");
        StringAssert.Contains(candidates, "report.html#result-2");
        Assert.IsFalse(candidates.Contains("Healthy numeric contrast.", StringComparison.Ordinal));
        Assert.IsFalse(candidates.Contains("Identity verified.", StringComparison.Ordinal));
        Assert.IsFalse(candidates.Contains("Withheld metadata", StringComparison.Ordinal));
        StringAssert.Contains(Section(html, "coverage"), "Withheld metadata; no assumed values.");
        StringAssert.Contains(Section(html, "coverage"), "report.html#result-3");
        Assert.IsFalse(html.Contains("class=\"pass\"", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("application passed", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    [DataRow("scan-hash")]
    [DataRow("artifact-hash")]
    [DataRow("malformed-scan")]
    [DataRow("missing-scan")]
    [DataRow("missing-report")]
    public void MissingOrChangedArtifactsNeverSupplyCurrentCountsOrUnverifiedReportLinks(string condition)
    {
        var page = CapturedPage("First", fixture.Report);
        var attempt = page.Attempts[0];
        var scanPath = Path.Combine(fixture.Root, attempt.Directory, "scan.json");
        if (condition == "scan-hash")
            File.AppendAllText(scanPath, " ");
        if (condition == "artifact-hash")
            File.AppendAllText(Path.Combine(fixture.Root, attempt.Directory, AuditTestFixture.SnapshotPath), " ");
        if (condition == "malformed-scan")
        {
            var json = JsonNode.Parse(File.ReadAllText(scanPath))!;
            json["results"] = null;
            File.WriteAllText(scanPath, json.ToJsonString(Json.Options));
            attempt.ScanSha256 = AuditFiles.Hash(scanPath);
        }
        if (condition == "missing-scan")
            File.Delete(scanPath);
        if (condition == "missing-report")
            File.Delete(Path.Combine(fixture.Root, attempt.Directory, "report.html"));

        var html = Render(State(page));

        Count(html, "captured-pages", 0);
        Count(html, "failure-occurrences", 0);
        Count(html, "unverified-pages", 1);
        Assert.IsFalse(html.Contains("href=\"./pages/First/attempt-1/report.html", StringComparison.Ordinal));
        Assert.AreEqual(condition == "missing-report", html.Contains("href=\"./pages/First/attempt-1/scan.json\"", StringComparison.Ordinal));
        StringAssert.Contains(html, "当前无有效的已采集扫描；不记作 0 次违规");
        Assert.IsFalse(html.Contains("class=\"pass\"", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("process")]
    [DataRow("window")]
    [DataRow("process-start")]
    [DataRow("root")]
    [DataRow("missing-root")]
    [DataRow("tool-version")]
    public void ScansFromChangedInstancesOrToolsAreNotCounted(string condition)
    {
        var report = fixture.Report;
        var target = condition switch
        {
            "process" => report.Target with { ProcessId = 99 },
            "window" => report.Target with { WindowHandle = 99 },
            "process-start" => report.Target with { ProcessStartedAtUtc = report.Target.ProcessStartedAtUtc.AddMinutes(1) },
            "root" => report.Target with { RootRuntimeId = "changed-root" },
            "missing-root" => report.Target with { RootRuntimeId = null },
            _ => report.Target
        };
        report = report with
        {
            Target = target,
            Axe = report.Axe! with { ProcessId = target.ProcessId, WindowHandle = target.WindowHandle },
            WinappVersion = condition == "tool-version" ? "different-version" : report.WinappVersion
        };

        var html = Render(State(CapturedPage("First", report)));

        Count(html, "captured-pages", 0);
        Count(html, "failure-occurrences", 0);
        StringAssert.Contains(html, condition == "tool-version" ? "工具版本不一致" : "窗口身份与本次巡检不一致");
        Assert.IsFalse(html.Contains("href=\"./pages/First/attempt-1/report.html", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BlockedLatestRetryKeepsOlderReportInHistoryWithoutCountingItsOldSuccess()
    {
        var page = CapturedPage("First", NavigationReport());
        page.Status = "blocked";
        page.Attempts.Add(new()
        {
            Number = 2, Directory = @"pages\First\attempt-2", StartedAtUtc = AuditTestFixture.Time.AddMinutes(2),
            FinishedAtUtc = AuditTestFixture.Time.AddMinutes(3), Status = "blocked",
            Error = "Window minimized; capture was not attempted."
        });
        var state = State(page) with { Status = "paused", Error = "Window minimized; navigation paused." };

        var html = Render(state);

        Count(html, "captured-pages", 0);
        Count(html, "blocked-pages", 1);
        Count(html, "failure-occurrences", 0);
        Assert.IsFalse(Section(html, "pages").Contains("href=\"./pages/First/attempt-1/report.html", StringComparison.Ordinal));
        StringAssert.Contains(Section(html, "history"), "href=\"./pages/First/attempt-1/report.html\"");
        StringAssert.Contains(Section(html, "history"), "href=\"./pages/First/attempt-1/scan.json\"");
        StringAssert.Contains(Section(html, "history"), "尝试 1（历史，不计入当前汇总）");
        StringAssert.Contains(Section(html, "history"), "尝试 2（最新）");
        StringAssert.Contains(Section(html, "progress"), "Window minimized; navigation paused.");
    }

    [TestMethod]
    public void ResumeSegmentsDistinguishOperatorDeclarationFromAutomaticBindingsAndShowRestorationAndTiming()
    {
        var state = State() with
        {
            Status = "paused",
            Error = "Changed process identity; resume rejected.",
            Invocations =
            [
                new()
                {
                    Number = 1, StartedAtUtc = AuditTestFixture.Time, FinishedAtUtc = AuditTestFixture.Time.AddSeconds(5),
                    Resuming = false, Status = "paused", Reason = "Requested stop-after reached.",
                    Restoration = new("restored", "Original navigation restored."),
                    Performance = new(3, 200, 4000)
                },
                new()
                {
                    Number = 2, StartedAtUtc = AuditTestFixture.Time.AddMinutes(1), FinishedAtUtc = AuditTestFixture.Time.AddMinutes(1).AddSeconds(2),
                    Resuming = true, OperatorConfirmedUnchangedState = true, RetryPages = ["RetryNav"],
                    Status = "blocked", Reason = "Changed process identity; resume rejected.",
                    Restoration = new("blocked", "Changed process; restoration was not attempted.")
                }
            ]
        };

        var html = Render(state);

        var segments = Section(html, "segments");
        StringAssert.Contains(segments, "执行段 1 · 首次执行");
        StringAssert.Contains(segments, "执行段 2 · 恢复");
        StringAssert.Contains(segments, "未声明（不能据此推断设置未变）");
        StringAssert.Contains(segments, "已声明（操作者确认，不是自动核验）");
        StringAssert.Contains(segments, "RetryNav");
        StringAssert.Contains(segments, WebUtility.HtmlEncode("3 次工具调用 · 工具调用 0.2 秒 · 审计 4 秒"));
        StringAssert.Contains(segments, "用时重叠，不能相加");
        StringAssert.Contains(Section(html, "progress"), "7 秒");
        StringAssert.Contains(Section(html, "progress"), "Changed process identity; resume rejected.");
        StringAssert.Contains(Section(html, "progress"), "Changed process; restoration was not attempted.");
        StringAssert.Contains(Section(html, "traceability"), "不能代替“应用设置未变”的操作者声明");
        StringAssert.Contains(html, "不是性能基准或速度承诺");
        StringAssert.Contains(html, "不是数字签名");
    }

    [TestMethod]
    [DataRow("before")]
    [DataRow("after")]
    [DataRow("heading")]
    [DataRow("finished")]
    [DataRow("hash")]
    [DataRow("page-status")]
    [DataRow("attempt-status")]
    [DataRow("duplicate-attempt")]
    public void IncompleteOrUnknownLatestAttemptIsNotPromotedToCaptured(string condition)
    {
        var page = CapturedPage("First", fixture.Report);
        var attempt = page.Attempts[0];
        if (condition == "before") attempt.Before = null;
        if (condition == "after") attempt.After = attempt.After! with { Selected = false };
        if (condition == "heading") attempt.After = attempt.After! with { HeadingMatched = false };
        if (condition == "finished") attempt.FinishedAtUtc = null;
        if (condition == "hash") attempt.ScanSha256 = null;
        if (condition == "page-status") page.Status = "future-page-state";
        if (condition == "attempt-status") attempt.Status = "future-attempt-state";
        if (condition == "duplicate-attempt") page.Attempts.Add(attempt with { });

        var html = Render(State(page));

        Count(html, "captured-pages", 0);
        Count(html, "failure-occurrences", 0);
        Count(html, "unverified-pages", 1);
        Assert.IsFalse(html.Contains("class=\"pass\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void UndiscoveredOrIncompleteInventoryCannotAppearFinished()
    {
        var missingInventory = State() with { Inventory = null, InventorySha256 = null, Status = "future-run-state", Invocations = [] };
        var html = Render(missingInventory);

        Count(html, "captured-pages", 0);
        StringAssert.Contains(html, " / 未知 页");
        StringAssert.Contains(html, WebUtility.HtmlEncode("未知执行状态 · future-run-state"));
        StringAssert.Contains(html, "0 / 0 都不是完成或通过");
        StringAssert.Contains(html, "未记录执行段");

        var pending = new SurveyPageState { Page = new("Pending", "Pending page", []), Status = "pending" };
        var state = State(pending) with { Pages = [] };
        var incomplete = Render(state, "incomplete.html");
        Count(incomplete, "pending-pages", 1);
        StringAssert.Contains(incomplete, "清单中有 1 页缺少状态记录");
        Count(incomplete, "captured-pages", 0);
    }

    [TestMethod]
    [DataRow(@"..\outside")]
    [DataRow(@"C:\outside")]
    [DataRow(@"pages\bad:stream")]
    [DataRow(@"pages\..\outside")]
    [DataRow(@"pages\CON")]
    [DataRow(@"..\outside<script>")]
    [DataRow("https://example.invalid/report")]
    public void UnsafeAttemptPathsAreEscapedTextNeverClickable(string directory)
    {
        var page = new SurveyPageState
        {
            Page = new("Unsafe", "Unsafe page", []), Status = "captured",
            Attempts = [new()
            {
                Number = 1, Directory = directory, StartedAtUtc = AuditTestFixture.Time,
                Status = "captured", ScanSha256 = new string('A', 64)
            }]
        };

        var html = Render(State(page));

        Count(html, "captured-pages", 0);
        StringAssert.Contains(html, WebUtility.HtmlEncode(directory));
        Assert.IsFalse(html.Contains("href=\"./pages/", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("href=\"./..", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("href=\"https:", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("<script", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void LocalLinksEncodeEachPathSegmentAndRenderingNeverChangesOriginalEvidence()
    {
        var attempt = WriteAttempt(@"pages\page #&%\attempt-1", fixture.Report);
        var page = new SurveyPageState { Page = new("Safe", "Safe page", []), Status = "captured", Attempts = [attempt] };
        var scanPath = Path.Combine(fixture.Root, attempt.Directory, "scan.json");
        var reportPath = Path.Combine(fixture.Root, attempt.Directory, "report.html");
        var scanBytes = File.ReadAllBytes(scanPath);
        var reportBytes = File.ReadAllBytes(reportPath);

        var html = Render(State(page));

        StringAssert.Contains(html, "href=\"./pages/page%20%23%26%25/attempt-1/report.html#result-0\"");
        StringAssert.Contains(html, "href=\"./pages/page%20%23%26%25/attempt-1/scan.json\"");
        CollectionAssert.AreEqual(scanBytes, File.ReadAllBytes(scanPath));
        CollectionAssert.AreEqual(reportBytes, File.ReadAllBytes(reportPath));
    }

    [TestMethod]
    public void AppProfilePageRulePathAndResumeStringsAreEscaped()
    {
        static string U(string field) => $"<script>{field} & \"quoted\"</script>";
        var report = fixture.Report with
        {
            Target = fixture.Report.Target with { ProcessName = U("process"), Title = U("window"), RootRuntimeId = U("root") },
            WinappVersion = U("tool-version"),
            Catalog = fixture.Report.Catalog.Select(check => check.Id == "axe.scan" ? check with { Source = U("source") } : check).ToArray(),
            Axe = fixture.Report.Axe! with { Findings = [fixture.Report.Axe.Findings[0] with { RuleId = U("rule") }] },
            Results = [fixture.Report.Results[0] with { SourceRuleId = U("rule"), ElementName = U("element"), Summary = U("summary") }]
        };
        var attempt = WriteAttempt(@"pages\escaped\attempt-1", report);
        attempt.Error = U("attempt-error");
        attempt.After = attempt.After! with { Heading = U("heading") };
        var page = new SurveyPageState { Page = new(U("page-id"), U("page-name"), [U("group")]), Status = "captured", Attempts = [attempt] };
        var state = State(page) with
        {
            RunId = U("run"), Name = U("profile"), Target = report.Target, Error = U("pause"),
            ProfileSha256 = U("profile-hash"), InventorySha256 = U("inventory-hash"),
            Tools = new(report.WinappVersion, U("native-hash"), U("runner-hash")),
            Context = new(new(U("app"), U("path"), U("app-hash"), U("version"), U("companion")),
                new(800, 600, 96, U("os"), U("culture"), false, null)),
            OriginalNavigation = new(U("original"), new Dictionary<string, bool> { [U("original-group")] = true }),
            Invocations = [new()
            {
                Number = 1, StartedAtUtc = AuditTestFixture.Time, Resuming = true,
                OperatorConfirmedUnchangedState = true, Status = U("segment-status"), Reason = U("segment-reason"),
                RetryPages = [U("retry")], Restoration = new(U("restore-status"), U("restore-message"))
            }]
        };
        state = state with { Inventory = state.Inventory! with { Excluded = [new(U("excluded-id"), U("excluded-name"), U("excluded-reason"))] } };

        var html = Render(state);

        foreach (var field in new[]
        {
            "process", "window", "root", "tool-version", "source", "rule", "element", "summary", "attempt-error", "heading",
            "page-id", "page-name", "group", "run", "profile", "pause", "profile-hash", "inventory-hash", "native-hash", "runner-hash",
            "app", "path", "app-hash", "version", "companion", "os", "culture", "original", "original-group",
            "segment-status", "segment-reason", "retry", "restore-status", "restore-message", "excluded-id", "excluded-name", "excluded-reason"
        })
            StringAssert.Contains(html, WebUtility.HtmlEncode(U(field)), field);
        Assert.IsFalse(html.Contains("<script", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("<button", StringComparison.OrdinalIgnoreCase));
        Count(html, "failure-occurrences", 1);
    }

    [TestMethod]
    public void SurveyRendererUsesCreateNewAndDoesNotMutateState()
    {
        var state = State(CapturedPage("First", fixture.Report));
        var originalState = JsonSerializer.Serialize(state, Json.Options);
        var html = Render(state);

        Assert.ThrowsExactly<IOException>(() => SurveyReportRenderer.Write(Path.Combine(fixture.Root, "survey-report.html"), state));

        Assert.AreEqual(html, File.ReadAllText(Path.Combine(fixture.Root, "survey-report.html")));
        Assert.AreEqual(originalState, JsonSerializer.Serialize(state, Json.Options));
    }

    private SurveyState State(params SurveyPageState[] pages)
    {
        var original = new SurveyNavigationState("First", new Dictionary<string, bool> { ["ToolsGroup"] = false });
        return new()
        {
            RunId = "survey-fixture", Name = "Existing app", StartedAtUtc = AuditTestFixture.Time,
            Target = fixture.Report.Target, ProfileSha256 = new string('A', 64),
            Tools = new(fixture.Report.WinappVersion, new string('B', 64), new string('C', 64)),
            Context = new(new("Demo", @"C:\Apps\Demo.exe", new string('D', 64), "1.0"),
                new(800, 600, 96, "Windows fixture", "zh-CN", false, true)),
            Status = "finished", Revision = 1, OriginalNavigation = original,
            Inventory = new(pages.Select(page => page.Page).ToArray(), [], ["SharedNav"], original),
            InventorySha256 = new string('E', 64), Pages = pages.ToList(),
            Invocations = [new()
            {
                Number = 1, StartedAtUtc = AuditTestFixture.Time, FinishedAtUtc = AuditTestFixture.Time.AddSeconds(5),
                Resuming = false, Status = "finished", Restoration = new("restored", "Original navigation restored.")
            }]
        };
    }

    private SurveyPageState CapturedPage(string id, AuditReport report, bool matchingSnapshotArtifact = true) => new()
    {
        Page = new(id, id + " page", ["ToolsGroup"]), Status = "captured",
        Attempts = [WriteAttempt(Path.Combine("pages", id, "attempt-1"), report, matchingSnapshotArtifact)]
    };

    private SurveyAttempt WriteAttempt(string directory, AuditReport report, bool matchingSnapshotArtifact = true)
    {
        var destination = Path.Combine(fixture.Root, directory);
        Directory.CreateDirectory(Path.Combine(destination, "evidence"));
        foreach (var artifact in fixture.Report.Artifacts)
        {
            var path = Path.Combine(destination, artifact.Path);
            if (artifact.Path == AuditTestFixture.SnapshotPath && matchingSnapshotArtifact)
                AuditFiles.WriteNew(path, report.Snapshot);
            else if (artifact.Path == AuditTestFixture.AxePath)
                AuditFiles.WriteNew(path, report.Axe);
            else if (artifact.Path == AuditTestFixture.ObservationPath)
                AuditFiles.WriteNew(path, report.Observation);
            else
                File.Copy(Path.Combine(fixture.Source, artifact.Path), path);
        }
        report = report with
        {
            Artifacts = fixture.Report.Artifacts.Select(artifact =>
                AuditFiles.Describe(destination, artifact.Path, artifact.MediaType, artifact.Source)).ToArray()
        };
        var scanPath = Path.Combine(destination, "scan.json");
        AuditFiles.WriteNew(scanPath, report);
        AuditReportRenderer.Write(Path.Combine(destination, "report.html"), report);
        return new()
        {
            Number = 1, Directory = directory, StartedAtUtc = report.StartedAtUtc,
            FinishedAtUtc = report.FinishedAtUtc, Status = "captured", CaptureStatus = report.Status,
            Before = new(true, true, "Verified heading", report.StartedAtUtc),
            After = new(true, true, "Verified heading", report.FinishedAtUtc),
            ScanSha256 = AuditFiles.Hash(scanPath), RuleFailures = report.RuleFailureCount, Nodes = 999,
            NavigationMilliseconds = 250, ScanMilliseconds = 3000
        };
    }

    private AuditReport NavigationReport()
    {
        var snapshot = AuditTestFixture.Edit(fixture.Report.Snapshot!.Value, json =>
        {
            var navigation = json["nodes"]![1]!;
            navigation["automationId"] = "SharedNav";
            navigation["controlType"] = "ListItem";
            navigation["name"] = null;
            var content = navigation.DeepClone();
            content["id"] = "n2";
            content["automationId"] = "";
            content["controlType"] = "Button";
            content["runtimeId"] = "content-runtime";
            json["nodes"]!.AsArray().Add(content);
        });
        var findings = new AccessibilityFinding[]
        {
            new("SharedRule", "Shared navigation lacks set information.", "Supply set information.", "ObjectInformation",
                new Dictionary<string, string> { ["RuntimeId"] = "runtime-button", ["AutomationId"] = "SharedNav", ["ControlType"] = "ListItem(50007)" }, []),
            new("NameNotNull", "A content button has no name.", "Supply a name.", "ObjectInformation",
                new Dictionary<string, string> { ["RuntimeId"] = "content-runtime", ["AutomationId"] = "", ["ControlType"] = "Button(50000)" }, [])
        };
        return fixture.Report with
        {
            Snapshot = snapshot,
            Axe = fixture.Report.Axe! with { Findings = findings, ResultGroupErrorCounts = [2] },
            Results =
            [
                new() { CheckId = "capture.scope", Status = "observed", Summary = "Exact target verified.", Evidence = [AuditTestFixture.SnapshotPath] },
                new() { CheckId = "text.contrast", Status = "inconclusive", Summary = "Provider metadata withheld.", Evidence = [AuditTestFixture.SnapshotPath] },
                new()
                {
                    CheckId = "axe.scan", SourceRuleId = "SharedRule", Status = "rule-failure", Summary = findings[0].Description,
                    ElementId = "n1", Details = new Dictionary<string, string> { ["runtimeId"] = "runtime-button" },
                    Evidence = [AuditTestFixture.AxePath, AuditTestFixture.SnapshotPath]
                },
                new()
                {
                    CheckId = "axe.scan", SourceRuleId = "NameNotNull", Status = "rule-failure", Summary = findings[1].Description,
                    ElementId = "n2", Evidence = [AuditTestFixture.AxePath, AuditTestFixture.SnapshotPath]
                }
            ]
        };
    }

    private string Render(SurveyState state, string name = "survey-report.html")
    {
        var path = Path.Combine(fixture.Root, name);
        SurveyReportRenderer.Write(path, state);
        return File.ReadAllText(path);
    }

    private static void Count(string html, string id, int count) =>
        StringAssert.Contains(html, $"<strong id=\"{id}\">{count}</strong>");

    private static string Section(string html, string id)
    {
        var start = html.IndexOf($"<section id=\"{id}\">", StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, $"Missing section {id}.");
        var end = html.IndexOf("</section>", start, StringComparison.Ordinal) + "</section>".Length;
        return html[start..end];
    }
}

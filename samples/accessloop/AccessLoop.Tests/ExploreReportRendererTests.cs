using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class ExploreReportRendererTests
{
    private AuditTestFixture fixture = null!;

    [TestInitialize]
    public void Initialize() => fixture = new();

    [TestCleanup]
    public void Cleanup() => fixture.Dispose();

    [TestMethod]
    public void SummaryDistinguishesThreePagesFromManyStatesAndKeepsTechnicalRecordsFolded()
    {
        var report = Report(
            Captured("001"), Captured("002", "scroll"), Captured("003", "dropdown"),
            Captured("004", "dialog"), Captured("005", pageId: "list"), Captured("006", pageId: "settings"));
        report.FinishedAtUtc = report.StartedAtUtc.AddMilliseconds(938104);
        report.States[0].RuleFailures = 912345;
        report.States[0].CaptureStatus = "completed";
        report.Performance = new(14, 25100, 75105);

        var html = Render(report);
        var summary = Section(html, "progress");

        Count(html, "reached-pages", 3);
        Count(html, "verified-states", 6);
        Count(html, "failure-occurrences", 6);
        StringAssert.Contains(summary, " / 3 页");
        StringAssert.Contains(summary, " / 6 个视图状态");
        StringAssert.Contains(summary, "15 分 38 秒");
        StringAssert.Contains(summary, "6 个状态为部分采集");
        StringAssert.Contains(summary, "WCAG");
        StringAssert.Contains(summary, "未执行源码修复");
        StringAssert.Contains(summary, "未主动变更下拉选项");
        StringAssert.Contains(summary, "不是一回事");
        StringAssert.Contains(summary, "已达到或超过预算");
        foreach (var detail in new[] { report.RunId, report.ProfileSha256, "PID", "HWND", "原生 UI", "912345" })
            Assert.IsFalse(summary.Contains(detail, StringComparison.Ordinal), detail);
        Assert.IsFalse(html.Contains("912345", StringComparison.Ordinal));
        Assert.IsTrue(html.IndexOf("id=\"pages\"", StringComparison.Ordinal) < html.IndexOf("id=\"details\"", StringComparison.Ordinal));
        StringAssert.Contains(html, "<details class=\"report-detail\"><summary>逐状态的前后见证与证据指纹</summary>");
        StringAssert.Contains(html, "<details class=\"report-detail\"><summary>范围事件与停止原因原始记录</summary>");
        StringAssert.Contains(html, "<details class=\"report-detail\"><summary>运行绑定、配置档案与详细耗时</summary>");
        StringAssert.Contains(html, "用时重叠，不能相加");
        Assert.IsFalse(html.Contains("<details open", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void RecycledRuntimeIdsAcrossScrollsAreRawOccurrencesNotUniqueRootCauses()
    {
        var report = Report(Captured("001"), Captured("002", "scroll"), Captured("003", "scroll"));
        var html = Render(report);

        Count(html, "reached-pages", 1);
        Count(html, "verified-states", 3);
        Count(html, "failure-occurrences", 3);
        StringAssert.Contains(Section(html, "progress"), "不声称独立根因数");
        StringAssert.Contains(html, "RuntimeId 可能在滚动时复用");
        Assert.IsFalse(html.Contains("shared-navigation", StringComparison.Ordinal));
        StringAssert.Contains(Section(html, "pages"), "3 个声明页面");
    }

    [TestMethod]
    public void EveryLinkAndAnchorResolvesAndImagesStayInTheIndividualStateReports()
    {
        var state = Captured("001", directory: @"states\page #&%\001");
        var html = Render(Report(state));

        StringAssert.Contains(html, "href=\"./states/page%20%23%26%25/001/report.html\"");
        StringAssert.Contains(html, "href=\"./states/page%20%23%26%25/001/scan.json\"");
        AssertLinksResolve(html);
        var individual = File.ReadAllText(Path.Combine(fixture.Root, state.Directory, "report.html"));
        StringAssert.Contains(individual, "id=\"result-0\"");
        StringAssert.Contains(individual, "data:image/png;base64,");
        StringAssert.Contains(individual, "Accessible name is missing.");
        Assert.IsFalse(html.Contains("<img", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("data:image", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("<script", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("<iframe", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("<form", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("https://", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("http://", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(html, "default-src 'none'");
        StringAssert.Contains(html, "没有独立绑定的哈希或签名");
        StringAssert.Contains(html, "不会重新校验外部文件");
        Assert.IsFalse(html.Contains("DO-NOT-RENDER", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("scan-hash")]
    [DataRow("artifact-hash")]
    [DataRow("artifact-length")]
    [DataRow("missing-artifact")]
    [DataRow("missing-scan")]
    [DataRow("missing-report")]
    [DataRow("report-directory")]
    [DataRow("invalid-json")]
    [DataRow("unknown-scan-status")]
    [DataRow("unsafe-artifact")]
    [DataRow("blocked-scan")]
    public void MissingChangedOrInvalidEvidenceCannotSupplyAnyTrustedCountsOrFileLinks(string condition)
    {
        var state = Captured("001");
        var directory = Path.Combine(fixture.Root, state.Directory);
        var scanPath = Path.Combine(directory, "scan.json");
        if (condition == "scan-hash")
            File.AppendAllText(scanPath, " ");
        if (condition == "artifact-hash")
        {
            var path = Path.Combine(directory, AuditTestFixture.ScreenshotPath);
            var bytes = File.ReadAllBytes(path);
            bytes[^1] ^= 1;
            File.WriteAllBytes(path, bytes);
        }
        if (condition == "artifact-length")
            File.AppendAllText(Path.Combine(directory, AuditTestFixture.SnapshotPath), " ");
        if (condition == "missing-artifact")
            File.Delete(Path.Combine(directory, AuditTestFixture.SnapshotPath));
        if (condition == "missing-scan")
            File.Delete(scanPath);
        if (condition is "missing-report" or "report-directory")
        {
            File.Delete(Path.Combine(directory, "report.html"));
            if (condition == "report-directory")
                Directory.CreateDirectory(Path.Combine(directory, "report.html"));
        }
        if (condition is "invalid-json" or "unknown-scan-status" or "unsafe-artifact" or "blocked-scan")
        {
            var json = JsonNode.Parse(File.ReadAllBytes(scanPath))!.AsObject();
            if (condition == "invalid-json") json["results"] = null;
            if (condition == "unknown-scan-status") json["status"] = "future";
            if (condition == "blocked-scan") json["status"] = "blocked";
            if (condition == "unsafe-artifact") json["artifacts"]![0]!["path"] = @"..\outside.json";
            File.WriteAllText(scanPath, json.ToJsonString(Json.Options));
            state.ScanSha256 = AuditFiles.Hash(scanPath);
        }
        state.RuleFailures = 987654;
        state.CaptureStatus = "completed";

        var html = Render(Report(state));

        Unverified(html);
        StringAssert.Contains(Section(html, "states"), "未知（不作零次）");
        StringAssert.Contains(Section(html, "states"), "没有可核验的报告链接");
        Assert.IsFalse(html.Contains("987654", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("snapshot")]
    [DataRow("axe")]
    [DataRow("both")]
    public void CapturedStateNeedsBothScopedSnapshotAndRawAxeEvidence(string missing)
    {
        var scan = fixture.Report with
        {
            Snapshot = missing is "snapshot" or "both" ? null : fixture.Report.Snapshot,
            Axe = missing is "axe" or "both" ? null : fixture.Report.Axe,
            Results = fixture.Report.Results.Select(result => result with { ElementId = null }).ToArray()
        };
        var state = Captured("001", scan: scan);
        _ = AuditFiles.ReadVerified(Path.Combine(fixture.Root, state.Directory, "scan.json"));

        var html = Render(Report(state));

        Unverified(html);
        StringAssert.Contains(html, "扫描缺少界面快照或 Axe 原始记录");
    }

    [TestMethod]
    [DataRow("scan-process")]
    [DataRow("scan-start")]
    [DataRow("scan-name")]
    [DataRow("scan-window")]
    [DataRow("scan-root")]
    [DataRow("scan-missing-root")]
    [DataRow("state-process")]
    [DataRow("state-start")]
    [DataRow("state-window")]
    [DataRow("state-root")]
    [DataRow("tool-version")]
    public void StateTargetsDoNotReplaceTheOriginalProcessStartOrWindowBinding(string condition)
    {
        var original = fixture.Report.Target;
        var target = condition switch
        {
            "scan-process" => original with { ProcessId = 99 },
            "scan-start" => original with { ProcessStartedAtUtc = original.ProcessStartedAtUtc.AddSeconds(1) },
            "scan-name" => original with { ProcessName = "Other application" },
            "scan-window" => original with { WindowHandle = 99 },
            "scan-root" => original with { RootRuntimeId = "other-root" },
            "scan-missing-root" => original with { RootRuntimeId = null },
            _ => original
        };
        var scan = Retarget(target);
        if (condition == "tool-version")
            scan = scan with { WinappVersion = "different-version" };
        var state = Captured("001", scan: scan);
        state.Target = condition switch
        {
            "state-process" => original with { ProcessId = 99 },
            "state-start" => original with { ProcessStartedAtUtc = original.ProcessStartedAtUtc.AddSeconds(1) },
            "state-window" => original with { WindowHandle = 99 },
            "state-root" => original with { RootRuntimeId = "other-root" },
            _ => state.Target
        };

        Unverified(Render(Report(state)));
    }

    [TestMethod]
    public void OptionalStateTargetAndRootDiscoveredDuringScanningPreserveOriginalWindowBinding()
    {
        var state = Captured("001");
        state.Target = null;
        var report = Report(state) with { Target = fixture.Report.Target with { RootRuntimeId = null } };

        var html = Render(report);

        Count(html, "reached-pages", 1);
        Count(html, "verified-states", 1);
        Count(html, "failure-occurrences", 1);
    }

    [TestMethod]
    [DataRow("declared")]
    [DataRow("same-window")]
    [DataRow("unknown-scope")]
    [DataRow("base")]
    [DataRow("dropdown")]
    [DataRow("wrong-dialog")]
    [DataRow("no-state-target")]
    [DataRow("other-process")]
    [DataRow("other-start")]
    public void AlternateWindowsAreNotAuthorizedByTargetsOrUnimplementedProfileScopes(string condition)
    {
        var target = fixture.Report.Target with { WindowHandle = 99, RootRuntimeId = "dialog-window-root" };
        if (condition == "other-process")
            target = target with { ProcessId = 99 };
        if (condition == "other-start")
            target = target with { ProcessStartedAtUtc = target.ProcessStartedAtUtc.AddSeconds(1) };
        var state = Captured("001", condition is "base" or "dropdown" ? condition : "dialog", scan: Retarget(target));
        if (condition == "no-state-target") state.Target = null;
        if (condition == "wrong-dialog") state.After = state.After! with { ControlId = "UndeclaredDialog" };
        var report = Report(state);
        var scope = condition == "same-window" ? "same-window" : condition == "unknown-scope" ? "future-scope" : "owned-window";
        report = report with
        {
            Profile = report.Profile with
            {
                Pages = report.Profile.Pages.Select(page => page.Id == "home"
                    ? page with { Dialogs = page.Dialogs.Select(dialog => dialog with { Scope = scope }).ToArray() } : page).ToArray()
            }
        };

        var html = Render(report);

        Unverified(html);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SeparateDropdownRemainsUnverifiedEvenWithNativePopupEvent(bool recordedNativeVerification)
    {
        var target = fixture.Report.Target with { WindowHandle = 99, RootRuntimeId = "popup-root" };
        var state = Captured("001", "dropdown", scan: Retarget(target));
        var report = Report(state);
        if (recordedNativeVerification)
            report.Events.Add(new(state.StartedAtUtc, "popup-verified",
                "Exact popup ownership and declared item markers verified by the executor.", 99, report.Target.ProcessId));

        var html = Render(report);

        Unverified(html);
        StringAssert.Contains(Section(html, "progress"), "独立弹窗仍属未核验范围");
        StringAssert.Contains(Section(html, "states"), "独立弹窗不在当前同窗口范围内");
        if (recordedNativeVerification)
            StringAssert.Contains(html, "Exact popup ownership and declared item markers verified by the executor.");
        AssertLinksResolve(html);
    }

    [TestMethod]
    [DataRow("base")]
    [DataRow("scroll")]
    [DataRow("dropdown")]
    [DataRow("dialog")]
    public void PopupEventDoesNotAuthorizeAnyKindToLeaveTheOriginalWindow(string kind)
    {
        var target = fixture.Report.Target with { WindowHandle = 99, RootRuntimeId = "popup-root" };
        var state = Captured("001", kind, scan: Retarget(target));
        var report = Report(state);
        report.Events.Add(new(state.StartedAtUtc, "popup-verified", "A dropdown popup was approved.", 99, report.Target.ProcessId));

        Unverified(Render(report));
    }

    [TestMethod]
    public void SevenSameWindowCapturesCoverThreeDeclaredPagesWithoutCrossStateDeduplication()
    {
        var basis = Captured("001");
        var middle = Captured("002", "scroll");
        var end = Captured("003", "scroll");
        end.Before = end.Before! with { ScrollPercent = 100 };
        end.After = end.After! with { ScrollPercent = 100 };
        var dropdown = Captured("004", "dropdown");
        var dialog = Captured("005", "dialog");
        var report = Report(basis, middle, end, dropdown, dialog,
            Captured("006", pageId: "list"), Captured("007", pageId: "settings"));
        report.Pages[0].ScrollStatus = "end-observed";
        report.Pages[0].Interactions.AddRange(
        [
            new()
            {
                Kind = "scroll", ControlId = "ResultsScroller", Status = "captured",
                Before = basis.Before, After = basis.After,
                Restoration = new("restored-scroll-position", "Original percentage observed again.")
            },
            new()
            {
                Kind = "dropdown", ControlId = "DisplayMode", Status = "captured",
                Before = dropdown.Before! with { ExpansionState = "Collapsed" },
                After = dropdown.After! with { ExpansionState = "Collapsed" },
                Restoration = new("restored-control", "Collapsed with original selection unchanged.")
            },
            new()
            {
                Kind = "dialog", ControlId = "InfoDialog", Status = "captured",
                Before = dialog.Before! with { DialogPresent = false },
                After = dialog.After! with { DialogPresent = false },
                Restoration = new("restored-control", "Named dialog dismissed; page observed again.")
            }
        ]);

        var html = Render(report);

        Count(html, "reached-pages", 3);
        Count(html, "verified-states", 7);
        Count(html, "failure-occurrences", 7);
        StringAssert.Contains(Section(html, "progress"), "7 个状态为部分采集");
        StringAssert.Contains(Section(html, "pages"), "末端已观察");
        Assert.IsFalse(Section(html, "pages").Contains("未声明／关联不唯一的交互", StringComparison.Ordinal));
        foreach (var state in report.States)
            StringAssert.Contains(html, $"href=\"./states/{state.Id}/report.html\"");
        AssertLinksResolve(html);
    }

    [TestMethod]
    public void SameWindowDialogCaptureAndInteractionUseRootWhileProfileKeepsOpenerSeparate()
    {
        var state = Captured("001", "dialog");
        var report = Report(state);
        report.Pages[0].Interactions.Add(new()
        {
            Kind = "dialog", ControlId = "InfoDialog", Status = "captured",
            Before = state.Before! with { ControlId = "InfoDialog", DialogPresent = false, FocusAutomationId = "PreDialogFocus" },
            After = state.After! with { ControlId = "InfoDialog", DialogPresent = false, FocusAutomationId = "HomeNav" },
            Restoration = new("restored-control", "Dialog closed; actual post-close focus observed without forcing.")
        });

        var html = Render(report);

        Count(html, "verified-states", 1);
        Count(html, "failure-occurrences", 1);
        StringAssert.Contains(Section(html, "pages"), "1 个已核验状态");
        StringAssert.Contains(html, "OpenInfo");
        StringAssert.Contains(html, "InfoDialog");
        Assert.IsFalse(Section(html, "pages").Contains("未声明／关联不唯一的交互", StringComparison.Ordinal));
        StringAssert.Contains(html, "href=\"./states/001/report.html\"");
        var interaction = report.Pages[0].Interactions.Single();
        Assert.AreEqual("InfoDialog", interaction.ControlId);
        Assert.AreEqual("InfoDialog", interaction.Before!.ControlId);
        Assert.AreEqual("InfoDialog", interaction.After!.ControlId);
        Assert.IsFalse(interaction.Before.DialogPresent);
        Assert.IsFalse(interaction.After.DialogPresent);
    }

    [TestMethod]
    public void DialogOpenerIsNotAnAliasForTheInteractionRoot()
    {
        var state = Captured("001", "dialog");
        var report = Report(state);
        report.Pages[0].Interactions.Add(new()
        {
            Kind = "dialog", ControlId = "OpenInfo", Status = "captured",
            Before = state.Before! with { DialogPresent = false },
            After = state.After! with { DialogPresent = false },
            Restoration = new("restored-control", "An opener-keyed record must remain unassociated.")
        });

        var html = Render(report);
        var pages = Section(html, "pages");

        StringAssert.Contains(pages, "<strong>对话框 1</strong>：1 个已核验状态；交互记录缺失或重复，操作与还原未确认");
        StringAssert.Contains(pages, "未声明／关联不唯一的交互，不计作覆盖");
        StringAssert.Contains(pages, "An opener-keyed record must remain unassociated.");
        Count(html, "verified-states", 1);
        Assert.AreEqual("OpenInfo", report.Pages[0].Interactions[0].ControlId);
    }

    [TestMethod]
    public void UnsupportedOwnedDialogRetainsBlockedReasonWithoutInventingACapture()
    {
        var report = Report(Captured("001"));
        report = report with
        {
            Profile = report.Profile with
            {
                Pages = report.Profile.Pages.Select(page => page.Id == "home"
                    ? page with { Dialogs = page.Dialogs.Select(dialog => dialog with { Scope = "owned-window" }).ToArray() } : page).ToArray()
            }
        };
        report.Pages[0].Interactions.Add(new()
        {
            Kind = "dialog", ControlId = "InfoDialog", Status = "blocked",
            Reason = "Separate-window dialogs are unsupported; the opener was not invoked.",
            Restoration = new("not-attempted", "No dialog was opened.")
        });

        var html = Render(report);

        Count(html, "verified-states", 1);
        StringAssert.Contains(Section(html, "pages"), "0 个已核验状态");
        StringAssert.Contains(Section(html, "pages"), "Separate-window dialogs are unsupported; the opener was not invoked.");
        StringAssert.Contains(Section(html, "pages"), "No dialog was opened.");
        Assert.AreEqual(1, report.States.Count);
    }

    [TestMethod]
    [DataRow("before-missing")]
    [DataRow("after-missing")]
    [DataRow("not-ready")]
    [DataRow("wrong-page")]
    [DataRow("wrong-window")]
    [DataRow("missing-observation-time")]
    [DataRow("reversed-observation-time")]
    [DataRow("no-finish")]
    [DataRow("reversed-time")]
    [DataRow("scan-outside-state-time")]
    [DataRow("missing-hash")]
    [DataRow("invalid-hash")]
    [DataRow("unknown-kind")]
    [DataRow("duplicate-state-id")]
    [DataRow("duplicate-directory")]
    public void IncompleteWitnessesOrAmbiguousStateRecordsNeverAppearCaptured(string condition)
    {
        var state = Captured("001");
        if (condition == "before-missing") state.Before = null;
        if (condition == "after-missing") state.After = null;
        if (condition == "not-ready") state.After = state.After! with { PageReady = false };
        if (condition == "wrong-page") state.After = state.After! with { PageId = "settings" };
        if (condition == "wrong-window") state.After = state.After! with { WindowHandle = 99 };
        if (condition == "missing-observation-time") state.Before = state.Before! with { ObservedAtUtc = default };
        if (condition == "reversed-observation-time") state.After = state.After! with { ObservedAtUtc = AuditTestFixture.Time.AddSeconds(-1) };
        if (condition == "no-finish") state.FinishedAtUtc = null;
        if (condition == "reversed-time") state.FinishedAtUtc = state.StartedAtUtc.AddSeconds(-1);
        if (condition == "scan-outside-state-time") state.FinishedAtUtc = state.StartedAtUtc;
        if (condition == "missing-hash") state.ScanSha256 = null;
        if (condition == "invalid-hash") state.ScanSha256 = "not-a-hash";
        if (condition == "unknown-kind") state = state with { Kind = "future-kind" };
        var report = Report(state);
        if (condition == "duplicate-state-id")
            report.States.Add(state with { Directory = @"states\unwritten" });
        if (condition == "duplicate-directory")
            report.States.Add(state with { Id = "002", Directory = @"STATES\001" });

        Unverified(Render(report));
    }

    [TestMethod]
    [DataRow("unknown-page")]
    [DataRow("missing-page-result")]
    [DataRow("duplicate-page-result")]
    [DataRow("duplicate-profile-page")]
    [DataRow("unknown-navigation-status")]
    [DataRow("blocked-navigation")]
    [DataRow("empty-profile")]
    [DataRow("unknown-profile-schema")]
    [DataRow("different-profile-process")]
    public void DeclaredPagesAndPageResultsMustBeUniquelyAssociated(string condition)
    {
        var state = Captured("001");
        if (condition == "unknown-page")
        {
            state = state with { PageId = "unlisted" };
            state.Before = state.Before! with { PageId = "unlisted" };
            state.After = state.After! with { PageId = "unlisted" };
        }
        var report = Report(state);
        if (condition == "unknown-page")
            report.Pages.Add(new() { PageId = "unlisted", Name = "Unknown page", NavigationStatus = "captured" });
        if (condition == "missing-page-result") report.Pages.RemoveAt(0);
        if (condition == "duplicate-page-result") report.Pages.Add(report.Pages[0] with { });
        if (condition == "unknown-navigation-status") report.Pages[0].NavigationStatus = "future-navigation";
        if (condition == "blocked-navigation") report.Pages[0].NavigationStatus = "blocked";
        if (condition == "duplicate-profile-page")
            report = report with { Profile = report.Profile with { Pages = [.. report.Profile.Pages, report.Profile.Pages[0]] } };
        if (condition == "empty-profile")
            report = report with { Profile = report.Profile with { Pages = [] } };
        if (condition == "unknown-profile-schema")
            report = report with { Profile = report.Profile with { SchemaVersion = 99 } };
        if (condition == "different-profile-process")
            report = report with { Profile = report.Profile with { ProcessName = "AnotherApplication" } };

        Unverified(Render(report));
    }

    [TestMethod]
    [DataRow("scroll")]
    [DataRow("dropdown")]
    [DataRow("dialog")]
    public void CapturedControlsMustBelongToThatDeclaredPage(string kind)
    {
        var state = Captured("001", kind);
        state.After = state.After! with { ControlId = "OtherPageControl" };

        Unverified(Render(Report(state)));
    }

    [TestMethod]
    [DataRow("pending", "待采集")]
    [DataRow("blocked", "受阻")]
    [DataRow("not-tested", "未测试")]
    [DataRow("future-state", "未知状态")]
    public void AllStateStatusesAndReasonsRemainVisibleWithoutBorrowingExistingScans(string status, string expected)
    {
        var state = Captured("001");
        state.Status = status;
        state.Error = "Provider did not expose the requested popup.";
        var html = Render(Report(state));

        Unverified(html);
        StringAssert.Contains(Section(html, "states"), expected);
        StringAssert.Contains(Section(html, "states"), status);
        StringAssert.Contains(Section(html, "states"), state.Error);
        Assert.IsFalse(html.Contains("class=\"pass\"", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("running", "正在探索")]
    [DataRow("finished", "执行已结束（不是合规结论）")]
    [DataRow("partial", "部分执行／覆盖不完整")]
    [DataRow("paused", "已暂停／覆盖未完成")]
    [DataRow("future-run", "未知执行状态")]
    public void RunStatusesAreDescriptiveNotComplianceVerdicts(string status, string expected)
    {
        var report = Report();
        report.Status = status;
        report.Error = "Execution explanation.";
        var html = Render(report);

        StringAssert.Contains(Section(html, "progress"), expected);
        StringAssert.Contains(Section(html, "progress"), status);
        StringAssert.Contains(Section(html, "progress"), report.Error);
        Count(html, "verified-states", 0);
        StringAssert.Contains(html, "WCAG");
    }

    [TestMethod]
    [DataRow("not-requested", "尚未执行")]
    [DataRow("pending", "滚动待执行")]
    [DataRow("end-observed", "末端已观察")]
    [DataRow("limit-reached", "有界滚动未完成")]
    [DataRow("blocked", "滚动受阻")]
    [DataRow("future-scroll", "未知滚动状态")]
    public void PageRowsSeparateObservedScrollEndFromBoundedIncompleteOrUnknownCoverage(string status, string expected)
    {
        var state = Captured("001", "scroll");
        state.Before = state.Before! with { ScrollPercent = 100 };
        state.After = state.After! with { ScrollPercent = 100 };
        var report = Report(state);
        report.Pages[0].ScrollStatus = status;
        report.Pages[0].ScrollReason = "Recorded scroll explanation.";

        var html = Render(report);
        var pages = Section(html, "pages");

        StringAssert.Contains(pages, expected);
        StringAssert.Contains(pages, status);
        StringAssert.Contains(pages, report.Pages[0].ScrollReason);
        Count(html, "verified-states", 1);
    }

    [TestMethod]
    public void CachedScrollEndCannotStandInForMissingOrNonterminalStateEvidence()
    {
        var report = Report(Captured("001", "scroll"));
        report.Pages[0].ScrollStatus = "end-observed";

        var html = Render(report);

        StringAssert.Contains(Section(html, "pages"), "缺少已核验的末端状态");
        StringAssert.Contains(Section(html, "progress"), "覆盖未确认");
    }

    [TestMethod]
    public void UnsupportedDropdownAndDialogKeepHonestReasonsWithoutNeedingScanFiles()
    {
        var report = Report(Captured("001"));
        report.Pages[0].Interactions.AddRange(
        [
            new() { Kind = "dropdown", ControlId = "DisplayMode", Status = "not-tested",
                Reason = "Popup item tree is not exposed.", Restoration = new("restored-control", "Original collapsed state observed.") },
            new() { Kind = "dialog", ControlId = "InfoDialog", Status = "blocked",
                Reason = "Dialog root was not exposed.", Restoration = new("blocked", "Dismissal was not verified.") }
        ]);
        report.States.Add(new()
        {
            Id = "002", PageId = "home", Kind = "dropdown", Name = "Unsupported dropdown",
            Directory = @"states\002", Status = "not-tested", Error = "No scan was captured."
        });
        var html = Render(report);

        Count(html, "verified-states", 1);
        Count(html, "failure-occurrences", 1);
        var pages = Section(html, "pages");
        StringAssert.Contains(pages, "Popup item tree is not exposed.");
        StringAssert.Contains(pages, "Dialog root was not exposed.");
        StringAssert.Contains(pages, "Dismissal was not verified.");
        StringAssert.Contains(pages, "0 个已核验状态");
        StringAssert.Contains(Section(html, "states"), "No scan was captured.");
        StringAssert.Contains(Section(html, "progress"), "没有已确认的完整还原");
        Assert.IsFalse(html.Contains("href=\"./states/002/", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("stopped", true, 0, true)]
    [DataRow("stopped", false, 0, false)]
    [DataRow("stopped", true, 1, false)]
    [DataRow("stopped", true, -1, false)]
    [DataRow("not-requested", false, 0, false)]
    [DataRow("no-boundary", false, 0, false)]
    [DataRow("blocked", true, 0, false)]
    [DataRow("not-tested", false, 0, false)]
    [DataRow("future-probe", true, 0, false)]
    public void GuardProbeIsSeparateSafetyEvidenceAndRequiresAnActualBoundary(string status, bool boundary, int actions, bool observed)
    {
        var report = WithProbe(Report(Captured("001")), boundary);
        report.GuardProbe.Status = status;
        report.GuardProbe.ActionsAfterBoundaryViolation = actions;
        report.GuardProbe.Reason = "Probe explanation.";

        var html = Render(report);
        var safety = Section(html, "safety");

        Assert.AreEqual(observed, safety.Contains("已观察到边界后停止，后续动作 0 次", StringComparison.Ordinal));
        StringAssert.Contains(safety, status);
        StringAssert.Contains(safety, "Probe explanation.");
        StringAssert.Contains(safety, "不是无障碍");
        Count(html, "failure-occurrences", 1);
        Count(html, "verified-states", 1);
        Assert.IsFalse(safety.Contains("探针成功", StringComparison.Ordinal));
        if (status != "not-requested")
            StringAssert.Contains(Section(html, "progress"), "此前记录不能证明最终页面／窗口已恢复");
    }

    [TestMethod]
    public void FinishedNormalPathAndStoppedGuardRemainSeparateWithoutClaimingFinalRestoration()
    {
        var report = WithProbe(Report(Captured("001")), true);
        report.GuardProbe.Status = "stopped";
        report.GuardProbe.Reason = "Stopped after the deliberately opened boundary window.";
        report.Restoration = new("restored-navigation", "The original page was observed before the probe.");
        var before = JsonSerializer.Serialize(report, Json.Options);

        var html = Render(report);
        var summary = Section(html, "progress");
        var safety = Section(html, "safety");

        StringAssert.Contains(summary, "执行已结束（不是合规结论）");
        StringAssert.Contains(summary, "finished");
        StringAssert.Contains(summary, "范围探针：已观察到边界后停止，后续动作 0 次");
        StringAssert.Contains(summary, "探针停止后不再还原");
        StringAssert.Contains(summary, "此前记录不能证明最终页面／窗口已恢复");
        Assert.IsFalse(summary.Contains("原导航已还原；控件还原", StringComparison.Ordinal));
        StringAssert.Contains(safety, "stopped");
        StringAssert.Contains(safety, report.Restoration.Message);
        Count(html, "failure-occurrences", 1);
        Assert.AreEqual(before, JsonSerializer.Serialize(report, Json.Options));
    }

    [TestMethod]
    [DataRow("incomplete", false)]
    [DataRow("not-attempted", true)]
    public void FinalProbeRestorationIsNotReplacedByEarlierOrdinaryRestorationEvent(string restoration, bool boundary)
    {
        var report = WithProbe(Report(Captured("001")), boundary);
        const string ordinary = "restored-navigation: Original page was observed before probe navigation.";
        report.Events.Insert(0, new(AuditTestFixture.Time.AddMilliseconds(3500), "ordinary-restoration",
            ordinary, report.Target.WindowHandle, report.Target.ProcessId));
        report.Status = boundary ? "finished" : "partial";
        report.GuardProbe.Status = boundary ? "stopped" : "not-tested";
        report.GuardProbe.Reason = boundary ? "A real boundary stopped all further input." : "No qualifying boundary was observed.";
        report.Restoration = new(restoration, boundary
            ? "No restoration was attempted after the boundary violation."
            : "Probe navigation changed the page; final restoration was not verified.");
        var before = JsonSerializer.Serialize(report, Json.Options);

        var html = Render(report);
        var summary = Section(html, "progress");
        var safety = Section(html, "safety");

        StringAssert.Contains(summary, "整体还原未确认");
        StringAssert.Contains(summary, report.Restoration.Message);
        StringAssert.Contains(safety, restoration);
        StringAssert.Contains(safety, report.Restoration.Message);
        Assert.IsFalse(summary.Contains(ordinary, StringComparison.Ordinal));
        Assert.IsFalse(safety.Contains(ordinary, StringComparison.Ordinal));
        Assert.IsFalse(summary.Contains("原导航已还原；控件还原", StringComparison.Ordinal));
        StringAssert.Contains(html, ordinary);
        StringAssert.Contains(html, "ordinary-restoration");
        StringAssert.Contains(safety, "关闭由本工作流启动的进程属于清理，不是界面还原");
        Assert.AreEqual(boundary, safety.Contains("已观察到边界后停止，后续动作 0 次", StringComparison.Ordinal));
        Count(html, "failure-occurrences", 1);
        Count(html, "verified-states", 1);
        Assert.AreEqual(before, JsonSerializer.Serialize(report, Json.Options));
    }

    [TestMethod]
    [DataRow("no-declaration")]
    [DataRow("unknown-page")]
    [DataRow("same-window")]
    [DataRow("missing-window")]
    [DataRow("missing-time")]
    [DataRow("unrelated-event")]
    [DataRow("different-process")]
    [DataRow("outside-run-time")]
    [DataRow("not-owned")]
    public void IncompleteOrUnrelatedBoundaryEventsDoNotVerifyTheProbe(string condition)
    {
        var report = WithProbe(Report(), true);
        report.GuardProbe.Status = "stopped";
        if (condition == "no-declaration")
            report = report with { Profile = report.Profile with { ScopeProbe = null } };
        if (condition == "unknown-page")
            report = report with { Profile = report.Profile with { ScopeProbe = new("other", "OpenOtherWindow", "Outside scope") } };
        if (condition == "not-owned")
            report = report with { OwnedApplication = false };
        report.Events[0] = condition switch
        {
            "same-window" => report.Events[0] with { WindowHandle = report.Target.WindowHandle },
            "missing-window" => report.Events[0] with { WindowHandle = null },
            "missing-time" => report.Events[0] with { AtUtc = default },
            "unrelated-event" => report.Events[0] with { Kind = "window-observed" },
            "different-process" => report.Events[0] with { ProcessId = 99 },
            "outside-run-time" => report.Events[0] with { AtUtc = report.FinishedAtUtc!.Value.AddMinutes(1) },
            _ => report.Events[0]
        };

        var html = Render(report);

        StringAssert.Contains(Section(html, "safety"), "探针未核验");
        Assert.IsFalse(html.Contains("已观察到边界后停止，后续动作 0 次", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("restored", false)]
    [DataRow("restored-navigation", false)]
    [DataRow("blocked", true)]
    [DataRow("incomplete", true)]
    [DataRow("failed", true)]
    [DataRow("not-attempted", true)]
    [DataRow("not-needed", true)]
    [DataRow("future-restoration", true)]
    public void RecognizedRunRestorationKeepsItsScopeAndUnknownStatesNeverLookRestored(string status, bool warning)
    {
        var report = Report();
        report.Restoration = new(status, "Original state restoration explanation.");

        var html = Render(report);

        Assert.AreEqual(warning, Section(html, "progress").Contains("整体还原未确认", StringComparison.Ordinal));
        StringAssert.Contains(Section(html, "safety"), status);
        StringAssert.Contains(Section(html, "safety"), report.Restoration.Message);
    }

    [TestMethod]
    public void ScopeFailureAndSelectionChangesAreVisibleOutsideClosedDetails()
    {
        var state = Captured("001", "dropdown");
        var report = Report(state);
        report.Events.Add(new(AuditTestFixture.Time, "window-boundary-changed", "Unexpected foreground window.", 99, 42));
        report.Restoration = new("blocked", "Cannot safely restore after a scope violation.");
        report.Pages[0].Interactions.Add(new()
        {
            Kind = "dropdown", ControlId = "DisplayMode", Status = "captured",
            Before = state.Before! with { SelectionAutomationIds = ["Compact"] },
            After = state.After! with { SelectionAutomationIds = ["Detailed"] },
            Restoration = new("restored-control", "Claimed restoration.")
        });

        var html = Render(report);
        var summary = Section(html, "progress");

        StringAssert.Contains(summary, "Unexpected foreground window.");
        StringAssert.Contains(summary, report.Restoration.Message);
        StringAssert.Contains(summary, "前后选择见证不一致");
        StringAssert.Contains(Section(html, "pages"), "选择状态前后不一致");
        Assert.IsFalse(summary.Contains("<details", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("monitor-unavailable", true)]
    [DataRow("unexpected-error", true)]
    [DataRow("popup-verified", false)]
    public void GuardFailuresAreVisibleButPositivePopupEventsStayInDetails(string kind, bool warning)
    {
        var report = Report(Captured("001"));
        report.Events.Add(new(AuditTestFixture.Time, kind, "Native scope event explanation.", 73, 42));

        var html = Render(report);

        Assert.AreEqual(warning, Section(html, "progress").Contains("Native scope event explanation.", StringComparison.Ordinal));
        StringAssert.Contains(html, "Native scope event explanation.");
        StringAssert.Contains(html, "窗口标题是预期值");
        StringAssert.Contains(html, "不能把声明值当作实际观察");
    }

    [TestMethod]
    public void EmptyAndUnfinishedRecordsHaveUnknownCoverageAndDurationRatherThanSuccess()
    {
        var report = Report() with { Pages = [], States = [] };
        report.FinishedAtUtc = null;
        report.Restoration = null;
        var html = Render(report);

        Count(html, "reached-pages", 0);
        Count(html, "verified-states", 0);
        Count(html, "failure-occurrences", 0);
        StringAssert.Contains(html, " / 3 页");
        StringAssert.Contains(Section(html, "progress"), "未记录有效的结束时间");
        StringAssert.Contains(Section(html, "progress"), "整体还原未确认");
        StringAssert.Contains(Section(html, "states"), "尚无视图状态记录");
        StringAssert.Contains(Section(html, "pages"), "未记录／未核验");
        AssertLinksResolve(html);
    }

    [TestMethod]
    public void MissingGuardRecordCannotBecomeAZeroActionSuccess()
    {
        var report = Report() with { GuardProbe = null! };

        var html = Render(report);

        StringAssert.Contains(Section(html, "progress"), "未记录范围探针");
        StringAssert.Contains(Section(html, "progress"), "此前记录不能证明最终页面／窗口已恢复");
        StringAssert.Contains(Section(html, "safety"), "没有可核验的安全观察");
        Assert.IsFalse(html.Contains("已观察到边界后停止", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ADeclaredSeparateDialogCannotBorrowTheOriginalWindowCapture()
    {
        var report = Report(Captured("001", "dialog"));
        report = report with
        {
            Profile = report.Profile with
            {
                Pages = report.Profile.Pages.Select(page => page.Id == "home"
                    ? page with { Dialogs = page.Dialogs.Select(dialog => dialog with { Scope = "owned-window" }).ToArray() } : page).ToArray()
            }
        };

        Unverified(Render(report));
    }

    [TestMethod]
    [DataRow("scroll", "restored-scroll-position", true)]
    [DataRow("scroll", "unchanged", true)]
    [DataRow("dropdown", "restored-control", true)]
    [DataRow("dialog", "restored-control", true)]
    [DataRow("scroll", "restored-control", false)]
    [DataRow("dropdown", "restored-scroll-position", false)]
    [DataRow("dialog", "future-restoration", false)]
    public void InteractionRestorationIsInterpretedWithinItsDeclaredKind(string kind, string status, bool recognized)
    {
        var state = Captured("001", kind);
        var report = Report(state);
        report.Pages[0].Interactions.Add(new()
        {
            Kind = kind, ControlId = state.Before!.ControlId!,
            Status = "captured",
            Before = kind switch
            {
                "dialog" => state.Before! with { DialogPresent = false },
                "dropdown" => state.Before! with { ExpansionState = "Collapsed" },
                _ => state.Before
            },
            After = kind switch
            {
                "dialog" => state.After! with { DialogPresent = false },
                "dropdown" => state.After! with { ExpansionState = "Collapsed" },
                _ => state.After
            },
            Restoration = new(status, "Per-control restoration explanation.")
        });

        var html = Render(report);

        Assert.AreEqual(!recognized, Section(html, "progress").Contains("没有已确认的完整还原", StringComparison.Ordinal));
        StringAssert.Contains(Section(html, "pages"), status);
        StringAssert.Contains(Section(html, "pages"), "Per-control restoration explanation.");
        Assert.IsFalse(Section(html, "pages").Contains("未声明／关联不唯一的交互", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("collapsed")]
    [DataRow("missing-expansion")]
    [DataRow("different-selection")]
    [DataRow("undeclared-selection")]
    [DataRow("different-scroll-position")]
    [DataRow("invalid-scroll-position")]
    [DataRow("different-viewport")]
    [DataRow("missing-dialog")]
    [DataRow("stale-witness")]
    [DataRow("early-after-witness")]
    [DataRow("state-error")]
    public void ContradictoryOrStaleStateWitnessesDoNotSupplyCoverage(string condition)
    {
        var kind = condition is "different-scroll-position" or "invalid-scroll-position" or "different-viewport" ? "scroll"
            : condition == "missing-dialog" ? "dialog" : "dropdown";
        var state = Captured("001", kind);
        if (condition == "collapsed")
        {
            state.Before = state.Before! with { ExpansionState = "Collapsed" };
            state.After = state.After! with { ExpansionState = "Collapsed" };
        }
        if (condition == "missing-expansion")
        {
            state.Before = state.Before! with { ExpansionState = null };
            state.After = state.After! with { ExpansionState = null };
        }
        if (condition == "different-selection") state.After = state.After! with { SelectionAutomationIds = ["Detailed"] };
        if (condition == "undeclared-selection")
        {
            state.Before = state.Before! with { SelectionAutomationIds = ["Unlisted"] };
            state.After = state.After! with { SelectionAutomationIds = ["Unlisted"] };
        }
        if (condition == "different-scroll-position") state.After = state.After! with { ScrollPercent = 60 };
        if (condition == "invalid-scroll-position")
        {
            state.Before = state.Before! with { ScrollPercent = -1 };
            state.After = state.After! with { ScrollPercent = -1 };
        }
        if (condition == "different-viewport") state.After = state.After! with { ViewSizePercent = 35 };
        if (condition == "missing-dialog")
        {
            state.Before = state.Before! with { DialogPresent = null };
            state.After = state.After! with { DialogPresent = null };
        }
        if (condition == "stale-witness") state.Before = state.Before! with { ObservedAtUtc = state.StartedAtUtc.AddMinutes(-1) };
        if (condition == "early-after-witness") state.After = state.After! with { ObservedAtUtc = state.StartedAtUtc };
        if (condition == "state-error") state.Error = "Capture state changed.";

        Unverified(Render(Report(state)));
    }

    [TestMethod]
    public void CapturesOutsideTheDeclaredStateAndTimeBudgetsAreRetainedButNotCounted()
    {
        var first = Captured("001");
        var second = Captured("002", pageId: "list");
        var laterScan = fixture.Report with
        {
            StartedAtUtc = AuditTestFixture.Time.AddSeconds(50),
            FinishedAtUtc = AuditTestFixture.Time.AddSeconds(53)
        };
        var third = Captured("003", pageId: "settings", scan: laterScan);
        var report = Report(first, third, second) with { Profile = Profile() with { MaxStates = 2, MaxDurationSeconds = 30 } };

        var html = Render(report);

        Count(html, "verified-states", 1);
        Count(html, "failure-occurrences", 1);
        StringAssert.Contains(Section(html, "states"), "超出声明的状态数量预算");
        StringAssert.Contains(Section(html, "states"), "不在本次运行的声明时间预算内");
        StringAssert.Contains(html, "href=\"./states/001/report.html\"");
        Assert.IsFalse(html.Contains("href=\"./states/002/", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("href=\"./states/003/", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BudgetAndElapsedTimeAreNotComputedByAddingOverlappingTimers()
    {
        var report = Report(Captured("001")) with { Profile = Profile() with { MaxStates = 1, MaxDurationSeconds = 75 } };
        report.Performance = new(8, 250_000, 650_000);
        var html = Render(report);
        var summary = Section(html, "progress");

        StringAssert.Contains(summary, "1 分 15 秒");
        StringAssert.Contains(summary, "已达到或超过预算");
        Assert.IsFalse(summary.Contains("15 分 0 秒", StringComparison.Ordinal));
        StringAssert.Contains(html, "原生 UI／Axe 调用用时与审计用时重叠，不能相加");
    }

    [TestMethod]
    [DataRow(@"..\outside")]
    [DataRow(@"C:\outside")]
    [DataRow(@"states\bad:stream")]
    [DataRow(@"states\..\outside")]
    [DataRow(@"states\CON")]
    [DataRow(@"states\trailing.")]
    [DataRow(@"..\outside<script>")]
    [DataRow("https://example.invalid/report")]
    public void UnsafeStatePathsRemainEscapedTextAndNeverBecomeLinks(string directory)
    {
        var state = Captured("001") with { Directory = directory };
        var html = Render(Report(state));

        Unverified(html);
        StringAssert.Contains(html, WebUtility.HtmlEncode(directory));
        Assert.IsFalse(html.Contains("href=\"https:", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("href=\"./..", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("<script", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void AppPageStateWitnessEventAndRestorationTextIsEscaped()
    {
        static string Unsafe(string field) => $"<script>{field} & \"quoted\"</script>";
        var state = Captured("001") with { Name = Unsafe("state-name") };
        state.Error = Unsafe("state-error");
        state.Before = state.Before! with { FocusAutomationId = Unsafe("focus"), SelectionAutomationIds = [Unsafe("selection")] };
        var report = Report(state) with
        {
            RunId = Unsafe("run"), Name = Unsafe("name"), ProfileSha256 = Unsafe("profile-hash"),
            Tools = new(fixture.Report.WinappVersion, Unsafe("native-hash"), Unsafe("runner-hash")),
            Profile = Profile() with { Pages = Profile().Pages.Select(page => page.Id == "home"
                ? page with { Name = Unsafe("page-name") } : page).ToArray() }
        };
        report.Error = Unsafe("run-error");
        report.Restoration = new(Unsafe("restore-status"), Unsafe("restore-message"));
        report.GuardProbe.Status = Unsafe("guard-status");
        report.GuardProbe.Reason = Unsafe("guard-reason");
        report.Events.Add(new(AuditTestFixture.Time, Unsafe("event-kind"), Unsafe("event-message")));
        report.Pages[0].Interactions.Add(new()
        {
            Kind = Unsafe("interaction-kind"), ControlId = Unsafe("control"), Status = Unsafe("interaction-status"),
            Reason = Unsafe("interaction-reason"), Before = state.Before
        });

        var html = Render(report);

        foreach (var field in new[]
        {
            "state-name", "state-error", "focus", "selection", "run", "name", "profile-hash", "native-hash", "runner-hash",
            "page-name", "run-error", "restore-status", "restore-message", "guard-status", "guard-reason",
            "event-kind", "event-message", "interaction-kind", "control", "interaction-status", "interaction-reason"
        })
            StringAssert.Contains(html, WebUtility.HtmlEncode(Unsafe(field)), field);
        Assert.IsFalse(html.Contains("<script", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("<button", StringComparison.OrdinalIgnoreCase));
        AssertLinksResolve(html);
    }

    [TestMethod]
    public void CreateNewPreservesTheOriginalReportAndAllRawEvidenceWithoutMutation()
    {
        var report = Report(Captured("001"), Captured("002", "scroll"));
        var original = JsonSerializer.Serialize(report, Json.Options);
        var files = Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);

        var html = Render(report);
        Assert.ThrowsExactly<IOException>(() => ExploreReportRenderer.Write(Path.Combine(fixture.Root, "explore-report.html"), report));

        Assert.AreEqual(html, File.ReadAllText(Path.Combine(fixture.Root, "explore-report.html")));
        Assert.AreEqual(original, JsonSerializer.Serialize(report, Json.Options));
        foreach (var (path, bytes) in files)
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path), path);
        Assert.AreEqual(files.Count + 1, Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Length);
    }

    private ExploreProfile Profile() => new()
    {
        Name = "Three-page WinUI demo", ProcessName = fixture.Report.Target.ProcessName,
        NavigationRootId = "Navigation", InitialPageId = "home",
        Pages =
        [
            new()
            {
                Id = "home", Name = "Overview", NavigationId = "HomeNav", ReadyId = "HomeReady",
                Scroll = new() { ContainerId = "ResultsScroller", TopAnchorId = "FirstResult", BottomAnchorId = "LastResult" },
                Dropdowns = [new() { ControlId = "DisplayMode", ItemIds = ["Compact", "Detailed"] }],
                Dialogs = [new() { OpenerId = "OpenInfo", RootId = "InfoDialog", DismissId = "CloseInfo" }]
            },
            new() { Id = "list", Name = "Items", NavigationId = "ListNav", ReadyId = "ListReady" },
            new() { Id = "settings", Name = "Settings", NavigationId = "SettingsNav", ReadyId = "SettingsReady" }
        ]
    };

    private ExploreReport Report(params ExploreStateResult[] states) => new()
    {
        RunId = "explore-fixture-run", Name = "Bounded accessibility demo", StartedAtUtc = AuditTestFixture.Time,
        FinishedAtUtc = AuditTestFixture.Time.AddMilliseconds(75105), Status = "finished",
        Target = fixture.Report.Target, ProfileSha256 = new string('A', 64),
        Tools = new(fixture.Report.WinappVersion, new string('B', 64), new string('C', 64)),
        Profile = Profile(), OwnedApplication = true, States = states.ToList(),
        Pages = Profile().Pages.Select(page => new ExplorePageResult
        {
            PageId = page.Id, Name = page.Name,
            NavigationStatus = states.Any(state => state.PageId == page.Id) ? "captured" : "pending"
        }).ToList(),
        Restoration = new("restored-navigation", "Original page observed; controls are recorded per interaction.")
    };

    private ExploreStateResult Captured(string id, string kind = "base", string pageId = "home", AuditReport? scan = null, string? directory = null)
    {
        scan ??= fixture.Report;
        directory ??= Path.Combine("states", id);
        var root = Path.Combine(fixture.Root, directory);
        Directory.CreateDirectory(Path.Combine(root, "evidence"));
        foreach (var artifact in fixture.Report.Artifacts)
        {
            var path = Path.Combine(root, artifact.Path);
            if (artifact.Path == AuditTestFixture.SnapshotPath)
                AuditFiles.WriteNew(path, scan.Snapshot);
            else if (artifact.Path == AuditTestFixture.AxePath)
                AuditFiles.WriteNew(path, scan.Axe);
            else if (artifact.Path == AuditTestFixture.ObservationPath)
                AuditFiles.WriteNew(path, scan.Observation);
            else
                File.Copy(Path.Combine(fixture.Source, artifact.Path), path);
        }
        scan = scan with
        {
            Artifacts = fixture.Report.Artifacts.Select(artifact =>
                AuditFiles.Describe(root, artifact.Path, artifact.MediaType, artifact.Source)).ToArray()
        };
        var pathToScan = Path.Combine(root, "scan.json");
        AuditFiles.WriteNew(pathToScan, scan);
        AuditReportRenderer.Write(Path.Combine(root, "report.html"), scan);
        var scroller = pageId == "home" && kind is "base" or "scroll";
        var observation = new ExploreObservation
        {
            ObservedAtUtc = scan.StartedAtUtc, PageId = pageId, WindowHandle = scan.Target.WindowHandle, PageReady = true,
            ControlId = scroller ? "ResultsScroller" : kind switch { "dropdown" => "DisplayMode", "dialog" => "InfoDialog", _ => null },
            ScrollPercent = scroller ? 50 : null, ViewSizePercent = scroller ? 25 : null,
            ExpansionState = kind == "dropdown" ? "Expanded" : null,
            SelectionAutomationIds = kind == "dropdown" ? ["Compact"] : [],
            DialogPresent = kind == "dialog" ? true : null, FocusAutomationId = "HomeNav"
        };
        return new()
        {
            Id = id, PageId = pageId, Name = $"{pageId} {kind} view", Kind = kind, Directory = directory,
            StartedAtUtc = scan.StartedAtUtc, FinishedAtUtc = scan.FinishedAtUtc, Status = "captured",
            Target = scan.Target, Before = observation, After = observation with { ObservedAtUtc = scan.FinishedAtUtc },
            ScanSha256 = AuditFiles.Hash(pathToScan), CaptureStatus = scan.Status, RuleFailures = scan.RuleFailureCount
        };
    }

    private AuditReport Retarget(AuditTarget target) => fixture.Report with
    {
        Target = target,
        Snapshot = AuditTestFixture.Edit(fixture.Report.Snapshot!.Value, json =>
        {
            json["processId"] = target.ProcessId;
            json["windowHandle"] = target.WindowHandle;
            json["rootRuntimeId"] = target.RootRuntimeId;
            json["nodes"]![0]!["runtimeId"] = target.RootRuntimeId;
        }),
        Observation = AuditTestFixture.Edit(fixture.Report.Observation!.Value, json =>
        {
            json["processId"] = target.ProcessId;
            json["windowHandle"] = target.WindowHandle;
        }),
        Axe = fixture.Report.Axe! with { ProcessId = target.ProcessId, WindowHandle = target.WindowHandle }
    };

    private static ExploreReport WithProbe(ExploreReport report, bool boundary)
    {
        report = report with { Profile = report.Profile with { ScopeProbe = new("home", "OpenOtherWindow", "Outside scope") } };
        if (boundary)
            report.Events.Add(new(AuditTestFixture.Time.AddSeconds(4), "window-boundary-changed", "Undeclared foreground window observed.", 99, 42));
        return report;
    }

    private string Render(ExploreReport report)
    {
        var path = Path.Combine(fixture.Root, "explore-report.html");
        ExploreReportRenderer.Write(path, report);
        return File.ReadAllText(path);
    }

    private void AssertLinksResolve(string html)
    {
        var ids = Regex.Matches(html, "\\bid=\"([^\"]+)\"").Select(match => WebUtility.HtmlDecode(match.Groups[1].Value)).ToArray();
        Assert.AreEqual(ids.Length, ids.Distinct(StringComparer.Ordinal).Count(), "Duplicate HTML IDs.");
        foreach (Match link in Regex.Matches(html, "href=\"([^\"]+)\""))
        {
            var url = WebUtility.HtmlDecode(link.Groups[1].Value);
            if (url.StartsWith('#'))
                Assert.IsTrue(ids.Contains(url[1..], StringComparer.Ordinal), url);
            else
            {
                Assert.IsTrue(url.StartsWith("./", StringComparison.Ordinal), url);
                var path = Uri.UnescapeDataString(url[2..]).Replace('/', Path.DirectorySeparatorChar);
                Assert.IsTrue(File.Exists(Path.Combine(fixture.Root, path)), url);
            }
        }
    }

    private static string Section(string html, string id)
    {
        var start = html.IndexOf($"<section id=\"{id}\">", StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, id);
        var end = html.IndexOf("</section>", start, StringComparison.Ordinal);
        Assert.IsTrue(end >= start, id);
        return html[start..end];
    }

    private static void Count(string html, string id, int count) =>
        StringAssert.Contains(html, $"id=\"{id}\">{count}</strong>");

    private static void Unverified(string html)
    {
        Count(html, "reached-pages", 0);
        Count(html, "verified-states", 0);
        Count(html, "failure-occurrences", 0);
        StringAssert.Contains(html, "未核验");
        Assert.IsFalse(html.Contains("href=\"./", StringComparison.Ordinal), "Unverified evidence must not have file links.");
    }
}

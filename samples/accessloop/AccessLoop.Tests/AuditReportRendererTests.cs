using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class AuditReportRendererTests
{
    private AuditTestFixture fixture = null!;

    [TestInitialize]
    public void Initialize() => fixture = new();

    [TestCleanup]
    public void Cleanup() => fixture.Dispose();

    [TestMethod]
    public void SummaryKeepsProblemsLimitsAndErrorsVisibleBeforeTechnicalMetadata()
    {
        const string error = "Snapshot budget exceeded.";
        var html = fixture.Render(fixture.Report with { Error = error });
        var execution = Section(html, "execution");
        var disclosure = execution.IndexOf("<details", StringComparison.Ordinal);

        StringAssert.Contains(execution[..disclosure], "确定的规则失败：<strong>1</strong>");
        StringAssert.Contains(execution[..disclosure], "部分采集");
        StringAssert.Contains(execution[..disclosure], "覆盖限制：");
        StringAssert.Contains(execution[..disclosure], error);
        Assert.IsFalse(execution[..disclosure].Contains("运行 ID", StringComparison.Ordinal));
        StringAssert.Contains(execution[disclosure..], "运行 ID");
        StringAssert.Contains(execution[disclosure..], "根 RuntimeId");
        var automatic = Section(html, "automatic");
        Assert.IsTrue(automatic.IndexOf("class=\"issue-summary\"", StringComparison.Ordinal)
            < automatic.IndexOf("<summary>规则、控件与原始证据", StringComparison.Ordinal));
        Assert.IsFalse(automatic[..automatic.IndexOf("<details", StringComparison.Ordinal)]
            .Contains("<dt>检查 ID", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TechnicalSectionsAreCollapsedWithoutRemovingStableNavigationTargets()
    {
        var html = fixture.Render();

        foreach (var section in new[] { "observations", "catalog", "tree", "focus", "events", "evidence" })
            Assert.IsTrue(Regex.IsMatch(html,
                "<details class=\"report-detail\"><summary>[^<]+</summary><section id=\"" + section + "\""),
                $"Technical section {section} should be inside a closed disclosure.");
        StringAssert.Contains(html, "href=\"#technical-details\"");
        StringAssert.Contains(html, "id=\"node-1\"");
        StringAssert.Contains(html, "href=\"#node-1\"");
        Assert.IsFalse(html.Contains("<script", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void AutomaticFactsNativeRuleFailuresAndAssistanceAreSeparate()
    {
        var automatic = fixture.Render();
        var assisted = fixture.Render(assessment: fixture.Assessment("ai", "suggestion"), name: "assisted.html");

        foreach (var text in new[]
        {
            "执行情况（不是合规结论）", "确定的规则失败", "客观观察（不是规则失败）", "待复核候选（不是规则失败）",
            "未知／无法判断", "不支持", "未测试", "WinAppCLI", "Axe.Windows", "AccessLoop",
            "NameNotNull", "axe.scan", "1.4.3 (partial)", "可选的人工／AI 补充评估"
        })
            StringAssert.Contains(assisted, WebUtility.HtmlEncode(text), text);
        StringAssert.Contains(assisted, "AI 辅助建议（非工具验证）");
        StringAssert.Contains(assisted, "declared-local-model");
        StringAssert.Contains(assisted, "suggestion");
        foreach (var section in new[] { "automatic", "candidates", "observations", "coverage" })
            Assert.AreEqual(Section(automatic, section), Section(assisted, section));
        Assert.IsFalse(assisted.Contains("class=\"pass\"", StringComparison.Ordinal));
        Assert.IsFalse(assisted.Contains("all-Axe-pass", StringComparison.Ordinal));
        Assert.IsTrue(assisted.IndexOf("id=\"automatic\"", StringComparison.Ordinal)
            < assisted.IndexOf("id=\"assisted\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ZeroAxeFindingsWithErrorsAndUnsupportedCoverageNeverMeansApplicationPass()
    {
        var report = fixture.Report with
        {
            Status = "partial", Error = "Observation timed out; visual checks are unsupported.",
            Axe = fixture.Report.Axe! with { Findings = [], ResultGroupErrorCounts = [0] },
            Results =
            [
                new() { CheckId = "axe.scan", Status = "no-violations-reported", Summary = "No Axe violations reported.", Evidence = [AuditTestFixture.AxePath] },
                new() { CheckId = "events.timeline", Status = "blocked", Summary = "Observation timed out." },
                new() { CheckId = "visual.estimates", Status = "unsupported", Summary = "No visual rule implemented." }
            ]
        };

        var html = fixture.Render(report);

        StringAssert.Contains(html, "<dt>执行状态</dt><dd>partial</dd>");
        StringAssert.Contains(html, "确定的规则失败：<strong>0</strong>");
        StringAssert.Contains(html, "规则失败为 0 不代表应用通过");
        StringAssert.Contains(html, WebUtility.HtmlEncode(report.Error));
        StringAssert.Contains(html, "受阻／未完成");
        StringAssert.Contains(html, "不支持");
        StringAssert.Contains(html, "只展示实际发现，不生成全部 Axe 规则通过清单");
        Assert.IsFalse(html.Contains("class=\"pass\"", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("application passed", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void MissingDataRemainsUnknownRatherThanBeingPresentedAsPositiveEvidence()
    {
        var html = fixture.Render(fixture.Report with
        {
            Status = "blocked", Snapshot = null, Observation = null, Axe = null, Results = [], Artifacts = []
        });

        foreach (var text in new[]
        {
            "未记录自动结果", "未提供 UIA 快照", "未记录焦点观察", "未记录事件或未执行观察",
            "未提供 Axe.Windows 扫描数据", "未保存证据文件", "未提供支持的截图证据", "未附加补充评估"
        })
            StringAssert.Contains(html, text);
        Assert.IsFalse(html.Contains("<img ", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("class=\"pass\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AppNamesPropertiesEventsRuleDescriptionsAndAssessmentTextAreEscaped()
    {
        static string U(string field) => $"<script>{field} & \"quoted\" 'text'</script>";
        var snapshot = AuditTestFixture.Edit(fixture.Report.Snapshot!.Value, json =>
        {
            json["title"] = U("snapshot-title");
            var node = json["nodes"]![1]!;
            foreach (var field in new[] { "name", "automationId", "controlType", "frameworkId", "className" })
                node[field] = U(field);
            node["textRuns"]![0]!["fontSize"] = new JsonObject { ["status"] = "error", ["error"] = U("text-error"), ["nativeType"] = U("native-type") };
            node["issues"] = new JsonArray(U("node-issue"));
        });
        var observation = AuditTestFixture.Edit(fixture.Report.Observation!.Value, json =>
        {
            json["stopReason"] = U("stop-reason");
            json["focusTrace"]![0]!["name"] = U("focus-name");
            json["focusTrace"]![0]!["runtimeId"] = U("focus-runtime");
            json["events"]![0]!["displayString"] = U("notification");
            json["events"]![0]!["activityId"] = U("activity");
        });
        var nativeFinding = fixture.Report.Axe!.Findings[0] with
        {
            RuleId = U("rule"), Description = U("description"), HowToFix = U("fix"), Standard = U("standard"),
            ElementProperties = new Dictionary<string, string> { [U("property-key")] = U("property-value") },
            Patterns = [U("pattern")]
        };
        var report = fixture.Report with
        {
            RunId = U("run"),
            Target = fixture.Report.Target with { ProcessName = U("process"), Title = U("title"), RootRuntimeId = U("root-runtime") },
            WinappVersion = U("winapp"), CatalogVersion = U("catalog-version"), Error = U("error"), PrivacyNotice = U("privacy"),
            Catalog = fixture.Report.Catalog.Select(check => check.Id == "axe.scan"
                ? check with { Title = U("catalog-title"), Source = U("catalog-source"), Criteria = [U("criterion")] } : check).ToArray(),
            Snapshot = snapshot, Observation = observation,
            Axe = fixture.Report.Axe! with { Engine = U("engine"), EngineVersion = U("engine-version"), Checkpoint = U("checkpoint"), Findings = [nativeFinding] },
            Results =
            [
                fixture.Report.Results[0] with
                {
                    SourceRuleId = nativeFinding.RuleId, ElementName = U("element-name"), Summary = U("summary"),
                    Details = new Dictionary<string, string> { [U("detail-key")] = U("detail-value") }
                }
            ]
        };
        var assessment = fixture.Assessment("ai", "suggestion") with
        {
            RunId = report.RunId,
            Author = new("ai", U("author"), U("model")),
            Entries = [fixture.Assessment("ai", "suggestion").Entries[0] with { Explanation = U("assessment") }]
        };

        var html = fixture.Render(report, assessment);

        foreach (var field in new[]
        {
            "run", "process", "title", "root-runtime", "winapp", "catalog-version", "error", "privacy",
            "catalog-title", "catalog-source", "criterion", "snapshot-title", "name", "automationId",
            "controlType", "frameworkId", "className", "text-error", "native-type", "node-issue",
            "stop-reason", "focus-name", "focus-runtime", "notification", "activity", "rule", "description",
            "fix", "standard", "property-key", "property-value", "pattern", "engine", "engine-version",
            "checkpoint", "element-name", "summary", "detail-key", "detail-value", "author", "model", "assessment"
        })
            StringAssert.Contains(html, WebUtility.HtmlEncode(U(field)), field);
        Assert.IsFalse(html.Contains("<script", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains(U("assessment"), StringComparison.Ordinal));
    }

    [TestMethod]
    public void TreeHierarchyPropertiesFocusLinksAndTextPrivacyAreReadable()
    {
        var html = fixture.Render();

        StringAssert.Contains(html, "<ol class=\"tree\"><li id=\"node-0\">");
        StringAssert.Contains(html, "</details><ol class=\"tree\"><li id=\"node-1\">");
        StringAssert.Contains(html, "href=\"#node-1\"");
        StringAssert.Contains(html, "AutomationId");
        StringAssert.Contains(html, "缺少 AutomationId（可测试性提示，不是自动可访问性失败）");
        StringAssert.Contains(html, "RuntimeId");
        StringAssert.Contains(html, "runtime-button");
        StringAssert.Contains(html, "ButtonClass");
        StringAssert.Contains(html, "Invoke");
        StringAssert.Contains(html, "left=125, top=100, width=90, height=32");
        StringAssert.Contains(html, "unsupported（不支持）");
        StringAssert.Contains(html, "mixed（混合，不能当作单一值）");
        StringAssert.Contains(html, "withheld（隐私保留）");
        StringAssert.Contains(html, "initial");
        StringAssert.Contains(html, "forward");
        StringAssert.Contains(html, "Saved");
        StringAssert.Contains(html, "save-1");
        StringAssert.Contains(html, "排除原始 Value、文档文本和文本运行内容");
        StringAssert.Contains(html, "截图仍可能包含输入数据或其他敏感内容");
        Assert.IsFalse(html.Contains("DO-NOT-RENDER-INPUT-VALUE", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("DO-NOT-RENDER-TEXT-CONTENT", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("918273645", StringComparison.Ordinal));
    }

    [TestMethod]
    public void OmittedAttributesAreNotConvertedToFalseOrSuccessfulValues()
    {
        var snapshot = AuditTestFixture.Edit(fixture.Report.Snapshot!.Value, json =>
        {
            var node = json["nodes"]![1]!.AsObject();
            node.Remove("isEnabled");
            node["textRuns"]![0]!["foregroundColor"] = new JsonObject { ["status"] = "value" };
        });

        var html = fixture.Render(fixture.Report with { Snapshot = snapshot });

        StringAssert.Contains(html, "<dt>已启用</dt><dd>未提供／未知</dd>");
        StringAssert.Contains(html, "未提供有效值（无法判断）");
    }

    [TestMethod]
    public void ScreenshotIsEmbeddedOfflineLinkedToHashedArtifactAndNeverFalselyOverlaid()
    {
        var html = fixture.Render();

        StringAssert.Contains(html, "href=\"./evidence/window%20%23%26%25.png\"");
        StringAssert.Contains(html, "src=\"data:image/png;base64,");
        StringAssert.Contains(html, fixture.Report.Artifacts.Single(artifact => artifact.MediaType == "image/png").Sha256);
        StringAssert.Contains(html, "未经视觉判定");
        StringAssert.Contains(html, "未绘制元素叠加标记");
        Assert.IsFalse(html.Contains("https://", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("http://", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("<script", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("class=\"overlay", StringComparison.Ordinal));
        StringAssert.Contains(html, "<html lang=\"zh-CN\">");
        StringAssert.Contains(html, "<summary>");
        StringAssert.Contains(html, "<th scope=\"col\">");
        StringAssert.Contains(html, "href=\"#main\"");
    }

    [TestMethod]
    public void AlteredScreenshotIsNotEmbeddedAndEvidenceFailureIsVisible()
    {
        File.AppendAllText(Path.Combine(fixture.Source, AuditTestFixture.ScreenshotPath), "changed");

        var html = fixture.Render();

        StringAssert.Contains(html, "证据文件无法验证");
        StringAssert.Contains(html, "截图证据无法验证，未显示");
        Assert.IsFalse(html.Contains("<img ", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PassiveCaptureMetadataDoesNotImplyScreenCoordinateAlignment()
    {
        AuditFiles.WriteNew(Path.Combine(fixture.Source, "screenshot.json"), new
        {
            schemaVersion = 1, processId = 42, windowHandle = 73, width = 1, height = 1,
            captureMethod = "windows-graphics-capture", activationRequested = false,
            capturedAtUtc = AuditTestFixture.Time, foregroundBefore = 73, foregroundAfter = 73
        });
        File.Copy(Path.Combine(fixture.Source, AuditTestFixture.ScreenshotPath), Path.Combine(fixture.Source, "screenshot.png"));
        var report = fixture.Report with
        {
            Artifacts = fixture.Report.Artifacts.Where(artifact => artifact.MediaType != "image/png")
                .Append(AuditFiles.Describe(fixture.Source, "screenshot.json", "application/json", "WinAppCLI passive capture metadata"))
                .Append(AuditFiles.Describe(fixture.Source, "screenshot.png", "image/png", "Windows Graphics Capture, exact-window")).ToArray(),
            Results = fixture.Report.Results.Append(new AuditCheckResult
            {
                CheckId = "capture.screenshot", Status = "observed", Summary = "Captured without requesting activation.",
                Evidence = ["screenshot.json", "screenshot.png"]
            }).ToArray()
        };

        var html = fixture.Render(report);

        StringAssert.Contains(html, "href=\"./screenshot.json\"");
        StringAssert.Contains(html, "href=\"./screenshot.png\"");
        StringAssert.Contains(html, "Windows Graphics Capture, exact-window");
        StringAssert.Contains(html, "src=\"data:image/png;base64,");
        StringAssert.Contains(html, "未绘制元素叠加标记");
        Assert.IsFalse(html.Contains("class=\"overlay", StringComparison.Ordinal));
    }

    [TestMethod]
    public void UnsafeArtifactTextIsEscapedButNeverTurnedIntoALink()
    {
        const string unsafePath = @"..\outside<script>.png";
        var report = fixture.Report with
        {
            Artifacts = [new(unsafePath, new string('A', 64), 1, "image/png", "<script>source</script>")],
            Results = [fixture.Report.Results[0] with { Evidence = [unsafePath] }]
        };

        var html = fixture.Render(report);

        StringAssert.Contains(html, WebUtility.HtmlEncode(unsafePath));
        StringAssert.Contains(html, WebUtility.HtmlEncode("<script>source</script>"));
        Assert.IsFalse(html.Contains("href=\"./..", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("<script", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("<img ", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RendererNeverOverwritesAnExistingReport()
    {
        var path = Path.Combine(fixture.Source, "report.html");
        var original = fixture.Render();

        Assert.ThrowsExactly<IOException>(() => AuditReportRenderer.Write(path, fixture.Report));

        Assert.AreEqual(original, File.ReadAllText(path));
    }

    [TestMethod]
    public void NormalCaptureAndMeasurementsAreObservationsNotProblemCards()
    {
        var report = fixture.Report with
        {
            Results =
            [
                new() { CheckId = "capture.scope", Status = "observed", Summary = "The requested process and exact window identity were verified.", Evidence = [AuditTestFixture.SnapshotPath] },
                fixture.Report.Results[1],
                fixture.Report.Results[2],
                new()
                {
                    CheckId = "text.contrast", Status = "observed", Summary = "Provider contrast is 21:1.",
                    ElementId = "n1", Details = new Dictionary<string, string> { ["ratio"] = "21", ["comparison"] = "meets-threshold" }
                },
                new() { CheckId = "events.timeline", Status = "observed", Summary = "No events received during the bounded interval." },
                new() { CheckId = "keyboard.repeats", Status = "observed", Summary = "One repeated focus target; not a trap verdict." },
                fixture.Report.Results[0]
            ]
        };

        var html = fixture.Render(report);
        var automatic = Section(html, "automatic");
        var candidates = Section(html, "candidates");
        var observations = Section(html, "observations");

        StringAssert.Contains(automatic, "id=\"result-6\"");
        Assert.AreEqual(1, Regex.Matches(automatic, "<article ").Count);
        StringAssert.Contains(automatic, "<details><summary>Axe.Windows 原生明细与结果引用（不重复计数）</summary>");
        foreach (var check in new[] { "capture.scope", "capture.tree", "uia.identifiers", "text.contrast", "events.timeline", "keyboard.repeats" })
        {
            Assert.IsFalse(automatic.Contains(check, StringComparison.Ordinal), check);
            Assert.IsFalse(candidates.Contains(check, StringComparison.Ordinal), check);
            StringAssert.Contains(observations, check);
        }
        StringAssert.Contains(observations, "exact window identity were verified");
        Assert.IsFalse(observations.Contains("<article ", StringComparison.Ordinal));
        Assert.IsFalse(observations.Contains("未声明标准关联", StringComparison.Ordinal));
        for (var index = 0; index < 6; index++)
            StringAssert.Contains(observations, $"<tr id=\"result-{index}\">");
    }

    [TestMethod]
    [DataRow("text.contrast", "observed", "below-threshold", true)]
    [DataRow("text.contrast", "observed", "meets-threshold", false)]
    [DataRow("text.contrast", "observed", "not-evaluated", false)]
    [DataRow("text.contrast", "inconclusive", "below-threshold", false)]
    [DataRow("text.contrast", "not-tested", "below-threshold", false)]
    [DataRow("text.contrast", "unsupported", "below-threshold", false)]
    [DataRow("text.contrast", "rule-failure", "below-threshold", false)]
    [DataRow("capture.scope", "observed", "below-threshold", false)]
    public void CandidatesRequireAnActualObservedCandidateOutcome(string checkId, string status, string comparison, bool candidate)
    {
        var report = fixture.Report with
        {
            Axe = null,
            Results = [new()
            {
                CheckId = checkId, Status = status, Summary = "Recorded comparison.", ElementId = "n1",
                Details = new Dictionary<string, string> { ["comparison"] = comparison }
            }]
        };
        var html = fixture.Render(report);

        Assert.AreEqual(candidate, Section(html, "candidates").Contains("id=\"result-0\"", StringComparison.Ordinal));
        Assert.AreEqual(status == "rule-failure", Section(html, "automatic").Contains("id=\"result-0\"", StringComparison.Ordinal));
        Assert.AreEqual(1, Regex.Matches(html, "id=\"result-0\"").Count);
        AssertResultDisclosureState(html, report);
    }

    [TestMethod]
    public void CoverageSummaryIsVisibleWhileOriginalAnchorsAndEvidenceAreCollapsed()
    {
        const string reason = "<script>withheld & unreadable</script>";
        var gap = new AuditCheckResult
        {
            CheckId = "text.contrast", Status = "inconclusive", Summary = reason, ElementId = "n1",
            SourceRuleId = "<native-rule>", Details = new Dictionary<string, string> { ["<text-run>"] = "<first>" },
            Evidence = [AuditTestFixture.SnapshotPath]
        };
        var report = fixture.Report with
        {
            Results =
            [
                fixture.Report.Results[1],
                fixture.Report.Results[0],
                gap,
                gap with { Details = new Dictionary<string, string> { ["<text-run>"] = "<second>" } },
                gap with { Status = "future<state>", Summary = "Unrecognized status is not a pass." }
            ]
        };

        var originalScan = File.ReadAllBytes(fixture.ScanPath);
        var html = fixture.Render(report, fixture.Assessment());
        var coverage = Section(html, "coverage");

        Assert.AreEqual(1, Regex.Matches(coverage, Regex.Escape(WebUtility.HtmlEncode(reason))).Count);
        StringAssert.Contains(coverage, "<td>2 条</td>");
        var disclosure = coverage.IndexOf("<details", StringComparison.Ordinal);
        StringAssert.Contains(coverage[..disclosure], "未知／无法判断");
        StringAssert.Contains(coverage[..disclosure], "2 条记录");
        Assert.IsFalse(coverage[..disclosure].Contains("id=\"result-", StringComparison.Ordinal));
        foreach (var text in new[] { "<native-rule>", "<text-run>", "<first>", "<second>", "future<state>" })
            StringAssert.Contains(coverage, WebUtility.HtmlEncode(text));
        StringAssert.Contains(coverage, "href=\"./evidence/snapshot.json\"");
        StringAssert.Contains(coverage, "href=\"#node-1\"");
        StringAssert.Contains(coverage, "不是规则失败");
        Assert.IsFalse(coverage.Contains("<article ", StringComparison.Ordinal));
        for (var index = 0; index < report.Results.Count; index++)
            Assert.AreEqual(1, Regex.Matches(html, $"id=\"result-{index}\"").Count);
        AssertResultDisclosureState(html, report);
        CollectionAssert.AreEqual(originalScan, File.ReadAllBytes(fixture.ScanPath));
        StringAssert.Contains(html, "id=\"assisted\"");
        Assert.IsFalse(html.Contains("<script", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertResultDisclosureState(string html, AuditReport report)
    {
        var closedDetails = new Stack<bool>();
        foreach (Match match in Regex.Matches(html, "<details\\b[^>]*>|</details>|<[^>]*\\bid=\"result-[0-9]+\"[^>]*>"))
        {
            if (match.Value.StartsWith("<details", StringComparison.Ordinal))
                closedDetails.Push(!Regex.IsMatch(match.Value, "\\bopen\\b"));
            else if (match.Value == "</details>")
                closedDetails.Pop();
            else
            {
                var index = int.Parse(Regex.Match(match.Value, "id=\"result-([0-9]+)\"").Groups[1].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
                var result = report.Results[index];
                var isProblem = result.Status == "rule-failure"
                    || (result.Status == "observed" && AuditVisuals.IsCandidate(result));
                Assert.AreEqual(!isProblem, closedDetails.Contains(true),
                    $"Only ordinary observations and coverage records should be folded: {match.Value}");
            }
        }
    }

    private static string Section(string html, string id)
    {
        var start = html.IndexOf($"<section id=\"{id}\">", StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, $"Missing section {id}.");
        var end = html.IndexOf("</section>", start, StringComparison.Ordinal) + "</section>".Length;
        return html[start..end];
    }
}

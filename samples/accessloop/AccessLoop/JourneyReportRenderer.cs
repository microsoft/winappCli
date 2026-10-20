using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AccessLoop;

public static class JourneyReportRenderer
{
    public static void Write(string path, JourneyReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var page = new Page(Path.GetDirectoryName(Path.GetFullPath(path))!, report);
        var content = page.Render();
        using var stream = AuditFiles.OpenNew(path);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private sealed class Page
    {
        private readonly string root;
        private readonly JourneyReport report;
        private readonly StringBuilder html = new();
        private readonly Dictionary<string, ArtifactLink> links = new(StringComparer.Ordinal);
        private readonly WorkbenchFinding[] findings;
        private readonly string[] findingIds;
        private readonly HashSet<string> fixedIds;

        private sealed record ArtifactLink(string? Url, string? Error);
        private sealed record Coverage(bool Reached, bool RequiredEvidence, string Status, string Reason);

        public Page(string root, JourneyReport report)
        {
            this.root = root;
            this.report = report;
            findings = report.Rounds.SelectMany(round => round.Findings).Concat(report.RemainingFindings)
                .GroupBy(finding => finding.Id, StringComparer.Ordinal).Select(group => group.Last()).ToArray();
            findingIds = findings.Select(finding => finding.Id).Concat(report.Repairs.Select(repair => repair.FindingId))
                .Distinct(StringComparer.Ordinal).ToArray();
            fixedIds = findingIds.Where(IsVerifiedFixed).ToHashSet(StringComparer.Ordinal);
        }

        private JourneyRound? Original => report.Rounds.FirstOrDefault();
        private JourneyRound? Latest => report.Rounds.LastOrDefault();

        public string Render()
        {
            html.Append("""
                <!doctype html>
                <html lang="zh-CN"><head><meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'">
                <title>AccessLoop 多页面旅程报告</title>
                <style>
                :root{font-family:system-ui,"Microsoft YaHei",sans-serif;line-height:1.6;color-scheme:light dark}
                body{max-width:88rem;margin:auto;padding:1.5rem;background:#fff;color:#202124}
                h1,h2,h3,h4{line-height:1.3;overflow-wrap:anywhere}a{color:#0753a0}a:visited{color:#60358d}
                :focus-visible{outline:3px solid #0753a0;outline-offset:3px}
                .skip{position:absolute;left:-10000px}.skip:focus{left:1rem;top:1rem;background:#fff;padding:1rem}
                nav,.links{display:flex;flex-wrap:wrap;gap:.5rem 1.5rem}.links{list-style:none;padding:0}
                section{border-top:1px solid #b7bcc4;margin-top:2rem;padding-top:1rem}
                article,.notice{border:1px solid #b7bcc4;padding:1rem;margin:1rem 0;border-radius:.3rem}
                .notice{background:#f4f6fa;border-inline-start:5px solid #8a4b00}
                .metrics{display:flex;flex-wrap:wrap;gap:1rem 2rem}.metrics>div{min-width:8rem}
                .metrics dd{font-weight:700;font-size:1.5rem}dl{margin:1rem 0}dt{font-weight:600}
                .badge{display:inline-block;border:1px solid currentColor;border-radius:.3rem;padding:.15rem .6rem;font-weight:700}
                dd{margin:0 0 .6rem;white-space:pre-wrap;overflow-wrap:anywhere}
                table{border-collapse:collapse;width:100%;text-align:start}th,td{padding:.6rem;border:1px solid #b7bcc4;vertical-align:top}
                th{background:#edf0f5}caption{text-align:start;font-weight:600;margin:.5rem 0}.table-scroll{overflow:auto}
                td,code,pre{overflow-wrap:anywhere}pre{white-space:pre-wrap;background:#f4f6fa;padding:1rem}
                summary{cursor:pointer;padding:.5rem}details{margin:.75rem 0}.unavailable{color:#6b4100}
                article:target{outline:3px solid #0753a0;outline-offset:3px}
                @media(max-width:45rem){body{padding:.75rem}.metrics{gap:.5rem 1rem}}
                @media(prefers-color-scheme:dark){body{background:#16191e;color:#edf0f5}a{color:#a8d1ff}
                a:visited{color:#dbc0ff}.notice,th,pre{background:#242a33}.skip:focus{background:#16191e}
                :focus-visible{outline-color:#a8d1ff}.unavailable{color:#ffd08a}.notice{border-inline-start-color:#ffd08a}}
                </style></head><body><a class="skip" href="#main">跳到报告正文</a>
                <header><h1>AccessLoop 多页面旅程报告</h1>
                <p>在声明的页面范围内导航、采集证据，并在获得独立源编辑授权后逐项修复、复测。</p>
                <nav aria-label="报告章节"><a href="#summary">结果与覆盖</a><a href="#plan">导航计划</a>
                <a href="#rounds">实际进展</a><a href="#pages">逐页证据</a><a href="#findings">问题与修复</a>
                <a href="#sources">输入与源文件哈希</a></nav></header><main id="main">
                """);
            Summary();
            Plan();
            Rounds();
            Pages();
            Findings();
            Sources();
            html.Append("""
                </main><footer><p>这是离线静态报告：不连接服务、不读取凭据、不自动连接应用，也不能执行修复。
                工作区与源文件路径只作为文字显示。请保留整个证据目录；生成时存在且通过相对路径、普通文件和非重解析点检查的文件才提供链接。
                这些链接不是内容哈希验证或作者签名；重新打开本页不会重新验证文件。
                可访问名称、通知、截图、差异和本地路径可能含敏感数据，分享前检查。</p></footer></body></html>
                """);
            return html.ToString();
        }

        private void Summary()
        {
            html.Append("<section id=\"summary\"><h2>运行结果（仅限声明范围）</h2><dl>");
            Pair("旅程", report.Name);
            StatusBadge("run-status", "总体状态（status）", report.Status);
            StatusBadge("verification-status", "必要缺陷／功能验证（verificationStatus）", report.VerificationStatus);
            StatusBadge("capture-status", "采集完整度（captureStatus，含可选字段）", report.CaptureStatus);
            Pair("状态含义", report.Status switch
            {
                "passed" => "运行器报告通过；只指声明页面、已支持规则和固定行为检查。仍须核对覆盖与错误。",
                "partial" => "采集仍有缺口；必要验证是否通过须另读 verificationStatus。总体 partial 的退出码为 2，不是合规通过。",
                "awaiting-approval" => "基线问题保留，等待独立的源编辑授权；不是已修复。",
                "needs-review" => "仍有需人工审阅的发现或不支持的自动修复。",
                "regression" => "复测被拒绝；检查失败与回滚记录，不能把规则暂时消失当作修复成功。",
                "inconclusive" => "构建、导航、采集或验证没有给出完整结论。",
                "completed" => "执行结束不代表覆盖完整、修复成功或符合标准。",
                _ => "保留原始状态，不推断成功。"
            });
            Pair("最新采集轮次", Latest?.Phase);
            html.Append("</dl>");
            if (report.Status == "partial" && report.VerificationStatus == "passed" && report.CaptureStatus == "partial"
                && RequiredRoundValid(Latest))
                html.Append("""
                    <p class="notice">必要缺陷／功能验证已通过，采集仍为 partial；两者不矛盾。
                    已接受的修复保留计数，隐私保留的 Name／文本不补造。总体仍为 partial，退出码 2，不是 WCAG 合规通过。</p>
                    """);
            html.Append("<h3>页面采集覆盖（最新一轮；必要验证另列）</h3><dl class=\"metrics\">");
            var coverage = report.ExpectedPages.Select(page => PageCoverage(Latest, page)).ToArray();
            Metric("coverage-declared", "声明页面", report.ExpectedPages.Count);
            Metric("coverage-reached", "已到达", coverage.Count(page => page.Reached));
            Metric("coverage-required", "必要验证证据齐备", coverage.Count(page => page.RequiredEvidence));
            Metric("coverage-complete", "采集完整", coverage.Count(page => page.Status == "complete"));
            Metric("coverage-partial", "部分／未确认到达", coverage.Count(page => page.Status == "partial"));
            Metric("coverage-unvisited", "未访问", coverage.Count(page => page.Status == "unvisited"));
            html.Append("""
                </dl><p>已到达只计实际观察到声明就绪控件的页面。完整、部分、未访问按声明清单逐页统计；
                采集完整不表示没有规则失败。必要验证证据齐备也不表示各项检查已通过，检查结果另列。
                旧轮次的成功不能填补最新轮次缺失的页面。</p>
                """);
            if (!RequiredRoundValid(Latest) || coverage.Any(page => page.Status != "complete"))
                html.Append("<p class=\"notice\">采集覆盖不完整：缺失、partial 或受阻的页面不能当作采集完成，即使必要验证通过或问题数为 0。</p>");
            if (!RequiredRoundValid(Latest))
                html.Append("<p class=\"notice\">必要验证证据不足：缺失路由、控件、Axe 或固定行为证据仍会阻止确认修复，不能用可选字段保留作为豁免。</p>");
            html.Append("<h3>问题与修复（独立问题 ID）</h3><dl class=\"metrics\">");
            Metric("finding-fixed", "已验证修复", fixedIds.Count);
            Metric("finding-unresolved", "未解决／未证实修复", findingIds.Length - fixedIds.Count);
            Metric("finding-blocked", "其中受阻／需人工", findingIds.Count(id => !fixedIds.Contains(id) && IsBlocked(id)));
            Metric("finding-remaining", "运行器保留的剩余问题", report.RemainingFindings.Count);
            Metric("repair-attempts", "实际修复记录", report.Repairs.Count);
            html.Append("""
                </dl><p>“已验证修复”要求有接受的修复记录、必要路由／控件／Axe／行为证据可靠的对应复测轮次、未回滚且问题不在保留清单中。
                仅因隐私保留而 partial 的采集可在必要证据可靠时保留修复结论，但仍显示 partial；
                截断、采集错误或根身份不一致不属于此例外。
                仅在某轮消失、没有报告或尚未尝试都不是修复。受阻问题包含在未解决计数中。</p>
                <p class="notice">这不是 WCAG 合规结论。0 个已报告问题不等于整个应用可访问。
                可选检查的 unknown／inconclusive、withheld（隐私保留）、unsupported 和 not-tested 保持未知、不支持或未测试；
                不补造截图对比度、任意页面状态或真实读屏输出的通过结论。</p>
                """);
            var failures = FailureMessages().Distinct(StringComparer.Ordinal).ToArray();
            if (failures.Length > 0)
            {
                html.Append("<h3>失败、受阻与回滚记录</h3><ul>");
                foreach (var error in failures)
                    html.Append("<li>").Append(H(error)).Append("</li>");
                html.Append("</ul>");
            }
            if (report.Repairs.Any(repair => repair.RolledBack || repair.Status != "fixed"))
                html.Append("""
                    <p class="notice">存在被拒绝或受阻的修复。最新复测可能描述回滚前的尝试，不是回滚后源文件的新扫描。
                    RolledBack=false 也不证明源文件未变或无需恢复；先看错误和原始源备份，不要覆盖并发编辑。</p>
                    """);
            html.Append("<ul class=\"links\"><li>");
            Link("journey-report.json", "机器可读旅程报告");
            html.Append("</li></ul></section>");
        }

        private void Plan()
        {
            html.Append("<section id=\"plan\"><h2>导航计划与真实候选</h2><dl>");
            Pair("计划来源", report.PlanSource switch
            {
                "declared-order" => "声明顺序（未调用 AI 规划）",
                "reviewed-plan" => "已审阅计划（提供的 JSON；不推断作者或模型）",
                "copilot" => "Copilot 有界规划（根据观察到的导航候选）",
                "unplanned" => "未生成计划",
                _ => "未识别来源（不推断是否使用 AI）"
            });
            Pair("来源原始值", report.PlanSource);
            Pair("页面顺序", report.Plan is null ? null : string.Join(" → ", report.Plan.PageOrder));
            Pair("计划说明（不是已执行证明）", report.Plan?.Explanation);
            html.Append("""
                </dl><p>计划只决定声明页面的访问顺序，不得增删或重复页面。Copilot 规划调用不使用工具；
                导航和验证由运行器执行。规划来源不代表修复建议来源；本报告未记录建议模型身份时不作推断。
                每轮启动新的自有演示进程，每次访问先回到 Home，再走冻结的 Home → 目标路由。</p>
                """);
            TableStart("观察到的导航候选", "页面", "导航 AutomationId", "运行时 Name", "ControlType", "RuntimeId");
            foreach (var candidate in report.Candidates)
            {
                html.Append("<tr>");
                Cell(candidate.PageId); Cell(candidate.NavigationId); Cell(candidate.Name); Cell(candidate.ControlType);
                Cell("候选记录未提供／未知；不由 AutomationId 补造");
                html.Append("</tr>");
            }
            TableEnd();
            if (report.Candidates.Count == 0)
                html.Append("<p>未记录导航候选；不能断言发现或到达了所有页面。</p>");
            TableStart("冻结路由（声明与重放依据）", "页面", "起点页面", "导航 AutomationId", "就绪 AutomationId");
            foreach (var route in report.FrozenRoutes)
            {
                html.Append("<tr>");
                Cell(route.PageId); Cell(route.FromPageId); Cell(route.NavigationId); Cell(route.ReadyId);
                html.Append("</tr>");
            }
            TableEnd();
            ArtifactList(null, "navigation-plan.json", "navigation-plan-input.json", "routes.json", @"discovery\candidates.json");
            html.Append("</section>");
        }

        private void Rounds()
        {
            html.Append("<section id=\"rounds\"><h2>实际轮次与逐项修复</h2><p>各轮记录的问题数：</p><p id=\"finding-trend\">")
                .Append(H(report.Rounds.Count == 0 ? "无轮次记录／未知" :
                    string.Join(" → ", report.Rounds.Select(round => round.Findings.Count.ToString(CultureInfo.InvariantCulture)))))
                .Append("</p><p>数字来自实际 Rounds，不是预设成功路径。被拒绝的复测也保留在此；其数字不替代运行器保留的剩余问题。</p>");
            TableStart("实际采集轮次", "轮次／必要验证状态", "必要验证证据齐备页数", "采集完整页数", "问题记录数", "错误", "原始轮次与路由证据");
            foreach (var round in report.Rounds)
            {
                html.Append("<tr>");
                Cell($"{round.Phase} · {round.Status}");
                Cell($"{report.ExpectedPages.Count(page => PageCoverage(round, page).RequiredEvidence)} / {report.ExpectedPages.Count}");
                Cell($"{report.ExpectedPages.Count(page => PageCoverage(round, page).Status == "complete")} / {report.ExpectedPages.Count}");
                Cell(round.Findings.Count.ToString(CultureInfo.InvariantCulture));
                Cell(round.Error ?? "未记录错误（不是完整性证明）");
                html.Append("<td>"); Link(round.Phase + "-round.json", "页面、检查及实际路由 JSON"); html.Append("</td></tr>");
            }
            TableEnd();
            TableStart("实际修复记录（剩余数来自 Repairs）", "序号／问题", "页面", "结果", "已回滚", "保留的剩余问题数");
            for (var index = 0; index < report.Repairs.Count; index++)
            {
                var repair = report.Repairs[index];
                html.Append("<tr><td><a href=\"#repair-").Append(index).Append("\">")
                    .Append(index + 1).Append(" · ").Append(H(repair.FindingId)).Append("</a></td>");
                Cell(repair.PageId); Cell(repair.Status); Cell(repair.RolledBack ? "是" : "未记录已回滚");
                Cell(repair.RemainingFindings.ToString(CultureInfo.InvariantCulture));
                html.Append("</tr>");
            }
            TableEnd();
            if (report.Repairs.Count == 0)
                html.Append("<p>没有修复记录；不声称已应用源编辑或已调用修复模型。</p>");
            html.Append("</section>");
        }

        private void Pages()
        {
            html.Append("<section id=\"pages\"><h2>声明页面：原始与最新证据</h2>");
            if (report.ExpectedPages.Count == 0)
                html.Append("<p class=\"notice\">未记录声明页面清单，覆盖范围未知。</p>");
            foreach (var expected in report.ExpectedPages)
            {
                html.Append("<article><h3>").Append(H(expected.Title)).Append(" · <code>")
                    .Append(H(expected.Id)).Append("</code></h3><dl>");
                var coverage = PageCoverage(Latest, expected);
                Pair("最新覆盖", $"{coverage.Status} · {coverage.Reason}");
                Pair("声明就绪控件", expected.ReadyId);
                Pair("必须保留的控件", string.Join("；", expected.RequiredControlIds));
                html.Append("</dl>");
                TableStart("原始与最新页面链接", "证据版本", "页面／采集状态", "本页报告与自动数据");
                PageVersion("原始", Original, expected.Id);
                PageVersion("最新／最终采集（不隐去失败尝试）", Latest, expected.Id);
                TableEnd();
                if (expected.Exercise is { } exercise)
                {
                    html.Append("<details><summary>冻结的行为验收条件</summary><dl>");
                    Pair("目标 AutomationId", exercise.TargetId);
                    Pair("预审目标 Name（不是模型自主发现）", exercise.ExpectedName);
                    Pair("输入控件／结果控件", $"{exercise.AnchorId} / {exercise.ResultId}");
                    Pair("固定输入", exercise.InputText);
                    Pair("初始结果", exercise.InitialResult);
                    Pair("Enter 后预期结果", exercise.ExpectedResult);
                    Pair("真实 Tab 上限", exercise.MaxTabs.ToString(CultureInfo.InvariantCulture));
                    html.Append("</dl></details>");
                }
                foreach (var round in report.Rounds)
                {
                    var pages = round.Pages.Where(page => page.PageId == expected.Id).ToArray();
                    html.Append("<details><summary>").Append(H(round.Phase)).Append(" · ")
                        .Append(H(PageCoverage(round, expected).Reason)).Append("</summary>");
                    if (pages.Length == 0)
                        html.Append("<p>未访问：此轮没有该声明页面的结果，不能用旧证据补齐。</p>");
                    foreach (var page in pages)
                        PageDetails(round, page, expected);
                    html.Append("</details>");
                }
                html.Append("</article>");
            }
            var extraPages = report.Rounds.SelectMany(round => round.Pages.Select(page => (round.Phase, Page: page)))
                .Where(item => !report.ExpectedPages.Any(expected => expected.Id == item.Page.PageId)).ToArray();
            if (extraPages.Length > 0)
            {
                html.Append("<h3>声明外的页面记录（不计入覆盖）</h3>");
                foreach (var (phase, page) in extraPages)
                {
                    html.Append("<p>").Append(H($"{phase} · {page.PageId} · {page.Status}")).Append("</p>");
                    PageLinks(page);
                }
            }
            html.Append("</section>");
        }

        private void PageVersion(string label, JourneyRound? round, string pageId)
        {
            var pages = round?.Pages.Where(page => page.PageId == pageId).ToArray() ?? [];
            html.Append("<tr>"); Cell($"{label} · {round?.Phase ?? "无轮次"}");
            Cell(pages.Length == 0 ? "未访问／无证据" :
                string.Join("；", pages.Select(page => $"{page.Status} / audit: {page.Audit?.Status ?? "未提供"}")));
            html.Append("<td>");
            foreach (var page in pages)
                PageLinks(page);
            if (pages.Length == 0)
                html.Append("不可用：没有本轮页面结果");
            html.Append("</td></tr>");
        }

        private void PageDetails(JourneyRound round, JourneyPageResult page, JourneyPageDefinition expected)
        {
            html.Append("<dl>");
            Pair("页面状态", page.Status);
            Pair("自动采集状态", page.Audit?.Status);
            Pair("必要验证证据", PageCoverage(round, expected).RequiredEvidence ? "齐备（检查结果另列）" : "不齐备／不能确认");
            Pair("实际路由", $"{page.Route.FromPageId} → {page.Route.PageId} · {page.Route.NavigationId} · ready: {page.Route.ReadyId}");
            Pair("观察到的控件", page.ObservedControlIds.Count == 0 ? null : string.Join("；", page.ObservedControlIds));
            Pair("目标进程", page.Audit?.Target.ProcessName);
            Pair("窗口标题", page.Audit?.Target.Title);
            Pair("根 RuntimeId", page.Audit?.Target.RootRuntimeId);
            Pair("页面错误", page.Error ?? "未记录");
            Pair("采集错误", page.Audit?.Error ?? "未记录");
            if (page.Audit is { } audit)
            {
                Pair("原始规则失败记录数", audit.RuleFailureCount.ToString(CultureInfo.InvariantCulture));
                Pair("其他记录（不是问题计数）", string.Join("；", audit.Results.Where(result => result.Status != "rule-failure")
                    .GroupBy(result => result.Status).Select(group => $"{group.Key}: {group.Count()}")));
            }
            html.Append("</dl>");
            PageLinks(page);
            Link(round.Phase + "-round.json", "实际路由和检查的原始记录");
            html.Append("""
                <p>UIA 缺失字段保持未知；withheld（隐私保留）不是有效值或失败。
                节点属性、规则失败卡片、可用的控件截图裁剪及未支持检查请打开原始页面报告。此页不复制或重拍截图。</p>
                """);
            Checks("就绪与控件保留检查", page.Checks.Select(check => (check.Id, check.Passed, check.Expected, check.Actual)));
            if (page.Exercise is not { } observation)
            {
                html.Append("<p>").Append(expected.Exercise is null ? "此页面未声明行为练习。" : "行为练习未提供／无法判断。").Append("</p>");
                return;
            }
            html.Append("<h4>声明的真实键盘练习</h4><dl>");
            Pair("练习状态", observation.Status); Pair("练习错误", observation.Error ?? "未记录");
            html.Append("</dl>");
            Checks("固定行为检查（含 Name）", observation.Checks.Select(check => (check.RuleId, check.Passed, check.Expected, check.Actual)));
            if (observation.FocusTrace.Count == 0)
                html.Append("<p>未记录真实焦点轨迹，不能补造 Tab／Enter 通过或改用 Invoke 证明键盘可用。</p>");
            else
            {
                html.Append("<ol>");
                foreach (var focus in observation.FocusTrace)
                    html.Append("<li>").Append(H($"Tab {focus.Tab} · {Display(focus.AutomationId)} · Name: {Display(focus.Name)}")).Append("</li>");
                html.Append("</ol>");
            }
        }

        private void Findings()
        {
            html.Append("""
                <section id="findings"><h2>问题、页面出现位置与修复结果</h2>
                <p>清单保留实际轮次发现和修复记录。正常的 capture.scope 等 observed 记录不是问题；
                未报告违规、未知或未测试也不自动变成规则失败。稳定 ID 只用于此项目中的跨轮比较，RuntimeId 只说明当次采集。</p>
                """);
            if (findingIds.Length == 0)
                html.Append("<p>未记录问题；必要页面证据不足时仍无法判断。可选采集缺口另列，不代表全部覆盖通过。</p>");
            foreach (var id in findingIds)
            {
                var finding = findings.FirstOrDefault(item => item.Id == id);
                html.Append("<article><h3><code>").Append(H(id)).Append("</code></h3><dl>");
                Pair("问题结果", fixedIds.Contains(id) ? "已验证修复" : IsBlocked(id) ? "受阻／需人工审阅，仍未解决" : "未解决／未证实修复");
                Pair("规则", finding?.RuleId);
                Pair("AutomationId", finding?.AutomationId);
                Pair("源项目（仅文字）", finding?.Project);
                Pair("修复归属页面", finding?.OwnerScenarioName);
                Pair("跨轮身份", finding is null ? null : finding.StableIdentity ? "记录为稳定" : "不稳定；需人工映射");
                Pair("自动修复资格", finding is null ? "缺少原始问题记录，无法判断" : finding.Repairable
                    ? "仅具备受限 Name 修复资格；仍须源候选唯一、哈希匹配及全页复测"
                    : finding.RepairReason ?? "未提供支持的自动修复理由；需人工审阅");
                if (finding?.RepairReason is { } reason && finding.Repairable)
                    Pair("修复限制", reason);
                html.Append("</dl>");
                TableStart("实际问题出现位置", "轮次／页面", "规则说明／修复提示", "运行时控件", "原始问题卡片");
                var affectedPages = new HashSet<string>(StringComparer.Ordinal);
                foreach (var round in report.Rounds)
                foreach (var item in round.Findings.Where(item => item.Id == id))
                foreach (var occurrence in item.Occurrences)
                {
                    affectedPages.Add(occurrence.ScenarioName);
                    html.Append("<tr>"); Cell($"{round.Phase} · {occurrence.ScenarioName}");
                    Cell($"{occurrence.Finding.Description}\n{occurrence.Finding.HowToFix}");
                    Cell($"RuntimeId: {Display(occurrence.Finding.ElementProperties.GetValueOrDefault("RuntimeId"))}\n" +
                        $"AutomationId: {Display(occurrence.Finding.AutomationId)}");
                    html.Append("<td>");
                    var matches = round.Pages.Where(page => page.PageId == occurrence.ScenarioName).ToArray();
                    if (matches.Length == 1)
                        FindingLink(matches[0], occurrence.Finding);
                    else
                        html.Append("页面结果缺失或重复，无法唯一关联原始问题卡片");
                    html.Append("</td></tr>");
                }
                TableEnd();
                foreach (var occurrence in report.RemainingFindings.Where(item => item.Id == id).SelectMany(item => item.Occurrences))
                    affectedPages.Add(occurrence.ScenarioName);
                foreach (var repair in report.Repairs.Where(repair => repair.FindingId == id))
                    affectedPages.Add(repair.PageId);
                html.Append("<h4>受影响页面的最新验证／基线报告路径</h4>");
                foreach (var pageId in affectedPages)
                {
                    html.Append("<p>").Append(H(pageId)).Append(" · ").Append(H(Latest?.Phase ?? "无轮次")).Append("</p>");
                    var pages = Latest?.Pages.Where(page => page.PageId == pageId).ToArray() ?? [];
                    foreach (var page in pages)
                        PageLinks(page);
                    if (pages.Length == 0)
                        html.Append("<p>最新轮未访问此页面；不能把旧报告当作最终验证。</p>");
                }
                var repairs = report.Repairs.Select((repair, index) => (Repair: repair, Index: index))
                    .Where(item => item.Repair.FindingId == id).ToArray();
                if (repairs.Length == 0)
                    html.Append("<p>尚无源候选／差异／修复记录。不直接读取当前工作区来补造源映射。</p>");
                foreach (var (repair, index) in repairs)
                    Repair(repair, index);
                html.Append("</article>");
            }
            html.Append("</section>");
        }

        private void Repair(JourneyRepair repair, int index)
        {
            html.Append("<article id=\"repair-").Append(index).Append("\"><h4>修复记录 ")
                .Append(index + 1).Append("</h4><dl>");
            Pair("修复状态（原始值）", repair.Status);
            Pair("页面／AutomationId", $"{repair.PageId} / {Display(repair.AutomationId)}");
            Pair("源候选路径（仅文字）", repair.SourcePath);
            Pair("RolledBack", repair.RolledBack ? "true · 已记录回滚" : "false · 未记录已回滚，不证明源文件未变");
            Pair("保留的剩余问题数", repair.RemainingFindings.ToString(CultureInfo.InvariantCulture));
            html.Append("</dl>");
            foreach (var error in repair.Errors)
                html.Append("<p class=\"notice\">").Append(H(error)).Append("</p>");
            if (repair.Status != "fixed" || repair.RolledBack)
                html.Append("<p class=\"notice\">此记录不是已保留的成功修复；查看失败、备份和回滚状态。</p>");
            if (repair.Diff is null)
                html.Append("<p>未记录差异；不能据此确认是否发生过源编辑。</p>");
            else
                html.Append("<details><summary>源候选差异（原样转义）</summary><pre>").Append(H(repair.Diff)).Append("</pre></details>");
            ArtifactList(repair.EvidencePath, "source-candidates.json", "proposal.json", "repair.diff", "repair-result.json", "source.original.txt");
            html.Append("</article>");
        }

        private void Sources()
        {
            html.Append("<section id=\"sources\"><h2>输入、原始源文件与未触及文件的哈希记录</h2><dl>");
            Pair("运行 ID", report.RunId);
            Pair("报告版本", report.SchemaVersion.ToString(CultureInfo.InvariantCulture));
            Pair("项目路径（仅文字）", report.Project);
            Pair("开始 UTC", report.StartedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            Pair("结束 UTC", report.FinishedAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            Pair("输入 SHA256", report.InputSha256);
            Pair("WinAppCLI 版本", report.WinappVersion);
            html.Append("""
                </dl><p>这些是运行时保存的原始哈希，不是当前工作区的重新测量。未触及文件保留原始哈希记录；
                有修复的文件也保留原始值用于追溯，不能把它误读为最终哈希。
                缺少最终测量时不保证当前文件仍一致。不得修改验收输入、预期 Name、检查或哈希来让验证通过。</p>
                """);
            TableStart("源文件基线（所有路径仅文字）", "项目内相对路径", "原始 SHA256", "修复记录关联");
            foreach (var source in report.OriginalSources)
            {
                var repairs = report.Repairs.Where(repair => string.Equals(repair.SourcePath, source.RelativePath, StringComparison.OrdinalIgnoreCase)).ToArray();
                html.Append("<tr>"); Cell(source.RelativePath); Cell(source.Sha256);
                Cell(repairs.Length == 0 ? "未触及记录：没有修复指向此文件；未重新读取当前文件" :
                    string.Join("；", repairs.Select(repair => $"{repair.Status} · RolledBack={repair.RolledBack}")));
                html.Append("</tr>");
            }
            TableEnd();
            if (report.OriginalSources.Count == 0)
                html.Append("<p>未提供源文件基线；不能证明未触及文件保持不变。</p>");
            ArtifactList(null, "journey-input.json", "acceptance.json", "source-baseline.json");
            html.Append("</section>");
        }

        private Coverage PageCoverage(JourneyRound? round, JourneyPageDefinition expected)
        {
            var pages = round?.Pages.Where(candidate => candidate.PageId == expected.Id).ToArray() ?? [];
            if (pages.Length == 0)
                return new(false, false, "unvisited", "未访问：没有本轮页面结果");
            var reached = pages.Any(candidate => candidate.ObservedControlIds.Contains(expected.ReadyId, StringComparer.Ordinal));
            if (pages.Length != 1)
                return new(reached, false, "partial", "部分：同一页面有重复结果");
            var page = pages[0];
            var reasons = new List<string>();
            if (!reached) reasons.Add("未观察到声明就绪控件");
            if (page.Status is not ("passed" or "failed")) reasons.Add($"页面状态 {page.Status}");
            if (page.Error is not null) reasons.Add(page.Error);
            if (page.Audit is null) reasons.Add("未提供页面审计");
            else
            {
                if (!JourneyPolicy.HasRequiredAuditEvidence(page.Audit)) reasons.Add("必要审计证据不足");
                if (page.Audit.Error is not null) reasons.Add(page.Audit.Error);
                if (page.Audit.Axe is null) reasons.Add("未提供 Axe 扫描");
            }
            if (report.FrozenRoutes.Count(route => route.PageId == expected.Id && route == page.Route) != 1)
                reasons.Add("无法确认重放冻结路由");
            if (expected.RequiredControlIds.Any(id => !page.ObservedControlIds.Contains(id, StringComparer.Ordinal)))
                reasons.Add("缺少必须保留的控件");
            if (page.Checks.Count == 0 || page.Checks.Any(check => !check.Passed))
                reasons.Add("就绪／控件保留检查缺失或失败");
            if (expected.Exercise is not null && (page.Exercise is null || page.Exercise.Error is not null || page.Exercise.Checks.Count != 5))
                reasons.Add("固定行为练习证据不完整");
            var requiredEvidence = reasons.Count == 0;
            if (page.Audit is { Status: not "completed" } audit)
                reasons.Add($"audit.Status={audit.Status}");
            return new(reached, requiredEvidence, reasons.Count == 0 ? "complete" : "partial",
                reasons.Count == 0 ? "采集完整（不代表没有规则失败）" : "部分：" + string.Join("；", reasons)
                    + (requiredEvidence ? "；必要验证证据齐备，可选采集仍不完整" : ""));
        }

        private bool RequiredRoundValid(JourneyRound? round) => round is not null && report.ExpectedPages.Count > 0
            && round.Error is null && (round.Status is "passed" or "failed")
            && round.Pages.Count == report.ExpectedPages.Count
            && report.ExpectedPages.All(page => PageCoverage(round, page).RequiredEvidence);

        private bool IsVerifiedFixed(string id)
        {
            var repair = report.Repairs.LastOrDefault(candidate => candidate.FindingId == id);
            if (repair is null || repair.Status != "fixed" || repair.RolledBack || repair.Errors.Count > 0
                || report.RemainingFindings.Any(finding => finding.Id == id))
                return false;
            var index = report.Rounds.FindLastIndex(round => round.Phase == repair.EvidencePath);
            return index > 0 && report.Rounds.Take(index).Any(round => round.Findings.Any(finding => finding.Id == id))
                && RequiredRoundValid(report.Rounds[index]) && !report.Rounds[index].Findings.Any(finding => finding.Id == id);
        }

        private bool IsBlocked(string id)
        {
            var finding = findings.FirstOrDefault(candidate => candidate.Id == id);
            var repair = report.Repairs.LastOrDefault(candidate => candidate.FindingId == id);
            return finding is null || !finding.Repairable || !finding.StableIdentity
                || (repair is not null && (!fixedIds.Contains(id) || repair.RolledBack));
        }

        private IEnumerable<string> FailureMessages()
        {
            if (report.Error is not null) yield return report.Error;
            foreach (var error in report.Errors) yield return error;
            foreach (var round in report.Rounds)
            {
                if (round.Error is not null) yield return $"{round.Phase}: {round.Error}";
                foreach (var page in round.Pages)
                foreach (var error in new[] { page.Error, page.Audit?.Error, page.Exercise?.Error }.OfType<string>())
                    yield return $"{round.Phase} / {page.PageId}: {error}";
            }
            foreach (var repair in report.Repairs)
            {
                if (repair.Status != "fixed" || repair.RolledBack)
                    yield return $"{repair.FindingId}: {repair.Status} · RolledBack={repair.RolledBack}";
                foreach (var error in repair.Errors) yield return $"{repair.FindingId}: {error}";
            }
        }

        private void FindingLink(JourneyPageResult page, AccessibilityFinding finding)
        {
            var matches = new List<int>();
            if (page.Audit is { } audit)
            {
                var runtime = finding.ElementProperties.GetValueOrDefault("RuntimeId");
                for (var index = 0; index < audit.Results.Count; index++)
                {
                    var result = audit.Results[index];
                    if (result.Status != "rule-failure" || (result.SourceRuleId ?? result.CheckId) != finding.RuleId || result.ElementId is null)
                        continue;
                    var nodes = SnapshotNodes(audit).Where(node => Property(node, "id") == result.ElementId).ToArray();
                    if (nodes.Length == 1 && (UsableIdentity(runtime)
                        ? Property(nodes[0], "runtimeId") == runtime
                        : UsableIdentity(finding.AutomationId) && Property(nodes[0], "automationId") == finding.AutomationId))
                        matches.Add(index);
                }
            }
            Link(EvidenceFile(page.EvidencePath, "report.html"), matches.Count == 1
                ? "规则失败卡片／已有控件截图（如可用）" : "原始页面报告（无法唯一关联控件截图）",
                matches.Count == 1 ? "#result-" + matches[0].ToString(CultureInfo.InvariantCulture) : "#automatic");
        }

        private static IEnumerable<JsonElement> SnapshotNodes(AuditReport audit) =>
            audit.Snapshot is { ValueKind: JsonValueKind.Object } snapshot
                && snapshot.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array
                ? nodes.EnumerateArray() : [];

        private static string? Property(JsonElement node, string key) => node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static bool UsableIdentity(string? value) => !string.IsNullOrWhiteSpace(value)
            && value is not ("withheld" or "unknown" or "unsupported" or "mixed" or "error");

        private void PageLinks(JourneyPageResult page)
        {
            html.Append("<p>");
            Link(EvidenceFile(page.EvidencePath, "report.html"), "页面报告");
            html.Append(" · "); Link(EvidenceFile(page.EvidencePath, "scan.json"), "自动扫描 JSON");
            html.Append("</p>");
        }

        private static string EvidenceFile(string evidencePath, string file) => evidencePath + "\\" + file;

        private void ArtifactList(string? evidencePath, params string[] files)
        {
            html.Append("<ul class=\"links\">");
            foreach (var file in files)
            {
                html.Append("<li>"); Link(evidencePath is null ? file : EvidenceFile(evidencePath, file), file); html.Append("</li>");
            }
            html.Append("</ul>");
        }

        private void Link(string relative, string label, string fragment = "")
        {
            if (!links.TryGetValue(relative, out var link))
            {
                try
                {
                    var resolved = AuditFiles.ResolveArtifact(root, relative);
                    var url = "./" + string.Join("/", Path.GetRelativePath(root, resolved).Split(Path.DirectorySeparatorChar)
                        .Select(Uri.EscapeDataString));
                    link = new(url, null);
                }
                catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    link = new(null, error.Message);
                }
                links.Add(relative, link);
            }
            if (link.Url is not null)
                html.Append("<a href=\"").Append(H(link.Url + fragment)).Append("\">").Append(H(label)).Append("</a>");
            else
                html.Append("<span class=\"unavailable\">").Append(H(label)).Append("不可用：").Append(H(link.Error)).Append("</span>");
            html.Append(" <code>").Append(H(relative + fragment)).Append("</code>");
        }

        private void Checks(string title, IEnumerable<(string Id, bool Passed, string Expected, string? Actual)> source)
        {
            var checks = source.ToArray();
            TableStart(title, "检查", "预期", "实际", "记录结果");
            foreach (var (id, passed, expected, actual) in checks)
            {
                html.Append("<tr>"); Cell(id); Cell(expected); Cell(actual); Cell(passed ? "通过此项检查" : "失败"); html.Append("</tr>");
            }
            TableEnd();
            if (checks.Length == 0)
                html.Append("<p>未记录检查；不视为通过。</p>");
        }

        private void TableStart(string caption, params string[] headings)
        {
            html.Append("<div class=\"table-scroll\" role=\"region\" tabindex=\"0\" aria-label=\"").Append(H(caption))
                .Append("\"><table><caption>").Append(H(caption)).Append("</caption><thead><tr>");
            foreach (var heading in headings)
                html.Append("<th scope=\"col\">").Append(H(heading)).Append("</th>");
            html.Append("</tr></thead><tbody>");
        }

        private void TableEnd() => html.Append("</tbody></table></div>");
        private void Cell(string? value) => html.Append("<td>").Append(H(Display(value))).Append("</td>");
        private void Pair(string label, string? value) => html.Append("<dt>").Append(H(label)).Append("</dt><dd>").Append(H(Display(value))).Append("</dd>");
        private void StatusBadge(string id, string label, string value) => html.Append("<dt>").Append(H(label))
            .Append("</dt><dd><span class=\"badge\" id=\"").Append(H(id)).Append("\">").Append(H(value)).Append("</span></dd>");
        private void Metric(string id, string label, int value) => html.Append("<div><dt>").Append(H(label))
            .Append("</dt><dd id=\"").Append(H(id)).Append("\">").Append(value).Append("</dd></div>");
        private static string Display(string? value) => value is null ? "未提供／未知" : value.Length == 0 ? "空字符串（已记录）" :
            value == "withheld" ? "withheld（隐私保留，不能当作有效值）" : value;
        private static string H(string? value) => WebUtility.HtmlEncode(value ?? "");
    }
}

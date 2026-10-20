using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AccessLoop;

public static partial class AuditReportRenderer
{
    public static void Write(string path, AuditReport report, AuditAssessmentDocument? assessment = null)
    {
        if (assessment is not null)
            AuditAssessment.Validate(assessment, report);
        var page = new Page(Path.GetDirectoryName(Path.GetFullPath(path))!, report, assessment);
        var html = page.Render();
        using var stream = AuditFiles.OpenNew(path);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(html);
    }

    private sealed partial class Page
    {
        private readonly string root;
        private readonly AuditReport report;
        private readonly AuditAssessmentDocument? assessment;
        private readonly StringBuilder html = new();
        private readonly Dictionary<string, AuditCheckDefinition> catalog = new(StringComparer.Ordinal);
        private readonly List<ArtifactLink> artifactLinks = [];
        private readonly Dictionary<string, ArtifactLink> artifacts = new(StringComparer.OrdinalIgnoreCase);
        private readonly JsonElement[] nodes;
        private readonly Dictionary<string, int> nodeIndexes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> runtimeIndexes = new(StringComparer.Ordinal);

        private sealed record ArtifactLink(AuditArtifact Artifact, string? Url, string? Error);

        public Page(string root, AuditReport report, AuditAssessmentDocument? assessment)
        {
            this.root = root;
            this.report = report;
            this.assessment = assessment;
            foreach (var check in report.Catalog)
                catalog.TryAdd(check.Id, check);
            nodes = Items(Field(report.Snapshot, "nodes")).ToArray();
            for (var index = 0; index < nodes.Length; index++)
            {
                if (String(Field(nodes[index], "id")) is { Length: > 0 } id)
                    nodeIndexes.TryAdd(id, index);
            }
            foreach (var group in nodes.Select((node, index) => (Id: String(Field(node, "runtimeId")), Index: index))
                .Where(node => !string.IsNullOrEmpty(node.Id)).GroupBy(node => node.Id!, StringComparer.Ordinal))
            {
                if (group.Count() == 1)
                    runtimeIndexes.Add(group.Key, group.Single().Index);
            }
            foreach (var artifact in report.Artifacts)
            {
                ArtifactLink link;
                try
                {
                    AuditFiles.VerifyArtifact(root, artifact);
                    link = new(artifact, RelativeUrl(artifact.Path), null);
                }
                catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    link = new(artifact, null, exception.Message);
                }
                artifactLinks.Add(link);
                try
                {
                    artifacts.TryAdd(AuditFiles.NormalizeArtifactPath(artifact.Path), link);
                }
                catch (InvalidDataException)
                {
                    // An unsafe reference is shown as unavailable, never turned into a URL.
                }
            }
            LoadControlImages();
        }

        public string Render()
        {
            html.Append("""
                <!doctype html>
                <html lang="zh-CN"><head><meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; img-src data:; base-uri 'none'; form-action 'none'">
                <title>AccessLoop 页面审计报告</title>
                <style>
                :root{color-scheme:light dark;font-family:system-ui,"Microsoft YaHei",sans-serif;line-height:1.6}
                body{margin:0 auto;max-width:96rem;padding:1.5rem;background:#fff;color:#202124}
                a{color:#0753a0}a:visited{color:#60358d}:focus-visible{outline:3px solid #0753a0;outline-offset:3px}
                .skip{position:absolute;left:-10000px}.skip:focus{left:1rem;top:1rem;background:#fff;padding:1rem;z-index:1}
                nav{display:flex;flex-wrap:wrap;gap:1rem}section{margin-block:2rem;border-block-start:1px solid #b7bcc4;padding-block-start:1rem}
                article,figure,.notice{border:1px solid #b7bcc4;border-radius:.3rem;padding:1rem;margin:1rem 0}
                .rule-failure{border-inline-start:5px solid #a51b27}.assisted{border-inline-start:5px solid #6951a5}
                .notice{background:#f4f6fa}.muted{color:#495462}h1,h2,h3,h4{line-height:1.3;overflow-wrap:anywhere}
                dl{display:grid;grid-template-columns:minmax(9rem,16rem) minmax(0,1fr);gap:.3rem 1rem}
                dt{font-weight:600}dd{margin:0;white-space:pre-wrap;overflow-wrap:anywhere}code{overflow-wrap:anywhere}
                .counts{display:flex;flex-wrap:wrap;gap:.7rem;list-style:none;padding:0}.counts li{border:1px solid #a7aeb7;padding:.5rem}
                table{border-collapse:collapse;width:100%;text-align:start}caption{text-align:start;font-weight:600;margin:.5rem 0}
                th,td{padding:.55rem;border:1px solid #b7bcc4;vertical-align:top;overflow-wrap:anywhere;min-width:5rem}
                th{background:#edf0f5}.table-scroll{overflow:auto}summary{cursor:pointer;padding:.45rem}
                details{margin:.4rem 0}summary:hover{background:#edf0f5}.tree{padding-inline-start:1.4rem}
                .report-detail{border:1px solid #b7bcc4;border-radius:.3rem;padding:.45rem .75rem;margin-block:.75rem}
                .report-detail>summary{font-weight:600}.report-detail>section{border:0;margin-block:1rem;padding:0}
                .lead{font-size:1.25rem;font-weight:600}.issue-summary{font-size:1.08rem}
                .reading-guide{margin-block:1rem}.coverage-summary li{margin-block:.4rem}
                .tree>li{border-inline-start:2px solid #c3c9d1;padding-inline-start:.5rem;list-style:decimal}
                figure img{max-width:100%;height:auto;border:1px solid #a7aeb7}pre{white-space:pre-wrap;overflow-wrap:anywhere}
                .window-map{display:block;width:100%;height:auto}.control-crop{display:block;max-width:100%;height:auto;max-height:16rem;margin-top:.6rem}
                .marker-outline,.marker-border,.crop-border{fill:none;vector-effect:non-scaling-stroke}
                .marker-outline{stroke:white;stroke-width:6}.marker-border,.crop-border{stroke:#ba0018;stroke-width:3}
                .marker-badge{fill:#7b0010;stroke:white;stroke-width:1}.marker-number{fill:white;font:bold 16px system-ui}
                .issue-marker:target .marker-border{stroke:#004bcc;stroke-width:6}.issue-marker:target .marker-badge{fill:#004bcc}
                .issue-marker a:focus .marker-border{stroke:#004bcc;stroke-width:6}
                .control-preview{background:#f4f6fa}.control-preview p{margin-block:.5rem 0}
                .location-legend{display:flex;flex-wrap:wrap;gap:.5rem 2rem}
                article:target,tr:target,.result-reference:target{outline:3px solid #0753a0;outline-offset:3px}
                .result-reference{display:inline-block;margin:.2rem .6rem .2rem 0}
                @media(max-width:45rem){body{padding:.75rem}dl{display:block}dd{margin-block-end:.5rem}.tree{padding-inline-start:1rem}}
                @media(prefers-color-scheme:dark){body{background:#16191e;color:#edf0f5}a{color:#a8d1ff}a:visited{color:#dbc0ff}
                :focus-visible{outline-color:#a8d1ff}.skip:focus{background:#16191e}.notice,th,summary:hover{background:#242a33}
                .muted{color:#c3c9d1}.control-preview{background:#242a33}.rule-failure{border-inline-start-color:#ff8994}.assisted{border-inline-start-color:#c0a6ff}}
                </style></head><body>
                <a class="skip" href="#main">跳到报告正文</a>
                <header><h1>AccessLoop 页面审计报告</h1>
                <p>先看本页结果与问题位置，再按需展开规则、控件属性和原始证据。</p>
                <nav aria-label="报告章节"><a href="#execution">本页摘要</a><a href="#automatic">发现的问题</a>
                <a href="#candidates">待复核</a><a href="#coverage">覆盖限制</a>
                <a href="#technical-details">证据与技术详情</a></nav></header>
                <main id="main">
                """);
            Execution();
            AutomaticResults();
            Candidates();
            WindowLocations();
            Coverage();
            if (assessment is not null)
                Assisted();
            html.Append("<section id=\"technical-details\"><h2>证据与技术详情</h2><p>按需查看采集过程；这些记录不增加问题数。</p>");
            ReportPresentation.Details(html, $"采集信息与客观观察 · {report.Results.Count(IsObservation)} 条", Observations);
            ReportPresentation.Details(html, "检查目录与未覆盖能力", Catalog);
            ReportPresentation.Details(html, $"完整 UIA 树与控件属性 · {nodes.Length} 个节点", Snapshot);
            ReportPresentation.Details(html, "键盘与焦点观察", Focus);
            ReportPresentation.Details(html, "UIA 事件记录", Events);
            ReportPresentation.Details(html, "原始截图、文件与证据校验", Evidence);
            if (assessment is null)
                ReportPresentation.Details(html, "人工／AI 补充评估 · 未附加", Assisted);
            html.Append("</section>");
            html.Append("""
                </main><footer><p>本页只读取本地证据，不连接外部服务，不运行应用操作，也不修改自动规则结果。
                SHA256 用于发现文件过期或被修改，不证明报告作者身份，也不是数字签名。
                重新打开静态页面不会重新计算外部文件的哈希；附加评估时会重新校验。</p></footer></body></html>
                """);
            return html.ToString();
        }

        private void Execution()
        {
            html.Append("<section id=\"execution\"><h2>本页摘要</h2><p class=\"lead\">")
                .Append(H(report.Target.Title ?? report.Target.ProcessName)).Append("</p><p class=\"muted\">采集于 ")
                .Append(H(report.StartedAtUtc.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture)))
                .Append("</p><ul class=\"counts\">");
            foreach (var (label, count) in new[]
            {
                ("确定的规则失败", report.RuleFailureCount),
                ("待复核候选", report.Results.Count(IsCandidateObservation))
            })
                html.Append("<li>").Append(H(label)).Append("：<strong>").Append(count).Append("</strong></li>");
            html.Append("<li>采集状态：<strong>").Append(H(ReportPresentation.CaptureStatus(report.Status)))
                .Append("</strong></li><li>本次用时：<strong>")
                .Append(H(ReportPresentation.Duration((decimal)(report.FinishedAtUtc - report.StartedAtUtc).TotalMilliseconds)))
                .Append("</strong></li></ul>");
            html.Append("<p class=\"reading-guide\">先查看下方问题说明和控件局部图；需要定位代码或复核规则时，再展开卡片中的技术详情。</p>");
            html.Append("<p class=\"notice\"><strong>覆盖限制：</strong>");
            if (report.Status == "partial")
                html.Append("部分信息未能采集。");
            else if (report.Status != "completed")
                html.Append("本次采集尚未确认完成。");
            html.Append("仅审计当前窗口和页面状态，未探索所有点击后的界面。未知、未测试和不支持的项目不计作通过。")
                .Append("<a href=\"#coverage\">查看覆盖缺口</a>。规则失败为 0 不代表应用通过。</p>");
            if (!string.IsNullOrEmpty(report.Error))
                html.Append("<h3>采集错误</h3><p class=\"notice\">").Append(H(report.Error)).Append("</p>");
            var unavailable = artifactLinks.Count(link => link.Error is not null);
            if (unavailable > 0)
                html.Append("<p class=\"notice\">生成本页时有 ").Append(unavailable)
                    .Append(" 个证据文件无法验证；缺失或改变的证据不是成功结果。<a href=\"#evidence\">查看原因</a>。</p>");
            ReportPresentation.Details(html, "运行信息、完整范围与隐私说明", ExecutionDetails);
            html.Append("</section>");
        }

        private void ExecutionDetails()
        {
            html.Append("<h3>执行情况（不是合规结论）</h3><dl>");
            Pair("执行状态", report.Status);
            Pair("运行 ID", report.RunId);
            Pair("采集模式", report.Mode);
            Pair("开始时间 UTC", report.StartedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            Pair("结束时间 UTC", report.FinishedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            Pair("目标进程", $"{report.Target.ProcessName} · PID {report.Target.ProcessId}");
            Pair("目标窗口 HWND", report.Target.WindowHandle.ToString(CultureInfo.InvariantCulture));
            Pair("窗口标题", report.Target.Title);
            Pair("进程启动时间 UTC", report.Target.ProcessStartedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            Pair("根 RuntimeId", report.Target.RootRuntimeId);
            Pair("WinAppCLI 版本", report.WinappVersion);
            Pair("目录版本", report.CatalogVersion);
            html.Append("</dl><h3>辅助记录（不计入问题数）</h3><ul class=\"counts\">");
            foreach (var (label, count) in new[]
            {
                ("采集信息与客观观察", report.Results.Count(IsObservation)),
                ("覆盖缺口", report.Results.Count(IsCoverageGap))
            })
            {
                html.Append("<li>").Append(H(label)).Append("：<strong>").Append(count).Append("</strong></li>");
            }
            html.Append("</ul><p>执行完成只说明本次采集结束。观察记录、未知、未测试和不支持的项目不能合并成合规分数。</p>");
            html.Append("""
                <h3>覆盖边界与隐私</h3>
                <p>范围是选定进程的一个窗口、当前页面和有界时间片。UIA 仅反映提供程序公开的属性；
                未展开内容、其他页面、其他进程的弹窗、自绘区域和未执行的业务路径可能不在覆盖范围内。
                未收到事件不证明辅助技术没有输出；有限 Tab 探测不能证明不存在键盘陷阱。</p>
                <p>UIA 属性采集排除原始 Value、文档文本和文本运行内容。受限的文本属性可标记为 withheld（隐私保留），
                不能当作正常值、通过或确定失败。可访问名称、通知文字和截图仍可能包含输入数据或其他敏感内容；分享前检查所有文件。</p>
                """);
            html.Append("<p>").Append(H(report.PrivacyNotice)).Append("</p>");
        }

        private void AutomaticResults()
        {
            html.Append("<section id=\"automatic\"><h2>确定的规则失败（").Append(report.RuleFailureCount).Append("）</h2>");
            html.Append("<p>这里只列扫描实际返回的规则失败。采集状态、观察记录和覆盖缺口不计入问题数。补充评估不会删除、覆盖这些记录或将它们改成通过。</p>");
            if (report.Results.Count == 0)
                html.Append("<p>未记录自动结果；无法据此确认应用可访问性。</p>");
            else if (report.RuleFailureCount == 0)
                html.Append("<p>本次已运行的检查未报告违规；不是页面或应用整体通过。请同时查看待复核候选与覆盖缺口。</p>");
            foreach (var (result, index) in IndexedResults().Where(item => item.Result.Status == "rule-failure"))
                ResultCard(result, index, candidate: false);
            Axe();
            html.Append("</section>");
        }

        private void Candidates()
        {
            var candidates = IndexedResults().Where(item => IsCandidateObservation(item.Result)).ToArray();
            html.Append("<section id=\"candidates\"><h2>待复核候选（不是规则失败） · ").Append(candidates.Length).Append("</h2>");
            html.Append("<p>候选必须有具体的异常比较结果，例如提供程序文字对比度低于数值阈值、但适用条件尚未确认。正常采集信息与正向数值测量不是候选。</p>");
            if (candidates.Length == 0)
                html.Append("<p>未记录待复核候选；这不补足未测试的项目。</p>");
            foreach (var (result, index) in candidates)
                ResultCard(result, index, candidate: true);
            html.Append("</section>");
        }

        private void ResultCard(AuditCheckResult result, int index, bool candidate)
        {
            catalog.TryGetValue(result.CheckId, out var definition);
            html.Append("<article id=\"result-").Append(index).Append("\" class=\"")
                .Append(candidate ? "candidate" : "rule-failure").Append("\"><h3>")
                .Append(H((candidate ? "待复核控件：" : "问题控件：") + ControlLabel(result))).Append("</h3>");
            html.Append("<p class=\"issue-summary\">").Append(H(result.Summary)).Append("</p>");
            ControlPreview(index, result);
            html.Append("<details class=\"report-detail\"><summary>规则、控件与原始证据</summary><dl>");
            Pair("检查 ID", result.CheckId);
            Pair("记录状态", $"{(candidate ? "候选观察（不是规则失败）" : StatusLabel(result.Status))} · {result.Status}");
            Pair("来源", definition?.Source ?? "未知来源（目录缺失）");
            Pair("原生规则 ID", result.SourceRuleId ?? "未提供；能力／观察项不等同于原生规则");
            if (definition?.Criteria.Count > 0)
                Pair("标准关联（目录）", Criteria(definition));
            html.Append("<dt>快照元素</dt><dd>");
            ElementReference(result.ElementId, result.ElementName);
            html.Append("</dd>");
            foreach (var (key, value) in result.Details)
                Pair(key, value);
            html.Append("<dt>证据引用</dt><dd>");
            References(result.Evidence);
            html.Append("</dd></dl></details></article>");
        }

        private void Observations()
        {
            html.Append("<section id=\"observations\"><h2>采集信息与客观观察</h2>");
            html.Append("<p>目标身份核对、节点数量、AutomationId 可测试性、数值测量、按键轨迹和事件记录用于理解采集过程，不作为缺陷或待复核候选。标准关联见检查目录。</p>");
            var results = IndexedResults().Where(item => IsObservation(item.Result)).ToArray();
            if (results.Length == 0)
                html.Append("<p>未记录采集信息或客观观察。</p>");
            else
            {
                TableStart("采集与观察记录（不计入问题数）", "检查／来源", "记录状态／说明", "元素与证据");
                foreach (var (result, index) in results)
                {
                    html.Append("<tr id=\"result-").Append(index).Append("\">");
                    CheckCell(result);
                    html.Append("<td>").Append(H(StatusLabel(result.Status))).Append(" · <code>").Append(H(result.Status))
                        .Append("</code><br>").Append(H(result.Summary));
                    ResultDetails(result);
                    html.Append("</td><td>");
                    ElementReference(result.ElementId, result.ElementName);
                    html.Append("<br>");
                    References(result.Evidence);
                    html.Append("</td></tr>");
                }
                TableEnd();
            }
            html.Append("</section>");
        }

        private void Coverage()
        {
            html.Append("<section id=\"coverage\"><h2>覆盖缺口（不是规则失败）</h2>");
            html.Append("<p>未知、未测试、不支持和受阻按检查与原因合并列示，不增加问题数。隐私保留 withheld 代表没有可用元数据，不能当作正常值或确定失败。每条原始记录仍可单独定位。</p>");
            var groups = IndexedResults().Where(item => IsCoverageGap(item.Result))
                .GroupBy(item => (item.Result.CheckId, item.Result.SourceRuleId, item.Result.Status, item.Result.Summary)).ToArray();
            if (groups.Length == 0)
                html.Append("<p>结果记录中未列出覆盖缺口；不代表目录内每项能力都已测试。请查看检查目录中的无结果项。</p>");
            else
            {
                html.Append("<ul class=\"coverage-summary\">");
                foreach (var check in groups.GroupBy(group => group.Key.CheckId, StringComparer.Ordinal))
                {
                    catalog.TryGetValue(check.Key, out var definition);
                    html.Append("<li><strong>").Append(H(definition?.Title ?? check.Key)).Append("</strong>：")
                        .Append(H(string.Join("；", check.GroupBy(group => group.Key.Status, StringComparer.Ordinal)
                            .Select(status => $"{StatusLabel(status.Key)} · {status.Sum(group => group.Count())} 条记录"))))
                        .Append("</li>");
                }
                html.Append("</ul><details class=\"report-detail\"><summary>展开覆盖原因与逐条证据</summary>");
                TableStart("按检查与原因合并的覆盖记录", "检查／来源", "状态／覆盖原因", "记录数", "原始定位与证据");
                foreach (var group in groups)
                {
                    html.Append("<tr>");
                    CheckCell(group.First().Result);
                    Cell($"{StatusLabel(group.Key.Status)} · {group.Key.Status}\n{group.Key.Summary}");
                    html.Append("<td>").Append(group.Count()).Append(" 条</td><td><details><summary>原始字段与证据 · ")
                        .Append(group.Count()).Append(" 条</summary>");
                    foreach (var (result, index) in group)
                    {
                        html.Append("<span class=\"result-reference\" id=\"result-").Append(index).Append("\">记录 ")
                            .Append(index + 1).Append(" · ");
                        ElementReference(result.ElementId, result.ElementName);
                        html.Append("</span><p>");
                        References(result.Evidence);
                        html.Append("</p>");
                        ResultDetails(result);
                    }
                    html.Append("</details></td></tr>");
                }
                TableEnd();
                html.Append("</details>");
            }
            html.Append("</section>");
        }

        private void CheckCell(AuditCheckResult result)
        {
            catalog.TryGetValue(result.CheckId, out var definition);
            html.Append("<th scope=\"row\"><code>").Append(H(result.CheckId)).Append("</code><br>")
                .Append(H(definition?.Source ?? "未知来源（目录缺失）"));
            if (!string.IsNullOrEmpty(result.SourceRuleId))
                html.Append("<br>原生规则 ID：<code>").Append(H(result.SourceRuleId)).Append("</code>");
            html.Append("</th>");
        }

        private void ResultDetails(AuditCheckResult result)
        {
            if (result.Details.Count == 0)
                return;
            html.Append("<details><summary>附加字段（原样）</summary><dl>");
            foreach (var (key, value) in result.Details)
                Pair(key, value);
            html.Append("</dl></details>");
        }

        private IEnumerable<(AuditCheckResult Result, int Index)> IndexedResults() =>
            report.Results.Select((result, index) => (result, index));

        private static bool IsCandidateObservation(AuditCheckResult result) =>
            result.Status == "observed" && AuditVisuals.IsCandidate(result);

        private static bool IsObservation(AuditCheckResult result) =>
            result.Status == "no-violations-reported" || (result.Status == "observed" && !IsCandidateObservation(result));

        private static bool IsCoverageGap(AuditCheckResult result) =>
            result.Status != "rule-failure" && !IsCandidateObservation(result) && !IsObservation(result);

        private void Catalog()
        {
            html.Append("""
                <section id="catalog"><h2>检查目录与缺失覆盖</h2>
                <p>目录列出能力，不是“所有规则均通过”的清单。标准编号只是该检查的关联范围，partial／observation 不代表完整标准验证。</p>
                <p><code>axe.scan</code> 是 Axe.Windows 扫描能力的聚合项；只有实际返回的原生规则 ID 才列为具体发现，不推测未返回规则的通过状态。</p>
                """);
            TableStart("能力目录", "检查 ID／标题", "来源／模式", "可用性", "标准关联", "本次记录");
            foreach (var check in report.Catalog)
            {
                html.Append("<tr><th scope=\"row\"><code>").Append(H(check.Id)).Append("</code><br>")
                    .Append(H(check.Title)).Append("</th>");
                Cell($"{check.Source} · {check.Mode}");
                Cell(check.Availability);
                Cell(Criteria(check));
                var results = report.Results.Where(result => result.CheckId == check.Id).ToArray();
                Cell(results.Length == 0 ? "无结果／未测试；不代表通过" :
                    string.Join("；", results.GroupBy(result => result.Status)
                        .Select(group => $"{StatusLabel(group.Key)} {group.Count()}")));
                html.Append("</tr>");
            }
            TableEnd();
            html.Append("</section>");
        }

        private void Axe()
        {
            html.Append("<details><summary>Axe.Windows 原生明细与结果引用（不重复计数）</summary><h3>Axe.Windows 实际规则发现</h3>");
            if (report.Axe is not { } axe)
            {
                html.Append("<p>未提供 Axe.Windows 扫描数据；没有发现列表不等于没有问题。</p></details>");
                return;
            }
            html.Append("<dl>");
            Pair("引擎／版本", $"{axe.Engine} · {axe.EngineVersion}");
            Pair("目标", $"PID {axe.ProcessId} · HWND {axe.WindowHandle}");
            Pair("检查点", axe.Checkpoint);
            Pair("结果组错误数（原样）", string.Join(", ", axe.ResultGroupErrorCounts));
            Pair("实际发现条数（去重后）", axe.Findings.Count.ToString(CultureInfo.InvariantCulture));
            html.Append("<dt>原生扫描证据</dt><dd>");
            References(report.Results.Where(result => result.CheckId == "axe.scan").SelectMany(result => result.Evidence)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            html.Append("</dd>");
            html.Append("</dl><p>结果组可以重叠；原始错误数不等于独立规则数量。只展示实际发现，不生成全部 Axe 规则通过清单。</p>");
            if (axe.Findings.Count == 0)
                html.Append("<p>本次 Axe 数据未列出规则失败；这不是应用、页面或标准整体通过的结论。</p>");
            var resultRows = report.Results.Select((result, index) => (Result: result, Index: index))
                .Where(item => item.Result.CheckId == "axe.scan" && item.Result.SourceRuleId is not null)
                .ToArray();
            var ruleResults = resultRows.ToLookup(item => item.Result.SourceRuleId!, StringComparer.Ordinal);
            var elementResults = resultRows.ToLookup(item => (item.Result.SourceRuleId!, item.Result.ElementId));
            foreach (var finding in axe.Findings)
            {
                html.Append("<div class=\"native-finding\"><h4>原生规则：<code>").Append(H(finding.RuleId))
                    .Append("</code></h4><dl>");
                Pair("来源", axe.Engine);
                Pair("能力目录 ID", "axe.scan");
                Pair("标准关联（目录）", Criteria(catalog.GetValueOrDefault("axe.scan")));
                Pair("原生标准说明", finding.Standard);
                Pair("描述", finding.Description);
                Pair("原生修复提示（不是已执行修复）", finding.HowToFix);
                foreach (var (key, value) in finding.ElementProperties)
                    Pair(key, value);
                Pair("控件模式", string.Join(", ", finding.Patterns));
                html.Append("<dt>快照元素</dt><dd>");
                RuntimeReference(finding.ElementProperties.GetValueOrDefault("RuntimeId"));
                html.Append("</dd><dt>对应自动结果与证据</dt><dd>");
                var candidates = ruleResults[finding.RuleId];
                var runtimeId = finding.ElementProperties.GetValueOrDefault("RuntimeId");
                if (runtimeId is not null && runtimeIndexes.TryGetValue(runtimeId, out var nodeIndex))
                {
                    var nodeId = String(Field(nodes[nodeIndex], "id"));
                    candidates = elementResults[(finding.RuleId, nodeId)];
                }
                var matches = candidates.Take(2).ToArray();
                if (matches.Length == 0)
                    html.Append("未找到对应结果引用；请检查扫描记录完整性。");
                else if (matches.Length > 1)
                    html.Append("<a href=\"#automatic\">查看该规则的自动结果</a>（同一规则存在多个候选引用，不伪造唯一元素关联）");
                else
                {
                    var (result, index) = matches[0];
                    html.Append("<p><a href=\"#result-").Append(index).Append("\">自动结果 ").Append(index + 1).Append("</a> · ");
                    References(result.Evidence);
                    html.Append("</p>");
                }
                html.Append("</dd></dl></div>");
            }
            html.Append("</details>");
        }

        private void Assisted()
        {
            html.Append("""
                <section id="assisted" class="assisted"><h2>可选的人工／AI 补充评估</h2>
                <p>这部分是独立判断层，不是自动工具验证。确认、争议或不适用说明都不会重新计算或抹去自动规则失败。
                作者与模型是声明的来源，不是身份认证。只有显式引用已采集证据的判断才可用于讨论；缺失覆盖仍然缺失。</p>
                """);
            if (assessment is null)
            {
                html.Append("<p>未附加补充评估。上面的自动报告可独立阅读；没有人工／AI 意见不影响原始事实记录。</p></section>");
                return;
            }
            html.Append("<dl>");
            Pair("补充来源", assessment.Author.Kind == "ai" ? "AI 辅助建议（非工具验证）" : "人工复核（独立判断）");
            Pair("作者声明", assessment.Author.Name);
            Pair("模型声明", assessment.Author.Model ?? "不适用／未声明");
            Pair("绑定运行 ID", assessment.RunId);
            Pair("绑定 scan.json SHA256", assessment.ScanSha256);
            Pair("评估时间 UTC", assessment.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            html.Append("</dl>");
            if (assessment.Entries.Count == 0)
                html.Append("<p>没有补充条目；不表示人工复核已覆盖全部内容。</p>");
            foreach (var entry in assessment.Entries)
            {
                catalog.TryGetValue(entry.CheckId, out var definition);
                html.Append("<article><h3>").Append(H(definition?.Title ?? entry.CheckId)).Append("</h3><dl>");
                Pair("检查 ID", entry.CheckId);
                Pair("补充状态（不改变自动结论）", AssessmentStatus(entry.Status));
                Pair("自动检查来源", definition?.Source);
                Pair("标准关联（目录）", Criteria(definition));
                html.Append("<dt>快照元素</dt><dd>");
                ElementReference(entry.ElementId, null);
                html.Append("</dd>");
                Pair("作者的说明／理由", entry.Explanation);
                html.Append("<dt>引用的原始证据</dt><dd>");
                References(entry.Evidence);
                html.Append("</dd></dl></article>");
            }
            html.Append("</section>");
        }

        private void ElementReference(string? id, string? name)
        {
            if (id is not null && nodeIndexes.TryGetValue(id, out var index))
                html.Append("<a href=\"#node-").Append(index).Append("\">").Append(H(id)).Append("</a>");
            else
                html.Append(H(id ?? "窗口级记录／未定位到快照节点"));
            if (!string.IsNullOrEmpty(name))
                html.Append(" · ").Append(H(name));
        }

        private void RuntimeReference(string? runtimeId)
        {
            if (runtimeId is not null && runtimeIndexes.TryGetValue(runtimeId, out var index))
                html.Append("<a href=\"#node-").Append(index).Append("\">").Append(H(runtimeId)).Append("</a>");
            else
                html.Append(H(runtimeId ?? "未提供 RuntimeId／无法关联快照节点"));
        }

        private void References(IReadOnlyList<string> references)
        {
            if (references.Count == 0)
                html.Append("未引用证据；不能据此确认未采集的内容。");
            for (var index = 0; index < references.Count; index++)
            {
                if (index > 0)
                    html.Append("；");
                ArtifactLink? link = null;
                try
                {
                    artifacts.TryGetValue(AuditFiles.NormalizeArtifactPath(references[index]), out link);
                }
                catch (InvalidDataException)
                {
                }
                if (link?.Url is not null)
                    html.Append("<a href=\"").Append(H(link.Url)).Append("\" download>").Append(H(references[index])).Append("</a>");
                else
                    html.Append(H(references[index])).Append("（未列入证据或无法验证）");
            }
        }

        private void Pair(string name, string? value) =>
            html.Append("<dt>").Append(H(name)).Append("</dt><dd>").Append(H(value ?? "未提供／未知")).Append("</dd>");

        private void Cell(string? value) => html.Append("<td>").Append(H(value ?? "未提供／未知")).Append("</td>");

        private void TableStart(string caption, params string[] columns)
        {
            html.Append("<div class=\"table-scroll\" role=\"region\" tabindex=\"0\" aria-label=\"").Append(H(caption))
                .Append("\"><table><caption>").Append(H(caption)).Append("</caption><thead><tr>");
            foreach (var column in columns)
                html.Append("<th scope=\"col\">").Append(H(column)).Append("</th>");
            html.Append("</tr></thead><tbody>");
        }

        private void TableEnd() => html.Append("</tbody></table></div>");
        private static string H(string? value) => WebUtility.HtmlEncode(value ?? "");
        private static JsonElement Field(JsonElement? value, string name) => value is { } element ? AuditFiles.Property(element, name) : default;
        private static IEnumerable<JsonElement> Items(JsonElement value) =>
            value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
        private static string? String(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        private static string Display(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => "未提供／未知",
            JsonValueKind.String => string.IsNullOrEmpty(value.GetString()) ? "（空）" : value.GetString()!,
            _ => value.GetRawText()
        };
        private static string Criteria(AuditCheckDefinition? definition) =>
            definition?.Criteria is { Count: > 0 } criteria ? string.Join("；", criteria) : "未声明标准关联；不推导标准合规";
        private static string RelativeUrl(string path) => "./" + string.Join('/',
            AuditFiles.NormalizeArtifactPath(path).Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
        private static string StatusLabel(string status) => status switch
        {
            "rule-failure" => "确定的规则失败",
            "no-violations-reported" => "该检查未报告违规（非整体通过）",
            "observed" => "客观观察（不是规则失败）",
            "inconclusive" => "未知／无法判断",
            "not-tested" => "未测试",
            "unsupported" => "不支持",
            "blocked" => "受阻／未完成",
            _ => "未知状态：" + status
        };
        private static string AssessmentStatus(string status) => status switch
        {
            "needs-review" => "待复核 · needs-review",
            "suggestion" => "建议（未确认） · suggestion",
            "confirmed" => "人工确认（仅补充条目） · confirmed",
            "disputed" => "有争议（保留自动结果） · disputed",
            "not-applicable" => "人工不适用说明（非自动豁免） · not-applicable",
            _ => "未知补充状态：" + status
        };
    }
}

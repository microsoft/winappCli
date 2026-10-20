using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AccessLoop;

public static class SurveyReportRenderer
{
    public static void Write(string path, SurveyState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var root = Path.GetDirectoryName(Path.GetFullPath(path))!;
        AuditFiles.EnsureDirectory(root);
        var html = new Page(root, state).Render();
        using var stream = AuditFiles.OpenNew(path);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(html);
    }

    private sealed record AttemptView(
        SurveyAttempt Attempt, AuditReport? Report, string? ScanRoot, string? ReportUrl, string? ScanUrl, string? Error)
    {
        public bool Captured => Attempt.Status == "captured" && (Report?.Status is "completed" or "partial")
            && ReportUrl is not null && Ready(Attempt.Before) && Ready(Attempt.After)
            && Attempt.StartedAtUtc != default && Attempt.FinishedAtUtc >= Attempt.StartedAtUtc;

        private static bool Ready(SurveyPageReady? ready) => ready is { Selected: true, HeadingMatched: true }
            && !string.IsNullOrWhiteSpace(ready.Heading) && ready.ObservedAtUtc != default;
    }

    private sealed record PageView(int Index, SurveyPageState State, bool InInventory, IReadOnlyList<AttemptView> Attempts)
    {
        public AttemptView? Latest => Attempts.LastOrDefault();
        public bool Current => InInventory && State.Status == "captured" && Latest?.Captured == true
            && Attempts.All(attempt => attempt.Attempt.Number > 0)
            && Attempts.Select(attempt => attempt.Attempt.Number).Distinct().Count() == Attempts.Count;
        public string Progress => Current ? "captured"
            : State.Status == "blocked" || Latest?.Attempt.Status is "blocked" or "interrupted" ? "blocked"
            : State.Status == "running" || Latest?.Attempt.Status == "running" ? "running"
            : State.Status == "pending" && Latest is null ? "pending" : "unverified";
    }

    private sealed record NavigationKey(string Check, string Source, string Rule, string Id, string Type, string RootRuntime, string Runtime);
    private sealed record Occurrence(PageView Page, int Index, AuditCheckResult Result, string Source, string Label, NavigationKey? Navigation);

    private sealed class Page
    {
        private readonly string root;
        private readonly SurveyState state;
        private readonly StringBuilder html = new();
        private readonly PageView[] pages;
        private readonly PageView[] current;
        private readonly Occurrence[] failures;
        private readonly Occurrence[][] shared;
        private readonly Occurrence[] separate;
        private readonly HashSet<string> navigationIds;
        private readonly int? expected;
        private readonly int missingPages;
        private readonly bool inventoryConsistent;

        public Page(string root, SurveyState state)
        {
            this.root = root;
            this.state = state;
            expected = state.Inventory?.Pages.Count;
            var inventoryIds = (state.Inventory?.Pages ?? []).Select(page => page.Id).ToArray();
            var uniqueIds = inventoryIds.Where(id => !string.IsNullOrWhiteSpace(id))
                .GroupBy(id => id, StringComparer.Ordinal).Where(group => group.Count() == 1)
                .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
            var pageIds = state.Pages.GroupBy(page => page.Page.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            navigationIds = (state.Inventory?.NavigationIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);
            inventoryConsistent = expected is > 0 && uniqueIds.Count == expected
                && pageIds.Count == expected && pageIds.All(pair => pair.Value == 1 && uniqueIds.Contains(pair.Key));
            missingPages = uniqueIds.Count(id => !pageIds.ContainsKey(id));
            pages = state.Pages.Select((page, index) => new PageView(index, page,
                uniqueIds.Contains(page.Page.Id) && pageIds[page.Page.Id] == 1,
                page.Attempts.OrderBy(attempt => attempt.Number).Select(ReadAttempt).ToArray())).ToArray();
            current = pages.Where(page => page.Current).ToArray();
            failures = current.SelectMany(Failures).ToArray();
            shared = failures.Where(item => item.Navigation is not null).GroupBy(item => item.Navigation!)
                .Where(group => group.Count() > 1 && group.Select(item => item.Page.Index).Distinct().Count() == group.Count())
                .Select(group => group.ToArray()).ToArray();
            var sharedOccurrences = shared.SelectMany(group => group).ToHashSet();
            separate = failures.Where(item => !sharedOccurrences.Contains(item)).ToArray();
        }

        private AttemptView ReadAttempt(SurveyAttempt attempt)
        {
            try
            {
                var directory = AuditFiles.NormalizeArtifactPath(attempt.Directory);
                if (string.IsNullOrEmpty(attempt.ScanSha256))
                    return new(attempt, null, null, null, null, "未保存扫描 SHA256；本次尝试没有可核验的完整报告。");
                AuditFiles.ValidateHash(attempt.ScanSha256, "attempt scan SHA256");
                var scanRelative = Path.Combine(directory, "scan.json");
                var scanPath = AuditFiles.ResolveArtifact(root, scanRelative);
                var verified = AuditFiles.ReadVerifiedDocument(scanPath);
                if (!string.Equals(verified.Sha256, attempt.ScanSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("scan.json SHA256 与本次尝试绑定的记录不一致。");
                if (!SameTarget(state.Target, verified.Report.Target))
                    throw new InvalidDataException("扫描的进程、启动时间或窗口身份与本次巡检不一致。");
                if (string.IsNullOrWhiteSpace(state.Tools?.WinappVersion)
                    || state.Tools.WinappVersion != verified.Report.WinappVersion)
                    throw new InvalidDataException("扫描的 WinAppCLI 版本与巡检绑定的工具版本不一致。");
                var reportRelative = Path.Combine(directory, "report.html");
                string? reportUrl = null;
                string? error = null;
                try
                {
                    AuditFiles.ResolveArtifact(root, reportRelative);
                    reportUrl = RelativeUrl(reportRelative);
                }
                catch (Exception exception) when (Unavailable(exception))
                {
                    error = "单页 report.html 不可用；未计入当前已采集页：" + exception.Message;
                }
                return new(attempt, verified.Report, verified.Root, reportUrl, RelativeUrl(scanRelative), error);
            }
            catch (Exception exception) when (Unavailable(exception))
            {
                return new(attempt, null, null, null, null, exception.Message);
            }
        }

        private IEnumerable<Occurrence> Failures(PageView page)
        {
            var attempt = page.Latest!;
            var report = attempt.Report!;
            var nodes = Items(Field(report.Snapshot, "nodes")).ToArray();
            var byId = nodes.ToDictionary(node => Text(node, "id")!, StringComparer.Ordinal);
            var runtimes = nodes.ToLookup(node => Runtime(Text(node, "runtimeId")), StringComparer.Ordinal);
            var identifiers = nodes.ToLookup(node => Text(node, "automationId"), StringComparer.Ordinal);
            var native = (report.Axe?.Findings ?? []).ToLookup(
                finding => (finding.RuleId, Runtime(finding.ElementProperties.GetValueOrDefault("RuntimeId"))));
            var safeSnapshot = CanAssociateNavigation(attempt);
            foreach (var (result, index) in report.Results.Select((result, index) => (result, index)))
            {
                if (result.Status != "rule-failure")
                    continue;
                var source = report.Catalog.Single(check => check.Id == result.CheckId).Source;
                var node = result.ElementId is { } id && byId.TryGetValue(id, out var found) ? found : default;
                var automationId = Text(node, "automationId");
                var type = Text(node, "controlType");
                var runtime = Runtime(Text(node, "runtimeId"));
                NavigationKey? key = null;
                if (safeSnapshot && result.CheckId == "axe.scan" && !string.IsNullOrWhiteSpace(result.SourceRuleId)
                    && automationId is not null && navigationIds.Contains(automationId)
                    && !string.IsNullOrWhiteSpace(type) && runtime is not null
                    && identifiers[automationId].Count() == 1 && runtimes[runtime].Count() == 1)
                {
                    var matches = native[(result.SourceRuleId, runtime)].Take(2).ToArray();
                    if (matches.Length == 1
                        && (string.IsNullOrWhiteSpace(matches[0].AutomationId) || matches[0].AutomationId == automationId)
                        && (!result.Details.TryGetValue("runtimeId", out var statedRuntime)
                            || AuditRules.SameRuntimeId(statedRuntime, runtime)))
                        key = new(result.CheckId, source, result.SourceRuleId, automationId, type,
                            Runtime(report.Target.RootRuntimeId)!, runtime);
                }
                var name = new[] { result.ElementName, Text(node, "name"), automationId }
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "无已公开名称";
                yield return new(page, index, result, source, $"{name} · {type ?? "未唯一定位的控件"}", key);
            }
        }

        private bool CanAssociateNavigation(AttemptView attempt)
        {
            if (navigationIds.Count == 0 || attempt.Report?.Snapshot is not { } snapshot)
                return false;
            try
            {
                AuditRules.ValidateSnapshot(snapshot, attempt.Report.Target);
                if (!AuditRules.SameRuntimeId(Text(snapshot, "rootRuntimeId"), attempt.Report.Target.RootRuntimeId))
                    return false;
                var artifact = attempt.Report.Artifacts.SingleOrDefault(artifact =>
                    Path.GetFileName(AuditFiles.NormalizeArtifactPath(artifact.Path)).Equals("snapshot.json", StringComparison.OrdinalIgnoreCase));
                if (artifact is null || artifact.MediaType != "application/json")
                    return false;
                using var saved = AuditFiles.ParseStrict(AuditFiles.ReadArtifactBytes(attempt.ScanRoot!, artifact, AuditFiles.MaxJsonBytes));
                return JsonElement.DeepEquals(saved.RootElement, snapshot);
            }
            catch (Exception exception) when (Unavailable(exception))
            {
                return false;
            }
        }

        public string Render()
        {
            html.Append("""
                <!doctype html>
                <html lang="zh-CN"><head><meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'">
                <title>AccessLoop 现有应用巡检报告</title>
                <style>
                :root{color-scheme:light dark;font-family:system-ui,"Microsoft YaHei",sans-serif;line-height:1.6}
                body{margin:0 auto;max-width:96rem;padding:1.5rem;background:#fff;color:#202124}
                a{color:#0753a0}a:visited{color:#60358d}:focus-visible,:target{outline:3px solid #0753a0;outline-offset:3px}
                .skip{position:absolute;left:-10000px}.skip:focus{left:1rem;top:1rem;background:#fff;padding:1rem}
                nav,.counts{display:flex;flex-wrap:wrap;gap:1rem}.counts{list-style:none;padding:0}.counts li{border:1px solid #b7bcc4;padding:.5rem}
                section{margin-block:2rem;border-block-start:1px solid #b7bcc4;padding-block-start:1rem}
                article,.notice{border:1px solid #b7bcc4;border-radius:.3rem;padding:1rem;margin:1rem 0}
                .rule-failure{border-inline-start:5px solid #a51b27}.notice{background:#f4f6fa}.muted{color:#495462}
                h1,h2,h3,code{overflow-wrap:anywhere}h1,h2,h3{line-height:1.3}
                dl{display:grid;grid-template-columns:minmax(9rem,17rem) minmax(0,1fr);gap:.3rem 1rem}
                dt{font-weight:600}dd{margin:0;white-space:pre-wrap;overflow-wrap:anywhere}
                table{border-collapse:collapse;width:100%;text-align:start}th,td{padding:.55rem;border:1px solid #b7bcc4;vertical-align:top;overflow-wrap:anywhere}
                th{background:#edf0f5}caption{text-align:start;font-weight:600;margin:.5rem 0}.table-scroll{overflow:auto}
                summary{cursor:pointer;padding:.45rem}details{margin:.4rem 0}summary:hover{background:#edf0f5}
                .report-detail{border:1px solid #b7bcc4;border-radius:.3rem;padding:.45rem .75rem;margin-block:.75rem}
                .report-detail>summary{font-weight:600}.report-detail>section{border:0;margin-block:1rem;padding:0}
                .issue-summary{font-size:1.08rem}.coverage-summary li{margin-block:.4rem}
                @media(max-width:45rem){body{padding:.75rem}dl{display:block}dd{margin-block-end:.5rem}}
                @media(prefers-color-scheme:dark){body{background:#16191e;color:#edf0f5}a{color:#a8d1ff}a:visited{color:#dbc0ff}
                .skip:focus{background:#16191e}.notice,th,summary:hover{background:#242a33}.muted{color:#c3c9d1}.rule-failure{border-inline-start-color:#ff8994}}
                </style></head><body><a class="skip" href="#main">跳到报告正文</a>
                <header><h1>AccessLoop 现有应用巡检报告</h1><p><strong>附加现有应用 · 只导航和审计</strong></p>
                """);
            html.Append("<p>").Append(H(state.Name)).Append("</p>");
            html.Append("""
                <nav aria-label="巡检报告章节"><a href="#progress">巡检摘要</a><a href="#failures">问题概览</a>
                <a href="#pages">按页面查看</a><a href="#coverage">覆盖限制</a>
                <a href="#survey-details">执行与技术详情</a></nav></header>
                <main id="main">
                """);
            Progress();
            Findings();
            Pages();
            Candidates();
            Coverage();
            html.Append("<section id=\"survey-details\"><h2>执行与技术详情</h2><p>需要追溯某次尝试或复核运行条件时再展开；这些记录不增加问题数。</p>");
            ReportPresentation.Details(html, "逐页重试历史与旧报告", History);
            ReportPresentation.Details(html, "暂停、续跑、还原与详细耗时", Segments);
            ReportPresentation.Details(html, "应用、工具、环境与文件指纹", Traceability);
            html.Append("</section>");
            html.Append("""
                </main><footer><p>本页是已保存证据的静态概览，不连接应用、不请求源码、不重建应用，也不执行修复。
                每页 report.html 才是该次采集的完整展示：保留截图、原始结果编号、全部观察与未知项和独立补充评估。
                概览不复制所有页面的图像。名称、截图和通知仍可能含敏感信息，分享前检查原始文件。</p>
                <p>生成概览时校验 scan.json 的绑定 SHA256 及其证据文件的哈希、长度和本地路径。
                report.html 仅作为本地页面链接，不宣称有独立签名；重新打开本静态页不会重新校验外部文件。
                原始哈希和配置档案用于追溯，不验证作者身份，也不是数字签名。</p></footer></body></html>
                """);
            return html.ToString();
        }

        private void Progress()
        {
            html.Append("<section id=\"progress\"><h2>巡检摘要</h2><p>")
                .Append(H($"{RunStatus(state.Status)} · {state.Status}")).Append("</p><ul class=\"counts\">");
            Metric("captured-pages", "当前已采集", current.Length, $" / {(expected?.ToString(CultureInfo.InvariantCulture) ?? "未知")} 页");
            Metric("failure-entries", "问题展示条目", separate.Length + shared.Length);
            Metric("failure-occurrences", "规则失败出现次数", failures.Length);
            var progressCounts = new[]
            {
                ("pending-pages", "待采集", pages.Count(page => page.Progress == "pending") + missingPages),
                ("blocked-pages", "受阻", pages.Count(page => page.Progress == "blocked")),
                ("running-pages", "进行中", pages.Count(page => page.Progress == "running")),
                ("unverified-pages", "状态或证据未核验", pages.Count(page => page.Progress == "unverified"))
            };
            foreach (var (id, label, count) in progressCounts.Where(item => item.Item3 > 0))
                Metric(id, label, count, " 页");
            html.Append("</ul>");
            var partial = current.Count(page => page.Latest!.Report!.Status == "partial");
            html.Append("<p class=\"notice\"><strong>覆盖限制：</strong>");
            if (partial > 0)
                html.Append(partial).Append(" 页为部分采集（partial）。");
            if (current.Length == 0)
                html.Append("当前没有可核验的已采集页面；0 条记录不表示未发现问题。");
            html.Append("页面遍历完成不等于无障碍通过；未知和未测试的内容不计作通过。")
                .Append("<a href=\"#coverage\">查看限制</a>，或<a href=\"#pages\">打开单页报告与截图</a>。</p>");
            if (!inventoryConsistent)
                html.Append("<p class=\"notice\">页面清单尚未完整建立，或清单与页面状态不一致；未知、缺失和 0 / 0 都不是完成或通过。</p>");
            var durations = state.Invocations.Where(invocation => invocation.StartedAtUtc != default
                && invocation.FinishedAtUtc >= invocation.StartedAtUtc).ToArray();
            html.Append("<p>累计用时：<strong>").Append(H(durations.Length == 0 ? "尚无有效的已结束执行段" :
                ReportPresentation.Duration(durations.Sum(invocation => (decimal)(invocation.FinishedAtUtc!.Value - invocation.StartedAtUtc).TotalMilliseconds))))
                .Append("</strong>（含重试，不含段间暂停）</p>");
            var last = state.Invocations.OrderBy(invocation => invocation.Number).LastOrDefault();
            if (state.Status == "paused" || !string.IsNullOrEmpty(state.Error) || !string.IsNullOrEmpty(last?.Reason))
            {
                html.Append("<div class=\"notice\"><h3>暂停／停止原因</h3>");
                var reasons = new[] { state.Error, last?.Reason }.Where(reason => !string.IsNullOrWhiteSpace(reason)).Distinct(StringComparer.Ordinal).ToArray();
                foreach (var reason in reasons)
                    html.Append("<p>").Append(H(reason)).Append("</p>");
                if (reasons.Length == 0)
                    html.Append("<p>已暂停，但没有记录原因；不能推断剩余页面已完成。</p>");
                html.Append("<p>窗口最小化、进程／版本或环境变化应先处理。恢复不会重启目标应用；只有确认应用设置没有变化时才应声明“设置未变”。</p></div>");
            }
            if (last?.Restoration?.Status is not ("restored-navigation" or "restored"))
                html.Append("<p class=\"notice\">末段导航还原记录：").Append(H(Restoration(last?.Restoration))).Append("</p>");
            html.Append("<details class=\"report-detail\"><summary>页面状态、分段耗时与还原</summary><dl>");
            Pair("采集开始", state.StartedAtUtc.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture));
            if (durations.Any(invocation => invocation.RetryPages.Count > 0))
            {
                Pair("遍历执行段", ReportPresentation.Duration(durations.Where(invocation => invocation.RetryPages.Count == 0)
                    .Sum(invocation => (decimal)(invocation.FinishedAtUtc!.Value - invocation.StartedAtUtc).TotalMilliseconds)));
                Pair("指定页面重试", ReportPresentation.Duration(durations.Where(invocation => invocation.RetryPages.Count > 0)
                    .Sum(invocation => (decimal)(invocation.FinishedAtUtc!.Value - invocation.StartedAtUtc).TotalMilliseconds)));
            }
            Pair("末段导航还原记录", Restoration(last?.Restoration));
            html.Append("</dl><ul class=\"counts\">");
            foreach (var (id, label, count) in progressCounts.Where(item => item.Item3 == 0))
                Metric(id, label, count, " 页");
            Metric("completed-captures", "completed", current.Count(page => page.Latest!.Report!.Status == "completed"), " 页");
            Metric("partial-captures", "partial", partial, " 页");
            html.Append("</ul><p>“已采集”表示该页最新尝试的导航选择与标题已核对，且存在可校验的扫描和单页报告；不是无障碍通过。旧报告不会替代受阻的新尝试。</p>")
                .Append("<p>partial 表示采集或覆盖存在限制，例如隐私保留的元数据，不等于页面有缺陷；completed 也不代表所有无障碍检查已测试或通过。</p></details>");
            html.Append("</section>");
        }

        private void Findings()
        {
            html.Append("<section id=\"failures\"><h2>问题概览 · 确定的规则失败</h2><p>只统计当前已校验的最新页面扫描中的 rule-failure 结果，不使用状态文件缓存的规则失败数。出现次数与分组条目数都不是经认证的独立根因数。</p>");
            if (failures.Length == 0)
                html.Append(current.Length == 0
                    ? "<p>当前没有可核验的已采集页面；0 条记录不表示未发现问题或应用通过。</p>"
                    : "<p>当前有效扫描中未报告违规；仅限这些页面与已运行检查，不代表整个应用通过。请继续查看候选和覆盖缺口。</p>");
            if (separate.Length == 0 && shared.Length > 0)
                html.Append("<p>当前规则失败都属于下方可安全合并展示的共享导航记录。</p>");
            if (separate.Length > 0)
                html.Append("<details class=\"report-detail\"><summary>展开各页的 ").Append(separate.Length)
                    .Append(" 条问题（不含已合并的共享导航记录）</summary>");
            foreach (var item in separate)
            {
                html.Append("<article class=\"rule-failure\"><h3>").Append(H(item.Label)).Append("</h3>");
                Rule(item);
                ResultLink(item.Page, item.Index, $"{item.Page.State.Page.Name} · 原始结果 {item.Index + 1}");
                html.Append("</article>");
            }
            if (separate.Length > 0)
                html.Append("</details>");
            html.Append("</section><section id=\"shared-navigation\"><h2>共享导航的重复出现记录</h2><ul class=\"counts\">");
            Metric("shared-navigation-entries", "共享导航分组", shared.Length);
            Metric("shared-navigation-occurrences", "这些分组的出现次数", shared.Sum(group => group.Length));
            html.Append("</ul><p>同一导航控件在多个页面重复出现时合并展示；不是把每次出现都当成新问题。</p>")
                .Append("<details><summary>查看合并依据</summary><p>仅对清单中已知的导航 ID、同一原生规则与 ControlType，在绑定的同一进程／窗口中有唯一且一致的 RuntimeId 关联时合并展示。名称为空、普通内容控件、身份变化或关联不唯一时保留逐页条目。</p></details>");
            foreach (var group in shared)
            {
                var first = group[0];
                html.Append("<article class=\"rule-failure shared-navigation\"><h3>").Append(H(first.Label))
                    .Append(" · ").Append(group.Length).Append(" 次出现</h3>");
                Rule(first);
                ResultLink(first.Page, first.Index, "查看一处原始结果与控件截图");
                html.Append("<details><summary>展开 ").Append(group.Length).Append(" 页的原始出现记录</summary><ul>");
                foreach (var item in group)
                {
                    html.Append("<li>");
                    ResultLink(item.Page, item.Index, $"{item.Page.State.Page.Name} · {item.Page.State.Page.Id} · 原始结果 {item.Index + 1}");
                    html.Append("</li>");
                }
                html.Append("</ul></details></article>");
            }
            if (shared.Length == 0)
                html.Append("<p>没有可安全合并的跨页导航记录；不按空名称或相似描述合并控件。</p>");
            html.Append("</section>");
        }

        private void Rule(Occurrence item)
        {
            html.Append("<p class=\"issue-summary\">").Append(H(item.Result.Summary))
                .Append("</p><details><summary>规则来源与关联信息</summary><dl>");
            Pair("来源／检查", $"{item.Source} · {item.Result.CheckId}");
            Pair("原生规则 ID", item.Result.SourceRuleId ?? "未提供；请查看检查来源");
            if (item.Navigation is { } navigation)
                Pair("分组关联", $"{navigation.Id} · {navigation.Type} · RuntimeId {navigation.Runtime}");
            html.Append("</dl></details>");
        }

        private void Candidates()
        {
            html.Append("<section id=\"candidates\"><h2>待复核候选（不是规则失败）</h2>");
            var candidates = current.SelectMany(page => page.Latest!.Report!.Results.Select((result, index) => (Page: page, Result: result, Index: index)))
                .Where(item => item.Result.Status == "observed" && AuditVisuals.IsCandidate(item.Result)).ToArray();
            html.Append("<p>").Append(candidates.Length).Append(" 次候选观察；只包含明确的候选比较结果。正常的身份核对、节点统计、正向测量和事件／按键记录留在单页的“采集信息与客观观察”中。</p>");
            if (candidates.Length > 0)
            {
                Table("候选原始结果", "页面", "来源／检查／原生规则 ID", "候选说明");
                foreach (var item in candidates)
                {
                    html.Append("<tr><td>");
                    ResultLink(item.Page, item.Index, item.Page.State.Page.Name);
                    html.Append("</td>");
                    Cell($"{item.Page.Latest!.Report!.Catalog.Single(check => check.Id == item.Result.CheckId).Source} · {item.Result.CheckId} · {item.Result.SourceRuleId ?? "未提供"}");
                    Cell(item.Result.Summary);
                    html.Append("</tr>");
                }
                TableEnd();
            }
            html.Append("</section>");
        }

        private void Coverage()
        {
            html.Append("<section id=\"coverage\"><h2>覆盖缺口（不增加问题数）</h2><p>以下仅汇总当前有效扫描，按检查、状态和原因合并；全部原始观察、逐项证据和未知项仍以每页报告为准。</p>");
            var groups = current.SelectMany(page => page.Latest!.Report!.Results.Select((result, index) => (Page: page, Result: result, Index: index)))
                .Where(item => item.Result.Status is not ("rule-failure" or "observed" or "no-violations-reported"))
                .GroupBy(item => (item.Result.CheckId, Source: item.Page.Latest!.Report!.Catalog.Single(check => check.Id == item.Result.CheckId).Source,
                    item.Result.SourceRuleId, item.Result.Status, item.Result.Summary)).ToArray();
            if (groups.Length == 0)
                html.Append("<p>没有可汇总的覆盖原因记录；不能推断所有检查已完成。缺失、受阻和 partial 页面仍需查看各页状态。</p>");
            else
            {
                html.Append("<ul class=\"coverage-summary\">");
                foreach (var check in groups.GroupBy(group => group.Key.CheckId, StringComparer.Ordinal))
                {
                    var definition = current.SelectMany(page => page.Latest!.Report!.Catalog).First(item => item.Id == check.Key);
                    html.Append("<li><strong>").Append(H(definition.Title)).Append("</strong>：")
                        .Append(check.SelectMany(group => group).Select(item => item.Page.Index).Distinct().Count())
                        .Append(" 页有未测试、未知或受限记录</li>");
                }
                html.Append("</ul><details class=\"report-detail\"><summary>展开覆盖原因与逐条证据</summary>");
                Table("按检查与原因合并的覆盖记录", "来源／检查／原生规则 ID", "状态／原因", "影响页数／记录数", "原始记录");
                foreach (var group in groups)
                {
                    html.Append("<tr>");
                    Cell($"{group.Key.Source} · {group.Key.CheckId} · {group.Key.SourceRuleId ?? "未提供原生规则 ID"}");
                    Cell($"{group.Key.Status}\n{group.Key.Summary}");
                    Cell($"{group.Select(item => item.Page.Index).Distinct().Count()} 页 · {group.Count()} 条");
                    html.Append("<td><details><summary>查看原始覆盖记录</summary><ul>");
                    foreach (var item in group)
                    {
                        html.Append("<li>");
                        ResultLink(item.Page, item.Index, $"{item.Page.State.Page.Name} · 原始结果 {item.Index + 1}");
                        html.Append("</li>");
                    }
                    html.Append("</ul></details></td></tr>");
                }
                TableEnd();
                html.Append("</details>");
            }
            html.Append("</section>");
        }

        private void Pages()
        {
            html.Append("<section id=\"pages\"><h2>按页面查看</h2><p>选择页面查看问题说明、控件图片和完整报告。本页问题数包含公共导航的重复出现；受阻的新尝试不会借用旧报告的成功结果。</p>");
            Table("逐页进度与证据", "页面", "本次结果", "报告与详情");
            foreach (var page in pages)
            {
                html.Append("<tr id=\"page-").Append(page.Index).Append("\"><th scope=\"row\">")
                    .Append(H(page.State.Page.Name)).Append("</th><td>")
                    .Append(H(PageStatus(page.Progress)));
                if (!page.InInventory)
                    html.Append("<br>不在唯一页面清单中，未计入当前进度");
                html.Append("<br>").Append(H(page.Current
                    ? $"{page.Latest!.Report!.RuleFailureCount} 次规则失败 · {ReportPresentation.CaptureStatus(page.Latest.Report.Status)}"
                    : "当前无有效的已采集扫描；不记作 0 次违规")).Append("</td><td>");
                if (page.Latest?.ReportUrl is { } reportUrl)
                    html.Append("<a href=\"").Append(H(reportUrl)).Append("\">打开单页报告与截图</a>");
                else
                    html.Append("暂无可校验的单页报告");
                if (!page.Current)
                    html.Append("<p>").Append(H(page.Latest?.Error ?? "导航选择、标题、结束时间或证据尚未完整核验；见尝试历史。")).Append("</p>");
                html.Append("<details><summary>页面详情与历史");
                if (page.Attempts.Count > 1)
                    html.Append(" · ").Append(page.Attempts.Count).Append(" 次尝试");
                html.Append("</summary><dl>");
                Pair("页面 ID／分组", $"{page.State.Page.Id} · {string.Join(" / ", page.State.Page.GroupIds)}");
                Pair("状态记录", page.State.Status);
                if (page.Latest is { } latest)
                {
                    Pair("最新尝试", $"{latest.Attempt.Number} · {latest.Attempt.Status}");
                    Pair("采集数据", page.Current
                        ? $"{latest.Report!.Status} · {latest.Report.RuleFailureCount} 次规则失败 · {NodeCount(latest.Report)}" : null);
                    Pair("导航／扫描用时", $"{Seconds(latest.Attempt.NavigationMilliseconds)} / {Seconds(latest.Attempt.ScanMilliseconds)}");
                }
                html.Append("</dl>");
                Links(page.Latest);
                html.Append("<br><a href=\"#history-").Append(page.Index).Append("\">所有尝试与历史证据</a></details></td></tr>");
            }
            TableEnd();
            if (missingPages > 0)
                html.Append("<p class=\"notice\">清单中有 ").Append(missingPages).Append(" 页缺少状态记录，作为待采集保留，不视为成功。</p>");
            html.Append("</section>");
        }

        private void History()
        {
            html.Append("<section id=\"history\"><h2>逐页尝试历史</h2><p>旧报告仅代表过去的一次采集。即使旧尝试成功，受阻或未完成的最新尝试仍是当前状态。</p>");
            foreach (var page in pages)
            {
                html.Append("<h3 id=\"history-").Append(page.Index).Append("\">").Append(H(page.State.Page.Name))
                    .Append(" · ").Append(H(page.State.Page.Id)).Append("</h3><details><summary>尝试记录 · ")
                    .Append(page.Attempts.Count).Append("</summary>");
                if (page.Attempts.Count == 0)
                    html.Append("<p>尚无尝试记录。</p>");
                foreach (var attempt in page.Attempts)
                {
                    html.Append("<h4>尝试 ").Append(attempt.Attempt.Number)
                        .Append(ReferenceEquals(attempt, page.Latest) ? "（最新）" : "（历史，不计入当前汇总）").Append("</h4><dl>");
                    Pair("状态记录", attempt.Attempt.Status);
                    Pair("目录（运行目录相对路径）", attempt.Attempt.Directory);
                    Pair("开始／结束 UTC", $"{At(attempt.Attempt.StartedAtUtc)} / {At(attempt.Attempt.FinishedAtUtc)}");
                    Pair("尝试用时", Elapsed(attempt.Attempt.StartedAtUtc, attempt.Attempt.FinishedAtUtc));
                    Pair("导航／扫描用时（记录值）", $"{Seconds(attempt.Attempt.NavigationMilliseconds)} / {Seconds(attempt.Attempt.ScanMilliseconds)}");
                    Pair("采集前选择／标题核对", ReadyText(attempt.Attempt.Before));
                    Pair("采集后选择／标题核对", ReadyText(attempt.Attempt.After));
                    Pair("采集状态摘要（以校验后的 scan 为准）", attempt.Attempt.CaptureStatus);
                    Pair("绑定 scan.json SHA256", attempt.Attempt.ScanSha256);
                    Pair("证据校验", attempt.Report is null ? "不可用／未校验" : "scan.json 与所列证据的哈希和长度一致");
                    if (attempt.Error is not null)
                        Pair("证据限制", attempt.Error);
                    if (attempt.Attempt.Error is not null)
                        Pair("尝试错误", attempt.Attempt.Error);
                    html.Append("</dl>");
                    Links(attempt);
                }
                html.Append("</details>");
            }
            html.Append("</section>");
        }

        private void Segments()
        {
            html.Append("<section id=\"segments\"><h2>执行段、暂停与恢复</h2><p>恢复时的自动绑定检查与操作者的声明是两件事：进程／窗口、版本、工具、环境和配置档案可由执行器核对；应用设置未变不能从 PID 推断。本静态页面不重新执行这些检查。</p>");
            if (state.Invocations.Count == 0)
                html.Append("<p>未记录执行段；无法确认自动检查、操作者声明或导航还原是否发生。</p>");
            foreach (var invocation in state.Invocations.OrderBy(invocation => invocation.Number))
            {
                html.Append("<article><h3>执行段 ").Append(invocation.Number)
                    .Append(invocation.Resuming ? " · 恢复" : " · 首次执行").Append("</h3><dl>");
                Pair("执行段状态", invocation.Status);
                Pair("开始／结束 UTC", $"{At(invocation.StartedAtUtc)} / {At(invocation.FinishedAtUtc)}");
                Pair("执行段用时", Elapsed(invocation.StartedAtUtc, invocation.FinishedAtUtc));
                Pair("操作者“应用设置未变”声明", invocation.OperatorConfirmedUnchangedState
                    ? "已声明（操作者确认，不是自动核验）" : "未声明（不能据此推断设置未变）");
                Pair("请求重试页面", invocation.RetryPages.Count == 0 ? "未请求" : string.Join("；", invocation.RetryPages));
                Pair("暂停／停止原因", invocation.Reason ?? "未记录");
                Pair("导航还原结果", Restoration(invocation.Restoration));
                if (invocation.Performance is { } performance)
                    Pair("执行器记录的调用与用时", $"{performance.NativeCommands} 次工具调用 · 工具调用 {Seconds(performance.NativeMilliseconds)} · 审计 {Seconds(performance.AuditMilliseconds)}");
                html.Append("</dl></article>");
            }
            html.Append("<p class=\"muted\">工具调用包含原生 UI 命令与 Axe 工作进程；其用时与审计用时重叠，不能相加。用时是本次记录，不是性能基准或速度承诺。缺少结束时间的执行段不计入已结束执行段总用时。</p></section>");
        }

        private void Traceability()
        {
            html.Append("<section id=\"traceability\"><h2>自动核对的绑定条件与追溯信息</h2><p>以下是运行记录，不是新的实时核验，也不能代替“应用设置未变”的操作者声明。哈希用于检测已保存字节的变化，不是签名。</p><dl>");
            Pair("配置档案名称", state.Name);
            Pair("运行 ID／修订", $"{state.RunId} · {state.Revision}");
            Pair("开始时间 UTC", At(state.StartedAtUtc));
            Pair("配置档案 SHA256", state.ProfileSha256);
            Pair("页面清单 SHA256", state.InventorySha256);
            Pair("绑定进程／窗口", state.Target is { } target ? $"{target.ProcessName} · PID {target.ProcessId} · HWND {target.WindowHandle}" : null);
            Pair("进程启动时间 UTC", At(state.Target?.ProcessStartedAtUtc));
            Pair("窗口标题／根 RuntimeId", state.Target is { } window ? $"{window.Title ?? "未提供"} · {window.RootRuntimeId ?? "未提供"}" : null);
            var application = state.Context?.Application;
            Pair("可执行文件路径", application?.ExecutablePath);
            Pair("应用进程名／产品版本", application is null ? null : $"{application.ProcessName} · {application.ProductVersion}");
            Pair("可执行文件 SHA256", application?.ExecutableSha256);
            Pair("伴随文件 SHA256", application?.CompanionSha256 ?? "未记录／不适用");
            Pair("WinAppCLI 版本", state.Tools?.WinappVersion);
            Pair("原生工具 SHA256", state.Tools?.NativeSha256);
            Pair("执行器 SHA256", state.Tools?.RunnerSha256);
            var environment = state.Context?.Environment;
            Pair("环境", environment is null ? null :
                $"{environment.WindowWidth} × {environment.WindowHeight} · DPI {environment.Dpi}\nOS {environment.OsVersion} · UI culture {environment.UiCulture}\nHigh contrast {environment.HighContrast} · AppsUseLightTheme {environment.AppsUseLightTheme?.ToString() ?? "未知"}");
            Pair("截图选项", state.Screenshot ? "请求单页截图（是否采集成功见每页证据）" : "未请求截图");
            var original = state.OriginalNavigation ?? state.Inventory?.OriginalNavigation;
            Pair("首次记录的所选页面", original?.SelectedPageId ?? "未记录");
            Pair("首次记录的分组展开状态", original is null ? "未记录" :
                string.Join("；", original.ExpandedGroups.Select(pair => $"{pair.Key}: {(pair.Value ? "展开" : "收起")}")));
            html.Append("</dl>");
            if (state.Inventory?.Excluded is { Count: > 0 } excluded)
            {
                Table("清单中不进入的项目", "ID", "名称", "排除原因");
                foreach (var item in excluded)
                {
                    html.Append("<tr>");
                    Cell(item.Id);
                    Cell(item.Name);
                    Cell(item.Reason);
                    html.Append("</tr>");
                }
                TableEnd();
            }
            html.Append("</section>");
        }

        private void Links(AttemptView? attempt)
        {
            if (attempt?.ReportUrl is { } reportUrl)
                html.Append("<a href=\"").Append(H(reportUrl)).Append("\">完整单页报告</a> · ");
            if (attempt?.ScanUrl is { } scanUrl)
                html.Append("<a href=\"").Append(H(scanUrl)).Append("\" download>scan.json</a>");
            else
                html.Append("暂无可校验的报告／扫描链接");
        }

        private void ResultLink(PageView page, int index, string label) =>
            html.Append("<a href=\"").Append(H(page.Latest!.ReportUrl)).Append("#result-")
                .Append(index).Append("\">").Append(H(label)).Append("</a>");

        private void Metric(string id, string label, int count, string suffix = "") =>
            html.Append("<li>").Append(H(label)).Append("：<strong id=\"").Append(id).Append("\">")
                .Append(count).Append("</strong>").Append(H(suffix)).Append("</li>");

        private void Pair(string label, string? value) =>
            html.Append("<dt>").Append(H(label)).Append("</dt><dd>").Append(H(value ?? "未提供／未知")).Append("</dd>");

        private void Cell(string? value) => html.Append("<td>").Append(H(value ?? "未提供／未知")).Append("</td>");

        private void Table(string caption, params string[] columns)
        {
            html.Append("<div class=\"table-scroll\" role=\"region\" tabindex=\"0\" aria-label=\"").Append(H(caption))
                .Append("\"><table><caption>").Append(H(caption)).Append("</caption><thead><tr>");
            foreach (var column in columns)
                html.Append("<th scope=\"col\">").Append(H(column)).Append("</th>");
            html.Append("</tr></thead><tbody>");
        }

        private void TableEnd() => html.Append("</tbody></table></div>");
    }

    private static bool SameTarget(AuditTarget? expected, AuditTarget actual) => expected is not null
        && expected.ProcessId == actual.ProcessId && expected.WindowHandle == actual.WindowHandle
        && expected.ProcessStartedAtUtc == actual.ProcessStartedAtUtc
        && string.Equals(expected.ProcessName, actual.ProcessName, StringComparison.OrdinalIgnoreCase)
        && (expected.RootRuntimeId is null || AuditRules.SameRuntimeId(expected.RootRuntimeId, actual.RootRuntimeId));

    private static bool Unavailable(Exception exception) =>
        exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private static string H(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static string RelativeUrl(string path) => "./" + string.Join('/',
        AuditFiles.NormalizeArtifactPath(path).Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString));
    private static JsonElement Field(JsonElement? value, string name) => value is { } element ? AuditFiles.Property(element, name) : default;
    private static string? Text(JsonElement value, string name) => Field(value, name) is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;
    private static IEnumerable<JsonElement> Items(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
    private static string NodeCount(AuditReport report) => Field(report.Snapshot, "nodes") is { ValueKind: JsonValueKind.Array } nodes
        ? $"{nodes.GetArrayLength()} 节点" : "未提供节点数据";
    private static string? Runtime(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
    private static string At(DateTimeOffset? value) => value is null || value == default(DateTimeOffset) ? "未记录" : value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static string Seconds(decimal milliseconds) => milliseconds < 0 ? "未知（无效用时）" : (milliseconds / 1000).ToString("0.###", CultureInfo.InvariantCulture) + " 秒";
    private static string Elapsed(DateTimeOffset start, DateTimeOffset? end) => start == default || end is null || end < start
        ? "未记录有效的结束时间" : Seconds((decimal)(end.Value - start).TotalMilliseconds);
    private static string ReadyText(SurveyPageReady? ready) => ready is null ? "未记录／未核对"
        : $"选择 {(ready.Selected ? "已核对" : "未确认")} · 标题 {(ready.HeadingMatched ? "匹配" : "未匹配")} · {ready.Heading ?? "标题未提供"} · {At(ready.ObservedAtUtc)}";
    private static string Restoration(SurveyRestoration? restoration) => restoration is null
        ? "未记录；不能确认已还原导航状态" : $"{restoration.Status} · {restoration.Message}";
    private static string RunStatus(string status) => status switch
    {
        "created" => "已创建，尚未采集", "discovering" => "正在发现页面", "running" => "正在巡检",
        "paused" => "已暂停", "finished" => "执行已结束（不是合规结论）", _ => "未知执行状态"
    };
    private static string PageStatus(string status) => status switch
    {
        "captured" => "导航核对且报告已采集", "pending" => "待采集", "running" => "进行中",
        "blocked" => "受阻／未完成", _ => "状态或证据未核验"
    };
}

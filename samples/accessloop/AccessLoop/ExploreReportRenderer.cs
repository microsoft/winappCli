using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AccessLoop;

public static class ExploreReportRenderer
{
    public static void Write(string path, ExploreReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var root = Path.GetDirectoryName(Path.GetFullPath(path))!;
        AuditFiles.EnsureDirectory(root);
        var html = new Page(root, report).Render();
        using var stream = AuditFiles.OpenNew(path);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(html);
    }

    private sealed record StateView(int Index, ExploreStateResult State, AuditReport? Scan, string? Error)
    {
        public bool Verified => Scan is not null && Error is null;
        public string Anchor => "state-" + Index.ToString(CultureInfo.InvariantCulture);
        public string ReportUrl => RelativeUrl(Path.Combine(State.Directory, "report.html"));
    }

    private sealed record PageView(int Index, ExplorePage Definition, ExplorePageResult? Result, bool Unique)
    {
        public string Anchor => "page-" + Index.ToString(CultureInfo.InvariantCulture);
    }

    private sealed class Page
    {
        private readonly ExploreReport report;
        private readonly StringBuilder html = new();
        private readonly PageView[] pages;
        private readonly StateView[] states;
        private readonly StateView[] verified;
        private readonly ExplorePageResult[] unassociatedPages;
        private readonly int reached;
        private readonly bool validProfile;

        public Page(string root, ExploreReport report)
        {
            this.report = report;
            var definitions = report.Profile?.Pages ?? [];
            validProfile = report.SchemaVersion == 1 && report.Profile?.SchemaVersion == 1
                && definitions.Count > 0 && report.Profile.MaxStates is > 0 and <= 64
                && report.Profile.MaxDurationSeconds is > 0 and <= 1800
                && string.Equals(report.Profile.ProcessName, report.Target?.ProcessName, StringComparison.OrdinalIgnoreCase);
            pages = definitions.Select((definition, index) =>
            {
                var matches = report.Pages.Where(page => page.PageId == definition.Id).ToArray();
                var unique = !string.IsNullOrWhiteSpace(definition.Id)
                    && definitions.Count(page => page.Id == definition.Id) == 1;
                return new PageView(index, definition, matches.Length == 1 ? matches[0] : null, unique);
            }).ToArray();
            unassociatedPages = report.Pages.Where(result => pages.Count(page =>
                page.Unique && ReferenceEquals(page.Result, result)) != 1).ToArray();
            states = report.States.Select((state, index) => ReadState(root, state, index)).ToArray();
            verified = states.Where(state => state.Verified).ToArray();
            reached = pages.Count(page => page.Unique && page.Result?.NavigationStatus == "captured"
                && verified.Any(state => state.State.PageId == page.Definition.Id));
        }

        private StateView ReadState(string root, ExploreStateResult state, int index)
        {
            try
            {
                var directory = AuditFiles.NormalizeArtifactPath(state.Directory);
                if (!validProfile || !pages.Any(page => page.Unique && page.Definition.Id == state.PageId
                    && page.Result?.NavigationStatus == "captured"))
                    throw new InvalidDataException("页面不在唯一的声明清单中，或缺少已到达的导航记录。");
                if (index >= report.Profile.MaxStates)
                    throw new InvalidDataException("该状态超出声明的状态数量预算；保留记录，但不计作范围内覆盖。");
                if (string.IsNullOrWhiteSpace(state.Id) || report.States.Count(item => item.Id == state.Id) != 1
                    || report.States.Count(item => SameDirectory(item.Directory, directory)) != 1)
                    throw new InvalidDataException("状态 ID 或证据目录重复／缺失；不能把同一记录重复计数。");
                if (state.Status != "captured" || !string.IsNullOrEmpty(state.Error))
                    throw new InvalidDataException("状态未确认采集；不使用已有文件推断成功。");
                AuditFiles.ValidateHash(state.ScanSha256, "state scan SHA256");
                var document = AuditFiles.ReadVerifiedDocument(
                    AuditFiles.ResolveArtifact(root, Path.Combine(directory, "scan.json")));
                if (!string.Equals(document.Sha256, state.ScanSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("scan.json SHA256 与该状态保存的指纹不一致。");
                var scan = document.Report;
                if (scan.Status is not ("completed" or "partial") || !string.IsNullOrEmpty(scan.Error))
                    throw new InvalidDataException("扫描采集受阻；不能计作已核验的已采集状态。");
                if (scan.Snapshot is null or { ValueKind: JsonValueKind.Null } || scan.Axe is null)
                    throw new InvalidDataException("扫描缺少界面快照或 Axe 原始记录；不能确认该视图状态已完成采集。");
                var page = pages.Single(page => page.Unique && page.Definition.Id == state.PageId).Definition;
                ValidateBinding(report, page, state, scan);
                if (string.IsNullOrWhiteSpace(report.Tools?.WinappVersion)
                    || report.Tools.WinappVersion != scan.WinappVersion)
                    throw new InvalidDataException("扫描工具版本与本次探索记录不一致。");
                if (state.StartedAtUtc == default || state.FinishedAtUtc is null
                    || state.FinishedAtUtc < state.StartedAtUtc
                    || scan.StartedAtUtc < state.StartedAtUtc || scan.FinishedAtUtc > state.FinishedAtUtc)
                    throw new InvalidDataException("扫描时间不在该状态的有效采集时间内。");
                if (report.StartedAtUtc == default || state.StartedAtUtc < report.StartedAtUtc
                    || (scan.FinishedAtUtc - report.StartedAtUtc).TotalSeconds > report.Profile.MaxDurationSeconds
                    || report.FinishedAtUtc is { } finish && state.FinishedAtUtc > finish)
                    throw new InvalidDataException("该扫描不在本次运行的声明时间预算内。");
                AuditFiles.ResolveArtifact(root, Path.Combine(directory, "report.html"));
                return new(index, state, scan, null);
            }
            catch (Exception exception) when (Unavailable(exception))
            {
                return new(index, state, null, exception.Message);
            }
        }

        public string Render()
        {
            html.Append("""
                <!doctype html>
                <html lang="zh-CN"><head><meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'">
                <title>AccessLoop 多状态无障碍探索报告</title>
                <style>
                :root{color-scheme:light dark;font-family:system-ui,"Microsoft YaHei",sans-serif;line-height:1.6}
                body{margin:0 auto;max-width:96rem;padding:1.5rem;background:#fff;color:#202124}
                a{color:#0753a0}a:visited{color:#60358d}:focus-visible,:target{outline:3px solid #0753a0;outline-offset:3px}
                .skip{position:absolute;left:-10000px}.skip:focus{left:1rem;top:1rem;background:#fff;padding:1rem}
                nav,.counts{display:flex;flex-wrap:wrap;gap:1rem}.counts{list-style:none;padding:0}
                .counts li{border:1px solid #b7bcc4;padding:.5rem}section{margin-block:2rem;border-block-start:1px solid #b7bcc4;padding-block-start:1rem}
                h1,h2,h3{line-height:1.3;overflow-wrap:anywhere}.notice{border:1px solid #b7bcc4;border-inline-start:5px solid #956000;padding:.75rem;background:#f4f6fa}
                table{border-collapse:collapse;width:100%;text-align:start}th,td{padding:.55rem;border:1px solid #b7bcc4;vertical-align:top;overflow-wrap:anywhere}
                th{background:#edf0f5}caption{text-align:start;font-weight:600;margin:.5rem 0}.table-scroll{overflow:auto}
                dl{display:grid;grid-template-columns:minmax(9rem,18rem) minmax(0,1fr);gap:.3rem 1rem}
                dt{font-weight:600}dd{margin:0;white-space:pre-wrap;overflow-wrap:anywhere}pre{white-space:pre-wrap;overflow-wrap:anywhere}
                summary{cursor:pointer;padding:.45rem}summary:hover{background:#edf0f5}
                .report-detail{border:1px solid #b7bcc4;border-radius:.3rem;padding:.45rem .75rem;margin-block:.75rem}
                .report-detail>summary{font-weight:600}.report-detail>section{border:0;margin-block:1rem;padding:0}
                .muted{color:#495462}
                @media(max-width:45rem){body{padding:.75rem}dl{display:block}dd{margin-block-end:.5rem}}
                @media(prefers-color-scheme:dark){body{background:#16191e;color:#edf0f5}a{color:#a8d1ff}a:visited{color:#dbc0ff}
                .skip:focus{background:#16191e}.notice,th,summary:hover{background:#242a33}.muted{color:#c3c9d1}.notice{border-inline-start-color:#e5b952}}
                </style></head><body><a class="skip" href="#main">跳到报告正文</a>
                <header><h1>AccessLoop 多状态无障碍探索报告</h1>
                """);
            html.Append("<p>").Append(H(report.Name)).Append("</p>");
            html.Append("""
                <nav aria-label="探索报告章节"><a href="#progress">探索摘要</a><a href="#pages">页面覆盖</a>
                <a href="#states">视图状态与截图</a><a href="#safety">范围与还原</a><a href="#details">证据与执行详情</a></nav>
                </header><main id="main">
                """);
            Progress();
            Pages();
            States();
            Safety();
            html.Append("<section id=\"details\"><h2>证据与执行详情</h2>");
            ReportPresentation.Details(html, "逐状态的前后见证与证据指纹", StateDetails);
            ReportPresentation.Details(html, "交互前后见证与还原记录", InteractionDetails);
            ReportPresentation.Details(html, "范围事件与停止原因原始记录", Events);
            ReportPresentation.Details(html, "运行绑定、配置档案与详细耗时", RunDetails);
            html.Append("""
                </section></main><footer>
                <p>本页只读取保存的本地证据，不连接应用或外部服务，不执行操作。
                各状态的 report.html 保留问题卡片、控件图片、全部观察和覆盖缺口；本概览不复制所有截图。</p>
                <p>生成概览时核验 scan.json 的绑定 SHA256，以及扫描所列文件的本地路径、哈希和长度。
                report.html 仅是本地展示链接，没有独立绑定的哈希或签名。重新打开静态页面不会重新校验外部文件。
                指纹不能证明作者身份，也不是数字签名。名称、截图和原始记录仍可能包含敏感信息，分享前请检查。</p>
                </footer></body></html>
                """);
            return html.ToString();
        }

        private void Progress()
        {
            html.Append("<section id=\"progress\"><h2>探索摘要</h2><p><strong>")
                .Append(H(RunStatus(report.Status))).Append("</strong> · ").Append(H(report.Status))
                .Append("</p><ul class=\"counts\">");
            Metric("reached-pages", "已到达声明页面", reached, $" / {pages.Length} 页");
            Metric("verified-states", "已核验采集", verified.Length, $" / {states.Length} 个视图状态");
            Metric("failure-occurrences", "规则失败出现次数", verified.Sum(state => state.Scan!.RuleFailureCount));
            html.Append("</ul><p>总用时：<strong>").Append(H(Elapsed(report.StartedAtUtc, report.FinishedAtUtc)))
                .Append("</strong>。页面与视图状态不是一回事：同一页的滚动位置、展开下拉和打开对话框分别记录。</p>")
                .Append("<p>审计执行结束不等于 WCAG 通过。未知、未测试和不支持的内容不计作通过。")
                .Append("本工作流未执行源码修复，也未主动变更下拉选项。</p>");
            html.Append("<div class=\"notice\"><strong>覆盖与停止提示</strong><ul>");
            html.Append("<li>当前范围仅限原始窗口中的声明页面与控件；独立弹窗仍属未核验范围。</li>");
            var partial = verified.Count(state => state.Scan!.Status == "partial");
            if (partial > 0)
                html.Append("<li>").Append(partial).Append(" 个状态为部分采集（partial），不是完整覆盖。</li>");
            if (verified.Length == 0)
                html.Append("<li>没有可核验的已采集状态；0 次规则失败不是未发现问题或应用通过。</li>");
            if (states.Any(state => !state.Verified))
                html.Append("<li>").Append(states.Count(state => !state.Verified))
                    .Append(" 个状态待采集、受阻、未测试或证据未核验；没有使用缓存计数。</li>");
            if (!validProfile || reached != pages.Length || pages.Length == 0 || unassociatedPages.Length > 0
                || pages.Any(page => !page.Unique || page.Result is null))
                html.Append("<li>声明页面尚未全部有核验记录，或页面关联不完整／不唯一；0 / 0 不表示完成。</li>");
            foreach (var page in pages.Where(page => page.Definition.Scroll is not null && !EndObserved(page)))
                html.Append("<li>").Append(H(page.Definition.Name)).Append("：").Append(H(ScrollText(page))).Append("</li>");
            if (report.Profile is { } profile)
            {
                html.Append("<li>有界范围：最多 ").Append(profile.MaxStates).Append(" 个状态，最多 ")
                    .Append(H(ReportPresentation.Duration((decimal)profile.MaxDurationSeconds * 1000))).Append("。");
                if (profile.MaxStates <= 0 || profile.MaxDurationSeconds <= 0)
                    html.Append("预算无效；不能确认覆盖范围。");
                else if (states.Length >= profile.MaxStates || report.FinishedAtUtc is { } end
                    && end >= report.StartedAtUtc && (end - report.StartedAtUtc).TotalSeconds >= profile.MaxDurationSeconds)
                    html.Append("已达到或超过预算；未观察内容仍属未测试，不外推为完整覆盖。");
                else
                    html.Append("未达到预算也不证明覆盖了所有内容。");
                html.Append("</li>");
            }
            if (report.Restoration?.Status is not ("restored-navigation" or "restored"))
                html.Append("<li>整体还原未确认：").Append(H(RestorationText(report.Restoration))).Append("</li>");
            else if (report.GuardProbe?.Status != "not-requested")
                html.Append("<li>普通探索阶段有还原记录；")
                    .Append(report.GuardProbe?.Status == "stopped" ? "探针停止后不再还原，"
                        : "探针是否改变最终状态不能由此前还原记录确认，")
                    .Append("此前记录不能证明最终页面／窗口已恢复。</li>");
            else if (report.Restoration.Status == "restored-navigation")
                html.Append("<li>原导航已还原；控件还原逐项见页面行，不保证焦点或应用数据回滚。</li>");
            var incompleteRestores = report.Pages.Sum(page => page.Interactions.Count(interaction => !InteractionRestored(interaction)));
            if (incompleteRestores > 0)
                html.Append("<li>").Append(incompleteRestores)
                    .Append(" 条交互记录没有已确认的完整还原；见页面行中的原因。</li>");
            if (report.Pages.SelectMany(page => page.Interactions).Any(SelectionChanged))
                html.Append("<li>前后选择见证不一致；不能宣称选择状态已保持或还原。</li>");
            if (report.GuardProbe?.Status != "not-requested")
                html.Append("<li>范围探针：").Append(H(GuardText())).Append("</li>");
            foreach (var item in report.Events.Where(item => IsWarningEvent(item.Kind)))
                html.Append("<li>").Append(H(item.Kind)).Append("：").Append(H(item.Message)).Append("</li>");
            if (!string.IsNullOrWhiteSpace(report.Error))
                html.Append("<li>").Append(H(report.Error)).Append("</li>");
            html.Append("</ul></div><p>规则失败按每次扫描的原始出现次数累加，不声称独立根因数。")
                .Append("<a href=\"#states\">打开各状态的问题卡片与图片</a>。</p></section>");
        }

        private void Pages()
        {
            html.Append("<section id=\"pages\"><h2>页面覆盖 · ").Append(pages.Length)
                .Append(" 个声明页面</h2><p>已到达只表示声明页有匹配的导航记录和已核验就绪状态，不表示该页所有交互都已覆盖。</p>");
            Table("声明页面与有界交互", "页面／导航", "基础视图", "滚动", "下拉／对话框及还原");
            foreach (var page in pages)
            {
                var basis = VerifiedFor(page, "base").ToArray();
                html.Append("<tr id=\"").Append(page.Anchor).Append("\"><th scope=\"row\">")
                    .Append(H(page.Definition.Name)).Append("</th><td>");
                html.Append(basis.Length == 0 ? "未核验采集" : $"已核验 {basis.Length} 个基础视图");
                foreach (var view in basis)
                {
                    html.Append("<br>");
                    Link("#" + view.Anchor, "查看基础视图");
                }
                html.Append("</td>");
                html.Append("<td>").Append(H(ScrollText(page)));
                if (page.Definition.Scroll is { } scroll)
                {
                    var records = page.Result?.Interactions.Where(item => item.Kind == "scroll"
                        && item.ControlId == scroll.ContainerId).ToArray() ?? [];
                    foreach (var interaction in records)
                        InteractionSummary(interaction);
                    if (records.Length != 1)
                        html.Append("<p>滚动交互记录缺失或重复；还原未确认。</p>");
                }
                html.Append("</td>");
                html.Append("<td>");
                DeclaredInteractions(page);
                html.Append("</td></tr><tr><td colspan=\"4\">导航：")
                    .Append(H(NavigationStatus(page.Result?.NavigationStatus)));
                if (!page.Unique || page.Result is null)
                    html.Append("；声明／结果缺失或重复，不能确认关联");
                Reason(page.Result?.Error);
                Reason(page.Result?.ScrollReason);
                html.Append("</td></tr>");
            }
            TableEnd();
            if (pages.Length == 0)
                html.Append("<p class=\"notice\">没有声明页面；范围未知，不是已完成。</p>");
            if (unassociatedPages.Length > 0)
            {
                html.Append("<div class=\"notice\"><h3>未关联的页面记录</h3>");
                foreach (var page in unassociatedPages)
                {
                    html.Append("<p>").Append(H(page.Name)).Append(" · ").Append(H(page.PageId))
                        .Append("：").Append(H(NavigationStatus(page.NavigationStatus)))
                        .Append("；滚动记录：").Append(H(page.ScrollStatus));
                    Reason(page.Error);
                    Reason(page.ScrollReason);
                    html.Append("</p>");
                    foreach (var interaction in page.Interactions)
                        InteractionSummary(interaction);
                }
                html.Append("</div>");
            }
            html.Append("</section>");
        }

        private void DeclaredInteractions(PageView page)
        {
            var definition = page.Definition;
            for (var index = 0; index < definition.Dropdowns.Count; index++)
                DeclaredInteraction(page, "dropdown", definition.Dropdowns[index].ControlId, $"下拉 {index + 1}");
            for (var index = 0; index < definition.Dialogs.Count; index++)
                DeclaredInteraction(page, "dialog", definition.Dialogs[index].RootId, $"对话框 {index + 1}");
            if (definition.Dropdowns.Count + definition.Dialogs.Count == 0)
                html.Append("<p>未声明下拉或对话框。</p>");
            foreach (var interaction in page.Result?.Interactions ?? [])
            {
                var declared = interaction.Kind switch
                {
                    "scroll" => definition.Scroll?.ContainerId == interaction.ControlId,
                    "dropdown" => definition.Dropdowns.Count(item => item.ControlId == interaction.ControlId) == 1,
                    "dialog" => definition.Dialogs.Count(item => item.RootId == interaction.ControlId) == 1,
                    _ => false
                };
                if (!declared)
                {
                    html.Append("<p>未声明／关联不唯一的交互，不计作覆盖。</p>");
                    InteractionSummary(interaction);
                }
            }
        }

        private void DeclaredInteraction(PageView page, string kind, string control, string label)
        {
            var interactions = page.Result?.Interactions.Where(item => item.Kind == kind && item.ControlId == control).ToArray() ?? [];
            var captured = VerifiedFor(page, kind).Count(state => state.State.Before?.ControlId == control);
            html.Append("<p><strong>").Append(H(label)).Append("</strong>：").Append(captured)
                .Append(" 个已核验状态");
            if (interactions.Length != 1)
                html.Append("；交互记录缺失或重复，操作与还原未确认");
            html.Append("</p>");
            foreach (var interaction in interactions)
                InteractionSummary(interaction);
        }

        private void InteractionSummary(ExploreInteractionResult interaction)
        {
            html.Append("<p>").Append(H(StateStatus(interaction.Status))).Append("；还原：")
                .Append(H(RestorationText(interaction.Restoration)));
            Reason(interaction.Reason);
            if (SelectionChanged(interaction))
                html.Append("<br>选择状态前后不一致；不能确认保持原选择。");
            html.Append("</p>");
        }

        private void States()
        {
            html.Append("<section id=\"states\"><h2>视图状态与截图 · ").Append(states.Length)
                .Append(" 条记录</h2><p>下列计数只来自通过文件与范围核验的扫描。缺失、改变、受阻或未知状态显示为未核验，不记作 0 次违规。</p>");
            Table("逐状态采集结果", "页面／视图", "状态与证据", "规则失败出现次数", "完整报告");
            foreach (var view in states)
            {
                var state = view.State;
                var page = pages.FirstOrDefault(page => page.Unique && page.Definition.Id == state.PageId);
                html.Append("<tr id=\"").Append(view.Anchor).Append("\"><th scope=\"row\">");
                if (page is not null)
                    Link("#" + page.Anchor, page.Definition.Name);
                else
                    html.Append(H(state.PageId)).Append("（未关联声明页面）");
                html.Append("<br>").Append(H(state.Name)).Append(" · ").Append(H(Kind(state.Kind))).Append("</th><td>")
                    .Append(H(StateStatus(state.Status))).Append("<br>")
                    .Append(H(view.Verified ? ReportPresentation.CaptureStatus(view.Scan!.Status) + "；证据已核验" : "未核验／不计入可信汇总"));
                Reason(state.Error);
                Reason(view.Error);
                if (!string.IsNullOrWhiteSpace(view.Scan?.Error))
                    Reason(view.Scan.Error);
                html.Append("</td>");
                Cell(view.Verified ? view.Scan!.RuleFailureCount.ToString(CultureInfo.InvariantCulture) : "未知（不作零次）");
                html.Append("<td>");
                if (view.Verified)
                    Link(view.ReportUrl, "打开该状态报告与图片");
                else
                    html.Append("没有可核验的报告链接");
                html.Append("<br>");
                Link("#state-details-" + view.Index.ToString(CultureInfo.InvariantCulture), "前后见证与原始记录");
                html.Append("</td></tr>");
            }
            TableEnd();
            if (states.Length == 0)
                html.Append("<p>尚无视图状态记录；未采集不是通过。</p>");
            html.Append("</section>");
        }

        private void Safety()
        {
            html.Append("<section id=\"safety\"><h2>范围与还原（不是无障碍发现）</h2><p id=\"guard-observation\">")
                .Append(H(GuardText())).Append("</p><p>探针只观察越界后是否停止。")
                .Append("stopped 加 0 次后续动作仅是安全观察，不是无障碍通过；没有边界事件不能当作成功探针。</p><dl>");
            Pair("探针状态", report.GuardProbe?.Status);
            Pair("探针原因", report.GuardProbe?.Reason);
            Pair("边界之后的动作数（记录值）", report.GuardProbe?.ActionsAfterBoundaryViolation.ToString(CultureInfo.InvariantCulture));
            Pair("整体还原", RestorationText(report.Restoration));
            html.Append("</dl><p>还原记录不改变规则失败数。受阻时不为完成还原而继续越界操作；")
                .Append("还原未确认、仅导航还原和未知状态都不表示应用已回到完整初始状态。")
                .Append("普通探索的还原记录也不代表随后触发范围探针之后的最终窗口状态。")
                .Append("关闭由本工作流启动的进程属于清理，不是界面还原。</p></section>");
        }

        private string GuardText()
        {
            var probe = report.GuardProbe;
            if (probe is null)
                return "未记录范围探针；没有可核验的安全观察。";
            if (probe.Status == "not-requested")
                return "未请求范围探针（未测试）。";
            if (probe.Status == "stopped")
            {
                var boundary = report.OwnedApplication && report.Target is { } target && report.Profile?.ScopeProbe is { } declared
                    && pages.Count(page => page.Unique && page.Definition.Id == declared.PageId) == 1
                    && report.Events.Any(item => item.Kind == "window-boundary-changed"
                        && item.AtUtc >= report.StartedAtUtc && item.AtUtc != default
                        && (report.FinishedAtUtc is null || item.AtUtc <= report.FinishedAtUtc)
                        && item.ProcessId == target.ProcessId
                        && item.WindowHandle is > 0 && item.WindowHandle != target.WindowHandle);
                if (!boundary)
                    return "已记录 stopped，但缺少与声明探针相符的边界事件；探针未核验。";
                return probe.ActionsAfterBoundaryViolation == 0
                    ? "已观察到边界后停止，后续动作 0 次；仅为范围保护的安全观察。"
                    : "范围保护未确认：边界后的动作数不是 0；请检查原始事件。";
            }
            return probe.Status switch
            {
                "no-boundary" => "未观察到边界；不能确认探针触发了范围保护。",
                "blocked" => "探针受阻／未完成；不能确认范围保护观察。",
                "not-tested" => "范围探针未测试。",
                _ => "未知探针状态：" + probe.Status + "；不计作成功观察。"
            };
        }

        private void StateDetails()
        {
            foreach (var view in states)
            {
                var state = view.State;
                html.Append("<h3 id=\"state-details-").Append(view.Index).Append("\">").Append(H(state.Name)).Append("</h3><dl>");
                Pair("状态 ID／页面 ID", $"{state.Id} / {state.PageId}");
                Pair("类型／原始状态", $"{state.Kind} / {state.Status}");
                Pair("证据目录（相对运行目录）", state.Directory);
                Pair("开始／结束 UTC", $"{At(state.StartedAtUtc)} / {At(state.FinishedAtUtc)}");
                Pair("状态用时", Elapsed(state.StartedAtUtc, state.FinishedAtUtc));
                Pair("状态绑定目标", TargetText(state.Target));
                Pair("绑定 scan.json SHA256", state.ScanSha256);
                Pair("核验结果", view.Verified ? "扫描指纹、所列文件、页面关联与目标绑定一致" : view.Error);
                Pair("状态原因", state.Error);
                html.Append("</dl>");
                Observation("采集前见证", state.Before);
                Observation("采集后见证", state.After);
                if (!view.Verified)
                    continue;
                html.Append("<p>");
                Link(RelativeUrl(Path.Combine(state.Directory, "scan.json")), "原始 scan.json");
                html.Append("</p><dl>");
                Pair("扫描运行 ID", view.Scan!.RunId);
                Pair("扫描目标", TargetText(view.Scan.Target));
                Pair("实际采集状态", view.Scan.Status);
                Pair("扫描原因", view.Scan.Error);
                html.Append("</dl>");
                Table("原始证据指纹（核验时）", "文件", "SHA256", "字节数／类型");
                foreach (var artifact in view.Scan.Artifacts)
                {
                    html.Append("<tr>");
                    Cell(artifact.Path);
                    Cell(artifact.Sha256);
                    Cell($"{artifact.Length} / {artifact.MediaType}");
                    html.Append("</tr>");
                }
                TableEnd();
            }
            html.Append("<p>每条状态保留自己的证据，不以其他状态替代缺失或失败的记录，也不把重复状态自动解释为重试成功。")
                .Append("RuntimeId 可能在滚动时复用，不能据此跨状态去重。</p>");
        }

        private void InteractionDetails()
        {
            foreach (var page in report.Pages)
            {
                html.Append("<h3>").Append(H(page.Name)).Append("</h3><dl>");
                Pair("页面 ID", page.PageId);
                Pair("原始导航状态", page.NavigationStatus);
                Pair("原始滚动状态／原因", $"{page.ScrollStatus} / {page.ScrollReason ?? "未记录"}");
                Pair("页面原因", page.Error);
                html.Append("</dl>");
                foreach (var interaction in page.Interactions)
                {
                    html.Append("<h4>").Append(H(Kind(interaction.Kind))).Append("</h4><dl>");
                    Pair("控件 ID", interaction.ControlId);
                    Pair("原始状态", interaction.Status);
                    Pair("原因", interaction.Reason);
                    Pair("还原记录", RestorationText(interaction.Restoration));
                    html.Append("</dl>");
                    Observation("交互前见证", interaction.Before);
                    Observation("交互后见证", interaction.After);
                }
            }
        }

        private void Events()
        {
            html.Append("<p>事件保留记录的时间、类型、进程／窗口与消息。声明档案中的窗口标题是预期值；")
                .Append("此处没有独立保存的实际窗口标题或原生所属窗口（owner）证据，不能把声明值当作实际观察。</p>");
            if (report.Events.Count == 0)
                html.Append("<p>没有事件记录；不推断边界保护已经触发。</p>");
            foreach (var item in report.Events)
            {
                html.Append("<dl>");
                Pair("UTC／事件类型", $"{At(item.AtUtc)} / {item.Kind}");
                Pair("消息", item.Message);
                Pair("PID／HWND", $"{item.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "未记录"} / {item.WindowHandle?.ToString(CultureInfo.InvariantCulture) ?? "未记录"}");
                html.Append("</dl>");
            }
        }

        private void RunDetails()
        {
            html.Append("<dl>");
            Pair("运行 ID／版本", $"{report.RunId} / {report.SchemaVersion}");
            Pair("原始执行状态", report.Status);
            Pair("开始／结束 UTC", $"{At(report.StartedAtUtc)} / {At(report.FinishedAtUtc)}");
            Pair("总用时", Elapsed(report.StartedAtUtc, report.FinishedAtUtc));
            Pair("原始目标绑定", TargetText(report.Target));
            Pair("应用启动方式", report.OwnedApplication ? "由本工作流启动" : "附加既有进程");
            Pair("截图请求", report.Screenshot ? "已请求；是否可用以各状态报告为准" : "未请求截图");
            Pair("配置档案 SHA256", report.ProfileSha256);
            Pair("WinAppCLI 版本", report.Tools?.WinappVersion);
            Pair("原生工具 SHA256", report.Tools?.NativeSha256);
            Pair("执行器 SHA256", report.Tools?.RunnerSha256);
            if (report.Performance is { } performance)
            {
                Pair("原生 UI／Axe 调用次数", performance.NativeCommands >= 0
                    ? performance.NativeCommands.ToString(CultureInfo.InvariantCulture) : "未知（无效次数）");
                Pair("原生 UI／Axe 调用用时", ReportPresentation.Duration(performance.NativeMilliseconds));
                Pair("审计用时", ReportPresentation.Duration(performance.AuditMilliseconds));
            }
            else
                Pair("详细耗时", "未记录");
            html.Append("</dl><p>原生 UI／Axe 调用用时与审计用时重叠，不能相加，也不能替代开始至结束的总用时。")
                .Append("缺少有效结束时间时不估算总时长。本次记录不是性能基准。</p><h3>完整声明范围</h3><pre>")
                .Append(H(JsonSerializer.Serialize(report.Profile, Json.Options))).Append("</pre>");
        }

        private IEnumerable<StateView> VerifiedFor(PageView page, string kind) => verified.Where(state =>
            page.Unique && state.State.PageId == page.Definition.Id && state.State.Kind == kind);

        private bool EndObserved(PageView page) => page.Definition.Scroll is not null && page.Result?.ScrollStatus == "end-observed"
            && VerifiedFor(page, "scroll").Any(state => state.State.After?.ScrollPercent is >= 99.9 and <= 100);

        private string ScrollText(PageView page)
        {
            if (page.Definition.Scroll is null)
                return page.Result?.ScrollStatus is null or "not-requested" ? "未声明滚动（未请求）"
                    : "未声明的滚动记录：" + page.Result.ScrollStatus + "；不计作覆盖";
            return page.Result?.ScrollStatus switch
            {
                "end-observed" => EndObserved(page) ? "末端已观察（end-observed）；不代表所有交互已覆盖"
                    : "记录为 end-observed，但缺少已核验的末端状态；覆盖未确认",
                "limit-reached" => "有界滚动未完成：已到位置／时间预算（limit-reached）",
                "blocked" => "滚动受阻（blocked）；覆盖未完成",
                "pending" => "滚动待执行（pending）；尚未观察末端",
                "not-requested" => "已声明滚动但尚未执行（not-requested）",
                null => "缺少滚动记录；覆盖未确认",
                _ => "未知滚动状态：" + page.Result.ScrollStatus + "；不视为末端已观察"
            };
        }

        private void Observation(string label, ExploreObservation? observation)
        {
            html.Append("<h4>").Append(H(label)).Append("</h4>");
            if (observation is null)
            {
                html.Append("<p>未记录／未核对。</p>");
                return;
            }
            html.Append("<dl>");
            Pair("观察时间 UTC", At(observation.ObservedAtUtc));
            Pair("页面 ID／就绪", $"{observation.PageId} / {(observation.PageReady ? "已就绪" : "未确认")}");
            Pair("HWND", observation.WindowHandle.ToString(CultureInfo.InvariantCulture));
            Pair("控件 ID", observation.ControlId);
            Pair("滚动百分比／视口百分比", $"{Number(observation.ScrollPercent)} / {Number(observation.ViewSizePercent)}");
            Pair("展开状态", observation.ExpansionState);
            Pair("选择 AutomationId", string.Join("；", observation.SelectionAutomationIds));
            Pair("对话框存在", observation.DialogPresent is { } present ? present ? "是" : "否" : "未记录");
            Pair("焦点 AutomationId", observation.FocusAutomationId);
            html.Append("</dl>");
        }

        private void Metric(string id, string label, int count, string suffix = "") => html.Append("<li>")
            .Append(H(label)).Append("：<strong id=\"").Append(id).Append("\">").Append(count)
            .Append("</strong>").Append(H(suffix)).Append("</li>");
        private void Pair(string label, string? value) => html.Append("<dt>").Append(H(label))
            .Append("</dt><dd>").Append(H(value ?? "未记录／未知")).Append("</dd>");
        private void Cell(string? value) => html.Append("<td>").Append(H(value ?? "未记录／未知")).Append("</td>");
        private void Link(string url, string label) => html.Append("<a href=\"").Append(H(url)).Append("\">").Append(H(label)).Append("</a>");
        private void Reason(string? reason)
        {
            if (!string.IsNullOrWhiteSpace(reason))
                html.Append("<br>").Append(H(reason));
        }
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

    private static void ValidateBinding(ExploreReport report, ExplorePage page, ExploreStateResult state, AuditReport scan)
    {
        if (!SameProcess(report.Target, scan.Target) || state.Target is { } bound
            && (!SameProcess(report.Target, bound) || !SameWindow(bound, scan.Target)))
            throw new InvalidDataException("扫描或状态目标的进程、启动时间或窗口与原始绑定不一致。");
        if (scan.Target.WindowHandle != report.Target.WindowHandle)
            throw new InvalidDataException("独立弹窗不在当前同窗口范围内；状态 target 或 popup-verified 事件不扩展声明范围。");
        var before = state.Before;
        var after = state.After;
        if (!Ready(before, state, scan.Target) || !Ready(after, state, scan.Target)
            || after!.ObservedAtUtc < before!.ObservedAtUtc
            || before!.ObservedAtUtc > scan.StartedAtUtc || after!.ObservedAtUtc < scan.FinishedAtUtc
            || before.ControlId != after.ControlId || !Stable(before.ScrollPercent, after.ScrollPercent, 0.1)
            || !Stable(before.ViewSizePercent, after.ViewSizePercent, 0.05)
            || before.ExpansionState != after.ExpansionState || before.DialogPresent != after.DialogPresent
            || !before.SelectionAutomationIds.SequenceEqual(after.SelectionAutomationIds, StringComparer.Ordinal))
            throw new InvalidDataException("缺少匹配页面、就绪状态和扫描窗口的有效前后见证。");
        var dialog = page.Dialogs.Where(item => item.RootId == before!.ControlId
            && item.RootId == after!.ControlId).ToArray();
        var associated = state.Kind switch
        {
            "base" => page.Scroll is null ? before.ControlId is null
                : before.ControlId == page.Scroll.ContainerId && Percent(before.ScrollPercent) && Percent(before.ViewSizePercent),
            "scroll" => page.Scroll is { } scroll && before!.ControlId == scroll.ContainerId
                && after!.ControlId == scroll.ContainerId && Percent(before.ScrollPercent) && Percent(after.ScrollPercent)
                && Percent(before.ViewSizePercent) && Percent(after.ViewSizePercent),
            "dropdown" => page.Dropdowns.Count(item => item.ControlId == before!.ControlId
                && item.ControlId == after!.ControlId && before.ExpansionState == "Expanded"
                && before.SelectionAutomationIds.Count == 1 && item.ItemIds.Count(id => id == before.SelectionAutomationIds[0]) == 1) == 1,
            "dialog" => dialog.Length == 1 && before!.DialogPresent == true && after!.DialogPresent == true
                && dialog[0].Scope == "same-window" && scan.Target.WindowHandle == report.Target.WindowHandle,
            _ => false
        };
        if (!associated)
            throw new InvalidDataException("状态类型或控件前后见证与声明页面的交互不一致。");
        if (!SameWindow(report.Target, scan.Target))
            throw new InvalidDataException("扫描的原始窗口／根身份发生变化。");
    }

    private static bool Ready(ExploreObservation? observation, ExploreStateResult state, AuditTarget target) =>
        observation is { PageReady: true } && observation.ObservedAtUtc != default
        && observation.PageId == state.PageId && observation.WindowHandle == target.WindowHandle
        && observation.ObservedAtUtc >= state.StartedAtUtc && observation.ObservedAtUtc <= state.FinishedAtUtc;
    private static bool Percent(double? value) => value is >= 0 and <= 100;
    private static bool Stable(double? before, double? after, double tolerance) => before is null ? after is null
        : before is { } first && after is { } last && Percent(first) && Percent(last) && Math.Abs(first - last) <= tolerance;
    private static bool SameProcess(AuditTarget? expected, AuditTarget actual) => expected is not null
        && expected.ProcessId == actual.ProcessId && expected.ProcessStartedAtUtc == actual.ProcessStartedAtUtc
        && string.Equals(expected.ProcessName, actual.ProcessName, StringComparison.OrdinalIgnoreCase);
    private static bool SameWindow(AuditTarget expected, AuditTarget actual) =>
        expected.WindowHandle == actual.WindowHandle
        && (expected.RootRuntimeId is null || AuditRules.SameRuntimeId(expected.RootRuntimeId, actual.RootRuntimeId));
    private static bool SameDirectory(string path, string normalized)
    {
        try { return string.Equals(AuditFiles.NormalizeArtifactPath(path), normalized, StringComparison.OrdinalIgnoreCase); }
        catch (Exception exception) when (Unavailable(exception)) { return false; }
    }
    private static bool SelectionChanged(ExploreInteractionResult interaction) => interaction.Before is { } before
        && interaction.After is { } after
        && !before.SelectionAutomationIds.ToHashSet(StringComparer.Ordinal).SetEquals(after.SelectionAutomationIds);
    private static bool InteractionRestored(ExploreInteractionResult interaction) => !SelectionChanged(interaction)
        && interaction.Kind switch
        {
            "scroll" => interaction.Restoration?.Status is "restored-scroll-position" or "unchanged",
            "dropdown" or "dialog" => interaction.Restoration?.Status == "restored-control",
            _ => false
        };
    private static string RestorationText(SurveyRestoration? restoration) => restoration is null
        ? "未记录；不能确认已还原"
        : $"{restoration.Status switch
        {
            "restored" => "已记录完整还原",
            "restored-navigation" => "仅导航已还原；其他状态未确认",
            "restored-control" => "已记录控件还原；不保证焦点还原",
            "restored-scroll-position" => "已记录滚动位置还原；不保证像素或焦点还原",
            "unchanged" => "已记录原状态未变；没有执行还原动作",
            "blocked" => "还原受阻",
            "incomplete" => "还原不完整",
            "failed" => "还原失败",
            "not-attempted" => "未尝试还原",
            "not-needed" => "记录为无需还原；不是还原成功证据",
            _ => "未知还原状态；不能确认已还原"
        }}（{restoration.Status}）：{restoration.Message}";
    private static bool IsWarningEvent(string kind) => kind is "window-boundary-changed" or "foreground-changed"
        or "scope-changed" or "scope-unavailable" or "environment-changed" or "monitor-failed"
        or "monitor-unavailable" or "unexpected-error" or "popup-unverified" or "state-changed" or "budget-exhausted";
    private static string RunStatus(string status) => status switch
    {
        "running" => "正在探索", "finished" => "执行已结束（不是合规结论）",
        "partial" => "部分执行／覆盖不完整", "paused" => "已暂停／覆盖未完成", _ => "未知执行状态；不视为完成"
    };
    private static string StateStatus(string? status) => status switch
    {
        "captured" => "记录为已采集（captured）", "pending" => "待采集（pending）",
        "blocked" => "受阻（blocked）", "not-tested" => "未测试（not-tested）",
        null => "未记录状态", _ => "未知状态：" + status + "；不视为已采集"
    };
    private static string NavigationStatus(string? status) => status switch
    {
        "captured" => "记录为已到达（captured）", "pending" => "待导航（pending）",
        "blocked" => "导航受阻（blocked）", null => "未记录／未核验", _ => "未知导航状态：" + status
    };
    private static string Kind(string kind) => kind switch
    {
        "base" => "基础视图", "scroll" => "滚动视图", "dropdown" => "展开下拉", "dialog" => "打开对话框",
        _ => "未知视图类型：" + kind
    };
    private static string TargetText(AuditTarget? target) => target is null ? "未提供；仅允许原始窗口"
        : $"{target.ProcessName} · PID {target.ProcessId} · HWND {target.WindowHandle} · 启动 {At(target.ProcessStartedAtUtc)}"
            + $" · {target.Title ?? "未提供标题"} · 根 RuntimeId {target.RootRuntimeId ?? "未记录"}";
    private static string Elapsed(DateTimeOffset start, DateTimeOffset? end) => start == default || end is null || end < start
        ? "未记录有效的结束时间" : ReportPresentation.Duration((decimal)(end.Value - start).TotalMilliseconds);
    private static string At(DateTimeOffset? value) => value is null || value == default(DateTimeOffset)
        ? "未记录" : value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static string Number(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "未记录";
    private static bool Unavailable(Exception exception) => exception is InvalidDataException or IOException
        or UnauthorizedAccessException or ArgumentException or NotSupportedException;
    private static string H(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static string RelativeUrl(string path) => "./" + string.Join('/',
        AuditFiles.NormalizeArtifactPath(path).Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString));
}

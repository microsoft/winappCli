using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AccessLoop;

public static class LoopReportRenderer
{
    public static void Write(string path, LoopReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var root = Absolute(report.Directory);
        AuditFiles.EnsureDirectory(root);
        var destination = Path.GetFullPath(path);
        _ = Relative(root, destination);
        AuditFiles.EnsureDirectory(Path.GetDirectoryName(destination)!);
        var html = new Page(root, Path.GetDirectoryName(destination)!, report).Render();
        using var stream = AuditFiles.OpenNew(destination);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(html);
    }

    private sealed record RoundView(int Index, LoopRoundResult Record, LoopVerifiedRound? Evidence, string? Error)
    {
        public string Anchor => $"round-{Index}";
        public bool Verified => Evidence is not null;
        public int Occurrences => Evidence!.Findings.Sum(group => group.Occurrences.Count);
    }

    private sealed record RepairView(
        int Index, LoopRepairResult Record, string? ProposalFile, string? DiffFile,
        string? AdvisorFile, RoundView? After, bool Fixed, string? Error)
    {
        public string Anchor => $"repair-{Index}";
        public string Source => Record.ProposalSource switch
        {
            "copilot" when AdvisorFile is not null => "Copilot 提案（已保存响应）",
            "copilot" => "声称 Copilot 提案，但调用证据未核验",
            "reviewed-file" when ProposalFile is not null => "人工审阅的提案文件（不是本次 AI 生成）",
            "reviewed-file" => "审阅文件提案尚未核验",
            null => "提案来源未记录",
            _ => "未知提案来源：" + Record.ProposalSource
        };
    }

    private sealed class Page
    {
        private readonly string root;
        private readonly string output;
        private readonly LoopReport report;
        private readonly StringBuilder html = new();
        private readonly RoundView[] rounds;
        private readonly RepairView[] repairs;
        private readonly string? planError;
        private readonly string? planFile;
        private readonly string? reproductionError;
        private readonly bool reproduced;
        private RoundView? Current => rounds.LastOrDefault();
        private IReadOnlyList<LoopStep> Steps => report.Profile?.Steps ?? [];
        private IReadOnlyList<LoopPage> Pages => report.Profile?.Pages ?? [];
        private IReadOnlyList<LoopRepairTarget> Targets => report.Profile?.RepairTargets ?? [];

        public Page(string root, string output, LoopReport report)
        {
            this.root = root;
            this.output = output;
            this.report = report;
            try
            {
                Need(report.SchemaVersion == 1 && report.Profile is not null && report.PlanSource == "preset",
                    "报告或路线来源未确认；只支持声明的 preset 路线。");
                LoopInput.Validate(report.Profile!);
                planFile = FileIn(root, Path.Combine(root, "plan.json"));
                AuditFiles.ValidateHash(report.PlanSha256, "plan SHA256");
                Need(HashMatches(planFile, report.PlanSha256)
                    && string.Equals(LoopInput.PlanHash(report.Profile!), report.PlanSha256, StringComparison.OrdinalIgnoreCase),
                    "plan.json、绑定指纹和当前声明不一致；不能确认执行了同一条路线。");
            }
            catch (Exception exception) when (Unavailable(exception))
            {
                planFile = null;
                planError = exception.Message;
            }
            rounds = (report.Rounds ?? []).Select((round, index) => ReadRound(round ?? new()
                { Id = "", Directory = "", Status = "missing", Error = "缺少轮次元数据。" }, index)).ToArray();
            repairs = (report.Repairs ?? []).Select((repair, index) => ReadRepair(repair ?? new()
                { AutomationId = "", ExpectedName = "", Directory = "", Status = "missing", Error = "缺少修复元数据。" }, index)).ToArray();
            var baseline = UniqueRound("baseline");
            var reproduction = UniqueRound("reproduction");
            if (baseline?.Evidence is { } first && reproduction?.Evidence is { } second)
            {
                try
                {
                    LoopRunner.RequireEquivalent(first, second);
                    reproduced = report.ReproductionStatus == "passed";
                    if (!reproduced)
                        reproductionError = "两轮证据可比较，但执行器没有记录复现通过；保留原始状态。";
                }
                catch (Exception exception) when (Unavailable(exception))
                {
                    reproductionError = exception.Message;
                }
            }
            else if (report.ReproductionStatus == "passed")
                reproductionError = report.ReplayOf is null
                    ? "缺少可核验的基线／复现轮；不能用 passed 字段代替实际复现。"
                    : "这是独立重放报告；本页不读取其他目录，因此不独立证明与旧轮次等价。";
        }

        private RoundView ReadRound(LoopRoundResult round, int index)
        {
            try
            {
                Need(planError is null, planError ?? "");
                Need(!string.IsNullOrWhiteSpace(round.Id) && report.Rounds.Count(item => item is not null && item.Id == round.Id) == 1,
                    "轮次 ID 缺失或重复。");
                var directory = DirectoryIn(root, round.Directory);
                Need(report.Rounds.Count(item => item is not null && SamePath(item.Directory, directory)) == 1, "轮次目录重复。");
                var verified = LoopEvidence.VerifyRound(report.Profile, round, root);
                _ = FileIn(directory, Path.Combine(directory, "actions.json"));
                foreach (var state in verified.States)
                {
                    var folder = DirectoryIn(directory, state.State.Directory);
                    _ = FileIn(folder, Path.Combine(folder, "report.html"));
                    _ = FileIn(folder, Path.Combine(folder, "scan.json"));
                }
                foreach (var command in verified.Round.Actions.SelectMany(action => action.Commands))
                {
                    _ = FileIn(directory, command.ResponseFile);
                    if (command.ErrorFile is { } error) _ = FileIn(directory, error);
                }
                foreach (var occurrence in verified.Findings.SelectMany(finding => finding.Occurrences))
                    _ = FileIn(DirectoryIn(directory, occurrence.StateDirectory), Path.Combine(occurrence.StateDirectory, "report.html"));
                return new(index, round, verified, null);
            }
            catch (Exception exception) when (Unavailable(exception))
            {
                return new(index, round, null, exception.Message);
            }
        }

        private RepairView ReadRepair(LoopRepairResult repair, int index)
        {
            string? proposalPath = null, diffPath = null, advisorPath = null;
            RoundView? after = null;
            try
            {
                Need(planError is null, planError ?? "");
                var declared = Targets.Where(target => target.AutomationId == repair.AutomationId
                    && target.ExpectedName == repair.ExpectedName).ToArray();
                Need(declared.Length == 1 && report.Repairs.Count(item => item is not null && item.AutomationId == repair.AutomationId) == 1,
                    "修复目标或预期名称不在唯一的冻结声明中。");
                var directory = DirectoryIn(root, repair.Directory);
                Need(repair.ProposalSource is "copilot" or "reviewed-file", "未知提案来源，不声称发生过 AI 修复。");
                proposalPath = FileIn(directory, Path.Combine(directory, "proposal.json"));
                using var document = AuditFiles.ParseStrict(AuditFiles.ReadBytes(proposalPath, 16 * 1024));
                var proposal = AuditFiles.Deserialize<RepairProposal>(document.RootElement);
                SourceRepair.ValidateProposalFields(proposal);
                Need(repair.Proposal is not null && Equal(proposal, repair.Proposal)
                    && proposal.AutomationId == repair.AutomationId && proposal.AccessibleName == repair.ExpectedName,
                    "提案文件与记录／冻结名称不一致。");
                if (repair.ProposalSource == "copilot")
                {
                    var responseFile = FileIn(directory, Path.Combine(directory, "advisor-response.txt"));
                    using var response = AuditFiles.ParseStrict(AuditFiles.ReadBytes(responseFile, 16 * 1024));
                    Need(Equal(proposal, AuditFiles.Deserialize<RepairProposal>(response.RootElement)), "Copilot 响应与接受的提案不一致。");
                    var eventsFile = FileIn(directory, Path.Combine(directory, "advisor-events.jsonl"));
                    var extracted = CopilotAdvisor.ExtractResponse(Encoding.UTF8.GetString(AuditFiles.ReadBytes(eventsFile, 1024 * 1024)));
                    using var extractedDocument = AuditFiles.ParseStrict(Encoding.UTF8.GetBytes(extracted));
                    Need(Equal(proposal, AuditFiles.Deserialize<RepairProposal>(extractedDocument.RootElement)), "Copilot 事件流未记录这一提案。");
                    _ = FileIn(directory, Path.Combine(directory, "advisor-prompt.txt"));
                    advisorPath = eventsFile;
                }
                diffPath = FileIn(directory, repair.DiffFile!);
                Need(AuditFiles.ReadBytes(diffPath, 4 * 1024 * 1024).Length > 0, "差异文件为空。");
                after = UniqueRound(repair.VerificationRoundId);
                Need(after?.Evidence is not null && after.Index > 0, "缺少可核验的修复后同路线轮次。");
                var before = rounds[after!.Index - 1];
                Need(before.Evidence is not null, "修复前轮次未核验，不能推断修复带来的变化。");
                var source = before.Record.Sources.SingleOrDefault(item => item.RelativePath == proposal.RelativePath);
                Need(source is not null && string.Equals(source.Sha256, proposal.SourceSha256, StringComparison.OrdinalIgnoreCase),
                    "提案的源码指纹没有绑定到修复前归档。");
                var updated = after.Record.Sources.SingleOrDefault(item => item.RelativePath == proposal.RelativePath);
                Need(updated is not null && !string.Equals(updated.Sha256, source!.Sha256, StringComparison.OrdinalIgnoreCase),
                    "没有观察到提案所指源码的实际版本变化；不能声称补丁已应用。");
                LoopRunner.RequireImprovement(before.Evidence!, after.Evidence!, repair.AutomationId);
                return new(index, repair, proposalPath, diffPath, advisorPath, after,
                    repair.Status == "fixed" && repair.Error is null, null);
            }
            catch (Exception exception) when (Unavailable(exception))
            {
                return new(index, repair, null, null, null, after, false, exception.Message);
            }
        }

        public string Render()
        {
            html.Append("""
                <!doctype html>
                <html lang="zh-CN"><head><meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'">
                <title>AccessLoop 真实闭环报告</title>
                <style>
                :root{color-scheme:light dark;font-family:system-ui,"Microsoft YaHei",sans-serif;line-height:1.6}
                body{max-width:86rem;margin:0 auto;padding:1.5rem;background:#fff;color:#202124}
                a{color:#0753a0}a:visited{color:#60358d}:focus-visible,:target{outline:3px solid #0753a0;outline-offset:3px}
                .skip{position:absolute;left:-10000px}.skip:focus{left:1rem;top:1rem;background:#fff;padding:1rem}
                nav,.counts{display:flex;gap:1rem;flex-wrap:wrap}.counts{list-style:none;padding:0}.counts li{border:1px solid #b7bcc4;padding:.6rem}
                .story{display:grid;grid-template-columns:repeat(auto-fit,minmax(13rem,1fr));gap:.75rem}.stage,article{border:1px solid #b7bcc4;border-radius:.4rem;padding:1rem;margin-block:1rem}
                .stage{margin:0}.stage h3{margin-top:0}.lead{font-size:1.4rem;font-weight:650}.notice{background:#f4f6fa;border-inline-start:5px solid #956000;padding:.8rem;margin:1rem 0}
                section{border-block-start:1px solid #b7bcc4;padding-top:1rem;margin:2rem 0}h1,h2,h3,h4{line-height:1.3;overflow-wrap:anywhere}
                .finding{border-inline-start:4px solid #a51b27}.muted{color:#495462}pre{background:#f4f6fa;padding:1rem;white-space:pre-wrap;overflow-wrap:anywhere}
                code,dd,li,p{overflow-wrap:anywhere}dl{display:grid;grid-template-columns:minmax(9rem,18rem) minmax(0,1fr);gap:.3rem 1rem}dt{font-weight:600}dd{margin:0;white-space:pre-wrap}
                .table-scroll{overflow:auto}table{border-collapse:collapse;width:100%;text-align:start}th,td{padding:.55rem;border:1px solid #b7bcc4;vertical-align:top}th{background:#edf0f5}
                caption{text-align:start;font-weight:600;margin:.5rem 0}.report-detail{border:1px solid #b7bcc4;border-radius:.3rem;padding:.45rem .75rem;margin:.75rem 0}
                summary{cursor:pointer;padding:.45rem;font-weight:600}summary:hover{background:#edf0f5}.report-detail>section{border:0;padding:0;margin:1rem 0}
                @media(max-width:45rem){body{padding:.75rem}dl{display:block}dd{margin-bottom:.5rem}}
                @media(prefers-color-scheme:dark){body{background:#16191e;color:#edf0f5}a{color:#a8d1ff}a:visited{color:#dbc0ff}
                .notice,pre,th,summary:hover{background:#242a33}.muted{color:#c3c9d1}.skip:focus{background:#16191e}.finding{border-inline-start-color:#ff8994}.notice{border-inline-start-color:#e5b952}}
                </style></head><body><a class="skip" href="#main">跳到报告正文</a>
                <header><h1>AccessLoop 真实闭环报告</h1>
                """);
            html.Append("<p>").Append(H(report.Name)).Append("</p><nav aria-label=\"闭环报告章节\">");
            foreach (var (id, label) in new[] { ("summary", "本次结果"), ("story", "闭环过程"), ("rounds", "问题与截图"),
                ("repairs", "提案与补丁"), ("reproduce", "如何复现"), ("details", "证据详情") })
                Link("#" + id, label);
            html.Append("</nav></header><main id=\"main\">");
            Summary();
            Story();
            Rounds();
            Repairs();
            Reproduce();
            Details();
            html.Append("""
                </main><footer><p>本页是本地静态证据视图，不运行命令、不连接应用或外部服务，也不修改扫描、截图或源码。
                状态报告保留原始问题编号、截图和控件局部图；此处不重新生成图像标记。</p>
                <p>哈希核验用于发现已保存字节变化，不是作者签名。重新打开 HTML 不会自动重新核验外部文件。
                report.html 与修复差异没有模型绑定的独立摘要，链接不等于其内容有签名。分享前检查名称、截图与原始响应中的敏感信息。</p></footer></body></html>
                """);
            return html.ToString();
        }

        private void Summary()
        {
            var current = Current?.Evidence;
            var names = current?.NameChecks.Count(check => check.Passed);
            var occurrences = Current?.Verified == true ? Current.Occurrences : (int?)null;
            var remaining = current?.Findings.Count(group => group.MappedTarget);
            var outcome = current is null ? "当前结果未核验：不能把缺失证据当作零问题。"
                : repairs.Any(repair => repair.Record.Status is "failed" or "rolled-back" or "rollback-blocked")
                    ? "修复未确认保留：失败或回滚不能算作成功闭环。"
                : names == Targets.Count && occurrences == 0
                    ? report.VerificationStatus == "passed" ? "声明的目标名称与同路线规则验证已满足。" : "当前证据满足目标检查；执行器最终结论仍需核对。"
                    : names == Targets.Count ? "目标名称已满足，但仍有其他规则失败需要处理。"
                    : "已定位到需要处理的目标问题；闭环尚未验证通过。";
            html.Append("<section id=\"summary\"><h2>本次结果</h2><p class=\"lead\">").Append(H(outcome))
                .Append("</p><ul class=\"counts\">");
            Metric("current-names", "预期 Name 已满足", names, $" / {Targets.Count} 个目标");
            Metric("current-pages", "当前已核验页面", current?.States.Select(state => state.State.PageId).Distinct(StringComparer.Ordinal).Count(), $" / {Pages.Count} 页");
            Metric("current-states", "当前已核验视图", current?.States.Count, $" / {Steps.Count(step => step.Kind == "scan")} 个状态");
            Metric("current-mapped", "当前源控件问题组", remaining);
            Metric("current-failures", "当前规则失败出现次数", occurrences);
            html.Append("</ul><p>总用时：<strong>").Append(H(Duration(report.StartedAtUtc, report.FinishedAtUtc)))
                .Append("</strong> · ").Append(H(Status(report.Status))).Append("</p>");
            html.Append("<div class=\"notice\"><strong>范围与覆盖</strong><p>")
                .Append(report.PlanSource == "preset" ? "业务数据与路线是预设，不是 Copilot 自主规划。保存计划本身不证明发生过操作。"
                    : "路线来源未知；不能声称已执行受支持的预设闭环。")
                .Append("本结论仅针对声明演示路线；目标修复通过不等于整应用或 WCAG 通过。</p>");
            var partial = rounds.Count(round => round.Evidence?.CaptureStatus == "partial");
            if (partial > 0)
                html.Append("<p>").Append(partial).Append(" 个已核验轮次为部分采集（partial）；未知与未测试内容没有变成通过。</p>");
            if (current is null)
                html.Append("<p>当前轮没有完整、可信的采集汇总；未知不是 0。旧轮次不代替缺失的最终验证。</p>");
            if (planError is not null) Warning(planError);
            if (reproductionError is not null) Warning(reproductionError);
            foreach (var round in rounds.Where(round => !round.Verified))
                Warning($"{RoundName(round.Record.Id)}：{round.Error}");
            foreach (var repair in repairs.Where(repair => repair.Error is not null || repair.Record.Status is "failed" or "rolled-back" or "rollback-blocked"))
                Warning($"{repair.Record.ExpectedName}：{repair.Error ?? repair.Record.Error ?? RepairStatus(repair.Record.Status)}");
            if (!string.IsNullOrWhiteSpace(report.Error)) Warning(report.Error);
            if (report.VerificationStatus is not ("passed" or "failed" or "not-run" or "inconclusive"))
                Warning("未知验证状态：" + report.VerificationStatus);
            html.Append("</div></section>");
        }

        private void Story()
        {
            html.Append("<section id=\"story\"><h2>从问题到同路线验证</h2><div class=\"story\">");
            Stage("1 · 基线检查", UniqueRound("baseline"), "未记录基线轮。");
            html.Append("<article class=\"stage\"><h3>2 · 实际复现</h3><p>")
                .Append(H(reproduced ? "实际动作、相同源码和问题出现记录已核对。" :
                    reproductionError ?? "尚未完成可信的基线／复现对照；不是只保存一份计划。")).Append("</p>");
            if (UniqueRound("reproduction") is { } replay) Link("#" + replay.Anchor, "查看复现轮");
            else if (UniqueRound("replay") is { } standalone) Link("#" + standalone.Anchor, "查看独立重放轮");
            html.Append("</article><article class=\"stage\"><h3>3 · 提案 → Name 补丁</h3><p>");
            if (repairs.Length == 0)
                html.Append("没有修复提案记录；不声称已调用 AI 或改动源码。");
            else
                html.Append(repairs.Count(repair => repair.Fixed)).Append(" 项修复有已核对的提案与后续验证；其余状态见修复记录。");
            html.Append("</p>");
            Link("#repairs", "区分 Copilot 与审阅文件");
            html.Append("</article>");
            Stage("4 · 重建后同路线验证", rounds.LastOrDefault(round => round.Record.Id.StartsWith("after-", StringComparison.Ordinal)),
                "尚无修复后验证轮；当前观察不冒充修复后的结果。");
            html.Append("</div></section>");
        }

        private void Stage(string label, RoundView? round, string missing)
        {
            html.Append("<article class=\"stage\"><h3>").Append(H(label)).Append("</h3><p>");
            html.Append(H(round is null ? missing : round.Verified
                ? $"{round.Record.Actions.Count} 个实际步骤 · {round.Evidence!.States.Count} 个视图 · {round.Occurrences} 次规则失败"
                : $"尚未核验：{round.Error}"));
            html.Append("</p>");
            if (round is not null) Link("#" + round.Anchor, "查看这一轮");
            html.Append("</article>");
        }

        private void Rounds()
        {
            html.Append("<section id=\"rounds\"><h2>逐轮问题与截图</h2><p>只合并源码、页面和扫描内身份都已确认的声明目标；同名或相同 RuntimeId 不足以跨状态合并。未映射、身份不唯一的失败逐次保留。</p>");
            html.Append("<p>单状态报告是被动快照，其中键盘 not-tested 保持原样；实际路线的按键与期望检查来自本轮 actions.json，不改写快照的检查目录。</p>");
            if (rounds.Any(round => round.Evidence?.NameChecks.Any(check => check.EvidenceSource.Contains("privacy-withheld", StringComparison.Ordinal)) == true))
                html.Append("<p>部分快照因宿主隐私信息缺失而隐去名称。这些目标的 Name 检查采用已确认非密码控件的前后属性见证；快照仍保持 partial，不把隐去的 null 当作名称缺陷或完整采集。</p>");
            foreach (var round in rounds)
            {
                html.Append("<article id=\"").Append(round.Anchor).Append("\"><h3>").Append(H(RoundName(round.Record.Id)))
                    .Append("</h3><p>").Append(H(Status(round.Record.Status))).Append(" · ")
                    .Append(H(Duration(round.Record.StartedAtUtc, round.Record.FinishedAtUtc))).Append("</p>");
                if (!round.Verified)
                {
                    Warning(round.Error ?? "轮次未核验。");
                    html.Append("<p>问题数未知；不提供可信的完成／零问题结论或状态报告链接。</p>");
                    RawStates(round);
                }
                else
                {
                    html.Append("<p>").Append(H(ReportPresentation.CaptureStatus(round.Evidence!.CaptureStatus))).Append("</p>");
                    html.Append("<ul class=\"counts\">");
                    Metric(round.Anchor + "-mapped", "源控件问题组", round.Evidence!.Findings.Count(group => group.MappedTarget));
                    Metric(round.Anchor + "-unmapped", "未映射／不唯一的出现记录", round.Evidence.Findings.Where(group => !group.MappedTarget).Sum(group => group.Occurrences.Count));
                    Metric(round.Anchor + "-failures", "全部规则失败出现次数", round.Occurrences);
                    html.Append("</ul>");
                    if (round.Occurrences == 0)
                        html.Append("<p>这轮已运行的规则未报告失败；这不是全应用通过，仍需查看覆盖限制。</p>");
                    foreach (var group in round.Evidence.Findings)
                        Finding(group);
                    StateTable(round);
                    html.Append("<p>");
                    FileLink(Path.Combine(round.Record.Directory, "actions.json"), "实际动作 actions.json");
                    html.Append(" · ");
                    Link("#" + round.Anchor + "-technical", "动作、见证与源码指纹");
                    html.Append("</p>");
                }
                html.Append("</article>");
            }
            if (rounds.Length == 0) html.Append("<p>尚无轮次证据；计划不是已执行记录。</p>");
            html.Append("</section>");
        }

        private void Finding(LoopFindingGroup group)
        {
            var target = group.MappedTarget ? Targets.Single(item => item.AutomationId == group.AutomationId) : null;
            var first = group.Occurrences[0];
            var label = target is null ? first.ElementName ?? first.AutomationId ?? "未唯一定位的控件" : target.ExpectedName;
            if (string.IsNullOrWhiteSpace(label)) label = "无已公开名称的控件";
            html.Append("<article class=\"finding\"><h4>").Append(H(label)).Append("</h4><p>")
                .Append(H(group.Description)).Append("</p><p>")
                .Append(group.MappedTarget ? "已映射静态 Button" : "未映射／身份不唯一，不合并")
                .Append(" · ").Append(group.Occurrences.Count).Append(" 次出现 · ").Append(H(group.RuleId)).Append("</p><ul>");
            foreach (var occurrence in group.Occurrences)
            {
                html.Append("<li>");
                var step = Steps.Single(item => item.Id == occurrence.CheckpointId);
                Link(Url(Path.Combine(occurrence.StateDirectory, "report.html")) + "#result-" + occurrence.ResultIndex.ToString(CultureInfo.InvariantCulture),
                    $"{PageName(occurrence.PageId)} · {step.Name} · 原始结果 {occurrence.ResultIndex + 1} 与图片");
                html.Append("</li>");
            }
            html.Append("</ul></article>");
        }

        private void StateTable(RoundView round)
        {
            Table("本轮页面与视图状态", "页面／检查点", "实际采集", "规则失败出现次数", "报告");
            foreach (var state in round.Evidence!.States)
            {
                html.Append("<tr>");
                Cell($"{PageName(state.State.PageId)} · {state.State.Name}");
                Cell(ReportPresentation.CaptureStatus(state.Scan.Status));
                Cell(state.Scan.RuleFailureCount.ToString(CultureInfo.InvariantCulture));
                html.Append("<td>");
                FileLink(Path.Combine(state.State.Directory, "report.html"), "完整问题卡片与截图");
                html.Append(" · ");
                FileLink(Path.Combine(state.State.Directory, "scan.json"), "scan.json");
                html.Append("</td></tr>");
            }
            TableEnd();
        }

        private void RawStates(RoundView round)
        {
            Table("尚未核验的状态记录", "声明检查点", "记录状态", "原因");
            foreach (var step in Steps.Where(step => step.Kind == "scan"))
            {
                var states = (round.Record.States ?? []).Where(state => state is not null && state.StepId == step.Id).ToArray();
                html.Append("<tr>"); Cell($"{PageName(step.PageId)} · {step.Name}");
                Cell(states.Length == 1 ? states[0].Status + "（未核验）" : states.Length == 0 ? "缺失／未测试" : "重复／关联不唯一");
                Cell(states.Length == 1 ? states[0].Error ?? "本轮证据链未完成核验" : "不能作为零问题结果");
                html.Append("</tr>");
            }
            foreach (var state in (round.Record.States ?? []).Where(state => state is not null
                && !Steps.Any(step => step.Kind == "scan" && step.Id == state.StepId)))
            {
                html.Append("<tr>"); Cell(state.Name); Cell(state.Status + "（未声明状态）"); Cell(state.Error); html.Append("</tr>");
            }
            TableEnd();
        }

        private void Repairs()
        {
            html.Append("<section id=\"repairs\"><h2>提案、补丁与后续验证</h2><p>期望名称在路线中事先审阅。Copilot 只参与修复提案，不负责规划路线；应用补丁与重新执行由闭环执行器完成。</p>");
            if (repairs.Length == 0) html.Append("<p>没有提案或补丁记录；本报告不据此声称 AI 已工作。</p>");
            foreach (var repair in repairs)
            {
                html.Append("<article id=\"").Append(repair.Anchor).Append("\"><h3>").Append(H(repair.Record.ExpectedName))
                    .Append("</h3><p>").Append(H(repair.Source)).Append("</p><p>")
                    .Append(H(repair.Fixed ? "本目标修复已通过后续同路线核对" : "未确认成功修复"))
                    .Append(" · ").Append(H(RepairStatus(repair.Record.Status))).Append("</p>");
                if (repair.Record.Proposal?.Explanation is { } explanation)
                    html.Append("<p>").Append(H(explanation)).Append("</p>");
                if (repair.Error is not null) Warning(repair.Error);
                if (repair.Record.Error is not null) Warning(repair.Record.Error);
                html.Append("<p>");
                if (repair.ProposalFile is { } proposal) { FileLink(proposal, "实际 proposal.json"); html.Append(" · "); }
                if (repair.DiffFile is { } diff) { FileLink(diff, "查看 repair.diff"); html.Append(" · "); }
                if (repair.AdvisorFile is { } advisor) { FileLink(advisor, "已保存的 Copilot 响应事件"); html.Append(" · "); }
                if (repair.After is { } after) Link("#" + after.Anchor, "对应修复后验证轮");
                html.Append("</p><p class=\"muted\">提案来源按保存记录说明；差异没有独立绑定的哈希，不据此保证任意未记录文件或构建产物。</p></article>");
            }
            html.Append("</section>");
        }

        private void Reproduce()
        {
            html.Append("<section id=\"reproduce\"><h2>如何实际复现</h2><p>复制命令到终端运行；本 HTML 不执行命令。请选择尚不存在的输出目录。</p>");
            if (planFile is not null)
            {
                html.Append("<p>先查看 ");
                FileLink(planFile, "冻结的 plan.json");
                html.Append("；它描述预设路线，不是执行证据。</p>");
            }
            if (rounds.Any(round => round.Verified) && planFile is not null)
            {
                var selected = UniqueRound("baseline") is { Verified: true } baseline ? baseline : rounds.First(round => round.Verified);
                var command = $"AccessLoop loop --replay {Quote(root)} --round {Quote(selected.Record.Id)} --accept-actions --output '.\\loop-replay-new'";
                html.Append("<pre><code>").Append(H(command)).Append("</code></pre>");
            }
            else
                html.Append("<p class=\"notice\">还没有可核验的轮次；先处理证据错误，不提供看似已经可复现的完成结论。</p>");
            html.Append("<p>重放会从选定轮次归档的源码启动新的自有进程，再执行同一路线；不会复用旧 PID／HWND。")
                .Append("保持记录的工具版本与截图策略；需要时追加 <code>--winapp '工具路径'</code> 或 <code>--json</code>。")
                .Append("若工具或归档已变化，先处理错误，不修改预期名称、计划或哈希来让验证通过。</p></section>");
        }

        private void Details()
        {
            html.Append("<section id=\"details\"><h2>证据与执行详情</h2>");
            ReportPresentation.Details(html, "路线、键盘准备与报告绑定", () =>
            {
                html.Append("<p>页面导航只将页签标题焦点设为准备起点，不把导航准备、滚动、Invoke 或展开动作包装成“全部通过键盘完成”。")
                    .Append("预设 key 步骤核对实际焦点后通过原生 ui send-keys --via send-input 输入，不使用 --target 修正焦点；下方保留实际前后属性与命令记录。</p><dl>");
                Pair("运行 ID", report.RunId);
                Pair("路线来源", report.PlanSource);
                Pair("冻结路线 SHA256", report.PlanSha256);
                Pair("原始执行／复现／验证／采集状态", $"{report.Status} / {report.ReproductionStatus} / {report.VerificationStatus} / {report.CaptureStatus}");
                Pair("重放来源声明（仅文字）", report.ReplayOf);
                Pair("WinAppCLI 版本", report.Tools?.WinappVersion);
                Pair("工具／执行器 SHA256", $"{report.Tools?.NativeSha256} / {report.Tools?.RunnerSha256}");
                Pair("截图请求", report.Screenshot ? "已请求；以每个状态实际报告为准" : "未请求");
                html.Append("</dl>");
            });
            foreach (var round in rounds)
                ReportPresentation.Details(html, RoundName(round.Record.Id) + " · 动作、前后见证和源码指纹", () => RoundDetails(round));
            html.Append("</section>");
        }

        private void RoundDetails(RoundView round)
        {
            html.Append("<div id=\"").Append(round.Anchor).Append("-technical\"><dl>");
            Pair("轮次 ID／原始状态", $"{round.Record.Id} / {round.Record.Status}");
            Pair("目标进程／窗口／启动时间", round.Record.Target is { } target
                ? $"{target.ProcessName} · PID {target.ProcessId} · HWND {target.WindowHandle} · {At(target.ProcessStartedAtUtc)}" : null);
            Pair("开始／结束 UTC", $"{At(round.Record.StartedAtUtc)} / {At(round.Record.FinishedAtUtc)}");
            Pair("actions.json SHA256", round.Record.ActionLogSha256);
            Pair("源码归档", round.Record.SourceDirectory);
            Pair("核验限制", round.Error);
            Pair("执行器原因", round.Record.Error);
            html.Append("</dl><h4>源码清单</h4><ul>");
            foreach (var source in (round.Record.Sources ?? []).OfType<JourneySourceStamp>())
                html.Append("<li>").Append(H($"{source.RelativePath} · {source.Sha256}")).Append("</li>");
            html.Append("</ul><p>核验归档与这一轮的源码清单；不同轮次的源码不默认相同。路线指纹按项目文件名规范化，源码内容由每轮归档指纹单独核验，")
                .Append("不依赖当前工作副本或原始示例文件。这里不额外保证工作副本、未记录文件或二进制构建产物。</p>");
            foreach (var action in (round.Record.Actions ?? []).OfType<LoopActionResult>())
            {
                html.Append("<h4>").Append(H(action.Step?.Name ?? "缺少步骤声明")).Append("</h4><dl>");
                Pair("步骤／类型／原始状态", $"{action.Step?.Id} / {action.Step?.Kind} / {action.Status}");
                Pair("执行时间", $"{At(action.StartedAtUtc)} / {At(action.FinishedAtUtc)}");
                Pair("原因", action.Error);
                html.Append("</dl>");
                JsonView("操作前原生属性", action.Before);
                JsonView("操作后原生属性", action.After);
                JsonView("类型化期望检查", action.Checks);
                foreach (var command in (action.Commands ?? []).OfType<LoopCommandEvidence>())
                {
                    html.Append("<p>").Append(H(command.Operation)).Append(" · ");
                    if (round.Verified)
                    {
                        FileLink(command.ResponseFile, "原始响应");
                        if (command.ErrorFile is { } error) { html.Append(" · "); FileLink(error, "原始诊断"); }
                    }
                    else
                        html.Append("命令文件未核验，不提供可信链接");
                    html.Append("</p><dl>");
                    Pair("参数数组（运行记录，不是复现命令）", JsonSerializer.Serialize(command.Arguments, Json.Options));
                    Pair("响应／诊断 SHA256", $"{command.ResponseSha256} / {command.ErrorSha256 ?? "无诊断文件"}");
                    html.Append("</dl>");
                }
            }
            foreach (var state in (round.Record.States ?? []).OfType<LoopStateResult>())
            {
                html.Append("<h4>").Append(H(state.Name)).Append("</h4><dl>");
                Pair("检查点／页面／记录状态", $"{state.StepId} / {state.PageId} / {state.Status}");
                Pair("扫描绑定 SHA256", state.ScanSha256);
                Pair("状态原因", state.Error);
                html.Append("</dl>");
                JsonView("必需控件：采集前", state.BeforeControls);
                JsonView("必需控件：采集后", state.AfterControls);
            }
            JsonView("范围事件（不增加规则失败数）", round.Record.ScopeEvents);
            html.Append("</div>");
        }

        private RoundView? UniqueRound(string? id) => id is not null && rounds.Count(round => round.Record.Id == id) == 1
            ? rounds.Single(round => round.Record.Id == id) : null;
        private string PageName(string id) => Pages.FirstOrDefault(page => page.Id == id)?.Name ?? id;
        private string Url(string path) => "./" + string.Join('/', Path.GetRelativePath(output, path)
            .Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString));
        private void FileLink(string path, string label) => Link(Url(path), label);
        private void Link(string href, string label) => html.Append("<a href=\"").Append(H(href)).Append("\">").Append(H(label)).Append("</a>");
        private void Warning(string text) => html.Append("<p class=\"notice\">").Append(H(text)).Append("</p>");
        private void Pair(string label, string? text) => html.Append("<dt>").Append(H(label)).Append("</dt><dd>").Append(H(text ?? "未记录／未知")).Append("</dd>");
        private void Cell(string? text) => html.Append("<td>").Append(H(text ?? "未知")).Append("</td>");
        private void JsonView<T>(string label, T value) => ReportPresentation.Details(html, label,
            () => html.Append("<pre>").Append(H(JsonSerializer.Serialize(value, Json.Options))).Append("</pre>"));
        private void Metric(string id, string label, int? count, string suffix = "") =>
            html.Append("<li>").Append(H(label)).Append("：<strong id=\"").Append(id).Append("\">")
                .Append(count?.ToString(CultureInfo.InvariantCulture) ?? "未知").Append("</strong>").Append(H(suffix)).Append("</li>");
        private void Table(string caption, params string[] columns)
        {
            html.Append("<div class=\"table-scroll\" role=\"region\" tabindex=\"0\" aria-label=\"").Append(H(caption))
                .Append("\"><table><caption>").Append(H(caption)).Append("</caption><thead><tr>");
            foreach (var column in columns) html.Append("<th scope=\"col\">").Append(H(column)).Append("</th>");
            html.Append("</tr></thead><tbody>");
        }
        private void TableEnd() => html.Append("</tbody></table></div>");
    }

    private static string Absolute(string? path)
    {
        Need(!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal),
            "证据路径必须是本地绝对路径，不能使用相对路径或网络路径。");
        var trimmed = Path.TrimEndingDirectorySeparator(path!);
        var drive = Path.GetPathRoot(trimmed)!;
        _ = AuditFiles.NormalizeArtifactPath(trimmed[drive.Length..]);
        return Path.GetFullPath(trimmed);
    }
    private static string Relative(string root, string path)
    {
        var full = Absolute(path);
        var relative = AuditFiles.NormalizeArtifactPath(Path.GetRelativePath(root, full));
        Need(full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "证据路径超出本次声明目录。");
        return relative;
    }
    private static string FileIn(string root, string path) =>
        LoopEvidence.BoundFile(root, Path.Combine(root, Relative(root, path)));
    private static string DirectoryIn(string root, string path) =>
        LoopEvidence.BoundDirectory(root, Path.Combine(root, Relative(root, path)));
    private static bool SamePath(string path, string expected)
    {
        try { return string.Equals(Absolute(path), expected, StringComparison.OrdinalIgnoreCase); }
        catch (Exception exception) when (Unavailable(exception)) { return false; }
    }
    private static bool HashMatches(string path, string expected)
    {
        AuditFiles.ValidateHash(expected, "SHA256");
        return string.Equals(AuditFiles.Hash(path), expected, StringComparison.OrdinalIgnoreCase);
    }
    private static bool Equal<T>(T first, T second) =>
        JsonElement.DeepEquals(JsonSerializer.SerializeToElement(first, Json.Options), JsonSerializer.SerializeToElement(second, Json.Options));
    private static string H(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static string Quote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
    private static string At(DateTimeOffset? value) => value is null || value == default(DateTimeOffset)
        ? "未记录" : value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static string Duration(DateTimeOffset start, DateTimeOffset? end) => start == default || end is null || end < start
        ? "尚无有效的结束时间" : ReportPresentation.Duration((decimal)(end.Value - start).TotalMilliseconds);
    private static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static bool Unavailable(Exception exception) => exception is InvalidDataException or IOException
        or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException
        or KeyNotFoundException or JsonException or FormatException or OverflowException;
    private static string RoundName(string id) => id switch
    {
        "baseline" => "基线", "reproduction" => "实际复现", "replay" => "独立重放",
        _ => id.StartsWith("after-", StringComparison.Ordinal) ? "修复后验证 · " + id : "轮次 · " + id
    };
    private static string Status(string status) => status switch
    {
        "running" => "执行中（running）", "completed" => "执行完成（completed，不是合规结论）",
        "partial" => "部分采集／执行（partial）", "failed" => "目标验证未通过（failed）",
        "blocked" => "执行受阻（blocked）", _ => "未知执行状态：" + status
    };
    private static string RepairStatus(string status) => status switch
    {
        "pending" => "尚未提案（pending）", "proposing" => "提案中（proposing）",
        "applied" => "已记录应用补丁，等待验证（applied）", "fixed" => "记录为已修复（fixed）",
        "failed" => "修复失败（failed）", "rolled-back" => "已记录回滚（rolled-back）",
        "rollback-blocked" => "回滚受阻（rollback-blocked）", _ => "未知修复状态：" + status
    };
}

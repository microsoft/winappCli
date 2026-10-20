using System.Globalization;
using System.Text.Json;

namespace AccessLoop;

public static partial class AuditReportRenderer
{
    private sealed partial class Page
    {
        private void Snapshot()
        {
            html.Append("<section id=\"tree\"><h2>UIA 树与属性（采集事实）</h2>");
            if (report.Snapshot is not { ValueKind: JsonValueKind.Object } snapshot)
            {
                html.Append("<p>未提供 UIA 快照；无法确认节点、属性或文本对比度。</p></section>");
                return;
            }
            html.Append("<dl>");
            foreach (var (name, label) in new[]
            {
                ("status", "快照状态"), ("capturedAtUtc", "采集时间 UTC"), ("processId", "进程 PID"),
                ("windowHandle", "窗口 HWND"), ("rootRuntimeId", "根 RuntimeId"), ("title", "快照标题"),
                ("maxDepth", "最大深度"), ("maxElements", "最大元素数"), ("depthLimitReached", "达到深度上限"),
                ("elementLimitReached", "达到元素上限")
            })
                Pair(label, Display(Field(snapshot, name)));
            html.Append("</dl>");
            Issues(Field(snapshot, "issues"));
            html.Append("""
                <p>层级按节点 id／parentId 展示；展开节点可查看属性。缺少 AutomationId 只是可测试性提示，
                不自动构成可访问性规则失败。缺失、withheld、mixed、unsupported 和 error 均不等于有效属性值。</p>
                """);
            if (nodes.Length == 0)
            {
                html.Append("<p>快照没有可展示的节点；这不是空页面通过的证据。</p></section>");
                return;
            }
            var children = new Dictionary<int, List<int>>();
            var roots = new List<int>();
            for (var index = 0; index < nodes.Length; index++)
            {
                var parentId = String(Field(nodes[index], "parentId"));
                if (parentId is not null && nodeIndexes.TryGetValue(parentId, out var parent))
                {
                    if (!children.TryGetValue(parent, out var list))
                        children.Add(parent, list = []);
                    list.Add(index);
                }
                else
                    roots.Add(index);
            }
            var visited = new HashSet<int>();
            html.Append("<ol class=\"tree\">");
            foreach (var index in roots)
                Node(index, 0, children, visited);
            html.Append("</ol>");
            if (visited.Count < nodes.Length)
            {
                html.Append("<h3>无法完整解析的层级</h3><p>存在循环、过深或断开的父节点引用；以下节点不能作为已验证的层级关系。</p><ol class=\"tree\">");
                for (var index = 0; index < nodes.Length; index++)
                    if (!visited.Contains(index))
                        Node(index, 0, children, visited);
                html.Append("</ol>");
            }
            html.Append("</section>");
        }

        private void Node(int index, int depth, Dictionary<int, List<int>> children, HashSet<int> visited)
        {
            if (!visited.Add(index))
                return;
            var node = nodes[index];
            html.Append("<li id=\"node-").Append(index).Append("\"><details");
            if (depth == 0)
                html.Append(" open");
            html.Append("><summary><code>").Append(H(Display(Field(node, "id")))).Append("</code> · ")
                .Append(H(Display(Field(node, "controlType")))).Append(" · ").Append(H(Display(Field(node, "name"))))
                .Append("</summary><dl>");
            foreach (var (name, label) in new[]
            {
                ("id", "节点 ID"), ("parentId", "父节点 ID"), ("depth", "记录深度"), ("runtimeId", "RuntimeId"),
                ("name", "可访问名称"), ("controlType", "ControlType"), ("frameworkId", "FrameworkId"),
                ("className", "ClassName"), ("isPassword", "密码字段"), ("isEnabled", "已启用"),
                ("isKeyboardFocusable", "可键盘聚焦"), ("hasKeyboardFocus", "持有键盘焦点"), ("isOffscreen", "屏幕外")
            })
                Pair(label, Display(Field(node, name)));
            var automationId = String(Field(node, "automationId"));
            Pair("AutomationId", string.IsNullOrWhiteSpace(automationId)
                ? "缺少 AutomationId（可测试性提示，不是自动可访问性失败）" : automationId);
            Pair("UIA 边界（提供程序坐标）", Bounds(Field(node, "bounds")));
            var patterns = Items(Field(node, "patterns")).Select(Display).ToArray();
            Pair("已公开的控件模式", patterns.Length == 0 ? "未提供模式；不推断控件行为" : string.Join("；", patterns));
            html.Append("</dl>");
            TextRuns(Field(node, "textRuns"));
            Issues(Field(node, "issues"));
            html.Append("</details>");
            if (children.TryGetValue(index, out var descendants))
            {
                if (depth < 100)
                {
                    html.Append("<ol class=\"tree\">");
                    foreach (var descendant in descendants)
                        Node(descendant, depth + 1, children, visited);
                    html.Append("</ol>");
                }
                else
                    html.Append("<p>层级展示达到安全深度限制；其余节点单独列出，不补造关系。</p>");
            }
            html.Append("</li>");
        }

        private void TextRuns(JsonElement value)
        {
            var runs = Items(value).ToArray();
            html.Append("<details><summary>文本属性（不含文本内容） · ").Append(runs.Length).Append("</summary>");
            if (runs.Length == 0)
            {
                html.Append("<p>未采集文本属性；不能据此计算或确认文字对比度。</p></details>");
                return;
            }
            html.Append("<p>属性值来自 UIA 提供程序，不是截图像素测量。number／boolean 仅在 status=value 时作为值展示。</p>");
            TableStart("文本运行属性", "运行 ID", "边界", "前景色", "背景色", "字号", "字重", "隐藏状态");
            foreach (var run in runs)
            {
                html.Append("<tr>");
                Cell(Display(Field(run, "id")));
                var bounds = Items(Field(run, "bounds")).Select(Bounds).ToArray();
                Cell(bounds.Length == 0 ? "未提供／未知" : string.Join("；", bounds));
                foreach (var name in new[] { "foregroundColor", "backgroundColor", "fontSize", "fontWeight", "isHidden" })
                    Cell(TextAttribute(Field(run, name)));
                html.Append("</tr>");
            }
            TableEnd();
            html.Append("</details>");
        }

        private static string TextAttribute(JsonElement attribute)
        {
            var status = String(Field(attribute, "status"));
            var description = status switch
            {
                "value" => "value（提供程序值）",
                "mixed" => "mixed（混合，不能当作单一值）",
                "unsupported" => "unsupported（不支持）",
                "error" => "error（读取失败）",
                "withheld" => "withheld（隐私保留）",
                _ => status is null ? "未提供／未知" : $"未知属性状态：{status}"
            };
            if (status == "value")
            {
                var number = Field(attribute, "number");
                var boolean = Field(attribute, "boolean");
                description += number.ValueKind == JsonValueKind.Number ? " · " + number.GetRawText()
                    : boolean.ValueKind is JsonValueKind.True or JsonValueKind.False ? " · " + boolean.GetRawText()
                    : " · 未提供有效值（无法判断）";
            }
            if (Field(attribute, "nativeType") is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } nativeType)
                description += " · nativeType: " + Display(nativeType);
            if (Field(attribute, "error") is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } error)
                description += " · " + Display(error);
            return description;
        }

        private static string Bounds(JsonElement bounds)
        {
            if (bounds.ValueKind != JsonValueKind.Object)
                return "未提供／未知";
            return string.Join(", ", new[] { "left", "top", "width", "height" }
                .Select(name => $"{name}={Display(Field(bounds, name))}"));
        }

        private void Focus()
        {
            html.Append("<section id=\"focus\"><h2>焦点轨迹（有界观察，不是导航正确性结论）</h2>");
            if (report.Observation is not { ValueKind: JsonValueKind.Object } observation)
            {
                html.Append("<p>未记录焦点观察；未测试键盘路径。</p></section>");
                return;
            }
            html.Append("<dl>");
            foreach (var (name, label) in new[]
            {
                ("status", "观察状态"), ("processId", "进程 PID"), ("windowHandle", "窗口 HWND"),
                ("startedAtUtc", "开始时间 UTC"), ("endedAtUtc", "结束时间 UTC"), ("durationSec", "时间预算（秒）"),
                ("requestedTabSteps", "请求的 Tab 步数"), ("completedTabSteps", "完成的 Tab 步数"),
                ("stopReason", "停止原因"), ("maxEvents", "事件上限"), ("eventLimitReached", "达到事件上限")
            })
                Pair(label, Display(Field(observation, name)));
            html.Append("</dl><p>initial／forward／reverse 保留实际采集方向。重复焦点只是观察，可能是正常循环；");
            html.Append("未访问候选、超时或离开目标窗口都不能直接判定键盘陷阱。此页面不会发送按键。</p>");
            Issues(Field(observation, "issues"));
            var trace = Items(Field(observation, "focusTrace")).ToArray();
            if (trace.Length == 0)
                html.Append("<p>没有焦点轨迹记录；没有轨迹不等于键盘可用。</p>");
            else
            {
                TableStart("焦点时间线 · WinAppCLI / UIA focusTrace", "序号／方向", "时间 UTC", "RuntimeId／快照链接",
                    "AutomationId", "名称／控件", "密码字段", "位于目标", "重复目标");
                foreach (var item in trace)
                {
                    html.Append("<tr>");
                    Cell($"{Display(Field(item, "sequence"))} · {Display(Field(item, "direction"))}");
                    Cell(Display(Field(item, "observedAtUtc")));
                    html.Append("<td>");
                    RuntimeReference(String(Field(item, "runtimeId")));
                    html.Append("</td>");
                    Cell(Display(Field(item, "automationId")));
                    Cell($"{Display(Field(item, "name"))} · {Display(Field(item, "controlType"))}");
                    Cell(Display(Field(item, "isPassword")));
                    Cell(Display(Field(item, "isInTarget")));
                    Cell(Display(Field(item, "repeat")));
                    html.Append("</tr>");
                }
                TableEnd();
            }
            html.Append("</section>");
        }

        private void Events()
        {
            html.Append("<section id=\"events\"><h2>作用域内的 UIA 事件（实际观察）</h2>");
            html.Append("<p>仅展示本次窗口／进程观察范围内记录的事件，按接收顺序排列。事件名称或通知文字不是屏幕阅读器实际播报的证明。</p>");
            var events = Items(Field(report.Observation, "events")).ToArray();
            if (events.Length == 0)
                html.Append("<p>未记录事件或未执行观察；不推断状态消息、LiveRegion 或辅助技术行为正确。</p>");
            else
            {
                TableStart("事件时间线 · WinAppCLI / UIA events", "序号／时间 UTC", "事件 kind", "RuntimeId／快照链接",
                    "AutomationId／名称／控件", "属性 ID", "通知类型／处理方式", "通知文字（可能含敏感信息）", "ActivityId");
                foreach (var item in events)
                {
                    html.Append("<tr>");
                    Cell($"{Display(Field(item, "sequence"))} · {Display(Field(item, "receivedAtUtc"))}");
                    Cell(Display(Field(item, "kind")));
                    html.Append("<td>");
                    RuntimeReference(String(Field(item, "runtimeId")));
                    html.Append("</td>");
                    Cell($"{Display(Field(item, "automationId"))} · {Display(Field(item, "name"))} · {Display(Field(item, "controlType"))}");
                    Cell(Display(Field(item, "propertyId")));
                    Cell($"{Display(Field(item, "notificationKind"))} · {Display(Field(item, "notificationProcessing"))}");
                    Cell(Display(Field(item, "displayString")));
                    Cell(Display(Field(item, "activityId")));
                    html.Append("</tr>");
                }
                TableEnd();
            }
            html.Append("</section>");
        }

        private void Issues(JsonElement value)
        {
            var issues = Items(value).ToArray();
            if (issues.Length == 0)
                return;
            html.Append("<h4>采集问题／限制（原始记录）</h4><ul>");
            foreach (var issue in issues)
                html.Append("<li><pre>").Append(H(Display(issue))).Append("</pre></li>");
            html.Append("</ul>");
        }

        private void Evidence()
        {
            html.Append("<section id=\"evidence\"><h2>原始证据与截图</h2><p>链接只指向本报告目录内的文件。SHA256 与长度绑定已保存的字节，");
            html.Append("用于检测变更，不代表数据作者或应用身份已通过认证。</p>");
            try
            {
                AuditFiles.ResolveArtifact(root, "scan.json");
                html.Append("<p><a href=\"./scan.json\" download>下载原始 scan.json</a>（自动结果，不由补充评估重写）</p>");
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                html.Append("<p>原始 scan.json 不可用；本页不能代替完整证据包。</p>");
            }
            if (artifactLinks.Count == 0)
                html.Append("<p>未保存证据文件；缺失证据不表示检查通过。</p>");
            else
            {
                TableStart("证据清单", "相对路径", "来源", "类型", "字节数", "SHA256", "生成本页时的校验");
                foreach (var link in artifactLinks)
                {
                    html.Append("<tr><th scope=\"row\">");
                    if (link.Url is not null)
                        html.Append("<a href=\"").Append(H(link.Url)).Append("\" download>").Append(H(link.Artifact.Path)).Append("</a>");
                    else
                        html.Append(H(link.Artifact.Path));
                    html.Append("</th>");
                    Cell(link.Artifact.Source);
                    Cell(link.Artifact.MediaType);
                    Cell(link.Artifact.Length.ToString(CultureInfo.InvariantCulture));
                    Cell(link.Artifact.Sha256);
                    Cell(link.Error is null ? "长度与 SHA256 一致（非身份认证）" : "无法验证：" + link.Error);
                    html.Append("</tr>");
                }
                TableEnd();
            }
            var screenshots = artifactLinks.Where(link => link.Artifact.MediaType is "image/png" or "image/jpeg").ToArray();
            html.Append("<h3>截图（可选证据）</h3>");
            if (screenshots.Length == 0)
                html.Append("<p>未提供支持的截图证据。UIA 属性不能替代实际像素或视觉检查。</p>");
            else
            {
                html.Append(locatedScreenshot is null
                    ? "<p>未绘制元素叠加标记：现有证据未证明 UIA 坐标与截图原点、裁剪和 DPI 比例对齐，不能伪造元素位置。</p>"
                    : "<p><a href=\"#locations\">查看整窗控件定位图</a>。仅对已核对坐标与前后身份的控件绘制标记，其余原因显示在问题卡片中。</p>");
                foreach (var screenshot in screenshots)
                    Screenshot(screenshot);
            }
            html.Append("</section>");
        }

        private void Screenshot(ArtifactLink screenshot)
        {
            if (ReferenceEquals(screenshot, locatedScreenshot))
            {
                html.Append("<p><a href=\"#locations\">").Append(H(screenshot.Artifact.Path))
                    .Append(" · 整窗截图与定位</a> · <a href=\"").Append(H(screenshot.Url))
                    .Append("\" download>下载未加标记的原始截图</a></p>");
                return;
            }
            html.Append("<figure><figcaption>").Append(H(screenshot.Artifact.Path)).Append(" · ")
                .Append(H(screenshot.Artifact.Source)).Append(" · SHA256 <code>").Append(H(screenshot.Artifact.Sha256))
                .Append("</code></figcaption>");
            if (screenshot.Url is null)
                html.Append("<p>截图证据无法验证，未显示。</p>");
            else
            {
                html.Append("<p><a href=\"").Append(H(screenshot.Url)).Append("\" download>原始截图文件</a></p>");
                try
                {
                    var bytes = AuditFiles.ReadArtifactBytes(root, screenshot.Artifact, 16 * 1024 * 1024);
                    var png = screenshot.Artifact.MediaType == "image/png";
                    if (png ? !bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                        : !bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 }))
                        throw new InvalidDataException("截图内容与声明的 PNG/JPEG 类型不符。");
                    html.Append("<img alt=\"").Append(H("目标窗口截图；未经视觉判定：" + screenshot.Artifact.Path))
                        .Append("\" src=\"data:").Append(png ? "image/png" : "image/jpeg").Append(";base64,")
                        .Append(Convert.ToBase64String(bytes)).Append("\">");
                }
                catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    html.Append("<p>截图未内嵌（上限 16 MiB；必须可验证且为 PNG/JPEG）：").Append(H(exception.Message)).Append("</p>");
                }
            }
            html.Append("</figure>");
        }
    }
}

using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;

namespace AccessLoop;

public static partial class AuditReportRenderer
{
    private sealed partial class Page
    {
        private readonly Dictionary<int, AuditControlImage> controlImages = [];
        private readonly Dictionary<string, int> markerNumbers = new(StringComparer.Ordinal);
        private ArtifactLink? locatedScreenshot;
        private string? screenshotData;
        private int imageWidth;
        private int imageHeight;

        private void LoadControlImages()
        {
            var candidates = report.Results.Select((result, index) => (Result: result, Index: index))
                .Where(item => item.Result.Status == "rule-failure" || IsCandidateObservation(item.Result)).ToArray();
            if (candidates.Length == 0)
                return;
            var candidateIndexes = candidates.Select(item => item.Index).ToHashSet();
            try
            {
                var screenshot = RequiredArtifact("screenshot.png", "image/png");
                var metadata = ReadJsonArtifact("screenshot.json");
                var original = ReadJsonArtifact("snapshot.json");
                var verification = ReadJsonArtifact(AuditVisuals.VerificationPath);
                if (report.Snapshot is not { } snapshot || !JsonElement.DeepEquals(original, snapshot))
                    throw new InvalidDataException("报告中的基线快照与保存的快照证据不一致，未绘制控件位置。");
                var bytes = AuditFiles.ReadArtifactBytes(root, screenshot.Artifact, 16 * 1024 * 1024);
                if (bytes.Length < 24 || !bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                    throw new InvalidDataException("局部图需要可验证的 PNG 图像和尺寸信息。");
                imageWidth = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
                imageHeight = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
                var mapped = AuditVisuals.Map(report, verification, metadata, imageWidth, imageHeight);
                foreach (var image in mapped.Where(image => candidateIndexes.Contains(image.ResultIndex)))
                {
                    controlImages.Add(image.ResultIndex, image);
                    if (image.Region is not null && image.ElementId is not null && !markerNumbers.ContainsKey(image.ElementId))
                        markerNumbers.Add(image.ElementId, markerNumbers.Count + 1);
                }
                if (markerNumbers.Count > 0)
                {
                    locatedScreenshot = screenshot;
                    screenshotData = "data:image/png;base64," + Convert.ToBase64String(bytes);
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
            {
                foreach (var item in candidates)
                    controlImages[item.Index] = new(item.Index, item.Result.ElementId, "unavailable",
                        "没有可靠的控件局部图：" + exception.Message);
            }
        }

        private ArtifactLink RequiredArtifact(string path, string mediaType)
        {
            if (!artifacts.TryGetValue(path, out var artifact) || artifact.Url is null || artifact.Artifact.MediaType != mediaType)
                throw new InvalidDataException($"缺少或无法验证 {path}，不根据屏幕坐标猜测位置。");
            return artifact;
        }

        private JsonElement ReadJsonArtifact(string path)
        {
            var artifact = RequiredArtifact(path, "application/json");
            using var json = AuditFiles.ParseStrict(AuditFiles.ReadArtifactBytes(root, artifact.Artifact, AuditFiles.MaxJsonBytes));
            return json.RootElement.Clone();
        }

        private string ControlLabel(AuditCheckResult result)
        {
            if (!string.IsNullOrWhiteSpace(result.ElementName))
                return result.ElementName;
            if (result.ElementId is { } id && nodeIndexes.TryGetValue(id, out var index))
            {
                var node = nodes[index];
                var type = String(Field(node, "controlType")) ?? "控件";
                var label = String(Field(node, "name"));
                if (!string.IsNullOrWhiteSpace(label))
                    return label + " · " + type;
                var automationId = String(Field(node, "automationId"));
                return type + (string.IsNullOrWhiteSpace(automationId) ? "（无已公开名称）" : " · " + automationId);
            }
            return "未唯一定位的控件";
        }

        private void ControlPreview(int resultIndex, AuditCheckResult result)
        {
            if (!controlImages.TryGetValue(resultIndex, out var image))
                return;
            if (image.Region is not { } region || image.ElementId is null || screenshotData is null)
            {
                html.Append("<p class=\"notice control-unavailable\">").Append(H(image.Reason)).Append("</p>");
                return;
            }
            var number = markerNumbers[image.ElementId];
            html.Append("<figure class=\"control-preview\" data-element-id=\"").Append(H(image.ElementId))
                .Append("\"><figcaption><strong>控件位置 ").Append(number).Append("：")
                .Append(H(ControlLabel(result))).Append("</strong> · ")
                .Append("<a href=\"#location-").Append(number).Append("\">查看整窗位置</a></figcaption>");
            var padding = 6;
            var left = Math.Max(0, region.Left - padding);
            var top = Math.Max(0, region.Top - padding);
            var right = Math.Min(imageWidth, region.Right + padding);
            var bottom = Math.Min(imageHeight, region.Bottom + padding);
            html.Append("<svg class=\"control-crop\" role=\"img\" aria-label=\"")
                .Append(H($"控件局部图：{ControlLabel(result)}；来自同一张窗口截图"))
                .Append("\" width=\"").Append(N(right - left)).Append("\" height=\"").Append(N(bottom - top))
                .Append("\" viewBox=\"").Append(N(left)).Append(' ').Append(N(top)).Append(' ')
                .Append(N(right - left)).Append(' ').Append(N(bottom - top)).Append("\">")
                .Append("<use href=\"#audit-window-image\"/>");
            RectElement(region, "crop-border");
            html.Append("</svg><details><summary>定位依据</summary><p class=\"muted\">")
                .Append(H(image.Reason)).Append("</p></details></figure>");
        }

        private void WindowLocations()
        {
            if (locatedScreenshot is null || screenshotData is null)
                return;
            var controls = controlImages.Values.Where(image => image.Region is not null && image.ElementId is not null)
                .GroupBy(image => image.ElementId!, StringComparer.Ordinal).Select(group => group.First()).ToArray();
            html.Append("""
                <section id="locations"><h2>规则失败与待复核候选的控件位置</h2>
                <p>编号对应控件，同一控件的多条记录共用编号。点击框内编号返回对应卡片；点击卡片中的“查看整窗位置”突出对应框。
                局部图裁自下面同一张原始窗口截图，不重新点击、滚动或截取另一个状态。</p>
                <p class="muted">物理截图原点、像素尺寸、RuntimeId 与控件边界已核对。前后状态一致仍不是原子采集；
                这是定位辅助，不是视觉无障碍判定。</p>
                <figure class="window-locations"><figcaption>选定窗口 · 已核对位置的控件</figcaption>
                """);
            html.Append("<svg class=\"window-map\" viewBox=\"0 0 ").Append(imageWidth).Append(' ').Append(imageHeight)
                .Append("\" role=\"group\" aria-label=\"窗口截图及规则失败与候选的控件定位链接\">")
                .Append("<image id=\"audit-window-image\" width=\"").Append(imageWidth).Append("\" height=\"").Append(imageHeight)
                .Append("\" href=\"").Append(screenshotData).Append("\"/>");
            foreach (var image in controls)
            {
                var number = markerNumbers[image.ElementId!];
                var region = image.Region!;
                var label = $"控件位置 {number}：{ControlLabel(report.Results[image.ResultIndex])}，查看对应记录";
                html.Append("<g id=\"location-").Append(number).Append("\" class=\"issue-marker\">")
                    .Append("<a href=\"#result-").Append(image.ResultIndex).Append("\" tabindex=\"0\" aria-label=\"")
                    .Append(H(label)).Append("\"><title>").Append(H(label)).Append("</title>");
                RectElement(region, "marker-outline");
                RectElement(region, "marker-border");
                var badgeX = Math.Clamp(region.Left, 0, Math.Max(0, imageWidth - 28));
                var badgeY = Math.Clamp(region.Top - 24, 0, Math.Max(0, imageHeight - 24));
                html.Append("<rect x=\"").Append(N(badgeX)).Append("\" y=\"").Append(N(badgeY))
                    .Append("\" width=\"28\" height=\"24\" class=\"marker-badge\"/>")
                    .Append("<text x=\"").Append(N(badgeX + 14)).Append("\" y=\"").Append(N(badgeY + 17))
                    .Append("\" text-anchor=\"middle\" class=\"marker-number\">").Append(number)
                    .Append("</text></a></g>");
            }
            html.Append("</svg></figure><ul class=\"location-legend\">");
            foreach (var image in controls)
            {
                var number = markerNumbers[image.ElementId!];
                html.Append("<li><a href=\"#location-").Append(number).Append("\">").Append(number).Append(" · ")
                    .Append(H(ControlLabel(report.Results[image.ResultIndex]))).Append("</a> · ")
                    .Append("<a href=\"#result-").Append(image.ResultIndex).Append("\">记录详情</a></li>");
            }
            html.Append("</ul><p>定位证据：");
            References(["screenshot.png", "screenshot.json", "snapshot.json", AuditVisuals.VerificationPath]);
            html.Append("</p></section>");
        }

        private void RectElement(AuditImageRect region, string css) =>
            html.Append("<rect x=\"").Append(N(region.Left)).Append("\" y=\"").Append(N(region.Top))
                .Append("\" width=\"").Append(N(region.Width)).Append("\" height=\"").Append(N(region.Height))
                .Append("\" class=\"").Append(css).Append("\"/>");

        private static string N(double number) => number.ToString("0.###", CultureInfo.InvariantCulture);
    }
}

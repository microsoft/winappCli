using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AccessLoop;

public sealed record AuditImageRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

public sealed record AuditControlImage(
    int ResultIndex, string? ElementId, string Status, string Reason, AuditImageRect? Region = null);

public static class AuditVisuals
{
    public const string VerificationPath = "snapshot-after-screenshot.json";

    public static bool IsCandidate(AuditCheckResult result) => result.Status == "rule-failure"
        || (result.CheckId == "text.contrast" && result.Details.GetValueOrDefault("comparison") == "below-threshold");

    public static IReadOnlyList<AuditControlImage> Map(
        AuditReport report, JsonElement verification, JsonElement screenshot, int imageWidth, int imageHeight)
    {
        var candidates = report.Results.Select((result, index) => (Result: result, Index: index))
            .Where(item => IsCandidate(item.Result)).ToArray();
        try
        {
            if (report.Snapshot is not { } snapshot)
                throw new InvalidDataException("未取得基线 UIA 快照，不能定位控件。");
            AuditRules.ValidateSnapshot(snapshot, report.Target);
            AuditRules.ValidateSnapshot(verification, report.Target);
            if (AuditJson.Integer(screenshot, "schemaVersion") != 1
                || AuditJson.Integer(screenshot, "processId") != report.Target.ProcessId
                || AuditJson.Long(screenshot, "windowHandle") != report.Target.WindowHandle
                || AuditJson.String(screenshot, "captureMethod") != "windows-graphics-capture"
                || AuditJson.Boolean(screenshot, "activationRequested") != false
                || AuditJson.OptionalString(screenshot, "coordinateMappingStatus") != "verified")
                throw new InvalidDataException("截图缺少已核对的物理像素坐标，不能假定窗口外框就是截图原点。");
            var boundsBefore = Rect(AuditFiles.Property(screenshot, "captureBoundsBefore"));
            var boundsAfter = Rect(AuditFiles.Property(screenshot, "captureBoundsAfter"));
            if (boundsBefore is null || boundsBefore != boundsAfter
                || imageWidth <= 0 || imageHeight <= 0 || (long)imageWidth * imageHeight > 16_000_000
                || AuditJson.Integer(screenshot, "width") != imageWidth || AuditJson.Integer(screenshot, "height") != imageHeight
                || boundsBefore.Width != imageWidth || boundsBefore.Height != imageHeight)
                throw new InvalidDataException("截图尺寸与物理边界不一致，或窗口在取帧期间移动／缩放，未绘制标记。");
            var started = Time(screenshot, "captureStartedAtUtc");
            var captured = Time(screenshot, "capturedAtUtc");
            if (Time(snapshot, "capturedAtUtc") > started || started > captured
                || captured > Time(verification, "capturedAtUtc")
                || Time(verification, "capturedAtUtc") > report.FinishedAtUtc)
                throw new InvalidDataException("快照没有按时间包围这次截图，不能把其他时间的控件位置当成截图位置。");
            var baselineNodes = AuditJson.Array(snapshot, "nodes").EnumerateArray().ToArray();
            var verificationNodes = AuditJson.Array(verification, "nodes").EnumerateArray().ToArray();
            var baselineRoot = baselineNodes.Single(node => AuditJson.OptionalString(node, "parentId") is null);
            var verificationRoot = verificationNodes.Single(node => AuditJson.OptionalString(node, "parentId") is null);
            if (!AuditRules.SameRuntimeId(AuditJson.OptionalString(snapshot, "rootRuntimeId"), AuditJson.OptionalString(verification, "rootRuntimeId"))
                || Rect(AuditFiles.Property(baselineRoot, "bounds")) is not { } rootBounds
                || rootBounds != Rect(AuditFiles.Property(verificationRoot, "bounds"))
                || IdentityNotRechecked(snapshot) || IdentityNotRechecked(verification))
                throw new InvalidDataException("截图前后的窗口根身份或位置无法核对，未绘制标记。");

            var nodesById = baselineNodes.ToDictionary(node => AuditJson.String(node, "id"), StringComparer.Ordinal);
            var beforeByRuntime = baselineNodes.Where(node => AuditJson.OptionalString(node, "runtimeId") is not null)
                .ToLookup(node => RuntimeId(node), StringComparer.Ordinal);
            var afterByRuntime = verificationNodes.Where(node => AuditJson.OptionalString(node, "runtimeId") is not null)
                .ToLookup(node => RuntimeId(node), StringComparer.Ordinal);
            return candidates.Select(item =>
            {
                var result = item.Result;
                AuditControlImage Missing(string reason) => new(item.Index, result.ElementId, "unavailable", reason);
                if (result.ElementId is null || !nodesById.TryGetValue(result.ElementId, out var node))
                    return Missing("本条发现未唯一关联到快照节点；不会猜测一个控件或截图位置。");
                var runtimeId = RuntimeId(node);
                if (runtimeId is null || beforeByRuntime[runtimeId].Count() != 1 || afterByRuntime[runtimeId].Count() != 1)
                    return Missing("截图前后没有唯一匹配的 RuntimeId；控件可能已消失、重建或不在采集范围内。");
                var after = afterByRuntime[runtimeId].Single();
                if (AuditJson.String(node, "controlType") != AuditJson.String(after, "controlType")
                    || AuditJson.OptionalString(node, "automationId") != AuditJson.OptionalString(after, "automationId")
                    || AuditJson.OptionalString(node, "name") != AuditJson.OptionalString(after, "name"))
                    return Missing("控件类型、标识或已公开名称在截图前后发生变化，不能确认是同一个页面状态。");
                if (AuditJson.Boolean(node, "isOffscreen") != false || AuditJson.Boolean(after, "isOffscreen") != false)
                    return Missing("控件在画面外，或提供程序没有确认其可见状态；不会自动滚动或打开它。");
                var rect = Rect(AuditFiles.Property(node, "bounds"));
                if (rect is null || rect != Rect(AuditFiles.Property(after, "bounds")))
                    return Missing("控件边界无效或在截图前后移动／缩放，未生成局部图。");
                if (result.CheckId == "axe.scan")
                {
                    var findings = report.Axe?.Findings.Where(finding =>
                        finding.RuleId == result.SourceRuleId
                        && AuditRules.SameRuntimeId(finding.ElementProperties.GetValueOrDefault("RuntimeId"), runtimeId)).Take(2).ToArray();
                    if (findings is not { Length: 1 }
                        || ParseAxeBounds(findings[0].ElementProperties.GetValueOrDefault("BoundingRectangle")) != rect)
                        return Missing("Axe 发现的控件边界没有与快照边界一致匹配，未将其他位置的图像附到本条问题。");
                }
                var left = Math.Max(0, Math.Floor(rect.Left - boundsBefore.Left));
                var top = Math.Max(0, Math.Floor(rect.Top - boundsBefore.Top));
                var right = Math.Min(imageWidth, Math.Ceiling(rect.Right - boundsBefore.Left));
                var bottom = Math.Min(imageHeight, Math.Ceiling(rect.Bottom - boundsBefore.Top));
                if (right <= left || bottom <= top)
                    return Missing("控件边界不与本次截图相交，未生成局部图。");
                var clipped = rect.Left < boundsBefore.Left || rect.Top < boundsBefore.Top
                    || rect.Right > boundsBefore.Right || rect.Bottom > boundsBefore.Bottom;
                return new AuditControlImage(item.Index, result.ElementId, clipped ? "clipped" : "located",
                    clipped ? "仅展示控件与截图相交的部分；不是完整控件图。"
                        : "同一窗口图像中的控件区域；RuntimeId 与边界在截图前后核对一致，不等于原子采集或内容正确性验证。",
                    new(left, top, right - left, bottom - top));
            }).ToArray();
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or OverflowException)
        {
            return candidates.Select(item => new AuditControlImage(item.Index, item.Result.ElementId,
                "unavailable", exception.Message)).ToArray();
        }
    }

    public static AuditImageRect? Rect(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return null;
        var numbers = new List<double>(4);
        foreach (var name in new[] { "left", "top", "width", "height" })
        {
            var field = AuditFiles.Property(value, name);
            if (field.ValueKind != JsonValueKind.Number || !field.TryGetDouble(out var number)
                || !double.IsFinite(number) || Math.Abs(number) > 10_000_000)
                return null;
            numbers.Add(number);
        }
        return numbers[2] <= 0 || numbers[3] <= 0 ? null : new(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    public static AuditImageRect? ParseAxeBounds(string? value)
    {
        if (value is null || value.Length > 1024)
            return null;
        var match = Regex.Match(value,
            @"^\[l=(-?\d+(?:\.\d+)?),t=(-?\d+(?:\.\d+)?),r=(-?\d+(?:\.\d+)?),b=(-?\d+(?:\.\d+)?)\]$",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!match.Success)
            return null;
        var values = match.Groups.Cast<Group>().Skip(1)
            .Select(group => double.Parse(group.Value, CultureInfo.InvariantCulture)).ToArray();
        if (values.Any(number => !double.IsFinite(number) || Math.Abs(number) > 10_000_000)
            || values[2] <= values[0] || values[3] <= values[1])
            return null;
        return new(values[0], values[1], values[2] - values[0], values[3] - values[1]);
    }

    private static DateTimeOffset Time(JsonElement value, string name) =>
        AuditFiles.Property(value, name) is { ValueKind: JsonValueKind.String } field && field.TryGetDateTimeOffset(out var result)
            ? result : throw new InvalidDataException($"截图位置证据缺少时间信息：{name}。");

    private static string? RuntimeId(JsonElement node) =>
        AuditJson.OptionalString(node, "runtimeId")?.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();

    private static bool IdentityNotRechecked(JsonElement snapshot) =>
        AuditJson.Array(snapshot, "issues").EnumerateArray().Any(issue =>
            AuditFiles.Property(issue, "code") is { ValueKind: JsonValueKind.String } code
                && code.GetString() is "root_identity_not_rechecked" or "target_changed" or "target_mismatch");
}

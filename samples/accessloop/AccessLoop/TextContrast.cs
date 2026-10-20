using System.Globalization;
using System.Text.Json;

namespace AccessLoop;

public static class TextContrast
{
    public static double Ratio(uint foregroundColorRef, uint backgroundColorRef)
    {
        if (foregroundColorRef > 0x00FFFFFF || backgroundColorRef > 0x00FFFFFF)
            throw new ArgumentOutOfRangeException(nameof(foregroundColorRef), "Expected 24-bit COLORREF colors, not ARGB.");
        var foreground = Luminance(foregroundColorRef);
        var background = Luminance(backgroundColorRef);
        return (Math.Max(foreground, background) + 0.05) / (Math.Min(foreground, background) + 0.05);
    }

    public static double Threshold(double points, double weight)
    {
        if (!double.IsFinite(points) || points <= 0 || points > 4096
            || !double.IsFinite(weight) || weight < 1 || weight > 1000 || weight != Math.Truncate(weight))
            throw new ArgumentOutOfRangeException(nameof(points), "A reliable positive point size and integer font weight are required.");
        return points >= 18 || (points >= 14 && weight >= 700) ? 3 : 4.5;
    }

    public static IEnumerable<AuditCheckResult> Evaluate(JsonElement snapshot)
    {
        var produced = false;
        foreach (var node in AuditJson.Array(snapshot, "nodes").EnumerateArray())
        {
            var type = AuditJson.String(node, "controlType");
            var runs = AuditJson.Array(node, "textRuns");
            if (runs.GetArrayLength() == 0)
            {
                if (type is "Edit" or "Text" or "Document")
                {
                    produced = true;
                    yield return Result(node, "inconclusive", "No text formatting ranges were exposed by this provider.");
                }
                continue;
            }
            foreach (var run in runs.EnumerateArray())
            {
                produced = true;
                var runId = AuditJson.String(run, "id");
                if (AuditJson.Boolean(node, "isPassword") != false)
                {
                    yield return Result(node, "inconclusive", "Password or unknown privacy: text attributes are not evaluated.", runId);
                    continue;
                }
                if (AuditJson.Boolean(node, "isEnabled") == false || AuditJson.Boolean(node, "isOffscreen") == true
                    || FieldBoolean(run, "isHidden") == true)
                {
                    yield return Result(node, "not-tested", "Disabled, offscreen or hidden text is excluded from this contrast check.", runId);
                    continue;
                }
                var foreground = FieldNumber(run, "foregroundColor");
                var background = FieldNumber(run, "backgroundColor");
                var size = FieldNumber(run, "fontSize");
                var weight = FieldNumber(run, "fontWeight");
                if (!IsColor(foreground) || !IsColor(background))
                {
                    yield return Result(node, "inconclusive",
                        "Mixed, unsupported, withheld, invalid or unreadable color data; no colors are assumed.", runId);
                    continue;
                }
                var ratio = Ratio((uint)foreground!.Value, (uint)background!.Value);
                var fontKnown = size is > 0 and <= 4096 && weight is >= 1 and <= 1000
                    && weight == Math.Truncate(weight.Value);
                double? threshold = fontKnown ? Threshold(size!.Value, weight!.Value) : null;
                var below = threshold is { } minimum && ratio < minimum;
                // Other text can include logos or incidental/decorative content; UIA does not identify those exceptions.
                var activeEdit = type == "Edit" && AuditJson.Boolean(node, "isKeyboardFocusable") == true
                    && AuditJson.Boolean(node, "isEnabled") == true && AuditJson.Boolean(node, "isOffscreen") == false
                    && FieldBoolean(run, "isHidden") == false;
                var status = below && activeEdit ? "rule-failure" : "observed";
                var details = new Dictionary<string, string>
                {
                    ["textRun"] = runId,
                    ["foregroundColorRef"] = $"0x{(uint)foreground.Value:X6}",
                    ["backgroundColorRef"] = $"0x{(uint)background.Value:X6}",
                    ["foregroundRgb"] = Rgb((uint)foreground.Value),
                    ["backgroundRgb"] = Rgb((uint)background.Value),
                    ["fontSizePoints"] = size?.ToString("R", CultureInfo.InvariantCulture) ?? "unknown",
                    ["fontWeight"] = weight?.ToString("R", CultureInfo.InvariantCulture) ?? "unknown",
                    ["ratio"] = ratio.ToString("R", CultureInfo.InvariantCulture),
                    ["threshold"] = threshold?.ToString("R", CultureInfo.InvariantCulture) ?? "undetermined-font-metrics",
                    ["comparison"] = threshold is null ? "not-evaluated" : below ? "below-threshold" : "meets-threshold",
                    ["basis"] = "Provider text attributes, not a measurement of all rendered pixels.",
                    ["applicability"] = activeEdit ? "Enabled, focusable, non-offscreen Edit control; provider data must describe its actual text."
                        : "Visibility or content exceptions are not established; numeric comparison is an observation, not a WCAG verdict."
                };
                var comparison = threshold is { } knownThreshold
                    ? $"{(below ? "is below" : "meets")} the {knownThreshold.ToString("0.#", CultureInfo.InvariantCulture)}:1 numeric threshold."
                    : "is recorded without a threshold verdict because font metrics are unavailable.";
                yield return Result(node, status,
                    $"Provider text contrast {ratio.ToString("0.###", CultureInfo.InvariantCulture)}:1 " +
                    comparison,
                    runId) with { Details = details };
            }
        }
        if (!produced)
            yield return new AuditCheckResult
            {
                CheckId = "text.contrast", Status = "inconclusive",
                Summary = "No eligible text formatting ranges were available. No text-contrast compliance conclusion is possible.",
                Evidence = ["snapshot.json"]
            };
    }

    private static AuditCheckResult Result(JsonElement node, string status, string summary, string? runId = null) => new()
    {
        CheckId = "text.contrast", Status = status, Summary = summary,
        ElementId = AuditJson.String(node, "id"), ElementName = AuditJson.OptionalString(node, "name"),
        Evidence = ["snapshot.json"],
        Details = runId is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["textRun"] = runId }
    };

    private static double? FieldNumber(JsonElement run, string name)
    {
        if (!run.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.Object)
            return null;
        if (AuditJson.OptionalString(field, "status") != "value"
            || !field.TryGetProperty("number", out var number) || number.ValueKind != JsonValueKind.Number
            || !number.TryGetDouble(out var value) || !double.IsFinite(value))
            return null;
        return value;
    }

    private static bool? FieldBoolean(JsonElement run, string name)
    {
        if (!run.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.Object
            || AuditJson.OptionalString(field, "status") != "value")
            return null;
        return AuditJson.Boolean(field, "boolean");
    }

    private static bool IsColor(double? color) =>
        color is >= 0 and <= 0xFFFFFF && color == Math.Truncate(color.Value);

    private static string Rgb(uint color) => $"#{color & 255:X2}{(color >> 8) & 255:X2}{(color >> 16) & 255:X2}";

    private static double Luminance(uint color) =>
        0.2126 * Channel(color & 255) + 0.7152 * Channel((color >> 8) & 255) + 0.0722 * Channel((color >> 16) & 255);

    private static double Channel(uint channel)
    {
        var srgb = channel / 255.0;
        return srgb <= 0.04045 ? srgb / 12.92 : Math.Pow((srgb + 0.055) / 1.055, 2.4);
    }
}

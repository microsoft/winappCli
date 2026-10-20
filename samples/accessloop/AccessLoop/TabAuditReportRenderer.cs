using System.Net;
using System.Text;

namespace AccessLoop;

internal static class TabAuditReportRenderer
{
    public static void Write(string path, TabAuditReport report)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var html = new StringBuilder("""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'">
            <title>AccessLoop standard-tab scan</title>
            <style>
            :root{font-family:system-ui,sans-serif;line-height:1.6;color:#202124;background:#fff}
            body{max-width:75rem;margin:auto;padding:2rem}h1,h2,h3{line-height:1.3}
            a{color:#0753a0}:focus-visible{outline:3px solid #0753a0;outline-offset:3px}
            table{border-collapse:collapse;width:100%;text-align:left}td,th{border:1px solid #aeb8c4;padding:.6rem;vertical-align:top}
            article,aside{border:1px solid #aeb8c4;padding:1rem;margin-block:1.5rem}
            article img{max-width:100%;max-height:28rem;object-fit:contain}
            code,td,p,li{overflow-wrap:anywhere}summary{cursor:pointer;font-weight:600}
            .notice{background:#f3f5f9;padding:1rem}.failure{border-inline-start:5px solid #a51b27}
            </style></head><body><main><h1>AccessLoop standard-tab scan</h1>
            """);
        html.Append("<p><strong>Overall: ").Append(E(report.Status)).Append("</strong> &middot; Navigation: ")
            .Append(E(report.NavigationStatus)).Append(" &middot; Capture: ").Append(E(report.CaptureStatus))
            .Append("</p>");
        if (!report.HasAuditResults)
            html.Append("<aside class=\"notice\"><strong>Not evaluated: no page audit results are available.</strong> ")
                .Append("The run stopped before usable page evidence was collected. This is not a zero-issue result.</aside>");
        else
            html.Append("<p>").Append(report.CapturedPageCount).Append(" page states captured; ")
                .Append(report.RuleFailureCount).Append(" rule-failure occurrences; ")
                .Append(report.Skipped.Count).Append(" skipped or unsupported entries.</p>");
        html.Append("<p class=\"notice\">").Append(E(report.CoverageNotice)).Append("</p>")
            .Append("<p>Target: PID ").Append(report.Target.ProcessId).Append(", HWND ").Append(report.Target.WindowHandle)
            .Append(". Evidence is local and may contain sensitive application labels or screenshots. Review it before sharing.</p>");
        if (report.Error is not null)
            html.Append("<aside><strong>Run stopped:</strong> ").Append(E(report.Error)).Append("</aside>");
        if (report.NavigationStatus == "not-applicable")
            html.Append("<p>No standard tab groups were discovered. Only the current page was audited; no navigation was performed.</p>");
        html.Append("<h2>Page evidence</h2><p><a href=\"scan.json\">Aggregate JSON</a>. ")
            .Append("Each page keeps its own unchanged checks, raw UIA/Axe evidence and assessment-compatible scan.json.</p>")
            .Append("<table><thead><tr><th>Page state</th><th>Navigation / capture</th><th>Failures</th><th>Evidence</th></tr></thead><tbody>");
        foreach (var page in report.Pages)
        {
            html.Append("<tr><td><a href=\"#").Append(E(page.Id)).Append("\">").Append(E(Label(page)))
                .Append("</a></td><td>").Append(E(page.Status)).Append(" / ").Append(E(page.CaptureStatus))
                .Append("</td><td>").Append(page.CaptureStatus == "blocked" ? "Not evaluated" : page.RuleFailureCount.ToString())
                .Append("</td><td><a href=\"")
                .Append(Url(Path.Combine(page.Directory, "report.html"))).Append("\">Page report</a> &middot; <a href=\"")
                .Append(Url(Path.Combine(page.Directory, "scan.json"))).Append("\">Page JSON</a></td></tr>");
        }
        html.Append("</tbody></table>");
        foreach (var page in report.Pages)
        {
            var scanPath = AuditFiles.ResolveArtifact(root, Path.Combine(page.Directory, "scan.json"));
            var verified = AuditFiles.ReadVerifiedDocument(scanPath);
            if (verified.Sha256 != page.ScanSha256)
                throw new InvalidDataException("Per-page audit changed before the aggregate report was rendered.");
            html.Append("<article id=\"").Append(E(page.Id)).Append("\"><h3>").Append(E(Label(page))).Append("</h3>");
            if (page.Error is not null)
                html.Append("<p class=\"notice\">").Append(E(page.Error)).Append("</p>");
            var screenshot = verified.Report.Artifacts.SingleOrDefault(artifact => artifact.Path == "screenshot.png" && artifact.MediaType == "image/png");
            if (screenshot is not null)
                html.Append("<a href=\"").Append(Url(Path.Combine(page.Directory, screenshot.Path))).Append("\"><img loading=\"lazy\" src=\"")
                    .Append(Url(Path.Combine(page.Directory, screenshot.Path))).Append("\" alt=\"Window screenshot for ")
                    .Append(E(Label(page))).Append("\"></a>");
            foreach (var failure in verified.Report.Results.Where(result => result.Status == "rule-failure"))
                html.Append("<p class=\"failure\"><strong>").Append(E(failure.SourceRuleId ?? failure.CheckId)).Append("</strong>: ")
                    .Append(E(failure.Summary)).Append(" <code>").Append(E(failure.ElementName ?? failure.ElementId ?? "Unmapped element"))
                    .Append("</code></p>");
            if (page.CaptureStatus == "blocked")
                html.Append("<p>Not evaluated: this page capture has no usable audit results.</p>");
            else if (page.RuleFailureCount == 0)
                html.Append("<p>No rule failures were reported in this page capture. Incomplete and unsupported checks remain separate.</p>");
            html.Append("</article>");
        }
        html.Append("<h2>Navigation coverage</h2>");
        if (report.NavigationStatus == "partial")
            html.Append("<p>Navigation did not complete. Unvisited states are not counted as passed.</p>");
        else if (report.Skipped.Count == 0)
            html.Append("<p>No discovered entries were skipped within the declared navigation limits.</p>");
        foreach (var skip in report.Skipped)
            html.Append("<p><code>").Append(E(skip.ItemRuntimeId ?? skip.ContainerRuntimeId ?? "Discovery"))
                .Append("</code>: ").Append(E(skip.Reason)).Append("</p>");
        html.Append("<h2>Selection restoration</h2>");
        if (report.Restorations.Count == 0)
            html.Append("<p>No tab selection restoration was performed.</p>");
        foreach (var restore in report.Restorations)
            html.Append("<p><code>").Append(E(restore.ContainerRuntimeId)).Append("</code>: <strong>").Append(E(restore.Status))
                .Append("</strong>").Append(restore.Error is null ? "" : " &mdash; " + E(restore.Error)).Append("</p>");
        html.Append("<details><summary>Native discovery and exact selection receipts</summary><ul>");
        foreach (var artifact in report.Artifacts.Where(artifact => artifact.Path.StartsWith("navigation" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            html.Append("<li><a href=\"").Append(Url(artifact.Path)).Append("\">").Append(E(artifact.Path)).Append("</a></li>");
        html.Append("</ul></details></main></body></html>");
        using var stream = AuditFiles.OpenNew(path);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(html);
    }

    private static string Label(TabAuditPage page) => page.SelectionPath.Count == 0 ? page.Id + ": current page"
        : page.Id + ": " + string.Join(" > ", page.SelectionPath.Select(item =>
            string.IsNullOrWhiteSpace(item.AutomationId) ? "Tab " + item.ItemRuntimeId : item.AutomationId));

    private static string Url(string path) => E(string.Join("/", AuditFiles.NormalizeArtifactPath(path)
        .Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString)));
    private static string E(string? text) => WebUtility.HtmlEncode(text ?? "");
}

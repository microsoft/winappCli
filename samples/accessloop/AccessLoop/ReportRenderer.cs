using System.Net;
using System.Text;

namespace AccessLoop;

public static class ReportRenderer
{
    public static void Write(string path, RunReport report)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var html = new StringBuilder("""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width,initial-scale=1">
              <meta name="color-scheme" content="light">
              <title>AccessLoop | Accessibility workbench</title>
              <style>
                *{box-sizing:border-box}body{margin:0;background:#f4f6fa;color:#19283d;font:16px/1.6 system-ui,sans-serif}
                main,footer{max-width:1180px;margin:auto;padding:32px 24px}footer{padding-top:0;color:#4d5d72}
                h1,h2,h3,h4,h5{line-height:1.3}h1{font-size:clamp(1.8rem,4vw,2.6rem);margin:.3em 0}
                h2{font-size:1.5rem;margin-top:0}h3{font-size:1.15rem}h4{font-size:1.1rem}h5{font-size:1rem}
                a{color:#174eb6;text-underline-offset:.15em}a:hover{text-decoration-thickness:2px}
                :focus-visible{outline:3px solid #174eb6;outline-offset:4px}p{margin:.6em 0 1em}
                code{font:.92em ui-monospace,Consolas,monospace;overflow-wrap:anywhere;white-space:pre-wrap}
                .eyebrow{color:#4d5d72;font-weight:650;letter-spacing:.06em;text-transform:uppercase;font-size:.8rem}
                .muted{color:#4d5d72}.skip{position:absolute;top:-100px;left:16px;background:white;padding:12px;z-index:1}
                .skip:focus{top:8px}.panel,.finding,details{background:white;border:1px solid #cbd4e1;border-radius:12px}
                .panel{padding:24px;margin:24px 0}.outcomes,.metrics,.screenshots{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:20px}
                .outcomes h2{font-size:1rem;color:#4d5d72}.outcome{font-size:1.6rem;font-weight:700;margin:.2em 0}
                .pass{color:#17613d}.fail{color:#a12a22}.pending{color:#73500c}
                .badge{display:inline-block;border:1px solid currentColor;border-radius:5px;padding:2px 8px;font-size:.8rem;font-weight:650}
                .metrics{grid-template-columns:repeat(4,minmax(0,1fr));border-top:1px solid #dbe1eb;padding-top:16px;margin-bottom:0}
                dt{color:#4d5d72;font-size:.9rem}dd{margin:0 0 .8em;overflow-wrap:anywhere}.metrics dd{font-size:1.4rem;font-weight:650}
                .notice{border-left:4px solid #96670b;background:#fff8e6;padding:12px 16px;margin:16px 0;overflow-wrap:anywhere}
                .links{display:flex;flex-wrap:wrap;gap:8px 20px;padding:0;list-style:none}.section{margin-top:36px}
                .finding{padding:20px;margin:16px 0;overflow-wrap:anywhere}.selected{border:2px solid #174eb6}
                .finding h3{margin:0 0 12px}.finding-id{user-select:all}.finding .badge{margin-right:8px}
                .finding dl{display:grid;grid-template-columns:160px minmax(0,1fr);gap:6px 16px;margin-bottom:0}
                .finding dd{margin:0}.guidance{border-top:1px solid #dbe1eb;margin-top:16px;padding-top:8px}
                details{margin:16px 0;overflow-wrap:anywhere}summary{cursor:pointer;padding:18px 20px}
                summary:hover{background:#f0f4fb}summary strong{margin-right:12px}.case-body{padding:0 20px 20px}
                .phase{border-top:1px solid #dbe1eb;padding-top:8px;margin-top:24px}.table-scroll{overflow-x:auto}
                table{width:100%;border-collapse:collapse;font-size:.9rem}caption{text-align:left;color:#4d5d72;padding:8px 0}
                th,td{text-align:left;vertical-align:top;padding:10px;border-bottom:1px solid #dbe1eb;overflow-wrap:anywhere}
                th{background:#f0f4fa}.focus-trace{padding-left:24px}.screenshots figure{margin:0}
                img{display:block;width:100%;height:auto;border:1px solid #cbd4e1;border-radius:6px}figcaption{font-size:.85rem;color:#4d5d72;padding-top:6px}
                .snapshots li{margin:16px 0;overflow-wrap:anywhere}
                @media(max-width:720px){main,footer{padding:20px 16px}.panel{padding:18px}.outcomes,.screenshots{grid-template-columns:1fr}
                  .metrics{grid-template-columns:repeat(2,minmax(0,1fr))}.finding dl{grid-template-columns:1fr;gap:0}.finding dd{margin-bottom:12px}}
                @media print{body{background:white}.panel,.finding,details{break-inside:avoid}.skip{display:none}}
              </style>
            </head>
            <body>
            <a class="skip" href="#findings">Skip to finding inventory</a>
            <main>
            <header>
              <p class="eyebrow">AccessLoop / Accessibility workbench</p>
            """);
        html.Append($"<h1>{E(report.SuiteName)}</h1>");
        html.Append("<p>Scan every case. Choose one finding. Verify one repair against the whole suite.</p></header>");
        html.Append("<section class=\"panel\" aria-label=\"Run summary\"><div class=\"outcomes\">");
        html.Append($"<div><h2>Action outcome</h2><p class=\"outcome {StatusClass(report.Status)}\">{E(report.Status)}</p><p>{E(ActionExplanation(report.Status))}</p></div>");
        html.Append($"<div><h2>Whole-suite status</h2><p class=\"outcome {StatusClass(report.SuiteStatus)}\">{E(report.SuiteStatus)}</p><p>{E(SuiteExplanation(report.SuiteStatus))}</p></div></div>");
        html.Append("<dl class=\"metrics\">");
        html.Append($"<div><dt>Grouped baseline findings</dt><dd>{report.Findings.Count}</dd></div>");
        html.Append($"<div><dt>Latest reported findings</dt><dd>{report.RemainingFindings.Count}</dd></div>");
        html.Append($"<div><dt>Baseline cases recorded</dt><dd>{report.Before.Count}</dd></div>");
        html.Append($"<div><dt>Rebuilt cases recorded</dt><dd>{report.After.Count}</dd></div></dl>");
        if (report.SelectedFindingId is not null)
            html.Append($"<p>Selected finding: <code class=\"finding-id\">{E(report.SelectedFindingId)}</code></p>");
        html.Append($"<p>Source edit: <strong>{(report.PatchRolledBack ? "rolled back" : report.PatchApplied ? "applied; not rolled back" : "not applied")}</strong>. Proposal source: {E(report.RepairSource ?? "not invoked")}.</p>");
        if (report.PatchRolledBack)
            html.Append("<p class=\"notice\">The source patch was rolled back. Rebuilt observations describe the attempted repair, not the restored source. Run check again to measure the restored app.</p>");
        if (report.Error is not null)
            html.Append($"<p class=\"notice\">{E(report.Error)}</p>");
        if (report.VerificationErrors.Count > 0)
        {
            html.Append("<h3>Verification issues</h3><ul>");
            foreach (var error in report.VerificationErrors)
                html.Append($"<li>{E(error)}</li>");
            html.Append("</ul>");
        }
        html.Append("<ul class=\"links\"><li><a href=\"./report.json\">Machine-readable report</a></li>");
        foreach (var file in new[] { "source-candidates.json", "proposal.json", "repair.diff", "source.original.txt" })
        {
            if (EvidenceUrl(directory, file) is { } url)
                html.Append($"<li><a href=\"{E(url)}\">{E(file)}</a></li>");
        }
        html.Append("</ul>");
        if (EvidenceUrl(directory, "source.original.txt") is not null)
            html.Append("<p class=\"notice\">Manual recovery: <code>source.original.txt</code> preserves the original source bytes if the run is forcibly interrupted. Only restore it after reviewing the current source and any concurrent edits. Automatic rollback refuses to overwrite source modified after the patch.</p>");
        html.Append("</section>");

        AppendInventory(html, report);
        AppendCases(html, directory, report);

        html.Append("<section class=\"panel\" aria-labelledby=\"inputs-heading\"><h2 id=\"inputs-heading\">Inputs and provenance</h2>");
        html.Append($"<p>Input: <code>{E(report.InputPath)}</code><br>Report schema: {report.SchemaVersion}<br>Rule version: <code>{E(report.RuleVersion)}</code><br>Native winapp: {E(report.WinappVersion)}</p>");
        html.Append("<p>These snapshots preserve the input bytes used for this run. A later repair starts a fresh baseline; this report is not authorization to reuse old results.</p><ol class=\"snapshots\">");
        foreach (var input in report.Inputs)
        {
            html.Append("<li>");
            AppendEvidenceLink(html, directory, input.EvidencePath, input.Path);
            html.Append($"<br>SHA-256: <code>{E(input.Sha256)}</code></li>");
        }
        html.Append("</ol></section></main>");
        html.Append("<footer><p>This is a static evidence and review page. It cannot run commands or apply changes. Keep the entire evidence directory together.</p>");
        html.Append("<p>Axe.Windows scans captured window states; separate assertions exercise the specified keyboard actions. Even a passed suite is not proof of whole-app accessibility or WCAG compliance. Evidence can contain window contents, source text, and local paths; review it before sharing.</p></footer></body></html>");
        File.WriteAllText(path, html.ToString());
    }

    private static void AppendInventory(StringBuilder html, RunReport report)
    {
        html.Append("<section class=\"section\" aria-labelledby=\"findings\"><h2 id=\"findings\">Finding inventory</h2>");
        html.Append("<p>Copy an ID into <code>repair &lt;suite.json&gt; --finding &lt;ID&gt;</code> to choose one finding. Findings marked as stable keep their IDs across reruns in the same project path; a copied project has different IDs.</p>");
        html.Append("<p class=\"muted\">A grouped finding can affect several cases. Scenario eligibility does not bypass source mapping or source-hash validation. Unsupported or ambiguous findings require manual review.</p>");
        var baselineIds = report.Findings.Select(f => f.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remainingIds = report.RemainingFindings.Select(f => f.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inventory = report.Findings.Concat(report.RemainingFindings).DistinctBy(f => f.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        if (inventory.Length == 0)
            html.Append("<p>No findings were cataloged. Missing or incomplete scans are not a pass; read the whole-suite status and case evidence.</p>");
        foreach (var finding in inventory)
        {
            var selected = string.Equals(report.SelectedFindingId, finding.Id, StringComparison.OrdinalIgnoreCase);
            var fixedFinding = selected && report.Status == "fixed" && report.After.Count > 0
                && !report.PatchRolledBack && !remainingIds.Contains(finding.Id);
            var state = fixedFinding ? "Fixed — selected repair verified"
                : !baselineIds.Contains(finding.Id) ? "New in rerun"
                : report.After.Count == 0 ? "Open in baseline"
                : remainingIds.Contains(finding.Id) ? "Remaining"
                : "Not observed in rerun — not confirmed fixed";
            var latest = report.RemainingFindings.FirstOrDefault(f => f.Id.Equals(finding.Id, StringComparison.OrdinalIgnoreCase));
            var occurrences = finding.Occurrences.Concat(latest?.Occurrences ?? []);
            var cases = occurrences.Select(o => o.ScenarioName).Distinct(StringComparer.Ordinal);
            html.Append($"<article class=\"finding{(selected ? " selected" : "")}\"><h3><code class=\"finding-id\">{E(finding.Id)}</code></h3>");
            if (selected)
                html.Append("<span class=\"badge\">Selected</span>");
            html.Append($"<span class=\"badge {(fixedFinding ? "pass" : "pending")}\">{E(state)}</span><dl>");
            html.Append($"<dt>Rule</dt><dd><code>{E(finding.RuleId)}</code></dd>");
            html.Append($"<dt>AutomationId</dt><dd><code>{E(finding.AutomationId ?? "(missing)")}</code></dd>");
            html.Append($"<dt>Source project</dt><dd><code>{E(finding.Project)}</code></dd>");
            html.Append($"<dt>Affected cases</dt><dd>{string.Join(", ", cases.Select(E))}</dd>");
            html.Append($"<dt>Identity</dt><dd>{(finding.StableIdentity ? "Stable within this project" : "Not stable; manual review required")}</dd>");
            html.Append($"<dt>{(baselineIds.Contains(finding.Id) ? "Baseline" : "Rerun")} eligibility</dt><dd>{(finding.Repairable ? "Eligible for a bounded name proposal" : "Manual review required")}");
            if (finding.RepairReason is not null)
                html.Append($"<br>{E(finding.RepairReason)}");
            if (finding.OwnerScenarioName is not null)
                html.Append($"<br>Repair scenario: {E(finding.OwnerScenarioName)}");
            html.Append("</dd></dl>");
            foreach (var guidance in occurrences.Select(o => (o.Finding.Description, o.Finding.HowToFix)).Distinct())
                html.Append($"<div class=\"guidance\"><p><strong>Why it failed:</strong> {E(guidance.Description)}</p><p><strong>How to fix:</strong> {E(guidance.HowToFix)}</p></div>");
            html.Append("</article>");
        }
        html.Append("</section>");
    }

    private static void AppendCases(StringBuilder html, string directory, RunReport report)
    {
        html.Append("<section class=\"section\" aria-labelledby=\"cases-heading\"><h2 id=\"cases-heading\">Case evidence</h2>");
        html.Append("<p>Expand a case to compare its real Axe.Windows scan, frozen assertions, focus trace, and screenshots. Each case launches a fresh app window.</p>");
        if (report.Before.Count == 0 && report.After.Count == 0)
            html.Append("<p class=\"pending\">No case observations were recorded. This is not a pass.</p>");
        foreach (var name in report.Before.Concat(report.After).Select(r => r.Scenario.Name).Distinct(StringComparer.Ordinal))
        {
            var before = report.Before.FirstOrDefault(r => r.Scenario.Name == name);
            var after = report.After.FirstOrDefault(r => r.Scenario.Name == name);
            var scenario = (before ?? after)!.Scenario;
            html.Append($"<details><summary><strong>{E(name)}</strong>Before: {E(before?.Observation.Status ?? "not recorded")} &middot; After: {E(after?.Observation.Status ?? "not run")}</summary><div class=\"case-body\">");
            html.Append($"<p>Project: <code>{E(scenario.Project)}</code><br>Target: <code>{E(scenario.TargetId)}</code> &middot; expected accessible name: <code>{E(scenario.ExpectedName)}</code></p>");
            AppendObservation(html, directory, before, "Before");
            AppendObservation(html, directory, after, "After");
            html.Append("</div></details>");
        }
        html.Append("</section>");
    }

    private static void AppendObservation(StringBuilder html, string directory, ScenarioResult? result, string label)
    {
        html.Append($"<section class=\"phase\"><h3>{label}</h3>");
        if (result is null)
        {
            html.Append("<p class=\"muted\">Not recorded. Missing evidence is not a pass.</p></section>");
            return;
        }
        var observation = result.Observation;
        html.Append($"<p>Status: <strong class=\"{StatusClass(observation.Status)}\">{E(observation.Status)}</strong></p>");
        if (observation.Error is not null)
            html.Append($"<p class=\"notice\">{E(observation.Error)}</p>");
        html.Append("<h4>Independent accessibility scan</h4>");
        if (observation.Scan is { } scan)
        {
            html.Append($"<p><strong>{E(scan.Engine)} {E(scan.EngineVersion)}</strong> &middot; checkpoint: {E(scan.Checkpoint)} &middot; {scan.Findings.Count} unique finding(s) in this scan.</p>");
            html.Append($"<p class=\"muted\">Process {scan.ProcessId}, window {scan.WindowHandle}. SDK returned {scan.RawErrorCount} reports in {scan.ResultGroupErrorCounts.Count} result group(s).</p>");
            if (scan.Findings.Count == 0)
                html.Append("<p>No violations reported in this captured UI Automation scope. This alone does not establish that the case or suite passed.</p>");
            else
            {
                html.Append("<div class=\"table-scroll\" role=\"region\" aria-label=\"Accessibility findings\" tabindex=\"0\"><table><caption>Findings in this case window</caption><thead><tr><th scope=\"col\">Rule / standard</th><th scope=\"col\">Runtime element</th><th scope=\"col\">Why it failed / how to fix</th></tr></thead><tbody>");
                foreach (var finding in scan.Findings)
                    html.Append($"<tr><td><strong>{E(finding.RuleId)}</strong><br>{E(finding.Standard)}</td><td><code>{E(finding.AutomationId ?? "(no AutomationId)")}</code><br>{E(finding.ElementProperties.GetValueOrDefault("ControlType"))}</td><td>{E(finding.Description)}<br><br>{E(finding.HowToFix)}</td></tr>");
                html.Append("</tbody></table></div>");
            }
        }
        else
            html.Append("<p class=\"pending\">Scan did not complete. Missing results are not a pass.</p>");
        html.Append("<h4>Frozen scenario assertions</h4>");
        if (observation.Checks.Count == 0)
            html.Append("<p>No assertions recorded; no pass is implied.</p>");
        else
        {
            html.Append("<div class=\"table-scroll\" role=\"region\" aria-label=\"Scenario assertion results\" tabindex=\"0\"><table><caption>Expected and observed behavior</caption><thead><tr><th scope=\"col\">Assertion</th><th scope=\"col\">Expected</th><th scope=\"col\">Observed</th><th scope=\"col\">Result</th></tr></thead><tbody>");
            foreach (var check in observation.Checks)
                html.Append($"<tr><td>{E(check.RuleId)}</td><td><code>{E(check.Expected)}</code></td><td><code>{E(check.Actual is "" ? "(empty)" : check.Actual)}</code></td><td class=\"{(check.Passed ? "pass" : "fail")}\">{(check.Passed ? "PASS" : "FAIL")}</td></tr>");
            html.Append("</tbody></table></div>");
        }
        html.Append("<h4>Keyboard focus trace</h4>");
        if (observation.FocusTrace.Count == 0)
            html.Append("<p>No focus observations recorded.</p>");
        else
        {
            html.Append("<ol class=\"focus-trace\">");
            foreach (var focus in observation.FocusTrace)
                html.Append($"<li>Tab {focus.Tab}: <code>{E(focus.AutomationId)}</code> &middot; name: <code>{E(focus.Name is "" ? "(empty)" : focus.Name)}</code></li>");
            html.Append("</ol>");
        }
        if (observation.VerifiedTargets.Count > 0)
            html.Append($"<p>Verified target IDs: {string.Join(", ", observation.VerifiedTargets.Select(id => $"<code>{E(id)}</code>"))}</p>");
        html.Append("<ul class=\"links\">");
        foreach (var (file, title) in new[]
        {
            ("axe-scan.json", "Axe.Windows structured evidence"),
            ("observation.json", "Observation and focus evidence"),
            ("tree.json", "UI Automation tree")
        })
        {
            html.Append("<li>");
            AppendEvidenceLink(html, directory, Path.Combine(result.EvidencePath, file), title);
            html.Append("</li>");
        }
        html.Append("</ul><div class=\"screenshots\">");
        foreach (var (file, title) in new[] { ("window.png", "Ready window"), ("after-action.png", "After the keyboard action") })
        {
            var relative = Path.Combine(result.EvidencePath, file);
            html.Append("<figure>");
            if (EvidenceUrl(directory, relative) is { } url)
                html.Append($"<a href=\"{E(url)}\"><img loading=\"lazy\" alt=\"{E($"{label}, {result.Scenario.Name}: {title}")}\" src=\"{E(url)}\"></a>");
            else
                html.Append("<p class=\"muted\">Screenshot not captured.</p>");
            html.Append($"<figcaption>{E(title)}</figcaption></figure>");
        }
        html.Append("</div></section>");
    }

    private static void AppendEvidenceLink(StringBuilder html, string directory, string relativePath, string label)
    {
        if (EvidenceUrl(directory, relativePath) is { } url)
            html.Append($"<a href=\"{E(url)}\">{E(label)}</a>");
        else
            html.Append($"<span class=\"muted\">{E(label)} (not captured)</span>");
    }

    private static string? EvidenceUrl(string directory, string relativePath)
    {
        var segments = relativePath.Split(['\\', '/']);
        if (segments.Any(s => s.Length == 0 || s is "." or ".." || s.Contains(':')
            || s.TrimEnd(' ', '.') != s || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            return null;
        if (!File.Exists(Path.Combine(directory, Path.Combine(segments))))
            return null;
        return "./" + string.Join("/", segments.Select(Uri.EscapeDataString));
    }

    private static string ActionExplanation(string status) => status switch
    {
        "passed" => "The captured suite checks passed. This is not an accessibility certification.",
        "failed" => "At least one captured scan or scenario assertion failed.",
        "fixed" => "The selected finding passed repair verification. Other findings or assertions can still fail.",
        "awaiting-selection" => "Choose an ID from the finding inventory and rerun repair with --finding <ID>. No source was edited.",
        "awaiting-approval" => "A proposal is ready for review. Use the reviewed proposal with --apply to authorize a source edit.",
        "regression" => "Repair verification was rejected. Review the verification issues and source-edit state.",
        "needs-review" => "No proposal is ready for approval. The selection is stale, unsupported, or ambiguous; address the reported reason and rescan.",
        "verifying" => "A patch was written, but verification has not finished. This is not a completed repair.",
        _ => "Evidence is incomplete or execution failed. Do not count this run as a pass."
    };

    private static string SuiteExplanation(string status) => status switch
    {
        "passed" => "No Axe.Windows findings and all assertions passed for the captured suite.",
        "failed" => "Known findings or failing assertions remain. The whole suite has not passed.",
        _ => "Not every required result is trustworthy or complete. Whole-suite success is unproven."
    };

    private static string StatusClass(string status) => status switch
    {
        "passed" or "fixed" => "pass",
        "failed" or "regression" => "fail",
        _ => "pending"
    };

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "(missing)");
}

using System.Text.Json;

namespace AccessLoop;

public static class AuditCommand
{
    public static async Task<int> ScanAsync(IReadOnlyList<string> args, CancellationToken token)
    {
        var options = AuditOptions.Parse(args);
        var platform = new NativeAuditPlatform(options.Winapp);
        var version = await platform.VersionAsync(token);
        var target = await platform.AttachAsync(options, token);
        var output = Path.GetFullPath(options.Output ?? Path.Combine(Environment.CurrentDirectory,
            ".accessloop", $"scan-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"));
        if (Path.Exists(output))
            throw new IOException($"Audit output already exists: {output}. Choose a new directory.");
        Directory.CreateDirectory(output);
        using var ownership = new FileStream(Path.Combine(output, ".capture-lock"), FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        if (options.Navigation == "standard-tabs")
        {
            var tabs = await new TabAuditRunner(platform).RunAsync(options, target, version, output, token);
            var tabsPath = Path.Combine(output, "scan.json");
            var tabsHtml = Path.Combine(output, "report.html");
            AuditFiles.WriteNew(tabsPath, tabs);
            TabAuditReportRenderer.Write(tabsHtml, tabs);
            if (options.Json)
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    tabs.Kind, tabs.RunId, tabs.Status, tabs.NavigationStatus, tabs.CaptureStatus,
                    tabs.CapturedPageCount, tabs.HasAuditResults, tabs.RuleFailureCount,
                    skippedCount = tabs.Skipped.Count,
                    scan = tabsPath, report = tabsHtml, scanSha256 = AuditFiles.Hash(tabsPath)
                }, Json.Options));
            else
            {
                Console.WriteLine(tabs.HasAuditResults
                    ? $"AccessLoop standard tabs: {tabs.Status}; pages: {tabs.CapturedPageCount}; rule-failure occurrences: {tabs.RuleFailureCount}."
                    : $"AccessLoop standard tabs: {tabs.Status}; not evaluated: no page audit results are available.");
                Console.WriteLine($"Navigation: {tabs.NavigationStatus}; capture: {tabs.CaptureStatus}; skipped: {tabs.Skipped.Count}.");
                Console.WriteLine("Tab selection can have app side effects. Partial coverage and unsupported checks are not a pass.");
                if (tabs.Error is not null)
                    Console.Error.WriteLine(tabs.Error);
                Console.WriteLine($"Evidence: {tabsHtml}");
            }
            return TabAuditRunner.ExitCode(tabs);
        }
        var report = await new AuditRunner(platform).RunAsync(options, target, version, output, token);
        var scanPath = Path.Combine(output, "scan.json");
        var htmlPath = Path.Combine(output, "report.html");
        AuditFiles.WriteNew(scanPath, report);
        AuditReportRenderer.Write(htmlPath, report);
        if (options.Json)
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                report.RunId, report.Status, report.RuleFailureCount,
                scan = scanPath,
                report = htmlPath,
                scanSha256 = AuditFiles.Hash(scanPath)
            }, Json.Options));
        else
        {
            Console.WriteLine($"AccessLoop audit: {report.Status}; rule failures: {report.RuleFailureCount}.");
            Console.WriteLine("Unknown, unsupported and untested checks remain separate; this is not a WCAG compliance verdict.");
            if (report.Error is not null)
                Console.Error.WriteLine(report.Error);
            Console.WriteLine($"Evidence: {htmlPath}");
        }
        return AuditRunner.ExitCode(report);
    }

    public static int Assessment(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Provide the original scan.json file. See --help.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var allowed = args[0] == "assessment-template"
            ? new[] { "--output", "--author", "--source", "--model" }
            : ["--output", "--assessment"];
        for (var index = 2; index < args.Count; index++)
        {
            var key = args[index];
            if (!allowed.Contains(key, StringComparer.Ordinal) || ++index >= args.Count
                || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--", StringComparison.Ordinal)
                || !values.TryAdd(key, args[index]))
                throw new ArgumentException($"Unknown, duplicate or incomplete assessment option: {key}.");
        }
        if (!values.TryGetValue("--output", out var output))
            throw new ArgumentException("Provide a new --output path.");
        if (args[0] == "assessment-template")
        {
            if (!values.TryGetValue("--author", out var author))
                throw new ArgumentException("Provide an explicit --author name for the assessment template.");
            _ = AuditAssessment.CreateTemplate(args[1], output, values.GetValueOrDefault("--source", "human"),
                author, values.GetValueOrDefault("--model"));
            Console.WriteLine($"Assessment template: {Path.GetFullPath(output)}");
            Console.WriteLine("Fill evidence-based entries before attaching; no assessment or automatic verdict has been inferred.");
            return 0;
        }
        if (!values.TryGetValue("--assessment", out var assessment))
            throw new ArgumentException("Provide --assessment <assessment.json>.");
        var reportPath = AuditAssessment.Attach(args[1], assessment, output);
        Console.WriteLine($"Combined report: {reportPath}");
        Console.WriteLine("The original automatic results and artifacts have not been changed.");
        return 0;
    }
}

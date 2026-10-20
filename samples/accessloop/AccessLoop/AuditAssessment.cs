using System.Text.Json;

namespace AccessLoop;

public sealed record AuditAssessmentAuthor(string Kind, string Name, string? Model = null);

public sealed record AuditAssessmentEntry
{
    public required string CheckId { get; init; }
    public string? ElementId { get; init; }
    public required string Status { get; init; }
    public required string Explanation { get; init; }
    public required IReadOnlyList<string> Evidence { get; init; }
}

public sealed record AuditAssessmentDocument
{
    public int SchemaVersion { get; init; } = 1;
    public required string RunId { get; init; }
    public required string ScanSha256 { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required AuditAssessmentAuthor Author { get; init; }
    public required IReadOnlyList<AuditAssessmentEntry> Entries { get; init; }
}

public static class AuditAssessment
{
    private const int MaxAssessmentBytes = 8 * 1024 * 1024;
    private const int MaxEntries = 10_000;

    public static AuditAssessmentDocument CreateTemplate(
        string scanPath, string outputPath, string authorKind, string authorName, string? model = null)
    {
        var scan = AuditFiles.ReadVerifiedDocument(scanPath);
        var author = new AuditAssessmentAuthor(authorKind, authorName, model);
        ValidateAuthor(author);
        var entries = new List<AuditAssessmentEntry>();
        foreach (var definition in scan.Report.Catalog)
        {
            var results = scan.Report.Results.Where(result => result.CheckId == definition.Id
                && result.Status is "observed" or "inconclusive" or "not-tested" or "unsupported" or "blocked").ToArray();
            if (results.Length == 0 && definition.Mode != "assisted" && definition.Availability != "assisted")
                continue;
            entries.Add(new()
            {
                CheckId = definition.Id,
                Status = "needs-review",
                Explanation = "",
                Evidence = results.SelectMany(result => result.Evidence)
                    .DistinctBy(AuditFiles.NormalizeArtifactPath, StringComparer.OrdinalIgnoreCase).ToArray()
            });
        }
        var document = new AuditAssessmentDocument
        {
            RunId = scan.Report.RunId,
            ScanSha256 = scan.Sha256,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Author = author,
            Entries = entries
        };
        Validate(document, scan.Report, scan.Sha256, allowTemplatePlaceholders: true);
        AuditFiles.WriteNewBytes(outputPath, Serialize(document));
        return document;
    }

    public static string Attach(string scanPath, string assessmentPath, string outputDirectory)
    {
        var scan = AuditFiles.ReadVerifiedDocument(scanPath);
        var document = Read(assessmentPath);
        Validate(document, scan.Report, scan.Sha256);
        var assessmentBytes = Serialize(document);
        if (!string.Equals(AuditFiles.Hash(scanPath), scan.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("The scan changed while the assessment was being validated.");

        var output = AuditFiles.CreateNewDirectory(outputDirectory);
        var copiedScanPath = Path.Combine(output, "scan.json");
        AuditFiles.WriteNewBytes(copiedScanPath, scan.Bytes);
        foreach (var artifact in scan.Report.Artifacts)
            AuditFiles.CopyVerifiedArtifact(scan.Root, artifact, output);
        var copiedScan = AuditFiles.ReadVerifiedDocument(copiedScanPath);
        Validate(document, copiedScan.Report, copiedScan.Sha256);
        AuditFiles.WriteNewBytes(Path.Combine(output, "assessment.json"), assessmentBytes);
        var htmlPath = Path.Combine(output, "report.html");
        AuditReportRenderer.Write(htmlPath, copiedScan.Report, document);
        return htmlPath;
    }

    private static AuditAssessmentDocument Read(string path)
    {
        using var json = AuditFiles.ParseStrict(AuditFiles.ReadBytes(path, MaxAssessmentBytes));
        AuditFiles.RequireMembers(json.RootElement, "assessment", "schemaVersion", "runId",
            "scanSha256", "createdAtUtc", "author", "entries");
        AuditFiles.RequireMembers(AuditFiles.Property(json.RootElement, "author"), "assessment author", "kind", "name");
        var document = AuditFiles.Deserialize<AuditAssessmentDocument>(json.RootElement);
        foreach (var item in AuditFiles.Property(json.RootElement, "entries").EnumerateArray())
            AuditFiles.RequireMembers(item, "assessment entry", "checkId", "status", "explanation", "evidence");
        return document;
    }

    private static byte[] Serialize(AuditAssessmentDocument document)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Json.Options);
        if (bytes.Length > MaxAssessmentBytes)
            throw new InvalidDataException($"The assessment exceeds the {MaxAssessmentBytes}-byte limit.");
        return bytes;
    }

    internal static void Validate(AuditAssessmentDocument document, AuditReport report,
        string? expectedScanHash = null, bool allowTemplatePlaceholders = false)
    {
        if (document.SchemaVersion != 1)
            throw new InvalidDataException("Only assessment schemaVersion 1 is supported.");
        AuditFiles.Text(document.RunId, "assessment runId", 256);
        if (!string.Equals(document.RunId, report.RunId, StringComparison.Ordinal))
            throw new InvalidDataException("The assessment runId does not match this scan.");
        AuditFiles.ValidateHash(document.ScanSha256, "assessment scanSha256");
        if (expectedScanHash is not null
            && !string.Equals(document.ScanSha256, expectedScanHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The assessment is stale: scanSha256 must match the exact scan.json bytes.");
        if (document.CreatedAtUtc == default || document.CreatedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Assessment createdAtUtc must be a UTC timestamp.");
        ValidateAuthor(document.Author);
        if (document.Entries is null || document.Entries.Count > MaxEntries)
            throw new InvalidDataException($"Assessment entries must be an array of at most {MaxEntries} items.");
        var catalog = report.Catalog.Select(check => check.Id).ToHashSet(StringComparer.Ordinal);
        var elements = AuditFiles.SnapshotNodeIds(report.Snapshot);
        var artifacts = report.Artifacts.Select(artifact => AuditFiles.NormalizeArtifactPath(artifact.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<(string CheckId, string? ElementId)>();
        foreach (var entry in document.Entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.CheckId) || !catalog.Contains(entry.CheckId))
                throw new InvalidDataException("An assessment checkId must exist in the scan catalog.");
            if (entry.ElementId is not null && !elements.Contains(entry.ElementId))
                throw new InvalidDataException($"Assessment elementId is not present in the scan snapshot: {entry.ElementId}.");
            if (!seen.Add((entry.CheckId, entry.ElementId)))
                throw new InvalidDataException("Only one assessment entry is allowed per checkId and elementId.");
            if (entry.Status is not ("needs-review" or "suggestion" or "confirmed" or "disputed" or "not-applicable"))
                throw new InvalidDataException($"Invalid assessment status: {entry.Status}.");
            if (document.Author.Kind == "ai" && entry.Status is "confirmed" or "not-applicable")
                throw new InvalidDataException("AI assessments cannot confirm compliance or declare an exception; use suggestion, needs-review, or disputed.");
            AuditFiles.Text(entry.Explanation, "assessment explanation",
                allowBlank: allowTemplatePlaceholders && entry.Status == "needs-review");
            AuditFiles.ValidateReferences(entry.Evidence, artifacts);
            if (entry.Status != "needs-review" && entry.Evidence.Count == 0)
                throw new InvalidDataException("Assessment judgments require evidence listed in scan.json; missing coverage is not a conclusion.");
        }
    }

    private static void ValidateAuthor(AuditAssessmentAuthor? author)
    {
        if (author is null || author.Kind is not ("human" or "ai"))
            throw new InvalidDataException("Assessment author.kind must be human or ai.");
        AuditFiles.Text(author.Name, "assessment author name", 256);
        if (author.Kind == "ai" || author.Model is not null)
            AuditFiles.Text(author.Model, "assessment author model", 256);
    }
}

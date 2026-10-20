using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class AuditAssessmentTests
{
    private AuditTestFixture fixture = null!;

    [TestInitialize]
    public void Initialize() => fixture = new();

    [TestCleanup]
    public void Cleanup() => fixture.Dispose();

    [TestMethod]
    [DataRow("human")]
    [DataRow("ai")]
    public void FilledTemplateAttachesWithoutChangingScanBytesEvidenceOrAutomaticFindings(string kind)
    {
        File.AppendAllText(fixture.ScanPath, "\r\n \t");
        var originalScan = File.ReadAllBytes(fixture.ScanPath);
        File.WriteAllText(Path.Combine(fixture.Source, "private-unlisted.txt"), "Do not copy unlisted files.");
        var templatePath = Path.Combine(fixture.Root, "template.json");

        var template = AuditAssessment.CreateTemplate(fixture.ScanPath, templatePath, kind, "Reviewer",
            kind == "ai" ? "declared-model-version" : null);

        Assert.AreEqual(fixture.Report.RunId, template.RunId);
        Assert.AreEqual(AuditFiles.Hash(fixture.ScanPath), template.ScanSha256);
        Assert.IsTrue(template.Entries.All(entry => entry.Status == "needs-review" && entry.Explanation == ""));
        Assert.IsTrue(template.Entries.Any(entry => entry.CheckId == "content.meaning"));
        Assert.IsTrue(template.Entries.Any(entry => entry.CheckId == "text.contrast"));
        Assert.IsTrue(template.Entries.Any(entry => entry.CheckId == "keyboard.path"));
        var filled = template with
        {
            Entries =
            [
                fixture.Assessment(kind, kind == "ai" ? "suggestion" : "confirmed").Entries[0],
                new()
                {
                    CheckId = "content.meaning", Status = "needs-review",
                    Explanation = "No business-task evidence was collected; this remains unreviewed.", Evidence = []
                }
            ]
        };
        fixture.SaveAssessment(filled, "template.json");
        var originalAssessment = File.ReadAllBytes(templatePath);

        var htmlPath = AuditAssessment.Attach(fixture.ScanPath, templatePath, fixture.Output);

        Assert.AreEqual(Path.Combine(fixture.Output, "report.html"), htmlPath);
        Assert.IsTrue(File.Exists(htmlPath));
        CollectionAssert.AreEqual(originalScan, File.ReadAllBytes(Path.Combine(fixture.Output, "scan.json")));
        CollectionAssert.AreEqual(originalScan, File.ReadAllBytes(fixture.ScanPath));
        CollectionAssert.AreEqual(originalAssessment, File.ReadAllBytes(templatePath));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Output, "private-unlisted.txt")));
        foreach (var artifact in fixture.Report.Artifacts)
            CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(fixture.Source, artifact.Path)),
                File.ReadAllBytes(Path.Combine(fixture.Output, artifact.Path)));
        var copied = AuditFiles.ReadVerified(Path.Combine(fixture.Output, "scan.json"));
        Assert.AreEqual(1, copied.RuleFailureCount);
        Assert.AreEqual("rule-failure", copied.Results[0].Status);
        Assert.AreEqual("NameNotNull", copied.Results[0].SourceRuleId);
        Assert.AreEqual("partial", copied.Status);
        var savedAssessment = JsonSerializer.Deserialize<AuditAssessmentDocument>(
            File.ReadAllText(Path.Combine(fixture.Output, "assessment.json")), Json.Options)!;
        Assert.AreEqual(kind, savedAssessment.Author.Kind);
        Assert.AreEqual(filled.ScanSha256, savedAssessment.ScanSha256);
        StringAssert.Contains(File.ReadAllText(htmlPath), "确定的规则失败：<strong>1</strong>");
    }

    [TestMethod]
    public void TemplateCreationDoesNotOverwriteAnyExistingFile()
    {
        var path = Path.Combine(fixture.Root, "template.json");
        File.WriteAllText(path, "original");

        Assert.ThrowsExactly<IOException>(() => AuditAssessment.CreateTemplate(fixture.ScanPath, path, "human", "Reviewer"));

        Assert.AreEqual("original", File.ReadAllText(path));
    }

    [TestMethod]
    public void UnfilledTemplateCannotBeImportedAsAnAssessment()
    {
        var path = Path.Combine(fixture.Root, "template.json");
        AuditAssessment.CreateTemplate(fixture.ScanPath, path, "human", "Reviewer");

        Assert.ThrowsExactly<InvalidDataException>(() => AuditAssessment.Attach(fixture.ScanPath, path, fixture.Output));
        Assert.IsFalse(Directory.Exists(fixture.Output));
    }

    [TestMethod]
    [DataRow("human", "", null)]
    [DataRow("robot", "Reviewer", null)]
    [DataRow("ai", "Reviewer", null)]
    [DataRow("ai", "Reviewer", "")]
    [DataRow("ai", "Reviewer", " ")]
    public void AuthorIdentityAndAiModelMustBeDeclared(string kind, string name, string? model)
    {
        var path = Path.Combine(fixture.Root, "template.json");

        Assert.ThrowsExactly<InvalidDataException>(() => AuditAssessment.CreateTemplate(fixture.ScanPath, path, kind, name, model));

        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    [DataRow("confirmed")]
    [DataRow("not-applicable")]
    public void AiCannotClaimConfirmationOrAnException(string status)
    {
        var path = fixture.SaveAssessment(fixture.Assessment("ai", status));

        Assert.ThrowsExactly<InvalidDataException>(() => AuditAssessment.Attach(fixture.ScanPath, path, fixture.Output));
        Assert.IsFalse(Directory.Exists(fixture.Output));
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Render(assessment: fixture.Assessment("ai", status)));
    }

    [TestMethod]
    [DataRow("suggestion")]
    [DataRow("needs-review")]
    [DataRow("disputed")]
    public void EvidenceBoundAiStatusesAreSupplementaryNotAutomaticResults(string status)
    {
        var path = fixture.SaveAssessment(fixture.Assessment("ai", status));

        var htmlPath = AuditAssessment.Attach(fixture.ScanPath, path, fixture.Output);

        Assert.AreEqual(1, AuditFiles.ReadVerified(Path.Combine(fixture.Output, "scan.json")).RuleFailureCount);
        StringAssert.Contains(File.ReadAllText(htmlPath), "AI 辅助建议（非工具验证）");
        StringAssert.Contains(File.ReadAllText(htmlPath), status);
    }

    [TestMethod]
    [DataRow("confirmed", true)]
    [DataRow("confirmed", false)]
    [DataRow("not-applicable", true)]
    [DataRow("not-applicable", false)]
    [DataRow("suggestion", false)]
    [DataRow("disputed", false)]
    public void JudgmentsRequireNonemptyReasonsAndListedEvidence(string status, bool removeReason)
    {
        var document = fixture.Assessment("human", status);
        var entry = document.Entries[0];
        document = document with
        {
            Entries = [removeReason ? entry with { Explanation = " " } : entry with { Evidence = [] }]
        };
        var path = fixture.SaveAssessment(document);

        Assert.ThrowsExactly<InvalidDataException>(() => AuditAssessment.Attach(fixture.ScanPath, path, fixture.Output));
        Assert.IsFalse(Directory.Exists(fixture.Output));
    }

    [TestMethod]
    public void HumanExceptionStaysSeparateFromUnsupportedAutomaticCoverage()
    {
        var document = fixture.Assessment() with
        {
            Entries =
            [
                new()
                {
                    CheckId = "visual.estimates", Status = "not-applicable",
                    Explanation = "Reviewer describes a scoped exception; no automated visual test was run.",
                    Evidence = [AuditTestFixture.ScreenshotPath]
                }
            ]
        };
        var path = fixture.SaveAssessment(document);

        var htmlPath = AuditAssessment.Attach(fixture.ScanPath, path, fixture.Output);

        var automatic = AuditFiles.ReadVerified(Path.Combine(fixture.Output, "scan.json"));
        Assert.AreEqual("unsupported", automatic.Results.Single(result => result.CheckId == "visual.estimates").Status);
        Assert.AreEqual(1, automatic.RuleFailureCount);
        StringAssert.Contains(File.ReadAllText(htmlPath), "人工不适用说明（非自动豁免）");
    }

    [TestMethod]
    [DataRow("run")]
    [DataRow("hash")]
    [DataRow("whitespace")]
    public void StaleRunOrExactScanHashIsRejectedBeforeOutputCreation(string mutation)
    {
        var document = fixture.Assessment();
        document = mutation switch
        {
            "run" => document with { RunId = "different-run" },
            "hash" => document with { ScanSha256 = new string('A', 64) },
            _ => document
        };
        var path = fixture.SaveAssessment(document);
        if (mutation == "whitespace")
            File.AppendAllText(fixture.ScanPath, "\n");

        Assert.ThrowsExactly<InvalidDataException>(() => AuditAssessment.Attach(fixture.ScanPath, path, fixture.Output));
        Assert.IsFalse(Directory.Exists(fixture.Output));
    }

    [TestMethod]
    public void Utf8BomIsAcceptedWithoutChangingTheEvidenceBindingOrCopiedScanBytes()
    {
        var original = new byte[] { 239, 187, 191 }.Concat(File.ReadAllBytes(fixture.ScanPath)).ToArray();
        File.WriteAllBytes(fixture.ScanPath, original);
        var path = fixture.SaveAssessment(fixture.Assessment());
        File.WriteAllBytes(path, new byte[] { 239, 187, 191 }.Concat(File.ReadAllBytes(path)).ToArray());

        AuditAssessment.Attach(fixture.ScanPath, path, fixture.Output);

        CollectionAssert.AreEqual(original, File.ReadAllBytes(Path.Combine(fixture.Output, "scan.json")));
        Assert.AreEqual(1, AuditFiles.ReadVerified(Path.Combine(fixture.Output, "scan.json")).RuleFailureCount);
    }

    [TestMethod]
    public void TamperedEvidencePreventsBothTemplateCreationAndAttachment()
    {
        var assessmentPath = fixture.SaveAssessment(fixture.Assessment());
        var templatePath = Path.Combine(fixture.Root, "template.json");
        var path = Path.Combine(fixture.Source, AuditTestFixture.AxePath);
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 1;
        File.WriteAllBytes(path, bytes);

        Assert.ThrowsExactly<InvalidDataException>(() => AuditAssessment.CreateTemplate(fixture.ScanPath, templatePath, "human", "Reviewer"));
        Assert.ThrowsExactly<InvalidDataException>(() => AuditAssessment.Attach(fixture.ScanPath, assessmentPath, fixture.Output));
        Assert.IsFalse(File.Exists(templatePath));
        Assert.IsFalse(Directory.Exists(fixture.Output));
    }

    [TestMethod]
    [DataRow("check")]
    [DataRow("element")]
    [DataRow("evidence")]
    [DataRow("traversal")]
    [DataRow("rooted")]
    [DataRow("duplicate-evidence")]
    [DataRow("duplicate-entry")]
    [DataRow("status")]
    public void AssessmentReferencesAndStatusesMustMatchTheCapturedRun(string mutation)
    {
        var document = fixture.Assessment();
        var entry = document.Entries[0];
        entry = mutation switch
        {
            "check" => entry with { CheckId = "invented.check" },
            "element" => entry with { ElementId = "another-window-node" },
            "evidence" => entry with { Evidence = ["not-captured.json"] },
            "traversal" => entry with { Evidence = [@"..\outside.json"] },
            "rooted" => entry with { Evidence = [fixture.ScanPath] },
            "duplicate-evidence" => entry with { Evidence = [AuditTestFixture.AxePath, @"EVIDENCE\AXE.JSON"] },
            "status" => entry with { Status = "rule-failure" },
            _ => entry
        };
        document = document with { Entries = mutation == "duplicate-entry" ? [entry, entry] : [entry] };
        var path = fixture.SaveAssessment(document);

        Assert.ThrowsExactly<InvalidDataException>(() => AuditAssessment.Attach(fixture.ScanPath, path, fixture.Output));
        Assert.IsFalse(Directory.Exists(fixture.Output));
    }

    [TestMethod]
    public void AnElementCannotBeClaimedWhenTheSnapshotIsMissing()
    {
        fixture.Report = fixture.Report with
        {
            Snapshot = null,
            Results = fixture.Report.Results.Select(result => result with { ElementId = null }).ToArray()
        };
        fixture.SaveScan();
        var path = fixture.SaveAssessment(fixture.Assessment());

        Assert.ThrowsExactly<InvalidDataException>(() => AuditAssessment.Attach(fixture.ScanPath, path, fixture.Output));
        Assert.IsFalse(Directory.Exists(fixture.Output));
    }

    [TestMethod]
    [DataRow("root")]
    [DataRow("author")]
    [DataRow("entry")]
    [DataRow("automatic-root")]
    [DataRow("automatic-entry")]
    [DataRow("missing-schema")]
    [DataRow("missing-explanation")]
    [DataRow("missing-evidence")]
    [DataRow("missing-author-name")]
    public void StrictAssessmentJsonRejectsDuplicateUnknownAndMissingMembers(string mutation)
    {
        var path = fixture.SaveAssessment(fixture.Assessment());
        var text = File.ReadAllText(path);
        if (mutation == "root")
            text = text[..^1] + ",\"RunId\":\"other\"}";
        else if (mutation == "author")
            text = text.Replace("\"kind\": \"human\"", "\"kind\": \"human\", \"Kind\": \"ai\"", StringComparison.Ordinal);
        else if (mutation == "entry")
            text = text.Replace("\"status\": \"confirmed\"", "\"status\": \"confirmed\", \"status\": \"suggestion\"", StringComparison.Ordinal);
        else
        {
            var json = JsonNode.Parse(text)!.AsObject();
            switch (mutation)
            {
                case "automatic-root": json["results"] = new JsonArray(); break;
                case "automatic-entry": json["entries"]![0]!["automaticStatus"] = "passed"; break;
                case "missing-schema": json.Remove("schemaVersion"); break;
                case "missing-explanation": json["entries"]![0]!.AsObject().Remove("explanation"); break;
                case "missing-evidence": json["entries"]![0]!.AsObject().Remove("evidence"); break;
                case "missing-author-name": json["author"]!.AsObject().Remove("name"); break;
            }
            text = json.ToJsonString(Json.Options);
        }
        File.WriteAllText(path, text);

        Assert.ThrowsExactly<InvalidDataException>(() => AuditAssessment.Attach(fixture.ScanPath, path, fixture.Output));
        Assert.IsFalse(Directory.Exists(fixture.Output));
    }

    [TestMethod]
    [DataRow("schema")]
    [DataRow("timestamp")]
    [DataRow("model")]
    [DataRow("hash-format")]
    public void InvalidAssessmentMetadataCannotBeImported(string mutation)
    {
        var document = fixture.Assessment("ai", "suggestion");
        document = mutation switch
        {
            "schema" => document with { SchemaVersion = 2 },
            "timestamp" => document with { CreatedAtUtc = default },
            "model" => document with { Author = document.Author with { Model = null } },
            "hash-format" => document with { ScanSha256 = "../not-a-hash" },
            _ => document
        };
        var path = fixture.SaveAssessment(document);

        Assert.ThrowsExactly<InvalidDataException>(() => AuditAssessment.Attach(fixture.ScanPath, path, fixture.Output));
        Assert.IsFalse(Directory.Exists(fixture.Output));
    }

    [TestMethod]
    public void AttachmentNeverReusesAnExistingDirectory()
    {
        var path = fixture.SaveAssessment(fixture.Assessment());
        Directory.CreateDirectory(fixture.Output);
        var marker = Path.Combine(fixture.Output, "keep.txt");
        File.WriteAllText(marker, "original");

        Assert.ThrowsExactly<IOException>(() => AuditAssessment.Attach(fixture.ScanPath, path, fixture.Output));

        Assert.AreEqual("original", File.ReadAllText(marker));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Output, "scan.json")));
    }

    [TestMethod]
    public void BlockedScanMayRequestReviewButCannotConfirmOmittedEvidence()
    {
        fixture.Report = fixture.Report with
        {
            Status = "blocked", Snapshot = null, Observation = null, Axe = null, Artifacts = [],
            Results = [new() { CheckId = "capture.tree", Status = "blocked", Summary = "No capture was obtained." }]
        };
        fixture.SaveScan();
        var template = AuditAssessment.CreateTemplate(fixture.ScanPath, Path.Combine(fixture.Root, "template.json"), "human", "Reviewer");
        var document = template with
        {
            Entries = [new() { CheckId = "capture.tree", Status = "confirmed", Explanation = "Unsupported assertion without evidence.", Evidence = [] }]
        };
        var path = fixture.SaveAssessment(document);
        Assert.ThrowsExactly<InvalidDataException>(() => AuditAssessment.Attach(fixture.ScanPath, path, fixture.Output));
        Assert.IsFalse(Directory.Exists(fixture.Output));
        document = document with
        {
            Entries = [document.Entries[0] with { Status = "needs-review", Explanation = "The capture is blocked; there is nothing to assess." }]
        };
        fixture.SaveAssessment(document);

        var htmlPath = AuditAssessment.Attach(fixture.ScanPath, path, fixture.Output);

        Assert.AreEqual("blocked", AuditFiles.ReadVerified(Path.Combine(fixture.Output, "scan.json")).Status);
        StringAssert.Contains(File.ReadAllText(htmlPath), "<dt>执行状态</dt><dd>blocked</dd>");
    }

    [TestMethod]
    public void ReadOnlyOriginalFilesAreNotMutated()
    {
        var path = fixture.SaveAssessment(fixture.Assessment());
        var originals = fixture.Report.Artifacts.Select(artifact => Path.Combine(fixture.Source, artifact.Path))
            .Append(fixture.ScanPath).Append(path).ToArray();
        var hashes = originals.Select(AuditFiles.Hash).ToArray();
        foreach (var original in originals)
            File.SetAttributes(original, File.GetAttributes(original) | FileAttributes.ReadOnly);
        try
        {
            AuditAssessment.Attach(fixture.ScanPath, path, fixture.Output);

            CollectionAssert.AreEqual(hashes, originals.Select(AuditFiles.Hash).ToArray());
        }
        finally
        {
            foreach (var original in originals)
                File.SetAttributes(original, File.GetAttributes(original) & ~FileAttributes.ReadOnly);
        }
    }
}

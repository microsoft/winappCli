using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class AuditFilesTests
{
    private AuditTestFixture fixture = null!;

    [TestInitialize]
    public void Initialize() => fixture = new();

    [TestCleanup]
    public void Cleanup() => fixture.Dispose();

    [TestMethod]
    public void JsonWritesUseCamelCaseIndentationAndNeverOverwrite()
    {
        var path = Path.Combine(fixture.Root, "new.json");
        AuditFiles.WriteNew(path, new { EvidenceName = "one", Count = 2 });
        var original = File.ReadAllBytes(path);

        StringAssert.Contains(Encoding.UTF8.GetString(original), "\"evidenceName\": \"one\"");
        StringAssert.Contains(Encoding.UTF8.GetString(original), "\n");
        Assert.ThrowsExactly<IOException>(() => AuditFiles.WriteNew(path, new { Count = 99 }));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void DescribeAndHashUsePersistedBytesAndUppercaseSha256()
    {
        const string relative = @"evidence\hash #&%.txt";
        var path = Path.Combine(fixture.Source, relative);
        File.WriteAllText(path, "abc", new UTF8Encoding(false));

        var artifact = AuditFiles.Describe(fixture.Source, relative, "text/plain", "fixture");

        Assert.AreEqual(relative, artifact.Path);
        Assert.AreEqual(3L, artifact.Length);
        Assert.AreEqual("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", artifact.Sha256);
        Assert.AreEqual(artifact.Sha256, AuditFiles.Hash(path));
        Assert.AreEqual(path, AuditFiles.ResolveArtifact(fixture.Source, relative));
    }

    [TestMethod]
    public void VerifiedReadPreservesFailuresObservationsAndMissingCoverage()
    {
        var report = AuditFiles.ReadVerified(fixture.ScanPath);

        Assert.AreEqual("partial", report.Status);
        Assert.AreEqual(1, report.RuleFailureCount);
        Assert.IsTrue(report.Results.Any(result => result.Status == "observed"));
        Assert.IsTrue(report.Results.Any(result => result.Status == "unsupported"));
        Assert.AreEqual("NameNotNull", report.Results[0].SourceRuleId);
    }

    [TestMethod]
    public void BlockedRecordWithoutCapturedEvidenceCanStillBeAValidRecord()
    {
        fixture.Report = fixture.Report with
        {
            Status = "blocked", Snapshot = null, Observation = null, Axe = null, Artifacts = [],
            Results = [new() { CheckId = "capture.tree", Status = "blocked", Summary = "Capture was unavailable." }]
        };
        fixture.SaveScan();

        var report = AuditFiles.ReadVerified(fixture.ScanPath);

        Assert.AreEqual("blocked", report.Status);
        Assert.AreEqual("blocked", report.Results.Single().Status);
        Assert.IsNull(report.Snapshot);
    }

    [TestMethod]
    [DataRow(@"..\outside.json")]
    [DataRow(@"nested\..\snapshot.json")]
    [DataRow(@".\snapshot.json")]
    [DataRow(@"C:\outside.json")]
    [DataRow(@"C:outside.json")]
    [DataRow(@"\outside.json")]
    [DataRow(@"\\server\share\outside.json")]
    [DataRow("https://example.invalid/evidence")]
    [DataRow("javascript:alert(1)")]
    [DataRow(@"snapshot.json:alternate")]
    [DataRow(@"nested\\snapshot.json")]
    [DataRow(@".. \outside.json")]
    [DataRow(@"nested.\snapshot.json")]
    [DataRow("CON")]
    [DataRow(@"dir\LPT1.png")]
    [DataRow(@"dir\LPT1 .png")]
    [DataRow("COM¹.txt")]
    public void UnsafeArtifactPathsAreRejected(string path)
    {
        Assert.ThrowsExactly<InvalidDataException>(() => AuditFiles.ResolveArtifact(fixture.Source, path));
        Assert.ThrowsExactly<InvalidDataException>(() => AuditFiles.Describe(fixture.Source, path, "text/plain", "fixture"));
    }

    [TestMethod]
    public void MissingOrInaccessibleArtifactsAreNotSkipped()
    {
        var path = Path.Combine(fixture.Source, AuditTestFixture.AxePath);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => AuditFiles.ReadVerified(fixture.ScanPath));
        File.Delete(path);
        Assert.Throws<IOException>(() => AuditFiles.ReadVerified(fixture.ScanPath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TamperedEvidenceIsRejectedEvenWhenItsLengthIsUnchanged(bool changeLength)
    {
        var path = Path.Combine(fixture.Source, AuditTestFixture.SnapshotPath);
        var bytes = File.ReadAllBytes(path);
        if (changeLength)
            File.AppendAllText(path, " ");
        else
        {
            bytes[^1] ^= 1;
            File.WriteAllBytes(path, bytes);
        }

        Assert.ThrowsExactly<InvalidDataException>(() => AuditFiles.ReadVerified(fixture.ScanPath));
    }

    [TestMethod]
    [DataRow("schemaVersion")]
    [DataRow("runId")]
    [DataRow("status")]
    [DataRow("catalog")]
    [DataRow("results")]
    [DataRow("artifacts")]
    public void RequiredRootFieldsCannotBeSuppliedByDeserializerDefaults(string field)
    {
        fixture.ChangeScan(json => json.Remove(field));
        Assert.ThrowsExactly<InvalidDataException>(() => AuditFiles.ReadVerified(fixture.ScanPath));
    }

    [TestMethod]
    public void RequiredArtifactAndCatalogFieldsCannotBeOmitted()
    {
        fixture.ChangeScan(json => json["artifacts"]![0]!.AsObject().Remove("source"));
        Assert.ThrowsExactly<InvalidDataException>(() => AuditFiles.ReadVerified(fixture.ScanPath));
        fixture.SaveScan();
        fixture.ChangeScan(json => json["catalog"]![0]!.AsObject().Remove("mode"));
        Assert.ThrowsExactly<InvalidDataException>(() => AuditFiles.ReadVerified(fixture.ScanPath));
    }

    [TestMethod]
    [DataRow("schema")]
    [DataRow("status")]
    [DataRow("result-status")]
    [DataRow("result-check")]
    [DataRow("result-element")]
    [DataRow("native-rule")]
    [DataRow("computed-count")]
    [DataRow("duplicate-catalog")]
    [DataRow("unknown-field")]
    public void InvalidSchemaStatusesIdsAndInventedFieldsAreRejected(string mutation)
    {
        fixture.ChangeScan(json =>
        {
            switch (mutation)
            {
                case "schema": json["schemaVersion"] = 2; break;
                case "status": json["status"] = "passed"; break;
                case "result-status": json["results"]![0]!["status"] = "passed"; break;
                case "result-check": json["results"]![0]!["checkId"] = "imaginary.rule"; break;
                case "result-element": json["results"]![0]!["elementId"] = "imaginary-node"; break;
                case "native-rule": json["results"]![0]!["sourceRuleId"] = null; break;
                case "computed-count": json["ruleFailureCount"] = 0; break;
                case "duplicate-catalog": json["catalog"]!.AsArray().Add(json["catalog"]![0]!.DeepClone()); break;
                case "unknown-field": json["automaticPass"] = true; break;
            }
        });
        Assert.ThrowsExactly<InvalidDataException>(() => AuditFiles.ReadVerified(fixture.ScanPath));
    }

    [TestMethod]
    [DataRow("exact")]
    [DataRow("case")]
    [DataRow("escaped")]
    [DataRow("nested")]
    public void DuplicateJsonMembersAreRejectedBeforeDeserialization(string duplicate)
    {
        var json = File.ReadAllText(fixture.ScanPath);
        json = duplicate switch
        {
            "nested" => json.Replace("\"checkId\": \"axe.scan\"", "\"checkId\": \"axe.scan\", \"CheckId\": \"capture.tree\"", StringComparison.Ordinal),
            "case" => json[..^1] + ",\"RunId\":\"other\"}",
            "escaped" => json[..^1] + ",\"\\u0072unId\":\"other\"}",
            _ => json[..^1] + ",\"runId\":\"other\"}"
        };
        File.WriteAllText(fixture.ScanPath, json);

        Assert.ThrowsExactly<InvalidDataException>(() => AuditFiles.ReadVerified(fixture.ScanPath));
    }

    [TestMethod]
    [DataRow("duplicate-path")]
    [DataRow("duplicate-name")]
    [DataRow("output-collision")]
    [DataRow("traversal")]
    [DataRow("unknown-reference")]
    [DataRow("duplicate-reference")]
    public void ArtifactNamesPathsAndReferencesCannotBeAmbiguous(string mutation)
    {
        fixture.ChangeScan(json =>
        {
            var artifacts = json["artifacts"]!.AsArray();
            switch (mutation)
            {
                case "duplicate-path":
                    var duplicate = artifacts[0]!.DeepClone();
                    duplicate["path"] = @"EVIDENCE\SNAPSHOT.JSON";
                    artifacts.Add(duplicate);
                    break;
                case "duplicate-name":
                    var duplicateName = artifacts[0]!.DeepClone();
                    duplicateName["path"] = @"elsewhere\snapshot.json";
                    artifacts.Add(duplicateName);
                    break;
                case "output-collision": artifacts[0]!["path"] = @"report.html\payload.json"; break;
                case "traversal": artifacts[0]!["path"] = @"..\outside.json"; break;
                case "unknown-reference": json["results"]![0]!["evidence"] = new JsonArray("missing.json"); break;
                case "duplicate-reference":
                    json["results"]![0]!["evidence"] = new JsonArray(AuditTestFixture.AxePath, @"EVIDENCE\AXE.JSON");
                    break;
            }
        });

        Assert.ThrowsExactly<InvalidDataException>(() => AuditFiles.ReadVerified(fixture.ScanPath));
    }

    [TestMethod]
    [DataRow("duplicate")]
    [DataRow("orphan")]
    [DataRow("cycle")]
    public void SnapshotElementReferencesAreUnambiguous(string mutation)
    {
        fixture.ChangeScan(json =>
        {
            var nodes = json["snapshot"]!["nodes"]!.AsArray();
            if (mutation == "duplicate")
                nodes[1]!["id"] = "n0";
            else if (mutation == "orphan")
                nodes[1]!["parentId"] = "absent";
            else
                nodes[0]!["parentId"] = "n1";
        });
        Assert.ThrowsExactly<InvalidDataException>(() => AuditFiles.ReadVerified(fixture.ScanPath));
    }

    [TestMethod]
    [DataRow("snapshot-schema")]
    [DataRow("node-id")]
    [DataRow("parent-id")]
    [DataRow("observation-schema")]
    public void MalformedNativeFieldTypesAreRejectedAsInvalidData(string mutation)
    {
        fixture.ChangeScan(json =>
        {
            switch (mutation)
            {
                case "snapshot-schema": json["snapshot"]!["schemaVersion"] = "one"; break;
                case "node-id": json["snapshot"]!["nodes"]![0]!["id"] = 0; break;
                case "parent-id": json["snapshot"]!["nodes"]![1]!["parentId"] = 0; break;
                case "observation-schema": json["observation"]!["schemaVersion"] = false; break;
            }
        });
        Assert.ThrowsExactly<InvalidDataException>(() => AuditFiles.ReadVerified(fixture.ScanPath));
    }

    [TestMethod]
    [DataRow("snapshot")]
    [DataRow("snapshot+events")]
    [DataRow("snapshot+keyboard+events")]
    public void AllScannerModesRoundTripThroughVerification(string mode)
    {
        fixture.Report = fixture.Report with { Mode = mode };
        fixture.SaveScan();

        Assert.AreEqual(mode, AuditFiles.ReadVerified(fixture.ScanPath).Mode);
    }

    [TestMethod]
    public void OversizedFilesAreRejectedBeforeTheirContentsAreRead()
    {
        var path = Path.Combine(fixture.Source, "oversized.json");
        using (var stream = new FileStream(path, FileMode.CreateNew))
            stream.SetLength(32L * 1024 * 1024 + 1);
        Assert.ThrowsExactly<InvalidDataException>(() => AuditFiles.ReadVerified(path));
        using (var stream = new FileStream(path, FileMode.Open))
            stream.SetLength(128L * 1024 * 1024 + 1);
        Assert.ThrowsExactly<InvalidDataException>(() => AuditFiles.Describe(fixture.Source, "oversized.json", "application/json", "fixture"));
    }

    [TestMethod]
    public void ReparseDirectoryAndRootEscapesAreRejectedWhereSupported()
    {
        var outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.json"), "{}");
        var link = Path.Combine(fixture.Source, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("Creating a filesystem link is unavailable on this host: " + exception.Message);
        }
        try
        {
            Assert.ThrowsExactly<InvalidDataException>(() => AuditFiles.ResolveArtifact(fixture.Source, @"linked\secret.json"));
            Assert.ThrowsExactly<InvalidDataException>(() => AuditFiles.ResolveArtifact(link, "secret.json"));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [TestMethod]
    public void ReparseFileEvidenceIsRejectedWhereSupported()
    {
        var target = Path.Combine(fixture.Root, "outside.json");
        File.WriteAllText(target, "{}");
        var link = Path.Combine(fixture.Source, "linked.json");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("Creating a filesystem link is unavailable on this host: " + exception.Message);
        }
        try
        {
            Assert.ThrowsExactly<InvalidDataException>(() => AuditFiles.Describe(fixture.Source, "linked.json", "application/json", "fixture"));
        }
        finally
        {
            File.Delete(link);
        }
    }
}

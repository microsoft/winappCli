using System.Net;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class AuditVisualsTests
{
    private static readonly DateTimeOffset Time = AuditTestFixture.Time;
    private static readonly AuditImageRect DefaultRegion = new(-775, 150, 90, 32);

    [TestMethod]
    public void MapsPhysicalPixelsWithNegativeMonitorOriginWithoutAutomationId()
    {
        var (report, after, screenshot) = Data();
        var image = AuditVisuals.Map(report, after, screenshot, 600, 400).Single();

        Assert.AreEqual("located", image.Status);
        Assert.AreEqual(new AuditImageRect(25, 50, 90, 32), image.Region);
        Assert.AreEqual("n1", image.ElementId);
        StringAssert.Contains(image.Reason, "不等于原子采集");
    }

    [TestMethod]
    public void SnapshotLocalIdsMayChangeButRuntimeIdentityMustMatch()
    {
        var (report, after, screenshot) = Data();
        after = AuditTestFixture.Edit(after, json => json["nodes"]![1]!["id"] = "n99");
        Assert.AreEqual("located", AuditVisuals.Map(report, after, screenshot, 600, 400).Single().Status);
        after = AuditTestFixture.Edit(after, json => json["nodes"]![1]!["runtimeId"] = "[1,8,9]");
        Assert.AreEqual("unavailable", AuditVisuals.Map(report, after, screenshot, 600, 400).Single().Status);
    }

    [TestMethod]
    public void MovedElementsAreNotAttachedToTheOldImagePosition()
    {
        var (report, after, screenshot) = Data();
        after = AuditTestFixture.Edit(after, json => json["nodes"]![1]!["bounds"]!["left"] = -774);
        var image = AuditVisuals.Map(report, after, screenshot, 600, 400).Single();
        Assert.IsNull(image.Region);
        StringAssert.Contains(image.Reason, "移动");
    }

    [TestMethod]
    public void WindowMovementAndImageScaleMismatchDisableAllMarkers()
    {
        var (report, after, screenshot) = Data();
        var moved = AuditTestFixture.Edit(screenshot, json => json["captureBoundsAfter"]!["left"] = -799);
        Assert.IsNull(AuditVisuals.Map(report, after, moved, 600, 400).Single().Region);
        Assert.IsNull(AuditVisuals.Map(report, after, screenshot, 900, 600).Single().Region);
        var noOrigin = AuditTestFixture.Edit(screenshot, json => json.Remove("captureBoundsBefore"));
        Assert.IsNull(AuditVisuals.Map(report, after, noOrigin, 600, 400).Single().Region);
    }

    [TestMethod]
    public void OffscreenAndUnmatchedFindingsHaveExplicitUnavailableReasons()
    {
        var (report, after, screenshot) = Data();
        after = AuditTestFixture.Edit(after, json => json["nodes"]![1]!["isOffscreen"] = true);
        StringAssert.Contains(AuditVisuals.Map(report, after, screenshot, 600, 400).Single().Reason, "画面外");
        report = report with { Results = [report.Results[0] with { ElementId = null }] };
        Assert.IsNull(AuditVisuals.Map(report, after, screenshot, 600, 400).Single().Region);
    }

    [TestMethod]
    public void AxeLocationMustAgreeWithBothSnapshots()
    {
        var (report, after, screenshot) = Data();
        report = report with
        {
            Axe = report.Axe! with
            {
                Findings = [report.Axe.Findings[0] with
                {
                    ElementProperties = new Dictionary<string, string>
                    {
                        ["RuntimeId"] = "[1,2,3]", ["BoundingRectangle"] = "[l=-774,t=150,r=-684,b=182]"
                    }
                }]
            }
        };
        StringAssert.Contains(AuditVisuals.Map(report, after, screenshot, 600, 400).Single().Reason, "Axe");
    }

    [TestMethod]
    public void EvidenceMustBracketTheImageAndPreserveRootIdentity()
    {
        var (report, after, screenshot) = Data();
        var earlier = AuditTestFixture.Edit(after, json => json["capturedAtUtc"] = Time.AddMilliseconds(500));
        Assert.IsNull(AuditVisuals.Map(report, earlier, screenshot, 600, 400).Single().Region);
        var changedRoot = AuditTestFixture.Edit(after, json => json["rootRuntimeId"] = "[2A,99]");
        Assert.IsNull(AuditVisuals.Map(report, changedRoot, screenshot, 600, 400).Single().Region);
    }

    [TestMethod]
    public void PartiallyClippedControlIsLabeledAndNeverCropsOutsideImage()
    {
        var (report, after, screenshot) = Data(region: new(-810, 150, 90, 32));
        var image = AuditVisuals.Map(report, after, screenshot, 600, 400).Single();
        Assert.AreEqual("clipped", image.Status);
        Assert.AreEqual(new AuditImageRect(0, 50, 80, 32), image.Region);
    }

    [TestMethod]
    public void NamesAndTypesChangingAcrossSnapshotsInvalidateTheLocation()
    {
        var (report, after, screenshot) = Data();
        after = AuditTestFixture.Edit(after, json => json["nodes"]![1]!["name"] = "Different action");
        Assert.IsNull(AuditVisuals.Map(report, after, screenshot, 600, 400).Single().Region);
    }

    [TestMethod]
    public void InvalidCoordinatesDoNotBecomeUsableRectangles()
    {
        Assert.IsNull(AuditVisuals.ParseAxeBounds("[l=5,t=4,r=5,b=9]"));
        Assert.IsNull(AuditVisuals.ParseAxeBounds("not a rectangle"));
        Assert.IsNull(AuditVisuals.Rect(JsonSerializer.SerializeToElement(new { left = 0, top = 0, width = -1, height = 2 })));
    }

    [TestMethod]
    public void VerifiedImagesAreCroppedAndLinkedWithoutRepeatingPngDataPerIssue()
    {
        using var fixture = new AuditTestFixture();
        PrepareFiles(fixture);
        var report = fixture.Report with { Results = [fixture.Report.Results[0], fixture.Report.Results[0]] };
        var html = fixture.Render(report);

        StringAssert.Contains(html, "class=\"control-crop\"");
        StringAssert.Contains(html, "viewBox=\"0 0 1 1\"");
        StringAssert.Contains(html, "href=\"#location-1\"");
        StringAssert.Contains(html, "href=\"#result-0\"");
        StringAssert.Contains(html, "<use href=\"#audit-window-image\"/>");
        StringAssert.Contains(html, "snapshot-after-screenshot.json");
        Assert.AreEqual(1, html.Split("data:image/png;base64,", StringSplitOptions.None).Length - 1);
        Assert.AreEqual(1, html.Split("id=\"location-1\"", StringSplitOptions.None).Length - 1);
        Assert.IsFalse(html.Contains("<script", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void SeparatingCaptureAndCoverageRowsPreservesOriginalCropAndResultIndexes()
    {
        using var fixture = new AuditTestFixture();
        PrepareFiles(fixture);
        var html = fixture.Render(fixture.Report with
        {
            Results =
            [
                new() { CheckId = "capture.scope", Status = "observed", Summary = "Requested target verified.", Evidence = ["snapshot.json"] },
                new() { CheckId = "text.contrast", Status = "inconclusive", Summary = "Provider metadata withheld.", ElementId = "n1", Evidence = ["snapshot.json"] },
                fixture.Report.Results[0]
            ]
        });

        StringAssert.Contains(html, "<article id=\"result-2\" class=\"rule-failure\">");
        StringAssert.Contains(html, "<tr id=\"result-0\">");
        StringAssert.Contains(html, "class=\"result-reference\" id=\"result-1\"");
        StringAssert.Contains(html, "href=\"#result-2\"");
        StringAssert.Contains(html, "href=\"#node-1\"");
        StringAssert.Contains(html, "id=\"location-1\"");
        StringAssert.Contains(html, "href=\"#location-1\"");
        StringAssert.Contains(html, "class=\"control-crop\"");
        Assert.AreEqual(1, html.Split("data:image/png;base64,", StringSplitOptions.None).Length - 1);
        Assert.AreEqual(1, html.Split("class=\"control-crop\"", StringSplitOptions.None).Length - 1);
    }

    [TestMethod]
    public void MarkerLabelsAreEscapedAndChangedVerificationCannotProduceACrop()
    {
        using var fixture = new AuditTestFixture();
        PrepareFiles(fixture);
        var label = "<script>alert('label')</script>";
        var html = fixture.Render(fixture.Report with
        {
            Results = [fixture.Report.Results[0] with { ElementName = label }]
        });
        StringAssert.Contains(html, WebUtility.HtmlEncode(label));
        Assert.IsFalse(html.Contains("<script", StringComparison.OrdinalIgnoreCase));
        File.AppendAllText(Path.Combine(fixture.Source, AuditVisuals.VerificationPath), "tampered");
        var invalid = fixture.Render(name: "tampered.html");
        Assert.IsFalse(invalid.Contains("class=\"control-crop\"", StringComparison.Ordinal));
        StringAssert.Contains(invalid, "没有可靠的控件局部图");
    }

    [TestMethod]
    public void ScreenshotEvidenceAndVisualLinksSurviveAssessmentAttachment()
    {
        using var fixture = new AuditTestFixture();
        PrepareFiles(fixture);
        var assessment = fixture.Assessment() with
        {
            Entries = [new AuditAssessmentEntry
            {
                CheckId = "axe.scan", ElementId = "n1", Status = "confirmed",
                Explanation = "The rule result identifies the same element shown in the verified image.",
                Evidence = ["axe.json", "snapshot.json", "screenshot.png"]
            }]
        };
        var input = fixture.SaveAssessment(assessment);
        var original = AuditFiles.Hash(fixture.ScanPath);
        var result = AuditAssessment.Attach(fixture.ScanPath, input, fixture.Output);
        Assert.AreEqual(original, AuditFiles.Hash(Path.Combine(fixture.Output, "scan.json")));
        StringAssert.Contains(File.ReadAllText(result), "class=\"control-crop\"");
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Output, AuditVisuals.VerificationPath)));
    }

    private static void PrepareFiles(AuditTestFixture fixture)
    {
        var (report, after, screenshot) = Data(1, 1, new(-800, 100, 1, 1));
        AuditFiles.WriteNew(Path.Combine(fixture.Source, "snapshot.json"), report.Snapshot!.Value);
        AuditFiles.WriteNew(Path.Combine(fixture.Source, "axe.json"), report.Axe);
        AuditFiles.WriteNew(Path.Combine(fixture.Source, AuditVisuals.VerificationPath), after);
        AuditFiles.WriteNew(Path.Combine(fixture.Source, "screenshot.json"), screenshot);
        File.Copy(Path.Combine(fixture.Source, AuditTestFixture.ScreenshotPath), Path.Combine(fixture.Source, "screenshot.png"));
        fixture.Report = report with
        {
            Artifacts = new[] { "snapshot.json", "axe.json", AuditVisuals.VerificationPath, "screenshot.json", "screenshot.png" }
                .Select(path => AuditFiles.Describe(fixture.Source, path,
                    path.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "application/json", "Fixture")).ToArray()
        };
        fixture.SaveScan();
    }

    private static (AuditReport Report, JsonElement After, JsonElement Screenshot) Data(
        int width = 600, int height = 400, AuditImageRect? region = null)
    {
        region ??= DefaultRegion;
        object RootBounds() => new { left = -807, top = 100, width = width + 14, height = height + 7 };
        JsonElement Snapshot(DateTimeOffset at) => JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1, processId = 42, windowHandle = 73, capturedAtUtc = at,
            status = "captured", maxDepth = 20, maxElements = 2000, rootRuntimeId = "[2A,49]",
            depthLimitReached = false, elementLimitReached = false, issues = Array.Empty<object>(),
            nodes = new object[]
            {
                new
                {
                    id = "n0", parentId = (string?)null, depth = 0, runtimeId = "[2A,49]",
                    controlType = "Window", name = "Fixture", bounds = RootBounds(), textRuns = Array.Empty<object>(), issues = Array.Empty<object>()
                },
                new
                {
                    id = "n1", parentId = "n0", depth = 1, runtimeId = "[1,2,3]", automationId = (string?)null,
                    controlType = "Button", name = "Save", isPassword = false, isOffscreen = false,
                    bounds = new { region.Left, region.Top, region.Width, region.Height },
                    textRuns = Array.Empty<object>(), issues = Array.Empty<object>()
                }
            }
        }, Json.Options);
        var axe = AccessibilityScan.Create(42, 73, "2.4.2", [new(1,
        [
            new("NameNotNull", "Name missing.", "Provide a name.", "ObjectInformation",
                new Dictionary<string, string>
                {
                    ["RuntimeId"] = "[1,2,3]",
                    ["BoundingRectangle"] = FormattableString.Invariant($"[l={region.Left},t={region.Top},r={region.Right},b={region.Bottom}]")
                }, [])
        ])]);
        var report = new AuditReport
        {
            RunId = "visual-fixture", StartedAtUtc = Time, FinishedAtUtc = Time.AddSeconds(5),
            Status = "completed", Mode = "snapshot", Target = new(42, 73, "Fixture", Time.AddMinutes(-1), "Fixture", "[2A,49]"),
            WinappVersion = "fixture", Snapshot = Snapshot(Time), Axe = axe,
            Results = [new()
            {
                CheckId = "axe.scan", SourceRuleId = "NameNotNull", ElementId = "n1", ElementName = "Save",
                Status = "rule-failure", Summary = "Name missing.", Evidence = ["axe.json", "snapshot.json"]
            }]
        };
        var screenshot = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1, processId = 42, windowHandle = 73, captureMethod = "windows-graphics-capture",
            activationRequested = false, coordinateMappingStatus = "verified", width, height,
            captureStartedAtUtc = Time.AddSeconds(1), capturedAtUtc = Time.AddSeconds(2),
            captureBoundsBefore = new { left = -800, top = 100, width, height },
            captureBoundsAfter = new { left = -800, top = 100, width, height }
        }, Json.Options);
        return (report, Snapshot(Time.AddSeconds(3)), screenshot);
    }
}

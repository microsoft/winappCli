// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Tests;

/// <summary>
/// Phase 0 harness for the shared sample-index contract
/// (<c>docs/winui-sample-index.schema.json</c>) behind
/// <see href="https://github.com/microsoft/winappCli/issues/703">#703</see>.
///
/// <para>These tests exist to answer one question before we ask WinUI-Gallery or
/// CommunityToolkit to publish anything: <b>is the schema sufficient?</b> If a scenario
/// can't survive a round trip through the index, the contract is missing a field — and the
/// cheapest possible time to learn that is now, not after an upstream maintainer has
/// reviewed and merged it.</para>
///
/// <para>Hermetic: no network. The Reactor-shaped fixture mirrors the real published
/// <c>reactor-search-index.json</c>, including the top-level and control-level fields it
/// carries that this contract does not model.</para>
/// </summary>
[TestClass]
public class SampleIndexTests
{
    private static readonly string[] ButtonKeywords = ["click", "press me", "command bar"];
    private static readonly string[] FlexKeywords = ["css layout", "flexbox"];
    private static readonly string[] ButtonRelated = ["HyperlinkButton", "ToggleButton"];
    private static readonly string[] ButtonXmlns = ["xmlns:controls=\"using:CommunityToolkit.WinUI.Controls\""];

    /// <summary>
    /// A control whose scenarios populate every field the contract models, including the
    /// two that only XAML sources use (<c>xaml</c>, <c>xmlnsImports</c>) and the two
    /// descriptions that Gallery keeps separately (<c>description</c>/<c>details</c>).
    /// </summary>
    private static Scenario[] FullyPopulatedScenarios() =>
    [
        new Scenario
        {
            Id = "button-1",
            ControlId = "button",
            ControlName = "Button",
            HeaderText = "A simple button",
            Xaml = "<Button Content=\"Click me\" />",
            CSharp = "var button = new Button();",
            Source = "gallery",
            NuGetPackage = "CommunityToolkit.WinUI.Controls.Primitives",
            XmlnsImports = ButtonXmlns,
            Description = "A long-form description of Button, the kind ControlInfoData.json carries.",
            ControlDescription = "A control that responds to user input.",
            RelatedControls = ButtonRelated,
            ApiNamespace = "Microsoft.UI.Xaml.Controls",
            Docs =
            [
                new DocLink { Title = "Button class", Uri = "https://learn.microsoft.com/button" },
                new DocLink { Title = "Buttons guidance", Uri = "https://learn.microsoft.com/buttons" },
            ],
        },
        new Scenario
        {
            Id = "button-2",
            ControlId = "button",
            ControlName = "Button",
            HeaderText = "XAML-only sample",
            Xaml = "<Button Content=\"No code-behind\" />",
            CSharp = null,
            Source = "gallery",
            NuGetPackage = "CommunityToolkit.WinUI.Controls.Primitives",
            XmlnsImports = ButtonXmlns,
            Description = "A long-form description of Button, the kind ControlInfoData.json carries.",
            ControlDescription = "A control that responds to user input.",
            RelatedControls = ButtonRelated,
            ApiNamespace = "Microsoft.UI.Xaml.Controls",
            Docs =
            [
                new DocLink { Title = "Button class", Uri = "https://learn.microsoft.com/button" },
                new DocLink { Title = "Buttons guidance", Uri = "https://learn.microsoft.com/buttons" },
            ],
        },
    ];

    /// <summary>
    /// The heart of Phase 0: every field of every scenario survives
    /// <see cref="SampleIndexWriter.Write"/> → <see cref="SampleIndexParser.Parse"/>
    /// unchanged. A failure here means the published schema cannot carry our corpus.
    /// </summary>
    [TestMethod]
    public void RoundTrip_PreservesEveryScenarioField()
    {
        var original = FullyPopulatedScenarios();
        var tags = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["button"] = ButtonKeywords };

        var json = SampleIndexWriter.Write(original, tags, source: "gallery");
        var (roundTripped, _, _) = SampleIndexParser.Parse(json, "gallery");

        Assert.AreEqual(original.Length, roundTripped.Length, "every scenario must survive the round trip");

        for (int i = 0; i < original.Length; i++)
        {
            var (before, after) = (original[i], roundTripped[i]);
            Assert.AreEqual(before.Id, after.Id, "Id");
            Assert.AreEqual(before.ControlId, after.ControlId, "ControlId");
            Assert.AreEqual(before.ControlName, after.ControlName, "ControlName");
            Assert.AreEqual(before.HeaderText, after.HeaderText, "HeaderText");
            Assert.AreEqual(before.Xaml, after.Xaml, "Xaml");
            Assert.AreEqual(before.CSharp, after.CSharp, "CSharp");
            Assert.AreEqual(before.Source, after.Source, "Source");
            Assert.AreEqual(before.NuGetPackage, after.NuGetPackage, "NuGetPackage");
            Assert.AreEqual(before.Description, after.Description, "Description (long-form)");
            Assert.AreEqual(before.ControlDescription, after.ControlDescription, "ControlDescription (one-line)");
            Assert.AreEqual(before.ApiNamespace, after.ApiNamespace, "ApiNamespace");
            CollectionAssert.AreEqual(before.RelatedControls, after.RelatedControls, "RelatedControls");
            CollectionAssert.AreEqual(before.XmlnsImports, after.XmlnsImports, "XmlnsImports");

            Assert.AreEqual(before.Docs.Length, after.Docs.Length, "Docs length");
            for (int d = 0; d < before.Docs.Length; d++)
            {
                Assert.AreEqual(before.Docs[d].Title, after.Docs[d].Title, "Docs.Title");
                Assert.AreEqual(before.Docs[d].Uri, after.Docs[d].Uri, "Docs.Uri");
            }
        }
    }

    /// <summary>
    /// The two search dictionaries must stay separate through a round trip. They are
    /// weighted differently by the search engine (author-written terms outrank
    /// supplementary ones), so collapsing them into one field would silently re-rank
    /// results — and would have asked upstream for a field that demotes the better signal.
    /// </summary>
    [TestMethod]
    public void RoundTrip_KeepsCuratedKeywordsSeparateFromTags()
    {
        string[] supplementary = ["press", "tap"];
        string[] authorWritten = ["command", "submit"];

        var tags = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["button"] = supplementary };
        var curated = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["button"] = authorWritten };

        var json = SampleIndexWriter.Write(FullyPopulatedScenarios(), tags, curated, "gallery");
        var (_, roundTrippedTags, roundTrippedCurated) = SampleIndexParser.Parse(json, "gallery");

        CollectionAssert.AreEqual(supplementary, roundTrippedTags["button"], "supplementary terms");
        CollectionAssert.AreEqual(authorWritten, roundTrippedCurated["button"], "author-written terms");
    }

    /// <summary>
    /// An index that omits <c>curatedKeywords</c> — which is every index published today,
    /// Reactor's included — must still populate the supplementary slot from
    /// <c>keywords</c>, and must leave the author-written slot empty rather than
    /// duplicating into it. Changing that mapping would re-weight Reactor's live results.
    /// </summary>
    [TestMethod]
    public void Parse_WithoutCuratedKeywords_LeavesTheAuthorSlotEmpty()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "controls": [
            {
              "id": "flex",
              "keywords": ["css layout", "flexbox"],
              "samples": [ { "code": "new Flex()" } ]
            }
          ]
        }
        """;

        var (_, tags, curated) = SampleIndexParser.Parse(json, "reactor");

        CollectionAssert.AreEqual(FlexKeywords, tags["flex"]);
        Assert.AreEqual(0, curated.Count, "an absent curatedKeywords must not be back-filled from keywords");
    }

    /// <summary>Curated keywords are carried by the index, so a source that publishes one
    /// also retires our hand-maintained tag files.</summary>
    [TestMethod]
    public void RoundTrip_PreservesCuratedTags()
    {
        var tags = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["button"] = ButtonKeywords };

        var json = SampleIndexWriter.Write(FullyPopulatedScenarios(), tags, source: "gallery");
        var (_, roundTripped, _) = SampleIndexParser.Parse(json, "gallery");

        CollectionAssert.AreEqual(ButtonKeywords, roundTripped["button"]);
    }

    /// <summary>
    /// Content that came back through the index must still pass the corpus boundary guard.
    /// This is the miniature of the Phase 3 assertion that the malformed-XAML drop count
    /// goes to zero: an index round trip must not itself corrupt markup or code.
    /// </summary>
    [TestMethod]
    public void RoundTrip_OutputSurvivesTheScenarioSanitizer()
    {
        var json = SampleIndexWriter.Write(FullyPopulatedScenarios(), source: "gallery");
        var (scenarios, _, _) = SampleIndexParser.Parse(json, "gallery");

        ScenarioSanitizer.SanitizeAll(scenarios);

        Assert.IsTrue(scenarios.All(s => s.Xaml is not null), "well-formed XAML must not be dropped");
        Assert.IsNotNull(scenarios[0].CSharp, "brace-balanced C# must not be dropped");
    }

    /// <summary>Regenerating against unchanged data must produce an identical file, or the
    /// drift check upstream would report noise instead of change.</summary>
    [TestMethod]
    public void Write_IsDeterministicAndOrdersControlsById()
    {
        Scenario[] unordered =
        [
            new Scenario { Id = "zebra-1", ControlId = "zebra", ControlName = "Zebra", CSharp = "new Zebra();", Source = "gallery" },
            new Scenario { Id = "alpha-1", ControlId = "alpha", ControlName = "Alpha", CSharp = "new Alpha();", Source = "gallery" },
        ];

        var first = SampleIndexWriter.Write(unordered, source: "gallery");
        var second = SampleIndexWriter.Write(unordered, source: "gallery");

        Assert.AreEqual(first, second, "the same input must produce a byte-identical index");
        Assert.IsTrue(
            first.IndexOf("alpha", StringComparison.Ordinal) < first.IndexOf("zebra", StringComparison.Ordinal),
            "controls must be written in id order regardless of input order");
    }

    /// <summary>No timestamp by default — see <see cref="SampleIndexWriter.Write"/>.</summary>
    [TestMethod]
    public void Write_OmitsGeneratedTimestampUnlessRequested()
    {
        var withoutStamp = SampleIndexWriter.Write(FullyPopulatedScenarios(), source: "gallery");
        StringAssert.DoesNotMatch(withoutStamp, new System.Text.RegularExpressions.Regex("generatedAtUtc"));

        var withStamp = SampleIndexWriter.Write(
            FullyPopulatedScenarios(), source: "gallery", generatedAtUtc: DateTimeOffset.UnixEpoch);
        StringAssert.Contains(withStamp, "generatedAtUtc");
    }

    /// <summary>XAML-only samples are the common case for Gallery and Toolkit, and the shape
    /// Reactor's parser never had to handle.</summary>
    [TestMethod]
    public void Parse_KeepsXamlOnlySamples_AndSkipsEmptyOnes()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "controls": [
            {
              "id": "button",
              "samples": [
                { "header": "xaml only", "xaml": "<Button />" },
                { "header": "nothing at all" },
                { "header": "blank strings", "xaml": "  ", "code": "  " },
                { "header": "code only", "code": "new Button();" }
              ]
            }
          ]
        }
        """;

        var (scenarios, _, _) = SampleIndexParser.Parse(json, "gallery");

        Assert.AreEqual(2, scenarios.Length, "samples with neither xaml nor code are skipped");
        Assert.AreEqual("button-1", scenarios[0].Id, "kept ids stay contiguous from 1");
        Assert.AreEqual("<Button />", scenarios[0].Xaml);
        Assert.IsNull(scenarios[0].CSharp, "a xaml-only sample must not gain empty C#");
        Assert.AreEqual("button-2", scenarios[1].Id);
        Assert.IsNull(scenarios[1].Xaml);
    }

    /// <summary>A control-level <c>usings</c> list must not turn a xaml-only sample into a
    /// scenario whose C# is nothing but using directives.</summary>
    [TestMethod]
    public void Parse_DoesNotFoldUsingsIntoASampleWithNoCode()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "controls": [
            {
              "id": "flex",
              "usings": ["Microsoft.UI.Reactor.Flex"],
              "samples": [ { "header": "xaml only", "xaml": "<Flex />" } ]
            }
          ]
        }
        """;

        var (scenarios, _, _) = SampleIndexParser.Parse(json, "gallery");

        Assert.AreEqual(1, scenarios.Length);
        Assert.IsNull(scenarios[0].CSharp, "usings alone are not a sample");
    }

    /// <summary>
    /// The version gate is the whole point of publishing <c>schemaVersion</c>: a future
    /// document must be refused rather than read with today's field meanings. An absent
    /// version is accepted so the contract stays compatible with indexes that predate it.
    /// </summary>
    [TestMethod]
    public void Parse_RefusesAnUnknownSchemaVersion()
    {
        const string future = """
        { "schemaVersion": 2, "controls": [ { "id": "button", "samples": [ { "code": "new Button();" } ] } ] }
        """;
        const string notANumber = """
        { "schemaVersion": "1", "controls": [ { "id": "button", "samples": [ { "code": "new Button();" } ] } ] }
        """;
        const string decimalInteger = """
        { "schemaVersion": 1.0, "controls": [ { "id": "button", "samples": [ { "code": "new Button();" } ] } ] }
        """;
        const string absent = """
        { "controls": [ { "id": "button", "samples": [ { "code": "new Button();" } ] } ] }
        """;

        Assert.AreEqual(0, SampleIndexParser.Parse(future, "gallery").scenarios.Length, "a newer contract is refused");
        Assert.AreEqual(0, SampleIndexParser.Parse(notANumber, "gallery").scenarios.Length, "a non-numeric version is refused");
        Assert.AreEqual(1, SampleIndexParser.Parse(decimalInteger, "gallery").scenarios.Length, "a decimal integer version is read as v1");
        Assert.AreEqual(1, SampleIndexParser.Parse(absent, "gallery").scenarios.Length, "an absent version is read as v1");
    }

    /// <summary>The reader runs on untrusted network content, so wrongly-typed array entries
    /// are skipped rather than thrown on.</summary>
    [TestMethod]
    public void Parse_SkipsNonObjectEntriesInsteadOfThrowing()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "controls": [
            "not a control",
            42,
            { "id": "button", "samples": [ "not a sample", { "code": "new Button();" } ] }
          ]
        }
        """;

        var (scenarios, _, _) = SampleIndexParser.Parse(json, "gallery");

        Assert.AreEqual(1, scenarios.Length);
        Assert.AreEqual("button-1", scenarios[0].Id);
    }

    /// <summary>A source can't label its samples as another source's, because
    /// <c>Source</c> is stamped by the caller and never read from the document.</summary>
    [TestMethod]
    public void Parse_IgnoresTheDocumentsOwnSourceField()
    {
        const string json = """
        { "schemaVersion": 1, "source": "gallery", "controls": [ { "id": "x", "samples": [ { "code": "new X();" } ] } ] }
        """;

        var (scenarios, _, _) = SampleIndexParser.Parse(json, "reactor");

        Assert.AreEqual("reactor", scenarios[0].Source);
    }

    /// <summary>
    /// Unmodelled fields must not break a reader — the real Reactor index already carries
    /// <c>generatedFrom</c>, <c>category</c> and <c>galleryRoute</c>, and upstream repos will
    /// add their own. This is why the schema sets <c>additionalProperties: true</c>.
    /// </summary>
    [TestMethod]
    public void Parse_ToleratesFieldsTheContractDoesNotModel()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "source": "reactor",
          "generatedFrom": "some/path.csproj",
          "controls": [
            {
              "id": "flex",
              "name": "Flex",
              "category": "Layout",
              "galleryRoute": "/flex",
              "samples": [ { "header": "Basic flex", "language": "csharp", "code": "new Flex()" } ]
            }
          ]
        }
        """;

        var (scenarios, _, _) = SampleIndexParser.Parse(json, "reactor");

        Assert.AreEqual(1, scenarios.Length);
        Assert.AreEqual("Flex", scenarios[0].ControlName);
    }

    /// <summary>
    /// The published schema, the reader and the writer must describe the same contract.
    /// Without this, a field added to <see cref="SampleIndexSchema"/> would silently not be
    /// in the document we hand upstream — or worse, we'd ask upstream for a field nothing
    /// reads.
    /// </summary>
    [TestMethod]
    public void Schema_DeclaresExactlyTheFieldsTheReaderAndWriterUse()
    {
        var schemaPath = Path.Join(AppContext.BaseDirectory, "winui-sample-index.schema.json");
        Assert.IsTrue(File.Exists(schemaPath), $"schema not found at {schemaPath}");

        using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var root = schema.RootElement;
        var defs = root.GetProperty("$defs");

        AssertPropertiesMatch(SampleIndexSchema.DocumentProperties, root, "document");
        AssertPropertiesMatch(SampleIndexSchema.ControlProperties, defs.GetProperty("control"), "control");
        AssertPropertiesMatch(SampleIndexSchema.DocLinkProperties, defs.GetProperty("docLink"), "docLink");
        AssertPropertiesMatch(SampleIndexSchema.SampleProperties, defs.GetProperty("sample"), "sample");

        Assert.AreEqual(
            SampleIndexSchema.Version,
            root.GetProperty("properties").GetProperty(SampleIndexSchema.SchemaVersion).GetProperty("const").GetInt32(),
            "the schema's pinned schemaVersion must match the version the reader accepts");
    }

    private static void AssertPropertiesMatch(string[] expected, JsonElement schemaNode, string level)
    {
        var declared = schemaNode.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();

        CollectionAssert.AreEquivalent(
            expected,
            declared,
            $"the {level} properties in docs/winui-sample-index.schema.json and SampleIndexSchema have drifted apart");
    }

    private static readonly string[] SegmentedXmlns = ["xmlns:tk=\"using:CommunityToolkit.WinUI.Controls\""];
    private static readonly string[] SegmentedMediaXmlns = ["xmlns:media=\"using:CommunityToolkit.WinUI.Media\""];
    private static readonly string[] ContosoXmlns = ["xmlns:c=\"using:Contoso\""];
    private static readonly Dictionary<string, string[]> NoKeywords = [];

    /// <summary>Two samples of one control that disagree on their prose and their imports —
    /// the shape every CommunityToolkit control has, because each sample is authored as its
    /// own markdown file with its own XAML.</summary>
    private static Scenario[] DivergentSamples() =>
    [
        new Scenario
        {
            Id = "segmented-1",
            ControlId = "segmented",
            ControlName = "Segmented",
            HeaderText = "Basic Segmented",
            Xaml = "<tk:Segmented />",
            Source = "toolkit",
            Description = "Shows the default Segmented control.",
            XmlnsImports = SegmentedXmlns,
        },
        new Scenario
        {
            Id = "segmented-2",
            ControlId = "segmented",
            ControlName = "Segmented",
            HeaderText = "Segmented with icons",
            Xaml = "<media:Segmented />",
            Source = "toolkit",
            Description = "Shows Segmented rendering icon-only items.",
            XmlnsImports = SegmentedMediaXmlns,
        },
    ];

    /// <summary>
    /// Sibling samples of one control can legitimately need different prose and different
    /// XAML namespace imports. If the contract could only carry those per control, a Toolkit
    /// index would silently collapse every sample onto the first one's values — emitting XAML
    /// with prefixes it never declares.
    /// </summary>
    [TestMethod]
    public void RoundTrip_KeepsDetailsAndImportsPerSampleWhenSiblingsDisagree()
    {
        var json = SampleIndexWriter.Write(DivergentSamples(), NoKeywords, NoKeywords, source: "toolkit");

        var control = JsonDocument.Parse(json).RootElement
            .GetProperty(SampleIndexSchema.Controls)[0];
        Assert.IsFalse(
            control.TryGetProperty(SampleIndexSchema.Details, out _),
            "details must not be hoisted to the control when the samples disagree");
        Assert.IsFalse(
            control.TryGetProperty(SampleIndexSchema.XmlnsImports, out _),
            "xmlnsImports must not be hoisted to the control when the samples disagree");

        var (scenarios, _, _) = SampleIndexParser.Parse(json, "toolkit");

        var basic = scenarios.Single(s => s.HeaderText == "Basic Segmented");
        var icons = scenarios.Single(s => s.HeaderText == "Segmented with icons");
        Assert.AreEqual("Shows the default Segmented control.", basic.Description);
        Assert.AreEqual("Shows Segmented rendering icon-only items.", icons.Description);
        CollectionAssert.AreEqual(SegmentedXmlns, basic.XmlnsImports);
        CollectionAssert.AreEqual(SegmentedMediaXmlns, icons.XmlnsImports);
    }

    /// <summary>
    /// The common case — every sample of a control shares one description and one set of
    /// imports, as WinUI-Gallery's do. Writing those once per control instead of once per
    /// sample is what keeps the published file small, so assert the hoist actually happens
    /// rather than only that the values survive.
    /// </summary>
    [TestMethod]
    public void Write_HoistsDetailsAndImportsToTheControlWhenEverySampleAgrees()
    {
        var json = SampleIndexWriter.Write(FullyPopulatedScenarios(), NoKeywords, NoKeywords, source: "gallery");

        var button = JsonDocument.Parse(json).RootElement
            .GetProperty(SampleIndexSchema.Controls)
            .EnumerateArray()
            .Single(c => c.GetProperty(SampleIndexSchema.Id).GetString() == "button");

        Assert.IsTrue(button.TryGetProperty(SampleIndexSchema.Details, out _));
        Assert.IsTrue(button.TryGetProperty(SampleIndexSchema.XmlnsImports, out _));

        foreach (var sample in button.GetProperty(SampleIndexSchema.Samples).EnumerateArray())
        {
            Assert.IsFalse(
                sample.TryGetProperty(SampleIndexSchema.Details, out _),
                "a sample must not repeat the control's details");
            Assert.IsFalse(
                sample.TryGetProperty(SampleIndexSchema.XmlnsImports, out _),
                "a sample must not repeat the control's xmlnsImports");
        }
    }

    /// <summary>
    /// An upstream author writing an index by hand will put shared prose on the control and
    /// leave it off the samples. Readers must inherit it rather than report no description.
    /// </summary>
    [TestMethod]
    public void Parse_InheritsControlDetailsAndImportsWhenASampleOmitsThem()
    {
        const string Json = """
        {
          "schemaVersion": 1,
          "source": "gallery",
          "controls": [
            {
              "id": "button",
              "name": "Button",
              "details": "Shared prose written once.",
              "xmlnsImports": ["xmlns:c=\"using:Contoso\""],
              "samples": [
                { "header": "One", "xaml": "<c:Button />" },
                { "header": "Two", "xaml": "<c:Button IsEnabled=\"False\" />" }
              ]
            }
          ]
        }
        """;

        var (scenarios, _, _) = SampleIndexParser.Parse(Json, "gallery");

        Assert.AreEqual(2, scenarios.Length);
        foreach (var scenario in scenarios)
        {
            Assert.AreEqual("Shared prose written once.", scenario.Description);
            CollectionAssert.AreEqual(ContosoXmlns, scenario.XmlnsImports);
        }
    }

    [TestMethod]
    public void Parse_DropsCodeTaggedAsALanguageOtherThanCSharp()
    {
        const string Json = """
        {
          "schemaVersion": 1,
          "source": "gallery",
          "controls": [
            {
              "id": "button",
              "name": "Button",
              "samples": [
                { "header": "Markup kept", "xaml": "<Button />", "code": "auto x = 1;", "language": "cpp" },
                { "header": "Nothing usable", "code": "auto y = 2;", "language": "cpp" },
                { "header": "Real C#", "code": "var b = 2;", "language": "csharp" }
              ]
            }
          ]
        }
        """;

        var (scenarios, _, _) = SampleIndexParser.Parse(Json, "gallery");

        // The C++ markup sample keeps its XAML but loses its code; the code-only C++
        // sample has nothing left and drops out, so ids stay contiguous from 1.
        Assert.AreEqual(2, scenarios.Length);
        Assert.AreEqual("button-1", scenarios[0].Id);
        Assert.AreEqual("<Button />", scenarios[0].Xaml);
        Assert.IsNull(scenarios[0].CSharp);
        Assert.AreEqual("button-2", scenarios[1].Id);
        Assert.AreEqual("Real C#", scenarios[1].HeaderText);
        Assert.AreEqual("var b = 2;", scenarios[1].CSharp);
    }

    [TestMethod]
    public void Parse_TreatsAnAbsentLanguageTagAsCSharp()
    {
        const string Json = """
        {
          "schemaVersion": 1,
          "source": "gallery",
          "controls": [
            {
              "id": "button",
              "name": "Button",
              "samples": [
                { "header": "Untagged", "code": "var a = 1;" },
                { "header": "Tagged", "code": "var b = 2;", "language": "CSharp" }
              ]
            }
          ]
        }
        """;

        var (scenarios, _, _) = SampleIndexParser.Parse(Json, "gallery");

        Assert.AreEqual(2, scenarios.Length);
        Assert.AreEqual("var a = 1;", scenarios[0].CSharp);
        Assert.AreEqual("var b = 2;", scenarios[1].CSharp);
    }

    [TestMethod]
    public void Parse_DropsCodeWhoseLanguageTagIsNotAString()
    {
        // A tag of the wrong JSON type is malformed, not absent, so it must not fall
        // back to the absent-means-C# default and reach the user as pasteable C#.
        const string Json = """
        {
          "schemaVersion": 1,
          "source": "gallery",
          "controls": [
            {
              "id": "button",
              "name": "Button",
              "samples": [
                { "header": "Numeric tag", "xaml": "<Button />", "code": "auto x = 1;", "language": 42 },
                { "header": "Null tag", "code": "auto y = 2;", "language": null },
                { "header": "Empty tag", "code": "auto z = 3;", "language": "" },
                { "header": "Real C#", "code": "var b = 2;", "language": "csharp" }
              ]
            }
          ]
        }
        """;

        var (scenarios, _, _) = SampleIndexParser.Parse(Json, "gallery");

        // Only the XAML of the numeric-tag sample survives; the three malformed
        // code-only samples drop out entirely, so ids stay contiguous from 1.
        Assert.AreEqual(2, scenarios.Length);
        Assert.AreEqual("button-1", scenarios[0].Id);
        Assert.AreEqual("<Button />", scenarios[0].Xaml);
        Assert.IsNull(scenarios[0].CSharp);
        Assert.AreEqual("button-2", scenarios[1].Id);
        Assert.AreEqual("var b = 2;", scenarios[1].CSharp);
    }

    [TestMethod]
    public void Parse_DropsAnOversizedUsingsBlockRatherThanCopyingItOntoEverySample()
    {
        // One control-level using longer than the cap, on a control with several samples:
        // the prefix is what gets multiplied, so it is dropped while the samples survive.
        var huge = new string('x', 9 * 1024);
        var json = $$"""
        {
          "schemaVersion": 1,
          "source": "gallery",
          "controls": [
            {
              "id": "button",
              "name": "Button",
              "usings": ["{{huge}}"],
              "samples": [
                { "header": "One", "code": "var a = 1;" },
                { "header": "Two", "code": "var b = 2;" }
              ]
            }
          ]
        }
        """;

        var (scenarios, _, _) = SampleIndexParser.Parse(json, "gallery");

        Assert.AreEqual(2, scenarios.Length);
        Assert.AreEqual("var a = 1;", scenarios[0].CSharp);
        Assert.AreEqual("var b = 2;", scenarios[1].CSharp);
    }

    [TestMethod]
    public void Parse_KeepsANormalUsingsBlockOnEverySample()
    {
        const string Json = """
        {
          "schemaVersion": 1,
          "source": "gallery",
          "controls": [
            {
              "id": "button",
              "name": "Button",
              "usings": ["Microsoft.UI.Xaml.Controls"],
              "samples": [
                { "header": "One", "code": "var a = 1;" }
              ]
            }
          ]
        }
        """;

        var (scenarios, _, _) = SampleIndexParser.Parse(Json, "gallery");

        Assert.AreEqual(1, scenarios.Length);
        Assert.AreEqual("using Microsoft.UI.Xaml.Controls;\n\nvar a = 1;", scenarios[0].CSharp);
    }
}

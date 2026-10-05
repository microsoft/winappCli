// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Tests;

/// <summary>
/// Hermetic tests for <see cref="ReactorFetcher"/>'s JSON parser — the
/// reactor-search-index.json → <see cref="Scenario"/> mapping. No network: the
/// index document is supplied inline so the C#-only shape, curated-keyword tags,
/// control-level usings carried alongside code, and empty-sample skipping are all verified
/// against a fixed input.
/// </summary>
[TestClass]
public class ReactorFetcherTests
{
    private static readonly string[] FlexKeywords = ["css layout", "flex", "flexbox"];
    private static readonly string[] AcrylicKeywords = ["material", "blur"];

    private const string SampleIndex = """
    {
      "controls": [
        {
          "id": "flex",
          "name": "Flex",
          "description": "CSS-style flex container.",
          "apiNamespace": "Microsoft.UI.Reactor",
          "nugetPackage": "Microsoft.UI.Reactor",
          "relatedControls": ["Grid", "StackPanel"],
          "usings": ["Microsoft.UI.Reactor.Flex"],
          "keywords": ["css layout", "flex", "flexbox"],
          "samples": [
            { "header": "Basic flex", "language": "csharp", "code": "new Flex()" },
            { "header": "Empty sample", "language": "csharp", "code": "   " }
          ]
        },
        {
          "id": "acrylic",
          "name": "Acrylic",
          "description": "Translucent material.",
          "apiNamespace": "Microsoft.UI.Reactor",
          "nugetPackage": "Microsoft.UI.Reactor",
          "keywords": ["material", "blur"],
          "samples": [
            { "header": "Acrylic brush", "language": "csharp", "code": "new Acrylic()" }
          ]
        },
        {
          "id": "",
          "name": "NoId",
          "samples": [ { "header": "x", "code": "new NoId()" } ]
        }
      ]
    }
    """;

    [TestMethod]
    public void Parse_MapsControlsToReactorScenarios()
    {
        var (scenarios, _) = ReactorFetcher.Parse(SampleIndex);

        // flex (1 kept, 1 empty skipped) + acrylic (1). The id-less control is skipped.
        Assert.AreEqual(2, scenarios.Length);
        Assert.IsTrue(scenarios.All(s => s.Source == "reactor"));
        Assert.IsTrue(scenarios.All(s => s.Xaml == null), "reactor samples are C#-only");
        Assert.IsTrue(scenarios.All(s => s.NuGetPackage == "Microsoft.UI.Reactor"));
    }

    [TestMethod]
    public void Parse_SkipsEmptyCodeSamples_AndKeepsIdsContiguous()
    {
        var (scenarios, _) = ReactorFetcher.Parse(SampleIndex);

        var flex = scenarios.Where(s => s.ControlId == "flex").ToList();
        Assert.AreEqual(1, flex.Count, "the whitespace-only sample must be skipped");
        Assert.AreEqual("flex-1", flex[0].Id, "kept sample ids stay contiguous from 1");
    }

    [TestMethod]
    public void Parse_CarriesControlLevelUsingsAlongsideSampleCode()
    {
        var (scenarios, _) = ReactorFetcher.Parse(SampleIndex);

        // Carried as data, not prepended: the code is a class-body fragment, so a `using`
        // glued to the front of it lands where C# does not allow one.
        var flex = scenarios.Single(s => s.ControlId == "flex");
        CollectionAssert.AreEqual(new[] { "Microsoft.UI.Reactor.Flex" }, flex.Usings);
        Assert.AreEqual("new Flex()", flex.CSharp);

        // Controls without usings keep their code verbatim and carry no imports.
        var acrylic = scenarios.Single(s => s.ControlId == "acrylic");
        Assert.AreEqual("new Acrylic()", acrylic.CSharp);
        Assert.AreEqual(0, acrylic.Usings.Length);
    }

    [TestMethod]
    public void Parse_ServesCuratedKeywordsVerbatimAsTags()
    {
        var (_, tags) = ReactorFetcher.Parse(SampleIndex);

        CollectionAssert.AreEqual(FlexKeywords, tags["flex"]);
        CollectionAssert.AreEqual(AcrylicKeywords, tags["acrylic"]);
    }

    [TestMethod]
    public void Parse_MalformedOrEmpty_ReturnsNoScenarios()
    {
        Assert.AreEqual(0, ReactorFetcher.Parse("{}").scenarios.Length);
        Assert.AreEqual(0, ReactorFetcher.Parse("""{ "controls": "nope" }""").scenarios.Length);
        Assert.AreEqual(0, ReactorFetcher.Parse("[]").scenarios.Length);
    }

    [TestMethod]
    public void Parse_InvalidJson_Throws()
    {
        // Parse does not swallow parse errors — CachedProviderBase.LoadAsync catches
        // them and falls back to Empty, so the contract here is "throws on garbage".
        Assert.Throws<System.Text.Json.JsonException>(() => ReactorFetcher.Parse(""));
        Assert.Throws<System.Text.Json.JsonException>(() => ReactorFetcher.Parse("not json"));
    }
}

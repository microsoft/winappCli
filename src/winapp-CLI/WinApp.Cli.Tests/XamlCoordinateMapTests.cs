// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services.DevTools.Comments;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class XamlCoordinateMapTests
{
    private const string Root = "<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">";
    private const string Heading = "                        <TextBlock Text=\"{x:Bind ViewModel.ListHeading, Mode=OneWay}\" FontFamily=\"Georgia\" FontSize=\"24\" />";
    private const string RewrittenHeading = "                        <TextBlock x:ConnectionId='36'                                                    FontFamily=\"Georgia\" FontSize=\"24\" />";
    private const string Border = "                                <Border Background=\"{ThemeResource SkyBrush}\" CornerRadius=\"9,9,0,0\">";

    [TestMethod]
    public void RecordedPair_MapsGeneratedColumnToOriginalDeclaration_NotAnAdjustedStoredColumn()
    {
        var source = Root + "\n" + new string('\n', 221) + Border + "\n" +
            new string('\n', 22) + Heading + "\n</Border>\n</Page>";
        var generated = source.Replace(Heading, RewrittenHeading, StringComparison.Ordinal);
        var fixture = Fixture(source, generated);
        Assert.AreEqual(123, Heading.Length);
        Assert.AreEqual(143, RewrittenHeading.Length);
        Assert.AreEqual(108, Heading.IndexOf("FontSize", StringComparison.Ordinal) + 1);
        Assert.AreEqual(128, RewrittenHeading.IndexOf("FontSize", StringComparison.Ordinal) + 1);
        var map = fixture.Create();
        var hit = map.Resolve(fixture.Proof, "MainPage.xaml", 246, 128, "Microsoft.UI.Xaml.Controls.TextBlock", null);
        Assert.AreEqual(Heading.TrimStart(), hit.Declaration);
        Assert.AreEqual(246, hit.RawLine);
        Assert.AreEqual(128, hit.RawColumn, "Raw capture remains unchanged; the mapping identifies a declaration, not an attribute.");
        Assert.AreEqual(246, hit.AuthoredLine);
        Assert.AreEqual(25, hit.AuthoredColumn);
        Assert.IsTrue(hit.Advisory, "No evidence links this current build to a legacy capture or running image.");
        Assert.AreEqual("unique-source-line", hit.Provenance);
        var border = map.Resolve(fixture.Proof, "MainPage.xaml", 223, 79, "Border", null);
        Assert.AreEqual(Border.TrimStart(), border.Declaration);
        Assert.AreEqual(33, border.AuthoredColumn);
    }

    [TestMethod]
    public void CompactSameTypeBindings_RejectEvenAnExactGeneratedColumn()
    {
        const string first = "<TextBlock Text=\"{x:Bind Vm.First}\" />";
        const string second = "<TextBlock Text=\"{x:Bind Vm.Second}\" />";
        var source = Root + "\n<Grid>" + first + second + "</Grid>\n</Page>";
        var generated = Root + "\n<Grid>" + Rewrite(first, 1) + Rewrite(second, 12345) + "</Grid>\n</Page>";
        var fixture = Fixture(source, generated);
        var map = fixture.Create();
        var line = generated.Split('\n')[1];
        var column = line.LastIndexOf("<TextBlock", StringComparison.Ordinal) + 2;
        Assert.ThrowsExactly<InvalidDataException>(() => map.Resolve(fixture.Proof, "MainPage.xaml", 2, column, "TextBlock", null));
        Assert.ThrowsExactly<InvalidDataException>(() => map.Resolve(fixture.Proof, "MainPage.xaml", 2, 0, "TextBlock", null));
        Assert.ThrowsExactly<InvalidDataException>(() => map.Resolve(fixture.Proof, "MainPage.xaml", 2, 2, "TextBlock", null));
        Assert.ThrowsExactly<InvalidDataException>(() => map.Resolve(fixture.Proof, "MainPage.xaml", 2, column, "Button", null));
        Assert.ThrowsExactly<InvalidDataException>(() => map.Resolve(fixture.Proof, "MainPage.xaml", 2, column, "TextBlock", "WrongName"));
    }

    [TestMethod]
    [DataRow("\n")]
    [DataRow("\r\n")]
    public void ContinuationAndQuotedMarkup_RetainFullAuthoredDeclaration(string newline)
    {
        var element = "<TextBlock\n x:Name=\"Heading\"\n Text=\"{x:Bind Vm.Title,\n Mode=OneWay}\"\n Tag=\"&gt;&lt;\"\n />";
        var source = (Root + "\n" + element + "\n</Page>").Replace("\n", newline, StringComparison.Ordinal);
        var generated = (Root + "\n" + Rewrite(element, 7) + "\n</Page>").Replace("\n", newline, StringComparison.Ordinal);
        var fixture = Fixture(source, generated);
        var hit = fixture.Create().Resolve(fixture.Proof, "MainPage.xaml", 6, 4, "TextBlock", "Heading");
        Assert.AreEqual(element.Replace("\n", newline, StringComparison.Ordinal), hit.Declaration);
        Assert.AreEqual(2, hit.AuthoredLine);
    }

    [TestMethod]
    [DataRow("literal")]
    [DataRow("removed-element")]
    [DataRow("reordered")]
    [DataRow("unexplained-connection")]
    [DataRow("retained-binding")]
    [DataRow("missing-connection")]
    [DataRow("duplicate-connection")]
    public void UnknownRewrite_FailsClosed(string change)
    {
        const string a = "<TextBlock Text=\"{x:Bind Vm.Title}\" FontSize=\"24\" />";
        const string b = "<Button Content=\"{x:Bind Vm.Button}\" />";
        var source = Root + "\n" + a + b + "\n</Page>";
        var generated = Root + "\n" + Rewrite(a, 1) + Rewrite(b, 2) + "\n</Page>";
        generated = change switch
        {
            "literal" => generated.Replace("FontSize=\"24\"", "FontSize=\"25\"", StringComparison.Ordinal),
            "removed-element" => Root + "\n" + Rewrite(a, 1) + "\n</Page>",
            "reordered" => Root + "\n" + Rewrite(b, 2) + Rewrite(a, 1) + "\n</Page>",
            "unexplained-connection" => generated.Replace("<Page ", "<Page x:ConnectionId='3' ", StringComparison.Ordinal),
            "retained-binding" => generated.Replace(Rewrite(a, 1), a.Replace("<TextBlock ", "<TextBlock x:ConnectionId='1' ", StringComparison.Ordinal), StringComparison.Ordinal),
            "missing-connection" => generated.Replace(" x:ConnectionId='1'", "", StringComparison.Ordinal),
            "duplicate-connection" => generated.Replace("ConnectionId='2'", "ConnectionId='1'", StringComparison.Ordinal),
            _ => throw new AssertFailedException(change),
        };
        Assert.ThrowsExactly<InvalidDataException>(() => Fixture(source, generated).Create());
    }

    [TestMethod]
    [DataRow("project")]
    [DataRow("config")]
    [DataRow("compiler")]
    [DataRow("resource")]
    [DataRow("link")]
    [DataRow("output")]
    [DataRow("xbf-output")]
    [DataRow("timestamp")]
    [DataRow("source-hash")]
    [DataRow("generated-hash")]
    [DataRow("xbf-hash")]
    [DataRow("missing-snapshot")]
    [DataRow("resource-map")]
    [DataRow("component-location")]
    public void MissingStaleOrForeignEvidence_IsNotAdmitted(string change)
    {
        var fixture = Fixture(Root + Heading + "</Page>", Root + RewrittenHeading + "</Page>");
        var proof = fixture.Proof;
        var invalid = change switch
        {
            "project" => proof with { ProjectPath = @"C:\other\Daylight.csproj" },
            "config" => proof with { ConfigurationIdentity = "" },
            "compiler" => proof with { CompilerIdentity = "" },
            "resource" => proof with { ResourcePath = "Other.xaml" },
            "link" => proof with { Link = "Other.xaml" },
            "output" => proof with { GeneratedPath = @"C:\other\MainPage.xaml" },
            "xbf-output" => proof with { XbfPath = @"C:\other\MainPage.xbf" },
            "timestamp" => proof with { SavedSourceWriteTicks = proof.SourceWriteTicks - 1 },
            "source-hash" => proof with { SourceHash = new string('A', 64) },
            "generated-hash" => proof with { GeneratedHash = new string('A', 64) },
            "xbf-hash" => proof with { XbfHash = new string('A', 64) },
            "missing-snapshot" => proof,
            "resource-map" => proof with { ResourceMapName = "ForeignMap" },
            "component-location" => proof with { ComponentResourceLocation = "Library" },
            _ => throw new AssertFailedException(change),
        };
        var snapshot = change == "missing-snapshot" ? fixture.Snapshot with { Files = [] } : fixture.Snapshot;
        Assert.ThrowsExactly<InvalidDataException>(() =>
            XamlCoordinateMap.Create(snapshot, invalid, fixture.Source, fixture.Generated, fixture.Xbf));
    }

    [TestMethod]
    public void ValidLink_MapsOnlyTheExplicitResourceAndCurrentBuild()
    {
        var fixture = Fixture(Root + "\n" + Heading + "\n</Page>", Root + "\n" + RewrittenHeading + "\n</Page>");
        var proof = fixture.Proof with
        {
            Link = @"Views\Header.xaml", ResourcePath = @"Views\Header.xaml",
            GeneratedPath = Path.Combine(fixture.Proof.IntermediateRoot, @"Views\Header.xaml"),
            XbfPath = Path.Combine(fixture.Proof.IntermediateRoot, @"Views\Header.xbf"),
        };
        var map = XamlCoordinateMap.Create(fixture.Snapshot, proof, fixture.Source, fixture.Generated, fixture.Xbf);
        Assert.AreEqual(Heading.TrimStart(), map.Resolve(proof, @"Views\Header.xaml", 2, 128, "TextBlock", null).Declaration);
        Assert.ThrowsExactly<InvalidDataException>(() => map.Resolve(proof, "MainPage.xaml", 2, 128, "TextBlock", null));
        Assert.ThrowsExactly<InvalidDataException>(() => map.Resolve(proof with { ConfigurationIdentity = "Release|arm64" },
            @"Views\Header.xaml", 2, 128, "TextBlock", null));
        Assert.ThrowsExactly<InvalidDataException>(() => map.Resolve(proof with { SourceHash = new string('B', 64) },
            @"Views\Header.xaml", 2, 128, "TextBlock", null));
    }

    [TestMethod]
    [DataRow("\r\n")]
    [DataRow("\n")]
    public void CompilerTerminalNewline_DoesNotChangeElementCoordinates(string newline)
    {
        var source = Root + "\n" + Heading + "\n</Page>";
        var generated = Root + "\n" + RewrittenHeading + "\n</Page>";
        var fixture = Fixture(source, generated + newline);
        Assert.AreEqual(25, fixture.Create().Resolve(fixture.Proof, "MainPage.xaml", 2, 128, "TextBlock", null).AuthoredColumn);
        // The WinUI compiler appends two line breaks to a source without a final newline.
        var doubled = Fixture(source, generated + newline + newline);
        Assert.AreEqual(25, doubled.Create().Resolve(doubled.Proof, "MainPage.xaml", 2, 128, "TextBlock", null).AuthoredColumn);
        Assert.ThrowsExactly<InvalidDataException>(() => Fixture(source, generated + "\n<!--new content-->").Create());
    }

    [TestMethod]
    [DataRow("Button", "Click=\"OnSave\"", true, true)]
    [DataRow("local:Downloader", "DownloadClicked=\"OnDownload\"", true, true)]
    [DataRow("AutoSuggestBox", "TextChanged=\"_OnText2\"", true, true)]
    [DataRow("Button", "Content=\"Save changes\"", true, false)]
    [DataRow("Button", "Content=\"#FF0000\"", true, false)]
    [DataRow("Button", "x:Uid=\"SaveButton\"", true, false)]
    [DataRow("Button", "Click=\"OnSave\"", false, false)]
    public void EventHandlerBlanking_RequiresAConnectedHandlerShapedAttribute(string element, string erased, bool connected, bool supported)
    {
        var declaration = "<" + element + " xmlns:local=\"using:MyApp\" " + erased + " />";
        var rewritten = "<" + element + (connected ? " x:ConnectionId='2'" : "") + " xmlns:local=\"using:MyApp\" " + new string(' ', erased.Length) + " />";
        var fixture = Fixture(Root + "\n" + declaration + "\n</Page>", Root + "\n" + rewritten + "\n</Page>");
        if (!supported)
        {
            Assert.ThrowsExactly<InvalidDataException>(() => fixture.Create());
            return;
        }
        Assert.AreEqual(declaration, fixture.Create().Resolve(fixture.Proof, "MainPage.xaml", 2, 30, element.Split(':').Last(), null).Declaration);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void DeferredLoadBinding_RequiresTheCompiledFalseLiteral(bool exact)
    {
        const string load = "x:Load=\"{x:Bind HasThink, Mode=OneWay}\"";
        var declaration = "<Border " + load + " />";
        var compiled = (exact ? "x:Load=\"False\"" : "x:Load=\"True\"").PadRight(load.Length);
        var fixture = Fixture(Root + "\n" + declaration + "\n</Page>",
            Root + "\n<Border x:ConnectionId='3' " + compiled + " />\n</Page>");
        if (!exact)
        {
            Assert.ThrowsExactly<InvalidDataException>(() => fixture.Create());
            return;
        }
        Assert.AreEqual(declaration, fixture.Create().Resolve(fixture.Proof, "MainPage.xaml", 2, 20, "Border", null).Declaration);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void TypedTemplateRootConnection_IsOnlyExplainedByATypedTemplate(bool typed)
    {
        const string directive = "x:DataType=\"local:Row\"";
        var template = "<DataTemplate xmlns:local=\"using:MyApp\"" + (typed ? " " + directive : "") + ">";
        var compiledTemplate = typed ? template.Replace(directive, new string(' ', directive.Length), StringComparison.Ordinal) : template;
        var fixture = Fixture(Root + "\n" + template + "\n<Border Padding=\"2\" />\n</DataTemplate>\n</Page>",
            Root + "\n" + compiledTemplate + "\n<Border x:ConnectionId='4' Padding=\"2\" />\n</DataTemplate>\n</Page>");
        if (!typed)
        {
            Assert.ThrowsExactly<InvalidDataException>(() => fixture.Create());
            return;
        }
        Assert.AreEqual("<Border Padding=\"2\" />", fixture.Create().Resolve(fixture.Proof, "MainPage.xaml", 3, 10, "Border", null).Declaration);
    }

    [TestMethod]
    [DataRow("valid")]
    [DataRow("foreign-element")]
    [DataRow("foreign-directive")]
    [DataRow("different-element")]
    [DataRow("unrelated-attribute")]
    [DataRow("changed-literal")]
    [DataRow("missing-binding-connection")]
    public void TypedTemplateDirectiveBlanking_RequiresExactKnownRewrite(string variant)
    {
        const string directive = "x:DataType=\"local:Row\"";
        const string target = "<TextBlock Text=\"{x:Bind Vm.Title}\" Tag=\"retained\" />";
        var template = "<DataTemplate xmlns:local=\"using:MyApp\" " + directive + ">";
        var source = Root + "\n" + template + "\n" + target + "\n</DataTemplate>\n</Page>";
        var generated = Root + "\n" + template.Replace(directive, new string(' ', directive.Length), StringComparison.Ordinal)
            + "\n" + Rewrite(target, 5) + "\n</DataTemplate>\n</Page>";
        if (variant == "foreign-element")
        {
            source = source.Replace("<DataTemplate ", "<DataTemplate xmlns=\"urn:custom\" ", StringComparison.Ordinal);
            generated = generated.Replace("<DataTemplate ", "<DataTemplate xmlns=\"urn:custom\" ", StringComparison.Ordinal);
        }
        if (variant == "foreign-directive")
        {
            source = source.Replace("x:DataType", "y:DataType", StringComparison.Ordinal).Replace("<DataTemplate ",
                "<DataTemplate xmlns:y=\"urn:custom\" ", StringComparison.Ordinal);
            generated = generated.Replace("<DataTemplate ", "<DataTemplate xmlns:y=\"urn:custom\" ", StringComparison.Ordinal);
        }
        if (variant == "different-element")
        {
            source = source.Replace("DataTemplate", "Grid", StringComparison.Ordinal);
            generated = generated.Replace("DataTemplate", "Grid", StringComparison.Ordinal);
        }
        if (variant == "unrelated-attribute")
        {
            source = source.Replace("x:DataType", "x:UnknownX", StringComparison.Ordinal);
        }
        if (variant == "changed-literal")
        {
            generated = generated.Replace("retained", "replaced", StringComparison.Ordinal);
        }
        if (variant == "missing-binding-connection")
        {
            generated = generated.Replace(" x:ConnectionId='5'", "", StringComparison.Ordinal);
        }
        var fixture = Fixture(source, generated);
        if (variant != "valid")
        {
            Assert.ThrowsExactly<InvalidDataException>(() => fixture.Create());
            return;
        }
        var map = fixture.Create();
        Assert.AreEqual(template, map.Resolve(fixture.Proof, "MainPage.xaml", 2, 3, "DataTemplate", null).Declaration);
        Assert.AreEqual(target, map.Resolve(fixture.Proof, "MainPage.xaml", 3, 30, "TextBlock", null).Declaration);
    }

    [TestMethod]
    public void ClassRootConnectionInsertion_PreservesRuntimeClassIdentity()
    {
        var root = Root.Replace("<Page ", "<Page x:Class=\"App.MainPage\" ", StringComparison.Ordinal);
        var fixture = Fixture(root + "\n" + Heading + "\n</Page>",
            root.Replace("<Page ", "<Page x:ConnectionId='1' ", StringComparison.Ordinal) + "\n" + RewrittenHeading + "\n</Page>");
        var map = fixture.Create();
        Assert.AreEqual(root, map.Resolve(fixture.Proof, "MainPage.xaml", 1, 8, "App.MainPage", null).Declaration);
        Assert.AreEqual(Heading.TrimStart(), map.Resolve(fixture.Proof, "MainPage.xaml", 2, 128, "TextBlock", null).Declaration);
        var nested = Fixture(Root + "<Grid x:Class=\"App.Inner\" /></Page>",
            Root + "<Grid x:ConnectionId='2' x:Class=\"App.Inner\" /></Page>");
        Assert.ThrowsExactly<InvalidDataException>(() => nested.Create());
    }

    [TestMethod]
    public void NamedElementConnectionInsertion_PreservesTheNamedDeclaration()
    {
        const string element = "<TextBlock x:Name=\"Heading\" Text=\"Literal\" />";
        var fixture = Fixture(Root + "\n" + element + "\n</Page>",
            Root + "\n" + element.Replace("<TextBlock ", "<TextBlock x:ConnectionId='4' ", StringComparison.Ordinal) + "\n</Page>");
        Assert.AreEqual(element, fixture.Create().Resolve(fixture.Proof, "MainPage.xaml", 2, 35, "TextBlock", "Heading").Declaration);
    }

    [TestMethod]
    public void UnchangedDeclaration_NeedsNoRewriteAssumption()
    {
        var text = Root + "\n<Border Background=\"Blue\" />\n</Page>";
        var fixture = Fixture(text, text);
        var hit = fixture.Create().Resolve(fixture.Proof, "MainPage.xaml", 2, 10, "Border", null);
        Assert.AreEqual("<Border Background=\"Blue\" />", hit.Declaration);
        Assert.AreEqual(hit.Declaration,
            fixture.Create().Resolve(fixture.Proof, "MainPage.xaml", 2, 1000, "Border", null).Declaration,
            "Unique declaration identity does not depend on a generated end-column estimate.");
    }

    [TestMethod]
    public void ArtifactBytesChangingAfterCapture_AreRejected()
    {
        var fixture = Fixture(Root + Heading + "</Page>", Root + RewrittenHeading + "</Page>");
        foreach (var artifact in new[] { fixture.Source, fixture.Generated, fixture.Xbf })
        {
            var original = artifact[0];
            artifact[0] ^= 1;
            Assert.ThrowsExactly<InvalidDataException>(() => fixture.Create());
            artifact[0] = original;
        }
    }

    [TestMethod]
    public void LegacyComment_KeepsRawAnchorAndStatus_AndOnlyReceivesAnAdvisoryCandidate()
    {
        var fixture = Fixture(Root + "\n" + Heading + "\n</Page>", Root + "\n" + RewrittenHeading + "\n</Page>");
        var comment = new Comment
        {
            Id = "historical", Text = "Keep this note", Status = CommentStatus.Open,
            ProjectRoot = @"C:\fixture",
            Anchor = new()
            {
                SourceFile = "MainPage.xaml", Line = 2, Column = 128,
                Identity = new() { Type = "TextBlock", Content = "Today's intentions" },
            },
        };
        var before = JsonSerializer.Serialize(comment, CommentsJsonContext.Default.Comment);
        var hit = fixture.Create().Resolve(fixture.Proof, comment.Anchor.SourceFile,
            comment.Anchor.Line.Value, comment.Anchor.Column.Value, comment.Anchor.Identity.Type!, null);
        Assert.IsTrue(hit.Advisory);
        Assert.AreEqual(before, JsonSerializer.Serialize(comment, CommentsJsonContext.Default.Comment));
        Assert.AreEqual(128, comment.Anchor.Column);
        Assert.AreEqual(25, hit.AuthoredColumn);
    }

    private sealed record Pair(GuestSourceManifest Snapshot, XamlCoordinateMap.BuildProof Proof,
        byte[] Source, byte[] Generated, byte[] Xbf)
    {
        internal XamlCoordinateMap Create() => XamlCoordinateMap.Create(Snapshot, Proof, Source, Generated, Xbf);
    }

    private static Pair Fixture(string source, string generated)
    {
        var sourceBytes = Encoding.UTF8.GetBytes(source);
        var generatedBytes = Encoding.UTF8.GetBytes(generated);
        var xbf = FingerprintedXbf(sourceBytes);
        const string project = @"C:\fixture\Daylight.csproj";
        const string intermediate = @"C:\fixture\obj\x64\Debug\net10.0-windows10.0.26100.0\win-x64";
        static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        var proof = new XamlCoordinateMap.BuildProof(project, "Debug|x64|net10.0-windows10.0.26100.0|win-x64",
            intermediate, "Microsoft.WindowsAppSDK.WinUI/2.3.9", "MainPage.xaml", "MainPage.xaml", null,
            Path.Combine(intermediate, "MainPage.xaml"), Path.Combine(intermediate, "MainPage.xbf"),
            Hash(sourceBytes), Hash(generatedBytes), Hash(xbf), 639256697795755320, 639256697795755320);
        return new(new(project, [new("MainPage.xaml", sourceBytes.Length, proof.SourceHash)]), proof, sourceBytes, generatedBytes, xbf);
    }

    internal static byte[] FingerprintedXbf(byte[] source)
    {
        // Header/empty-table contract fixture, not executable compiled XAML or live-image evidence.
        var xbf = new byte[160];
        "XBF\0"u8.CopyTo(xbf);
        BinaryPrimitives.WriteUInt32LittleEndian(xbf.AsSpan(4), 144);
        BinaryPrimitives.WriteUInt32LittleEndian(xbf.AsSpan(8), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(xbf.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(xbf.AsSpan(16), 1);
        for (var i = 0; i < 6; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(xbf.AsSpan(20 + i * 8), (ulong)(120 + i * 4));
        }
        Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(source))).CopyTo(xbf, XbfSourceFingerprint.FingerprintOffset);
        return xbf;
    }

    [TestMethod]
    public void OriginalFingerprintAndRewriteEquivalence_DoNotProveGeneratedConnectionIdWidth()
    {
        var source = Root + "\n" + Heading + "\n</Page>";
        var generated = Root + "\n" + RewrittenHeading + "\n</Page>";
        var first = Fixture(source, generated);
        var differentWidth = Fixture(source, generated.Replace("ConnectionId='36'", "ConnectionId='136'", StringComparison.Ordinal));
        CollectionAssert.AreEqual(first.Source, differentWidth.Source);
        CollectionAssert.AreEqual(first.Xbf, differentWidth.Xbf,
            "This header fixture fingerprints original bytes only; it is not a compiled node/line-stream proof.");
        Assert.AreNotEqual(first.Proof.GeneratedHash, differentWidth.Proof.GeneratedHash);
        var firstMap = first.Create();
        var differentMap = differentWidth.Create();
        Assert.AreEqual(firstMap.Elements[1], differentMap.Elements[1], "The table records source spans, not generated ConnectionId widths.");
        Assert.AreEqual(firstMap.Resolve(first.Proof, "MainPage.xaml", 2, 144, "TextBlock", null).AuthoredColumn,
            differentMap.Resolve(differentWidth.Proof, "MainPage.xaml", 2, 144, "TextBlock", null).AuthoredColumn);
        Assert.IsTrue(firstMap.Resolve(first.Proof, "MainPage.xaml", 2, 128, "TextBlock", null).Advisory);
        Assert.IsTrue(differentMap.Resolve(differentWidth.Proof, "MainPage.xaml", 2, 129, "TextBlock", null).Advisory);
    }

    [TestMethod]
    [DataRow("<Grid><TextBlock x:Name=\"Heading\" /></Grid>", "TextBlock", "Heading")]
    [DataRow("<Border\n /><TextBlock x:Name=\"Heading\" />", "TextBlock", "Heading")]
    [DataRow("<TextBlock x:Name=\"Heading\" /><TextBlock />", "TextBlock", "Heading")]
    [DataRow("<DataTemplate><TextBlock /></DataTemplate>", "TextBlock", null)]
    [DataRow("<Grid xmlns:p=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation?IsApiContractPresent(Windows.Foundation.UniversalApiContract,8)\"><p:TextBlock /><TextBlock /></Grid>", "TextBlock", null)]
    public void AllOpeningSpans_CountBeforeTypeNameOrTemplateFiltering(string elements, string type, string? name)
    {
        var source = Root + "\n" + elements + "\n</Page>";
        var fixture = Fixture(source, source);
        var line = elements.Contains('\n') ? 3 : 2;
        Assert.ThrowsExactly<InvalidDataException>(() =>
            fixture.Create().Resolve(fixture.Proof, "MainPage.xaml", line, 3, type, name));
    }

    [TestMethod]
    public void TemplateDeclaration_CanBeUniqueWithoutClaimingUniqueRuntimeInstance()
    {
        var source = Root + "\n<Page.Resources>\n<DataTemplate x:Key=\"Item\">\n" + Heading +
            "\n</DataTemplate>\n</Page.Resources>\n</Page>";
        var fixture = Fixture(source, source.Replace(Heading, RewrittenHeading, StringComparison.Ordinal));
        Assert.AreEqual(4, fixture.Create().Resolve(fixture.Proof, "MainPage.xaml", 4, 128, "TextBlock", null).AuthoredLine);
    }

    [TestMethod]
    public void ConnectionInsertion_MustNotIntroduceNewLines()
    {
        var source = Root + "\n" + Heading + "\n</Page>";
        var generated = Root + "\n" + RewrittenHeading.Replace("ConnectionId='36'", "ConnectionId='\n36'", StringComparison.Ordinal) + "\n</Page>";
        Assert.ThrowsExactly<InvalidDataException>(() => Fixture(source, generated).Create());
    }

    [TestMethod]
    public void ChangedBindingWithIdenticalRewriteAndCopiedTimestamp_IsNotBuildProven()
    {
        var fixture = Fixture(Root + Heading + "</Page>", Root + RewrittenHeading + "</Page>");
        var changed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(fixture.Source)
            .Replace("ListHeading", "LostHeading", StringComparison.Ordinal));
        var hash = Convert.ToHexString(SHA256.HashData(changed));
        var currentProof = fixture.Proof with { SourceHash = hash };
        var currentSnapshot = fixture.Snapshot with { Files = [new("MainPage.xaml", changed.Length, hash)] };
        var error = Assert.ThrowsExactly<InvalidDataException>(() => XamlCoordinateMap.Create(
            currentSnapshot, currentProof, changed, fixture.Generated, fixture.Xbf));
        StringAssert.Contains(error.Message, "fingerprint recorded by the compiler");
    }

    private static string Rewrite(string element, int id)
    {
        var property = element.Contains("Text=", StringComparison.Ordinal) ? "Text" : "Content";
        var start = element.IndexOf(property + "=\"", StringComparison.Ordinal);
        var end = element.IndexOf('"', start + property.Length + 2) + 1;
        var characters = element.ToCharArray();
        for (var i = start; i < end; i++)
        {
            if (characters[i] is not ('\r' or '\n')) { characters[i] = ' '; }
        }
        var insertion = element.IndexOfAny([' ', '\n']);
        return new string(characters).Insert(insertion, $" x:ConnectionId='{id}'");
    }
}

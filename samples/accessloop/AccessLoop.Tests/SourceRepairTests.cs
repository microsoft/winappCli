using System.Text;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class SourceRepairTests
{
    private string directory = null!;
    private string project = null!;
    private const string Target = "SaveButton";
    private const string Expected = "Save document";
    private const string Button = """<Button x:Name="SaveControl" AutomationProperties.AutomationId="SaveButton" />""";

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(AppContext.BaseDirectory, "source-repair-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        project = Path.Combine(directory, "Demo.csproj");
        File.WriteAllText(project, """<Project Sdk="Microsoft.NET.Sdk" />""");
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(directory, recursive: true);

    [TestMethod]
    public void LocatorUsesExplicitAutomationIdRatherThanXName()
    {
        Write("MainWindow.xaml", Wrap(
            """<Button x:Name="SaveButton" AutomationProperties.AutomationId="DifferentId" />""" + "\n" + Button));
        var candidates = SourceRepair.Locate(project, Target);
        Assert.HasCount(1, candidates);
        Assert.AreEqual("MainWindow.xaml", candidates[0].RelativePath);
        Assert.AreEqual("Button", candidates[0].ElementName);
        Assert.AreEqual(Target, candidates[0].AutomationId);
        Assert.AreEqual(5, candidates[0].Line);
        Assert.HasCount(64, candidates[0].Sha256);
        StringAssert.Contains(candidates[0].Snippet, "SaveControl");
        Assert.IsTrue(candidates[0].Snippet.Length <= 768);
    }

    [TestMethod]
    public void XNameAloneDoesNotCreateCandidateOrDefect()
    {
        Write("MainWindow.xaml", Wrap("""<Button x:Name="SaveButton" />"""));
        Assert.HasCount(0, SourceRepair.Locate(project, Target));
    }

    [TestMethod]
    public void LocatorIgnoresBuildOutputsAndResourceTemplates()
    {
        foreach (var excluded in new[] { "bin", "obj", ".git", @"Views\bin" })
            Write(Path.Combine(excluded, "Ignored.xaml"), Wrap(Button));
        Write("Templates.xaml", Wrap($"""
            <Grid.Resources>
              <DataTemplate x:Key="Row">{Button}</DataTemplate>
              <Style x:Key="StyledButton" TargetType="Button">
                <Setter Property="Template"><Setter.Value><ControlTemplate>{Button}</ControlTemplate></Setter.Value></Setter>
              </Style>
            </Grid.Resources>
            """));
        Write("MainWindow.xaml", Wrap(Button));
        Assert.HasCount(1, SourceRepair.Locate(project, Target));
    }

    [TestMethod]
    public void AmbiguousButtonsNeverAutoApply()
    {
        var path = Write("MainWindow.xaml", Wrap(Button + "\n" + Button));
        var original = File.ReadAllBytes(path);
        var candidates = SourceRepair.Locate(project, Target);
        Assert.HasCount(2, candidates);
        var error = Assert.ThrowsExactly<InvalidDataException>(
            () => SourceRepair.Apply(candidates[0], Proposal(candidates[0]), Expected));
        StringAssert.Contains(error.Message, "exactly one");
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void NewDuplicateAfterMappingAlsoBlocksRepair()
    {
        Write("MainWindow.xaml", Wrap(Button));
        var candidate = Single();
        Write("AnotherWindow.xaml", Wrap(Button));
        Assert.ThrowsExactly<InvalidDataException>(() => SourceRepair.Apply(candidate, Proposal(candidate), Expected));
    }

    [TestMethod]
    public void SharedIdOnUnsupportedControlBlocksMapping()
    {
        Write("MainWindow.xaml", Wrap(Button + """<TextBlock AutomationProperties.AutomationId="SaveButton" />"""));
        var error = Assert.ThrowsExactly<InvalidDataException>(() => SourceRepair.Locate(project, Target));
        StringAssert.Contains(error.Message, "non-Button");
    }

    [TestMethod]
    public void ApplyAddsOnlyAccessibleNameAndRollbackRestoresOriginalBytes()
    {
        var original = Wrap(Button);
        var path = Write("MainWindow.xaml", original);
        var candidate = Single();
        var repair = SourceRepair.Apply(candidate, Proposal(candidate), Expected);
        var expected = original.Replace(
            """AutomationProperties.AutomationId="SaveButton" />""",
            """AutomationProperties.AutomationId="SaveButton" AutomationProperties.Name="Save document" />""",
            StringComparison.Ordinal);
        Assert.AreEqual(expected, File.ReadAllText(path));
        StringAssert.Contains(repair.Diff, "--- a/MainWindow.xaml\n+++ b/MainWindow.xaml\n@@");
        StringAssert.Contains(repair.Diff, "-" + Button);
        StringAssert.Contains(repair.Diff, """+<Button x:Name="SaveControl" AutomationProperties.AutomationId="SaveButton" AutomationProperties.Name="Save document" />""");
        Assert.IsFalse(repair.Diff.EndsWith(" \n", StringComparison.Ordinal));
        SourceRepair.Rollback(repair);
        Assert.AreEqual(original, File.ReadAllText(path));
    }

    [TestMethod]
    public void DiffRecordsMissingFinalNewlineWithoutAddingOne()
    {
        var path = Write("MainWindow.xaml", Wrap(Button).TrimEnd('\n'));
        var candidate = Single();
        var repair = SourceRepair.Apply(candidate, Proposal(candidate), Expected);
        Assert.IsFalse(File.ReadAllText(path).EndsWith('\n'));
        StringAssert.Contains(repair.Diff, "\\ No newline at end of file\n");
    }

    [TestMethod]
    public void ApplyUpdatesStaticNameWithXmlEscapingAndPreservesOtherAttributes()
    {
        var path = Write("MainWindow.xaml", Wrap(
            """<Button x:Name="SaveControl" Content="unchanged" Click="Save_Click" AutomationProperties.AutomationId="SaveButton" AutomationProperties.Name="Wrong" />"""));
        var candidate = Single();
        const string name = "Save & close \"document\"";
        SourceRepair.Apply(candidate, Proposal(candidate) with { AccessibleName = name }, name);
        var element = XDocument.Load(path).Descendants().Single(e => e.Name.LocalName == "Button");
        Assert.AreEqual(name, (string?)element.Attribute("AutomationProperties.Name"));
        Assert.AreEqual("SaveControl", (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")));
        Assert.AreEqual("unchanged", (string?)element.Attribute("Content"));
        Assert.AreEqual("Save_Click", (string?)element.Attribute("Click"));
    }

    [TestMethod]
    public void ExistingNameSpanPreservesOtherQuotesEntitiesAndMixedNewlines()
    {
        const string oldAttribute = "AutomationProperties.Name \r\n =\t'Old &amp; name'";
        var original = Wrap(
            "<Button Tag='Unrelated > value &amp; &#xE105;'\r" +
            " AutomationProperties.AutomationId='SaveButton'\n " + oldAttribute +
            "\r\n Content=\"&#xE105;\" />");
        var path = Write("MainWindow.xaml", original);
        var candidate = Single();
        SourceRepair.Apply(candidate, Proposal(candidate), Expected);
        Assert.AreEqual(original.Replace(oldAttribute, """AutomationProperties.Name="Save document" """.TrimEnd(),
            StringComparison.Ordinal), File.ReadAllText(path));
    }

    [TestMethod]
    [DataRow("\n")]
    [DataRow("\r\n")]
    [DataRow("\r")]
    public void InsertedNameFollowsLastAttributeWithoutReformattingStartTag(string newline)
    {
        const string lastAttribute = """Tag="A > B &quot;quote&quot;" """;
        var original = Wrap(
            "<Button\n AutomationProperties.AutomationId='SaveButton'\n " +
            lastAttribute + "\n />").Replace("\n", newline, StringComparison.Ordinal);
        var path = Write("MainWindow.xaml", original);
        var candidate = Single();
        SourceRepair.Apply(candidate, Proposal(candidate), Expected);
        var expected = original.Replace(lastAttribute,
            """Tag="A > B &quot;quote&quot;" AutomationProperties.Name="Save document" """,
            StringComparison.Ordinal);
        Assert.AreEqual(expected, File.ReadAllText(path));
    }

    [TestMethod]
    public void LastNamespaceDeclarationAlsoProvidesAnExactInsertionPosition()
    {
        const string declaration = "xmlns:local='using:Demo'";
        var original = Wrap(
            "<Button AutomationProperties.AutomationId='SaveButton'\n " + declaration + " />");
        var path = Write("MainWindow.xaml", original);
        var candidate = Single();
        SourceRepair.Apply(candidate, Proposal(candidate), Expected);
        Assert.AreEqual(original.Replace(declaration,
            declaration + """ AutomationProperties.Name="Save document" """.TrimEnd(),
            StringComparison.Ordinal), File.ReadAllText(path));
    }

    [TestMethod]
    [DataRow("{Binding SaveLabel}")]
    [DataRow("{x:Bind SaveLabel}")]
    [DataRow("{StaticResource SaveLabel}")]
    [DataRow("{ThemeResource SaveLabel}")]
    public void BoundOrResourceNamesAreNotOverwritten(string name)
    {
        var path = Write("MainWindow.xaml", Wrap(
            $"""<Button AutomationProperties.AutomationId="SaveButton" AutomationProperties.Name="{name}" />"""));
        var original = File.ReadAllBytes(path);
        var candidate = Single();
        var error = Assert.ThrowsExactly<InvalidDataException>(
            () => SourceRepair.Apply(candidate, Proposal(candidate), Expected));
        StringAssert.Contains(error.Message, "manual repair");
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
    }

    [TestMethod]
    [DataRow("""<Button x:Uid="LocalizedSave" AutomationProperties.AutomationId="SaveButton" />""")]
    [DataRow("""<Button x:Load="True" AutomationProperties.AutomationId="SaveButton" />""")]
    [DataRow("""<Grid x:DeferLoadStrategy="Lazy"><Button AutomationProperties.AutomationId="SaveButton" /></Grid>""")]
    [DataRow("""<Button AutomationProperties.AutomationId="SaveButton"><AutomationProperties.Name>Old name</AutomationProperties.Name></Button>""")]
    public void UnsupportedNameOrLifetimeCasesAreLeftUntouched(string content)
    {
        var path = Write("MainWindow.xaml", Wrap(content));
        var original = File.ReadAllBytes(path);
        var candidate = Single();
        Assert.ThrowsExactly<InvalidDataException>(() => SourceRepair.Apply(candidate, Proposal(candidate), Expected));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
    }

    [TestMethod]
    [DataRow(@"..\Outside.xaml")]
    [DataRow("..\\Demo\\MainWindow.xaml")]
    [DataRow("C:\\Outside.xaml")]
    [DataRow("MainWindow.xaml:extra")]
    [DataRow("Other.xaml")]
    public void UntrustedProposalPathsCannotSelectAnotherFile(string path)
    {
        Write("MainWindow.xaml", Wrap(Button));
        var candidate = Single();
        Assert.ThrowsExactly<InvalidDataException>(
            () => SourceRepair.Apply(candidate, Proposal(candidate) with { RelativePath = path }, Expected));
    }

    [TestMethod]
    public void ProposalMustMatchIdentityHashAndFrozenExpectedName()
    {
        Write("MainWindow.xaml", Wrap(Button));
        var candidate = Single();
        var proposal = Proposal(candidate);
        foreach (var invalid in new[]
        {
            proposal with { AutomationId = "OtherButton" },
            proposal with { SourceSha256 = new string('0', 64) },
            proposal with { AccessibleName = "Change the test instead" },
            proposal with { AccessibleName = "{Binding Unexpected}" },
            proposal with { Explanation = " " },
            proposal with { Explanation = new string('x', 501) }
        })
            Assert.ThrowsExactly<InvalidDataException>(() => SourceRepair.Apply(candidate, invalid, Expected));
    }

    [TestMethod]
    public void CallerCannotSubstituteCandidateFullPathOrForgeProvenance()
    {
        Write("MainWindow.xaml", Wrap(Button));
        var candidate = Single();
        var forged = candidate with { FullPath = Path.Combine(directory, "Other.xaml") };
        Assert.ThrowsExactly<InvalidDataException>(() => SourceRepair.Apply(forged, Proposal(candidate), Expected));
        var reconstructed = new SourceCandidate(candidate.RelativePath, candidate.FullPath, candidate.Line,
            candidate.ElementName, candidate.AutomationId, candidate.Sha256, candidate.Snippet);
        Assert.ThrowsExactly<InvalidDataException>(() => SourceRepair.Apply(reconstructed, Proposal(candidate), Expected));
    }

    [TestMethod]
    public void StaleSourceIsNeverOverwritten()
    {
        var path = Write("MainWindow.xaml", Wrap(Button));
        var candidate = Single();
        File.AppendAllText(path, "\n<!-- Concurrent edit -->");
        var concurrent = File.ReadAllBytes(path);
        var error = Assert.ThrowsExactly<InvalidDataException>(
            () => SourceRepair.Apply(candidate, Proposal(candidate), Expected));
        StringAssert.Contains(error.Message, "source changed");
        CollectionAssert.AreEqual(concurrent, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void RollbackRefusesConcurrentUserChanges()
    {
        var path = Write("MainWindow.xaml", Wrap(Button));
        var candidate = Single();
        var repair = SourceRepair.Apply(candidate, Proposal(candidate), Expected);
        File.AppendAllText(path, "\n<!-- User edit after repair -->");
        var concurrent = File.ReadAllBytes(path);
        var error = Assert.ThrowsExactly<InvalidDataException>(() => SourceRepair.Rollback(repair));
        StringAssert.Contains(error.Message, "Rollback refused");
        CollectionAssert.AreEqual(concurrent, File.ReadAllBytes(path));
    }

    [TestMethod]
    [DataRow("utf8", false)]
    [DataRow("utf8", true)]
    [DataRow("utf16le", true)]
    [DataRow("utf16be", true)]
    [DataRow("utf16le", false)]
    [DataRow("utf32le", true)]
    [DataRow("utf32be", true)]
    public void EncodingBomAndCrlfArePreserved(string kind, bool bom)
    {
        Encoding encoding = kind switch
        {
            "utf8" => new UTF8Encoding(bom, true),
            "utf16le" => new UnicodeEncoding(false, bom, true),
            "utf16be" => new UnicodeEncoding(true, bom, true),
            "utf32le" => new UTF32Encoding(false, bom, true),
            "utf32be" => new UTF32Encoding(true, bom, true),
            _ => throw new InvalidOperationException()
        };
        var original = Wrap(Button).Replace("\n", "\r\n", StringComparison.Ordinal);
        var path = Path.Combine(directory, "MainWindow.xaml");
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(original)).ToArray();
        File.WriteAllBytes(path, bytes);
        var candidate = Single();
        var repair = SourceRepair.Apply(candidate, Proposal(candidate), Expected);
        var updated = File.ReadAllBytes(path);
        CollectionAssert.AreEqual(encoding.GetPreamble(), updated[..encoding.GetPreamble().Length]);
        var decoded = encoding.GetString(updated.AsSpan(encoding.GetPreamble().Length));
        Assert.IsTrue(decoded.Contains("\r\n", StringComparison.Ordinal));
        Assert.IsFalse(decoded.Replace("\r\n", "", StringComparison.Ordinal).Contains('\n'));
        Assert.AreEqual(original.Replace(
            """AutomationProperties.AutomationId="SaveButton" />""",
            """AutomationProperties.AutomationId="SaveButton" AutomationProperties.Name="Save document" />""",
            StringComparison.Ordinal), decoded);
        SourceRepair.Rollback(repair);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void OversizedSnippetsAreBounded()
    {
        Write("MainWindow.xaml", Wrap(Button.Replace(" />", $" Tag=\"{new string('x', 4000)}\" />", StringComparison.Ordinal)));
        Assert.IsTrue(Single().Snippet.Length <= 768);
    }

    [TestMethod]
    public void LocatorDoesNotFollowDirectoryLinks()
    {
        var outside = Path.Combine(directory, "obj", "external");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "External.xaml"), Wrap(Button));
        var link = Path.Combine(directory, "Linked");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("Creating a test symbolic link is unavailable: " + ex.Message);
        }
        try
        {
            Assert.HasCount(0, SourceRepair.Locate(project, Target));
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
        }
    }

    private SourceCandidate Single() => SourceRepair.Locate(project, Target).Single();

    private static RepairProposal Proposal(SourceCandidate candidate) =>
        new(candidate.RelativePath, candidate.Sha256, candidate.AutomationId, Expected,
            "Give the icon-only Save button a meaningful accessible name.");

    private string Write(string relativePath, string content)
    {
        var path = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static string Wrap(string content) =>
        "<Window xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"\n" +
        "        xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" +
        "<Grid>\n" + content + "\n</Grid>\n</Window>\n";
}

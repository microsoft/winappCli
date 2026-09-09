// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.ApiSearch;

namespace WinApp.Cli.Tests;

/// <summary>
/// A description is prose an agent reads to decide whether an API does what it needs, and
/// documentation markup carries part of that prose in attributes rather than in text.
/// Dropping an element therefore does not leave an obviously broken description — it
/// leaves a fluent sentence that has quietly lost its subject, which reads as an answer.
/// These pin the elements whose meaning lives in an attribute.
/// </summary>
[TestClass]
public class XmlDocParserTests
{
    private DirectoryInfo _tempDir = null!;

    [TestInitialize]
    public void Setup() =>
        _tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "winapp-xmldoc-" + Guid.NewGuid().ToString("N")));

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            _tempDir.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Writes a documentation file holding one member summary.</summary>
    private string WriteDoc(string memberName, string summaryXml)
    {
        string path = Path.Combine(_tempDir.FullName, "Doc.xml");
        File.WriteAllText(path, $"""
            <?xml version="1.0"?>
            <doc>
              <assembly><name>Doc</name></assembly>
              <members>
                <member name="{memberName}">
                  <summary>{summaryXml}</summary>
                </member>
              </members>
            </doc>
            """);
        return path;
    }

    private string ParseSummary(string memberName, string summaryXml) =>
        XmlDocParser.ParseFile(WriteDoc(memberName, summaryXml))[memberName];

    [TestMethod]
    public void Summary_SeeLangword_KeepsTheKeyword()
    {
        // This is the real System.Text.Json.JsonSerializerOptions.Encoder summary. Removing
        // the element leaves "or to use the default encoder" -- still fluent, so nothing
        // signals that the one word answering "what may I pass?" is gone.
        string summary = ParseSummary(
            "P:A.B.Encoder",
            """Gets or sets the encoder to use when escaping strings, or <see langword="null" /> to use the default encoder.""");

        Assert.AreEqual(
            "Gets or sets the encoder to use when escaping strings, or null to use the default encoder.",
            summary);
    }

    [TestMethod]
    public void Summary_SeeCref_UsesTheShortTypeName()
    {
        string summary = ParseSummary(
            "P:A.B.Options",
            """Configures <see cref="T:System.Text.Json.JsonSerializerOptions" /> for this call.""");

        Assert.AreEqual("Configures JsonSerializerOptions for this call.", summary);
    }

    [TestMethod]
    public void Summary_MethodCref_DoesNotEndUpNamingAParameterType()
    {
        // A method reference carries a parameter list, and its own dotted type names sit
        // after the last dot of the method name.
        string summary = ParseSummary(
            "M:A.B.Write",
            """Calls <see cref="M:System.String.Format(System.String,System.Object)" /> first.""");

        Assert.AreEqual("Calls Format first.", summary);
    }

    [TestMethod]
    public void Summary_CrefWithNoNamespace_DropsTheDocIdPrefix()
    {
        string summary = ParseSummary("T:A.B", """See <see cref="T:Widget" />.""");

        Assert.AreEqual("See Widget.", summary);
    }

    [TestMethod]
    public void Summary_SeeWithItsOwnText_PrefersThatText()
    {
        string summary = ParseSummary(
            "T:A.B",
            """Read <see cref="T:System.String">the string docs</see> for details.""");

        Assert.AreEqual("Read the string docs for details.", summary);
    }

    [TestMethod]
    public void Summary_Paramref_NamesTheParameter()
    {
        string summary = ParseSummary(
            "M:A.B.Write(System.String)",
            """Writes <paramref name="value" /> to the stream.""");

        Assert.AreEqual("Writes value to the stream.", summary);
    }

    [TestMethod]
    public void Summary_Paragraphs_DoNotRunTogether()
    {
        // Collapsing whitespace across a block boundary would produce "firstSecond".
        string summary = ParseSummary("T:A.B", "<para>first</para><para>Second</para>");

        Assert.AreEqual("first Second", summary);
    }

    [TestMethod]
    public void Summary_InlineCode_IsKept()
    {
        string summary = ParseSummary("T:A.B", "Pass <c>true</c> to enable.");

        Assert.AreEqual("Pass true to enable.", summary);
    }

    [TestMethod]
    public void Summary_AdjacentReferences_KeepTheSpaceBetweenThem()
    {
        // The reader is configured to ignore whitespace, and the only thing separating
        // two references is a whitespace-only text node.
        string summary = ParseSummary(
            "T:A.B",
            """Compare <see cref="T:A.Alpha" /> <see cref="T:A.Beta" /> carefully.""");

        Assert.AreEqual("Compare Alpha Beta carefully.", summary);
    }

    [TestMethod]
    public void Summary_MemberWithoutSummary_IsSkipped()
    {
        string path = Path.Combine(_tempDir.FullName, "NoSummary.xml");
        File.WriteAllText(path, """
            <?xml version="1.0"?>
            <doc>
              <members>
                <member name="T:A.B"><remarks>only remarks</remarks></member>
                <member name="T:A.C"><summary>real</summary></member>
              </members>
            </doc>
            """);

        Dictionary<string, string> docs = XmlDocParser.ParseFile(path);

        Assert.IsFalse(docs.ContainsKey("T:A.B"), "a member with no summary contributes no description");
        Assert.AreEqual("real", docs["T:A.C"], "a later member must still be read");
    }
}

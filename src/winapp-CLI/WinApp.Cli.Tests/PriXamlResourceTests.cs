// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Xml.Linq;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class PriXamlResourceTests
{
    private static readonly byte[] Xbf = XamlCoordinateMapTests.FingerprintedXbf("<Page/>"u8.ToArray());
    private static readonly Dictionary<string, string> Expected = new() { ["MainPage.xaml"] = Convert.ToHexString(SHA256.HashData(Xbf)) };

    private static XDocument Dump() => XDocument.Parse($"""
        <PriInfo><ResourceMap name="App" primary="true" uniqueName="ms-appx://App/">
          <ResourceMapSubtree name="Files"><NamedResource name="MainPage.xbf" index="2"
            uri="ms-resource://App/Files/MainPage.xbf">
            <Decision index="1"><QualifierSet index="0"/></Decision>
            <Candidate type="EmbeddedData"><QualifierSet index="0"/>
              <Base64Value>{Convert.ToBase64String(Xbf)}</Base64Value>
            </Candidate>
          </NamedResource></ResourceMapSubtree>
        </ResourceMap></PriInfo>
        """);

    [TestMethod]
    public void SingleNeutralResource_ProvesTheExactCompiledBytes() =>
        PriService.VerifyXamlResourcesDump(Dump(), Expected);

    [TestMethod]
    [DataRow("base64")]
    [DataRow("duplicate-name")]
    [DataRow("alternative")]
    [DataRow("qualifier")]
    [DataRow("nonempty-index")]
    [DataRow("path")]
    [DataRow("map")]
    [DataRow("uri")]
    [DataRow("type")]
    [DataRow("bytes")]
    [DataRow("primary")]
    public void AmbiguousOrDifferentResources_FailClosed(string change)
    {
        var dump = Dump();
        var named = dump.Descendants("NamedResource").Single();
        var candidate = named.Element("Candidate")!;
        switch (change)
        {
            case "base64": candidate.Element("Base64Value")!.Value = "not!base64"; break;
            case "duplicate-name": named.AddAfterSelf(new XElement(named)); break;
            case "alternative": candidate.AddAfterSelf(new XElement(candidate)); break;
            case "qualifier": candidate.Element("QualifierSet")!.Add(new XElement("Qualifier", new XAttribute("name", "Language"), "en-US")); break;
            case "nonempty-index": candidate.Element("QualifierSet")!.SetAttributeValue("index", "1"); break;
            case "path": named.Parent!.SetAttributeValue("name", "Other"); break;
            case "map": dump.Descendants("ResourceMap").Single().SetAttributeValue("name", "Other"); break;
            case "uri": named.SetAttributeValue("uri", "ms-resource://App/Files/Wrong.xbf"); break;
            case "type": candidate.SetAttributeValue("type", "Path"); break;
            case "bytes": candidate.Element("Base64Value")!.Value = Convert.ToBase64String("different XBF"u8); break;
            case "primary": dump.Root!.Add(new XElement(dump.Descendants("ResourceMap").Single())); break;
        }
        Assert.ThrowsExactly<InvalidDataException>(() => PriService.VerifyXamlResourcesDump(dump, Expected));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PathResource_VerifiesReferencedBytes_IncludingIdenticalQualifiedAlternatives(bool qualified)
    {
        var root = TestPaths.TempRoot("pri-path");
        Directory.CreateDirectory(Path.Combine(root, "selected"));
        try
        {
            File.WriteAllBytes(Path.Combine(root, "selected", "heading.xbf"), Xbf);
            var dump = Dump();
            var named = dump.Descendants("NamedResource").Single();
            var candidate = named.Element("Candidate")!;
            candidate.SetAttributeValue("type", "Path");
            candidate.Element("Base64Value")!.ReplaceWith(new XElement("Value", @"selected\heading.xbf"));
            if (qualified)
            {
                var alternative = new XElement(candidate);
                alternative.Element("QualifierSet")!.SetAttributeValue("index", "1");
                alternative.Element("QualifierSet")!.Add(new XElement("Qualifier",
                    new XAttribute("name", "Language"), new XAttribute("value", "en-US")));
                named.Add(alternative);
                named.Element("Decision")!.Add(new XElement(alternative.Element("QualifierSet")!));
            }
            var paths = PriService.VerifyXamlResourcesDump(dump, Expected, root);
            Assert.HasCount(1, paths["MainPage.xaml"]);
            Assert.AreEqual("selected/heading.xbf", paths["MainPage.xaml"][0]);
            File.WriteAllBytes(Path.Combine(root, "selected", "heading.xbf"), "other XBF"u8.ToArray());
            Assert.ThrowsExactly<InvalidDataException>(() => PriService.VerifyXamlResourcesDump(dump, Expected, root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow("../MainPage.xbf")]
    [DataRow("/MainPage.xbf")]
    [DataRow("C:/MainPage.xbf")]
    [DataRow("folder/../MainPage.xbf")]
    [DataRow("MainPage.xbf:stream")]
    [DataRow("MainPage.xaml")]
    [DataRow("file://MainPage.xbf")]
    public void PathResource_RejectsUnsafePaths(string path)
    {
        var dump = Dump();
        var candidate = dump.Descendants("Candidate").Single();
        candidate.SetAttributeValue("type", "Path");
        candidate.Element("Base64Value")!.ReplaceWith(new XElement("Value", path));
        Assert.ThrowsExactly<InvalidDataException>(() => PriService.VerifyXamlResourcesDump(dump, Expected, Path.GetTempPath()));
    }

    [TestMethod]
    public void QualifiedAlternativesWithDifferentBytes_NeverGuessActiveContext()
    {
        var dump = Dump();
        var named = dump.Descendants("NamedResource").Single();
        var other = new XElement(named.Element("Candidate")!);
        other.Element("QualifierSet")!.SetAttributeValue("index", "1");
        named.Element("Decision")!.Add(new XElement(other.Element("QualifierSet")!));
        other.Element("Base64Value")!.Value = Convert.ToBase64String("different"u8);
        named.Add(other);
        Assert.ThrowsExactly<InvalidDataException>(() => PriService.VerifyXamlResourcesDump(dump, Expected));
    }
}

// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class PriIdentityTests
{
    private const string OriginalName = "PriFidelityB97C";
    private const string EffectiveName = "PriFidelityB97C.wa";
    private const string OriginalPri = "original PRI bytes";
    private const string ReindexedPri = "reindexed PRI bytes";
    private DirectoryInfo _root = null!;
    private DirectoryInfo _layout = null!;
    private FakeBuildToolsService _tools = null!;
    private TaskContext _context = null!;
    private XDocument _before = null!;
    private XDocument _after = null!;
    private Action<string>? _onNew;
    private string PriPath => Path.Combine(_layout.FullName, "resources.pri");

    [TestInitialize]
    public void Setup()
    {
        _root = Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), $".pri-identity-tests-{Guid.NewGuid():N}"));
        _layout = _root.CreateSubdirectory("layout with spaces");
        File.WriteAllText(PriPath, OriginalPri);
        _before = XDocument.Parse(PriIdentityTestData.Original, LoadOptions.PreserveWhitespace);
        _after = Rename(_before);
        foreach (var candidate in _before.Descendants("Candidate").Where(element => (string?)element.Attribute("type") == "Path"))
        {
            var path = Path.Combine(_layout.FullName, candidate.Element("Value")!.Value);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("payload: " + candidate.Element("Value")!.Value));
        }
        _tools = new FakeBuildToolsService { Handler = (_, args) => EmulateTool(args) };
        _context = new TaskContext(new GroupableTask("test", null), null, new TestConsole(),
            NullLogger<PriIdentityTests>.Instance, new Lock());
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_root.Exists)
        {
            _root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task ReindexIdentity_PreservesEntireGraphAndPayloads_UsesOnlyPriIndexerOutsideLayout()
    {
        var untouchedPath = Path.Combine(_layout.FullName, "FidelityLibrary", "resources.pri");
        File.WriteAllText(untouchedPath, "unchanged library PRI");
        File.WriteAllText(Path.Combine(_layout.FullName, "priconfig.xml"), "user configuration");
        var originals = Directory.GetFiles(_layout.FullName, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        _onNew = args =>
        {
            var input = Argument(args, "/pr");
            var output = Argument(args, "/of");
            var config = XDocument.Load(Argument(args, "/cf"));
            Assert.IsFalse(input.StartsWith(_layout.FullName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(output.StartsWith(_layout.FullName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(OriginalPri, File.ReadAllText(Path.Combine(input, "original.pri")));
            Assert.AreEqual(1, Directory.GetFiles(input).Length);
            Assert.AreEqual(OriginalPri, File.ReadAllText(PriPath), "The destination must remain original until validation completes.");
            Assert.AreEqual(EffectiveName, Argument(args, "/in"));
            var index = config.Root!.Element("index")!;
            Assert.AreEqual("\\", (string?)index.Attribute("root"));
            Assert.AreEqual("original.pri", (string?)index.Attribute("startIndexAt"));
            Assert.AreEqual(1, index.Elements().Count());
            Assert.AreEqual("PRI", (string?)index.Element("indexer-config")!.Attribute("type"));
            Assert.AreEqual(0, config.Descendants("default").Count());
            Assert.AreEqual(0, config.Descendants("packaging").Count());
        };

        await ReindexAsync();

        Assert.AreEqual(ReindexedPri, File.ReadAllText(PriPath));
        foreach (var (path, bytes) in originals.Where(pair => pair.Key != PriPath))
        {
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        }
        CollectionAssert.AreEquivalent(originals.Keys.ToArray(), Directory.GetFiles(_layout.FullName, "*", SearchOption.AllDirectories));
        Assert.AreEqual(3, _tools.Invocations.Count);
        Assert.IsTrue(_tools.Invocations.All(call => call.ToolName == "makepri.exe"));
        StringAssert.StartsWith(_tools.Invocations[0].Arguments, "dump ");
        StringAssert.StartsWith(_tools.Invocations[1].Arguments, "new ");
        StringAssert.StartsWith(_tools.Invocations[2].Arguments, "dump ");
        StringAssert.Contains(_tools.Invocations[0].Arguments, "/dt detailed");
        StringAssert.Contains(_tools.Invocations[2].Arguments, "/dt detailed");
        AssertScratchRemoved();
    }

    [TestMethod]
    public async Task ReindexIdentity_AllowsOnlyDocumentedSerializationDifferences()
    {
        foreach (var attribute in _after.Descendants().Attributes("index"))
        {
            attribute.Value = (uint.Parse(attribute.Value, CultureInfo.InvariantCulture) + 100).ToString(CultureInfo.InvariantCulture);
        }
        _after.Descendants("VersionInfo").Single().SetAttributeValue("checksum", "12345");
        var base64 = _after.Descendants("Base64Value").Single();
        base64.Value = " \r\n" + base64.Value.Insert(40, " \n") + "\r\n";
        foreach (var element in _after.Descendants().Where(element => element.Name != "Decision").Reverse().ToArray())
        {
            var children = element.Elements().Reverse().ToArray();
            if (children.Length > 0)
            {
                element.ReplaceNodes(children);
            }
            var attributes = element.Attributes().Reverse().ToArray();
            element.ReplaceAttributes(attributes);
        }

        await ReindexAsync();

        Assert.AreEqual(ReindexedPri, File.ReadAllText(PriPath));
        AssertScratchRemoved();
    }

    [TestMethod]
    [DataRow("string")]
    [DataRow("string-whitespace")]
    [DataRow("path")]
    [DataRow("embedded")]
    [DataRow("type")]
    [DataRow("priority")]
    [DataRow("default-score")]
    [DataRow("language")]
    [DataRow("scale")]
    [DataRow("contrast")]
    [DataRow("target-size")]
    [DataRow("alternate-form")]
    [DataRow("qualifier-set")]
    [DataRow("decision-order")]
    [DataRow("candidate")]
    [DataRow("library-subtree")]
    [DataRow("environment-checksum")]
    public async Task ReindexIdentity_RejectsSemanticLossEvenWithUnchangedResourceCounts(string change)
    {
        switch (change)
        {
            case "string":
                Candidate(_after, "String").Element("Value")!.Value = "lost localization";
                break;
            case "string-whitespace":
                Candidate(_after, "String").Element("Value")!.Value += " ";
                break;
            case "path":
                var candidate = Candidate(_after, "Path");
                var oldPath = candidate.Element("Value")!.Value;
                File.Copy(Path.Combine(_layout.FullName, oldPath), Path.Combine(_layout.FullName, "different.png"));
                candidate.Element("Value")!.Value = "different.png";
                break;
            case "embedded":
                var embedded = _after.Descendants("Base64Value").Single();
                var bytes = Convert.FromBase64String(embedded.Value);
                bytes[^1] ^= 1;
                embedded.Value = Convert.ToBase64String(bytes);
                break;
            case "type":
                Candidate(_after, "Path").SetAttributeValue("type", "String");
                break;
            case "priority":
                ChangeQualifiers("Language", "priority", "701");
                break;
            case "default-score":
                ChangeQualifiers("Language", "scoreAsDefault", "0.9");
                break;
            case "language":
                ChangeQualifiers("Language", "value", "DE-DE");
                break;
            case "scale":
                ChangeQualifiers("Scale", "value", "300");
                break;
            case "contrast":
                ChangeQualifiers("Contrast", "value", "WHITE");
                break;
            case "target-size":
                ChangeQualifiers("TargetSize", "value", "96");
                break;
            case "alternate-form":
                ChangeQualifiers("AlternateForm", "value", "OTHER");
                break;
            case "qualifier-set":
                foreach (var set in _after.Descendants("QualifierSet").Where(set => (string?)set.Attribute("index") == "5"))
                {
                    set.Elements("Qualifier").First().Remove();
                }
                break;
            case "decision-order":
                foreach (var decision in _after.Descendants("Decision").Where(decision => (string?)decision.Attribute("index") == "2"))
                {
                    decision.ReplaceNodes(decision.Elements().Reverse().ToArray());
                }
                break;
            case "candidate":
                Candidate(_after, "String").Remove();
                break;
            case "library-subtree":
                foreach (var subtree in _after.Descendants("ResourceMapSubtree").Where(subtree => (string?)subtree.Attribute("name") == "FidelityLibrary"))
                {
                    subtree.SetAttributeValue("name", "DifferentLibrary");
                }
                foreach (var uri in _after.Descendants("NamedResource").Attributes("uri"))
                {
                    uri.Value = uri.Value.Replace("/FidelityLibrary/", "/DifferentLibrary/", StringComparison.Ordinal);
                }
                break;
            case "environment-checksum":
                _after.Descendants("WindowsEnvironment").Single().SetAttributeValue("checksum", "42");
                break;
        }

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(ReindexAsync);

        StringAssert.Contains(error.Message, "Cannot prove PRI identity fidelity");
        Assert.AreEqual(3, _tools.Invocations.Count);
        AssertOriginalAndClean();
    }

    [TestMethod]
    [DataRow("map-name")]
    [DataRow("map-uri")]
    [DataRow("resource-authority")]
    [DataRow("multi-root")]
    [DataRow("split")]
    [DataRow("unknown-element")]
    [DataRow("unknown-attribute")]
    [DataRow("missing-header")]
    [DataRow("unknown-version")]
    [DataRow("auto-merge")]
    [DataRow("reverse-map")]
    [DataRow("non-primary")]
    [DataRow("unknown-type")]
    [DataRow("bad-base64")]
    [DataRow("bad-index")]
    [DataRow("bad-checksum")]
    [DataRow("conflicting-reference")]
    [DataRow("scope-count")]
    [DataRow("item-count")]
    [DataRow("duplicate-resource")]
    [DataRow("missing-payload")]
    public async Task ReindexIdentity_RejectsUnsupportedOrCorruptOriginalBeforeReindexing(string corruption)
    {
        var map = _before.Root!.Element("ResourceMap")!;
        switch (corruption)
        {
            case "map-name":
                map.SetAttributeValue("name", "OtherName");
                break;
            case "map-uri":
                map.SetAttributeValue("uniqueName", "ms-appx://OtherName/");
                break;
            case "resource-authority":
                _before.Descendants("NamedResource").First().SetAttributeValue("uri", "ms-resource://OtherName/LibraryMessage");
                break;
            case "multi-root":
                _before.Root.Add(new XElement(map));
                break;
            case "split":
                _before.Root.Add(new XElement("ResourceMap", new XAttribute("name", "SplitMap")));
                break;
            case "unknown-element":
                map.Add(new XElement("ResourceLink", new XAttribute("target", "other.pri")));
                break;
            case "unknown-attribute":
                map.SetAttributeValue("resourcePack", "true");
                break;
            case "missing-header":
                _before.Root.Element("PriHeader")!.Remove();
                break;
            case "unknown-version":
                map.SetAttributeValue("version", "2.0");
                break;
            case "auto-merge":
                _before.Descendants("AutoMerge").Single().Value = "true";
                break;
            case "reverse-map":
                _before.Descendants("ReverseMap").Single().Value = "true";
                break;
            case "non-primary":
                map.SetAttributeValue("primary", "false");
                break;
            case "unknown-type":
                Candidate(_before, "String").SetAttributeValue("type", "Reference");
                break;
            case "bad-base64":
                _before.Descendants("Base64Value").Single().Value = "invalid base64!";
                break;
            case "bad-index":
                _before.Descendants("Qualifier").First().SetAttributeValue("index", "not-a-reference");
                break;
            case "bad-checksum":
                map.Element("VersionInfo")!.SetAttributeValue("checksum", "not-a-checksum");
                break;
            case "conflicting-reference":
                _before.Descendants("Qualifier").First().SetAttributeValue("priority", "900");
                break;
            case "scope-count":
                map.Element("VersionInfo")!.SetAttributeValue("numScopes", "1");
                break;
            case "item-count":
                map.Element("VersionInfo")!.SetAttributeValue("numItems", "1");
                break;
            case "duplicate-resource":
                var resource = _before.Descendants("NamedResource").First();
                resource.Parent!.Add(new XElement(resource));
                map.Element("VersionInfo")!.SetAttributeValue("numItems", "8");
                break;
            case "missing-payload":
                Candidate(_before, "Path").Element("Value")!.Remove();
                break;
        }

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(ReindexAsync);

        Assert.AreEqual(1, _tools.Invocations.Count, "Do not reindex an input whose semantics cannot be proved.");
        AssertOriginalAndClean();
    }

    [TestMethod]
    [DataRow("unchanged-map")]
    [DataRow("wrong-map-uri")]
    [DataRow("wrong-resource-authority")]
    [DataRow("multiple-maps")]
    [DataRow("unknown-format")]
    public async Task ReindexIdentity_RejectsUnprovenResultMap(string result)
    {
        var map = _after.Root!.Element("ResourceMap")!;
        switch (result)
        {
            case "unchanged-map":
                _after = new XDocument(_before);
                break;
            case "wrong-map-uri":
                map.SetAttributeValue("uniqueName", $"ms-appx://{OriginalName}/");
                break;
            case "wrong-resource-authority":
                _after.Descendants("NamedResource").First().SetAttributeValue("uri", $"ms-resource://{OriginalName}/Unexpected");
                break;
            case "multiple-maps":
                _after.Root.Add(new XElement(map));
                break;
            case "unknown-format":
                _after.Root.Name = "PriInfoV2";
                break;
        }

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(ReindexAsync);

        Assert.AreEqual(3, _tools.Invocations.Count);
        AssertOriginalAndClean();
    }

    [TestMethod]
    [DataRow("ms-appx://PriFidelityB97C/Assets/Probe.png")]
    [DataRow("ms-resource://PriFidelityB97C/Resources/AppDisplayName")]
    [DataRow("Prefix MS-APPX://prifidelityb97c/Assets/Probe.png suffix")]
    [DataRow("ms-appx://%50riFidelityB97C/Assets/Probe.png")]
    [DataRow("Prefix ms-resource://prifidelityb97%63/Resources/AppDisplayName suffix")]
    [DataRow("<ms-resource://%50riFidelityB97C>")]
    public async Task ReindexIdentity_RejectsKnownOriginalAuthorityStringWithActionableError(string value)
    {
        Candidate(_before, "String").Element("Value")!.Value = value;

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(ReindexAsync);

        StringAssert.Contains(error.Message, "explicit original package authority");
        StringAssert.Contains(error.Message, "authority-less");
        StringAssert.Contains(error.Message, Candidate(_before, "String").Parent!.Attribute("uri")!.Value);
        Assert.AreEqual(1, _tools.Invocations.Count);
        AssertOriginalAndClean();
    }

    [TestMethod]
    [DataRow("ms-appx:///Assets/Probe.png")]
    [DataRow("ms-resource:///Resources/AppDisplayName")]
    [DataRow("Prefix <ms-appx:///Assets/Probe.png> and ms-resource:///Resources/AppDisplayName")]
    [DataRow("ms-appx://PriFidelityB97C.other/Assets/Probe.png")]
    [DataRow("  Exact whitespace\r\n\tmust survive.  ")]
    public async Task ReindexIdentity_PreservesAuthorityFreeAndUnrelatedStringsExactly(string value)
    {
        Candidate(_before, "String").Element("Value")!.Value = value;
        _after = Rename(_before);

        await ReindexAsync();

        Assert.AreEqual(ReindexedPri, File.ReadAllText(PriPath));
    }

    [TestMethod]
    [DataRow(@"..\outside.png")]
    [DataRow(@"\rooted.png")]
    [DataRow(@"C:\outside.png")]
    [DataRow(@"C:outside.png")]
    [DataRow(@"\\server\share\payload.png")]
    [DataRow(@"Assets\..\..\outside.png")]
    [DataRow(@"Assets\Probe.png:stream")]
    [DataRow(@"Assets\trailing. \payload.png")]
    [DataRow("original.pri")]
    [DataRow("resources.pri")]
    [DataRow("missing.png")]
    public async Task ReindexIdentity_RejectsUnsafeMissingOrTemporaryCandidatePaths(string path)
    {
        Candidate(_before, "Path").Element("Value")!.Value = path;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(ReindexAsync);

        Assert.AreEqual(1, _tools.Invocations.Count);
        AssertOriginalAndClean();
    }

    [TestMethod]
    public async Task ReindexIdentity_RejectsCandidateThroughDirectoryLink()
    {
        var outside = _root.CreateSubdirectory("outside");
        File.WriteAllText(Path.Combine(outside.FullName, "payload.png"), "outside payload");
        var link = Path.Combine(_layout.FullName, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, outside.FullName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Inconclusive($"Creating the test directory link requires Developer Mode or elevation: {ex.Message}");
        }
        Candidate(_before, "Path").Element("Value")!.Value = @"linked\payload.png";
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(ReindexAsync);
            AssertOriginalAndClean();
            Assert.AreEqual("outside payload", File.ReadAllText(Path.Combine(outside.FullName, "payload.png")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [TestMethod]
    public async Task ReindexIdentity_RejectsChangedPayloadBytes()
    {
        var payload = Path.Combine(_layout.FullName, Candidate(_before, "Path").Element("Value")!.Value);
        _onNew = _ => File.AppendAllText(payload, "changed bytes");

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(ReindexAsync);

        StringAssert.Contains(error.Message, "file payload changed");
        AssertOriginalAndClean();
    }

    [TestMethod]
    public async Task ReindexIdentity_DoesNotOverwriteConcurrentPriChange()
    {
        _onNew = _ => File.WriteAllText(PriPath, "concurrent PRI");

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(ReindexAsync);

        StringAssert.Contains(error.Message, "changed during identity reindexing");
        Assert.AreEqual("concurrent PRI", File.ReadAllText(PriPath));
        AssertScratchRemoved();
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task ReindexIdentity_ToolFailureAtAnyStageLeavesOriginalUntouched(int failedCall)
    {
        var call = 0;
        _tools.Handler = (_, args) => call++ == failedCall
            ? throw new InvalidOperationException("MakePri rejected corrupt input")
            : EmulateTool(args);

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(ReindexAsync);

        StringAssert.Contains(error.Message, "MakePri rejected corrupt input");
        AssertOriginalAndClean();
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("<PriInfo>")]
    [DataRow("<UnknownFormat/>")]
    [DataRow("<!DOCTYPE PriInfo [<!ENTITY x SYSTEM 'file:///not-read'>]><PriInfo>&x;</PriInfo>")]
    public async Task ReindexIdentity_MalformedDumpHasNoSuccessFallback(string dump)
    {
        _tools.Handler = (_, args) =>
        {
            File.WriteAllText(Argument(args, "/of"), dump);
            return ("", "");
        };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(ReindexAsync);

        Assert.AreEqual(1, _tools.Invocations.Count);
        AssertOriginalAndClean();
    }

    [TestMethod]
    public async Task ReindexIdentity_MissingDumpHasNoSuccessFallback()
    {
        _tools.Handler = (_, _) => ("", "");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(ReindexAsync);

        Assert.AreEqual(1, _tools.Invocations.Count);
        AssertOriginalAndClean();
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("empty")]
    [DataRow("extra-pri")]
    [DataRow("split-directory")]
    public async Task ReindexIdentity_RejectsMissingEmptyOrSplitToolOutput(string output)
    {
        _tools.Handler = (_, args) =>
        {
            if (!args.StartsWith("new ", StringComparison.Ordinal))
            {
                return EmulateTool(args);
            }
            var path = Argument(args, "/of");
            if (output != "missing")
            {
                File.WriteAllText(path, output == "empty" ? "" : ReindexedPri);
            }
            if (output == "extra-pri")
            {
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "split.pri"), "split");
            }
            if (output == "split-directory")
            {
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(path)!, "fr-FR"));
            }
            return ("", "");
        };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(ReindexAsync);

        Assert.AreEqual(2, _tools.Invocations.Count);
        AssertOriginalAndClean();
    }

    [TestMethod]
    public async Task ReindexIdentity_RequiresExistingPri()
    {
        File.Delete(PriPath);

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(ReindexAsync);

        Assert.AreEqual(0, _tools.Invocations.Count);
        Assert.IsFalse(File.Exists(PriPath));
        AssertScratchRemoved();
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task ReindexIdentity_CancellationBeforeCommitLeavesOriginalUntouched(int cancelAfterCall)
    {
        using var cancellation = new CancellationTokenSource();
        if (cancelAfterCall == 0)
        {
            cancellation.Cancel();
        }
        _tools.Handler = (_, args) =>
        {
            var result = EmulateTool(args);
            if (_tools.Invocations.Count == cancelAfterCall)
            {
                cancellation.Cancel();
            }
            return result;
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new PriService(_tools).ReindexIdentityAsync(_layout, OriginalName, EffectiveName, _context, cancellation.Token));

        Assert.AreEqual(cancelAfterCall, _tools.Invocations.Count);
        AssertOriginalAndClean();
    }

    [TestMethod]
    public async Task FakePriService_CapturesIdentityAndPropagatesFailure()
    {
        var failure = new InvalidOperationException("unsupported resource graph");
        var fake = new FakePriService { ReindexIdentityException = failure };

        var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            fake.ReindexIdentityAsync(_layout, OriginalName, EffectiveName, _context));

        Assert.AreSame(failure, actual);
        Assert.AreEqual((_layout, OriginalName, EffectiveName), fake.ReindexIdentityCalls.Single());
    }

    private Task ReindexAsync() => new PriService(_tools).ReindexIdentityAsync(_layout, OriginalName, EffectiveName, _context);

    private void ChangeQualifiers(string name, string attribute, string value)
    {
        foreach (var qualifier in _after.Descendants("Qualifier").Where(element => (string?)element.Attribute("name") == name))
        {
            qualifier.SetAttributeValue(attribute, value);
        }
    }

    private static XElement Candidate(XDocument document, string type) =>
        document.Descendants("Candidate").First(element => (string?)element.Attribute("type") == type);

    private static XDocument Rename(XDocument original)
    {
        var renamed = new XDocument(original);
        var map = renamed.Root!.Element("ResourceMap")!;
        map.SetAttributeValue("name", EffectiveName);
        map.SetAttributeValue("uniqueName", $"ms-appx://{EffectiveName}/");
        foreach (var resource in renamed.Descendants("NamedResource"))
        {
            var uri = resource.Attribute("uri")!;
            uri.Value = uri.Value.Replace($"ms-resource://{OriginalName}/", $"ms-resource://{EffectiveName}/", StringComparison.Ordinal);
        }
        return renamed;
    }

    private (string stdout, string stderr) EmulateTool(string args)
    {
        var output = Argument(args, "/of");
        if (args.StartsWith("new ", StringComparison.Ordinal))
        {
            _onNew?.Invoke(args);
            File.WriteAllText(output, ReindexedPri);
        }
        else if (args.StartsWith("dump ", StringComparison.Ordinal))
        {
            var input = Argument(args, "/if");
            var document = Path.GetFileName(input) == "original.pri" ? _before : _after;
            document.Save(output, SaveOptions.DisableFormatting);
        }
        else
        {
            Assert.Fail("Identity reindexing must not reconstruct a default/image-only PRI.");
        }
        return ("", "");
    }

    private static string Argument(string args, string option)
    {
        var match = Regex.Match(args, Regex.Escape(option) + "\\s+\"([^\"]+)\"");
        Assert.IsTrue(match.Success, $"Missing {option} in {args}");
        var value = match.Groups[1].Value;
        return value.StartsWith(@"\\?\", StringComparison.Ordinal) ? value[4..] : value;
    }

    private void AssertOriginalAndClean()
    {
        Assert.AreEqual(OriginalPri, File.ReadAllText(PriPath));
        AssertScratchRemoved();
    }

    private void AssertScratchRemoved() =>
        Assert.AreEqual(0, _root.GetDirectories(".winapp-pri-identity-*").Length);
}

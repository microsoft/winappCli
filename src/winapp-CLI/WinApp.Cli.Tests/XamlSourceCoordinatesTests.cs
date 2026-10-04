// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services.DevTools;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class XamlSourceCoordinatesTests
{
    public TestContext TestContext { get; set; } = null!;
    private string _root = null!;
    private string _intermediate = null!;
    private FileInfo _project = null!;
    private const string Original = "<Page xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n<TextBlock Text=\"{x:Bind Vm.Title}\" FontSize=\"24\" />\n</Page>";
    private static readonly string Rewritten = Original.Replace(
        "<TextBlock Text=\"{x:Bind Vm.Title}\"", "<TextBlock x:ConnectionId='36'" + new string(' ', 25), StringComparison.Ordinal);

    [TestInitialize]
    public void Setup()
    {
        _root = TestPaths.TempRoot("source-coordinates");
        _intermediate = Path.Combine(_root, "obj", "selected");
        Directory.CreateDirectory(_intermediate);
        _project = new(Path.Combine(_root, "App.csproj"));
        File.WriteAllText(_project.FullName, "<Project/>");
        File.WriteAllText(Path.Combine(_root, "Main.xaml"), Original, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_intermediate, "Main.xaml"), Rewritten, new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(_intermediate, "Main.xbf"),
            XamlCoordinateMapTests.FingerprintedXbf(Encoding.UTF8.GetBytes(Original)));
        WriteMetadata();
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    private void WriteMetadata(string? project = null, string? resourceMap = null, bool includeXbf = true, bool fingerprint = true)
    {
        File.WriteAllText(Path.Combine(_intermediate, "input.json"), JsonSerializer.Serialize(new
        {
            ProjectPath = project ?? _project.FullName,
            OutputPath = _intermediate,
            IsPass1 = false, XAMLFingerprint = fingerprint,
            XamlPages = new[] { new
            {
                ItemSpec = "Main.xaml", FullPath = Path.Combine(_root, "Main.xaml"),
                XamlResourceMapName = resourceMap ?? "",
            } },
            XamlApplications = Array.Empty<object>(),
        }));
        File.WriteAllText(Path.Combine(_intermediate, "output.json"), JsonSerializer.Serialize(new
        {
            GeneratedXamlFiles = new[] { Path.Combine(_intermediate, "Main.xaml") },
            GeneratedXbfFiles = includeXbf ? new[] { Path.Combine(_intermediate, "Main.xbf") } : [],
            GeneratedCodeFiles = Array.Empty<string>(),
        }));
        new XDocument(new XElement("State", new XElement("XamlSourceFileData",
            new XAttribute("XamlFileName", "Main.xaml"),
            new XAttribute("GeneratedCodePathPrefix", Path.Combine(_intermediate, "Main")),
            new XAttribute("XamlFileTimeAtLastCompileInTicks", File.GetLastWriteTime(Path.Combine(_root, "Main.xaml")).Ticks))))
            .Save(Path.Combine(_intermediate, "state.xml"));
    }

    private async Task<GuestSourceManifest> SnapshotAsync() =>
        await GuestSourceSnapshot.CreateAsync(_project, ["Main.xaml"], new(Path.Combine(_root, "snapshot")),
            TestContext.CancellationToken);

    private IReadOnlyList<XamlSourceCoordinateFile> Capture(GuestSourceManifest snapshot) =>
        XamlSourceCoordinates.Capture(_project, snapshot, Path.Combine(_intermediate, "input.json"),
            Path.Combine(_intermediate, "output.json"), Path.Combine(_intermediate, "state.xml")).Files;

    [TestMethod]
    public async Task NoCompilerMetadata_ProducesLikelyOnly_WithAllOpeningSpansAndParentContext()
    {
        File.WriteAllText(Path.Combine(_root, "Main.xaml"),
            "<Page\n xmlns:p=\"urn:conditional\"><Grid><TextBlock/></Grid>\n<DataTemplate>\n<TextBlock/>\n</DataTemplate></Page>");
        var snapshot = await SnapshotAsync();
        var mapping = snapshot.Coordinates!.Single();
        Assert.AreEqual("likely", mapping.Attribution);
        Assert.AreEqual("", mapping.XbfHash);
        Assert.AreEqual("", mapping.GeneratedHash);
        Assert.HasCount(5, mapping.Elements);
        Assert.HasCount(3, mapping.Elements.Where(element => element.Line <= 2 && element.EndLine >= 2).ToArray(),
            "A multiline parent, Grid and TextBlock all count before type filtering.");
        var templateChild = mapping.Elements[^1];
        Assert.AreEqual(3, templateChild.ParentLine);
        Assert.AreEqual("DataTemplate", templateChild.ParentType);
        StringAssert.Contains(mapping.Evidence!, "unverified");
    }

    [TestMethod]
    public async Task VisualStudioSavedState_ValidatesActualRewriteAndFingerprint_WithoutCompilerJson()
    {
        File.Delete(Path.Combine(_intermediate, "input.json"));
        File.Delete(Path.Combine(_intermediate, "output.json"));
        var compiler = XamlSourceCoordinates.FromProperties(_project, new Dictionary<string, string>
        {
            ["XamlSavedStateFilePath"] = Path.Combine(_intermediate, "state.xml"),
        });
        Assert.IsNotNull(compiler);
        var map = XamlSourceCoordinates.Capture(_project, await SnapshotAsync(), compiler.Input, compiler.Output, compiler.SavedState).Files.Single();
        Assert.AreEqual("", map.CompilerInputHash);
        Assert.AreEqual("", map.CompilerOutputHash);
        Assert.AreEqual(64, map.GeneratedHash.Length);
        Assert.AreEqual(2, map.Elements[1].Line);
        Assert.AreEqual(1, map.Elements[1].Column);
    }

    [TestMethod]
    [DataRow("prefix")]
    [DataRow("duplicate")]
    [DataRow("source")]
    [DataRow("xbf")]
    [DataRow("rewrite")]
    public async Task VisualStudioSavedState_RejectsUnprovenAssociations(string change)
    {
        var statePath = Path.Combine(_intermediate, "state.xml");
        var state = XDocument.Load(statePath);
        var entry = state.Descendants("XamlSourceFileData").Single();
        if (change == "prefix") { entry.SetAttributeValue("GeneratedCodePathPrefix", Path.Combine(_root, "elsewhere", "Main")); }
        if (change == "duplicate") { entry.AddAfterSelf(new XElement(entry)); }
        if (change == "source") { entry.SetAttributeValue("XamlFileName", "Other.xaml"); }
        state.Save(statePath);
        if (change == "xbf") { File.WriteAllBytes(Path.Combine(_intermediate, "Main.xbf"), XamlCoordinateMapTests.FingerprintedXbf("<Other/>"u8.ToArray())); }
        if (change == "rewrite") { File.WriteAllText(Path.Combine(_intermediate, "Main.xaml"), Rewritten.Replace("24", "25", StringComparison.Ordinal)); }
        var snapshot = await SnapshotAsync();
        var capture = XamlSourceCoordinates.Capture(_project, snapshot, "", "", statePath);
        Assert.IsEmpty(capture.Files);
        Assert.AreEqual("Main.xaml", capture.Exclusions.Single().Source);
        Assert.IsFalse(string.IsNullOrWhiteSpace(capture.Exclusions.Single().Reason));
    }

    [TestMethod]
    public async Task ExactSelectedTuple_ProducesBoundedMap_WithoutGeneratedCodeOrDirectorySearch()
    {
        var sources = await SnapshotAsync();
        var unselected = Path.Combine(_root, "obj", "newer");
        Directory.CreateDirectory(unselected);
        File.WriteAllText(Path.Combine(unselected, "Main.xaml"), "not this build");
        var map = Capture(sources).Single();
        Assert.AreEqual("Main.xaml", map.Source);
        Assert.AreEqual("Main.xaml", map.Resource);
        Assert.AreEqual(sources.Files.Single().Sha256, map.SourceHash);
        Assert.AreEqual(64, map.XbfHash.Length);
        Assert.HasCount(2, map.Elements);
        var heading = map.Elements[1];
        Assert.AreEqual("TextBlock", heading.Type);
        Assert.AreEqual(2, heading.Line);
        Assert.AreEqual(1, heading.Column);
        Assert.AreEqual(2, heading.EndLine);
        var inventory = Encoding.UTF8.GetString(GuestSourceSnapshot.SerializeInventory(sources with { Coordinates = [map] }));
        StringAssert.Contains(inventory, "\"elements\":[{\"line\":1,\"endLine\":1,\"column\":1,\"type\":\"Page\"},{\"line\":2,\"endLine\":2,\"column\":1,\"type\":\"TextBlock\"}]",
            "The native reader consumes this compact element shape; absent names and parents are omitted.");
    }

    [TestMethod]
    [DataRow("project")]
    [DataRow("resource")]
    [DataRow("missing-xbf")]
    [DataRow("no-fingerprint")]
    [DataRow("copied-time-binding-edit")]
    [DataRow("snapshot-edit")]
    [DataRow("generated-edit")]
    [DataRow("wrong-saved-source")]
    public async Task UnverifiedSelectedTuple_IsRejected(string change)
    {
        if (change == "project") { WriteMetadata(project: Path.Combine(_root, "Other.csproj")); }
        if (change == "resource") { WriteMetadata(resourceMap: "other"); }
        if (change == "missing-xbf") { WriteMetadata(includeXbf: false); }
        if (change == "no-fingerprint") { WriteMetadata(fingerprint: false); }
        if (change == "copied-time-binding-edit")
        {
            var file = Path.Combine(_root, "Main.xaml");
            var time = File.GetLastWriteTimeUtc(file);
            File.WriteAllText(file, Original.Replace("Vm.Title", "Vm.Other", StringComparison.Ordinal), new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(file, time);
        }
        var snapshot = await SnapshotAsync();
        if (change == "snapshot-edit") { snapshot = snapshot with { Files = [snapshot.Files[0] with { Sha256 = new string('A', 64) }] }; }
        if (change == "generated-edit")
        {
            File.WriteAllText(Path.Combine(_intermediate, "Main.xaml"),
                Rewritten.Replace("FontSize=\"24\"", "FontSize=\"25\"", StringComparison.Ordinal));
        }
        if (change == "wrong-saved-source")
        {
            var path = Path.Combine(_intermediate, "state.xml");
            var xml = XDocument.Load(path);
            xml.Descendants("XamlSourceFileData").Single().SetAttributeValue("XamlFileName", "Other.xaml");
            xml.Save(path);
        }
        if (change is "project" or "no-fingerprint")
        {
            Assert.ThrowsExactly<InvalidDataException>(() => Capture(snapshot));
        }
        else
        {
            var capture = XamlSourceCoordinates.Capture(_project, snapshot, Path.Combine(_intermediate, "input.json"),
                Path.Combine(_intermediate, "output.json"), Path.Combine(_intermediate, "state.xml"));
            Assert.IsEmpty(capture.Files);
            Assert.AreEqual("Main.xaml", capture.Exclusions.Single().Source);
            Assert.IsFalse(string.IsNullOrWhiteSpace(capture.Exclusions.Single().Reason));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnprovableDictionary_IsExcludedWithoutDisablingVerifiedPage(bool savedStateOnly)
    {
        const string dictionary = "<ResourceDictionary xmlns=\"urn:test\" />";
        File.WriteAllText(Path.Combine(_root, "Theme.xaml"), dictionary);
        File.WriteAllText(Path.Combine(_intermediate, "Theme.xaml"), dictionary + "\n");
        File.WriteAllBytes(Path.Combine(_intermediate, "Theme.xbf"),
            XamlCoordinateMapTests.FingerprintedXbf(Encoding.UTF8.GetBytes(dictionary)));
        var inputPath = Path.Combine(_intermediate, "input.json");
        var input = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(inputPath))!;
        input["XamlPages"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
        {
            ["ItemSpec"] = "Theme.xaml", ["FullPath"] = Path.Combine(_root, "Theme.xaml"),
        });
        File.WriteAllText(inputPath, input.ToJsonString());
        var outputPath = Path.Combine(_intermediate, "output.json");
        var output = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(outputPath))!;
        output["GeneratedXamlFiles"]!.AsArray().Add(Path.Combine(_intermediate, "Theme.xaml"));
        output["GeneratedXbfFiles"]!.AsArray().Add(Path.Combine(_intermediate, "Theme.xbf"));
        File.WriteAllText(outputPath, output.ToJsonString());
        var statePath = Path.Combine(_intermediate, "state.xml");
        var state = XDocument.Load(statePath);
        state.Root!.Add(new XElement("XamlSourceFileData", new XAttribute("XamlFileName", "Theme.xaml"),
            new XAttribute("GeneratedCodePathPrefix", "")));
        state.Save(statePath);
        var manifest = await GuestSourceSnapshot.CreateAsync(_project, ["Main.xaml", "Theme.xaml"],
            new(Path.Combine(_root, "snapshot")), TestContext.CancellationToken,
            new(savedStateOnly ? "" : inputPath, savedStateOnly ? "" : outputPath, statePath));
        Assert.IsNull(manifest.CoordinateError);
        Assert.AreEqual("Main.xaml", manifest.Coordinates!.Single().Source);
        var excluded = manifest.CoordinateExclusions!.Single();
        Assert.AreEqual("Theme.xaml", excluded.Source);
        Assert.AreEqual("Theme.xaml", excluded.Resource);
        StringAssert.Contains(excluded.Reason, "saved state");
        using var inventory = JsonDocument.Parse(GuestSourceSnapshot.SerializeInventory(manifest));
        Assert.AreEqual("Theme.xaml", inventory.RootElement.GetProperty("coordinateExclusions")[0].GetProperty("source").GetString());
        var run = new Commands.RunCommandResult { SourceWarnings = manifest.CoordinateExclusions };
        using var launch = JsonDocument.Parse(JsonSerializer.Serialize(run, Commands.RunCommandJsonContext.Default.RunCommandResult));
        Assert.AreEqual(excluded.Reason, launch.RootElement.GetProperty("sourceWarnings")[0].GetProperty("reason").GetString());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ClasslessDictionary_WithCompiledTimestamp_IsAdmitted(bool savedStateOnly)
    {
        const string dictionary = "<ResourceDictionary xmlns=\"urn:test\" />";
        var themePath = Path.Combine(_root, "Theme.xaml");
        File.WriteAllText(themePath, dictionary);
        // The compiler re-emits a classless dictionary with a BOM and appended line breaks.
        File.WriteAllText(Path.Combine(_intermediate, "Theme.xaml"), dictionary + "\r\n\r\n", new UTF8Encoding(true));
        File.WriteAllBytes(Path.Combine(_intermediate, "Theme.xbf"),
            XamlCoordinateMapTests.FingerprintedXbf(Encoding.UTF8.GetBytes(dictionary)));
        var inputPath = Path.Combine(_intermediate, "input.json");
        var input = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(inputPath))!;
        input["XamlPages"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
        {
            ["ItemSpec"] = "Theme.xaml", ["FullPath"] = themePath,
        });
        File.WriteAllText(inputPath, input.ToJsonString());
        var outputPath = Path.Combine(_intermediate, "output.json");
        var output = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(outputPath))!;
        output["GeneratedXamlFiles"]!.AsArray().Add(Path.Combine(_intermediate, "Theme.xaml"));
        output["GeneratedXbfFiles"]!.AsArray().Add(Path.Combine(_intermediate, "Theme.xbf"));
        File.WriteAllText(outputPath, output.ToJsonString());
        var statePath = Path.Combine(_intermediate, "state.xml");
        var state = XDocument.Load(statePath);
        state.Root!.Add(new XElement("XamlSourceFileData", new XAttribute("XamlFileName", "Theme.xaml"),
            new XAttribute("ClassFullName", ""), new XAttribute("GeneratedCodePathPrefix", ""),
            new XAttribute("XamlFileTimeAtLastCompileInTicks", File.GetLastWriteTime(themePath).Ticks)));
        state.Save(statePath);
        var manifest = await GuestSourceSnapshot.CreateAsync(_project, ["Main.xaml", "Theme.xaml"],
            new(Path.Combine(_root, "snapshot")), TestContext.CancellationToken,
            new(savedStateOnly ? "" : inputPath, savedStateOnly ? "" : outputPath, statePath));
        Assert.IsNull(manifest.CoordinateError);
        Assert.IsEmpty(manifest.CoordinateExclusions ?? []);
        Assert.HasCount(2, manifest.Coordinates!);
        Assert.IsTrue(manifest.Coordinates!.Any(file => file.Source == "Main.xaml") && manifest.Coordinates!.Any(file => file.Source == "Theme.xaml"));
    }

    [TestMethod]
    public async Task UnrewrittenSource_DoesNotRequireCoordinateMappingOrAnXbfFingerprint()
    {
        File.WriteAllText(Path.Combine(_intermediate, "Main.xaml"), Original, new UTF8Encoding(false));
        WriteMetadata(includeXbf: false);
        File.Delete(Path.Combine(_intermediate, "Main.xbf"));
        Assert.IsEmpty(Capture(await SnapshotAsync()));
    }

    [TestMethod]
    public async Task OwnedLaunch_TransportsHashedInventoryAndHoldsTheSelectedPayload()
    {
        var compiler = new XamlCompilerArtifacts(Path.Combine(_intermediate, "input.json"),
            Path.Combine(_intermediate, "output.json"), Path.Combine(_intermediate, "state.xml"));
        string inventoryPath;
        using (var launch = await XamlSourceCoordinates.XamlCoordinateLaunch.CreateAsync(
            _project, ["Main.xaml"], compiler, TestContext.CancellationToken))
        {
            Assert.IsNotNull(launch);
            var environment = launch.Apply(new Dictionary<string, string?>(), _intermediate);
            inventoryPath = environment["WINAPP_DEVTOOLS_SOURCE_INVENTORY"]!;
            Assert.AreEqual(launch.Hash, environment["WINAPP_DEVTOOLS_SOURCE_INVENTORY_HASH"]);
            Assert.AreEqual(launch.Hash, environment["WINAPP_DEVTOOLS_SOURCE_PAYLOAD_HELD"]);
            Assert.IsNull(launch.Error);
            Assert.ThrowsExactly<IOException>(() => File.WriteAllText(inventoryPath, "replaced"));
            Assert.ThrowsExactly<IOException>(() => new FileStream(inventoryPath, FileMode.Open, FileAccess.Read, FileShare.Read).Dispose(),
                "Only a delete-on-close handle refuses openers without delete sharing; that is what removes the file if the CLI is killed.");
            Assert.AreEqual(Path.GetTempPath().TrimEnd('\\'), Path.GetDirectoryName(inventoryPath));
            Assert.ThrowsExactly<IOException>(() => File.WriteAllText(Path.Combine(_intermediate, "Main.xbf"), "replaced"));
        }
        Assert.IsFalse(File.Exists(inventoryPath));
        File.WriteAllText(Path.Combine(_intermediate, "Main.xbf"), "released-after-launch-handshake");
    }

    [TestMethod]
    public async Task GuestInventory_CarriesTheSameTable_AndMissingPayloadNeverClaimsAuthority()
    {
        var manifest = await SnapshotAsync();
        manifest = manifest with { Coordinates = Capture(manifest) };
        var bytes = GuestSourceSnapshot.SerializeInventory(manifest);
        var path = Path.Combine(_root, "inventory.json");
        File.WriteAllBytes(path, bytes);
        var emptyPayload = Directory.CreateDirectory(Path.Combine(_root, "empty"));
        using var lease = XamlSourceCoordinates.HoldPayload(path, emptyPayload.FullName);
        Assert.IsFalse(lease.Verified);
        StringAssert.Contains(lease.Error!, "deployed XAML payload cannot be verified");
        var environment = XamlSourceCoordinates.XamlCoordinateLaunch.Apply(
            new Dictionary<string, string?>(), path, new string('A', 64), lease.Verified);
        Assert.IsNull(environment["WINAPP_DEVTOOLS_SOURCE_PAYLOAD_HELD"]);
        using var document = JsonDocument.Parse(bytes);
        Assert.AreEqual("Main.xaml", document.RootElement.GetProperty("coordinates")[0].GetProperty("resource").GetString());
    }

    [TestMethod]
    public async Task LooseXbfBesideAnIndexedPayload_DoesNotProveTheSelectedResource()
    {
        var manifest = await SnapshotAsync();
        manifest = manifest with { Coordinates = Capture(manifest) };
        var path = Path.Combine(_root, "inventory.json");
        File.WriteAllBytes(path, GuestSourceSnapshot.SerializeInventory(manifest));
        File.WriteAllText(Path.Combine(_intermediate, "resources.pri"), "different indexed resource");
        using var lease = XamlSourceCoordinates.HoldPayload(path, _intermediate);
        Assert.IsFalse(lease.Verified);
        StringAssert.Contains(lease.Error!, "PRI resource key");
    }

    [TestMethod]
    public async Task PriHashBinding_HoldsTheActualPayloadAndRejectsReplacement()
    {
        var manifest = await SnapshotAsync();
        var pri = Path.Combine(_intermediate, "resources.pri");
        File.WriteAllText(pri, "controlled PRI bytes verified by the separate resource-key tests");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(pri)));
        manifest = manifest with { Coordinates = Capture(manifest).Select(file => file with { PriHash = hash }).ToArray() };
        var path = Path.Combine(_root, "inventory.json");
        File.WriteAllBytes(path, GuestSourceSnapshot.SerializeInventory(manifest));
        using (var lease = XamlSourceCoordinates.HoldPayload(path, _intermediate))
        {
            Assert.IsTrue(lease.Verified, lease.Error);
            Assert.ThrowsExactly<IOException>(() => File.WriteAllText(pri, "replacement"));
        }
        File.WriteAllText(pri, "replacement");
        using var changed = XamlSourceCoordinates.HoldPayload(path, _intermediate);
        Assert.IsFalse(changed.Verified);
    }

    [TestMethod]
    public async Task PriPathBinding_HoldsBothIndexAndReferencedXbf_AndRejectsChangedXbf()
    {
        var manifest = await SnapshotAsync();
        var pri = Path.Combine(_intermediate, "resources.pri");
        File.WriteAllText(pri, "PRI Path proof supplied by the resource-key parser");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(pri)));
        manifest = manifest with { Coordinates = Capture(manifest).Select(file => file with
        { PriHash = hash, PayloadPaths = ["Main.xbf"] }).ToArray() };
        var path = Path.Combine(_root, "inventory.json");
        File.WriteAllBytes(path, GuestSourceSnapshot.SerializeInventory(manifest));
        using (var lease = XamlSourceCoordinates.HoldPayload(path, _intermediate))
        {
            Assert.IsTrue(lease.Verified, lease.Error);
            Assert.ThrowsExactly<IOException>(() => File.WriteAllText(pri, "replacement"));
            Assert.ThrowsExactly<IOException>(() => File.WriteAllText(Path.Combine(_intermediate, "Main.xbf"), "replacement"));
        }
        File.WriteAllText(Path.Combine(_intermediate, "Main.xbf"), "replacement");
        using var changed = XamlSourceCoordinates.HoldPayload(path, _intermediate);
        Assert.IsFalse(changed.Verified);
    }

    [TestMethod]
    public async Task LegacyHostView_ReceivesOnlyAnAdvisoryCandidate_WithoutAnchorPromotion()
    {
        var manifest = await SnapshotAsync();
        manifest = manifest with { Coordinates = Capture(manifest) };
        var view = new CommentView
        {
            ProjectRoot = _root,
            Anchor = new() { SourceFile = "Main.xaml", Line = 2, Column = 55, Identity = new() { Type = "TextBlock", Content = "dynamic text" } },
        };
        var before = JsonSerializer.Serialize(view, CommentsJsonContext.Default.CommentView);
        var candidate = CommentViewBuilder.FindMappedCandidate(view, manifest);
        Assert.IsNotNull(candidate);
        Assert.IsTrue(candidate.Advisory);
        Assert.AreEqual(1, candidate.Column);
        Assert.AreEqual(before, JsonSerializer.Serialize(view, CommentsJsonContext.Default.CommentView));
        Assert.IsFalse(view.AnchorConfirmed);
        Assert.IsEmpty(view.Hits);
        File.WriteAllText(Path.Combine(_root, "Main.xaml"), Original.Replace("Vm.Title", "Vm.Other", StringComparison.Ordinal));
        Assert.IsNull(CommentViewBuilder.FindMappedCandidate(view, manifest));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SelectedArtifacts_AllowDiskMatchingAfterAnIncrementalBuildOrNoBuild(bool noBuild)
    {
        var options = new Models.ProjectRunOptions("Debug", "x64", null, noBuild, true, [],
            CaptureDevToolsSources: true);
        var arguments = noBuild ? Services.ProjectRunService.BuildEvaluateArguments(_project, options)
            : Helpers.WindowsCommandLine.JoinArguments(Services.ProjectRunService.BuildPublishArguments(
                _project, options, "quiet", publish: false))!;
        Assert.IsFalse(arguments.Contains("--no-incremental", StringComparison.Ordinal));
        var compiler = XamlSourceCoordinates.FromProperties(_project, new Dictionary<string, string>
        {
            ["XamlCompilerExeInputJson"] = Path.Combine(_intermediate, "input.json"),
            ["XamlCompilerExeOutputJson"] = Path.Combine(_intermediate, "output.json"),
            ["XamlSavedStateFilePath"] = Path.Combine(_intermediate, "state.xml"),
        });
        Assert.IsNotNull(compiler);
        var manifest = await GuestSourceSnapshot.CreateAsync(_project, ["Main.xaml"],
            new(Path.Combine(_root, "snapshot")), TestContext.CancellationToken, compiler);
        Assert.IsNull(manifest.CoordinateError);
        Assert.HasCount(1, manifest.Coordinates!);
        Assert.IsFalse(manifest.CoordinatesAdvisory);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PublishArtifacts_AllowDiskMatchingWithoutForcingCompilation(bool noBuild)
    {
        var options = new Models.ProjectRunOptions("Debug", "x64", null, noBuild, true, [],
            CaptureDevToolsSources: true);
        var arguments = Services.ProjectRunService.BuildPublishArguments(_project, options, "quiet");
        Assert.AreEqual("publish", arguments[0]);
        Assert.IsFalse(arguments.Contains("--no-incremental"));
        Assert.AreEqual(noBuild, arguments.Contains("--no-build"));
        var compiler = new XamlCompilerArtifacts(Path.Combine(_intermediate, "input.json"),
            Path.Combine(_intermediate, "output.json"), Path.Combine(_intermediate, "state.xml"));
        var manifest = await GuestSourceSnapshot.CreateAsync(_project, ["Main.xaml"],
            new(Path.Combine(_root, "snapshot")), TestContext.CancellationToken, compiler);
        Assert.IsNull(manifest.CoordinateError);
        Assert.HasCount(1, manifest.Coordinates!);
        Assert.IsFalse(manifest.CoordinatesAdvisory);
    }

    [TestMethod]
    public async Task NoBuildCapture_AllowsDiskMatchingWithoutClaimingRunningBytes()
    {
        var compiler = new XamlCompilerArtifacts(Path.Combine(_intermediate, "input.json"),
            Path.Combine(_intermediate, "output.json"), Path.Combine(_intermediate, "state.xml"));
        var manifest = await GuestSourceSnapshot.CreateAsync(_project, ["Main.xaml"],
            new(Path.Combine(_root, "snapshot")), TestContext.CancellationToken, compiler);
        Assert.IsFalse(manifest.CoordinatesAdvisory);
        Assert.HasCount(1, manifest.Coordinates!);
        using var document = JsonDocument.Parse(GuestSourceSnapshot.SerializeInventory(manifest));
        Assert.IsFalse(document.RootElement.GetProperty("coordinatesAdvisory").GetBoolean());
    }

    [TestMethod]
    public async Task InvalidCompilerTuple_ProducesExplicitUnavailableReason_NotAnInventedMap()
    {
        WriteMetadata(includeXbf: false);
        var compiler = new XamlCompilerArtifacts(Path.Combine(_intermediate, "input.json"),
            Path.Combine(_intermediate, "output.json"), Path.Combine(_intermediate, "state.xml"));
        var manifest = await GuestSourceSnapshot.CreateAsync(_project, ["Main.xaml"],
            new(Path.Combine(_root, "snapshot")), TestContext.CancellationToken, compiler);
        Assert.IsEmpty(manifest.Coordinates!);
        Assert.IsNull(manifest.CoordinateError);
        StringAssert.Contains(manifest.CoordinateExclusions!.Single().Reason, "does not list both artifacts");
    }

    [TestMethod]
    public async Task MissingGeneratedXaml_DoesNotWeakenFullFileRewriteValidation()
    {
        File.Delete(Path.Combine(_intermediate, "Main.xaml"));
        var compiler = new XamlCompilerArtifacts(Path.Combine(_intermediate, "input.json"),
            Path.Combine(_intermediate, "output.json"), Path.Combine(_intermediate, "state.xml"));
        var manifest = await GuestSourceSnapshot.CreateAsync(_project, ["Main.xaml"],
            new(Path.Combine(_root, "snapshot")), TestContext.CancellationToken, compiler);
        Assert.IsEmpty(manifest.Coordinates!);
        var exclusion = manifest.CoordinateExclusions!.Single();
        Assert.AreEqual("Main.xaml", exclusion.Source);
        Assert.IsFalse(string.IsNullOrWhiteSpace(exclusion.Reason));
        Assert.HasCount(1, manifest.Files, "Ordinary source transport is retained.");
    }
}

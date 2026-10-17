// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="PriService"/>. MakePri.exe is never invoked for real; instead a fake
/// <see cref="IBuildToolsService"/> stands in and (for the config/dump flows) writes the XML output
/// files that the real tool would produce so the service's XML manipulation and parsing can be exercised.
/// </summary>
[TestClass]
public class PriServiceTests
{
    private DirectoryInfo _tempDir = null!;
    private DirectoryInfo _packageDir = null!;
    private FakeBuildToolsService _buildTools = null!;
    private PriService _service = null!;
    private TaskContext _taskContext = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"PriSvcTest_{Guid.NewGuid():N}"));
        _tempDir.Create();
        _packageDir = _tempDir.CreateSubdirectory("package");
        _buildTools = new FakeBuildToolsService();
        _service = new PriService(_buildTools);

        var task = new GroupableTask("test", null);
        _taskContext = new TaskContext(task, null, new TestConsole(), NullLogger<PriServiceTests>.Instance, new Lock());
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_tempDir.Exists)
        {
            _tempDir.Delete(recursive: true);
        }
    }

    private const string BasePriConfig = @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<resources targetOsVersion=""10.0.0"" majorVersion=""1"">
  <index root=""\"" startIndexAt=""\"">
    <default>
      <qualifier name=""Language"" value=""en-US"" />
    </default>
    <indexer-config type=""folder"" qualifierDelimiter=""."" />
    <indexer-config type=""PRI"" />
  </index>
</resources>";

    #region CreatePriConfigAsync

    [TestMethod]
    public async Task CreatePriConfigAsync_WritesResfiles_FilteringToImageExtensions()
    {
        _buildTools.Handler = (_, _) =>
        {
            File.WriteAllText(Path.Combine(_packageDir.FullName, "priconfig.xml"), BasePriConfig);
            return ("", "");
        };

        var candidates = new[]
        {
            "Assets\\Logo.png",
            "Assets\\Splash.jpg",
            "readme.txt",       // filtered out (not an image extension)
            "app.dll",          // filtered out
            "Assets\\Icon.ico",
        };

        await _service.CreatePriConfigAsync(_packageDir, _taskContext, candidates);

        var resfiles = await File.ReadAllTextAsync(Path.Combine(_packageDir.FullName, "pri.resfiles"));
        StringAssert.Contains(resfiles, "Assets\\Logo.png");
        StringAssert.Contains(resfiles, "Assets\\Splash.jpg");
        StringAssert.Contains(resfiles, "Assets\\Icon.ico");
        Assert.IsFalse(resfiles.Contains("readme.txt", StringComparison.Ordinal));
        Assert.IsFalse(resfiles.Contains("app.dll", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CreatePriConfigAsync_InvokesMakePriCreateConfig()
    {
        _buildTools.Handler = (_, _) =>
        {
            File.WriteAllText(Path.Combine(_packageDir.FullName, "priconfig.xml"), BasePriConfig);
            return ("", "");
        };

        await _service.CreatePriConfigAsync(_packageDir, _taskContext, ["a.png"], language: "fr-FR", platformVersion: "10.0.1");

        Assert.AreEqual(1, _buildTools.Invocations.Count);
        var (tool, args) = _buildTools.Invocations[0];
        Assert.AreEqual("makepri.exe", tool);
        StringAssert.Contains(args, "createconfig");
        StringAssert.Contains(args, "lang-fr-FR");
        StringAssert.Contains(args, "/pv 10.0.1");
    }

    [TestMethod]
    public async Task CreatePriConfigAsync_InjectsResfilesIndexerAndFolderQualifierAttributes()
    {
        _buildTools.Handler = (_, _) =>
        {
            File.WriteAllText(Path.Combine(_packageDir.FullName, "priconfig.xml"), BasePriConfig);
            return ("", "");
        };

        var configPath = await _service.CreatePriConfigAsync(_packageDir, _taskContext, ["Logo.png"]);

        var xml = await File.ReadAllTextAsync(configPath.FullName);
        // A resfiles indexer-config must be appended.
        StringAssert.Contains(xml, "type=\"resfiles\"");
        // startIndexAt must be rewritten to the relative resfiles path.
        StringAssert.Contains(xml, ".\\pri.resfiles");
        // Folder indexer must be augmented with both qualifier flags (created since they were absent).
        StringAssert.Contains(xml, "foldernameAsQualifier=\"true\"");
        StringAssert.Contains(xml, "filenameAsQualifier=\"true\"");
    }

    [TestMethod]
    public async Task CreatePriConfigAsync_UpdatesExistingFolderQualifierAttributes()
    {
        // Folder indexer already declares the attributes (with false) — service should flip them to true.
        const string configWithFolderAttrs = @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<resources targetOsVersion=""10.0.0"" majorVersion=""1"">
  <index root=""\"" startIndexAt=""\"">
    <indexer-config type=""folder"" foldernameAsQualifier=""false"" filenameAsQualifier=""false"" qualifierDelimiter=""."" />
  </index>
</resources>";
        _buildTools.Handler = (_, _) =>
        {
            File.WriteAllText(Path.Combine(_packageDir.FullName, "priconfig.xml"), configWithFolderAttrs);
            return ("", "");
        };

        var configPath = await _service.CreatePriConfigAsync(_packageDir, _taskContext, ["Logo.png"]);

        var xml = await File.ReadAllTextAsync(configPath.FullName);
        StringAssert.Contains(xml, "foldernameAsQualifier=\"true\"");
        StringAssert.Contains(xml, "filenameAsQualifier=\"true\"");
        Assert.IsFalse(xml.Contains("=\"false\"", StringComparison.Ordinal), "Existing false flags should have been flipped to true.");
    }

    [TestMethod]
    public async Task CreatePriConfigAsync_MissingPackageDir_ThrowsDirectoryNotFound()
    {
        var missing = new DirectoryInfo(Path.Combine(_tempDir.FullName, "missing"));

        await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(() =>
            _service.CreatePriConfigAsync(missing, _taskContext, ["a.png"]));
    }

    [TestMethod]
    public async Task CreatePriConfigAsync_NullCandidates_ThrowsArgumentNull()
    {
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() =>
            _service.CreatePriConfigAsync(_packageDir, _taskContext, null!));
    }

    [TestMethod]
    public async Task CreatePriConfigAsync_ToolFailure_WrappedInInvalidOperationException()
    {
        _buildTools.Handler = (_, _) => throw new InvalidOperationException("makepri boom");

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _service.CreatePriConfigAsync(_packageDir, _taskContext, ["a.png"]));
        StringAssert.Contains(ex.Message, "Failed to create PRI configuration");
    }

    #endregion

    #region GeneratePriFileAsync

    [TestMethod]
    public async Task GeneratePriFileAsync_ParsesResourceFilesFromOutput()
    {
        File.WriteAllText(Path.Combine(_packageDir.FullName, "priconfig.xml"), BasePriConfig);
        _buildTools.Handler = (_, _) =>
            ("Some header line\r\nResource File: resources.pri\r\nResource File: fr-FR\\resources.pri\r\nTrailing", "");

        var result = await _service.GeneratePriFileAsync(_packageDir, _taskContext);

        Assert.AreEqual(2, result.Count);
        Assert.IsTrue(result.Any(f => f.Name == "resources.pri"));
        Assert.IsTrue(result.Any(f => f.FullName.EndsWith(Path.Combine("fr-FR", "resources.pri"), StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task GeneratePriFileAsync_StripsNullChars_AndIsCaseInsensitive()
    {
        File.WriteAllText(Path.Combine(_packageDir.FullName, "priconfig.xml"), BasePriConfig);
        _buildTools.Handler = (_, _) => ("resource file: a.pri\0\r\n\0RESOURCE FILE: b.pri", "");

        var result = await _service.GeneratePriFileAsync(_packageDir, _taskContext);

        Assert.AreEqual(2, result.Count);
    }

    [TestMethod]
    public async Task GeneratePriFileAsync_NoResourceLines_ReturnsEmptyList()
    {
        File.WriteAllText(Path.Combine(_packageDir.FullName, "priconfig.xml"), BasePriConfig);
        _buildTools.Handler = (_, _) => ("Nothing relevant here", "");

        var result = await _service.GeneratePriFileAsync(_packageDir, _taskContext);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public async Task GeneratePriFileAsync_MissingPackageDir_ThrowsDirectoryNotFound()
    {
        var missing = new DirectoryInfo(Path.Combine(_tempDir.FullName, "missing"));

        await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(() =>
            _service.GeneratePriFileAsync(missing, _taskContext));
    }

    [TestMethod]
    public async Task GeneratePriFileAsync_MissingConfig_ThrowsFileNotFound()
    {
        // No priconfig.xml written.
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            _service.GeneratePriFileAsync(_packageDir, _taskContext));
    }

    [TestMethod]
    public async Task GeneratePriFileAsync_ToolFailure_WrappedInInvalidOperationException()
    {
        File.WriteAllText(Path.Combine(_packageDir.FullName, "priconfig.xml"), BasePriConfig);
        _buildTools.Handler = (_, _) => throw new InvalidOperationException("makepri new boom");

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _service.GeneratePriFileAsync(_packageDir, _taskContext));
        StringAssert.Contains(ex.Message, "Failed to generate PRI file");
    }

    [TestMethod]
    public async Task GeneratePriFileAsync_RespectsExplicitConfigAndOutputPaths()
    {
        var customConfig = new FileInfo(Path.Combine(_packageDir.FullName, "custom.xml"));
        File.WriteAllText(customConfig.FullName, BasePriConfig);
        var customOutput = new FileInfo(Path.Combine(_packageDir.FullName, "custom.pri"));

        _buildTools.Handler = (_, args) => (args, "");

        await _service.GeneratePriFileAsync(_packageDir, _taskContext, customConfig, customOutput);

        var (_, invokedArgs) = _buildTools.Invocations[0];
        StringAssert.Contains(invokedArgs, "custom.xml");
        StringAssert.Contains(invokedArgs, "custom.pri");
    }

    #endregion

    #region ExtractLanguagesFromPriAsync

    [TestMethod]
    public async Task ExtractLanguagesFromPriAsync_ReturnsSortedDistinctLanguages()
    {
        var priFile = new FileInfo(Path.Combine(_packageDir.FullName, "resources.pri"));
        File.WriteAllText(priFile.FullName, "binary");

        const string dump = @"<PriInfo>
  <ResourceMap>
    <Candidate qualifiers=""Language-fr-FR"" />
    <Candidate qualifiers=""Language-en-US, Scale-200"" />
    <Candidate qualifiers=""Language-en-US"" />
    <Candidate qualifiers=""Scale-100"" />
  </ResourceMap>
</PriInfo>";
        _buildTools.Handler = (_, args) =>
        {
            WriteDumpFile(args, dump);
            return ("", "");
        };

        var languages = await _service.ExtractLanguagesFromPriAsync(priFile, _taskContext, CancellationToken.None);

        string[] expected = ["en-US", "fr-FR"];
        CollectionAssert.AreEqual(expected, languages);
    }

    [TestMethod]
    public async Task ExtractLanguagesFromPriAsync_DeletesDumpFileAfterReading()
    {
        var priFile = new FileInfo(Path.Combine(_packageDir.FullName, "resources.pri"));
        File.WriteAllText(priFile.FullName, "binary");
        string? dumpPath = null;
        _buildTools.Handler = (_, args) =>
        {
            dumpPath = ExtractOfPath(args);
            File.WriteAllText(dumpPath, "<Candidate qualifiers=\"Language-de-DE\" />");
            return ("", "");
        };

        var languages = await _service.ExtractLanguagesFromPriAsync(priFile, _taskContext, CancellationToken.None);

        string[] expected = ["de-DE"];
        CollectionAssert.AreEqual(expected, languages);
        Assert.IsNotNull(dumpPath);
        Assert.IsFalse(File.Exists(dumpPath), "The temporary dump file should be deleted after parsing.");
    }

    [TestMethod]
    public async Task ExtractLanguagesFromPriAsync_NoDumpProduced_ReturnsEmpty()
    {
        var priFile = new FileInfo(Path.Combine(_packageDir.FullName, "resources.pri"));
        File.WriteAllText(priFile.FullName, "binary");
        // Handler writes nothing, so the dump file never exists.
        _buildTools.Handler = (_, _) => ("", "");

        var languages = await _service.ExtractLanguagesFromPriAsync(priFile, _taskContext, CancellationToken.None);

        Assert.AreEqual(0, languages.Count);
    }

    [TestMethod]
    public async Task ExtractLanguagesFromPriAsync_ToolThrows_ReturnsEmptyWithoutPropagating()
    {
        var priFile = new FileInfo(Path.Combine(_packageDir.FullName, "resources.pri"));
        File.WriteAllText(priFile.FullName, "binary");
        _buildTools.Handler = (_, _) => throw new InvalidOperationException("makepri dump boom");

        var languages = await _service.ExtractLanguagesFromPriAsync(priFile, _taskContext, CancellationToken.None);

        Assert.AreEqual(0, languages.Count);
    }

    [TestMethod]
    public async Task ExtractLanguagesFromPriAsync_NoLanguageQualifiers_ReturnsEmpty()
    {
        var priFile = new FileInfo(Path.Combine(_packageDir.FullName, "resources.pri"));
        File.WriteAllText(priFile.FullName, "binary");
        _buildTools.Handler = (_, args) =>
        {
            WriteDumpFile(args, "<Candidate qualifiers=\"Scale-200\" /><Candidate qualifiers=\"Contrast-high\" />");
            return ("", "");
        };

        var languages = await _service.ExtractLanguagesFromPriAsync(priFile, _taskContext, CancellationToken.None);

        Assert.AreEqual(0, languages.Count);
    }

    #endregion

    #region MergeAssetsIntoPriAsync

    private void WriteLayoutFile(string relativePath, string content = "x")
    {
        var path = Path.Combine(_packageDir.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string ExtractPrPath(string arguments)
    {
        var match = Regex.Match(arguments, @"/pr\s+""([^""]+)""");
        Assert.IsTrue(match.Success, $"Expected a quoted /pr argument in: {arguments}");
        return match.Groups[1].Value;
    }

    [TestMethod]
    public async Task MergeAssetsIntoPriAsync_IndexesExistingPriAndAssets_AndReplacesResourcesPri()
    {
        WriteLayoutFile("resources.pri", "build-pri");
        WriteLayoutFile("Assets\\AppList.png");
        WriteLayoutFile("Assets\\AppList.targetsize-24_altform-unplated.png");
        string[]? resfiles = null;
        bool originalCopied = false;
        bool unplatedCopied = false;
        _buildTools.Handler = (_, args) =>
        {
            var input = ExtractPrPath(args);
            resfiles = File.ReadAllLines(Path.Combine(input, "assets.resfiles"));
            originalCopied = File.ReadAllText(Path.Combine(input, "original.pri")) == "build-pri";
            unplatedCopied = File.Exists(Path.Combine(input, "Assets", "AppList.targetsize-24_altform-unplated.png"));
            File.WriteAllText(ExtractOfPath(args), "merged-pri");
            return ("", "");
        };

        var merged = await _service.MergeAssetsIntoPriAsync(
            _packageDir, "Contoso.App",
            ["Assets/AppList.png", "Assets\\AppList.targetsize-24_altform-unplated.png"],
            _taskContext);

        Assert.IsTrue(merged);
        Assert.AreEqual(1, _buildTools.Invocations.Count);
        var (tool, args) = _buildTools.Invocations[0];
        Assert.AreEqual("makepri.exe", tool);
        StringAssert.StartsWith(args, "new ");
        StringAssert.Contains(args, "/in \"Contoso.App\"");
        Assert.IsTrue(originalCopied, "The existing resources.pri should be indexed as original.pri.");
        Assert.IsTrue(unplatedCopied, "The unplated asset should be copied next to the resfiles list.");
        string[] expected = ["Assets\\AppList.png", "Assets\\AppList.targetsize-24_altform-unplated.png"];
        CollectionAssert.AreEqual(expected, resfiles);
        Assert.AreEqual("merged-pri", File.ReadAllText(Path.Combine(_packageDir.FullName, "resources.pri")));
    }

    [TestMethod]
    public async Task MergeAssetsIntoPriAsync_SkipsNonImagesMissingFilesAndPathsOutsideTheLayout()
    {
        WriteLayoutFile("resources.pri");
        WriteLayoutFile("Assets\\Logo.png");
        WriteLayoutFile("readme.txt");
        File.WriteAllText(Path.Combine(_tempDir.FullName, "outside.png"), "x");
        string[]? resfiles = null;
        _buildTools.Handler = (_, args) =>
        {
            resfiles = File.ReadAllLines(Path.Combine(ExtractPrPath(args), "assets.resfiles"));
            File.WriteAllText(ExtractOfPath(args), "merged");
            return ("", "");
        };

        var merged = await _service.MergeAssetsIntoPriAsync(
            _packageDir, "Contoso.App",
            ["Assets\\Logo.png", "readme.txt", "Assets\\Missing.png", "..\\outside.png", Path.Combine(_tempDir.FullName, "outside.png")],
            _taskContext);

        Assert.IsTrue(merged);
        string[] expected = ["Assets\\Logo.png"];
        CollectionAssert.AreEqual(expected, resfiles);
    }

    [TestMethod]
    public async Task MergeAssetsIntoPriAsync_NoResourcesPri_ReturnsFalseWithoutRunningMakePri()
    {
        WriteLayoutFile("Assets\\Logo.png");

        var merged = await _service.MergeAssetsIntoPriAsync(_packageDir, "Contoso.App", ["Assets\\Logo.png"], _taskContext);

        Assert.IsFalse(merged);
        Assert.AreEqual(0, _buildTools.Invocations.Count);
    }

    [TestMethod]
    public async Task MergeAssetsIntoPriAsync_NoUsableAssets_ReturnsFalseAndLeavesPriUntouched()
    {
        WriteLayoutFile("resources.pri", "build-pri");

        var merged = await _service.MergeAssetsIntoPriAsync(_packageDir, "Contoso.App", ["Assets\\Missing.png", "notes.txt"], _taskContext);

        Assert.IsFalse(merged);
        Assert.AreEqual(0, _buildTools.Invocations.Count);
        Assert.AreEqual("build-pri", File.ReadAllText(Path.Combine(_packageDir.FullName, "resources.pri")));
    }

    [TestMethod]
    public async Task MergeAssetsIntoPriAsync_InvalidPackageName_Throws()
    {
        WriteLayoutFile("resources.pri");

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => _service.MergeAssetsIntoPriAsync(_packageDir, "bad\" /o", ["Assets\\Logo.png"], _taskContext));
    }

    [TestMethod]
    public async Task MergeAssetsIntoPriAsync_NoOutputProduced_ThrowsAndLeavesPriUntouched()
    {
        WriteLayoutFile("resources.pri", "build-pri");
        WriteLayoutFile("Assets\\Logo.png");
        _buildTools.Handler = (_, _) => ("", "");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _service.MergeAssetsIntoPriAsync(_packageDir, "Contoso.App", ["Assets\\Logo.png"], _taskContext));

        Assert.AreEqual("build-pri", File.ReadAllText(Path.Combine(_packageDir.FullName, "resources.pri")));
    }

    #endregion

    private static void WriteDumpFile(string arguments, string content)
    {
        var dumpPath = ExtractOfPath(arguments);
        File.WriteAllText(dumpPath, content);
    }

    /// <summary>Extracts the value of the <c>/of "..."</c> output-file argument, stripping any extended-length prefix.</summary>
    private static string ExtractOfPath(string arguments)
    {
        var match = Regex.Match(arguments, "/of\\s+\"([^\"]+)\"");
        Assert.IsTrue(match.Success, $"Expected an /of argument in: {arguments}");
        var path = match.Groups[1].Value;
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }
}

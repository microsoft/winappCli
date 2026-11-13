// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using Spectre.Console.Testing;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="ManifestService"/>, the orchestrator that turns CLI inputs into a
/// generated AppxManifest and its assets. Exercises the real prompt/derive workflow, manifest
/// generation with logo and executable-icon extraction, asset-reference extraction (including
/// splash / lock-screen / custom-dimension parsing), attribute formatting, and the full set of
/// execution-alias result branches.
/// </summary>
// UpdateManifestAssets renders bitmaps through GDI+ (System.Drawing), whose encoder
// lookup is not thread-safe and races under MSTest's method-level parallelism. Production
// renders assets sequentially, so serialize this class rather than altering product code.
[DoNotParallelize]
[TestClass]
public class ManifestServiceTests
{
    private DirectoryInfo _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"ManifestSvcTest_{Guid.NewGuid():N}"));
        _tempDir.Create();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_tempDir.Exists)
        {
            try { _tempDir.Delete(recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    private static ManifestService NewService(IAnsiConsole? console = null)
        => new(new ManifestTemplateService(), new ImageAssetService(), console ?? new TestConsole());

    private static TaskContext CreateTaskContext()
    {
        var task = new GroupableTask("test", null);
        return new TaskContext(task, null, new TestConsole(), NullLogger.Instance, new Lock());
    }

    private string WriteManifest(string name, string content)
    {
        var path = Path.Combine(_tempDir.FullName, name);
        File.WriteAllText(path, content);
        return path;
    }

    #region PromptForManifestInfoAsync

    [TestMethod]
    public async Task PromptForManifestInfo_UseDefaults_CleansNameAndFillsDefaults()
    {
        var info = await NewService().PromptForManifestInfoAsync(
            _tempDir, packageName: "My App!!", publisherName: null, version: "1.0.0.0",
            description: null, executable: null, useDefaults: true);

        Assert.AreEqual("MyApp", info.PackageName, "invalid characters are stripped from the package name");
        Assert.AreEqual("1.0.0.0", info.Version);
        Assert.IsFalse(string.IsNullOrWhiteSpace(info.PublisherName), "a default publisher is filled in");
        Assert.IsFalse(string.IsNullOrWhiteSpace(info.Description), "a default description is filled in");
    }

    [TestMethod]
    public async Task PromptForManifestInfo_WithExecutable_DerivesMetadataFromVersionInfo()
    {
        var exe = Environment.ProcessPath;
        if (exe is null || !File.Exists(exe))
        {
            Assert.Inconclusive("No process executable available to read version info from.");
            return;
        }

        var info = await NewService().PromptForManifestInfoAsync(
            _tempDir, packageName: null, publisherName: null, version: "1.0.0.0",
            description: null, executable: exe, useDefaults: true);

        Assert.IsFalse(string.IsNullOrWhiteSpace(info.PackageName), "package name is derived from the executable metadata");
        Assert.IsTrue(Regex.IsMatch(info.PackageName, "^[-.A-Za-z0-9]+$"), "derived package name is schema-clean");
        Assert.IsFalse(string.IsNullOrWhiteSpace(info.PublisherName), "publisher is derived from the executable company name");
    }

    [TestMethod]
    public async Task PromptForManifestInfo_EmptyDescription_FallsBackToDefault()
    {
        // An empty/whitespace description (e.g. an exe with no Comments/FileDescription) must be
        // normalized to the default rather than emitting Description="" — the ??= path alone would
        // keep the empty string.
        var info = await NewService().PromptForManifestInfoAsync(
            _tempDir, packageName: "MyApp", publisherName: "CN=Me", version: "1.0.0.0",
            description: "   ", executable: null, useDefaults: true);

        Assert.IsFalse(string.IsNullOrWhiteSpace(info.Description), "whitespace description is replaced with a default");
        Assert.AreEqual(SystemDefaultsHelper.GetDefaultDescription(), info.Description);
    }

    [TestMethod]
    public async Task PromptForManifestInfo_Interactive_UsesPromptedValues()
    {
        using var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Input.PushTextWithEnter("MyPkg");
        console.Input.PushTextWithEnter("CN=Me");
        console.Input.PushTextWithEnter("9.9.9.9");
        console.Input.PushTextWithEnter("My description");

        var info = await NewService(console).PromptForManifestInfoAsync(
            _tempDir, packageName: "seed", publisherName: "seedPub", version: "1.0.0.0",
            description: "seedDesc", executable: null, useDefaults: false);

        Assert.AreEqual("MyPkg", info.PackageName);
        Assert.AreEqual("CN=Me", info.PublisherName);
        Assert.AreEqual("9.9.9.9", info.Version);
        Assert.AreEqual("My description", info.Description);
    }

    [TestMethod]
    public async Task PromptForManifestInfo_Interactive_CleansOverriddenName()
    {
        // A raw interactive override with invalid Identity characters must be re-cleaned so it
        // cannot produce malformed XML or an unpackable Identity name.
        using var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Input.PushTextWithEnter("A&B App");
        console.Input.PushTextWithEnter("CN=Me");
        console.Input.PushTextWithEnter("1.0.0.0");
        console.Input.PushTextWithEnter("desc");

        var info = await NewService(console).PromptForManifestInfoAsync(
            _tempDir, packageName: "seed", publisherName: "seedPub", version: "1.0.0.0",
            description: "seedDesc", executable: null, useDefaults: false);

        Assert.AreEqual("ABApp", info.PackageName, "overridden name should be sanitized to the Identity schema");
    }

    #endregion

    #region CleanPackageName

    [TestMethod]
    [DataRow("My App!!", "MyApp", DisplayName = "strips spaces and punctuation")]
    [DataRow("Contoso.Sample-App", "Contoso.Sample-App", DisplayName = "keeps dots and hyphens")]
    [DataRow("!!!", "DefaultPackage", DisplayName = "all-invalid falls back to DefaultPackage")]
    [DataRow("   ", "DefaultPackage", DisplayName = "whitespace falls back to DefaultPackage")]
    [DataRow("ab", "ab1", DisplayName = "pads to minimum length of 3")]
    public void CleanPackageName_SanitizesToIdentitySchema(string input, string expected)
    {
        Assert.AreEqual(expected, ManifestService.CleanPackageName(input));
    }

    [TestMethod]
    public void CleanPackageName_TruncatesToFiftyCharacters()
    {
        var result = ManifestService.CleanPackageName(new string('a', 80));
        Assert.AreEqual(50, result.Length);
    }

    #endregion

    #region GenerateManifestAsync

    [TestMethod]
    public async Task GenerateManifest_NoLogoNoExecutable_WritesManifest()
    {
        var info = new ManifestGenerationInfo("MyApp", "CN=Test", "1.0.0.0", "A description");

        await NewService().GenerateManifestAsync(
            _tempDir, info, ManifestTemplates.Packaged, logoPath: null, executable: null, CreateTaskContext());

        Assert.IsTrue(File.Exists(Path.Combine(_tempDir.FullName, "Package.appxmanifest")));
    }

    [TestMethod]
    public async Task GenerateManifest_WithLogo_RegeneratesAssetsAndIco()
    {
        var info = new ManifestGenerationInfo("MyApp", "CN=Test", "1.0.0.0", "A description");
        var logo = new FileInfo(PngHelper.CreateRasterPng(Path.Combine(_tempDir.FullName, "logo.png"), 64, 64, System.Drawing.Color.SlateBlue));

        await NewService().GenerateManifestAsync(
            _tempDir, info, ManifestTemplates.Packaged, logo, executable: null, CreateTaskContext());

        Assert.IsTrue(File.Exists(Path.Combine(_tempDir.FullName, "Package.appxmanifest")));
        // The generated manifest references Assets\* logos, so assets are regenerated from the
        // provided logo and an app.ico is written into that assets directory.
        Assert.IsTrue(File.Exists(Path.Combine(_tempDir.FullName, "Assets", "app.ico")), "an app.ico is produced next to the assets");
    }

    [TestMethod]
    public async Task GenerateManifest_WithExecutable_ExtractsIconAndCreatesManifest()
    {
        // Any existing executable works: the icon extractor is stubbed below so the test does
        // not depend on the real shell image list (which can be empty on a headless CI session).
        var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        var exe = File.Exists(notepad) ? notepad : Environment.ProcessPath;
        if (exe is null || !File.Exists(exe))
        {
            Assert.Inconclusive("No executable available.");
            return;
        }

        var info = new ManifestGenerationInfo("MyApp", "CN=Test", "1.0.0.0", "A description");

        var service = NewService();
        // Deterministically exercise the extracted-icon -> app.ico path. The lambda returns a
        // fresh icon each call; the production code owns and disposes it.
        service.ExecutableIconExtractor = _ => new System.Drawing.Icon(System.Drawing.SystemIcons.Application, 256, 256);

        await service.GenerateManifestAsync(
            _tempDir, info, ManifestTemplates.Packaged, logoPath: null, executable: exe, CreateTaskContext());

        Assert.IsTrue(File.Exists(Path.Combine(_tempDir.FullName, "Package.appxmanifest")),
            "the manifest is generated when relying on executable icon extraction");
        Assert.IsTrue(File.Exists(Path.Combine(_tempDir.FullName, "Assets", "app.ico")),
            "the icon extracted from the executable is written out as app.ico");
    }

    #endregion

    #region UpdateManifestAssetsAsync

    [TestMethod]
    public async Task UpdateManifestAssets_ManifestWithoutReferences_GeneratesDefaultAssets()
    {
        var manifest = WriteManifest("Package.appxmanifest", """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="App" Publisher="CN=Test" Version="1.0.0.0" />
            </Package>
            """);
        var logo = new FileInfo(PngHelper.CreateRasterPng(Path.Combine(_tempDir.FullName, "logo.png"), 40, 20, System.Drawing.Color.SlateBlue));

        await NewService().UpdateManifestAssetsAsync(new FileInfo(manifest), logo, CreateTaskContext());

        var assetsDir = Path.Combine(_tempDir.FullName, "Assets");
        Assert.IsTrue(File.Exists(Path.Combine(assetsDir, "StoreLogo.png")), "default asset set is generated when the manifest has no references");
        Assert.IsTrue(File.Exists(Path.Combine(assetsDir, "app.ico")), "an app.ico is written into the default assets directory");
    }

    [TestMethod]
    public async Task UpdateManifestAssets_ManifestWithReferences_RegeneratesReferencedAssets()
    {
        var manifest = WriteManifest("Package.appxmanifest", """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
              <Applications>
                <Application Id="App">
                  <uap:VisualElements Square44x44Logo="Assets\AppList.png" Square150x150Logo="Assets\MedTile.png" />
                </Application>
              </Applications>
            </Package>
            """);
        var logo = new FileInfo(PngHelper.CreateRasterPng(Path.Combine(_tempDir.FullName, "logo.png"), 40, 20, System.Drawing.Color.SlateBlue));

        await NewService().UpdateManifestAssetsAsync(new FileInfo(manifest), logo, CreateTaskContext());

        var assetsDir = Path.Combine(_tempDir.FullName, "Assets");
        Assert.IsTrue(File.Exists(Path.Combine(assetsDir, "AppList.png")), "referenced 44x44 asset is regenerated");
        Assert.IsTrue(File.Exists(Path.Combine(assetsDir, "MedTile.png")), "referenced 150x150 asset is regenerated");
        Assert.IsTrue(File.Exists(Path.Combine(assetsDir, "app.ico")), "app.ico lands next to the 44x44 app icon");
    }

    [TestMethod]
    public async Task UpdateManifestAssets_ReferencesWithoutAppIcon_UsesMostCommonAssetDirectory()
    {
        // Deliberately list the lone Images\ reference FIRST and give Assets\ the majority
        // (two references vs one). A "pick the first/only directory" implementation would put
        // app.ico under Images\; correct majority selection must put it under Assets\.
        var manifest = WriteManifest("Package.appxmanifest", """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
              <Applications>
                <Application Id="App">
                  <uap:VisualElements Square71x71Logo="Images\SmallTile.png" Square150x150Logo="Assets\MedTile.png">
                    <uap:DefaultTile Wide310x150Logo="Assets\WideTile.png" />
                  </uap:VisualElements>
                </Application>
              </Applications>
            </Package>
            """);
        var logo = new FileInfo(PngHelper.CreateRasterPng(Path.Combine(_tempDir.FullName, "logo.png"), 40, 20, System.Drawing.Color.SlateBlue));

        await NewService().UpdateManifestAssetsAsync(new FileInfo(manifest), logo, CreateTaskContext());

        var assetsDir = Path.Combine(_tempDir.FullName, "Assets");
        var imagesDir = Path.Combine(_tempDir.FullName, "Images");
        Assert.IsTrue(File.Exists(Path.Combine(imagesDir, "SmallTile.png")), "the minority Images\\ reference is regenerated");
        Assert.IsTrue(File.Exists(Path.Combine(assetsDir, "MedTile.png")), "referenced 150x150 asset is regenerated");
        Assert.IsTrue(File.Exists(Path.Combine(assetsDir, "WideTile.png")), "referenced wide asset is regenerated");
        Assert.IsTrue(File.Exists(Path.Combine(assetsDir, "app.ico")),
            "with no 44x44 icon, app.ico lands in the majority (Assets) directory, not the first-listed one");
        Assert.IsFalse(File.Exists(Path.Combine(imagesDir, "app.ico")),
            "app.ico must not fall into the minority (Images) directory that was listed first");
    }

    #endregion

    #region ExtractAssetReferencesFromManifest

    [TestMethod]
    public void ExtractAssetReferences_FindsEveryKnownAssetType()
    {
        var manifest = WriteManifest("m.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
              <Properties>
                <Logo>Assets\StoreLogo.png</Logo>
              </Properties>
              <Applications>
                <Application Id="App">
                  <uap:VisualElements Square150x150Logo="Assets\Med.png" Square44x44Logo="Assets\App.png">
                    <uap:DefaultTile Wide310x150Logo="Assets\Wide.png" />
                    <uap:SplashScreen Image="Assets\Splash.png" />
                    <uap:LockScreen BadgeLogo="Assets\Badge.png" />
                  </uap:VisualElements>
                </Application>
              </Applications>
            </Package>
            """);

        var refs = ManifestService.ExtractAssetReferencesFromManifest(new FileInfo(manifest), CreateTaskContext());

        void AssertRef(string contains, int w, int h) =>
            Assert.IsTrue(refs.Any(r => r.RelativePath.Contains(contains) && r.BaseWidth == w && r.BaseHeight == h),
                $"expected {contains} => {w}x{h}");

        AssertRef("StoreLogo", 50, 50);
        AssertRef("Med.png", 150, 150);
        AssertRef("App.png", 44, 44);
        AssertRef("Wide.png", 310, 150);
        AssertRef("Splash.png", 620, 300);
        AssertRef("Badge.png", 24, 24);
        Assert.AreEqual(6, refs.Count);
    }

    [TestMethod]
    public void ExtractAssetReferences_CustomDimensionFilename_ParsedFromName()
    {
        var manifest = WriteManifest("m.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Properties>
                <Logo>Assets\brand512x256.png</Logo>
              </Properties>
            </Package>
            """);

        var refs = ManifestService.ExtractAssetReferencesFromManifest(new FileInfo(manifest), CreateTaskContext());

        Assert.AreEqual(1, refs.Count);
        Assert.AreEqual(512, refs[0].BaseWidth);
        Assert.AreEqual(256, refs[0].BaseHeight);
    }

    [TestMethod]
    public void ExtractAssetReferences_UnknownFilename_DefaultsToStoreLogoSize()
    {
        var manifest = WriteManifest("m.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Properties>
                <Logo>Assets\brand.png</Logo>
              </Properties>
            </Package>
            """);

        var refs = ManifestService.ExtractAssetReferencesFromManifest(new FileInfo(manifest), CreateTaskContext());

        Assert.AreEqual(1, refs.Count);
        Assert.AreEqual(50, refs[0].BaseWidth);
        Assert.AreEqual(50, refs[0].BaseHeight);
    }

    [TestMethod]
    public void ExtractAssetReferences_MalformedManifest_ReturnsEmpty()
    {
        var manifest = WriteManifest("bad.xml", "<Package><unclosed>");

        var refs = ManifestService.ExtractAssetReferencesFromManifest(new FileInfo(manifest), CreateTaskContext());

        Assert.AreEqual(0, refs.Count, "an unparseable manifest yields no references rather than throwing");
    }

    #endregion

    #region FormatXmlAttributes

    [TestMethod]
    public void FormatXmlAttributes_SplitsElementsWithMoreThanTwoAttributes()
    {
        var input = string.Join("\n",
            "<root>",
            "  <a x=\"1\" y=\"2\" z=\"3\" />",
            "  <b p=\"1\" q=\"2\" />",
            "  plain text",
            "</root>");

        var result = ManifestService.FormatXmlAttributes(input);

        // Element with 3 attributes is split so each attribute is on its own line.
        Assert.Contains("<a" + Environment.NewLine, result);
        Assert.Contains("x=\"1\"", result);
        Assert.Contains("z=\"3\" />", result);
        // Element with 2 attributes stays on a single line.
        Assert.Contains("<b p=\"1\" q=\"2\" />", result);
        // Non-tag lines pass through untouched.
        Assert.Contains("plain text", result);
        // The trailing newline added by the loop is trimmed.
        Assert.IsTrue(result.EndsWith("</root>", StringComparison.Ordinal), "output should not end with an extra newline");
    }

    #endregion

    #region AddExecutionAliasAsync

    private const string AliasManifestHeader =
        "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\" " +
        "xmlns:uap5=\"http://schemas.microsoft.com/appx/manifest/uap/windows10/5\" " +
        "xmlns:uap10=\"http://schemas.microsoft.com/appx/manifest/uap/windows10/10\" " +
        "IgnorableNamespaces=\"uap10\">";

    private FileInfo WriteAliasManifest(string applicationsXml)
    {
        var content = $"""
            <?xml version="1.0" encoding="utf-8"?>
            {AliasManifestHeader}
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
            {applicationsXml}
              </Applications>
            </Package>
            """;
        return new FileInfo(WriteManifest("Package.appxmanifest", content));
    }

    [TestMethod]
    public async Task AddAlias_InfersFromExecutable_AddsAliasAndDeclaresNamespace()
    {
        var manifest = WriteAliasManifest(
            "    <Application Id=\"testApp\" Executable=\"myapp.exe\" EntryPoint=\"Windows.FullTrustApplication\" />");

        var result = await NewService().AddExecutionAliasAsync(new AddExecutionAliasOptions(manifest, null, null));

        Assert.AreEqual(AddExecutionAliasStatus.Added, result.Status);
        Assert.AreEqual("myapp.exe", result.AliasName);

        var content = await File.ReadAllTextAsync(manifest.FullName);
        Assert.Contains("uap5:ExecutionAlias", content);
        Assert.Contains("Alias=\"myapp.exe\"", content);

        // Parse the manifest and assert the actual IgnorableNamespaces attribute value. A plain
        // Contains("uap5") is tautological once the uap5:ExecutionAlias element exists; this
        // instead pins the attribute update at ManifestService lines ~619-627.
        var ignorable = XDocument.Parse(content).Root!
            .Attribute("IgnorableNamespaces")!.Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("uap5", ignorable, "uap5 is appended to IgnorableNamespaces");
        Assert.Contains("uap10", ignorable, "the pre-existing IgnorableNamespaces entry is preserved");
    }

    [TestMethod]
    public async Task AddAlias_ManifestWithoutUap5_DeclaresNamespaceOnPackage()
    {
        // Manifest declares neither uap5 nor IgnorableNamespaces, so adding an alias must add
        // the uap5 namespace declaration to the Package element.
        var manifest = new FileInfo(WriteManifest("Package.appxmanifest", """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
              <Applications>
                <Application Id="testApp" Executable="myapp.exe" EntryPoint="Windows.FullTrustApplication" />
              </Applications>
            </Package>
            """));

        var result = await NewService().AddExecutionAliasAsync(new AddExecutionAliasOptions(manifest, null, null));

        Assert.AreEqual(AddExecutionAliasStatus.Added, result.Status);

        var root = XDocument.Parse(await File.ReadAllTextAsync(manifest.FullName)).Root!;
        Assert.AreEqual(
            "http://schemas.microsoft.com/appx/manifest/uap/windows10/5",
            root.GetNamespaceOfPrefix("uap5")?.NamespaceName,
            "the uap5 namespace is declared when the manifest lacks it");
    }

    [TestMethod]
    public async Task AddAlias_MalformedManifest_ReturnsParseError()
    {
        var manifest = new FileInfo(WriteManifest("Package.appxmanifest", "<Package><unclosed>"));

        var result = await NewService().AddExecutionAliasAsync(new AddExecutionAliasOptions(manifest, null, null));

        Assert.AreEqual(AddExecutionAliasStatus.ManifestParseError, result.Status);
        Assert.IsFalse(string.IsNullOrEmpty(result.ErrorMessage));
    }

    [TestMethod]
    public async Task AddAlias_NoApplicationElement_ReturnsNoApplication()
    {
        var manifest = new FileInfo(WriteManifest("Package.appxmanifest", $"""
            <?xml version="1.0" encoding="utf-8"?>
            {AliasManifestHeader}
              <Identity Name="test-app" Publisher="CN=test" Version="1.0.0.0" />
            </Package>
            """));

        var result = await NewService().AddExecutionAliasAsync(new AddExecutionAliasOptions(manifest, null, null));

        Assert.AreEqual(AddExecutionAliasStatus.NoApplicationElement, result.Status);
    }

    [TestMethod]
    public async Task AddAlias_AppIdNotFound_ReturnsApplicationIdNotFound()
    {
        var manifest = WriteAliasManifest("    <Application Id=\"testApp\" Executable=\"myapp.exe\" />");

        var result = await NewService().AddExecutionAliasAsync(new AddExecutionAliasOptions(manifest, null, "otherApp"));

        Assert.AreEqual(AddExecutionAliasStatus.ApplicationIdNotFound, result.Status);
    }

    [TestMethod]
    public async Task AddAlias_ExecutableWithTraversal_ReturnsInvalidAliasName()
    {
        var manifest = WriteAliasManifest("    <Application Id=\"testApp\" Executable=\"..\\evil.exe\" />");

        var result = await NewService().AddExecutionAliasAsync(new AddExecutionAliasOptions(manifest, null, null));

        Assert.AreEqual(AddExecutionAliasStatus.InvalidAliasName, result.Status);
    }

    [TestMethod]
    public async Task AddAlias_NoExecutableNoAlias_ReturnsCouldNotInfer()
    {
        var manifest = WriteAliasManifest("    <Application Id=\"testApp\" />");

        var result = await NewService().AddExecutionAliasAsync(new AddExecutionAliasOptions(manifest, null, null));

        Assert.AreEqual(AddExecutionAliasStatus.CouldNotInferAlias, result.Status);
    }

    [TestMethod]
    public async Task AddAlias_ExecutableEndsWithSeparator_ReturnsCouldNotInfer()
    {
        var manifest = WriteAliasManifest("    <Application Id=\"testApp\" Executable=\"app\\\" />");

        var result = await NewService().AddExecutionAliasAsync(new AddExecutionAliasOptions(manifest, null, null));

        Assert.AreEqual(AddExecutionAliasStatus.CouldNotInferAlias, result.Status);
    }

    [TestMethod]
    public async Task AddAlias_UnsafeExplicitAlias_ReturnsInvalidAliasName()
    {
        var manifest = WriteAliasManifest("    <Application Id=\"testApp\" Executable=\"myapp.exe\" />");

        var result = await NewService().AddExecutionAliasAsync(new AddExecutionAliasOptions(manifest, "bad/alias", null));

        Assert.AreEqual(AddExecutionAliasStatus.InvalidAliasName, result.Status);
    }

    [TestMethod]
    public async Task AddAlias_SameAliasAlreadyPresent_ReturnsAlreadyExists()
    {
        var manifest = WriteAliasManifest("""
                <Application Id="testApp" Executable="myapp.exe">
                  <Extensions>
                    <uap5:Extension Category="windows.appExecutionAlias">
                      <uap5:AppExecutionAlias>
                        <uap5:ExecutionAlias Alias="myapp.exe" />
                      </uap5:AppExecutionAlias>
                    </uap5:Extension>
                  </Extensions>
                </Application>
            """);

        var result = await NewService().AddExecutionAliasAsync(new AddExecutionAliasOptions(manifest, "myapp", null));

        Assert.AreEqual(AddExecutionAliasStatus.AlreadyExists, result.Status);
    }

    [TestMethod]
    public async Task AddAlias_DifferentAliasAlreadyPresent_ReturnsConflict()
    {
        var manifest = WriteAliasManifest("""
                <Application Id="testApp" Executable="myapp.exe">
                  <Extensions>
                    <uap5:Extension Category="windows.appExecutionAlias">
                      <uap5:AppExecutionAlias>
                        <uap5:ExecutionAlias Alias="other.exe" />
                      </uap5:AppExecutionAlias>
                    </uap5:Extension>
                  </Extensions>
                </Application>
            """);

        var result = await NewService().AddExecutionAliasAsync(new AddExecutionAliasOptions(manifest, "myapp", null));

        Assert.AreEqual(AddExecutionAliasStatus.ConflictingAliasExists, result.Status);
        Assert.AreEqual("other.exe", result.ExistingAlias);
    }

    [TestMethod]
    public async Task AddAlias_ExistingEmptyAppExecutionAliasBlock_AddsAlias()
    {
        var manifest = WriteAliasManifest("""
                <Application Id="testApp" Executable="myapp.exe">
                  <Extensions>
                    <uap5:Extension Category="windows.appExecutionAlias">
                      <uap5:AppExecutionAlias />
                    </uap5:Extension>
                  </Extensions>
                </Application>
            """);

        var result = await NewService().AddExecutionAliasAsync(new AddExecutionAliasOptions(manifest, "myapp", null));

        Assert.AreEqual(AddExecutionAliasStatus.Added, result.Status);
        Assert.Contains("Alias=\"myapp.exe\"", await File.ReadAllTextAsync(manifest.FullName));
    }

    [TestMethod]
    public async Task AddAlias_ExistingExtensionWithoutAppExecutionAlias_CreatesBlock()
    {
        var manifest = WriteAliasManifest("""
                <Application Id="testApp" Executable="myapp.exe">
                  <Extensions>
                    <uap5:Extension Category="windows.appExecutionAlias" />
                  </Extensions>
                </Application>
            """);

        var result = await NewService().AddExecutionAliasAsync(new AddExecutionAliasOptions(manifest, "myapp", null));

        Assert.AreEqual(AddExecutionAliasStatus.Added, result.Status);
        Assert.Contains("uap5:AppExecutionAlias", await File.ReadAllTextAsync(manifest.FullName));
    }

    #endregion
}

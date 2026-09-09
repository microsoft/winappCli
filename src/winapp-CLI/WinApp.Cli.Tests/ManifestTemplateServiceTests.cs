// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="ManifestTemplateService"/>. Exercises the real
/// <see cref="ManifestTemplateService.GenerateCompleteManifestAsync"/> workflow (template
/// expansion, publisher normalization, ApplicationId derivation and default-asset extraction)
/// end-to-end, plus the ASCII Windows-id sanitizer and the DN helpers it exposes.
/// </summary>
[TestClass]
public class ManifestTemplateServiceTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";

    private DirectoryInfo _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"ManifestTplTest_{Guid.NewGuid():N}"));
        _tempDir.Create();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_tempDir.Exists)
        {
            _tempDir.Delete(recursive: true);
        }
    }

    private static TaskContext CreateTaskContext()
    {
        var task = new GroupableTask("test", null);
        var console = new Spectre.Console.Testing.TestConsole();
        return new TaskContext(task, null, console, NullLogger.Instance, new Lock());
    }

    #region FixAsciiWindowsId

    [TestMethod]
    [DataRow("", "Default", DisplayName = "empty -> Default fallback")]
    [DataRow("   ", "Default", DisplayName = "whitespace -> Default fallback")]
    [DataRow("MyApp", "MyApp", DisplayName = "already valid single segment")]
    [DataRow("com.contoso.app", "com.contoso.app", DisplayName = "already valid dotted id")]
    [DataRow("My App", "My.App", DisplayName = "space becomes a dot separator")]
    [DataRow("com.my-app", "com.my.app", DisplayName = "dash becomes a dot separator")]
    [DataRow("123abc", "A123abc", DisplayName = "segment starting with digit gets A prefix")]
    [DataRow("Contoso.App_2024!", "Contoso.App.A2024", DisplayName = "invalid chars split, digit segment prefixed")]
    [DataRow("...", "Default", DisplayName = "all-separator input -> Default fallback")]
    public void FixAsciiWindowsId_SanitizesToValidIdentifier(string input, string expected)
    {
        Assert.AreEqual(expected, ManifestTemplateService.FixAsciiWindowsId(input));
    }

    [TestMethod]
    public void FixAsciiWindowsId_TruncatesToMaxLength()
    {
        var input = new string('a', 300);

        var result = ManifestTemplateService.FixAsciiWindowsId(input);

        Assert.AreEqual(255, result.Length, "Windows ids are capped at 255 characters");
        Assert.IsTrue(result.All(c => c == 'a'));
    }

    #endregion

    #region StripCnPrefix / IsDistinguishedName

    [TestMethod]
    public void StripCnPrefix_RemovesCnFromSimpleDistinguishedName()
    {
        Assert.AreEqual("Contoso", ManifestTemplateService.StripCnPrefix("CN=Contoso"));
    }

    [TestMethod]
    public void StripCnPrefix_LeavesMultiComponentDistinguishedNameUnchanged()
    {
        const string dn = "CN=Contoso, O=Contoso Corp";
        Assert.AreEqual(dn, ManifestTemplateService.StripCnPrefix(dn));
    }

    [TestMethod]
    public void StripCnPrefix_LeavesBareNameUnchanged()
    {
        Assert.AreEqual("Contoso", ManifestTemplateService.StripCnPrefix("Contoso"));
    }

    [TestMethod]
    [DataRow("CN=Contoso Ltd", true, DisplayName = "CN distinguished name")]
    [DataRow("OU=Finance, DC=corp, DC=com", true, DisplayName = "multi-attribute distinguished name")]
    [DataRow("Contoso Ltd", false, DisplayName = "bare name is not a DN")]
    [DataRow("", false, DisplayName = "empty is not a DN")]
    public void IsDistinguishedName_ClassifiesInput(string input, bool expected)
    {
        Assert.AreEqual(expected, ManifestTemplateService.IsDistinguishedName(input));
    }

    #endregion

    #region GenerateCompleteManifestAsync

    [TestMethod]
    public async Task GenerateCompleteManifest_Packaged_ProducesManifestAndAssets()
    {
        var outDir = new DirectoryInfo(Path.Combine(_tempDir.FullName, "packaged"));

        await new ManifestTemplateService().GenerateCompleteManifestAsync(
            outDir,
            packageName: "MyTestApp",
            publisherName: "Contoso Ltd",
            version: "2.3.4.5",
            manifestTemplate: ManifestTemplates.Packaged,
            description: "A sample packaged app",
            taskContext: CreateTaskContext());

        var manifestPath = Path.Combine(outDir.FullName, "Package.appxmanifest");
        Assert.IsTrue(File.Exists(manifestPath), "Package.appxmanifest should be generated");

        var doc = XDocument.Load(manifestPath);
        var root = doc.Root!;
        var identity = root.Element(Ns + "Identity")!;

        Assert.AreEqual("MyTestApp", identity.Attribute("Name")!.Value);
        Assert.AreEqual("CN=Contoso Ltd", identity.Attribute("Publisher")!.Value, "bare publisher is wrapped as CN=");
        Assert.AreEqual("2.3.4.5", identity.Attribute("Version")!.Value, "version placeholder should be replaced");

        var props = root.Element(Ns + "Properties")!;
        Assert.AreEqual("MyTestApp", props.Element(Ns + "DisplayName")!.Value);
        Assert.AreEqual("Contoso Ltd", props.Element(Ns + "PublisherDisplayName")!.Value, "display name strips CN=");

        var app = root.Element(Ns + "Applications")!.Element(Ns + "Application")!;
        Assert.AreEqual("myTestApp", app.Attribute("Id")!.Value, "ApplicationId is camelCased and ascii-sanitized");

        var visual = app.Element(Uap + "VisualElements")!;
        Assert.AreEqual("A sample packaged app", visual.Attribute("Description")!.Value);
        Assert.AreEqual("MyTestApp", visual.Attribute("DisplayName")!.Value);

        // Default assets are extracted alongside the manifest.
        var assetsDir = new DirectoryInfo(Path.Combine(outDir.FullName, "Assets"));
        Assert.IsTrue(assetsDir.Exists, "Assets directory should be generated");
        Assert.IsTrue(assetsDir.GetFiles("*.png").Length > 0, "default PNG assets should be extracted");
    }

    [TestMethod]
    public async Task GenerateCompleteManifest_Sparse_UsesSparseTemplate()
    {
        var outDir = new DirectoryInfo(Path.Combine(_tempDir.FullName, "sparse"));

        await new ManifestTemplateService().GenerateCompleteManifestAsync(
            outDir,
            packageName: "SparseApp",
            publisherName: "Contoso",
            version: "1.2.3.0",
            manifestTemplate: ManifestTemplates.Sparse,
            description: "Sparse sample",
            taskContext: CreateTaskContext());

        var manifestPath = Path.Combine(outDir.FullName, "Package.appxmanifest");
        var doc = XDocument.Load(manifestPath);
        var root = doc.Root!;

        Assert.AreEqual("SparseApp", root.Element(Ns + "Identity")!.Attribute("Name")!.Value);

        // The sparse template carries AllowExternalContent, which the packaged one does not.
        XNamespace uap10 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";
        Assert.IsNotNull(
            root.Element(Ns + "Properties")!.Element(uap10 + "AllowExternalContent"),
            "sparse template should include uap10:AllowExternalContent");
    }

    [TestMethod]
    public async Task GenerateCompleteManifest_PreservesExistingDistinguishedName()
    {
        var outDir = new DirectoryInfo(Path.Combine(_tempDir.FullName, "dn"));

        await new ManifestTemplateService().GenerateCompleteManifestAsync(
            outDir,
            packageName: "DnApp",
            publisherName: "CN=Existing Corp, O=Existing",
            version: "1.0.0.0",
            manifestTemplate: ManifestTemplates.Packaged,
            description: "DN publisher",
            taskContext: CreateTaskContext());

        var doc = XDocument.Load(Path.Combine(outDir.FullName, "Package.appxmanifest"));
        var identity = doc.Root!.Element(Ns + "Identity")!;

        Assert.AreEqual("CN=Existing Corp, O=Existing", identity.Attribute("Publisher")!.Value,
            "an already-valid DN must be preserved verbatim");
        Assert.AreEqual("CN=Existing Corp, O=Existing",
            doc.Root!.Element(Ns + "Properties")!.Element(Ns + "PublisherDisplayName")!.Value,
            "multi-component DN is shown in full as the publisher display name");
    }

    [TestMethod]
    public async Task GenerateCompleteManifest_CamelCasesMultiWordPackageName()
    {
        var outDir = new DirectoryInfo(Path.Combine(_tempDir.FullName, "multiword"));

        await new ManifestTemplateService().GenerateCompleteManifestAsync(
            outDir,
            packageName: "My-Cool_App Name",
            publisherName: "Contoso",
            version: "1.0.0.0",
            manifestTemplate: ManifestTemplates.Packaged,
            description: "multi word",
            taskContext: CreateTaskContext());

        var doc = XDocument.Load(Path.Combine(outDir.FullName, "Package.appxmanifest"));
        var app = doc.Root!.Element(Ns + "Applications")!.Element(Ns + "Application")!;

        Assert.AreEqual("myCoolAppName", app.Attribute("Id")!.Value,
            "separators split words that are camelCased into a single identifier");
    }

    [TestMethod]
    public async Task GenerateCompleteManifest_EmptyPackageName_FallsBackToDefaultApplicationId()
    {
        var outDir = new DirectoryInfo(Path.Combine(_tempDir.FullName, "empty"));

        await new ManifestTemplateService().GenerateCompleteManifestAsync(
            outDir,
            packageName: "",
            publisherName: "Contoso",
            version: "1.0.0.0",
            manifestTemplate: ManifestTemplates.Packaged,
            description: "empty name",
            taskContext: CreateTaskContext());

        var doc = XDocument.Load(Path.Combine(outDir.FullName, "Package.appxmanifest"));
        var app = doc.Root!.Element(Ns + "Applications")!.Element(Ns + "Application")!;

        Assert.AreEqual("Default", app.Attribute("Id")!.Value,
            "an empty package name yields the Default ApplicationId fallback");
    }

    [TestMethod]
    public async Task GenerateCompleteManifest_DisplayNameOverride_ReplacesBothDisplayNames()
    {
        // Single-file mode infers a human-readable display name from #:property WinAppDisplayName, which
        // is independent of the sanitized Identity name.
        var outDir = new DirectoryInfo(Path.Join(_tempDir.FullName, "displayname"));

        await new ManifestTemplateService().GenerateCompleteManifestAsync(
            outDir,
            packageName: "com.contoso.counter",
            publisherName: "Contoso",
            version: "1.0.0.0",
            manifestTemplate: ManifestTemplates.Packaged,
            description: "counts things",
            taskContext: CreateTaskContext(),
            displayName: "Contoso Counter");

        var root = XDocument.Load(Path.Join(outDir.FullName, "Package.appxmanifest")).Root!;

        Assert.AreEqual("com.contoso.counter", root.Element(Ns + "Identity")!.Attribute("Name")!.Value,
            "the display name must not leak into the identity");
        Assert.AreEqual("Contoso Counter", root.Element(Ns + "Properties")!.Element(Ns + "DisplayName")!.Value);
        Assert.AreEqual("Contoso Counter",
            root.Element(Ns + "Applications")!.Element(Ns + "Application")!
                .Element(Uap + "VisualElements")!.Attribute("DisplayName")!.Value);
    }

    [TestMethod]
    public async Task GenerateCompleteManifest_DisplayNameOverride_IsXmlEscaped()
    {
        var outDir = new DirectoryInfo(Path.Join(_tempDir.FullName, "displayname_escape"));

        await new ManifestTemplateService().GenerateCompleteManifestAsync(
            outDir,
            packageName: "EscapeApp",
            publisherName: "Contoso",
            version: "1.0.0.0",
            manifestTemplate: ManifestTemplates.Packaged,
            description: "escaping",
            taskContext: CreateTaskContext(),
            displayName: "Tom & Jerry <\"quoted\">");

        // Loading at all proves the document stayed well-formed; the value round-trips decoded.
        var root = XDocument.Load(Path.Join(outDir.FullName, "Package.appxmanifest")).Root!;

        Assert.AreEqual("Tom & Jerry <\"quoted\">", root.Element(Ns + "Properties")!.Element(Ns + "DisplayName")!.Value);
    }

    [TestMethod]
    public async Task GenerateCompleteManifest_ApplicationIdOverride_ReplacesTheDerivedId()
    {
        // Single-file mode pins Application/@Id to "App" rather than deriving it from a dotted
        // reverse-DNS package name, whose sanitization is a needless source of surprise.
        var outDir = new DirectoryInfo(Path.Join(_tempDir.FullName, "appid"));

        await new ManifestTemplateService().GenerateCompleteManifestAsync(
            outDir,
            packageName: "com.contoso.counter",
            publisherName: "Contoso",
            version: "1.0.0.0",
            manifestTemplate: ManifestTemplates.Packaged,
            description: "fixed id",
            taskContext: CreateTaskContext(),
            applicationId: "App");

        var app = XDocument.Load(Path.Join(outDir.FullName, "Package.appxmanifest"))
            .Root!.Element(Ns + "Applications")!.Element(Ns + "Application")!;

        Assert.AreEqual("App", app.Attribute("Id")!.Value);
    }

    [TestMethod]
    public async Task GenerateCompleteManifest_NoOverrides_KeepsThePreExistingDerivations()
    {
        // Guards the callers that never pass the new arguments (manifest generate, init, sparse identity):
        // omitting them must produce exactly what it produced before they existed.
        var outDir = new DirectoryInfo(Path.Join(_tempDir.FullName, "no_overrides"));

        await new ManifestTemplateService().GenerateCompleteManifestAsync(
            outDir,
            packageName: "MyTestApp",
            publisherName: "Contoso",
            version: "1.0.0.0",
            manifestTemplate: ManifestTemplates.Packaged,
            description: "unchanged",
            taskContext: CreateTaskContext());

        var root = XDocument.Load(Path.Join(outDir.FullName, "Package.appxmanifest")).Root!;
        var app = root.Element(Ns + "Applications")!.Element(Ns + "Application")!;

        Assert.AreEqual("MyTestApp", root.Element(Ns + "Properties")!.Element(Ns + "DisplayName")!.Value,
            "the display name still defaults to the package name");
        Assert.AreEqual("MyTestApp", app.Element(Uap + "VisualElements")!.Attribute("DisplayName")!.Value);
        Assert.AreEqual("myTestApp", app.Attribute("Id")!.Value,
            "the ApplicationId is still camelCased from the package name");
    }

    #endregion
}

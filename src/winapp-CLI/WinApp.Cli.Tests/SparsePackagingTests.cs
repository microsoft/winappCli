// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using System.Xml.Linq;
using WinApp.Cli.Commands;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for the sparse identity packaging workflow: init --exe --sparse, embed-identity (XML mode),
/// and version normalization. Uses real services (no build tools required for these paths).
/// </summary>
[TestClass]
[DoNotParallelize]
public class SparsePackagingTests : BaseCommandTests
{
    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        return services.AddSingleton<IDevModeService, FakeDevModeService>();
    }

    private string CopyTestExe(string fileName = "app.exe")
    {
        // A real PE file is needed so FileVersionInfo/icon extraction succeed.
        var dest = Path.Join(_tempDirectory.FullName, fileName);
        File.Copy(Path.Join(Environment.SystemDirectory, "notepad.exe"), dest, overwrite: true);
        return dest;
    }

    // Sparse init defaults to a dedicated ./sparse/ folder under the current directory, which the
    // test harness sets to _tempDirectory.
    private string SparseDir => Path.Join(_tempDirectory.FullName, "sparse");
    private string SparseManifestPath => Path.Join(SparseDir, "appxmanifest.xml");

    private const string MinimalSparseManifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
                 IgnorableNamespaces="uap10">
          <Identity Name="SparsePkg" Publisher="CN=TestPublisher" Version="1.0.0.0" ProcessorArchitecture="neutral" />
          <Properties>
            <DisplayName>SparsePkg</DisplayName>
            <PublisherDisplayName>Test</PublisherDisplayName>
            <Logo>Assets\StoreLogo.png</Logo>
            <uap10:AllowExternalContent>true</uap10:AllowExternalContent>
          </Properties>
          <Applications>
            <Application Id="SparsePkg" Executable="app.exe" uap10:RuntimeBehavior="win32App" uap10:TrustLevel="mediumIL" />
          </Applications>
        </Package>
        """;

    private const string NonSparseManifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 IgnorableNamespaces="">
          <Identity Name="FullPkg" Publisher="CN=TestPublisher" Version="1.0.0.0" />
          <Properties>
            <DisplayName>FullPkg</DisplayName>
            <PublisherDisplayName>Test</PublisherDisplayName>
            <Logo>Assets\StoreLogo.png</Logo>
          </Properties>
        </Package>
        """;

    [TestMethod]
    public async Task InitSparse_WithExe_GeneratesSparseIdentityManifest()
    {
        // Arrange
        var exe = CopyTestExe();
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { "--exe", exe, "--sparse", "--use-defaults", "--name", "MySparseApp" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Sparse init should succeed");

        var manifestPath = SparseManifestPath;
        Assert.IsTrue(File.Exists(manifestPath), "appxmanifest.xml should be generated in the default ./sparse/ folder");

        var content = await File.ReadAllTextAsync(manifestPath, TestContext.CancellationToken);
        Assert.Contains("uap10:AllowExternalContent", content, "Should be a sparse manifest");
        Assert.Contains("ProcessorArchitecture=\"neutral\"", content, "Identity should be neutral arch");
        Assert.Contains("MinVersion=\"10.0.19041.0\"", content, "MinVersion should be 19041");
        Assert.Contains("uap10:RuntimeBehavior=\"win32App\"", content, "Application should use win32App");
        Assert.DoesNotContain("EntryPoint=", content, "win32App must not declare EntryPoint");
        Assert.Contains("Executable=\"app.exe\"", content, "Executable should be substituted with the exe name");
        Assert.Contains("Name=\"MySparseApp\"", content, "Package name override should be applied");

        Assert.IsTrue(Directory.Exists(Path.Join(SparseDir, "Assets")), "Assets directory should be generated");
    }

    [TestMethod]
    public async Task InitSparse_WithOutputDir_WritesToThatDirectory()
    {
        // Arrange
        var exe = CopyTestExe();
        var outDir = Directory.CreateDirectory(Path.Join(_tempDirectory.FullName, "identity"));
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { "--exe", exe, "--sparse", "--use-defaults", "--output-dir", outDir.FullName };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Sparse init should succeed");
        Assert.IsTrue(File.Exists(Path.Join(outDir.FullName, "appxmanifest.xml")), "Manifest should be written to --output-dir");
    }

    [TestMethod]
    public async Task InitSparse_Interactive_PromptsOutsideStatusDisplay_DoesNotThrow()
    {
        // Regression: interactive sparse init (no --use-defaults) used to run the metadata
        // prompts INSIDE the live status spinner, which Spectre.Console rejects with
        // "Trying to run one or more interactive functions concurrently". Force the live-spinner
        // path and push prompt answers to prove the prompt now runs before the status display.
        var exe = CopyTestExe();
        var statusService = (StatusService)GetRequiredService<IStatusService>();
        statusService.ShouldUseLiveSpinnerProvider = (_, _) => true;

        TestAnsiConsole.Input.PushTextWithEnter("InteractivePkg");   // Package name
        TestAnsiConsole.Input.PushTextWithEnter("CN=Interactive");   // Publisher name
        TestAnsiConsole.Input.PushTextWithEnter("2.3.4.5");          // Version
        TestAnsiConsole.Input.PushTextWithEnter("Interactive desc"); // Description

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { "--exe", exe, "--sparse" };

        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        Assert.AreEqual(0, exitCode, "Interactive sparse init should complete without throwing");
        var manifestPath = SparseManifestPath;
        Assert.IsTrue(File.Exists(manifestPath), "appxmanifest.xml should be generated");
        var content = await File.ReadAllTextAsync(manifestPath, TestContext.CancellationToken);
        Assert.Contains("Name=\"InteractivePkg\"", content, "Prompted package name should be applied");
        Assert.Contains("Version=\"2.3.4.5\"", content, "Prompted version should be applied");
    }

    [TestMethod]
    public async Task InitSparse_NonExeTarget_ReturnsError()
    {
        // Arrange — a non-.exe target must be rejected before any manifest is written,
        // because sparse identity is embedded into an .exe.
        var notExe = Path.Join(_tempDirectory.FullName, "notes.txt");
        await File.WriteAllTextAsync(notExe, "not an executable", TestContext.CancellationToken);
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { "--exe", notExe, "--sparse", "--use-defaults" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(1, exitCode, "A non-.exe --exe target should fail");
        Assert.IsFalse(File.Exists(SparseManifestPath), "No manifest should be generated for a non-exe target");
    }

    [TestMethod]
    public async Task InitSparse_ExistingManifest_WithoutForce_ReturnsError()
    {
        // Arrange — a pre-existing appxmanifest.xml must not be silently overwritten.
        var exe = CopyTestExe();
        Directory.CreateDirectory(SparseDir);
        var existing = SparseManifestPath;
        await File.WriteAllTextAsync(existing, "<hand-authored/>", TestContext.CancellationToken);
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { "--exe", exe, "--sparse", "--use-defaults" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(1, exitCode, "Init should fail rather than overwrite an existing manifest");
        var content = await File.ReadAllTextAsync(existing, TestContext.CancellationToken);
        Assert.AreEqual("<hand-authored/>", content, "Existing manifest must be left untouched");
    }

    [TestMethod]
    public async Task InitSparse_ExistingManifest_WithForce_Overwrites()
    {
        // Arrange
        var exe = CopyTestExe();
        Directory.CreateDirectory(SparseDir);
        var existing = SparseManifestPath;
        await File.WriteAllTextAsync(existing, "<hand-authored/>", TestContext.CancellationToken);
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { "--exe", exe, "--sparse", "--use-defaults", "--force" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Init with --force should overwrite the existing manifest");
        var content = await File.ReadAllTextAsync(existing, TestContext.CancellationToken);
        Assert.Contains("uap10:AllowExternalContent", content, "Manifest should be replaced with the generated sparse manifest");
    }

    [TestMethod]
    public async Task InitSparse_ForceWithoutSparse_ReturnsError()
    {
        // Arrange — --force is sparse-only; without --sparse it must be rejected.
        var exe = CopyTestExe();
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { "--exe", exe, "--force", "--use-defaults" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(1, exitCode, "--force without --sparse should fail");
    }

    [TestMethod]
    public async Task InitSparse_ExeWithoutSparse_ReturnsError()
    {
        // Arrange
        var exe = CopyTestExe();
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { "--exe", exe, "--use-defaults" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(1, exitCode, "--exe without --sparse should fail");
        Assert.IsFalse(File.Exists(SparseManifestPath), "No manifest should be generated");
    }

    [TestMethod]
    public async Task InitSparse_WithoutExe_ReturnsError()
    {
        // Arrange — --sparse requires --exe; without it the flow must fail cleanly.
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { "--sparse", "--use-defaults" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(1, exitCode, "--sparse without --exe should fail");
        Assert.IsFalse(File.Exists(SparseManifestPath), "No manifest should be generated");
    }

    [TestMethod]
    public async Task InitSparse_WithPositionalDirectory_ReturnsError()
    {
        // Arrange — the positional base directory is not used by the sparse flow; passing it
        // should be rejected (pointing at --output-dir) rather than silently ignored.
        var exe = CopyTestExe();
        var ignoreDir = Directory.CreateDirectory(Path.Join(_tempDirectory.FullName, "ignoreme")).FullName;
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { ignoreDir, "--exe", exe, "--sparse", "--use-defaults" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(1, exitCode, "A positional directory with --sparse should be rejected");
        Assert.IsFalse(File.Exists(SparseManifestPath), "No manifest should be generated when input is rejected");
        Assert.Contains("--output-dir", ConsoleStdErr.ToString(), "Error should point the user at --output-dir");
    }

    [TestMethod]
    public async Task EmbedIdentity_XmlMode_InsertsMsixElement()
    {
        // Arrange: generate a sparse manifest to read identity from
        var exe = CopyTestExe();
        var initCommand = GetRequiredService<InitCommand>();
        await ParseAndInvokeWithCaptureAsync(initCommand, ["--exe", exe, "--sparse", "--use-defaults", "--name", "EmbeddedApp", "--publisher", "CN=Contoso"]);
        var manifestPath = SparseManifestPath;
        var targetManifest = Path.Join(_tempDirectory.FullName, "app.manifest");

        var embedCommand = GetRequiredService<EmbedIdentityCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(embedCommand, [targetManifest, "--manifest", manifestPath]);

        // Assert
        Assert.AreEqual(0, exitCode, "embed-identity XML mode should succeed");
        Assert.IsTrue(File.Exists(targetManifest), "SxS manifest should be created");
        var content = await File.ReadAllTextAsync(targetManifest, TestContext.CancellationToken);
        Assert.Contains("packageName=\"EmbeddedApp\"", content, "msix element should carry the package name");
        Assert.Contains("urn:schemas-microsoft-com:msix.v1", content, "msix namespace should be present");
    }

    [TestMethod]
    public async Task EmbedIdentity_XmlMode_ReplacesExistingElement()
    {
        // Arrange
        var exe = CopyTestExe();
        var initCommand = GetRequiredService<InitCommand>();
        await ParseAndInvokeWithCaptureAsync(initCommand, ["--exe", exe, "--sparse", "--use-defaults", "--name", "IdempotentApp"]);
        var manifestPath = SparseManifestPath;
        var targetManifest = Path.Join(_tempDirectory.FullName, "app.manifest");
        var embedCommand = GetRequiredService<EmbedIdentityCommand>();

        // Act: run twice
        await ParseAndInvokeWithCaptureAsync(embedCommand, [targetManifest, "--manifest", manifestPath]);
        var exitCode = await ParseAndInvokeWithCaptureAsync(embedCommand, [targetManifest, "--manifest", manifestPath]);

        // Assert: still exactly one <msix> element
        Assert.AreEqual(0, exitCode);
        var content = await File.ReadAllTextAsync(targetManifest, TestContext.CancellationToken);
        var occurrences = content.Split("<msix", StringSplitOptions.None).Length - 1;
        Assert.AreEqual(1, occurrences, "Re-running embed-identity should replace, not duplicate, the msix element");
    }

    [TestMethod]
    public async Task EmbedIdentity_AutoDiscoversManifestInSparseFolder()
    {
        // The dedicated ./sparse/ folder that 'init --exe --sparse' writes to by default must be
        // found by embed-identity without an explicit --manifest (zero-config step 3).
        var exe = CopyTestExe();
        var initCommand = GetRequiredService<InitCommand>();
        await ParseAndInvokeWithCaptureAsync(initCommand, ["--exe", exe, "--sparse", "--use-defaults", "--name", "AutoFound"]);
        var targetManifest = Path.Join(_tempDirectory.FullName, "app.manifest");
        var embedCommand = GetRequiredService<EmbedIdentityCommand>();

        // Act — no --manifest; discovery must locate ./sparse/appxmanifest.xml
        var exitCode = await ParseAndInvokeWithCaptureAsync(embedCommand, [targetManifest]);

        // Assert
        Assert.AreEqual(0, exitCode, "embed-identity should auto-discover the manifest in ./sparse/");
        var content = await File.ReadAllTextAsync(targetManifest, TestContext.CancellationToken);
        Assert.Contains("packageName=\"AutoFound\"", content, "Discovered manifest identity should be embedded");
    }

    [TestMethod]
    public async Task EmbedIdentity_XmlMode_NewFile_AddsTopLevelAssemblyIdentity()
    {
        // A newly created fusion manifest must carry a top-level <assemblyIdentity> alongside
        // <msix>, or Windows won't grant identity (per the MS grant-identity docs).
        var exe = CopyTestExe();
        var initCommand = GetRequiredService<InitCommand>();
        await ParseAndInvokeWithCaptureAsync(initCommand, ["--exe", exe, "--sparse", "--use-defaults", "--name", "IdentityApp", "--publisher", "CN=Contoso"]);
        var manifestPath = SparseManifestPath;
        var targetManifest = Path.Join(_tempDirectory.FullName, "app.manifest");
        var embedCommand = GetRequiredService<EmbedIdentityCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(embedCommand, [targetManifest, "--manifest", manifestPath]);

        // Assert
        Assert.AreEqual(0, exitCode);
        var root = XDocument.Load(targetManifest).Root!;
        var topLevelIdentity = root.Elements().Where(e => e.Name.LocalName == "assemblyIdentity").ToList();
        Assert.AreEqual(1, topLevelIdentity.Count, "Exactly one top-level <assemblyIdentity> must be present");
        Assert.AreEqual("IdentityApp", topLevelIdentity[0].Attribute("name")?.Value);
        Assert.AreEqual(1, root.Elements().Count(e => e.Name.LocalName == "msix"), "The <msix> identity element must be present");
    }

    [TestMethod]
    public async Task EmbedIdentity_XmlMode_DependencyOnlyManifest_AddsTopLevelAssemblyIdentity()
    {
        // A fusion manifest whose only <assemblyIdentity> is nested under <dependency> (e.g.
        // Common-Controls) has no identity of its own, so a top-level one must still be added.
        var exe = CopyTestExe();
        var initCommand = GetRequiredService<InitCommand>();
        await ParseAndInvokeWithCaptureAsync(initCommand, ["--exe", exe, "--sparse", "--use-defaults", "--name", "DepApp", "--publisher", "CN=Contoso"]);
        var manifestPath = SparseManifestPath;
        var targetManifest = Path.Join(_tempDirectory.FullName, "app.manifest");
        await File.WriteAllTextAsync(targetManifest, """
            <?xml version="1.0" encoding="utf-8"?>
            <assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
              <dependency>
                <dependentAssembly>
                  <assemblyIdentity type="win32" name="Microsoft.Windows.Common-Controls" version="6.0.0.0" publicKeyToken="6595b64144ccf1df" language="*" processorArchitecture="*" />
                </dependentAssembly>
              </dependency>
            </assembly>
            """, TestContext.CancellationToken);
        var embedCommand = GetRequiredService<EmbedIdentityCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(embedCommand, [targetManifest, "--manifest", manifestPath]);

        // Assert
        Assert.AreEqual(0, exitCode);
        var root = XDocument.Load(targetManifest).Root!;
        var topLevelIdentity = root.Elements().Where(e => e.Name.LocalName == "assemblyIdentity").ToList();
        Assert.AreEqual(1, topLevelIdentity.Count, "A top-level <assemblyIdentity> must be added even when a nested dependency identity exists");
        Assert.AreEqual("DepApp", topLevelIdentity[0].Attribute("name")?.Value);
    }

    [TestMethod]
    public async Task EmbedIdentity_XmlMode_ForeignNamespaceIdentity_AddsAsmV1AssemblyIdentity()
    {
        // A root-child <assemblyIdentity> in a non-asm.v1 namespace (e.g. asm.v3) does not grant
        // the assembly its SxS identity, so the required asm.v1 <assemblyIdentity> must still be added.
        var exe = CopyTestExe();
        var initCommand = GetRequiredService<InitCommand>();
        await ParseAndInvokeWithCaptureAsync(initCommand, ["--exe", exe, "--sparse", "--use-defaults", "--name", "NsApp", "--publisher", "CN=Contoso"]);
        var manifestPath = SparseManifestPath;
        var targetManifest = Path.Join(_tempDirectory.FullName, "app.manifest");
        await File.WriteAllTextAsync(targetManifest, """
            <?xml version="1.0" encoding="utf-8"?>
            <assembly xmlns="urn:schemas-microsoft-com:asm.v1" xmlns:v3="urn:schemas-microsoft-com:asm.v3" manifestVersion="1.0">
              <v3:assemblyIdentity version="1.0.0.0" name="Foreign.App" type="win32" />
            </assembly>
            """, TestContext.CancellationToken);
        var embedCommand = GetRequiredService<EmbedIdentityCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(embedCommand, [targetManifest, "--manifest", manifestPath]);

        // Assert
        Assert.AreEqual(0, exitCode);
        XNamespace asmV1 = "urn:schemas-microsoft-com:asm.v1";
        var root = XDocument.Load(targetManifest).Root!;
        var asmV1Identity = root.Elements(asmV1 + "assemblyIdentity").ToList();
        Assert.AreEqual(1, asmV1Identity.Count, "An asm.v1 <assemblyIdentity> must be added even when a foreign-namespace one is present");
        Assert.AreEqual("NsApp", asmV1Identity[0].Attribute("name")?.Value);
    }

    [TestMethod]
    public async Task EmbedIdentity_UnsupportedExtension_ReturnsError()
    {
        // Arrange: a valid sparse manifest exists, but the target is an unsupported file type.
        var exe = CopyTestExe();
        var initCommand = GetRequiredService<InitCommand>();
        await ParseAndInvokeWithCaptureAsync(initCommand, ["--exe", exe, "--sparse", "--use-defaults"]);
        var manifestPath = SparseManifestPath;
        var badTarget = Path.Join(_tempDirectory.FullName, "notes.txt");

        var embedCommand = GetRequiredService<EmbedIdentityCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(embedCommand, [badTarget, "--manifest", manifestPath]);

        // Assert
        Assert.AreEqual(1, exitCode, "An unsupported target extension should fail");
    }

    [TestMethod]
    public async Task EmbedIdentity_UnsupportedExtension_NoManifest_ReportsUnsupportedTarget()
    {
        // Reproduces the reviewer's scenario: an unsupported target in a directory with no sparse
        // manifest must report the actionable unsupported-target error, NOT a confusing
        // "AppX manifest not found". Target-type validation runs before manifest discovery.
        var isolated = Directory.CreateDirectory(Path.Join(_tempDirectory.FullName, "no-manifest"));
        var badTarget = Path.Join(isolated.FullName, "notes.txt");
        await File.WriteAllTextAsync(badTarget, "not a manifest", TestContext.CancellationToken);
        var embedCommand = GetRequiredService<EmbedIdentityCommand>();

        // Act (no --manifest, and nothing to auto-detect)
        var exitCode = await ParseAndInvokeWithCaptureAsync(embedCommand, [badTarget]);

        // Assert
        Assert.AreEqual(1, exitCode, "An unsupported target should fail even without a manifest");
        var allOutput = $"{ConsoleStdOut}{ConsoleStdErr}";
        Assert.Contains("Unsupported target", allOutput);
        Assert.IsFalse(allOutput.Contains("AppX manifest not found"), "Should not report a missing manifest for an unsupported target");
    }

    [TestMethod]
    public async Task EmbedIdentity_ManifestNotFound_ReturnsError()
    {
        // Arrange: target lives in a directory with no manifest, and there is none in cwd either.
        var isolated = Directory.CreateDirectory(Path.Join(_tempDirectory.FullName, "isolated"));
        var target = Path.Join(isolated.FullName, "app.manifest");
        var embedCommand = GetRequiredService<EmbedIdentityCommand>();

        // Act (no --manifest, nothing to auto-detect)
        var exitCode = await ParseAndInvokeWithCaptureAsync(embedCommand, [target]);

        // Assert
        Assert.AreEqual(1, exitCode, "Missing identity manifest should fail");
        Assert.IsFalse(File.Exists(target), "No SxS manifest should be written when identity can't be resolved");
    }

    [TestMethod]
    public async Task CreateSparseIdentityPackage_MissingManifest_Throws()
    {
        var msixService = GetRequiredService<IMsixService>();
        var missing = new FileInfo(Path.Join(_tempDirectory.FullName, "does-not-exist.xml"));

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            msixService.CreateSparseIdentityPackageAsync(missing, null, TestTaskContext, cancellationToken: TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task CreateSparseIdentityPackage_NonSparseManifest_Throws()
    {
        var manifestPath = new FileInfo(Path.Join(_tempDirectory.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(manifestPath.FullName, NonSparseManifest, TestContext.CancellationToken);
        var msixService = GetRequiredService<IMsixService>();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            msixService.CreateSparseIdentityPackageAsync(manifestPath, null, TestTaskContext, cancellationToken: TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task CreateSparseIdentityPackage_MissingApplication_Throws()
    {
        // MakeAppx runs with /nv, so a sparse manifest that declares AllowExternalContent but is
        // missing its <Application Id> must be rejected here rather than packed into a package that
        // deployment would later refuse.
        var manifestPath = new FileInfo(Path.Join(_tempDirectory.FullName, "appxmanifest.xml"));
        var noApp = """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
                     IgnorableNamespaces="uap10">
              <Identity Name="SparsePkg" Publisher="CN=TestPublisher" Version="1.0.0.0" ProcessorArchitecture="neutral" />
              <Properties>
                <DisplayName>SparsePkg</DisplayName>
                <PublisherDisplayName>Test</PublisherDisplayName>
                <Logo>Assets\StoreLogo.png</Logo>
                <uap10:AllowExternalContent>true</uap10:AllowExternalContent>
              </Properties>
            </Package>
            """;
        await File.WriteAllTextAsync(manifestPath.FullName, noApp, TestContext.CancellationToken);
        var msixService = GetRequiredService<IMsixService>();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            msixService.CreateSparseIdentityPackageAsync(manifestPath, null, TestTaskContext, cancellationToken: TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task CreateSparseIdentityPackage_MsixbundleOutput_Throws()
    {
        // A sparse identity package must be a single .msix, never a bundle. Passing a .msixbundle
        // output must be rejected rather than silently creating a directory with that name.
        var manifestPath = new FileInfo(Path.Join(_tempDirectory.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(manifestPath.FullName, MinimalSparseManifest, TestContext.CancellationToken);
        var output = new FileInfo(Path.Join(_tempDirectory.FullName, "SparsePkg.msixbundle"));
        var msixService = GetRequiredService<IMsixService>();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            msixService.CreateSparseIdentityPackageAsync(manifestPath, output, TestTaskContext, cancellationToken: TestContext.CancellationToken));
        Assert.IsFalse(Directory.Exists(output.FullName), "A directory must not be created for a rejected .msixbundle output");
    }

    [TestMethod]
    public async Task EmbedIdentity_NonSparseManifest_ReturnsError()
    {
        // embed-identity only applies to sparse (AllowExternalContent) packages. A full package
        // manifest must be rejected, and no SxS manifest should be written.
        var manifestPath = Path.Join(_tempDirectory.FullName, "appxmanifest.xml");
        await File.WriteAllTextAsync(manifestPath, NonSparseManifest, TestContext.CancellationToken);
        var target = Path.Join(_tempDirectory.FullName, "app.manifest");
        var embedCommand = GetRequiredService<EmbedIdentityCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(embedCommand, [target, "--manifest", manifestPath]);

        Assert.AreEqual(1, exitCode, "A non-sparse manifest should be rejected by embed-identity");
        Assert.IsFalse(File.Exists(target), "No SxS manifest should be written for a non-sparse manifest");
    }

    [TestMethod]
    public void ResolveSparseOutputPath_ExistingDottedDirectory_TreatedAsFolder()
    {
        // A directory whose name contains a dot (e.g. 'release.v2') must be treated as the output
        // folder, not misread as an invalid file extension.
        var dottedDir = Directory.CreateDirectory(Path.Join(_tempDirectory.FullName, "release.v2"));

        var (outputMsix, outputFolder) = MsixService.ResolveSparseOutputPath(
            new DirectoryInfo(dottedDir.FullName), "SparsePkg.identity.msix", _tempDirectory);

        Assert.AreEqual(dottedDir.FullName, outputFolder.FullName);
        Assert.AreEqual(Path.Join(dottedDir.FullName, "SparsePkg.identity.msix"), outputMsix.FullName);
    }

    [TestMethod]
    public void ResolveSparseOutputPath_Null_UsesCurrentDirectoryDefault()
    {
        var (outputMsix, outputFolder) = MsixService.ResolveSparseOutputPath(
            null, "SparsePkg.identity.msix", _tempDirectory);

        Assert.AreEqual(_tempDirectory.FullName, outputFolder.FullName);
        Assert.AreEqual(Path.Join(_tempDirectory.FullName, "SparsePkg.identity.msix"), outputMsix.FullName);
    }

    [TestMethod]
    public void ResolveSparseOutputPath_MsixFile_TreatedAsFile()
    {
        var target = new FileInfo(Path.Join(_tempDirectory.FullName, "custom.msix"));

        var (outputMsix, outputFolder) = MsixService.ResolveSparseOutputPath(
            target, "SparsePkg.identity.msix", _tempDirectory);

        Assert.AreEqual(target.FullName, outputMsix.FullName);
        Assert.AreEqual(_tempDirectory.FullName, outputFolder.FullName);
    }

    [TestMethod]
    public void ResolveSparseOutputPath_MsixbundleFile_Throws()
    {
        var target = new FileInfo(Path.Join(_tempDirectory.FullName, "custom.msixbundle"));

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            MsixService.ResolveSparseOutputPath(target, "SparsePkg.identity.msix", _tempDirectory));
    }

    [TestMethod]
    public async Task GenerateCompleteManifest_EscapesSpecialCharsInDescriptionAndExe()
    {
        // Description is free text inferred from exe metadata; exe names can contain '&'.
        // Both must be XML-escaped so the generated manifest stays well-formed.
        var templateService = GetRequiredService<IManifestTemplateService>();
        await templateService.GenerateCompleteManifestAsync(
            _tempDirectory,
            "EscapeTest",
            "CN=Test",
            "1.0.0.0",
            ManifestTemplates.Sparse,
            "Tom & Jerry's <app> \"quoted\"",
            TestTaskContext,
            manifestFileName: "appxmanifest.xml",
            executableName: "a&b.exe",
            cancellationToken: TestContext.CancellationToken);

        var content = await File.ReadAllTextAsync(Path.Join(_tempDirectory.FullName, "appxmanifest.xml"), TestContext.CancellationToken);

        // The manifest must parse as well-formed XML despite the special characters.
        var doc = System.Xml.Linq.XDocument.Parse(content);
        Assert.IsNotNull(doc.Root);
        Assert.Contains("&amp;", content, "Ampersands must be escaped");
        Assert.DoesNotContain("Tom & Jerry", content, "A raw, unescaped ampersand would be invalid XML");
    }

    [TestMethod]
    public void NormalizeManifestVersion_HandlesVariousInputs()
    {
        Assert.AreEqual("1.2.3.4", ManifestService.NormalizeManifestVersion("1.2.3.4"));
        Assert.AreEqual("2.0.0.0", ManifestService.NormalizeManifestVersion("2.0"));
        Assert.AreEqual("1.0.0.0", ManifestService.NormalizeManifestVersion("1.0.0.0"));
        Assert.IsNull(ManifestService.NormalizeManifestVersion("not-a-version"));
        Assert.IsNull(ManifestService.NormalizeManifestVersion(null));
        Assert.IsNull(ManifestService.NormalizeManifestVersion(""));

        // A five-part value must be rejected outright rather than silently truncated to "1.2.3.4",
        // so the caller falls back to a packable default instead of packaging a version the user
        // never entered.
        Assert.IsNull(ManifestService.NormalizeManifestVersion("1.2.3.4.5"));

        // MSIX Identity/@Version components are 16-bit; out-of-range values must be rejected so the
        // caller falls back to a packable default instead of emitting a manifest MakeAppx rejects.
        Assert.AreEqual("65535.65535.65535.65535", ManifestService.NormalizeManifestVersion("65535.65535.65535.65535"));
        Assert.IsNull(ManifestService.NormalizeManifestVersion("70000.0.0.0"));
        Assert.IsNull(ManifestService.NormalizeManifestVersion("1.65536.0.0"));

        // FileVersionInfo.FileVersion is often decorated (e.g. notepad.exe reports
        // "10.0.26100.32860 (WinBuild.160101.0800)"). The leading numeric token must be parsed so
        // common executables get their real inferred version instead of the 1.0.0.0 fallback.
        Assert.AreEqual("10.0.26100.32860", ManifestService.NormalizeManifestVersion("10.0.26100.32860 (WinBuild.160101.0800)"));
        Assert.AreEqual("1.2.0.0", ManifestService.NormalizeManifestVersion("1.2 beta"));
    }

    // A sparse manifest as produced by an older template / hand-edit: RuntimeBehavior is the
    // incorrect "packagedClassicApp", the Identity has no ProcessorArchitecture, the
    // TargetDeviceFamily MinVersion predates the 10.0.19041.0 AllowExternalContent requires, and a
    // stray EntryPoint is present.
    private const string StaleSparseManifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
                 IgnorableNamespaces="uap10">
          <Identity Name="SparsePkg" Publisher="CN=TestPublisher" Version="1.0.0.0" />
          <Properties>
            <DisplayName>SparsePkg</DisplayName>
            <PublisherDisplayName>Test</PublisherDisplayName>
            <Logo>Assets\StoreLogo.png</Logo>
            <uap10:AllowExternalContent>true</uap10:AllowExternalContent>
          </Properties>
          <Dependencies>
            <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.18362.0" MaxVersionTested="10.0.19041.0" />
          </Dependencies>
          <Applications>
            <Application Id="SparsePkg" Executable="app.exe" EntryPoint="app.exe" uap10:RuntimeBehavior="packagedClassicApp" />
          </Applications>
        </Package>
        """;

    [TestMethod]
    public void NormalizeSparseIdentityManifest_StaleManifest_CorrectsFields()
    {
        var result = MsixService.NormalizeSparseIdentityManifest(StaleSparseManifest, out var corrections);
        var doc = XDocument.Parse(result);
        XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        XNamespace uap10 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";

        var app = doc.Root!.Element(ns + "Applications")!.Element(ns + "Application")!;
        Assert.AreEqual("win32App", app.Attribute(uap10 + "RuntimeBehavior")?.Value, "RuntimeBehavior should be corrected");
        Assert.AreEqual("mediumIL", app.Attribute(uap10 + "TrustLevel")?.Value, "TrustLevel should be set");
        Assert.IsNull(app.Attribute("EntryPoint"), "EntryPoint should be removed");

        var identity = doc.Root!.Element(ns + "Identity")!;
        Assert.AreEqual("neutral", identity.Attribute("ProcessorArchitecture")?.Value, "Missing ProcessorArchitecture should default to neutral");

        var tdf = doc.Root!.Element(ns + "Dependencies")!.Element(ns + "TargetDeviceFamily")!;
        Assert.AreEqual("10.0.19041.0", tdf.Attribute("MinVersion")?.Value, "MinVersion below the floor should be raised");

        Assert.IsFalse(corrections.Count == 0, "Corrections should be reported for a stale manifest");
    }

    [TestMethod]
    public void NormalizeSparseIdentityManifest_CorrectManifest_IsNoOp()
    {
        var result = MsixService.NormalizeSparseIdentityManifest(MinimalSparseManifest, out var corrections);
        var doc = XDocument.Parse(result);
        XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        XNamespace uap10 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";

        var app = doc.Root!.Element(ns + "Applications")!.Element(ns + "Application")!;
        Assert.AreEqual("win32App", app.Attribute(uap10 + "RuntimeBehavior")?.Value);
        Assert.AreEqual("mediumIL", app.Attribute(uap10 + "TrustLevel")?.Value);
        Assert.AreEqual("neutral", doc.Root!.Element(ns + "Identity")!.Attribute("ProcessorArchitecture")?.Value);

        Assert.IsEmpty(corrections, "An already-correct manifest should produce no corrections");
    }

    [TestMethod]
    public void GetSparseFolderContentWarnings_SparseManifest_WarnsOnAssetsAndBinaries()
    {
        var folder = _tempDirectory.CreateSubdirectory("sparse-folder");
        File.WriteAllText(Path.Join(folder.FullName, "StoreLogo.png"), "png");
        File.WriteAllText(Path.Join(folder.FullName, "app.exe"), "MZ");

        var warnings = MsixService.GetSparseFolderContentWarnings(folder, MinimalSparseManifest);

        Assert.HasCount(2, warnings);
        Assert.IsTrue(warnings.Any(w => w.Contains("Assets found")), "Expected an assets warning");
        Assert.IsTrue(warnings.Any(w => w.Contains("Binaries found")), "Expected a binaries warning");
    }

    [TestMethod]
    public void GetSparseFolderContentWarnings_OnlyManifest_NoWarnings()
    {
        var folder = _tempDirectory.CreateSubdirectory("manifest-only");
        File.WriteAllText(Path.Join(folder.FullName, "appxmanifest.xml"), MinimalSparseManifest);

        var warnings = MsixService.GetSparseFolderContentWarnings(folder, MinimalSparseManifest);

        Assert.IsEmpty(warnings);
    }

    [TestMethod]
    public void GetSparseFolderContentWarnings_NonSparseManifest_NoWarnings()
    {
        var folder = _tempDirectory.CreateSubdirectory("non-sparse");
        File.WriteAllText(Path.Join(folder.FullName, "StoreLogo.png"), "png");
        File.WriteAllText(Path.Join(folder.FullName, "app.exe"), "MZ");

        // Not a sparse manifest, so folder content warnings must not fire.
        var warnings = MsixService.GetSparseFolderContentWarnings(folder, NonSparseManifest);

        Assert.IsEmpty(warnings);
    }
}

/// <summary>
/// Tests for sparse-aware routing in the pack command. Uses a fake MSIX service to verify the
/// command dispatches to the sparse identity path without invoking real build tools.
/// </summary>
[TestClass]
public class SparsePackRoutingTests : BaseCommandTests
{
    private FakeMsixService _fakeMsixService = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeMsixService = new FakeMsixService();
        return services.AddSingleton<IMsixService>(_fakeMsixService);
    }

    private const string SparseManifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                 xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
                 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
                 IgnorableNamespaces="uap uap10 rescap">
          <Identity Name="SparsePkg" Publisher="CN=TestPublisher" Version="1.0.0.0" ProcessorArchitecture="neutral" />
          <Properties>
            <DisplayName>SparsePkg</DisplayName>
            <PublisherDisplayName>Test</PublisherDisplayName>
            <Logo>Assets\StoreLogo.png</Logo>
            <uap10:AllowExternalContent>true</uap10:AllowExternalContent>
          </Properties>
          <Dependencies>
            <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" />
          </Dependencies>
          <Applications>
            <Application Id="SparsePkg" Executable="app.exe" uap10:RuntimeBehavior="win32App" uap10:TrustLevel="mediumIL" />
          </Applications>
          <Capabilities><rescap:Capability Name="runFullTrust" /></Capabilities>
        </Package>
        """;

    private const string PackagedManifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
                 IgnorableNamespaces="uap rescap">
          <Identity Name="FullPkg" Publisher="CN=TestPublisher" Version="1.0.0.0" />
          <Properties>
            <DisplayName>FullPkg</DisplayName>
            <PublisherDisplayName>Test</PublisherDisplayName>
            <Logo>Assets\StoreLogo.png</Logo>
          </Properties>
          <Dependencies>
            <TargetDeviceFamily Name="Windows.Universal" MinVersion="10.0.18362.0" MaxVersionTested="10.0.26100.0" />
          </Dependencies>
          <Applications>
            <Application Id="FullPkg" Executable="app.exe" EntryPoint="Windows.FullTrustApplication" />
          </Applications>
          <Capabilities><rescap:Capability Name="runFullTrust" /></Capabilities>
        </Package>
        """;

    [TestMethod]
    public async Task Pack_SparseManifestFile_RoutesToSparseIdentityPath()
    {
        // Arrange
        var manifestPath = Path.Join(_tempDirectory.FullName, "appxmanifest.xml");
        await File.WriteAllTextAsync(manifestPath, SparseManifest, TestContext.CancellationToken);
        var packageCommand = GetRequiredService<PackageCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(packageCommand, [manifestPath]);

        // Assert
        Assert.AreEqual(0, exitCode, "Sparse pack should succeed");
        Assert.AreEqual(1, _fakeMsixService.CreateSparseIdentityCalls.Count, "Should route to CreateSparseIdentityPackageAsync");
        Assert.IsFalse(_fakeMsixService.CreateSparseIdentityCalls[0].AutoSign, "No cert provided means no signing");
    }

    [TestMethod]
    public async Task Pack_SparseManifestFile_WithInapplicableOption_ReturnsError()
    {
        // Arrange
        var manifestPath = Path.Join(_tempDirectory.FullName, "appxmanifest.xml");
        await File.WriteAllTextAsync(manifestPath, SparseManifest, TestContext.CancellationToken);
        var packageCommand = GetRequiredService<PackageCommand>();

        // Act — --name does not apply to identity-only packaging
        var exitCode = await ParseAndInvokeWithCaptureAsync(packageCommand, [manifestPath, "--name", "Ignored"]);

        // Assert
        Assert.AreEqual(1, exitCode, "Passing an inapplicable option should fail rather than silently ignore it");
        Assert.AreEqual(0, _fakeMsixService.CreateSparseIdentityCalls.Count, "Should not route to sparse path when options are invalid");
    }

    [TestMethod]
    public async Task Pack_NonSparseManifestFile_ReturnsError()
    {
        // Arrange
        var manifestPath = Path.Join(_tempDirectory.FullName, "appxmanifest.xml");
        await File.WriteAllTextAsync(manifestPath, PackagedManifest, TestContext.CancellationToken);
        var packageCommand = GetRequiredService<PackageCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(packageCommand, [manifestPath]);

        // Assert
        Assert.AreEqual(1, exitCode, "Passing a non-sparse manifest file should fail");
        Assert.AreEqual(0, _fakeMsixService.CreateSparseIdentityCalls.Count, "Should not route to sparse path");
    }

    [TestMethod]
    public async Task Pack_SparseManifestFile_WithCert_Signs()
    {
        // Arrange
        var manifestPath = Path.Join(_tempDirectory.FullName, "appxmanifest.xml");
        await File.WriteAllTextAsync(manifestPath, SparseManifest, TestContext.CancellationToken);
        var certPath = Path.Join(_tempDirectory.FullName, "dev.pfx");
        await File.WriteAllTextAsync(certPath, "not-a-real-cert", TestContext.CancellationToken);
        var packageCommand = GetRequiredService<PackageCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(packageCommand, [manifestPath, "--cert", certPath]);

        // Assert
        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeMsixService.CreateSparseIdentityCalls.Count);
        Assert.IsTrue(_fakeMsixService.CreateSparseIdentityCalls[0].AutoSign, "Providing --cert should request signing");
    }

    [TestMethod]
    public async Task Pack_MissingManifestFile_ReportsMissingManifestNotFolder()
    {
        // A path that looks like a manifest file (.xml/.appxmanifest) but doesn't exist should be
        // reported as a missing manifest file with the sparse-init hint — not "input folder not found".
        var packageCommand = GetRequiredService<PackageCommand>();
        var missing = Path.Join(_tempDirectory.FullName, "does-not-exist.appxmanifest.xml");

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(packageCommand, [missing]);

        // Assert
        Assert.AreEqual(1, exitCode, "A missing manifest file should fail");
        Assert.AreEqual(0, _fakeMsixService.CreateSparseIdentityCalls.Count, "Should not route to the sparse path");
        var output = ConsoleStdOut.ToString() + ConsoleStdErr;
        Assert.Contains("Manifest file not found", output, "A missing manifest path should be reported as a missing file");
    }

    [TestMethod]
    public async Task Pack_MalformedManifestFile_ReportsParseErrorNotMissingElement()
    {
        // A manifest-named file that contains the literal "AllowExternalContent" but is not valid
        // XML must be reported as a parse failure — NOT misreported as "missing AllowExternalContent"
        // (which would hide the real syntax error and mislead the user).
        var packageCommand = GetRequiredService<PackageCommand>();
        var malformed = Path.Join(_tempDirectory.FullName, "appxmanifest.xml");
        await File.WriteAllTextAsync(malformed, "<Package><Properties><AllowExternalContent>true</AllowExternalContent></Package>");

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(packageCommand, [malformed]);

        // Assert
        Assert.AreEqual(1, exitCode, "A malformed manifest should fail");
        Assert.AreEqual(0, _fakeMsixService.CreateSparseIdentityCalls.Count, "Should not route to the sparse path");
        var output = ConsoleStdOut.ToString() + ConsoleStdErr;
        Assert.Contains("could not be read as valid XML", output, "A malformed manifest should be reported as a parse error");
        Assert.DoesNotContain("missing uap10:AllowExternalContent", output, "A parse failure must not be reported as a missing element");
    }
}

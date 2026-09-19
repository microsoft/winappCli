// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Unit tests for the pure/offline helpers in <c>MsixService.cs</c> that do not require the
/// external SDK build tools: manifest identity parsing, the manifest-referenced-file staging
/// helper, and the dotnet package-list fetch. External tools are never invoked here — the
/// static helpers are exercised directly (via reflection for private members) and the dotnet
/// service is faked.
/// </summary>
[TestClass]
public class MsixServicePackagingTests : BaseCommandTests
{
    private MsixService _msixService = null!;
    private FakeDotNetService _fakeDotNet = null!;
    private FakePriService _fakePri = null!;

    private static readonly MethodInfo CopyManifestReferencedFilesMethod =
        typeof(MsixService).GetMethod("CopyManifestReferencedFiles", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo FetchDotNetPackageListMethod =
        typeof(MsixService).GetMethod("FetchDotNetPackageListAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo CopyDirectoryRecursiveMethod =
        typeof(MsixService).GetMethod("CopyDirectoryRecursive", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo ResolveResourceLanguageXGenerateMethod =
        typeof(MsixService).GetMethod("ResolveResourceLanguageXGenerateAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo TryDeleteFileMethod =
        typeof(MsixService).GetMethod("TryDeleteFile", BindingFlags.NonPublic | BindingFlags.Static)!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeDotNet = new FakeDotNetService();
        _fakePri = new FakePriService();
        return services
            .AddSingleton<IDotNetService>(_fakeDotNet)
            .AddSingleton<IPriService>(_fakePri);
    }

    [TestInitialize]
    public void SetupService()
    {
        _msixService = (MsixService)GetRequiredService<IMsixService>();
    }

    // ---- ParseAppxManifestFromPathAsync -------------------------------------------

    [TestMethod]
    public async Task ParseAppxManifestFromPathAsync_NonexistentFile_ThrowsFileNotFoundException()
    {
        var missing = new FileInfo(Path.Combine(_tempDirectory.FullName, "does-not-exist.xml"));

        var ex = await Assert.ThrowsExactlyAsync<FileNotFoundException>(
            () => MsixService.ParseAppxManifestFromPathAsync(missing, TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "AppX manifest not found");
    }

    [TestMethod]
    public async Task ParseAppxManifestFromPathAsync_ValidManifest_ReturnsIdentity()
    {
        const string manifest =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="Contoso.MyApp" Publisher="CN=Contoso" Version="1.2.3.0" />
              <Applications>
                <Application Id="MyApp" Executable="MyApp.exe" />
              </Applications>
            </Package>
            """;
        var manifestPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(manifestPath.FullName, manifest, TestContext.CancellationToken);

        var result = await MsixService.ParseAppxManifestFromPathAsync(manifestPath, TestContext.CancellationToken);

        Assert.AreEqual("Contoso.MyApp", result.PackageName);
        Assert.AreEqual("CN=Contoso", result.Publisher);
        Assert.AreEqual("MyApp", result.ApplicationId);
    }

    // ---- ExtractPublisherFromPathAsync --------------------------------------------

    [TestMethod]
    public async Task ExtractPublisherFromPathAsync_NonexistentFile_ThrowsFileNotFoundException()
    {
        var missing = new FileInfo(Path.Combine(_tempDirectory.FullName, "does-not-exist.xml"));

        var ex = await Assert.ThrowsExactlyAsync<FileNotFoundException>(
            () => MsixService.ExtractPublisherFromPathAsync(missing, TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "AppX manifest not found");
    }

    [TestMethod]
    public async Task ExtractPublisherFromPathAsync_ManifestWithoutApplications_ReturnsPublisher()
    {
        // A partially-complete manifest (valid Identity/@Publisher, no Applications/Name) must still
        // yield the publisher — unlike ParseAppxManifestFromPathAsync, which requires an Application
        // element (issue #839).
        const string manifest =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="FlowHarnessApp" Publisher="CN=FlowHarnessPublisher, O=Fabrikam Inc, C=US" Version="1.0.0.0" />
            </Package>
            """;
        var manifestPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "Partial.appxmanifest"));
        await File.WriteAllTextAsync(manifestPath.FullName, manifest, TestContext.CancellationToken);

        var publisher = await MsixService.ExtractPublisherFromPathAsync(manifestPath, TestContext.CancellationToken);

        Assert.AreEqual("CN=FlowHarnessPublisher, O=Fabrikam Inc, C=US", publisher);
    }

    [TestMethod]
    public async Task ExtractPublisherFromPathAsync_MissingPublisher_ThrowsInvalidOperationException()
    {
        const string manifest =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="FlowHarnessApp" Version="1.0.0.0" />
            </Package>
            """;
        var manifestPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "NoPublisher.appxmanifest"));
        await File.WriteAllTextAsync(manifestPath.FullName, manifest, TestContext.CancellationToken);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => MsixService.ExtractPublisherFromPathAsync(manifestPath, TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "Publisher");
    }

    [TestMethod]
    public async Task ExtractPublisherFromPathAsync_EmptyPublisher_ThrowsInvalidOperationException()
    {
        // An empty/whitespace Publisher would otherwise slip through and surface a generic downstream
        // "Publisher name cannot be empty" error rather than the actionable manifest guidance.
        const string manifest =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="FlowHarnessApp" Publisher="   " Version="1.0.0.0" />
            </Package>
            """;
        var manifestPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "EmptyPublisher.appxmanifest"));
        await File.WriteAllTextAsync(manifestPath.FullName, manifest, TestContext.CancellationToken);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => MsixService.ExtractPublisherFromPathAsync(manifestPath, TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "Publisher");
    }

    // ---- CopyManifestReferencedFiles ----------------------------------------------

    [TestMethod]
    public void CopyManifestReferencedFiles_EmptySet_DoesNothing()
    {
        var stagingDir = _tempDirectory.CreateSubdirectory("staging-empty");

        CopyManifestReferencedFilesMethod.Invoke(null,
        [
            new HashSet<string>(),
            _tempDirectory,
            _tempDirectory,
            stagingDir,
            TestTaskContext,
            CancellationToken.None,
        ]);

        Assert.AreEqual(0, stagingDir.EnumerateFileSystemInfos().Count(), "Nothing should be staged for an empty reference set");
    }

    [TestMethod]
    public void CopyManifestReferencedFiles_CopiesFromRoots_SkipsPresentAndMissing()
    {
        var manifestDir = _tempDirectory.CreateSubdirectory("manifest-dir");
        var inputFolder = _tempDirectory.CreateSubdirectory("input-dir");
        var stagingDir = _tempDirectory.CreateSubdirectory("staging-dir");

        // Present in the manifest directory → copied from there.
        File.WriteAllText(Path.Combine(manifestDir.FullName, "from-manifest.txt"), "manifest-copy");
        // Present only in the input folder → copied from there.
        File.WriteAllText(Path.Combine(inputFolder.FullName, "from-input.txt"), "input-copy");
        // Already present in staging → left untouched (not overwritten).
        File.WriteAllText(Path.Combine(stagingDir.FullName, "already-staged.txt"), "original-staged");
        File.WriteAllText(Path.Combine(manifestDir.FullName, "already-staged.txt"), "should-not-overwrite");

        var referencedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "from-manifest.txt",
            "from-input.txt",
            "already-staged.txt",
            "missing-everywhere.txt",
        };

        CopyManifestReferencedFilesMethod.Invoke(null,
        [
            referencedFiles,
            manifestDir,
            inputFolder,
            stagingDir,
            TestTaskContext,
            CancellationToken.None,
        ]);

        Assert.AreEqual("manifest-copy", File.ReadAllText(Path.Combine(stagingDir.FullName, "from-manifest.txt")));
        Assert.AreEqual("input-copy", File.ReadAllText(Path.Combine(stagingDir.FullName, "from-input.txt")));
        Assert.AreEqual("original-staged", File.ReadAllText(Path.Combine(stagingDir.FullName, "already-staged.txt")),
            "Files already present in staging must not be overwritten");
        Assert.IsFalse(File.Exists(Path.Combine(stagingDir.FullName, "missing-everywhere.txt")),
            "Files missing from all roots are skipped");
    }

    [TestMethod]
    public void CopyManifestReferencedFiles_PathEscapingReferenceWithRealSource_IsBlockedByGuardWithWarning()
    {
        // Staging is nested one level deeper than the manifest dir so the escaping DESTINATION
        // (stage\pwned.txt) is DISTINCT from the real SOURCE the reference resolves to
        // (temp\pwned.txt). That models a manifest referencing an out-of-tree path that actually
        // exists: absent the destination-escape guard the method would try to copy a real file to a
        // location OUTSIDE stagingDir.
        var manifestDir = _tempDirectory.CreateSubdirectory("mani");
        var inputFolder = _tempDirectory.CreateSubdirectory("inp");
        var stagingParent = _tempDirectory.CreateSubdirectory("stage");
        var stagingDir = stagingParent.CreateSubdirectory("inner");

        var escapingRef = Path.Combine("..", "pwned.txt");
        // manifestDir\..\pwned.txt == _tempDirectory\pwned.txt — a real, populated source file.
        var realSource = Path.Combine(_tempDirectory.FullName, "pwned.txt");
        File.WriteAllText(realSource, "sensitive");
        // stagingDir\..\pwned.txt == stage\pwned.txt — the escaping destination that must never be written.
        var escapedDestination = Path.Combine(stagingParent.FullName, "pwned.txt");

        var referencedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { escapingRef };

        CopyManifestReferencedFilesMethod.Invoke(null,
        [
            referencedFiles,
            manifestDir,
            inputFolder,
            stagingDir,
            TestTaskContext,
            CancellationToken.None,
        ]);

        // The destination-escape guard runs BEFORE the source lookup and skips with a distinct
        // "path escape" warning. (A separate source-containment guard is a backstop, so proving THIS
        // guard fired requires asserting its specific message, not merely that no file escaped.)
        var messages = TestTask.SubTasks.OfType<StatusMessageTask>().Select(t => t.CompletedMessage ?? string.Empty).ToList();
        Assert.IsTrue(
            messages.Any(m => m.Contains("path escape", StringComparison.OrdinalIgnoreCase) && m.Contains("pwned.txt", StringComparison.OrdinalIgnoreCase)),
            $"The path-escape guard should warn about the escaping reference. Messages:\n{string.Join("\n", messages)}");
        Assert.IsFalse(File.Exists(escapedDestination), "A path-escaping reference must never be written outside the staging directory");
        Assert.AreEqual("sensitive", File.ReadAllText(realSource), "The real source file must be left untouched");
    }

    // ---- FetchDotNetPackageListAsync ----------------------------------------------

    [TestMethod]
    public async Task FetchDotNetPackageList_NoCsproj_ReturnsNull()
    {
        // _tempDirectory (the current directory) contains no .csproj.
        var result = await InvokeFetchDotNetPackageListAsync();

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task FetchDotNetPackageList_WithCsproj_ReturnsFakeList()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_tempDirectory.FullName, "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0-windows10.0.19041.0</TargetFramework></PropertyGroup></Project>",
            TestContext.CancellationToken);

        _fakeDotNet.PackageListResult = new DotNetPackageListJson(
        [
            new DotNetProject(
            [
                new DotNetFramework("net8.0-windows10.0.19041.0",
                    [new DotNetPackage("Microsoft.WindowsAppSDK", "1.6.0", "1.6.0")],
                    []),
            ]),
        ]);

        var result = await InvokeFetchDotNetPackageListAsync();

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.Projects.Count);
        Assert.AreEqual("Microsoft.WindowsAppSDK", result.Projects[0].Frameworks[0].TopLevelPackages[0].Id);
    }

    private async Task<DotNetPackageListJson?> InvokeFetchDotNetPackageListAsync()
    {
        var task = (Task<DotNetPackageListJson?>)FetchDotNetPackageListMethod.Invoke(
            _msixService, [CancellationToken.None])!;
        return await task;
    }

    // ---- CopyDirectoryRecursive ---------------------------------------------------

    [TestMethod]
    public void CopyDirectoryRecursive_ExcludedTopLevelDirectory_IsSkipped()
    {
        var source = _tempDirectory.CreateSubdirectory("copy-src");
        File.WriteAllText(Path.Combine(source.FullName, "root.txt"), "root");
        var keep = source.CreateSubdirectory("keep");
        File.WriteAllText(Path.Combine(keep.FullName, "nested.txt"), "nested");
        var excluded = source.CreateSubdirectory("node_modules");
        File.WriteAllText(Path.Combine(excluded.FullName, "junk.txt"), "junk");

        var destination = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "copy-dest"));

        CopyDirectoryRecursiveMethod.Invoke(null,
        [
            source,
            destination,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node_modules" },
        ]);

        Assert.IsTrue(File.Exists(Path.Combine(destination.FullName, "root.txt")),
            "Top-level files are copied");
        Assert.IsTrue(File.Exists(Path.Combine(destination.FullName, "keep", "nested.txt")),
            "Non-excluded subdirectories are copied recursively");
        Assert.IsFalse(Directory.Exists(Path.Combine(destination.FullName, "node_modules")),
            "Excluded top-level directories must be skipped");
    }

    // ---- CreateMsixPackageAsync: manifest resolution ------------------------------

    [TestMethod]
    public async Task CreateMsixPackageAsync_ExplicitManifestPathMissing_ThrowsFileNotFound()
    {
        var inputFolder = _tempDirectory.CreateSubdirectory("pkg-input");
        var missingManifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "does-not-exist-manifest.xml"));

        var ex = await Assert.ThrowsExactlyAsync<FileNotFoundException>(
            () => _msixService.CreateMsixPackageAsync(
                inputFolder: inputFolder,
                outputPath: _tempDirectory,
                TestTaskContext,
                manifestPath: missingManifest,
                cancellationToken: TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "Manifest file not found");
    }

    // ---- ResolveResourceLanguageXGenerateAsync ------------------------------------

    [TestMethod]
    public async Task ResolveResourceLanguageXGenerate_NoXGenerate_ReturnsManifestUnchanged()
    {
        const string manifest =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="Contoso.MyApp" Publisher="CN=Contoso" Version="1.0.0.0" />
              <Resources>
                <Resource Language="en-US" />
              </Resources>
            </Package>
            """;

        var result = await InvokeResolveResourceLanguageXGenerateAsync(manifest, _tempDirectory);

        Assert.AreEqual(manifest, result, "Manifests without x-generate are returned untouched");
        Assert.AreEqual(0, _fakePri.ExtractLanguagesCallCount, "PRI should not be inspected when there is no x-generate");
    }

    [TestMethod]
    public async Task ResolveResourceLanguageXGenerate_WithPriLanguages_ReplacesWithConcreteLanguages()
    {
        const string manifest =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="Contoso.MyApp" Publisher="CN=Contoso" Version="1.0.0.0" />
              <Resources>
                <Resource Language="x-generate" />
              </Resources>
            </Package>
            """;
        var inputFolder = _tempDirectory.CreateSubdirectory("xgen-with-pri");
        // A resources.pri file must exist for the PRI language-extraction branch to run.
        await File.WriteAllTextAsync(Path.Combine(inputFolder.FullName, "resources.pri"), "binary-pri", TestContext.CancellationToken);
        _fakePri.LanguagesToReturn = ["en-US", "fr-FR"];

        var result = await InvokeResolveResourceLanguageXGenerateAsync(manifest, inputFolder);

        Assert.AreEqual(1, _fakePri.ExtractLanguagesCallCount, "Existing resources.pri should be inspected for languages");
        StringAssert.Contains(result, "en-US");
        StringAssert.Contains(result, "fr-FR");
        Assert.IsFalse(result.Contains("x-generate"), "x-generate should be replaced with concrete languages");
    }

    [TestMethod]
    public async Task ResolveResourceLanguageXGenerate_NoPriFile_FallsBackToEnUs()
    {
        const string manifest =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="Contoso.MyApp" Publisher="CN=Contoso" Version="1.0.0.0" />
              <Resources>
                <Resource Language="x-generate" />
              </Resources>
            </Package>
            """;
        // No resources.pri present in this input folder.
        var inputFolder = _tempDirectory.CreateSubdirectory("xgen-no-pri");

        var result = await InvokeResolveResourceLanguageXGenerateAsync(manifest, inputFolder);

        Assert.AreEqual(0, _fakePri.ExtractLanguagesCallCount, "No PRI means no language extraction");
        StringAssert.Contains(result, "en-US");
        Assert.IsFalse(result.Contains("x-generate"), "x-generate should fall back to en-US");
    }

    private async Task<string> InvokeResolveResourceLanguageXGenerateAsync(string manifestContent, DirectoryInfo inputFolder)
    {
        var task = (Task<string>)ResolveResourceLanguageXGenerateMethod.Invoke(
            _msixService,
            [manifestContent, inputFolder, TestTaskContext, CancellationToken.None])!;
        return await task;
    }

    // ---- TryDeleteFile ------------------------------------------------------------

    [TestMethod]
    public void TryDeleteFile_DeletesExistingFile()
    {
        var target = new FileInfo(Path.Combine(_tempDirectory.FullName, "to-delete.txt"));
        File.WriteAllText(target.FullName, "content");

        TryDeleteFileMethod.Invoke(null, [target]);

        target.Refresh();
        Assert.IsFalse(target.Exists, "TryDeleteFile should remove an existing file");
    }

    [TestMethod]
    public void TryDeleteFile_LockedFile_SwallowsException()
    {
        var target = new FileInfo(Path.Combine(_tempDirectory.FullName, "locked.txt"));
        File.WriteAllText(target.FullName, "content");

        // Hold an exclusive handle so Delete() throws IOException; TryDeleteFile must swallow it.
        using (var handle = new FileStream(target.FullName, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            TryDeleteFileMethod.Invoke(null, [target]);
        }

        target.Refresh();
        Assert.IsTrue(target.Exists, "The locked file remains, but no exception should escape TryDeleteFile");
    }

    [TestMethod]
    public void BuildDefaultMsixFileName_ArchitectureAndVersion_UsesNameVersionArch()
    {
        var name = MsixService.BuildDefaultMsixFileName("MyApp", "x64", "1.2.3.0");
        Assert.AreEqual("MyApp_1.2.3.0_x64.msix", name);
    }

    [TestMethod]
    public void BuildDefaultMsixFileName_VersionOnly_UsesNameVersion()
    {
        var name = MsixService.BuildDefaultMsixFileName("MyApp", null, "1.2.3.0");
        Assert.AreEqual("MyApp_1.2.3.0.msix", name);
    }

    [TestMethod]
    public void BuildDefaultMsixFileName_ArchitectureWithoutVersion_UsesNameArch()
    {
        // Version null → the (not null, _) arm; the arch-only file name is used.
        Assert.AreEqual("MyApp_arm64.msix", MsixService.BuildDefaultMsixFileName("MyApp", "arm64", null));

        // Whitespace version is treated as "no version" and also falls through to the arch-only arm.
        Assert.AreEqual("MyApp_arm64.msix", MsixService.BuildDefaultMsixFileName("MyApp", "arm64", "   "));
    }

    [TestMethod]
    public void BuildDefaultMsixFileName_NoArchitectureNoVersion_UsesNameOnly()
    {
        Assert.AreEqual("MyApp.msix", MsixService.BuildDefaultMsixFileName("MyApp", null, null));

        // Whitespace version with no architecture also falls through to the name-only arm.
        Assert.AreEqual("MyApp.msix", MsixService.BuildDefaultMsixFileName("MyApp", null, " "));
    }
}

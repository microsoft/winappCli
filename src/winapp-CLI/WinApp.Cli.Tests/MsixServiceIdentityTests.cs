// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="MsixService"/> identity workflows (sparse / loose-layout registration)
/// defined in <c>MsixService.Identity.cs</c>. External SDK tools and package registration are
/// faked; no real makeappx/makepri/Add-AppxPackage is invoked.
/// </summary>
[TestClass]
public class MsixServiceIdentityTests : BaseCommandTests
{
    private MsixService _msixService = null!;
    private FakePackageRegistrationService _fakeRegistration = null!;
    private FakeDevModeService _fakeDevMode = null!;
    private ScriptedMtBuildToolsService _fakeBuildTools = null!;
    private FakeWindowsAppRuntimeService _fakeWindowsAppRuntime = null!;
    private FakeDotNetService _fakeDotNet = null!;

    private static readonly MethodInfo CopyFilesFromRecipeMethod =
        typeof(MsixService).GetMethod("CopyFilesFromRecipeAsync", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static object Reconciliation(string name) => Enum.Parse<LayoutReconciliation>(name);

    private static readonly MethodInfo SyncFilesToOutputMethod =
        typeof(MsixService).GetMethod("SyncFilesToOutputDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo EmbedManifestFileToExeMethod =
        typeof(MsixService).GetMethod("EmbedManifestFileToExeAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly string[] LegacyTempManifestNames =
        ["temp_extracted.manifest", "merged.manifest", "msix_identity_temp.manifest"];

    /// <summary>Everything a minimal recipe stages, and nothing else.</summary>
    private static readonly string[] PayloadOnlyLayout = ["appxmanifest.xml", "TestApp.dll"];

    private static readonly MethodInfo RemoveMsixElementsMethod =
        typeof(MsixService).GetMethod("RemoveMsixElements", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo EnsureWindowsAppRuntimeInstalledMethod =
        typeof(MsixService).GetMethod("EnsureWindowsAppRuntimeInstalledAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeRegistration = new FakePackageRegistrationService();
        _fakeDevMode = new FakeDevModeService();
        _fakeBuildTools = new ScriptedMtBuildToolsService();
        _fakeWindowsAppRuntime = new FakeWindowsAppRuntimeService();
        _fakeDotNet = new FakeDotNetService();

        return services
            .AddSingleton<IPackageRegistrationService>(_fakeRegistration)
            .AddSingleton<IDevModeService>(_fakeDevMode)
            .AddSingleton<IBuildToolsService>(_fakeBuildTools)
            .AddSingleton<IWindowsAppRuntimeService>(_fakeWindowsAppRuntime)
            .AddSingleton<IDotNetService>(_fakeDotNet)
            .AddSingleton<INugetService, FakeNugetService>();
    }

    [TestInitialize]
    public void SetupService()
    {
        _msixService = (MsixService)GetRequiredService<IMsixService>();
    }

    // ---- Manifest / recipe helpers ------------------------------------------------

    private const string ManifestNamespaces =
        "xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\" " +
        "xmlns:uap=\"http://schemas.microsoft.com/appx/manifest/uap/windows10\" " +
        "xmlns:build=\"http://schemas.microsoft.com/developer/appx/2015/build\" " +
        "IgnorableNamespaces=\"uap build\"";

    /// <summary>An MSBuild-generated manifest (contains a build:Metadata makepri.exe item).</summary>
    /// <summary>A raw (non-MSBuild) manifest: no build:Metadata makepri.exe item.</summary>
    private static string BuildRawManifest(string packageName = "TestApp", string exe = "TestApp.exe")
    {
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package {ManifestNamespaces}>
              <Identity Name="{packageName}" Publisher="CN=TestPublisher" Version="1.0.0.0" ProcessorArchitecture="x64" />
              <Properties>
                <DisplayName>{packageName}</DisplayName>
                <PublisherDisplayName>Test</PublisherDisplayName>
                <Logo>Assets\logo.png</Logo>
              </Properties>
              <Resources>
                <Resource Language="x-generate" />
              </Resources>
              <Applications>
                <Application Id="App" Executable="{exe}" EntryPoint="Windows.FullTrustApplication" />
              </Applications>
            </Package>
            """;
    }

    private static string BuildMSBuildManifest(string packageName = "TestApp", string exe = "TestApp.exe")
    {
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package {ManifestNamespaces}>
              <Identity Name="{packageName}" Publisher="CN=TestPublisher" Version="1.0.0.0" ProcessorArchitecture="x64" />
              <Properties>
                <DisplayName>{packageName}</DisplayName>
                <PublisherDisplayName>Test</PublisherDisplayName>
                <Logo>Assets\logo.png</Logo>
              </Properties>
              <Applications>
                <Application Id="App" Executable="{exe}" EntryPoint="Windows.FullTrustApplication" />
              </Applications>
              <build:Metadata>
                <build:Item Name="makepri.exe" Version="10.0.0.0" />
              </build:Metadata>
            </Package>
            """;
    }

    private string WriteRecipe(FileInfo srcManifest, params (string Include, string PackagePath)[] files)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">");
        sb.AppendLine("  <ItemGroup>");
        sb.AppendLine($"    <AppXManifest Include=\"{srcManifest.FullName}\">");
        sb.AppendLine("      <PackagePath>appxmanifest.xml</PackagePath>");
        sb.AppendLine("    </AppXManifest>");
        foreach (var (include, packagePath) in files)
        {
            sb.AppendLine($"    <AppxPackagedFile Include=\"{include}\">");
            sb.AppendLine($"      <PackagePath>{packagePath}</PackagePath>");
            sb.AppendLine("    </AppxPackagedFile>");
        }
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine("</Project>");

        var recipePath = Path.Combine(_tempDirectory.FullName, "TestApp.build.appxrecipe");
        File.WriteAllText(recipePath, sb.ToString());
        return recipePath;
    }

    /// <summary>
    /// Invokes the layout copy. Defaults to <c>Exact</c> — winapp's own generated layout — because
    /// that is what a plain <c>winapp run</c> produces; tests covering a user-named directory pass
    /// <c>Additive</c> explicitly.
    /// </summary>
    private Task InvokeCopyFilesFromRecipeAsync(FileInfo recipe, DirectoryInfo outputDir, string reconciliation = "Exact")
    {
        return (Task)CopyFilesFromRecipeMethod.Invoke(
            null, [recipe, outputDir, TestTaskContext, Reconciliation(reconciliation), CancellationToken.None])!;
    }

    /// <summary>Runs the layout copy and returns the exception it failed with, or null if it succeeded.</summary>
    private async Task<Exception?> CaptureCopyFailureAsync(FileInfo recipe, DirectoryInfo outputDir, string reconciliation = "Exact")
    {
        try
        {
            await InvokeCopyFilesFromRecipeAsync(recipe, outputDir, reconciliation);
            return null;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            return ex.InnerException;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private void InvokeSyncFilesToOutputDirectory(DirectoryInfo input, DirectoryInfo output, FileInfo manifest, string reconciliation = "Exact")
    {
        SyncFilesToOutputMethod.Invoke(null, [input, output, manifest, TestTaskContext, Reconciliation(reconciliation)]);
    }

    // ---- CopyFilesFromRecipeAsync -------------------------------------------------

    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_CopiesManifestAndPackagedFiles()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("recipe-src");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        var srcData = new FileInfo(Path.Combine(srcDir.FullName, "TestApp.exe"));
        await File.WriteAllTextAsync(srcData.FullName, "exe-bytes", TestContext.CancellationToken);

        var recipe = new FileInfo(WriteRecipe(srcManifest, (srcData.FullName, "TestApp.exe")));
        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));

        await InvokeCopyFilesFromRecipeAsync(recipe, outputDir);

        Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, "appxmanifest.xml")), "Manifest should be copied to PackagePath");
        Assert.AreEqual("exe-bytes", await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "TestApp.exe"), TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_NestedPackagePath_CreatesSubdirectories()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("recipe-src");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        var srcData = new FileInfo(Path.Combine(srcDir.FullName, "logo.png"));
        await File.WriteAllTextAsync(srcData.FullName, "png", TestContext.CancellationToken);

        var recipe = new FileInfo(WriteRecipe(srcManifest, (srcData.FullName, @"Assets\logo.png")));
        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));

        await InvokeCopyFilesFromRecipeAsync(recipe, outputDir);

        Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, "Assets", "logo.png")), "Nested asset should be created under Assets");
    }

    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_SkipsUnchangedFiles()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("recipe-src");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        var srcData = new FileInfo(Path.Combine(srcDir.FullName, "data.bin"));
        await File.WriteAllTextAsync(srcData.FullName, "AAAAA", TestContext.CancellationToken);
        var timestamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(srcData.FullName, timestamp);

        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));
        outputDir.Create();
        // Pre-existing dest: same length + same timestamp but DIFFERENT content -> must be skipped.
        var destData = Path.Combine(outputDir.FullName, "data.bin");
        await File.WriteAllTextAsync(destData, "BBBBB", TestContext.CancellationToken);
        File.SetLastWriteTimeUtc(destData, timestamp);

        var recipe = new FileInfo(WriteRecipe(srcManifest, (srcData.FullName, "data.bin")));

        await InvokeCopyFilesFromRecipeAsync(recipe, outputDir);

        Assert.AreEqual("BBBBB", await File.ReadAllTextAsync(destData, TestContext.CancellationToken),
            "Unchanged file (same size + timestamp) should be skipped, not overwritten");
    }

    /// <summary>
    /// A recipe entry whose source the build did not produce means the build is incomplete. Staging
    /// the rest would produce a layout mixing this build with an older one, so it fails first.
    /// </summary>
    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_MissingSourceFile_FailsBeforeTouchingTheLayout()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("recipe-src");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);

        var missing = Path.Combine(srcDir.FullName, "does-not-exist.dll");
        var recipe = new FileInfo(WriteRecipe(srcManifest, (missing, "does-not-exist.dll")));
        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));

        var failure = await CaptureCopyFailureAsync(recipe, outputDir);

        Assert.IsInstanceOfType<InvalidOperationException>(failure, "A build that did not produce a listed file must fail materialization");
        StringAssert.Contains(failure!.Message, "does-not-exist.dll", StringComparison.Ordinal);
        Assert.IsFalse(File.Exists(Path.Combine(outputDir.FullName, "appxmanifest.xml")),
            "Validation must fail before anything is written into the layout");
    }

    // ---- CopyFilesFromRecipeAsync: reconciliation ---------------------------------

    /// <summary>
    /// The layout is winapp's own directory and MSBuild never cleans it, so a file dropped from the
    /// app must disappear from the layout on the next materialization. Before this was reconciled,
    /// the layout was the union of every build ever put into it.
    /// </summary>
    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_SecondRun_RemovesContentDroppedFromTheBuild()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("recipe-src");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);

        var fixtures = srcDir.CreateSubdirectory("Fixtures");
        var keep = new FileInfo(Path.Combine(fixtures.FullName, "keep.txt"));
        var dropped = new FileInfo(Path.Combine(fixtures.FullName, "dropped.txt"));
        await File.WriteAllTextAsync(keep.FullName, "keep", TestContext.CancellationToken);
        await File.WriteAllTextAsync(dropped.FullName, "dropped", TestContext.CancellationToken);

        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));

        await InvokeCopyFilesFromRecipeAsync(
            new FileInfo(WriteRecipe(srcManifest,
                (keep.FullName, @"Fixtures\keep.txt"),
                (dropped.FullName, @"Fixtures\dropped.txt"))),
            outputDir);

        Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, "Fixtures", "dropped.txt")), "First run should stage both fixtures");

        // The file is removed from the app and the next build no longer lists it.
        dropped.Delete();

        await InvokeCopyFilesFromRecipeAsync(
            new FileInfo(WriteRecipe(srcManifest, (keep.FullName, @"Fixtures\keep.txt"))),
            outputDir);

        Assert.IsFalse(File.Exists(Path.Combine(outputDir.FullName, "Fixtures", "dropped.txt")),
            "A file the recipe no longer lists must not survive in the layout");
        Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, "Fixtures", "keep.txt")),
            "A file the recipe still lists must be kept");
        Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, "appxmanifest.xml")), "The manifest must be kept");
    }

    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_RemovesDirectoryItsOwnPruningEmptied()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("recipe-src");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        var nested = srcDir.CreateSubdirectory("Fixtures");
        var only = new FileInfo(Path.Combine(nested.FullName, "only.txt"));
        await File.WriteAllTextAsync(only.FullName, "only", TestContext.CancellationToken);

        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));

        await InvokeCopyFilesFromRecipeAsync(
            new FileInfo(WriteRecipe(srcManifest, (only.FullName, @"Fixtures\only.txt"))), outputDir);

        // A directory that was already empty before this run is the caller's, not ours.
        var preexistingEmpty = Directory.CreateDirectory(Path.Combine(outputDir.FullName, "PreexistingEmpty"));

        only.Delete();
        await InvokeCopyFilesFromRecipeAsync(new FileInfo(WriteRecipe(srcManifest)), outputDir);

        Assert.IsFalse(Directory.Exists(Path.Combine(outputDir.FullName, "Fixtures")),
            "A directory left empty by pruning should not linger in the layout");
        Assert.IsTrue(Directory.Exists(preexistingEmpty.FullName),
            "A directory that pruning did not empty must be left alone");
        Assert.IsTrue(outputDir.Exists, "The layout directory itself must never be removed");
    }

    /// <summary>
    /// A build that stopped producing a file it still lists leaves the previous layout — a state that
    /// is known to work — exactly as it was, rather than half-updating it.
    /// </summary>
    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_MissingSourceOnSecondRun_LeavesThePriorLayoutIntact()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("recipe-src");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        var payload = new FileInfo(Path.Combine(srcDir.FullName, "TestApp.dll"));
        await File.WriteAllTextAsync(payload.FullName, "v1", TestContext.CancellationToken);

        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));

        await InvokeCopyFilesFromRecipeAsync(
            new FileInfo(WriteRecipe(srcManifest, (payload.FullName, "TestApp.dll"))), outputDir);

        payload.Delete();

        // Same recipe, source now gone.
        var failure = await CaptureCopyFailureAsync(
            new FileInfo(WriteRecipe(srcManifest, (payload.FullName, "TestApp.dll"))), outputDir);

        Assert.IsInstanceOfType<InvalidOperationException>(failure, "A missing source must fail rather than silently keep stale content");
        Assert.AreEqual("v1", await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "TestApp.dll"), TestContext.CancellationToken),
            "The previous layout must survive a failed materialization unchanged");
        Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, "appxmanifest.xml")),
            "The previous layout must survive a failed materialization unchanged");
    }

    /// <summary>A recipe that describes nothing is evidence of an incomplete build, not of an empty app.</summary>
    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_RecipeListsNothing_FailsAndLeavesLayoutUntouched()
    {
        var outputDir = Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, "layout"));
        var existing = Path.Combine(outputDir.FullName, "TestApp.exe");
        await File.WriteAllTextAsync(existing, "exe", TestContext.CancellationToken);

        var recipePath = Path.Combine(_tempDirectory.FullName, "Empty.build.appxrecipe");
        await File.WriteAllTextAsync(
            recipePath,
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup />
            </Project>
            """,
            TestContext.CancellationToken);

        var failure = await CaptureCopyFailureAsync(new FileInfo(recipePath), new DirectoryInfo(outputDir.FullName));

        Assert.IsInstanceOfType<InvalidOperationException>(failure, "An empty recipe must fail rather than be treated as an empty app");
        Assert.IsTrue(File.Exists(existing), "An empty recipe must not empty the layout");
    }

    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_MalformedRecipe_FailsAndLeavesLayoutUntouched()
    {
        var outputDir = Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, "layout"));
        var existing = Path.Combine(outputDir.FullName, "TestApp.exe");
        await File.WriteAllTextAsync(existing, "exe", TestContext.CancellationToken);

        var recipePath = Path.Combine(_tempDirectory.FullName, "Broken.build.appxrecipe");
        await File.WriteAllTextAsync(recipePath, "<Project><ItemGroup>", TestContext.CancellationToken);

        var failure = await CaptureCopyFailureAsync(new FileInfo(recipePath), new DirectoryInfo(outputDir.FullName));

        Assert.IsInstanceOfType<InvalidOperationException>(failure, "A truncated recipe must fail rather than be read as a small app");
        Assert.IsTrue(File.Exists(existing), "A malformed recipe must not empty the layout");
    }

    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_DuplicatePackagePathsDifferingOnlyByCase_Fails()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("recipe-src");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        var first = new FileInfo(Path.Combine(srcDir.FullName, "one.dll"));
        var second = new FileInfo(Path.Combine(srcDir.FullName, "two.dll"));
        await File.WriteAllTextAsync(first.FullName, "one", TestContext.CancellationToken);
        await File.WriteAllTextAsync(second.FullName, "two", TestContext.CancellationToken);

        // On Windows these are one destination, so which file wins would be arbitrary.
        var recipe = new FileInfo(WriteRecipe(srcManifest,
            (first.FullName, "Shared.dll"),
            (second.FullName, "shared.dll")));

        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));
        var failure = await CaptureCopyFailureAsync(recipe, outputDir);

        Assert.IsInstanceOfType<InvalidOperationException>(failure, "Two sources mapped to one destination must fail");
        StringAssert.Contains(failure!.Message, "two different files", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_IdenticalDuplicateEntry_IsAccepted()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("recipe-src");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        var payload = new FileInfo(Path.Combine(srcDir.FullName, "TestApp.dll"));
        await File.WriteAllTextAsync(payload.FullName, "payload", TestContext.CancellationToken);

        // The same file listed twice is redundant, not ambiguous.
        var recipe = new FileInfo(WriteRecipe(srcManifest,
            (payload.FullName, "TestApp.dll"),
            (payload.FullName, "TestApp.dll")));

        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));
        await InvokeCopyFilesFromRecipeAsync(recipe, outputDir);

        Assert.AreEqual("payload", await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "TestApp.dll"), TestContext.CancellationToken));
    }

    [TestMethod]
    [DataRow(@"..\escaped.dll", DisplayName = "parent traversal")]
    [DataRow(@"Sub\..\..\escaped.dll", DisplayName = "traversal after a subdirectory")]
    [DataRow(@"C:\Windows\System32\escaped.dll", DisplayName = "absolute path")]
    public async Task CopyFilesFromRecipeAsync_PackagePathEscapingTheLayout_Fails(string packagePath)
    {
        var srcDir = _tempDirectory.CreateSubdirectory("recipe-src");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        var payload = new FileInfo(Path.Combine(srcDir.FullName, "payload.dll"));
        await File.WriteAllTextAsync(payload.FullName, "payload", TestContext.CancellationToken);

        var recipe = new FileInfo(WriteRecipe(srcManifest, (payload.FullName, packagePath)));
        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));

        var failure = await CaptureCopyFailureAsync(recipe, outputDir);

        Assert.IsInstanceOfType<InvalidOperationException>(failure, $"'{packagePath}' does not name a location inside the package");
        Assert.IsFalse(File.Exists(Path.Combine(_tempDirectory.FullName, "escaped.dll")), "Nothing may be written outside the layout");
    }

    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_NeverRemovesManifestOrResourcesPri()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("recipe-src");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        var srcExe = new FileInfo(Path.Combine(srcDir.FullName, "TestApp.exe"));
        await File.WriteAllTextAsync(srcExe.FullName, "exe", TestContext.CancellationToken);

        var outputDir = Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, "layout"));
        var pri = Path.Combine(outputDir.FullName, "resources.pri");
        await File.WriteAllTextAsync(pri, "pri", TestContext.CancellationToken);
        var registeredManifest = Path.Combine(outputDir.FullName, "appxmanifest.xml");
        await File.WriteAllTextAsync(registeredManifest, "previously registered", TestContext.CancellationToken);

        // Recipe lists only the exe: neither the manifest nor the PRI appear in it.
        var recipe = new FileInfo(WriteRecipeWithoutManifest(srcExe.FullName, "TestApp.exe"));

        await InvokeCopyFilesFromRecipeAsync(recipe, new DirectoryInfo(outputDir.FullName));

        Assert.IsTrue(File.Exists(pri), "resources.pri backs a live registration and must never be pruned");
        Assert.IsTrue(File.Exists(registeredManifest), "The layout manifest backs a live registration and must never be pruned");
    }

    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_LayoutContainsTheBuildOutput_PrunesNothing()
    {
        // The recipe lives in the build output directory; here the user pointed the layout at the
        // folder above it, so the build output is inside the layout.
        var layout = _tempDirectory.CreateSubdirectory("bin");
        var srcDir = layout.CreateSubdirectory("Debug");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        var srcExe = new FileInfo(Path.Combine(srcDir.FullName, "TestApp.exe"));
        await File.WriteAllTextAsync(srcExe.FullName, "exe", TestContext.CancellationToken);
        var symbols = Path.Combine(srcDir.FullName, "TestApp.pdb");
        await File.WriteAllTextAsync(symbols, "pdb", TestContext.CancellationToken);

        var recipePath = Path.Combine(srcDir.FullName, "TestApp.build.appxrecipe");
        await File.WriteAllTextAsync(
            recipePath,
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup>
                <AppXManifest Include="{srcManifest.FullName}"><PackagePath>AppxManifest.xml</PackagePath></AppXManifest>
                <AppxPackagedFile Include="{srcExe.FullName}"><PackagePath>TestApp.exe</PackagePath></AppxPackagedFile>
              </ItemGroup>
            </Project>
            """,
            TestContext.CancellationToken);

        await InvokeCopyFilesFromRecipeAsync(new FileInfo(recipePath), layout);

        Assert.IsTrue(File.Exists(symbols), "A build artifact the recipe does not package must survive when the layout contains the build output");
        Assert.IsTrue(File.Exists(recipePath), "The recipe itself must survive");
        Assert.IsTrue(File.Exists(Path.Combine(layout.FullName, "TestApp.exe")), "The layout is still produced");
    }

    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_PruningStaysInsideTheLayout()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("recipe-src");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);

        var sibling = _tempDirectory.CreateSubdirectory("sibling");
        var siblingFile = Path.Combine(sibling.FullName, "unrelated.txt");
        await File.WriteAllTextAsync(siblingFile, "unrelated", TestContext.CancellationToken);

        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));

        await InvokeCopyFilesFromRecipeAsync(new FileInfo(WriteRecipe(srcManifest)), outputDir);

        Assert.IsTrue(File.Exists(siblingFile), "Nothing outside the layout may be touched");
        Assert.IsTrue(File.Exists(Path.Combine(srcDir.FullName, "AppxManifest.xml")), "The build output may not be touched");
    }

    /// <summary>
    /// A junction inside the layout points at a tree winapp does not own, so pruning must stop at it
    /// rather than delete whatever it targets. It cannot simply be stepped over either: the layout
    /// would then be registered with an unknown tree hanging off it while the run reported that the
    /// layout matches the build. It is stale content winapp will not remove, so the run fails.
    /// </summary>
    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_DoesNotDeleteThroughADirectoryJunction()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("recipe-src");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);

        var outside = _tempDirectory.CreateSubdirectory("outside");
        var outsideFile = Path.Combine(outside.FullName, "precious.txt");
        await File.WriteAllTextAsync(outsideFile, "precious", TestContext.CancellationToken);

        var outputDir = Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, "layout"));
        var junction = Path.Combine(outputDir.FullName, "linked");
        if (!TryCreateJunction(junction, outside.FullName))
        {
            Assert.Inconclusive("Could not create a directory junction on this machine.");
            return;
        }

        try
        {
            var failure = await CaptureCopyFailureAsync(
                new FileInfo(WriteRecipe(srcManifest)), new DirectoryInfo(outputDir.FullName));

            Assert.IsInstanceOfType<InvalidOperationException>(
                failure, "a layout holding a link winapp will not remove is not an exact layout");
            StringAssert.Contains(failure!.Message, "linked", StringComparison.OrdinalIgnoreCase);

            Assert.IsTrue(File.Exists(outsideFile), "Pruning must not follow a junction out of the layout");
            Assert.IsTrue(Directory.Exists(junction), "The link itself must be left for the user to deal with");
            Assert.IsTrue(Directory.Exists(outside.FullName), "The link's target must be left alone entirely");
        }
        finally
        {
            try
            {
                Directory.Delete(junction, recursive: false);
            }
            catch
            {
                // Best-effort cleanup; the temp root is removed anyway.
            }
        }
    }

    /// <summary>
    /// The same junction in a layout the user named. Nothing there is winapp's to remove, so the
    /// link is neither followed nor complained about -- the run just adds to the layout.
    /// </summary>
    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_UserSuppliedLayout_LeavesADirectoryJunctionAlone()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("additive-junction-src");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);

        var outside = _tempDirectory.CreateSubdirectory("additive-outside");
        var outsideFile = Path.Combine(outside.FullName, "precious.txt");
        await File.WriteAllTextAsync(outsideFile, "precious", TestContext.CancellationToken);

        var outputDir = Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, "additive-junction-layout"));
        var junction = Path.Combine(outputDir.FullName, "linked");
        if (!TryCreateJunction(junction, outside.FullName))
        {
            Assert.Inconclusive("Could not create a directory junction on this machine.");
            return;
        }

        try
        {
            await InvokeCopyFilesFromRecipeAsync(
                new FileInfo(WriteRecipe(srcManifest)), new DirectoryInfo(outputDir.FullName), "Additive");

            Assert.IsTrue(File.Exists(outsideFile), "Nothing behind the link may be touched");
            Assert.IsTrue(Directory.Exists(junction), "The link must survive an additive run");
        }
        finally
        {
            try
            {
                Directory.Delete(junction, recursive: false);
            }
            catch
            {
                // Best-effort cleanup; the temp root is removed anyway.
            }
        }
    }

    // ---- CopyFilesFromRecipeAsync: ownership, links, locking ----------------------

    /// <summary>
    /// A user-named layout can already hold files winapp never staged. Those are not winapp's to
    /// delete, however confidently the recipe says they are not part of the app.
    /// </summary>
    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_UserSuppliedLayout_KeepsFilesWinappNeverStaged()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("recipe-src");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        var payload = new FileInfo(Path.Combine(srcDir.FullName, "TestApp.dll"));
        await File.WriteAllTextAsync(payload.FullName, "payload", TestContext.CancellationToken);

        var outputDir = Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, "user-layout"));
        var userFile = Path.Combine(outputDir.FullName, "my-notes.txt");
        await File.WriteAllTextAsync(userFile, "mine", TestContext.CancellationToken);
        var userDir = Directory.CreateDirectory(Path.Combine(outputDir.FullName, "MyStuff"));
        var nestedUserFile = Path.Combine(userDir.FullName, "nested.bin");
        await File.WriteAllTextAsync(nestedUserFile, "also mine", TestContext.CancellationToken);

        await InvokeCopyFilesFromRecipeAsync(
            new FileInfo(WriteRecipe(srcManifest, (payload.FullName, "TestApp.dll"))),
            new DirectoryInfo(outputDir.FullName),
            "Additive");

        Assert.IsTrue(File.Exists(userFile), "A file winapp never staged must survive in a user-named layout");
        Assert.IsTrue(File.Exists(nestedUserFile), "A nested file winapp never staged must survive");
        Assert.IsTrue(Directory.Exists(userDir.FullName), "A directory winapp never created must survive");
        Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, "TestApp.dll")), "The app's own files are still staged");
    }

    /// <summary>
    /// A user-named layout is never pruned, not even of a file winapp itself staged on an earlier
    /// run. Telling those apart would mean keeping a record of what winapp wrote, and any such record
    /// can go missing or grow stale — at which point the code deletes a developer's file on the
    /// strength of bookkeeping it cannot verify. Leaving a stale file behind is recoverable by
    /// pointing at a fresh directory, which the option's documentation says to do; deleting the
    /// wrong file is not.
    /// </summary>
    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_UserSuppliedLayout_RemovesNothingAtAll()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("recipe-src");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        var keep = new FileInfo(Path.Combine(srcDir.FullName, "keep.dll"));
        var dropped = new FileInfo(Path.Combine(srcDir.FullName, "dropped.dll"));
        await File.WriteAllTextAsync(keep.FullName, "keep", TestContext.CancellationToken);
        await File.WriteAllTextAsync(dropped.FullName, "dropped", TestContext.CancellationToken);

        var outputDir = Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, "user-layout"));
        var userFile = Path.Combine(outputDir.FullName, "my-notes.txt");
        await File.WriteAllTextAsync(userFile, "mine", TestContext.CancellationToken);

        await InvokeCopyFilesFromRecipeAsync(
            new FileInfo(WriteRecipe(srcManifest, (keep.FullName, "keep.dll"), (dropped.FullName, "dropped.dll"))),
            new DirectoryInfo(outputDir.FullName),
            "Additive");

        dropped.Delete();

        await InvokeCopyFilesFromRecipeAsync(
            new FileInfo(WriteRecipe(srcManifest, (keep.FullName, "keep.dll"))),
            new DirectoryInfo(outputDir.FullName),
            "Additive");

        Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, "dropped.dll")),
            "A user-named layout is additive: nothing is removed from it");
        Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, "keep.dll")), "A file still in the app must be kept");
        Assert.IsTrue(File.Exists(userFile), "The user's own file must survive");
    }

    /// <summary>
    /// The same directory reached the two ways — as the default and as an explicit
    /// <c>--output-appx-directory</c> — is not the same thing, because only in the first case did
    /// winapp create it. The caller says which, so the identical path can be pruned in one case and
    /// left alone in the other.
    /// </summary>
    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_SamePathExplicitAndImplicit_OnlyImplicitPrunes()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("recipe-src");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        var payload = new FileInfo(Path.Combine(srcDir.FullName, "TestApp.dll"));
        await File.WriteAllTextAsync(payload.FullName, "payload", TestContext.CancellationToken);
        var recipe = new FileInfo(WriteRecipe(srcManifest, (payload.FullName, "TestApp.dll")));

        var outputDir = Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, "layout"));
        var stale = Path.Combine(outputDir.FullName, "stale.dll");

        await File.WriteAllTextAsync(stale, "stale", TestContext.CancellationToken);
        await InvokeCopyFilesFromRecipeAsync(recipe, new DirectoryInfo(outputDir.FullName), "Additive");
        Assert.IsTrue(File.Exists(stale), "An explicitly named directory keeps files the recipe does not list");

        await InvokeCopyFilesFromRecipeAsync(recipe, new DirectoryInfo(outputDir.FullName), "Exact");
        Assert.IsFalse(File.Exists(stale), "The generated layout is reconciled to exactly what the build produced");
    }

    /// <summary>
    /// Nothing about the reconciliation is remembered between runs, so deleting the layout and
    /// building it again is just another first run. A design that recorded what it had staged would
    /// have to notice that its record now describes a directory that no longer exists.
    /// </summary>
    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_LayoutDeletedAndRecreatedBetweenRuns_StillReconciles()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("recipe-src");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        var payload = new FileInfo(Path.Combine(srcDir.FullName, "TestApp.dll"));
        await File.WriteAllTextAsync(payload.FullName, "payload", TestContext.CancellationToken);
        var recipe = new FileInfo(WriteRecipe(srcManifest, (payload.FullName, "TestApp.dll")));

        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));

        await InvokeCopyFilesFromRecipeAsync(recipe, outputDir);

        Directory.Delete(outputDir.FullName, recursive: true);
        Directory.CreateDirectory(outputDir.FullName);
        var stale = Path.Combine(outputDir.FullName, "stale.dll");
        await File.WriteAllTextAsync(stale, "stale", TestContext.CancellationToken);

        await InvokeCopyFilesFromRecipeAsync(recipe, new DirectoryInfo(outputDir.FullName));

        Assert.IsFalse(File.Exists(stale), "A recreated generated layout is still reconciled to the build");
        Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, "TestApp.dll")), "The app's files are staged again");
    }

    /// <summary>
    /// The layout holds app payload and nothing else. Any state winapp needed to keep about the
    /// layout would ship inside the package and be registered with it, so there is none.
    /// </summary>
    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_LayoutContainsOnlyAppPayload()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("recipe-src");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        var payload = new FileInfo(Path.Combine(srcDir.FullName, "TestApp.dll"));
        await File.WriteAllTextAsync(payload.FullName, "payload", TestContext.CancellationToken);

        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));

        await InvokeCopyFilesFromRecipeAsync(
            new FileInfo(WriteRecipe(srcManifest, (payload.FullName, "TestApp.dll"))), outputDir);

        var staged = outputDir.GetFiles("*", SearchOption.AllDirectories).Select(f => f.Name).ToList();
        CollectionAssert.AreEquivalent(
            PayloadOnlyLayout,
            staged,
            "The layout must contain only app payload");
    }

    /// <summary>
    /// A junction inside the layout is a way out of it: copying to <c>Assets\logo.png</c> writes
    /// through the link to wherever it points, so a write the caller believes is confined to the
    /// layout could land anywhere. Checking only the layout root does not catch this.
    /// </summary>
    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_SubdirectoryIsAJunction_RefusesToCopyThroughIt()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("recipe-src");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        var payload = new FileInfo(Path.Combine(srcDir.FullName, "logo.png"));
        await File.WriteAllTextAsync(payload.FullName, "new", TestContext.CancellationToken);

        var elsewhere = _tempDirectory.CreateSubdirectory("elsewhere");
        var victim = Path.Combine(elsewhere.FullName, "logo.png");
        await File.WriteAllTextAsync(victim, "do not overwrite me", TestContext.CancellationToken);

        var outputDir = Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, "layout"));

        if (!TryCreateJunction(Path.Combine(outputDir.FullName, "Assets"), elsewhere.FullName))
        {
            Assert.Inconclusive("Could not create a directory junction on this machine.");
        }

        var failure = await CaptureCopyFailureAsync(
            new FileInfo(WriteRecipe(srcManifest, (payload.FullName, @"Assets\logo.png"))),
            new DirectoryInfo(outputDir.FullName));

        Assert.IsInstanceOfType<InvalidOperationException>(failure, "A junction inside the layout must be refused");
        StringAssert.Contains(failure!.Message, "symbolic link or junction");
        Assert.AreEqual("do not overwrite me", await File.ReadAllTextAsync(victim, TestContext.CancellationToken),
            "The file the junction pointed at must not have been written");
    }

    /// <summary>
    /// Packaging stages into a directory winapp just created under the system temp path, which on
    /// many machines is reached through a junction. Nothing there is ever pruned, so the link rules
    /// that make deletion safe have nothing to protect and must not reject the path.
    /// </summary>
    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_StagingUnderAJunction_IsAllowedWhenNothingIsPruned()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("recipe-src");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        var payload = new FileInfo(Path.Combine(srcDir.FullName, "TestApp.dll"));
        await File.WriteAllTextAsync(payload.FullName, "payload", TestContext.CancellationToken);

        var realTemp = _tempDirectory.CreateSubdirectory("real-temp");
        var linkedTemp = Path.Combine(_tempDirectory.FullName, "linked-temp");

        if (!TryCreateJunction(linkedTemp, realTemp.FullName))
        {
            Assert.Inconclusive("Could not create a directory junction on this machine.");
        }

        var stagingDir = new DirectoryInfo(Path.Combine(linkedTemp, "staging"));

        await InvokeCopyFilesFromRecipeAsync(
            new FileInfo(WriteRecipe(srcManifest, (payload.FullName, "TestApp.dll"))), stagingDir, "None");

        Assert.IsTrue(File.Exists(Path.Combine(realTemp.FullName, "staging", "TestApp.dll")),
            "Fresh staging under a junction-backed temp directory must succeed");
    }

    /// <summary>
    /// Every safety rule here is phrased as "inside the layout". A link in the layout's own path makes
    /// that phrase name a different tree, so such a path is refused rather than reasoned about.
    /// </summary>
    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_LayoutRootIsAJunction_IsRefused()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("recipe-src");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);

        var real = _tempDirectory.CreateSubdirectory("real-target");
        var precious = Path.Combine(real.FullName, "precious.txt");
        await File.WriteAllTextAsync(precious, "precious", TestContext.CancellationToken);

        var layoutPath = Path.Combine(_tempDirectory.FullName, "layout-link");
        if (!TryCreateJunction(layoutPath, real.FullName))
        {
            Assert.Inconclusive("Could not create a directory junction on this machine.");
            return;
        }

        try
        {
            var failure = await CaptureCopyFailureAsync(
                new FileInfo(WriteRecipe(srcManifest)), new DirectoryInfo(layoutPath));

            Assert.IsInstanceOfType<InvalidOperationException>(failure, "A layout root that is a junction must be refused");
            StringAssert.Contains(failure!.Message, "junction", StringComparison.OrdinalIgnoreCase);
            Assert.IsTrue(File.Exists(precious), "Nothing in the junction's target may be touched");
        }
        finally
        {
            try
            {
                Directory.Delete(layoutPath, recursive: false);
            }
            catch
            {
                // Best-effort cleanup; the temp root is removed anyway.
            }
        }
    }

    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_LayoutParentIsAJunction_IsRefused()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("recipe-src");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);

        var real = _tempDirectory.CreateSubdirectory("real-parent");
        var layoutInsideReal = Directory.CreateDirectory(Path.Combine(real.FullName, "AppX"));
        var precious = Path.Combine(layoutInsideReal.FullName, "precious.txt");
        await File.WriteAllTextAsync(precious, "precious", TestContext.CancellationToken);

        var parentLink = Path.Combine(_tempDirectory.FullName, "parent-link");
        if (!TryCreateJunction(parentLink, real.FullName))
        {
            Assert.Inconclusive("Could not create a directory junction on this machine.");
            return;
        }

        try
        {
            var failure = await CaptureCopyFailureAsync(
                new FileInfo(WriteRecipe(srcManifest)), new DirectoryInfo(Path.Combine(parentLink, "AppX")));

            Assert.IsInstanceOfType<InvalidOperationException>(failure, "A layout reached through a junctioned parent must be refused");
            Assert.IsTrue(File.Exists(precious), "Nothing behind the junction may be touched");
        }
        finally
        {
            try
            {
                Directory.Delete(parentLink, recursive: false);
            }
            catch
            {
                // Best-effort cleanup; the temp root is removed anyway.
            }
        }
    }

    /// <summary>
    /// A layout that still holds content the app dropped must not go on to be registered or deployed,
    /// so a stale file winapp cannot delete fails the whole materialization rather than warning.
    /// </summary>
    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_StaleFileLocked_FailsMaterialization()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("recipe-src");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        var keep = new FileInfo(Path.Combine(srcDir.FullName, "keep.dll"));
        var dropped = new FileInfo(Path.Combine(srcDir.FullName, "dropped.dll"));
        await File.WriteAllTextAsync(keep.FullName, "keep", TestContext.CancellationToken);
        await File.WriteAllTextAsync(dropped.FullName, "dropped", TestContext.CancellationToken);

        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));

        await InvokeCopyFilesFromRecipeAsync(
            new FileInfo(WriteRecipe(srcManifest, (keep.FullName, "keep.dll"), (dropped.FullName, "dropped.dll"))),
            outputDir);

        dropped.Delete();

        var stagedStale = Path.Combine(outputDir.FullName, "dropped.dll");

        // Held open with no sharing, the way a running instance of the app holds its own binaries.
        using (var _ = new FileStream(stagedStale, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var failure = await CaptureCopyFailureAsync(
                new FileInfo(WriteRecipe(srcManifest, (keep.FullName, "keep.dll"))), outputDir);

            Assert.IsInstanceOfType<InvalidOperationException>(failure,
                "A stale file that cannot be removed must fail materialization, not warn and succeed");
            StringAssert.Contains(failure!.Message, "dropped.dll", StringComparison.Ordinal);
        }

        await InvokeCopyFilesFromRecipeAsync(
            new FileInfo(WriteRecipe(srcManifest, (keep.FullName, "keep.dll"))), outputDir);

        Assert.IsFalse(File.Exists(stagedStale), "Once the lock is released the retry must remove the stale file");
    }

    /// <summary>
    /// The generated layout is derived entirely from the build, so concurrent runs converge on the
    /// same content rather than on whichever finished last. Nothing is carried between runs that
    /// could be left describing neither of them.
    /// </summary>
    [TestMethod]
    public async Task CopyFilesFromRecipeAsync_ConcurrentRunsOnOneLayout_ProduceAConsistentResult()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("recipe-src");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);

        var payloads = new List<FileInfo>();
        for (var i = 0; i < 12; i++)
        {
            var file = new FileInfo(Path.Combine(srcDir.FullName, $"payload{i}.dll"));
            await File.WriteAllTextAsync(file.FullName, $"payload{i}", TestContext.CancellationToken);
            payloads.Add(file);
        }

        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));
        var entries = payloads.Select(p => (p.FullName, p.Name)).ToArray();

        // Distinct recipe files: concurrency is being tested in the layout, not in the recipe reader.
        var recipes = Enumerable.Range(0, 4)
            .Select(i =>
            {
                var path = Path.Combine(_tempDirectory.FullName, $"Concurrent{i}.build.appxrecipe");
                File.Copy(WriteRecipe(srcManifest, entries), path, overwrite: true);
                return new FileInfo(path);
            })
            .ToList();

        await Task.WhenAll(recipes.Select(recipe => Task.Run(
            () => InvokeCopyFilesFromRecipeAsync(recipe, new DirectoryInfo(outputDir.FullName)),
            TestContext.CancellationToken)));

        foreach (var payload in payloads)
        {
            Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, payload.Name)),
                $"{payload.Name} must survive concurrent materializations of the same layout");
        }

        Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, "appxmanifest.xml")),
            "The manifest must survive concurrent materializations of the same layout");
    }

    /// <summary>
    /// The lease is what stops a second run from rewriting a layout the first has materialized and
    /// is still consuming, so it must actually exclude — and must say what to do about it rather
    /// than blocking forever.
    /// </summary>
    [TestMethod]
    public void LayoutLease_SecondHolderOnTheSameLayout_IsRefusedWithAWayForward()
    {
        var layout = _tempDirectory.CreateSubdirectory("leased-layout");

        using var first = LayoutLease.Acquire(_testCacheDirectory, layout, TestContext.CancellationToken);

        var second = Assert.ThrowsExactly<TimeoutException>(() => LayoutLease.Acquire(
            _testCacheDirectory,
            new DirectoryInfo(layout.FullName.ToUpperInvariant()),
            TestContext.CancellationToken,
            TimeSpan.FromMilliseconds(200)));

        StringAssert.Contains(second.Message, "--output-appx-directory", StringComparison.Ordinal);
    }

    /// <summary>Two different layouts are unrelated, so one must not block the other.</summary>
    [TestMethod]
    public void LayoutLease_DifferentLayouts_DoNotBlockEachOther()
    {
        var first = _tempDirectory.CreateSubdirectory("layout-a");
        var second = _tempDirectory.CreateSubdirectory("layout-b");

        using var leaseA = LayoutLease.Acquire(_testCacheDirectory, first, TestContext.CancellationToken);
        using var leaseB = LayoutLease.Acquire(_testCacheDirectory, second, TestContext.CancellationToken);
    }

    /// <summary>
    /// The lease is bookkeeping about the layout, not part of it: anything written inside would be
    /// packaged and registered as app payload.
    /// </summary>
    [TestMethod]
    public void LayoutLease_WritesNothingIntoTheLayout()
    {
        var layout = _tempDirectory.CreateSubdirectory("layout-c");

        using (var lease = LayoutLease.Acquire(_testCacheDirectory, layout, TestContext.CancellationToken))
        {
            Assert.AreEqual(0, layout.GetFileSystemInfos().Length, "The lease must not write into the layout");
        }

        Assert.AreEqual(0, layout.GetFileSystemInfos().Length, "Releasing the lease must not write into the layout");
    }

    /// <summary>
    /// The lease must be releasable more than once: the target run path releases it explicitly at
    /// the consume boundary and again from the <c>finally</c> that covers every other exit.
    /// </summary>
    [TestMethod]
    public void LayoutLease_ReleasedTwice_IsHarmlessAndFreesTheLayout()
    {
        var layout = _tempDirectory.CreateSubdirectory("layout-d");

        var lease = LayoutLease.Acquire(_testCacheDirectory, layout, TestContext.CancellationToken);
        lease.Dispose();
        lease.Dispose();

        // A second run must be able to claim it immediately, without waiting out the timeout.
        using var next = LayoutLease.Acquire(
            _testCacheDirectory, layout, TestContext.CancellationToken, TimeSpan.FromMilliseconds(200));
    }

    /// <summary>
    /// The shape of a packaged Windows Sandbox run, twice: the host deploys an exact payload into
    /// the guest, and the guest registers from a layout directory the host created beside it. When
    /// the app drops a file, the second run must drop it from that registration layout too --
    /// otherwise the guest keeps registering content the app no longer contains, which is the bug
    /// this whole change exists to fix. Without <c>--clean</c>: a workaround is not a fix.
    /// </summary>
    [TestMethod]
    public async Task GuestRegistrationLayout_SecondRunAfterAFileIsDropped_NoLongerContainsIt()
    {
        // The deployed payload: an exact copy of the host layout, so it never holds the dropped file.
        var payload = _tempDirectory.CreateSubdirectory("abc");
        var manifest = new FileInfo(Path.Combine(payload.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(payload.FullName, "TestApp.exe"), "exe", TestContext.CancellationToken);

        var fixtures = payload.CreateSubdirectory("Fixtures");
        var dropped = new FileInfo(Path.Combine(fixtures.FullName, "removed.json"));
        await File.WriteAllTextAsync(dropped.FullName, "{}", TestContext.CancellationToken);

        // The registration layout: a sibling the host names, so it is winapp's own -- Exact.
        var registrationLayout = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "abc-layout"));

        InvokeSyncFilesToOutputDirectory(payload, registrationLayout, manifest);

        var stagedDropped = Path.Combine(registrationLayout.FullName, "Fixtures", "removed.json");
        Assert.IsTrue(File.Exists(stagedDropped), "the first run must stage the file the app still had");

        // The app drops the file and is redeployed. The payload is exact, so it simply goes away.
        dropped.Delete();

        InvokeSyncFilesToOutputDirectory(payload, registrationLayout, manifest);

        Assert.IsFalse(File.Exists(stagedDropped),
            "the guest must not go on registering a file the app no longer contains");
        Assert.IsTrue(File.Exists(Path.Combine(registrationLayout.FullName, "TestApp.exe")),
            "the rest of the app must survive");
        Assert.IsTrue(File.Exists(Path.Combine(registrationLayout.FullName, "appxmanifest.xml")),
            "the manifest the package registers from must survive");
    }

    /// <summary>
    /// The same two runs against a directory the user named. Nothing is removed, because winapp
    /// cannot tell a build leftover from a file the developer put there.
    /// </summary>
    [TestMethod]
    public async Task UserNamedLayout_SecondRunAfterAFileIsDropped_KeepsEverything()
    {
        var payload = _tempDirectory.CreateSubdirectory("additive-src");
        var manifest = new FileInfo(Path.Combine(payload.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);

        var dropped = new FileInfo(Path.Combine(payload.FullName, "removed.json"));
        await File.WriteAllTextAsync(dropped.FullName, "{}", TestContext.CancellationToken);

        var layout = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "additive-layout"));

        InvokeSyncFilesToOutputDirectory(payload, layout, manifest, "Additive");

        var staged = Path.Combine(layout.FullName, "removed.json");
        Assert.IsTrue(File.Exists(staged));

        dropped.Delete();

        InvokeSyncFilesToOutputDirectory(payload, layout, manifest, "Additive");

        Assert.IsTrue(File.Exists(staged),
            "a directory the user named is only ever added to, even when the app drops a file");
    }

    /// <summary>
    /// The most ordinary way to name an output directory is inside the folder being packaged --
    /// <c>winapp run . --output-appx-directory .\AppX</c>. The layout must not be walked as part of
    /// its own input: the second run would copy the first run's layout into a subdirectory of
    /// itself, the third would copy that, and the layout would grow a deeper copy of itself forever.
    /// </summary>
    [TestMethod]
    public async Task SyncFilesToOutputDirectory_LayoutNestedInsideTheInput_DoesNotCopyItselfIntoItself()
    {
        var input = _tempDirectory.CreateSubdirectory("nested-input");
        var manifest = new FileInfo(Path.Combine(input.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(input.FullName, "App.exe"), "exe", TestContext.CancellationToken);

        // The layout the user named, inside the input.
        var layout = new DirectoryInfo(Path.Combine(input.FullName, "AppX"));

        InvokeSyncFilesToOutputDirectory(input, layout, manifest, "Additive");
        InvokeSyncFilesToOutputDirectory(input, layout, manifest, "Additive");

        Assert.IsFalse(Directory.Exists(Path.Combine(layout.FullName, "AppX")),
            "the layout must not contain a copy of itself");
        Assert.IsTrue(File.Exists(Path.Combine(layout.FullName, "App.exe")), "the app must still be staged");
        Assert.IsTrue(File.Exists(Path.Combine(layout.FullName, "appxmanifest.xml")), "the manifest must still be staged");
    }

    /// <summary>The control: a layout beside the input is unaffected by that exclusion.</summary>
    [TestMethod]
    public async Task SyncFilesToOutputDirectory_LayoutBesideTheInput_StagesEverything()
    {
        var input = _tempDirectory.CreateSubdirectory("sibling-input");
        var manifest = new FileInfo(Path.Combine(input.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(input.FullName, "App.exe"), "exe", TestContext.CancellationToken);
        var assets = input.CreateSubdirectory("Assets");
        await File.WriteAllTextAsync(Path.Combine(assets.FullName, "logo.png"), "png", TestContext.CancellationToken);

        var layout = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "sibling-layout"));

        InvokeSyncFilesToOutputDirectory(input, layout, manifest, "Additive");
        InvokeSyncFilesToOutputDirectory(input, layout, manifest, "Additive");

        Assert.IsTrue(File.Exists(Path.Combine(layout.FullName, "App.exe")));
        Assert.IsTrue(File.Exists(Path.Combine(layout.FullName, "Assets", "logo.png")));
        Assert.IsFalse(Directory.Exists(Path.Combine(layout.FullName, "sibling-layout")),
            "a sibling layout is not part of its own input either");
    }

    /// <summary>
    /// The same nesting for the generated layout, which is reconciled exactly. Excluding the layout
    /// from its own input is what keeps the layout's own files from being read back as part of the
    /// app -- and, on the exact path, from making a stale file look like content the build produced.
    /// </summary>
    [TestMethod]
    public async Task SyncFilesToOutputDirectory_GeneratedLayoutNestedInsideTheInput_StillDropsRemovedFiles()
    {
        var input = _tempDirectory.CreateSubdirectory("nested-exact-input");
        var manifest = new FileInfo(Path.Combine(input.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(input.FullName, "App.exe"), "exe", TestContext.CancellationToken);

        var dropped = new FileInfo(Path.Combine(input.FullName, "Fixtures", "removed.json"));
        dropped.Directory!.Create();
        await File.WriteAllTextAsync(dropped.FullName, "{}", TestContext.CancellationToken);

        var layout = new DirectoryInfo(Path.Combine(input.FullName, "AppX"));

        InvokeSyncFilesToOutputDirectory(input, layout, manifest);
        Assert.IsTrue(File.Exists(Path.Combine(layout.FullName, "Fixtures", "removed.json")));

        dropped.Delete();

        InvokeSyncFilesToOutputDirectory(input, layout, manifest);

        Assert.IsFalse(File.Exists(Path.Combine(layout.FullName, "Fixtures", "removed.json")),
            "a file the app no longer contains must not survive in the generated layout");
        Assert.IsFalse(Directory.Exists(Path.Combine(layout.FullName, "AppX")),
            "the layout must not contain a copy of itself");
        Assert.IsTrue(File.Exists(Path.Combine(layout.FullName, "App.exe")));
    }

    /// <summary>
    /// The reverse nesting cannot be made to work: every input file would land beside the input's
    /// own copy of it, and an exact layout would judge the build itself to be content the app
    /// dropped. It is refused rather than half-supported.
    /// </summary>
    [TestMethod]
    public async Task SyncFilesToOutputDirectory_LayoutContainingTheInput_IsRefused()
    {
        var layout = _tempDirectory.CreateSubdirectory("outer-layout");
        var input = layout.CreateSubdirectory("build-output");
        var manifest = new FileInfo(Path.Combine(input.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);

        var failure = Assert.ThrowsExactly<TargetInvocationException>(
            () => InvokeSyncFilesToOutputDirectory(input, layout, manifest, "Additive"));

        Assert.IsInstanceOfType<InvalidOperationException>(failure.InnerException);
        StringAssert.Contains(failure.InnerException!.Message, "contains the folder being packaged", StringComparison.Ordinal);
    }

    /// <summary>
    /// A directory link in the build output cannot be walked -- following it could lead anywhere,
    /// including back into the layout -- and a layout quietly missing whatever was behind it is the
    /// same bug as stale content: an app that registers and runs with files missing. Both the
    /// generated layout and one the caller named refuse it, and say which link stopped them.
    /// </summary>
    [TestMethod]
    [DataRow("Exact", DisplayName = "the layout winapp generates")]
    [DataRow("Additive", DisplayName = "a layout the caller named")]
    public async Task SyncFilesToOutputDirectory_DirectoryLinkInTheInput_IsRefused(string reconciliation)
    {
        var input = _tempDirectory.CreateSubdirectory($"linked-input-{reconciliation}");
        var manifest = new FileInfo(Path.Combine(input.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(input.FullName, "App.exe"), "exe", TestContext.CancellationToken);

        var outside = _tempDirectory.CreateSubdirectory($"linked-input-target-{reconciliation}");
        var outsideFile = Path.Combine(outside.FullName, "elsewhere.txt");
        await File.WriteAllTextAsync(outsideFile, "elsewhere", TestContext.CancellationToken);

        var junction = Path.Combine(input.FullName, "Plugins");
        if (!TryCreateJunction(junction, outside.FullName))
        {
            Assert.Inconclusive("Could not create a directory junction on this machine.");
            return;
        }

        try
        {
            var layout = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, $"linked-layout-{reconciliation}"));

            var failure = Assert.ThrowsExactly<TargetInvocationException>(
                () => InvokeSyncFilesToOutputDirectory(input, layout, manifest, reconciliation));

            Assert.IsInstanceOfType<InvalidOperationException>(failure.InnerException);

            var message = failure.InnerException!.Message;
            StringAssert.Contains(message, junction, StringComparison.OrdinalIgnoreCase);

            // Naming --output-appx-directory here would read as a lossless way out, and it is not:
            // that layout is missing everything behind the link too.
            Assert.IsFalse(message.Contains("--output-appx-directory", StringComparison.OrdinalIgnoreCase),
                $"the error must not offer an explicit layout as a workaround, because it loses the same files: {message}");

            Assert.IsTrue(File.Exists(outsideFile), "nothing behind the link may be touched");
            Assert.IsFalse(File.Exists(Path.Combine(layout.FullName, "App.exe")),
                "the run fails before anything is staged");
        }
        finally
        {
            try
            {
                Directory.Delete(junction, recursive: false);
            }
            catch
            {
                // Best-effort cleanup; the temp root is removed anyway.
            }
        }
    }

    /// <summary>
    /// The same for a linked file. Copying it would read straight through the link, so the layout
    /// would be built from content that is not in the folder being packaged at all.
    /// </summary>
    [TestMethod]
    [DataRow("Exact", DisplayName = "the layout winapp generates")]
    [DataRow("Additive", DisplayName = "a layout the caller named")]
    public async Task SyncFilesToOutputDirectory_FileLinkInTheInput_IsRefusedAndItsTargetIsUntouched(string reconciliation)
    {
        var input = _tempDirectory.CreateSubdirectory($"filelink-input-{reconciliation}");
        var manifest = new FileInfo(Path.Combine(input.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(input.FullName, "App.exe"), "exe", TestContext.CancellationToken);

        var outside = _tempDirectory.CreateSubdirectory($"filelink-target-{reconciliation}");
        var target = Path.Combine(outside.FullName, "shared.dll");
        await File.WriteAllTextAsync(target, "the-real-bytes", TestContext.CancellationToken);

        var link = Path.Combine(input.FullName, "shared.dll");
        if (!TryCreateFileSymbolicLink(link, target))
        {
            Assert.Inconclusive("Could not create a file symbolic link on this machine (needs Developer Mode).");
            return;
        }

        var layout = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, $"filelink-layout-{reconciliation}"));

        var failure = Assert.ThrowsExactly<TargetInvocationException>(
            () => InvokeSyncFilesToOutputDirectory(input, layout, manifest, reconciliation));

        Assert.IsInstanceOfType<InvalidOperationException>(failure.InnerException);
        StringAssert.Contains(failure.InnerException!.Message, link, StringComparison.OrdinalIgnoreCase);

        Assert.AreEqual("the-real-bytes", await File.ReadAllTextAsync(target, TestContext.CancellationToken),
            "the link's target must not be written to");
        Assert.IsFalse(File.Exists(Path.Combine(layout.FullName, "shared.dll")),
            "nothing may be copied through the link");
        Assert.IsFalse(File.Exists(Path.Combine(layout.FullName, "App.exe")),
            "the run fails before anything is staged");
    }

    /// <summary>
    /// The failure has to arrive before the first write, or a layout that was registerable a moment
    /// ago is left half-rebuilt. The previous layout is still exactly what it was.
    /// </summary>
    [TestMethod]
    public async Task SyncFilesToOutputDirectory_LinkAppearingLater_LeavesTheWorkingLayoutIntact()
    {
        var input = _tempDirectory.CreateSubdirectory("late-link-input");
        var manifest = new FileInfo(Path.Combine(input.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(input.FullName, "App.exe"), "v1", TestContext.CancellationToken);

        var layout = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "late-link-layout"));

        InvokeSyncFilesToOutputDirectory(input, layout, manifest);
        Assert.AreEqual("v1", await File.ReadAllTextAsync(Path.Combine(layout.FullName, "App.exe"), TestContext.CancellationToken));

        // The next build changes the app and introduces a link.
        await File.WriteAllTextAsync(Path.Combine(input.FullName, "App.exe"), "v2", TestContext.CancellationToken);
        var outside = _tempDirectory.CreateSubdirectory("late-link-target");
        var junction = Path.Combine(input.FullName, "Plugins");
        if (!TryCreateJunction(junction, outside.FullName))
        {
            Assert.Inconclusive("Could not create a directory junction on this machine.");
            return;
        }

        try
        {
            Assert.ThrowsExactly<TargetInvocationException>(
                () => InvokeSyncFilesToOutputDirectory(input, layout, manifest));

            Assert.AreEqual("v1", await File.ReadAllTextAsync(Path.Combine(layout.FullName, "App.exe"), TestContext.CancellationToken),
                "the layout that worked before must not be half-rebuilt into one that does not");
        }
        finally
        {
            try
            {
                Directory.Delete(junction, recursive: false);
            }
            catch
            {
                // Best-effort cleanup; the temp root is removed anyway.
            }
        }
    }

    /// <summary>
    /// The layout is kept out of its own input by comparing paths as they are spelled, which a link
    /// defeats: an input named through a junction and a layout named by its real path are the same
    /// tree, and neither path says so. Left alone, the layout is walked as part of its own input and
    /// grows a copy of itself on every run -- the very shape the exclusion exists to prevent.
    /// </summary>
    [TestMethod]
    public async Task SyncFilesToOutputDirectory_InputNamedThroughALink_IsRefusedRatherThanNestedIntoItself()
    {
        var real = _tempDirectory.CreateSubdirectory("alias-real");
        var manifest = new FileInfo(Path.Combine(real.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(real.FullName, "App.exe"), "exe", TestContext.CancellationToken);

        var alias = Path.Combine(_tempDirectory.FullName, "alias-link");
        if (!TryCreateJunction(alias, real.FullName))
        {
            Assert.Inconclusive("Could not create a directory junction on this machine.");
            return;
        }

        try
        {
            // The layout by its real path; the input by the alias. Spelled this way the layout looks
            // like it is outside the input, and it is not.
            var layout = new DirectoryInfo(Path.Combine(real.FullName, "AppX"));
            var aliasedInput = new DirectoryInfo(alias);

            for (var run = 1; run <= 2; run++)
            {
                var failure = Assert.ThrowsExactly<TargetInvocationException>(
                    () => InvokeSyncFilesToOutputDirectory(aliasedInput, layout, manifest, "Additive"));

                Assert.IsInstanceOfType<InvalidOperationException>(failure.InnerException);
                StringAssert.Contains(failure.InnerException!.Message, alias, StringComparison.OrdinalIgnoreCase);
            }

            Assert.IsFalse(Directory.Exists(Path.Combine(layout.FullName, "AppX")),
                "the layout must never have been staged into itself");

            // An ancestor link is the same problem one level up, and is refused for the same reason.
            var nestedInput = new DirectoryInfo(Path.Combine(alias, "Nested"));
            Directory.CreateDirectory(Path.Combine(real.FullName, "Nested"));

            var ancestorFailure = Assert.ThrowsExactly<TargetInvocationException>(
                () => InvokeSyncFilesToOutputDirectory(nestedInput, layout, manifest, "Additive"));
            Assert.IsInstanceOfType<InvalidOperationException>(ancestorFailure.InnerException);
            StringAssert.Contains(ancestorFailure.InnerException!.Message, alias, StringComparison.OrdinalIgnoreCase);

            // The control that matters: the ordinary project directory, with no link anywhere in its
            // path, is still packaged exactly as before.
            InvokeSyncFilesToOutputDirectory(real, layout, manifest, "Additive");
            InvokeSyncFilesToOutputDirectory(real, layout, manifest, "Additive");

            Assert.IsTrue(File.Exists(Path.Combine(layout.FullName, "App.exe")));
            Assert.IsFalse(Directory.Exists(Path.Combine(layout.FullName, "AppX")),
                "the real path still keeps the layout out of its own input");
        }
        finally
        {
            try
            {
                Directory.Delete(alias, recursive: false);
            }
            catch
            {
                // Best-effort cleanup; the temp root is removed anyway.
            }
        }
    }

    /// <summary>
    /// The guest registration layout, with a junction left inside it. The guest is about to register
    /// the package from this directory, so an unknown tree hanging off it is not something to report
    /// success over -- and it is not winapp's to delete either. The run fails and the link's target
    /// is untouched.
    /// </summary>
    [TestMethod]
    public async Task SyncFilesToOutputDirectory_StaleJunctionInAnExactLayout_FailsAndLeavesTheTargetAlone()
    {
        var payload = _tempDirectory.CreateSubdirectory("junction-payload");
        var manifest = new FileInfo(Path.Combine(payload.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(payload.FullName, "App.exe"), "exe", TestContext.CancellationToken);

        var outside = _tempDirectory.CreateSubdirectory("junction-payload-target");
        var outsideFile = Path.Combine(outside.FullName, "precious.txt");
        await File.WriteAllTextAsync(outsideFile, "precious", TestContext.CancellationToken);

        var layout = Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, "junction-layout"));
        var junction = Path.Combine(layout.FullName, "stale-link");
        if (!TryCreateJunction(junction, outside.FullName))
        {
            Assert.Inconclusive("Could not create a directory junction on this machine.");
            return;
        }

        try
        {
            var failure = Assert.ThrowsExactly<TargetInvocationException>(
                () => InvokeSyncFilesToOutputDirectory(payload, new DirectoryInfo(layout.FullName), manifest));

            Assert.IsInstanceOfType<InvalidOperationException>(failure.InnerException);
            StringAssert.Contains(failure.InnerException!.Message, "stale-link", StringComparison.OrdinalIgnoreCase);

            Assert.IsTrue(File.Exists(outsideFile), "the link's target must not be deleted through");
            Assert.IsTrue(Directory.Exists(junction), "the link itself is left for the user to deal with");
        }
        finally
        {
            try
            {
                Directory.Delete(junction, recursive: false);
            }
            catch
            {
                // Best-effort cleanup; the temp root is removed anyway.
            }
        }
    }

    /// <summary>
    /// The same for a package staged without a recipe. That path now shares the recipe path's link
    /// rules, so it has to share this exemption too, or packaging would start failing on any machine
    /// whose temp directory is reached through a junction.
    /// </summary>
    [TestMethod]
    public async Task SyncFilesToOutputDirectory_StagingUnderAJunction_IsAllowedWhenNothingIsPruned()
    {
        var input = _tempDirectory.CreateSubdirectory("none-staging-input");
        var manifest = new FileInfo(Path.Combine(input.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(input.FullName, "TestApp.dll"), "payload", TestContext.CancellationToken);

        var realTemp = _tempDirectory.CreateSubdirectory("none-real-temp");
        var linkedTemp = Path.Combine(_tempDirectory.FullName, "none-linked-temp");

        if (!TryCreateJunction(linkedTemp, realTemp.FullName))
        {
            Assert.Inconclusive("Could not create a directory junction on this machine.");
            return;
        }

        var stagingDir = new DirectoryInfo(Path.Combine(linkedTemp, "staging"));

        InvokeSyncFilesToOutputDirectory(input, stagingDir, manifest, "None");

        Assert.IsTrue(File.Exists(Path.Combine(realTemp.FullName, "staging", "TestApp.dll")),
            "Fresh staging under a junction-backed temp directory must succeed");
    }

    /// <summary>
    /// A file symbolic link, which unlike a junction needs Developer Mode or elevation, so callers
    /// treat a false return as "cannot test this here".
    /// </summary>
    private static bool TryCreateFileSymbolicLink(string linkPath, string target)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, target);
            return File.Exists(linkPath)
                && new FileInfo(linkPath).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Junction creation needs no elevation, unlike a symbolic link.</summary>
    private static bool TryCreateJunction(string linkPath, string target)    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", "mklink", "/J", linkPath, target },
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            process?.WaitForExit(15000);
            return Directory.Exists(linkPath)
                && new DirectoryInfo(linkPath).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Writes a recipe with a packaged file but no AppXManifest entry.</summary>
    private string WriteRecipeWithoutManifest(string include, string packagePath)
    {
        var recipePath = Path.Combine(_tempDirectory.FullName, "NoManifest.build.appxrecipe");
        File.WriteAllText(
            recipePath,
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup>
                <AppxPackagedFile Include="{include}"><PackagePath>{packagePath}</PackagePath></AppxPackagedFile>
              </ItemGroup>
            </Project>
            """);
        return recipePath;
    }

    // ---- SyncFilesToOutputDirectory -----------------------------------------------

    [TestMethod]
    public async Task SyncFilesToOutputDirectory_CopiesFilesAndManifest()
    {
        var inputDir = _tempDirectory.CreateSubdirectory("input");
        await File.WriteAllTextAsync(Path.Combine(inputDir.FullName, "TestApp.exe"), "exe", TestContext.CancellationToken);
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);

        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "out"));

        InvokeSyncFilesToOutputDirectory(inputDir, outputDir, manifest);

        Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, "TestApp.exe")), "Input files should be synced");
        Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, "appxmanifest.xml")), "Manifest should be copied");
    }

    [TestMethod]
    public async Task SyncFilesToOutputDirectory_RenamesPackageAppxmanifest()
    {
        var inputDir = _tempDirectory.CreateSubdirectory("input");
        await File.WriteAllTextAsync(Path.Combine(inputDir.FullName, "app.exe"), "exe", TestContext.CancellationToken);
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "Package.appxmanifest"));
        await File.WriteAllTextAsync(manifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);

        var outputDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "out"));

        InvokeSyncFilesToOutputDirectory(inputDir, outputDir, manifest);

        Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, "appxmanifest.xml")), "Package.appxmanifest should be renamed to appxmanifest.xml");
        Assert.IsFalse(File.Exists(Path.Combine(outputDir.FullName, "Package.appxmanifest")), "Original Package.appxmanifest name should not remain");
    }

    [TestMethod]
    public async Task SyncFilesToOutputDirectory_ProtectsExistingManifestAndPri()
    {
        var inputDir = _tempDirectory.CreateSubdirectory("input");
        await File.WriteAllTextAsync(Path.Combine(inputDir.FullName, "app.exe"), "exe", TestContext.CancellationToken);

        var outputDir = _tempDirectory.CreateSubdirectory("out");
        // Pre-existing protected files that a naive mirror-sync would delete.
        await File.WriteAllTextAsync(Path.Combine(outputDir.FullName, "resources.pri"), "keep-pri", TestContext.CancellationToken);

        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);

        InvokeSyncFilesToOutputDirectory(inputDir, outputDir, manifest);

        Assert.AreEqual("keep-pri", await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "resources.pri"), TestContext.CancellationToken),
            "Existing resources.pri must be protected during sync");
        Assert.IsTrue(File.Exists(Path.Combine(outputDir.FullName, "app.exe")), "New input file should still be copied");
    }

    [TestMethod]
    public async Task SyncFilesToOutputDirectory_InputEqualsOutput_CopiesManifestOnly()
    {
        var dir = _tempDirectory.CreateSubdirectory("same");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "TestApp.exe"), "exe", TestContext.CancellationToken);
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);

        // input == output: the SyncDirectory step is skipped, only the manifest copy runs.
        InvokeSyncFilesToOutputDirectory(dir, dir, manifest);

        Assert.IsTrue(File.Exists(Path.Combine(dir.FullName, "appxmanifest.xml")), "Manifest should still be copied into the directory");
        Assert.IsTrue(File.Exists(Path.Combine(dir.FullName, "TestApp.exe")), "Existing file should remain untouched");
    }

    // ---- IsPathInsideDirectory (additional branch coverage) -----------------------

    [TestMethod]
    public void IsPathInsideDirectory_ParentOfContainer_ReturnsFalse()
    {
        var container = _tempDirectory.CreateSubdirectory("child").FullName;
        var parent = _tempDirectory.FullName;
        Assert.IsFalse(MsixService.IsPathInsideDirectory(parent, container), "'..' traversal must be rejected");
    }

    [TestMethod]
    public void IsPathInsideDirectory_DifferentRoot_ReturnsFalse()
    {
        // A UNC path is on a different root from the local temp container, so GetRelativePath
        // returns a rooted path -> not contained.
        Assert.IsFalse(MsixService.IsPathInsideDirectory(@"\\server\share\file", _tempDirectory.FullName));
    }

    [TestMethod]
    public void IsPathInsideDirectory_InvalidCandidatePath_ReturnsFalse()
    {
        // Embedded null char makes Path.GetFullPath throw -> caught -> false.
        Assert.IsFalse(MsixService.IsPathInsideDirectory("bad\0path", _tempDirectory.FullName));
    }

    // ---- Register wrappers --------------------------------------------------------

    [TestMethod]
    public async Task RegisterSparsePackageAsync_Success_DelegatesToRegistrationService()
    {
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, "manifest", TestContext.CancellationToken);
        var external = _tempDirectory;

        await _msixService.RegisterSparsePackageAsync(manifest, external, TestTaskContext, TestContext.CancellationToken);

        Assert.HasCount(1, _fakeRegistration.RegisterSparseCalls);
        Assert.AreEqual(manifest.FullName, _fakeRegistration.RegisterSparseCalls[0].ManifestPath);
        Assert.AreEqual(external.FullName, _fakeRegistration.RegisterSparseCalls[0].ExternalLocation);
    }

    [TestMethod]
    public async Task RegisterSparsePackageAsync_RegistrationFails_ThrowsInvalidOperation()
    {
        _fakeRegistration.RegisterSparseThrows = new InvalidOperationException("boom");
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, "manifest", TestContext.CancellationToken);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _msixService.RegisterSparsePackageAsync(manifest, _tempDirectory, TestTaskContext, TestContext.CancellationToken));
        Assert.Contains("Failed to register sparse package", ex.Message);
    }

    [TestMethod]
    public async Task RegisterLooseLayoutPackageAsync_Success_DelegatesToRegistrationService()
    {
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, "manifest", TestContext.CancellationToken);

        await _msixService.RegisterLooseLayoutPackageAsync(manifest, TestTaskContext, TestContext.CancellationToken);

        Assert.HasCount(1, _fakeRegistration.RegisterLooseLayoutCalls);
        Assert.AreEqual(manifest.FullName, _fakeRegistration.RegisterLooseLayoutCalls[0]);
    }

    [TestMethod]
    public async Task RegisterLooseLayoutPackageAsync_RegistrationFails_ThrowsInvalidOperation()
    {
        _fakeRegistration.RegisterLooseLayoutThrows = new InvalidOperationException("boom");
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, "manifest", TestContext.CancellationToken);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _msixService.RegisterLooseLayoutPackageAsync(manifest, TestTaskContext, TestContext.CancellationToken));
        Assert.Contains("Failed to register package", ex.Message);
    }

    // ---- AddSparseIdentityAsync guards --------------------------------------------

    [TestMethod]
    public async Task AddSparseIdentityAsync_ManifestMissing_ThrowsFileNotFound()
    {
        var missingManifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "nope.xml"));

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            _msixService.AddSparseIdentityAsync(null, missingManifest, noInstall: true, keepIdentity: false, TestTaskContext, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task AddSparseIdentityAsync_DevModeDisabled_ThrowsInvalidOperation()
    {
        _fakeDevMode.IsEnabledResult = false;
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _msixService.AddSparseIdentityAsync("app.exe", manifest, noInstall: false, keepIdentity: false, TestTaskContext, TestContext.CancellationToken));
        Assert.Contains("Developer Mode", ex.Message);
    }

    [TestMethod]
    public async Task AddSparseIdentityAsync_PlaceholderExecutableWithoutEntryPoint_Throws()
    {
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, BuildMSBuildManifest(exe: "$targetnametoken$.exe"), TestContext.CancellationToken);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _msixService.AddSparseIdentityAsync(null, manifest, noInstall: true, keepIdentity: false, TestTaskContext, TestContext.CancellationToken));
        Assert.Contains("placeholder", ex.Message);
    }

    [TestMethod]
    public async Task AddSparseIdentityAsync_EntryPointNotFound_ThrowsFileNotFound()
    {
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);

        var missingExe = Path.Combine(_tempDirectory.FullName, "not-here.exe");

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            _msixService.AddSparseIdentityAsync(missingExe, manifest, noInstall: true, keepIdentity: false, TestTaskContext, TestContext.CancellationToken));
    }

    // ---- AddLooseLayoutIdentityAsync guards ---------------------------------------

    [TestMethod]
    public async Task AddLooseLayoutIdentityAsync_ManifestMissing_ThrowsFileNotFound()
    {
        var missingManifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "nope.xml"));
        var input = _tempDirectory.CreateSubdirectory("input");
        var output = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "out"));

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            _msixService.AddLooseLayoutIdentityAsync(missingManifest, input, output, TestTaskContext, LayoutReconciliation.Exact, cancellationToken: TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task AddLooseLayoutIdentityAsync_DevModeDisabled_ThrowsInvalidOperation()
    {
        _fakeDevMode.IsEnabledResult = false;
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(manifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        var input = _tempDirectory.CreateSubdirectory("input");
        var output = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "out"));

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _msixService.AddLooseLayoutIdentityAsync(manifest, input, output, TestTaskContext, LayoutReconciliation.Exact, cancellationToken: TestContext.CancellationToken));
        Assert.Contains("Developer Mode", ex.Message);
    }

    // ---- AddLooseLayoutIdentityAsync MSBuild workflow -----------------------------

    [TestMethod]
    public async Task AddLooseLayoutIdentityAsync_MSBuildManifestWithRecipe_RegistersLooseLayout()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("build-output");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        var srcExe = new FileInfo(Path.Combine(srcDir.FullName, "TestApp.exe"));
        await File.WriteAllTextAsync(srcExe.FullName, "exe", TestContext.CancellationToken);

        // Recipe file lives in the input directory (srcDir) with the *.build.appxrecipe extension.
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">");
        sb.AppendLine("  <ItemGroup>");
        sb.AppendLine($"    <AppXManifest Include=\"{srcManifest.FullName}\"><PackagePath>appxmanifest.xml</PackagePath></AppXManifest>");
        sb.AppendLine($"    <AppxPackagedFile Include=\"{srcExe.FullName}\"><PackagePath>TestApp.exe</PackagePath></AppxPackagedFile>");
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine("</Project>");
        await File.WriteAllTextAsync(Path.Combine(srcDir.FullName, "TestApp.build.appxrecipe"), sb.ToString(), TestContext.CancellationToken);

        var output = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));

        var result = await _msixService.AddLooseLayoutIdentityAsync(
            srcManifest, srcDir, output, TestTaskContext, LayoutReconciliation.Exact, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual("TestApp", result.PackageName);
        Assert.AreEqual("CN=TestPublisher", result.Publisher);
        Assert.AreEqual("App", result.ApplicationId);
        Assert.IsTrue(File.Exists(Path.Combine(output.FullName, "appxmanifest.xml")), "Layout manifest should be produced from the recipe");
        Assert.HasCount(1, _fakeRegistration.RegisterLooseLayoutCalls);
    }

    [TestMethod]
    public async Task AddLooseLayoutIdentityAsync_MSBuildManifestNoRecipe_FallsBackToSync()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("build-output");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "Package.appxmanifest"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(srcDir.FullName, "TestApp.exe"), "exe", TestContext.CancellationToken);

        var output = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));

        var result = await _msixService.AddLooseLayoutIdentityAsync(
            srcManifest, srcDir, output, TestTaskContext, LayoutReconciliation.Exact, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual("TestApp", result.PackageName);
        Assert.IsTrue(File.Exists(Path.Combine(output.FullName, "appxmanifest.xml")), "Fallback sync should copy/rename manifest into the layout");
        Assert.IsTrue(File.Exists(Path.Combine(output.FullName, "TestApp.exe")), "Fallback sync should copy input files");
        Assert.HasCount(1, _fakeRegistration.RegisterLooseLayoutCalls);
    }

    [TestMethod]
    public async Task AddLooseLayoutIdentityAsync_RawManifest_ProcessesAndRegisters()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("raw-input");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "Package.appxmanifest"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildRawManifest(), TestContext.CancellationToken);
        // A concrete (non-runtime) executable so placeholder resolution/PE detection has a target.
        await File.WriteAllTextAsync(Path.Combine(srcDir.FullName, "TestApp.exe"), "not-a-real-pe", TestContext.CancellationToken);

        var output = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));

        var result = await _msixService.AddLooseLayoutIdentityAsync(
            srcManifest, srcDir, output, TestTaskContext, LayoutReconciliation.Exact, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual("TestApp", result.PackageName);
        Assert.AreEqual("App", result.ApplicationId);
        Assert.IsTrue(File.Exists(Path.Combine(output.FullName, "appxmanifest.xml")), "Processed manifest should be written to the layout");
        Assert.HasCount(1, _fakeRegistration.RegisterLooseLayoutCalls);
        // x-generate should have been resolved during processing.
        var written = await File.ReadAllTextAsync(Path.Combine(output.FullName, "appxmanifest.xml"), TestContext.CancellationToken);
        Assert.DoesNotContain("x-generate", written, "x-generate language token should be resolved");
    }

    [TestMethod]
    [DataRow("RestartAgent.exe", DisplayName = "Windows App SDK restart agent")]
    [DataRow("DeploymentAgent.exe", DisplayName = "Windows App SDK deployment agent")]
    public async Task AddLooseLayoutIdentityAsync_RawManifest_PlaceholderWithWinAppSdkHelper_InfersAppExe(string helperExeName)
    {
        // Issue #790: WindowsAppSDKSelfContained=true extracts the Windows App SDK framework
        // payload into the build output root, so a helper exe always sits next to the app exe.
        // $targetnametoken$ must still resolve to the app exe without --executable.
        var srcDir = _tempDirectory.CreateSubdirectory("raw-input");
        var srcManifest = new FileInfo(Path.Join(srcDir.FullName, "Package.appxmanifest"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildRawManifest(exe: "$targetnametoken$.exe"), TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Join(srcDir.FullName, "TestApp.exe"), "not-a-real-pe", TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Join(srcDir.FullName, helperExeName), "not-a-real-pe", TestContext.CancellationToken);

        var output = new DirectoryInfo(Path.Join(_tempDirectory.FullName, "layout"));

        var result = await _msixService.AddLooseLayoutIdentityAsync(
            srcManifest, srcDir, output, TestTaskContext, LayoutReconciliation.Exact, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual("TestApp", result.PackageName);
        var written = await File.ReadAllTextAsync(Path.Join(output.FullName, "appxmanifest.xml"), TestContext.CancellationToken);
        Assert.Contains(@"Executable=""TestApp.exe""", written,
            $"{helperExeName} should be skipped so the app exe resolves without --executable");
        Assert.DoesNotContain("$targetnametoken$", written, "No $targetnametoken$ placeholder should remain");
    }

    // ---- MaterializeLooseLayoutAsync (execution-target seam) ----------------------

    /// <summary>
    /// Materialization must produce the identical layout, because a guest failure that cannot be
    /// reproduced by looking at the same folder locally is not diagnosable.
    /// </summary>
    [TestMethod]
    public async Task MaterializeLooseLayoutAsync_MSBuildManifest_ProducesTheSameLayoutAsRegistering()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("build-output");
        var srcManifest = new FileInfo(TestPaths.Under(srcDir.FullName, "Package.appxmanifest"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        await File.WriteAllTextAsync(TestPaths.Under(srcDir.FullName, "TestApp.exe"), "exe", TestContext.CancellationToken);

        var registered = new DirectoryInfo(TestPaths.Under(_tempDirectory.FullName, "registered"));
        var materialized = new DirectoryInfo(TestPaths.Under(_tempDirectory.FullName, "materialized"));

        var registeredResult = await _msixService.AddLooseLayoutIdentityAsync(
            srcManifest, srcDir, registered, TestTaskContext, LayoutReconciliation.Exact, cancellationToken: TestContext.CancellationToken);

        var materializedResult = await _msixService.MaterializeLooseLayoutAsync(
            srcManifest, srcDir, materialized, TestTaskContext, LayoutReconciliation.Exact, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(registeredResult.PackageName, materializedResult.PackageName);
        Assert.AreEqual(registeredResult.Publisher, materializedResult.Publisher);
        Assert.AreEqual(registeredResult.ApplicationId, materializedResult.ApplicationId);
        AssertLayoutsMatch(registered, materialized);
    }

    /// <summary>
    /// The user-visible shape of the bug: an execution target mirrors the host layout exactly, so a
    /// file the app no longer contains must be gone from the layout the second time it is materialized.
    /// </summary>
    [TestMethod]
    public async Task MaterializeLooseLayoutAsync_SecondRun_DropsContentRemovedFromTheApp()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("build-output");
        var srcManifest = new FileInfo(TestPaths.Under(srcDir.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        await File.WriteAllTextAsync(TestPaths.Under(srcDir.FullName, "TestApp.exe"), "exe", TestContext.CancellationToken);
        var fixtures = srcDir.CreateSubdirectory("Fixtures");
        var dropped = new FileInfo(TestPaths.Under(fixtures.FullName, "dropped.txt"));
        await File.WriteAllTextAsync(dropped.FullName, "dropped", TestContext.CancellationToken);

        async Task WriteBuildRecipeAsync(bool includeDropped)
        {
            var droppedEntry = includeDropped
                ? $"<AppxPackagedFile Include=\"{dropped.FullName}\"><PackagePath>Fixtures\\dropped.txt</PackagePath></AppxPackagedFile>"
                : string.Empty;

            await File.WriteAllTextAsync(
                TestPaths.Under(srcDir.FullName, "TestApp.build.appxrecipe"),
                $"""
                <?xml version="1.0" encoding="utf-8"?>
                <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                  <ItemGroup>
                    <AppXManifest Include="{srcManifest.FullName}"><PackagePath>appxmanifest.xml</PackagePath></AppXManifest>
                    <AppxPackagedFile Include="{TestPaths.Under(srcDir.FullName, "TestApp.exe")}"><PackagePath>TestApp.exe</PackagePath></AppxPackagedFile>
                    {droppedEntry}
                  </ItemGroup>
                </Project>
                """,
                TestContext.CancellationToken);
        }

        // An explicitly supplied layout directory, as `--output-appx-directory` produces.
        var layout = new DirectoryInfo(TestPaths.Under(_tempDirectory.FullName, "explicit-layout"));

        await WriteBuildRecipeAsync(includeDropped: true);
        await _msixService.MaterializeLooseLayoutAsync(
            srcManifest, srcDir, layout, TestTaskContext, LayoutReconciliation.Exact, cancellationToken: TestContext.CancellationToken);
        Assert.IsTrue(File.Exists(TestPaths.Under(layout.FullName, "Fixtures", "dropped.txt")));

        // The file is removed from the app; the rebuild no longer produces or lists it.
        dropped.Delete();
        await WriteBuildRecipeAsync(includeDropped: false);

        await _msixService.MaterializeLooseLayoutAsync(
            srcManifest, srcDir, layout, TestTaskContext, LayoutReconciliation.Exact, cancellationToken: TestContext.CancellationToken);

        Assert.IsFalse(Directory.Exists(TestPaths.Under(layout.FullName, "Fixtures")),
            "Content removed from the app must not survive in the layout an execution target mirrors");
        Assert.IsTrue(File.Exists(TestPaths.Under(layout.FullName, "TestApp.exe")), "The app itself must still be staged");
        Assert.IsTrue(File.Exists(TestPaths.Under(layout.FullName, "appxmanifest.xml")), "The manifest must still be staged");
    }

    [TestMethod]
    public async Task MaterializeLooseLayoutAsync_RawManifest_ProducesTheSameLayoutAsRegistering()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("raw-input");
        var srcManifest = new FileInfo(TestPaths.Under(srcDir.FullName, "Package.appxmanifest"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildRawManifest(), TestContext.CancellationToken);
        await File.WriteAllTextAsync(TestPaths.Under(srcDir.FullName, "TestApp.exe"), "not-a-real-pe", TestContext.CancellationToken);

        var registered = new DirectoryInfo(TestPaths.Under(_tempDirectory.FullName, "registered"));
        var materialized = new DirectoryInfo(TestPaths.Under(_tempDirectory.FullName, "materialized"));

        await _msixService.AddLooseLayoutIdentityAsync(
            srcManifest, srcDir, registered, TestTaskContext, LayoutReconciliation.Exact, cancellationToken: TestContext.CancellationToken);

        var materializedResult = await _msixService.MaterializeLooseLayoutAsync(
            srcManifest, srcDir, materialized, TestTaskContext, LayoutReconciliation.Exact, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual("TestApp", materializedResult.PackageName);
        AssertLayoutsMatch(registered, materialized);
    }

    /// <summary>
    /// The whole point of the seam: running somewhere else must leave this machine alone.
    /// </summary>
    [TestMethod]
    public async Task MaterializeLooseLayoutAsync_RegistersNothingAndInstallsNoRuntime()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("build-output");
        var srcManifest = new FileInfo(TestPaths.Under(srcDir.FullName, "Package.appxmanifest"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        await File.WriteAllTextAsync(TestPaths.Under(srcDir.FullName, "TestApp.exe"), "exe", TestContext.CancellationToken);

        var output = new DirectoryInfo(TestPaths.Under(_tempDirectory.FullName, "layout"));

        await _msixService.MaterializeLooseLayoutAsync(
            srcManifest, srcDir, output, TestTaskContext, LayoutReconciliation.Exact, cancellationToken: TestContext.CancellationToken);

        Assert.IsEmpty(_fakeRegistration.RegisterLooseLayoutCalls, "Materializing for another target must not register on this machine");
        Assert.IsEmpty(_fakeRegistration.UnregisterCalls, "Materializing for another target must not touch host registrations");
        Assert.IsEmpty(_fakeWindowsAppRuntime.InstallRuntimeCalls, "Materializing for another target must not install a runtime on this machine");
    }

    /// <summary>
    /// Developer Mode is a prerequisite for registering a package, and materialization registers
    /// nothing — so demanding it would fail a <c>--on sandbox</c> run on a step it never performs.
    /// </summary>
    [TestMethod]
    public async Task MaterializeLooseLayoutAsync_DevModeDisabled_StillMaterializes()
    {
        _fakeDevMode.IsEnabledResult = false;

        var srcDir = _tempDirectory.CreateSubdirectory("build-output");
        var srcManifest = new FileInfo(TestPaths.Under(srcDir.FullName, "Package.appxmanifest"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildMSBuildManifest(), TestContext.CancellationToken);
        await File.WriteAllTextAsync(TestPaths.Under(srcDir.FullName, "TestApp.exe"), "exe", TestContext.CancellationToken);

        var output = new DirectoryInfo(TestPaths.Under(_tempDirectory.FullName, "layout"));

        var result = await _msixService.MaterializeLooseLayoutAsync(
            srcManifest, srcDir, output, TestTaskContext, LayoutReconciliation.Exact, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual("TestApp", result.PackageName);
        Assert.IsTrue(File.Exists(TestPaths.Under(output.FullName, "appxmanifest.xml")));
    }

    [TestMethod]
    public async Task MaterializeLooseLayoutAsync_ManifestMissing_ThrowsFileNotFound()
    {
        var input = _tempDirectory.CreateSubdirectory("in");
        var output = _tempDirectory.CreateSubdirectory("out");
        var missingManifest = new FileInfo(TestPaths.Under(input.FullName, "Package.appxmanifest"));

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            _msixService.MaterializeLooseLayoutAsync(missingManifest, input, output, TestTaskContext, LayoutReconciliation.Exact, cancellationToken: TestContext.CancellationToken));
    }

    /// <summary>Compares two layouts by relative path and content.</summary>
    private static void AssertLayoutsMatch(DirectoryInfo expected, DirectoryInfo actual)
    {
        static Dictionary<string, byte[]> Read(DirectoryInfo root) =>
            root.EnumerateFiles("*", SearchOption.AllDirectories).ToDictionary(
                file => Path.GetRelativePath(root.FullName, file.FullName),
                file => File.ReadAllBytes(file.FullName),
                StringComparer.OrdinalIgnoreCase);

        var expectedFiles = Read(expected);
        var actualFiles = Read(actual);

        CollectionAssert.AreEquivalent(
            expectedFiles.Keys.ToList(),
            actualFiles.Keys.ToList(),
            "Materialized layout should contain exactly the same files as the registered one");

        foreach (var (relativePath, content) in expectedFiles)
        {
            CollectionAssert.AreEqual(content, actualFiles[relativePath], $"'{relativePath}' should be byte-identical");
        }
    }

    // ---- AddSparseIdentityAsync workflow ------------------------------------------

    private (FileInfo manifest, string exePath) ArrangeSparseInputs()
    {
        // Manifest + assets live in one directory; the executable in another, so the
        // asset-copy branch (originalManifestDir != entryPointDir) is exercised.
        var srcDir = _tempDirectory.CreateSubdirectory("src");
        var assets = srcDir.CreateSubdirectory("Assets");
        File.WriteAllText(Path.Combine(assets.FullName, "logo.png"), "png-bytes");
        var manifest = new FileInfo(Path.Combine(srcDir.FullName, "Package.appxmanifest"));
        File.WriteAllText(manifest.FullName, BuildRawManifest());

        var binDir = _tempDirectory.CreateSubdirectory("bin");
        var exePath = Path.Combine(binDir.FullName, "TestApp.exe");
        File.WriteAllText(exePath, "not-a-real-pe");

        return (manifest, exePath);
    }

    [TestMethod]
    public async Task AddSparseIdentityAsync_NoInstall_GeneratesSparseStructureAndDebugIdentity()
    {
        var (manifest, exePath) = ArrangeSparseInputs();

        var result = await _msixService.AddSparseIdentityAsync(
            exePath, manifest, noInstall: true, keepIdentity: false, TestTaskContext, TestContext.CancellationToken);

        // keepIdentity:false -> CreateDebugIdentity appends ".debug".
        Assert.AreEqual("TestApp.debug", result.PackageName);
        Assert.AreEqual("App.debug", result.ApplicationId);
        Assert.AreEqual("CN=TestPublisher", result.Publisher);
        // Asset copy + PRI generation should have produced resources.pri next to the exe.
        Assert.IsTrue(File.Exists(Path.Combine(Path.GetDirectoryName(exePath)!, "resources.pri")),
            "resources.pri should be generated in the entry-point directory");
        // noInstall -> registration is skipped.
        Assert.IsEmpty(_fakeRegistration.RegisterSparseCalls);
    }

    [TestMethod]
    public async Task AddSparseIdentityAsync_KeepIdentityWithInstall_RegistersSparsePackage()
    {
        var (manifest, exePath) = ArrangeSparseInputs();

        var result = await _msixService.AddSparseIdentityAsync(
            exePath, manifest, noInstall: false, keepIdentity: true, TestTaskContext, TestContext.CancellationToken);

        // keepIdentity:true -> original identity is preserved (no ".debug" suffix).
        Assert.AreEqual("TestApp", result.PackageName);
        Assert.AreEqual("App", result.ApplicationId);
        // Install path -> unregister probe + sparse registration with external location.
        Assert.HasCount(1, _fakeRegistration.RegisterSparseCalls);
        Assert.AreEqual(Path.GetDirectoryName(exePath), _fakeRegistration.RegisterSparseCalls[0].ExternalLocation);
    }

    [TestMethod]
    public async Task AddSparseIdentityAsync_PlaceholderManifest_ResolvesForDebugIdentity()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("src");
        var manifest = new FileInfo(Path.Combine(srcDir.FullName, "Package.appxmanifest"));
        await File.WriteAllTextAsync(manifest.FullName, BuildRawManifest(exe: "$targetnametoken$.exe"), TestContext.CancellationToken);

        var binDir = _tempDirectory.CreateSubdirectory("bin");
        var exePath = Path.Combine(binDir.FullName, "TestApp.exe");
        await File.WriteAllTextAsync(exePath, "pe", TestContext.CancellationToken);

        var result = await _msixService.AddSparseIdentityAsync(
            exePath, manifest, noInstall: true, keepIdentity: false, TestTaskContext, TestContext.CancellationToken);

        // Placeholder Executable ($targetnametoken$) resolves to the entry-point name.
        Assert.AreEqual("TestApp.debug", result.PackageName);
    }

    [TestMethod]
    public async Task AddSparseIdentityAsync_ExeInManifestDirectory_SkipsAssetCopy()
    {
        // Manifest, exe and assets all in one directory -> the "same directory" skip branch.
        var dir = _tempDirectory.CreateSubdirectory("app");
        var assets = dir.CreateSubdirectory("Assets");
        await File.WriteAllTextAsync(Path.Combine(assets.FullName, "logo.png"), "png", TestContext.CancellationToken);
        var manifest = new FileInfo(Path.Combine(dir.FullName, "Package.appxmanifest"));
        await File.WriteAllTextAsync(manifest.FullName, BuildRawManifest(), TestContext.CancellationToken);
        var exePath = Path.Combine(dir.FullName, "TestApp.exe");
        await File.WriteAllTextAsync(exePath, "pe", TestContext.CancellationToken);

        var result = await _msixService.AddSparseIdentityAsync(
            exePath, manifest, noInstall: true, keepIdentity: false, TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual("TestApp.debug", result.PackageName);
        Assert.IsTrue(File.Exists(Path.Combine(dir.FullName, "resources.pri")), "PRI should still be generated when manifest and exe share a directory");
    }

    [TestMethod]
    public async Task AddSparseIdentityAsync_NullEntryPoint_ResolvesEntryPointTokenAndDerivesExe()
    {
        // No entrypoint argument: the executable is derived from the manifest's Application/@Executable.
        // The EntryPoint attribute carries a resolvable token, so the placeholder-resolution branch runs
        // while the Executable attribute itself stays concrete.
        var exeAbs = Path.Combine(_tempDirectory.FullName, "TestApp.exe");
        await File.WriteAllTextAsync(exeAbs, "pe", TestContext.CancellationToken);
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "Package.appxmanifest"));
        var content = BuildRawManifest(exe: exeAbs).Replace("Windows.FullTrustApplication", "$targetentrypoint$");
        await File.WriteAllTextAsync(manifest.FullName, content, TestContext.CancellationToken);

        var result = await _msixService.AddSparseIdentityAsync(
            entryPointPath: null, manifest, noInstall: true, keepIdentity: false, TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual("TestApp.debug", result.PackageName);
    }

    [TestMethod]
    public async Task AddSparseIdentityAsync_NullEntryPoint_ExecutablePlaceholder_Throws()
    {
        // The Executable attribute is itself a placeholder and no entrypoint is provided, so the
        // executable cannot be resolved -> the workflow fails fast with an actionable error.
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "Package.appxmanifest"));
        await File.WriteAllTextAsync(manifest.FullName, BuildRawManifest(exe: "$targetnametoken$.exe"), TestContext.CancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _msixService.AddSparseIdentityAsync(
                entryPointPath: null, manifest, noInstall: true, keepIdentity: false, TestTaskContext, TestContext.CancellationToken));
    }

    // ---- AddLooseLayoutIdentityAsync (non-MSBuild edge/error paths) ----------------

    [TestMethod]
    public async Task AddLooseLayoutIdentityAsync_RawManifest_ExecutableMissing_ThrowsFileNotFound()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("raw-input");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "Package.appxmanifest"));
        // Executable declared in manifest but not present in the input/output layout.
        await File.WriteAllTextAsync(srcManifest.FullName, BuildRawManifest(exe: "Missing.exe"), TestContext.CancellationToken);

        var output = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            _msixService.AddLooseLayoutIdentityAsync(srcManifest, srcDir, output, TestTaskContext, LayoutReconciliation.Exact, cancellationToken: TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task AddLooseLayoutIdentityAsync_RawManifest_RenamesExePriToResourcesPri()
    {
        var srcDir = _tempDirectory.CreateSubdirectory("raw-input");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "Package.appxmanifest"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildRawManifest(), TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(srcDir.FullName, "TestApp.exe"), "pe", TestContext.CancellationToken);
        // A pri named after the executable should be renamed to resources.pri.
        await File.WriteAllTextAsync(Path.Combine(srcDir.FullName, "TestApp.pri"), "pri-bytes", TestContext.CancellationToken);

        var output = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));

        var result = await _msixService.AddLooseLayoutIdentityAsync(
            srcManifest, srcDir, output, TestTaskContext, LayoutReconciliation.Exact, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual("TestApp", result.PackageName);
        Assert.IsTrue(File.Exists(Path.Combine(output.FullName, "resources.pri")), "TestApp.pri should be renamed to resources.pri");
        Assert.IsFalse(File.Exists(Path.Combine(output.FullName, "TestApp.pri")), "The original <exe>.pri should no longer exist after rename");
    }

    // ---- Executable manifest embedding (mt.exe) ----------------------------------

    private Task InvokeEmbedManifestFileToExeAsync(FileInfo exe, FileInfo manifest) =>
        (Task)EmbedManifestFileToExeMethod.Invoke(
            _msixService, [exe, manifest, TestTaskContext, TestContext.CancellationToken])!;

    [TestMethod]
    public async Task AddSparseIdentityAsync_ExecutableHasExistingManifest_MergesAndSkipsDuplicateIdentity()
    {
        // mt.exe -inputresource now "extracts" a manifest that already declares an
        // assemblyIdentity, so the identity-merge branches are exercised.
        _fakeBuildTools.SimulateExistingExeManifest = true;
        var (manifest, exePath) = ArrangeSparseInputs();

        var result = await _msixService.AddSparseIdentityAsync(
            exePath, manifest, noInstall: true, keepIdentity: false, TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual("TestApp.debug", result.PackageName);
        // Because an existing manifest was found, mt.exe should have been asked to MERGE
        // (-manifest ... -out) rather than copy the new manifest verbatim.
        Assert.IsTrue(
            _fakeBuildTools.MtInvocations.Any(a =>
                a.Contains("-manifest", StringComparison.OrdinalIgnoreCase) &&
                a.Contains("-out:", StringComparison.OrdinalIgnoreCase)),
            "Expected an mt.exe merge invocation when the executable already carries a manifest");
    }

    [TestMethod]
    public async Task AddSparseIdentityAsync_ManifestExtractionFails_FallsBackWithoutThrowing()
    {
        // mt.exe -inputresource throws -> TryExtractManifestFromExeAsync swallows it and
        // reports "no existing manifest", so the flow proceeds with the new manifest.
        _fakeBuildTools.ThrowWhen = (tool, args) =>
            tool.Contains("mt", StringComparison.OrdinalIgnoreCase) &&
            args.Contains("-inputresource", StringComparison.OrdinalIgnoreCase);
        var (manifest, exePath) = ArrangeSparseInputs();

        var result = await _msixService.AddSparseIdentityAsync(
            exePath, manifest, noInstall: true, keepIdentity: false, TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual("TestApp.debug", result.PackageName);
    }

    [TestMethod]
    public async Task EmbedManifestFileToExeAsync_ExecutableMissing_ThrowsFileNotFound()
    {
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "some.manifest"));
        await File.WriteAllTextAsync(manifest.FullName, "<assembly/>", TestContext.CancellationToken);
        var missingExe = new FileInfo(Path.Combine(_tempDirectory.FullName, "missing.exe"));

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(
            () => InvokeEmbedManifestFileToExeAsync(missingExe, manifest));
    }

    [TestMethod]
    public async Task EmbedManifestFileToExeAsync_ManifestMissing_ThrowsFileNotFound()
    {
        var exe = new FileInfo(Path.Combine(_tempDirectory.FullName, "app.exe"));
        await File.WriteAllTextAsync(exe.FullName, "pe", TestContext.CancellationToken);
        var missingManifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "missing.manifest"));

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(
            () => InvokeEmbedManifestFileToExeAsync(exe, missingManifest));
    }

    [TestMethod]
    public async Task EmbedManifestFileToExeAsync_MtToolFails_ThrowsInvalidOperation()
    {
        // mt.exe fails while writing the merged manifest back into the executable -> the
        // failure is wrapped in an InvalidOperationException.
        _fakeBuildTools.ThrowWhen = (tool, args) =>
            tool.Contains("mt", StringComparison.OrdinalIgnoreCase) &&
            args.Contains("-outputresource", StringComparison.OrdinalIgnoreCase);
        var exe = new FileInfo(Path.Join(_tempDirectory.FullName, "app.exe"));
        await File.WriteAllTextAsync(exe.FullName, "pe", TestContext.CancellationToken);
        var manifest = new FileInfo(Path.Join(_tempDirectory.FullName, "new.manifest"));
        await File.WriteAllTextAsync(manifest.FullName, "<assembly/>", TestContext.CancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => InvokeEmbedManifestFileToExeAsync(exe, manifest));
    }

    [TestMethod]
    public async Task EmbedManifestFileToExeAsync_DoesNotDeleteUserFilesNamedLikeTempManifests()
    {
        // Regression: the manifest temp files used while embedding must live under the system
        // temp directory, not next to the exe, so they never silently clobber a user's own files
        // that happen to share those legacy names.
        _fakeBuildTools.SimulateExistingExeManifest = true;
        var (_, exePath) = ArrangeSparseInputs();
        var exe = new FileInfo(exePath);
        var manifest = new FileInfo(Path.Join(_tempDirectory.FullName, "new.manifest"));
        await File.WriteAllTextAsync(
            manifest.FullName,
            "<assembly xmlns=\"urn:schemas-microsoft-com:asm.v1\" manifestVersion=\"1.0\"/>",
            TestContext.CancellationToken);

        var planted = LegacyTempManifestNames
            .Select(n => new FileInfo(Path.Join(exe.DirectoryName!, n)))
            .ToArray();
        foreach (var file in planted)
        {
            await File.WriteAllTextAsync(file.FullName, "user-content", TestContext.CancellationToken);
        }

        await InvokeEmbedManifestFileToExeAsync(exe, manifest);

        foreach (var file in planted)
        {
            file.Refresh();
            Assert.IsTrue(file.Exists, $"User file '{file.Name}' next to the exe must survive manifest embedding");
            Assert.AreEqual("user-content", await File.ReadAllTextAsync(file.FullName, TestContext.CancellationToken));
        }
    }

    [TestMethod]
    public void RemoveMsixElements_StripsExistingMsixIdentity()
    {
        // Regression: re-branding an exe must be idempotent. Any <msix> already present in the
        // extracted manifest has to be removed before the mt.exe merge, otherwise mt.exe fails
        // with c1010001 ("Values of attribute ... not equal") when the identity differs.
        var manifestFile = new FileInfo(Path.Join(_tempDirectory.FullName, "sxs.manifest"));
        File.WriteAllText(
            manifestFile.FullName,
            "<assembly xmlns=\"urn:schemas-microsoft-com:asm.v1\" manifestVersion=\"1.0\">" +
            "<msix xmlns=\"urn:schemas-microsoft-com:msix.v1\" publisher=\"CN=Old\" packageName=\"Old.App\" applicationId=\"App\"/>" +
            "<assemblyIdentity version=\"1.0.0.0\" name=\"Some.App\" type=\"win32\"/></assembly>");

        RemoveMsixElementsMethod.Invoke(null, [manifestFile, TestTaskContext]);

        var content = File.ReadAllText(manifestFile.FullName);
        StringAssert.DoesNotMatch(content, new Regex("<msix", RegexOptions.IgnoreCase), "the stale <msix> identity should be removed");
        StringAssert.Contains(content, "assemblyIdentity", StringComparison.Ordinal);
    }

    // ---- PRI generation failure paths --------------------------------------------

    [TestMethod]
    public async Task AddSparseIdentityAsync_PriGenerationFails_ContinuesWithWarning()
    {
        // makepri failing during sparse PRI staging must be swallowed (best-effort).
        _fakeBuildTools.ThrowWhen = (tool, _) => tool.Contains("makepri", StringComparison.OrdinalIgnoreCase);
        var (manifest, exePath) = ArrangeSparseInputs();

        var result = await _msixService.AddSparseIdentityAsync(
            exePath, manifest, noInstall: true, keepIdentity: false, TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual("TestApp.debug", result.PackageName);
        // PRI generation failed, so resources.pri should NOT have been produced next to the exe.
        Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(exePath)!, "resources.pri")));
    }

    [TestMethod]
    public async Task AddLooseLayoutIdentityAsync_RawManifest_PriGenerationFails_ContinuesWithWarning()
    {
        _fakeBuildTools.ThrowWhen = (tool, _) => tool.Contains("makepri", StringComparison.OrdinalIgnoreCase);
        var srcDir = _tempDirectory.CreateSubdirectory("raw-input");
        var srcManifest = new FileInfo(Path.Combine(srcDir.FullName, "Package.appxmanifest"));
        await File.WriteAllTextAsync(srcManifest.FullName, BuildRawManifest(), TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(srcDir.FullName, "TestApp.exe"), "pe", TestContext.CancellationToken);

        var output = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "layout"));

        // The PRI failure is swallowed; registration still proceeds and returns the identity.
        var result = await _msixService.AddLooseLayoutIdentityAsync(
            srcManifest, srcDir, output, TestTaskContext, LayoutReconciliation.Exact, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual("TestApp", result.PackageName);
    }

    // ---- GenerateSparsePackageStructureAsync direct edge cases -------------------

    [TestMethod]
    public async Task GenerateSparsePackageStructureAsync_ExistingDebugDirectory_IsRecreated()
    {
        var (manifest, exePath) = ArrangeSparseInputs();

        // First call creates the debug directory and an initial resources.pri next to the exe.
        await _msixService.GenerateSparsePackageStructureAsync(
            manifest, exePath, keepIdentity: false, null, TestTaskContext, TestContext.CancellationToken);

        // Second call must delete the pre-existing debug directory and replace the
        // pre-existing target resources.pri. keepIdentity:true keeps the original identity.
        var (debugManifestPath, debugIdentity) = await _msixService.GenerateSparsePackageStructureAsync(
            manifest, exePath, keepIdentity: true, null, TestTaskContext, TestContext.CancellationToken);

        Assert.IsTrue(File.Exists(debugManifestPath.FullName));
        Assert.AreEqual("TestApp", debugIdentity.PackageName);
    }

    // ---- EnsureWindowsAppRuntimeInstalledAsync -----------------------------------

    private static DotNetPackageListJson WinAppSdkPackageList(string version = "1.6.240701") =>
        new([
            new DotNetProject([
                new DotNetFramework(
                    "net8.0-windows10.0.19041.0",
                    [new DotNetPackage("Microsoft.WindowsAppSDK", version, version)],
                    [])
            ])
        ]);

    private Task InvokeEnsureWindowsAppRuntimeInstalledAsync(DotNetPackageListJson? list) =>
        (Task)EnsureWindowsAppRuntimeInstalledMethod.Invoke(
            _msixService, [list, null, TestTaskContext, TestContext.CancellationToken, false])!;

    [TestMethod]
    public async Task EnsureWindowsAppRuntimeInstalledAsync_InstallsMissingPackages()
    {
        _fakeWindowsAppRuntime.MsixDirectory = _tempDirectory.CreateSubdirectory("runtime-msix");
        _fakeWindowsAppRuntime.InstallRuntimeResult = (InstalledCount: 3, ErrorCount: 0);

        await InvokeEnsureWindowsAppRuntimeInstalledAsync(WinAppSdkPackageList());

        Assert.HasCount(1, _fakeWindowsAppRuntime.InstallRuntimeCalls);
        var messages = TestTask.SubTasks.OfType<StatusMessageTask>().Select(t => t.CompletedMessage ?? string.Empty).ToList();
        Assert.IsTrue(
            messages.Any(m => m.Contains("Installed 3 Windows App Runtime package(s)", StringComparison.OrdinalIgnoreCase)),
            $"Expected a success message naming the 3 installed packages. Messages:\n{string.Join("\n", messages)}");
    }

    [TestMethod]
    public async Task EnsureWindowsAppRuntimeInstalledAsync_ReportsInstallErrors()
    {
        _fakeWindowsAppRuntime.MsixDirectory = _tempDirectory.CreateSubdirectory("runtime-msix");
        _fakeWindowsAppRuntime.InstallRuntimeResult = (InstalledCount: 0, ErrorCount: 2);

        await InvokeEnsureWindowsAppRuntimeInstalledAsync(WinAppSdkPackageList());

        Assert.HasCount(1, _fakeWindowsAppRuntime.InstallRuntimeCalls);
        var messages = TestTask.SubTasks.OfType<StatusMessageTask>().Select(t => t.CompletedMessage ?? string.Empty).ToList();
        Assert.IsTrue(
            messages.Any(m => m.Contains("2 runtime package(s) failed to install", StringComparison.OrdinalIgnoreCase)),
            $"Expected a warning naming the 2 failed installs. Messages:\n{string.Join("\n", messages)}");
    }

    [TestMethod]
    public async Task EnsureWindowsAppRuntimeInstalledAsync_RuntimeDirNotFound_SkipsInstall()
    {
        // FindWindowsAppSdkMsixDirectory returns null -> nothing to install.
        _fakeWindowsAppRuntime.MsixDirectory = null;

        await InvokeEnsureWindowsAppRuntimeInstalledAsync(WinAppSdkPackageList());

        Assert.IsEmpty(_fakeWindowsAppRuntime.InstallRuntimeCalls);
    }

    [TestMethod]
    public async Task EnsureWindowsAppRuntimeInstalledAsync_ProjectMode_ForwardsNoRestoreToPackageList()
    {
        // C43: `dotnet list package` performs an implicit restore on current SDKs, so a run that
        // requested --no-restore must forward it to runtime discovery too. A package list without a
        // Windows App SDK reference makes the public entry return right after the discovery pass —
        // enough to prove the flag is threaded into GetPackageListAsync.
        _fakeDotNet.PackageListResult = new DotNetPackageListJson(
        [
            new DotNetProject(
            [
                new DotNetFramework(
                    "net10.0-windows10.0.19041.0",
                    [new DotNetPackage("SomeOther.Package", "1.0.0", "1.0.0")],
                    [])
            ])
        ]);

        var csproj = new FileInfo(Path.Combine(_tempDirectory.FullName, "App.csproj"));

        await _msixService.EnsureWindowsAppRuntimeInstalledAsync(
            csproj, "x64", framework: null, noRestore: true, TestTaskContext, TestContext.CancellationToken);
        Assert.AreEqual(true, _fakeDotNet.LastGetPackageListNoRestore,
            "--no-restore must be forwarded to dotnet list package during runtime discovery");

        await _msixService.EnsureWindowsAppRuntimeInstalledAsync(
            csproj, "x64", framework: null, noRestore: false, TestTaskContext, TestContext.CancellationToken);
        Assert.AreEqual(false, _fakeDotNet.LastGetPackageListNoRestore,
            "runtime discovery must restore normally when --no-restore was not requested");
    }

    [TestMethod]
    public async Task EnsureWindowsAppRuntimeInstalledAsync_RefsSdkButExactRuntimeMissing_FailsClosed()
    {
        // M4 (gate 1, Identity.cs:454-466): the app positively references the Windows App SDK, but the
        // exact runtime it was built against can't be located (empty expected-packages), so the runtime
        // can't be version-verified. Falling open would risk launching against an unrelated registered
        // runtime, so it must fail closed with an actionable error instead of silently continuing.
        _fakeDotNet.PackageListResult = WinAppSdkPackageList();
        _fakeWindowsAppRuntime.MsixDirectory = null; // exact runtime unavailable -> empty expected list
        var csproj = new FileInfo(Path.Combine(_tempDirectory.FullName, "App.csproj"));

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _msixService.EnsureWindowsAppRuntimeInstalledAsync(
                csproj, "x64", framework: null, noRestore: false, TestTaskContext, TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "could not be located");
    }

    [TestMethod]
    public async Task EnsureWindowsAppRuntimeInstalledAsync_RuntimeNotRegisteredAfterInstall_FailsClosed()
    {
        // M4 (gate 2, Identity.cs:483-490): the exact runtime packages ARE resolved (non-empty expected
        // list, so gate 1 is skipped), but after the install attempt the Framework + DDLM still isn't
        // registered for the target arch. A missing runtime must abort the launch with an actionable error
        // rather than being treated as success.
        _fakeDotNet.PackageListResult = WinAppSdkPackageList();
        _fakeWindowsAppRuntime.MsixDirectory = _tempDirectory.CreateSubdirectory("runtime-msix");
        _fakeWindowsAppRuntime.InstallRuntimePackages =
            [("Microsoft.WindowsAppRuntime.1.6", "1.6.240701"), ("Microsoft.WinAppRuntime.DDLM", "1.6.240701")];
        _fakeWindowsAppRuntime.IsRuntimeRegisteredResult = false;
        var csproj = new FileInfo(Path.Combine(_tempDirectory.FullName, "App.csproj"));

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _msixService.EnsureWindowsAppRuntimeInstalledAsync(
                csproj, "x64", framework: null, noRestore: false, TestTaskContext, TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "not registered");
    }

    [TestMethod]
    public async Task EnsureWindowsAppRuntimeInstalledAsync_RuntimeResolvedAndRegistered_Succeeds()
    {
        // Complement to the fail-closed gates: when the exact runtime resolves AND is registered for the
        // target arch, the method completes without throwing (the happy path the gates guard).
        _fakeDotNet.PackageListResult = WinAppSdkPackageList();
        _fakeWindowsAppRuntime.MsixDirectory = _tempDirectory.CreateSubdirectory("runtime-msix");
        _fakeWindowsAppRuntime.InstallRuntimePackages =
            [("Microsoft.WindowsAppRuntime.1.6", "1.6.240701"), ("Microsoft.WinAppRuntime.DDLM", "1.6.240701")];
        _fakeWindowsAppRuntime.IsRuntimeRegisteredResult = true;
        var csproj = new FileInfo(Path.Combine(_tempDirectory.FullName, "App.csproj"));

        await _msixService.EnsureWindowsAppRuntimeInstalledAsync(
            csproj, "x64", framework: null, noRestore: false, TestTaskContext, TestContext.CancellationToken);

        Assert.HasCount(1, _fakeWindowsAppRuntime.InstallRuntimeCalls);
    }

    [TestMethod]
    public async Task EnsureWindowsAppRuntimeInstalledAsync_UnresolvedList_FailsOpenWithWarning()
    {
        // M4 (tolerant branch, Identity.cs:468-473): when the package list can't be resolved we cannot
        // positively confirm the SDK reference, so an empty expected list must NOT fail closed — it warns
        // and proceeds (fail-open) rather than blocking a launch we can't reason about.
        _fakeDotNet.PackageListResult = null; // unresolved -> referencesWindowsAppSdk == false
        _fakeWindowsAppRuntime.MsixDirectory = null;
        _fakeWindowsAppRuntime.IsRuntimeRegisteredResult = true;
        var csproj = new FileInfo(Path.Combine(_tempDirectory.FullName, "App.csproj"));

        await _msixService.EnsureWindowsAppRuntimeInstalledAsync(
            csproj, "x64", framework: null, noRestore: false, TestTaskContext, TestContext.CancellationToken);

        var messages = TestTask.SubTasks.OfType<StatusMessageTask>().Select(t => t.CompletedMessage ?? string.Empty).ToList();
        Assert.IsTrue(
            messages.Any(m => m.Contains("Could not determine the exact Windows App Runtime", StringComparison.OrdinalIgnoreCase)),
            $"Expected a fail-open warning when the package list is unresolved. Messages:\n{string.Join("\n", messages)}");
    }

    // ---- FilterPackageListToFramework (C2: TFM-aware runtime resolution) ----------

    private static DotNetPackageListJson MultiTargetedWinAppSdkList() =>
        new([
            new DotNetProject([
                new DotNetFramework(
                    "net8.0-windows10.0.19041.0",
                    [new DotNetPackage("Microsoft.WindowsAppSDK", "1.5.240311", "1.5.240311")],
                    []),
                new DotNetFramework(
                    "net10.0-windows10.0.26100.0",
                    [new DotNetPackage("Microsoft.WindowsAppSDK", "1.7.250101", "1.7.250101")],
                    [])
            ])
        ]);

    [TestMethod]
    public void FilterPackageListToFramework_MultiTargeted_NarrowsToSelectedTfm()
    {
        var filtered = MsixService.FilterPackageListToFramework(MultiTargetedWinAppSdkList(), "net10.0-windows10.0.26100.0");

        var frameworks = filtered!.Projects[0].Frameworks;
        Assert.HasCount(1, frameworks);
        Assert.AreEqual("net10.0-windows10.0.26100.0", frameworks[0].Framework);
        Assert.AreEqual("1.7.250101", frameworks[0].TopLevelPackages[0].ResolvedVersion,
            "the retained framework must carry the SDK version for the built TFM, not the sibling's");
    }

    [TestMethod]
    public void FilterPackageListToFramework_NullFramework_ReturnsListUnchanged()
    {
        var list = MultiTargetedWinAppSdkList();

        var filtered = MsixService.FilterPackageListToFramework(list, null);

        Assert.AreSame(list, filtered, "a null TFM must not narrow the list (fail-open for single-target/folder mode)");
    }

    [TestMethod]
    public void FilterPackageListToFramework_TfmNotPresent_KeepsAllFrameworks()
    {
        // An unexpected moniker mismatch must not blank out the SDK reference — keep every framework.
        var filtered = MsixService.FilterPackageListToFramework(MultiTargetedWinAppSdkList(), "net9.0-windows10.0.22621.0");

        Assert.HasCount(2, filtered!.Projects[0].Frameworks);
    }

    // ---- ReferencesWindowsAppSdk (runtime-prep skip gate, review NOTE) ------------

    [TestMethod]
    public void ReferencesWindowsAppSdk_WinAppSdkTopLevel_ReturnsTrue()
    {
        Assert.IsTrue(MsixService.ReferencesWindowsAppSdk(WinAppSdkPackageList()));
    }

    [TestMethod]
    public void ReferencesWindowsAppSdk_WinAppSdkTransitive_ReturnsTrue()
    {
        var list = new DotNetPackageListJson([
            new DotNetProject([
                new DotNetFramework(
                    "net8.0-windows10.0.19041.0",
                    [new DotNetPackage("Contoso.Ui", "1.0.0", "1.0.0")],
                    [new DotNetPackage("Microsoft.WindowsAppSDK", "1.6.240701", "1.6.240701")])
            ])
        ]);

        Assert.IsTrue(MsixService.ReferencesWindowsAppSdk(list));
    }

    [TestMethod]
    public void ReferencesWindowsAppSdk_NoWinAppSdk_ReturnsFalse()
    {
        // A plain console/desktop Exe that does not reference the Windows App SDK: runtime prep is
        // wasted work and the public entry point skips it based on this classification.
        var list = new DotNetPackageListJson([
            new DotNetProject([
                new DotNetFramework(
                    "net8.0-windows10.0.19041.0",
                    [new DotNetPackage("Newtonsoft.Json", "13.0.3", "13.0.3")],
                    [new DotNetPackage("System.Text.Json", "8.0.0", "8.0.0")])
            ])
        ]);

        Assert.IsFalse(MsixService.ReferencesWindowsAppSdk(list));
    }

    [TestMethod]
    public void ReferencesWindowsAppSdk_NoProjectsOrPackages_ReturnsFalse()
    {
        Assert.IsFalse(MsixService.ReferencesWindowsAppSdk(new DotNetPackageListJson([])));
        Assert.IsFalse(MsixService.ReferencesWindowsAppSdk(new DotNetPackageListJson([
            new DotNetProject([new DotNetFramework("net8.0", [], [])])
        ])));
    }

    // ---- UnregisterExistingPackageAsync error handling ---------------------------

    [TestMethod]
    public async Task UnregisterExistingPackageAsync_TransientFailure_IsSwallowedAndReturnsFalse()
    {
        // A non-InvalidOperation/non-cancellation failure during inspection is logged and
        // treated as "no package removed" so it never blocks the caller's overall flow.
        _fakeRegistration.FindDevPackagesThrows = new InvalidDataException("transient deployment error");

        var removed = await _msixService.UnregisterExistingPackageAsync(
            "Contoso.App", TestTaskContext, cancellationToken: TestContext.CancellationToken);

        Assert.IsFalse(removed);
    }
}

/// <summary>
/// Build-tools fake that layers mt.exe scripting on top of <see cref="FakeBuildToolsService"/>:
/// it can simulate an executable that already carries an embedded Win32 manifest, and can throw
/// for selected tool invocations. makepri/makeappx emulation is delegated to the inner fake.
/// </summary>
internal sealed class ScriptedMtBuildToolsService : IBuildToolsService
{
    private static readonly Regex MtOutRegex =
        new("-out:?\\s*\"(?<path>[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly FakeBuildToolsService _inner = new() { Handler = FakeBuildToolsService.EmulateSdkToolOutput };

    /// <summary>Recorded mt.exe argument strings.</summary>
    public List<string> MtInvocations { get; } = [];

    /// <summary>
    /// When true, an mt.exe "-inputresource ... -out:PATH" invocation writes
    /// <see cref="ExtractedManifestXml"/> to PATH, simulating an executable that already
    /// carries an embedded Win32 manifest.
    /// </summary>
    public bool SimulateExistingExeManifest { get; set; }

    public string ExtractedManifestXml { get; set; } =
        "<assembly xmlns=\"urn:schemas-microsoft-com:asm.v1\" manifestVersion=\"1.0\">" +
        "<assemblyIdentity version=\"1.0.0.0\" name=\"Existing.App\" type=\"win32\"/></assembly>";

    /// <summary>
    /// When set, any tool invocation for which the predicate (executableName, arguments) returns
    /// true throws an <see cref="InvalidOperationException"/> instead of running.
    /// </summary>
    public Func<string, string, bool>? ThrowWhen { get; set; }

    public FileInfo? GetBuildToolPath(string toolName) => _inner.GetBuildToolPath(toolName);

    public Task<FileInfo> EnsureBuildToolAvailableAsync(string toolName, TaskContext taskContext, CancellationToken cancellationToken = default) =>
        _inner.EnsureBuildToolAvailableAsync(toolName, taskContext, cancellationToken);

    public Task<DirectoryInfo?> EnsureBuildToolsAsync(TaskContext taskContext, bool forceLatest = false, CancellationToken cancellationToken = default) =>
        _inner.EnsureBuildToolsAsync(taskContext, forceLatest, cancellationToken);

    public Task<(string stdout, string stderr)> RunBuildToolAsync(Tool tool, string arguments, TaskContext taskContext, bool printErrors = true, FileInfo? toolPathOverride = null, IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        if (ThrowWhen?.Invoke(tool.ExecutableName, arguments) == true)
        {
            throw new InvalidOperationException($"Simulated {tool.ExecutableName} failure");
        }

        if (tool.ExecutableName.Contains("mt", StringComparison.OrdinalIgnoreCase))
        {
            MtInvocations.Add(arguments);
            if (SimulateExistingExeManifest && arguments.Contains("-inputresource", StringComparison.OrdinalIgnoreCase))
            {
                var match = MtOutRegex.Match(arguments);
                if (match.Success)
                {
                    var outPath = match.Groups["path"].Value;
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                    File.WriteAllText(outPath, ExtractedManifestXml);
                }
            }
            return Task.FromResult<(string, string)>((string.Empty, string.Empty));
        }

        return _inner.RunBuildToolAsync(tool, arguments, taskContext, printErrors, toolPathOverride, environment, workingDirectory, cancellationToken);
    }
}


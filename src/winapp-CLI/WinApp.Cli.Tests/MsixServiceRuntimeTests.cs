// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Test double for <see cref="IWinmdService"/> that returns pre-configured WinRT components and
/// activatable classes, so the third-party WinRT manifest paths can be driven without a real
/// .winmd binary or NuGet cache layout.
/// </summary>
internal sealed class FakeWinmdService : IWinmdService
{
    public List<WinRTComponent> Components { get; } = [];
    public Dictionary<string, IReadOnlyList<string>> ClassesByWinmd { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> GetActivatableClasses(FileInfo winmdPath)
        => ClassesByWinmd.TryGetValue(winmdPath.Name, out var classes) ? classes : [];

    public IReadOnlyList<WinRTComponent> DiscoverWinRTComponents(
        DirectoryInfo nugetCacheDir,
        Dictionary<string, string> packages,
        string architecture,
        IReadOnlySet<string>? excludePackageNames = null)
        => Components;
}

/// <summary>
/// Tests for the runtime-staging helpers in <c>MsixService.Runtime.cs</c>. The pure file/parse
/// helpers are exercised directly (via reflection for the private static members); no real
/// Windows App SDK runtime, makeappx, or mt.exe is invoked.
/// </summary>
[TestClass]
public class MsixServiceRuntimeTests : BaseCommandTests
{
    private MsixService _msixService = null!;
    private FakeWinmdService _winmdService = null!;

    private static readonly MethodInfo CopyRuntimeFilesMethod =
        typeof(MsixService).GetMethod("CopyRuntimeFilesAsync", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo GetRuntimePackageInfoMethod =
        typeof(MsixService).GetMethod("GetWindowsAppRuntimePackageInfo", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo GetComponentsMethod =
        typeof(MsixService).GetMethod("GetComponents", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo AddThirdPartyExtensionsMethod =
        typeof(MsixService).GetMethod("AddThirdPartyWinRTExtensionsToAppxManifestAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo AppendThirdPartyEntriesMethod =
        typeof(MsixService).GetMethod("AppendThirdPartyWinRTManifestEntriesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo UpdateWinAppSdkDependencyMethod =
        typeof(MsixService).GetMethod("UpdateWindowsAppSdkDependencyAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo PrepareRuntimeMethod =
        typeof(MsixService).GetMethod("PrepareRuntimeForPackagingAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo EmbedActivationManifestMethod =
        typeof(MsixService).GetMethod("EmbedActivationManifestToExeAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo PackSingleFolderMethod =
        typeof(MsixService).GetMethod("PackSingleFolderToMsixAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo ResolveXGenerateMethod =
        typeof(MsixService).GetMethod("ResolveResourceLanguageXGenerateAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo SignMsixMethod =
        typeof(MsixService).GetMethod("SignMsixPackageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        return services
            .AddSingleton<IBuildToolsService>(_ => new FakeBuildToolsService { Handler = FakeBuildToolsService.EmulateSdkToolOutput })
            .AddSingleton<INugetService, FakeNugetService>()
            .AddSingleton<IWinmdService, FakeWinmdService>();
    }

    [TestInitialize]
    public void SetupService()
    {
        _msixService = (MsixService)GetRequiredService<IMsixService>();
        _winmdService = (FakeWinmdService)GetRequiredService<IWinmdService>();
    }

    // ---- CopyRuntimeFilesAsync ----------------------------------------------------

    private Task InvokeCopyRuntimeFilesAsync(DirectoryInfo extracted, DirectoryInfo deployment)
    {
        return (Task)CopyRuntimeFilesMethod.Invoke(
            null, [extracted, deployment, TestTaskContext, CancellationToken.None])!;
    }

    [TestMethod]
    public async Task CopyRuntimeFilesAsync_CopiesMatchingPatternsPreservingStructure()
    {
        var extracted = _tempDirectory.CreateSubdirectory("extracted");
        await File.WriteAllTextAsync(Path.Combine(extracted.FullName, "Microsoft.ui.xaml.dll"), "dll", TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(extracted.FullName, "app.winmd"), "winmd", TestContext.CancellationToken);
        var nested = extracted.CreateSubdirectory("Assets");
        await File.WriteAllTextAsync(Path.Combine(nested.FullName, "logo.png"), "png", TestContext.CancellationToken);
        // A file that matches none of the runtime patterns must be ignored.
        await File.WriteAllTextAsync(Path.Combine(extracted.FullName, "readme.txt"), "txt", TestContext.CancellationToken);

        var deployment = _tempDirectory.CreateSubdirectory("deployment");

        await InvokeCopyRuntimeFilesAsync(extracted, deployment);

        Assert.IsTrue(File.Exists(Path.Combine(deployment.FullName, "Microsoft.ui.xaml.dll")), "*.dll should be copied");
        Assert.IsTrue(File.Exists(Path.Combine(deployment.FullName, "app.winmd")), "*.winmd should be copied");
        Assert.IsTrue(File.Exists(Path.Combine(deployment.FullName, "Assets", "logo.png")), "Nested *.png should preserve structure");
        Assert.IsFalse(File.Exists(Path.Combine(deployment.FullName, "readme.txt")), "Non-matching files should be skipped");
    }

    [TestMethod]
    public async Task CopyRuntimeFilesAsync_EmptyExtractedDirectory_CopiesNothing()
    {
        var extracted = _tempDirectory.CreateSubdirectory("extracted");
        var deployment = _tempDirectory.CreateSubdirectory("deployment");

        await InvokeCopyRuntimeFilesAsync(extracted, deployment);

        Assert.IsEmpty(deployment.GetFiles("*", SearchOption.AllDirectories));
    }

    // ---- GetWindowsAppRuntimePackageInfo ------------------------------------------

    private object? InvokeGetRuntimePackageInfo(DirectoryInfo msixDir)
    {
        return GetRuntimePackageInfoMethod.Invoke(
            null, [TestTaskContext, msixDir, CancellationToken.None]);
    }

    private DirectoryInfo CreateMsixInventory(params string[] inventoryLines)
    {
        var arch = WorkspaceSetupService.GetSystemArchitecture();
        var msixDir = _tempDirectory.CreateSubdirectory($"msix-{Guid.NewGuid():N}");
        var archDir = msixDir.CreateSubdirectory($"win10-{arch}");
        File.WriteAllLines(Path.Combine(archDir.FullName, "msix.inventory"), inventoryLines);
        return msixDir;
    }

    [TestMethod]
    public void GetWindowsAppRuntimePackageInfo_ValidInventory_ReturnsRuntimeNameAndVersion()
    {
        var msixDir = CreateMsixInventory(
            "Microsoft.WindowsAppRuntime.1.7.Framework.msix=Microsoft.WindowsAppRuntime.1.7.Framework_7000.522.1444.0_x64__8wekyb3d8bbwe",
            "Microsoft.WindowsAppRuntime.1.7.msix=Microsoft.WindowsAppRuntime.1.7_7000.522.1444.0_x64__8wekyb3d8bbwe");

        var result = InvokeGetRuntimePackageInfo(msixDir);

        Assert.IsNotNull(result);
        Assert.AreEqual("Microsoft.WindowsAppRuntime.1.7", result.GetType().GetProperty("RuntimeName")!.GetValue(result));
        Assert.AreEqual("7000.522.1444.0", result.GetType().GetProperty("MinVersion")!.GetValue(result));
    }

    [TestMethod]
    public void GetWindowsAppRuntimePackageInfo_NoInventoryFile_ReturnsNull()
    {
        var msixDir = _tempDirectory.CreateSubdirectory("empty-msix");

        var result = InvokeGetRuntimePackageInfo(msixDir);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void GetWindowsAppRuntimePackageInfo_OnlyFrameworkPackages_ReturnsNull()
    {
        var msixDir = CreateMsixInventory(
            "Microsoft.WindowsAppRuntime.1.7.Framework.msix=Microsoft.WindowsAppRuntime.1.7.Framework_7000.522.1444.0_x64__8wekyb3d8bbwe");

        var result = InvokeGetRuntimePackageInfo(msixDir);

        Assert.IsNull(result);
    }

    // ---- GetComponents ------------------------------------------------------------

    [TestMethod]
    public void GetComponents_ReturnsOnlyExistingAppxFragments()
    {
        var nugetService = GetRequiredService<INugetService>();
        var cacheDir = nugetService.GetNuGetGlobalPackagesDir();

        // Create a fragment for one package; the other package has no fragment on disk.
        var fragmentDir = Directory.CreateDirectory(
            Path.Combine(cacheDir.FullName, "contoso.winrt.component", "1.2.3", "runtimes-framework"));
        var fragmentPath = Path.Combine(fragmentDir.FullName, "package.appxfragment");
        File.WriteAllText(fragmentPath, "<fragment/>");

        var packageDependencies = new Dictionary<string, string>
        {
            ["Contoso.WinRT.Component"] = "1.2.3",
            ["Missing.Package"] = "9.9.9",
        };

        var result = (IEnumerable<FileInfo>)GetComponentsMethod.Invoke(_msixService, [packageDependencies])!;
        var files = result.ToList();

        Assert.HasCount(1, files);
        Assert.AreEqual(fragmentPath, files[0].FullName);
    }

    // ---- Third-party WinRT: AppxManifest InProcessServer extensions ----------------

    private const string MinimalPackageManifest =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
          <Identity Name="Contoso.App" Publisher="CN=Contoso" Version="1.0.0.0" ProcessorArchitecture="x64" />
          <Applications>
            <Application Id="App" />
          </Applications>
        </Package>
        """;

    private static DotNetPackageListJson PackageListWith(string id, string version) =>
        new([new DotNetProject([new DotNetFramework("net10.0", [new DotNetPackage(id, version, version)], [])])]);

    private async Task<string> InvokeAddThirdPartyExtensionsAsync(string manifest, DotNetPackageListJson? packageList)
    {
        return await (Task<string>)AddThirdPartyExtensionsMethod.Invoke(
            _msixService, [manifest, packageList, TestTaskContext, CancellationToken.None])!;
    }

    private async Task InvokeAppendThirdPartyEntriesAsync(StringBuilder sb, DotNetPackageListJson? packageList)
    {
        var arch = WorkspaceSetupService.GetSystemArchitecture();
        await (Task)AppendThirdPartyEntriesMethod.Invoke(
            _msixService, [sb, arch, packageList, TestTaskContext, CancellationToken.None])!;
    }

    [TestMethod]
    public async Task AddThirdPartyWinRTExtensions_AddsInProcessServerEntries()
    {
        _winmdService.Components.Add(new WinRTComponent(new FileInfo("Contoso.Widgets.winmd"), "Contoso.Widgets.dll"));
        _winmdService.ClassesByWinmd["Contoso.Widgets.winmd"] = ["Contoso.Widgets.Button", "Contoso.Widgets.Slider"];

        var result = await InvokeAddThirdPartyExtensionsAsync(MinimalPackageManifest, PackageListWith("Contoso.Widgets", "2.0.0"));

        Assert.Contains("windows.activatableClass.inProcessServer", result);
        Assert.Contains("Contoso.Widgets.dll", result);
        Assert.Contains("Contoso.Widgets.Button", result);
        Assert.Contains("Contoso.Widgets.Slider", result);
    }

    [TestMethod]
    public async Task AddThirdPartyWinRTExtensions_NoUserPackages_ReturnsOriginalManifest()
    {
        // No dotNetPackageList and no winapp.yaml → GetAllUserPackagesAsync returns empty.
        var result = await InvokeAddThirdPartyExtensionsAsync(MinimalPackageManifest, null);

        Assert.AreEqual(MinimalPackageManifest, result);
    }

    [TestMethod]
    public async Task AddThirdPartyWinRTExtensions_NoComponentsDiscovered_ReturnsOriginalManifest()
    {
        // Packages exist but the winmd service discovers no components.
        var result = await InvokeAddThirdPartyExtensionsAsync(MinimalPackageManifest, PackageListWith("Contoso.Widgets", "2.0.0"));

        Assert.AreEqual(MinimalPackageManifest, result);
    }

    [TestMethod]
    public async Task AddThirdPartyWinRTExtensions_ComponentWithoutClasses_ReturnsOriginalManifest()
    {
        // Component is discovered but exposes no activatable classes → nothing added.
        _winmdService.Components.Add(new WinRTComponent(new FileInfo("Empty.winmd"), "Empty.dll"));

        var result = await InvokeAddThirdPartyExtensionsAsync(MinimalPackageManifest, PackageListWith("Empty", "1.0.0"));

        Assert.AreEqual(MinimalPackageManifest, result);
    }

    // ---- Third-party WinRT: SxS manifest entries -----------------------------------

    [TestMethod]
    public async Task AppendThirdPartyWinRTEntries_AppendsActivatableClassEntries()
    {
        _winmdService.Components.Add(new WinRTComponent(new FileInfo("Contoso.Widgets.winmd"), "Contoso.Widgets.dll"));
        _winmdService.ClassesByWinmd["Contoso.Widgets.winmd"] = ["Contoso.Widgets.Button"];

        var sb = new StringBuilder();
        await InvokeAppendThirdPartyEntriesAsync(sb, PackageListWith("Contoso.Widgets", "2.0.0"));

        var xml = sb.ToString();
        Assert.Contains("<asmv3:file name='Contoso.Widgets.dll'>", xml);
        Assert.Contains("Contoso.Widgets.Button", xml);
    }

    [TestMethod]
    public async Task AppendThirdPartyWinRTEntries_SkipsDllAlreadyRegistered()
    {
        _winmdService.Components.Add(new WinRTComponent(new FileInfo("Contoso.Widgets.winmd"), "Contoso.Widgets.dll"));
        _winmdService.ClassesByWinmd["Contoso.Widgets.winmd"] = ["Contoso.Widgets.Button"];

        // Pre-seed the SxS manifest with the same DLL (as WinAppSDK fragments would) so it is deduped.
        var sb = new StringBuilder();
        sb.AppendLine("    <asmv3:file name='Contoso.Widgets.dll'>");
        sb.AppendLine("    </asmv3:file>");
        var lengthBefore = sb.Length;

        await InvokeAppendThirdPartyEntriesAsync(sb, PackageListWith("Contoso.Widgets", "2.0.0"));

        Assert.AreEqual(lengthBefore, sb.Length, "Already-registered DLL should not be appended again");
    }

    [TestMethod]
    public async Task AppendThirdPartyWinRTEntries_NoUserPackages_LeavesBuilderUnchanged()
    {
        var sb = new StringBuilder();
        await InvokeAppendThirdPartyEntriesAsync(sb, null);

        Assert.AreEqual(0, sb.Length);
    }

    // ---- UpdateWindowsAppSdkDependencyAsync ----------------------------------------

    private const string SdkPackageId = "Microsoft.WindowsAppSDK";
    private const string RuntimeIdentity =
        "Microsoft.WindowsAppRuntime.1.6_6000.318.240.0_x64__8wekyb3d8bbwe";

    /// <summary>
    /// Lays out a fake NuGet-cached Windows App SDK MSIX inventory so that
    /// <c>GetRuntimeMsixDirAsync</c> / <c>GetWindowsAppRuntimePackageInfo</c> resolve offline.
    /// </summary>
    private void ArrangeRuntimeMsixCache(string sdkVersion, string runtimeIdentity)
    {
        var cacheDir = GetRequiredService<INugetService>().GetNuGetGlobalPackagesDir();
        var arch = WorkspaceSetupService.GetSystemArchitecture();
        var msixArchDir = Directory.CreateDirectory(
            Path.Combine(cacheDir.FullName, SdkPackageId.ToLowerInvariant(), sdkVersion, "tools", "MSIX", $"win10-{arch}"));
        File.WriteAllLines(
            Path.Combine(msixArchDir.FullName, "msix.inventory"),
            [$"Microsoft.WindowsAppRuntime.msix={runtimeIdentity}"]);
    }

    private async Task<string> InvokeUpdateWinAppSdkDependencyAsync(string manifest, DotNetPackageListJson? packageList)
    {
        return await (Task<string>)UpdateWinAppSdkDependencyMethod.Invoke(
            _msixService, [manifest, packageList, TestTaskContext, CancellationToken.None])!;
    }

    private static string ManifestWithDependencies(string dependencyElements) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
          <Identity Name="Contoso.App" Publisher="CN=Contoso" Version="1.0.0.0" ProcessorArchitecture="x64" />
          <Dependencies>
        {dependencyElements}
          </Dependencies>
          <Applications>
            <Application Id="App" />
          </Applications>
        </Package>
        """;

    [TestMethod]
    public async Task UpdateWindowsAppSdkDependency_NoDependenciesElement_AddsDependency()
    {
        ArrangeRuntimeMsixCache("1.6.0", RuntimeIdentity);

        var result = await InvokeUpdateWinAppSdkDependencyAsync(MinimalPackageManifest, PackageListWith(SdkPackageId, "1.6.0"));

        Assert.Contains("<Dependencies", result);
        Assert.Contains("Microsoft.WindowsAppRuntime.1.6", result);
        Assert.Contains("6000.318.240.0", result);
    }

    [TestMethod]
    public async Task UpdateWindowsAppSdkDependency_ExistingRuntimeDependency_UpdatesInPlace()
    {
        ArrangeRuntimeMsixCache("1.6.0", RuntimeIdentity);
        var manifest = ManifestWithDependencies(
            "    <PackageDependency Name=\"Microsoft.WindowsAppRuntime.1.5\" MinVersion=\"5000.0.0.0\" Publisher=\"CN=Microsoft Corporation\" />");

        var result = await InvokeUpdateWinAppSdkDependencyAsync(manifest, PackageListWith(SdkPackageId, "1.6.0"));

        Assert.Contains("Microsoft.WindowsAppRuntime.1.6", result);
        Assert.Contains("6000.318.240.0", result);
        Assert.DoesNotContain("Microsoft.WindowsAppRuntime.1.5", result);
        Assert.DoesNotContain("5000.0.0.0", result);
    }

    [TestMethod]
    public async Task UpdateWindowsAppSdkDependency_DependenciesWithoutRuntime_AddsRuntimeDependency()
    {
        ArrangeRuntimeMsixCache("1.6.0", RuntimeIdentity);
        var manifest = ManifestWithDependencies(
            "    <PackageDependency Name=\"Microsoft.VCLibs.140.00\" MinVersion=\"14.0.0.0\" Publisher=\"CN=Microsoft Corporation\" />");

        var result = await InvokeUpdateWinAppSdkDependencyAsync(manifest, PackageListWith(SdkPackageId, "1.6.0"));

        Assert.Contains("Microsoft.VCLibs.140.00", result);
        Assert.Contains("Microsoft.WindowsAppRuntime.1.6", result);
    }

    [TestMethod]
    public async Task UpdateWindowsAppSdkDependency_SdkNotResolvable_ReturnsOriginalManifest()
    {
        // No MSIX cache arranged → the runtime MSIX directory cannot be resolved.
        var result = await InvokeUpdateWinAppSdkDependencyAsync(MinimalPackageManifest, PackageListWith(SdkPackageId, "9.9.9"));

        Assert.AreEqual(MinimalPackageManifest, result);
    }

    [TestMethod]
    public async Task UpdateWindowsAppSdkDependency_NoApplicationsElement_AppendsDependenciesToRoot()
    {
        ArrangeRuntimeMsixCache("1.6.0", RuntimeIdentity);
        // A manifest with neither <Dependencies> nor <Applications> exercises the
        // "append Dependencies to the root" fallback branch.
        var manifest =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="Contoso.App" Publisher="CN=Contoso" Version="1.0.0.0" ProcessorArchitecture="x64" />
            </Package>
            """;

        var result = await InvokeUpdateWinAppSdkDependencyAsync(manifest, PackageListWith(SdkPackageId, "1.6.0"));

        Assert.Contains("<Dependencies", result);
        Assert.Contains("Microsoft.WindowsAppRuntime.1.6", result);
    }

    [TestMethod]
    public async Task UpdateWindowsAppSdkDependency_InventoryHasOnlyFrameworkPackage_ReturnsOriginalManifest()
    {
        // MSIX directory resolves, but the inventory contains only a Framework package,
        // so GetWindowsAppRuntimePackageInfo yields null and the manifest is left unchanged.
        var arch = WorkspaceSetupService.GetSystemArchitecture();
        var cacheDir = GetRequiredService<INugetService>().GetNuGetGlobalPackagesDir();
        var archDir = Directory.CreateDirectory(
            Path.Combine(cacheDir.FullName, SdkPackageId.ToLowerInvariant(), "1.6.0", "tools", "MSIX", $"win10-{arch}"));
        await File.WriteAllLinesAsync(
            Path.Combine(archDir.FullName, "msix.inventory"),
            ["Microsoft.WindowsAppRuntime.1.6.Framework.msix=Microsoft.WindowsAppRuntime.1.6.Framework_6000.318.240.0_x64__8wekyb3d8bbwe"],
            TestContext.CancellationToken);

        var result = await InvokeUpdateWinAppSdkDependencyAsync(MinimalPackageManifest, PackageListWith(SdkPackageId, "1.6.0"));

        Assert.AreEqual(MinimalPackageManifest, result);
    }

    // ---- SetupSelfContainedAsync ---------------------------------------------------

    /// <summary>
    /// Creates the Windows App SDK <c>tools/MSIX/win10-{arch}</c> directory in the fake NuGet cache,
    /// optionally with an <c>msix.inventory</c>, plus a real (zip) runtime .msix package containing
    /// <paramref name="runtimeFileNames"/>. Returns the arch used.
    /// </summary>
    private string ArrangeRuntimeMsixPackage(string sdkVersion, string msixFileName, bool withInventory, params string[] runtimeFileNames)
    {
        var arch = WorkspaceSetupService.GetSystemArchitecture();
        var cacheDir = GetRequiredService<INugetService>().GetNuGetGlobalPackagesDir();
        var toolsMsixArchDir = Directory.CreateDirectory(
            Path.Combine(cacheDir.FullName, SdkPackageId.ToLowerInvariant(), sdkVersion, "tools", "MSIX", $"win10-{arch}"));

        if (withInventory)
        {
            File.WriteAllLines(
                Path.Combine(toolsMsixArchDir.FullName, "msix.inventory"),
                [$"{msixFileName}={RuntimeIdentity}"]);
        }

        var zipSrc = _tempDirectory.CreateSubdirectory($"zipsrc-{Guid.NewGuid():N}");
        foreach (var name in runtimeFileNames)
        {
            File.WriteAllText(Path.Combine(zipSrc.FullName, name), "content");
        }
        ZipFile.CreateFromDirectory(zipSrc.FullName, Path.Combine(toolsMsixArchDir.FullName, msixFileName));
        return arch;
    }

    [TestMethod]
    public async Task SetupSelfContainedAsync_WithInventory_ExtractsAndCopiesRuntimeFiles()
    {
        var arch = ArrangeRuntimeMsixPackage("1.6.0", "Microsoft.WindowsAppRuntime.1.6.msix", withInventory: true,
            "Microsoft.Foo.dll", "Contoso.winmd", "readme.txt");
        var winappDir = GetRequiredService<IWinappDirectoryService>().GetLocalWinappDirectory();

        await _msixService.SetupSelfContainedAsync(winappDir, arch, TestTaskContext, PackageListWith(SdkPackageId, "1.6.0"), TestContext.CancellationToken);

        var deploymentDir = Path.Combine(winappDir.FullName, "self-contained", arch, "deployment");
        Assert.IsTrue(File.Exists(Path.Combine(deploymentDir, "Microsoft.Foo.dll")), "*.dll should be copied to deployment");
        Assert.IsTrue(File.Exists(Path.Combine(deploymentDir, "Contoso.winmd")), "*.winmd should be copied to deployment");
        Assert.IsFalse(File.Exists(Path.Combine(deploymentDir, "readme.txt")), "Non-runtime files should not be copied");
    }

    [TestMethod]
    public async Task SetupSelfContainedAsync_WithoutInventory_FallsBackToFilePatternSearch()
    {
        // No inventory → the method falls back to Microsoft.WindowsAppRuntime.*.msix file search.
        var arch = ArrangeRuntimeMsixPackage("1.6.0", "Microsoft.WindowsAppRuntime.1.6.msix", withInventory: false,
            "Microsoft.Bar.dll");
        var winappDir = GetRequiredService<IWinappDirectoryService>().GetLocalWinappDirectory();

        await _msixService.SetupSelfContainedAsync(winappDir, arch, TestTaskContext, PackageListWith(SdkPackageId, "1.6.0"), TestContext.CancellationToken);

        var deploymentDir = Path.Combine(winappDir.FullName, "self-contained", arch, "deployment");
        Assert.IsTrue(File.Exists(Path.Combine(deploymentDir, "Microsoft.Bar.dll")));
    }

    [TestMethod]
    public async Task SetupSelfContainedAsync_MsixToolsDirectoryMissing_DoesNotStageRuntime()
    {
        // Create tools/MSIX (so GetRuntimeMsixDirAsync resolves) but NOT the win10-{arch} subfolder.
        // The internal DirectoryNotFoundException is caught by the sub-task wrapper, so nothing is staged.
        var cacheDir = GetRequiredService<INugetService>().GetNuGetGlobalPackagesDir();
        Directory.CreateDirectory(Path.Combine(cacheDir.FullName, SdkPackageId.ToLowerInvariant(), "1.6.0", "tools", "MSIX"));
        var winappDir = GetRequiredService<IWinappDirectoryService>().GetLocalWinappDirectory();
        var arch = WorkspaceSetupService.GetSystemArchitecture();

        await _msixService.SetupSelfContainedAsync(winappDir, arch, TestTaskContext, PackageListWith(SdkPackageId, "1.6.0"), TestContext.CancellationToken);

        var deploymentDir = Path.Combine(winappDir.FullName, "self-contained", arch, "deployment");
        Assert.IsFalse(Directory.Exists(deploymentDir), "No runtime files should be staged when the MSIX tools directory is missing");
    }

    [TestMethod]
    public async Task SetupSelfContainedAsync_RuntimeMsixDirectoryNotFound_DoesNotStageRuntime()
    {
        // Nothing arranged in the cache → GetRuntimeMsixDirAsync returns null; the resulting
        // DirectoryNotFoundException is caught by the sub-task wrapper.
        var winappDir = GetRequiredService<IWinappDirectoryService>().GetLocalWinappDirectory();
        var arch = WorkspaceSetupService.GetSystemArchitecture();

        await _msixService.SetupSelfContainedAsync(winappDir, arch, TestTaskContext, PackageListWith(SdkPackageId, "1.6.0"), TestContext.CancellationToken);

        var deploymentDir = Path.Combine(winappDir.FullName, "self-contained", arch, "deployment");
        Assert.IsFalse(Directory.Exists(deploymentDir), "No runtime files should be staged when the runtime MSIX directory is not found");
    }

    // ---- PrepareRuntimeForPackagingAsync -------------------------------------------

    [TestMethod]
    public async Task PrepareRuntimeForPackagingAsync_MergesRuntimeIntoStagingWithoutOverwriting()
    {
        var arch = ArrangeRuntimeMsixPackage("1.6.0", "Microsoft.WindowsAppRuntime.1.6.msix", withInventory: true,
            "Microsoft.Foo.dll", "resources.pri");

        // Staging already contains an app-owned resources.pri that must NOT be overwritten.
        var stagingDir = _tempDirectory.CreateSubdirectory("staging");
        var appPri = Path.Combine(stagingDir.FullName, "resources.pri");
        await File.WriteAllTextAsync(appPri, "APP-OWNED", TestContext.CancellationToken);

        var task = (Task<DirectoryInfo>)PrepareRuntimeMethod.Invoke(
            _msixService, [stagingDir, PackageListWith(SdkPackageId, "1.6.0"), TestTaskContext, TestContext.CancellationToken, arch])!;
        var runtimeSourceDir = await task;

        // Runtime DLL is merged into staging; app-owned resources.pri is preserved.
        Assert.IsTrue(File.Exists(Path.Combine(stagingDir.FullName, "Microsoft.Foo.dll")), "Runtime DLL should be merged into staging");
        Assert.AreEqual("APP-OWNED", await File.ReadAllTextAsync(appPri, TestContext.CancellationToken), "App-owned file must not be overwritten by runtime file");
        Assert.IsTrue(runtimeSourceDir.Exists);
        Assert.EndsWith(Path.Combine("self-contained", arch, "deployment"), runtimeSourceDir.FullName);
    }

    // ---- EmbedActivationManifestToExeAsync -----------------------------------------

    private Task InvokeEmbedActivationManifestAsync(FileInfo exe, DirectoryInfo deployment, FileInfo appxManifest, DotNetPackageListJson? packageList, string? targetArch = null)
    {
        return (Task)EmbedActivationManifestMethod.Invoke(
            _msixService, [exe, deployment, appxManifest, packageList, TestTaskContext, CancellationToken.None, targetArch])!;
    }

    [TestMethod]
    public async Task EmbedActivationManifestToExe_GeneratesManifestAndInvokesMtTool()
    {
        var exeDir = _tempDirectory.CreateSubdirectory("embed-app");
        var exe = new FileInfo(Path.Combine(exeDir.FullName, "App.exe"));
        await File.WriteAllTextAsync(exe.FullName, "MZ", TestContext.CancellationToken);

        var deployment = _tempDirectory.CreateSubdirectory("embed-deployment");
        await File.WriteAllTextAsync(Path.Combine(deployment.FullName, "Microsoft.ui.xaml.dll"), "dll", TestContext.CancellationToken);

        var appxManifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "WindowsAppSdk.AppxManifest.xml"));
        await File.WriteAllTextAsync(appxManifest.FullName, MinimalPackageManifest, TestContext.CancellationToken);

        await InvokeEmbedActivationManifestAsync(exe, deployment, appxManifest, PackageListWith(SdkPackageId, "1.6.0"));

        // The whole pipeline runs offline through the mocked build-tools seam: verify mt.exe was
        // asked to extract the current manifest and then embed the merged one into the executable.
        var invocations = ((FakeBuildToolsService)GetRequiredService<IBuildToolsService>()).Invocations;
        Assert.IsTrue(invocations.Any(i => i.ToolName.Contains("mt.exe", StringComparison.OrdinalIgnoreCase) && i.Arguments.Contains("-inputresource", StringComparison.OrdinalIgnoreCase)),
            "mt.exe should be invoked to extract the existing manifest");
        Assert.IsTrue(invocations.Any(i => i.ToolName.Contains("mt.exe", StringComparison.OrdinalIgnoreCase) && i.Arguments.Contains("-outputresource", StringComparison.OrdinalIgnoreCase)),
            "mt.exe should be invoked to embed the merged manifest into the executable");
        // The temporary manifest is always cleaned up in the finally block.
        Assert.IsFalse(File.Exists(Path.Combine(exeDir.FullName, "WindowsAppSDK_temp.manifest")), "Temp manifest should be removed");
    }

    [TestMethod]
    public async Task EmbedActivationManifestToExe_NoWindowsSdkPackages_Throws()
    {
        var exeDir = _tempDirectory.CreateSubdirectory("embed-nopkg");
        var exe = new FileInfo(Path.Combine(exeDir.FullName, "App.exe"));
        await File.WriteAllTextAsync(exe.FullName, "MZ", TestContext.CancellationToken);

        var deployment = _tempDirectory.CreateSubdirectory("embed-nopkg-deployment");
        var appxManifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "NoPkg.AppxManifest.xml"));
        await File.WriteAllTextAsync(appxManifest.FullName, MinimalPackageManifest, TestContext.CancellationToken);

        // No package list and no winapp.yaml → no Windows App SDK packages can be resolved.
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => InvokeEmbedActivationManifestAsync(exe, deployment, appxManifest, null));
    }

    // ---- MsixService.cs: resource-language & signing guards -------------------------

    [TestMethod]
    public async Task ResolveResourceLanguageXGenerate_NoPri_DefaultsToEnUs()
    {
        var manifest =
            "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\">\n" +
            "  <Resources>\n    <Resource Language=\"x-generate\" />\n  </Resources>\n</Package>";
        var inputFolder = _tempDirectory.CreateSubdirectory("xgen-no-pri");

        var result = await (Task<string>)ResolveXGenerateMethod.Invoke(
            _msixService, [manifest, inputFolder, TestTaskContext, CancellationToken.None])!;

        Assert.DoesNotContain("x-generate", result);
        Assert.Contains("en-US", result);
    }

    [TestMethod]
    public async Task ResolveResourceLanguageXGenerate_NoXGenerate_ReturnsUnchanged()
    {
        var manifest =
            "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\">\n" +
            "  <Resources>\n    <Resource Language=\"en-US\" />\n  </Resources>\n</Package>";
        var inputFolder = _tempDirectory.CreateSubdirectory("xgen-none");

        var result = await (Task<string>)ResolveXGenerateMethod.Invoke(
            _msixService, [manifest, inputFolder, TestTaskContext, CancellationToken.None])!;

        Assert.AreEqual(manifest, result);
    }

    [TestMethod]
    public async Task SignMsixPackage_GenerateDevCertWithoutPublisher_Throws()
    {
        var outputFolder = _tempDirectory.CreateSubdirectory("sign-nopub");
        var outputMsix = new FileInfo(Path.Combine(outputFolder.FullName, "app.msix"));
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "AppxManifest.xml"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => (Task)SignMsixMethod.Invoke(
            _msixService,
            [outputFolder, "", true, false, "MyApp", "", outputMsix, (FileInfo?)null, manifest, TestTaskContext, CancellationToken.None])!);
    }

    [TestMethod]
    public async Task SignMsixPackage_NoCertificateAndNoGenerate_Throws()
    {
        var outputFolder = _tempDirectory.CreateSubdirectory("sign-nocert");
        var outputMsix = new FileInfo(Path.Combine(outputFolder.FullName, "app.msix"));
        var manifest = new FileInfo(Path.Combine(_tempDirectory.FullName, "AppxManifest.xml"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => (Task)SignMsixMethod.Invoke(
            _msixService,
            [outputFolder, "", false, false, "MyApp", "Contoso", outputMsix, (FileInfo?)null, manifest, TestTaskContext, CancellationToken.None])!);
    }

    // ---- PackSingleFolderToMsixAsync: self-contained end-to-end --------------------

    /// <summary>
    /// Arranges a runtime MSIX package in the fake NuGet cache whose zip contains a valid,
    /// loadable <c>AppxManifest.xml</c> (required by the self-contained embed step) plus the
    /// given runtime DLLs. Returns the architecture used.
    /// </summary>
    private string ArrangeRuntimeMsixPackageWithManifest(string sdkVersion, string msixFileName, string appxManifestContent, params string[] runtimeDlls)
    {
        var arch = WorkspaceSetupService.GetSystemArchitecture();
        var cacheDir = GetRequiredService<INugetService>().GetNuGetGlobalPackagesDir();
        var toolsMsixArchDir = Directory.CreateDirectory(
            Path.Combine(cacheDir.FullName, SdkPackageId.ToLowerInvariant(), sdkVersion, "tools", "MSIX", $"win10-{arch}"));
        File.WriteAllLines(
            Path.Combine(toolsMsixArchDir.FullName, "msix.inventory"),
            [$"{msixFileName}={RuntimeIdentity}"]);

        var zipSrc = _tempDirectory.CreateSubdirectory($"rtzip-{Guid.NewGuid():N}");
        File.WriteAllText(Path.Combine(zipSrc.FullName, "AppxManifest.xml"), appxManifestContent);
        foreach (var dll in runtimeDlls)
        {
            File.WriteAllText(Path.Combine(zipSrc.FullName, dll), "dll");
        }
        ZipFile.CreateFromDirectory(zipSrc.FullName, Path.Combine(toolsMsixArchDir.FullName, msixFileName));
        return arch;
    }

    [TestMethod]
    public async Task PackSingleFolderToMsix_SelfContained_StagesRuntimeEmbedsManifestAndPacks()
    {
        // A runtime package (with an embeddable AppxManifest.xml) available in the cache.
        const string runtimeManifest =
            "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\">" +
            "<Identity Name=\"Runtime\" Publisher=\"CN=Microsoft\" Version=\"1.0.0.0\" /></Package>";
        var arch = ArrangeRuntimeMsixPackageWithManifest(
            "1.6.0", "Microsoft.WindowsAppRuntime.1.6.msix", runtimeManifest, "Microsoft.ui.xaml.dll");

        // App input folder with the executable referenced by the manifest.
        var inputFolder = _tempDirectory.CreateSubdirectory("sc-input");
        await File.WriteAllTextAsync(Path.Combine(inputFolder.FullName, "App.exe"), "MZ", TestContext.CancellationToken);
        var manifest =
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="Contoso.App" Publisher="CN=Contoso" Version="1.0.0.0" ProcessorArchitecture="{arch}" />
              <Applications>
                <Application Id="App" Executable="App.exe" />
              </Applications>
            </Package>
            """;
        var resolvedManifest = new FileInfo(Path.Combine(inputFolder.FullName, "appxmanifest.xml"));
        await File.WriteAllTextAsync(resolvedManifest.FullName, manifest, TestContext.CancellationToken);

        var outputMsix = new FileInfo(Path.Combine(_tempDirectory.CreateSubdirectory("sc-out").FullName, "app.msix"));

        await (Task)PackSingleFolderMethod.Invoke(_msixService, [
            inputFolder, manifest, resolvedManifest, outputMsix, TestTaskContext,
            true /*selfContained*/, "App.exe" /*executable*/, arch /*targetArch*/,
            true /*skipPri*/, PackageListWith(SdkPackageId, "1.6.0"), CancellationToken.None])!;

        // makeappx (mocked) produced the package, and mt.exe embedded the activation manifest.
        outputMsix.Refresh();
        Assert.IsTrue(outputMsix.Exists, "makeappx should have produced the .msix");
        var invocations = ((FakeBuildToolsService)GetRequiredService<IBuildToolsService>()).Invocations;
        Assert.IsTrue(invocations.Any(i => i.ToolName.Contains("mt.exe", StringComparison.OrdinalIgnoreCase)),
            "mt.exe should be invoked to embed the self-contained activation manifest");
        Assert.IsTrue(invocations.Any(i => i.ToolName.Contains("makeappx", StringComparison.OrdinalIgnoreCase)),
            "makeappx should be invoked to pack the staging folder");
    }

    // ---- Additional inventory / third-party / self-contained edge cases -------------

    [TestMethod]
    public void GetWindowsAppRuntimePackageInfo_IdentityWithoutUnderscore_ReturnsNull()
    {
        // A main (non-Framework) runtime entry whose PackageIdentity has no '_' separator
        // cannot be split into name/version, so it is treated as "not found".
        var msixDir = CreateMsixInventory(
            "Microsoft.WindowsAppRuntime.1.7.msix=Microsoft.WindowsAppRuntime.1.7");

        var result = InvokeGetRuntimePackageInfo(msixDir);

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task AppendThirdPartyWinRTEntries_ComponentWithoutClasses_LeavesBuilderUnchanged()
    {
        // Component discovered but exposes no activatable classes → skipped (continue).
        _winmdService.Components.Add(new WinRTComponent(new FileInfo("Empty.winmd"), "Empty.dll"));

        var sb = new StringBuilder();
        await InvokeAppendThirdPartyEntriesAsync(sb, PackageListWith("Empty", "1.0.0"));

        Assert.AreEqual(0, sb.Length, "A component with no activatable classes should append nothing");
    }

    [TestMethod]
    public async Task AddThirdPartyWinRTExtensions_DuplicateDllAlreadyRegistered_Skipped()
    {
        _winmdService.Components.Add(new WinRTComponent(new FileInfo("Contoso.Widgets.winmd"), "Contoso.Widgets.dll"));
        _winmdService.ClassesByWinmd["Contoso.Widgets.winmd"] = ["Contoso.Widgets.Button"];

        // The manifest already registers the same DLL via a <Path> element, so the
        // discovered component must be deduped rather than added a second time.
        const string manifestWithDll =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="Contoso.App" Publisher="CN=Contoso" Version="1.0.0.0" ProcessorArchitecture="x64" />
              <Applications>
                <Application Id="App">
                  <Extensions>
                    <Extension Category="windows.activatableClass.inProcessServer">
                      <InProcessServer>
                        <Path>Contoso.Widgets.dll</Path>
                      </InProcessServer>
                    </Extension>
                  </Extensions>
                </Application>
              </Applications>
            </Package>
            """;

        var result = await InvokeAddThirdPartyExtensionsAsync(manifestWithDll, PackageListWith("Contoso.Widgets", "2.0.0"));

        var occurrences = result.Split("Contoso.Widgets.dll").Length - 1;
        Assert.AreEqual(1, occurrences, "Already-registered DLL must not be added a second time");
    }

    [TestMethod]
    public async Task SetupSelfContainedAsync_EmptyMsixToolsDirectory_DoesNotStageRuntime()
    {
        // tools/MSIX/win10-{arch} exists but is empty (no inventory, no .msix files), so the
        // file-pattern fallback finds nothing and throws (swallowed by the sub-task wrapper).
        var arch = WorkspaceSetupService.GetSystemArchitecture();
        var cacheDir = GetRequiredService<INugetService>().GetNuGetGlobalPackagesDir();
        Directory.CreateDirectory(Path.Combine(cacheDir.FullName, SdkPackageId.ToLowerInvariant(), "1.6.0", "tools", "MSIX", $"win10-{arch}"));
        var winappDir = GetRequiredService<IWinappDirectoryService>().GetLocalWinappDirectory();

        await _msixService.SetupSelfContainedAsync(winappDir, arch, TestTaskContext, PackageListWith(SdkPackageId, "1.6.0"), TestContext.CancellationToken);

        var deploymentDir = Path.Combine(winappDir.FullName, "self-contained", arch, "deployment");
        Assert.IsFalse(Directory.Exists(deploymentDir), "No runtime files should be staged when the MSIX tools directory is empty");
    }

    [TestMethod]
    public async Task SetupSelfContainedAsync_PreExistingExtractedDirectory_Recreated()
    {
        var arch = ArrangeRuntimeMsixPackage("1.6.0", "Microsoft.WindowsAppRuntime.1.6.msix", withInventory: true, "Microsoft.Foo.dll");
        var winappDir = GetRequiredService<IWinappDirectoryService>().GetLocalWinappDirectory();

        // Pre-create the extracted directory with a stale file so the "delete existing" branch runs.
        var extractedDir = Directory.CreateDirectory(Path.Combine(winappDir.FullName, "self-contained", arch, "extracted"));
        var staleFile = Path.Combine(extractedDir.FullName, "stale.txt");
        await File.WriteAllTextAsync(staleFile, "stale", TestContext.CancellationToken);

        await _msixService.SetupSelfContainedAsync(winappDir, arch, TestTaskContext, PackageListWith(SdkPackageId, "1.6.0"), TestContext.CancellationToken);

        Assert.IsFalse(File.Exists(staleFile), "Pre-existing extracted directory should be recreated (stale contents removed)");
        var deploymentDir = Path.Combine(winappDir.FullName, "self-contained", arch, "deployment");
        Assert.IsTrue(File.Exists(Path.Combine(deploymentDir, "Microsoft.Foo.dll")));
    }

    [TestMethod]
    public async Task PackSingleFolderToMsix_MSBuildManifestWithRecipe_CopiesFromRecipe()
    {
        var arch = WorkspaceSetupService.GetSystemArchitecture();
        var inputFolder = _tempDirectory.CreateSubdirectory("recipe-input");
        await File.WriteAllTextAsync(Path.Combine(inputFolder.FullName, "App.exe"), "MZ", TestContext.CancellationToken);

        // MSBuild-generated manifest (build:Metadata makepri.exe) so the recipe path is taken.
        var manifest =
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:build="http://schemas.microsoft.com/developer/appx/2015/build">
              <Identity Name="Contoso.App" Publisher="CN=Contoso" Version="1.0.0.0" ProcessorArchitecture="{arch}" />
              <Applications>
                <Application Id="App" Executable="App.exe" />
              </Applications>
              <build:Metadata>
                <build:Item Name="makepri.exe" Version="10.0.0.0" />
              </build:Metadata>
            </Package>
            """;
        var srcManifest = new FileInfo(Path.Combine(inputFolder.FullName, "AppxManifest.xml"));
        await File.WriteAllTextAsync(srcManifest.FullName, manifest, TestContext.CancellationToken);

        // A .build.appxrecipe listing the manifest + exe with their package paths.
        var recipe = new StringBuilder();
        recipe.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        recipe.AppendLine("<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">");
        recipe.AppendLine("  <ItemGroup>");
        recipe.AppendLine($"    <AppXManifest Include=\"{srcManifest.FullName}\"><PackagePath>appxmanifest.xml</PackagePath></AppXManifest>");
        recipe.AppendLine($"    <AppxPackagedFile Include=\"{Path.Combine(inputFolder.FullName, "App.exe")}\"><PackagePath>App.exe</PackagePath></AppxPackagedFile>");
        recipe.AppendLine("  </ItemGroup>");
        recipe.AppendLine("</Project>");
        await File.WriteAllTextAsync(Path.Combine(inputFolder.FullName, "App.build.appxrecipe"), recipe.ToString(), TestContext.CancellationToken);

        var outputMsix = new FileInfo(Path.Combine(_tempDirectory.CreateSubdirectory("recipe-out").FullName, "app.msix"));

        await (Task)PackSingleFolderMethod.Invoke(_msixService, [
            inputFolder, manifest, srcManifest, outputMsix, TestTaskContext,
            false /*selfContained*/, "App.exe" /*executable*/, arch /*targetArch*/,
            true /*skipPri*/, PackageListWith(SdkPackageId, "1.6.0"), CancellationToken.None])!;

        outputMsix.Refresh();
        Assert.IsTrue(outputMsix.Exists, "makeappx should have produced the .msix from the recipe-staged layout");
        Assert.IsTrue(File.Exists(Path.Combine(inputFolder.FullName, "App.build.appxrecipe")), "recipe input remains");
    }
}

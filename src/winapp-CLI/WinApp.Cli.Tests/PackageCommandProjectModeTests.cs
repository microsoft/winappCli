// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Project-mode routing tests for <see cref="PackageCommand"/>. A <see cref="FakeProjectRunService"/>
/// supplies canned build outcomes and a <see cref="FakeMsixService"/> records the packaging call, so
/// the .csproj → build → package pipeline can be verified without invoking the real .NET SDK or MSIX
/// tooling.
/// </summary>
[TestClass]
public class PackageCommandProjectModeTests : BaseCommandTests
{
    private FakeMsixService _fakeMsixService = null!;
    private FakeProjectRunService _fakeProjectRunService = null!;

    private const string TestManifestContent = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                 IgnorableNamespaces="uap">
          <Identity Name="TestPackage" Publisher="CN=TestPublisher" Version="1.0.0.0" />
          <Properties>
            <DisplayName>Test Package</DisplayName>
            <PublisherDisplayName>Test Publisher</PublisherDisplayName>
            <Logo>Assets\Logo.png</Logo>
          </Properties>
          <Applications>
            <Application Id="TestApp" Executable="TestApp.exe" EntryPoint="TestApp.App">
              <uap:VisualElements DisplayName="Test App" Description="Test application"
                                  BackgroundColor="#777777" Square150x150Logo="Assets\Logo.png" Square44x44Logo="Assets\Logo.png" />
            </Application>
          </Applications>
        </Package>
        """;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeMsixService = new FakeMsixService();
        _fakeProjectRunService = new FakeProjectRunService();
        return services
            .AddSingleton<IMsixService>(_fakeMsixService)
            .AddSingleton<IProjectRunService>(_fakeProjectRunService)
            .AddSingleton<INugetService, FakeNugetService>();
    }

    private FileInfo CreateCsproj(string name = "App.csproj")
    {
        var path = Path.Join(_tempDirectory.FullName, name);
        File.WriteAllText(path, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        return new FileInfo(path);
    }

    private DirectoryInfo CreateTargetDir(bool withManifest)
    {
        var dir = _tempDirectory.CreateSubdirectory($"bin_{Guid.NewGuid():N}");
        if (withManifest)
        {
            File.WriteAllText(Path.Join(dir.FullName, "appxmanifest.xml"), TestManifestContent);
        }
        return dir;
    }

    private void SetPackagedOutcome(FileInfo csproj, DirectoryInfo targetDir, string arch = "x64", bool selfContained = false, bool noRestore = false, string? framework = null)
    {
        _fakeProjectRunService.BuildOutcome = new ProjectBuildOutcome(
            new ProjectRunResolution(csproj, targetDir.FullName, null, ProjectPackaging.Packaged, selfContained, arch, framework, noRestore), 0);
    }

    // ---- Packaged happy path -------------------------------------------------

    [TestMethod]
    public async Task ProjectMode_Packaged_PackagesTargetDirWithProjectContext()
    {
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: true);
        SetPackagedOutcome(csproj, targetDir, arch: "arm64", noRestore: true, framework: "net10.0-windows");
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeProjectRunService.PublishAndResolveCalls.Count, "Project mode must publish (not build) the project");
        Assert.AreEqual(1, _fakeMsixService.CreatePackageCalls.Count, "Packaged project should call CreateMsixPackageAsync once");
        Assert.AreEqual(targetDir.FullName, _fakeMsixService.CreatePackageCalls[0].FullName, "Packaging must target the publish output (PublishDir)");

        var args = _fakeMsixService.LastCreatePackageArgs!;
        Assert.AreEqual(csproj.FullName, args.ProjectFile!.FullName, "The resolved project must be threaded into packaging");
        Assert.AreEqual("net10.0-windows", args.Framework, "The built framework must be threaded into packaging");
        Assert.IsTrue(args.NoRestore, "The resolution's NoRestore must be threaded into packaging");
        Assert.AreEqual("arm64", args.TargetArch, "The resolved architecture must be threaded into packaging");
    }

    [TestMethod]
    public async Task ProjectMode_ThreadsPackageGraphFromBuiltAssets()
    {
        // The build's evaluated project.assets.json + RID must reach packaging as a PackageGraphSource so the
        // Windows App SDK dependency is read from the graph that was actually built, not re-evaluated with
        // default configuration/RID (which can pick the wrong graph for conditional package references).
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: true);
        var assetsPath = Path.Join(targetDir.FullName, "project.assets.json");
        _fakeProjectRunService.BuildOutcome = new ProjectBuildOutcome(
            new ProjectRunResolution(csproj, targetDir.FullName, null, ProjectPackaging.Packaged, false, "arm64",
                ProjectAssetsFile: assetsPath, ProjectAssetsRuntimeIdentifier: "win-arm64"), 0);
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName]);

        Assert.AreEqual(0, exitCode);
        var graph = _fakeMsixService.LastCreatePackageArgs!.PackageGraph;
        Assert.IsNotNull(graph, "A built assets file must be threaded as a PackageGraphSource");
        Assert.AreEqual(assetsPath, graph!.AssetsFile.FullName);
        Assert.AreEqual("win-arm64", graph.RuntimeIdentifier);
    }

    [TestMethod]
    public async Task ProjectMode_NoAssetsFile_ThreadsNullPackageGraph()
    {
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: true);
        SetPackagedOutcome(csproj, targetDir); // resolution has no ProjectAssetsFile
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName]);

        Assert.AreEqual(0, exitCode);
        Assert.IsNull(_fakeMsixService.LastCreatePackageArgs!.PackageGraph,
            "With no evaluated assets file, packaging must fall back to null (cwd/dotnet package list)");
    }

    [TestMethod]
    public async Task ProjectMode_ForwardsBuildOptions()
    {
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: true);
        SetPackagedOutcome(csproj, targetDir, arch: "arm64");
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [csproj.FullName, "-c", "Release", "--arch", "arm64", "-f", "net10.0-windows", "--no-restore", "-p", "Foo=Bar"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeProjectRunService.BuildOptions.Count);
        var options = _fakeProjectRunService.BuildOptions[0];
        Assert.AreEqual("Release", options.Configuration);
        Assert.AreEqual("arm64", options.Architecture);
        Assert.AreEqual("net10.0-windows", options.Framework);
        Assert.IsTrue(options.NoRestore);
        CollectionAssert.Contains(options.Properties.ToList(), "Foo=Bar");
    }

    [TestMethod]
    public async Task ProjectMode_NativeMsixProject_LetsSdkPackageAndDelivers()
    {
        // An MSIX-tooling project takes the native path: the SDK produces the package during publish and
        // winapp delivers it — it must NOT go through the generic publish/repackage path.
        var csproj = CreateCsproj();
        var produced = new FileInfo(Path.Join(_tempDirectory.FullName, "App_1.0.0.0_arm64.msix"));
        File.WriteAllText(produced.FullName, "msix");
        _fakeProjectRunService.IsNativeMsixProject = true;
        _fakeProjectRunService.NativeMsixOutcome = new NativeMsixPublishOutcome(produced, 0);
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeProjectRunService.PublishNativeMsixCalls.Count, "Native project must use the SDK native packaging path");
        Assert.AreEqual(1, _fakeMsixService.DeliverNativeMsixCalls.Count, "The SDK-produced package must be delivered, not repackaged");
        Assert.AreEqual(0, _fakeProjectRunService.PublishAndResolveCalls.Count, "Native project must not use the generic publish path");
        Assert.AreEqual(0, _fakeMsixService.CreatePackageCalls.Count, "Native project must not go through MsixService repackaging");
    }

    [TestMethod]
    public async Task ProjectMode_NativeMsixProject_RejectsManifestOption()
    {
        // --manifest is a generic-layout option; an MSIX-tooling project configures AppxManifest in the
        // project, so reject it before packaging rather than silently ignoring it.
        var csproj = CreateCsproj();
        var manifest = new FileInfo(Path.Join(_tempDirectory.FullName, "Package.appxmanifest"));
        File.WriteAllText(manifest.FullName, "<Package/>");
        _fakeProjectRunService.IsNativeMsixProject = true;
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--manifest", manifest.FullName]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.PublishNativeMsixCalls.Count, "--manifest must be rejected before packaging a native project");
    }

    [TestMethod]
    public async Task ProjectMode_NativeMsixProject_PropagatesPackagingFailure()
    {
        // Native packaging failure is reported as-is — never a silent fall back to generic packaging.
        var csproj = CreateCsproj();
        _fakeProjectRunService.IsNativeMsixProject = true;
        _fakeProjectRunService.NativeMsixOutcome = new NativeMsixPublishOutcome(null, 7);
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName]);

        Assert.AreEqual(7, exitCode, "The native publish exit code must propagate");
        Assert.AreEqual(0, _fakeProjectRunService.PublishAndResolveCalls.Count, "A failed native project must not fall back to generic packaging");
        Assert.AreEqual(0, _fakeMsixService.DeliverNativeMsixCalls.Count);
    }

    [TestMethod]
    public async Task ProjectMode_RejectsAppxPackageDirProperty()
    {
        // AppxPackageDir is winapp's internal staging location; the public selector is --output.
        var csproj = CreateCsproj();
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "-p", "AppxPackageDir=C:\\out"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.PublishAndResolveCalls.Count, "-p AppxPackageDir must be rejected before packaging");
        Assert.AreEqual(0, _fakeProjectRunService.PublishNativeMsixCalls.Count);
    }

    [TestMethod]
    public async Task ProjectMode_ArchWithRuntimeIdentifierProperty_Conflicts()
    {
        // --arch is the dedicated selector; a simultaneous explicit -p RuntimeIdentifier is a conflict.
        var csproj = CreateCsproj();
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--arch", "arm64", "-p", "RuntimeIdentifier=win-x64"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.PublishAndResolveCalls.Count, "A conflicting target selection must be rejected before packaging");
    }

    [TestMethod]
    public async Task ProjectMode_RejectsStoreUploadRequest()
    {
        // Store-upload (.msixupload) is out of project-mode scope; it must fail rather than produce a .msix.
        var csproj = CreateCsproj();
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "-p", "UapAppxPackageBuildMode=StoreUpload"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.PublishAndResolveCalls.Count, "A Store-upload request must be rejected before packaging");
        Assert.AreEqual(0, _fakeProjectRunService.PublishNativeMsixCalls.Count);
    }

    [TestMethod]
    public async Task ProjectMode_RejectsResourceSplitRequest()
    {
        // Resource-split bundling is out of project-mode scope; an architecture-only bundle is not equivalent.
        var csproj = CreateCsproj();
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "-p", "AppxBundleAutoResourcePackageQualifiers=Language"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.PublishAndResolveCalls.Count, "A resource-split request must be rejected before packaging");
        Assert.AreEqual(0, _fakeProjectRunService.PublishNativeMsixCalls.Count);
    }

    [TestMethod]
    public async Task ProjectMode_NoSignWithCert_Conflicts()
    {
        var csproj = CreateCsproj();
        var cert = new FileInfo(Path.Join(_tempDirectory.FullName, "dev.pfx"));
        File.WriteAllText(cert.FullName, "x");
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--no-sign", "--cert", cert.FullName]);

        Assert.AreEqual(1, exitCode, "--no-sign and --cert are mutually exclusive");
    }

    [TestMethod]
    public async Task ProjectMode_NativeSelfContained_InjectsWindowsAppSdkSelfContained()
    {
        var csproj = CreateCsproj();
        var produced = new FileInfo(Path.Join(_tempDirectory.FullName, "App_1.0.0.0_arm64.msix"));
        File.WriteAllText(produced.FullName, "msix");
        _fakeProjectRunService.IsNativeMsixProject = true;
        _fakeProjectRunService.NativeMsixOutcome = new NativeMsixPublishOutcome(produced, 0);
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--self-contained"]);

        Assert.AreEqual(0, exitCode);
        // The native publish options must carry the self-contained property so the SDK bundles the runtime.
        var options = _fakeProjectRunService.BuildOptions[^1];
        CollectionAssert.Contains(options.Properties.ToList(), "WindowsAppSDKSelfContained=true");
    }

    [TestMethod]
    public async Task ProjectMode_MultipleArches_ProducesBundle()
    {
        var csproj = CreateCsproj();
        var produced = new FileInfo(Path.Join(_tempDirectory.FullName, "App_1.0.0.0_x64.msix"));
        File.WriteAllText(produced.FullName, "msix");
        _fakeProjectRunService.IsNativeMsixProject = true;
        _fakeProjectRunService.NativeMsixOutcome = new NativeMsixPublishOutcome(produced, 0);
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--arch", "x64", "--arch", "arm64"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(2, _fakeProjectRunService.PublishNativeMsixCalls.Count, "one native publish per requested architecture");
        Assert.AreEqual(1, _fakeMsixService.CreateBundleFromPackagesCalls.Count, "the produced slices must be bundled once");
        Assert.AreEqual(2, _fakeMsixService.CreateBundleFromPackagesCalls[0].Slices.Count);
    }

    [TestMethod]
    public async Task ProjectMode_DuplicateArch_Rejected()
    {
        var csproj = CreateCsproj();
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--arch", "x64", "--arch", "x64"]);

        Assert.AreEqual(1, exitCode, "a duplicate --arch must be rejected");
    }

    [TestMethod]
    public async Task ProjectMode_MultipleArches_RequireBundleOutputExtension()
    {
        var csproj = CreateCsproj();
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--arch", "x64", "--arch", "arm64", "--output", "app.msix"]);

        Assert.AreEqual(1, exitCode, "two architectures produce a .msixbundle, so a .msix --output is rejected");
    }

    [TestMethod]
    public async Task ProjectMode_ProjectThumbprintSigning_Rejected()
    {
        // Certificate-store thumbprint signing is the selected, enabled policy but unsupported — it must
        // error with a remedy, not be silently ignored.
        var csproj = CreateCsproj();
        _fakeProjectRunService.IsNativeMsixProject = true;
        _fakeProjectRunService.ProjectSigning = new ProjectSigningProperties(true, null, null, "ABCD1234", null);
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.PublishNativeMsixCalls.Count, "an unsupported signing policy must be rejected before packaging");
    }

    [TestMethod]
    public async Task ProjectMode_SigningEnabledWithoutCertificate_Rejected()
    {
        // AppxPackageSigningEnabled=true with no usable certificate must error, never downgrade to unsigned.
        var csproj = CreateCsproj();
        _fakeProjectRunService.IsNativeMsixProject = true;
        _fakeProjectRunService.ProjectSigning = new ProjectSigningProperties(true, null, null, null, null);
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName]);

        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task ProjectMode_ProjectKeyFileSigning_UsedForNative()
    {
        var csproj = CreateCsproj();
        var pfx = new FileInfo(Path.Join(_tempDirectory.FullName, "proj.pfx"));
        File.WriteAllText(pfx.FullName, "pfx");
        var produced = new FileInfo(Path.Join(_tempDirectory.FullName, "App_1.0.0.0_arm64.msix"));
        File.WriteAllText(produced.FullName, "msix");
        _fakeProjectRunService.IsNativeMsixProject = true;
        _fakeProjectRunService.NativeMsixOutcome = new NativeMsixPublishOutcome(produced, 0);
        _fakeProjectRunService.ProjectSigning = new ProjectSigningProperties(true, pfx.FullName, "secret", null, "http://timestamp");
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName]);

        Assert.AreEqual(0, exitCode);
        var call = _fakeMsixService.DeliverNativeMsixCalls[0];
        Assert.IsTrue(call.AutoSign, "the project's PFX signing configuration must sign the artifact");
        Assert.AreEqual(pfx.FullName, call.CertPath!.FullName);
    }

    [TestMethod]
    public async Task ProjectMode_SigningDisabledWithRetainedThumbprint_DeliversUnsigned()
    {
        // AppxPackageSigningEnabled=false is an unsigned policy even with a retained thumbprint (spec §6).
        var csproj = CreateCsproj();
        var produced = new FileInfo(Path.Join(_tempDirectory.FullName, "App_1.0.0.0_arm64.msix"));
        File.WriteAllText(produced.FullName, "msix");
        _fakeProjectRunService.IsNativeMsixProject = true;
        _fakeProjectRunService.NativeMsixOutcome = new NativeMsixPublishOutcome(produced, 0);
        _fakeProjectRunService.ProjectSigning = new ProjectSigningProperties(false, null, null, "ABCD1234", null);
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName]);

        Assert.AreEqual(0, exitCode);
        Assert.IsFalse(_fakeMsixService.DeliverNativeMsixCalls[0].AutoSign, "disabled signing must not sign, even with a retained thumbprint");
    }

    [TestMethod]
    public async Task ProjectMode_DefaultsToReleaseConfiguration()
    {
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: true);
        SetPackagedOutcome(csproj, targetDir);
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual("Release", _fakeProjectRunService.BuildOptions[0].Configuration,
            "pack produces a distributable, so project mode must default to Release when no -c is given");
    }

    [TestMethod]
    public async Task ProjectMode_UsesEvaluatedManifest_WhenNotInPackagingOutput()
    {
        // A packaged (EnableMsixTooling) WinUI app publishes its payload to PublishDir but the
        // MSBuild-generated AppxManifest.xml and .appxrecipe live in the build output
        // (FinalAppxManifestName / AppxPackageRecipe), not the publish folder. Pack must thread that
        // evaluated manifest into packaging rather than failing to find one in the output folder.
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        var generated = _tempDirectory.CreateSubdirectory($"gen_{Guid.NewGuid():N}");
        var manifest = new FileInfo(Path.Join(generated.FullName, "AppxManifest.xml"));
        File.WriteAllText(manifest.FullName, TestManifestContent);
        var recipe = new FileInfo(Path.Join(generated.FullName, "app.build.appxrecipe"));
        File.WriteAllText(recipe.FullName, "<Project />");
        _fakeProjectRunService.BuildOutcome = new ProjectBuildOutcome(
            new ProjectRunResolution(
                csproj, targetDir.FullName, null, ProjectPackaging.Packaged, false, "x64", null, false,
                AppxManifestPath: manifest.FullName, AppxRecipePath: recipe.FullName),
            0);
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName]);

        Assert.AreEqual(0, exitCode, "The evaluated manifest must be used when the output folder has none");
        Assert.AreEqual(1, _fakeMsixService.CreatePackageCalls.Count);
        Assert.AreEqual(manifest.FullName, _fakeMsixService.LastCreatePackageArgs!.ManifestPath!.FullName,
            "The FinalAppxManifestName-resolved manifest must be threaded into packaging");
    }

    [TestMethod]
    public async Task ProjectMode_ThreadsOwningSolutionIntoBuildOptions()
    {
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: true);
        var solution = new FileInfo(Path.Join(_tempDirectory.FullName, "App.sln"));
        File.WriteAllText(solution.FullName, "");
        _fakeProjectRunService.InputResolutionOverride =
            new RunInputResolution(WinAppRunMode.Project, csproj, csproj.Directory!, solution);
        SetPackagedOutcome(csproj, targetDir);
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(solution.FullName, _fakeProjectRunService.BuildOptions[0].Solution!.FullName,
            "The owning solution must be threaded into the build so $(SolutionDir) is defined");
    }

    // ---- Self-contained reconciliation --------------------------------------

    [TestMethod]
    public async Task ProjectMode_SelfContainedProject_DoesNotRebundleRuntime()
    {
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: true);
        SetPackagedOutcome(csproj, targetDir, selfContained: true);
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName]);

        Assert.AreEqual(0, exitCode);
        var args = _fakeMsixService.LastCreatePackageArgs!;
        Assert.IsTrue(args.SelfContained, "A self-contained project must package with the self-contained manifest model");
        Assert.IsTrue(args.RuntimeAlreadyBundled, "A self-contained build must not have its runtime re-bundled");
    }

    [TestMethod]
    public async Task ProjectMode_SelfContainedFlagOnly_BundlesRuntime()
    {
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: true);
        SetPackagedOutcome(csproj, targetDir, selfContained: false);
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--self-contained"]);

        Assert.AreEqual(0, exitCode);
        var args = _fakeMsixService.LastCreatePackageArgs!;
        Assert.IsTrue(args.SelfContained);
        Assert.IsFalse(args.RuntimeAlreadyBundled, "A framework-dependent build with --self-contained must have winapp bundle the runtime");
    }

    // ---- Error paths ---------------------------------------------------------

    [TestMethod]
    public async Task ProjectMode_Unpackaged_ErrorsWithoutPackaging()
    {
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        _fakeProjectRunService.BuildOutcome = new ProjectBuildOutcome(
            new ProjectRunResolution(csproj, targetDir.FullName, Path.Join(targetDir.FullName, "App.exe"), ProjectPackaging.Unpackaged, false, "x64"), 0);
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMsixService.CreatePackageCalls.Count, "An unpackaged project must not be packaged");
    }

    [TestMethod]
    public async Task ProjectMode_DefinitivelyUnpackaged_FailsBeforeBuilding()
    {
        var csproj = CreateCsproj();
        _fakeProjectRunService.DefinitivelyUnpackaged = true;
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.PublishAndResolveCalls.Count, "A definitively-unpackaged project must fail before publishing");
    }

    [TestMethod]
    public async Task ProjectMode_DefinitivelyUnpackaged_SkippedUnderNoBuild()
    {
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: true);
        _fakeProjectRunService.DefinitivelyUnpackaged = true; // would fast-fail, but --no-build skips the probe
        SetPackagedOutcome(csproj, targetDir);
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--no-build"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.IsDefinitivelyUnpackagedCalls.Count, "--no-build must skip the pre-build unpackaged probe");
    }

    [TestMethod]
    public async Task ProjectMode_BuildFailure_PropagatesExitCode()
    {
        var csproj = CreateCsproj();
        _fakeProjectRunService.BuildOutcome = new ProjectBuildOutcome(null, 42);
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName]);

        Assert.AreEqual(42, exitCode, "A build failure must propagate dotnet's exit code");
        Assert.AreEqual(0, _fakeMsixService.CreatePackageCalls.Count);
    }

    [TestMethod]
    public async Task ProjectMode_PackagedButNoManifestInOutput_Errors()
    {
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetPackagedOutcome(csproj, targetDir);
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeMsixService.CreatePackageCalls.Count, "No manifest in the build output must error before packaging");
    }

    [TestMethod]
    public async Task ProjectMode_MissingCsproj_Errors()
    {
        var missing = Path.Join(_tempDirectory.FullName, "Nope.csproj");
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [missing]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.PublishAndResolveCalls.Count);
    }

    [TestMethod]
    public async Task ProjectMode_InvalidProperty_Errors()
    {
        var csproj = CreateCsproj();
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "-p", "Foo"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.PublishAndResolveCalls.Count, "A malformed -p must be rejected before publishing");
    }

    [TestMethod]
    public async Task ProjectMode_CommaPackedProperty_Errors()
    {
        // A single -p must not pack multiple properties with ',' — MSBuild splits on ',' and could smuggle a
        // dedicated-flag property (e.g. RuntimeIdentifier) past the -c/-r/-f contract. Matches winapp run.
        var csproj = CreateCsproj();
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "-p", "A=1,B=2"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.PublishAndResolveCalls.Count, "A comma-packed -p must be rejected before publishing");
    }

    [TestMethod]
    public async Task ProjectMode_OutputMsixbundle_RejectedBeforeBuilding()
    {
        var csproj = CreateCsproj();
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--output", "out.msixbundle"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.PublishAndResolveCalls.Count, "A single .csproj cannot produce a .msixbundle — reject before publishing");
    }

    // ---- Classification ------------------------------------------------------

    [TestMethod]
    public async Task Package_DirectoryNamedLikeCsproj_StaysFolderMode()
    {
        // A directory literally named "payload.csproj" is a legal folder and must be packaged as a
        // build-output folder, not misclassified as a project to build.
        var dir = _tempDirectory.CreateSubdirectory("payload.csproj");
        File.WriteAllText(Path.Join(dir.FullName, "appxmanifest.xml"), TestManifestContent);
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [dir.FullName]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.PublishAndResolveCalls.Count, "A directory named *.csproj must not enter project mode");
        Assert.AreEqual(1, _fakeMsixService.CreatePackageCalls.Count);
        Assert.IsNull(_fakeMsixService.LastCreatePackageArgs!.ProjectFile, "Folder mode must not thread a project file");
    }
}

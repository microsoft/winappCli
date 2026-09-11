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
        Assert.AreEqual(1, _fakeMsixService.CreatePackageCalls.Count, "Packaged project should call CreateMsixPackageAsync once");
        Assert.AreEqual(targetDir.FullName, _fakeMsixService.CreatePackageCalls[0].FullName, "Packaging must target the build output (TargetDir)");

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
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count, "A definitively-unpackaged project must fail before the build");
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
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count);
    }

    [TestMethod]
    public async Task ProjectMode_InvalidProperty_Errors()
    {
        var csproj = CreateCsproj();
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "-p", "Foo"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count, "A malformed -p must be rejected before building");
    }

    [TestMethod]
    public async Task ProjectMode_OutputMsixbundle_RejectedBeforeBuilding()
    {
        var csproj = CreateCsproj();
        var command = GetRequiredService<PackageCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--output", "out.msixbundle"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count, "A single .csproj cannot produce a .msixbundle — reject before building");
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
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count, "A directory named *.csproj must not enter project mode");
        Assert.AreEqual(1, _fakeMsixService.CreatePackageCalls.Count);
        Assert.IsNull(_fakeMsixService.LastCreatePackageArgs!.ProjectFile, "Folder mode must not thread a project file");
    }
}
